using System.Text;
using LoadTester.Core.Configuration;
using LoadTester.Core.Execution;
using LoadTester.Core.Templating;

namespace LoadTester.Core.Http;

/// <summary>Turns an endpoint definition plus iteration context into an HttpRequestMessage.</summary>
public sealed class RequestBuilder
{
    private readonly GlobalSettings _settings;

    public RequestBuilder(GlobalSettings settings) => _settings = settings;

    public HttpRequestMessage Build(EndpointConfig endpoint, IterationContext ctx)
    {
        var url = BuildUrl(_settings.BaseAddress, PlaceholderResolver.Resolve(endpoint.Path, ctx));
        var request = new HttpRequestMessage(HttpMethod.Parse(endpoint.Method), url);

        // Merge first (endpoint overrides defaults), then apply each header exactly once.
        var merged = new Dictionary<string, string>(_settings.DefaultHeaders, StringComparer.OrdinalIgnoreCase);
        foreach (var (name, template) in endpoint.Headers)
            merged[name] = template;

        string? contentType = null;
        var contentHeaders = new List<(string Name, string Value)>();
        foreach (var (name, template) in merged)
        {
            var value = PlaceholderResolver.Resolve(template, ctx);
            if (name.Equals("Content-Type", StringComparison.OrdinalIgnoreCase))
                contentType = value;
            else if (name.Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
            { /* computed from the body */ }
            else if (ContentHeaderNames.IsContentHeader(name))
                contentHeaders.Add((name, value)); // applied to the content below, never to request.Headers
            else if (!request.Headers.TryAddWithoutValidation(name, value))
                throw new InvalidOperationException($"invalid request header '{name}'");
        }

        if (endpoint.BodyTemplate is { } bodyTemplate)
        {
            var body = PlaceholderResolver.Resolve(bodyTemplate, ctx);
            var content = new ByteArrayContent(Encoding.UTF8.GetBytes(body));
            content.Headers.TryAddWithoutValidation("Content-Type", endpoint.ContentType ?? contentType ?? "application/json");
            foreach (var (name, value) in contentHeaders)
                content.Headers.TryAddWithoutValidation(name, value);
            request.Content = content;
        }

        return request;
    }

    internal static string BuildUrl(string? baseAddress, string path)
    {
        if (Uri.TryCreate(path, UriKind.Absolute, out var absolute) &&
            (absolute.Scheme == "http" || absolute.Scheme == "https"))
            return path;

        if (string.IsNullOrWhiteSpace(baseAddress))
            throw new InvalidOperationException($"Relative path '{path}' requires settings.baseAddress.");

        return baseAddress.TrimEnd('/') + "/" + path.TrimStart('/');
    }
}
