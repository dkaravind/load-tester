using LoadTester.Core.Data;
using LoadTester.Core.Http;
using LoadTester.Core.Templating;

namespace LoadTester.Core.Configuration;

public sealed class ValidationResult
{
    public List<string> Errors { get; } = new();
    public List<string> Warnings { get; } = new();
    public bool IsValid => Errors.Count == 0;
}

public static class ConfigValidator
{
    private static readonly HashSet<string> Methods = new(StringComparer.OrdinalIgnoreCase)
        { "GET", "POST", "PUT", "DELETE", "PATCH", "HEAD", "OPTIONS" };

    private static readonly HashSet<string> PhaseTypes = new(StringComparer.OrdinalIgnoreCase)
        { "constant", "ramp", "injectRate", "rampRate", "pause" };

    private static readonly HashSet<string> ReportFormats = new(StringComparer.OrdinalIgnoreCase)
        { "console", "json", "csv", "html" };

    /// <param name="configDir">Directory of the config file; relative paths (data feeds) resolve against it.</param>
    public static ValidationResult Validate(LoadTestConfig config, string configDir)
    {
        var result = new ValidationResult();
        void Error(string msg) => result.Errors.Add(msg);
        void Warn(string msg) => result.Warnings.Add(msg);

        ValidateSettings(config.Settings, Error, Warn);
        ValidateEndpoints(config, Error, Warn);
        ValidateScenarios(config, configDir, Error, Warn);
        return result;
    }

    private static void ValidateSettings(GlobalSettings settings, Action<string> error, Action<string> warn)
    {
        if (settings.RequestTimeoutSeconds <= 0)
            error("settings.requestTimeoutSeconds must be > 0.");
        if (settings.MaxConnectionsPerServer <= 0)
            error("settings.maxConnectionsPerServer must be > 0.");
        if (settings.BaseAddress is { } baseAddress &&
            (!Uri.TryCreate(baseAddress, UriKind.Absolute, out var uri) || (uri.Scheme != "http" && uri.Scheme != "https")))
            error($"settings.baseAddress is not a valid http(s) URL: '{baseAddress}'.");

        foreach (var format in settings.Reports.Formats.Where(f => !ReportFormats.Contains(f)))
            error($"settings.reports.formats contains unknown format '{format}' (expected console, json, csv or html).");
    }

    private static void ValidateEndpoints(LoadTestConfig config, Action<string> error, Action<string> warn)
    {
        if (config.Endpoints.Count == 0)
            error("Config declares no endpoints.");

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var endpoint in config.Endpoints)
        {
            var label = string.IsNullOrWhiteSpace(endpoint.Name) ? "(unnamed)" : endpoint.Name;
            if (string.IsNullOrWhiteSpace(endpoint.Name))
                error("An endpoint is missing a name.");
            else if (!seen.Add(endpoint.Name))
                error($"Duplicate endpoint name '{endpoint.Name}'.");

            if (!Methods.Contains(endpoint.Method))
                error($"Endpoint '{label}': unknown HTTP method '{endpoint.Method}'.");

            if (string.IsNullOrWhiteSpace(endpoint.Path))
                error($"Endpoint '{label}': path is required.");
            else if (!IsAbsoluteHttpUrl(endpoint.Path) && string.IsNullOrWhiteSpace(config.Settings.BaseAddress))
                error($"Endpoint '{label}': path '{endpoint.Path}' is relative but settings.baseAddress is not set.");

            foreach (var status in endpoint.ExpectedStatusCodes.Where(s => s is < 100 or > 599))
                error($"Endpoint '{label}': expected status code {status} is out of range 100-599.");

            if (endpoint.TimeoutSeconds is <= 0)
                error($"Endpoint '{label}': timeoutSeconds must be > 0.");

            if (endpoint.BodyTemplate is null)
            {
                foreach (var header in endpoint.Headers.Keys.Where(ContentHeaderNames.IsContentHeader))
                    error($"Endpoint '{label}': '{header}' is a content header and needs a request body " +
                          "(bodyTemplate or bodyFile); it cannot be sent on a body-less request.");
            }

            foreach (var template in EnumerateTemplates(endpoint))
                ValidateTemplate(template, $"endpoint '{label}'", error);
        }
    }

    private static void ValidateScenarios(LoadTestConfig config, string configDir, Action<string> error, Action<string> warn)
    {
        if (config.Scenarios.Count == 0)
            error("Config declares no scenarios.");
        else if (config.Scenarios.All(s => !s.Enabled))
            warn("All scenarios are disabled; nothing will run unless one is selected explicitly.");

        var endpointsByName = new Dictionary<string, EndpointConfig>(StringComparer.OrdinalIgnoreCase);
        foreach (var endpoint in config.Endpoints.Where(e => !string.IsNullOrWhiteSpace(e.Name)))
            endpointsByName.TryAdd(endpoint.Name, endpoint);

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var scenario in config.Scenarios)
        {
            var label = string.IsNullOrWhiteSpace(scenario.Name) ? "(unnamed)" : scenario.Name;
            if (string.IsNullOrWhiteSpace(scenario.Name))
                error("A scenario is missing a name.");
            else if (!seen.Add(scenario.Name))
                error($"Duplicate scenario name '{scenario.Name}'.");

            if (scenario.Steps.Count == 0)
                error($"Scenario '{label}': at least one step is required.");
            if (scenario.Phases.Count == 0)
                error($"Scenario '{label}': at least one phase is required.");
            if (scenario.MaxPendingRequests <= 0)
                error($"Scenario '{label}': maxPendingRequests must be > 0.");

            foreach (var step in scenario.Steps)
            {
                if (string.IsNullOrWhiteSpace(step.Endpoint))
                    error($"Scenario '{label}': a step is missing its endpoint reference.");
                else if (!endpointsByName.ContainsKey(step.Endpoint))
                    error($"Scenario '{label}': step references unknown endpoint '{step.Endpoint}'.");

                if (step.PauseAfterSeconds < 0)
                    error($"Scenario '{label}': pauseAfterSeconds cannot be negative.");

                foreach (var capture in step.Capture)
                {
                    if (string.IsNullOrWhiteSpace(capture.As))
                        error($"Scenario '{label}': capture in step '{step.Endpoint}' is missing 'as'.");
                    if (!JsonPathLite.IsValidPath(capture.JsonPath))
                        error($"Scenario '{label}': capture path '{capture.JsonPath}' is invalid. " +
                              "Use the form $.property, $.a.b[0].c or $[0].id.");
                }
            }

            foreach (var phase in scenario.Phases)
                ValidatePhase(scenario, phase, label, error);

            if (scenario.Warmup is { } warmup)
            {
                if (warmup.DurationSeconds <= 0)
                    error($"Scenario '{label}': warmup.durationSeconds must be > 0.");
                if (warmup.Concurrency < 1)
                    error($"Scenario '{label}': warmup.concurrency must be >= 1.");
            }

            ValidateDataFeed(scenario, label, configDir, error);
            ValidateThresholds(scenario, label, error);
            ValidateScenarioTemplates(scenario, label, endpointsByName, configDir, error);
        }
    }

    /// <summary>
    /// Cross-checks {{data.*}} and {{vars.*}} usage per scenario: a data token needs a dataFeed with
    /// that column, and a vars token needs an earlier step capturing that name. Without this, a config
    /// that passes validation can still fail on 100% of its requests at runtime.
    /// </summary>
    private static void ValidateScenarioTemplates(
        ScenarioConfig scenario,
        string label,
        IReadOnlyDictionary<string, EndpointConfig> endpointsByName,
        string configDir,
        Action<string> error)
    {
        var hasFeed = scenario.DataFeed is { } feed && !string.IsNullOrWhiteSpace(feed.File);
        IReadOnlyList<string>? feedColumns = null; // null = header unknown, skip column checks
        if (hasFeed)
        {
            var path = Path.GetFullPath(Path.Combine(configDir, scenario.DataFeed!.File));
            if (File.Exists(path))
            {
                try
                {
                    var header = DataFeed.ParseCsv(File.ReadLines(path).FirstOrDefault() ?? "");
                    if (header.Count > 0) feedColumns = header[0];
                }
                catch (IOException) { /* runtime load will surface the problem */ }
            }
        }

        var captured = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var step in scenario.Steps)
        {
            if (endpointsByName.TryGetValue(step.Endpoint ?? "", out var endpoint))
            {
                foreach (var token in EnumerateTemplates(endpoint).SelectMany(PlaceholderResolver.ExtractTokens))
                {
                    var name = token.Split(':', 2)[0].Trim();
                    if (name.StartsWith("data.", StringComparison.OrdinalIgnoreCase))
                    {
                        var column = name["data.".Length..];
                        if (!hasFeed)
                            error($"Scenario '{label}': endpoint '{endpoint.Name}' uses {{{{{token}}}}} " +
                                  "but the scenario has no dataFeed.");
                        else if (feedColumns is not null &&
                                 !feedColumns.Contains(column, StringComparer.OrdinalIgnoreCase))
                            error($"Scenario '{label}': {{{{{token}}}}} references a column not present in " +
                                  $"'{scenario.DataFeed!.File}' (columns: {string.Join(", ", feedColumns)}).");
                    }
                    else if (name.StartsWith("vars.", StringComparison.OrdinalIgnoreCase))
                    {
                        var varName = name["vars.".Length..];
                        if (!captured.Contains(varName))
                            error($"Scenario '{label}': endpoint '{endpoint.Name}' uses {{{{{token}}}}} " +
                                  $"but no earlier step captures '{varName}'.");
                    }
                }
            }

            foreach (var capture in step.Capture.Where(c => !string.IsNullOrWhiteSpace(c.As)))
                captured.Add(capture.As);
        }
    }

    private static void ValidatePhase(ScenarioConfig scenario, PhaseConfig phase, string label, Action<string> error)
    {
        if (!PhaseTypes.Contains(phase.Type))
        {
            error($"Scenario '{label}': unknown phase type '{phase.Type}' " +
                  "(expected constant, ramp, injectRate, rampRate or pause).");
            return;
        }
        if (phase.DurationSeconds <= 0)
            error($"Scenario '{label}': phase '{phase.Type}' needs durationSeconds > 0.");

        switch (phase.Type.ToLowerInvariant())
        {
            case "constant" when phase.Concurrency < 1:
                error($"Scenario '{label}': constant phase needs concurrency >= 1.");
                break;
            case "ramp" when phase.From < 0 || phase.To < 0 || Math.Max(phase.From, phase.To) < 1:
                error($"Scenario '{label}': ramp phase needs from/to >= 0 with at least one of them >= 1.");
                break;
            case "injectrate" when phase.RatePerSecond <= 0:
                error($"Scenario '{label}': injectRate phase needs ratePerSecond > 0.");
                break;
            case "ramprate" when phase.From < 0 || phase.To < 0 || Math.Max(phase.From, phase.To) < 1:
                error($"Scenario '{label}': rampRate phase needs from/to >= 0 with at least one of them >= 1.");
                break;
        }
    }

    private static void ValidateDataFeed(ScenarioConfig scenario, string label, string configDir, Action<string> error)
    {
        if (scenario.DataFeed is not { } feed) return;

        if (!feed.Mode.Equals("circular", StringComparison.OrdinalIgnoreCase) &&
            !feed.Mode.Equals("random", StringComparison.OrdinalIgnoreCase))
            error($"Scenario '{label}': dataFeed.mode must be 'circular' or 'random'.");
        if (string.IsNullOrWhiteSpace(feed.File))
            error($"Scenario '{label}': dataFeed.file is required.");
        else if (!File.Exists(Path.GetFullPath(Path.Combine(configDir, feed.File))))
            error($"Scenario '{label}': dataFeed file not found: {Path.GetFullPath(Path.Combine(configDir, feed.File))}");
    }

    private static void ValidateThresholds(ScenarioConfig scenario, string label, Action<string> error)
    {
        if (scenario.Thresholds is not { } t) return;
        foreach (var (name, value) in new (string, double?)[]
                 {
                     ("maxErrorRatePercent", t.MaxErrorRatePercent), ("maxP95Ms", t.MaxP95Ms),
                     ("maxP99Ms", t.MaxP99Ms), ("maxMeanMs", t.MaxMeanMs),
                     ("minRequestsPerSecond", t.MinRequestsPerSecond),
                 })
        {
            if (value is < 0)
                error($"Scenario '{label}': thresholds.{name} cannot be negative.");
        }
    }

    private static IEnumerable<string> EnumerateTemplates(EndpointConfig endpoint)
    {
        yield return endpoint.Path;
        if (endpoint.BodyTemplate is { } body) yield return body;
        foreach (var value in endpoint.Headers.Values) yield return value;
    }

    private static void ValidateTemplate(string template, string owner, Action<string> error)
    {
        foreach (var token in PlaceholderResolver.ExtractTokens(template))
        {
            if (!PlaceholderResolver.IsKnownToken(token))
                error($"Unknown placeholder '{{{{{token}}}}}' in {owner}. " +
                      $"Known: {string.Join(", ", PlaceholderResolver.KnownTokenNames)}, vars.<name>, data.<column>, env:<NAME>.");
        }
    }

    private static bool IsAbsoluteHttpUrl(string path) =>
        Uri.TryCreate(path, UriKind.Absolute, out var uri) && (uri.Scheme == "http" || uri.Scheme == "https");
}
