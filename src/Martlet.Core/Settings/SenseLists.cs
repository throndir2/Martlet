using System.Buffers.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Martlet.Core.Cluster;
using Martlet.Core.Contracts;

namespace Martlet.Core.Settings;

/// <summary>How this PC reaches a paired computer (hosts.json, nonsecret: the pairing secret stays in Windows Credential
/// Manager), as an image or audio model's place on it needs.</summary>
public sealed record SenseHost(string HostId, string Origin, string SpkiFingerprint, string DeviceId, Guid CredentialId);

/// <summary>The image model's and the audio model's lists (docs/SENSE_MODELS.md, The image and audio pools): Companion › Vision
/// and Companion › Hearing, each PC's own (pools-local.json, pool areas <c>vision</c> and <c>hearing</c>). Each member's setting
/// is its model: Ollama on this PC (this-pc), a paired computer's Thinking pool role or Ollama (computer, with its engine), a
/// Thinking pool model on one graphics card (gpu), an OpenAI-compatible server you run (address) or a cloud provider (cloud,
/// whose model is part of the member). An empty list means no model of its own: Thinking's own model takes the pictures or
/// recordings. A list is made once from sense-models.json (<see cref="FromSenseModels"/>).</summary>
public static class SenseLists
{
    /// <summary>A service member's setting that the owner allowed pictures and recordings to go there (<see cref="Allowed"/>).</summary>
    public const string MediaSetting = "media";
    public const string Allowed = "allowed";
    /// <summary>A computer member's engine (<see cref="PoolSettingKeys.Engine"/>): its Thinking pool role (the default) or its
    /// Ollama.</summary>
    public const string RoleEngine = "deep-thinking";
    public const string OllamaEngine = "ollama";
    /// <summary>A cloud member's provider when it isn't a named one: any OpenAI-compatible server.</summary>
    public const string ChatCompletionsProvider = "chat-completions";
    public const string OpenAiProvider = "openai";
    public const string OpenAiBaseUrl = "https://api.openai.com/v1";

    public static PoolArea Area(SenseKind kind) => kind == SenseKind.Image ? PoolAreas.Vision : PoolAreas.Hearing;

    /// <summary>The member's model: a cloud member's own, else its model setting; null before one is chosen.</summary>
    public static string? ModelOf(PoolMember member)
    {
        ArgumentNullException.ThrowIfNull(member);
        return member.Kind == PoolMemberKind.Cloud ? member.Model : member.Setting(PoolSettingKeys.Model);
    }

    /// <summary>Whether pictures and recordings may go to <paramref name="member"/>: always to this PC and your paired computers;
    /// to a service you run only when it is on this PC or you allowed it (<see cref="MediaSetting"/>); to a cloud provider only
    /// with your agreement for this area (<see cref="PoolMember.Consented"/>).</summary>
    public static bool MayReceive(PoolMember member, PoolArea area)
    {
        ArgumentNullException.ThrowIfNull(member);
        ArgumentNullException.ThrowIfNull(area);
        return member.Kind switch
        {
            PoolMemberKind.Address => OnThisPc(member.Address) || member.Setting(MediaSetting) == Allowed,
            PoolMemberKind.Cloud => member.Consented(area.Id),
            _ => true
        };
    }

    /// <summary>Whether an address is this PC's own (loopback).</summary>
    public static bool OnThisPc(string? address) => Uri.TryCreate(address, UriKind.Absolute, out var uri) && uri.IsLoopback;

    /// <summary>The base URL of a cloud member: its own, else its named provider's.</summary>
    public static string? BaseUrl(PoolMember member)
    {
        ArgumentNullException.ThrowIfNull(member);
        return member.Origin ?? (member.Provider == OpenAiProvider ? OpenAiBaseUrl : ChatCompletionsEndpointCatalog.ById(member.Provider)?.BaseUrl);
    }

    /// <summary>The provider name a cloud member gets for <paramref name="baseUrl"/>: a named endpoint's ID, OpenAI, else the server's
    /// host ("127.0.0.1", "llm.lan") or, when that can't be a name, any OpenAI-compatible server.</summary>
    public static string ProviderFor(string baseUrl) =>
        ChatCompletionsEndpointCatalog.Named(baseUrl)?.Id ?? (string.Equals(baseUrl, OpenAiBaseUrl, StringComparison.Ordinal) ? OpenAiProvider
            : Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri) && uri.IdnHost is { Length: > 0 and <= 64 } host && char.IsAsciiLetterOrDigit(host[0]) &&
              host.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-') ? host : ChatCompletionsProvider);

    /// <summary>The graphics card's Thinking pool route a computer or card member's model runs on, or null for a computer's
    /// Ollama. A card past the last one a host can run a Thinking pool model on has none: <c>""</c>.</summary>
    public static string? RouteOf(PoolMember member)
    {
        ArgumentNullException.ThrowIfNull(member);
        if (member.Kind == PoolMemberKind.Gpu)
            return member.Card is { } card and >= 1 and <= SelfHostSetup.DeepThinkingMaximumCards ? SelfHostSetup.DeepThinkingRouteIdFor(card) : "";
        return member.Setting(PoolSettingKeys.Engine) == OllamaEngine ? null : SelfHostSetup.DeepThinkingRouteId;
    }

    /// <summary><paramref name="member"/> as the place a sense job is sent to, or null when it can't take one on this PC now: no
    /// model chosen, a computer this PC isn't paired with, a card with no Thinking pool route or an invalid address. A cloud
    /// member or a service uses its own key from <paramref name="keys"/> (pool-keys.json), else Thinking's key for the same base
    /// URL, else none.</summary>
    public static DeepThinkingSettings? Model(PoolMember member, PoolArea area, Func<string, SenseHost?> hosts, PoolKeys keys)
    {
        ArgumentNullException.ThrowIfNull(member);
        ArgumentNullException.ThrowIfNull(area);
        ArgumentNullException.ThrowIfNull(hosts);
        ArgumentNullException.ThrowIfNull(keys);
        if (ModelOf(member) is not { Length: > 0 } model) return null;
        DeepThinkingSettings? place = member.Kind switch
        {
            PoolMemberKind.ThisPc => new() { Place = DeepThinkingPlace.Endpoint, Origin = GenerationSupport.LocalOllamaChatBaseUrl, ModelId = model },
            PoolMemberKind.Address when member.Address is { } address => new()
            {
                Place = DeepThinkingPlace.Endpoint, Origin = address.TrimEnd('/'), ModelId = model, CredentialId = keys.For(area.Id, member.Key)
            },
            PoolMemberKind.Cloud when BaseUrl(member) is { } origin => new()
            {
                Place = DeepThinkingPlace.Endpoint, Origin = origin.TrimEnd('/'), ModelId = model, CredentialId = keys.For(area.Id, member.Key)
            },
            PoolMemberKind.Computer or PoolMemberKind.Gpu when member.HostId is { } id && hosts(id) is { } host && RouteOf(member) is not "" =>
                new()
                {
                    Place = DeepThinkingPlace.Host, ModelId = model, HostId = host.HostId, HostOrigin = host.Origin,
                    HostSpkiFingerprint = host.SpkiFingerprint, HostDeviceId = host.DeviceId, HostCredentialId = host.CredentialId,
                    HostRouteId = RouteOf(member)
                },
            _ => null
        };
        if (place is null) return null;
        try
        {
            place.Validate();
            return place;
        }
        catch (Exception error) when (error is ContractException or ArgumentException or UriFormatException) { return null; }
    }

    /// <summary>The places this PC sends <paramref name="area"/>'s jobs to, in the list's order (<see cref="PoolRouting.Order"/>:
    /// on, kept for <paramref name="device"/> or every companion PC, agreed to), leaving out members that may not receive
    /// pictures and recordings (<see cref="MayReceive"/>) or can't take a job on this PC (<see cref="Model"/>). One place once.</summary>
    public static IReadOnlyList<DeepThinkingSettings> Models(PoolArea area, PoolList? list, string device, Func<string, SenseHost?> hosts, PoolKeys keys)
    {
        ArgumentNullException.ThrowIfNull(area);
        return [.. PoolRouting.Order(area, list, device, m => MayReceive(m, area)).Members
            .Select(m => Model(m, area, hosts, keys)).OfType<DeepThinkingSettings>().DistinctBy(m => m.Key)];
    }

    /// <summary>A model of its own from sense-models.json as a list member: Ollama on this PC is this-pc, a paired computer's
    /// Thinking pool role or Ollama is a computer (a Thinking pool model on card 2 and up is that card), any other endpoint is a
    /// cloud member that the owner agreed to at <paramref name="at"/> (they chose it for these pictures or recordings).</summary>
    public static PoolMember Member(DeepThinkingSettings own, PoolArea area, DateTimeOffset at)
    {
        ArgumentNullException.ThrowIfNull(own);
        ArgumentNullException.ThrowIfNull(area);
        if (own.Place == DeepThinkingPlace.Host)
            return (own.OnHostRole && own.Card > 1 ? PoolMember.Gpu(own.HostId!, own.Card)
                    : PoolMember.Computer(own.HostId!).WithSetting(PoolSettingKeys.Engine, own.OnHostRole ? RoleEngine : OllamaEngine))
                .WithSetting(PoolSettingKeys.Model, own.ModelId);
        var origin = (own.Origin ?? "").TrimEnd('/');
        if (string.Equals(origin, GenerationSupport.LocalOllamaChatBaseUrl, StringComparison.Ordinal))
            return PoolMember.ThisPc().WithSetting(PoolSettingKeys.Model, own.ModelId);
        return PoolMember.Cloud(ProviderFor(origin), own.ModelId, origin).WithConsent(area.Id, at);
    }

    /// <summary><paramref name="kind"/>'s first list, made from sense-models.json (<paramref name="legacy"/>): empty when the text
    /// model takes it, else its model of its own (the other kind's when it used the same model). A model with its own key keeps it:
    /// <paramref name="keys"/> gets the area and member's entry for the same credential.</summary>
    public static PoolList FromSenseModels(SenseKind kind, SenseModels legacy, ref PoolKeys keys, DateTimeOffset at)
    {
        ArgumentNullException.ThrowIfNull(legacy);
        var area = Area(kind);
        if (legacy.Place(kind) is not { } own) return new() { Area = area.Id };
        var member = Member(own, area, at);
        if (own.CredentialId is { } credential) keys = keys.With(area.Id, member.Key, credential);
        return new() { Area = area.Id, Members = [member] };
    }

    /// <summary>The paired computers in hosts.json, by host ID; empty when it can't be read.</summary>
    public static IReadOnlyDictionary<string, SenseHost> ReadHosts(string? directory)
    {
        var found = new Dictionary<string, SenseHost>(StringComparer.Ordinal);
        if (directory is null) return found;
        try
        {
            var path = Path.Combine(directory, "hosts.json");
            if (!File.Exists(path) || new FileInfo(path).Length > 262_144) return found;
            foreach (var host in (JsonNode.Parse(File.ReadAllText(path))?["hosts"] as JsonArray ?? []).OfType<JsonObject>())
            {
                var pairing = host["pairing"];
                if (pairing?["hostId"]?.GetValue<string>() is not { } id || pairing["origin"]?.GetValue<string>() is not { } origin ||
                    pairing["spkiFingerprint"]?.GetValue<string>() is not { } pin || pairing["deviceId"]?.GetValue<string>() is not { } device ||
                    Credential(pairing["credentialId"]?.GetValue<string>()) is not { } credential)
                    continue;
                found[id] = new(id, origin, pin, device, credential);
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException or FormatException) { }
        return found;
    }

    // A pairing credential ID (16 random bytes, base64url) as the GUID a paired place keeps.
    private static Guid? Credential(string? id)
    {
        if (id is not { Length: 22 }) return null;
        var bytes = new byte[16];
        return Base64Url.DecodeFromChars(id, bytes) == 16 ? new Guid(bytes) : null;
    }
}
