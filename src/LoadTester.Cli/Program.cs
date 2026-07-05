using LoadTester.Cli;
using LoadTester.Core.Configuration;
using LoadTester.Core.Execution;
using LoadTester.Core.Reporting;

CliOptions options;
try
{
    options = CliOptions.Parse(args);
}
catch (ArgumentException ex)
{
    Console.Error.WriteLine(ex.Message);
    return 2;
}

if (options.ShowHelp)
{
    Console.WriteLine(CliOptions.Usage);
    return 0;
}

LoadTestConfig config;
string configDir;
try
{
    config = ConfigLoader.Load(options.ConfigPath);
    configDir = Path.GetDirectoryName(Path.GetFullPath(options.ConfigPath))!;

    var validation = ConfigValidator.Validate(config, configDir);
    foreach (var warning in validation.Warnings)
        Console.WriteLine($"warning: {warning}");
    if (!validation.IsValid)
    {
        foreach (var error in validation.Errors)
            Console.Error.WriteLine($"error: {error}");
        Console.Error.WriteLine($"\nConfig {options.ConfigPath} is invalid ({validation.Errors.Count} error(s)).");
        return 2;
    }
}
catch (ConfigException ex)
{
    Console.Error.WriteLine($"error: {ex.Message}");
    return 2;
}

if (options.ValidateOnly)
{
    Console.WriteLine($"Config {options.ConfigPath} is valid: " +
                      $"{config.Endpoints.Count} endpoint(s), {config.Scenarios.Count} scenario(s).");
    return 0;
}

if (options.ListOnly)
{
    Console.WriteLine("Endpoints:");
    foreach (var e in config.Endpoints)
        Console.WriteLine($"  {e.Name,-28} {e.Method,-6} {e.Path}");
    Console.WriteLine("\nScenarios:");
    foreach (var s in config.Scenarios)
        Console.WriteLine($"  {s.Name,-28} {(s.Enabled ? "enabled " : "disabled")} " +
                          $"steps: {string.Join(" → ", s.Steps.Select(st => st.Name ?? st.Endpoint))}");
    return 0;
}

// First Ctrl+C: stop starting new requests, finish in-flight, report partial results.
// Second Ctrl+C: abort in-flight requests. Third: let the runtime kill the process.
using var gracefulCts = new CancellationTokenSource();
using var hardCts = new CancellationTokenSource();
var interruptCount = 0;
Console.CancelKeyPress += (_, e) =>
{
    switch (Interlocked.Increment(ref interruptCount))
    {
        case 1:
            e.Cancel = true;
            Console.WriteLine("\nStopping gracefully — finishing in-flight requests (Ctrl+C again to abort)...");
            gracefulCts.Cancel();
            break;
        case 2:
            e.Cancel = true;
            Console.WriteLine("\nAborting in-flight requests...");
            hardCts.Cancel();
            break;
    }
};

try
{
    var runOptions = new RunOptions
    {
        ConfigFile = Path.GetFullPath(options.ConfigPath),
        ConfigDirectory = configDir,
        ScenarioFilter = options.Scenarios,
        Quiet = options.Quiet,
        StatusIntervalSeconds = options.StatusIntervalSeconds,
    };

    if (!options.Quiet)
    {
        var toRun = LoadEngine.SelectScenarios(config, options.Scenarios);
        Console.WriteLine($"loadtester — {options.ConfigPath} → {config.Settings.BaseAddress ?? "(absolute URLs)"}");
        Console.WriteLine($"scenarios: {string.Join(", ", toRun.Select(s => s.Name))}\n");
    }

    var report = await new LoadEngine().RunAsync(config, runOptions, gracefulCts.Token, hardCts.Token);

    var formats = options.Formats ?? config.Settings.Reports.Formats;
    if (formats.Contains("console", StringComparer.OrdinalIgnoreCase))
        new ConsoleReporter().Write(report);

    var outputRoot = options.OutputFolder ?? config.Settings.Reports.Folder;
    if (!Path.IsPathRooted(outputRoot))
        outputRoot = Path.Combine(Environment.CurrentDirectory, outputRoot);

    // Millisecond resolution plus a -N suffix so concurrent runs never clobber each other's reports.
    var runFolder = Path.Combine(outputRoot, report.StartedUtc.ToString("yyyy-MM-dd_HHmmss_fff"));
    for (var n = 2; Directory.Exists(runFolder); n++)
        runFolder = Path.Combine(outputRoot, report.StartedUtc.ToString("yyyy-MM-dd_HHmmss_fff") + "-" + n);

    var written = ReportWriter.Write(report, formats, runFolder);
    if (written.Count > 0)
    {
        Console.WriteLine();
        foreach (var path in written)
            Console.WriteLine($"report: {path}");
    }

    if (hardCts.IsCancellationRequested) return 130;
    if (report.Thresholds.Any(t => !t.Passed))
    {
        Console.Error.WriteLine("\nOne or more thresholds FAILED.");
        return 1;
    }
    return 0;
}
catch (ConfigException ex)
{
    Console.Error.WriteLine($"error: {ex.Message}");
    return 2;
}
catch (Exception ex)
{
    Console.Error.WriteLine($"fatal: {ex}");
    return 3;
}
