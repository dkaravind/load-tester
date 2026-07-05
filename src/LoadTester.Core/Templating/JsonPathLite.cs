using System.Globalization;
using System.Text.Json;

namespace LoadTester.Core.Templating;

/// <summary>
/// Minimal JSON path evaluator for response captures. Supports property access and
/// array indexing: "$.id", "$.data.items[0].token", "$[2].name". No wildcards or filters.
/// </summary>
public static class JsonPathLite
{
    public static bool IsValidPath(string path) => TryParse(path, out _);

    /// <summary>
    /// Extracts the value at <paramref name="path"/>. Strings are returned raw (unquoted);
    /// numbers/booleans/objects/arrays as their JSON text. Returns null when the path has no match
    /// or the value is JSON null. Throws <see cref="JsonException"/> for non-JSON input.
    /// </summary>
    public static string? Extract(string json, string path)
    {
        if (!TryParse(path, out var segments))
            throw new ArgumentException($"Invalid JSON path '{path}'", nameof(path));

        using var doc = JsonDocument.Parse(json);
        var current = doc.RootElement;
        foreach (var segment in segments)
        {
            if (segment is string property)
            {
                if (current.ValueKind != JsonValueKind.Object || !current.TryGetProperty(property, out current))
                    return null;
            }
            else
            {
                var index = (int)segment;
                if (current.ValueKind != JsonValueKind.Array || index >= current.GetArrayLength())
                    return null;
                current = current[index];
            }
        }

        return current.ValueKind switch
        {
            JsonValueKind.String => current.GetString(),
            JsonValueKind.Null or JsonValueKind.Undefined => null,
            _ => current.GetRawText(),
        };
    }

    /// <summary>Parses into segments: string = property name, int = array index.</summary>
    private static bool TryParse(string path, out List<object> segments)
    {
        segments = new List<object>();
        if (string.IsNullOrEmpty(path) || path[0] != '$') return false;

        var i = 1;
        while (i < path.Length)
        {
            switch (path[i])
            {
                case '.':
                {
                    var start = ++i;
                    while (i < path.Length && path[i] != '.' && path[i] != '[') i++;
                    if (i == start) return false;
                    segments.Add(path[start..i]);
                    break;
                }
                case '[':
                {
                    var start = ++i;
                    while (i < path.Length && path[i] != ']') i++;
                    if (i == path.Length) return false;
                    if (!int.TryParse(path[start..i], NumberStyles.None, CultureInfo.InvariantCulture, out var index))
                        return false;
                    segments.Add(index);
                    i++; // past ']'
                    break;
                }
                default:
                    return false;
            }
        }
        return segments.Count > 0;
    }
}
