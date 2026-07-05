using System.Diagnostics;
using LoadTester.Core.Configuration;
using LoadTester.Core.Metrics;

namespace LoadTester.Core.Execution;

public sealed class ScenarioRunResult
{
    public required IReadOnlyList<RequestRecord> Records { get; init; }
    public required double MeasuredSeconds { get; init; }
    public required List<string> PhaseDescriptions { get; init; }
    public required long IterationsCompleted { get; init; }
    public required long IterationsFailed { get; init; }
    public required long IterationsThrottled { get; init; }

    /// <summary>Open-model iterations still in flight when the drain grace expired; their results were lost.</summary>
    public required long IterationsAbandoned { get; init; }
}

/// <summary>
/// Drives one scenario through warmup and its load phases.
/// Closed-model phases (constant/ramp) keep N virtual users looping;
/// open-model phases (injectRate/rampRate) start iterations on a timer regardless of completion.
/// </summary>
public sealed class ScenarioRunner
{
    private static readonly TimeSpan SchedulerTick = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan IdleWorkerPoll = TimeSpan.FromMilliseconds(50);

    private readonly ScenarioConfig _scenario;
    private readonly IterationExecutor _executor;
    private readonly MetricsCollector _metrics;
    private readonly LiveMonitor _monitor;
    private readonly TimeSpan _drainGrace;

    private long _iterationCounter;
    private long _iterationsCompleted;
    private long _iterationsFailed;
    private long _iterationsThrottled;
    private int _pending; // open-model iterations in flight

    /// <param name="drainGrace">
    /// How long to wait for open-model stragglers at scenario end. Must cover the worst-case
    /// iteration: the sum of every step's effective timeout plus think time (see LoadEngine).
    /// </param>
    public ScenarioRunner(
        ScenarioConfig scenario,
        IterationExecutor executor,
        MetricsCollector metrics,
        LiveMonitor monitor,
        TimeSpan drainGrace)
    {
        _scenario = scenario;
        _executor = executor;
        _metrics = metrics;
        _monitor = monitor;
        _drainGrace = drainGrace;
    }

    /// <param name="graceful">Stops starting new work; in-flight requests finish and are reported.</param>
    /// <param name="hard">Aborts in-flight requests; they are not recorded.</param>
    public async Task<ScenarioRunResult> RunAsync(CancellationToken graceful, CancellationToken hard)
    {
        if (_scenario.Warmup is { } warmup && !graceful.IsCancellationRequested)
        {
            _monitor.SetPhase(_scenario.Name, $"warmup c={warmup.Concurrency}");
            await RunClosedPhaseAsync(
                "warmup", warmup.DurationSeconds, _ => warmup.Concurrency, warmup.Concurrency,
                graceful, hard).ConfigureAwait(false);

            // Warmup requests are not recorded, so their iteration tallies must not be reported either.
            Interlocked.Exchange(ref _iterationsCompleted, 0);
            Interlocked.Exchange(ref _iterationsFailed, 0);
        }

        _metrics.StartMeasurement();
        var descriptions = new List<string>();

        foreach (var phase in _scenario.Phases)
        {
            if (graceful.IsCancellationRequested || hard.IsCancellationRequested) break;

            var description = Describe(phase);
            descriptions.Add(description);
            _monitor.SetPhase(_scenario.Name, description);
            var phaseName = phase.Name ?? phase.Type;

            switch (phase.Type.ToLowerInvariant())
            {
                case "constant":
                    await RunClosedPhaseAsync(phaseName, phase.DurationSeconds,
                        _ => phase.Concurrency, phase.Concurrency, graceful, hard).ConfigureAwait(false);
                    break;

                case "ramp":
                    await RunClosedPhaseAsync(phaseName, phase.DurationSeconds,
                        progress => Interpolate(phase.From, phase.To, progress),
                        Math.Max(phase.From, phase.To), graceful, hard).ConfigureAwait(false);
                    break;

                case "injectrate":
                    await RunOpenPhaseAsync(phaseName, phase.DurationSeconds,
                        _ => phase.RatePerSecond, graceful, hard).ConfigureAwait(false);
                    break;

                case "ramprate":
                    await RunOpenPhaseAsync(phaseName, phase.DurationSeconds,
                        progress => phase.From + (phase.To - phase.From) * progress, graceful, hard).ConfigureAwait(false);
                    break;

                case "pause":
                    try { await Task.Delay(TimeSpan.FromSeconds(phase.DurationSeconds), graceful).ConfigureAwait(false); }
                    catch (OperationCanceledException) { /* graceful stop during pause */ }
                    break;

                default:
                    throw new InvalidOperationException($"Unknown phase type '{phase.Type}' (validation should have caught this).");
            }
        }

        await DrainPendingAsync(hard).ConfigureAwait(false);
        var abandoned = Volatile.Read(ref _pending);
        var measured = _metrics.ElapsedSeconds;
        _metrics.StopMeasurement();
        _monitor.ClearPhase(_scenario.Name);

        return new ScenarioRunResult
        {
            Records = _metrics.Snapshot(),
            MeasuredSeconds = measured,
            PhaseDescriptions = descriptions,
            IterationsCompleted = Interlocked.Read(ref _iterationsCompleted),
            IterationsFailed = Interlocked.Read(ref _iterationsFailed),
            IterationsThrottled = Interlocked.Read(ref _iterationsThrottled),
            IterationsAbandoned = abandoned,
        };
    }

    /// <summary>Closed model: maxWorkers virtual users; worker i runs iterations while i &lt; target(progress).</summary>
    private async Task RunClosedPhaseAsync(
        string phaseName, double durationSeconds, Func<double, int> targetConcurrency, int maxWorkers,
        CancellationToken graceful, CancellationToken hard)
    {
        var duration = TimeSpan.FromSeconds(durationSeconds);
        var clock = Stopwatch.StartNew();
        var workers = new Task[Math.Max(maxWorkers, 1)];

        for (var i = 0; i < workers.Length; i++)
        {
            var workerIndex = i;
            workers[i] = Task.Run(async () =>
            {
                while (clock.Elapsed < duration &&
                       !graceful.IsCancellationRequested && !hard.IsCancellationRequested)
                {
                    var progress = Math.Clamp(clock.Elapsed / duration, 0, 1);
                    if (workerIndex < targetConcurrency(progress))
                    {
                        var id = Interlocked.Increment(ref _iterationCounter);
                        var ok = await _executor.RunIterationAsync(id, phaseName, hard).ConfigureAwait(false);
                        Tally(ok, hard);
                    }
                    else
                    {
                        try { await Task.Delay(IdleWorkerPoll, hard).ConfigureAwait(false); }
                        catch (OperationCanceledException) { break; }
                    }
                }
            }, CancellationToken.None);
        }

        await Task.WhenAll(workers).ConfigureAwait(false);
    }

    /// <summary>Open model: start rate(progress) iterations per second, fire-and-forget, bounded by maxPendingRequests.</summary>
    private async Task RunOpenPhaseAsync(
        string phaseName, double durationSeconds, Func<double, double> ratePerSecond,
        CancellationToken graceful, CancellationToken hard)
    {
        var duration = TimeSpan.FromSeconds(durationSeconds);
        var clock = Stopwatch.StartNew();
        double carry = 0;
        var lastTick = TimeSpan.Zero;

        while (clock.Elapsed < duration &&
               !graceful.IsCancellationRequested && !hard.IsCancellationRequested)
        {
            var now = clock.Elapsed;
            var progress = Math.Clamp(now / duration, 0, 1);
            carry += ratePerSecond(progress) * (now - lastTick).TotalSeconds;
            lastTick = now;

            var toStart = (int)carry;
            carry -= toStart;

            for (var i = 0; i < toStart; i++)
            {
                if (Volatile.Read(ref _pending) >= _scenario.MaxPendingRequests)
                {
                    Interlocked.Increment(ref _iterationsThrottled);
                    _monitor.NoteThrottled();
                    continue;
                }

                Interlocked.Increment(ref _pending);
                var id = Interlocked.Increment(ref _iterationCounter);
                _ = Task.Run(async () =>
                {
                    try
                    {
                        var ok = await _executor.RunIterationAsync(id, phaseName, hard).ConfigureAwait(false);
                        Tally(ok, hard);
                    }
                    finally
                    {
                        Interlocked.Decrement(ref _pending);
                    }
                }, CancellationToken.None);
            }

            var wait = SchedulerTick - (clock.Elapsed - now);
            if (wait > TimeSpan.Zero)
            {
                try { await Task.Delay(wait, hard).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
            }
        }
    }

    /// <summary>Waits for open-model stragglers up to the worst-case iteration length, or until hard abort.</summary>
    private async Task DrainPendingAsync(CancellationToken hard)
    {
        var clock = Stopwatch.StartNew();
        while (Volatile.Read(ref _pending) > 0 && clock.Elapsed < _drainGrace && !hard.IsCancellationRequested)
            await Task.Delay(50, CancellationToken.None).ConfigureAwait(false);
    }

    private void Tally(bool ok, CancellationToken hard)
    {
        if (hard.IsCancellationRequested) return; // aborted iterations are not counted
        if (ok) Interlocked.Increment(ref _iterationsCompleted);
        else Interlocked.Increment(ref _iterationsFailed);
    }

    private static int Interpolate(int from, int to, double progress) =>
        (int)Math.Round(from + (to - from) * progress);

    private static string Describe(PhaseConfig phase) => phase.Type.ToLowerInvariant() switch
    {
        "constant" => $"constant c={phase.Concurrency} {phase.DurationSeconds:F0}s",
        "ramp" => $"ramp {phase.From}→{phase.To} {phase.DurationSeconds:F0}s",
        "injectrate" => $"inject {phase.RatePerSecond:F0}/s {phase.DurationSeconds:F0}s",
        "ramprate" => $"rampRate {phase.From}→{phase.To}/s {phase.DurationSeconds:F0}s",
        "pause" => $"pause {phase.DurationSeconds:F0}s",
        _ => phase.Type,
    };
}
