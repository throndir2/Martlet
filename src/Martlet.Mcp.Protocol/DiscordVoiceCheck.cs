using System.IO;
using System.Runtime.CompilerServices;
using Martlet.Discord;

namespace Martlet.Mcp;

/// <summary>discord_voice_check: Discord voice without Discord. It loads the shipped libdave.dll (DAVE, Discord's voice
/// encryption) from martletDirectory and runs an offline DAVE session (MLS key package, frame encryptor and decryptor), then
/// pushes one spoken utterance through the production voice path with a fake transport: a Windows voice says it into memory, it
/// is encoded to 48 kHz stereo Opus packets as a Discord client sends them, and DiscordVoiceConversation orders, decodes,
/// downsamples and endpoints them per speaker, hands the utterance to FIXTURE speech-to-text and a FIXTURE reply engine, speaks
/// the reply with a Windows voice into memory and encodes it back to Opus frames, which are decoded again to measure them. A
/// second phase talks over a long reply to check barge-in. No network, microphone, speaker, provider or credential is used.</summary>
internal static class DiscordVoiceCheck
{
    private const uint SpeakerSsrc = 4242, OtherSsrc = 4343;
    private const ulong Owner = 1001, Other = 2002, Bot = 9;
    private const string Said = "Hey Martlet, can you hear me in the Discord call?";
    private const string Reply = "Yes, I can hear you clearly.";

    internal static async Task<object> RunAsync(string? martletDirectory, CancellationToken cancellation)
    {
        var own = DiscordVoiceNatives.Check(AppContext.BaseDirectory);
        var shipped = martletDirectory is null ? null : DiscordVoiceNatives.Check(martletDirectory);

        var transport = new FakeTransport();
        var speech = new FixtureSpeech();
        var replies = new FixtureReplies();
        var users = new Dictionary<uint, ulong> { [SpeakerSsrc] = Owner, [OtherSsrc] = Other };
        var humans = 1;
        object utterance, reply, bargeIn;
        await using (var conversation = new DiscordVoiceConversation(new(77, 66, "General", false), transport, speech, () => replies,
            () => new(DiscordChatMode.Sometimes, ["Martlet"], humans, Bot, id => new(id, id == Owner ? "Owner" : "Friend", id == Owner)),
            ssrc => users.TryGetValue(ssrc, out var user) ? user : null))
        {
            var said = Speak(Said, 16_000);
            var packets = Packets(said, 16_000);
            ushort sequence = 0;
            foreach (var packet in packets) conversation.Receive(SpeakerSsrc, sequence++, packet);
            await WaitAsync(() => !conversation.Counts.Speaking && transport.Frames > 0 && conversation.Counts.Spoken == 1, cancellation);
            var counts = conversation.Counts;
            var heard = speech.Heard.FirstOrDefault();
            var turn = replies.Turns.FirstOrDefault();
            utterance = new
            {
                saidMs = said.Length * 1000 / 16_000, packetsSent = packets.Count,
                heardMs = heard?.Milliseconds, heardLevel = heard?.Level, utterances = counts.Utterances,
                transcribed = counts.Transcribed, stt = "FIXTURE",
                turn = turn is null ? null : new { turn.Text, turn.Addressed, source = turn.Source.ToString(), speaker = turn.Speaker.Name, owner = turn.Speaker.IsOwner }
            };
            var back = Decode(transport.Take());
            reply = new
            {
                replies = counts.Replies, spoken = counts.Spoken, tts = speech.Voice, framesOut = back.Frames,
                spokenMs = back.Frames * 20, level = back.Level, speakingOn = transport.SpeakingOnCount, speakingOff = transport.SpeakingOffCount
            };

            // Barge-in: a long reply, and a second person talks over it with real words.
            humans = 2;
            speech.Long = true;
            var talking = conversation.SpeakAsync("A long story that goes on.", cancellation);
            await WaitAsync(() => transport.Frames > 25, cancellation);
            var over = Packets(Speak("Wait, hold on a second, Martlet.", 16_000), 16_000);
            sequence = 0;
            foreach (var packet in over)
            {
                conversation.Receive(OtherSsrc, sequence++, packet);
                await Task.Delay(20, cancellation);
                if (conversation.Counts.BargeIns > 0) break;
            }
            await talking;
            var framesWhenStopped = transport.Frames;
            bargeIn = new
            {
                stopped = conversation.Counts.BargeIns == 1, framesBeforeStop = framesWhenStopped,
                longReplyFrames = FixtureSpeech.LongSeconds * 50, speakersHeard = conversation.Counts.SpeakersHeard
            };
            var ok = own.Ok && (shipped?.Ok ?? true) && turn is { Addressed: true } && heard is { Milliseconds: > 1000 } &&
                back.Frames > 25 && back.Level > 300 && conversation.Counts.BargeIns == 1 && framesWhenStopped < FixtureSpeech.LongSeconds * 50;
            return new
            {
                ok, natives = own, shipped = shipped ?? (object)"martletDirectory not given", utterance, reply, bargeIn,
                evidence = "Offline: FIXTURE speech-to-text and reply engine, a Windows voice rendered to memory, a fake voice " +
                    "transport. libdave ran a local DAVE session; no Discord connection, network or audio device was used."
            };
        }
    }

    private static async Task WaitAsync(Func<bool> condition, CancellationToken cancellation)
    {
        for (var i = 0; i < 1000 && !condition(); i++) await Task.Delay(20, cancellation);
    }

    // A Windows voice saying it at `rate` mono PCM16, into memory (never played).
    private static short[] Speak(string text, int rate)
    {
        using var stream = new MemoryStream();
        using (var voice = new System.Speech.Synthesis.SpeechSynthesizer())
        {
            voice.SetOutputToAudioStream(stream, new System.Speech.AudioFormat.SpeechAudioFormatInfo(rate,
                System.Speech.AudioFormat.AudioBitsPerSample.Sixteen, System.Speech.AudioFormat.AudioChannel.Mono));
            voice.Speak(text);
        }
        return DiscordVoiceAudio.ToSamples(stream.ToArray());
    }

    // As a Discord client sends speech: 48 kHz stereo, 20 ms Opus frames, then its silence frames.
    private static List<byte[]> Packets(short[] speech, int rate)
    {
        var stereo = new DiscordUpsampler(rate).Convert(speech);
        var encoder = new DiscordOpusEncoder();
        var frame = DiscordVoiceAudio.FrameSamples * 2;
        var packets = new List<byte[]>();
        for (var offset = 0; offset + frame <= stereo.Length; offset += frame) packets.Add(encoder.Encode(stereo.AsSpan(offset, frame)));
        for (var i = 0; i < 5; i++) packets.Add(DiscordVoiceAudio.SilenceFrame.ToArray());
        return packets;
    }

    private static (int Frames, int Level) Decode(IReadOnlyList<byte[]> frames)
    {
        var decoder = new DiscordOpusDecoder();
        double sum = 0;
        long count = 0;
        var audible = 0;
        foreach (var frame in frames)
        {
            if (frame.Length <= 3) continue;
            audible++;
            foreach (var sample in decoder.Decode(frame)) { sum += sample * (double)sample; count++; }
        }
        return (audible, count == 0 ? 0 : (int)Math.Sqrt(sum / count));
    }

    private sealed class FakeTransport : IDiscordVoiceTransport
    {
        private readonly List<byte[]> frames = [];
        public int SpeakingOnCount, SpeakingOffCount;
        public int Frames { get { lock (frames) return frames.Count; } }

        public IReadOnlyList<byte[]> Take()
        {
            lock (frames)
            {
                var taken = frames.ToArray();
                frames.Clear();
                return taken;
            }
        }

        public ValueTask SpeakingAsync(bool speaking, CancellationToken token)
        {
            if (speaking) Interlocked.Increment(ref SpeakingOnCount); else Interlocked.Increment(ref SpeakingOffCount);
            return default;
        }

        // Paced at about real time, as NetCord's voice stream sends.
        public async ValueTask SendAsync(ReadOnlyMemory<byte> opusFrame, CancellationToken token)
        {
            lock (frames) frames.Add(opusFrame.ToArray());
            await Task.Delay(5, token);
        }
    }

    private sealed record Heard(int Milliseconds, int Level);

    private sealed class FixtureSpeech : IDiscordSpeech
    {
        public const int LongSeconds = 20;
        public List<Heard> Heard { get; } = [];
        public bool Long { get; set; }
        public string Voice { get; private set; } = "Windows voice (System.Speech) rendered to memory";
        public string? ListenProblem => null;
        public string? SpeakProblem => null;

        public Task<string> TranscribeAsync(ReadOnlyMemory<byte> pcm16kMono, CancellationToken token)
        {
            var samples = DiscordVoiceAudio.ToSamples(pcm16kMono.Span);
            var level = samples.Length == 0 ? 0 : (int)Math.Sqrt(samples.Average(s => (double)s * s));
            lock (Heard) Heard.Add(new(samples.Length * 1000 / DiscordVoiceAudio.SpeechRate, level));
            return Task.FromResult(samples.Length > 8000 ? Said : "");
        }

        public async IAsyncEnumerable<DiscordSpeechAudio> SpeakAsync(string text, [EnumeratorCancellation] CancellationToken token)
        {
            if (Long)
            {
                // 20 s of a voice-level tone, streamed in 100 ms chunks.
                for (var chunk = 0; chunk < LongSeconds * 10; chunk++)
                {
                    token.ThrowIfCancellationRequested();
                    var pcm = new short[2400];
                    for (var i = 0; i < pcm.Length; i++) pcm[i] = (short)(5000 * Math.Sin(2 * Math.PI * 180 * (chunk * 2400 + i) / 24_000.0));
                    yield return new(24_000, DiscordVoiceAudio.ToBytes(pcm));
                    await Task.Yield();
                }
                yield break;
            }
            short[] spoken;
            try { spoken = await Task.Run(() => Speak(text, 24_000), token); }
            catch (Exception error) when (error is PlatformNotSupportedException or InvalidOperationException)
            {
                Voice = "FIXTURE tone (no Windows voice)";
                spoken = [.. Enumerable.Range(0, 24_000).Select(i => (short)(5000 * Math.Sin(2 * Math.PI * 200 * i / 24_000.0)))];
            }
            yield return new(24_000, DiscordVoiceAudio.ToBytes(spoken));
        }
    }

    private sealed class FixtureReplies : IDiscordReplyEngine
    {
        public List<DiscordTurn> Turns { get; } = [];
        public Task<DiscordReply?> ReplyAsync(DiscordTurn turn, CancellationToken token)
        {
            lock (Turns) Turns.Add(turn);
            return Task.FromResult<DiscordReply?>(new(Reply));
        }
    }
}
