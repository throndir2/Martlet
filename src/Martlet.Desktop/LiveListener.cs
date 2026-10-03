using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using Martlet.Audio;
using Martlet.Core.Settings;

namespace Martlet.Desktop;

/// <summary>What always listening made of one utterance: the words heard, or why there are none (another voice for Voice ID,
/// a speech-to-text or microphone failure). Text is set only when the utterance was transcribed; Recording only when Thinking may
/// also hear it.</summary>
internal sealed record HeardSpeech(LiveConversationStatus Status, string? Text, double? Confidence, HeardVoices? Voices,
    SpeakerCheck? SpeakerCheck, Voiceprint? Voiceprint, Martlet.Providers.BoundedWaveAudio? Recording = null);

/// <summary>Always listening (<see cref="LiveConversationController.Listen"/>): one loop on its own slot beside replies. It
/// records one utterance at a time and transcribes each in order while it already listens for the next, so nothing said while
/// Martlet thinks is lost. It holds off only while Martlet speaks (so it never hears itself) or other setup work owns the app
/// slot, and keeps going after microphone and speech-to-text failures. The talk window takes what it heard with
/// <see cref="TryTake"/>.</summary>
internal sealed class LiveListener(ListeningOptions options, Voiceprint? voiceprint)
{
    private readonly ConcurrentQueue<HeardSpeech> results = new();
    private LiveConversationOperation? utterance;
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
    internal void BeginTranscribing() => Interlocked.Increment(ref transcribing);
    internal void EndTranscribing() => Interlocked.Decrement(ref transcribing);
    public override string ToString() => nameof(LiveListener);
}
