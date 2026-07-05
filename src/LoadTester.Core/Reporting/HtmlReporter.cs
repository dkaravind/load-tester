using System.Globalization;
using System.Net;
using System.Text;
using LoadTester.Core.Metrics;

namespace LoadTester.Core.Reporting;

/// <summary>Self-contained HTML report: summary tables plus per-second SVG charts. No external assets.</summary>
public static class HtmlReporter
{
    public static string Render(RunReport report)
    {
        var sb = new StringBuilder();
        sb.AppendLine("<!doctype html><html><head><meta charset=\"utf-8\">");
        sb.AppendLine($"<title>Load test report — {H(Path.GetFileName(report.ConfigFile))}</title>");
        sb.AppendLine("""
            <style>
              :root { color-scheme: light dark; --line:#8884; --muted:#888; --fail:#d4453a; --ok:#2e8b57; --accent:#3d6fb4; }
              body { font: 14px/1.5 system-ui, sans-serif; margin: 2rem auto; max-width: 1080px; padding: 0 1rem; }
              h1 { font-size: 1.4rem; } h2 { font-size: 1.15rem; margin-top: 2.5rem; }
              table { border-collapse: collapse; width: 100%; margin: .75rem 0; }
              th, td { padding: .35rem .6rem; text-align: right; border-bottom: 1px solid var(--line); }
              th:first-child, td:first-child { text-align: left; }
              th { font-weight: 600; }
              .muted { color: var(--muted); }
              .fail { color: var(--fail); font-weight: 600; } .pass { color: var(--ok); font-weight: 600; }
              .tiles { display: flex; gap: 1rem; flex-wrap: wrap; margin: .75rem 0; }
              .tile { border: 1px solid var(--line); border-radius: 8px; padding: .6rem 1rem; min-width: 8rem; }
              .tile b { display: block; font-size: 1.3rem; }
              svg { width: 100%; height: auto; }
              figure { margin: 1rem 0; } figcaption { color: var(--muted); font-size: .85rem; }
            </style></head><body>
            """);

        sb.AppendLine($"<h1>Load test report</h1><p class=\"muted\">config: {H(report.ConfigFile)} · " +
                      $"started {report.StartedUtc:yyyy-MM-dd HH:mm:ss}Z · total {F(report.TotalDurationSeconds)}s" +
                      (report.Interrupted ? " · <span class=\"fail\">interrupted (partial results)</span>" : "") + "</p>");

        foreach (var scenario in report.Scenarios)
            RenderScenario(sb, scenario);

        RenderThresholds(sb, report);
        sb.AppendLine("</body></html>");
        return sb.ToString();
    }

    private static void RenderScenario(StringBuilder sb, ScenarioReport scenario)
    {
        var totals = scenario.Totals;
        var errorRate = totals.Count > 0 ? 100.0 * totals.Failed / totals.Count : 0;

        sb.AppendLine($"<h2>{H(scenario.Name)}</h2>");
        sb.AppendLine($"<p class=\"muted\">{H(string.Join(" → ", scenario.Phases))} · {F(scenario.MeasuredDurationSeconds)}s measured</p>");
        sb.AppendLine("<div class=\"tiles\">");
        Tile(sb, "requests", Fmt.Num(totals.Count));
        Tile(sb, "rps", F(totals.RequestsPerSecond));
        Tile(sb, "error rate", F(errorRate) + "%", errorRate > 0);
        Tile(sb, "p95", Fmt.Ms(totals.Latency.P95));
        Tile(sb, "p99", Fmt.Ms(totals.Latency.P99));
        if (scenario.IterationsThrottled > 0)
            Tile(sb, "throttled", Fmt.Num(scenario.IterationsThrottled), true);
        sb.AppendLine("</div>");

        sb.AppendLine("<table><tr><th>step</th><th>count</th><th>ok</th><th>fail</th><th>rps</th>" +
                      "<th>min</th><th>mean</th><th>p50</th><th>p90</th><th>p95</th><th>p99</th><th>max</th></tr>");
        foreach (var step in scenario.Steps.Append(scenario.Steps.Count > 1 ? totals : null).OfType<StepReport>())
        {
            var l = step.Latency;
            sb.AppendLine($"<tr><td>{H(step.Name)}</td><td>{Fmt.Num(step.Count)}</td><td>{Fmt.Num(step.Ok)}</td>" +
                          $"<td{(step.Failed > 0 ? " class=\"fail\"" : "")}>{Fmt.Num(step.Failed)}</td>" +
                          $"<td>{F(step.RequestsPerSecond)}</td><td>{Fmt.Ms(l.Min)}</td><td>{Fmt.Ms(l.Mean)}</td>" +
                          $"<td>{Fmt.Ms(l.P50)}</td><td>{Fmt.Ms(l.P90)}</td><td>{Fmt.Ms(l.P95)}</td>" +
                          $"<td>{Fmt.Ms(l.P99)}</td><td>{Fmt.Ms(l.Max)}</td></tr>");
        }
        sb.AppendLine("</table>");

        var errors = scenario.Steps.SelectMany(s => s.Errors.Select(e => (s.Name, e))).ToList();
        if (errors.Count > 0)
        {
            sb.AppendLine("<table><tr><th>step</th><th>error</th><th>count</th></tr>");
            foreach (var (step, error) in errors)
                sb.AppendLine($"<tr><td>{H(step)}</td><td style=\"text-align:left\">{H(error.Error)}</td><td>{Fmt.Num(error.Count)}</td></tr>");
            sb.AppendLine("</table>");
        }

        if (scenario.TimeSeries.Count > 1)
        {
            sb.AppendLine(LineChart("Throughput (requests per second)", scenario.TimeSeries,
                new[] { ("total", "var(--accent)", scenario.TimeSeries.Select(b => (double)b.Count).ToArray()),
                        ("failed", "var(--fail)", scenario.TimeSeries.Select(b => (double)b.Failed).ToArray()) }));
            sb.AppendLine(LineChart("Latency (ms)", scenario.TimeSeries,
                new[] { ("p95", "var(--accent)", scenario.TimeSeries.Select(b => b.P95Ms).ToArray()),
                        ("mean", "var(--ok)", scenario.TimeSeries.Select(b => b.MeanMs).ToArray()) }));
        }
    }

    private static void Tile(StringBuilder sb, string label, string value, bool alert = false) =>
        sb.AppendLine($"<div class=\"tile\"><span class=\"muted\">{H(label)}</span>" +
                      $"<b{(alert ? " class=\"fail\"" : "")}>{H(value)}</b></div>");

    private static void RenderThresholds(StringBuilder sb, RunReport report)
    {
        if (report.Thresholds.Count == 0) return;
        sb.AppendLine("<h2>Thresholds</h2><table><tr><th>scenario</th><th>rule</th><th>limit</th><th>actual</th><th>result</th></tr>");
        foreach (var t in report.Thresholds)
            sb.AppendLine($"<tr><td>{H(t.Scenario)}</td><td style=\"text-align:left\">{H(t.Rule)}</td>" +
                          $"<td>{F(t.Limit)}</td><td>{F(t.Actual)}</td>" +
                          $"<td class=\"{(t.Passed ? "pass" : "fail")}\">{(t.Passed ? "PASS" : "FAIL")}</td></tr>");
        sb.AppendLine("</table>");
    }

    private static string LineChart(string title, List<TimeBucket> buckets, (string Label, string Color, double[] Values)[] series)
    {
        const int width = 1000, height = 260, padLeft = 56, padRight = 16, padTop = 16, padBottom = 34;
        var plotW = width - padLeft - padRight;
        var plotH = height - padTop - padBottom;

        var xMax = Math.Max(buckets[^1].Second, 1);
        var yMax = Math.Max(series.SelectMany(s => s.Values).Max(), 1);
        yMax *= 1.08; // headroom

        double X(double second) => padLeft + second / xMax * plotW;
        double Y(double value) => padTop + (1 - value / yMax) * plotH;

        var sb = new StringBuilder();
        sb.Append($"<figure><figcaption>{H(title)} — ");
        sb.Append(string.Join(" · ", series.Select(s => $"<span style=\"color:{s.Color}\">■</span> {H(s.Label)}")));
        sb.Append("</figcaption>");
        sb.Append($"<svg viewBox=\"0 0 {width} {height}\" role=\"img\">");

        for (var i = 0; i <= 4; i++) // horizontal gridlines + y labels
        {
            var value = yMax / 4 * i;
            var y = Y(value);
            sb.Append($"<line x1=\"{padLeft}\" y1=\"{F(y)}\" x2=\"{width - padRight}\" y2=\"{F(y)}\" stroke=\"var(--line)\" stroke-width=\"1\"/>");
            sb.Append($"<text x=\"{padLeft - 6}\" y=\"{F(y + 4)}\" text-anchor=\"end\" font-size=\"11\" fill=\"var(--muted)\">{F(Math.Round(value, 1))}</text>");
        }
        for (var i = 0; i <= 5; i++) // x labels (seconds)
        {
            var second = xMax / 5.0 * i;
            sb.Append($"<text x=\"{F(X(second))}\" y=\"{height - 12}\" text-anchor=\"middle\" font-size=\"11\" fill=\"var(--muted)\">{F(Math.Round(second))}s</text>");
        }

        foreach (var (_, color, values) in series)
        {
            var points = string.Join(" ",
                buckets.Select((b, i) => $"{F(X(b.Second))},{F(Y(values[i]))}"));
            sb.Append($"<polyline points=\"{points}\" fill=\"none\" stroke=\"{color}\" stroke-width=\"1.8\"/>");
        }

        sb.Append("</svg></figure>");
        return sb.ToString();
    }

    private static string H(string text) => WebUtility.HtmlEncode(text);
    private static string F(double value) => value.ToString("0.##", CultureInfo.InvariantCulture);
}
