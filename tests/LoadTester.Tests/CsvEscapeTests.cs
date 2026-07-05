using LoadTester.Core.Reporting;
using Xunit;

namespace LoadTester.Tests;

public class CsvEscapeTests
{
    [Theory]
    [InlineData("plain", "plain")]
    [InlineData("has,comma", "\"has,comma\"")]
    [InlineData("has\"quote", "\"has\"\"quote\"")]
    [InlineData("line\nbreak", "\"line\nbreak\"")]
    [InlineData("carriage\rreturn", "\"carriage\rreturn\"")]  // bare \r must be quoted too
    public void Escape_QuotesDelimitersAndLineBreaks(string input, string expected)
    {
        Assert.Equal(expected, CsvReporter.Escape(input));
    }

    [Theory]
    [InlineData("=HYPERLINK(\"http://evil\")", "\"'=HYPERLINK(\"\"http://evil\"\")\"")]
    [InlineData("+sum", "'+sum")]
    [InlineData("-neg", "'-neg")]
    [InlineData("@cmd", "'@cmd")]
    public void Escape_NeutralizesSpreadsheetFormulas(string input, string expected)
    {
        Assert.Equal(expected, CsvReporter.Escape(input));
    }
}
