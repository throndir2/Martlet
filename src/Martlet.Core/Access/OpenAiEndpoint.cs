using System.Text.Json;
using System.Text.Json.Serialization;

namespace Martlet.Core.Access;

/// <summary>The OpenAI-compatible endpoint's shared choices (openai-endpoint.json in the data folder, the
/// <c>openai-endpoint</c> shared setting, so they are the same on all your computers; docs/OPENAI_ENDPOINT.md). Nonsecret:
/// the public address you give people and the host your proxy sends requests to. The keys themselves are API keys.</summary>
public sealed record OpenAiEndpointSettings
{
    public const string FileName = "openai-endpoint.json";
    public const string SharedKey = "openai-endpoint";
    public const int MaximumAddressLength = 200;
    private static readonly JsonSerializerOptions Canonical = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        RespectNullableAnnotations = true,
        MaxDepth = 4
    };
    private static readonly JsonSerializerOptions Indented = new(Canonical) { WriteIndented = true };

    public int SchemaVersion { get; init; } = 1;
    /// <summary>The base URL people put in their app, for example <c>https://llm.example.com/v1</c>: your reverse proxy with a
    /// real certificate in front of one of your hosts. Null until you set one.</summary>
    public string? PublicAddress { get; init; }
    /// <summary>The host your proxy sends requests to (the front door). Any host works: it runs the models it has itself and
    /// sends the others to the host that runs them. Null: the first host of your network.</summary>
    public string? FrontDoorHostId { get; init; }

    [JsonIgnore] public bool IsDefault => PublicAddress is null && FrontDoorHostId is null;

    private bool Valid() => SchemaVersion == 1 &&
        (PublicAddress is null || NormalizeAddress(PublicAddress, out _) == PublicAddress) &&
        (FrontDoorHostId is null || FrontDoorHostId is { Length: > 0 and <= 128 } &&
            FrontDoorHostId.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-'));

    /// <summary>Throws <see cref="Contracts.ContractException"/> when the choices aren't usable as they are.</summary>
    public void Validate() => Contracts.ContractRules.Require(Valid(), "The OpenAI-compatible endpoint settings are invalid.");

    /// <summary>The base URL for what the owner typed: <c>llm.example.com</c>, <c>https://llm.example.com</c> and
    /// <c>https://llm.example.com/v1</c> all give <c>https://llm.example.com/v1</c>. Null with a reason when it can't be one:
    /// not https, a path other than /v1, a user name, a query or too long.</summary>
    public static string? NormalizeAddress(string? text, out string? problem)
    {
        problem = null;
        var value = (text ?? "").Trim();
        if (value.Length == 0) { problem = "Type the address, for example https://llm.example.com/v1."; return null; }
        if (!value.Contains("://", StringComparison.Ordinal)) value = "https://" + value;
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Host.Length == 0)
        {
            problem = "That isn't a web address: type it like https://llm.example.com/v1.";
            return null;
        }
        if (uri.Scheme != Uri.UriSchemeHttps)
        {
            problem = "Use an https address: apps send the key with every request, so it must travel encrypted.";
            return null;
        }
        if (uri.UserInfo.Length > 0 || uri.Query.Length > 0 || uri.Fragment.Length > 0)
        {
            problem = "Leave out user names, ? and # parts.";
            return null;
        }
        var path = uri.AbsolutePath.TrimEnd('/');
        if (path is not ("" or OpenAiEndpoint.BasePath))
        {
            problem = "Use the address of the site itself, or with /v1 at the end.";
            return null;
        }
        var normalized = $"https://{uri.IdnHost.ToLowerInvariant()}{(uri.IsDefaultPort ? "" : ":" + uri.Port)}{OpenAiEndpoint.BasePath}";
        if (normalized.Length > MaximumAddressLength) { problem = "That address is too long."; return null; }
        return normalized;
    }

    /// <summary>The canonical JSON every computer writes for the same choices, for sharing.</summary>
    public string Share() => JsonSerializer.Serialize(this, Canonical);

    /// <summary>Choices another computer shared (<see cref="Share"/>); null when they aren't ones this Martlet reads.</summary>
    public static OpenAiEndpointSettings? Parse(string json)
    {
        try
        {
            var parsed = JsonSerializer.Deserialize<OpenAiEndpointSettings>(json, Canonical);
            return parsed is not null && parsed.Valid() ? parsed : null;
        }
        catch (Exception error) when (error is JsonException or NotSupportedException or ArgumentException) { return null; }
    }

    public static OpenAiEndpointSettings Load(string? directory) => Read(directory).Settings;

    /// <summary>The saved choices and the file's state: none, loaded or unreadable (which reads as the default).</summary>
    public static (OpenAiEndpointSettings Settings, string State) Read(string? directory)
    {
        if (directory is null) return (new(), "none");
        try
        {
            var path = Path.Combine(directory, FileName);
            if (!File.Exists(path)) return (new(), "none");
            if (new FileInfo(path).Length > 16_384) return (new(), "unreadable");
            return Parse(File.ReadAllText(path)) is { } loaded ? (loaded, "loaded") : (new(), "unreadable");
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return (new(), "unreadable"); }
    }

    /// <summary>Saves openai-endpoint.json atomically; false when the choices are invalid or the data folder can't be written.</summary>
    public bool Save(string? directory)
    {
        if (directory is null || !Valid()) return false;
        try
        {
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, FileName);
            var temporary = Path.Combine(directory, $"openai-endpoint.{Guid.NewGuid():N}.tmp");
            try
            {
                File.WriteAllText(temporary, JsonSerializer.Serialize(this, Indented));
                File.Move(temporary, path, overwrite: true);
            }
            finally
            {
                if (File.Exists(temporary)) File.Delete(temporary);
            }
            Settings.SettingsFiles.NotifySaved(path);
            return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return false; }
    }
}

/// <summary>The OpenAI-compatible endpoint every Martlet host serves (docs/OPENAI_ENDPOINT.md): its paths, the header a
/// front-door host adds when it sends a request on to another host, and the text Martlet gives the person who gets a key.</summary>
public static class OpenAiEndpoint
{
    public const string BasePath = "/v1";
    public const string ModelsPath = "/v1/models";
    public const string ChatPath = "/v1/chat/completions";
    public const string CompletionsPath = "/v1/completions";
    public const string EmbeddingsPath = "/v1/embeddings";
    /// <summary>The POST paths a host answers with its own Ollama (each takes a <c>model</c>).</summary>
    public static readonly IReadOnlyList<string> RelayPaths = [ChatPath, CompletionsPath, EmbeddingsPath];
    /// <summary>Set by a front-door host on a request it sends on to another host of the network: that host then answers with
    /// its own models only, so a request is never sent on twice.</summary>
    public const string ForwardedHeader = "X-Martlet-Forwarded";
    /// <summary>The longest one OpenAI-compatible request may run (the gateway's longest job).</summary>
    public static readonly TimeSpan MaximumDuration = TimeSpan.FromMinutes(15);
    /// <summary>The largest request body (a chat with pictures in base64).</summary>
    public const int MaximumRequestBytes = 32 * 1024 * 1024;

    /// <summary>Whether <paramref name="target"/> (a request's path) is one of the endpoint's.</summary>
    public static bool IsPath(string target) => target == BasePath || target.StartsWith(BasePath + "/", StringComparison.Ordinal);

    /// <summary>The scopes of a key for the OpenAI-compatible endpoint: <see cref="ApiKeyScopes.OpenAi"/>, and
    /// <see cref="ApiKeyScopes.EqualPriority"/> when its requests rank with your own background work.</summary>
    public static IReadOnlyList<string> Scopes(bool equalPriority) =>
        equalPriority ? [ApiKeyScopes.EqualPriority, ApiKeyScopes.OpenAi] : [ApiKeyScopes.OpenAi];

    /// <summary>The scopes <paramref name="key"/> gets when its OpenAI-compatible priority changes; its other scopes stay.</summary>
    public static IReadOnlyList<string> WithPriority(ApiKey key, bool equalPriority)
    {
        ArgumentNullException.ThrowIfNull(key);
        var others = key.Scopes.Where(s => s != ApiKeyScopes.EqualPriority);
        return [.. (equalPriority ? others.Append(ApiKeyScopes.EqualPriority) : others).Order(StringComparer.Ordinal)];
    }

    /// <summary>"Your Martlet first" or "Same as your background work", for a key's row.</summary>
    public static string Priority(ApiKey key) => key.Allows(ApiKeyScopes.EqualPriority)
        ? "Same priority as your own background work; your live conversations still come first"
        : "Your Martlet first: any request of yours stops theirs";

    /// <summary>The text to send to the person who got a key: the address, the key, the models and a first request.</summary>
    public static string Invite(string name, string baseUrl, string token, IReadOnlyList<string> models)
    {
        var model = models.FirstOrDefault() ?? "<model>";
        return $"""
            Hi {name}! You can use my Martlet language models with any app that works with the OpenAI API.

            Base URL: {baseUrl}
            API key:  {token}
            Models:   {(models.Count == 0 ? "ask me which ones are on" : string.Join(", ", models))}

            Try it:
            {CurlExample(baseUrl, model, token)}

            In Python (pip install openai):
            {PythonExample(baseUrl, model, token)}

            Keep the key private. My own work comes first, so a request can wait or ask you to try again in a moment.
            """;
    }

    public static string CurlExample(string baseUrl, string model, string token = "$MARTLET_KEY") =>
        $"curl {baseUrl}/chat/completions -H \"Authorization: Bearer {token}\" -H \"Content-Type: application/json\" " +
        $"-d '{{\"model\":\"{model}\",\"messages\":[{{\"role\":\"user\",\"content\":\"Hello!\"}}]}}'";

    public static string PythonExample(string baseUrl, string model, string token = "<your key>") => $$"""
        from openai import OpenAI
        client = OpenAI(base_url="{{baseUrl}}", api_key="{{token}}")
        reply = client.chat.completions.create(model="{{model}}", messages=[{"role": "user", "content": "Hello!"}])
        print(reply.choices[0].message.content)
        """;

    /// <summary>The front door's port unless the owner chooses another (docs/OPENAI_ENDPOINT.md). UDP 9444 finds moved hosts, so
    /// the front door stays off that number to avoid confusion.</summary>
    public const int DefaultFrontDoorPort = 9445;

    /// <summary>The error code an OpenAI client gets from a front door without a key: Check address looks for it to tell that
    /// an address reaches a Martlet front door.</summary>
    public const string FrontDoorMissingKey = "missing_api_key";

    /// <summary>The help behind the "?" next to the address (Share › Share your language models): the ways to give the front door
    /// an https address with a real certificate. The wiki (Sharing-with-Friends#get-an-https-address) has the steps.</summary>
    public const string ExposeHelp =
        "Apps need an https address with a real certificate in front of the front door. Use Tailscale Funnel (no domain), " +
        "Cloudflare Tunnel (a domain on Cloudflare) or Nginx Proxy Manager, Nginx or Caddy (your own domain); Show setup gives the " +
        "exact text. Then type the address here and choose Check address.";

    /// <summary>An nginx server block that serves <paramref name="serverName"/> with a real certificate and sends it to the
    /// front door (<paramref name="upstreamOrigin"/>, https://&lt;home address&gt;:&lt;front door port&gt;). The front door serves
    /// only its web page and /v1 and answers 404 for the rest, so the whole site can go to it. Its certificate is the host's own,
    /// so nginx doesn't check it: keep nginx and the host on your home network.</summary>
    public static string NginxConfig(string serverName, string upstreamOrigin) => $$"""
        # Martlet front door: https://{{serverName}} -> {{upstreamOrigin}}
        limit_req_zone $binary_remote_addr zone=martlet_front_door:10m rate=10r/s;

        server {
            listen 443 ssl;
            http2 on;
            server_name {{serverName}};

            ssl_certificate     /etc/letsencrypt/live/{{serverName}}/fullchain.pem;
            ssl_certificate_key /etc/letsencrypt/live/{{serverName}}/privkey.pem;

            client_max_body_size 32m;

            location / {
                limit_req zone=martlet_front_door burst=40 nodelay;
                proxy_pass {{upstreamOrigin}};
                proxy_ssl_verify off;
                proxy_ssl_server_name off;
                proxy_http_version 1.1;
                proxy_set_header Host $host;
                proxy_set_header Connection "";
                proxy_buffering off;
                proxy_request_buffering off;
                proxy_read_timeout 900s;
                proxy_send_timeout 900s;
            }
        }
        """;

    /// <summary>The same as <see cref="NginxConfig"/> for Caddy, which gets the certificate by itself.</summary>
    public static string CaddyConfig(string serverName, string upstreamOrigin) => $$"""
        {{serverName}} {
            reverse_proxy {{upstreamOrigin}} {
                transport http {
                    tls_insecure_skip_verify
                }
                flush_interval -1
            }
        }
        """;

    /// <summary>Nginx Proxy Manager's fields for a proxy host that sends <paramref name="serverName"/> to the front door.</summary>
    public static string ProxyManagerSteps(string serverName, string upstreamHost, int port) => $$"""
        Hosts › Proxy Hosts › Add Proxy Host
          Domain Names:           {{serverName}}
          Scheme:                 https
          Forward Hostname / IP:  {{upstreamHost}}
          Forward Port:           {{port}}
          Block Common Exploits:  on
          Websockets Support:     off
        SSL tab: your certificate for the name (or Request a new SSL Certificate), Force SSL, HTTP/2 Support.
        Advanced tab, Custom Nginx Configuration:
          client_max_body_size 32m;
          proxy_http_version 1.1;
          proxy_buffering off;
          proxy_request_buffering off;
          proxy_read_timeout 900s;
          proxy_send_timeout 900s;
        """;

    /// <summary>The Tailscale Funnel command for the front door's computer: Tailscale gives the front door a public https
    /// address with a real certificate and sends it to the front door on this computer's loopback.</summary>
    public static string TailscaleCommand(int port) =>
        $"tailscale funnel --bg https+insecure://127.0.0.1:{port}";

    /// <summary>A Cloudflare Tunnel ingress rule (cloudflared's config.yml) that sends <paramref name="serverName"/> to the front door.</summary>
    public static string CloudflareTunnelConfig(string serverName, string upstreamOrigin) => $$"""
        ingress:
          - hostname: {{serverName}}
            service: {{upstreamOrigin}}
            originRequest:
              noTLSVerify: true
          - service: http_status:404
        """;
}