using System.IO;
using Martlet.Core.Contracts;
using Martlet.Core.Speakers;
using Martlet.Providers;
using Martlet.Sherpa;

namespace Martlet.Desktop;

/// <summary>One voice heard in an utterance and what Martlet made of it. <see cref="Voice"/> is null when the voice could not
/// be told apart (too little speech, or too close to call); <see cref="Added"/> means it was new and joined the list.
/// <see cref="Mine"/>: the voice is the signed-in person's (<see cref="LocalVoices.IsYours"/>) as it was heard.</summary>
internal sealed record HeardVoice(KnownVoice? Voice, VoiceMatchKind Kind, double Score, double Seconds, bool Added)
{
    internal bool Mine { get; init; }
}

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
/// by, never audio (the last few clips of voices the owner hasn't named are kept apart, on this PC only: <see cref="VoiceClips"/>); the engine (sherpa-onnx with the WeSpeaker and pyannote models, AudioTranscriber's pipeline) ships in
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
        Clips = new VoiceClips(directory);
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
        Clips.Prune(roster);
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
    /// <summary>The last few clips of each voice the owner hasn't named yet, on this PC only.</summary>
    internal VoiceClips Clips { get; }
    /// <summary>Raised (off the dispatcher too) after the voice list changed.</summary>
    internal event Action? Changed;
    /// <summary>Raised (off the dispatcher) after a new clip was kept.</summary>
    internal event Action? ClipsChanged;

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
        // The same utterance was already analyzed for a reply started early (Identify): who spoke is known.
        (byte[] Hash, SpeakerAnalysis Analysis)? earlier;
        lock (gate)
        {
            earlier = identified;
            identified = null;
        }
        var analysis = earlier is { } early && early.Hash.AsSpan().SequenceEqual(Hash(samples)) ? early.Analysis : current.Analyze(samples);
        var heard = new List<HeardVoice>();
        var spans = new List<(string Id, double Start, double End)>();
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
                        spans.Add((match.Voice.Id, speaker.Start, speaker.End));
                        heard.Add(new(roster.Resolve(match.Voice.Id), match.Kind, match.Score, speaker.CleanSeconds, false));
                        break;
                    case VoiceMatchKind.New:
                        var (next, added) = roster.Add(speaker.Voiceprint, speaker.CleanSeconds, by, now);
                        roster = next;
                        if (added is not null) spans.Add((added.Id, speaker.Start, speaker.End));
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
                    spans.Add((match.Voice.Id, 0, samples.Length / (double)SpeakerEngine.SampleRate));
                    heard.Add(new(roster.Resolve(match.Voice.Id), match.Kind, match.Score, analysis.SpeechSeconds, false));
                }
                else heard.Add(new(null, match.Kind, match.Score, analysis.SpeechSeconds, false));
            }
            if (companion is not null)
                for (var i = 0; i < heard.Count; i++)
                {
                    if (heard[i].Voice is not { } voice) continue;
                    var next = roster.DropHeardNames(voice.Id, name => companion.Matches(name) || VoiceUpdates.IsNotName(name), by, now);
                    if (ReferenceEquals(next, roster)) continue;
                    roster = next;
                    repaired++;
                    heard[i] = heard[i] with { Voice = roster.Resolve(voice.Id) };
                }
            if (!ReferenceEquals(before, roster)) SaveLocked();
        }
        KeepClips(samples, spans);
        if (repaired > 0) ErrorLog.Info($"A voice heard went by Martlet's own name or a placeholder, learned by mistake; dropped it from {repaired} voice(s).");
        if (heard.Any(h => h.Voice is not null)) Changed?.Invoke();
        return Stamp(new(heard, analysis.Overlap));
    }

    /// <summary>Who spoke in one utterance as <see cref="Recognize"/> would say it, without changing anything: nothing is
    /// learned, added, repaired, kept or saved. A reply started early (Companion › Listening › Start replies early) uses it
    /// before the turn ends; <see cref="Recognize"/> of exactly the same samples then reuses this analysis (only its SHA-256
    /// is kept to tell them apart, never audio). A voice new to the list has no tag yet, so it reads as one that couldn't be
    /// told apart.</summary>
    internal HeardVoices Identify(float[] samples)
    {
        if (!Active) return HeardVoices.None;
        SpeakerEngine current;
        lock (gate) current = engine ??= new SpeakerEngine(appDirectory);
        var analysis = current.Analyze(samples);
        var hash = Hash(samples);
        lock (gate)
        {
            identified = (hash, analysis);
            var heard = new List<HeardVoice>();
            foreach (var speaker in analysis.Speakers)
            {
                var match = roster.Identify(speaker.Voiceprint);
                if (match.Kind == VoiceMatchKind.Known && heard.Any(h => h.Voice?.Id == match.Voice!.Id))
                    match = match with { Kind = VoiceMatchKind.Unsure };
                heard.Add(match.Kind == VoiceMatchKind.Known
                    ? new(roster.Resolve(match.Voice!.Id), match.Kind, match.Score, speaker.CleanSeconds, false)
                    : new(null, VoiceMatchKind.Unsure, match.Score, speaker.CleanSeconds, false));
            }
            if (heard.Count == 0 && analysis.Whole is { } whole)
            {
                var match = roster.Identify(whole);
                heard.Add(match.Kind == VoiceMatchKind.Known
                    ? new(roster.Resolve(match.Voice!.Id), match.Kind, match.Score, analysis.SpeechSeconds, false)
                    : new(null, match.Kind, match.Score, analysis.SpeechSeconds, false));
            }
            return Stamp(new(heard, analysis.Overlap));
        }
    }

    // The last utterance Identify analyzed: its samples' SHA-256 and who was heard in it.
    private (byte[] Hash, SpeakerAnalysis Analysis)? identified;

    private static byte[] Hash(float[] samples) =>
        System.Security.Cryptography.SHA256.HashData(System.Runtime.InteropServices.MemoryMarshal.AsBytes(samples.AsSpan()));

    /// <summary>Keeps what each voice the owner hasn't named yet said as its newest clip, copied now (the caller clears the
    /// samples) and written in the background, so recognition never waits for the disk.</summary>
    private void KeepClips(float[] samples, IReadOnlyList<(string Id, double Start, double End)> spans)
    {
        if (!Clips.Enabled || spans.Count == 0) return;
        var current = Roster;
        var now = Now;
        var clips = new List<(string Id, float[] Samples)>();
        foreach (var (id, start, end) in spans.DistinctBy(s => s.Id))
        {
            if (current.Resolve(id) is not { } voice || !VoiceClips.Wanted(voice)) continue;
            var first = Math.Clamp((int)(start * SpeakerEngine.SampleRate), 0, samples.Length);
            var last = Math.Clamp((int)(Math.Min(end, start + VoiceClips.MaximumSeconds) * SpeakerEngine.SampleRate), first, samples.Length);
            if (last > first) clips.Add((voice.Id, samples[first..last]));
        }
        if (clips.Count == 0) return;
        Task.Run(() =>
        {
            foreach (var (id, clip) in clips) Clips.Save(id, clip, now);
            ClipsChanged?.Invoke();
        });
    }

    internal void SetNames(string id, string? name, IEnumerable<string> others) => Change(r => r.SetNames(id, name, others, by, Now));

    /// <summary>Drops from every voice the names it learned in conversation that are the <paramref name="companion"/>'s own or
    /// placeholders such as "no name yet" (learning names refuses them now; older versions could pick one up). Names the owner typed stay. Returns how many voices
    /// changed.</summary>
    internal int DropCompanionNames(CompanionNames companion)
    {
        var changed = 0;
        Change(r => r.Live.Aggregate(r, (current, voice) =>
        {
            var next = current.DropHeardNames(voice.Id, name => companion.Matches(name) || VoiceUpdates.IsNotName(name), by, Now);
            if (!ReferenceEquals(next, current)) changed++;
            return next;
        }));
        if (changed > 0) ErrorLog.Info($"Dropped Martlet's own name or a placeholder, learned by mistake, from {changed} voice(s).");
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

    // ---------- whose voice: links to accounts (docs/ACCOUNTS.md, Voices) ----------

    private Guid? account, ownerAccount;

    /// <summary>The signed-in account: "your voice" is a voice linked to it. Null until the desktop knows its account; then a
    /// voice marked as the owner's (<see cref="KnownVoice.Owner"/>) stands for yours, as before accounts.</summary>
    internal Guid? Account { get { lock (gate) return account; } }

    /// <summary>The household owner's account, when known.</summary>
    internal Guid? OwnerAccount { get { lock (gate) return ownerAccount; } }

    /// <summary>Sets the signed-in account and the household owner's (only between replies, when the account changes). With the
    /// owner's account known, each voice an older Martlet marked "This is me" links to it.</summary>
    internal void UseAccount(Guid? signedIn, Guid? owner)
    {
        bool changed;
        lock (gate)
        {
            changed = account != signedIn || ownerAccount != owner;
            account = signedIn;
            ownerAccount = owner;
        }
        if (owner is { } id) Change(r => r.LinkOwnerVoices(id, by, Now));
        if (changed) Changed?.Invoke();
    }

    /// <summary>Whether <paramref name="voice"/> is the signed-in person's: linked to their account.</summary>
    internal bool IsYours(KnownVoice? voice)
    {
        if (voice is null || voice.Removed) return false;
        lock (gate) return account is { } signedIn ? voice.Account == signedIn : voice.Owner;
    }

    /// <summary>The signed-in person's voices, most recently heard first.</summary>
    internal IReadOnlyList<KnownVoice> Yours => Roster.Live.Where(IsYours).ToArray();

    /// <summary>Links the voice to the signed-in account (<paramref name="yours"/>), or unlinks it. Without a known account it
    /// marks the voice as the owner's, as before accounts.</summary>
    internal void Link(string id, bool yours)
    {
        Guid? signedIn, owner;
        lock (gate) (signedIn, owner) = (account, ownerAccount);
        if (signedIn is null) SetOwner(id, yours);
        else Change(r => yours || r.Resolve(id)?.Account == signedIn
            ? r.SetAccount(id, yours ? signedIn : null, yours && signedIn == owner, by, Now) : r);
    }

    /// <summary>W12, *Merge another account into this one*: every voice linked to <paramref name="from"/> links to
    /// <paramref name="into"/>. Returns how many moved.</summary>
    internal int MoveLinks(Guid from, Guid into)
    {
        Guid? owner;
        lock (gate) owner = ownerAccount;
        var moving = Roster.LinkedTo(from);
        if (moving.Count > 0) Change(r => moving.Aggregate(r, (current, voice) => current.SetAccount(voice.Id, into, into == owner, by, Now)));
        return moving.Count;
    }

    private HeardVoices Stamp(HeardVoices heard) =>
        heard with { Voices = heard.Voices.Select(v => v.Voice is null ? v : v with { Mine = IsYours(v.Voice) }).ToArray() };
    internal void Join(string fromId, string intoId)
    {
        if (Roster.Resolve(fromId) is { } from && Roster.Resolve(intoId) is { } into && from.Id != into.Id) Clips.Move(from.Id, into.Id);
        Change(r => r.Join(fromId, intoId, by, Now));
    }
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
            if (ownerAccount is { } owner) next = next.LinkOwnerVoices(owner, by, Now);
            if (next.Digest() != roster.Digest())
            {
                roster = next;
                SaveLocked();
                changed = true;
            }
        }
        if (changed)
        {
            Clips.Prune(Roster);
            Changed?.Invoke();
        }
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
        Clips.Prune(Roster);
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
/// is sent anywhere. The Listening route's model (one of <see cref="ParakeetModels"/>) loads on the first utterance (or when
/// Listening switches to it) and stays loaded; one model is loaded at a time, so asking for another one unloads the first.</summary>
internal sealed class ParakeetListener(string root) : ILocalTranscriber, IDisposable
{
    private readonly object gate = new();
    private ParakeetEngine? engine;
    private bool disposed;

    /// <summary>The model is downloaded here and Martlet's folder has the speech runtime.</summary>
    internal bool Installed(string modelId) => ParakeetEngine.Installed(root, modelId);
    internal string Root => root;
    /// <summary>The model loaded (or loading) now; null before the first use.</summary>
    internal string? Loaded { get { lock (gate) return engine?.Model.Id; } }

    public Task<LocalTranscript> TranscribeAsync(string modelId, ReadOnlyMemory<byte> pcm16kMono, CancellationToken cancellationToken)
    {
        if (ParakeetModels.Find(modelId) is null) throw new InvalidOperationException("Unknown local speech-to-text model.");
        var samples = Pcm.ToFloats(pcm16kMono.Span);
        return Task.Run(() =>
        {
            try
            {
                try { return Engine(modelId).Transcribe(samples).ToLocal(); }
                // Another model took its place while this waited for it: the current one transcribes it instead.
                catch (ObjectDisposedException) when (!disposed) { return Engine(modelId).Transcribe(samples).ToLocal(); }
            }
            finally { Array.Clear(samples); }
        }, cancellationToken);
    }

    /// <summary>Loads <paramref name="modelId"/> in the background so the first utterance is not slower.</summary>
    internal Task WarmAsync(string modelId) => Task.Run(() =>
    {
        try { if (Installed(modelId)) Engine(modelId).Warm(); }
        catch (Exception error) when (error is SherpaException or DllNotFoundException or BadImageFormatException or InvalidOperationException)
        {
            ErrorLog.Warn("Loading Parakeet failed.", error);
        }
    });

    private ParakeetEngine Engine(string modelId)
    {
        ParakeetEngine? replaced = null;
        ParakeetEngine current;
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (engine is not null && engine.Model.Id != modelId)
            {
                replaced = engine;
                engine = null;
            }
            current = engine ??= new ParakeetEngine(root, modelId);
        }
        // Unloading waits for a transcription still running on the old model, so it happens outside the gate.
        replaced?.Dispose();
        return current;
    }

    public void Dispose()
    {
        ParakeetEngine? loaded;
        lock (gate)
        {
            disposed = true;
            loaded = engine;
            engine = null;
        }
        loaded?.Dispose();
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
