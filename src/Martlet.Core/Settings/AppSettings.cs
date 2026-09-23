using System.Text.Json.Serialization;
using Martlet.Core.Contracts;

namespace Martlet.Core.Settings;

public enum ProfileKind { NotConfigured, Fixture, Api, ExistingEndpoints }

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record AppSettings : IContract
{
    public const int CurrentSchemaVersion = 4;
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
        ContractRules.Require(SchemaVersion != 1 || Audio is null, "Version 1 cannot contain audio setup.");
        Audio?.Validate();
        ContractRules.Require(SchemaVersion < 3 ? Companion is null : Companion is not null,
            "Settings before version 3 cannot contain companion profiles; version 3 requires them.");
        Companion?.Validate();
        ContractRules.Require(SchemaVersion < 4 ? Memory is null : Memory is not null,
            "Settings before version 4 cannot contain memory settings; version 4 requires them.");
        Memory?.Validate();
        if (Setup is not null)
        {
            var legacy = Profile.Credentials.Select(item => item.CredentialId).ToHashSet();
            ContractRules.Require(Setup.Routes.All(route => route.CredentialId is not { } id || !legacy.Contains(id)) &&
                Setup.PendingRemovals.All(item => !legacy.Contains(item.CredentialId)),
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
            Setup = settings.Setup ?? new() { SchemaVersion = 1, Checkpoint = SetupStep.Choice, Routes = [], PendingRemovals = [] },
            Companion = settings.Companion ?? CompanionSettings.Create(),
            Memory = settings.Memory ?? MemorySettings.Create()
        };
    }
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
