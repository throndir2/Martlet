using System.IO;
using Martlet.Core.Contracts;
using Martlet.Core.Speakers;
using Martlet.Providers;
using Martlet.Sherpa;

namespace Martlet.Desktop;

/// <summary>One voice heard in an utterance and what Martlet made of it. <see cref="Voice"/> is null when the voice could not
/// be told apart (too little speech, or too close to call); <see cref="Added"/> means it was new and joined the list.</summary>
internal sealed record HeardVoice(KnownVoice? Voice, VoiceMatchKind Kind, double Score, double Seconds, bool Added);

/// <summary>Who spoke in one utterance: <see cref="Speaker"/> is the voice with the most speech (the one talking to Martlet),
/// <see cref="Others"/> anyone else heard clearly enough.</summary>
internal sealed record HeardVoices(IReadOnlyList<HeardVoice> Voices, bool Overlap)
{
    internal static HeardVoices None { get; } = new([], false);
    internal HeardVoice? Speaker => Voices.FirstOrDefault();
    internal IReadOnlyList<HeardVoice> Others => Voices.Skip(1).ToArray();
    internal IReadOnlyList<KnownVoice> Known => Voices.Select(v => v.Voice).OfType<KnownVoice>().ToArray();
}

/// <summary>Recognizing the people Martlet hears. The voice list (voices.json) holds voiceprints and the names each voice goes
/// by, never audio; the engine (sherpa-onnx with the WeSpeaker and pyannote models, AudioTranscriber's pipeline) ships in
/// Martlet's folder and runs on this PC. Whether it is on (voice-recognition.txt, on unless the owner turned it off) is one of
/// Martlet's shared settings, and the list itself travels through the paired hosts while Martlet is the same on all the
/// owner's computers.</summary>
internal sealed class LocalVoices : IDisposable
{
    internal const string RosterFile = "voices.json";
    internal const string EnabledFile = "voice-recognition.txt";
    private readonly object gate = new();
    private readonly string? directory;
    private readonly string? appDirectory;
    private readonly string by;
    private readonly TimeProvider clock;
    private SpeakerEngine? engine;
    private VoiceRoster roster = VoiceRoster.Empty;

    /// <param name="appDirectory">Where the engine and voice models are; Martlet's own folder unless a test says otherwise.</param>
    internal LocalVoices(string? directory, string? device = null, TimeProvider? clock = null, string? appDirectory = null)
    {
        this.directory = directory;
        this.appDirectory = appDirectory;
        by = device ?? HostSetupCommands.SuggestedDeviceId();
        this.clock = clock ?? TimeProvider.System;
        Included = SpeakerEngine.Included(appDirectory);
        if (directory is null) return;
        Enabled = ReadChoice(EnabledFile) ?? true;
        try
        {
            var path = Path.Combine(directory, RosterFile);
            if (File.Exists(path)) roster = VoiceRoster.Parse(File.ReadAllBytes(path));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ContractException)
        {
            LoadError = "Couldn't read your saved voices. Martlet will start a new list.";
        }
    }

    /// <summary>The data folder's speech directory, where Parakeet is downloaded.</summary>
    internal static string SpeechRoot(string directory) => Path.Combine(directory, "speech");

    internal bool Available => directory is not null;
    /// <summary>The engine and voice models are in Martlet's folder (always, in a complete installation).</summary>
    internal bool Included { get; }
    internal bool Enabled { get; private set; }
    /// <summary>On and included: each utterance is checked against the voice list.</summary>
    internal bool Active => Enabled && Included;
    internal string? LoadError { get; private set; }
    internal string Device => by;
    internal VoiceRoster Roster { get { lock (gate) return roster; } }
    /// <summary>Raised (off the dispatcher too) after the voice list changed.</summary>
    internal event Action? Changed;

    internal void SetEnabled(bool on)
    {
        WriteChoice(EnabledFile, on);
        Enabled = on;
        Changed?.Invoke();
    }

    /// <summary>When voice-recognition.txt last changed (null when the owner never chose).</summary>
    internal DateTimeOffset? EnabledChangedAt
    {
        get
        {
            if (directory is null) return null;
            var path = Path.Combine(directory, EnabledFile);
            try { return File.Exists(path) ? new DateTimeOffset(File.GetLastWriteTimeUtc(path), TimeSpan.Zero) : null; }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return null; }
        }
    }

    /// <summary>Who spoke in one utterance (16 kHz mono samples). Confident matches teach the voice a little more; a clearly new
    /// voice with enough clean speech joins the list as "Voice N"; anything in between stays unattributed. A voice heard drops
    /// any name it learned in conversation that is one of the <paramref name="companion"/>'s own, so nobody is called by it.</summary>
    internal HeardVoices Recognize(float[] samples, CompanionNames? companion = null)
    {
        if (!Active) return HeardVoices.None;
        SpeakerEngine current;
        lock (gate) current = engine ??= new SpeakerEngine(appDirectory);
        var analysis = current.Analyze(samples);
        var heard = new List<HeardVoice>();
        var repaired = 0;
        lock (gate)
        {
            var before = roster;
            var now = clock.GetUtcNow();
            foreach (var speaker in analysis.Speakers)
            {
                var match = roster.Identify(speaker.Voiceprint);
                if (match.Kind == VoiceMatchKind.Known && heard.Any(h => h.Voice?.Id == match.Voice!.Id))
                    match = match with { Kind = VoiceMatchKind.Unsure };
                switch (match.Kind)
                {
                    case VoiceMatchKind.Known:
                        roster = roster.Learn(match.Voice!.Id, speaker.Voiceprint, speaker.CleanSeconds, by, now);
                        heard.Add(new(roster.Resolve(match.Voice.Id), match.Kind, match.Score, speaker.CleanSeconds, false));
                        break;
                    case VoiceMatchKind.New:
                        var (next, added) = roster.Add(speaker.Voiceprint, speaker.CleanSeconds, by, now);
                        roster = next;
                        heard.Add(new(added, added is null ? VoiceMatchKind.Unsure : VoiceMatchKind.New, match.Score, speaker.CleanSeconds, added is not null));
                        break;
                    default:
                        heard.Add(new(null, match.Kind, match.Score, speaker.CleanSeconds, false));
                        break;
                }
            }
            // Too short to learn from: a confident match is still recognized, but nothing new is added.
            if (heard.Count == 0 && analysis.Whole is { } whole)
            {
                var match = roster.Identify(whole);
                if (match.Kind == VoiceMatchKind.Known)
                {
                    roster = roster.Heard(match.Voice!.Id, by, now);
                    heard.Add(new(roster.Resolve(match.Voice.Id), match.Kind, match.Score, analysis.SpeechSeconds, false));
                }
                else heard.Add(new(null, match.Kind, match.Score, analysis.SpeechSeconds, false));
            }
            if (companion is not null)
                for (var i = 0; i < heard.Count; i++)
                {
                    if (heard[i].Voice is not { } voice) continue;
                    var next = roster.DropHeardNames(voice.Id, companion.Matches, by, now);
                    if (ReferenceEquals(next, roster)) continue;
                    roster = next;
                    repaired++;
                    heard[i] = heard[i] with { Voice = roster.Resolve(voice.Id) };
                }
            if (!ReferenceEquals(before, roster)) SaveLocked();
        }
        if (repaired > 0) ErrorLog.Info($"A voice heard went by Martlet's own name, learned by mistake; dropped it from {repaired} voice(s).");
        if (heard.Any(h => h.Voice is not null)) Changed?.Invoke();
        return new(heard, analysis.Overlap);
    }

    internal void SetNames(string id, string? name, IEnumerable<string> others) => Change(r => r.SetNames(id, name, others, by, Now));

    /// <summary>Drops from every voice the names it learned in conversation that are the <paramref name="companion"/>'s own
    /// (learning names refuses them now; older versions could pick one up). Names the owner typed stay. Returns how many voices
    /// changed.</summary>
    internal int DropCompanionNames(CompanionNames companion)
    {
        var changed = 0;
        Change(r => r.Live.Aggregate(r, (current, voice) =>
        {
            var next = current.DropHeardNames(voice.Id, companion.Matches, by, Now);
            if (!ReferenceEquals(next, current)) changed++;
            return next;
        }));
        if (changed > 0) ErrorLog.Info($"Dropped Martlet's own name, learned by mistake, from {changed} voice(s).");
        return changed;
    }

    /// <summary>Makes the changes learning names asked for (already checked by <see cref="VoiceUpdates.Parse"/>).</summary>
    internal (IReadOnlyList<VoiceUpdateResult> Applied, IReadOnlyList<VoiceUpdateRefusal> Refused) Apply(IReadOnlyList<VoiceUpdate> updates)
    {
        (IReadOnlyList<VoiceUpdateResult> Applied, IReadOnlyList<VoiceUpdateRefusal> Refused) result = ([], []);
        if (updates.Count == 0) return result;
        Change(r =>
        {
            var (next, applied, refused) = VoiceUpdates.Apply(r, updates, by, Now);
            result = (applied, refused);
            return next;
        });
        return result;
    }

    internal void SetOwner(string id, bool owner) => Change(r => r.SetOwner(id, owner, by, Now));
    internal void Join(string fromId, string intoId) => Change(r => r.Join(fromId, intoId, by, Now));
    internal void Forget(string id) => Change(r => r.Forget(id, by, Now));

    /// <summary>Forgets every voice (tombstones remain, so other copies drop them too).</summary>
    internal void ForgetAll() => Change(r => r.Live.Aggregate(r, (current, voice) => current.Forget(voice.Id, by, Now)));

    /// <summary>Merges another computer's copy into this PC's list; true when this PC's list changed.</summary>
    internal bool Merge(VoiceRoster incoming)
    {
        var changed = false;
        lock (gate)
        {
            var next = VoiceRoster.Merge(roster, incoming);
            if (next.Digest() != roster.Digest())
            {
                roster = next;
                SaveLocked();
                changed = true;
            }
        }
        if (changed) Changed?.Invoke();
        return changed;
    }

    private DateTimeOffset Now => clock.GetUtcNow();

    private void Change(Func<VoiceRoster, VoiceRoster> change)
    {
        lock (gate)
        {
            var next = change(roster);
            if (ReferenceEquals(next, roster)) return;
            roster = next;
            SaveLocked();
        }
        Changed?.Invoke();
    }

    private void SaveLocked()
    {
        if (directory is null) return;
        try
        {
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, RosterFile);
            var temporary = Path.Combine(directory, $"voices.{Guid.NewGuid():N}.tmp");
            try
            {
                File.WriteAllBytes(temporary, roster.Write());
                File.Move(temporary, path, overwrite: true);
            }
            finally
            {
                if (File.Exists(temporary)) File.Delete(temporary);
            }
            LoadError = null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ContractException)
        {
            LoadError = "Couldn't save your voices. Changes last until Martlet closes.";
            ErrorLog.Warn("Saving the voice list failed.", error);
        }
    }

    private bool? ReadChoice(string file)
    {
        try { return File.ReadAllText(Path.Combine(directory!, file)).Trim() switch { "on" => true, "off" => false, _ => null }; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return null; }
    }

    private void WriteChoice(string file, bool on)
    {
        if (directory is null) throw new InvalidOperationException("Martlet's data folder isn't available.");
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, file), on ? "on" : "off");
    }

    public void Dispose()
    {
        lock (gate)
        {
            engine?.Dispose();
            engine = null;
        }
    }
}

/// <summary>Parakeet on this PC as the conversation's speech-to-text: the utterance is transcribed in memory here and nothing
/// is sent anywhere. The model loads on the first utterance (or when Listening switches to it) and stays loaded.</summary>
internal sealed class ParakeetListener(string root) : ILocalTranscriber, IDisposable
{
    private readonly object gate = new();
    private ParakeetEngine? engine;

    internal bool Installed => ParakeetEngine.Installed(root);
    internal string Root => root;

    public Task<LocalTranscript> TranscribeAsync(string modelId, ReadOnlyMemory<byte> pcm16kMono, CancellationToken cancellationToken)
    {
        if (modelId != SherpaComponents.ParakeetModelId) throw new InvalidOperationException("Unknown local speech-to-text model.");
        var samples = Pcm.ToFloats(pcm16kMono.Span);
        return Task.Run(() =>
        {
            try { return Engine().Transcribe(samples).ToLocal(); }
            finally { Array.Clear(samples); }
        }, cancellationToken);
    }

    /// <summary>Loads the model in the background so the first utterance is not slower.</summary>
    internal Task WarmAsync() => Task.Run(() =>
    {
        try { if (Installed) Engine().Warm(); }
        catch (Exception error) when (error is SherpaException or DllNotFoundException or BadImageFormatException or InvalidOperationException)
        {
            ErrorLog.Warn("Loading Parakeet failed.", error);
        }
    });

    private ParakeetEngine Engine()
    {
        lock (gate) return engine ??= new ParakeetEngine(root);
    }

    public void Dispose()
    {
        lock (gate)
        {
            engine?.Dispose();
            engine = null;
        }
    }
}

internal static class Pcm
{
    /// <summary>Parakeet's transcript with what the model said about it (its token probabilities and when it heard them), for
    /// the utterance filter.</summary>
    internal static LocalTranscript ToLocal(this ParakeetTranscript heard) => new(heard.Text, new TranscriptionEvidence
    {
        Engine = "parakeet", MeanProbability = heard.Confidence, MinimumProbability = heard.Minimum,
        WordsStart = heard.FirstToken is { } first ? TimeSpan.FromSeconds(first) : null,
        WordsEnd = heard.LastToken is { } last ? TimeSpan.FromSeconds(last) : null
    });

    internal static float[] ToFloats(ReadOnlySpan<byte> pcm16)
    {
        var samples = new float[pcm16.Length / 2];
        for (var i = 0; i < samples.Length; i++)
            samples[i] = System.Buffers.Binary.BinaryPrimitives.ReadInt16LittleEndian(pcm16.Slice(i * 2, 2)) / 32768f;
        return samples;
    }
}
