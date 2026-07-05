namespace LoadTester.Core.Http;

/// <summary>HTTP headers that belong on the content, not the request (per RFC 7231 §3.1 / HttpContentHeaders).</summary>
internal static class ContentHeaderNames
{
    private static readonly HashSet<string> Names = new(StringComparer.OrdinalIgnoreCase)
    {
        "Allow", "Content-Disposition", "Content-Encoding", "Content-Language", "Content-Length",
        "Content-Location", "Content-MD5", "Content-Range", "Content-Type", "Expires", "Last-Modified",
    };

    public static bool IsContentHeader(string name) => Names.Contains(name);
}
