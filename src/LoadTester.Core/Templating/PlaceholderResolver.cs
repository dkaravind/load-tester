using System.Collections.Concurrent;
using System.Globalization;
using System.Text.RegularExpressions;
using LoadTester.Core.Execution;

namespace LoadTester.Core.Templating;

/// <summary>
/// Substitutes {{tokens}} in paths, headers and bodies. Built-in tokens:
///   {{guid}}                     new GUID
///   {{randomInt:min:max}}        inclusive random integer
///   {{randomDouble:min:max[:d]}} random double with d decimals (default 2)
///   {{randomString:length}}      random alphanumeric string
///   {{now[:format]}}             local time, ISO-8601 or a custom .NET format
///   {{utcNow[:format]}}          UTC time
///   {{timestamp}}                unix seconds
///   {{timestampMs}}              unix milliseconds
///   {{iteration}}                iteration id (1-based, per scenario)
///   {{env:NAME}}                 environment variable
///   {{vars.name}}                variable captured from an earlier step
///   {{data.column}}              column of the scenario's CSV data feed row
/// Extend with <see cref="Register"/> before the run starts.
/// </summary>
public static partial class PlaceholderResolver
{
    public delegate string TokenHandler(string[] args, IterationContext ctx);

    private static readonly ConcurrentDictionary<string, TokenHandler> Handlers = new(StringComparer.OrdinalIgnoreCase);

    [GeneratedRegex(@"\{\{\s*([^{}]+?)\s*\}\}")]
    private static partial Regex TokenRegex();

    static PlaceholderResolver()
    {
        Handlers["guid"] = (_, _) => Guid.NewGuid().ToString();
        Handlers["randomInt"] = (args, _) =>
        {
            RequireArgs("randomInt", args, 2);
            var min = ParseInt("randomInt", args[0]);
            var max = ParseInt("randomInt", args[1]);
            if (min > max) throw new TemplateException($"randomInt: min {min} > max {max}");
            return Random.Shared.NextInt64(min, (long)max + 1).ToString(CultureInfo.InvariantCulture);
        };
        Handlers["randomDouble"] = (args, _) =>
        {
            if (args.Length is < 2 or > 3) throw new TemplateException("randomDouble expects min:max[:decimals]");
            var min = ParseDouble("randomDouble", args[0]);
            var max = ParseDouble("randomDouble", args[1]);
            if (min > max) throw new TemplateException($"randomDouble: min {min} > max {max}");
            var decimals = Math.Clamp(args.Length == 3 ? ParseInt("randomDouble", args[2]) : 2, 0, 15);
            var value = min + Random.Shared.NextDouble() * (max - min);
            // Fixed-point, never scientific notation (round-trip ToString would emit 5E-05 etc.).
            return value.ToString("F" + decimals, CultureInfo.InvariantCulture);
        };
        Handlers["randomString"] = (args, _) =>
        {
            RequireArgs("randomString", args, 1);
            var length = ParseInt("randomString", args[0]);
            if (length is < 1 or > 65536) throw new TemplateException("randomString length must be 1-65536");
            const string alphabet = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789";
            return string.Create(length, alphabet,
                (span, chars) => { for (var i = 0; i < span.Length; i++) span[i] = chars[Random.Shared.Next(chars.Length)]; });
        };
        Handlers["now"] = (args, _) => FormatTime(DateTime.Now, args);
        Handlers["utcNow"] = (args, _) => FormatTime(DateTime.UtcNow, args);
        Handlers["timestamp"] = (_, _) => DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
        Handlers["timestampMs"] = (_, _) => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture);
        Handlers["iteration"] = (_, ctx) => ctx.IterationId.ToString(CultureInfo.InvariantCulture);
        Handlers["env"] = (args, _) =>
        {
            RequireArgs("env", args, 1);
            return Environment.GetEnvironmentVariable(args[0])
                   ?? throw new TemplateException($"environment variable '{args[0]}' is not set");
        };
    }

    /// <summary>Names usable in validation messages (excludes the vars./data./env: prefix forms).</summary>
    public static IReadOnlyCollection<string> KnownTokenNames => Handlers.Keys.OrderBy(k => k).ToArray();

    /// <summary>Registers (or replaces) a custom token, e.g. Register("orderId", (args, ctx) => ...).</summary>
    public static void Register(string name, TokenHandler handler) => Handlers[name] = handler;

    public static string Resolve(string template, IterationContext ctx)
    {
        if (!template.Contains("{{", StringComparison.Ordinal)) return template;
        return TokenRegex().Replace(template, match => ResolveToken(match.Groups[1].Value, ctx));
    }

    /// <summary>All raw tokens found in a template, e.g. "randomInt:1:10", "vars.paymentId".</summary>
    public static IReadOnlyList<string> ExtractTokens(string template) =>
        TokenRegex().Matches(template).Select(m => m.Groups[1].Value).ToList();

    public static bool IsKnownToken(string token)
    {
        var name = token.Split(':', 2)[0].Trim();
        if (name.StartsWith("vars.", StringComparison.OrdinalIgnoreCase))
            return name.Length > "vars.".Length;
        if (name.StartsWith("data.", StringComparison.OrdinalIgnoreCase))
            return name.Length > "data.".Length;
        return Handlers.ContainsKey(name);
    }

    private static string ResolveToken(string token, IterationContext ctx)
    {
        if (token.StartsWith("vars.", StringComparison.OrdinalIgnoreCase))
        {
            var name = token["vars.".Length..];
            return ctx.Vars.TryGetValue(name, out var value)
                ? value
                : throw new TemplateException($"variable '{name}' has not been captured by an earlier step");
        }
        if (token.StartsWith("data.", StringComparison.OrdinalIgnoreCase))
        {
            var column = token["data.".Length..];
            if (ctx.DataRow is null) throw new TemplateException("{{data.*}} used but the scenario has no dataFeed");
            return ctx.DataRow.TryGetValue(column, out var value)
                ? value
                : throw new TemplateException($"data feed has no column '{column}'");
        }

        var parts = token.Split(':');
        var handlerName = parts[0].Trim();
        if (!Handlers.TryGetValue(handlerName, out var handler))
            throw new TemplateException($"unknown placeholder '{{{{{token}}}}}'");
        return handler(parts.Skip(1).ToArray(), ctx);
    }

    private static string FormatTime(DateTime time, string[] args) =>
        args.Length == 0
            ? time.ToString("o", CultureInfo.InvariantCulture)
            : time.ToString(string.Join(':', args), CultureInfo.InvariantCulture); // rejoin so formats may contain ':'

    private static void RequireArgs(string name, string[] args, int count)
    {
        if (args.Length != count)
            throw new TemplateException($"{name} expects {count} argument(s), got {args.Length}");
    }

    private static int ParseInt(string name, string value) =>
        int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var result)
            ? result
            : throw new TemplateException($"{name}: '{value}' is not an integer");

    private static double ParseDouble(string name, string value) =>
        double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var result)
            ? result
            : throw new TemplateException($"{name}: '{value}' is not a number");
}

/// <summary>Raised when a template cannot be resolved; recorded as a failed request, never crashes the run.</summary>
public sealed class TemplateException : Exception
{
    public TemplateException(string message) : base(message) { }
}
