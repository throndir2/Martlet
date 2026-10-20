using Martlet.Core.Settings;
using Martlet.Credentials.Windows;
using Martlet.Discord;

namespace Martlet.Desktop;

/// <summary>The Discord bot token in Windows Credential Manager (Martlet/v3/discord-bot/&lt;id&gt;), for the shared Discord
/// setting (<see cref="DiscordSharing"/>).</summary>
internal sealed class WindowsDiscordTokenVault(WindowsCredentialStore vault) : IDiscordTokenVault
{
    public string? Read(Guid credentialId)
    {
        if (credentialId == Guid.Empty) return null;
        using var read = vault.ReadDiscordBotToken(credentialId);
        if (read.Error != CredentialError.None || read.Secret is null) return null;
        string? token = null;
        read.Secret.Use(value => token = value.ToString());
        return token;
    }

    public bool Write(Guid credentialId, string token)
    {
        using var lease = new SecretLease(token);
        return vault.WriteDiscordBotToken(credentialId, lease) == CredentialError.None;
    }

    public void Delete(Guid credentialId) => vault.DeleteDiscordBotToken(credentialId);
}
