namespace WorkpulsActivityReport;

public sealed record ImportSource(string Machine, string Path);

public sealed class Options
{
    public string LogsRoot { get; set; } = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "workpuls-agent", "logs");

    public string DataDir { get; set; } = System.IO.Path.Combine(AppHome.Directory, "data");
    public string OutDir { get; set; } = System.IO.Path.Combine(AppHome.Directory, "reports");
    public string LocalName { get; set; } = "PC";
    public List<ImportSource> Imports { get; } = new();
    public bool Collect { get; set; } = true;
    public bool Open { get; set; } = true;

    public const string Usage = """
        WorkpulsActivityReport - Workpuls/Insightful აგენტის ლოგების ანალიზი და HTML რეპორტი.

        ყოველი გაშვებისას ლოგები კოპირდება (gzip) აპლიკაციის data\ საქაღალდეში, რომ აგენტის
        მიერ ძველი ფაილების წაშლის შემდეგაც დარჩეს. რეპორტი იქმნება მთელ დაგროვილ პერიოდზე.
        <app> = exe-ის საქაღალდე; ClickOnce ინსტალაციისას - %LOCALAPPDATA%\WorkpulsActivityReport
        (რომ განახლებისას/წაშლისას არქივი არ დაიკარგოს).

        პარამეტრები:
          --logs <dir>            აგენტის ლოგების საქაღალდე (ნაგულისხმევი: %APPDATA%\workpuls-agent\logs)
          --name <name>           ამ კომპიუტერის სახელი რეპორტში (ნაგულისხმევი: PC)
          --import <Name>=<path>  სხვა კომპიუტერის ლოგები (საქაღალდე ან .zip), მაგ: --import Laptop=D:\laptop-logs.zip
                                  (შეიძლება რამდენჯერმე). main\<Name>\ ქვესაქაღალდეებიც ავტომატურად იკითხება.
          --data <dir>            არქივის საქაღალდე (ნაგულისხმევი: <app>\data)
          --out <dir>             რეპორტების საქაღალდე (ნაგულისხმევი: <app>\reports)
          --no-collect            ახალი ლოგები არ წამოიღოს, მხოლოდ არქივიდან ააგოს რეპორტი
          --no-open               რეპორტი ბრაუზერში არ გახსნას
          -h, --help              დახმარება
        """;

    public static Options? Parse(string[] args, TextWriter error)
    {
        var o = new Options();
        for (int i = 0; i < args.Length; i++)
        {
            string a = args[i];
            string Next()
            {
                if (i + 1 >= args.Length) throw new ArgumentException($"'{a}' მოითხოვს მნიშვნელობას.");
                return args[++i];
            }

            try
            {
                switch (a.ToLowerInvariant())
                {
                    case "--logs": o.LogsRoot = System.IO.Path.GetFullPath(Next()); break;
                    case "--name": o.LocalName = Next(); break;
                    case "--data": o.DataDir = System.IO.Path.GetFullPath(Next()); break;
                    case "--out": o.OutDir = System.IO.Path.GetFullPath(Next()); break;
                    case "--no-collect": o.Collect = false; break;
                    case "--no-open": o.Open = false; break;
                    case "--import":
                        var v = Next();
                        int eq = v.IndexOf('=');
                        if (eq <= 0 || eq == v.Length - 1) throw new ArgumentException("--import ფორმატი: <Name>=<path>");
                        o.Imports.Add(new ImportSource(v[..eq].Trim(), System.IO.Path.GetFullPath(v[(eq + 1)..].Trim('"', ' '))));
                        break;
                    case "-h" or "--help" or "/?":
                        return null;
                    default:
                        throw new ArgumentException($"უცნობი პარამეტრი: {a}");
                }
            }
            catch (ArgumentException ex)
            {
                error.WriteLine(ex.Message);
                return null;
            }
        }
        return o;
    }
}
