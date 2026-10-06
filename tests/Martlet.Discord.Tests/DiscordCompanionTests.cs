using Martlet.Discord;

namespace Martlet.Discord.Tests;

public sealed class DiscordCompanionTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 6, 12, 0, 0, TimeSpan.Zero);
    private const ulong Home = 5000, Owner = 1, Ana = 2, Bo = 3, Bot = 100;

    private sealed class Clock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    [Fact]
    public void FeaturesShareOneCommandListReplacedByName()
    {
        var commands = new DiscordCommands();
        commands.Add(DiscordCommands.Slash("join", "Join"), _ => default);
        commands.Add(DiscordCommands.Slash("friend", "Friend"), _ => default);
        commands.Add(DiscordCommands.Slash("friend", "Friend again"), _ => default);
        Assert.Equal(["friend", "join"], commands.Names);
        var friend = commands.Properties.Single(c => c.Name == "friend");
        Assert.Equal("Friend again", ((NetCord.Rest.SlashCommandProperties)friend).Description);
        Assert.Equal(2, friend.IntegrationTypes!.Count());
    }

    [Fact]
    public void FriendRequestMovesThroughAskApproveAndRemove()
    {
        var preferences = new DiscordPreferences { OwnerUserId = Owner };
        var (state, outcome) = DiscordFriends.Ask(new(), preferences, Ana, " Ana\n", T0);
        Assert.Equal(DiscordFriendAsk.Requested, outcome);
        Assert.Equal("Ana", Assert.Single(state.Requests).Name);
        Assert.Equal(DiscordFriendAsk.AlreadyPending, DiscordFriends.Ask(state, preferences, Ana, "Ana", T0).Outcome);
        Assert.Equal(DiscordFriendAsk.IsOwner, DiscordFriends.Ask(state, preferences, Owner, "Me", T0).Outcome);

        var approved = DiscordFriends.Approve(state, preferences, Ana)!.Value;
        Assert.Empty(approved.State.Requests);
        var person = Assert.Single(approved.Preferences.People);
        Assert.Equal((Ana, "Ana", true), (person.UserId, person.Name, person.MayCall));
        Assert.Equal(DiscordFriendAsk.AlreadyFriend, DiscordFriends.Ask(approved.State, approved.Preferences, Ana, "Ana", T0).Outcome);
        Assert.Null(DiscordFriends.Approve(approved.State, approved.Preferences, Bo));

        var (_, removed) = DiscordFriends.Remove(approved.State, approved.Preferences, Ana);
        Assert.Empty(removed.People);
        Assert.False(removed.Knows(Ana));
    }

    [Fact]
    public void DeclinedPeopleWaitAWeekAndThePendingListIsBounded()
    {
        var preferences = new DiscordPreferences { OwnerUserId = Owner };
        var (state, _) = DiscordFriends.Ask(new(), preferences, Bo, "Bo", T0);
        state = DiscordFriends.Decline(state, Bo, T0)!;
        Assert.Empty(state.Requests);
        Assert.Equal(DiscordFriendAsk.TryLater, DiscordFriends.Ask(state, preferences, Bo, "Bo", T0.AddDays(6)).Outcome);
        Assert.Equal(DiscordFriendAsk.Requested, DiscordFriends.Ask(state, preferences, Bo, "Bo", T0.AddDays(8)).Outcome);
        Assert.Null(DiscordFriends.Decline(new(), Bo, T0));

        var full = new DiscordCompanionState
        {
            Requests = [.. Enumerable.Range(10, DiscordFriends.MaximumPending).Select(i => new DiscordFriendRequest((ulong)i, "x", T0))]
        };
        Assert.Equal(DiscordFriendAsk.Full, DiscordFriends.Ask(full, preferences, 999, "Late", T0).Outcome);
    }

    [Fact]
    public void FindsFriendsAndMemoryPeopleOnlyByAnUnambiguousName()
    {
        DiscordPerson[] people = [new(Ana, "Ana Lima"), new(Bo, "Bo"), new(4, "Bo")];
        Assert.Equal(Ana, DiscordFriends.Find(people, "ana")!.UserId);
        Assert.Equal(Ana, DiscordFriends.Find(people, "Ana Lima.")!.UserId);
        Assert.Null(DiscordFriends.Find(people, "Bo"));
        Assert.Null(DiscordFriends.Find(people, "Cy"));
        Assert.Equal("ANA LIMA", DiscordFriends.MemoryName(people[0], ["Sam", "ANA LIMA"]));
        Assert.Null(DiscordFriends.MemoryName(people[0], ["Ana"]));
    }

    [Fact]
    public void CallChannelIsPrivateToThePersonTheBotAndTheOwner()
    {
        var preferences = new DiscordPreferences { OwnerUserId = Owner, HomeGuildId = Home, People = [new(Ana, "Ana")] };
        var plan = DiscordCalls.Plan(preferences, Bot, preferences.People[0], "Hiyori", []);
        Assert.Equal("Hiyori & Ana", plan.ChannelName);
        Assert.Null(plan.ReuseChannelId);
        var everyone = Assert.Single(plan.Overwrites, o => o.Role);
        Assert.Equal(Home, everyone.Id);
        Assert.True(everyone.Denied.HasFlag(DiscordPermission.ViewChannel | DiscordPermission.Connect));
        Assert.Equal(DiscordPermission.None, everyone.Allowed);
        foreach (var member in new[] { Ana, Bot, Owner })
            Assert.True(plan.Overwrites.Single(o => o.Id == member && !o.Role).Allowed.HasFlag(DiscordCalls.Talk));
        Assert.True(plan.Overwrites.Single(o => o.Id == Bot).Allowed.HasFlag(DiscordPermission.ManageChannels | DiscordPermission.CreateInstantInvite));
        Assert.Equal(4, plan.Overwrites.Count);
        // Every permission Martlet grants on the channel, it asks for in its home server (Discord rejects anything else).
        Assert.All(plan.Overwrites, o => Assert.Equal(o.Allowed, o.Allowed & DiscordInvite.HomeServerPermissions));

        Assert.Equal(77UL, DiscordCalls.Plan(preferences, Bot, preferences.People[0], "Hiyori", [(77, "Hiyori & Ana")]).ReuseChannelId);
        Assert.Equal("Martlet & Ana", DiscordCalls.ChannelName(" ", preferences.People[0]));
        // Calling the owner: no duplicate owner overwrite.
        Assert.Equal(3, DiscordCalls.Plan(preferences, Bot, new(Owner, "Me"), null, []).Overwrites.Count);
    }

    [Fact]
    public void CallsNeedAFriendWhoTakesCallsAHomeServerAndAConnection()
    {
        var preferences = new DiscordPreferences { HomeGuildId = Home, People = [new(Ana, "Ana"), new(Bo, "Bo", MayCall: false)] };
        Assert.Null(DiscordCalls.Problem(preferences, preferences.People[0], online: true));
        Assert.Contains("doesn't take calls", DiscordCalls.Problem(preferences, preferences.People[1], true));
        Assert.Contains("isn't connected", DiscordCalls.Problem(preferences, preferences.People[0], false));
        Assert.Contains("home server", DiscordCalls.Problem(preferences with { HomeGuildId = 0 }, preferences.People[0], true));
        Assert.Contains("isn't one of", DiscordCalls.Problem(preferences, new(9, "Stranger"), true));
        var ring = DiscordCalls.RingText("Hiyori", Home, 77, "https://discord.gg/x", voiceReady: false);
        Assert.Contains("https://discord.com/channels/5000/77", ring);
        Assert.Contains("https://discord.gg/x", ring);
        Assert.Contains("as soon as", ring);
        Assert.DoesNotContain("as soon as", DiscordCalls.RingText("Hiyori", Home, 77, null, voiceReady: true));
    }

    [Fact]
    public void CallChannelsExpireWhenUnansweredOrAfterEveryoneLeft()
    {
        var call = new DiscordCallChannel(Home, 77, Ana, "Martlet & Ana", T0);
        Assert.False(DiscordCalls.Expired(call, 0, T0.AddMinutes(9)));
        Assert.True(DiscordCalls.Expired(call, 0, T0.AddMinutes(10)));
        Assert.False(DiscordCalls.Expired(call, 1, T0.AddHours(3)));
        var seen = DiscordCalls.Seen(call, 1, T0.AddMinutes(30));
        Assert.False(DiscordCalls.Expired(seen, 0, T0.AddMinutes(31)));
        Assert.True(DiscordCalls.Expired(seen, 0, T0.AddMinutes(32)));
    }

    [Fact]
    public void PresenceFollowsWhatMartletIsDoing()
    {
        Assert.Equal(new DiscordPresenceState(DiscordPresenceStatus.Online, "Hanging out"), DiscordPresence.For(new()));
        Assert.Equal(new DiscordPresenceState(DiscordPresenceStatus.DoNotDisturb, "Talking with you"), DiscordPresence.For(new(Talking: true, Listening: true)));
        Assert.Equal(DiscordPresenceStatus.Idle, DiscordPresence.For(new(Paused: true, Talking: true)).Status);
        Assert.Equal(new DiscordPresenceState(DiscordPresenceStatus.Idle, "Away for a bit"), DiscordPresence.For(new(Away: true, Listening: true)));
        Assert.Equal("On a call with Ana", DiscordPresence.For(new(Paused: true, CallingWith: "Ana")).Text);
        Assert.Equal("Listening", DiscordPresence.For(new(Listening: true)).Text);
        Assert.Equal("Watching along", DiscordPresence.For(new(Listening: true, Watching: true)).Text);
        Assert.Equal(DiscordPresence.MaximumText, DiscordPresence.For(new(Activity: new string('a', 300))).Text.Length);
    }

    [Fact]
    public void PresenceIsSentOnlyOnChangeAndAtMostOncePerInterval()
    {
        var throttle = new DiscordPresenceThrottle(TimeSpan.FromSeconds(20));
        var online = new DiscordPresenceState(DiscordPresenceStatus.Online, "Hanging out");
        var busy = new DiscordPresenceState(DiscordPresenceStatus.DoNotDisturb, "Talking with you");
        Assert.Equal(online, throttle.Offer(online, T0));
        Assert.Null(throttle.Offer(online, T0.AddSeconds(30)));
        Assert.Equal(busy, throttle.Offer(busy, T0.AddSeconds(31)));
        Assert.Null(throttle.Offer(online, T0.AddSeconds(35)));
        Assert.Equal(online, throttle.Waiting);
        Assert.Equal(T0.AddSeconds(51), throttle.Due);
        Assert.Equal(online, throttle.Offer(online, T0.AddSeconds(51)));
        Assert.Null(throttle.Waiting);
        throttle.Reset();
        Assert.Equal(online, throttle.Offer(online, T0.AddSeconds(52)));
    }

    [Fact]
    public void AvatarChangesOnlyForANewCharacterAndRarely()
    {
        var never = new DiscordCompanionState();
        Assert.True(DiscordAvatarPolicy.Allowed(never, "model:a", manual: false, T0));
        var changed = never with { AvatarUpdatedAt = T0, AvatarSource = "model:a" };
        Assert.Null(DiscordAvatarPolicy.NextAllowed(changed, "model:a", manual: false));
        Assert.False(DiscordAvatarPolicy.Allowed(changed, "model:b", manual: false, T0.AddMinutes(29)));
        Assert.True(DiscordAvatarPolicy.Allowed(changed, "model:b", manual: false, T0.AddMinutes(30)));
        Assert.False(DiscordAvatarPolicy.Allowed(changed, "model:a", manual: true, T0.AddMinutes(9)));
        Assert.True(DiscordAvatarPolicy.Allowed(changed, "model:a", manual: true, T0.AddMinutes(10)));
    }

    [Fact]
    public async Task CompanionApprovesCallsSweepsAndRateLimitsAgainstARehearsalDiscord()
    {
        var directory = Path.Combine(Path.GetTempPath(), "martlet-discord-" + Guid.NewGuid().ToString("N"));
        try
        {
            var preferences = new DiscordPreferences { OwnerUserId = Owner, HomeGuildId = Home };
            var clock = new Clock(T0);
            var transport = new DiscordRehearsalTransport { BotId = Bot };
            var companion = new DiscordCompanion(directory, () => preferences, update => { preferences = update(preferences); return true; }, clock)
            {
                Transport = transport
            };
            DiscordFriendRequest? notified = null;
            companion.Requested += request => notified = request;
            Assert.Equal(DiscordFriendAsk.Requested, companion.Ask(Ana, "Ana"));
            Assert.Equal(Ana, notified!.UserId);
            Assert.Single(DiscordCompanionState.Load(directory).Requests);
            Assert.True(companion.Approve(Ana, "Hiyori"));
            Assert.True(preferences.Knows(Ana));
            Assert.Empty(DiscordCompanionState.Load(directory).Requests);

            var first = await companion.CallAsync(Ana, "Hiyori", null, CancellationToken.None);
            Assert.True(first.Rang, first.Message);
            Assert.True(first.InvitedToServer);
            Assert.False(first.Joined);
            var channel = transport.Channels[first.ChannelId];
            Assert.Equal("Hiyori & Ana", channel.Name);
            Assert.Equal(4, channel.Overwrites.Count);
            var ring = transport.Directs.Last(d => d.UserId == Ana).Text;
            Assert.Contains($"https://discord.com/channels/{Home}/{first.ChannelId}", ring);
            Assert.Contains("discord.gg/rehearsal", ring);

            transport.Members.Add(Ana);
            ulong joined = 0;
            var second = await companion.CallAsync(Ana, "Hiyori", (guild, id, _) => { joined = id; return Task.CompletedTask; }, CancellationToken.None);
            Assert.Equal(first.ChannelId, second.ChannelId);
            Assert.True(second.Joined);
            Assert.False(second.InvitedToServer);
            Assert.Equal(first.ChannelId, joined);
            Assert.Single(companion.State.Calls);

            transport.SetOccupants(first.ChannelId, 1);
            clock.Now = T0.AddMinutes(20);
            Assert.Equal(0, await companion.SweepAsync(CancellationToken.None));
            Assert.Equal(Ana, companion.ActiveCall()!.UserId);
            transport.SetOccupants(first.ChannelId, 0);
            clock.Now = T0.AddMinutes(23);
            Assert.Equal(1, await companion.SweepAsync(CancellationToken.None));
            Assert.Empty(companion.State.Calls);
            Assert.Contains(first.ChannelId, transport.Deleted);

            Assert.True(companion.AllowCalls(Ana, false));
            Assert.False((await companion.CallAsync(Ana, "Hiyori", null, CancellationToken.None)).Rang);

            var picture = new byte[] { 1, 2, 3 };
            Assert.StartsWith("Bot picture updated", await companion.AvatarAsync(_ => Task.FromResult<byte[]?>(picture), "model:a", false, CancellationToken.None,
                _ => Task.FromResult<byte[]?>(picture)));
            Assert.Equal(1, transport.Banners);
            Assert.StartsWith("The bot's picture already", await companion.AvatarAsync(_ => Task.FromResult<byte[]?>(picture), "model:a", false, CancellationToken.None));
            Assert.StartsWith("Discord allows few", await companion.AvatarAsync(_ => Task.FromResult<byte[]?>(picture), "model:b", false, CancellationToken.None));
            Assert.Equal(1, transport.Avatars);

            Assert.NotNull(await companion.PresenceAsync(new(Talking: true), CancellationToken.None));
            Assert.Null(await companion.PresenceAsync(new(), CancellationToken.None));
            Assert.Equal("Talking with you", Assert.Single(transport.Presences).Text);
            Assert.Equal("Talking with you", companion.Presence!.Text);

            Assert.True(companion.Remove(Ana));
            Assert.False(preferences.Knows(Ana));
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }
}
