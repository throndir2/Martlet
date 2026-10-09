using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Martlet.Core.Contracts;
using Martlet.Core.Settings;
using Martlet.Discord;

namespace Martlet.Desktop;

/// <summary>The Companion page's Discord tab: the guided setup of Martlet's own Discord application and bot token, the
/// connection (on/off and live status), the invite links, the chat modes with per-channel rules, and the owner's account and
/// home server. The token goes straight from the password box into Windows Credential Manager (<see cref="DiscordService"/>);
/// it is never shown, logged or readable through MCP.</summary>
public partial class MainWindow
{
    private sealed record DiscordModeItem(DiscordChatMode Mode)
    {
        public override string ToString() => DiscordSetup.ModeName(Mode);
    }

    private static readonly IReadOnlyList<DiscordModeItem> DiscordModes = [.. Enum.GetValues<DiscordChatMode>().Select(mode => new DiscordModeItem(mode))];
    private static readonly DiscordEntry NoHomeServer = new(0, "None");

    private string? discordTokenStatus;
    private string? discordOwnerDraft;
    private bool discordWatched;
    private bool discordBusy;

    private void RenderDiscordTab(Panel page)
    {
        if (!discordWatched)
        {
            discordWatched = true;
            discord.Changed += () => Dispatcher.BeginInvoke(() =>
            {
                if (DiscordCanRender()) RenderTab();
            });
        }
        var saved = discord.Preferences;
        var status = discord.Status;
        page.Children.Add(DiscordNowCard(saved, status));
        page.Children.Add(DiscordConnectionCard(saved, status));
        page.Children.Add(DiscordSetupCard(saved, status));
        page.Children.Add(DiscordInviteCard(saved));
        page.Children.Add(DiscordChatCard(saved, status));
        page.Children.Add(DiscordPeopleCard(saved, status));
        page.Children.Add(DiscordFriendsCard());
        page.Children.Add(DiscordCallsCard());
    }

    // ---------- guided setup and token ----------

    private Border DiscordSetupCard(DiscordPreferences saved, DiscordBotStatus status)
    {
        var steps = new StackPanel();
        void Step(string text) => steps.Children.Add(Note(text, new Thickness(0, 4, 0, 0)));
        Step("1. Open the Discord Developer Portal and choose New Application. Name it after your character.");
        steps.Children.Add(Row(PageButton("Open the Developer Portal", () => OpenInBrowser(new Uri(DiscordInvite.PortalUrl)), link: true,
            id: "DiscordOpenPortal")));
        Step("2. On Bot, choose Reset Token, copy the token and paste it below. Martlet keeps it in Windows Credential Manager.");
        Step("3. Still on Bot, under Privileged Gateway Intents, turn on Message Content Intent and save.");
        Step("4. On Installation, allow both Guild Install and User Install.");
        Step("5. Back here, use Add to a server for each server (and your home server), and Add to my account to use /martlet in DMs and group DMs.");
        if (saved.ApplicationId != 0)
            steps.Children.Add(Row(PageButton("Open my bot's page", () => OpenInBrowser(new Uri(DiscordInvite.ApplicationPage(saved.ApplicationId))),
                link: true, id: "DiscordOpenBotPage")));

        var token = new PasswordBox { MaxLength = SecretLease.MaximumLength, Width = 420, HorizontalAlignment = HorizontalAlignment.Left };
        AutomationProperties.SetName(token, "Discord bot token");
        AutomationProperties.SetAutomationId(token, "DiscordToken");
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
        save = PageButton(saved.Configured ? "Replace token" : "Save and connect", () => SaveDiscordTokenAsync(token, save!).Forget(),
            primary: !saved.Configured, id: "DiscordTokenSave");
        save.IsEnabled = !discordBusy;
        var forget = saved.Configured ? PageButton("Forget the bot", () => ForgetDiscordAsync().Forget(), id: "DiscordForget") : null;

        var children = new List<UIElement>
        {
            Heading("Set up Martlet's Discord bot"),
            Note("Martlet joins Discord as its own bot, which you create once in Discord's Developer Portal (Discord has no way to make it for you). " +
                "Martlet never logs in as a person.", new Thickness(0, 0, 0, 6)),
            Status(DiscordSetup.Describe(DiscordSetup.Next(saved, status)), "DiscordSetupNext", 15),
            DetailExpander("Step by step", "DiscordSetupSteps", steps, expanded: !saved.Configured),
            new Label { Content = "Bot _token", Target = token, Padding = new Thickness(0, 12, 0, 4) },
            new Grid { Width = 420, HorizontalAlignment = HorizontalAlignment.Left, Children = { token, savedMark } },
            Status(saved.Configured ? $"A bot token is saved for application {saved.ApplicationId}." : "No bot token saved.", "DiscordConfigured")
        };
        if (discordTokenStatus is { } result) children.Add(Status(result, "DiscordTokenStatus"));
        children.Add(Row(save, forget));
        children.Add(Note("Saving checks that it looks like a bot token, keeps it in Windows Credential Manager and connects. " +
            "Forget the bot disconnects and removes the token; your chat modes and people stay.", new Thickness(0, 2, 0, 0)));
        return Card([.. children]);
    }

    private async Task SaveDiscordTokenAsync(PasswordBox box, Button button)
    {
        if (closing || discordBusy) return;
        using (var entered = box.SecurePassword)
            if (entered.Length == 0)
            {
                discordTokenStatus = "Paste the bot token from the Developer Portal first.";
                RenderTab();
                return;
            }
        discordBusy = true;
        button.IsEnabled = false;
        try
        {
            CredentialError error;
            using (var token = TakeKey(box)) error = discord.SaveToken(token);
            if (error != CredentialError.None)
            {
                discordTokenStatus = error == CredentialError.InvalidInput
                    ? "That isn't a Discord bot token. On the Bot page in the Developer Portal, choose Reset Token and copy the whole token."
                    : $"Couldn't save the token: {CredentialMessages.Describe(error)}";
                ActionText.Text = discordTokenStatus;
                return;
            }
            discordTokenStatus = "Bot token saved in Windows Credential Manager. Connecting...";
            if (!discord.Save(current => current with { Enabled = true }))
            {
                discordTokenStatus = "The token is saved, but Martlet couldn't save the Discord setup. Check access to your data directory.";
                return;
            }
            var problem = await discord.StartAsync(lifetime.Token);
            discordTokenStatus = problem is null ? "Bot token saved in Windows Credential Manager." : $"Bot token saved, but {problem}";
            ActionText.Text = problem ?? "Martlet's Discord bot is connecting.";
        }
        catch (OperationCanceledException) { }
        finally
        {
            discordBusy = false;
            if (!closing) RenderTab();
        }
    }

    private async Task ForgetDiscordAsync()
    {
        if (!ConfirmationDialog.Confirm(this, "Forget Martlet's Discord bot?\n\nMartlet disconnects from Discord and removes the bot token " +
                "from Windows Credential Manager. Your chat modes and people stay. Reset the token in the Developer Portal if it may have leaked.",
                "Martlet - Discord"))
            return;
        await discord.ForgetAsync();
        discordTokenStatus = "The bot token is forgotten.";
        ActionText.Text = "Martlet's Discord bot is disconnected and its token removed.";
        if (!closing) RenderTab();
    }

    // ---------- connection ----------

    /// <summary>Discord's main choice: whether Martlet connects its bot to Discord whenever it runs (with an explicit Off), then
    /// the connection's live state.</summary>
    private Border DiscordConnectionCard(DiscordPreferences saved, DiscordBotStatus status)
    {
        var choices = OnOffChoices("DiscordEnabled", "DiscordEnabledOff", "Connect Martlet to Discord whenever Martlet runs",
            "Martlet's bot joins your servers and DMs and chats as the chat modes below say.",
            "Martlet doesn't connect to Discord. Your bot token, chat modes and people are kept.", saved.Enabled && saved.Configured,
            !saved.Configured ? "Set up Martlet's bot below first." : discordBusy ? "Wait for the last change to finish." : null,
            on => { if (on != discord.Preferences.Enabled) SetDiscordEnabledAsync(on).Forget(); });
        choices[1].IsEnabled = !discordBusy;
        var reconnect = PageButton("Reconnect", () => SetDiscordEnabledAsync(true).Forget(), id: "DiscordReconnect");
        reconnect.IsEnabled = saved.Configured && saved.Enabled && !discordBusy;

        var children = new List<UIElement>
        {
            Heading("Connection"),
            choices[0],
            choices[1],
            Status(DiscordSetup.StatusLine(status), "DiscordState", 15),
            Status(!saved.Configured ? "Off: no bot token saved."
                : saved.Enabled ? "On: Martlet connects to Discord whenever it runs." : "Off: Martlet doesn't connect to Discord.", "DiscordEnabledStatus"),
            Status("Bot: " + (status.BotName ?? "not connected yet"), "DiscordBotName"),
            Status("Servers: " + (status.State == DiscordBotState.Online ? DiscordSetup.Servers(status.Servers) : "unknown until connected"), "DiscordServers")
        };
        if (status.Problem is { } problem)
        {
            var line = Status("Problem: " + problem, "DiscordProblem");
            line.Margin = new Thickness(0, 6, 0, 0);
            children.Add(line);
            if (DiscordSetup.IsIntentProblem(problem) && saved.ApplicationId != 0)
                children.Add(Row(PageButton("Open my bot's page", () => OpenInBrowser(new Uri(DiscordInvite.ApplicationPage(saved.ApplicationId))),
                    link: true, id: "DiscordFixIntent")));
        }
        children.Add(Status(discord.TextStatusLine, "DiscordTextStatus"));
        children.Add(Status(discord.VoiceSummary, "DiscordVoiceStatus"));
        children.Add(Row(reconnect));
        return Card([.. children]);
    }

    private async Task SetDiscordEnabledAsync(bool on)
    {
        if (closing || discordBusy) return;
        if (discord.Preferences.Enabled != on && !discord.Save(current => current with { Enabled = on }))
        {
            ActionText.Text = "Couldn't save the Discord setting. Check access to your data directory.";
            RenderTab();
            return;
        }
        discordBusy = true;
        try
        {
            if (on)
            {
                ActionText.Text = "Connecting to Discord...";
                var problem = await discord.StartAsync(lifetime.Token);
                ActionText.Text = problem ?? "Martlet's Discord bot is connecting.";
            }
            else
            {
                await discord.StopAsync();
                ActionText.Text = "Martlet's Discord bot is off.";
            }
        }
        catch (OperationCanceledException) { }
        finally
        {
            discordBusy = false;
            if (!closing) RenderTab();
        }
    }

    // ---------- invite links ----------

    private Border DiscordInviteCard(DiscordPreferences saved)
    {
        var children = new List<UIElement>
        {
            Heading("Invite Martlet"),
            Note("Discord bots can't have friends, so Martlet joins servers you add it to, and its app can be installed on your own account " +
                "for /martlet in any DM or group DM.", new Thickness(0, 0, 0, 6))
        };
        if (saved.ApplicationId == 0)
        {
            children.Add(Status("Save the bot token first; the links come from its application.", "DiscordInviteStatus"));
            return Card([.. children]);
        }
        void Link(string title, string detail, string url, string id, string openId)
        {
            var box = new TextBox { Text = url, IsReadOnly = true, Width = 520, HorizontalAlignment = HorizontalAlignment.Left, TextWrapping = TextWrapping.Wrap };
            AutomationProperties.SetAutomationId(box, id);
            AutomationProperties.SetName(box, title + " link");
            var heading = new TextBlock { Text = title, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 10, 0, 2) };
            children.Add(heading);
            children.Add(Note(detail, new Thickness(0, 0, 0, 4)));
            children.Add(box);
            children.Add(Row(PageButton(title, () => OpenInBrowser(new Uri(url)), primary: id == "DiscordServerLink", id: openId)));
        }
        Link("Add to a server", "Chat and voice in a server you manage: read and send messages, join voice channels, speak and listen.",
            DiscordInvite.ServerUrl(saved.ApplicationId), "DiscordServerLink", "DiscordInviteServer");
        Link("Add to home server", "Your own small server for Martlet: it may also make private call channels and bring people into them.",
            DiscordInvite.ServerUrl(saved.ApplicationId, DiscordInvite.HomeServerPermissions), "DiscordHomeLink", "DiscordInviteHome");
        Link("Add to my account", "Installs Martlet's commands on your Discord account, so /martlet works in DMs and group DMs.",
            DiscordInvite.UserUrl(saved.ApplicationId), "DiscordUserLink", "DiscordInviteUser");
        return Card([.. children]);
    }

    // ---------- chat modes ----------

    private Border DiscordChatCard(DiscordPreferences saved, DiscordBotStatus status)
    {
        ComboBox Mode(string id, string name, DiscordChatMode current, Func<DiscordPreferences, DiscordChatMode, DiscordPreferences> apply)
        {
            var box = new ComboBox { ItemsSource = DiscordModes, SelectedItem = DiscordModes.First(m => m.Mode == current), Width = 240,
                HorizontalAlignment = HorizontalAlignment.Left, MinHeight = 30 };
            AutomationProperties.SetAutomationId(box, id);
            AutomationProperties.SetName(box, name);
            box.SelectionChanged += (_, _) =>
            {
                if (box.SelectedItem is DiscordModeItem picked) SaveDiscord(current => apply(current, picked.Mode), $"{name}: {picked}.");
            };
            return box;
        }
        var server = Mode("DiscordServerChat", "Server channels", saved.ServerChat, (p, m) => p with { ServerChat = m });
        var direct = Mode("DiscordDirectChat", "Direct messages", saved.DirectChat, (p, m) => p with { DirectChat = m });
        var voice = Mode("DiscordVoiceChat", "Voice channels", saved.VoiceChat, (p, m) => p with { VoiceChat = m });
        var anyone = new CheckBox
        {
            IsChecked = saved.DirectFromAnyone, Margin = new Thickness(0, 8, 0, 0),
            Content = new TextBlock { TextWrapping = TextWrapping.Wrap, Text = "Anyone may DM Martlet (not only you and the people it knows)" }
        };
        AutomationProperties.SetAutomationId(anyone, "DiscordDirectFromAnyone");
        anyone.Checked += (_, _) => SaveDiscord(p => p with { DirectFromAnyone = true }, "Anyone may DM Martlet.");
        anyone.Unchecked += (_, _) => SaveDiscord(p => p with { DirectFromAnyone = false }, "Only you and the people Martlet knows may DM it.");

        var children = new List<UIElement>
        {
            Heading("Where Martlet chats"),
            Note("Off: never. Only when mentioned: when someone mentions or replies to Martlet, says its name or uses /martlet. " +
                "Sometimes: Martlet joins in when it thinks it should. Always: it answers everything.", new Thickness(0, 0, 0, 6)),
            Status(DiscordSetup.ChatSummary(saved), "DiscordChatModes"),
            new Label { Content = "_Server channels", Target = server, Padding = new Thickness(0, 10, 0, 4) }, server,
            new Label { Content = "_Direct messages", Target = direct, Padding = new Thickness(0, 8, 0, 4) }, direct,
            anyone,
            new Label { Content = "_Voice channels", Target = voice, Padding = new Thickness(0, 8, 0, 4) }, voice,
            new TextBlock { Text = "Channels with their own mode", FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 14, 0, 2) }
        };
        if (saved.Channels.Count == 0) children.Add(Note("None: every server channel uses the server mode above.", new Thickness(0, 0, 0, 4)));
        foreach (var rule in saved.Channels)
        {
            var line = Status($"{rule.Name}: {DiscordSetup.ModeName(rule.Mode)}", $"DiscordRule-{rule.ChannelId}");
            line.Margin = new Thickness(0, 6, 0, 0);
            children.Add(line);
            children.Add(PageButton("Remove", () => SaveDiscord(p => p with { Channels = [.. p.Channels.Where(c => c.ChannelId != rule.ChannelId)] },
                $"{rule.Name} uses the server mode again."), link: true, id: $"DiscordRuleRemove-{rule.ChannelId}"));
        }
        var channels = discord.TextChannels().Where(channel => saved.Channels.All(rule => rule.ChannelId != channel.Id)).ToArray();
        if (channels.Length == 0)
            children.Add(Status(status.State == DiscordBotState.Online
                ? "Martlet's servers have no other text channels it can see."
                : "Connect the bot to pick a channel.", "DiscordRuleChannelsStatus"));
        else
        {
            var channel = new ComboBox { ItemsSource = channels, SelectedIndex = 0, Width = 360, MinHeight = 30, HorizontalAlignment = HorizontalAlignment.Left };
            AutomationProperties.SetAutomationId(channel, "DiscordRuleChannel");
            AutomationProperties.SetName(channel, "Channel");
            var mode = new ComboBox { ItemsSource = DiscordModes, SelectedIndex = (int)DiscordChatMode.Always, Width = 200, MinHeight = 30,
                Margin = new Thickness(10, 0, 0, 0) };
            AutomationProperties.SetAutomationId(mode, "DiscordRuleMode");
            AutomationProperties.SetName(mode, "Mode for this channel");
            var add = PageButton("Add rule", () =>
            {
                if (channel.SelectedItem is not DiscordEntry picked || mode.SelectedItem is not DiscordModeItem chosen) return;
                SaveDiscord(p => p with { Channels = [.. p.Channels, new DiscordChannelRule(picked.GuildId, picked.Id, picked.Name, chosen.Mode)] },
                    $"{picked.Name}: {chosen}.");
            }, id: "DiscordRuleAdd");
            children.Add(new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0), Children = { channel, mode } });
            children.Add(Row(add));
        }
        return Card([.. children]);
    }

    // ---------- the owner and the home server ----------

    private Border DiscordPeopleCard(DiscordPreferences saved, DiscordBotStatus status)
    {
        var owner = new TextBox
        {
            MaxLength = 64, Width = 280, HorizontalAlignment = HorizontalAlignment.Left,
            Text = discordOwnerDraft ?? (saved.OwnerUserId != 0 ? saved.OwnerUserId.ToString(System.Globalization.CultureInfo.InvariantCulture) : "")
        };
        AutomationProperties.SetAutomationId(owner, "DiscordOwnerId");
        AutomationProperties.SetName(owner, "Your Discord user ID");
        owner.TextChanged += (_, _) => discordOwnerDraft = owner.Text;
        var saveOwner = PageButton("Save", () => SaveDiscordOwner(owner.Text), id: "DiscordOwnerSave");

        var children = new List<UIElement>
        {
            Heading("People on Discord"),
            Note("Martlet treats your Discord account as you: it always may DM Martlet and Martlet calls it \"you\".", new Thickness(0, 0, 0, 6)),
            Status(DiscordSetup.PeopleSummary(saved), "DiscordPeopleCount"),
            Status(saved.OwnerUserId != 0 ? $"Your account: {saved.OwnerUserId}." : "Your account isn't set.", "DiscordOwnerStatus"),
            new Label { Content = "Your Discord _user ID", Target = owner, Padding = new Thickness(0, 10, 0, 4) },
            owner,
            Note("DM Martlet's bot or write where it can see, then pick yourself below. Or in Discord turn on Settings > Advanced > Developer Mode, " +
                "right-click your name and choose Copy User ID, and paste it here.", new Thickness(0, 4, 0, 0)),
            Row(saveOwner)
        };
        var index = 0;
        foreach (var person in discord.SeenPeople().Take(5))
        {
            var n = index++;
            var id = person.Id;
            children.Add(PageButton($"That's me: {person.Name}", () => SaveDiscordOwner(id.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                link: true, id: $"DiscordOwnerPick-{n}"));
        }

        var guilds = discord.Guilds().ToList();
        if (saved.HomeGuildId != 0 && guilds.All(g => g.Id != saved.HomeGuildId))
            guilds.Insert(0, new DiscordEntry(saved.HomeGuildId, $"Saved server {saved.HomeGuildId}"));
        guilds.Insert(0, NoHomeServer);
        var home = new ComboBox
        {
            ItemsSource = guilds, SelectedItem = guilds.FirstOrDefault(g => g.Id == saved.HomeGuildId) ?? NoHomeServer, Width = 360, MinHeight = 30,
            HorizontalAlignment = HorizontalAlignment.Left
        };
        AutomationProperties.SetAutomationId(home, "DiscordHomeServer");
        AutomationProperties.SetName(home, "Home server");
        home.SelectionChanged += (_, _) =>
        {
            if (home.SelectedItem is DiscordEntry picked && picked.Id != discord.Preferences.HomeGuildId)
                SaveDiscord(p => p with { HomeGuildId = picked.Id }, picked.Id == 0 ? "Martlet has no home server." : $"Martlet's home server is {picked.Name}.");
        };
        children.Add(new Label { Content = "_Home server", Target = home, Padding = new Thickness(0, 14, 0, 4) });
        children.Add(home);
        children.Add(Note(status.State == DiscordBotState.Online
            ? "A small server of your own where Martlet makes private voice channels to call people. Add it with Add to home server."
            : "Connect the bot to pick from its servers.", new Thickness(0, 4, 0, 0)));
        return Card([.. children]);
    }

    private void SaveDiscordOwner(string text)
    {
        if (text.Trim().Length == 0)
        {
            discordOwnerDraft = null;
            SaveDiscord(p => p with { OwnerUserId = 0 }, "Your Discord account is cleared.");
            return;
        }
        if (!DiscordSetup.TryParseId(text, out var id))
        {
            ActionText.Text = "That isn't a Discord user ID. It is a long number; Copy User ID gives it.";
            return;
        }
        discordOwnerDraft = null;
        SaveDiscord(p => p with { OwnerUserId = id }, "Martlet knows your Discord account.");
    }

    private void SaveDiscord(Func<DiscordPreferences, DiscordPreferences> update, string done)
    {
        ActionText.Text = discord.Save(update) ? done : "Couldn't save the Discord setup. Check access to your data directory.";
        Dispatcher.BeginInvoke(() =>
        {
            if (!closing && openTab == CompanionTab.Discord) RenderTab();
        });
    }

    /// <summary>The Discord page may be rebuilt for a status change: it is open and nobody is typing or choosing in it.</summary>
    private bool DiscordCanRender() => !closing && openTab == CompanionTab.Discord &&
        System.Windows.Input.Keyboard.FocusedElement is not (System.Windows.Controls.Primitives.TextBoxBase or PasswordBox or ComboBox or ComboBoxItem);
}
