using System.Text.Json.Serialization;
using Martlet.Core.Contracts;

namespace Martlet.Core.Settings;

public enum ProfileKind { NotConfigured, Fixture, Api, ExistingEndpoints }

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record AppSettings : IContract
{
    public const int CurrentSchemaVersion = 1;
    public const int MaxFileBytes = 65_536;
    public required int SchemaVersion { get; init; }
    public required ProfileSettings Profile { get; init; }

    public static AppSettings CreateUnconfigured() => new()
    {
        SchemaVersion = CurrentSchemaVersion,
        Profile = new ProfileSettings { SchemaVersion = 1, Id = Guid.NewGuid(), Kind = ProfileKind.NotConfigured, Credentials = [] }
    };

    public void Validate()
    {
        ContractRules.Require(SchemaVersion == CurrentSchemaVersion, "Use a compatible app or restore a compatible settings backup; this file was not changed.", ErrorCode.UnsupportedVersion);
        ContractRules.Require(Profile is not null, "A profile is required.");
        Profile!.Validate();
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
