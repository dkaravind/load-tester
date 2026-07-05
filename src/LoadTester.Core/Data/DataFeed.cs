using LoadTester.Core.Configuration;

namespace LoadTester.Core.Data;

/// <summary>
/// CSV-backed data feed. The first row is the header; each iteration gets one row,
/// exposed to templates as {{data.column}}.
/// </summary>
public sealed class DataFeed
{
    private readonly IReadOnlyList<Dictionary<string, string>> _rows;
    private readonly bool _random;

    public IReadOnlyList<string> Columns { get; }

    private DataFeed(IReadOnlyList<string> columns, IReadOnlyList<Dictionary<string, string>> rows, bool random)
    {
        Columns = columns;
        _rows = rows;
        _random = random;
    }

    public static DataFeed Load(DataFeedConfig config, string configDir)
    {
        var path = Path.GetFullPath(Path.Combine(configDir, config.File));
        var lines = ParseCsv(File.ReadAllText(path));
        if (lines.Count < 2)
            throw new ConfigException($"Data feed {path} needs a header row and at least one data row.");

        var columns = lines[0];
        if (columns.Distinct(StringComparer.OrdinalIgnoreCase).Count() != columns.Count)
            throw new ConfigException($"Data feed {path} has duplicate column names.");

        var rows = new List<Dictionary<string, string>>(lines.Count - 1);
        for (var i = 1; i < lines.Count; i++)
        {
            if (lines[i].Count != columns.Count)
                throw new ConfigException(
                    $"Data feed {path} row {i + 1} has {lines[i].Count} fields, expected {columns.Count}.");
            var row = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (var c = 0; c < columns.Count; c++) row[columns[c]] = lines[i][c];
            rows.Add(row);
        }

        return new DataFeed(columns, rows, config.Mode.Equals("random", StringComparison.OrdinalIgnoreCase));
    }

    public IReadOnlyDictionary<string, string> GetRow(long iterationId) =>
        _random
            ? _rows[Random.Shared.Next(_rows.Count)]
            : _rows[(int)((iterationId - 1) % _rows.Count)];

    /// <summary>RFC-4180-ish CSV: quoted fields, doubled quotes, CRLF or LF. Blank lines are skipped.</summary>
    internal static List<List<string>> ParseCsv(string text)
    {
        var records = new List<List<string>>();
        var record = new List<string>();
        var field = new System.Text.StringBuilder();
        var inQuotes = false;
        var sawQuote = false; // a quoted empty field ("") is a real value, not a blank line

        void EndField() { record.Add(field.ToString()); field.Clear(); }
        void EndRecord()
        {
            EndField();
            if (record.Count > 1 || record[0].Length > 0 || sawQuote) records.Add(record);
            record = new List<string>();
            sawQuote = false;
        }

        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (inQuotes)
            {
                if (c == '"')
                {
                    if (i + 1 < text.Length && text[i + 1] == '"') { field.Append('"'); i++; }
                    else inQuotes = false;
                }
                else field.Append(c);
            }
            else switch (c)
            {
                case '"': inQuotes = true; sawQuote = true; break;
                case ',': EndField(); break;
                case '\r': break;
                case '\n': EndRecord(); break;
                default: field.Append(c); break;
            }
        }
        if (field.Length > 0 || record.Count > 0 || sawQuote) EndRecord();
        return records;
    }
}
