using LoadTester.Core.Configuration;
using LoadTester.Core.Execution;
using LoadTester.Core.Http;
using Xunit;

namespace LoadTester.Tests;

public class RequestBuilderTests
{
    private static IterationContext Ctx() => new() { IterationId = 1 };

    [Theory]
    [InlineData("http://host:5000", "/api/x", "http://host:5000/api/x")]
    [InlineData("http://host:5000/", "/api/x", "http://host:5000/api/x")]
    [InlineData("http://host:5000/", "api/x", "http://host:5000/api/x")]
    [InlineData("http://host", "https://other/abs", "https://other/abs")] // absolute path wins
    public void BuildUrl_CombinesBaseAndPath(string baseAddress, string path, string expected)
    {
        Assert.Equal(expected, RequestBuilder.BuildUrl(baseAddress, path));
    }

    [Fact]
    public void BuildUrl_RelativeWithoutBase_Throws()
    {
        Assert.Throws<InvalidOperationException>(() => RequestBuilder.BuildUrl(null, "/api/x"));
    }

    [Fact]
    public void Build_MergesHeaders_EndpointOverridesDefaults()
    {
        var settings = new GlobalSettings
        {
            BaseAddress = "http://host",
            DefaultHeaders = { ["Accept"] = "application/json", ["X-Env"] = "default" },
        };
        var endpoint = new EndpointConfig
        {
            Name = "e", Method = "GET", Path = "/x",
            Headers = { ["X-Env"] = "endpoint" },
        };

        using var request = new RequestBuilder(settings).Build(endpoint, Ctx());

        Assert.Equal("application/json", request.Headers.GetValues("Accept").Single());
        Assert.Equal("endpoint", request.Headers.GetValues("X-Env").Single());
    }

    [Fact]
    public async Task Build_Body_GetsResolvedTemplateAndContentType()
    {
        var settings = new GlobalSettings { BaseAddress = "http://host" };
        var endpoint = new EndpointConfig
        {
            Name = "e", Method = "POST", Path = "/x",
            BodyTemplate = """{ "n": {{randomInt:7:7}} }""",
        };

        using var request = new RequestBuilder(settings).Build(endpoint, Ctx());

        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("""{ "n": 7 }""", await request.Content!.ReadAsStringAsync());
        Assert.Equal("application/json", request.Content.Headers.GetValues("Content-Type").Single());
    }

    [Fact]
    public void Build_PathPlaceholders_AreResolved()
    {
        var settings = new GlobalSettings { BaseAddress = "http://host" };
        var endpoint = new EndpointConfig { Name = "e", Method = "GET", Path = "/api/items/{{vars.id}}" };
        var ctx = Ctx();
        ctx.Vars["id"] = "42";

        using var request = new RequestBuilder(settings).Build(endpoint, ctx);

        Assert.Equal("http://host/api/items/42", request.RequestUri!.ToString());
    }
}
