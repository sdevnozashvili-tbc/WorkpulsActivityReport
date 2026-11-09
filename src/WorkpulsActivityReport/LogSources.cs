using System.IO.Compression;
using System.Text.RegularExpressions;

namespace WorkpulsActivityReport;

public enum LogKind { Main, Tracker }

/// <summary>A log file to analyze: either an archived (gzip) copy or the original file read directly.</summary>
public sealed record LogFile(string Machine, LogKind Kind, string Name, long Length, Func<Stream> Open, bool Compressed, string Location)
{
    public string Key => LogSources.Key(Machine, Kind, Name);

    public IEnumerable<string> ReadLines()
    {
        using var raw = Open();
        using var stream = Compressed ? new GZipStream(raw, CompressionMode.Decompress) : raw;
        using var reader = new StreamReader(stream);
        while (reader.ReadLine() is { } line) yield return line;
    }
}

/// <summary>Finds agent log files in the logs folder and in <c>--import</c> sources.</summary>
public static partial class LogSources
{
    [GeneratedRegex(@"^\d{4}-\d\d-\d\d\.log(\.\d+)?$", RegexOptions.IgnoreCase)]
    private static partial Regex MainLogName();

    [GeneratedRegex(@"^tracker_\d{4}_\d\d_\d\d_\d+\.log$", RegexOptions.IgnoreCase)]
    private static partial Regex TrackerLogName();

    internal static LogKind? KindOf(string fileName) =>
        MainLogName().IsMatch(fileName) ? LogKind.Main :
        TrackerLogName().IsMatch(fileName) ? LogKind.Tracker : null;

    public static string Key(string machine, LogKind kind, string name) => $"{machine}/{kind.ToString().ToLowerInvariant()}/{name}";

    internal static string SafeName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var s = new string(name.Select(c => invalid.Contains(c) || c == '/' ? '_' : c).ToArray()).Trim();
        return s.Length == 0 ? "_" : s;
    }

    /// <summary>Opens a file the agent may still be writing to, without locking it.</summary>
    public static Stream OpenShared(string path) =>
        new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);

    public static List<LogFile> Enumerate(Options o, List<string> warnings)
    {
        var result = new List<LogFile>();
        var local = SafeName(o.LocalName);

        var main = Path.Combine(o.LogsRoot, "main");
        if (Directory.Exists(main))
        {
            FromDirectory(local, main, result, warnings);
            foreach (var sub in SafeEnumerate(() => Directory.EnumerateDirectories(main), main, warnings))
                FromDirectory(SafeName(Path.GetFileName(sub)), sub, result, warnings);
        }
        else
        {
            warnings.Add($"საქაღალდე ვერ მოიძებნა: {main}");
        }

        var tracker = Path.Combine(o.LogsRoot, "tracker");
        if (Directory.Exists(tracker)) FromDirectory(local, tracker, result, warnings);

        foreach (var imp in o.Imports)
        {
            var machine = SafeName(imp.Machine);
            if (Directory.Exists(imp.Path)) FromDirectory(machine, imp.Path, result, warnings);
            else if (File.Exists(imp.Path) && imp.Path.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) FromZip(machine, imp.Path, result, warnings);
            else warnings.Add($"--import: ვერ მოიძებნა საქაღალდე ან .zip: {imp.Path}");
        }
        return result;
    }

    private static void FromDirectory(string machine, string dir, List<LogFile> result, List<string> warnings)
    {
        foreach (var path in SafeEnumerate(() => Directory.EnumerateFiles(dir), dir, warnings))
        {
            var name = Path.GetFileName(path);
            var kind = KindOf(name);
            if (kind is null) continue;
            try
            {
                // NTFS directory listings report a stale size for files the agent still has open, so use the stream length.
                long length;
                using (var s = OpenShared(path)) length = s.Length;
                result.Add(new LogFile(machine, kind.Value, name, length, () => OpenShared(path), Compressed: false, path));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                warnings.Add($"ვერ წავიკითხე {path}: {ex.Message}");
            }
        }
    }

    private static void FromZip(string machine, string zipPath, List<LogFile> result, List<string> warnings)
    {
        try
        {
            using var fs = OpenShared(zipPath);
            using var zip = new ZipArchive(fs, ZipArchiveMode.Read);
            foreach (var e in zip.Entries)
            {
                var kind = KindOf(e.Name);
                if (kind is null) continue;
                var entryName = e.FullName;
                result.Add(new LogFile(machine, kind.Value, e.Name, e.Length, () => ReadZipEntry(zipPath, entryName), Compressed: false, $"{zipPath}!{entryName}"));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            warnings.Add($"ვერ წავიკითხე {zipPath}: {ex.Message}");
        }
    }

    private static Stream ReadZipEntry(string zipPath, string entryName)
    {
        using var fs = OpenShared(zipPath);
        using var zip = new ZipArchive(fs, ZipArchiveMode.Read);
        var ms = new MemoryStream();
        using (var es = zip.GetEntry(entryName)!.Open()) es.CopyTo(ms);
        ms.Position = 0;
        return ms;
    }

    private static IEnumerable<string> SafeEnumerate(Func<IEnumerable<string>> list, string dir, List<string> warnings)
    {
        try { return list().ToList(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            warnings.Add($"ვერ წავიკითხე {dir}: {ex.Message}");
            return [];
        }
    }
}
