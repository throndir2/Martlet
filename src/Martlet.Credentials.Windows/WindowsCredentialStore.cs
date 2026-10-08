using Martlet.Core.Contracts;
using Martlet.Core.Settings;

namespace Martlet.Credentials.Windows;

public interface ICredentialNative
{
    bool IsSupported { get; }
    int Write(string target, ReadOnlySpan<char> secret);
    int Read(string target, out SecretLease? secret);
    int Delete(string target);
    /// <summary>When the credential was last written, or null when it is missing or unreadable.</summary>
    DateTimeOffset? WrittenAt(string target) => null;
}

public sealed class WindowsCredentialStore(ICredentialNative native) : ICredentialStore, ICredentialTimes
{
    public WindowsCredentialStore() : this((ICredentialNative?)LabCredentialNative.FromEnvironment() ?? new WindowsCredentialNative()) { }

    public DateTimeOffset? WrittenAt(CredentialBinding binding) =>
        Valid(binding) && native.IsSupported ? native.WrittenAt(Target(binding)) : null;

    public CredentialError Write(CredentialBinding binding, SecretLease secret)
    {
        if (!Valid(binding)) return CredentialError.InvalidInput;
        if (!native.IsSupported) return CredentialError.UnsupportedPlatform;
        var error = CredentialError.InvalidInput;
        secret.Use(value => error = Map(native.Write(Target(binding), value)));
        return error;
    }

    public CredentialReadResult Read(CredentialBinding binding)
    {
        if (!Valid(binding)) return new(CredentialError.InvalidInput, null);
        if (!native.IsSupported) return new(CredentialError.UnsupportedPlatform, null);
        var result = Map(native.Read(Target(binding), out var secret));
        if (result != CredentialError.None) { secret?.Dispose(); return new(result, null); }
        return secret is null ? new(CredentialError.Unavailable, null) : new(result, secret);
    }

    public CredentialError Delete(CredentialBinding binding)
    {
        if (!Valid(binding)) return CredentialError.InvalidInput;
        return native.IsSupported ? Map(native.Delete(Target(binding))) : CredentialError.UnsupportedPlatform;
    }

    private static bool Valid(CredentialBinding binding)
    {
        try { binding.Validate(); return true; }
        catch (ContractException) { return false; }
    }

    private static string Target(CredentialBinding binding) =>
        binding.RouteType == SetupRouteType.OpenAi
            ? $"Martlet/v2/{binding.ProfileId:N}/api.openai.com/{binding.ProviderAlias}/{binding.CredentialId:N}"
            : binding.RouteType == SetupRouteType.ChatCompletions
                ? $"Martlet/v3/{binding.ProfileId:N}/chat-completions/{binding.ScopeDigest()}/{binding.CredentialId:N}"
            : binding.RouteType == SetupRouteType.ElevenLabs
                ? $"Martlet/v3/{binding.ProfileId:N}/api.elevenlabs.io/{binding.ScopeDigest()}/{binding.CredentialId:N}"
                : $"Martlet/v3/{binding.ProfileId:N}/gateway/{binding.RouteType}/{binding.ScopeDigest()}/{binding.CredentialId:N}";

    private static CredentialError Map(int error) => error switch
    {
        0 => CredentialError.None, 1168 => CredentialError.Missing, 5 => CredentialError.AccessDenied,
        87 or 13 => CredentialError.InvalidInput, _ => CredentialError.Unavailable
    };

    // Device secret for a paired Martlet host that relays Audio2Face; scoped to the exact host and credential.
    public CredentialError WriteAvatarHostSecret(string hostId, string credentialId, SecretLease secret)
    {
        if (!AvatarHostScope(hostId, credentialId)) return CredentialError.InvalidInput;
        if (!native.IsSupported) return CredentialError.UnsupportedPlatform;
        var error = CredentialError.InvalidInput;
        secret.Use(value => error = Map(native.Write(AvatarHostTarget(hostId, credentialId), value)));
        return error;
    }

    public CredentialReadResult ReadAvatarHostSecret(string hostId, string credentialId)
    {
        if (!AvatarHostScope(hostId, credentialId)) return new(CredentialError.InvalidInput, null);
        if (!native.IsSupported) return new(CredentialError.UnsupportedPlatform, null);
        var result = Map(native.Read(AvatarHostTarget(hostId, credentialId), out var secret));
        if (result != CredentialError.None) { secret?.Dispose(); return new(result, null); }
        return secret is null ? new(CredentialError.Unavailable, null) : new(result, secret);
    }

    public CredentialError DeleteAvatarHostSecret(string hostId, string credentialId) =>
        !AvatarHostScope(hostId, credentialId) ? CredentialError.InvalidInput
            : native.IsSupported ? Map(native.Delete(AvatarHostTarget(hostId, credentialId))) : CredentialError.UnsupportedPlatform;

    private static bool AvatarHostScope(string hostId, string credentialId) =>
        hostId is { Length: > 0 and <= 64 } && hostId.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-') &&
        credentialId is { Length: 22 } && credentialId.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');

    private static string AvatarHostTarget(string hostId, string credentialId) =>
        $"Martlet/v3/avatar-host/{hostId}/{credentialId}";

    // sudo password for an SSH account on a Martlet host (user@computer:port), remembered only when the owner asks.
    public CredentialError WriteSshSudoSecret(string account, SecretLease secret)
    {
        if (!SshAccount(account)) return CredentialError.InvalidInput;
        if (!native.IsSupported) return CredentialError.UnsupportedPlatform;
        var error = CredentialError.InvalidInput;
        secret.Use(value => error = Map(native.Write(SshSudoTarget(account), value)));
        return error;
    }

    public CredentialReadResult ReadSshSudoSecret(string account)
    {
        if (!SshAccount(account)) return new(CredentialError.InvalidInput, null);
        if (!native.IsSupported) return new(CredentialError.UnsupportedPlatform, null);
        var result = Map(native.Read(SshSudoTarget(account), out var secret));
        if (result != CredentialError.None) { secret?.Dispose(); return new(result, null); }
        return secret is null ? new(CredentialError.Unavailable, null) : new(result, secret);
    }

    public CredentialError DeleteSshSudoSecret(string account) =>
        !SshAccount(account) ? CredentialError.InvalidInput
            : native.IsSupported ? Map(native.Delete(SshSudoTarget(account))) : CredentialError.UnsupportedPlatform;

    private static bool SshAccount(string account) =>
        account is { Length: > 0 and <= 330 } && account.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-' or '@' or ':');

    private static string SshSudoTarget(string account) => $"Martlet/v3/ssh-sudo/{account}";

    // Home Assistant long-lived access token for the smart home connection; one per saved connection.
    public CredentialError WriteHomeAssistantToken(Guid credentialId, SecretLease secret)
    {
        if (credentialId == Guid.Empty) return CredentialError.InvalidInput;
        if (!native.IsSupported) return CredentialError.UnsupportedPlatform;
        var error = CredentialError.InvalidInput;
        secret.Use(value => error = Map(native.Write(HomeAssistantTarget(credentialId), value)));
        return error;
    }

    public CredentialReadResult ReadHomeAssistantToken(Guid credentialId)
    {
        if (credentialId == Guid.Empty) return new(CredentialError.InvalidInput, null);
        if (!native.IsSupported) return new(CredentialError.UnsupportedPlatform, null);
        var result = Map(native.Read(HomeAssistantTarget(credentialId), out var secret));
        if (result != CredentialError.None) { secret?.Dispose(); return new(result, null); }
        return secret is null ? new(CredentialError.Unavailable, null) : new(result, secret);
    }

    public CredentialError DeleteHomeAssistantToken(Guid credentialId) =>
        credentialId == Guid.Empty ? CredentialError.InvalidInput
            : native.IsSupported ? Map(native.Delete(HomeAssistantTarget(credentialId))) : CredentialError.UnsupportedPlatform;

    private static string HomeAssistantTarget(Guid credentialId) => $"Martlet/v3/home-assistant/{credentialId:N}";

    // Discord bot token for Martlet's own Discord app; one per saved setup.
    public CredentialError WriteDiscordBotToken(Guid credentialId, SecretLease secret)
    {
        if (credentialId == Guid.Empty) return CredentialError.InvalidInput;
        if (!native.IsSupported) return CredentialError.UnsupportedPlatform;
        var error = CredentialError.InvalidInput;
        secret.Use(value => error = Map(native.Write(DiscordTarget(credentialId), value)));
        return error;
    }

    public CredentialReadResult ReadDiscordBotToken(Guid credentialId)
    {
        if (credentialId == Guid.Empty) return new(CredentialError.InvalidInput, null);
        if (!native.IsSupported) return new(CredentialError.UnsupportedPlatform, null);
        var result = Map(native.Read(DiscordTarget(credentialId), out var secret));
        if (result != CredentialError.None) { secret?.Dispose(); return new(result, null); }
        return secret is null ? new(CredentialError.Unavailable, null) : new(result, secret);
    }

    public CredentialError DeleteDiscordBotToken(Guid credentialId) =>
        credentialId == Guid.Empty ? CredentialError.InvalidInput
            : native.IsSupported ? Map(native.Delete(DiscordTarget(credentialId))) : CredentialError.UnsupportedPlatform;

    private static string DiscordTarget(Guid credentialId) => $"Martlet/v3/discord-bot/{credentialId:N}";

    // A value an MCP server in mcp.json needs (an API key for its environment or headers), referenced there as ${secret:NAME}.
    // The scope is one mcp.json file; the value is kept base64url-encoded so any text fits the credential format.
    public const int MaxMcpSecretBytes = SecretLease.MaximumLength / 4 * 3;

    public CredentialError WriteMcpSecret(string scope, string name, string value)
    {
        if (!McpSecretScope(scope, name) || value.Length == 0 || System.Text.Encoding.UTF8.GetByteCount(value) > MaxMcpSecretBytes)
            return CredentialError.InvalidInput;
        if (!native.IsSupported) return CredentialError.UnsupportedPlatform;
        var bytes = System.Text.Encoding.UTF8.GetBytes(value);
        var encoded = System.Buffers.Text.Base64Url.EncodeToChars(bytes);
        try { return Map(native.Write(McpSecretTarget(scope, name), encoded)); }
        finally
        {
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(bytes);
            Array.Clear(encoded);
        }
    }

    public CredentialError ReadMcpSecret(string scope, string name, out string? value)
    {
        value = null;
        if (!McpSecretScope(scope, name)) return CredentialError.InvalidInput;
        if (!native.IsSupported) return CredentialError.UnsupportedPlatform;
        var result = Map(native.Read(McpSecretTarget(scope, name), out var secret));
        using (secret)
        {
            if (result != CredentialError.None) return result;
            if (secret is null) return CredentialError.Unavailable;
            string? decoded = null;
            secret.Use(chars =>
            {
                try { decoded = System.Text.Encoding.UTF8.GetString(System.Buffers.Text.Base64Url.DecodeFromChars(chars)); }
                catch (FormatException) { decoded = null; }
            });
            value = decoded;
            return decoded is null ? CredentialError.Unavailable : CredentialError.None;
        }
    }

    public CredentialError DeleteMcpSecret(string scope, string name) =>
        !McpSecretScope(scope, name) ? CredentialError.InvalidInput
            : native.IsSupported ? Map(native.Delete(McpSecretTarget(scope, name))) : CredentialError.UnsupportedPlatform;

    private static bool McpSecretScope(string scope, string name) =>
        scope is { Length: 16 } && scope.All(char.IsAsciiHexDigitLower) &&
        name is { Length: > 0 and <= 128 } && name.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-');

    private static string McpSecretTarget(string scope, string name) => $"Martlet/v3/mcp/{scope}/{name}";

    // A messaging app's bot token (Companion › Messaging), one per saved connection. Kept base64url-encoded like MCP secrets,
    // because bot tokens contain characters (':') a credential lease doesn't take.
    public CredentialError WriteMessagingToken(string app, Guid credentialId, string value)
    {
        if (!MessagingApp(app) || credentialId == Guid.Empty || value.Length == 0 ||
            System.Text.Encoding.UTF8.GetByteCount(value) > MaxMcpSecretBytes)
            return CredentialError.InvalidInput;
        if (!native.IsSupported) return CredentialError.UnsupportedPlatform;
        var bytes = System.Text.Encoding.UTF8.GetBytes(value);
        var encoded = System.Buffers.Text.Base64Url.EncodeToChars(bytes);
        try { return Map(native.Write(MessagingTarget(app, credentialId), encoded)); }
        finally
        {
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(bytes);
            Array.Clear(encoded);
        }
    }

    public CredentialError ReadMessagingToken(string app, Guid credentialId, out string? value)
    {
        value = null;
        if (!MessagingApp(app) || credentialId == Guid.Empty) return CredentialError.InvalidInput;
        if (!native.IsSupported) return CredentialError.UnsupportedPlatform;
        var result = Map(native.Read(MessagingTarget(app, credentialId), out var secret));
        using (secret)
        {
            if (result != CredentialError.None) return result;
            if (secret is null) return CredentialError.Unavailable;
            string? decoded = null;
            secret.Use(chars =>
            {
                try { decoded = System.Text.Encoding.UTF8.GetString(System.Buffers.Text.Base64Url.DecodeFromChars(chars)); }
                catch (FormatException) { decoded = null; }
            });
            value = decoded;
            return decoded is null ? CredentialError.Unavailable : CredentialError.None;
        }
    }

    public CredentialError DeleteMessagingToken(string app, Guid credentialId) =>
        !MessagingApp(app) || credentialId == Guid.Empty ? CredentialError.InvalidInput
            : native.IsSupported ? Map(native.Delete(MessagingTarget(app, credentialId))) : CredentialError.UnsupportedPlatform;

    private static bool MessagingApp(string app) => app is { Length: > 0 and <= 32 } && app.All(char.IsAsciiLetterLower);

    private static string MessagingTarget(string app, Guid credentialId) => $"Martlet/v3/messaging/{app}/{credentialId:N}";
}
