namespace LoadTester.Core.Configuration;

/// <summary>Root of the endpoints.json configuration file.</summary>
public sealed class LoadTestConfig
{
    public GlobalSettings Settings { get; set; } = new();
    public List<EndpointConfig> Endpoints { get; set; } = new();
    public List<ScenarioConfig> Scenarios { get; set; } = new();
}

public sealed class GlobalSettings
{
    /// <summary>Base address prepended to relative endpoint paths, e.g. "https://localhost:5001".</summary>
    public string? BaseAddress { get; set; }

    /// <summary>Headers sent with every request. Endpoint headers override these on name collision.</summary>
    public Dictionary<string, string> DefaultHeaders { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Per-request timeout. Endpoints may override individually.</summary>
    public double RequestTimeoutSeconds { get; set; } = 30;

    /// <summary>Connection pool size towards each host.</summary>
    public int MaxConnectionsPerServer { get; set; } = 1024;

    /// <summary>Accept any TLS certificate (self-signed dev certs).</summary>
    public bool SkipTlsVerification { get; set; }

    /// <summary>Follow 3xx redirects automatically. Off by default so redirect statuses are measurable.</summary>
    public bool FollowRedirects { get; set; }

    /// <summary>Run enabled scenarios concurrently instead of one after another.</summary>
    public bool RunScenariosInParallel { get; set; }

    public ReportSettings Reports { get; set; } = new();
}

public sealed class ReportSettings
{
    /// <summary>Output folder for report files; a timestamped subfolder is created per run.</summary>
    public string Folder { get; set; } = "reports";

    /// <summary>Any of: console, json, csv, html.</summary>
    public List<string> Formats { get; set; } = new() { "console", "json", "html" };
}

public sealed class EndpointConfig
{
    /// <summary>Unique name; scenarios reference endpoints by this name.</summary>
    public string Name { get; set; } = "";

    public string Method { get; set; } = "GET";

    /// <summary>Relative path (joined with settings.baseAddress) or an absolute http(s) URL. Supports {{placeholders}}.</summary>
    public string Path { get; set; } = "";

    /// <summary>Extra headers for this endpoint; values support {{placeholders}}.</summary>
    public Dictionary<string, string> Headers { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Inline request body template. Supports {{placeholders}}.</summary>
    public string? BodyTemplate { get; set; }

    /// <summary>Path (relative to the config file) of a file holding the body template. Mutually exclusive with bodyTemplate.</summary>
    public string? BodyFile { get; set; }

    /// <summary>Content type when a body is present. Defaults to application/json.</summary>
    public string? ContentType { get; set; }

    /// <summary>Statuses counted as success. Empty means any 2xx.</summary>
    public List<int> ExpectedStatusCodes { get; set; } = new();

    /// <summary>Overrides settings.requestTimeoutSeconds for this endpoint.</summary>
    public double? TimeoutSeconds { get; set; }
}

public sealed class ScenarioConfig
{
    public string Name { get; set; } = "";

    public bool Enabled { get; set; } = true;

    /// <summary>Steps executed in order on every iteration (one iteration = one simulated user pass).</summary>
    public List<StepConfig> Steps { get; set; } = new();

    /// <summary>Optional warmup executed before measurement starts; its requests are not recorded.</summary>
    public WarmupConfig? Warmup { get; set; }

    /// <summary>Load phases executed in order. See PhaseConfig for the supported types.</summary>
    public List<PhaseConfig> Phases { get; set; } = new();

    /// <summary>Abort the remaining steps of an iteration when a step fails. Default true.</summary>
    public bool StopIterationOnFailure { get; set; } = true;

    /// <summary>Optional CSV feed; row values are exposed to templates as {{data.column}}.</summary>
    public DataFeedConfig? DataFeed { get; set; }

    /// <summary>Pass/fail assertions evaluated after the run; failures set a non-zero exit code.</summary>
    public ThresholdConfig? Thresholds { get; set; }

    /// <summary>Safety valve for rate-based phases: max in-flight iterations before new ones are skipped (and counted as throttled).</summary>
    public int MaxPendingRequests { get; set; } = 10_000;
}

public sealed class StepConfig
{
    /// <summary>Name of the endpoint to call.</summary>
    public string Endpoint { get; set; } = "";

    /// <summary>Display name in reports; defaults to the endpoint name.</summary>
    public string? Name { get; set; }

    /// <summary>Values extracted from the JSON response body into iteration variables ({{vars.x}}).</summary>
    public List<CaptureConfig> Capture { get; set; } = new();

    /// <summary>Think time after this step (not measured as latency).</summary>
    public double PauseAfterSeconds { get; set; }
}

public sealed class CaptureConfig
{
    /// <summary>Simple JSON path, e.g. "$.id" or "$.items[0].token".</summary>
    public string JsonPath { get; set; } = "";

    /// <summary>Variable name; later steps reference it as {{vars.name}}.</summary>
    public string As { get; set; } = "";
}

/// <summary>
/// One load phase. type selects the model:
///   constant      – closed model, fixed concurrent virtual users (concurrency)
///   ramp          – closed model, concurrency ramps linearly from→to
///   injectRate    – open model, start ratePerSecond iterations/s regardless of completion
///   rampRate      – open model, rate ramps linearly from→to iterations/s
///   pause         – idle wait
/// </summary>
public sealed class PhaseConfig
{
    public string Type { get; set; } = "";

    public double DurationSeconds { get; set; }

    /// <summary>constant: number of concurrent virtual users.</summary>
    public int Concurrency { get; set; }

    /// <summary>ramp/rampRate: starting value.</summary>
    public int From { get; set; }

    /// <summary>ramp/rampRate: ending value.</summary>
    public int To { get; set; }

    /// <summary>injectRate: iterations started per second.</summary>
    public double RatePerSecond { get; set; }

    /// <summary>Display name in reports; defaults to the type.</summary>
    public string? Name { get; set; }
}

public sealed class WarmupConfig
{
    public double DurationSeconds { get; set; } = 5;
    public int Concurrency { get; set; } = 1;
}

public sealed class DataFeedConfig
{
    /// <summary>CSV file with a header row, path relative to the config file.</summary>
    public string File { get; set; } = "";

    /// <summary>"circular" (default) walks rows in order per iteration; "random" picks a random row.</summary>
    public string Mode { get; set; } = "circular";
}

public sealed class ThresholdConfig
{
    public double? MaxErrorRatePercent { get; set; }
    public double? MaxP95Ms { get; set; }
    public double? MaxP99Ms { get; set; }
    public double? MaxMeanMs { get; set; }
    public double? MinRequestsPerSecond { get; set; }
}
