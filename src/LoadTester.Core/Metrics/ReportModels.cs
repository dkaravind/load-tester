namespace LoadTester.Core.Metrics;

/// <summary>Everything reporters need; serialized as-is by the JSON reporter.</summary>
public sealed class RunReport
{
    public required DateTime StartedUtc { get; init; }
    public required string ConfigFile { get; init; }
    public required double TotalDurationSeconds { get; init; }
    public required bool Interrupted { get; init; }
    public required List<ScenarioReport> Scenarios { get; init; }
    public required List<ThresholdResult> Thresholds { get; init; }
}

public sealed class ScenarioReport
{
    public required string Name { get; init; }
    public required double MeasuredDurationSeconds { get; init; }
    public required List<string> Phases { get; init; }
    public required long IterationsCompleted { get; init; }
    public required long IterationsFailed { get; init; }
    /// <summary>Iterations skipped by rate phases because maxPendingRequests was hit.</summary>
    public required long IterationsThrottled { get; init; }
    /// <summary>Iterations still in flight when the drain grace expired; their results were lost.</summary>
    public required long IterationsAbandoned { get; init; }
    public required StepReport Totals { get; init; }
    public required List<StepReport> Steps { get; init; }
    public required List<TimeBucket> TimeSeries { get; init; }
}

public sealed class StepReport
{
    public required string Name { get; init; }
    public required long Count { get; init; }
    public required long Ok { get; init; }
    public required long Failed { get; init; }
    public required double RequestsPerSecond { get; init; }
    public required long BytesReceived { get; init; }
    /// <summary>Latency over all requests, failures included (timeouts count).</summary>
    public required LatencyStats Latency { get; init; }
    /// <summary>Latency over successful requests only.</summary>
    public required LatencyStats OkLatency { get; init; }
    public required Dictionary<string, long> StatusCodes { get; init; }
    public required List<ErrorSummary> Errors { get; init; }
}

public sealed class LatencyStats
{
    public required double Min { get; init; }
    public required double Mean { get; init; }
    public required double StdDev { get; init; }
    public required double P50 { get; init; }
    public required double P75 { get; init; }
    public required double P90 { get; init; }
    public required double P95 { get; init; }
    public required double P99 { get; init; }
    public required double Max { get; init; }

    public static readonly LatencyStats Empty = new()
        { Min = 0, Mean = 0, StdDev = 0, P50 = 0, P75 = 0, P90 = 0, P95 = 0, P99 = 0, Max = 0 };
}

/// <summary>Per-second aggregate for charts and the timeseries CSV.</summary>
public sealed class TimeBucket
{
    public required int Second { get; init; }
    public required long Count { get; init; }
    public required long Failed { get; init; }
    public required double MeanMs { get; init; }
    public required double P95Ms { get; init; }
}

public sealed class ErrorSummary
{
    public required string Error { get; init; }
    public required long Count { get; init; }
}

public sealed class ThresholdResult
{
    public required string Scenario { get; init; }
    public required string Rule { get; init; }
    public required double Limit { get; init; }
    public required double Actual { get; init; }
    public required bool Passed { get; init; }
}
