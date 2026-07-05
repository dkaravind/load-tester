using LoadTester.Core.Execution;
using LoadTester.Core.Templating;
using Xunit;

namespace LoadTester.Tests;

public class PlaceholderResolverTests
{
    private static IterationContext Ctx(long id = 1) => new() { IterationId = id };

    [Fact]
    public void Resolve_TemplateWithoutTokens_IsUnchanged()
    {
        Assert.Equal("/api/payments", PlaceholderResolver.Resolve("/api/payments", Ctx()));
    }

    [Fact]
    public void Resolve_Guid_ProducesValidGuid()
    {
        var result = PlaceholderResolver.Resolve("{{guid}}", Ctx());
        Assert.True(Guid.TryParse(result, out _));
    }

    [Fact]
    public void Resolve_RandomInt_StaysInInclusiveRange()
    {
        for (var i = 0; i < 200; i++)
        {
            var value = int.Parse(PlaceholderResolver.Resolve("{{randomInt:5:7}}", Ctx()));
            Assert.InRange(value, 5, 7);
        }
    }

    [Fact]
    public void Resolve_MultipleTokens_AllReplaced()
    {
        var result = PlaceholderResolver.Resolve("id={{iteration}}&r={{randomInt:1:1}}", Ctx(42));
        Assert.Equal("id=42&r=1", result);
    }

    [Fact]
    public void Resolve_Vars_ReadsCapturedValue()
    {
        var ctx = Ctx();
        ctx.Vars["paymentId"] = "abc-123";
        Assert.Equal("/api/payments/abc-123", PlaceholderResolver.Resolve("/api/payments/{{vars.paymentId}}", ctx));
    }

    [Fact]
    public void Resolve_MissingVar_Throws()
    {
        var ex = Assert.Throws<TemplateException>(() => PlaceholderResolver.Resolve("{{vars.nope}}", Ctx()));
        Assert.Contains("nope", ex.Message);
    }

    [Fact]
    public void Resolve_DataColumn_ReadsRow()
    {
        var ctx = new IterationContext
        {
            IterationId = 1,
            DataRow = new Dictionary<string, string> { ["cardId"] = "tok-1" },
        };
        Assert.Equal("tok-1", PlaceholderResolver.Resolve("{{data.cardId}}", ctx));
    }

    [Fact]
    public void Resolve_DataWithoutFeed_Throws()
    {
        Assert.Throws<TemplateException>(() => PlaceholderResolver.Resolve("{{data.cardId}}", Ctx()));
    }

    [Fact]
    public void Resolve_UnknownToken_Throws()
    {
        Assert.Throws<TemplateException>(() => PlaceholderResolver.Resolve("{{nonsense}}", Ctx()));
    }

    [Fact]
    public void Resolve_Env_ReadsEnvironmentVariable()
    {
        Environment.SetEnvironmentVariable("LOADTESTER_TEST_TOKEN", "secret");
        try
        {
            Assert.Equal("Bearer secret",
                PlaceholderResolver.Resolve("Bearer {{env:LOADTESTER_TEST_TOKEN}}", Ctx()));
        }
        finally
        {
            Environment.SetEnvironmentVariable("LOADTESTER_TEST_TOKEN", null);
        }
    }

    [Fact]
    public void Resolve_NowWithColonFormat_RejoinsFormat()
    {
        var result = PlaceholderResolver.Resolve("{{utcNow:yyyy-MM-dd}}", Ctx());
        Assert.Matches(@"^\d{4}-\d{2}-\d{2}$", result);
    }

    [Fact]
    public void Resolve_RandomString_HasRequestedLength()
    {
        Assert.Equal(16, PlaceholderResolver.Resolve("{{randomString:16}}", Ctx()).Length);
    }

    [Fact]
    public void Resolve_RandomInt_IncludesIntMaxValue()
    {
        Assert.Equal("2147483647",
            PlaceholderResolver.Resolve("{{randomInt:2147483647:2147483647}}", Ctx()));
    }

    [Fact]
    public void Resolve_RandomDouble_UsesFixedPointNeverScientificNotation()
    {
        // 0.00005..0.00005 forced value; round-trip ToString would render 5E-05.
        Assert.Equal("0.000050", PlaceholderResolver.Resolve("{{randomDouble:0.00005:0.00005:6}}", Ctx()));
        Assert.Equal("0.500", PlaceholderResolver.Resolve("{{randomDouble:0.5:0.5:3}}", Ctx()));
    }

    [Fact]
    public void Resolve_Vars_IsCaseInsensitive()
    {
        var ctx = Ctx();
        ctx.Vars["PaymentId"] = "abc";
        Assert.Equal("abc", PlaceholderResolver.Resolve("{{vars.paymentid}}", ctx));
    }

    [Fact]
    public void Register_CustomToken_IsResolvedAndKnown()
    {
        PlaceholderResolver.Register("answer", (_, _) => "42");
        Assert.Equal("42", PlaceholderResolver.Resolve("{{answer}}", Ctx()));
        Assert.True(PlaceholderResolver.IsKnownToken("answer"));
    }

    [Theory]
    [InlineData("guid", true)]
    [InlineData("randomInt:1:10", true)]
    [InlineData("vars.x", true)]
    [InlineData("data.col", true)]
    [InlineData("env:HOME", true)]
    [InlineData("vars.", false)]
    [InlineData("bogus", false)]
    public void IsKnownToken_ChecksNames(string token, bool expected)
    {
        Assert.Equal(expected, PlaceholderResolver.IsKnownToken(token));
    }

    [Fact]
    public void ExtractTokens_FindsAllTokens()
    {
        var tokens = PlaceholderResolver.ExtractTokens("{{guid}}/x/{{ vars.id }}?n={{randomInt:1:5}}");
        Assert.Equal(new[] { "guid", "vars.id", "randomInt:1:5" }, tokens);
    }
}
