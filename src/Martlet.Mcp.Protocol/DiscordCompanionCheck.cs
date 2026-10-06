using System.IO;
using System.Globalization;
using Martlet.Discord;

namespace Martlet.Mcp;

/// <summary>discord_companion_check: Martlet's Discord companion state as saved in a data directory (friends, waiting friend
/// requests, call channels it made, when the bot's picture last changed; counts and names, never tokens), then a rehearsal of the
/// production <see cref="DiscordCompanion"/> against an in-memory Discord (<see cref="DiscordRehearsalTransport"/>; no token, contacts
/// nothing): a /friend ask, the owner's approval, a call (the private channel's permission overwrites, the ring DM with its server
/// invite, reusing the channel and joining), the cleanup of the call channel, presence mapping and its rate limit, and the
/// avatar rate limit. With <c>requestFrom</c> (and an explicit dataDirectory) it also files a friend request into that directory's
/// discord-companion.json, as /friend ask would, so Companion › Discord shows it for approval.</summary>
internal static class DiscordCompanionCheck
{
    internal static async Task<object> RunAsync(string directory, bool explicitDirectory, string? person, string? character,
        ulong? requestFrom, string? requestName, CancellationToken cancellation)
    {
        person = string.IsNullOrWhiteSpace(person) ? "Ana" : person.Trim();
        if (person.Length > 64 || person.Any(char.IsControl)) throw new ArgumentException("'person' must be 1-64 characters of one-line text.");
        if (character is { Length: > 64 } || character?.Any(char.IsControl) == true) throw new ArgumentException("'character' must be one line, at most 64 characters.");

        object? filed = null;
        if (requestFrom is { } from)
        {
            if (!explicitDirectory) throw new ArgumentException("requestFrom writes a friend request; pass an explicit (disposable) dataDirectory.");
            if (from == 0) throw new ArgumentException("requestFrom must be a Discord user ID.");
            var saved = DiscordPreferences.Load(directory);
            var (next, outcome) = DiscordFriends.Ask(DiscordCompanionState.Load(directory), saved, from, requestName ?? "Rehearsal friend",
                DateTimeOffset.UtcNow);
            var written = outcome == DiscordFriendAsk.Requested && next.Save(directory);
            filed = new { outcome = outcome.ToString(), written };
        }

        var preferences = DiscordPreferences.Load(directory);
        var state = DiscordCompanionState.Load(directory);
        var current = new
        {
            configured = preferences.Configured,
            enabled = preferences.Enabled,
            homeServer = preferences.HomeGuildId != 0,
            friends = preferences.People.Count,
            friendsWhoTakeCalls = preferences.People.Count(p => p.MayCall),
            friendNames = preferences.People.Select(p => p.Name).ToArray(),
            requestsWaiting = state.Requests.Count,
            requestNames = state.Requests.Select(r => r.Name).ToArray(),
            recentlyDeclined = state.Declined.Count,
            callChannels = state.Calls.Select(c => c.Name).ToArray(),
            avatarUpdatedAt = state.AvatarUpdatedAt?.ToString("o", CultureInfo.InvariantCulture),
            avatarNextAutomatic = state.AvatarUpdatedAt is { } at ? (at + DiscordAvatarPolicy.Interval).ToString("o", CultureInfo.InvariantCulture) : null
        };

        var rehearsal = await RehearseAsync(person, character, cancellation);
        return new { current, filed, rehearsal };
    }

    private static async Task<object> RehearseAsync(string person, string? character, CancellationToken cancellation)
    {
        const ulong home = 5000, owner = 1, friend = 2, bot = 100;
        var scratch = Path.Combine(Path.GetTempPath(), "martlet-discord-rehearsal-" + Guid.NewGuid().ToString("N"));
        try
        {
            var preferences = new DiscordPreferences { OwnerUserId = owner, HomeGuildId = home };
            var transport = new DiscordRehearsalTransport { BotId = bot };
            var companion = new DiscordCompanion(scratch, () => preferences, update => { preferences = update(preferences); return true; })
            {
                Transport = transport
            };
            var asked = companion.Ask(friend, person);
            var askedAgain = companion.Ask(friend, person);
            var waiting = companion.State.Requests.Count;
            var approved = companion.Approve(friend, character);
            await Task.Delay(50, cancellation);
            var call = await companion.CallAsync(friend, character, null, cancellation);
            var channel = transport.Channels.TryGetValue(call.ChannelId, out var made) ? made : default;
            transport.Members.Add(friend);
            var joinedChannel = 0UL;
            var again = await companion.CallAsync(friend, character, (_, id, _) => { joinedChannel = id; return Task.CompletedTask; }, cancellation);
            var removedNow = await companion.SweepAsync(cancellation);
            await companion.EndCallAsync(call.ChannelId, cancellation);
            companion.AllowCalls(friend, false);
            var refused = await companion.CallAsync(friend, character, null, cancellation);
            var left = companion.Remove(friend);

            var throttle = new DiscordPresenceThrottle();
            var now = DateTimeOffset.UtcNow;
            var firstPresence = throttle.Offer(DiscordPresence.For(new(Talking: true)), now);
            var tooSoon = throttle.Offer(DiscordPresence.For(new()), now.AddSeconds(5));
            var later = throttle.Offer(DiscordPresence.For(new()), now + DiscordPresence.MinimumInterval);

            var avatarState = new DiscordCompanionState { AvatarUpdatedAt = now, AvatarSource = "model:a" };
            return new
            {
                friendRequest = new
                {
                    asked = asked.ToString(),
                    askedAgain = askedAgain.ToString(),
                    waiting,
                    approved,
                    welcomeDm = transport.Directs.Any(d => d.UserId == friend && d.Text.Contains("friends", StringComparison.Ordinal)),
                    removedByThemselves = left,
                    knownAfterRemove = preferences.Knows(friend)
                },
                call = new
                {
                    rang = call.Rang,
                    message = call.Message,
                    channelName = channel.Name,
                    overwrites = channel.Overwrites?.Select(o => new
                    {
                        who = o.Role ? "@everyone" : o.Id == friend ? "friend" : o.Id == bot ? "bot" : o.Id == owner ? "owner" : "other",
                        allowed = o.Allowed.ToString(),
                        denied = o.Denied.ToString()
                    }).ToArray(),
                    ringDm = transport.Directs.LastOrDefault(d => d.UserId == friend && d.Text.Contains("calling", StringComparison.Ordinal)).Text,
                    invitedToServer = call.InvitedToServer,
                    reusedChannel = again.ChannelId == call.ChannelId,
                    joinedSecondCall = again.Joined && joinedChannel == call.ChannelId,
                    sweptWhileFresh = removedNow,
                    deletedAfterEnd = transport.Deleted.Contains(call.ChannelId),
                    refusedWhenCallsOff = refused.Message
                },
                presence = new
                {
                    mapping = new[]
                    {
                        Describe("nothing", new()), Describe("listening", new(Listening: true)), Describe("watching", new(Watching: true)),
                        Describe("talking", new(Talking: true)), Describe("paused", new(Paused: true)), Describe("away", new(Away: true)),
                        Describe("on a call", new(CallingWith: person))
                    },
                    firstSent = firstPresence?.Text,
                    changeWithinIntervalHeld = tooSoon is null,
                    sentAfterInterval = later?.Text,
                    minimumIntervalSeconds = DiscordPresence.MinimumInterval.TotalSeconds
                },
                avatar = new
                {
                    sameCharacterSkipped = DiscordAvatarPolicy.NextAllowed(avatarState, "model:a", manual: false) is null,
                    newCharacterTooSoon = !DiscordAvatarPolicy.Allowed(avatarState, "model:b", manual: false, now.AddMinutes(5)),
                    newCharacterLater = DiscordAvatarPolicy.Allowed(avatarState, "model:b", manual: false, now + DiscordAvatarPolicy.Interval),
                    manualMinimumMinutes = DiscordAvatarPolicy.ManualInterval.TotalMinutes,
                    automaticMinimumMinutes = DiscordAvatarPolicy.Interval.TotalMinutes
                }
            };
        }
        finally
        {
            try { if (Directory.Exists(scratch)) Directory.Delete(scratch, recursive: true); }
            catch (IOException) { }
        }

        static object Describe(string when, DiscordPresenceInputs inputs)
        {
            var state = DiscordPresence.For(inputs);
            return new { when, status = state.Status.ToString(), text = state.Text };
        }
    }
}
