using System.Globalization;
using LoadTester.Core.Metrics;

namespace LoadTester.Core.Reporting;

/// <summary>Prints the final summary tables. Writes to any TextWriter (Console.Out by default).</summary>
public sealed class ConsoleReporter
{
    private readonly TextWriter _out;

    public ConsoleReporter(TextWriter? writer = null) => _out = writer ?? Console.Out;

    public void Write(RunReport report)
    {
        _out.WriteLine();
        _out.WriteLine($"Run finished in {report.TotalDurationSeconds.ToString("F1", CultureInfo.InvariantCulture)}s" +
                       (report.Interrupted ? "  (INTERRUPTED — partial results)" : ""));

        foreach (var scenario in report.Scenarios)
            WriteScenario(scenario);

        WriteThresholds(report);
    }

    private void WriteScenario(ScenarioReport scenario)
    {
        _out.WriteLine();
        _out.WriteLine(new string('─', 100));
        _out.WriteLine($" Scenario: {scenario.Name}   " +
                       $"({scenario.MeasuredDurationSeconds.ToString("F1", CultureInfo.InvariantCulture)}s measured, " +
                       $"iterations ok/failed: {Fmt.Num(scenario.IterationsCompleted)}/{Fmt.Num(scenario.IterationsFailed)}" +
                       (scenario.IterationsThrottled > 0 ? $", throttled: {Fmt.Num(scenario.IterationsThrottled)}" : "") +
                       (scenario.IterationsAbandoned > 0 ? $", ABANDONED: {Fmt.Num(scenario.IterationsAbandoned)}" : "") + ")");
        _out.WriteLine($" Phases: {string.Join(" → ", scenario.Phases)}");
        _out.WriteLine(new string('─', 100));

        var rows = new List<string[]>
        {
            new[] { "step", "count", "ok", "fail", "rps", "min", "mean", "p50", "p75", "p90", "p95", "p99", "max", "data" },
        };
        foreach (var step in scenario.Steps)
            rows.Add(Row(step));
        if (scenario.Steps.Count > 1)
            rows.Add(Row(scenario.Totals));

        WriteTable(rows);

        var statusCodes = string.Join("  ",
            scenario.Totals.StatusCodes.Select(kv => $"{kv.Key}×{Fmt.Num(kv.Value)}"));
        if (statusCodes.Length > 0)
            _out.WriteLine($" status codes: {statusCodes}");

        if (scenario.Totals.Failed > 0)
        {
            _out.WriteLine(" errors:");
            foreach (var step in scenario.Steps)
                foreach (var error in step.Errors)
                    _out.WriteLine($"   {Fmt.Num(error.Count),8} × [{step.Name}] {error.Error}");
        }
    }

    private static string[] Row(StepReport step) => new[]
    {
        step.Name,
        Fmt.Num(step.Count),
        Fmt.Num(step.Ok),
        Fmt.Num(step.Failed),
        step.RequestsPerSecond.ToString("F1", CultureInfo.InvariantCulture),
        Fmt.Ms(step.Latency.Min),
        Fmt.Ms(step.Latency.Mean),
        Fmt.Ms(step.Latency.P50),
        Fmt.Ms(step.Latency.P75),
        Fmt.Ms(step.Latency.P90),
        Fmt.Ms(step.Latency.P95),
        Fmt.Ms(step.Latency.P99),
        Fmt.Ms(step.Latency.Max),
        Fmt.Bytes(step.BytesReceived),
    };

    private void WriteTable(List<string[]> rows)
    {
        var widths = new int[rows[0].Length];
        foreach (var row in rows)
            for (var c = 0; c < row.Length; c++)
                widths[c] = Math.Max(widths[c], row[c].Length);

        for (var r = 0; r < rows.Count; r++)
        {
            var cells = rows[r].Select((cell, c) => c == 0 ? cell.PadRight(widths[c]) : cell.PadLeft(widths[c]));
            _out.WriteLine(" " + string.Join("  ", cells));
            if (r == 0)
                _out.WriteLine(" " + string.Join("  ", widths.Select(w => new string('·', w))));
        }
    }

    private void WriteThresholds(RunReport report)
    {
        if (report.Thresholds.Count == 0) return;

        _out.WriteLine();
        _out.WriteLine(" Thresholds:");
        foreach (var t in report.Thresholds)
        {
            var mark = t.Passed ? "PASS" : "FAIL";
            _out.WriteLine($"   [{mark}] {t.Scenario}: {t.Rule} " +
                           $"limit {t.Limit.ToString(CultureInfo.InvariantCulture)}, " +
                           $"actual {t.Actual.ToString(CultureInfo.InvariantCulture)}");
        }
    }
}
