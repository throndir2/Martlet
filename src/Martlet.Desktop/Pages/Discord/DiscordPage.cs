using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Martlet.Core.Settings;
using Martlet.Discord;
using Martlet.Presentation;

namespace Martlet.Desktop;

/// <summary>Companion › Discord, the UI only: Now, the connection (on/off and live status), the guided setup of Martlet's own
/// Discord application and bot token, the invite links, the chat modes with per-channel rules and the owner's account and home
/// server (DiscordPage.Chat.cs), Friends and calls (DiscordPage.Friends.cs) and Martlet in your Discord calls
/// (DiscordPage.Calls.cs). What each line says is in <see cref="DiscordPageModel"/>; what the page needs from the app is
/// <see cref="IDiscordPageServices"/>. The token goes straight from the password box to the window, which keeps it in Windows
/// Credential Manager; it is never shown, logged or readable through MCP.</summary>
internal sealed partial class DiscordPage(IDiscordPageServices services)
{
    private sealed record DiscordModeItem(DiscordChatMode Mode)
    {
        public override string ToString() => DiscordSetup.ModeName(Mode);
    }

    private static readonly IReadOnlyList<DiscordModeItem> DiscordModes = [.. Enum.GetValues<DiscordChatMode>().Select(mode => new DiscordModeItem(mode))];
    private static readonly DiscordEntry NoHomeServer = new(0, "None");

    private DiscordService Discord => services.Discord;

    /// <summary>Draws the page's cards into <paramref name="page"/>.</summary>
    internal void Render(Panel page)
    {
        var model = new DiscordPageModel(services);
        page.Children.Add(PageNowCard(model.NowText, null, "DiscordNow", model.ProblemText));
        page.Children.Add(ConnectionCard(model));
        page.Children.Add(SetupCard(model));
        page.Children.Add(InviteCard(model.Saved));
        page.Children.Add(ChatCard(model));
        page.Children.Add(PeopleCard(model));
        page.Children.Add(FriendsCard(model));
        page.Children.Add(CallsCard());
    }

    // ---------- guided setup and token ----------

    private Border SetupCard(DiscordPageModel model)
    {
        var saved = model.Saved;
        var steps = new StackPanel();
        void Step(string text) => steps.Children.Add(Note(text, new Thickness(0, 4, 0, 0)));
        Step("Step 1: open the Discord Developer Portal, choose New Application and name it after your character.");
        steps.Children.Add(Row(PageButton("Open the Developer Portal", () => services.OpenInBrowser(new Uri(DiscordInvite.PortalUrl)), link: true,
            id: "DiscordOpenPortal")));
        Step("Step 2: on Bot, choose Reset Token and paste the token below (Martlet keeps it in Windows Credential Manager).");
        Step("Step 3: still on Bot, under Privileged Gateway Intents, turn on Message Content Intent and save.");
        Step("Step 4: on Installation, allow both Guild Install and User Install.");
        Step("Step 5: back here, use Add to a server for each server (and your home server), and Add to my account to use /martlet in DMs and group DMs.");
        if (saved.ApplicationId != 0)
            steps.Children.Add(Row(PageButton("Open my bot's page", () => services.OpenInBrowser(new Uri(DiscordInvite.ApplicationPage(saved.ApplicationId))),
                link: true, id: "DiscordOpenBotPage")));

        var token = new PasswordBox { MaxLength = SecretLease.MaximumLength, Width = 420, HorizontalAlignment = HorizontalAlignment.Left };
        AutomationProperties.SetName(token, "Discord bot token");
        AutomationProperties.SetAutomationId(token, "DiscordToken");
        // Once a token is saved, the "Token saved" mark fills the empty box instead.
        if (!saved.Configured) InputHint.SetText(token, "Paste the bot token from the Developer Portal");
        var savedMark = new TextBlock
        {
            Text = "••••••••  Token saved (type to replace)", IsHitTestVisible = false, VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(12, 0, 0, 0), Opacity = 0.7,
            Visibility = saved.Configured ? Visibility.Visible : Visibility.Collapsed
        };
        savedMark.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
        token.PasswordChanged += (_, _) =>
        {
            using var entered = token.SecurePassword;
            savedMark.Visibility = saved.Configured && entered.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        };
        Button? save = null;
        save = PageButton(saved.Configured ? "Replace token" : "Save and connect", () => services.SaveToken(token, save!),
            primary: !saved.Configured, id: "DiscordTokenSave");
        save.IsEnabled = !services.Busy;
        var forget = saved.Configured ? PageButton("Forget the bot", services.Forget, id: "DiscordForget") : null;

        var children = new List<UIElement>
        {
            Heading("Set up Martlet's Discord bot"),
            HelpTip.Explain("Martlet joins Discord as its own bot, which you make once in Discord's Developer Portal. " +
                "Discord has no way to make the bot for you, and Martlet never logs in as a person.", new Thickness(0, 0, 0, 6),
                "DiscordSetup", "setting up the bot", wiki: "Discord#set-up-the-bot"),
            Status(DiscordSetup.Describe(DiscordSetup.Next(saved, model.Status)), "DiscordSetupNext", 15),
            MainWindow.DetailExpander("Step by step", "DiscordSetupSteps", steps, expanded: !saved.Configured),
            new Label { Content = "Bot _token", Target = token, Padding = new Thickness(0, 12, 0, 4) },
            new Grid { Width = 420, HorizontalAlignment = HorizontalAlignment.Left, Children = { token, savedMark } },
            Status(model.ConfiguredText, "DiscordConfigured")
        };
        if (services.TokenStatus is { } result) children.Add(Status(result, "DiscordTokenStatus"));
        children.Add(Row(save, forget));
        children.Add(HelpTip.Explain("Saving keeps the token in Windows Credential Manager and connects. " +
            "Martlet first checks that it looks like a bot token. Forget the bot disconnects and removes the token, but keeps your chat modes and people.",
            new Thickness(0, 2, 0, 0), "DiscordTokenSaving", "the bot token", wiki: "Discord#set-up-the-bot"));
        return Card([.. children]);
    }

    // ---------- connection ----------

    /// <summary>Discord's main choice: whether Martlet connects its bot to Discord whenever it runs (with an explicit Off), then
    /// the connection's live state.</summary>
    private Border ConnectionCard(DiscordPageModel model)
    {
        var saved = model.Saved;
        var status = model.Status;
        var choices = MainWindow.OnOffChoices("DiscordEnabled", "DiscordEnabledOff", "Connect Martlet to Discord whenever Martlet runs",
            "Martlet's bot joins your servers and DMs and chats as the chat modes below say.",
            "Martlet doesn't connect to Discord, but keeps your bot token, chat modes and people.", saved.Enabled && saved.Configured,
            model.EnableBlocked, on => { if (on != Discord.Preferences.Enabled) services.SetEnabled(on); });
        choices[1].IsEnabled = !services.Busy;
        var reconnect = PageButton("Reconnect", () => services.SetEnabled(true), id: "DiscordReconnect");
        reconnect.IsEnabled = saved.Configured && saved.Enabled && !services.Busy;

        var children = new List<UIElement>
        {
            Heading("Connection"),
            choices[0],
            choices[1],
            Status(DiscordSetup.StatusLine(status), "DiscordState", 15),
            Status(model.EnabledText, "DiscordEnabledStatus"),
            Status(model.BotText, "DiscordBotName"),
            Status(model.ServersText, "DiscordServers")
        };
        if (status.Problem is { } problem)
        {
            var line = Status("Problem: " + problem, "DiscordProblem");
            line.Margin = new Thickness(0, 6, 0, 0);
            children.Add(line);
            if (DiscordSetup.IsIntentProblem(problem) && saved.ApplicationId != 0)
                children.Add(Row(PageButton("Open my bot's page", () => services.OpenInBrowser(new Uri(DiscordInvite.ApplicationPage(saved.ApplicationId))),
                    link: true, id: "DiscordFixIntent")));
        }
        children.Add(Status(Discord.TextStatusLine, "DiscordTextStatus"));
        children.Add(Status(Discord.VoiceSummary, "DiscordVoiceStatus"));
        children.Add(Row(reconnect));
        return Card([.. children]);
    }
}
