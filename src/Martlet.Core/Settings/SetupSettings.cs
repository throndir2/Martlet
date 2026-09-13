using System.Text.Json.Serialization;
using Martlet.Core.Contracts;

namespace Martlet.Core.Settings;

public enum SetupRole { Stt, Llm, Tts }
public enum SetupStep { Choice, Destinations, Credentials, Review }

public static class OpenAiSetup
{
    public const string Origin = "https://api.openai.com";
    public const string Pricing = "https://openai.com/api/pricing/";
    public const string Retention = "https://platform.openai.com/docs/guides/your-data";
    public const string Disclosure = "Cloud requests may cost money. Price, quota, model access and key validity are unknown. " +
        "Saving is not permission for capture or future paid requests. Screen and memory stay OFF. " +
        "No network, model discovery or billable health test is performed.";

    public static string Alias(SetupRole role) => role switch
    {
        SetupRole.Stt => "openai-stt", SetupRole.Llm => "openai-llm", SetupRole.Tts => "openai-tts",
        _ => throw new ContractException(ErrorCode.InvalidContract, "Choose STT, LLM or TTS.")
    };

    public static string Boundary(SetupRole role) => role switch
    {
        SetupRole.Stt => "Microphone audio -> cloud STT. Even speech later suppressed can be disclosed and charged.",
        SetupRole.Llm => "Transcript and conversation text -> cloud LLM.",
        SetupRole.Tts => "Response text -> cloud TTS. Output is generated voice, not a human recording.",
        _ => throw new ContractException(ErrorCode.InvalidContract, "Choose STT, LLM or TTS.")
    };

    internal static void UpstreamId(string value)
    {
        ContractRules.Require(value is { Length: >= 1 and <= 128 } &&
            value.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.'),
            "Enter an explicit upstream model or voice ID (1-128 ASCII letters, digits, dot, underscore or hyphen); no endpoint, key or default is accepted.");
    }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record DestinationConsent : IContract
{
    public required int SchemaVersion { get; init; }
    public required SetupRole Role { get; init; }
    public required string ProviderAlias { get; init; }
    public required string Origin { get; init; }
    public required string ModelId { get; init; }
    public string? VoiceId { get; init; }
    public Guid? CredentialId { get; init; }
    public required Guid ConfigurationRevision { get; init; }
    public required bool UserSelected { get; init; }

    public void Validate()
    {
        ContractRules.Require(SchemaVersion == 1, "Unsupported consent version.", ErrorCode.UnsupportedVersion);
        ContractRules.Defined(Role);
        ContractRules.Require(UserSelected && ConfigurationRevision != Guid.Empty, "Destination consent requires an explicit user selection and configuration revision.");
        ContractRules.Require(ProviderAlias == OpenAiSetup.Alias(Role) && Origin == OpenAiSetup.Origin,
            "Only the named OpenAI destination is supported. Never send a cloud key to a custom endpoint.");
        OpenAiSetup.UpstreamId(ModelId);
        ContractRules.Require(Role == SetupRole.Tts ? VoiceId is not null : VoiceId is null, "Select a voice only for TTS.");
        if (VoiceId is not null) OpenAiSetup.UpstreamId(VoiceId);
        ContractRules.Require(CredentialId != Guid.Empty, "A credential reference must be a nonempty UUID.");
    }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record SetupRoute : IContract
{
    public required SetupRole Role { get; init; }
    public required string ProviderAlias { get; init; }
    public required string Origin { get; init; }
    public required string ModelId { get; init; }
    public string? VoiceId { get; init; }
    public Guid? CredentialId { get; init; }
    public required Guid ConfigurationRevision { get; init; }
    public DestinationConsent? Consent { get; init; }

    public DestinationConsent Selection() => new()
    {
        SchemaVersion = 1, Role = Role, ProviderAlias = ProviderAlias, Origin = Origin,
        ModelId = ModelId, VoiceId = VoiceId, CredentialId = CredentialId,
        ConfigurationRevision = ConfigurationRevision, UserSelected = true
    };

    public void Validate()
    {
        Selection().Validate();
        Consent?.Validate();
        ContractRules.Require(Consent is null || Consent == Selection(),
            "This route changed. Review its destination, model, voice and credential, then explicitly consent again.");
    }

    public SetupRoute WithCredential(Guid? id) => this with
    {
        CredentialId = id, Consent = null, ConfigurationRevision = Guid.NewGuid()
    };
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record PendingCredentialRemoval
{
    public required SetupRole Role { get; init; }
    public required Guid CredentialId { get; init; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record SetupSettings : IContract
{
    public required int SchemaVersion { get; init; }
    public required SetupStep Checkpoint { get; init; }
    public required IReadOnlyList<SetupRoute> Routes { get; init; }
    public required IReadOnlyList<PendingCredentialRemoval> PendingRemovals { get; init; }

    public void Validate()
    {
        ContractRules.Require(SchemaVersion == 1, "Unsupported setup version.", ErrorCode.UnsupportedVersion);
        ContractRules.Defined(Checkpoint);
        ContractRules.Require(Routes is { Count: <= 3 } && PendingRemovals is { Count: <= 16 }, "Setup supports three roles and at most sixteen pending credential removals.");
        var roles = new HashSet<SetupRole>();
        var ids = new HashSet<Guid>();
        foreach (var route in Routes!)
        {
            ContractRules.Require(route is not null, "A route cannot be null.");
            route!.Validate();
            ContractRules.Require(roles.Add(route.Role), "Each role must have exactly one selected route.");
            if (route.CredentialId is { } id)
                ContractRules.Require(ids.Add(id), "Credentials cannot be shared across role policies.");
        }
        foreach (var removal in PendingRemovals!)
        {
            ContractRules.Require(removal is not null && removal.CredentialId != Guid.Empty, "A pending removal requires its owned reference.");
            ContractRules.Defined(removal!.Role);
            ContractRules.Require(ids.Add(removal.CredentialId), "A pending removal cannot delete an active or duplicate credential.");
        }
    }

    // Preparing an edit is in memory only. Migration occurs on an explicit successful save.
    public static AppSettings Begin(AppSettings? prior)
    {
        var settings = prior ?? AppSettings.CreateUnconfigured();
        settings.Validate();
        return settings with
        {
            SchemaVersion = AppSettings.CurrentSchemaVersion,
            Setup = settings.Setup ?? new() { SchemaVersion = 1, Checkpoint = SetupStep.Choice, Routes = [], PendingRemovals = [] }
        };
    }

    public static AppSettings SelectRoute(AppSettings settings, SetupRole role, string modelId, string? voiceId)
    {
        settings.Validate();
        var setup = settings.Setup ?? throw new ContractException(ErrorCode.InvalidContract, "Open setup before selecting a route.");
        var old = setup.Routes.SingleOrDefault(r => r.Role == role);
        var route = new SetupRoute
        {
            Role = role, ProviderAlias = OpenAiSetup.Alias(role), Origin = OpenAiSetup.Origin,
            ModelId = modelId, VoiceId = voiceId, CredentialId = old?.CredentialId,
            ConfigurationRevision = Guid.NewGuid()
        };
        route.Validate();
        if (old is not null && old.ModelId == modelId && old.VoiceId == voiceId)
            route = old;
        return ReplaceRoute(settings, route);
    }

    public static AppSettings ReplaceRoute(AppSettings settings, SetupRoute route)
    {
        var updated = settings with { Setup = settings.Setup! with
        {
            Routes = settings.Setup!.Routes.Where(r => r.Role != route.Role).Append(route).OrderBy(r => r.Role).ToArray()
        } };
        updated.Validate();
        return updated;
    }

    public static string Describe(AppSettings? settings)
    {
        if (settings?.Setup is not { } setup)
            return "Setup not started. Open Setup / resume; fixture needs no account or key.";
        var lines = new List<string>
        {
            $"Saved choice: {settings.Profile.Kind}. Checkpoint: {setup.Checkpoint}. Configuration only; live account and device readiness are not established by this summary.",
            "Saved choices do not authorize recording or provider requests. This summary does not start capture, screen or memory.",
            AudioSetupStatus.From(settings.Audio).Describe(),
            OpenAiSetup.Disclosure
        };
        foreach (var role in Enum.GetValues<SetupRole>())
        {
            var route = setup.Routes.SingleOrDefault(r => r.Role == role);
            lines.Add(route is null ? $"{role}: not configured. Select a named route and upstream ID in Setup." :
                $"{role}: route selected; {(route.Consent is null ? "consent missing or invalidated by a change; review again" : "destination choice recorded, NOT per-turn authorization")}; " +
                $"{(route.CredentialId is null ? "credential not configured" : "credential reference saved, OS presence and API validity unknown")}. Connection not checked by this summary.");
        }
        if (setup.PendingRemovals.Count != 0)
            lines.Add("Credential cleanup pending. Open setup and explicitly retry removal of the listed detached references.");
        return string.Join(Environment.NewLine, lines);
    }
}
