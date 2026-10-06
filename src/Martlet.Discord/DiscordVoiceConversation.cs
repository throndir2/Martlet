using System.Threading.Channels;

namespace Martlet.Discord;

/// <summary>Speech-to-text and text-to-speech for Discord voice, from Martlet's configured routes (the desktop decides which:
/// never the PC's microphone or speakers).</summary>
public interface IDiscordSpeech
{
    /// <summary>Why Discord voice can't transcribe now, or null when it can.</summary>
    string? ListenProblem { get; }
    /// <summary>Why Discord voice can't speak now, or null when it can.</summary>
    string? SpeakProblem { get; }
    /// <summary>The words in one utterance (16 kHz mono PCM16), trimmed; empty when none were heard.</summary>
    Task<string> TranscribeAsync(ReadOnlyMemory<byte> pcm16kMono, CancellationToken token);
    /// <summary>Speaks <paramref name="text"/> as mono PCM16 chunks, streaming.</summary>
    IAsyncEnumerable<DiscordSpeechAudio> SpeakAsync(string text, CancellationToken token);
}

/// <summary>A chunk of mono PCM16 speech at <paramref name="SampleRate"/>.</summary>
public readonly record struct DiscordSpeechAudio(int SampleRate, byte[] Pcm);

/// <summary>Where Martlet's voice goes: the Discord voice connection (paced at real time), or a fake one in checks.</summary>
public interface IDiscordVoiceTransport
{
    ValueTask SpeakingAsync(bool speaking, CancellationToken token);
    /// <summary>Sends one 20 ms Opus frame; returns when the connection is ready for the next one.</summary>
    ValueTask SendAsync(ReadOnlyMemory<byte> opusFrame, CancellationToken token);
}

/// <summary>What a voice conversation needs to know right now: the voice chat mode, Martlet's names (Martlet and the
/// character's), how many people are in the call, the bot's own ID and who a user is.</summary>
public sealed record DiscordVoiceContext(DiscordChatMode Mode, IReadOnlyList<string> Names, int Humans, ulong BotId,
    Func<ulong, DiscordSpeaker> Speaker);

/// <summary>Counters for status, Doctor and MCP (never what was said).</summary>
public sealed record DiscordVoiceCounts(int SpeakersHeard, int Utterances, int Transcribed, int Replies, int Spoken, int BargeIns,
    int LostFrames, bool Speaking, string? LastError);

/// <summary>One voice channel conversation: hears everyone (per speaker), transcribes each utterance, asks the reply engine
/// (with the voice chat mode) and speaks the reply into the call, stopping when someone talks over it with real words.
/// Received packets go through a bounded queue, so Discord's receive loop never waits on decoding or replies.</summary>
public sealed class DiscordVoiceConversation : IAsyncDisposable
{
    private const int SilenceFramesAfterSpeech = 5;
    private const int RecentLines = 12;
    private readonly DiscordPlace place;
    private readonly IDiscordVoiceTransport transport;
    private readonly IDiscordSpeech speech;
    private readonly Func<IDiscordReplyEngine?> replies;
    private readonly Func<DiscordVoiceContext> context;
    private readonly TimeProvider clock;
    private readonly DiscordVoiceListener listener;
    private readonly Lock gate = new();
    private readonly Channel<Packet> packets = Channel.CreateBounded<Packet>(new BoundedChannelOptions(1000)
    {
        FullMode = BoundedChannelFullMode.DropWrite, SingleReader = true
    });
    private readonly Channel<DiscordHeard> utterances = Channel.CreateUnbounded<DiscordHeard>(new() { SingleReader = true });
    private readonly SemaphoreSlim speaking = new(1, 1);
    private readonly CancellationTokenSource closing = new();
    private readonly List<DiscordLine> recent = [];
    private readonly Task pump, worker;
    private CancellationTokenSource? speakingNow;
    private int utteranceCount, transcribed, replyCount, spoken, bargeIns, pending;
    private int heardSpeakers, lostFrames;
    private bool hearing;
    private string? lastError;

    public DiscordVoiceConversation(DiscordPlace place, IDiscordVoiceTransport transport, IDiscordSpeech speech,
        Func<IDiscordReplyEngine?> replies, Func<DiscordVoiceContext> context, Func<uint, ulong?> userOf,
        TimeProvider? clock = null, DiscordEndpointOptions? options = null)
    {
        this.place = place;
        this.transport = transport;
        this.speech = speech;
        this.replies = replies;
        this.context = context;
        this.clock = clock ?? TimeProvider.System;
        listener = new(userOf, options);
        pump = Task.Run(PumpAsync);
        worker = Task.Run(WorkAsync);
    }

    public DiscordPlace Place => place;

    /// <summary>Raised off the UI thread when the counters change.</summary>
    public event Action? Changed;

    public DiscordVoiceCounts Counts
    {
        get
        {
            lock (gate)
                return new(heardSpeakers, utteranceCount, transcribed, replyCount, spoken, bargeIns, lostFrames,
                    speakingNow is not null, lastError);
        }
    }

    /// <summary>Takes one received Opus packet (copied; returns at once).</summary>
    public void Receive(uint ssrc, ushort sequence, ReadOnlySpan<byte> opus)
    {
        Interlocked.Increment(ref pending);
        if (!packets.Writer.TryWrite(new(ssrc, sequence, opus.ToArray(), false))) Interlocked.Decrement(ref pending);
    }

    /// <summary>A speaker left the call: whatever they were saying is finished.</summary>
    public void Forget(uint ssrc)
    {
        Interlocked.Increment(ref pending);
        if (!packets.Writer.TryWrite(new(ssrc, 0, [], true))) Interlocked.Decrement(ref pending);
    }

    /// <summary>Waits until every received packet and finished utterance has been handled (for checks and tests).</summary>
    public async Task DrainAsync(TimeSpan timeout, CancellationToken token = default)
    {
        var until = clock.GetUtcNow() + timeout;
        while (Volatile.Read(ref pending) > 0 || Volatile.Read(ref hearing))
        {
            if (clock.GetUtcNow() > until) throw new TimeoutException("The voice conversation didn't finish in time.");
            await Task.Delay(20, token).ConfigureAwait(false);
        }
    }

    /// <summary>Speaks <paramref name="text"/> into the call (a reply or something Martlet starts saying itself).</summary>
    public async Task SpeakAsync(string text, CancellationToken token)
    {
        if (speech.SpeakProblem is { } problem) { Fail(problem); return; }
        await speaking.WaitAsync(token).ConfigureAwait(false);
        using var now = CancellationTokenSource.CreateLinkedTokenSource(token, closing.Token);
        lock (gate) speakingNow = now;
        Changed?.Invoke();
        var encoder = new DiscordOpusEncoder();
        var frame = new short[DiscordVoiceAudio.FrameSamples * DiscordVoiceAudio.DiscordChannels];
        var filled = 0;
        try
        {
            await transport.SpeakingAsync(true, now.Token).ConfigureAwait(false);
            foreach (var segment in DiscordVoiceRules.Segments(text))
            {
                DiscordUpsampler? upsampler = null;
                await foreach (var audio in speech.SpeakAsync(segment, now.Token).ConfigureAwait(false))
                {
                    if (upsampler?.SourceRate != audio.SampleRate) upsampler = new(audio.SampleRate);
                    var stereo = upsampler.Convert(DiscordVoiceAudio.ToSamples(audio.Pcm));
                    for (var offset = 0; offset < stereo.Length;)
                    {
                        var take = Math.Min(frame.Length - filled, stereo.Length - offset);
                        stereo.AsSpan(offset, take).CopyTo(frame.AsSpan(filled));
                        filled += take;
                        offset += take;
                        if (filled < frame.Length) continue;
                        await transport.SendAsync(encoder.Encode(frame), now.Token).ConfigureAwait(false);
                        filled = 0;
                    }
                }
            }
            if (filled > 0)
            {
                frame.AsSpan(filled).Clear();
                await transport.SendAsync(encoder.Encode(frame), now.Token).ConfigureAwait(false);
            }
            lock (gate) spoken++;
        }
        catch (OperationCanceledException) when (now.IsCancellationRequested && !closing.IsCancellationRequested) { }
        catch (Exception error) when (error is not OperationCanceledException) { Fail("Speaking failed: " + error.Message); }
        finally
        {
            lock (gate) speakingNow = null;
            try
            {
                if (!closing.IsCancellationRequested)
                {
                    for (var i = 0; i < SilenceFramesAfterSpeech; i++)
                        await transport.SendAsync(DiscordVoiceAudio.SilenceFrame.ToArray(), closing.Token).ConfigureAwait(false);
                    await transport.SpeakingAsync(false, closing.Token).ConfigureAwait(false);
                }
            }
            catch (Exception error) when (error is OperationCanceledException or InvalidOperationException or ObjectDisposedException) { }
            speaking.Release();
            Changed?.Invoke();
        }
    }

    private async Task PumpAsync()
    {
        var reader = packets.Reader;
        var token = closing.Token;
        while (!token.IsCancellationRequested)
        {
            try
            {
                using var wait = CancellationTokenSource.CreateLinkedTokenSource(token);
                wait.CancelAfter(TimeSpan.FromMilliseconds(100));
                await reader.WaitToReadAsync(wait.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested) { }
            catch (OperationCanceledException) { return; }
            var now = clock.GetUtcNow();
            try
            {
                while (reader.TryRead(out var packet))
                {
                    if (packet.Leave) { if (listener.Remove(packet.Ssrc) is { } last) Finished(last); }
                    else foreach (var heard in listener.Receive(packet.Ssrc, packet.Sequence, packet.Frame, now)) Finished(heard);
                    Interlocked.Decrement(ref pending);
                }
                foreach (var heard in listener.Tick(now)) Finished(heard);
                Volatile.Write(ref hearing, listener.Hearing);
                lock (gate) { heardSpeakers = listener.Heard.Count; lostFrames = listener.LostFrames; }
                CancellationTokenSource? talking;
                lock (gate) talking = speakingNow;
                if (talking is not null && DiscordVoiceRules.BargeIn(true, listener.TalkingOver(now), context().BotId))
                {
                    lock (gate) bargeIns++;
                    talking.Cancel();
                }
            }
            catch (Exception error) when (error is not OperationCanceledException) { Fail("Couldn't decode voice: " + error.Message); }
        }
    }

    private void Finished(DiscordHeard heard)
    {
        Interlocked.Increment(ref pending);
        lock (gate) utteranceCount++;
        if (!utterances.Writer.TryWrite(heard)) Interlocked.Decrement(ref pending);
        Changed?.Invoke();
    }

    private async Task WorkAsync()
    {
        var token = closing.Token;
        try
        {
            await foreach (var heard in utterances.Reader.ReadAllAsync(token).ConfigureAwait(false))
            {
                try { await HandleAsync(heard, token).ConfigureAwait(false); }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { return; }
                catch (Exception error) when (error is not OperationCanceledException) { Fail(error.Message); }
                finally { Interlocked.Decrement(ref pending); }
            }
        }
        catch (OperationCanceledException) { }
    }

    private async Task HandleAsync(DiscordHeard heard, CancellationToken token)
    {
        if (speech.ListenProblem is { } problem) { Fail(problem); return; }
        var pcm = DiscordVoiceAudio.ToBytes(heard.Speech);
        string text;
        try { text = await speech.TranscribeAsync(pcm, token).ConfigureAwait(false); }
        finally { Array.Clear(pcm); Array.Clear(heard.Speech); }
        if (string.IsNullOrWhiteSpace(text)) return;
        var now = context();
        var speaker = now.Speaker(heard.UserId);
        DiscordLine[] before;
        lock (gate)
        {
            transcribed++;
            before = [.. recent];
            Remember(new(speaker.Name, text, clock.GetUtcNow(), false));
        }
        Changed?.Invoke();
        var addressed = DiscordVoiceRules.Addressed(text, now.Names, now.Humans);
        if (!DiscordChatRules.Considers(now.Mode, addressed)) return;
        if (replies() is not { } engine) { Fail("The Discord reply engine isn't ready."); return; }
        var reply = await engine.ReplyAsync(new(place, speaker, text, DiscordTurnSource.Voice, addressed, before), token)
            .ConfigureAwait(false);
        if (reply is null || string.IsNullOrWhiteSpace(reply.Text)) return;
        lock (gate)
        {
            replyCount++;
            Remember(new("Martlet", reply.Text, clock.GetUtcNow(), true));
        }
        await SpeakAsync(reply.Text, token).ConfigureAwait(false);
    }

    private void Remember(DiscordLine line)
    {
        recent.Add(line);
        if (recent.Count > RecentLines) recent.RemoveAt(0);
    }

    private void Fail(string problem)
    {
        lock (gate) lastError = problem;
        Changed?.Invoke();
    }

    public async ValueTask DisposeAsync()
    {
        if (closing.IsCancellationRequested) return;
        closing.Cancel();
        packets.Writer.TryComplete();
        utterances.Writer.TryComplete();
        try { await Task.WhenAll(pump, worker).ConfigureAwait(false); }
        catch (OperationCanceledException) { }
    }

    private sealed record Packet(uint Ssrc, ushort Sequence, byte[] Frame, bool Leave);
}
