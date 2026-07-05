using LoadTester.Core.Configuration;
using LoadTester.Core.Metrics;

namespace LoadTester.Core.Execution;

/// <summary>
/// Evaluates scenario thresholds against the measured totals. Latency rules use the
/// all-requests distribution (timeouts included), matching how users experience the API.
/// </summary>
public static class ThresholdEvaluator
{
    public static List<ThresholdResult> Evaluate(
        IReadOnlyList<(ScenarioConfig Scenario, ScenarioRunResult Result)> results)
    {
        var outcomes = new List<ThresholdResult>();
        foreach (var (scenario, result) in results)
        {
            if (scenario.Thresholds is not { } thresholds) continue;

            var records = result.Records;
            var count = records.Count;
            var failed = records.Count(r => !r.Success);
            var latencies = records.Select(r => r.LatencyMs).ToArray();
            Array.Sort(latencies);

            void Check(string rule, double? limit, double actual, bool actualMustBeBelow = true)
            {
                if (limit is not { } max) return;
                outcomes.Add(new ThresholdResult
                {
                    Scenario = scenario.Name,
                    Rule = rule,
                    Limit = max,
                    Actual = Math.Round(actual, 2),
                    Passed = actualMustBeBelow ? actual <= max : actual >= max,
                });
            }

            Check("maxErrorRatePercent", thresholds.MaxErrorRatePercent, count > 0 ? 100.0 * failed / count : 0);
            Check("maxP95Ms", thresholds.MaxP95Ms, StatsCalculator.Percentile(latencies, 95));
            Check("maxP99Ms", thresholds.MaxP99Ms, StatsCalculator.Percentile(latencies, 99));
            Check("maxMeanMs", thresholds.MaxMeanMs, latencies.Length > 0 ? latencies.Average() : 0);
            Check("minRequestsPerSecond", thresholds.MinRequestsPerSecond,
                result.MeasuredSeconds > 0 ? count / result.MeasuredSeconds : 0, actualMustBeBelow: false);
        }
        return outcomes;
    }
}
