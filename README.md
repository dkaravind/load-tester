# LoadTester

A configuration-driven HTTP load-testing console app for .NET 8, inspired by
[NBomber](https://nbomber.com/). You describe **endpoints** and **scenarios** in an
`endpoints.json` file; the CLI runs the scenarios and produces console, JSON, CSV and
HTML reports with latency percentiles, throughput timelines and pass/fail thresholds.

```
LoadTester.sln
├── src/LoadTester.Core       # engine: config, execution, metrics, reporting (no external deps)
├── src/LoadTester.Cli        # the console app (assembly name: loadtester)
├── samples/LoadTester.MockApi# tiny in-memory payments API to try the tool against
├── samples/endpoints.json    # demo config (all features)
├── samples/paymentsapi.endpoints.json  # config for the real PaymentsApi in this repo
└── tests/LoadTester.Tests    # unit tests
```

## Quick start

```bash
# terminal 1 — start the demo target API (http://localhost:5089)
dotnet run --project samples/LoadTester.MockApi

# terminal 2 — run the demo scenario
cd samples
dotnet run --project ../src/LoadTester.Cli -- --config endpoints.json --scenario smoke
```

You get a live status line every 5 seconds, then a summary table, and report files under
`reports/<timestamp>/`:

```
 step            count     ok  fail    rps     min    mean     p50     p95     p99    max
 list-payments     549    549     0   36.3   5.0ms  33.9ms  34.2ms  58.9ms  82.2ms  111ms
 create-payment    549    549     0   36.3  14.8ms  68.6ms  69.3ms   116ms   121ms  152ms
 ...
 Thresholds:
   [PASS] smoke: maxErrorRatePercent limit 1, actual 0
```

To load-test the PaymentsApi in this repo:

```bash
cd samples
dotnet run --project ../src/LoadTester.Cli -- --config paymentsapi.endpoints.json            # baseline
dotnet run --project ../src/LoadTester.Cli -- --config paymentsapi.endpoints.json -s degradation-repro
```

## CLI

```
loadtester [--config endpoints.json] [options]

-c, --config <path>      Config file (default: ./endpoints.json)
-s, --scenario <names>   Run only these scenarios (comma separated or repeat the flag).
                         Explicitly selected scenarios run even if enabled=false.
-o, --output <folder>    Report folder (default: settings.reports.folder)
-f, --formats <list>     Override report formats: console,json,csv,html
    --interval <secs>    Seconds between live status lines (default 5)
    --validate           Validate the config and exit
    --list               List endpoints and scenarios and exit
-q, --quiet              Suppress the live status line
```

Exit codes: `0` success · `1` thresholds failed · `2` config/argument error ·
`3` unexpected error · `130` aborted. First **Ctrl+C** stops gracefully (in-flight requests
finish, partial results are reported); second Ctrl+C aborts in-flight requests.

## Configuration

One JSON file describes everything. Comments and trailing commas are allowed; unknown
property names are rejected (so typos fail loudly instead of silently doing nothing).

```jsonc
{
  "settings": {
    "baseAddress": "http://localhost:5089",   // prepended to relative endpoint paths
    "defaultHeaders": { "Accept": "application/json" },
    "requestTimeoutSeconds": 30,              // per request; endpoints can override
    "maxConnectionsPerServer": 1024,
    "skipTlsVerification": false,             // accept self-signed dev certs
    "followRedirects": false,                 // keep 3xx measurable; true to follow redirects
    "runScenariosInParallel": false,          // default: scenarios run one after another
    "reports": { "folder": "reports", "formats": [ "console", "json", "html", "csv" ] }
  },

  "endpoints": [
    {
      "name": "create-payment",               // unique; referenced by scenario steps
      "method": "POST",
      "path": "/payments",                    // or an absolute http(s) URL
      "headers": { "Authorization": "Bearer {{env:API_TOKEN}}" },
      "bodyTemplate": "{ \"amount\": {{randomDouble:5:250:2}}, \"ref\": \"{{guid}}\" }",
      // "bodyFile": "bodies/payment.json",   // alternative: template in a file
      "contentType": "application/json",      // default when a body is present
      "expectedStatusCodes": [ 201 ],         // empty = any 2xx counts as success
      "timeoutSeconds": 10                    // overrides settings.requestTimeoutSeconds
    }
  ],

  "scenarios": [
    {
      "name": "payment-journey",
      "enabled": true,                        // disabled scenarios run only via --scenario
      "warmup": { "durationSeconds": 5, "concurrency": 2 },  // not measured
      "steps": [                              // executed in order per iteration
        {
          "endpoint": "create-payment",
          "name": "create",                   // optional display name in reports
          "capture": [ { "jsonPath": "$.id", "as": "paymentId" } ],
          "pauseAfterSeconds": 0.2            // think time (not measured as latency)
        },
        { "endpoint": "get-payment" }         // its path can use {{vars.paymentId}}
      ],
      "stopIterationOnFailure": true,         // skip remaining steps when one fails
      "dataFeed": { "file": "data/cards.csv", "mode": "circular" },  // or "random"
      "phases": [ /* see below */ ],
      "maxPendingRequests": 10000,            // open-model safety valve
      "thresholds": {
        "maxErrorRatePercent": 1,
        "maxP95Ms": 3000,                     // over all requests, timeouts included
        "maxP99Ms": null,
        "maxMeanMs": null,
        "minRequestsPerSecond": 20
      }
    }
  ]
}
```

### Load phases

Phases run in order and can be mixed freely. Two load models:

**Closed model** — a fixed pool of virtual users; each starts its next iteration only after
the previous one finishes (like NBomber `KeepConstant`/`RampingConstant`):

```jsonc
{ "type": "constant", "concurrency": 50, "durationSeconds": 60 }
{ "type": "ramp", "from": 1, "to": 50, "durationSeconds": 30 }     // also ramps down: 50→0
```

**Open model** — iterations start at a target *arrival rate* regardless of how slow responses
get (like NBomber `Inject`/`RampingInject`). This is the model that exposes queueing collapse,
because a slow API doesn't slow the load generator down:

```jsonc
{ "type": "injectRate", "ratePerSecond": 100, "durationSeconds": 60 }
{ "type": "rampRate", "from": 10, "to": 300, "durationSeconds": 120 }
```

If responses lag so far behind that `maxPendingRequests` iterations are in flight, new
iterations are skipped and reported as **throttled** — a strong signal the target is saturated.

And `{ "type": "pause", "durationSeconds": 10 }` idles between phases.

### Placeholders

Usable in `path`, header values and body templates:

| Token | Meaning |
|---|---|
| `{{guid}}` | new GUID |
| `{{randomInt:1:100}}` | inclusive random integer |
| `{{randomDouble:5:250:2}}` | random double, optional decimals (default 2) |
| `{{randomString:16}}` | random alphanumeric string |
| `{{now}}` / `{{utcNow}}` | ISO-8601 timestamp; optional format: `{{utcNow:yyyy-MM-dd}}` |
| `{{timestamp}}` / `{{timestampMs}}` | unix seconds / milliseconds |
| `{{iteration}}` | iteration id (1-based, per scenario) |
| `{{env:NAME}}` | environment variable (secrets stay out of the config) |
| `{{vars.name}}` | value captured from an earlier step's response |
| `{{data.column}}` | column of the scenario's CSV data-feed row |

Captures use a simple JSON path (`$.id`, `$.items[0].token` — no wildcards). A failed capture
fails the step.

## Reports

- **console** — summary table per scenario (per-step count/ok/fail/rps + latency percentiles),
  status-code breakdown, top errors, threshold results.
- **json** — `summary.json`, the full report model (good for CI trend tooling).
- **csv** — `summary.csv` (per-step stats) and `timeseries.csv` (per-second count/failed/mean/p95).
- **html** — `report.html`, self-contained page with stat tiles, tables and SVG charts
  (throughput and latency per second).

Latency percentiles use the nearest-rank method. Warmup requests are excluded. Timeouts and
transport errors count as failures and are included in the all-requests latency distribution
that thresholds are evaluated against.

## Extending it

The engine is a small set of composable pieces in `LoadTester.Core`; the CLI is ~150 lines
of wiring. Natural extension points:

- **Custom placeholder tokens** — `PlaceholderResolver.Register("orderId", (args, ctx) => ...)`
  before the run starts (e.g. in a small custom `Program.cs` that references Core).
- **New report formats** — implement a renderer over `RunReport` (see `JsonReporter`, ~10 lines)
  and add a case to `ReportWriter`.
- **New phase types** — add a case to `ScenarioRunner.RunAsync`'s switch plus validation in
  `ConfigValidator.ValidatePhase` (e.g. step-load: constant × N repeating).
- **Embedding** — reference `LoadTester.Core` and drive `LoadEngine.RunAsync` from your own
  host (CI harness, benchmark suite); `RunOptions.StatusSink` redirects live output.

## Design notes

- One shared `HttpClient`/`SocketsHttpHandler` per run; per-request timeouts via linked
  cancellation tokens; response bodies are fully read so latency includes the body transfer.
- Cookies are disabled: a shared cookie jar would make every virtual user share one session,
  and a load balancer's affinity cookie would pin all generated load to a single backend.
  Redirects are not followed unless `followRedirects` is true, so 3xx responses stay measurable.
- Config validation cross-checks templates against their scenario: `{{data.*}}` requires a
  data feed containing that column, `{{vars.*}}` requires an earlier step capturing that name —
  so a doomed config fails `--validate` instead of producing a run full of template errors.
- Requests are recorded lock-free into a `ConcurrentQueue`; stats are computed once at the end.
- Closed-model ramps re-evaluate the target concurrency continuously; idle workers park on a
  50 ms poll. Open-model scheduling uses 100 ms ticks with fractional-rate carry, so rates
  below 10/s are honored exactly.
- Graceful stop (first Ctrl+C) reports whatever was measured; aborted requests (second Ctrl+C)
  are discarded rather than skewing the percentiles.
