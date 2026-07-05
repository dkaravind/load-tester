using LoadTester.Core.Configuration;
using LoadTester.Core.Data;
using Xunit;

namespace LoadTester.Tests;

public class DataFeedTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("loadtester-feed").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private DataFeed Load(string csv, string mode = "circular")
    {
        File.WriteAllText(Path.Combine(_dir, "feed.csv"), csv);
        return DataFeed.Load(new DataFeedConfig { File = "feed.csv", Mode = mode }, _dir);
    }

    [Fact]
    public void Load_ParsesHeaderAndRows()
    {
        var feed = Load("storeId,cardId\nS1,C1\nS2,C2\n");
        Assert.Equal(new[] { "storeId", "cardId" }, feed.Columns);
        Assert.Equal("C1", feed.GetRow(1)["cardId"]);
    }

    [Fact]
    public void GetRow_Circular_WrapsAround()
    {
        var feed = Load("a\n1\n2\n3\n");
        Assert.Equal("1", feed.GetRow(1)["a"]);
        Assert.Equal("3", feed.GetRow(3)["a"]);
        Assert.Equal("1", feed.GetRow(4)["a"]); // wraps
    }

    [Fact]
    public void ParseCsv_HandlesQuotedFieldsAndEscapedQuotes()
    {
        var rows = DataFeed.ParseCsv("name,notes\n\"Smith, John\",\"said \"\"hi\"\"\"\n");
        Assert.Equal("Smith, John", rows[1][0]);
        Assert.Equal("said \"hi\"", rows[1][1]);
    }

    [Fact]
    public void ParseCsv_SkipsBlankLines()
    {
        var rows = DataFeed.ParseCsv("a,b\n1,2\n\n3,4\n");
        Assert.Equal(3, rows.Count);
    }

    [Fact]
    public void ParseCsv_QuotedEmptyField_IsARealRowNotABlankLine()
    {
        var rows = DataFeed.ParseCsv("id\n1\n\"\"\n3\n");
        Assert.Equal(4, rows.Count);
        Assert.Equal("", rows[2][0]);
    }

    [Fact]
    public void Load_RowWithWrongFieldCount_Throws()
    {
        Assert.Throws<ConfigException>(() => Load("a,b\n1\n"));
    }

    [Fact]
    public void Load_HeaderOnly_Throws()
    {
        Assert.Throws<ConfigException>(() => Load("a,b\n"));
    }
}
