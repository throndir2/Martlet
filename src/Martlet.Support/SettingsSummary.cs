using System.Text.Json.Serialization;
using Martlet.Core.Contracts;
using Martlet.Core.Settings;

namespace Martlet.Support;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RoleSummary : IContract
{
    public required SetupRole Role { get; init; }
    public required bool RouteSelected { get; init; }
    public required bool DestinationSelected { get; init; }
    public required bool CredentialReferenced { get; init; }
    public void Validate()
    {
        Guard.Defined(Role);
        Guard.Require(RouteSelected || !DestinationSelected && !CredentialReferenced);
    }
}

// No AppSettings, object, dictionary, provider or credential-store parameter is accepted.
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record SettingsSummary : IContract
{
    public required int SchemaVersion { get; init; }
    public required SettingsLoadState LoadState { get; init; }
    public ProfileKind? ProfileKind { get; init; }
    public SetupStep? Checkpoint { get; init; }
    public RoleSummary? Stt { get; init; }
    public RoleSummary? Llm { get; init; }
    public RoleSummary? Tts { get; init; }
    public required int PendingCredentialRemovals { get; init; }
    public required bool InputSelected { get; init; }
    public required bool OutputSelected { get; init; }
    public required bool InputUsesDefault { get; init; }
    public required bool OutputUsesDefault { get; init; }

    public void Validate()
    {
        Guard.Require(SchemaVersion == 1, SupportFailure.UnsupportedVersion);
        Guard.Defined(LoadState);
        if (ProfileKind is { } kind) Guard.Defined(kind);
        if (Checkpoint is { } step) Guard.Defined(step);
        Guard.Require((LoadState == SettingsLoadState.Loaded) == (ProfileKind is not null));
        Guard.Require(PendingCredentialRemovals is >= 0 and <= 16);
        Guard.Require(Checkpoint is null
            ? Stt is null && Llm is null && Tts is null && PendingCredentialRemovals == 0
            : LoadState == SettingsLoadState.Loaded && Stt?.Role == SetupRole.Stt &&
                Llm?.Role == SetupRole.Llm && Tts?.Role == SetupRole.Tts);
        Stt?.Validate(); Llm?.Validate(); Tts?.Validate();
        Guard.Require(LoadState == SettingsLoadState.Loaded || !InputSelected && !OutputSelected);
    }

    public static SettingsSummary FromStatus(SettingsLoadState state, ProfileKind? kind, SetupStatus? setup)
    {
        try { setup?.Validate(); }
        catch (ContractException) { throw new SupportException(SupportFailure.InvalidData); }
        RoleSummary? Role(SetupRole role)
        {
            var source = setup?.Roles.Single(r => r.Role == role);
            return source is null ? null : new()
            {
                Role = role, RouteSelected = source.RouteSelected, DestinationSelected = source.DestinationSelected,
                CredentialReferenced = source.CredentialReferenced
            };
        }
        var value = new SettingsSummary
        {
            SchemaVersion = 1, LoadState = state, ProfileKind = kind, Checkpoint = setup?.Checkpoint,
            Stt = Role(SetupRole.Stt), Llm = Role(SetupRole.Llm), Tts = Role(SetupRole.Tts),
            PendingCredentialRemovals = setup?.PendingRemovals ?? 0,
            InputSelected = setup?.Audio?.InputSelected ?? false,
            OutputSelected = setup?.Audio?.OutputSelected ?? false,
            InputUsesDefault = setup?.Audio?.InputUsesDefault ?? true,
            OutputUsesDefault = setup?.Audio?.OutputUsesDefault ?? true
        };
        value.Validate();
        return value;
    }
}
