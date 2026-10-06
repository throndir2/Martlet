using System.Globalization;

namespace Martlet.Discord;

/// <summary>An in-memory Discord for rehearsals (tests and MCP's discord_companion_check): a home server whose channels, members
/// and voice occupants are just lists, recording every DM, presence and avatar Martlet sends. Contacts nothing; needs no token.</summary>
public sealed class DiscordRehearsalTransport : IDiscordCompanionTransport
{
    private readonly Lock gate = new();
    private readonly Dictionary<ulong, (ulong Guild, string Name, IReadOnlyList<DiscordOverwrite> Overwrites)> channels = [];
    private readonly Dictionary<ulong, int> occupants = [];
    private ulong next = 900_000_000_000_000_000;

    public bool Online { get; set; } = true;
    public ulong BotId { get; init; } = 100;
    public HashSet<ulong> Members { get; } = [];
    public List<(ulong UserId, string Text, bool Picture)> Directs { get; } = [];
    public List<DiscordPresenceState> Presences { get; } = [];
    public int Avatars { get; private set; }
    public int Banners { get; private set; }
    public List<ulong> Deleted { get; } = [];

    public IReadOnlyDictionary<ulong, (ulong Guild, string Name, IReadOnlyList<DiscordOverwrite> Overwrites)> Channels
    {
        get { lock (gate) return new Dictionary<ulong, (ulong, string, IReadOnlyList<DiscordOverwrite>)>(channels); }
    }

    public ulong AddVoiceChannel(ulong guildId, string name)
    {
        lock (gate)
        {
            var id = ++next;
            channels[id] = (guildId, name, []);
            return id;
        }
    }

    public void SetOccupants(ulong channelId, int count) { lock (gate) occupants[channelId] = count; }

    public IReadOnlyList<(ulong Id, string Name)> VoiceChannels(ulong guildId)
    {
        lock (gate) return [.. channels.Where(c => c.Value.Guild == guildId).Select(c => (c.Key, c.Value.Name))];
    }

    public int Occupants(ulong guildId, ulong channelId) { lock (gate) return occupants.GetValueOrDefault(channelId); }

    public Task<bool> IsMemberAsync(ulong guildId, ulong userId, CancellationToken token) => Task.FromResult(Members.Contains(userId));

    public Task<ulong> CreateVoiceChannelAsync(ulong guildId, string name, IReadOnlyList<DiscordOverwrite> overwrites, CancellationToken token)
    {
        lock (gate)
        {
            var id = ++next;
            channels[id] = (guildId, name, overwrites);
            return Task.FromResult(id);
        }
    }

    public Task SetOverwritesAsync(ulong channelId, IReadOnlyList<DiscordOverwrite> overwrites, CancellationToken token)
    {
        lock (gate)
        {
            if (!channels.TryGetValue(channelId, out var channel)) throw new InvalidOperationException("Unknown channel.");
            channels[channelId] = channel with { Overwrites = overwrites };
        }
        return Task.CompletedTask;
    }

    public Task DeleteChannelAsync(ulong channelId, CancellationToken token)
    {
        lock (gate)
        {
            channels.Remove(channelId);
            occupants.Remove(channelId);
            Deleted.Add(channelId);
        }
        return Task.CompletedTask;
    }

    public Task<string> CreateInviteAsync(ulong channelId, CancellationToken token) =>
        Task.FromResult("https://discord.gg/rehearsal" + (channelId % 10_000).ToString(CultureInfo.InvariantCulture));

    public Task SendDirectAsync(ulong userId, string text, byte[]? png, CancellationToken token)
    {
        lock (gate) Directs.Add((userId, text, png is not null));
        return Task.CompletedTask;
    }

    public Task SetPresenceAsync(DiscordPresenceState presence, CancellationToken token)
    {
        lock (gate) Presences.Add(presence);
        return Task.CompletedTask;
    }

    public Task SetAvatarAsync(byte[] png, byte[]? banner, CancellationToken token)
    {
        lock (gate)
        {
            Avatars++;
            if (banner is not null) Banners++;
        }
        return Task.CompletedTask;
    }
}
