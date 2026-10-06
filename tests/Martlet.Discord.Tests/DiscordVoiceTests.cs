using System.Runtime.CompilerServices;
using Martlet.Discord;

namespace Martlet.Discord.Tests;

public sealed class DiscordVoiceTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 6, 12, 0, 0, TimeSpan.Zero);

    // 20 ms of a 48 kHz stereo tone (voice-like level), as Discord sends it.
    private static short[] Tone(int frameIndex, double hz = 220, double amplitude = 6000)
    {
        var frame = new short[DiscordVoiceAudio.FrameSamples * 2];
        for (var i = 0; i < DiscordVoiceAudio.FrameSamples; i++)
        {
            var t = (frameIndex * DiscordVoiceAudio.FrameSamples + i) / (double)DiscordVoiceAudio.DiscordRate;
            frame[2 * i] = frame[2 * i + 1] = (short)(amplitude * Math.Sin(2 * Math.PI * hz * t));
        }
        return frame;
    }

    private static List<byte[]> Packets(int frames, double hz = 220, double amplitude = 6000)
    {
        var encoder = new DiscordOpusEncoder();
        return [.. Enumerable.Range(0, frames).Select(i => encoder.Encode(Tone(i, hz, amplitude)))];
    }

    private static double Rms(ReadOnlySpan<short> samples) => Math.Sqrt(samples.ToArray().Average(s => (double)s * s));

    private static double Frequency(ReadOnlySpan<short> samples, int rate)
    {
        var crossings = 0;
        for (var i = 1; i < samples.Length; i++) if (samples[i - 1] < 0 && samples[i] >= 0) crossings++;
        return crossings * rate / (double)samples.Length;
    }

    [Fact]
    public void Voice_audio_round_trips_from_speech_rate_through_opus_and_back()
    {
        const int rate = 24_000;
        var speech = new short[rate];
        for (var i = 0; i < speech.Length; i++) speech[i] = (short)(8000 * Math.Sin(2 * Math.PI * 300 * i / rate));
        var stereo = new DiscordUpsampler(rate).Convert(speech);
        Assert.InRange(stereo.Length, 2 * 48_000 - 4, 2 * 48_000 + 4);
        var encoder = new DiscordOpusEncoder();
        var decoder = new DiscordOpusDecoder();
        var down = new DiscordDownsampler();
        var heard = new List<short>();
        for (var offset = 0; offset + 1920 <= stereo.Length; offset += 1920)
            heard.AddRange(down.Convert(decoder.Decode(encoder.Encode(stereo.AsSpan(offset, 1920)))));
        Assert.InRange(heard.Count, 15_900, 16_000);
        var steady = heard.Skip(3200).ToArray();
        Assert.InRange(Frequency(steady, 16_000), 290, 310);
        Assert.InRange(Rms(steady) / Rms(speech), 0.8, 1.2);
    }

    [Fact]
    public void Upsampler_joins_chunks_without_gaps()
    {
        var up = new DiscordUpsampler(16_000);
        var whole = new DiscordUpsampler(16_000).Convert(Enumerable.Range(0, 640).Select(i => (short)(i * 10)).ToArray());
        var parts = up.Convert(Enumerable.Range(0, 320).Select(i => (short)(i * 10)).ToArray())
            .Concat(up.Convert(Enumerable.Range(320, 320).Select(i => (short)(i * 10)).ToArray())).ToArray();
        Assert.Equal(whole, parts);
    }

    [Fact]
    public void Packet_order_sorts_late_packets_drops_duplicates_and_conceals_loss()
    {
        var order = new DiscordPacketOrder(depth: 2);
        var ready = new List<byte[]?>();
        order.Push(65534, [1], ready);
        order.Push(0, [3], ready);     // 65535 is late
        order.Push(65535, [2], ready); // arrives: 1,2,3 in order across the wrap
        order.Push(65535, [9], ready); // duplicate
        Assert.Equal([1, 2, 3], ready.Select(frame => frame![0]));
        ready.Clear();
        order.Push(2, [5], ready);     // 1 missing
        order.Push(3, [6], ready);
        Assert.Empty(ready);
        order.Push(4, [7], ready);     // two later packets overtook it: conceal it
        Assert.Equal(new byte?[] { null, 5, 6, 7 }, ready.Select(frame => frame?[0]));
        Assert.Equal(1, order.Lost);
    }

    [Fact]
    public void Listener_maps_ssrcs_to_users_and_ends_each_speakers_utterance_separately()
    {
        var users = new Dictionary<uint, ulong> { [11] = 1001, [22] = 2002 };
        var listener = new DiscordVoiceListener(ssrc => users.TryGetValue(ssrc, out var user) ? user : null);
        var alice = Packets(50, 220);
        var bob = Packets(25, 330);
        var unknown = Packets(30, 440);
        var heard = new List<DiscordHeard>();
        for (var i = 0; i < 50; i++)
        {
            var now = Start.AddMilliseconds(i * 20);
            heard.AddRange(listener.Receive(11, (ushort)i, alice[i], now));
            if (i < 25) heard.AddRange(listener.Receive(22, (ushort)(100 + i), bob[i], now));
            if (i < 30) heard.AddRange(listener.Receive(33, (ushort)(500 + i), unknown[i], now));
        }
        Assert.Empty(heard);
        Assert.True(listener.Hearing);
        // Bob stopped sending at 0.5 s: his utterance ends 0.7 s later; Alice is still talking.
        heard.AddRange(listener.Tick(Start.AddMilliseconds(500 + 700)));
        var only = Assert.Single(heard);
        Assert.Equal(2002UL, only.UserId);
        Assert.InRange(only.Duration.TotalMilliseconds, 400, 520);
        heard.AddRange(listener.Tick(Start.AddMilliseconds(1000 + 700)));
        Assert.Equal([2002UL, 1001UL], heard.Select(h => h.UserId));
        Assert.InRange(heard[1].Duration.TotalMilliseconds, 900, 1020);
        Assert.Equal(new HashSet<ulong> { 1001, 2002 }, listener.Heard.ToHashSet());
    }

    [Fact]
    public void Endpointer_ends_on_silence_frames_and_ignores_a_short_sound()
    {
        var endpointer = new DiscordSpeakerEndpointer(new());
        var tone = new DiscordDownsampler().Convert(Tone(0)).Concat(new DiscordDownsampler().Convert(Tone(1))).ToArray(); // 40 ms
        var quiet = new short[DiscordVoiceAudio.SpeechFrameSamples];
        var now = Start;
        Assert.Null(endpointer.Append(tone, now)); // a 40 ms click
        short[]? result = null;
        for (var i = 0; i < 40 && result is null; i++) result = endpointer.Append(quiet, now = now.AddMilliseconds(20));
        Assert.Null(result);
        Assert.False(endpointer.Active);

        var words = new short[DiscordVoiceAudio.SpeechRate / 2];
        for (var i = 0; i < words.Length; i++) words[i] = (short)(5000 * Math.Sin(2 * Math.PI * 200 * i / 16_000.0));
        Assert.Null(endpointer.Append(words, now));
        Assert.True(endpointer.Voiced >= TimeSpan.FromMilliseconds(480));
        for (var i = 0; i < 40 && result is null; i++) result = endpointer.Append(quiet, now = now.AddMilliseconds(20));
        Assert.NotNull(result);
        Assert.InRange(result.Length, words.Length, words.Length + 16_000 * 1.0);
    }

    [Fact]
    public void Barge_in_needs_real_words_from_someone_else_while_martlet_speaks()
    {
        var listener = new DiscordVoiceListener(ssrc => ssrc == 7 ? 70UL : null);
        var packets = Packets(30);
        for (var i = 0; i < 10; i++) listener.Receive(7, (ushort)i, packets[i], Start.AddMilliseconds(20 * i));
        Assert.Empty(listener.TalkingOver(Start.AddMilliseconds(200)));    // 200 ms: could be a cough
        for (var i = 10; i < 30; i++) listener.Receive(7, (ushort)i, packets[i], Start.AddMilliseconds(20 * i));
        var talking = listener.TalkingOver(Start.AddMilliseconds(600));
        Assert.Equal([70UL], talking);
        Assert.True(DiscordVoiceRules.BargeIn(speaking: true, talking, botId: 1));
        Assert.False(DiscordVoiceRules.BargeIn(speaking: false, talking, botId: 1));
        Assert.False(DiscordVoiceRules.BargeIn(speaking: true, [1UL], botId: 1));
    }

    [Fact]
    public void Voice_rooms_join_once_move_leave_and_notice_being_alone()
    {
        var rooms = new DiscordVoiceRooms();
        Assert.True(rooms.TryBeginJoin(1, 10));
        Assert.False(rooms.TryBeginJoin(1, 10));
        Assert.False(rooms.TryBeginJoin(1, 20)); // still joining
        rooms.Joined(1, 10);
        Assert.Equal(DiscordVoicePhase.Connected, rooms.Phase(1));
        Assert.False(rooms.TryBeginJoin(1, 10));
        Assert.True(rooms.TryBeginJoin(1, 20));  // a move to another channel
        rooms.Joined(1, 20);
        Assert.False(rooms.People(1, 2, Start));
        Assert.True(rooms.People(1, 0, Start));
        Assert.False(rooms.People(1, 0, Start.AddSeconds(5))); // already alone
        Assert.Empty(rooms.AloneTooLong(Start.AddSeconds(90), TimeSpan.FromMinutes(2)));
        Assert.Equal([1UL], rooms.AloneTooLong(Start.AddMinutes(2), TimeSpan.FromMinutes(2)));
        Assert.False(rooms.People(1, 1, Start.AddMinutes(2)));
        Assert.Empty(rooms.AloneTooLong(Start.AddMinutes(5), TimeSpan.FromMinutes(2)));
        Assert.True(rooms.TryBeginLeave(1));
        Assert.False(rooms.TryBeginLeave(1));
        Assert.False(rooms.TryBeginJoin(1, 30)); // finish leaving first
        Assert.Equal(20UL, rooms.Left(1));
        Assert.Equal(DiscordVoicePhase.Idle, rooms.Phase(1));
        Assert.Null(rooms.Left(1));
    }

    [Theory]
    [InlineData("hey martlet, what's up", 3, true)]
    [InlineData("Hey Aria are you there", 3, true)]
    [InlineData("the martletology club", 3, false)]
    [InlineData("anyone want to play", 3, false)]
    [InlineData("anyone want to play", 1, true)]
    public void A_turn_is_addressed_by_name_or_by_a_one_to_one_call(string text, int humans, bool addressed) =>
        Assert.Equal(addressed, DiscordVoiceRules.Addressed(text, ["Martlet", "Aria"], humans));

    [Fact]
    public void Following_the_owner_and_cutting_replies_into_segments()
    {
        Assert.Equal(5UL, DiscordVoiceRules.FollowOwner(true, 5, 4));
        Assert.Null(DiscordVoiceRules.FollowOwner(true, 5, 5));
        Assert.Null(DiscordVoiceRules.FollowOwner(true, null, 5));
        Assert.Null(DiscordVoiceRules.FollowOwner(false, 5, 4));
        var segments = DiscordVoiceRules.Segments("One. Two! " + new string('a', 30) + " three? " + string.Join(' ', Enumerable.Repeat("word", 30)), 40);
        Assert.All(segments, segment => Assert.InRange(segment.Length, 1, 40));
        Assert.Equal("One. Two!", segments[0]);
    }

    [Fact]
    public void Libdave_loads_and_runs_an_offline_dave_session()
    {
        var report = DiscordVoiceNatives.Check();
        Assert.True(report.Ok, report.Problem);
        Assert.True(report.DaveProtocolVersion >= 1);
        Assert.True(report.KeyPackageBytes > 100);
    }

    [Fact]
    public async Task Conversation_transcribes_replies_speaks_and_stops_when_talked_over()
    {
        var transport = new FakeTransport();
        var speech = new FakeSpeech();
        var replies = new FakeReplies();
        var users = new Dictionary<uint, ulong> { [1] = 100, [2] = 200 };
        await using var conversation = new DiscordVoiceConversation(new(9, 8, "General", false), transport, speech, () => replies,
            () => new(DiscordChatMode.Sometimes, ["Martlet"], 2, 1, id => new(id, $"user{id}", id == 100)),
            ssrc => users.TryGetValue(ssrc, out var user) ? user : null, options: new() { EndSilence = TimeSpan.FromMilliseconds(150) });
        var packets = Packets(25);
        for (var i = 0; i < 25; i++) conversation.Receive(1, (ushort)i, packets[i]);
        await WaitUntil(() => transport.Frames > 20);
        Assert.Equal("hello Martlet", replies.Turns.Single().Text);
        Assert.True(replies.Turns.Single().Addressed);
        Assert.Equal(DiscordTurnSource.Voice, replies.Turns.Single().Source);
        Assert.True(transport.SpeakingOn);
        // Someone talks over the (long) reply with real words: Martlet stops.
        var over = Packets(40, 330);
        for (var i = 0; i < 40; i++) { conversation.Receive(2, (ushort)i, over[i]); await Task.Delay(5); }
        await WaitUntil(() => conversation.Counts.BargeIns == 1 && !conversation.Counts.Speaking && conversation.Counts.SpeakersHeard == 2);
        Assert.True(transport.Frames < FakeSpeech.Seconds * 50);
        Assert.False(transport.SpeakingOn);
        Assert.True(conversation.Counts.Transcribed >= 1);
        Assert.Equal(2, conversation.Counts.SpeakersHeard);
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        for (var i = 0; i < 500 && !condition(); i++) await Task.Delay(20);
        Assert.True(condition());
    }

    private sealed class FakeTransport : IDiscordVoiceTransport
    {
        private int frames;
        public int Frames => Volatile.Read(ref frames);
        public bool SpeakingOn { get; private set; }
        public ValueTask SpeakingAsync(bool speaking, CancellationToken token) { SpeakingOn = speaking; return default; }
        public async ValueTask SendAsync(ReadOnlyMemory<byte> opusFrame, CancellationToken token)
        {
            Interlocked.Increment(ref frames);
            await Task.Delay(2, token);
        }
    }

    private sealed class FakeSpeech : IDiscordSpeech
    {
        public const int Seconds = 10;
        public string? ListenProblem => null;
        public string? SpeakProblem => null;
        public Task<string> TranscribeAsync(ReadOnlyMemory<byte> pcm16kMono, CancellationToken token) =>
            Task.FromResult(pcm16kMono.Length > 3200 ? "hello Martlet" : "");
        public async IAsyncEnumerable<DiscordSpeechAudio> SpeakAsync(string text, [EnumeratorCancellation] CancellationToken token)
        {
            for (var i = 0; i < Seconds * 10; i++)
            {
                token.ThrowIfCancellationRequested();
                yield return new(24_000, new byte[4800]);
                await Task.Yield();
            }
        }
    }

    private sealed class FakeReplies : IDiscordReplyEngine
    {
        public List<DiscordTurn> Turns { get; } = [];
        public Task<DiscordReply?> ReplyAsync(DiscordTurn turn, CancellationToken token)
        {
            int count;
            lock (Turns) { Turns.Add(turn); count = Turns.Count; }
            return Task.FromResult<DiscordReply?>(count == 1 ? new("A long answer.") : null);
        }
    }
}
