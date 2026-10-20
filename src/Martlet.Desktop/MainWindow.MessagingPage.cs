using System.Windows.Controls;
using Martlet.Messaging;

namespace Martlet.Desktop;

/// <summary>The services MainWindow gives Companion › Messaging (<see cref="MessagingPage"/> in Pages\Messaging). The work the page
/// shows stays in the window (MainWindow.Messaging.cs).</summary>
public partial class MainWindow : IMessagingPageServices
{
    private MessagingPage? messagingPage;

    private MessagingPage MessagingPage => messagingPage ??= new(this);

    MessagingPreferences IMessagingPageServices.Preferences => messaging.Preferences;
    MessagingStatus IMessagingPageServices.Status(MessagingApp app) => messaging.Status(app);
    bool IMessagingPageServices.Running(MessagingApp app) => messaging.Running(app);
    PairingCode? IMessagingPageServices.Pairing(MessagingApp app) => messaging.Pairing(app);
    string? IMessagingPageServices.Cloudflared => messaging.Cloudflared;
    CloudflaredInstall? IMessagingPageServices.OwnCloudflared => messaging.OwnCloudflared;
    string? IMessagingPageServices.TelegramNote => messagingNote;
    string? IMessagingPageServices.WhatsAppNote => whatsAppNote;
    bool IMessagingPageServices.TelegramBusy => messagingBusy;
    bool IMessagingPageServices.WhatsAppBusy => whatsAppBusy;
    (string Token, string Secret, string PhoneId, string AccountId, string Address)? IMessagingPageServices.WhatsAppDraft
    {
        get => whatsAppDraft;
        set => whatsAppDraft = value;
    }

    void IMessagingPageServices.ConnectTelegram(PasswordBox token, Button connect) => ConnectTelegramAsync(token, connect).Forget();
    void IMessagingPageServices.ConnectWhatsApp(PasswordBox token, PasswordBox secret, string phoneId, string accountId, string address) =>
        ConnectWhatsAppAsync(token, secret, phoneId, accountId, address).Forget();
    void IMessagingPageServices.GetCloudflared() => GetCloudflaredAsync().Forget();
    void IMessagingPageServices.SetAnswers(MessagingApp app, ChannelPreferences saved, bool on) => SetMessagingAnswers(app, saved, on);
    void IMessagingPageServices.SetSpeakReplies(MessagingApp app, bool speak) => messaging.SetSpeakReplies(app, speak);
    void IMessagingPageServices.Pair(MessagingApp app) => PairMessagingChat(app);
    void IMessagingPageServices.CancelPairing(MessagingApp app) => messaging.CancelPairing(app);
    void IMessagingPageServices.RemoveChat(MessagingApp app, MessagingChat chat) => RemoveMessagingChat(app, chat);
    void IMessagingPageServices.Disconnect(MessagingApp app) => DisconnectMessaging(app);
    void IMessagingPageServices.OpenInBrowser(Uri address) => OpenInBrowser(address);
}
