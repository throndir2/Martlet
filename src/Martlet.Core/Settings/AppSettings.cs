using System.Text.Json.Serialization;
using Martlet.Core.Contracts;

namespace Martlet.Core.Settings;

// Fixture is a retired "Demo only" choice kept so saved numeric values still load; Setup saves it as Api.
public enum ProfileKind { NotConfigured, Fixture, Api, ExistingEndpoints, SelfHosted }

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record AppSettings : IContract
{
    public const int CurrentSchemaVersion = 6;
    /// <summary>From settings v6 memory is ON by default; earlier versions only recorded the old OFF default.</summary>
    public const int MemoryOnByDefaultSchemaVersion = 6;
    public const int MaxFileBytes = 131_072;
    public required int SchemaVersion { get; init; }
    public required ProfileSettings Profile { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public SetupSettings? Setup { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public AudioSettings? Audio { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public CompanionSettings? Companion { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public MemorySettings? Memory { get; init; }
    /// <summary>Optional reply generation settings (Companion > Replies); absent while every value is the model default.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public GenerationSettings? Generation { get; init; }
    /// <summary>Optional second Thinking destination used when a reply's Thinking request fails (Companion › Thinking).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ThinkingFallbackSettings? ThinkingFallback { get; init; }
    /// <summary>Optional edits to Martlet's internal prompts (Companion › Prompts); absent while every prompt is built in.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public PromptSettings? Prompts { get; init; }

    public static AppSettings CreateUnconfigured() => new()
    {
        SchemaVersion = 1,
        Profile = new ProfileSettings { SchemaVersion = 1, Id = Guid.NewGuid(), Kind = ProfileKind.NotConfigured, Credentials = [] }
    };

    public void Validate()
    {
        ContractRules.Require(SchemaVersion is >= 1 and <= CurrentSchemaVersion, "Use a compatible app or restore a compatible settings backup; this file was not changed.", ErrorCode.UnsupportedVersion);
        ContractRules.Require(Profile is not null, "A profile is required.");
        Profile!.Validate();
        ContractRules.Require(SchemaVersion == 1 ? Setup is null : Setup is not null,
            "Version 1 cannot contain setup; later versions require a setup checkpoint.");
        Setup?.Validate();
        ContractRules.Require(SchemaVersion < 5
                ? Setup is null || Setup.SchemaVersion == 1
                : Setup?.SchemaVersion == SetupSettings.CurrentSchemaVersion,
            "Settings v5 requires versioned route discriminators; earlier settings retain the legacy setup contract.");
        ContractRules.Require(SchemaVersion >= 5 || Profile.Kind != ProfileKind.SelfHosted,
            "Self-hosted profiles require settings v5.");
        ContractRules.Require(SchemaVersion != 1 || Audio is null, "Version 1 cannot contain audio setup.");
        Audio?.Validate();
        ContractRules.Require(SchemaVersion < 3 ? Companion is null : Companion is not null,
            "Settings before version 3 cannot contain companion profiles; version 3 requires them.");
        Companion?.Validate();
        ContractRules.Require(SchemaVersion < 4 ? Memory is null : Memory is not null,
            "Settings before version 4 cannot contain memory settings; version 4 requires them.");
        Memory?.Validate();
        ContractRules.Require(Generation is null || !Generation.IsDefault,
            "Generation settings are saved as absent when every value is the model default.");
        Generation?.Validate();
        ThinkingFallback?.Validate();
        ContractRules.Require(Prompts is null || !Prompts.IsDefault,
            "Prompt settings are saved as absent when every prompt is built in.");
        Prompts?.Validate();
        if (Setup is not null)
        {
            var legacy = Profile.Credentials.Select(item => item.CredentialId).ToHashSet();
            ContractRules.Require(Setup.Routes.All(route => route.CredentialId is not { } id || !legacy.Contains(id)) &&
                Setup.PendingRemovals.All(item => !legacy.Contains(item.CredentialId)) &&
                (Setup.RetainedGatewayCredentials ?? []).All(item => !legacy.Contains(item.CredentialId)),
                "Legacy credential references are preserved, never reused or deleted by setup.");
        }
    }

    internal static AppSettings UpgradeToCurrent(AppSettings? prior)
    {
        var settings = prior ?? CreateUnconfigured();
        settings.Validate();
        return settings with
        {
            SchemaVersion = CurrentSchemaVersion,
            Setup = (settings.Setup ?? new()
            {
                SchemaVersion = 1,
                Checkpoint = SetupStep.Choice,
                Routes = [],
                PendingRemovals = []
            }).UpgradeToCurrent(),
            Companion = settings.Companion ?? CompanionSettings.Create(),
            Memory = settings.SchemaVersion < MemoryOnByDefaultSchemaVersion && settings.Memory is { } memory
                ? memory.Configure(true, memory.StoragePolicy, memory.CustomDirectory)
                : settings.Memory ?? MemorySettings.Create()
        };
    }

    // Before v6 a saved OFF was only the old default, never a choice under the ON default; it reads as ON until saved as v6.
    internal static AppSettings ApplyMemoryDefault(AppSettings settings) =>
        settings.SchemaVersion < MemoryOnByDefaultSchemaVersion && settings.Memory is { Enabled: false } memory
            ? settings with { Memory = memory with { Enabled = true } }
            : settings;
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ProfileSettings : IContract
{
    public required int SchemaVersion { get; init; }
    public required Guid Id { get; init; }
    public required ProfileKind Kind { get; init; }
    public required IReadOnlyList<SecretReference> Credentials { get; init; }

    public void Validate()
    {
        ContractRules.Require(SchemaVersion == 1, "The profile version is unsupported; use a compatible app.", ErrorCode.UnsupportedVersion);
        ContractRules.Require(Id != Guid.Empty, "The profile identifier must be a nonempty UUID.");
        ContractRules.Defined(Kind);
        ContractRules.Require(Credentials is { Count: <= 16 }, "At most 16 credential references are supported.");
        var providers = new HashSet<string>(StringComparer.Ordinal);
        foreach (var reference in Credentials!)
        {
            ContractRules.Require(reference is not null, "A credential reference cannot be null.");
            reference!.Validate();
            ContractRules.Require(providers.Add(reference.ProviderId), "A provider cannot have duplicate credential references.");
        }
    }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record SecretReference : IContract
{
    public required string ProviderId { get; init; }
    public required Guid CredentialId { get; init; }

    public void Validate()
    {
        ContractRules.Identifier(ProviderId);
        ContractRules.Require(CredentialId != Guid.Empty, "A credential reference must be a nonempty UUID, never a raw secret.");
    }
}
