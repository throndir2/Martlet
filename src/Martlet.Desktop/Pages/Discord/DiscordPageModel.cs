using Martlet.Discord;
using Martlet.Discord.Calls;

namespace Martlet.Desktop;

/// <summary>Companion › Discord's decisions, without WPF. The page makes one each time it draws: it reads the saved Discord setup
/// and the bot's state once, and says what Now, the connection, people, friends and the call camera lines show.
/// <see cref="DiscordPage"/> draws it, and <see cref="IDiscordPageServices"/> is all it reads in the app.</summary>
internal sealed class DiscordPageModel
{
    private readonly IDiscordPageServices services;

    internal DiscordPageModel(IDiscordPageServices services)
    {
        this.services = services;
        Saved = services.Discord.Preferences;
        Status = services.Discord.Status;
    }

    internal DiscordPreferences Saved { get; }
    internal DiscordBotStatus Status { get; }
    internal bool Online => Status.State == DiscordBotState.Online;

    /// <summary>Now's line (DiscordNow) and its problem (DiscordNowProblem).</summary>
    internal string NowText => Now(Saved, Status);

    internal string? ProblemText => Problem(Saved, Status);

    /// <summary>Companion › Discord: whether Martlet's bot is on Discord, and in how many servers.</summary>
    internal static string Now(DiscordPreferences saved, DiscordBotStatus status) =>
        !saved.Configured ? "Off: Martlet's Discord bot isn't set up yet."
        : !saved.Enabled ? "Off: Martlet doesn't connect to Discord."
        : status.State switch
        {
            DiscordBotState.Online => $"Discord: the bot{(status.BotName is { Length: > 0 } name ? $" {name}" : "")} is connected to " +
                $"{DiscordSetup.Servers(status.Servers)}.",
            DiscordBotState.Connecting => "Discord: connecting...",
            DiscordBotState.Failed => "Discord: on, but not connected.",
            _ => "Discord: on, but not connected yet."
        };

    /// <summary>Why the Discord bot isn't connected while it is on, or null.</summary>
    internal static string? Problem(DiscordPreferences saved, DiscordBotStatus status) =>
        saved.Configured && saved.Enabled && status.State == DiscordBotState.Failed ? status.Problem ?? "Discord closed the connection." : null;

    /// <summary>The connection card's lines (DiscordEnabledStatus, DiscordBotName, DiscordServers).</summary>
    internal string EnabledText => !Saved.Configured ? "Off: no bot token saved."
        : Saved.Enabled ? "On: Martlet connects to Discord whenever it runs." : "Off: Martlet doesn't connect to Discord.";

    internal string BotText => "Bot: " + (Status.BotName ?? "not connected yet");

    internal string ServersText => "Servers: " + (Online ? DiscordSetup.Servers(Status.Servers) : "unknown until connected");

    /// <summary>Why the Discord on/off choice can't be chosen now, or null.</summary>
    internal string? EnableBlocked => !Saved.Configured ? "Set up Martlet's bot below first." : services.Busy ? "Wait for the last change to finish." : null;

    /// <summary>The setup card's token line (DiscordConfigured).</summary>
    internal string ConfiguredText => Saved.Configured ? $"A bot token is saved for application {Saved.ApplicationId}." : "No bot token saved.";

    /// <summary>The people card's account line (DiscordOwnerStatus).</summary>
    internal string OwnerText => Saved.OwnerUserId != 0 ? $"Your account: {Saved.OwnerUserId}." : "Your account isn't set.";

    /// <summary>Friends and calls' first line (DiscordFriendsStatus): friends, requests waiting and the call now.</summary>
    internal string FriendsText
    {
        get
        {
            var companion = services.Discord.Companion;
            var waiting = companion.State.Requests.Count;
            var call = companion.ActiveCall();
            var text = $"{Saved.People.Count} {(Saved.People.Count == 1 ? "friend" : "friends")}, " +
                $"{waiting} {(waiting == 1 ? "request" : "requests")} waiting, " +
                (call is null ? "no call now" : $"on a call in {call.Name}");
            if (companion.LastCall is { } last) text += $" (last call: {last.TrimEnd('.')})";
            if (Saved.HomeGuildId == 0) text += "; choose a home server to call people";
            return text + ".";
        }
    }

    /// <summary>The bot's Discord status, in words (DiscordPresenceStatus).</summary>
    internal string PresenceText => services.Discord.Companion.Presence is { } shown
        ? $"Discord status: {shown.Status switch { DiscordPresenceStatus.Idle => "Idle", DiscordPresenceStatus.DoNotDisturb => "Do not disturb", _ => "Online" }}, \"{shown.Text}\"."
        : "Discord status: not set yet (it follows what Martlet is doing while the bot is connected).";

    /// <summary>When the bot's picture last changed (DiscordAvatarStatus).</summary>
    internal string AvatarText
    {
        get
        {
            var companion = services.Discord.Companion;
            var last = companion.State.AvatarUpdatedAt is { } at ? $"Picture last changed {at.ToLocalTime():g}" : "Picture not changed by Martlet yet";
            return companion.LastAvatar is { } note ? $"{last} ({note.TrimEnd('.')})." : last + ".";
        }
    }

    /// <summary>Who is talking in the call, so far (DiscordCallAttribution).</summary>
    internal static string AttributionText(DiscordCallService calls) =>
        $"Who is talking: {calls.Attribution.Source}; {calls.Attribution.Attributed} {(calls.Attribution.Attributed == 1 ? "person" : "people")} named so far.";

    /// <summary>The camera view's state line (DiscordCallCameraStatus).</summary>
    internal static string CameraText(bool open, bool showing, DiscordCallPreferences saved) => !open ? "The camera view is closed."
        : showing
            ? $"The camera view is open: the character in its own 16:9 window titled \"Martlet camera\" on {saved.CameraDescription}."
            : "The camera view is on, but the character isn't showing yet; it opens there as soon as the character shows.";

    /// <summary>The camera picture's state line (DiscordCallCameraPictureStatus) when no action said something else.</summary>
    internal static string PictureText(DiscordCallPreferences saved, bool hasPicture, bool canDraw) =>
        saved.CameraBackground == DiscordCameraBackground.Picture ? $"The camera shows {saved.CameraDescription}."
        : hasPicture ? "Choose Picture above to use the saved picture again."
        : canDraw ? "No picture chosen yet."
        : "No picture chosen yet: set up Companion › Picture making to have Martlet draw one.";
}
