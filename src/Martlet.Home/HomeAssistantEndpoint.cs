using System.Net;
using System.Net.Sockets;

namespace Martlet.Home;

/// <summary>The one Home Assistant address Martlet talks to. Plain http is accepted only on the local network (private,
/// loopback or link-local addresses and local host names); anything else must use https.</summary>
public static class HomeAssistantEndpoint
{
    public const int DefaultPort = 8123;
    public const string Example = "http://homeassistant.local:8123";

    private static readonly string[] LocalSuffixes = [".local", ".lan", ".home", ".home.arpa", ".internal", ".localdomain", ".ts.net"];

    /// <summary>Turns what the user typed ("homeassistant.local", "192.168.1.5:8123", "https://ha.example.com/") into a base
    /// address ending in '/'. Without a scheme and port, Home Assistant's default port 8123 is assumed.</summary>
    public static Uri Normalize(string? address)
    {
        var text = (address ?? "").Trim().Trim('"').TrimEnd('/');
        if (text.Length == 0)
            throw new HomeAssistantException(HomeAssistantFailure.InvalidAddress,
                $"Enter your Home Assistant address, for example {Example}.");
        var typedScheme = text.Contains("://", StringComparison.Ordinal);
        if (!typedScheme) text = "http://" + text;
        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") ||
            uri.UserInfo.Length > 0 || uri.Query.Length > 0 || uri.Fragment.Length > 0 || uri.Host.Length == 0)
            throw new HomeAssistantException(HomeAssistantFailure.InvalidAddress,
                $"That isn't a Home Assistant address. Use the address you open Home Assistant with, for example {Example}.");
        var builder = new UriBuilder(uri);
        if (!typedScheme && uri.IsDefaultPort) builder.Port = DefaultPort;
        var path = builder.Path.TrimEnd('/');
        if (path.EndsWith("/api", StringComparison.OrdinalIgnoreCase)) path = path[..^4];
        builder.Path = path + "/";
        var normalized = builder.Uri;
        if (normalized.Scheme == Uri.UriSchemeHttp && !IsLocalNetwork(normalized))
            throw new HomeAssistantException(HomeAssistantFailure.InvalidAddress,
                "Use https:// for Home Assistant outside your home network.");
        return normalized;
    }

    /// <summary>The address as shown to the user, without the trailing '/'.</summary>
    public static string Display(Uri baseUri) => baseUri.AbsoluteUri.TrimEnd('/');

    /// <summary>Whether <paramref name="uri"/> points at the same Home Assistant (scheme, host, port and base path).</summary>
    public static bool Contains(Uri baseUri, Uri uri) =>
        string.Equals(uri.Scheme, baseUri.Scheme, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(uri.IdnHost, baseUri.IdnHost, StringComparison.OrdinalIgnoreCase) && uri.Port == baseUri.Port &&
        uri.AbsolutePath.StartsWith(baseUri.AbsolutePath, StringComparison.Ordinal);

    public static bool IsLocalNetwork(Uri uri)
    {
        var host = uri.IdnHost.Trim('[', ']');
        if (IPAddress.TryParse(host, out var ip))
        {
            if (IPAddress.IsLoopback(ip)) return true;
            if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
            if (ip.AddressFamily == AddressFamily.InterNetwork)
            {
                var b = ip.GetAddressBytes();
                return b[0] == 10 || b[0] == 172 && b[1] is >= 16 and <= 31 || b[0] == 192 && b[1] == 168 ||
                    b[0] == 169 && b[1] == 254 || b[0] == 100 && b[1] is >= 64 and <= 127;
            }
            return ip.IsIPv6LinkLocal || ip.IsIPv6UniqueLocal;
        }
        return !host.Contains('.') ||
            LocalSuffixes.Any(suffix => host.EndsWith(suffix, StringComparison.OrdinalIgnoreCase));
    }
}
