using System.Globalization;
using System.Text.RegularExpressions;

namespace WorkpulsActivityReport;

/// <summary>Per-minute state codes used in the minute strings (one char per minute, 1440 per day).</summary>
public static class State
{
    public const char NoData = '0';
    public const char Active = '1';
    public const char AlwaysActive = '2';
    public const char Idle = '3';
    public const char Away = '4';

    public static bool IsActive(char c) => c is Active or AlwaysActive;
}

public sealed record MachineDay(string Minutes, int IdleEvents, int Locks);

/// <summary>
/// Parses the agent "main" log (<c>YYYY-MM-DD HH:mm:ss.fff - level - message</c>) into a per-minute state timeline.
/// </summary>
public static class MainLogParser
{
    public const int MinutesPerDay = 1440;

    // An agent heartbeat is logged roughly every 20 s, so a minute is considered "agent running"
    // when any line exists within this many minutes of it.
    private const int AliveWindow = 2;

    public static MachineDay ParseDay(DateOnly day, IEnumerable<string> lines)
    {
        var seen = new bool[MinutesPerDay];
        var events = new List<(DateTime T, char S)>();
        int idleEvents = 0, locks = 0;

        foreach (var line in lines)
        {
            if (!TryParseLine(line, out var t, out int msgStart)) continue;
            if (DateOnly.FromDateTime(t) != day) continue;
            seen[(int)t.TimeOfDay.TotalMinutes] = true;

            char s = Classify(line.AsSpan(msgStart), ref idleEvents, ref locks);
            if (s != '\0') events.Add((t, s));
        }

        return new MachineDay(BuildMinutes(day, seen, events), idleEvents, locks);
    }

    internal static bool TryParseLine(string line, out DateTime time, out int messageStart)
    {
        time = default;
        messageStart = 0;
        if (line.Length < 26 || line[4] != '-' || line[7] != '-' || line[10] != ' ' || line[13] != ':' || line[16] != ':') return false;

        var s = line.AsSpan();
        if (!int.TryParse(s[..4], NumberStyles.None, CultureInfo.InvariantCulture, out int y) ||
            !int.TryParse(s[5..7], NumberStyles.None, CultureInfo.InvariantCulture, out int mo) ||
            !int.TryParse(s[8..10], NumberStyles.None, CultureInfo.InvariantCulture, out int d) ||
            !int.TryParse(s[11..13], NumberStyles.None, CultureInfo.InvariantCulture, out int h) ||
            !int.TryParse(s[14..16], NumberStyles.None, CultureInfo.InvariantCulture, out int mi) ||
            !int.TryParse(s[17..19], NumberStyles.None, CultureInfo.InvariantCulture, out int sec))
            return false;

        int ms = 0;
        int p = 19;
        if (line[p] == '.')
        {
            p++;
            int digits = 0;
            while (p < line.Length && char.IsAsciiDigit(line[p]))
            {
                if (digits < 3) { ms = ms * 10 + (line[p] - '0'); digits++; }
                p++;
            }
            for (; digits < 3; digits++) ms *= 10;
        }

        int sep1 = line.IndexOf(" - ", p, StringComparison.Ordinal);
        if (sep1 != p) return false;
        int sep2 = line.IndexOf(" - ", sep1 + 3, StringComparison.Ordinal);
        if (sep2 < 0) return false;

        if (mo is < 1 or > 12 || d < 1 || d > DateTime.DaysInMonth(y, mo) || h > 23 || mi > 59 || sec > 59) return false;
        time = new DateTime(y, mo, d, h, mi, sec, ms);
        messageStart = sep2 + 3;
        return true;
    }

    internal static char Classify(ReadOnlySpan<char> msg, ref int idleEvents, ref int locks)
    {
        if (msg.StartsWith("App active="))
        {
            var v = msg["App active=".Length..];
            if (v.StartsWith("true")) return State.Active;
            if (v.StartsWith("false")) { idleEvents++; return State.Idle; }
            if (v.StartsWith("undefined")) return State.Away;
            return '\0';
        }
        if (msg.StartsWith("App always-active")) return State.AlwaysActive;
        if (msg.StartsWith("Power monitor: lock-screen")) { locks++; return State.Away; }
        if (msg.StartsWith("Power monitor: suspend")) return State.Away;
        return '\0';
    }

    private static string BuildMinutes(DateOnly day, bool[] seen, List<(DateTime T, char S)> events)
    {
        var ordered = events.OrderBy(e => e.T).ToList();
        var d0 = day.ToDateTime(TimeOnly.MinValue);
        var chars = new char[MinutesPerDay];
        char state = '\0';
        int j = 0;

        for (int i = 0; i < MinutesPerDay; i++)
        {
            bool alive = false;
            for (int k = Math.Max(0, i - AliveWindow); k <= Math.Min(MinutesPerDay - 1, i + AliveWindow) && !alive; k++) alive = seen[k];

            // Sample the state in the middle of the minute using the last event before it.
            var sample = d0.AddSeconds(i * 60 + 30);
            while (j < ordered.Count && ordered[j].T <= sample) state = ordered[j++].S;

            chars[i] = !alive ? State.NoData : state == '\0' ? State.Active : state;
        }
        return new string(chars);
    }
}

/// <summary>
/// Parses the agent "tracker" log: <c>YYYY/MM/DD HH:mm:ss | CODE | tid | OTHER|Injected inputs detected, n=N</c>.
/// OTHER is typically remote (RDP) input, Injected is synthetic input.
/// </summary>
public static partial class TrackerLogParser
{
    public sealed record InputCounts(int Other, int Injected);

    [GeneratedRegex(@"^(\d{4})/(\d\d)/(\d\d) .*\| (OTHER|Injected) inputs detected, n=(\d+)")]
    private static partial Regex InputLine();

    public static Dictionary<string, InputCounts> Parse(IEnumerable<string> lines)
    {
        var result = new Dictionary<string, InputCounts>();
        foreach (var line in lines)
        {
            var m = InputLine().Match(line);
            if (!m.Success) continue;
            var day = $"{m.Groups[1].Value}-{m.Groups[2].Value}-{m.Groups[3].Value}";
            int n = int.Parse(m.Groups[5].Value, CultureInfo.InvariantCulture);
            var c = result.GetValueOrDefault(day, new InputCounts(0, 0));
            result[day] = m.Groups[4].Value == "OTHER" ? c with { Other = c.Other + n } : c with { Injected = c.Injected + n };
        }
        return result;
    }
}
