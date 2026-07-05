using System.Collections.Concurrent;

namespace LoadTester.Core.Metrics;

/// <summary>
/// Cheap rolling counters shared by all scenarios, drained periodically for the live status line.
/// Latency sums are kept in microseconds as longs so Interlocked works.
/// </summary>
public sealed class LiveMonitor
{
    private long _windowCount, _windowFailed, _windowLatencySumMicros, _windowLatencyMaxMicros;
    private long _totalCount, _totalFailed, _totalThrottled;
    private readonly ConcurrentDictionary<string, string> _phases = new();

    public void NoteRequest(double latencyMs, bool success)
    {
        var micros = (long)(latencyMs * 1000);
        Interlocked.Increment(ref _windowCount);
        Interlocked.Increment(ref _totalCount);
        if (!success)
        {
            Interlocked.Increment(ref _windowFailed);
            Interlocked.Increment(ref _totalFailed);
        }
        Interlocked.Add(ref _windowLatencySumMicros, micros);

        long seen;
        while (micros > (seen = Interlocked.Read(ref _windowLatencyMaxMicros)))
            if (Interlocked.CompareExchange(ref _windowLatencyMaxMicros, micros, seen) == seen)
                break;
    }

    public void NoteThrottled() => Interlocked.Increment(ref _totalThrottled);

    public void SetPhase(string scenario, string description) => _phases[scenario] = description;

    public void ClearPhase(string scenario) => _phases.TryRemove(scenario, out _);

    /// <summary>Builds the status line for the elapsed window and resets the window counters.</summary>
    public string DrainStatusLine(TimeSpan elapsed, double windowSeconds)
    {
        var count = Interlocked.Exchange(ref _windowCount, 0);
        var failed = Interlocked.Exchange(ref _windowFailed, 0);
        var latencySum = Interlocked.Exchange(ref _windowLatencySumMicros, 0);
        var latencyMax = Interlocked.Exchange(ref _windowLatencyMaxMicros, 0);
        var total = Interlocked.Read(ref _totalCount);
        var totalFailed = Interlocked.Read(ref _totalFailed);
        var throttled = Interlocked.Read(ref _totalThrottled);

        var phases = string.Join(" | ", _phases.Select(p => $"{p.Key}: {p.Value}"));
        var rps = windowSeconds > 0 ? count / windowSeconds : 0;
        var avgMs = count > 0 ? latencySum / 1000.0 / count : 0;
        var okPercent = total > 0 ? 100.0 * (total - totalFailed) / total : 100;

        var line = $"{elapsed:hh\\:mm\\:ss} {phases} | total {total:N0} ({okPercent:F1}% ok) " +
                   $"| rps {rps:F1} | avg {avgMs:F0}ms | max {latencyMax / 1000.0:F0}ms";
        if (throttled > 0) line += $" | throttled {throttled:N0}";
        return line;
    }
}
