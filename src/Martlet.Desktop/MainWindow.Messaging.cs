using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Martlet.Messaging;
using Martlet.Presentation;

namespace Martlet.Desktop;

/// <summary>Companion › Messaging: Martlet in messaging apps. Telegram: the owner makes a bot with BotFather and pastes its
/// token. WhatsApp: the owner makes a Meta app with the WhatsApp product and pastes its access token and app secret; Martlet
/// finds the number, opens a public address (Cloudflare quick tunnel) and registers the webhook itself. Either way the owner
/// pairs their own chat with a code, and Martlet answers that chat's text messages in the same conversation as the talk window
/// (memory, personality, history) while it runs on this PC, even while Windows is locked. Nothing but the paired one-to-one
/// chats is answered. Secrets are kept in Windows Credential Manager.</summary>
public partial class MainWindow
{
    private string? messagingNote;
    private string? whatsAppNote;
    private bool messagingBusy;
    private bool whatsAppBusy;
    private (string Token, string Secret, string PhoneId, string AccountId, string Address)? whatsAppDraft;

    private void MessagingChanged()
    {
        if (closing || openTab != CompanionTab.Messaging) return;
        if (System.Windows.Input.Keyboard.FocusedElement is PasswordBox or System.Windows.Controls.Primitives.TextBoxBase) return;
        RenderTab();
    }

    /// <summary>One message from a paired chat: it goes into the running conversation (started hidden when there is none) as
    /// typed text and comes back as the reply's text.</summary>
    private async Task<string> AnswerMessageAsync(MessagingApp app, InboundMessage message, CancellationToken token)
    {
        var name = MessagingService.Name(app);
        var asked = await Dispatcher.InvokeAsync(() =>
        {
            if (closing) return Task.FromResult("Martlet is closing, so try again once it runs again.");
            if (Role == DeviceRole.Host) return Task.FromResult("This PC is a Martlet host now, so Martlet doesn't talk on it.");
            if (ConversationSession() is not { } talk) return Task.FromResult("Martlet is busy on your PC right now, so try again in a moment.");
            if (!talk.IsVisible) talk.StartInBackground();
            ErrorLog.Info($"{name}: a paired chat sent a message; Martlet answers it in the conversation.");
            var origin = new Martlet.Conversation.HistorySource(app.ToString().ToLowerInvariant(), message.ChatId, null, message.ChatName,
                message.MessageId is { } id ? [id] : null);
            var reply = talk.AskFromMessage(message.Text ?? "", $"{message.ChatName} ({name})", messaging.Preferences[app].SpeakReplies, token,
                origin, message.ChatName);
            RenderConversationButton();
            UpdateTray();
            return reply;
        });
        return await asked;
    }

    private async Task ConnectTelegramAsync(PasswordBox box, Button connect)
    {
        if (closing || messagingBusy) return;
        var token = box.Password;
        box.Clear();
        if (token.Trim().Length == 0)
        {
            messagingNote = "Paste the bot token from BotFather first.";
            RenderTab();
            return;
        }
        if (Role == DeviceRole.Host)
        {
            ActionText.Text = HostHasNoCompanionText;
            return;
        }
        messagingBusy = true;
        connect.IsEnabled = false;
        messagingNote = "Checking the token with Telegram...";
        RenderTab();
        try
        {
            var bot = await messaging.ConnectTelegramAsync(token, lifetime.Token);
            messagingNote = null;
            ActionText.Text = $"Connected to @{bot.Username}: pair your chat so Martlet answers it.";
            ErrorLog.Info($"Telegram: connected to @{bot.Username}.");
        }
        catch (Exception error) when (error is MessagingException or ArgumentException or InvalidOperationException)
        {
            messagingNote = error.Message;
        }
        catch (OperationCanceledException) { return; }
        finally
        {
            messagingBusy = false;
            if (!closing) RenderTab();
        }
    }

    private async Task ConnectWhatsAppAsync(PasswordBox tokenBox, PasswordBox secretBox, string phone, string account, string address)
    {
        if (closing || whatsAppBusy) return;
        if (Role == DeviceRole.Host)
        {
            ActionText.Text = HostHasNoCompanionText;
            return;
        }
        var saved = messaging.Preferences.WhatsApp;
        var kept = saved.Connected ? messaging.SavedWhatsAppSecrets() : null;
        var token = tokenBox.Password.Trim().Length > 0 ? tokenBox.Password : kept?.AccessToken ?? "";
        var secret = secretBox.Password.Trim().Length > 0 ? secretBox.Password : kept?.AppSecret ?? "";
        if (token.Trim().Length == 0 || secret.Trim().Length == 0)
        {
            whatsAppNote = "Paste the access token and the app secret first.";
            RenderTab();
            return;
        }
        if (address.Trim().Length == 0 && messaging.Cloudflared is null && !await GetCloudflaredAsync()) return;
        whatsAppBusy = true;
        whatsAppNote = "Checking the token and app secret with Meta...";
        RenderTab();
        try
        {
            var found = await messaging.ConnectWhatsAppAsync(token, secret, phone, account, address, lifetime.Token);
            whatsAppDraft = null;
            whatsAppNote = null;
            ActionText.Text = $"Connected to {found.Number} ({found.Name}): pair your chat so Martlet answers it.";
            ErrorLog.Info("WhatsApp: connected a business number.");
        }
        catch (Exception error) when (error is MessagingException or ArgumentException or InvalidOperationException)
        {
            whatsAppNote = error.Message;
        }
        catch (OperationCanceledException) { return; }
        finally
        {
            whatsAppBusy = false;
            if (!closing) RenderTab();
        }
    }

    /// <summary>Downloads Cloudflare's cloudflared (with the owner's OK) into Martlet's tools folder.</summary>
    private async Task<bool> GetCloudflaredAsync()
    {
        if (closing || whatsAppBusy) return false;
        if (messaging.ToolsDirectory is not { } tools) return false;
        if (!ConfirmationDialog.Confirm(this, "Download Cloudflare's official cloudflared program, which opens a public web address on the internet " +
                "where WhatsApp delivers messages to Martlet?\n\nMartlet downloads it from " +
                $"{CloudflareQuickTunnel.Download.Host} (about 60 MB) into {tools}, runs it only while it answers WhatsApp, and Cloudflare's " +
                "terms apply.", "Martlet - messaging", "Download", "Not now"))
            return false;
        whatsAppBusy = true;
        whatsAppNote = "Downloading cloudflared...";
        RenderTab();
        string? problem = null;
        BackgroundTask? task = null;
        try
        {
            // A background task (Background tasks lists it); this page shows its progress too.
            var done = await HostRunWindow.RunAsync(this, "Download cloudflared", async run =>
            {
                task = run.BackgroundTask;
                run.Status(whatsAppNote);
                run.Output.Report($"Downloading cloudflared from {CloudflareQuickTunnel.Download.Host} (about 60 MB) into {tools}.");
                var progress = new Progress<double>(fraction =>
                {
                    whatsAppNote = $"Downloading cloudflared... {fraction:P0}";
                    run.Status(whatsAppNote);
                    if (!closing && openTab == CompanionTab.Messaging && System.Windows.Input.Keyboard.FocusedElement is not (PasswordBox or System.Windows.Controls.Primitives.TextBoxBase)) RenderTab();
                });
                using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token, run.Token);
                try
                {
                    var path = await CloudflareQuickTunnel.DownloadAsync(tools, progress, cancellation.Token);
                    run.Output.Report($"Saved {path} (cloudflared {CloudflareQuickTunnel.Installed(tools)?.Version}; size and SHA-256 match GitHub's release).");
                }
                catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { throw; }
                catch (Exception error) when (error is System.Net.Http.HttpRequestException or IOException or UnauthorizedAccessException or
                    InvalidDataException or OperationCanceledException)
                {
                    problem = error.Message;
                    throw new InvalidOperationException(error.Message, error);
                }
                return "cloudflared is ready.";
            });
            if (done is not null)
            {
                whatsAppNote = "cloudflared is ready.";
                ErrorLog.Info("WhatsApp: downloaded cloudflared.");
                return true;
            }
            whatsAppNote = task?.State == BackgroundTaskState.Canceled ? "The cloudflared download was canceled."
                : $"Couldn't download cloudflared ({(problem ?? task?.Status)?.TrimEnd('.')}): install it yourself (winget install Cloudflare.cloudflared) and try again.";
            return false;
        }
        finally
        {
            whatsAppBusy = false;
            if (!closing) RenderTab();
        }
    }

    private void DisconnectMessaging(MessagingApp app)
    {
        var saved = messaging.Preferences[app];
        var question = app == MessagingApp.WhatsApp
            ? $"Disconnect {saved.Handle}, remove its access token and app secret from this PC and forget its paired chats (your Meta app and number stay)?"
            : $"Disconnect {saved.Handle}, remove its token from this PC and forget its paired chats (the bot itself stays in Telegram)?";
        if (!ConfirmationDialog.Confirm(this, question, "Martlet - messaging")) return;
        messaging.Disconnect(app);
        if (app == MessagingApp.WhatsApp)
        {
            whatsAppNote = null;
            whatsAppDraft = null;
        }
        else messagingNote = null;
        ActionText.Text = $"{MessagingService.Name(app)} disconnected.";
        ErrorLog.Info($"{MessagingService.Name(app)}: disconnected.");
        RenderTab();
    }

    private void PairMessagingChat(MessagingApp app)
    {
        if (messaging.StartPairing(app) is null) ActionText.Text = $"Pairing works while Martlet answers {MessagingService.Name(app)} on this PC.";
        RenderTab();
    }

    private void RemoveMessagingChat(MessagingApp app, MessagingChat chat)
    {
        if (!ConfirmationDialog.Confirm(this, $"Stop answering {chat.Name} (the chat can pair again with a new code)?", "Martlet - messaging"))
            return;
        messaging.RemoveChat(app, chat.Id);
        RenderTab();
    }
}
