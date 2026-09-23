namespace Martlet.Avatar.Hosting;

public static class RendererResourcePolicy
{
    public const string Origin = "https://martlet-avatar.invalid/";
    public const string Document = Origin + "index.html";
    private const string Policy = "default-src 'none'; script-src 'self'; " +
        "style-src 'self' 'unsafe-inline'; connect-src 'self'; img-src 'self' blob:; " +
        "worker-src 'none'; frame-src 'none'; object-src 'none'; base-uri 'none'";
    public static string ContentSecurityPolicy(bool live2d) => live2d
        ? Policy.Replace("script-src 'self';", "script-src 'self' 'wasm-unsafe-eval';", StringComparison.Ordinal) : Policy;
    public static string CanonicalName(string name) => string.Join('/', name.Split('/').Select(Uri.EscapeDataString));

    public static string? ResourceName(string url, string method)
    {
        if (method != "GET" || !url.StartsWith(Origin, StringComparison.Ordinal) ||
            !Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.UserInfo.Length != 0 ||
            uri.Query.Length != 0 || uri.Fragment.Length != 0) return null;
        var path = Uri.UnescapeDataString(url[Origin.Length..]);
        if (path.Length is 0 or > 512 || path.Contains('\\') || path.Contains('%') ||
            path.Any(char.IsControl) || path.Split('/').Any(s => s.Length == 0 || s is "." or ".."))
            return null;
        var canonical = CanonicalName(path);
        return canonical == url[Origin.Length..] ? canonical : null;
    }
}
