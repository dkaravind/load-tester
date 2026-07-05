using LoadTester.Core.Configuration;
using Xunit;

namespace LoadTester.Tests;

public class ConfigTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("loadtester-tests").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string WriteConfig(string json)
    {
        var path = Path.Combine(_dir, "endpoints.json");
        File.WriteAllText(path, json);
        return path;
    }

    private const string ValidConfig = """
        {
          // comments are allowed
          "settings": { "baseAddress": "http://localhost:5089" },
          "endpoints": [
            { "name": "list", "method": "GET", "path": "/api/payments" },
          ],
          "scenarios": [
            {
              "name": "smoke",
              "steps": [ { "endpoint": "list" } ],
              "phases": [ { "type": "constant", "concurrency": 2, "durationSeconds": 5 } ]
            }
          ]
        }
        """;

    [Fact]
    public void Load_ValidConfigWithCommentsAndTrailingCommas_Parses()
    {
        var config = ConfigLoader.Load(WriteConfig(ValidConfig));
        Assert.Single(config.Endpoints);
        Assert.Single(config.Scenarios);
        var result = ConfigValidator.Validate(config, _dir);
        Assert.True(result.IsValid, string.Join("; ", result.Errors));
    }

    [Fact]
    public void Load_UnknownProperty_ThrowsWithHelpfulMessage()
    {
        // "senarios" is a typo — must fail loudly instead of silently running nothing.
        var ex = Assert.Throws<ConfigException>(() =>
            ConfigLoader.Load(WriteConfig("""{ "senarios": [] }""")));
        Assert.Contains("senarios", ex.Message);
    }

    [Fact]
    public void Load_MissingFile_Throws()
    {
        Assert.Throws<ConfigException>(() => ConfigLoader.Load(Path.Combine(_dir, "nope.json")));
    }

    [Fact]
    public void Load_BodyFile_IsInlined()
    {
        File.WriteAllText(Path.Combine(_dir, "body.json"), """{ "a": "{{guid}}" }""");
        var config = ConfigLoader.Load(WriteConfig("""
            {
              "settings": { "baseAddress": "http://x" },
              "endpoints": [ { "name": "p", "method": "POST", "path": "/x", "bodyFile": "body.json" } ],
              "scenarios": []
            }
            """));
        Assert.Contains("{{guid}}", config.Endpoints[0].BodyTemplate);
    }

    [Fact]
    public void Validate_UnknownEndpointReference_Fails()
    {
        var config = ConfigLoader.Load(WriteConfig(ValidConfig.Replace("\"endpoint\": \"list\"", "\"endpoint\": \"nope\"")));
        var result = ConfigValidator.Validate(config, _dir);
        Assert.Contains(result.Errors, e => e.Contains("unknown endpoint 'nope'"));
    }

    [Fact]
    public void Validate_RelativePathWithoutBaseAddress_Fails()
    {
        var config = ConfigLoader.Load(WriteConfig(ValidConfig.Replace("\"baseAddress\": \"http://localhost:5089\"", "\"runScenariosInParallel\": false")));
        var result = ConfigValidator.Validate(config, _dir);
        Assert.Contains(result.Errors, e => e.Contains("baseAddress"));
    }

    [Fact]
    public void Validate_UnknownPhaseType_Fails()
    {
        var config = ConfigLoader.Load(WriteConfig(ValidConfig.Replace("\"type\": \"constant\"", "\"type\": \"warp\"")));
        var result = ConfigValidator.Validate(config, _dir);
        Assert.Contains(result.Errors, e => e.Contains("unknown phase type 'warp'"));
    }

    [Fact]
    public void Validate_UnknownPlaceholder_Fails()
    {
        var config = ConfigLoader.Load(WriteConfig(ValidConfig.Replace("/api/payments", "/api/{{bogusToken}}")));
        var result = ConfigValidator.Validate(config, _dir);
        Assert.Contains(result.Errors, e => e.Contains("bogusToken"));
    }

    [Fact]
    public void Validate_DuplicateNames_Fail()
    {
        var config = ConfigLoader.Load(WriteConfig("""
            {
              "settings": { "baseAddress": "http://x" },
              "endpoints": [
                { "name": "a", "method": "GET", "path": "/1" },
                { "name": "A", "method": "GET", "path": "/2" }
              ],
              "scenarios": [
                { "name": "s", "steps": [ { "endpoint": "a" } ],
                  "phases": [ { "type": "constant", "concurrency": 1, "durationSeconds": 1 } ] }
              ]
            }
            """));
        var result = ConfigValidator.Validate(config, _dir);
        Assert.Contains(result.Errors, e => e.Contains("Duplicate endpoint name"));
    }

    [Fact]
    public void Validate_DataTokenWithoutDataFeed_Fails()
    {
        var config = ConfigLoader.Load(WriteConfig(ValidConfig.Replace("/api/payments", "/api/{{data.cardId}}")));
        var result = ConfigValidator.Validate(config, _dir);
        Assert.Contains(result.Errors, e => e.Contains("data.cardId") && e.Contains("no dataFeed"));
    }

    [Fact]
    public void Validate_DataTokenWithUnknownColumn_Fails()
    {
        File.WriteAllText(Path.Combine(_dir, "feed.csv"), "storeId,cardId\nS1,C1\n");
        var config = ConfigLoader.Load(WriteConfig("""
            {
              "settings": { "baseAddress": "http://x" },
              "endpoints": [ { "name": "a", "method": "GET", "path": "/x/{{data.storId}}" } ],
              "scenarios": [
                { "name": "s", "steps": [ { "endpoint": "a" } ],
                  "dataFeed": { "file": "feed.csv", "mode": "Random" },
                  "phases": [ { "type": "constant", "concurrency": 1, "durationSeconds": 1 } ] }
              ]
            }
            """));
        var result = ConfigValidator.Validate(config, _dir);
        Assert.Contains(result.Errors, e => e.Contains("data.storId") && e.Contains("column"));
        // "Random" (any casing) must be accepted, matching runtime behavior.
        Assert.DoesNotContain(result.Errors, e => e.Contains("dataFeed.mode"));
    }

    [Fact]
    public void Validate_VarsTokenWithoutEarlierCapture_Fails()
    {
        var config = ConfigLoader.Load(WriteConfig(ValidConfig.Replace("/api/payments", "/api/{{vars.paymentId}}")));
        var result = ConfigValidator.Validate(config, _dir);
        Assert.Contains(result.Errors, e => e.Contains("vars.paymentId") && e.Contains("no earlier step captures"));
    }

    [Fact]
    public void Validate_VarsTokenCapturedByEarlierStep_Passes()
    {
        var config = ConfigLoader.Load(WriteConfig("""
            {
              "settings": { "baseAddress": "http://x" },
              "endpoints": [
                { "name": "create", "method": "POST", "path": "/x", "bodyTemplate": "{}" },
                { "name": "get", "method": "GET", "path": "/x/{{vars.id}}" }
              ],
              "scenarios": [
                { "name": "s",
                  "steps": [
                    { "endpoint": "create", "capture": [ { "jsonPath": "$.id", "as": "id" } ] },
                    { "endpoint": "get" }
                  ],
                  "phases": [ { "type": "constant", "concurrency": 1, "durationSeconds": 1 } ] }
              ]
            }
            """));
        var result = ConfigValidator.Validate(config, _dir);
        Assert.True(result.IsValid, string.Join("; ", result.Errors));
    }

    [Fact]
    public void Validate_ContentHeaderOnBodylessEndpoint_Fails()
    {
        var config = ConfigLoader.Load(WriteConfig("""
            {
              "settings": { "baseAddress": "http://x" },
              "endpoints": [
                { "name": "a", "method": "GET", "path": "/x", "headers": { "Content-Encoding": "gzip" } }
              ],
              "scenarios": [
                { "name": "s", "steps": [ { "endpoint": "a" } ],
                  "phases": [ { "type": "constant", "concurrency": 1, "durationSeconds": 1 } ] }
              ]
            }
            """));
        var result = ConfigValidator.Validate(config, _dir);
        Assert.Contains(result.Errors, e => e.Contains("Content-Encoding") && e.Contains("content header"));
    }

    [Fact]
    public void Validate_BadCaptureAndThresholds_Fail()
    {
        var config = ConfigLoader.Load(WriteConfig("""
            {
              "settings": { "baseAddress": "http://x" },
              "endpoints": [ { "name": "a", "method": "GET", "path": "/1" } ],
              "scenarios": [
                {
                  "name": "s",
                  "steps": [ { "endpoint": "a", "capture": [ { "jsonPath": "id", "as": "x" } ] } ],
                  "phases": [ { "type": "constant", "concurrency": 0, "durationSeconds": 1 } ],
                  "thresholds": { "maxP95Ms": -5 }
                }
              ]
            }
            """));
        var result = ConfigValidator.Validate(config, _dir);
        Assert.Contains(result.Errors, e => e.Contains("capture path 'id'"));
        Assert.Contains(result.Errors, e => e.Contains("concurrency >= 1"));
        Assert.Contains(result.Errors, e => e.Contains("maxP95Ms"));
    }
}
