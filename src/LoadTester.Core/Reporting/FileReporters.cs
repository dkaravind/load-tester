using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using LoadTester.Core.Metrics;

namespace LoadTester.Core.Reporting;

/// <summary>Writes the selected report files into a per-run folder; returns the written paths.</summary>
public static class ReportWriter
{
    public static List<string> Write(RunReport report, IEnumerable<string> formats, string outputFolder)
    {
        var written = new List<string>();
        var fileFormats = formats.Where(f => !f.Equals("console", StringComparison.OrdinalIgnoreCase)).ToList();
        if (fileFormats.Count == 0) return written;

        Directory.CreateDirectory(outputFolder);
        foreach (var format in fileFormats)
        {
            switch (format.ToLowerInvariant())
            {
                case "json":
                    written.Add(WriteFile(outputFolder, "summary.json", JsonReporter.Render(report)));
                    break;
                case "csv":
                    written.Add(WriteFile(outputFolder, "summary.csv", CsvReporter.RenderSummary(report)));
                    written.Add(WriteFile(outputFolder, "timeseries.csv", CsvReporter.RenderTimeSeries(report)));
                    break;
                case "html":
                    written.Add(WriteFile(outputFolder, "report.html", HtmlReporter.Render(report)));
                    break;
            }
        }
        return written;
    }

    private static string WriteFile(string folder, string name, string content)
    {
        var path = Path.Combine(folder, name);
        File.WriteAllText(path, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        return path;
    }
}

public static class JsonReporter
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static string Render(RunReport report) => JsonSerializer.Serialize(report, Options);
}

public static class CsvReporter
{
    public static string RenderSummary(RunReport report)
    {
        var sb = new StringBuilder();
        sb.AppendLine("scenario,step,count,ok,failed,rps,bytes,min_ms,mean_ms,stddev_ms,p50_ms,p75_ms,p90_ms,p95_ms,p99_ms,max_ms");
        foreach (var scenario in report.Scenarios)
        {
            foreach (var step in scenario.Steps.Append(scenario.Totals))
            {
                var l = step.Latency;
                sb.AppendLine(string.Join(',',
                    Escape(scenario.Name), Escape(step.Name), step.Count, step.Ok, step.Failed,
                    F(step.RequestsPerSecond), step.BytesReceived,
                    F(l.Min), F(l.Mean), F(l.StdDev), F(l.P50), F(l.P75), F(l.P90), F(l.P95), F(l.P99), F(l.Max)));
            }
        }
        return sb.ToString();
    }

    public static string RenderTimeSeries(RunReport report)
    {
        var sb = new StringBuilder();
        sb.AppendLine("scenario,second,count,failed,mean_ms,p95_ms");
        foreach (var scenario in report.Scenarios)
            foreach (var bucket in scenario.TimeSeries)
                sb.AppendLine(string.Join(',',
                    Escape(scenario.Name), bucket.Second, bucket.Count, bucket.Failed, F(bucket.MeanMs), F(bucket.P95Ms)));
        return sb.ToString();
    }

    private static string F(double value) => value.ToString("0.##", CultureInfo.InvariantCulture);

    internal static string Escape(string value)
    {
        // Neutralize spreadsheet formula interpretation for user-supplied names (=, +, -, @, tab).
        if (value.Length > 0 && value[0] is '=' or '+' or '-' or '@' or '\t')
            value = "'" + value;
        return value.IndexOfAny(new[] { ',', '"', '\n', '\r' }) >= 0
            ? "\"" + value.Replace("\"", "\"\"") + "\""
            : value;
    }
}
