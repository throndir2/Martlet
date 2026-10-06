using NetCord;
using NetCord.Gateway;
using NetCord.Rest;

namespace Martlet.Discord;

/// <summary>What Martlet's companion features need from Discord, so they run against the real bot (<see cref="NetCordCompanionTransport"/>)
/// or a fake one (tests, MCP's discord_companion_check) without a token.</summary>
public interface IDiscordCompanionTransport
{
    bool Online { get; }
    ulong BotId { get; }
    /// <summary>The server's voice channels (ID and name).</summary>
    IReadOnlyList<(ulong Id, string Name)> VoiceChannels(ulong guildId);
    /// <summary>How many people other than the bot are in a voice channel now.</summary>
    int Occupants(ulong guildId, ulong channelId);
    Task<bool> IsMemberAsync(ulong guildId, ulong userId, CancellationToken token);
    Task<ulong> CreateVoiceChannelAsync(ulong guildId, string name, IReadOnlyList<DiscordOverwrite> overwrites, CancellationToken token);
    Task SetOverwritesAsync(ulong channelId, IReadOnlyList<DiscordOverwrite> overwrites, CancellationToken token);
    Task DeleteChannelAsync(ulong channelId, CancellationToken token);
    /// <summary>A one-day invite link to a channel (joining it also joins the server).</summary>
    Task<string> CreateInviteAsync(ulong channelId, CancellationToken token);
    Task SendDirectAsync(ulong userId, string text, byte[]? png, CancellationToken token);
    Task SetPresenceAsync(DiscordPresenceState presence, CancellationToken token);
    /// <summary>Sets the bot's avatar and, when given, its profile banner (both PNG) in one change.</summary>
    Task SetAvatarAsync(byte[] png, byte[]? banner, CancellationToken token);
}

/// <summary>The result of calling someone: whether it rang, what to tell the owner and the channel used.</summary>
public sealed record DiscordCallResult(bool Rang, string Message, DiscordCallPlan? Plan = null, ulong ChannelId = 0, bool Joined = false,
    bool InvitedToServer = false);

/// <summary>Martlet as a Discord friend, within what Discord allows: the friend list (requests the owner approves, people who
/// leave), calls through private voice channels in the home server, its presence and its avatar. Saves its state in the data
/// directory and talks to Discord through <see cref="Transport"/>.</summary>
public sealed class DiscordCompanion
{
    private readonly Lock gate = new();
    private readonly string? directory;
    private readonly Func<DiscordPreferences> preferences;
    private readonly Func<Func<DiscordPreferences, DiscordPreferences>, bool> savePreferences;
    private readonly TimeProvider clock;
    private readonly DiscordPresenceThrottle presence = new();
    private DiscordCompanionState state;
    private string? lastCall, lastAvatar, lastProblem;

    public DiscordCompanion(string? directory, Func<DiscordPreferences> preferences,
        Func<Func<DiscordPreferences, DiscordPreferences>, bool> savePreferences, TimeProvider? clock = null)
    {
        this.directory = directory;
        this.preferences = preferences;
        this.savePreferences = savePreferences;
        this.clock = clock ?? TimeProvider.System;
        state = DiscordCompanionState.Load(directory);
    }

    public IDiscordCompanionTransport? Transport { get; set; }
    public DiscordCompanionState State { get { lock (gate) return state; } }
    /// <summary>Raised (off the UI thread) when requests, friends, calls, presence or the avatar change.</summary>
    public event Action? Changed;
    /// <summary>Someone asked to be Martlet's friend (for a notification).</summary>
    public event Action<DiscordFriendRequest>? Requested;

    public string? LastCall { get { lock (gate) return lastCall; } }
    public string? LastAvatar { get { lock (gate) return lastAvatar; } }
    public string? LastProblem { get { lock (gate) return lastProblem; } }
    /// <summary>The status Discord shows now (the last one sent).</summary>
    public DiscordPresenceState? Presence { get { lock (gate) return presence.Sent; } }

    private DateTimeOffset Now => clock.GetUtcNow();

    private bool Update(Func<DiscordCompanionState, DiscordCompanionState> change)
    {
        lock (gate)
        {
            var next = change(state);
            if (next == state) return true;
            if (directory is not null && !next.Save(directory)) return false;
            state = next;
        }
        Changed?.Invoke();
        return true;
    }

    private void Note(string? problem)
    {
        bool changed;
        lock (gate) { changed = lastProblem != problem; lastProblem = problem; }
        if (changed) Changed?.Invoke();
    }

    // ---------- friends ----------

    public DiscordFriendAsk Ask(ulong userId, string? name)
    {
        DiscordFriendAsk outcome = default;
        DiscordFriendRequest? added = null;
        var saved = Update(current =>
        {
            var (next, result) = DiscordFriends.Ask(current, preferences(), userId, name, Now);
            outcome = result;
            if (result == DiscordFriendAsk.Requested) added = next.Requests[^1];
            return next;
        });
        if (!saved) return DiscordFriendAsk.TryLater;
        if (added is not null) Requested?.Invoke(added);
        return outcome;
    }

    /// <summary>What <c>/friend</c> answers the person who asked.</summary>
    public static string AskReply(DiscordFriendAsk outcome, string? character)
    {
        var who = string.IsNullOrWhiteSpace(character) ? "Martlet" : character.Trim();
        return outcome switch
        {
            DiscordFriendAsk.Requested => $"I asked my person to add you. You'll get a DM from {who} once they say yes.",
            DiscordFriendAsk.AlreadyPending => "Your request is still waiting for my person to look at it.",
            DiscordFriendAsk.AlreadyFriend => "We're already friends! Use /friend remove if you'd rather not be.",
            DiscordFriendAsk.IsOwner => "You're my person; you don't need to ask.",
            DiscordFriendAsk.Full => "Too many friend requests are waiting right now. Try again later.",
            _ => "Not right now. Try again another day."
        };
    }

    public bool Approve(ulong userId, string? character = null)
    {
        var current = State;
        if (DiscordFriends.Approve(current, preferences(), userId) is not { } approved) return false;
        if (!savePreferences(_ => approved.Preferences)) return false;
        Update(_ => approved.State);
        var who = string.IsNullOrWhiteSpace(character) ? "Martlet" : character.Trim();
        DirectLaterAsync(userId, $"Yay, we're friends now! DM me any time. {who} may sometimes call you; use /friend remove if you'd rather not.");
        return true;
    }

    public bool Decline(ulong userId) => DiscordFriends.Decline(State, userId, Now) is { } next && Update(_ => next);

    public bool Add(ulong userId, string? name, bool mayCall = true) =>
        userId != 0 && savePreferences(saved => DiscordFriends.Add(saved, userId, name, mayCall)) &&
        Update(current => current with { Requests = [.. current.Requests.Where(r => r.UserId != userId)] });

    public bool Remove(ulong userId)
    {
        var (next, _) = DiscordFriends.Remove(State, preferences(), userId);
        return savePreferences(saved => DiscordFriends.Remove(next, saved, userId).Preferences) && Update(_ => next);
    }

    public bool AllowCalls(ulong userId, bool mayCall) => savePreferences(saved => DiscordFriends.AllowCalls(saved, userId, mayCall));

    private void DirectLaterAsync(ulong userId, string text)
    {
        if (Transport is not { Online: true } transport) return;
        _ = Task.Run(async () =>
        {
            try { await transport.SendDirectAsync(userId, text, null, CancellationToken.None).ConfigureAwait(false); }
            catch (Exception error) when (IsDiscordFailure(error)) { Note($"Couldn't DM {userId}: {error.Message}"); }
        });
    }

    // ---------- calls ----------

    /// <summary>Calls a friend: makes (or reuses) the private channel "Martlet & name" in the home server, DMs them a link (with a
    /// server invite when they aren't in the server yet) and joins it with <paramref name="joinVoice"/> (null while Martlet can't
    /// talk in Discord voice: the DM then says it will join once it can).</summary>
    public async Task<DiscordCallResult> CallAsync(ulong userId, string? character, Func<ulong, ulong, CancellationToken, Task>? joinVoice,
        CancellationToken token)
    {
        var saved = preferences();
        var person = saved.People.FirstOrDefault(p => p.UserId == userId);
        var transport = Transport;
        if (DiscordCalls.Problem(saved, person, transport?.Online == true) is { } problem) return Finish(new(false, problem));
        try
        {
            var plan = DiscordCalls.Plan(saved, transport!.BotId, person!, character, transport.VoiceChannels(saved.HomeGuildId));
            ulong channel;
            if (plan.ReuseChannelId is { } existing)
            {
                channel = existing;
                await transport.SetOverwritesAsync(channel, plan.Overwrites, token).ConfigureAwait(false);
            }
            else channel = await transport.CreateVoiceChannelAsync(plan.GuildId, plan.ChannelName, plan.Overwrites, token).ConfigureAwait(false);
            var now = Now;
            Update(current => current with
            {
                Calls = [.. current.Calls.Where(c => c.ChannelId != channel), new(plan.GuildId, channel, person!.UserId, plan.ChannelName, now)]
            });
            string? invite = null;
            if (!await transport.IsMemberAsync(plan.GuildId, person!.UserId, token).ConfigureAwait(false))
            {
                try { invite = await transport.CreateInviteAsync(channel, token).ConfigureAwait(false); }
                catch (Exception error) when (IsDiscordFailure(error)) { Note($"Couldn't make a server invite: {error.Message}"); }
            }
            await transport.SendDirectAsync(person.UserId, DiscordCalls.RingText(character, plan.GuildId, channel, invite, joinVoice is not null),
                null, token).ConfigureAwait(false);
            var joined = false;
            string? joinProblem = null;
            if (joinVoice is not null)
            {
                // The friend already has the link, so a failed join still counts as ringing them.
                try
                {
                    await joinVoice(plan.GuildId, channel, token).ConfigureAwait(false);
                    joined = true;
                }
                catch (Exception error) when (IsDiscordFailure(error)) { joinProblem = error.Message; }
            }
            return Finish(new(true, joined ? $"Calling {person.Name} in {plan.ChannelName}."
                : joinProblem is not null ? $"Rang {person.Name} in {plan.ChannelName}, but Martlet couldn't join: {joinProblem}"
                : $"Rang {person.Name} in {plan.ChannelName}; Martlet joins once it can talk in Discord voice.", plan, channel, joined, invite is not null));
        }
        catch (Exception error) when (IsDiscordFailure(error))
        {
            return Finish(new(false, $"Couldn't call {person!.Name}: {error.Message}"));
        }

        DiscordCallResult Finish(DiscordCallResult result)
        {
            lock (gate) lastCall = result.Message;
            Changed?.Invoke();
            return result;
        }
    }

    /// <summary>The call channel Martlet is using now with whom (someone is in it), or null.</summary>
    public DiscordCallChannel? ActiveCall()
    {
        if (Transport is not { Online: true } transport) return null;
        return State.Calls.LastOrDefault(call => transport.Occupants(call.GuildId, call.ChannelId) > 0);
    }

    /// <summary>Removes call channels nobody came to or everyone left, and forgets ones already gone. Returns how many it removed.</summary>
    public async Task<int> SweepAsync(CancellationToken token)
    {
        if (Transport is not { Online: true } transport) return 0;
        var now = Now;
        var removed = 0;
        foreach (var call in State.Calls)
        {
            var exists = transport.VoiceChannels(call.GuildId).Any(channel => channel.Id == call.ChannelId);
            var occupants = exists ? transport.Occupants(call.GuildId, call.ChannelId) : 0;
            if (exists && !DiscordCalls.Expired(call, occupants, now))
            {
                var seen = DiscordCalls.Seen(call, occupants, now);
                if (seen != call) Update(current => current with { Calls = [.. current.Calls.Select(c => c.ChannelId == call.ChannelId ? seen : c)] });
                continue;
            }
            if (exists)
            {
                try { await transport.DeleteChannelAsync(call.ChannelId, token).ConfigureAwait(false); removed++; }
                catch (Exception error) when (IsDiscordFailure(error)) { Note($"Couldn't remove the call channel {call.Name}: {error.Message}"); continue; }
            }
            Update(current => current with { Calls = [.. current.Calls.Where(c => c.ChannelId != call.ChannelId)] });
        }
        return removed;
    }

    /// <summary>Ends a call now: forgets and removes its channel (when Martlet left it).</summary>
    public async Task EndCallAsync(ulong channelId, CancellationToken token)
    {
        if (State.Calls.All(call => call.ChannelId != channelId)) return;
        if (Transport is { Online: true } transport)
        {
            try { await transport.DeleteChannelAsync(channelId, token).ConfigureAwait(false); }
            catch (Exception error) when (IsDiscordFailure(error)) { Note($"Couldn't remove a call channel: {error.Message}"); return; }
        }
        Update(current => current with { Calls = [.. current.Calls.Where(c => c.ChannelId != channelId)] });
    }

    // ---------- presence ----------

    /// <summary>Shows what Martlet is doing as the bot's status, when it changed and no faster than Discord allows. Returns the
    /// presence sent, or null (unchanged or waiting; call again later to send a waiting one).</summary>
    public async Task<DiscordPresenceState?> PresenceAsync(DiscordPresenceInputs inputs, CancellationToken token)
    {
        if (Transport is not { Online: true } transport) return null;
        var wanted = DiscordPresence.For(inputs);
        DiscordPresenceState? send;
        lock (gate) send = presence.Offer(wanted, Now);
        if (send is null) return null;
        try { await transport.SetPresenceAsync(send, token).ConfigureAwait(false); }
        catch (Exception error) when (IsDiscordFailure(error)) { Note($"Couldn't update Discord status: {error.Message}"); return null; }
        Changed?.Invoke();
        return send;
    }

    /// <summary>After a reconnect Discord shows no status, so the next update is sent again.</summary>
    public void PresenceReset() { lock (gate) presence.Reset(); }

    // ---------- avatar ----------

    /// <summary>Sets the bot's avatar to a picture of the character (<paramref name="source"/> names it, such as the model ID), only
    /// when the character changed (or <paramref name="manual"/>) and no more often than <see cref="DiscordAvatarPolicy"/> allows.
    /// Returns what happened, for the owner.</summary>
    public async Task<string> AvatarAsync(Func<CancellationToken, Task<byte[]?>> picture, string source, bool manual, CancellationToken token,
        Func<CancellationToken, Task<byte[]?>>? banner = null)
    {
        if (Transport is not { Online: true } transport) return Avatar("Martlet's Discord bot isn't connected.");
        var current = State;
        var next = DiscordAvatarPolicy.NextAllowed(current, source, manual);
        if (next is null) return Avatar("The bot's picture already shows this character.");
        if (Now < next) return Avatar($"Discord allows few picture changes; the next one can be at {next.Value.ToLocalTime():t}.");
        var png = await picture(token).ConfigureAwait(false);
        if (png is not { Length: > 0 } || png.Length > DiscordAvatarPolicy.MaximumBytes)
            return Avatar("No picture of the character to use (show the character, or choose one with a picture).");
        var wide = banner is null ? null : await banner(token).ConfigureAwait(false);
        if (wide is { Length: > DiscordAvatarPolicy.MaximumBytes }) wide = null;
        try { await transport.SetAvatarAsync(png, wide, token).ConfigureAwait(false); }
        catch (Exception error) when (IsDiscordFailure(error))
        {
            // Count a refusal too, so a rate-limited Discord isn't asked again at once.
            Update(saved => saved with { AvatarUpdatedAt = Now });
            return Avatar($"Discord didn't take the new picture: {error.Message}");
        }
        var at = Now;
        Update(saved => saved with { AvatarUpdatedAt = at, AvatarSource = source });
        return Avatar($"Bot picture updated at {at.ToLocalTime():t}.");

        string Avatar(string message)
        {
            bool changed;
            lock (gate) { changed = lastAvatar != message; lastAvatar = message; }
            if (changed) Changed?.Invoke();
            return message;
        }
    }

    internal static bool IsDiscordFailure(Exception error) => error is RestException or HttpRequestException or TaskCanceledException or
        InvalidOperationException or IOException or TimeoutException or System.Net.WebSockets.WebSocketException;
}

/// <summary>The real Discord side of <see cref="DiscordCompanion"/>, over the bot's gateway connection and REST client.</summary>
public sealed class NetCordCompanionTransport(GatewayClient client) : IDiscordCompanionTransport
{
    public bool Online => client.Status == WebSocketStatus.Ready;
    public ulong BotId => client.Id;

    public IReadOnlyList<(ulong Id, string Name)> VoiceChannels(ulong guildId) =>
        client.Cache.Guilds.TryGetValue(guildId, out var guild)
            ? [.. guild.Channels.Values.OfType<VoiceGuildChannel>().Select(channel => (channel.Id, channel.Name))]
            : [];

    public int Occupants(ulong guildId, ulong channelId) =>
        client.Cache.Guilds.TryGetValue(guildId, out var guild)
            ? guild.VoiceStates.Values.Count(state => state.ChannelId == channelId && state.UserId != client.Id)
            : 0;

    public async Task<bool> IsMemberAsync(ulong guildId, ulong userId, CancellationToken token)
    {
        try
        {
            await client.Rest.GetGuildUserAsync(guildId, userId, cancellationToken: token).ConfigureAwait(false);
            return true;
        }
        catch (RestException error) when (error.StatusCode == System.Net.HttpStatusCode.NotFound) { return false; }
    }

    public async Task<ulong> CreateVoiceChannelAsync(ulong guildId, string name, IReadOnlyList<DiscordOverwrite> overwrites, CancellationToken token)
    {
        var channel = await client.Rest.CreateGuildChannelAsync(guildId, new GuildChannelProperties(name, ChannelType.VoiceGuildChannel)
        {
            PermissionOverwrites = [.. overwrites.Select(Overwrite)]
        }, cancellationToken: token).ConfigureAwait(false);
        return channel.Id;
    }

    public async Task SetOverwritesAsync(ulong channelId, IReadOnlyList<DiscordOverwrite> overwrites, CancellationToken token)
    {
        foreach (var overwrite in overwrites)
            await client.Rest.ModifyGuildChannelPermissionsAsync(channelId, Overwrite(overwrite), cancellationToken: token).ConfigureAwait(false);
    }

    public Task DeleteChannelAsync(ulong channelId, CancellationToken token) => client.Rest.DeleteChannelAsync(channelId, cancellationToken: token);

    public async Task<string> CreateInviteAsync(ulong channelId, CancellationToken token)
    {
        var invite = await client.Rest.CreateGuildChannelInviteAsync(channelId, new InviteProperties { MaxAge = 86_400, MaxUses = 1, Unique = true },
            cancellationToken: token).ConfigureAwait(false);
        return "https://discord.gg/" + invite.Code;
    }

    public async Task SendDirectAsync(ulong userId, string text, byte[]? png, CancellationToken token)
    {
        var dm = await client.Rest.GetDMChannelAsync(userId, cancellationToken: token).ConfigureAwait(false);
        var message = new MessageProperties { Content = text, AllowedMentions = AllowedMentionsProperties.None };
        using var stream = png is null ? null : new MemoryStream(png, writable: false);
        if (stream is not null) message.Attachments = [new AttachmentProperties("martlet.png", stream)];
        await client.Rest.SendMessageAsync(dm.Id, message, cancellationToken: token).ConfigureAwait(false);
    }

    public Task SetPresenceAsync(DiscordPresenceState presence, CancellationToken token) =>
        client.UpdatePresenceAsync(new PresenceProperties(presence.Status switch
        {
            DiscordPresenceStatus.Idle => UserStatusType.Idle,
            DiscordPresenceStatus.DoNotDisturb => UserStatusType.DoNotDisturb,
            _ => UserStatusType.Online
        })
        {
            Activities = [new UserActivityProperties("Custom Status", UserActivityType.Custom) { State = presence.Text }]
        }, cancellationToken: token).AsTask();

    public Task SetAvatarAsync(byte[] png, byte[]? banner, CancellationToken token) =>
        client.Rest.ModifyCurrentUserAsync(options =>
        {
            options.Avatar = new ImageProperties(ImageFormat.Png, png);
            if (banner is not null) options.Banner = new ImageProperties(ImageFormat.Png, banner);
        }, cancellationToken: token);

    private static PermissionOverwriteProperties Overwrite(DiscordOverwrite overwrite) =>
        new(overwrite.Id, overwrite.Role ? PermissionOverwriteType.Role : PermissionOverwriteType.User)
        {
            Allowed = (Permissions)(ulong)overwrite.Allowed,
            Denied = (Permissions)(ulong)overwrite.Denied
        };
}
