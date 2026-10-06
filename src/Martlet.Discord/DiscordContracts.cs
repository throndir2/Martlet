namespace Martlet.Discord;

/// <summary>How readily Martlet answers in one kind of Discord place: never, only when addressed (a mention, a reply to it,
/// its name or a DM), sometimes on its own when it thinks it should (the reply engine may stay quiet), or to everything.</summary>
public enum DiscordChatMode { Off, Mentions, Sometimes, Always }

public enum DiscordTurnSource { Text, Voice }

/// <summary>Who said something in Discord. <paramref name="IsOwner"/> is the Martlet owner's own Discord account.</summary>
public sealed record DiscordSpeaker(ulong UserId, string Name, bool IsOwner);

/// <summary>Where something was said: a server text or voice channel, a DM or (through the user-installed app) a group DM.</summary>
public sealed record DiscordPlace(ulong ChannelId, ulong? GuildId, string Name, bool Direct)
{
    public string Key => GuildId is { } guild ? $"{guild}/{ChannelId}" : $"dm/{ChannelId}";
}

/// <summary>One recent line of a Discord conversation, for context.</summary>
public sealed record DiscordLine(string Speaker, string Text, DateTimeOffset At, bool FromMartlet);

/// <summary>One thing said in Discord that Martlet may answer. <paramref name="Addressed"/> means it was meant for Martlet (a
/// mention, a reply to Martlet, its name, a DM or a slash command); otherwise it is ambient chatter that Martlet answers only
/// when it decides to.</summary>
public sealed record DiscordTurn(DiscordPlace Place, DiscordSpeaker Speaker, string Text, DiscordTurnSource Source,
    bool Addressed, IReadOnlyList<DiscordLine> Recent);

public sealed record DiscordReply(string Text);

/// <summary>Turns a Discord turn into Martlet's reply with its own persona, Thinking route and memory. Returns null to stay
/// quiet (an ambient turn it chose not to answer). Implementations must never delay or evict the local conversation.</summary>
public interface IDiscordReplyEngine
{
    Task<DiscordReply?> ReplyAsync(DiscordTurn turn, CancellationToken token);
}

public static class DiscordChatRules
{
    /// <summary>Whether a turn in a place with <paramref name="mode"/> goes to the reply engine at all.</summary>
    public static bool Considers(DiscordChatMode mode, bool addressed) => mode switch
    {
        DiscordChatMode.Off => false,
        DiscordChatMode.Mentions => addressed,
        _ => true
    };

    /// <summary>Whether the reply engine may stay quiet ([pass]) on this turn rather than having to answer.</summary>
    public static bool MayPass(DiscordChatMode mode, bool addressed) => mode == DiscordChatMode.Sometimes && !addressed;
}
