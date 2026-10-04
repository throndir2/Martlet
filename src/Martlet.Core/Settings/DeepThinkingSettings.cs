using System.Text.Json;
using System.Text.Json.Serialization;
using Martlet.Core.Contracts;

namespace Martlet.Core.Settings;

/// <summary>Where Deep thinking runs: with the Thinking model, on an OpenAI-compatible endpoint (a cloud provider, another
/// server, or Ollama on this PC with its own model) or on a paired Martlet host's Ollama.</summary>
public enum DeepThinkingPlace { SameAsThinking, Endpoint, Host }

/// <summary>Companion › Deep thinking › Where it thinks, on this PC (deep-thinking.json in the data folder; never part of the
/// settings your computers share, because which machine is free to think depends on the computer you talk to). Thinking
/// answers you; Deep thinking works out what Martlet hands it in the background (think_longer). On another machine (a paired
/// computer or a cloud provider) it runs in parallel with the conversation; on the hardware the conversation uses it waits for
/// quiet moments (<see cref="DeepThinkingPlan"/>). An endpoint's own key is in Windows Credential Manager
/// (<see cref="CredentialId"/>, a reference); a paired computer is reached with this PC's pairing, whose secret stays where pairing
/// saved it.</summary>
public sealed record DeepThinkingSettings
{
    public const string FileName = "deep-thinking.json";
    private const int MaxFileBytes = 64 * 1024;

    [JsonConverter(typeof(JsonStringEnumConverter<DeepThinkingPlace>))]
    public DeepThinkingPlace Place { get; init; }
    /// <summary>The endpoint's API base URL (Place Endpoint).</summary>
    public string? Origin { get; init; }
    /// <summary>The model: the endpoint's model ID, or the one the paired computer's Ollama route serves.</summary>
    public string? ModelId { get; init; }
    /// <summary>The endpoint's own key in Windows Credential Manager; null uses Thinking's key for the same base URL, or none.</summary>
    public Guid? CredentialId { get; init; }
    /// <summary>The paired computer (Place Host): its ID, pinned gateway origin and key, and this PC's pairing with it.</summary>
    public string? HostId { get; init; }
    public string? HostOrigin { get; init; }
    public string? HostSpkiFingerprint { get; init; }
    public string? HostDeviceId { get; init; }
    public Guid? HostCredentialId { get; init; }
    public DateTimeOffset? ChosenAt { get; init; }

    [JsonIgnore] public bool Separate => Place != DeepThinkingPlace.SameAsThinking;

    /// <summary>Whether the endpoint runs on this PC (Ollama, LM Studio or another server on loopback).</summary>
    [JsonIgnore]
    public bool OnThisPc => Place == DeepThinkingPlace.Endpoint && Uri.TryCreate(Origin, UriKind.Absolute, out var origin) && origin.IsLoopback;

    /// <summary>Where it thinks, in words: "the Thinking model", "Ollama on this PC (gemma4:12b)", "diva (gemma4:27b)",
    /// "openrouter.ai (x-ai/grok-4.3)".</summary>
    public string Describe() => Place switch
    {
        DeepThinkingPlace.Host => $"{HostId} ({ModelId})",
        DeepThinkingPlace.Endpoint when string.Equals(Origin, GenerationSupport.LocalOllamaChatBaseUrl, StringComparison.Ordinal) =>
            $"Ollama on this PC ({ModelId})",
        DeepThinkingPlace.Endpoint => $"{(Uri.TryCreate(Origin, UriKind.Absolute, out var uri) ? uri.IdnHost : Origin)} ({ModelId})",
        _ => "the Thinking model"
    };

    /// <summary>Throws when the saved destination isn't usable as it is.</summary>
    public void Validate()
    {
        ContractRules.Defined(Place);
        switch (Place)
        {
            case DeepThinkingPlace.Endpoint:
                _ = ChatCompletionsSetup.BaseUri(Origin ?? "");
                ChatCompletionsSetup.ModelId(ModelId ?? "");
                ContractRules.Require(CredentialId != Guid.Empty && HostId is null, "An endpoint's key reference is invalid.");
                break;
            case DeepThinkingPlace.Host:
                ContractRules.Require(HostId is { Length: > 0 and <= 128 } && !HostId.Any(char.IsControl) &&
                    Uri.TryCreate(HostOrigin, UriKind.Absolute, out var origin) && origin.Scheme == Uri.UriSchemeHttps &&
                    HostSpkiFingerprint is { Length: > 0 and <= 200 } && HostDeviceId is { Length: > 0 and <= 128 } &&
                    HostCredentialId is { } pairing && pairing != Guid.Empty && CredentialId is null && Origin is null,
                    "The paired computer for Deep thinking is incomplete. Choose it again.");
                ChatCompletionsSetup.ModelId(ModelId ?? "");
                break;
            default:
                ContractRules.Require(Origin is null && ModelId is null && CredentialId is null && HostId is null,
                    "Same as Thinking keeps no destination of its own.");
                break;
        }
    }

    public static DeepThinkingSettings Load(string? directory) => Read(directory).Settings;

    /// <summary>The saved destination and the file's state: none, loaded or unreadable (which reads as Same as Thinking).</summary>
    public static (DeepThinkingSettings Settings, string State) Read(string? directory)
    {
        if (directory is null) return (new(), "none");
        try
        {
            var path = Path.Combine(directory, FileName);
            if (!File.Exists(path)) return (new(), "none");
            if (new FileInfo(path).Length > MaxFileBytes) return (new(), "unreadable");
            var loaded = JsonSerializer.Deserialize<DeepThinkingSettings>(File.ReadAllText(path));
            if (loaded is null) return (new(), "unreadable");
            loaded.Validate();
            return (loaded, "loaded");
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or NotSupportedException or
            ContractException or ArgumentException)
        {
            return (new(), "unreadable");
        }
    }

    /// <summary>Saves deep-thinking.json atomically; false when the data folder can't be written.</summary>
    public bool Save(string? directory)
    {
        if (directory is null) return false;
        Validate();
        try
        {
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, FileName);
            var temporary = Path.Combine(directory, $"deep-thinking.{Guid.NewGuid():N}.tmp");
            try
            {
                File.WriteAllText(temporary, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
                File.Move(temporary, path, overwrite: true);
            }
            finally
            {
                if (File.Exists(temporary)) File.Delete(temporary);
            }
            return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return false; }
    }

    /// <summary>The endpoint's own key scope, like a Chat Completions Thinking key bound to its exact base URL.</summary>
    public CredentialBinding Binding(Guid profileId, Guid credentialId) =>
        new(profileId, credentialId, SetupRole.Llm, SetupRouteType.ChatCompletions, ChatCompletionsSetup.Alias, Origin!);

    /// <summary>Whether it borrows the Thinking route's key: no key of its own, and the same base URL as a Chat Completions
    /// Thinking route that has one.</summary>
    public bool UsesThinkingKey(SetupRoute? thinking) =>
        Place == DeepThinkingPlace.Endpoint && CredentialId is null &&
        thinking is { RouteType: SetupRouteType.ChatCompletions, CredentialId: not null } && thinking.Origin == Origin;

    /// <summary>Whether it is exactly the Thinking route already (same endpoint and model).</summary>
    public bool SameAs(SetupRoute? thinking) =>
        Place == DeepThinkingPlace.SameAsThinking ||
        Place == DeepThinkingPlace.Endpoint && thinking?.RouteType == SetupRouteType.ChatCompletions && thinking.Origin == Origin &&
        thinking.ModelId == ModelId;
}

/// <summary>Whether a background think runs in parallel with the conversation or waits for quiet moments, and why. It waits
/// only when it would share hardware with what the conversation needs right now: the Thinking model's own server when that is
/// on this PC (a local server answers one request at a time and keeps one conversation in its prompt cache), this PC's
/// graphics card while Thinking or the voice uses it, or a paired computer that does one of the conversation's jobs
/// (Thinking, the voice or listening; its gateway serves one request per job and its graphics card is shared).</summary>
public sealed record DeepThinkingPlan(bool Parallel, string Why)
{
    public static DeepThinkingPlan For(DeepThinkingSettings deep, IReadOnlyList<SetupRoute> routes)
    {
        ArgumentNullException.ThrowIfNull(deep);
        ArgumentNullException.ThrowIfNull(routes);
        var thinking = routes.SingleOrDefault(r => r.Role == SetupRole.Llm);
        var thinkingHere = thinking is not null && IsThisPc(thinking);
        if (deep.SameAs(thinking))
            return thinking is null ? new(true, "Thinking isn't set up yet.")
                : thinkingHere
                ? new(false, "Thinking runs on this PC and its server answers one request at a time, so a think waits for quiet moments.")
                : thinking?.RouteType == SetupRouteType.GatewayOllama
                    ? new(false, $"Thinking runs on {thinking.Gateway?.HostId ?? "a paired computer"}, which answers one request at a time, so a think waits for quiet moments.")
                    : new(true, "Thinking's provider answers several requests at once, so a think runs alongside the conversation.");
        if (deep.OnThisPc)
        {
            var busy = routes.Where(r => r.Enabled != false && IsThisPc(r) && r.Role is SetupRole.Llm or SetupRole.Tts).ToArray();
            return busy.Length > 0
                ? new(false, $"It runs on this PC, where {Jobs(busy)} also runs{(busy.Any(r => r.Role == SetupRole.Llm) && SameServer(deep, thinking) ? " on the same server" : "")}, so a think waits for quiet moments.")
                : new(true, "It runs on this PC while the conversation's models run elsewhere, so a think runs alongside the conversation.");
        }
        if (deep.Place == DeepThinkingPlace.Host)
        {
            var shared = routes.Where(r => r.Enabled != false && SelfHostSetup.IsGateway(r.RouteType) && r.Gateway?.HostId == deep.HostId).ToArray();
            return shared.Length > 0
                ? new(false, $"{deep.HostId} also does {Jobs(shared)} for the conversation, so a think waits for quiet moments there.")
                : new(true, $"{deep.HostId} does none of the conversation's jobs, so a think runs there alongside the conversation.");
        }
        return new(true, "It runs on its own provider, so a think runs alongside the conversation.");
    }

    // A route served on this PC: a loopback endpoint (Ollama, LM Studio...) or this PC's own host service.
    private static bool IsThisPc(SetupRoute route) =>
        route.RouteType == SetupRouteType.ChatCompletions && Uri.TryCreate(route.Origin, UriKind.Absolute, out var origin) && origin.IsLoopback ||
        SelfHostSetup.IsGateway(route.RouteType) && Uri.TryCreate(route.Gateway?.Origin, UriKind.Absolute, out var gateway) && gateway.IsLoopback;

    private static bool SameServer(DeepThinkingSettings deep, SetupRoute? thinking) =>
        thinking?.RouteType == SetupRouteType.ChatCompletions && Uri.TryCreate(thinking.Origin, UriKind.Absolute, out var a) &&
        Uri.TryCreate(deep.Origin, UriKind.Absolute, out var b) && a.Authority == b.Authority;

    private static string Jobs(IEnumerable<SetupRoute> routes) => string.Join(" and ", routes.Select(r => r.Role switch
    {
        SetupRole.Llm => "Thinking",
        SetupRole.Tts => "the voice",
        _ => "listening"
    }).Distinct());
}
