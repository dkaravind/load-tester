namespace LoadTester.Cli;

public sealed class CliOptions
{
    private static readonly HashSet<string> KnownFormats =
        new(StringComparer.OrdinalIgnoreCase) { "console", "json", "csv", "html" };

    public string ConfigPath { get; private set; } = "endpoints.json";
    public List<string> Scenarios { get; } = new();
    public string? OutputFolder { get; private set; }
    public List<string>? Formats { get; private set; }
    public bool ValidateOnly { get; private set; }
    public bool ListOnly { get; private set; }
    public bool Quiet { get; private set; }
    public double StatusIntervalSeconds { get; private set; } = 5;
    public bool ShowHelp { get; private set; }

    public static CliOptions Parse(string[] args)
    {
        var options = new CliOptions();
        for (var i = 0; i < args.Length; i++)
        {
            string Next(string flag) =>
                i + 1 < args.Length ? args[++i] : throw new ArgumentException($"{flag} requires a value.");

            switch (args[i])
            {
                case "--config" or "-c":
                    options.ConfigPath = Next("--config");
                    break;
                case "--scenario" or "-s":
                    options.Scenarios.AddRange(
                        Next("--scenario").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
                    break;
                case "--output" or "-o":
                    options.OutputFolder = Next("--output");
                    break;
                case "--formats" or "-f":
                    options.Formats = Next("--formats")
                        .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                        .ToList();
                    if (options.Formats.Count == 0)
                        throw new ArgumentException("--formats requires at least one of: console, json, csv, html.");
                    foreach (var format in options.Formats.Where(f => !KnownFormats.Contains(f)))
                        throw new ArgumentException(
                            $"Unknown report format '{format}'. Expected: console, json, csv or html.");
                    break;
                case "--interval":
                    if (!double.TryParse(Next("--interval"), out var interval) || interval <= 0)
                        throw new ArgumentException("--interval requires a positive number of seconds.");
                    options.StatusIntervalSeconds = interval;
                    break;
                case "--validate":
                    options.ValidateOnly = true;
                    break;
                case "--list":
                    options.ListOnly = true;
                    break;
                case "--quiet" or "-q":
                    options.Quiet = true;
                    break;
                case "--help" or "-h":
                    options.ShowHelp = true;
                    break;
                default:
                    throw new ArgumentException($"Unknown argument '{args[i]}'. Use --help for usage.");
            }
        }
        return options;
    }

    public const string Usage = """
        loadtester — configuration-driven HTTP load testing

        Usage:
          loadtester [--config endpoints.json] [options]

        Options:
          -c, --config <path>      Config file (default: ./endpoints.json)
          -s, --scenario <names>   Run only these scenarios (comma separated or repeat the flag).
                                   Explicitly selected scenarios run even if enabled=false.
          -o, --output <folder>    Report folder (default: settings.reports.folder from the config)
          -f, --formats <list>     Override report formats: console,json,csv,html
              --interval <secs>    Seconds between live status lines (default 5)
              --validate           Validate the config and exit
              --list               List endpoints and scenarios and exit
          -q, --quiet              Suppress the live status line
          -h, --help               Show this help

        Exit codes:
          0 success · 1 thresholds failed · 2 config/argument error · 3 unexpected error · 130 aborted
        """;
}
