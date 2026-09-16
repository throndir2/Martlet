using System.Net;

namespace Martlet.Providers.Ollama;

internal static class OllamaChatTransport
{
    internal static SocketsHttpHandler CreateHandler() => new()
    {
        AllowAutoRedirect = false,
        UseProxy = false,
        Proxy = null,
        UseCookies = false,
        Credentials = null,
        DefaultProxyCredentials = null,
        AutomaticDecompression = DecompressionMethods.None,
        MaxResponseHeadersLength = 16,
        PooledConnectionLifetime = TimeSpan.FromMinutes(5)
    };

    internal static bool IsNdjson(HttpContent content)
    {
        var type = content.Headers.ContentType;
        return type is not null &&
            string.Equals(type.MediaType, "application/x-ndjson", StringComparison.OrdinalIgnoreCase) &&
            content.Headers.ContentEncoding.Count == 0 &&
            type.Parameters.Count <= 1 &&
            type.Parameters.All(p => string.Equals(p.Name, "charset", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(p.Value?.Trim('"'), "utf-8", StringComparison.OrdinalIgnoreCase));
    }
}
