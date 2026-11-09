using System.IO.Compression;
using System.Text;

namespace WorkpulsActivityReport.Tests;

public class MainLogParserTests
{
    private static readonly DateOnly Day = new(2026, 11, 9);

    private static string L(string time, string msg) => $"2026-11-09 {time}.123 - info - {msg}";

    private static IEnumerable<string> Heartbeat(int fromMinute, int toMinute) =>
        Enumerable.Range(fromMinute, toMinute - fromMinute + 1)
            .Select(m => L($"{m / 60:00}:{m % 60:00}:10", "Sending message to watchdog succeeded"));

    [Fact]
    public void Minutes_follow_state_events_and_heartbeat()
    {
        var lines = Heartbeat(9 * 60, 12 * 60).Concat(new[]
        {
            L("09:00:05", "App active=true"),
            L("10:00:00", "App active=false"),
            L("10:30:00", "App always-active"),
            L("11:00:00", "Power monitor: lock-screen"),
            L("11:30:00", "App active=true"),
        });

        var r = MainLogParser.ParseDay(Day, lines);

        Assert.Equal(1440, r.Minutes.Length);
        Assert.Equal(State.NoData, r.Minutes[8 * 60]);
        Assert.Equal(State.Active, r.Minutes[9 * 60 + 30]);
        Assert.Equal(State.Idle, r.Minutes[10 * 60 + 10]);
        Assert.Equal(State.AlwaysActive, r.Minutes[10 * 60 + 40]);
        Assert.Equal(State.Away, r.Minutes[11 * 60 + 10]);
        Assert.Equal(State.Active, r.Minutes[11 * 60 + 40]);
        Assert.Equal(State.Active, r.Minutes[12 * 60 + 2]);  // inside the +-2 minute alive window
        Assert.Equal(State.NoData, r.Minutes[12 * 60 + 3]);
        Assert.Equal(1, r.IdleEvents);
        Assert.Equal(1, r.Locks);
    }

    [Fact]
    public void Lines_from_other_days_and_garbage_are_ignored()
    {
        var r = MainLogParser.ParseDay(Day, new[]
        {
            "2026-11-10 09:00:00.000 - info - App active=true",
            "not a log line",
            "2026-11-09 25:00:00.000 - info - App active=true",
        });

        Assert.All(r.Minutes, c => Assert.Equal(State.NoData, c));
    }

    [Theory]
    [InlineData("2026-11-09 16:42:43.123 - info - App active=true", true)]
    [InlineData("2026-11-09 16:42:43.1234567 - debug - x", true)]
    [InlineData("2026-11-09 16:42:43 - info - x", true)]
    [InlineData("2026/11/09 16:42:43 | INFO | 1 | x", false)]
    public void TryParseLine_accepts_agent_format(string line, bool ok) =>
        Assert.Equal(ok, MainLogParser.TryParseLine(line, out _, out _));
}

public class TrackerLogParserTests
{
    [Fact]
    public void Sums_other_and_injected_per_day()
    {
        var r = TrackerLogParser.Parse(new[]
        {
            "2026/11/09 10:00:00 | INFO | 12 | OTHER inputs detected, n=5",
            "2026/11/09 10:05:00 | INFO | 12 | OTHER inputs detected, n=3",
            "2026/11/09 10:06:00 | WARN | 12 | Injected inputs detected, n=2",
            "2026/11/10 10:06:00 | INFO | 12 | OTHER inputs detected, n=1",
            "2026/11/10 10:06:00 | INFO | 12 | something else",
        });

        Assert.Equal(new TrackerLogParser.InputCounts(8, 2), r["2026-11-09"]);
        Assert.Equal(new TrackerLogParser.InputCounts(1, 0), r["2026-11-10"]);
    }
}

public class DayStatsTests
{
    private static string Fill(params (int From, int To, char S)[] ranges)
    {
        var c = Enumerable.Repeat(State.NoData, 1440).ToArray();
        foreach (var (from, to, s) in ranges) for (int i = from; i < to; i++) c[i] = s;
        return new string(c);
    }

    [Fact]
    public void Union_overlap_and_idle_are_not_double_counted()
    {
        var minutes = new Dictionary<string, string>
        {
            ["Laptop"] = Fill((540, 600, '1'), (600, 660, '3')),
            ["PC"] = Fill((570, 630, '1'), (630, 700, '4')),
        };

        var s = DayStats.Compute(minutes);

        Assert.Equal(60, s.Active["Laptop"]);
        Assert.Equal(60, s.Active["PC"]);
        Assert.Equal(90, s.Union);     // 09:00-10:30
        Assert.Equal(30, s.Overlap);   // 09:30-10:00
        Assert.Equal(30, s.Idle);      // 10:30-11:00 (laptop idle, PC away)
        Assert.Equal(40, s.Away);      // 11:00-11:40
        Assert.Equal(540, s.First);
        Assert.Equal(629, s.Last);
    }
}

public sealed class LogArchiveTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "wpar-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    private string Write(string rel, string content)
    {
        var p = Path.Combine(_dir, "src", rel);
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        File.WriteAllText(p, content);
        return p;
    }

    [Fact]
    public void Collects_machines_keeps_rotated_files_and_updates_grown_files()
    {
        var logs = Path.Combine(_dir, "src");
        Write(@"main\2026-11-09.log", "2026-11-09 09:00:00.000 - info - App active=true\n");
        Write(@"main\audit.json", "{}");
        Write(@"main\Laptop\2026-11-09.log", "x\n");
        Write(@"tracker\tracker_2026_11_09_1.log", "y\n");
        var o = new Options { LogsRoot = logs, DataDir = Path.Combine(_dir, "data") };

        var first = LogCollector.Collect(o, new LogArchive(o.DataDir));
        Assert.Equal(3, first.Copied);

        // Agent rotates the old file away and appends to the current one.
        File.Delete(Path.Combine(logs, @"main\Laptop\2026-11-09.log"));
        File.AppendAllText(Path.Combine(logs, @"main\2026-11-09.log"), "2026-11-09 10:00:00.000 - info - App active=false\n");

        var archive = new LogArchive(o.DataDir);
        var second = LogCollector.Collect(o, archive);
        Assert.Equal(1, second.Copied);
        Assert.Equal(1, second.Unchanged);

        var files = archive.Files();
        Assert.Contains(files, f => f.Machine == "Laptop" && f.Kind == LogKind.Main);
        Assert.Contains(files, f => f.Machine == "PC" && f.Kind == LogKind.Tracker);
        var pc = Assert.Single(files, f => f.Machine == "PC" && f.Kind == LogKind.Main);
        Assert.Equal(2, pc.ReadLines().Count());
    }

    [Fact]
    public void Imports_machine_logs_from_zip()
    {
        var zipPath = Path.Combine(_dir, "laptop.zip");
        Directory.CreateDirectory(_dir);
        using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create))
        {
            foreach (var name in new[] { "2026-11-06.log", "audit.json" })
            {
                using var w = new StreamWriter(zip.CreateEntry(name).Open(), Encoding.UTF8);
                w.WriteLine("2026-11-06 09:00:00.000 - info - App active=true");
            }
        }
        var o = new Options { LogsRoot = Path.Combine(_dir, "none"), DataDir = Path.Combine(_dir, "data") };
        o.Imports.Add(new ImportSource("Laptop", zipPath));

        var archive = new LogArchive(o.DataDir);
        var r = LogCollector.Collect(o, archive);

        Assert.Equal(1, r.Copied);
        Assert.Equal("2026-11-06.log", Assert.Single(archive.Files()).Name);
    }
    [Fact]
    public void Reads_sources_directly_when_archive_is_unavailable()
    {
        var logs = Path.Combine(_dir, "src");
        Write(@"main\2026-11-09.log", "2026-11-09 09:00:00.000 - info - App active=true\n");
        Write(@"tracker\tracker_2026_11_09_1.log", "y\n");
        // A data folder underneath an existing file can never be created.
        var blocker = Write("blocker.txt", "");
        var o = new Options { LogsRoot = logs, DataDir = Path.Combine(blocker, "data") };

        Assert.ThrowsAny<IOException>(() => new LogArchive(o.DataDir));
        var r = LogCollector.Collect(o, archive: null);

        Assert.Equal(2, r.Direct);
        Assert.All(r.Files, f => Assert.False(f.Compressed));
        var data = new ActivityAnalyzer(o.DataDir, r.Files).Analyze("PC", null);
        Assert.Equal("2026-11-09", Assert.Single(data.Days).Day);
    }

    [Fact]
    public void Falls_back_to_direct_read_per_file_when_archive_write_fails()
    {
        var logs = Path.Combine(_dir, "src");
        Write(@"main\2026-11-08.log", "a\n");
        Write(@"main\2026-11-09.log", "b\n");
        var o = new Options { LogsRoot = logs, DataDir = Path.Combine(_dir, "data") };
        var archive = new LogArchive(o.DataDir);
        // Occupy the archive target path with a directory so the gzip copy cannot be moved into place.
        Directory.CreateDirectory(Path.Combine(o.DataDir, "archive", "PC", "main", "2026-11-09.log.gz"));

        var r = LogCollector.Collect(o, archive);

        Assert.Equal(1, r.Copied);
        Assert.Equal(1, r.Direct);
        Assert.NotEmpty(r.Warnings);
        Assert.Equal(2, r.Files.Count);
        Assert.False(Assert.Single(r.Files, f => f.Name == "2026-11-09.log").Compressed);
    }
    [Fact]
    public void Merges_one_archive_into_another_without_double_compression()
    {
        Write(@"main\2026-11-09.log", "a\nb\n");
        var old = new Options { LogsRoot = Path.Combine(_dir, "src"), DataDir = Path.Combine(_dir, "old") };
        LogCollector.Collect(old, new LogArchive(old.DataDir));

        var target = new LogArchive(Path.Combine(_dir, "new"));
        var source = new LogArchive(old.DataDir, readOnly: true).Files();
        Assert.True(target.Store(Assert.Single(source)));
        Assert.False(target.Store(source[0]));

        Assert.Equal(["a", "b"], Assert.Single(target.Files()).ReadLines());
    }

    [Fact]
    public void Detects_paths_under_clickonce_root()
    {
        Assert.True(AppHome.IsUnder(Path.Combine(AppHome.ClickOnceRoot, "X", "Y", "app"), AppHome.ClickOnceRoot));
        Assert.False(AppHome.IsUnder(@"C:\Apps\WorkpulsActivityReport\publish", AppHome.ClickOnceRoot));
    }
    [Fact]
    public void Subfolder_with_a_copy_of_local_logs_is_not_another_device()
    {
        const string day9 = "2026-11-09 09:00:00.123 - info - App active=true\n2026-11-09 11:00:00.000 - info - App active=false\n";
        Write(@"main\2026-11-09.log", day9);
        Write(@"main\New folder\2026-11-09.log", day9[..60]);
        Write(@"main\New folder\2026-11-06.log", "2026-11-06 09:00:00.456 - info - App active=true\n2026-11-06 10:00:00.000 - info - App active=false\n");
        var o = new Options { LogsRoot = Path.Combine(_dir, "src"), DataDir = Path.Combine(_dir, "data") };

        var r = LogCollector.Collect(o, archive: null);
        var analyzer = new ActivityAnalyzer(o.DataDir, r.Files);
        var data = analyzer.Analyze("PC", null);

        Assert.Equal(["PC"], data.Machines);
        Assert.Equal(["New folder"], analyzer.CopiesOfLocal);
        Assert.Equal(["2026-11-06", "2026-11-09"], data.Days.Select(d => d.Day));
    }

    [Fact]
    public void Subfolder_with_different_logs_is_another_device()
    {
        Write(@"main\2026-11-09.log", "2026-11-09 09:00:00.123 - info - App active=true\n");
        Write(@"main\Laptop\2026-11-09.log", "2026-11-09 08:30:00.987 - info - App active=true\n");
        var o = new Options { LogsRoot = Path.Combine(_dir, "src"), DataDir = Path.Combine(_dir, "data") };

        var analyzer = new ActivityAnalyzer(o.DataDir, LogCollector.Collect(o, archive: null).Files);

        Assert.Equal(["Laptop", "PC"], analyzer.Analyze("PC", null).Machines);
        Assert.Empty(analyzer.CopiesOfLocal);
    }
}