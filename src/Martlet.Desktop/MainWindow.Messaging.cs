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
    private static readonly Uri MetaNewApp = new("https://developers.facebook.com/apps/creation/");
    private static readonly Uri MetaApps = new("https://developers.facebook.com/apps/");
    private static readonly Uri MetaSystemUsers = new("https://business.facebook.com/latest/settings/system_users");
    private static readonly Uri WhatsAppGuide = new("https://developers.facebook.com/docs/whatsapp/cloud-api/get-started");
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

    private void RenderMessagingTab(Panel page)
    {
        var saved = messaging.Preferences;
        page.Children.Add(MessagingNowCard(saved));
        page.Children.Add(MessagingChoiceCard(saved));
        page.Children.Add(TelegramCard(saved.Telegram, messaging.Status(MessagingApp.Telegram), saved.Telegram.Connected));
        if (saved.Telegram.Connected) page.Children.Add(ChatsCard(MessagingApp.Telegram, saved.Telegram));
        page.Children.Add(WhatsAppCard(saved.WhatsApp, messaging.Status(MessagingApp.WhatsApp)));
        if (saved.WhatsApp.Connected) page.Children.Add(ChatsCard(MessagingApp.WhatsApp, saved.WhatsApp));
        page.Children.Add(Card(
            Heading("How it works"),
            HelpTip.Explain("Messages travel through Telegram's or WhatsApp's servers to Martlet on this PC, which asks the Thinking model you chose and " +
                "sends the reply back. Martlet answers only the chats you paired here, and only while it runs on this PC (also while Windows is " +
                "locked). Replies are text and join the same conversation as the talk window.", new Thickness(0, 0, 0, 0), "Messaging", "messaging",
                wiki: "Telegram#how-it-works")));
    }

    private Border TelegramCard(TelegramPreferences saved, MessagingStatus status, bool connected)
    {
        var token = new PasswordBox { MaxLength = 200, Width = 420, HorizontalAlignment = HorizontalAlignment.Left };
        AutomationProperties.SetName(token, "Telegram bot token");
        AutomationProperties.SetAutomationId(token, "MessagingTelegramToken");
        // Once connected, the "Token saved" mark fills the empty box instead.
        if (!connected) InputHint.SetText(token, "Paste the token from BotFather (123456789:AAH…)");
        var tokenField = SecretField(token, connected, "Token saved (type to replace)");

        Button? connect = null;
        connect = PageButton(messagingBusy ? "Checking..." : connected ? "Use this token" : "Connect", () => ConnectTelegramAsync(token, connect!).Forget(),
            primary: !connected, id: "MessagingTelegramConnect");
        connect.IsEnabled = !messagingBusy;
        var botFather = PageButton("Open BotFather", () => OpenInBrowser(new Uri("https://t.me/BotFather")), link: true, id: "MessagingOpenBotFather");
        var disconnect = connected ? PageButton("Disconnect", () => DisconnectMessaging(MessagingApp.Telegram), id: "MessagingTelegramDisconnect") : null;

        var children = new List<UIElement>
        {
            Heading("Telegram"),
            Status(MessagingStatusText(saved, status, messaging.Running(MessagingApp.Telegram)), "MessagingStatus", 15)
        };
        if (messagingNote is { } note) children.Add(Status(note, "MessagingNote"));
        if (!connected)
            children.Add(HelpTip.Explain("Make a bot with BotFather in Telegram, paste its token here and press Connect. " +
                "Then pair your chat with the code Martlet shows.", new Thickness(0, 8, 0, 0), "TelegramSetup", "setting up Telegram",
                wiki: "Telegram#set-up-telegram"));
        children.Add(new Label { Content = "Bot _token", Target = token, Padding = new Thickness(0, 10, 0, 4) });
        children.Add(tokenField);
        children.Add(Note("Martlet checks the token with Telegram, keeps it in Windows Credential Manager on this PC and sends it only to Telegram.",
            new Thickness(0, 4, 0, 0)));
        children.Add(Row(connect, botFather, disconnect));
        children.Add(SpeakRepliesCheck(MessagingApp.Telegram, saved, "MessagingTelegram"));
        return Card([.. children]);
    }

    private Border WhatsAppCard(WhatsAppPreferences saved, MessagingStatus status)
    {
        var connected = saved.Connected;
        var draft = whatsAppDraft ?? ("", "", saved.PhoneNumberId, saved.BusinessAccountId, saved.PublicAddress);
        var token = new PasswordBox { MaxLength = 700, Width = 420, HorizontalAlignment = HorizontalAlignment.Left, Password = draft.Token };
        AutomationProperties.SetName(token, "WhatsApp access token");
        AutomationProperties.SetAutomationId(token, "MessagingWhatsAppToken");
        var secret = new PasswordBox { MaxLength = 64, Width = 420, HorizontalAlignment = HorizontalAlignment.Left, Password = draft.Secret };
        AutomationProperties.SetName(secret, "Meta app secret");
        AutomationProperties.SetAutomationId(secret, "MessagingWhatsAppSecret");
        TextBox Field(string value, string name, string id)
        {
            var box = new TextBox { Text = value, MaxLength = 200, Width = 420, HorizontalAlignment = HorizontalAlignment.Left };
            AutomationProperties.SetName(box, name);
            AutomationProperties.SetAutomationId(box, id);
            return box;
        }
        var phone = Field(draft.PhoneId, "Phone number ID", "MessagingWhatsAppPhoneId");
        var account = Field(draft.AccountId, "WhatsApp Business account ID", "MessagingWhatsAppAccountId");
        var address = Field(draft.Address, "Public address", "MessagingWhatsAppAddress");
        // Once connected, the "saved" marks fill the empty secret boxes instead.
        if (!connected)
        {
            InputHint.SetText(token, "Paste your Meta access token (EA…)");
            InputHint.SetText(secret, "Paste the App secret from App settings › Basic");
        }
        InputHint.SetText(phone, "106540352242922");
        InputHint.SetText(account, "102290129340398");
        InputHint.SetText(address, "https://martlet.example.com");
        void Keep() => whatsAppDraft = (token.Password, secret.Password, phone.Text, account.Text, address.Text);
        token.PasswordChanged += (_, _) => Keep();
        secret.PasswordChanged += (_, _) => Keep();
        phone.TextChanged += (_, _) => Keep();
        account.TextChanged += (_, _) => Keep();
        address.TextChanged += (_, _) => Keep();

        Button? connect = null;
        connect = PageButton(whatsAppBusy ? "Working..." : connected ? "Save and reconnect" : "Connect",
            () => ConnectWhatsAppAsync(token, secret, phone, account, address).Forget(), primary: !connected, id: "MessagingWhatsAppConnect");
        connect.IsEnabled = !whatsAppBusy;
        var disconnect = connected ? PageButton("Disconnect", () => DisconnectMessaging(MessagingApp.WhatsApp), id: "MessagingWhatsAppDisconnect") : null;

        var children = new List<UIElement>
        {
            Heading("WhatsApp"),
            Status(MessagingStatusText(saved, status, messaging.Running(MessagingApp.WhatsApp)), "MessagingWhatsAppStatus", 15)
        };
        if (whatsAppNote is { } note) children.Add(Status(note, "MessagingWhatsAppNote"));
        if (!connected)
        {
            children.Add(HelpTip.Explain(WhatsAppSteps, new Thickness(0, 8, 0, 0), "WhatsAppSetup", "setting up WhatsApp",
                wiki: "Telegram#set-up-whatsapp"));
            children.Add(Row(
                PageButton("Create a Meta app", () => OpenInBrowser(MetaNewApp), link: true, id: "MessagingWhatsAppOpenMeta"),
                PageButton("Your Meta apps", () => OpenInBrowser(MetaApps), link: true, id: "MessagingWhatsAppOpenApps"),
                PageButton("System users", () => OpenInBrowser(MetaSystemUsers), link: true, id: "MessagingWhatsAppOpenSystemUsers"),
                PageButton("Meta's WhatsApp guide", () => OpenInBrowser(WhatsAppGuide), link: true, id: "MessagingWhatsAppOpenGuide")));
        }
        children.Add(new Label { Content = "_Access token", Target = token, Padding = new Thickness(0, 10, 0, 4) });
        children.Add(SecretField(token, connected, "Token saved (type to replace)"));
        children.Add(new Label { Content = "App _secret", Target = secret, Padding = new Thickness(0, 10, 0, 4) });
        children.Add(SecretField(secret, connected, "Secret saved (type to replace)"));
        children.Add(new Label { Content = "_Phone number ID (optional)", Target = phone, Padding = new Thickness(0, 10, 0, 4) });
        children.Add(phone);
        children.Add(new Label { Content = "WhatsApp _Business account ID (optional)", Target = account, Padding = new Thickness(0, 10, 0, 4) });
        children.Add(account);
        children.Add(Note("Leave the IDs empty and Martlet finds them from the token (the first number it reaches).", new Thickness(0, 4, 0, 0)));

        var cloudflared = messaging.Cloudflared;
        children.Add(new Label { Content = "Public a_ddress (optional)", Target = address, Padding = new Thickness(0, 10, 0, 4) });
        children.Add(address);
        children.Add(Status(WhatsAppAddressText(saved, cloudflared, messaging.OwnCloudflared), "MessagingWhatsAppTunnel"));
        children.Add(Row(
            cloudflared is null ? PageButton(whatsAppBusy ? "Downloading..." : "Get cloudflared", () => GetCloudflaredAsync().Forget(), id: "MessagingWhatsAppGetCloudflared") : null,
            PageButton("About quick tunnels", () => OpenInBrowser(CloudflareQuickTunnel.About), link: true, id: "MessagingWhatsAppOpenTunnelHelp")));
        children.Add(HelpTip.Explain("Martlet checks the token and secret with Meta, keeps them in Windows Credential Manager on this PC and sends them only " +
            "to Meta. Messages Meta delivers are checked against the app secret, so nobody else can post to Martlet's address.", new Thickness(0, 4, 0, 0),
            "WhatsAppSecrets", "the WhatsApp secrets", wiki: "Telegram#set-up-whatsapp"));
        children.Add(Row(connect, disconnect));
        children.Add(SpeakRepliesCheck(MessagingApp.WhatsApp, saved, "MessagingWhatsApp"));
        return Card([.. children]);
    }

    /// <summary>WhatsApp's setup in short; the numbered steps are in the wiki (docs/wiki/Telegram.md, "Set up WhatsApp").</summary>
    internal const string WhatsAppSteps =
        "Make a Meta app with WhatsApp, then paste its access token and app secret here and press Connect. " +
        "Meta gives you a free test number, Martlet finds it and sets up the webhook itself, and then you pair your chat with a code.";

    /// <summary>Where Meta delivers WhatsApp messages: the owner's own address or a Cloudflare quick tunnel (and whether
    /// cloudflared is here).</summary>
    internal static string WhatsAppAddressText(WhatsAppPreferences saved, string? cloudflared, CloudflaredInstall? own = null)
    {
        var port = saved.Port > 0 ? $"http://localhost:{saved.Port}" : "Martlet's local port (shown once connected)";
        if (saved.PublicAddress.Length > 0)
            return $"Meta delivers messages to {saved.PublicAddress}, which must forward to {port} (with Host: localhost).";
        if (cloudflared is null)
            return "Leave the address empty and Martlet opens a free Cloudflare quick tunnel (no account, port or router change), but it needs Cloudflare's " +
                (own is { Verified: false }
                    ? "cloudflared, and Martlet doesn't run its copy because it doesn't match the SHA-256 checked at download (or is from an older Martlet): " +
                      "Get cloudflared downloads and checks the official build again (about 60 MB)."
                    : "cloudflared, which isn't on this PC yet: Get cloudflared downloads the official build from GitHub (about 60 MB) and checks its SHA-256.");
        if (Martlet.Messaging.CloudflaredSignature.Problem(cloudflared) is { } unsigned)
            return $"Martlet doesn't run {cloudflared} ({unsigned}), so Get cloudflared downloads Cloudflare's signed build.";
        var which = own is { Verified: true } && string.Equals(own.Path, cloudflared, StringComparison.OrdinalIgnoreCase)
            ? $"cloudflared {own.Version}, SHA-256 and Cloudflare's signature checked ({cloudflared})"
            : $"cloudflared, Cloudflare's signature checked ({cloudflared})";
        return $"Leave the address empty and Martlet opens a free Cloudflare quick tunnel to {port} with {which}; " +
            "use your own https address only if you already have one that forwards there.";
    }

    private static Grid SecretField(PasswordBox box, bool saved, string text)
    {
        var mark = new TextBlock
        {
            Text = "••••••••  " + text, IsHitTestVisible = false, VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(12, 0, 0, 0), Opacity = 0.7, Visibility = saved && box.Password.Length == 0 ? Visibility.Visible : Visibility.Collapsed
        };
        mark.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
        box.PasswordChanged += (_, _) => mark.Visibility = saved && box.Password.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        return new Grid { Width = 420, HorizontalAlignment = HorizontalAlignment.Left, Children = { box, mark } };
    }

    /// <summary>Whether replies to the app's messages are also said aloud on this PC. Whether Martlet answers the app at all is the
    /// page's main choice (<see cref="MessagingChoiceCard"/>).</summary>
    private CheckBox SpeakRepliesCheck(MessagingApp app, ChannelPreferences saved, string prefix)
    {
        var speak = new CheckBox
        {
            IsChecked = saved.SpeakReplies, IsEnabled = saved.Connected, Margin = new Thickness(0, 12, 0, 4),
            Content = new TextBlock { TextWrapping = TextWrapping.Wrap, Text = "Also say replies aloud on this PC (never while Windows is locked)" }
        };
        AutomationProperties.SetAutomationId(speak, prefix + "Speak");
        speak.Click += (_, _) => messaging.SetSpeakReplies(app, speak.IsChecked == true);
        return speak;
    }

    /// <summary>Companion › Messaging's status line: whether Martlet answers the app on this PC now, or why not.</summary>
    internal static string MessagingStatusText(ChannelPreferences saved, MessagingStatus status, bool running)
    {
        if (!saved.Connected)
            return saved is WhatsAppPreferences
                ? "Not connected: follow the steps below to give Martlet a WhatsApp number."
                : "Not connected: make a Telegram bot and paste its token below.";
        var handle = saved.Handle;
        if (!saved.Enabled) return $"Connected to {handle}, but turned off on this PC.";
        var chats = saved.Chats.Count == 0 ? "no chats paired yet" : saved.Chats.Count == 1 ? "1 paired chat" : $"{saved.Chats.Count} paired chats";
        var last = status.LastAnswered is { } at ? $", last answered at {at.ToLocalTime():t}" : "";
        return status.State switch
        {
            MessagingState.Running => $"Answering {handle} on this PC ({chats}{last}).",
            MessagingState.Connecting when saved is WhatsAppPreferences => $"Connecting {handle}: opening its public address and pointing the webhook at it...",
            MessagingState.Connecting => $"Connecting to {handle}...",
            MessagingState.Retrying => $"Can't reach {handle} right now ({status.Problem?.TrimEnd('.')}), but Martlet keeps trying.",
            MessagingState.Failed => $"{handle} stopped: {status.Problem}",
            _ when !running => $"{handle} isn't running on this PC; it starts with Martlet on your companion PC.",
            _ => $"Starting {handle}..."
        };
    }

    private Border ChatsCard(MessagingApp app, ChannelPreferences saved)
    {
        var name = MessagingService.Name(app);
        var prefix = app == MessagingApp.WhatsApp ? "MessagingWhatsApp" : "Messaging";
        var status = messaging.Status(app);
        var children = new List<UIElement>
        {
            Heading($"Paired {name} chats"),
            Status(saved.Chats.Count == 0 ? "No chats paired yet: pair yours so Martlet answers it." :
                $"Martlet answers {(saved.Chats.Count == 1 ? "this chat" : $"these {saved.Chats.Count} chats")}:", prefix + "Chats")
        };
        var index = 0;
        foreach (var chat in saved.Chats)
        {
            var n = index++;
            var line = Status(chat.Name, $"{prefix}Chat-{n}");
            line.Margin = new Thickness(0, 6, 0, 0);
            children.Add(line);
            children.Add(PageButton("Remove", () => RemoveMessagingChat(app, chat), link: true, id: $"{prefix}ChatRemove-{n}"));
        }
        if (messaging.Pairing(app) is { } code)
        {
            var link = saved is WhatsAppPreferences whatsApp
                ? new Uri($"https://wa.me/{whatsApp.Digits}?text={code.Code}")
                : new Uri($"https://t.me/{((TelegramPreferences)saved).BotUsername}?start={code.Code}");
            var codeText = new TextBlock { Text = code.Code, FontSize = 28, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 10, 0, 0) };
            AutomationProperties.SetAutomationId(codeText, prefix + "PairCode");
            children.Add(codeText);
            children.Add(Status($"Send this code to {saved.Handle} in {name} before {code.Expires.ToLocalTime():t}, or open the link on the " +
                $"phone you use {name} on (it works once).", prefix + "PairStatus"));
            children.Add(Row(PageButton($"Open in {name}", () => OpenInBrowser(link), link: true, id: prefix + "PairOpen"),
                PageButton("Cancel", () => messaging.CancelPairing(app), link: true, id: prefix + "PairCancel")));
        }
        else
        {
            var pair = PageButton("Pair a chat", () => PairMessagingChat(app), primary: saved.Chats.Count == 0, id: prefix + "Pair");
            pair.IsEnabled = status.State == MessagingState.Running;
            children.Add(Row(pair));
            if (status.State != MessagingState.Running)
                children.Add(Note($"Pairing works while Martlet answers {name} on this PC.", new Thickness(0, 0, 0, 0)));
        }
        return Card([.. children]);
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

    private async Task ConnectWhatsAppAsync(PasswordBox tokenBox, PasswordBox secretBox, TextBox phone, TextBox account, TextBox address)
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
        if (address.Text.Trim().Length == 0 && messaging.Cloudflared is null && !await GetCloudflaredAsync()) return;
        whatsAppBusy = true;
        whatsAppNote = "Checking the token and app secret with Meta...";
        RenderTab();
        try
        {
            var found = await messaging.ConnectWhatsAppAsync(token, secret, phone.Text, account.Text, address.Text, lifetime.Token);
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
