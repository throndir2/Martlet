using NetCord;
using NetCord.Gateway;

namespace Martlet.Discord.Tests;

public sealed class DiscordBotLifetimeTests
{
    // Not a real token: the bot ID part is base64 of 123456789012345678. The client is never started. It is built at run time so
    // no token-shaped text is in the repository.
    private static readonly string FakeToken = string.Join('.', "MTIzNDU2Nzg5MDEyMzQ1Njc4", "GaBcDe", new string('x', 38));

    [Fact]
    public async Task ReconnectShowsConnectingThenOnlineAndAFatalCloseLetsTheClientGo()
    {
        await using var bot = new DiscordBot();
        var client = new GatewayClient(new BotToken(FakeToken));
        bot.Adopt(client);
        bot.Publish(new(DiscordBotState.Online, "Martlet", 42, 1));

        bot.Disconnected(client, reconnect: true, null, null);
        Assert.Equal(DiscordBotState.Connecting, bot.Status.State);
        Assert.Equal(("Martlet", 42UL), (bot.Status.BotName, bot.Status.BotId));
        Assert.Contains("reconnecting", bot.Status.Problem);
        Assert.Same(client, bot.Client);

        bot.Resumed(client);
        Assert.Equal(DiscordBotState.Online, bot.Status.State);
        Assert.Null(bot.Status.Problem);

        var stale = new GatewayClient(new BotToken(FakeToken));
        bot.Disconnected(stale, reconnect: false, 4004, null);
        Assert.Equal(DiscordBotState.Online, bot.Status.State);
        stale.Dispose();

        bot.Disconnected(client, reconnect: false, 4004, null);
        Assert.Equal(DiscordBotState.Failed, bot.Status.State);
        Assert.Contains("rejected the bot token", bot.Status.Problem);
        Assert.Null(bot.Client);
        bot.Resumed(client);
        Assert.Equal(DiscordBotState.Failed, bot.Status.State);
    }

    [Fact]
    public async Task AWelcomeDmStillWaitingEndsWithTheConnection()
    {
        var preferences = new DiscordPreferences { OwnerUserId = 1, HomeGuildId = 2 };
        var transport = new WaitingTransport();
        var companion = new DiscordCompanion(null, () => preferences, update => { preferences = update(preferences); return true; })
        {
            Transport = transport
        };
        Assert.Equal(DiscordFriendAsk.Requested, companion.Ask(7, "Ana"));
        Assert.True(companion.Approve(7, "Hiyori"));
        var token = await transport.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(token.IsCancellationRequested);

        companion.CancelPending();
        Assert.True(token.IsCancellationRequested);
        await transport.Ended.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Task.Delay(50);
        Assert.Null(companion.LastProblem);
    }

    private sealed class WaitingTransport : IDiscordCompanionTransport
    {
        internal TaskCompletionSource<CancellationToken> Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Ended { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Online => true;
        public ulong BotId => 3;
        public IReadOnlyList<(ulong Id, string Name)> VoiceChannels(ulong guildId) => [];
        public int Occupants(ulong guildId, ulong channelId) => 0;
        public Task<bool> IsMemberAsync(ulong guildId, ulong userId, CancellationToken token) => Task.FromResult(true);
        public Task<ulong> CreateVoiceChannelAsync(ulong guildId, string name, IReadOnlyList<DiscordOverwrite> overwrites, CancellationToken token) =>
            Task.FromResult(1UL);
        public Task SetOverwritesAsync(ulong channelId, IReadOnlyList<DiscordOverwrite> overwrites, CancellationToken token) => Task.CompletedTask;
        public Task DeleteChannelAsync(ulong channelId, CancellationToken token) => Task.CompletedTask;
        public Task<string> CreateInviteAsync(ulong channelId, CancellationToken token) => Task.FromResult("https://discord.gg/x");

        public async Task SendDirectAsync(ulong userId, string text, byte[]? png, CancellationToken token)
        {
            Started.TrySetResult(token);
            try { await Task.Delay(Timeout.Infinite, token); }
            finally { Ended.TrySetResult(); }
        }

        public Task SetPresenceAsync(DiscordPresenceState presence, CancellationToken token) => Task.CompletedTask;
        public Task SetAvatarAsync(byte[] png, byte[]? banner, CancellationToken token) => Task.CompletedTask;
    }
}
