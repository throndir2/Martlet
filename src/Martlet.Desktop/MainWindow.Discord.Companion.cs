using System.IO;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Threading;
using Martlet.Avatars;
using Martlet.Discord;
using Martlet.Presentation;

namespace Martlet.Desktop;

/// <summary>Companion › Discord › Friends and calls: the people Martlet knows on Discord as its friends (requests from
/// <c>/friend ask</c> to approve, adding someone by ID, who takes calls), calling them through the home server, and the bot's
/// status and picture following Martlet and its character.</summary>
public partial class MainWindow
{
    private readonly DispatcherTimer discordPresenceTimer = new() { Interval = TimeSpan.FromSeconds(15) };
    private (string Source, byte[]? Png)? discordFallbackPicture;
    private bool discordPresenceBusy;
    private string discordFriendsResult = "";

    /// <summary>Wires the companion features to the character, the conversation and the presence timer (once, at start).</summary>
    private void InitializeDiscordCompanion()
    {
        discord.AttachCompanion(
            (portrait, token) => Dispatcher.InvokeAsync(() => avatar.SnapshotAsync(portrait, token)).Task.Unwrap(),
            () => Dispatcher.Invoke(DiscordAvatarSource),
            DiscordAvatarPictureAsync,
            () => Dispatcher.Invoke(() => homeSettings?.Companion?.ActivePersona?.Name));
        if (conversation is not null)
        {
            conversation.DiscordCaller = discord;
            conversation.CallCamera = new CallCameraBridge(this);
        }
        discord.Companion.Requested += request => Dispatcher.BeginInvoke(() =>
            ErrorLog.Info("Discord: someone asked to be Martlet's friend; approve or decline in Companion › Discord."));
        discordPresenceTimer.Tick += (_, _) => UpdateDiscordPresence();
        discordPresenceTimer.Start();
    }

    /// <summary>What the bot's picture is made from: the chosen character model (hashed, never its path), or null for none.</summary>
    private string? DiscordAvatarSource() => homeAvatar is { } profile
        ? "model:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(profile.Renderer + "|" + profile.ModelPath)))[..16]
        : null;

    /// <summary>A head-and-shoulders picture of the showing character, else a VRM model's own thumbnail.</summary>
    private async Task<byte[]?> DiscordAvatarPictureAsync(CancellationToken token)
    {
        var shown = await Dispatcher.InvokeAsync(() => avatar.SnapshotAsync(portrait: true, token)).Task.Unwrap().ConfigureAwait(false);
        if (shown is not null) return shown;
        var (profile, source) = await Dispatcher.InvokeAsync(() => (homeAvatar, DiscordAvatarSource()));
        if (profile is null || source is null || profile.Renderer != AvatarRenderer.Vrm) return null;
        if (discordFallbackPicture is { } cached && cached.Source == source) return cached.Png;
        byte[]? thumbnail = null;
        try
        {
            var (_, images) = await CharacterThemeImages.ReadModelAsync(profile.Renderer, profile.ModelPath, token).ConfigureAwait(false);
            thumbnail = images.FirstOrDefault(image => image.Thumbnail)?.Bytes;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException or
            Martlet.Core.Contracts.ContractException)
        {
            ErrorLog.Warn($"Couldn't read the character's thumbnail for Discord: {error.Message}");
        }
        discordFallbackPicture = (source, thumbnail);
        return thumbnail;
    }

    /// <summary>What Martlet is doing, for the bot's status.</summary>
    private DiscordPresenceInputs DiscordPresenceNow()
    {
        var talk = openConversation;
        var call = discord.Companion.ActiveCall();
        return new(
            Paused: talk?.Paused == true,
            Away: UserIdle() > TimeSpan.FromMinutes(10),
            Talking: conversation?.Replying == true || talk?.HearingYou == true,
            Listening: talk?.ListeningStarted == true,
            Watching: talk?.WatchingStarted == true,
            CallingWith: call is null ? null : discord.Preferences.People.FirstOrDefault(p => p.UserId == call.UserId)?.Name);
    }

    private void UpdateDiscordPresence()
    {
        if (closing || discordPresenceBusy || discord.Status.State != DiscordBotState.Online) return;
        discordPresenceBusy = true;
        var inputs = DiscordPresenceNow();
        _ = Task.Run(async () =>
        {
            try
            {
                await discord.UpdatePresenceAsync(inputs, lifetime.Token).ConfigureAwait(false);
                await discord.UpdateAvatarAsync(manual: false, lifetime.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { }
            finally { await Dispatcher.InvokeAsync(() => discordPresenceBusy = false); }
        });
    }

    // ---------- the card ----------

    private UIElement DiscordFriendsCard()
    {
        var saved = discord.Preferences;
        var companion = discord.Companion;
        var state = companion.State;
        var status = Note(DiscordFriendsSummary(), new Thickness(0, 0, 0, 6));
        AutomationProperties.SetAutomationId(status, "DiscordFriendsStatus");
        var result = Note(discordFriendsResult, new Thickness(0, 6, 0, 0));
        AutomationProperties.SetAutomationId(result, "DiscordFriendsResult");
        void Done(string message)
        {
            result.Text = discordFriendsResult = message;
            if (openTab is not null) Dispatcher.BeginInvoke(RenderTab, DispatcherPriority.Background);
        }

        var stack = new List<UIElement>
        {
            Heading("Friends and calls"),
            status,
            Note("Discord bots can't have a friends list, so these people are Martlet's friends: they can DM it, and Martlet " +
                "can call those who take calls. Anyone can ask with /friend ask; you decide here. They can leave with /friend remove.",
                new Thickness(0, 0, 0, 8))
        };

        if (state.Requests.Count > 0)
        {
            stack.Add(Heading("Waiting for you"));
            foreach (var request in state.Requests)
            {
                var id = request.UserId.ToString(CultureInfo.InvariantCulture);
                stack.Add(Note($"{request.Name} ({id}) asked {request.At.ToLocalTime():g}.", new Thickness(0, 4, 0, 0)));
                stack.Add(Row(
                    PageButton("Approve", () => Done(companion.Approve(request.UserId, discord.Character)
                        ? $"{request.Name} is Martlet's friend now." : "Couldn't save that."), primary: true, id: "DiscordFriendApprove-" + id),
                    PageButton("Decline", () => Done(companion.Decline(request.UserId)
                        ? $"Declined {request.Name}; they can ask again in a week." : "Couldn't save that."), id: "DiscordFriendDecline-" + id)));
            }
        }

        stack.Add(Heading("Friends"));
        var roster = localVoices.Roster;
        var voiceNames = roster.Voices.Where(v => !v.Removed && v.Named).Select(MemoryPeople.Label).ToList();
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
            var call = PageButton("Call", () => CallDiscordFriend(person, result), id: "DiscordCall-" + id);
            call.IsEnabled = DiscordCalls.Problem(saved, person, discord.Status.State == DiscordBotState.Online) is null;
            stack.Add(Row(call, PageButton("Remove", () => Done(companion.Remove(person.UserId)
                ? $"{person.Name} isn't Martlet's friend anymore." : "Couldn't save that."), id: "DiscordFriendRemove-" + id)));
        }

        var addId = new TextBox { Width = 200, MaxLength = 20 };
        AutomationProperties.SetAutomationId(addId, "DiscordFriendAddId");
        AutomationProperties.SetName(addId, "Discord user ID");
        var addName = new TextBox { Width = 160, MaxLength = DiscordFriends.MaximumNameLength, Margin = new Thickness(8, 0, 0, 0) };
        AutomationProperties.SetAutomationId(addName, "DiscordFriendAddName");
        AutomationProperties.SetName(addName, "Name");
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

        var presence = Note(DiscordPresenceSummary(), new Thickness(0, 10, 0, 0));
        AutomationProperties.SetAutomationId(presence, "DiscordPresenceStatus");
        stack.Add(presence);
        var picture = Note(DiscordAvatarSummary(), new Thickness(0, 4, 0, 0));
        AutomationProperties.SetAutomationId(picture, "DiscordAvatarStatus");
        stack.Add(picture);
        stack.Add(Row(PageButton("Update picture now", () =>
        {
            result.Text = "Taking a picture of the character…";
            discord.UpdateAvatarAsync(manual: true, lifetime.Token).ContinueWith(task =>
                Dispatcher.BeginInvoke(() => Done(task.IsCompletedSuccessfully ? task.Result : "Couldn't update the picture.")), TaskScheduler.Default);
        }, id: "DiscordAvatarUpdate")));

        var about = new Expander
        {
            Header = "What Discord allows",
            Margin = new Thickness(0, 8, 0, 0),
            Content = HelpTip.Explain("A Discord bot can't start a DM call, join a group DM or send camera video. So Martlet calls through a " +
                "private voice channel in your home server (only you, the friend and Martlet can see it; it's removed after the call), " +
                "its Discord picture follows the character, and /selfie posts a picture of it. Showing the live character in a call " +
                "would need a Discord Activity, a possible later step.", new Thickness(0, 4, 0, 0), "DiscordBot", "Discord bots")
        };
        AutomationProperties.SetAutomationId(about, "DiscordFriendsAbout");
        stack.Add(about);
        stack.Add(result);
        return Card([.. stack]);
    }

    private void CallDiscordFriend(DiscordPerson person, TextBlock result)
    {
        result.Text = $"Calling {person.Name}…";
        discord.CallAsync(person, lifetime.Token).ContinueWith(task => Dispatcher.BeginInvoke(() =>
            result.Text = discordFriendsResult = task.IsCompletedSuccessfully ? task.Result.Message : $"Couldn't call {person.Name}."), TaskScheduler.Default);
    }

    private string DiscordFriendsSummary()
    {
        var saved = discord.Preferences;
        var companion = discord.Companion;
        var waiting = companion.State.Requests.Count;
        var call = companion.ActiveCall();
        var text = $"{saved.People.Count} {(saved.People.Count == 1 ? "friend" : "friends")}, " +
            $"{waiting} {(waiting == 1 ? "request" : "requests")} waiting. " +
            (call is null ? "No call now." : $"On a call in {call.Name}.");
        if (companion.LastCall is { } last) text += $" Last call: {last}";
        if (saved.HomeGuildId == 0) text += " Choose a home server to call people.";
        return text;
    }

    private string DiscordPresenceSummary() => discord.Companion.Presence is { } shown
        ? $"Discord status: {shown.Status switch { DiscordPresenceStatus.Idle => "Idle", DiscordPresenceStatus.DoNotDisturb => "Do not disturb", _ => "Online" }}, \"{shown.Text}\"."
        : "Discord status: not set yet (it follows what Martlet is doing while the bot is connected).";

    private string DiscordAvatarSummary()
    {
        var state = discord.Companion.State;
        var last = state.AvatarUpdatedAt is { } at ? $"Picture last changed {at.ToLocalTime():g}." : "Picture not changed by Martlet yet.";
        return discord.Companion.LastAvatar is { } note ? $"{last} {note}" : last;
    }

    private static TimeSpan UserIdle() => DiscordIdle.UserIdle();
}

/// <summary>How long since the last keyboard or mouse input on this PC (for the Discord status's Away).</summary>
internal static class DiscordIdle
{
    [StructLayout(LayoutKind.Sequential)]
    private struct LastInput { public uint Size; public uint Time; }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetLastInputInfo(ref LastInput info);

    internal static TimeSpan UserIdle()
    {
        var info = new LastInput { Size = (uint)Marshal.SizeOf<LastInput>() };
        return GetLastInputInfo(ref info) ? TimeSpan.FromMilliseconds(unchecked((uint)Environment.TickCount - info.Time)) : TimeSpan.Zero;
    }
}
