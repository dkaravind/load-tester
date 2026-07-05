using LoadTester.Cli;
using Xunit;

namespace LoadTester.Tests;

public class CliOptionsTests
{
    [Fact]
    public void Parse_Defaults()
    {
        var options = CliOptions.Parse(Array.Empty<string>());
        Assert.Equal("endpoints.json", options.ConfigPath);
        Assert.Empty(options.Scenarios);
        Assert.Null(options.Formats);
        Assert.False(options.Quiet);
    }

    [Fact]
    public void Parse_ScenariosAccumulateAcrossFlagsAndCommas()
    {
        var options = CliOptions.Parse(new[] { "-s", "a,b", "--scenario", "c" });
        Assert.Equal(new[] { "a", "b", "c" }, options.Scenarios);
    }

    [Fact]
    public void Parse_ValidFormats_AreAccepted()
    {
        var options = CliOptions.Parse(new[] { "-f", "console,JSON,html" });
        Assert.Equal(3, options.Formats!.Count);
    }

    [Theory]
    [InlineData("bogus")]
    [InlineData("json,htlm")] // typo must fail loudly, not silently discard the run's reports
    public void Parse_UnknownFormat_Throws(string formats)
    {
        var ex = Assert.Throws<ArgumentException>(() => CliOptions.Parse(new[] { "--formats", formats }));
        Assert.Contains("format", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Parse_UnknownFlag_Throws()
    {
        Assert.Throws<ArgumentException>(() => CliOptions.Parse(new[] { "--bogus" }));
    }

    [Fact]
    public void Parse_FlagMissingValue_Throws()
    {
        Assert.Throws<ArgumentException>(() => CliOptions.Parse(new[] { "--config" }));
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-3")]
    [InlineData("abc")]
    public void Parse_BadInterval_Throws(string interval)
    {
        Assert.Throws<ArgumentException>(() => CliOptions.Parse(new[] { "--interval", interval }));
    }
}
