using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Martlet.Messaging;
using Martlet.Presentation;

namespace Martlet.Desktop;

/// <summary>Companion › Messaging, the UI only: Now, the main choice (which apps Martlet answers on this PC), the Telegram card,
/// the WhatsApp card (MessagingPage.WhatsApp.cs), each connected app's paired chats and How it works. What each line says is in
/// <see cref="MessagingPageModel"/>; what the page needs from the app is <see cref="IMessagingPageServices"/>. Martlet answers only
/// the chats paired here, and the secrets stay in Windows Credential Manager.</summary>
internal sealed partial class MessagingPage(IMessagingPageServices services)
{
    private static readonly Uri MetaNewApp = new("https://developers.facebook.com/apps/creation/");
    private static readonly Uri MetaApps = new("https://developers.facebook.com/apps/");
    private static readonly Uri MetaSystemUsers = new("https://business.facebook.com/latest/settings/system_users");
    private static readonly Uri WhatsAppGuide = new("https://developers.facebook.com/docs/whatsapp/cloud-api/get-started");

    /// <summary>Draws the page's cards into <paramref name="page"/>.</summary>
    internal void Render(Panel page)
    {
        var model = new MessagingPageModel(services);
        var saved = model.Saved;
        page.Children.Add(PageNowCard(model.NowText, null, "MessagingNow", model.ProblemText));
        page.Children.Add(ChoiceCard(saved));
        page.Children.Add(TelegramCard(model, saved.Telegram));
        if (saved.Telegram.Connected) page.Children.Add(ChatsCard(MessagingApp.Telegram, saved.Telegram));
        page.Children.Add(WhatsAppCard(model, saved.WhatsApp));
        if (saved.WhatsApp.Connected) page.Children.Add(ChatsCard(MessagingApp.WhatsApp, saved.WhatsApp));
        page.Children.Add(Card(
            Heading("How it works"),
            HelpTip.Explain("Messages travel through Telegram's or WhatsApp's servers to Martlet on this PC, which asks the Thinking model you chose and " +
                "sends the reply back. Martlet answers only the chats you paired here, and only while it runs on this PC (also while Windows is " +
                "locked). Replies are text and join the same conversation as the talk window.", new Thickness(0, 0, 0, 0), "Messaging", "messaging",
                wiki: "Telegram#how-it-works")));
    }

    /// <summary>The main choice: for each app, whether Martlet answers it on this PC (On) or not (Off). An app that isn't set up
    /// yet can only be Off.</summary>
    private Border ChoiceCard(MessagingPreferences saved)
    {
        var stack = new List<UIElement>
        {
            Heading("Answer messages"),
            Note("Choose which apps Martlet answers on this PC; set an app up below before you turn it on.", new Thickness(0, 0, 0, 4))
        };
        foreach (var app in MessagingPageModel.AppsInOrder)
        {
            var name = MessagingService.Name(app);
            var prefs = saved[app];
            var prefix = app == MessagingApp.WhatsApp ? "MessagingWhatsApp" : "MessagingTelegram";
            var on = prefs.Enabled && prefs.Connected;
            var title = new TextBlock { Text = name, FontWeight = FontWeights.SemiBold, Width = 110, VerticalAlignment = VerticalAlignment.Center };
            var yes = new RadioButton { Content = "On", GroupName = prefix + "Answer", IsChecked = on, IsEnabled = prefs.Connected,
                Margin = new Thickness(0, 0, 18, 0), VerticalAlignment = VerticalAlignment.Center };
            AutomationProperties.SetName(yes, $"{name}: On, Martlet answers {(prefs.Connected ? prefs.Handle : name)} on this PC");
            AutomationProperties.SetAutomationId(yes, prefix + "On");
            var no = new RadioButton { Content = "Off", GroupName = prefix + "Answer", IsChecked = !on, VerticalAlignment = VerticalAlignment.Center };
            AutomationProperties.SetName(no, $"{name}: Off, Martlet doesn't answer {name} on this PC");
            AutomationProperties.SetAutomationId(no, prefix + "Off");
            yes.Checked += (_, _) => services.SetAnswers(app, prefs, true);
            no.Checked += (_, _) => services.SetAnswers(app, prefs, false);
            var row = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
            row.Children.Add(title);
            row.Children.Add(yes);
            row.Children.Add(no);
            stack.Add(row);
            stack.Add(Note(prefs.Connected ? $"{prefs.Handle}." : $"Not set up yet: set it up under {name} below.", new Thickness(110, 2, 0, 0)));
        }
        return Card([.. stack]);
    }

    private Border TelegramCard(MessagingPageModel model, TelegramPreferences saved)
    {
        var connected = saved.Connected;
        var token = new PasswordBox { MaxLength = 200, Width = 420, HorizontalAlignment = HorizontalAlignment.Left };
        AutomationProperties.SetName(token, "Telegram bot token");
        AutomationProperties.SetAutomationId(token, "MessagingTelegramToken");
        // Once connected, the "Token saved" mark fills the empty box instead.
        if (!connected) InputHint.SetText(token, "Paste the token from BotFather (123456789:AAH…)");
        var tokenField = SecretField(token, connected, "Token saved (type to replace)");

        Button? connect = null;
        connect = PageButton(services.TelegramBusy ? "Checking..." : connected ? "Use this token" : "Connect",
            () => services.ConnectTelegram(token, connect!), primary: !connected, id: "MessagingTelegramConnect");
        connect.IsEnabled = !services.TelegramBusy;
        var botFather = PageButton("Open BotFather", () => services.OpenInBrowser(new Uri("https://t.me/BotFather")), link: true, id: "MessagingOpenBotFather");
        var disconnect = connected ? PageButton("Disconnect", () => services.Disconnect(MessagingApp.Telegram), id: "MessagingTelegramDisconnect") : null;

        var children = new List<UIElement>
        {
            Heading("Telegram"),
            Status(model.StatusLine(MessagingApp.Telegram), "MessagingStatus", 15)
        };
        if (services.TelegramNote is { } note) children.Add(Status(note, "MessagingNote"));
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

    /// <summary>A secret box with a "saved" mark that fills it while it is empty and a secret is saved.</summary>
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
    /// page's main choice (<see cref="ChoiceCard"/>).</summary>
    private CheckBox SpeakRepliesCheck(MessagingApp app, ChannelPreferences saved, string prefix)
    {
        var speak = new CheckBox
        {
            IsChecked = saved.SpeakReplies, IsEnabled = saved.Connected, Margin = new Thickness(0, 12, 0, 4),
            Content = new TextBlock { TextWrapping = TextWrapping.Wrap, Text = "Also say replies aloud on this PC (never while Windows is locked)" }
        };
        AutomationProperties.SetAutomationId(speak, prefix + "Speak");
        speak.Click += (_, _) => services.SetSpeakReplies(app, speak.IsChecked == true);
        return speak;
    }

    private Border ChatsCard(MessagingApp app, ChannelPreferences saved)
    {
        var name = MessagingService.Name(app);
        var prefix = MessagingPageModel.Prefix(app);
        var status = services.Status(app);
        var children = new List<UIElement>
        {
            Heading($"Paired {name} chats"),
            Status(MessagingPageModel.ChatsText(saved), prefix + "Chats")
        };
        var index = 0;
        foreach (var chat in saved.Chats)
        {
            var n = index++;
            var line = Status(chat.Name, $"{prefix}Chat-{n}");
            line.Margin = new Thickness(0, 6, 0, 0);
            children.Add(line);
            children.Add(PageButton("Remove", () => services.RemoveChat(app, chat), link: true, id: $"{prefix}ChatRemove-{n}"));
        }
        if (services.Pairing(app) is { } code)
        {
            var link = MessagingPageModel.PairingLink(saved, code);
            var codeText = new TextBlock { Text = code.Code, FontSize = 28, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 10, 0, 0) };
            AutomationProperties.SetAutomationId(codeText, prefix + "PairCode");
            children.Add(codeText);
            children.Add(Status($"Send this code to {saved.Handle} in {name} before {code.Expires.ToLocalTime():t}, or open the link on the " +
                $"phone you use {name} on (it works once).", prefix + "PairStatus"));
            children.Add(Row(PageButton($"Open in {name}", () => services.OpenInBrowser(link), link: true, id: prefix + "PairOpen"),
                PageButton("Cancel", () => services.CancelPairing(app), link: true, id: prefix + "PairCancel")));
        }
        else
        {
            var pair = PageButton("Pair a chat", () => services.Pair(app), primary: saved.Chats.Count == 0, id: prefix + "Pair");
            pair.IsEnabled = status.State == MessagingState.Running;
            children.Add(Row(pair));
            if (status.State != MessagingState.Running)
                children.Add(Note($"Pairing works while Martlet answers {name} on this PC.", new Thickness(0, 0, 0, 0)));
        }
        return Card([.. children]);
    }
}
