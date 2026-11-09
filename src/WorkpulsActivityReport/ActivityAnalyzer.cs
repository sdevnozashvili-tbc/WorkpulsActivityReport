using System.Globalization;
using System.Text.Json;

namespace WorkpulsActivityReport;

public sealed class DayStats
{
    public Dictionary<string, int> Active { get; init; } = new();
    public int Union { get; init; }
    public int Overlap { get; init; }
    public int Idle { get; init; }
    public int Away { get; init; }
    public int AlwaysActive { get; init; }
    public int First { get; init; } = -1;
    public int Last { get; init; } = -1;

    /// <summary>
    /// Union = minutes active on at least one machine; Overlap = active on two or more (e.g. working over RDP);
    /// Idle/Away count only minutes when no machine was active.
    /// </summary>
    public static DayStats Compute(IReadOnlyDictionary<string, string> minutes)
    {
        var active = minutes.Keys.ToDictionary(k => k, _ => 0);
        int union = 0, overlap = 0, idle = 0, away = 0, always = 0, first = -1, last = -1;

        for (int i = 0; i < MainLogParser.MinutesPerDay; i++)
        {
            int n = 0;
            bool anyIdle = false, anyAway = false, anyAlways = false;
            foreach (var (machine, s) in minutes)
            {
                char c = s[i];
                if (State.IsActive(c)) { n++; active[machine]++; }
                anyIdle |= c == State.Idle;
                anyAway |= c == State.Away;
                anyAlways |= c == State.AlwaysActive;
            }

            if (n > 0) { union++; if (first < 0) first = i; last = i; }
            if (n > 1) overlap++;
            if (n == 0 && anyIdle) idle++;
            else if (n == 0 && anyAway) away++;
            if (anyAlways) always++;
        }

        return new DayStats { Active = active, Union = union, Overlap = overlap, Idle = idle, Away = away, AlwaysActive = always, First = first, Last = last };
    }
}

public sealed class DayData
{
    public required string Day { get; init; }
    public required Dictionary<string, string> Minutes { get; init; }
    public required Dictionary<string, int> IdleEvents { get; init; }
    public required Dictionary<string, int> Locks { get; init; }
    public int OtherInputs { get; init; }
    public int InjectedInputs { get; init; }
    public bool HasTracker { get; init; }
    public required DayStats Stats { get; init; }
}

public sealed class ReportData
{
    public required string GeneratedAt { get; init; }
    public string? PreviousRun { get; init; }
    public required string LocalMachine { get; init; }
    public required List<string> Machines { get; init; }
    public required List<DayData> Days { get; init; }
}

/// <summary>Builds <see cref="ReportData"/> from the archive, caching parsed days that have not changed.</summary>
public sealed class ActivityAnalyzer(string dataDir, IReadOnlyList<LogFile> files)
{
    private const string ParserVersion = "1";
    private readonly string _cacheDir = Path.Combine(dataDir, "cache");

    private sealed record CacheEntry(string Signature, MachineDay Data);

    public List<string> Warnings { get; } = [];

    public ReportData Analyze(string localMachine, DateTime? previousRun)
    {
        localMachine = LogSources.SafeName(localMachine);

        var perMachine = DropCopiesOfLocal(files.Where(f => f.Kind == LogKind.Main).ToList(), localMachine)
            .GroupBy(f => f.Machine)
            .ToDictionary(g => g.Key, g => ParseMachine(g.Key, g));

        var inputs = TrackerLogParser.Parse(files
            .Where(f => f.Kind == LogKind.Tracker && f.Machine == localMachine)
            .SelectMany(SafeLines));

        // Other machines first, this machine last (matches the "laptop -> RDP -> PC" reading order).
        var machines = perMachine.Keys.Where(m => m != localMachine).OrderBy(m => m, StringComparer.OrdinalIgnoreCase).ToList();
        if (perMachine.ContainsKey(localMachine)) machines.Add(localMachine);

        var empty = new string(State.NoData, MainLogParser.MinutesPerDay);
        var days = perMachine.Values.SelectMany(d => d.Keys).Distinct().Order(StringComparer.Ordinal).Select(day =>
        {
            var minutes = machines.ToDictionary(m => m, m => perMachine[m].TryGetValue(day, out var md) ? md.Minutes : empty);
            var input = inputs.GetValueOrDefault(day);
            return new DayData
            {
                Day = day,
                Minutes = minutes,
                IdleEvents = machines.ToDictionary(m => m, m => perMachine[m].TryGetValue(day, out var md) ? md.IdleEvents : 0),
                Locks = machines.ToDictionary(m => m, m => perMachine[m].TryGetValue(day, out var md) ? md.Locks : 0),
                OtherInputs = input?.Other ?? 0,
                InjectedInputs = input?.Injected ?? 0,
                HasTracker = input is not null,
                Stats = DayStats.Compute(minutes),
            };
        }).ToList();

        return new ReportData
        {
            GeneratedAt = DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture),
            PreviousRun = previousRun?.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture),
            LocalMachine = localMachine,
            Machines = machines,
            Days = days,
        };
    }

    private Dictionary<string, MachineDay> ParseMachine(string machine, IEnumerable<LogFile> files)
    {
        var result = new Dictionary<string, MachineDay>();
        foreach (var dayGroup in files.GroupBy(f => f.Name[..10]))
        {
            if (!DateOnly.TryParseExact(dayGroup.Key, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day)) continue;

            // "2026-11-05.log" is written first, then "2026-11-05.log.1", ".log.2", ...
            var ordered = dayGroup.OrderBy(f => f.Name.Length).ThenBy(f => f.Name, StringComparer.Ordinal).ToList();
            var signature = ParserVersion + "|" + string.Join("|", ordered.Select(f => $"{f.Name}:{f.Length}"));
            var cachePath = Path.Combine(_cacheDir, machine, dayGroup.Key + ".json");

            var cached = TryReadCache(cachePath);
            if (cached is not null && cached.Signature == signature)
            {
                result[dayGroup.Key] = cached.Data;
                continue;
            }

            var warningsBefore = Warnings.Count;
            var parsed = MainLogParser.ParseDay(day, ordered.SelectMany(SafeLines));
            if (Warnings.Count == warningsBefore)
            {
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(cachePath)!);
                    File.WriteAllText(cachePath, JsonSerializer.Serialize(new CacheEntry(signature, parsed)));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // The cache is only an optimization; without write access every run simply re-parses.
                }
            }
            result[dayGroup.Key] = parsed;
        }
        return result;
    }

    /// <summary>Folders that were recognized as copies of this machine's own logs (not another device).</summary>
    public List<string> CopiesOfLocal { get; } = [];

    /// <summary>
    /// A <c>main\&lt;folder&gt;</c> is only another device if its logs are really different. Log lines carry
    /// millisecond timestamps and random fragment ids, so two devices never write an identical first line for a day;
    /// if any day of a folder starts exactly like this machine's log for that day, the folder is a copy of this
    /// machine's logs. Its days are then merged into this machine (they may cover days the agent already deleted).
    /// </summary>
    private List<LogFile> DropCopiesOfLocal(List<LogFile> main, string localMachine)
    {
        string? Fingerprint(IEnumerable<LogFile> day)
        {
            var first = day.OrderBy(f => f.Name.Length).ThenBy(f => f.Name, StringComparer.Ordinal).First();
            try { return first.ReadLines().FirstOrDefault(l => l.Length > 0); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException) { return null; }
        }

        var byMachineDay = main.GroupBy(f => (f.Machine, Day: f.Name[..10])).ToDictionary(g => g.Key, g => g.ToList());
        var localPrints = byMachineDay.Where(kv => kv.Key.Machine == localMachine)
            .Select(kv => Fingerprint(kv.Value)).OfType<string>().ToHashSet(StringComparer.Ordinal);
        if (localPrints.Count == 0) return main;

        var copies = byMachineDay.Keys.Select(k => k.Machine).Where(m => m != localMachine).Distinct()
            .Where(m => byMachineDay.Where(kv => kv.Key.Machine == m).Any(kv => Fingerprint(kv.Value) is { } p && localPrints.Contains(p)))
            .ToHashSet();
        if (copies.Count == 0) return main;
        CopiesOfLocal.AddRange(copies.Order());

        var localDays = byMachineDay.Keys.Where(k => k.Machine == localMachine).Select(k => k.Day).ToHashSet();
        var result = main.Where(f => !copies.Contains(f.Machine)).ToList();
        // Days only present in a copy: take the longest copy of that day and attribute it to this machine.
        foreach (var day in byMachineDay.Where(kv => copies.Contains(kv.Key.Machine) && !localDays.Contains(kv.Key.Day))
                     .GroupBy(kv => kv.Key.Day))
        {
            var best = day.OrderByDescending(kv => kv.Value.Sum(f => f.Length)).First().Value;
            result.AddRange(best.Select(f => f with { Machine = localMachine }));
        }
        return result;
    }

    private IEnumerable<string> SafeLines(LogFile f)
    {
        try
        {
            return f.ReadLines().ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            Warnings.Add($"{f.Location}: ვერ წავიკითხე ({ex.Message})");
            return [];
        }
    }

    private static CacheEntry? TryReadCache(string path)
    {
        if (!File.Exists(path)) return null;
        try { return JsonSerializer.Deserialize<CacheEntry>(File.ReadAllText(path)); }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException) { return null; }
    }
}
