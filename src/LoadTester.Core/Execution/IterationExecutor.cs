using System.Diagnostics;
using System.Text;
using System.Text.Json;
using LoadTester.Core.Configuration;
using LoadTester.Core.Data;
using LoadTester.Core.Http;
using LoadTester.Core.Metrics;
using LoadTester.Core.Templating;

namespace LoadTester.Core.Execution;

/// <summary>
/// Runs one iteration: all scenario steps in order, recording one metric entry per request.
/// Never throws for request-level problems — failures become failed records.
/// </summary>
public sealed class IterationExecutor
{
    private readonly ScenarioConfig _scenario;
    private readonly IReadOnlyDictionary<string, EndpointConfig> _endpoints;
    private readonly GlobalSettings _settings;
    private readonly HttpClient _client;
    private readonly RequestBuilder _builder;
    private readonly MetricsCollector _metrics;
    private readonly LiveMonitor _monitor;
    private readonly DataFeed? _dataFeed;

    public IterationExecutor(
        ScenarioConfig scenario,
        IReadOnlyDictionary<string, EndpointConfig> endpoints,
        GlobalSettings settings,
        HttpClient client,
        MetricsCollector metrics,
        LiveMonitor monitor,
        DataFeed? dataFeed)
    {
        _scenario = scenario;
        _endpoints = endpoints;
        _settings = settings;
        _client = client;
        _builder = new RequestBuilder(settings);
        _metrics = metrics;
        _monitor = monitor;
        _dataFeed = dataFeed;
    }

    /// <summary>Returns true when every step succeeded. Hard cancellation aborts without recording.</summary>
    public async Task<bool> RunIterationAsync(long iterationId, string phaseName, CancellationToken hardToken)
    {
        var ctx = new IterationContext
        {
            IterationId = iterationId,
            DataRow = _dataFeed?.GetRow(iterationId),
        };

        var allOk = true;
        foreach (var step in _scenario.Steps)
        {
            if (hardToken.IsCancellationRequested) return false;

            var ok = await RunStepAsync(step, ctx, phaseName, hardToken).ConfigureAwait(false);
            if (!ok)
            {
                allOk = false;
                if (_scenario.StopIterationOnFailure) break;
            }

            if (step.PauseAfterSeconds > 0)
            {
                try { await Task.Delay(TimeSpan.FromSeconds(step.PauseAfterSeconds), hardToken).ConfigureAwait(false); }
                catch (OperationCanceledException) { return allOk; }
            }
        }
        return allOk;
    }

    private async Task<bool> RunStepAsync(StepConfig step, IterationContext ctx, string phaseName, CancellationToken hardToken)
    {
        var endpoint = _endpoints[step.Endpoint];
        var stepName = step.Name ?? endpoint.Name;
        var timeout = TimeSpan.FromSeconds(endpoint.TimeoutSeconds ?? _settings.RequestTimeoutSeconds);

        var status = 0;
        long bytes = 0;
        string? error = null;
        var success = false;
        var start = Stopwatch.GetTimestamp();

        try
        {
            using var request = _builder.Build(endpoint, ctx);
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(hardToken);
            cts.CancelAfter(timeout);

            using var response = await _client
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token)
                .ConfigureAwait(false);
            var content = await response.Content.ReadAsByteArrayAsync(cts.Token).ConfigureAwait(false);

            status = (int)response.StatusCode;
            bytes = content.LongLength;
            success = endpoint.ExpectedStatusCodes.Count > 0
                ? endpoint.ExpectedStatusCodes.Contains(status)
                : response.IsSuccessStatusCode;
            if (!success)
                error = $"HTTP {status}";
            else if (step.Capture.Count > 0)
                (success, error) = ApplyCaptures(step, ctx, content);
        }
        catch (OperationCanceledException) when (hardToken.IsCancellationRequested)
        {
            return false; // run aborted: in-flight request is intentionally not recorded
        }
        catch (OperationCanceledException)
        {
            error = $"timeout after {timeout.TotalSeconds:F0}s";
        }
        catch (HttpRequestException ex)
        {
            error = ex.InnerException is { } inner ? $"{ex.Message} ({inner.Message})" : ex.Message;
        }
        catch (TemplateException ex)
        {
            error = $"template error: {ex.Message}";
        }
        catch (Exception ex)
        {
            error = $"{ex.GetType().Name}: {ex.Message}";
        }

        var latencyMs = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        _metrics.Record(stepName, phaseName, latencyMs, status, success, bytes, error);
        _monitor.NoteRequest(latencyMs, success);
        return success;
    }

    private static (bool ok, string? error) ApplyCaptures(StepConfig step, IterationContext ctx, byte[] content)
    {
        string body;
        try
        {
            body = Encoding.UTF8.GetString(content);
            foreach (var capture in step.Capture)
            {
                var value = JsonPathLite.Extract(body, capture.JsonPath);
                if (value is null)
                    return (false, $"capture '{capture.As}': no value at {capture.JsonPath}");
                ctx.Vars[capture.As] = value;
            }
        }
        catch (JsonException)
        {
            return (false, $"capture '{step.Capture[0].As}': response body is not valid JSON");
        }
        return (true, null);
    }
}
