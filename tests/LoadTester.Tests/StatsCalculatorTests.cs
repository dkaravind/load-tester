using LoadTester.Core.Metrics;
using Xunit;

namespace LoadTester.Tests;

public class StatsCalculatorTests
{
    [Fact]
    public void Percentile_SingleValue_ReturnsThatValue()
    {
        var sorted = new[] { 42.0 };
        Assert.Equal(42.0, StatsCalculator.Percentile(sorted, 50));
        Assert.Equal(42.0, StatsCalculator.Percentile(sorted, 99));
    }

    [Fact]
    public void Percentile_EmptyArray_ReturnsZero()
    {
        Assert.Equal(0, StatsCalculator.Percentile(Array.Empty<double>(), 95));
    }

    [Fact]
    public void Percentile_NearestRank_OnHundredValues()
    {
        // 1..100 sorted: nearest-rank p95 is the 95th value.
        var sorted = Enumerable.Range(1, 100).Select(i => (double)i).ToArray();
        Assert.Equal(50.0, StatsCalculator.Percentile(sorted, 50));
        Assert.Equal(95.0, StatsCalculator.Percentile(sorted, 95));
        Assert.Equal(99.0, StatsCalculator.Percentile(sorted, 99));
        Assert.Equal(100.0, StatsCalculator.Percentile(sorted, 100));
    }

    [Fact]
    public void Percentile_TenValues_P90IsNinthValue()
    {
        var sorted = new[] { 10.0, 20, 30, 40, 50, 60, 70, 80, 90, 100 };
        Assert.Equal(90.0, StatsCalculator.Percentile(sorted, 90));
        Assert.Equal(50.0, StatsCalculator.Percentile(sorted, 50));
    }

    [Fact]
    public void BuildLatencyStats_ComputesMeanAndStdDev()
    {
        var stats = StatsCalculator.BuildLatencyStats(new[] { 2.0, 4.0, 4.0, 4.0, 5.0, 5.0, 7.0, 9.0 });
        Assert.Equal(5.0, stats.Mean);
        Assert.Equal(2.0, stats.StdDev); // classic population-stddev example
        Assert.Equal(2.0, stats.Min);
        Assert.Equal(9.0, stats.Max);
    }

    [Fact]
    public void BuildLatencyStats_Empty_ReturnsZeros()
    {
        var stats = StatsCalculator.BuildLatencyStats(Array.Empty<double>());
        Assert.Equal(0, stats.Mean);
        Assert.Equal(0, stats.P95);
    }

    [Fact]
    public void BuildScenarioReport_GroupsByStepAndBucketsBySecond()
    {
        var records = new List<RequestRecord>
        {
            new("stepA", "constant", 0.1, 100, 200, true, 10, null),
            new("stepA", "constant", 0.5, 200, 200, true, 10, null),
            new("stepB", "constant", 1.2, 300, 500, false, 5, "HTTP 500"),
            new("stepA", "constant", 1.8, 400, 200, true, 10, null),
        };

        var report = StatsCalculator.BuildScenarioReport(
            "s", records, measuredSeconds: 2.0, phases: new List<string> { "constant" },
            iterationsCompleted: 3, iterationsFailed: 1, iterationsThrottled: 0, iterationsAbandoned: 0,
            stepOrder: new[] { "stepA", "stepB" });

        Assert.Equal(4, report.Totals.Count);
        Assert.Equal(3, report.Totals.Ok);
        Assert.Equal(1, report.Totals.Failed);
        Assert.Equal(2.0, report.Totals.RequestsPerSecond);

        Assert.Equal(2, report.Steps.Count);
        Assert.Equal("stepA", report.Steps[0].Name);
        Assert.Equal(3, report.Steps[0].Count);
        Assert.Equal("HTTP 500", report.Steps[1].Errors.Single().Error);

        Assert.Equal(2, report.TimeSeries.Count);
        Assert.Equal(2, report.TimeSeries[0].Count);   // offsets 0.1, 0.5 → second 0
        Assert.Equal(2, report.TimeSeries[1].Count);   // offsets 1.2, 1.8 → second 1
        Assert.Equal(1, report.TimeSeries[1].Failed);
    }

    [Fact]
    public void BuildScenarioReport_TimeSeries_FillsIdleSecondsWithZeroBuckets()
    {
        // Requests at seconds 0 and 3 only (e.g. a pause phase in between): the series must not
        // skip seconds 1-2, or charts would interpolate phantom throughput across the gap.
        var records = new List<RequestRecord>
        {
            new("s", "p", 0.2, 50, 200, true, 1, null),
            new("s", "p", 3.7, 60, 200, true, 1, null),
        };

        var report = StatsCalculator.BuildScenarioReport(
            "s", records, 4.0, new List<string>(), 2, 0, 0, 0, new[] { "s" });

        Assert.Equal(4, report.TimeSeries.Count);
        Assert.Equal(new[] { 0, 1, 2, 3 }, report.TimeSeries.Select(b => b.Second));
        Assert.Equal(0, report.TimeSeries[1].Count);
        Assert.Equal(0, report.TimeSeries[2].Count);
        Assert.Equal(1, report.TimeSeries[3].Count);
    }
}
