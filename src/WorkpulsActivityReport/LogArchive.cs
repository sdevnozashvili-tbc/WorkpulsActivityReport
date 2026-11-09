using System.IO.Compression;
using System.Text.Json;

namespace WorkpulsActivityReport;

/// <summary>
/// Append-only, gzip-compressed copy of the agent logs. The agent keeps only the last ~10 files,
/// so every run copies new or grown files here and never deletes anything.
/// </summary>
public sealed class LogArchive
{
    private sealed record Entry(long Length, DateTime LastWriteUtc);

    private readonly string _root;
    private readonly string _manifestPath;
    private readonly Dictionary<string, Entry> _manifest;

    /// <exception cref="IOException">The archive folder cannot be created.</exception>
    /// <exception cref="UnauthorizedAccessException">The archive folder is not accessible.</exception>
    public LogArchive(string dataDir, bool readOnly = false)
    {
        _root = Path.Combine(dataDir, "archive");
        _manifestPath = Path.Combine(_root, "manifest.json");
        if (!readOnly) Directory.CreateDirectory(_root);
        try
        {
            _manifest = File.Exists(_manifestPath)
                ? JsonSerializer.Deserialize<Dictionary<string, Entry>>(File.ReadAllText(_manifestPath)) ?? new()
                : new();
        }
        catch (JsonException)
        {
            _manifest = new();
        }
    }

    /// <summary>Copies the file unless the archive already has an equal or longer copy (logs only grow).</summary>
    /// <returns><c>true</c> if copied, <c>false</c> if the archive was already up to date.</returns>
    public bool Store(LogFile source)
    {
        _manifest.TryGetValue(source.Key, out var existing);
        if (existing is not null && existing.Length >= source.Length) return false;

        var dest = PathOf(source.Key);
        Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
        var tmp = dest + ".tmp";
        long copied = 0;
        try
        {
            using (var raw = source.Open())
            using (var src = source.Compressed ? new GZipStream(raw, CompressionMode.Decompress) : raw)
            using (var outFile = File.Create(tmp))
            using (var gz = new GZipStream(outFile, CompressionLevel.SmallestSize))
            {
                var buf = new byte[1 << 16];
                int n;
                while ((n = src.Read(buf, 0, buf.Length)) > 0) { gz.Write(buf, 0, n); copied += n; }
            }

            if (existing is not null && existing.Length >= copied) return false;
            File.Move(tmp, dest, overwrite: true);
        }
        finally
        {
            TryDelete(tmp);
        }

        _manifest[source.Key] = new Entry(copied, DateTime.UtcNow);
        return true;
    }

    public IReadOnlyList<LogFile> Files() =>
        _manifest.Select(kv =>
        {
            var parts = kv.Key.Split('/');
            var path = PathOf(kv.Key);
            return new LogFile(parts[0], Enum.Parse<LogKind>(parts[1], ignoreCase: true), parts[2], kv.Value.Length,
                () => File.OpenRead(path), Compressed: true, path);
        })
        .Where(f => File.Exists(f.Location))
        .ToList();

    public void SaveManifest()
    {
        var tmp = _manifestPath + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(_manifest, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(tmp, _manifestPath, overwrite: true);
    }

    private string PathOf(string key)
    {
        var parts = key.Split('/');
        return Path.Combine(_root, parts[0], parts[1], parts[2] + ".gz");
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}

/// <summary>
/// Copies source logs into the archive and returns the set of files to analyze. Any file that cannot be archived
/// (permissions, disk full, locked archive, no archive at all) is read directly from its original location instead.
/// </summary>
public static class LogCollector
{
    public sealed record Result(IReadOnlyList<LogFile> Files, int Copied, int Unchanged, int Direct, List<string> Warnings);

    public static Result Collect(Options o, LogArchive? archive)
    {
        var warnings = new List<string>();
        var sources = o.Collect ? LogSources.Enumerate(o, warnings) : [];

        if (archive is null)
            return new Result(sources, 0, 0, sources.Count, warnings);

        int copied = 0, unchanged = 0;
        var direct = new List<LogFile>();
        foreach (var f in sources)
        {
            try
            {
                if (archive.Store(f)) copied++; else unchanged++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                warnings.Add($"არქივში ვერ ჩავწერე {f.Location} ({ex.Message}); იკითხება პირდაპირ.");
                direct.Add(f);
            }
        }

        if (o.Collect)
        {
            try { archive.SaveManifest(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                warnings.Add($"არქივის manifest ვერ შეინახა: {ex.Message}");
            }
        }

        // Prefer the longer copy when both the archive and a direct source exist for the same file.
        var files = archive.Files().ToDictionary(f => f.Key);
        foreach (var f in direct)
            if (!files.TryGetValue(f.Key, out var a) || a.Length < f.Length) files[f.Key] = f;

        return new Result(files.Values.ToList(), copied, unchanged, direct.Count, warnings);
    }
}
