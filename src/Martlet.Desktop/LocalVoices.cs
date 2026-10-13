using System.IO;
using Martlet.Core.Cluster;
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
/// Martlet's folder and runs on this PC, or on a computer of the Voice recognition list (Companion › People,
/// <see cref="VoiceRecognitionHosts"/>), with this PC's processor as the fallback that keeps it working
/// (<see cref="VoiceRecognitionRouter{T}"/>, <see cref="VoiceEngineKeeper{T}"/>). Whether it is on (voice-recognition.txt, on unless the owner turned it off) is one of
/// Martlet's shared settings, and the list itself travels through the paired hosts while Martlet is the same on all the
/// owner's computers.</summary>
internal sealed class LocalVoices : IDisposable
{
    internal const string RosterFile = "voices.json";
    internal const string EnabledFile = "voice-recognition.txt";
    private readonly object gate = new();
    private readonly string? directory;
    private readonly string by;
    private readonly TimeProvider clock;
    private readonly VoiceEngineKeeper<SpeakerAnalysis> keeper;
    private readonly VoiceRecognitionHosts hosts;
    private readonly VoiceRecognitionRouter<SpeakerAnalysis> router;
    private VoiceRecognitionLast? lastRecognition;
    private VoiceRoster roster = VoiceRoster.Empty;

    /// <param name="appDirectory">Where the engine and voice models are; Martlet's own folder unless a test says otherwise.</param>
    /// <param name="engine">Makes this PC's engine; tests give a fake one.</param>
    /// <param name="remote">Recognizes on a computer of the Voice recognition list; tests give a fake one.</param>
    /// <param name="queue">The work queue; tests give their own.</param>
    internal LocalVoices(string? directory, string? device = null, TimeProvider? clock = null, string? appDirectory = null,
        Func<IVoiceEngine<SpeakerAnalysis>>? engine = null, VoiceRecognitionRouter<SpeakerAnalysis>.Remote? remote = null,
        WorkQueue? queue = null)
    {
        this.directory = directory;
        by = device ?? HostSetupCommands.SuggestedDeviceId();
        this.clock = clock ?? TimeProvider.System;
        Included = engine is not null || SpeakerEngine.Included(appDirectory);
        keeper = new(engine ?? (() => new SherpaVoiceEngine(appDirectory)), this.clock);
        keeper.Log += (line, error) =>
        {
            if (error is null) ErrorLog.Info(line);
            else ErrorLog.Warn(line, error);
        };
        keeper.Changed += () => RecognitionChanged?.Invoke();
        hosts = new(directory);
        router = new(keeper, remote ?? hosts.AnalyzeAsync, queue, VoiceRecognitionHosts.Classify, this.clock);
        router.Changed += () => RecognitionChanged?.Invoke();
        RecognitionChanged += WriteStateSoon;
        Clips = new VoiceClips(directory);
        if (directory is null) return;
        Enabled = ReadChoice(EnabledFile) ?? true;
        if (!Enabled) keeper.Pause();
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
        if (on) keeper.Resume();
        else keeper.Pause();
        Changed?.Invoke();
        WriteStateSoon();
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
        // The same utterance was already analyzed for a reply started early (Identify): who spoke is known.
        (byte[] Hash, SpeakerAnalysis Analysis)? earlier;
        lock (gate)
        {
            earlier = identified;
            identified = null;
        }
        var analysis = earlier is { } early && early.Hash.AsSpan().SequenceEqual(Hash(samples)) ? early.Analysis : Analyze(samples);
        var heard = new List<HeardVoice>();
        var spans = new List<(string Id, double Start, double End)>();
        var notes = new List<string>();
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
                        var (next, added) = roster.Add(speaker.Voiceprint, speaker.CleanSeconds, by, now, out var forgotten);
                        roster = next;
                        if (forgotten is not null)
                            notes.Add($"Martlet forgot {forgotten.DisplayName} (not heard since {forgotten.LastHeardAt.ToLocalTime():yyyy-MM-dd}) " +
                                $"to make room for a new voice: the voice list keeps at most {VoiceRoster.MaximumVoices:N0} voices. " +
                                "Name a voice in Companion › People to keep it.");
                        if (added is null && !listFullNoted)
                            notes.Add($"A new voice wasn't added: the voice list is full ({VoiceRoster.MaximumVoices:N0} voices) and every " +
                                "voice has a name or belongs to someone. Forget or merge a voice in Companion › People to make room.");
                        listFullNoted = added is null;
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
        // A voice forgotten to make room takes its clips with it, as when the owner forgets one.
        if (notes.Count > 0) Clips.Prune(Roster);
        foreach (var note in notes) ErrorLog.Info(note);
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
        var analysis = Analyze(samples);
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
    // A full list whose voices all have names was already noted in the log; noted again after a voice can be added.
    private bool listFullNoted;

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

    /// <summary>The voice engine is in memory now (it loads on the first utterance it checks).</summary>
    internal bool Loaded => keeper.Loaded;

    /// <summary>How this PC's engine is doing (ready, failed and when it tries again, its speed).</summary>
    internal VoiceEngineStatus Engine => keeper.Status;

    /// <summary>What Martlet knows about each computer of the Voice recognition list it tried since it started.</summary>
    internal IReadOnlyList<VoiceRecognitionHost> RecognitionHosts => router.Hosts;

    /// <summary>The last recognition: where, how long and why places before it were passed over; null before the first.</summary>
    internal VoiceRecognitionLast? LastRecognition { get { lock (gate) return lastRecognition; } }

    /// <summary>Raised (off the dispatcher too) when where recognition runs or how this PC's engine is doing changed.</summary>
    internal event Action? RecognitionChanged;

    /// <summary>Why voice recognition doesn't work on this PC now, in plain words; null while it works (or is off).</summary>
    internal string? RecognitionProblem => RecognitionFailure is { } failure ? "Voice recognition on this PC stopped working: " + failure : null;

    /// <summary>This PC's engine's failure and when Martlet tries it again; null while it works (or recognition is off).</summary>
    internal string? RecognitionFailure => Enabled && Included && keeper.Status is { State: VoiceEngineState.Failed } failed
        ? failed.Problem + (failed.RetryAt is { } at ? $" Martlet loads it again and tries at {at.ToLocalTime():t}." : "")
        : null;

    /// <summary>Home's Try again: loads a new engine now. True when it works.</summary>
    internal bool CheckEngine() => keeper.Check();

    /// <summary>The places this PC tries, first to last (<see cref="VoiceRecognitionHosts.Targets"/>).</summary>
    internal IReadOnlyList<VoiceRecognitionTarget> RecognitionTargets => VoiceRecognitionHosts.Targets(directory, by);

    /// <summary>People's Check it now: recognizes three seconds of a generated tone through the Voice recognition list, the way an
    /// utterance goes (<see cref="LastRecognition"/> says where and how long). Nothing is learned, added or kept. Throws
    /// <see cref="VoiceEngineException"/> when no place could recognize it.</summary>
    internal VoiceRecognitionLast Check()
    {
        var tone = new float[3 * SpeakerEngine.SampleRate];
        for (var i = 0; i < tone.Length; i++)
        {
            var t = i / (double)SpeakerEngine.SampleRate;
            var f = 140 + 40 * Math.Sin(Math.PI * t);
            tone[i] = (float)(0.2 * Math.Sin(2 * Math.PI * f * t) + 0.1 * Math.Sin(4 * Math.PI * f * t));
        }
        Analyze(tone);
        return LastRecognition!;
    }

    /// <summary>Recognizes the utterance at the first place of the Voice recognition list that can (<see cref="VoiceRecognitionRouter{T}"/>):
    /// a computer's role or this PC's engine, which loads first when it isn't (also after Free up this PC unloaded it) and is
    /// loaded again after a failure (<see cref="VoiceEngineKeeper{T}"/>).</summary>
    private SpeakerAnalysis Analyze(float[] samples)
    {
        var targets = RecognitionTargets;
        // Called on a worker thread (the turn's recognition task); the router waits for a computer at most a few seconds.
        var run = router.RunAsync(targets, samples, CancellationToken.None).GetAwaiter().GetResult();
        var seconds = samples.Length / (double)SpeakerEngine.SampleRate;
        var last = new VoiceRecognitionLast(run.By.Key, run.By.Here ? "this PC's processor" : $"{run.By.HostId}'s Voice recognition role",
            Math.Round(run.Took.TotalMilliseconds), Math.Round(seconds, 1), run.Raced, run.Passed, Now);
        string? before;
        lock (gate)
        {
            before = lastRecognition?.Key;
            lastRecognition = last;
        }
        ErrorLog.Info($"Voice recognition: {last.Where} in {last.Milliseconds:0} ms ({seconds:0.0} s of speech)" +
            (run.Raced ? ", racing this PC after a computer didn't answer in time" : "") +
            (run.Passed.Count > 0 ? "; passed over: " + string.Join("; ", run.Passed) : "") + ".");
        if (before != last.Key) RecognitionChanged?.Invoke();
        else WriteStateSoon();
        return run.Result;
    }

    private int stateQueued;
    private bool writesState;

    /// <summary>Whether this PC's recognition state is written for MCP (<see cref="VoiceRecognitionHosts.StateFile"/>); the main
    /// window turns it on.</summary>
    internal bool WritesState
    {
        get => writesState;
        set
        {
            writesState = value;
            WriteStateSoon();
        }
    }

    /// <summary>Writes <see cref="VoiceRecognitionHosts.StateFile"/> a second from now (once for any number of changes), in the
    /// background, so recognition never waits for the disk. Only once the main window turned <see cref="WritesState"/> on.</summary>
    private void WriteStateSoon()
    {
        if (!WritesState || directory is null || Interlocked.Exchange(ref stateQueued, 1) == 1) return;
        _ = Task.Run(async () =>
        {
            await Task.Delay(TimeSpan.FromSeconds(1)).ConfigureAwait(false);
            Volatile.Write(ref stateQueued, 0);
            VoiceRecognitionHosts.WriteState(directory, Enabled, keeper.Status, keeper.Loaded, LastRecognition, router.Hosts,
                RecognitionTargets, RecognitionProblem);
        });
    }

    /// <summary>Home's Free up this PC: takes the voice engine out of memory. The next utterance loads it again. True when it
    /// was loaded.</summary>
    internal bool Unload() => keeper.Unload();

    public void Dispose()
    {
        keeper.Dispose();
        hosts.Dispose();
    }
}

/// <summary>The last recognition, for People, Home and the status file: the place's key and name, how long it took, how much
/// speech, whether this PC raced a slow computer, and why places before it were passed over.</summary>
internal sealed record VoiceRecognitionLast(string Key, string Where, double Milliseconds, double Seconds, bool Raced,
    IReadOnlyList<string> Passed, DateTimeOffset At);

/// <summary>This PC's sherpa-onnx engine (<see cref="SpeakerEngine"/>) as <see cref="VoiceEngineKeeper{T}"/> keeps it. Load takes
/// a voiceprint of a second of silence, which loads both models.</summary>
internal sealed class SherpaVoiceEngine(string? appDirectory) : IVoiceEngine<SpeakerAnalysis>
{
    private readonly SpeakerEngine engine = new(appDirectory);

    public SpeakerAnalysis Analyze(float[] samples) => engine.Analyze(samples);

    public void Load() => _ = engine.Voiceprint(new float[SpeakerEngine.SampleRate]);

    public void Dispose() => engine.Dispose();
}

/// <summary>Parakeet on this PC as the conversation's speech-to-text: the utterance is transcribed in memory here and nothing
/// is sent anywhere. The Listening route's model (one of <see cref="ParakeetModels"/>) loads on the first utterance (or when
/// Listening switches to it) and stays loaded; one model is loaded at a time, so asking for another one unloads the first.</summary>
internal sealed class ParakeetListener(string root) : ILocalTranscriber, IDisposable
{
    private readonly object gate = new();
    private ParakeetEngine? engine;
    private bool disposed;
    private int warming;

    /// <summary>The model is downloaded here and Martlet's folder has the speech runtime.</summary>
    internal bool Installed(string modelId) => ParakeetEngine.Installed(root, modelId);
    internal string Root => root;
    /// <summary>The model loaded (or loading) now; null before the first use.</summary>
    internal string? Loaded { get { lock (gate) return engine?.Model.Id; } }
    /// <summary>The model that is in memory now (null when none is): its name, such as "Parakeet TDT 0.6B v3".</summary>
    internal string? InMemory { get { lock (gate) return engine is { IsLoaded: true } loaded ? loaded.Model.Name : null; } }
    /// <summary><see cref="WarmAsync"/> is loading a model now: its name, else null.</summary>
    internal string? Warming => Volatile.Read(ref warming) > 0 ? warmingName : null;
    private volatile string? warmingName;
    /// <summary>When the warming load started, for how long it has taken.</summary>
    internal long WarmingSince { get; private set; }

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

    /// <summary>Loads <paramref name="modelId"/> in the background so the first utterance is not slower. While it loads,
    /// <see cref="Warming"/> names it (Home and the talk window say Martlet is warming up).</summary>
    internal Task WarmAsync(string modelId) => Task.Run(() =>
    {
        try
        {
            if (!Installed(modelId)) return;
            var current = Engine(modelId);
            if (current.IsLoaded) return;
            warmingName = current.Model.Name;
            WarmingSince = System.Diagnostics.Stopwatch.GetTimestamp();
            Interlocked.Increment(ref warming);
            try { current.Warm(); }
            finally { Interlocked.Decrement(ref warming); }
        }
        catch (ObjectDisposedException) { }
        catch (Exception error) when (error is SherpaException or DllNotFoundException or BadImageFormatException or InvalidOperationException)
        {
            ErrorLog.Warn("Loading Parakeet failed.", error);
        }
    });

    /// <summary>Home's Free up this PC: takes the model out of memory without closing the listener. The next utterance (or
    /// <see cref="WarmAsync"/>) loads it again. The model's name when one was in memory, else null.</summary>
    internal string? Unload()
    {
        ParakeetEngine? loaded;
        lock (gate)
        {
            loaded = engine;
            engine = null;
        }
        var name = loaded is { IsLoaded: true } ? loaded.Model.Name : null;
        // Unloading waits for a transcription still running, so it happens outside the gate.
        loaded?.Dispose();
        return name;
    }

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
