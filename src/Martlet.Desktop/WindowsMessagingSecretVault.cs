using Martlet.Core.Settings;
using Martlet.Credentials.Windows;
using Martlet.Messaging;

namespace Martlet.Desktop;

/// <summary>A messaging app's secrets in Windows Credential Manager (Martlet/v3/messaging/&lt;app&gt;/&lt;id&gt;), for the shared
/// messaging setting (<see cref="MessagingSharing"/>).</summary>
internal sealed class WindowsMessagingSecretVault(WindowsCredentialStore vault) : IMessagingSecretVault
{
    private static string Key(MessagingApp app) => app == MessagingApp.WhatsApp ? MessagingService.WhatsAppKey : MessagingService.TelegramKey;

    public string? Read(MessagingApp app, Guid credentialId) =>
        credentialId != Guid.Empty && vault.ReadMessagingToken(Key(app), credentialId, out var secret) == CredentialError.None ? secret : null;

    public bool Write(MessagingApp app, Guid credentialId, string secret) =>
        vault.WriteMessagingToken(Key(app), credentialId, secret) == CredentialError.None;

    public void Delete(MessagingApp app, Guid credentialId) => vault.DeleteMessagingToken(Key(app), credentialId);
}
