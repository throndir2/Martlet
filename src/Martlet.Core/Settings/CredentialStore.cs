using System.Security.Cryptography;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json.Serialization;
using System.Text.Json;
using Martlet.Core.Contracts;

namespace Martlet.Core.Settings;

public enum CredentialError { None, Missing, AccessDenied, UnsupportedPlatform, InvalidInput, Unavailable }

public sealed record CredentialBinding(
    Guid ProfileId,
    Guid CredentialId,
    SetupRole Role,
    SetupRouteType RouteType,
    string ProviderAlias,
    string Origin,
    string? HostId = null,
    string? SpkiFingerprint = null,
    string? DeviceRole = null,
    string? DeviceId = null)
{
    public CredentialBinding(
        Guid profileId,
        Guid credentialId,
        SetupRole role,
        string providerAlias,
        string origin)
        : this(profileId, credentialId, role, SetupRouteType.OpenAi,
            providerAlias, origin)
    {
    }

    public void Validate()
    {
        ContractRules.Require(ProfileId != Guid.Empty && CredentialId != Guid.Empty, "A credential requires an owned profile and fresh reference.");
        ContractRules.Defined(Role);
        ContractRules.Defined(RouteType);
        if (RouteType == SetupRouteType.ChatCompletions)
        {
            _ = ChatCompletionsSetup.BaseUri(Origin);
            ContractRules.Require(Role == SetupRole.Llm && ProviderAlias == ChatCompletionsSetup.Alias &&
                HostId is null && SpkiFingerprint is null && DeviceRole is null && DeviceId is null,
                "Chat Completions credentials are limited to their exact API base URL and LLM role.");
            return;
        }
        if (RouteType == SetupRouteType.OpenAi)
        {
            ContractRules.Require(ProviderAlias == OpenAiSetup.Alias(Role) && Origin == OpenAiSetup.Origin &&
                HostId is null && SpkiFingerprint is null && DeviceRole is null && DeviceId is null,
                "OpenAI credential use is limited to the selected origin and role.");
            return;
        }
        ContractRules.Require(RouteType is SetupRouteType.GatewayOllama or SetupRouteType.GatewayF5 &&
            ProviderAlias == (RouteType == SetupRouteType.GatewayOllama
                ? SelfHostSetup.GatewayOllamaAlias
                : SelfHostSetup.GatewayF5Alias) &&
            Role == (RouteType == SetupRouteType.GatewayOllama ? SetupRole.Llm : SetupRole.Tts),
            "A gateway credential is limited to its named inference route.");
        SelfHostSetup.Identifier(DeviceId, 64);
        new GatewayEndpointSettings
        {
            SchemaVersion = 1,
            Origin = Origin,
            HostId = HostId!,
            SpkiFingerprint = SpkiFingerprint!,
            DeviceRole = DeviceRole!
        }.Validate();
    }

    public static CredentialBinding For(AppSettings settings, SetupRole role, Guid id)
    {
        var route = settings.Setup?.Routes.SingleOrDefault(item => item.Role == role) ??
            throw new ContractException(ErrorCode.InvalidContract, "Select the exact credential route first.");
        return For(settings.Profile.Id, route, id);
    }

    public static CredentialBinding For(Guid profileId, SetupRoute route, Guid id)
    {
        var routeType = route.RouteType ?? SetupRouteType.OpenAi;
        return new(
            profileId,
            id,
            route.Role,
            routeType,
            route.ProviderAlias,
            route.Origin,
            route.Gateway?.HostId,
            route.Gateway?.SpkiFingerprint,
            route.Gateway?.DeviceRole,
            route.GatewayDeviceId);
    }

    public static CredentialBinding For(AppSettings settings, PendingCredentialRemoval removal)
    {
        ArgumentNullException.ThrowIfNull(removal);
        if (removal.Scope is null)
            return new(
                settings.Profile.Id,
                removal.CredentialId,
                removal.Role,
                SetupRouteType.OpenAi,
                OpenAiSetup.Alias(removal.Role),
                OpenAiSetup.Origin);
        var scope = removal.Scope;
        return new(
            settings.Profile.Id,
            removal.CredentialId,
            removal.Role,
            scope.RouteType,
            scope.ProviderAlias,
            scope.Origin,
            scope.HostId,
            scope.SpkiFingerprint,
            scope.DeviceRole,
            scope.DeviceId);
    }

    public string ScopeDigest()
    {
        Validate();
        var canonical = Encoding.UTF8.GetBytes(string.Join('\n',
            RouteType,
            Role,
            ProviderAlias,
            Origin,
            HostId ?? "",
            SpkiFingerprint ?? "",
            DeviceRole ?? "",
            DeviceId ?? ""));
        return Convert.ToHexStringLower(SHA256.HashData(canonical));
    }
}

public delegate void SecretAction(ReadOnlySpan<char> value);

// No string conversion or serialization surface. Consumers must dispose the lease.
[JsonConverter(typeof(SecretLeaseJsonConverter))]
public sealed class SecretLease : IDisposable
{
    public const int MaximumLength = 1024;
    private char[]? value;

    public SecretLease(ReadOnlySpan<char> secret)
    {
        if (secret.Length is < 1 or > MaximumLength)
            throw new ContractException(ErrorCode.InvalidContract, "Enter a credential token of 1-1024 ASCII characters.");
        foreach (var c in secret)
            if (!char.IsAsciiLetterOrDigit(c) && c is not '-' and not '_' and not '.')
                throw new ContractException(ErrorCode.InvalidContract, "The credential contains unsupported characters. Whitespace and header separators are not accepted.");
        value = secret.ToArray();
    }

    public void Use(SecretAction action)
    {
        ObjectDisposedException.ThrowIf(value is null, this);
        action(value);
    }

    public void Dispose()
    {
        if (value is not null)
            CryptographicOperations.ZeroMemory(MemoryMarshal.AsBytes(value.AsSpan()));
        value = null;
    }

    public override string ToString() => "[credential redacted]";
}

public sealed class SecretLeaseJsonConverter : JsonConverter<SecretLease>
{
    public override SecretLease Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        throw new JsonException("Credentials cannot be imported from JSON; use an explicit OS-vault action.");

    public override void Write(Utf8JsonWriter writer, SecretLease value, JsonSerializerOptions options) =>
        writer.WriteStringValue("[credential redacted]");
}

public sealed record CredentialReadResult(CredentialError Error, [property: JsonIgnore] SecretLease? Secret) : IDisposable
{
    public void Dispose() => Secret?.Dispose();
    public override string ToString() => $"Credential read: {Error}; [credential redacted]";
}

public interface ICredentialStore
{
    CredentialError Write(CredentialBinding binding, SecretLease secret);
    CredentialReadResult Read(CredentialBinding binding);
    CredentialError Delete(CredentialBinding binding);
}

public static class CredentialMessages
{
    public static string Describe(CredentialError error) => error switch
    {
        CredentialError.None => "The requested OS vault action succeeded. Provider/gateway validity, revocation, access, price, quota and readiness remain unknown. Paired devices do not expire.",
        CredentialError.Missing => "The owned credential is missing. Enter the OpenAI key or re-pair the exact gateway route explicitly; restoring settings does not restore a deleted credential.",
        CredentialError.AccessDenied => "Windows denied access to this credential. Check the signed-in user and vault access; do not elevate or disable protection.",
        CredentialError.UnsupportedPlatform => "Windows Credential Manager is unavailable on this platform. Fixture and no-key setup remain usable.",
        CredentialError.InvalidInput => "The credential or origin/host/pin/role binding is invalid. Review the named route and enter or pair it again.",
        _ => "Windows Credential Manager is unavailable. Retry in the intended signed-in Windows session; do not change system protection."
    };
}
