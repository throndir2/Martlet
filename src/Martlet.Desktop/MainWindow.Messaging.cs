using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Martlet.Messaging;

namespace Martlet.Desktop;

/// <summary>Companion › Messaging: Martlet in messaging apps. Telegram is the first: the owner makes a bot with BotFather,
/// pastes its token (kept in Windows Credential Manager), pairs their own chat with a code, and Martlet answers that chat's
/// text messages in the same conversation as the talk window (memory, personality, history) while it runs on this PC, even
/// while Windows is locked. Nothing but the paired one-to-one chats is answered.</summary>
public partial class MainWindow
{
    private string? messagingNote;
    private bool messagingBusy;

    private void MessagingChanged()
    {
        if (closing || openTab != CompanionTab.Messaging) return;
        if (System.Windows.Input.Keyboard.FocusedElement is PasswordBox) return;
        RenderTab();
    }

    /// <summary>One message from a paired chat: it goes into the running conversation (started hidden when there is none) as
    /// typed text and comes back as the reply's text.</summary>
    private async Task<string> AnswerMessageAsync(InboundMessage message, CancellationToken token)
    {
        var asked = await Dispatcher.InvokeAsync(() =>
        {
            if (closing) return Task.FromResult("Martlet is closing. Try again once it runs again.");
            if (Role == DeviceRole.Host) return Task.FromResult("This PC is a Martlet host now, so Martlet doesn't talk on it.");
            if (ConversationSession() is not { } talk) return Task.FromResult("Martlet is busy on your PC right now. Try again in a moment.");
            if (!talk.IsVisible) talk.StartInBackground();
            ErrorLog.Info("Telegram: a paired chat sent a message; Martlet answers it in the conversation.");
            var reply = talk.AskFromMessage(message.Text ?? "", $"{message.ChatName} (Telegram)", messaging.Preferences.Telegram.SpeakReplies, token);
            RenderConversationButton();
            UpdateTray();
            return reply;
        });
        return await asked;
    }

    private void RenderMessagingTab(Panel page)
    {
        var saved = messaging.Preferences.Telegram;
        var status = messaging.Status;
        var connected = saved.BotUsername.Length > 0;
        page.Children.Add(TelegramCard(saved, status, connected));
        if (connected) page.Children.Add(ChatsCard(saved, status));
        page.Children.Add(Card(
            Heading("How it works"),
            Note("Messages travel through Telegram's servers to Martlet on this PC, which asks the Thinking model you chose and sends the reply " +
                "back. Martlet answers only the chats you paired here, never groups or strangers, and only while it runs on this PC (also while " +
                "Windows is locked). Replies are text and join the same conversation as the talk window, so Martlet remembers what you said in " +
                "either. Only one program can read a bot's messages, so use each bot on one PC. Martlet reads text messages only for now; a tool " +
                "or smart-home action that asks first waits for you at this PC.", new Thickness(0, 0, 0, 0))));
    }

    private Border TelegramCard(TelegramPreferences saved, MessagingStatus status, bool connected)
    {
        var token = new PasswordBox { MaxLength = 200, Width = 420, HorizontalAlignment = HorizontalAlignment.Left };
        AutomationProperties.SetName(token, "Telegram bot token");
        AutomationProperties.SetAutomationId(token, "MessagingTelegramToken");
        var savedMark = new TextBlock
        {
            Text = "••••••••  Token saved (type to replace)", IsHitTestVisible = false, VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(12, 0, 0, 0), Opacity = 0.7, Visibility = connected ? Visibility.Visible : Visibility.Collapsed
        };
        savedMark.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
        token.PasswordChanged += (_, _) => savedMark.Visibility = connected && token.Password.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        var tokenField = new Grid { Width = 420, HorizontalAlignment = HorizontalAlignment.Left, Children = { token, savedMark } };

        Button? connect = null;
        connect = PageButton(messagingBusy ? "Checking..." : connected ? "Use this token" : "Connect", () => ConnectTelegramAsync(token, connect!).Forget(),
            primary: !connected, id: "MessagingTelegramConnect");
        connect.IsEnabled = !messagingBusy;
        var botFather = PageButton("Open BotFather", () => OpenInBrowser(new Uri("https://t.me/BotFather")), link: true, id: "MessagingOpenBotFather");
        var disconnect = connected ? PageButton("Disconnect", DisconnectTelegram, id: "MessagingTelegramDisconnect") : null;

        var on = new CheckBox
        {
            IsChecked = saved.Enabled && connected, IsEnabled = connected, Margin = new Thickness(0, 12, 0, 4),
            Content = new TextBlock { TextWrapping = TextWrapping.Wrap, Text = "Answer Telegram messages on this PC" }
        };
        AutomationProperties.SetAutomationId(on, "MessagingTelegramOn");
        on.Click += (_, _) =>
        {
            if (Role == DeviceRole.Host && on.IsChecked == true)
            {
                ActionText.Text = HostHasNoCompanionText;
                on.IsChecked = false;
                return;
            }
            messaging.SetEnabled(on.IsChecked == true);
            ActionText.Text = on.IsChecked == true ? $"Martlet answers @{saved.BotUsername} on this PC." : "Martlet stopped answering Telegram on this PC.";
        };
        var speak = new CheckBox
        {
            IsChecked = saved.SpeakReplies, IsEnabled = connected, Margin = new Thickness(0, 0, 0, 4),
            Content = new TextBlock { TextWrapping = TextWrapping.Wrap, Text = "Also say replies aloud on this PC (never while Windows is locked)" }
        };
        AutomationProperties.SetAutomationId(speak, "MessagingTelegramSpeak");
        speak.Click += (_, _) => messaging.SetSpeakReplies(speak.IsChecked == true);

        var children = new List<UIElement>
        {
            Heading("Telegram"),
            Status(TelegramStatusText(saved, status, connected, messaging.Running), "MessagingStatus", 15)
        };
        if (messagingNote is { } note) children.Add(Status(note, "MessagingNote"));
        if (!connected)
            children.Add(Note("1. In Telegram, open BotFather, send /newbot and pick a name for your Martlet bot.\n" +
                "2. Paste the token BotFather gives you here and press Connect.\n3. Pair your chat with the code Martlet shows.",
                new Thickness(0, 8, 0, 0)));
        children.Add(new Label { Content = "Bot _token", Target = token, Padding = new Thickness(0, 10, 0, 4) });
        children.Add(tokenField);
        children.Add(Note("Martlet checks the token with Telegram, keeps it in Windows Credential Manager on this PC and sends it only to Telegram.",
            new Thickness(0, 4, 0, 0)));
        children.Add(Row(connect, botFather, disconnect));
        children.Add(on);
        children.Add(speak);
        return Card([.. children]);
    }

    /// <summary>Companion › Messaging's status line: whether Martlet answers the bot on this PC now, or why not.</summary>
    internal static string TelegramStatusText(TelegramPreferences saved, MessagingStatus status, bool connected, bool running)
    {
        if (!connected) return "Not connected. Make a Telegram bot and paste its token below.";
        var bot = $"@{saved.BotUsername}";
        if (!saved.Enabled) return $"Connected to {bot}, but turned off on this PC.";
        var chats = saved.Chats.Count == 0 ? "no chats paired yet" : saved.Chats.Count == 1 ? "1 paired chat" : $"{saved.Chats.Count} paired chats";
        var last = status.LastAnswered is { } at ? $" Last answered at {at.ToLocalTime():t}." : "";
        return status.State switch
        {
            MessagingState.Running => $"Answering {bot} on this PC ({chats}).{last}",
            MessagingState.Connecting => $"Connecting to {bot}...",
            MessagingState.Retrying => $"Can't reach {bot} right now: {status.Problem} Martlet keeps trying.",
            MessagingState.Failed => $"{bot} stopped: {status.Problem}",
            _ when !running => $"{bot} isn't running on this PC. It starts with Martlet on your companion PC.",
            _ => $"Starting {bot}..."
        };
    }

    private Border ChatsCard(TelegramPreferences saved, MessagingStatus status)
    {
        var children = new List<UIElement>
        {
            Heading("Paired chats"),
            Status(saved.Chats.Count == 0 ? "No chats paired yet. Pair yours so Martlet answers it." :
                $"Martlet answers {(saved.Chats.Count == 1 ? "this chat" : $"these {saved.Chats.Count} chats")}:", "MessagingChats")
        };
        var index = 0;
        foreach (var chat in saved.Chats)
        {
            var n = index++;
            var line = Status(chat.Name, $"MessagingChat-{n}");
            line.Margin = new Thickness(0, 6, 0, 0);
            children.Add(line);
            children.Add(PageButton("Remove", () => RemoveTelegramChat(chat), link: true, id: $"MessagingChatRemove-{n}"));
        }
        var pairing = messaging.Pairing;
        if (pairing is { } code)
        {
            var link = new Uri($"https://t.me/{saved.BotUsername}?start={code.Code}");
            var codeText = new TextBlock { Text = code.Code, FontSize = 28, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 10, 0, 0) };
            AutomationProperties.SetAutomationId(codeText, "MessagingPairCode");
            children.Add(codeText);
            children.Add(Status($"Send this code to @{saved.BotUsername} in Telegram before {code.Expires.ToLocalTime():t}, or open the link on the " +
                "phone you use Telegram on. It works once.", "MessagingPairStatus"));
            children.Add(Row(PageButton("Open in Telegram", () => OpenInBrowser(link), link: true, id: "MessagingPairOpen"),
                PageButton("Cancel", () => messaging.CancelPairing(), link: true, id: "MessagingPairCancel")));
        }
        else
        {
            var pair = PageButton("Pair a chat", PairTelegramChat, primary: saved.Chats.Count == 0, id: "MessagingPair");
            pair.IsEnabled = status.State == MessagingState.Running;
            children.Add(Row(pair));
            if (status.State != MessagingState.Running)
                children.Add(Note("Pairing works while Martlet answers the bot on this PC.", new Thickness(0, 0, 0, 0)));
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
            ActionText.Text = $"Connected to @{bot.Username}. Pair your chat so Martlet answers it.";
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

    private void DisconnectTelegram()
    {
        var bot = messaging.Preferences.Telegram.BotUsername;
        if (!ConfirmationDialog.Confirm(this, $"Disconnect @{bot}?\n\nMartlet stops answering it, removes its token from this PC and forgets the paired " +
                "chats. The bot itself stays in Telegram; delete it with BotFather if you no longer need it.", "Martlet - messaging"))
            return;
        messaging.Disconnect();
        messagingNote = null;
        ActionText.Text = "Telegram disconnected.";
        ErrorLog.Info("Telegram: disconnected.");
        RenderTab();
    }

    private void PairTelegramChat()
    {
        if (messaging.StartPairing() is null) ActionText.Text = "Pairing works while Martlet answers the bot on this PC.";
        RenderTab();
    }

    private void RemoveTelegramChat(MessagingChat chat)
    {
        if (!ConfirmationDialog.Confirm(this, $"Stop answering {chat.Name}?\n\nThe chat can pair again with a new code.", "Martlet - messaging"))
            return;
        messaging.RemoveChat(chat.Id);
        RenderTab();
    }
}
