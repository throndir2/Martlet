using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Martlet.Discord;
using Martlet.Presentation;

namespace Martlet.Desktop;

/// <summary>Companion › Discord › Friends and calls: the people Martlet knows on Discord as its friends (requests from
/// <c>/friend ask</c> to approve, adding someone by ID, who takes calls), calling them through the home server, and the bot's
/// status and picture following Martlet and its character.</summary>
internal sealed partial class DiscordPage
{
    private UIElement FriendsCard(DiscordPageModel model)
    {
        var saved = model.Saved;
        var companion = Discord.Companion;
        var state = companion.State;
        var status = Note(model.FriendsText, new Thickness(0, 0, 0, 6));
        AutomationProperties.SetAutomationId(status, "DiscordFriendsStatus");
        var result = Note(services.FriendsResult, new Thickness(0, 6, 0, 0));
        AutomationProperties.SetAutomationId(result, "DiscordFriendsResult");
        void Done(string message)
        {
            result.Text = message;
            services.FriendsDone(message);
        }

        var stack = new List<UIElement>
        {
            Heading("Friends and calls"),
            status,
            HelpTip.Explain("Discord bots can't have a friends list, so these people are Martlet's friends: they can DM it, and Martlet " +
                "can call those who take calls. Anyone can ask with /friend ask and you decide here; they can leave with /friend remove.",
                new Thickness(0, 0, 0, 8), "DiscordFriends", "Martlet's friends", wiki: "Discord#friends-and-calls")
        };

        if (state.Requests.Count > 0)
        {
            stack.Add(Heading("Waiting for you"));
            foreach (var request in state.Requests)
            {
                var id = request.UserId.ToString(CultureInfo.InvariantCulture);
                stack.Add(Note($"{request.Name} ({id}) asked {request.At.ToLocalTime():g}.", new Thickness(0, 4, 0, 0)));
                stack.Add(Row(
                    PageButton("Approve", () => Done(companion.Approve(request.UserId, Discord.Character)
                        ? $"{request.Name} is Martlet's friend now." : "Couldn't save that."), primary: true, id: "DiscordFriendApprove-" + id),
                    PageButton("Decline", () => Done(companion.Decline(request.UserId)
                        ? $"Declined {request.Name}; they can ask again in a week." : "Couldn't save that."), id: "DiscordFriendDecline-" + id)));
            }
        }

        stack.Add(Heading("Friends"));
        var voiceNames = services.VoiceNames();
        if (saved.People.Count == 0) stack.Add(Note("No friends yet.", new Thickness(0, 0, 0, 4)));
        foreach (var person in saved.People)
        {
            var id = person.UserId.ToString(CultureInfo.InvariantCulture);
            var known = DiscordFriends.MemoryName(person, voiceNames);
            var line = Note($"{person.Name} ({id})" + (known is null ? "" : $" — Martlet also knows {known} by voice"),
                new Thickness(0, 6, 0, 0));
            AutomationProperties.SetAutomationId(line, "DiscordFriend-" + id);
            stack.Add(line);
            var mayCall = new CheckBox { Content = "Martlet may call them", IsChecked = person.MayCall, Margin = new Thickness(0, 4, 0, 0) };
            AutomationProperties.SetAutomationId(mayCall, "DiscordFriendMayCall-" + id);
            mayCall.Checked += (_, _) => Done(companion.AllowCalls(person.UserId, true) ? $"Martlet may call {person.Name}." : "Couldn't save that.");
            mayCall.Unchecked += (_, _) => Done(companion.AllowCalls(person.UserId, false) ? $"Martlet won't call {person.Name}." : "Couldn't save that.");
            stack.Add(mayCall);
            var call = PageButton("Call", () => services.CallFriend(person, result), id: "DiscordCall-" + id);
            call.IsEnabled = DiscordCalls.Problem(saved, person, model.Online) is null;
            stack.Add(Row(call, PageButton("Remove", () => Done(companion.Remove(person.UserId)
                ? $"{person.Name} isn't Martlet's friend anymore." : "Couldn't save that."), id: "DiscordFriendRemove-" + id)));
        }

        var addId = new TextBox { Width = 200, MaxLength = 20 };
        AutomationProperties.SetAutomationId(addId, "DiscordFriendAddId");
        AutomationProperties.SetName(addId, "Discord user ID");
        InputHint.SetText(addId, "123456789012345678");
        var addName = new TextBox { Width = 160, MaxLength = DiscordFriends.MaximumNameLength, Margin = new Thickness(8, 0, 0, 0) };
        AutomationProperties.SetAutomationId(addName, "DiscordFriendAddName");
        AutomationProperties.SetName(addName, "Name");
        InputHint.SetText(addName, "Sam");
        var add = PageButton("Add", () =>
        {
            if (!ulong.TryParse(addId.Text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var userId) || userId == 0)
            {
                result.Text = "Paste a Discord user ID (Discord: Settings › Advanced › Developer Mode, then right-click a person › Copy User ID).";
                return;
            }
            var name = addName.Text.Trim();
            Done(companion.Add(userId, name) ? $"Added {DiscordFriends.CleanName(name, userId)}." : "Couldn't save that.");
        }, id: "DiscordFriendAdd");
        stack.Add(Heading("Add someone by ID"));
        var addRow = new WrapPanel();
        addRow.Children.Add(addId);
        addRow.Children.Add(addName);
        stack.Add(addRow);
        stack.Add(Row(add));

        var presence = Note(model.PresenceText, new Thickness(0, 10, 0, 0));
        AutomationProperties.SetAutomationId(presence, "DiscordPresenceStatus");
        stack.Add(presence);
        var picture = Note(model.AvatarText, new Thickness(0, 4, 0, 0));
        AutomationProperties.SetAutomationId(picture, "DiscordAvatarStatus");
        stack.Add(picture);
        stack.Add(Row(PageButton("Update picture now", () =>
        {
            result.Text = "Taking a picture of the character…";
            services.UpdatePictureNow(Done);
        }, id: "DiscordAvatarUpdate")));

        var about = new Expander
        {
            Header = "What Discord allows",
            Margin = new Thickness(0, 8, 0, 0),
            Content = HelpTip.Explain("A Discord bot can't start a DM call, join a group DM or send camera video. So Martlet calls through a " +
                "private voice channel in your home server (removed after the call), and its Discord picture follows the character.",
                new Thickness(0, 4, 0, 0), "DiscordBot", "Discord bots", wiki: "Discord#what-discord-allows")
        };
        AutomationProperties.SetAutomationId(about, "DiscordFriendsAbout");
        stack.Add(about);
        stack.Add(result);
        return Card([.. stack]);
    }
}
