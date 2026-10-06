using System.Collections.Concurrent;
using Martlet.Discord;
using NetCord;
using NetCord.Gateway;

namespace Martlet.Desktop;

/// <summary>A server, channel or person the connected bot can see, for Companion › Discord's pickers.</summary>
internal sealed record DiscordEntry(ulong Id, string Name, ulong GuildId = 0)
{
    public override string ToString() => Name;
}

/// <summary>Read-only lists from the connected bot's cache for Companion › Discord: its servers, their text channels and the
/// people it has seen write (so the owner can pick their own account or add someone without Developer Mode). Nothing is fetched
/// from Discord; while the bot is off the lists are empty.</summary>
internal sealed partial class DiscordService
{
    private const int SeenLimit = 50;
    private readonly ConcurrentDictionary<ulong, (string Name, DateTimeOffset At, bool Direct)> seen = new();
    private bool watchingAuthors;

    /// <summary>Starts remembering who writes to the bot (in memory only, newest 50) for the people pickers.</summary>
    internal void WatchAuthors()
    {
        if (watchingAuthors) return;
        watchingAuthors = true;
        Bot.Message += message =>
        {
            if (message.Author.IsBot) return default;
            seen[message.Author.Id] = (message.Author.GlobalName ?? message.Author.Username, DateTimeOffset.UtcNow, message.GuildId is null);
            if (seen.Count > SeenLimit)
                foreach (var old in seen.OrderBy(entry => entry.Value.At).Take(seen.Count - SeenLimit).ToArray()) seen.TryRemove(old.Key, out _);
            return default;
        };
    }

    /// <summary>The servers the bot is in, by name.</summary>
    internal IReadOnlyList<DiscordEntry> Guilds() =>
        Bot.Client?.Cache.Guilds.Values.Select(guild => new DiscordEntry(guild.Id, guild.Name)).OrderBy(g => g.Name, StringComparer.CurrentCultureIgnoreCase).ToArray() ?? [];

    /// <summary>Every text channel in the bot's servers, named "Server › #channel", in server then channel order.</summary>
    internal IReadOnlyList<DiscordEntry> TextChannels() =>
        Bot.Client?.Cache.Guilds.Values
            .OrderBy(guild => guild.Name, StringComparer.CurrentCultureIgnoreCase)
            .SelectMany(guild => guild.Channels.Values.OfType<TextGuildChannel>()
                .OrderBy(channel => channel.Position)
                .Select(channel => new DiscordEntry(channel.Id, $"{guild.Name} › #{channel.Name}", guild.Id)))
            .ToArray() ?? [];

    /// <summary>People the bot has seen write since it connected (DMs first, newest first), and server members it knows.</summary>
    internal IReadOnlyList<DiscordEntry> SeenPeople()
    {
        var botId = Status.BotId;
        var written = seen.OrderByDescending(entry => entry.Value.Direct).ThenByDescending(entry => entry.Value.At)
            .Select(entry => new DiscordEntry(entry.Key, entry.Value.Name));
        var members = Bot.Client?.Cache.Guilds.Values.SelectMany(guild => guild.Users.Values)
            .Where(user => !user.IsBot)
            .Select(user => new DiscordEntry(user.Id, user.GlobalName ?? user.Username)) ?? [];
        return written.Concat(members).Where(entry => entry.Id != botId).DistinctBy(entry => entry.Id).Take(SeenLimit).ToArray();
    }

    /// <summary>A server's name from the cache, or null when the bot isn't connected or isn't in it.</summary>
    internal string? GuildName(ulong guildId) =>
        Bot.Client?.Cache.Guilds.TryGetValue(guildId, out var guild) == true ? guild.Name : null;
}
