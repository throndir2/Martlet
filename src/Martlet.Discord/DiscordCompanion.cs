using System.Globalization;
using System.Text.Json;

namespace Martlet.Discord;

/// <summary>Someone who asked (with <c>/friend</c>) to be one of the people Martlet knows; the owner approves or declines.</summary>
public sealed record DiscordFriendRequest(ulong UserId, string Name, DateTimeOffset At);

/// <summary>Someone the owner declined, so they can't ask again at once.</summary>
public sealed record DiscordDeclined(ulong UserId, DateTimeOffset At);

/// <summary>A private call channel Martlet made in its home server, kept so it is cleaned up (even after a restart).</summary>
public sealed record DiscordCallChannel(ulong GuildId, ulong ChannelId, ulong UserId, string Name, DateTimeOffset CreatedAt,
    DateTimeOffset? LastOccupiedAt = null);

/// <summary>Martlet's companion state on Discord (<c>discord-companion.json</c> beside <c>discord.json</c>): friend requests waiting
/// for the owner, recent declines, the call channels it made and when the bot's avatar last changed. No secrets.</summary>
public sealed record DiscordCompanionState
{
    public const string FileName = "discord-companion.json";

    public IReadOnlyList<DiscordFriendRequest> Requests { get; init; } = [];
    public IReadOnlyList<DiscordDeclined> Declined { get; init; } = [];
    public IReadOnlyList<DiscordCallChannel> Calls { get; init; } = [];
    public DateTimeOffset? AvatarUpdatedAt { get; init; }
    /// <summary>What the avatar was last made from (a model ID), so the same character isn't uploaded again.</summary>
    public string? AvatarSource { get; init; }

    public static DiscordCompanionState Load(string? directory)
    {
        if (directory is null) return new();
        try
        {
            var path = Path.Combine(directory, FileName);
            if (!File.Exists(path)) return new();
            var loaded = JsonSerializer.Deserialize<DiscordCompanionState>(File.ReadAllText(path)) ?? new();
            return loaded with { Requests = loaded.Requests ?? [], Declined = loaded.Declined ?? [], Calls = loaded.Calls ?? [] };
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException) { return new(); }
    }

    public bool Save(string? directory)
    {
        if (directory is null) return false;
        try
        {
            Directory.CreateDirectory(directory);
            var temporary = Path.Combine(directory, $"discord-companion.{Guid.NewGuid():N}.tmp");
            try
            {
                File.WriteAllText(temporary, JsonSerializer.Serialize(this));
                File.Move(temporary, Path.Combine(directory, FileName), overwrite: true);
            }
            finally
            {
                if (File.Exists(temporary)) File.Delete(temporary);
            }
            return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return false; }
    }
}

public enum DiscordFriendAsk { Requested, AlreadyPending, AlreadyFriend, IsOwner, TryLater, Full }

/// <summary>The friend list's rules: Discord bots can't have friends, so Martlet's People list stands in for one. Anyone may ask
/// with <c>/friend</c>; the owner approves in Martlet (or adds someone by ID); a person may leave with <c>/friend remove</c>.</summary>
public static class DiscordFriends
{
    public const int MaximumPending = 50;
    public const int MaximumNameLength = 64;
    /// <summary>How long someone the owner declined waits before asking again.</summary>
    public static readonly TimeSpan DeclineCooldown = TimeSpan.FromDays(7);

    public static string CleanName(string? name, ulong userId)
    {
        var clean = new string((name ?? "").Where(c => !char.IsControl(c)).ToArray()).Trim();
        if (clean.Length > MaximumNameLength) clean = clean[..MaximumNameLength].TrimEnd();
        return clean.Length > 0 ? clean : userId.ToString(CultureInfo.InvariantCulture);
    }

    public static (DiscordCompanionState State, DiscordFriendAsk Outcome) Ask(DiscordCompanionState state, DiscordPreferences preferences,
        ulong userId, string? name, DateTimeOffset now)
    {
        if (userId == 0) throw new ArgumentOutOfRangeException(nameof(userId));
        if (userId == preferences.OwnerUserId) return (state, DiscordFriendAsk.IsOwner);
        if (preferences.People.Any(person => person.UserId == userId)) return (state, DiscordFriendAsk.AlreadyFriend);
        if (state.Requests.Any(request => request.UserId == userId)) return (state, DiscordFriendAsk.AlreadyPending);
        if (state.Declined.Any(declined => declined.UserId == userId && now - declined.At < DeclineCooldown))
            return (state, DiscordFriendAsk.TryLater);
        if (state.Requests.Count >= MaximumPending) return (state, DiscordFriendAsk.Full);
        return (state with
        {
            Requests = [.. state.Requests, new(userId, CleanName(name, userId), now)],
            Declined = [.. state.Declined.Where(declined => declined.UserId != userId)]
        }, DiscordFriendAsk.Requested);
    }

    /// <summary>Moves a waiting request into the People list (allowed to call). Null when there is no such request.</summary>
    public static (DiscordCompanionState State, DiscordPreferences Preferences)? Approve(DiscordCompanionState state,
        DiscordPreferences preferences, ulong userId)
    {
        if (state.Requests.FirstOrDefault(request => request.UserId == userId) is not { } request) return null;
        return (state with { Requests = [.. state.Requests.Where(r => r.UserId != userId)] }, Add(preferences, userId, request.Name));
    }

    public static DiscordCompanionState? Decline(DiscordCompanionState state, ulong userId, DateTimeOffset now)
    {
        if (!state.Requests.Any(request => request.UserId == userId)) return null;
        return state with
        {
            Requests = [.. state.Requests.Where(r => r.UserId != userId)],
            Declined = [.. state.Declined.Where(d => d.UserId != userId && now - d.At < DeclineCooldown), new(userId, now)]
        };
    }

    /// <summary>Adds (or renames) someone the owner knows by Discord user ID.</summary>
    public static DiscordPreferences Add(DiscordPreferences preferences, ulong userId, string? name, bool mayCall = true)
    {
        if (userId == 0) throw new ArgumentOutOfRangeException(nameof(userId));
        var person = new DiscordPerson(userId, CleanName(name, userId), mayCall);
        return preferences with { People = [.. preferences.People.Where(p => p.UserId != userId), person] };
    }

    /// <summary>Takes someone off the list (the owner's Remove or the person's own <c>/friend remove</c>) and drops any waiting
    /// request, so they are neither DMed nor called again.</summary>
    public static (DiscordCompanionState State, DiscordPreferences Preferences) Remove(DiscordCompanionState state,
        DiscordPreferences preferences, ulong userId) =>
        (state with { Requests = [.. state.Requests.Where(r => r.UserId != userId)] },
            preferences with { People = [.. preferences.People.Where(p => p.UserId != userId)] });

    public static DiscordPreferences AllowCalls(DiscordPreferences preferences, ulong userId, bool mayCall) =>
        preferences with { People = [.. preferences.People.Select(p => p.UserId == userId ? p with { MayCall = mayCall } : p)] };

    /// <summary>The one person in <paramref name="people"/> a spoken or typed name means (case-insensitive, whole name or first
    /// word), or null when none or more than one match.</summary>
    public static DiscordPerson? Find(IEnumerable<DiscordPerson> people, string? name)
    {
        var wanted = (name ?? "").Trim().TrimEnd('.', '!', '?').Trim();
        if (wanted.Length == 0) return null;
        var list = people.ToList();
        var exact = list.Where(p => string.Equals(p.Name, wanted, StringComparison.OrdinalIgnoreCase)).ToList();
        if (exact.Count == 1) return exact[0];
        if (exact.Count > 1) return null;
        var first = list.Where(p => string.Equals(p.Name.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault(), wanted,
            StringComparison.OrdinalIgnoreCase)).ToList();
        return first.Count == 1 ? first[0] : null;
    }

    /// <summary>The one name among Martlet's Memory people (voices it knows) that is this Discord person's name, or null: a simple,
    /// safe link only when exactly one voice goes by that name.</summary>
    public static string? MemoryName(DiscordPerson person, IEnumerable<string> memoryNames)
    {
        var matches = memoryNames.Where(n => string.Equals(n.Trim(), person.Name.Trim(), StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        return matches.Count == 1 ? matches[0] : null;
    }
}

/// <summary>One permission overwrite on a channel: for the server's @everyone role (<paramref name="Role"/>) or one member.</summary>
public sealed record DiscordOverwrite(ulong Id, bool Role, DiscordPermission Allowed, DiscordPermission Denied);

/// <summary>How Martlet calls someone: a private voice channel in its home server that only that person, the bot and the owner
/// can see, reused when it already exists.</summary>
public sealed record DiscordCallPlan(ulong GuildId, string ChannelName, IReadOnlyList<DiscordOverwrite> Overwrites, ulong? ReuseChannelId);

public static class DiscordCalls
{
    public const DiscordPermission Talk = DiscordPermission.ViewChannel | DiscordPermission.Connect | DiscordPermission.Speak |
        DiscordPermission.UseVoiceActivity;
    /// <summary>A call channel nobody joined is removed after this long.</summary>
    public static readonly TimeSpan Unanswered = TimeSpan.FromMinutes(10);
    /// <summary>A call channel everyone left is removed after this long.</summary>
    public static readonly TimeSpan Ended = TimeSpan.FromMinutes(2);

    /// <summary>"Ana" for a person, "Martlet & Ana" for the channel (at most 100 characters, Discord's limit).</summary>
    public static string ChannelName(string? character, DiscordPerson person)
    {
        var who = string.IsNullOrWhiteSpace(character) ? "Martlet" : character.Trim();
        var name = $"{who} & {person.Name}";
        return name.Length <= 100 ? name : name[..100];
    }

    /// <summary>Why Martlet can't call <paramref name="person"/> now, or null when it can.</summary>
    public static string? Problem(DiscordPreferences preferences, DiscordPerson? person, bool online)
    {
        if (person is null) return "Martlet doesn't know that person on Discord.";
        if (!preferences.People.Any(p => p.UserId == person.UserId)) return $"{person.Name} isn't one of Martlet's Discord friends.";
        if (!person.MayCall) return $"{person.Name} doesn't take calls from Martlet.";
        if (preferences.HomeGuildId == 0) return "Choose Martlet's home server first (private call channels are made there).";
        if (!online) return "Martlet's Discord bot isn't connected.";
        return null;
    }

    public static DiscordCallPlan Plan(DiscordPreferences preferences, ulong botId, DiscordPerson person, string? character,
        IEnumerable<(ulong Id, string Name)> voiceChannels)
    {
        var guild = preferences.HomeGuildId;
        if (guild == 0) throw new InvalidOperationException("No home server.");
        var name = ChannelName(character, person);
        List<DiscordOverwrite> overwrites =
        [
            // @everyone's role ID is the server's ID: nobody else sees or joins the channel.
            new(guild, true, DiscordPermission.None, DiscordPermission.ViewChannel | DiscordPermission.Connect),
            new(person.UserId, false, Talk, DiscordPermission.None)
        ];
        if (botId != 0 && botId != person.UserId)
            overwrites.Add(new(botId, false, Talk | DiscordPermission.ManageChannels | DiscordPermission.MoveMembers |
                DiscordPermission.CreateInstantInvite, DiscordPermission.None));
        if (preferences.OwnerUserId != 0 && preferences.OwnerUserId != person.UserId && preferences.OwnerUserId != botId)
            overwrites.Add(new(preferences.OwnerUserId, false, Talk, DiscordPermission.None));
        var reuse = voiceChannels.Where(channel => string.Equals(channel.Name, name, StringComparison.Ordinal)).Select(c => (ulong?)c.Id)
            .FirstOrDefault();
        return new(guild, name, overwrites, reuse);
    }

    public static string JumpUrl(ulong guildId, ulong channelId) =>
        string.Create(CultureInfo.InvariantCulture, $"https://discord.com/channels/{guildId}/{channelId}");

    /// <summary>The DM that rings someone: a jump link, a server invite when they aren't in the home server yet, and, when Martlet
    /// can't talk in voice yet, that it will join once it can.</summary>
    public static string RingText(string? character, ulong guildId, ulong channelId, string? serverInvite, bool voiceReady)
    {
        var who = string.IsNullOrWhiteSpace(character) ? "Martlet" : character.Trim();
        var text = $"📞 {who} is calling you — join here: {JumpUrl(guildId, channelId)}";
        if (serverInvite is not null) text += $"\nYou're not in {who}'s server yet: join it first with {serverInvite}";
        if (!voiceReady) text += $"\n{who} will join the call as soon as it can talk in voice on this computer.";
        return text;
    }

    /// <summary>Whether a call channel should go: everyone left a while ago, or nobody came.</summary>
    public static bool Expired(DiscordCallChannel call, int occupants, DateTimeOffset now) =>
        occupants == 0 && (call.LastOccupiedAt is { } last ? now - last >= Ended : now - call.CreatedAt >= Unanswered);

    /// <summary>The call channel with its last-occupied time refreshed while someone is in it.</summary>
    public static DiscordCallChannel Seen(DiscordCallChannel call, int occupants, DateTimeOffset now) =>
        occupants > 0 ? call with { LastOccupiedAt = now } : call;
}

public enum DiscordPresenceStatus { Online, Idle, DoNotDisturb }

/// <summary>What Martlet is doing, for its Discord presence.</summary>
public sealed record DiscordPresenceInputs(bool Paused = false, bool Away = false, bool Talking = false, bool Listening = false,
    bool Watching = false, string? CallingWith = null, string? Activity = null);

public sealed record DiscordPresenceState(DiscordPresenceStatus Status, string Text);

public static class DiscordPresence
{
    public const int MaximumText = 128;
    /// <summary>At most one presence update this often (Discord drops fast changes).</summary>
    public static readonly TimeSpan MinimumInterval = TimeSpan.FromSeconds(20);

    public static DiscordPresenceState For(DiscordPresenceInputs inputs)
    {
        ArgumentNullException.ThrowIfNull(inputs);
        DiscordPresenceState state = inputs switch
        {
            { CallingWith: { Length: > 0 } name } => new(DiscordPresenceStatus.Online, $"On a call with {name}"),
            { Paused: true } => new(DiscordPresenceStatus.Idle, "Taking a break"),
            { Talking: true } => new(DiscordPresenceStatus.DoNotDisturb, "Talking with you"),
            { Away: true } => new(DiscordPresenceStatus.Idle, "Away for a bit"),
            { Activity: { Length: > 0 } activity } => new(DiscordPresenceStatus.Online, activity.Trim()),
            { Watching: true } => new(DiscordPresenceStatus.Online, "Watching along"),
            { Listening: true } => new(DiscordPresenceStatus.Online, "Listening"),
            _ => new(DiscordPresenceStatus.Online, "Hanging out")
        };
        var text = new string(state.Text.Where(c => !char.IsControl(c)).ToArray());
        return state with { Text = text.Length <= MaximumText ? text : text[..MaximumText] };
    }
}

/// <summary>Sends presence only when it changes, at most once per <see cref="DiscordPresence.MinimumInterval"/>; a change
/// inside the interval waits and only the latest is sent.</summary>
public sealed class DiscordPresenceThrottle(TimeSpan? interval = null)
{
    private readonly TimeSpan interval = interval ?? DiscordPresence.MinimumInterval;
    private DiscordPresenceState? sent;
    private DateTimeOffset sentAt = DateTimeOffset.MinValue;

    public DiscordPresenceState? Sent => sent;
    public DiscordPresenceState? Waiting { get; private set; }

    /// <summary>Offers the wanted presence; returns the one to send now, or null (unchanged, or waiting for the interval).</summary>
    public DiscordPresenceState? Offer(DiscordPresenceState wanted, DateTimeOffset now)
    {
        if (wanted == sent) { Waiting = null; return null; }
        if (now - sentAt < interval) { Waiting = wanted; return null; }
        Waiting = null;
        sent = wanted;
        sentAt = now;
        return wanted;
    }

    /// <summary>When a waiting presence may go, or null.</summary>
    public DateTimeOffset? Due => Waiting is null ? null : sentAt + interval;

    /// <summary>Forgets what was sent (after a reconnect Discord shows the bot online without a status).</summary>
    public void Reset() { sent = null; sentAt = DateTimeOffset.MinValue; }
}

/// <summary>How often the bot's avatar may follow the character: Discord allows only a couple of avatar changes in a short while,
/// so Martlet changes it only when the character changed (or on the owner's Update now), and never more than once per
/// <see cref="Interval"/>.</summary>
public static class DiscordAvatarPolicy
{
    public static readonly TimeSpan Interval = TimeSpan.FromMinutes(30);
    /// <summary>Even the owner's Update now waits this long after the last change.</summary>
    public static readonly TimeSpan ManualInterval = TimeSpan.FromMinutes(10);
    /// <summary>Discord's avatar upload limit is 10 MB; Martlet keeps snapshots far smaller.</summary>
    public const int MaximumBytes = 8 * 1024 * 1024;

    /// <summary>When the avatar may change next for <paramref name="source"/>, or null when it never needs to (already showing).</summary>
    public static DateTimeOffset? NextAllowed(DiscordCompanionState state, string source, bool manual)
    {
        if (!manual && string.Equals(state.AvatarSource, source, StringComparison.Ordinal)) return null;
        return state.AvatarUpdatedAt is { } last ? last + (manual ? ManualInterval : Interval) : DateTimeOffset.MinValue;
    }

    public static bool Allowed(DiscordCompanionState state, string source, bool manual, DateTimeOffset now) =>
        NextAllowed(state, source, manual) is { } next && now >= next;
}
