using System.Runtime.CompilerServices;
using Martlet.Core.Contracts;
using Martlet.Core.Settings;
using Martlet.Discord;
using Martlet.Providers;
using NetCord;
using NetCord.Gateway;
using NetCord.Gateway.Voice;
using NetCord.Rest;

namespace Martlet.Desktop;

/// <summary>Discord voice: Martlet joins server voice channels (with /join, when someone moves it into one, or following the
/// owner), hears each person separately, transcribes their utterances, answers through the reply engine and speaks into the
/// call. It never uses this PC's microphone or speakers. Voice is end-to-end encrypted (DAVE) by NetCord with libdave.</summary>
internal sealed partial class DiscordService
{
    private static readonly TimeSpan AloneCheck = TimeSpan.FromSeconds(15);
    private readonly DiscordVoiceRooms rooms = new();
    private readonly Dictionary<ulong, VoiceRoom> voiceRooms = [];
    private readonly Lock voiceGate = new();
    private GatewayClient? voiceHooked;
    private Timer? aloneTimer;
    private DiscordVoiceNativeReport? natives;
    private string? voiceProblem;
    private DiscordVoiceCounts ended = new(0, 0, 0, 0, 0, 0, 0, false, null);

    /// <summary>Speech-to-text and voice for Discord calls (set by the desktop); null until wired.</summary>
    internal IDiscordSpeech? Speech { get; set; }

    /// <summary>Raised when Martlet leaves a voice channel for any reason (guild ID, channel ID).</summary>
    internal event Action<ulong, ulong>? VoiceLeft;

    /// <summary>Raised when the last person leaves Martlet's voice channel (guild ID, channel ID), before the alone timeout.</summary>
    internal event Action<ulong, ulong>? VoiceEmptied;

    internal DiscordVoiceNativeReport Natives => natives ??= DiscordVoiceNatives.Check();

    internal DiscordVoiceStatus VoiceStatus
    {
        get
        {
            var room = Rooms().FirstOrDefault();
            var counts = room?.Conversation.Counts;
            var total = Sum(ended, counts);
            var client = Bot.Client;
            return new(room is not null, room is null ? null : GuildName(client, room.GuildId),
                room is null ? null : ChannelName(client, room.GuildId, room.ChannelId), total.SpeakersHeard, total.Utterances,
                total.Transcribed, total.Replies, total.Spoken, total.BargeIns, room is not null && Natives.LibDaveLoaded,
                Natives.Ok, counts?.LastError ?? voiceProblem);
        }
    }

    /// <summary>One line for Companion › Discord and MCP (DiscordVoiceStatus): where Martlet is, what it heard and said (counts
    /// only), DAVE and the natives, and the last problem.</summary>
    internal string VoiceSummary
    {
        get
        {
            var status = VoiceStatus;
            var parts = new List<string?>
            {
                status.Connected ? $"Voice: in {status.Channel} ({status.Guild})" : "Voice: not in a voice channel",
                $"{status.SpeakersHeard} speakers heard, {status.Transcribed} utterances transcribed, {status.Spoken} replies spoken",
                status.Connected ? (status.DaveActive ? "DAVE on" : "DAVE off") : null,
                status.NativesLoaded ? $"libdave loaded (DAVE v{Natives.DaveProtocolVersion})" : $"libdave not loaded: {Natives.Problem}",
                status.LastError is { } error ? "Last problem: " + error : null
            };
            return string.Join(" · ", parts.OfType<string>());
        }
    }

    private void InitializeVoice()
    {
        Bot.Commands.Add(GuildCommand("join", "Martlet joins the voice channel you're in"), JoinCommandAsync);
        Bot.Commands.Add(GuildCommand("leave", "Martlet leaves the voice channel"), LeaveCommandAsync);
        Bot.Ready += client =>
        {
            lock (voiceGate)
            {
                if (voiceHooked == client) return default;
                voiceHooked = client;
            }
            client.VoiceStateUpdate += state => OnVoiceStateAsync(client, state);
            return default;
        };
        Bot.Changed += state =>
        {
            if (state.State is DiscordBotState.Off or DiscordBotState.Failed && Rooms().Count > 0) LeaveAllAsync().Forget();
        };
        aloneTimer = new(_ => LeaveEmptyRooms(), null, AloneCheck, AloneCheck);
    }

    /// <summary>Joins (or moves to) a voice channel in a server and starts listening and speaking there. The companion feature
    /// calls this to "call" someone in a private channel.</summary>
    internal async Task JoinVoiceAsync(ulong guildId, ulong channelId, CancellationToken token)
    {
        if (Bot.Client is not { } client) throw new InvalidOperationException("The Discord bot isn't connected.");
        if (!Natives.Ok) throw new InvalidOperationException(Natives.Problem ?? "Discord voice's libraries didn't load.");
        if (!rooms.TryBeginJoin(guildId, channelId)) return;
        await CloseRoomAsync(guildId, leaveChannel: false, raise: false).ConfigureAwait(false);
        VoiceClient? connection = null;
        DiscordVoiceConversation? conversation = null;
        try
        {
            connection = await client.JoinVoiceChannelAsync(guildId, channelId, new VoiceClientConfiguration(),
                TimeSpan.FromSeconds(10), cancellationToken: token).ConfigureAwait(false);
            var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var voiceClient = connection;
            connection.Ready += () => { ready.TrySetResult(); return default; };
            var transport = new NetCordVoiceTransport(connection);
            conversation = new(new(channelId, guildId, ChannelName(client, guildId, channelId), false), transport,
                Speech ?? NoSpeech.Instance, () => Replies, () => VoiceContext(guildId, channelId),
                ssrc => voiceClient.Cache.SsrcUsers.TryGetValue(ssrc, out var user) ? user : null);
            var heard = conversation;
            connection.VoiceReceive += args =>
            {
                heard.Receive(args.Ssrc, args.SequenceNumber, args.Frame);
                return default;
            };
            connection.Disconnect += args =>
            {
                if (!args.Reconnect) OnConnectionLost(guildId, voiceClient).Forget();
                return default;
            };
            conversation.Changed += () => Changed?.Invoke();
            await connection.StartAsync(token).ConfigureAwait(false);
            await ready.Task.WaitAsync(TimeSpan.FromSeconds(15), token).ConfigureAwait(false);
            transport.Start();
            lock (voiceGate) voiceRooms[guildId] = new(guildId, channelId, connection, transport, conversation);
            rooms.Joined(guildId, channelId);
            voiceProblem = null;
            rooms.People(guildId, Humans(client, guildId, channelId, null), DateTimeOffset.UtcNow);
            ErrorLog.Info($"Discord voice: joined a channel ({Humans(client, guildId, channelId, null)} people there).");
        }
        catch (Exception error)
        {
            rooms.Left(guildId);
            if (conversation is not null) await conversation.DisposeAsync().ConfigureAwait(false);
            connection?.Dispose();
            voiceProblem = error is TimeoutException ? "Discord voice didn't connect in time." : $"Couldn't join voice: {error.Message}";
            ErrorLog.Warn("Discord voice: joining failed.", error);
            try { await client.UpdateVoiceStateAsync(new(guildId, null)).ConfigureAwait(false); }
            catch (Exception leave) when (leave is not OutOfMemoryException) { }
            Changed?.Invoke();
            throw;
        }
        Changed?.Invoke();
    }

    /// <summary>Leaves the voice channel Martlet is in on a server, if any.</summary>
    internal async Task LeaveVoiceAsync(ulong guildId)
    {
        if (!rooms.TryBeginLeave(guildId)) return;
        await CloseRoomAsync(guildId, leaveChannel: true, raise: true).ConfigureAwait(false);
    }

    private async Task LeaveAllAsync()
    {
        foreach (var room in Rooms()) await LeaveVoiceAsync(room.GuildId).ConfigureAwait(false);
    }

    private async Task CloseRoomAsync(ulong guildId, bool leaveChannel, bool raise)
    {
        VoiceRoom? room;
        lock (voiceGate) voiceRooms.Remove(guildId, out room);
        if (room is not null)
        {
            var counts = room.Conversation.Counts;
            await room.Conversation.DisposeAsync().ConfigureAwait(false);
            lock (voiceGate) ended = Sum(ended, counts) with { LastError = counts.LastError };
            await room.Transport.DisposeAsync().ConfigureAwait(false);
            try { await room.Connection.CloseAsync().ConfigureAwait(false); }
            catch (Exception error) when (error is InvalidOperationException or ObjectDisposedException or System.Net.WebSockets.WebSocketException) { }
            room.Connection.Dispose();
        }
        if (leaveChannel && Bot.Client is { } client)
        {
            try { await client.UpdateVoiceStateAsync(new(guildId, null)).ConfigureAwait(false); }
            catch (Exception error) when (error is InvalidOperationException or ObjectDisposedException or System.Net.WebSockets.WebSocketException) { }
        }
        if (!raise) return;
        var channel = rooms.Left(guildId) ?? room?.ChannelId;
        if (channel is { } left) VoiceLeft?.Invoke(guildId, left);
        Changed?.Invoke();
    }

    private async Task OnConnectionLost(ulong guildId, VoiceClient connection)
    {
        VoiceRoom? room;
        lock (voiceGate) room = voiceRooms.GetValueOrDefault(guildId);
        if (room?.Connection != connection || !rooms.TryBeginLeave(guildId)) return;
        voiceProblem = "Discord closed the voice connection.";
        await CloseRoomAsync(guildId, leaveChannel: true, raise: true).ConfigureAwait(false);
    }

    private async ValueTask OnVoiceStateAsync(GatewayClient client, VoiceState state)
    {
        try
        {
            var botId = Bot.Status.BotId;
            if (state.UserId == botId) await OwnStateAsync(state).ConfigureAwait(false);
            else
            {
                var preferences = Preferences;
                if (state.UserId == preferences.OwnerUserId && rooms.Phase(state.GuildId) != DiscordVoicePhase.Joining &&
                    DiscordVoiceRules.FollowOwner(preferences.VoiceFollowOwner, state.ChannelId, rooms.Channel(state.GuildId)) is { } follow &&
                    preferences.VoiceChat != DiscordChatMode.Off)
                    JoinQuietlyAsync(state.GuildId, follow).Forget();
                if (rooms.Channel(state.GuildId) is { } channel && rooms.Phase(state.GuildId) == DiscordVoicePhase.Connected &&
                    rooms.People(state.GuildId, Humans(client, state.GuildId, channel, state), DateTimeOffset.UtcNow))
                    VoiceEmptied?.Invoke(state.GuildId, channel);
            }
            Changed?.Invoke();
        }
        catch (Exception error) when (error is not OutOfMemoryException) { ErrorLog.Warn("Discord voice: a voice state update failed.", error); }
    }

    // Martlet's own voice state: someone moved it (with Move Members) into another channel, or disconnected it.
    private async Task OwnStateAsync(VoiceState state)
    {
        var phase = rooms.Phase(state.GuildId);
        if (phase is DiscordVoicePhase.Joining or DiscordVoicePhase.Leaving) return;
        if (state.ChannelId is not { } channel)
        {
            if (phase == DiscordVoicePhase.Connected && rooms.TryBeginLeave(state.GuildId))
                await CloseRoomAsync(state.GuildId, leaveChannel: false, raise: true).ConfigureAwait(false);
            return;
        }
        if (rooms.Channel(state.GuildId) != channel) JoinQuietlyAsync(state.GuildId, channel).Forget();
    }

    private async Task JoinQuietlyAsync(ulong guildId, ulong channelId)
    {
        try { await JoinVoiceAsync(guildId, channelId, CancellationToken.None).ConfigureAwait(false); }
        catch (Exception error) when (error is not OutOfMemoryException) { }
    }

    private void LeaveEmptyRooms()
    {
        var minutes = Math.Max(1, Preferences.VoiceLeaveAloneMinutes);
        foreach (var guild in rooms.AloneTooLong(DateTimeOffset.UtcNow, TimeSpan.FromMinutes(minutes)))
        {
            ErrorLog.Info($"Discord voice: leaving after {minutes} minutes alone.");
            LeaveVoiceAsync(guild).Forget();
        }
    }

    private async ValueTask JoinCommandAsync(SlashCommandInteraction interaction)
    {
        var problem = interaction.GuildId is not { } guildId ? "Use /join in a server, from a voice channel."
            : Preferences.VoiceChat == DiscordChatMode.Off ? "Voice chat is off in Martlet (Companion › Discord)."
            : !Natives.Ok ? $"Martlet's voice can't start here: {Natives.Problem}"
            : Bot.Client?.Cache.Guilds.GetValueOrDefault(guildId)?.VoiceStates.GetValueOrDefault(interaction.User.Id)?.ChannelId is not { }
                ? "Join a voice channel first, then use /join." : null;
        if (problem is not null) { await ReplyAsync(interaction, problem).ConfigureAwait(false); return; }
        var guild = interaction.GuildId!.Value;
        var channel = Bot.Client!.Cache.Guilds[guild].VoiceStates[interaction.User.Id].ChannelId!.Value;
        await ReplyAsync(interaction, $"Joining <#{channel}>…").ConfigureAwait(false);
        await JoinQuietlyAsync(guild, channel).ConfigureAwait(false);
    }

    private async ValueTask LeaveCommandAsync(SlashCommandInteraction interaction)
    {
        if (interaction.GuildId is not { } guildId || rooms.Phase(guildId) == DiscordVoicePhase.Idle)
        {
            await ReplyAsync(interaction, "I'm not in a voice channel here.").ConfigureAwait(false);
            return;
        }
        await ReplyAsync(interaction, "Leaving the voice channel.").ConfigureAwait(false);
        await LeaveVoiceAsync(guildId).ConfigureAwait(false);
    }

    private static async Task ReplyAsync(SlashCommandInteraction interaction, string text)
    {
        try
        {
            await interaction.SendResponseAsync(InteractionCallback.Message(new InteractionMessageProperties()
                .WithContent(text).WithFlags(MessageFlags.Ephemeral))).ConfigureAwait(false);
        }
        catch (Exception error) when (error is RestException or System.Net.Http.HttpRequestException or TaskCanceledException)
        {
            ErrorLog.Warn("Discord voice: couldn't answer a slash command.", error);
        }
    }

    private static SlashCommandProperties GuildCommand(string name, string description) => new(name, description)
    {
        IntegrationTypes = [ApplicationIntegrationType.GuildInstall],
        Contexts = [InteractionContextType.Guild]
    };

    private DiscordVoiceContext VoiceContext(ulong guildId, ulong channelId)
    {
        var client = Bot.Client;
        var status = Bot.Status;
        var guild = client?.Cache.Guilds.GetValueOrDefault(guildId);
        var nickname = guild?.Users.GetValueOrDefault(status.BotId)?.Nickname;
        string[] names = [.. new[] { "Martlet", status.BotName, nickname }.OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase)];
        var preferences = Preferences;
        return new(preferences.VoiceChat, names, Humans(client, guildId, channelId, null), status.BotId, id =>
        {
            var user = guild?.Users.GetValueOrDefault(id);
            var name = user?.Nickname ?? user?.GlobalName ?? user?.Username ??
                preferences.People.FirstOrDefault(person => person.UserId == id)?.Name ?? "someone";
            return new(id, name, id == preferences.OwnerUserId);
        });
    }

    // People (not bots, not Martlet) in a voice channel, from the cache with the update just received applied.
    private int Humans(GatewayClient? client, ulong guildId, ulong channelId, VoiceState? update)
    {
        if (client?.Cache.Guilds.GetValueOrDefault(guildId) is not { } guild) return 0;
        var states = guild.VoiceStates.ToDictionary(pair => pair.Key, pair => pair.Value.ChannelId);
        if (update is not null) states[update.UserId] = update.ChannelId;
        var botId = Bot.Status.BotId;
        return states.Count(pair => pair.Value == channelId && pair.Key != botId &&
            guild.Users.GetValueOrDefault(pair.Key)?.IsBot != true);
    }

    private static string GuildName(GatewayClient? client, ulong guildId) =>
        client?.Cache.Guilds.GetValueOrDefault(guildId)?.Name ?? "a server";

    private static string ChannelName(GatewayClient? client, ulong guildId, ulong channelId) =>
        client?.Cache.Guilds.GetValueOrDefault(guildId)?.Channels.GetValueOrDefault(channelId)?.Name ?? "a voice channel";

    private List<VoiceRoom> Rooms() { lock (voiceGate) return [.. voiceRooms.Values]; }

    private static DiscordVoiceCounts Sum(DiscordVoiceCounts a, DiscordVoiceCounts? b) => b is null ? a : new(
        a.SpeakersHeard + b.SpeakersHeard, a.Utterances + b.Utterances, a.Transcribed + b.Transcribed, a.Replies + b.Replies,
        a.Spoken + b.Spoken, a.BargeIns + b.BargeIns, a.LostFrames + b.LostFrames, b.Speaking, b.LastError ?? a.LastError);

    private sealed record VoiceRoom(ulong GuildId, ulong ChannelId, VoiceClient Connection, NetCordVoiceTransport Transport,
        DiscordVoiceConversation Conversation);

    /// <summary>Martlet's voice into the call: one Opus frame per write to NetCord's voice stream, which paces them at real time.</summary>
    private sealed class NetCordVoiceTransport(VoiceClient connection) : IDiscordVoiceTransport, IAsyncDisposable
    {
        private System.IO.Stream? stream;

        public void Start() => stream = connection.CreateVoiceStream(new VoiceStreamConfiguration { NormalizeSpeed = true });

        public ValueTask SpeakingAsync(bool speaking, CancellationToken token) =>
            connection.EnterSpeakingStateAsync(new(speaking ? SpeakingFlags.Microphone : default), cancellationToken: token);

        public ValueTask SendAsync(ReadOnlyMemory<byte> opusFrame, CancellationToken token) =>
            stream is { } open ? open.WriteAsync(opusFrame, token) : default;

        public async ValueTask DisposeAsync()
        {
            if (stream is { } open) await open.DisposeAsync().ConfigureAwait(false);
        }
    }

    private sealed class NoSpeech : IDiscordSpeech
    {
        public static NoSpeech Instance { get; } = new();
        public string? ListenProblem => "Discord voice isn't wired to speech-to-text yet.";
        public string? SpeakProblem => "Discord voice isn't wired to a voice yet.";
        public Task<string> TranscribeAsync(ReadOnlyMemory<byte> pcm16kMono, CancellationToken token) => Task.FromResult("");
        public async IAsyncEnumerable<DiscordSpeechAudio> SpeakAsync(string text, [EnumeratorCancellation] CancellationToken token)
        {
            await Task.CompletedTask;
            yield break;
        }
    }
}

internal sealed record DiscordVoiceStatus(bool Connected, string? Guild, string? Channel, int SpeakersHeard, int Utterances,
    int Transcribed, int Replies, int Spoken, int BargeIns, bool DaveActive, bool NativesLoaded, string? LastError);

/// <summary>Discord voice's speech-to-text and voice, from Martlet's own setup and never the cloud: other people's voices are
/// transcribed only by Parakeet on this PC or the paired computer that Listening uses (as Add a voice does), and replies are
/// spoken by a paired host's voice or a Windows voice (the Windows voice in Its voice, or Windows' recommended voice when Its
/// voice is a cloud voice). While the local conversation is replying, transcription waits for it, so Discord never holds the
/// shared speech model when the owner is talking to Martlet here.</summary>
internal sealed class DiscordSpeech(Func<IReadOnlyList<SetupRoute>?> routes, ParakeetListener? parakeet, string? dataDirectory,
    Func<bool> localBusy) : IDiscordSpeech, IDisposable
{
    private readonly SemaphoreSlim transcribing = new(1, 1);
    private readonly Lock gate = new();
    private RecordingTranscriber? transcriber;
    private string? transcriberKey;
    private string? windowsVoice;

    public string? ListenProblem => Transcriber() is null
        ? "Discord voice transcribes on this PC or a paired computer: download Parakeet in Companion › Listening." : null;

    public string? SpeakProblem => null;

    public async Task<string> TranscribeAsync(ReadOnlyMemory<byte> pcm16kMono, CancellationToken token)
    {
        var chosen = Transcriber() ?? throw new InvalidOperationException(ListenProblem);
        await transcribing.WaitAsync(token).ConfigureAwait(false);
        try
        {
            for (var waited = 0; localBusy() && waited < 300; waited++) await Task.Delay(50, token).ConfigureAwait(false);
            return await chosen.TranscribePcmAsync(pcm16kMono, token).ConfigureAwait(false);
        }
        finally { transcribing.Release(); }
    }

    public async IAsyncEnumerable<DiscordSpeechAudio> SpeakAsync(string text, [EnumeratorCancellation] CancellationToken token)
    {
        var tts = routes()?.FirstOrDefault(route => route.Role == SetupRole.Tts);
        var input = new BoundedSpeechInput(text.Length > 1000 ? text[..1000] : text);
        if (tts is { Enabled: true } && tts.Consent == tts.Selection() && dataDirectory is not null &&
            LiveConversationConfiguration.HostSpeechTargetOf(tts) is { } host)
        {
            var ids = new CorrelationIds { SessionId = Guid.NewGuid(), TurnId = Guid.NewGuid(), RequestId = Guid.NewGuid() };
            await foreach (var chunk in new HostSpeechClient(dataDirectory).StreamAsync(host, input, ids, 0,
                               DateTimeOffset.UtcNow.AddSeconds(30), token).ConfigureAwait(false))
                yield return new(24_000, chunk);
            yield break;
        }
        var voice = LiveConversationConfiguration.WindowsVoiceTargetOf(tts)?.VoiceId ?? await WindowsVoiceAsync(token).ConfigureAwait(false);
        var pcm = await Task.Run(() => WindowsVoices.Synthesize(voice, input.Text, DateTimeOffset.UtcNow.AddSeconds(30), token), token)
            .ConfigureAwait(false);
        yield return new(24_000, pcm);
    }

    private async Task<string> WindowsVoiceAsync(CancellationToken token)
    {
        lock (gate) if (windowsVoice is not null) return windowsVoice;
        var chosen = WindowsVoices.Recommended(await WindowsVoices.ListAsync(token).ConfigureAwait(false))?.Id ??
            throw new InvalidOperationException("No Windows voice is installed for Discord voice.");
        lock (gate) windowsVoice = chosen;
        return chosen;
    }

    private RecordingTranscriber? Transcriber()
    {
        var current = routes();
        var stt = current?.FirstOrDefault(route => route.Role == SetupRole.Stt);
        var key = $"{stt?.RouteType}|{stt?.ModelId}|{stt?.Gateway?.HostId}|{stt?.Enabled}";
        lock (gate)
        {
            if (transcriber is not null && key == transcriberKey) return transcriber;
            var old = transcriber;
            transcriber = RecordingTranscriber.Choose(current, parakeet,
                dataDirectory is null ? null : LocalVoices.SpeechRoot(dataDirectory));
            transcriberKey = key;
            if (old is not null) Task.Run(old.Dispose).Forget();
            return transcriber;
        }
    }

    public void Dispose()
    {
        lock (gate) transcriber?.Dispose();
    }
}
