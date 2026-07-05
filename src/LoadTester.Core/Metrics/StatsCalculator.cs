namespace LoadTester.Core.Metrics;

public static class StatsCalculator
{
    /// <summary>Aggregates raw records into a scenario report. Percentiles use the nearest-rank method.</summary>
    public static ScenarioReport BuildScenarioReport(
        string scenarioName,
        IReadOnlyList<RequestRecord> records,
        double measuredSeconds,
        List<string> phases,
        long iterationsCompleted,
        long iterationsFailed,
        long iterationsThrottled,
        long iterationsAbandoned,
        IReadOnlyList<string> stepOrder)
    {
        var byStep = records
            .GroupBy(r => r.Step)
            .OrderBy(g => { var i = IndexOf(stepOrder, g.Key); return i < 0 ? int.MaxValue : i; })
            .Select(g => BuildStepReport(g.Key, g.ToList(), measuredSeconds))
            .ToList();

        return new ScenarioReport
        {
            Name = scenarioName,
            MeasuredDurationSeconds = Math.Round(measuredSeconds, 2),
            Phases = phases,
            IterationsCompleted = iterationsCompleted,
            IterationsFailed = iterationsFailed,
            IterationsThrottled = iterationsThrottled,
            IterationsAbandoned = iterationsAbandoned,
            Totals = BuildStepReport("TOTAL", records, measuredSeconds),
            Steps = byStep,
            TimeSeries = BuildTimeSeries(records),
        };
    }

    private static StepReport BuildStepReport(string name, IReadOnlyCollection<RequestRecord> records, double seconds)
    {
        var okLatencies = records.Where(r => r.Success).Select(r => r.LatencyMs).ToArray();
        var allLatencies = records.Select(r => r.LatencyMs).ToArray();

        var statusCodes = records
            .GroupBy(r => r.StatusCode)
            .OrderBy(g => g.Key)
            .ToDictionary(g => g.Key == 0 ? "none" : g.Key.ToString(), g => g.LongCount());

        var errors = records
            .Where(r => r.Error is not null)
            .GroupBy(r => r.Error!)
            .Select(g => new ErrorSummary { Error = g.Key, Count = g.LongCount() })
            .OrderByDescending(e => e.Count)
            .Take(10)
            .ToList();

        return new StepReport
        {
            Name = name,
            Count = records.Count,
            Ok = okLatencies.Length,
            Failed = records.Count - okLatencies.Length,
            RequestsPerSecond = seconds > 0 ? Math.Round(records.Count / seconds, 2) : 0,
            BytesReceived = records.Sum(r => r.Bytes),
            Latency = BuildLatencyStats(allLatencies),
            OkLatency = BuildLatencyStats(okLatencies),
            StatusCodes = statusCodes,
            Errors = errors,
        };
    }

    public static LatencyStats BuildLatencyStats(double[] latencies)
    {
        if (latencies.Length == 0) return LatencyStats.Empty;

        Array.Sort(latencies);
        var mean = latencies.Average();
        var variance = latencies.Sum(l => (l - mean) * (l - mean)) / latencies.Length;

        return new LatencyStats
        {
            Min = Round(latencies[0]),
            Mean = Round(mean),
            StdDev = Round(Math.Sqrt(variance)),
            P50 = Round(Percentile(latencies, 50)),
            P75 = Round(Percentile(latencies, 75)),
            P90 = Round(Percentile(latencies, 90)),
            P95 = Round(Percentile(latencies, 95)),
            P99 = Round(Percentile(latencies, 99)),
            Max = Round(latencies[^1]),
        };
    }

    /// <summary>Nearest-rank percentile over a pre-sorted array.</summary>
    public static double Percentile(double[] sorted, double percentile)
    {
        if (sorted.Length == 0) return 0;
        var rank = (int)Math.Ceiling(percentile / 100.0 * sorted.Length);
        return sorted[Math.Clamp(rank - 1, 0, sorted.Length - 1)];
    }

    private static List<TimeBucket> BuildTimeSeries(IReadOnlyList<RequestRecord> records)
    {
        var bySecond = records
            .GroupBy(r => (int)r.OffsetSeconds)
            .ToDictionary(g => g.Key, g =>
            {
                var latencies = g.Select(r => r.LatencyMs).ToArray();
                Array.Sort(latencies);
                return new TimeBucket
                {
                    Second = g.Key,
                    Count = g.LongCount(),
                    Failed = g.LongCount(r => !r.Success),
                    MeanMs = Round(latencies.Average()),
                    P95Ms = Round(Percentile(latencies, 95)),
                };
            });
        if (bySecond.Count == 0) return new List<TimeBucket>();

        // Fill idle seconds (pauses, saturation stalls) with zero buckets so charts and the
        // timeseries CSV show the gap instead of interpolating across it.
        var last = bySecond.Keys.Max();
        var series = new List<TimeBucket>(last + 1);
        for (var second = 0; second <= last; second++)
            series.Add(bySecond.TryGetValue(second, out var bucket)
                ? bucket
                : new TimeBucket { Second = second, Count = 0, Failed = 0, MeanMs = 0, P95Ms = 0 });
        return series;
    }

    private static double Round(double value) => Math.Round(value, 2);

    private static int IndexOf(IReadOnlyList<string> list, string value)
    {
        for (var i = 0; i < list.Count; i++)
            if (string.Equals(list[i], value, StringComparison.OrdinalIgnoreCase))
                return i;
        return -1;
    }
}
