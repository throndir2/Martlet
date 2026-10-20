using System.Net;
using System.Net.Sockets;
using Martlet.Core.Settings;
using Martlet.Core.Singing;

namespace Martlet.Core.Cluster;

/// <summary>One thing Martlet uses an endpoint for: <see cref="Purpose"/> in the app's words ("Thinking", "Thinking pool",
/// "Picture making", "Tools"), the <see cref="Page"/> where it is changed, the model (or the MCP server's name) and whether it
/// is on.</summary>
public sealed record EndpointUse(string Purpose, string Page, string? Model, bool On)
{
    /// <summary>"Thinking (gpt-4o-mini)", "Thinking pool (x-ai/grok-4.3, turned off)" or "Tools (home)".</summary>
    public string Describe()
    {
        var details = new[] { Model, On ? null : "turned off" }.OfType<string>().ToArray();
        return details.Length == 0 ? Purpose : $"{Purpose} ({string.Join(", ", details)})";
    }
}

/// <summary>A cloud provider or a server on your network (docs/GLOSSARY.md: an endpoint) that Martlet sends work to: never this
/// PC itself and never a paired Martlet computer. <see cref="Address"/> is its scheme, host and port only (never a path, which
/// can hold a secret), or null for a cloud provider whose address Martlet doesn't know. <see cref="Id"/> is a stable automation
/// ID part.</summary>
public sealed record ExternalEndpoint(string Id, string Name, string? Address, bool Cloud, IReadOnlyList<EndpointUse> Uses)
{
    public string Kind => Cloud ? "Cloud provider" : "Server on your network";

    /// <summary>"Cloud provider at https://openrouter.ai. Used for Thinking (google/gemma-4-26b-a4b-it); Picture making (...)."</summary>
    public string Describe() =>
        $"{Kind}{(Address is null ? "" : " at " + Address)}. Used for {string.Join("; ", Uses.Select(u => u.Describe()))}.";
}

/// <summary>An MCP server from mcp.json that Martlet reaches over HTTP; <paramref name="Url"/> null for one it starts on this PC.</summary>
public sealed record ExternalTool(string Name, string? Url, bool Off);

/// <summary>What <see cref="ExternalEndpoints.Find"/> reads: the Thinking, Listening and Speaking routes, Thinking's fallback,
/// the Thinking pool, every area's pool list and the MCP servers.</summary>
public sealed record ExternalEndpointSources
{
    public IReadOnlyList<SetupRoute> Routes { get; init; } = [];
    public ThinkingFallbackSettings? Fallback { get; init; }
    public ThinkingPoolSettings? ThinkingPool { get; init; }
    public IReadOnlyList<PoolList> Pools { get; init; } = [];
    public IReadOnlyList<ExternalTool> Tools { get; init; } = [];
}

/// <summary>Devices › Cloud providers and servers: every endpoint outside this PC and your paired computers that a saved choice
/// sends work to, each once with everything it is used for. Reads nothing itself; never a key.</summary>
public static class ExternalEndpoints
{
    public const string ThinkingPoolPurpose = "Thinking pool", FallbackPurpose = "If Thinking fails", ToolsPurpose = "Tools";

    private static readonly IReadOnlyList<(string Origin, string Name)> KnownNames =
    [
        (OpenAiSetup.Origin, "OpenAI"),
        (ElevenLabsSetup.Origin, "ElevenLabs"),
        (SingingCloud.MurekaOrigin, SingingCloud.Name(SingingCloud.Mureka)),
        (SingingCloud.FalOrigin, SingingCloud.Name(SingingCloud.Fal)),
        .. ChatCompletionsEndpointCatalog.NamedEndpoints.Select(e => (e.BaseUrl, e.Name))
    ];

    public static IReadOnlyList<ExternalEndpoint> Find(ExternalEndpointSources sources)
    {
        ArgumentNullException.ThrowIfNull(sources);
        var found = new Dictionary<string, (string Name, string? Address, bool Cloud, List<EndpointUse> Uses)>(StringComparer.Ordinal);
        void Add(string? url, string? provider, EndpointUse use)
        {
            string id;
            (string Name, string? Address, bool Cloud) place;
            if (Place(url) is { } at) (id, place) = (Identify(at.Address), (at.Name, at.Address, at.Cloud));
            else if (url is null && provider is { Length: > 0 }) (id, place) = (Identify(provider), (provider, null, true));
            else return;
            if (!found.TryGetValue(id, out var entry)) found[id] = entry = (place.Name, place.Address, place.Cloud, []);
            if (!entry.Uses.Contains(use)) entry.Uses.Add(use);
        }

        foreach (var role in new[] { SetupRole.Llm, SetupRole.Stt, SetupRole.Tts })
            foreach (var route in sources.Routes.Where(r => r.Role == role))
                Add(RouteOrigin(route), null, new(RoleName(role), RolePage(role), route.ModelId is { Length: > 0 } model ? model : null,
                    route.Enabled != false));
        if (sources.Fallback is { } fallback)
            Add(fallback.Origin, null, new(FallbackPurpose, RolePage(SetupRole.Llm), fallback.ModelId, true));
        if (sources.ThinkingPool is { } pool)
            foreach (var member in pool.All.Where(ThinkingPoolSettings.IsExternal))
                Add(member.Origin, null, new(ThinkingPoolPurpose, "Companion › Thinking pool", member.ModelId, !pool.IsOff(member.Key)));
        foreach (var area in PoolAreas.All)
            foreach (var list in sources.Pools.Where(p => p.Area == area.Id))
                foreach (var member in list.Members)
                {
                    var url = member.Kind switch
                    {
                        PoolMemberKind.Address => member.Address,
                        PoolMemberKind.Cloud => member.Origin ?? ProviderOrigin(member.Provider),
                        _ => null
                    };
                    if (url is null && member.Kind != PoolMemberKind.Cloud) continue;
                    Add(url, member.Provider, new(area.Title, area.Page, member.Model ?? member.Setting(PoolSettingKeys.Model), !member.Off));
                }
        foreach (var tool in sources.Tools)
            if (tool.Url is not null) Add(tool.Url, null, new(ToolsPurpose, "Companion › Tools", tool.Name, !tool.Off));

        return [.. found.OrderByDescending(e => e.Value.Cloud).ThenBy(e => e.Value.Name, StringComparer.OrdinalIgnoreCase)
            .Select(e => new ExternalEndpoint(e.Key, e.Value.Name, e.Value.Address, e.Value.Cloud, e.Value.Uses))];
    }

    /// <summary>"2 cloud providers and 1 server on your network." or that there is none.</summary>
    public static string Summary(IReadOnlyList<ExternalEndpoint> endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        if (endpoints.Count == 0) return "None: Martlet sends work only to this PC and your own computers.";
        var cloud = endpoints.Count(e => e.Cloud);
        var servers = endpoints.Count - cloud;
        var parts = new List<string>();
        if (cloud > 0) parts.Add($"{cloud} cloud provider{(cloud == 1 ? "" : "s")}");
        if (servers > 0) parts.Add($"{servers} server{(servers == 1 ? "" : "s")} on your network");
        return string.Join(" and ", parts) + ". Each line says what Martlet uses it for.";
    }

    /// <summary>The address a Thinking, Listening or Speaking route sends to: OpenAI, ElevenLabs or a Chat Completions endpoint.
    /// Null for a route on a paired computer (a gateway) or on this PC.</summary>
    private static string? RouteOrigin(SetupRoute route) => route.RouteType switch
    {
        null or SetupRouteType.OpenAi => OpenAiSetup.Origin,
        SetupRouteType.ChatCompletions or SetupRouteType.ElevenLabs => route.Origin,
        _ => null
    };

    /// <summary>A pool cloud member's address when it saves none: its provider's known API address, or null.</summary>
    internal static string? ProviderOrigin(string? provider) => provider switch
    {
        "openai" => OpenAiSetup.Origin,
        SingingCloud.ElevenLabs => ElevenLabsSetup.Origin,
        SingingCloud.Mureka => SingingCloud.MurekaOrigin,
        SingingCloud.Fal => SingingCloud.FalOrigin,
        _ => ChatCompletionsEndpointCatalog.ById(provider)?.BaseUrl
    };

    private static string RoleName(SetupRole role) => role switch
    {
        SetupRole.Llm => "Thinking",
        SetupRole.Stt => "Listening",
        _ => "Speaking"
    };

    private static string RolePage(SetupRole role) => role switch
    {
        SetupRole.Llm => "Companion › Thinking",
        SetupRole.Stt => "Companion › Listening",
        _ => "Companion › Voice"
    };

    /// <summary>An http or https address that isn't this PC: its scheme, host and port, its name and whether it is in the cloud.</summary>
    internal static (string Name, string Address, bool Cloud)? Place(string? url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") || uri.IsLoopback ||
            string.IsNullOrEmpty(uri.Host))
            return null;
        var named = KnownNames.Where(k => Uri.TryCreate(k.Origin, UriKind.Absolute, out var known) &&
            string.Equals(known.Host, uri.Host, StringComparison.OrdinalIgnoreCase)).Select(k => k.Name).FirstOrDefault();
        return (named ?? uri.IdnHost, $"{uri.Scheme}://{uri.Authority}", !OnYourNetwork(uri));
    }

    private static string Identify(string text)
    {
        var start = text.IndexOf("://", StringComparison.Ordinal);
        var id = (start >= 0 ? text[(start + 3)..] : text).ToLowerInvariant();
        return new string(id.Select(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' ? c : '-').ToArray()).Trim('-');
    }

    /// <summary>A private or link-local address, a name without a dot, or a home or overlay network name (.local, .lan,
    /// .home.arpa, .internal, Tailscale's .ts.net).</summary>
    private static bool OnYourNetwork(Uri uri)
    {
        if (IPAddress.TryParse(uri.Host.Trim('[', ']'), out var address)) return IsPrivate(address);
        var host = uri.IdnHost.TrimEnd('.').ToLowerInvariant();
        return !host.Contains('.') || new[] { ".local", ".lan", ".home.arpa", ".internal", ".ts.net" }.Any(host.EndsWith);
    }

    private static bool IsPrivate(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (address.AddressFamily == AddressFamily.InterNetworkV6)
            return address.IsIPv6LinkLocal || address.IsIPv6SiteLocal || address.IsIPv6UniqueLocal;
        var b = address.GetAddressBytes();
        return b[0] == 10 || b[0] == 172 && b[1] is >= 16 and <= 31 || b[0] == 192 && b[1] == 168 || b[0] == 169 && b[1] == 254 ||
            b[0] == 100 && b[1] is >= 64 and <= 127;
    }
}
