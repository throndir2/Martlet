using System.Windows.Controls;
using Martlet.Messaging;

namespace Martlet.Desktop;

/// <summary>Everything Companion › Messaging (<see cref="MessagingPage"/> and <see cref="MessagingPageModel"/>) reads and starts in
/// the app. MainWindow gives it to the page (MainWindow.MessagingPage.cs) and keeps the work the page shows (rule 4 of
/// Pages\README.md): connecting Telegram and WhatsApp, downloading cloudflared, pairing, removing chats and disconnecting
/// (MainWindow.Messaging.cs). The secret boxes go to the window as they are (<see cref="PasswordBox"/>), so a token never
/// becomes a string on the way.</summary>
internal interface IMessagingPageServices
{
    // ---------- what the page reads ----------

    /// <summary>The saved messaging setup (messaging.json).</summary>
    MessagingPreferences Preferences { get; }

    MessagingStatus Status(MessagingApp app);

    /// <summary>Whether the app's bridge runs on this PC now.</summary>
    bool Running(MessagingApp app);

    /// <summary>The pairing code that works now for the app, or null.</summary>
    PairingCode? Pairing(MessagingApp app);

    /// <summary>The cloudflared.exe Martlet would run, or null; and Martlet's own downloaded copy.</summary>
    string? Cloudflared { get; }

    CloudflaredInstall? OwnCloudflared { get; }

    /// <summary>The last outcome of a Telegram or WhatsApp connect (never a secret), or null.</summary>
    string? TelegramNote { get; }

    string? WhatsAppNote { get; }

    /// <summary>Whether a Telegram connect, or a WhatsApp connect or cloudflared download, runs now.</summary>
    bool TelegramBusy { get; }

    bool WhatsAppBusy { get; }

    /// <summary>What was typed in the WhatsApp card and isn't saved yet, kept while the page draws again.</summary>
    (string Token, string Secret, string PhoneId, string AccountId, string Address)? WhatsAppDraft { get; set; }

    // ---------- what the page starts ----------

    void ConnectTelegram(PasswordBox token, Button connect);

    void ConnectWhatsApp(PasswordBox token, PasswordBox secret, string phoneId, string accountId, string address);

    void GetCloudflared();

    /// <summary>The page's main choice: whether Martlet answers the app on this PC.</summary>
    void SetAnswers(MessagingApp app, ChannelPreferences saved, bool on);

    void SetSpeakReplies(MessagingApp app, bool speak);

    void Pair(MessagingApp app);

    void CancelPairing(MessagingApp app);

    void RemoveChat(MessagingApp app, MessagingChat chat);

    void Disconnect(MessagingApp app);

    void OpenInBrowser(Uri address);
}
