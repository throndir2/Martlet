using System.Security.Cryptography;
using System.Runtime.InteropServices;
using System.Text.Json.Serialization;
using System.Text.Json;
using Martlet.Core.Contracts;

namespace Martlet.Core.Settings;

public enum CredentialError { None, Missing, AccessDenied, UnsupportedPlatform, InvalidInput, Unavailable }

public sealed record CredentialBinding(Guid ProfileId, Guid CredentialId, SetupRole Role, string ProviderAlias, string Origin)
{
    public void Validate()
    {
        ContractRules.Require(ProfileId != Guid.Empty && CredentialId != Guid.Empty, "A credential requires an owned profile and fresh reference.");
        ContractRules.Require(ProviderAlias == OpenAiSetup.Alias(Role) && Origin == OpenAiSetup.Origin,
            "Credential use is limited to the selected OpenAI origin and role.");
    }

    public static CredentialBinding For(AppSettings settings, SetupRole role, Guid id) =>
        new(settings.Profile.Id, id, role, OpenAiSetup.Alias(role), OpenAiSetup.Origin);
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
            throw new ContractException(ErrorCode.InvalidContract, "Enter an API key of 1-1024 ASCII token characters.");
        foreach (var c in secret)
            if (!char.IsAsciiLetterOrDigit(c) && c is not '-' and not '_' and not '.')
                throw new ContractException(ErrorCode.InvalidContract, "The key contains unsupported characters. Whitespace and header separators are not accepted.");
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
        CredentialError.None => "The requested OS vault action succeeded. API validity, access, price and quota remain unknown.",
        CredentialError.Missing => "The owned credential is missing. Enter a key explicitly; restoring settings does not restore a deleted key.",
        CredentialError.AccessDenied => "Windows denied access to this credential. Check the signed-in user and vault access; do not elevate or disable protection.",
        CredentialError.UnsupportedPlatform => "Windows Credential Manager is unavailable on this platform. Fixture and no-key setup remain usable.",
        CredentialError.InvalidInput => "The credential or origin/role binding is invalid. Review the named route and paste a supported key.",
        _ => "Windows Credential Manager is unavailable. Retry in the intended signed-in Windows session; do not change system protection."
    };
}
