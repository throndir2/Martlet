using System.Globalization;

namespace Martlet.Discord;

/// <summary>Discord permission bits Martlet's bot asks for (https://discord.com/developers/docs/topics/permissions).</summary>
[Flags]
public enum DiscordPermission : ulong
{
    None = 0,
    CreateInstantInvite = 1UL << 0,
    ManageChannels = 1UL << 4,
    AddReactions = 1UL << 6,
    ViewChannel = 1UL << 10,
    SendMessages = 1UL << 11,
    EmbedLinks = 1UL << 14,
    AttachFiles = 1UL << 15,
    ReadMessageHistory = 1UL << 16,
    Connect = 1UL << 20,
    Speak = 1UL << 21,
    MoveMembers = 1UL << 24,
    UseVoiceActivity = 1UL << 25,
    SendMessagesInThreads = 1UL << 38
}

/// <summary>The links that set up Martlet's Discord app. Discord has no API to create an application, so the owner creates it
/// in the Developer Portal; Martlet then builds the invite links from its application ID.</summary>
public static class DiscordInvite
{
    public const string PortalUrl = "https://discord.com/developers/applications";

    /// <summary>What Martlet needs in any server: read and write chat, join voice channels, speak and hear.</summary>
    public const DiscordPermission ServerPermissions = DiscordPermission.ViewChannel | DiscordPermission.SendMessages |
        DiscordPermission.ReadMessageHistory | DiscordPermission.AddReactions | DiscordPermission.EmbedLinks |
        DiscordPermission.AttachFiles | DiscordPermission.SendMessagesInThreads | DiscordPermission.Connect |
        DiscordPermission.Speak | DiscordPermission.UseVoiceActivity;

    /// <summary>Martlet's own home server also lets it make private call channels, invite people to them and bring them in.</summary>
    public const DiscordPermission HomeServerPermissions = ServerPermissions | DiscordPermission.ManageChannels | DiscordPermission.CreateInstantInvite |
        DiscordPermission.MoveMembers;

    public static string ApplicationPage(ulong applicationId) =>
        $"{PortalUrl}/{applicationId.ToString(CultureInfo.InvariantCulture)}/bot";

    /// <summary>Adds the bot to a server (guild install).</summary>
    public static string ServerUrl(ulong applicationId, DiscordPermission permissions = ServerPermissions)
    {
        ArgumentOutOfRangeException.ThrowIfZero(applicationId);
        return "https://discord.com/oauth2/authorize?client_id=" + applicationId.ToString(CultureInfo.InvariantCulture) +
            "&scope=bot%20applications.commands&permissions=" + ((ulong)permissions).ToString(CultureInfo.InvariantCulture) +
            "&integration_type=0";
    }

    /// <summary>Installs Martlet's commands on a person's own account (user install), so they work in DMs and group DMs.</summary>
    public static string UserUrl(ulong applicationId)
    {
        ArgumentOutOfRangeException.ThrowIfZero(applicationId);
        return "https://discord.com/oauth2/authorize?client_id=" + applicationId.ToString(CultureInfo.InvariantCulture) +
            "&scope=applications.commands&integration_type=1";
    }

    /// <summary>The application ID is the first part of a bot token (base64 of the bot's user ID), or 0 when it isn't one.</summary>
    public static ulong ApplicationIdFromToken(ReadOnlySpan<char> token)
    {
        var dot = token.IndexOf('.');
        if (dot <= 0) return 0;
        var part = token[..dot].ToString().Replace('-', '+').Replace('_', '/');
        part = part.PadRight(part.Length + (4 - part.Length % 4) % 4, '=');
        try
        {
            var text = System.Text.Encoding.ASCII.GetString(Convert.FromBase64String(part));
            return ulong.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var id) ? id : 0;
        }
        catch (FormatException) { return 0; }
    }
}
