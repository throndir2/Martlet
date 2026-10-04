using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using Martlet.Audio;
using Martlet.Conversation;
using Martlet.Core.Settings;

namespace Martlet.Desktop;

/// <summary>What always listening made of one utterance: the words heard, or why there are none (another voice for Voice ID,
/// a speech-to-text or microphone failure). Text is set only when the utterance was transcribed; Recording only when Thinking may
/// also hear it. Ignored: the utterance filter dropped it (Text is what speech-to-text wrote, only to show it as ignored).
/// Interrupt: its words, said over Martlet, stop it (BargeInPolicy); SpeechStartedAt is when its voice began (controller clock).
/// Early: the utterance was offered to be answered from its recording before its transcript (Answer from my voice).</summary>
internal sealed record HeardSpeech(LiveConversationStatus Status, string? Text, double? Confidence, HeardVoices? Voices,
    SpeakerCheck? SpeakerCheck, Voiceprint? Voiceprint, Martlet.Providers.BoundedWaveAudio? Recording = null,
    ReplyTimeline? Timeline = null, Martlet.Providers.UtteranceDecision? Ignored = null, BargeInDecision? Interrupt = null,
    long SpeechStartedAt = 0, EarlyHearing? Early = null)
{
    /// <summary>What was heard turned out not to be words at all: the utterance filter dropped it, or speech-to-text found no
    /// speech.</summary>
    internal bool NotWords => Ignored is not null || Status.Code == "stt.NoSpeech";
}

/// <summary>An utterance a Thinking model that hears may answer from its recording right away (Companion › Listening › Answer
/// from my voice): always listening offers it as soon as Voice ID let it through, before speech-to-text, and the talk window
/// takes it when nothing else waits. Speech-to-text runs beside the reply: <see cref="Words"/> is the utterance's own result
/// (null when listening stopped), which fills in what was said for the history and memory; when it turns out not to be words
/// (<see cref="HeardSpeech.NotWords"/>), the reply stops, before it speaks if it can.</summary>
internal sealed class EarlyHearing(Martlet.Providers.BoundedWaveAudio recording, ReplyTimeline? timeline)
{
    private readonly TaskCompletionSource<HeardSpeech?> words = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int taken, ended;
    private long transcribedAt;
    internal Martlet.Providers.BoundedWaveAudio Recording { get; } = recording;
    /// <summary>The utterance's steps so far (end of speech, recording, Voice ID), for the reply's latency line.</summary>
    internal ReplyTimeline? Timeline { get; } = timeline;
    internal Task<HeardSpeech?> Words => words.Task;
    /// <summary>Speech-to-text for this utterance is over and it no longer counts as being transcribed.</summary>
    internal bool Ended => Volatile.Read(ref ended) != 0;
    internal void End() => Volatile.Write(ref ended, 1);
    /// <summary>When speech-to-text finished beside the reply (controller clock; 0 until then).</summary>
    internal long TranscribedAt => Interlocked.Read(ref transcribedAt);
    /// <summary>A reply was started from the recording; otherwise the words, once ready, are answered as usual.</summary>
    internal bool Taken => Volatile.Read(ref taken) != 0;
    internal bool Take() => Interlocked.Exchange(ref taken, 1) == 0;
    internal void Release() => Volatile.Write(ref taken, 0);
    internal void Finish(HeardSpeech? speech, long at)
    {
        Interlocked.Exchange(ref transcribedAt, at);
        words.TrySetResult(speech);
    }
    public override string ToString() => nameof(EarlyHearing);
}

/// <summary>Always listening (<see cref="LiveConversationController.Listen"/>): one loop on its own slot beside replies. It
/// records one utterance at a time and transcribes each in order while it already listens for the next, so nothing said while
/// Martlet thinks is lost. It holds off only while Martlet speaks (so it never hears itself) or other setup work owns the app
/// slot, and keeps going after microphone and speech-to-text failures. The talk window takes what it heard with
/// <see cref="TryTake"/>.</summary>
internal sealed class LiveListener(ListeningOptions options, Voiceprint? voiceprint)
{
    private readonly ConcurrentQueue<HeardSpeech> results = new();
    private LiveConversationOperation? utterance;
    private EarlyHearing? early;
    private int transcribing, held;
    private long revision;
    private string? ended;

    internal ListeningOptions Options { get; } = options;
    /// <summary>It hears what this PC plays, not the microphone.</summary>
    internal bool Pc => Options.Pc;
    internal Voiceprint? Voiceprint { get; } = voiceprint;
    internal SetupOperation Worker { get; set; } = null!;
    internal bool Running => !Worker.Completion.IsCompleted;
    /// <summary>Changes when this listener is stopped, revoking the utterances it authorized (and only those).</summary>
    internal long Revision => Interlocked.Read(ref revision);
    internal void Revoke() => Interlocked.Increment(ref revision);
    /// <summary>The utterance being recorded now.</summary>
    internal LiveConversationOperation? Utterance { get => Volatile.Read(ref utterance); set => Volatile.Write(ref utterance, value); }
    /// <summary>Someone is talking right now (longer than a cough or click).</summary>
    internal bool Hearing => Utterance is { Hearing: true };
    /// <summary>The user has talked over Martlet with real words (BargeInPolicy): never a hum, a cough, laughter or what this PC
    /// plays.</summary>
    internal bool TalkingOver => Utterance is { TalkingOver: true };
    /// <summary>Why the utterance being recorded stopped Martlet, and how long after its voice began that was decided.</summary>
    internal TalkOverResult? TalkOver => Utterance?.TalkOver;
    /// <summary>Utterances recorded and still being checked or transcribed.</summary>
    internal int Transcribing => Volatile.Read(ref transcribing);
    /// <summary>Not listening for a moment: Martlet is speaking, or other setup work owns the microphone.</summary>
    internal bool Held { get => Volatile.Read(ref held) != 0; set => Volatile.Write(ref held, value ? 1 : 0); }
    internal double VoiceLevel => Held ? -100 : Utterance?.VoiceLevel ?? -100;
    /// <summary>The microphone is open and delivering audio right now.</summary>
    internal bool MicrophoneWorks => Utterance?.Capture?.Snapshot is { State: CaptureState.Capturing, CanonicalSamples: > 0 };
    /// <summary>Why listening ended by itself (setup changed or can't be used), or null while it runs or after Stop.</summary>
    internal string? Ended { get => Volatile.Read(ref ended); set => Volatile.Write(ref ended, value); }
    internal string Status => Held ? "listen.held" : Utterance?.Status.Code ?? "listen.starting";

    internal void Post(HeardSpeech speech) => results.Enqueue(speech);
    internal bool TryTake([NotNullWhen(true)] out HeardSpeech? speech) => results.TryDequeue(out speech);
    /// <summary>The utterance being transcribed that a reply may start from right away (Answer from my voice); null otherwise.
    /// It is withdrawn before its words are posted, so the talk window never answers the same utterance twice.</summary>
    internal EarlyHearing? Early => Volatile.Read(ref early);
    internal void Offer(EarlyHearing offered) => Volatile.Write(ref early, offered);
    internal void Withdraw(EarlyHearing offered) => Interlocked.CompareExchange(ref early, null, offered);
    /// <summary>Something heard waits to be taken (it is posted before <see cref="Transcribing"/> drops).</summary>
    internal bool HasResults => !results.IsEmpty;
    internal void BeginTranscribing() => Interlocked.Increment(ref transcribing);
    internal void EndTranscribing() => Interlocked.Decrement(ref transcribing);
    public override string ToString() => nameof(LiveListener);
}
