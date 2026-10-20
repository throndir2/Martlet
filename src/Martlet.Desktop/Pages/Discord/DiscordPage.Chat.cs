using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Martlet.Discord;
using Martlet.Presentation;

namespace Martlet.Desktop;

/// <summary>Companion › Discord's cards for where Martlet chats: the invite links, the chat modes with per-channel rules, and the
/// owner's account and home server.</summary>
internal sealed partial class DiscordPage
{
    // ---------- invite links ----------

    private Border InviteCard(DiscordPreferences saved)
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
            children.Add(Row(PageButton(title, () => services.OpenInBrowser(new Uri(url)), primary: id == "DiscordServerLink", id: openId)));
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

    private Border ChatCard(DiscordPageModel model)
    {
        var saved = model.Saved;
        ComboBox Mode(string id, string name, DiscordChatMode current, Func<DiscordPreferences, DiscordChatMode, DiscordPreferences> apply)
        {
            var box = new ComboBox { ItemsSource = DiscordModes, SelectedItem = DiscordModes.First(m => m.Mode == current), Width = 240,
                HorizontalAlignment = HorizontalAlignment.Left, MinHeight = 30 };
            AutomationProperties.SetAutomationId(box, id);
            AutomationProperties.SetName(box, name);
            box.SelectionChanged += (_, _) =>
            {
                if (box.SelectedItem is DiscordModeItem picked) services.Save(current => apply(current, picked.Mode), $"{name}: {picked}.");
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
        anyone.Checked += (_, _) => services.Save(p => p with { DirectFromAnyone = true }, "Anyone may DM Martlet.");
        anyone.Unchecked += (_, _) => services.Save(p => p with { DirectFromAnyone = false }, "Only you and the people Martlet knows may DM it.");

        var children = new List<UIElement>
        {
            Heading("Where Martlet chats"),
            HelpTip.Explain("Choose how often Martlet chats in each place. " +
                "Off: never; Only when mentioned: when someone mentions or replies to Martlet, says its name or uses /martlet; " +
                "Sometimes: when Martlet thinks it should join in; Always: it answers everything.", new Thickness(0, 0, 0, 6),
                "DiscordModes", "chat modes", wiki: "Discord#how-often-she-joins-in"),
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
            children.Add(PageButton("Remove", () => services.Save(p => p with { Channels = [.. p.Channels.Where(c => c.ChannelId != rule.ChannelId)] },
                $"{rule.Name} uses the server mode again."), link: true, id: $"DiscordRuleRemove-{rule.ChannelId}"));
        }
        var channels = Discord.TextChannels().Where(channel => saved.Channels.All(rule => rule.ChannelId != channel.Id)).ToArray();
        if (channels.Length == 0)
            children.Add(Status(model.Online
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
                services.Save(p => p with { Channels = [.. p.Channels, new DiscordChannelRule(picked.GuildId, picked.Id, picked.Name, chosen.Mode)] },
                    $"{picked.Name}: {chosen}.");
            }, id: "DiscordRuleAdd");
            children.Add(new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0), Children = { channel, mode } });
            children.Add(Row(add));
        }
        return Card([.. children]);
    }

    // ---------- the owner and the home server ----------

    private Border PeopleCard(DiscordPageModel model)
    {
        var saved = model.Saved;
        var owner = new TextBox
        {
            MaxLength = 64, Width = 280, HorizontalAlignment = HorizontalAlignment.Left,
            Text = services.OwnerDraft ?? (saved.OwnerUserId != 0 ? saved.OwnerUserId.ToString(CultureInfo.InvariantCulture) : "")
        };
        AutomationProperties.SetAutomationId(owner, "DiscordOwnerId");
        AutomationProperties.SetName(owner, "Your Discord user ID");
        InputHint.SetText(owner, "123456789012345678");
        owner.TextChanged += (_, _) => services.OwnerDraft = owner.Text;
        var saveOwner = PageButton("Save", () => services.SaveOwner(owner.Text), id: "DiscordOwnerSave");

        var children = new List<UIElement>
        {
            Heading("People on Discord"),
            Note("Martlet treats your Discord account as you: it always may DM Martlet and Martlet calls it \"you\".", new Thickness(0, 0, 0, 6)),
            Status(DiscordSetup.PeopleSummary(saved), "DiscordPeopleCount"),
            Status(model.OwnerText, "DiscordOwnerStatus"),
            new Label { Content = "Your Discord _user ID", Target = owner, Padding = new Thickness(0, 10, 0, 4) },
            owner,
            HelpTip.Explain("DM Martlet's bot or write where it can see, then pick yourself below. " +
                "Or in Discord turn on Settings > Advanced > Developer Mode, right-click your name, choose Copy User ID and paste it here.",
                new Thickness(0, 4, 0, 0), "DiscordOwner", "your Discord user ID", wiki: "Discord#who-you-are"),
            Row(saveOwner)
        };
        var index = 0;
        foreach (var person in Discord.SeenPeople().Take(5))
        {
            var n = index++;
            var id = person.Id;
            children.Add(PageButton($"That's me: {person.Name}", () => services.SaveOwner(id.ToString(CultureInfo.InvariantCulture)),
                link: true, id: $"DiscordOwnerPick-{n}"));
        }

        var guilds = Discord.Guilds().ToList();
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
            if (home.SelectedItem is DiscordEntry picked && picked.Id != Discord.Preferences.HomeGuildId)
                services.Save(p => p with { HomeGuildId = picked.Id }, picked.Id == 0 ? "Martlet has no home server." : $"Martlet's home server is {picked.Name}.");
        };
        children.Add(new Label { Content = "_Home server", Target = home, Padding = new Thickness(0, 14, 0, 4) });
        children.Add(home);
        children.Add(model.Online
            ? HelpTip.Explain("A small server of your own where Martlet makes private voice channels to call people. " +
                "Add Martlet to it with Add to home server.", new Thickness(0, 4, 0, 0), "DiscordHome", "the home server", wiki: "Discord#friends-and-calls")
            : Note("Connect the bot to pick from its servers.", new Thickness(0, 4, 0, 0)));
        return Card([.. children]);
    }
}
