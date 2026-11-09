namespace WorkpulsActivityReport;

/// <summary>
/// Where <c>data\</c> and <c>reports\</c> live. A portable copy keeps them next to the exe; a ClickOnce install
/// runs from a versioned folder under <c>%LOCALAPPDATA%\Apps\2.0</c> that is replaced on update and deleted on
/// uninstall, so it uses <c>%LOCALAPPDATA%\WorkpulsActivityReport</c> instead.
/// </summary>
public static class AppHome
{
    public static string ClickOnceRoot { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Apps", "2.0");

    public static string PersistentDirectory { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WorkpulsActivityReport");

    public static bool IsClickOnce { get; } = IsUnder(AppContext.BaseDirectory, ClickOnceRoot);

    public static string Directory { get; } = IsClickOnce ? PersistentDirectory : AppContext.BaseDirectory;

    internal static bool IsUnder(string path, string root) =>
        Path.GetFullPath(path).StartsWith(Path.GetFullPath(root).TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// <c>data\</c> folders left inside ClickOnce install folders by earlier versions
    /// (layout: <c>Apps\2.0\&lt;x&gt;\&lt;y&gt;\&lt;app folder&gt;\data</c>).
    /// </summary>
    public static IEnumerable<string> LegacyClickOnceDataDirs(string currentDataDir)
    {
        if (!System.IO.Directory.Exists(ClickOnceRoot)) return [];
        try
        {
            return System.IO.Directory.EnumerateDirectories(ClickOnceRoot)
                .SelectMany(System.IO.Directory.EnumerateDirectories)
                .SelectMany(System.IO.Directory.EnumerateDirectories)
                .Select(d => Path.Combine(d, "data"))
                .Where(d => File.Exists(Path.Combine(d, "archive", "manifest.json")))
                .Where(d => !string.Equals(Path.GetFullPath(d).TrimEnd('\\'), Path.GetFullPath(currentDataDir).TrimEnd('\\'),
                    StringComparison.OrdinalIgnoreCase))
                .ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    /// <summary>Merges archives from older ClickOnce installs into <paramref name="archive"/>; returns files copied.</summary>
    public static int MigrateLegacy(LogArchive archive, string currentDataDir, List<string> warnings)
    {
        var copied = 0;
        foreach (var dir in LegacyClickOnceDataDirs(currentDataDir))
        {
            try
            {
                foreach (var f in new LogArchive(dir, readOnly: true).Files())
                    if (archive.Store(f)) copied++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                warnings.Add($"ძველი ინსტალაციის არქივი ვერ გადმოვიტანე ({dir}): {ex.Message}");
            }
        }
        if (copied > 0) archive.SaveManifest();
        return copied;
    }
}
