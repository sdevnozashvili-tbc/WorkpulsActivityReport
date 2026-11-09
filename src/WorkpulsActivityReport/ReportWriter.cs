using System.Globalization;
using System.Reflection;
using System.Text.Json;

namespace WorkpulsActivityReport;

public static class ReportWriter
{
    private const string Placeholder = "/*__DATA__*/null";

    public static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public static string Render(ReportData data)
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("template.html")
            ?? throw new InvalidOperationException("template.html resource is missing.");
        using var reader = new StreamReader(stream);
        var template = reader.ReadToEnd();
        if (!template.Contains(Placeholder)) throw new InvalidOperationException("Data placeholder not found in template.");
        // The default encoder escapes '<', '>' and '&', so the JSON is safe inside a <script> block.
        return template.Replace(Placeholder, JsonSerializer.Serialize(data, Json));
    }

    /// <summary>Writes <c>report.html</c> (latest) plus a timestamped copy; returns the latest report path.</summary>
    public static string Write(ReportData data, string outDir)
    {
        Directory.CreateDirectory(outDir);
        var html = Render(data);
        var latest = Path.Combine(outDir, "report.html");
        File.WriteAllText(latest, html);
        File.WriteAllText(Path.Combine(outDir, $"report-{DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture)}.html"), html);
        return latest;
    }
}

public sealed class RunState
{
    public DateTime? LastRun { get; set; }

    public static RunState Load(string dataDir)
    {
        var path = PathOf(dataDir);
        if (!File.Exists(path)) return new RunState();
        try { return JsonSerializer.Deserialize<RunState>(File.ReadAllText(path)) ?? new RunState(); }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException) { return new RunState(); }
    }

    public void Save(string dataDir)
    {
        Directory.CreateDirectory(dataDir);
        File.WriteAllText(PathOf(dataDir), JsonSerializer.Serialize(this));
    }

    private static string PathOf(string dataDir) => Path.Combine(dataDir, "state.json");
}
