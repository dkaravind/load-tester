using System.Diagnostics;
using LoadTester.Core.Configuration;
using LoadTester.Core.Data;
using LoadTester.Core.Http;
using LoadTester.Core.Metrics;

namespace LoadTester.Core.Execution;

public sealed class RunOptions
{
    public required string ConfigFile { get; init; }
    public required string ConfigDirectory { get; init; }

    /// <summary>Scenario names to run; empty means all enabled scenarios.</summary>
    public List<string> ScenarioFilter { get; init; } = new();

    /// <summary>Suppress the periodic live status line.</summary>
    public bool Quiet { get; init; }

    /// <summary>Seconds between live status lines.</summary>
    public double StatusIntervalSeconds { get; init; } = 5;

    /// <summary>Sink for live status lines; defaults to Console.WriteLine.</summary>
    public Action<string> StatusSink { get; init; } = Console.WriteLine;
}

/// <summary>Top-level orchestrator: picks scenarios, owns the shared HttpClient, aggregates reports.</summary>
public sealed class LoadEngine
{
    public async Task<RunReport> RunAsync(
        LoadTestConfig config, RunOptions options, CancellationToken graceful, CancellationToken hard)
    {
        var scenarios = SelectScenarios(config, options.ScenarioFilter);
        if (scenarios.Count == 0)
            throw new ConfigException("No scenarios to run: none are enabled and none were selected with --scenario.");

        var startedUtc = DateTime.UtcNow;
        var runClock = Stopwatch.StartNew();
        using var client = HttpClientBuilder.Create(config.Settings);
        var monitor = new LiveMonitor();
        var endpoints = config.Endpoints.ToDictionary(e => e.Name, StringComparer.OrdinalIgnoreCase);

        using var statusCts = new CancellationTokenSource();
        var statusTask = options.Quiet
            ? Task.CompletedTask
            : RunStatusLoopAsync(monitor, runClock, options, statusCts.Token);

        var results = new List<(ScenarioConfig Scenario, ScenarioRunResult Result)>();
        try
        {
            if (config.Settings.RunScenariosInParallel)
            {
                var tasks = scenarios
                    .Select(s => RunScenarioAsync(s, config, endpoints, client, monitor, options, graceful, hard))
                    .ToList();
                var completed = await Task.WhenAll(tasks).ConfigureAwait(false);
                results.AddRange(scenarios.Zip(completed));
            }
            else
            {
                foreach (var scenario in scenarios)
                {
                    if (graceful.IsCancellationRequested || hard.IsCancellationRequested) break;
                    results.Add((scenario,
                        await RunScenarioAsync(scenario, config, endpoints, client, monitor, options, graceful, hard)
                            .ConfigureAwait(false)));
                }
            }
        }
        finally
        {
            statusCts.Cancel();
            await statusTask.ConfigureAwait(false);
        }

        var scenarioReports = results
            .Select(r => StatsCalculator.BuildScenarioReport(
                r.Scenario.Name,
                r.Result.Records,
                r.Result.MeasuredSeconds,
                r.Result.PhaseDescriptions,
                r.Result.IterationsCompleted,
                r.Result.IterationsFailed,
                r.Result.IterationsThrottled,
                r.Result.IterationsAbandoned,
                r.Scenario.Steps.Select(s => s.Name ?? s.Endpoint).ToList()))
            .ToList();

        return new RunReport
        {
            StartedUtc = startedUtc,
            ConfigFile = options.ConfigFile,
            TotalDurationSeconds = Math.Round(runClock.Elapsed.TotalSeconds, 2),
            Interrupted = graceful.IsCancellationRequested || hard.IsCancellationRequested,
            Scenarios = scenarioReports,
            Thresholds = ThresholdEvaluator.Evaluate(results),
        };
    }

    private static async Task<ScenarioRunResult> RunScenarioAsync(
        ScenarioConfig scenario,
        LoadTestConfig config,
        IReadOnlyDictionary<string, EndpointConfig> endpoints,
        HttpClient client,
        LiveMonitor monitor,
        RunOptions options,
        CancellationToken graceful,
        CancellationToken hard)
    {
        var dataFeed = scenario.DataFeed is { } feedConfig
            ? DataFeed.Load(feedConfig, options.ConfigDirectory)
            : null;

        var metrics = new MetricsCollector();
        var executor = new IterationExecutor(scenario, endpoints, config.Settings, client, metrics, monitor, dataFeed);
        var runner = new ScenarioRunner(scenario, executor, metrics, monitor, DrainGrace(scenario, config.Settings, endpoints));
        return await runner.RunAsync(graceful, hard).ConfigureAwait(false);
    }

    /// <summary>
    /// Worst-case duration of one iteration: every step's effective timeout plus think time,
    /// with scheduling slack. Open-model stragglers get this long to finish before being abandoned.
    /// </summary>
    internal static TimeSpan DrainGrace(
        ScenarioConfig scenario, GlobalSettings settings, IReadOnlyDictionary<string, EndpointConfig> endpoints)
    {
        double seconds = 5;
        foreach (var step in scenario.Steps)
        {
            seconds += endpoints.TryGetValue(step.Endpoint, out var endpoint)
                ? endpoint.TimeoutSeconds ?? settings.RequestTimeoutSeconds
                : settings.RequestTimeoutSeconds;
            seconds += step.PauseAfterSeconds;
        }
        return TimeSpan.FromSeconds(seconds);
    }

    public static List<ScenarioConfig> SelectScenarios(LoadTestConfig config, List<string> filter)
    {
        if (filter.Count == 0)
            return config.Scenarios.Where(s => s.Enabled).ToList();

        var unknown = filter
            .Where(name => !config.Scenarios.Any(s => s.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
            .ToList();
        if (unknown.Count > 0)
            throw new ConfigException(
                $"Unknown scenario(s): {string.Join(", ", unknown)}. " +
                $"Available: {string.Join(", ", config.Scenarios.Select(s => s.Name))}");

        // Explicit selection runs the scenario even when enabled=false.
        return config.Scenarios
            .Where(s => filter.Any(name => name.Equals(s.Name, StringComparison.OrdinalIgnoreCase)))
            .ToList();
    }

    private static async Task RunStatusLoopAsync(
        LiveMonitor monitor, Stopwatch runClock, RunOptions options, CancellationToken token)
    {
        var interval = TimeSpan.FromSeconds(Math.Max(options.StatusIntervalSeconds, 0.1));
        while (!token.IsCancellationRequested)
        {
            try { await Task.Delay(interval, token).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
            options.StatusSink(monitor.DrainStatusLine(runClock.Elapsed, interval.TotalSeconds));
        }
    }
}
