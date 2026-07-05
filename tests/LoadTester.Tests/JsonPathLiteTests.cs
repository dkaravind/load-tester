using LoadTester.Core.Templating;
using Xunit;

namespace LoadTester.Tests;

public class JsonPathLiteTests
{
    private const string Json = """
        {
          "id": "abc-123",
          "amount": 42.5,
          "approved": true,
          "meta": null,
          "batch": { "reference": "B-9" },
          "items": [ { "token": "t0" }, { "token": "t1" } ]
        }
        """;

    [Theory]
    [InlineData("$.id", "abc-123")]
    [InlineData("$.amount", "42.5")]
    [InlineData("$.approved", "true")]
    [InlineData("$.batch.reference", "B-9")]
    [InlineData("$.items[1].token", "t1")]
    public void Extract_FindsValues(string path, string expected)
    {
        Assert.Equal(expected, JsonPathLite.Extract(Json, path));
    }

    [Theory]
    [InlineData("$.missing")]
    [InlineData("$.meta")]
    [InlineData("$.items[9].token")]
    [InlineData("$.id.nested")]
    public void Extract_MissingOrNull_ReturnsNull(string path)
    {
        Assert.Null(JsonPathLite.Extract(Json, path));
    }

    [Fact]
    public void Extract_ObjectValue_ReturnsRawJson()
    {
        var raw = JsonPathLite.Extract(Json, "$.batch");
        Assert.Contains("\"reference\"", raw);
    }

    [Theory]
    [InlineData("$.id", true)]
    [InlineData("$.a.b[0].c", true)]
    [InlineData("$[0].id", true)]
    [InlineData("$", false)]       // no segments
    [InlineData("id", false)]      // must start with $
    [InlineData("$.", false)]      // empty property
    [InlineData("$.a[x]", false)]  // non-numeric index
    [InlineData("$.a[1", false)]   // unterminated index
    [InlineData("", false)]
    public void IsValidPath_ChecksSyntax(string path, bool expected)
    {
        Assert.Equal(expected, JsonPathLite.IsValidPath(path));
    }
}
