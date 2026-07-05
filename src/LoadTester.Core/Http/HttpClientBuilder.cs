using System.Net;
using LoadTester.Core.Configuration;

namespace LoadTester.Core.Http;

public static class HttpClientBuilder
{
    /// <summary>
    /// One shared client for the whole run. Per-request timeouts are enforced with linked
    /// cancellation tokens, so the client's own timeout is disabled.
    /// </summary>
    public static HttpClient Create(GlobalSettings settings)
    {
        var handler = new SocketsHttpHandler
        {
            MaxConnectionsPerServer = settings.MaxConnectionsPerServer,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            AutomaticDecompression = DecompressionMethods.All,
            // No shared cookie jar: with one process-wide container every virtual user would share
            // a session, and load-balancer affinity cookies would pin all load to one backend.
            UseCookies = false,
            // Off by default so 3xx statuses are classified against expectedStatusCodes instead of
            // being followed silently (which would also fold redirect chains into one latency).
            AllowAutoRedirect = settings.FollowRedirects,
        };
        if (settings.SkipTlsVerification)
            handler.SslOptions.RemoteCertificateValidationCallback = (_, _, _, _) => true;

        return new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
    }
}
