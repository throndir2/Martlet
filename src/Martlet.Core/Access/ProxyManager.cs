using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Martlet.Core.Access;

/// <summary>What <see cref="ProxyManager"/> found for a name and would do: <see cref="Site"/> is the proxy host that already has
/// the name (null: none), <see cref="Certificate"/> the certificate the site uses or gets (null: Let's Encrypt is asked for
/// one), and <see cref="Text"/> says it in plain words for the owner to confirm.</summary>
public sealed record ProxyManagerPlan(string Name, int? Site, string? Now, int? Certificate, string? CertificateName, bool Changes, string Text);

/// <summary>Points a site in Nginx Proxy Manager at Martlet's front door (docs/OPENAI_ENDPOINT.md) through the API its own web
/// page uses: signs in with the NPM account, finds the proxy host that already has the name (and changes only where it sends
/// people and the settings streaming needs) or adds one, and uses a certificate that already covers the name, else asks
/// Let's Encrypt for one. The email and password are used once to sign in and never kept.</summary>
public sealed class ProxyManager(HttpClient http, Uri manager)
{
    /// <summary>Nginx settings for streamed replies and long requests (the Advanced tab's Custom Nginx Configuration).</summary>
    public static readonly string AdvancedConfig = string.Join('\n', "client_max_body_size 32m;", "proxy_http_version 1.1;",
        "proxy_buffering off;", "proxy_request_buffering off;", "proxy_read_timeout 900s;", "proxy_send_timeout 900s;");

    private string? token;

    /// <summary>Nginx Proxy Manager's address from what the owner typed (http://192.168.1.20:81, or a name and port), or null.</summary>
    public static Uri? Address(string? text)
    {
        var value = (text ?? "").Trim().TrimEnd('/');
        if (value.Length == 0) return null;
        if (!value.Contains("://", StringComparison.Ordinal)) value = "http://" + value;
        return Uri.TryCreate(value + "/", UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" && uri.Host.Length > 0 &&
            uri.AbsolutePath == "/" ? uri : null;
    }

    /// <summary>Whether Nginx Proxy Manager answers at <paramref name="address"/> (its API says status OK).</summary>
    public static async Task<bool> AnswersAsync(HttpClient http, Uri address, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await http.GetAsync(new Uri(address, "api/"), cancellationToken);
            if (!response.IsSuccessStatusCode) return false;
            var body = await response.Content.ReadFromJsonAsync<JsonObject>(cancellationToken);
            return Text(body?["status"]) == "OK";
        }
        catch (Exception error) when (error is HttpRequestException or JsonException or InvalidOperationException or NotSupportedException ||
            error is TaskCanceledException && !cancellationToken.IsCancellationRequested) { return false; }
    }

    public async Task SignInAsync(string identity, string secret, CancellationToken cancellationToken)
    {
        using var response = await http.PostAsJsonAsync(new Uri(manager, "api/tokens"), new { identity, secret }, cancellationToken);
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden or HttpStatusCode.BadRequest)
            throw new InvalidOperationException("Nginx Proxy Manager didn't accept that email and password.");
        await Ensure(response, "sign in", cancellationToken);
        token = Text((await response.Content.ReadFromJsonAsync<JsonObject>(cancellationToken))?["token"]) ??
            throw new InvalidOperationException("Nginx Proxy Manager signed in but gave no token.");
    }

    /// <summary>What Martlet would do for <paramref name="name"/> so it goes to https://<paramref name="upstreamHost"/>:<paramref name="port"/>.</summary>
    public async Task<ProxyManagerPlan> PlanAsync(string name, string upstreamHost, int port, string frontDoor, CancellationToken cancellationToken)
    {
        name = name.Trim().TrimEnd('.').ToLowerInvariant();
        var target = $"https://{upstreamHost}:{port}";
        var sites = await ListAsync("api/nginx/proxy-hosts", cancellationToken);
        var site = sites.FirstOrDefault(s => Names(s).Contains(name, StringComparer.OrdinalIgnoreCase));
        var certificates = await ListAsync("api/nginx/certificates", cancellationToken);
        if (site is not null)
        {
            var id = Number(site["id"]) ?? 0;
            var now = $"{Text(site["forward_scheme"])}://{Text(site["forward_host"])}:{Number(site["forward_port"])}";
            var used = Number(site["certificate_id"]) is int c and > 0 ? c : (int?)null;
            var certificate = used ?? Covering(certificates, name);
            var certificateName = Describe(certificates, certificate);
            var right = now == target && used is not null && Flag(site["ssl_forced"]) != false &&
                (Text(site["advanced_config"]) ?? "").Contains("proxy_buffering off;", StringComparison.Ordinal) &&
                Flag(site["enabled"]) != false;
            return new(name, id, now, certificate, certificateName, !right, right
                ? $"{name} already goes to {target}, the front door on {frontDoor}, with its certificate ({certificateName}); nothing to change."
                : $"{name} goes to {now} now (proxy host {id} in Nginx Proxy Manager): Martlet changes it to {target}, the front door on " +
                  $"{frontDoor}, turns on streaming and " +
                  (used is not null ? $"keeps its certificate ({certificateName})."
                      : certificate is not null ? $"gives it your certificate {certificateName}."
                      : $"asks Let's Encrypt for a certificate, so {name} must reach this proxy on port 80."));
        }
        var cover = Covering(certificates, name);
        var coverName = Describe(certificates, cover);
        return new(name, null, null, cover, coverName, true,
            $"Nginx Proxy Manager has no site for {name}: Martlet adds one that goes to {target}, the front door on {frontDoor}, " +
            (cover is not null ? $"with your certificate {coverName}."
                : $"and asks Let's Encrypt for a certificate, so {name} must reach this proxy on port 80."));
    }

    /// <summary>Carries out <paramref name="plan"/>; <paramref name="email"/> is the Let's Encrypt account when a certificate is
    /// asked for. Returns what changed.</summary>
    public async Task<string> ApplyAsync(ProxyManagerPlan plan, string upstreamHost, int port, string email, CancellationToken cancellationToken)
    {
        if (!plan.Changes) return plan.Text;
        var body = new JsonObject
        {
            ["forward_scheme"] = "https", ["forward_host"] = upstreamHost, ["forward_port"] = port,
            ["block_exploits"] = true, ["allow_websocket_upgrade"] = false, ["caching_enabled"] = false,
            ["ssl_forced"] = true, ["http2_support"] = true, ["advanced_config"] = AdvancedConfig,
            ["certificate_id"] = plan.Certificate is int certificate ? certificate : "new"
        };
        // A new site always takes meta; a site that is there keeps its own unless Let's Encrypt is asked for a certificate.
        if (plan.Site is null || plan.Certificate is null)
            body["meta"] = new JsonObject { ["letsencrypt_agree"] = plan.Certificate is null, ["letsencrypt_email"] = email, ["dns_challenge"] = false };
        HttpResponseMessage response;
        if (plan.Site is int site)
            response = await SendAsync(HttpMethod.Put, $"api/nginx/proxy-hosts/{site}", body, cancellationToken);
        else
        {
            body["domain_names"] = new JsonArray(plan.Name);
            body["access_list_id"] = 0;
            body["hsts_enabled"] = false;
            body["hsts_subdomains"] = false;
            body["locations"] = new JsonArray();
            response = await SendAsync(HttpMethod.Post, "api/nginx/proxy-hosts", body, cancellationToken);
        }
        using (response) await Ensure(response, plan.Site is null ? "add the site" : "change the site", cancellationToken);
        var target = $"https://{upstreamHost}:{port}";
        return plan.Site is int changed
            ? $"Changed {plan.Name} (proxy host {changed}) in Nginx Proxy Manager: it goes to {target} now instead of {plan.Now}."
            : $"Added {plan.Name} to Nginx Proxy Manager: it goes to {target}" +
              (plan.Certificate is null ? ", with a new Let's Encrypt certificate." : $", with your certificate {plan.CertificateName}.");
    }

    private async Task<JsonArray> ListAsync(string path, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(manager, path));
        Authorize(request);
        using var response = await http.SendAsync(request, cancellationToken);
        await Ensure(response, "read its sites", cancellationToken);
        return await response.Content.ReadFromJsonAsync<JsonArray>(cancellationToken) ?? [];
    }

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, JsonObject body, CancellationToken cancellationToken)
    {
        var request = new HttpRequestMessage(method, new Uri(manager, path)) { Content = JsonContent.Create(body) };
        Authorize(request);
        try { return await http.SendAsync(request, cancellationToken); }
        finally { request.Dispose(); }
    }

    private void Authorize(HttpRequestMessage request) =>
        request.Headers.Authorization = new("Bearer", token ?? throw new InvalidOperationException("Sign in to Nginx Proxy Manager first."));

    private static async Task Ensure(HttpResponseMessage response, string what, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode) return;
        string? message = null;
        try { message = Text((await response.Content.ReadFromJsonAsync<JsonObject>(cancellationToken))?["error"]?["message"]); }
        catch (Exception error) when (error is JsonException or InvalidOperationException or NotSupportedException) { }
        throw new InvalidOperationException($"Nginx Proxy Manager couldn't {what} ({(int)response.StatusCode}" +
            (message is null ? ")." : $": {message})."));
    }

    private static IEnumerable<string> Names(JsonNode? item) =>
        item?["domain_names"] is JsonArray names ? names.Select(n => Text(n) ?? "") : [];

    private static int? Number(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<int>(out var number) ? number : null;

    private static string? Text(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    /// <summary>A yes/no field: NPM sends true/false, older versions 1/0; null when it is missing.</summary>
    private static bool? Flag(JsonNode? node) =>
        node is not JsonValue value ? null : value.TryGetValue<bool>(out var flag) ? flag : value.TryGetValue<int>(out var number) ? number != 0 : null;

    /// <summary>A certificate that covers <paramref name="name"/> (the name itself or *.its parent) and hasn't expired.</summary>
    private static int? Covering(JsonArray certificates, string name)
    {
        var dot = name.IndexOf('.');
        var wildcard = dot > 0 ? "*" + name[dot..] : null;
        return certificates.Where(c => Names(c).Any(n => string.Equals(n, name, StringComparison.OrdinalIgnoreCase) ||
                wildcard is not null && string.Equals(n, wildcard, StringComparison.OrdinalIgnoreCase)) &&
                !(DateTimeOffset.TryParse(Text(c?["expires_on"]), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var ends) &&
                  ends < DateTimeOffset.UtcNow))
            .Select(c => Number(c?["id"])).FirstOrDefault(id => id is not null);
    }

    private static string? Describe(JsonArray certificates, int? id) =>
        id is null ? null : certificates.FirstOrDefault(c => Number(c?["id"]) == id) is { } certificate
            ? string.Join(", ", Names(certificate)) : $"certificate {id}";
}
