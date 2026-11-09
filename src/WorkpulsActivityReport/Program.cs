using System.Diagnostics;
using System.Globalization;
using System.Text;
using WorkpulsActivityReport;

Console.OutputEncoding = Encoding.UTF8;

var options = Options.Parse(args, Console.Error);
if (options is null)
{
    Console.WriteLine(Options.Usage);
    return args.Any(a => a is "-h" or "--help" or "/?") ? 0 : 1;
}

LogArchive? archive = null;
try
{
    archive = new LogArchive(options.DataDir);
}
catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
{
    Console.WriteLine($"! არქივის საქაღალდე მიუწვდომელია ({ex.Message}).");
    Console.WriteLine("  ლოგები წაიკითხება პირდაპირ წყაროდან (წინა გაშვებების დაგროვება ამ რეჟიმში არ ხდება).");
}

if (options.Collect) Console.WriteLine($"ლოგების წყარო: {options.LogsRoot}");
var collected = LogCollector.Collect(options, archive);
if (archive is not null && options.Collect)
    Console.WriteLine($"არქივი: {Path.Combine(options.DataDir, "archive")}  (ახალი/განახლებული: {collected.Copied}, უცვლელი: {collected.Unchanged})");
if (collected.Direct > 0)
    Console.WriteLine($"პირდაპირ წაკითხული (არქივის გარეშე): {collected.Direct} ფაილი");
foreach (var w in collected.Warnings) Console.WriteLine($"  ! {w}");

if (archive is not null && AppHome.IsClickOnce)
{
    var migrationWarnings = new List<string>();
    var migrated = AppHome.MigrateLegacy(archive, options.DataDir, migrationWarnings);
    if (migrated > 0)
    {
        Console.WriteLine($"ძველი ინსტალაციიდან გადმოტანილია {migrated} ფაილი → {options.DataDir}");
        collected = collected with { Files = MergeArchive(collected.Files, archive) };
    }
    foreach (var w in migrationWarnings) Console.WriteLine($"  ! {w}");
}
if (AppHome.IsClickOnce) Console.WriteLine($"მონაცემების საქაღალდე: {AppHome.Directory}");

var state = RunState.Load(options.DataDir);
var analyzer = new ActivityAnalyzer(options.DataDir, collected.Files);
var data = analyzer.Analyze(options.LocalName, state.LastRun);
foreach (var w in analyzer.Warnings) Console.WriteLine($"  ! {w}");
foreach (var c in analyzer.CopiesOfLocal)
    Console.WriteLine($"„{c}“ შეიცავს ამავე კომპიუტერის ლოგების ასლს და ცალკე მოწყობილობად არ ითვლება.");
if (data.Days.Count == 0)
{
    Console.WriteLine("ანალიზისთვის ლოგები ვერ მოიძებნა.");
    return 2;
}

Console.WriteLine();
Console.WriteLine($"კომპიუტერები: {string.Join(", ", data.Machines)}   პერიოდი: {data.Days[0].Day} — {data.Days[^1].Day} ({data.Days.Count} დღე)");
Console.WriteLine();
Console.WriteLine($"{"კვირა (ორშ.)",-14}{"დღე",5}{"აქტიური სთ",13}{"უმოქმ. სთ",12}");
static string H(int minutes) => (minutes / 60.0).ToString("0.0", CultureInfo.InvariantCulture);
foreach (var week in data.Days.GroupBy(d =>
         {
             var dt = DateTime.ParseExact(d.Day, "yyyy-MM-dd", CultureInfo.InvariantCulture);
             return dt.AddDays(-(((int)dt.DayOfWeek + 6) % 7)).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
         }))
{
    Console.WriteLine($"{week.Key,-14}{week.Count(d => d.Stats.Union > 0),5}{H(week.Sum(d => d.Stats.Union)),13}{H(week.Sum(d => d.Stats.Idle)),12}");
}
Console.WriteLine($"{"სულ",-14}{data.Days.Count(d => d.Stats.Union > 0),5}{H(data.Days.Sum(d => d.Stats.Union)),13}{H(data.Days.Sum(d => d.Stats.Idle)),12}");

string report;
try
{
    report = ReportWriter.Write(data, options.OutDir);
}
catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
{
    var fallback = Path.Combine(Path.GetTempPath(), "WorkpulsActivityReport", "reports");
    Console.WriteLine($"! {options.OutDir} — ჩაწერა ვერ მოხერხდა ({ex.Message}); ვწერ {fallback}-ში.");
    report = ReportWriter.Write(data, fallback);
}
Console.WriteLine();
Console.WriteLine($"რეპორტი: {report}");

state.LastRun = DateTime.Now;
try { state.Save(options.DataDir); }
catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }

if (options.Open)
{
    try { Process.Start(new ProcessStartInfo(report) { UseShellExecute = true }); }
    catch (Exception ex) { Console.WriteLine($"ბრაუზერში გახსნა ვერ მოხერხდა: {ex.Message}"); }
}
return 0;

static IReadOnlyList<LogFile> MergeArchive(IReadOnlyList<LogFile> current, LogArchive archive)
{
    var files = archive.Files().ToDictionary(f => f.Key);
    foreach (var f in current)
        if (!files.TryGetValue(f.Key, out var a) || a.Length < f.Length) files[f.Key] = f;
    return files.Values.ToList();
}
