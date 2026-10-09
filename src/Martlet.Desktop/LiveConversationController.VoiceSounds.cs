using Martlet.Conversation;
using Martlet.Core.Settings;

namespace Martlet.Desktop;

/// <summary>Where a touch zone's voice sounds stand for the voice replies speak with now: the voice in words (no secrets), whether
/// it is a paid cloud voice, the sounds it makes alone, which of them are made on this PC, which are being made, why the last
/// one couldn't be made, and what the last touch's sound did.</summary>
internal sealed record VoiceSoundStatus(string? Voice, bool Paid, SpeechEngine? Engine, IReadOnlyList<VoiceTag> Sounds,
    IReadOnlyList<string> Made, IReadOnlyList<string> Making, string? Problem, string? Last)
{
    /// <summary>Why no voice sound plays at all, or null when the voice makes some.</summary>
    internal string? NoSounds => VoiceSounds.NoSoundsReason(Engine, Voice is not null);
}

// Voice sounds a touch zone plays (a gasp, a giggle, a sigh: "sound:<cue>" entries in its reaction list). Each is made once per voice
// and character with the reply's own voice path (ConversationRuntime.SynthesizeAsync, the engine's tag alone) and kept in
// voice-sounds\ in the data folder; a touch plays the kept clip at once through the reply's speakers with lip sync
// (ConversationRuntime.PlayClipAsync), never a request to the voice. Clips are made only while no reply runs, and a paid cloud
// voice makes one only on the owner's click (Hear it).
internal sealed partial class LiveConversationController
{
    private readonly object voiceSoundLock = new();
    private string? voiceSoundKey;
    private readonly Dictionary<string, byte[]> voiceSoundClips = new(StringComparer.Ordinal);
    private readonly Queue<string> voiceSoundQueue = new();
    private readonly HashSet<string> voiceSoundQueued = new(StringComparer.Ordinal);
    private bool voiceSoundMaking;
    private string? voiceSoundProblem, voiceSoundLast;
    private readonly CancellationTokenSource voiceSoundStop = new();
    private ConversationRuntime? voiceSoundRuntime;
    private ConversationCredentialSource? voiceSoundCredentials;
    private ConversationAuthorization? voiceSoundAuthorization;
    // The clip each zone played last, so the zone's sounds take turns.
    private readonly Dictionary<string, int> voiceSoundTurns = new(StringComparer.Ordinal);

    /// <summary>Raised (on any thread) when a voice sound was made, played or skipped.</summary>
    internal event Action? VoiceSoundsChanged;

    /// <summary>Where voice sounds stand now for the voice replies speak with.</summary>
    internal VoiceSoundStatus VoiceSoundsNow
    {
        get
        {
            var configured = Configuration;
            var voice = QuickSoundVoice(configured);
            var engine = configured?.SpeakingEngine();
            var sounds = VoiceSounds.Supported(engine);
            lock (voiceSoundLock)
            {
                FollowVoiceLocked(voice?.Key);
                var made = voice is { } v && dataDirectory is not null
                    ? [.. VoiceSoundLibrary.Made(dataDirectory, v.Key).Select(c => c.Cue).Union(voiceSoundClips.Keys).Distinct(StringComparer.Ordinal)]
                    : voiceSoundClips.Keys.ToArray();
                return new(voice?.Words, voice?.Paid == true, engine, sounds, made, [.. voiceSoundQueue], voiceSoundProblem, voiceSoundLast);
            }
        }
    }

    /// <summary>The voice sounds a talk window loaded with these settings would make, from the settings alone (before any talk
    /// window configured this controller).</summary>
    internal static IReadOnlyList<VoiceTag> VoiceSoundsOf(SetupSettings? setup) =>
        VoiceSounds.Supported(VoiceSounds.EngineOf(setup?.Routes.FirstOrDefault(r => r.Role == SetupRole.Tts && r.Enabled == true)));

    /// <summary>Plays one of <paramref name="cues"/> for a touch on <paramref name="zone"/> (they take turns, skipping any the voice
    /// doesn't make), when the rules allow (<see cref="VoiceSoundGate"/>): never over Martlet's voice or a song, never while
    /// <paramref name="userTalking"/>, never with <paramref name="aloud"/> (Speak Martlet's replies aloud) off. A sound whose clip
    /// isn't made yet is made in the background when the voice is free to use, and plays from the next touch. Returns what
    /// happened, as the last-touch line says it.</summary>
    internal VoiceSoundDecision PlayVoiceSound(string zone, IReadOnlyList<string> cues, string reason, bool userTalking, bool aloud)
    {
        if (cues.Count == 0) return new(VoiceSoundVerdict.Skip, "the zone has no voice sounds");
        var configured = Configuration;
        var voice = QuickSoundVoice(configured);
        var engine = configured?.SpeakingEngine();
        var makes = cues.Where(cue => VoiceSounds.Tag(engine, cue) is not null).ToArray();
        string cue;
        lock (voiceSoundLock)
        {
            var turn = voiceSoundTurns.TryGetValue(zone, out var last) ? last + 1 : 0;
            cue = makes.Length > 0 ? makes[turn % makes.Length] : cues.FirstOrDefault() ?? "";
            if (makes.Length > 0) voiceSoundTurns[zone] = turn % makes.Length;
        }
        bool stopped;
        lock (gate) stopped = paused || muted || locked;
        var clip = voice is { } chosen ? Clip(chosen.Key, cue) : null;
        var decision = VoiceSoundGate.Decide(new(voice is not null, aloud, stopped, makes.Length > 0, Speaking is not null, userTalking,
            runtime.ClipPlaying, clip is not null));
        var why = decision.Why;
        switch (decision.Verdict)
        {
            case VoiceSoundVerdict.Play:
                var output = configured!.SpeechOutput();
                Task.Run(async () =>
                {
                    var began = clock.GetTimestamp();
                    var played = await runtime.PlayClipAsync(clip!, output, voiceSoundStop.Token).ConfigureAwait(false);
                    ErrorLog.Info($"Voice sound: {cue} for {reason} {(played ? "played" : "was cut short or couldn't play")} " +
                        $"({clip!.Length / 48} ms clip, {clock.GetElapsedTime(began).TotalMilliseconds:0} ms).");
                }).Forget();
                break;
            case VoiceSoundVerdict.Make:
                why = voice!.Value.Paid
                    ? why + $"; {voice.Value.Words} is paid, so it is made only when you click Hear it on the zone"
                    : MakeVoiceSounds([cue], click: false) ? why + "; it is being made and plays from the next touch" : why;
                break;
        }
        var line = $"{VoiceSounds.Label(cue)}: {(decision.Verdict == VoiceSoundVerdict.Play ? "played" : "not played")} ({why})";
        lock (voiceSoundLock) voiceSoundLast = line;
        if (decision.Verdict != VoiceSoundVerdict.Play) ErrorLog.Info($"Voice sound: {cue} for {reason} not played ({why}).");
        VoiceSoundsChanged?.Invoke();
        return decision with { Why = line };
    }

    /// <summary>The owner's Hear it on a sound: plays it now when its clip is made (unless Martlet speaks), otherwise makes it
    /// first (also with a paid cloud voice: one short request) and then plays it. Returns what happens, in words.</summary>
    internal string HearVoiceSound(string cue)
    {
        var configured = Configuration;
        var voice = QuickSoundVoice(configured);
        var engine = configured?.SpeakingEngine();
        if (voice is not { } chosen) return "Martlet has no voice set up yet. Open the talk window once, then try again.";
        if (VoiceSounds.Tag(engine, cue) is null) return VoiceSounds.NoSoundsReason(engine, true) ?? $"{chosen.Words} doesn't make that sound.";
        if (Speaking is not null) return "Martlet is speaking. Try again when it is quiet.";
        if (Clip(chosen.Key, cue) is { } clip)
        {
            if (runtime.ClipPlaying) return "Another voice sound is still playing.";
            var output = configured!.SpeechOutput();
            Task.Run(async () =>
            {
                var played = await runtime.PlayClipAsync(clip, output, voiceSoundStop.Token).ConfigureAwait(false);
                lock (voiceSoundLock) voiceSoundLast = $"{VoiceSounds.Label(cue)}: {(played ? "played" : "not played")} (you clicked Hear it)";
                ErrorLog.Info($"Voice sound: {cue} {(played ? "played" : "couldn't play")} (Hear it).");
                VoiceSoundsChanged?.Invoke();
            }).Forget();
            return $"Playing {VoiceSounds.Label(cue).ToLowerInvariant()}.";
        }
        lock (voiceSoundLock) voiceSoundHear = cue;
        return MakeVoiceSounds([cue], click: true)
            ? $"Making {VoiceSounds.Label(cue).ToLowerInvariant()} with {chosen.Words}; it plays when it is ready."
            : "It couldn't be made now.";
    }

    // The sound the owner asked to hear: it plays once it is made.
    private string? voiceSoundHear;

    /// <summary>The voice sounds the character's touch zones use now (their cues), so a new voice makes them before the first
    /// touch; set by the main window.</summary>
    internal Func<IEnumerable<string>>? WantedVoiceSounds { get; set; }

    // Follows the voice: the sounds the zones use are made in the background when the voice is free to use.
    private void FollowVoiceSounds()
    {
        if (WantedVoiceSounds?.Invoke() is { } wanted) MakeVoiceSounds([.. wanted], click: false);
        VoiceSoundsChanged?.Invoke();
    }

    /// <summary>Makes the clips of <paramref name="cues"/> the voice makes and doesn't have yet, one after another in the
    /// background, each only while no reply runs. A paid cloud voice makes them only on the owner's <paramref name="click"/>.
    /// Returns false when there is no voice, or it is paid and this isn't a click.</summary>
    internal bool MakeVoiceSounds(IEnumerable<string> cues, bool click)
    {
        var configured = Configuration;
        if (QuickSoundVoice(configured) is not { } voice || voice.Paid && !click) return false;
        var engine = configured!.SpeakingEngine();
        var missing = cues.Distinct(StringComparer.Ordinal).Where(c => VoiceSounds.Tag(engine, c) is not null && Clip(voice.Key, c) is null).ToArray();
        bool start;
        lock (voiceSoundLock)
        {
            FollowVoiceLocked(voice.Key);
            foreach (var cue in missing)
                if (voiceSoundQueued.Add(cue)) voiceSoundQueue.Enqueue(cue);
            start = !voiceSoundMaking && voiceSoundQueue.Count > 0;
            if (start) voiceSoundMaking = true;
        }
        if (start) Task.Run(() => MakeVoiceSoundsAsync(voice.Key, voiceSoundStop.Token)).Forget();
        return true;
    }

    // The clip kept for the cue with this voice, read from the data folder the first time.
    private byte[]? Clip(string key, string cue)
    {
        lock (voiceSoundLock)
        {
            FollowVoiceLocked(key);
            if (voiceSoundClips.TryGetValue(cue, out var kept)) return kept;
        }
        if (dataDirectory is null || VoiceSoundLibrary.Load(dataDirectory, key, cue) is not { } loaded) return null;
        lock (voiceSoundLock)
        {
            if (voiceSoundKey != key) return null;
            voiceSoundClips[cue] = loaded;
        }
        return loaded;
    }

    // Another voice (or character) forgets the clips kept in memory and what waits to be made.
    private void FollowVoiceLocked(string? key)
    {
        if (key == voiceSoundKey) return;
        voiceSoundKey = key;
        voiceSoundClips.Clear();
        voiceSoundQueue.Clear();
        voiceSoundQueued.Clear();
        voiceSoundProblem = null;
        voiceSoundHear = null;
    }

    // Says each waiting sound once with the reply's own voice, never beside a reply (the voice is the reply's first), and keeps
    // the clips for this voice and character.
    private async Task MakeVoiceSoundsAsync(string key, CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                string cue;
                lock (voiceSoundLock)
                {
                    if (voiceSoundKey != key || voiceSoundQueue.Count == 0) return;
                    cue = voiceSoundQueue.Peek();
                }
                while (Replying || Speaking is not null) await Task.Delay(TimeSpan.FromMilliseconds(200), clock, token).ConfigureAwait(false);
                var configured = Configuration;
                if (QuickSoundVoice(configured) is not { } voice || voice.Key != key) return;
                var tag = VoiceSounds.Tag(configured!.SpeakingEngine(), cue);
                string? problem = null;
                var began = clock.GetTimestamp();
                if (tag is not null)
                {
                    try
                    {
                        var authorization = new ConversationAuthorization(configured, voice: true, microphone: false, clock,
                            () => Configuration?.Revision == configured.Revision, settings.LoadAsync, vault, token);
                        Volatile.Write(ref voiceSoundAuthorization, authorization);
                        var output = new SpeechOutput(configured.SpeechSelection(), configured.SpeechOutput(), LiveConversationConfiguration.SpeechLimits);
                        var pcm = await VoiceSoundRuntime().SynthesizeAsync(output, configured.HostSpeechTarget(), VoiceSounds.Text(tag), 1,
                            authorization, token, configured.ElevenLabsVoiceTarget()).ConfigureAwait(false);
                        var clip = QuickSoundAudio.Prepare(pcm, VoiceSoundLibrary.MaximumLength);
                        if (clip.Length == 0) problem = $"{voice.Words} made no sound for {tag.Text}";
                        else
                        {
                            var kept = dataDirectory is null || VoiceSoundLibrary.Save(dataDirectory, key, cue, clip);
                            string? hear;
                            lock (voiceSoundLock)
                            {
                                if (voiceSoundKey != key) return;
                                voiceSoundClips[cue] = clip;
                                hear = voiceSoundHear == cue ? cue : null;
                                if (hear is not null) voiceSoundHear = null;
                                voiceSoundProblem = kept ? null : "the sounds couldn't be kept on this PC, so they are made again next time";
                            }
                            ErrorLog.Info($"Voice sound: made {cue} ({tag.Text}) with {voice.Words} in " +
                                $"{clock.GetElapsedTime(began).TotalMilliseconds:0} ms ({clip.Length / 48} ms clip).");
                            if (hear is not null) HearVoiceSound(hear);
                        }
                    }
                    catch (OperationCanceledException) when (token.IsCancellationRequested) { return; }
                    catch (Exception error) when (error is not OutOfMemoryException)
                    {
                        problem = $"couldn't make {cue} with {voice.Words} ({(error is LiveActionException live ? live.Code : error.Message.TrimEnd('.'))})";
                    }
                    finally { Volatile.Write(ref voiceSoundAuthorization, null); }
                }
                lock (voiceSoundLock)
                {
                    if (voiceSoundKey != key) return;
                    if (voiceSoundQueue.Count > 0 && voiceSoundQueue.Peek() == cue) voiceSoundQueue.Dequeue();
                    // A sound that failed is tried again only when a touch or a click asks for it again.
                    voiceSoundQueued.Remove(cue);
                    if (problem is not null) voiceSoundProblem = problem;
                }
                if (problem is not null) ErrorLog.Warn($"Voice sound: {problem}.");
                VoiceSoundsChanged?.Invoke();
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        finally
        {
            string? next;
            lock (voiceSoundLock)
            {
                voiceSoundMaking = false;
                next = voiceSoundQueue.Count > 0 && !token.IsCancellationRequested ? voiceSoundKey : null;
                if (next is not null) voiceSoundMaking = true;
            }
            if (next is not null) Task.Run(() => MakeVoiceSoundsAsync(next, token)).Forget();
            VoiceSoundsChanged?.Invoke();
        }
    }

    // The voice sounds' own runtime: the reply's voice destinations, with credentials only for one making's own one-use
    // authorization.
    private ConversationRuntime VoiceSoundRuntime()
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            var credentials = voiceSoundCredentials ??= new(() => Volatile.Read(ref voiceSoundAuthorization));
            return voiceSoundRuntime ??= runtimeFactory?.Invoke(credentials, clock) ??
                ConversationRuntime.Create(credentials, playbackDevices, clock: clock,
                    hostSpeech: dataDirectory is null ? null : new HostSpeechClient(dataDirectory));
        }
    }

    private async Task DisposeVoiceSoundsAsync()
    {
        voiceSoundStop.Cancel();
        ConversationRuntime? owned;
        lock (gate) owned = voiceSoundRuntime;
        if (owned is not null) await owned.DisposeAsync().ConfigureAwait(false);
    }
}
