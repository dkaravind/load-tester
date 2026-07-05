using System.Text.Json;
using System.Text.Json.Serialization;

namespace LoadTester.Core.Configuration;

/// <summary>Raised for anything wrong with the config file; the message is user-facing.</summary>
public sealed class ConfigException : Exception
{
    public ConfigException(string message) : base(message) { }
    public ConfigException(string message, Exception inner) : base(message, inner) { }
}

public static class ConfigLoader
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        // Fail loudly on typos like "senarios" or "concurency" instead of silently ignoring them.
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    /// <summary>Loads and deserializes the config file. Body files referenced by endpoints are inlined.</summary>
    public static LoadTestConfig Load(string path)
    {
        if (!File.Exists(path))
            throw new ConfigException($"Config file not found: {Path.GetFullPath(path)}");

        LoadTestConfig? config;
        try
        {
            using var stream = File.OpenRead(path);
            config = JsonSerializer.Deserialize<LoadTestConfig>(stream, Options);
        }
        catch (JsonException ex)
        {
            var location = ex.LineNumber is { } line ? $" (line {line + 1}, pos {ex.BytePositionInLine + 1})" : "";
            throw new ConfigException($"Invalid JSON in {path}{location}: {ex.Message}", ex);
        }

        if (config is null)
            throw new ConfigException($"Config file {path} is empty.");

        InlineBodyFiles(config, Path.GetDirectoryName(Path.GetFullPath(path))!);
        return config;
    }

    private static void InlineBodyFiles(LoadTestConfig config, string configDir)
    {
        foreach (var endpoint in config.Endpoints)
        {
            if (string.IsNullOrWhiteSpace(endpoint.BodyFile)) continue;
            if (endpoint.BodyTemplate is not null)
                throw new ConfigException($"Endpoint '{endpoint.Name}': bodyTemplate and bodyFile are mutually exclusive.");

            var bodyPath = Path.GetFullPath(Path.Combine(configDir, endpoint.BodyFile));
            if (!File.Exists(bodyPath))
                throw new ConfigException($"Endpoint '{endpoint.Name}': bodyFile not found: {bodyPath}");
            endpoint.BodyTemplate = File.ReadAllText(bodyPath);
        }
    }
}
