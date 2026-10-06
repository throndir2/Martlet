using System.Text.RegularExpressions;

namespace Martlet.Discord;

/// <summary>Voice decisions that don't need a connection: who an utterance is meant for, when Martlet stops talking because
/// someone talks over it, which channel following the owner leads to, and how a reply is cut into spoken segments.</summary>
public static partial class DiscordVoiceRules
{
    /// <summary>A spoken turn is meant for Martlet when it says one of Martlet's names (Martlet or the character's), or when the
    /// speaker is the only person in the call with Martlet (a one-to-one call, such as the owner talking alone with it).</summary>
    public static bool Addressed(string text, IEnumerable<string> names, int humansInCall)
    {
        if (humansInCall <= 1) return true;
        foreach (var name in names)
        {
            if (string.IsNullOrWhiteSpace(name)) continue;
            if (Regex.IsMatch(text, $@"(?<![\p{{L}}\p{{N}}]){Regex.Escape(name.Trim())}(?![\p{{L}}\p{{N}}])",
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100)))
                return true;
        }
        return false;
    }

    /// <summary>Whether Martlet stops speaking: it is speaking and someone (never Martlet itself) has talked over it with enough
    /// voice to be words.</summary>
    public static bool BargeIn(bool speaking, IReadOnlyCollection<ulong> talkingOver, ulong botId) =>
        speaking && talkingOver.Any(user => user != botId);

    /// <summary>Where following the owner takes Martlet in a server: the owner's channel when following is on and the owner is in
    /// a different voice channel than Martlet; null to stay put (following off, the owner left voice, or already together).</summary>
    public static ulong? FollowOwner(bool follow, ulong? ownerChannel, ulong? botChannel) =>
        follow && ownerChannel is { } channel && channel != botChannel ? channel : null;

    /// <summary>A reply cut at sentence ends into segments of at most <paramref name="maximumCharacters"/>, so the first is spoken
    /// sooner and each fits one speech request.</summary>
    public static IReadOnlyList<string> Segments(string reply, int maximumCharacters = 400)
    {
        var segments = new List<string>();
        var current = "";
        foreach (Match sentence in Sentence().Matches(reply))
        {
            var text = sentence.Value.Trim();
            if (text.Length == 0) continue;
            if (current.Length > 0 && current.Length + 1 + text.Length > maximumCharacters)
            {
                segments.Add(current);
                current = "";
            }
            while (text.Length > maximumCharacters)
            {
                var cut = text.LastIndexOf(' ', maximumCharacters - 1);
                if (cut <= 0) cut = maximumCharacters;
                segments.Add(text[..cut].Trim());
                text = text[cut..].Trim();
            }
            current = current.Length == 0 ? text : current + " " + text;
        }
        if (current.Length > 0) segments.Add(current);
        return segments;
    }

    [GeneratedRegex(@"[^.!?\n]+[.!?]*|\n", RegexOptions.CultureInvariant)]
    private static partial Regex Sentence();
}

public enum DiscordVoicePhase { Idle, Joining, Connected, Leaving }

/// <summary>Martlet's voice connection in each server (one per server, as Discord allows): joining, connected or leaving, and
/// since when it has been alone. Thread-safe.</summary>
public sealed class DiscordVoiceRooms
{
    private readonly Lock gate = new();
    private readonly Dictionary<ulong, Room> rooms = [];

    public DiscordVoicePhase Phase(ulong guildId) { lock (gate) return rooms.TryGetValue(guildId, out var room) ? room.Phase : DiscordVoicePhase.Idle; }
    public ulong? Channel(ulong guildId) { lock (gate) return rooms.TryGetValue(guildId, out var room) ? room.ChannelId : null; }
    public IReadOnlyList<(ulong GuildId, ulong ChannelId, DiscordVoicePhase Phase)> All
    {
        get { lock (gate) return [.. rooms.Select(pair => (pair.Key, pair.Value.ChannelId, pair.Value.Phase))]; }
    }

    /// <summary>Starts joining <paramref name="channelId"/>; false when already joining or in it, or while leaving. Joining
    /// another channel of the same server while connected is a move (the old connection is replaced).</summary>
    public bool TryBeginJoin(ulong guildId, ulong channelId)
    {
        lock (gate)
        {
            if (rooms.TryGetValue(guildId, out var room) &&
                (room.Phase is DiscordVoicePhase.Joining or DiscordVoicePhase.Leaving || room.ChannelId == channelId)) return false;
            rooms[guildId] = new(channelId, DiscordVoicePhase.Joining, null);
            return true;
        }
    }

    public void Joined(ulong guildId, ulong channelId)
    {
        lock (gate) rooms[guildId] = new(channelId, DiscordVoicePhase.Connected, null);
    }

    /// <summary>Starts leaving; false when not joining or connected.</summary>
    public bool TryBeginLeave(ulong guildId)
    {
        lock (gate)
        {
            if (!rooms.TryGetValue(guildId, out var room) || room.Phase == DiscordVoicePhase.Leaving) return false;
            rooms[guildId] = room with { Phase = DiscordVoicePhase.Leaving };
            return true;
        }
    }

    /// <summary>The connection ended (left, failed or disconnected); returns the channel it was in.</summary>
    public ulong? Left(ulong guildId)
    {
        lock (gate) return rooms.Remove(guildId, out var room) ? room.ChannelId : null;
    }

    /// <summary>Records how many people (not bots) are in Martlet's channel. Returns true when the channel just became empty.</summary>
    public bool People(ulong guildId, int humans, DateTimeOffset now)
    {
        lock (gate)
        {
            if (!rooms.TryGetValue(guildId, out var room) || room.Phase != DiscordVoicePhase.Connected) return false;
            if (humans > 0) { rooms[guildId] = room with { AloneSince = null }; return false; }
            if (room.AloneSince is not null) return false;
            rooms[guildId] = room with { AloneSince = now };
            return true;
        }
    }

    /// <summary>Servers where Martlet has been alone in its channel for at least <paramref name="after"/>.</summary>
    public IReadOnlyList<ulong> AloneTooLong(DateTimeOffset now, TimeSpan after)
    {
        lock (gate)
            return [.. rooms.Where(pair => pair.Value is { Phase: DiscordVoicePhase.Connected, AloneSince: { } since } && now - since >= after)
                .Select(pair => pair.Key)];
    }

    private sealed record Room(ulong ChannelId, DiscordVoicePhase Phase, DateTimeOffset? AloneSince);
}
