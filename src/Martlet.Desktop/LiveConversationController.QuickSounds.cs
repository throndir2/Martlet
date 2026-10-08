using System.IO;
using Martlet.Conversation;
using Martlet.Core.Settings;

namespace Martlet.Desktop;

/// <summary>Where Companion › Voice › Quick sounds while Martlet thinks stands: off, no voice to make them with, being made,
/// ready, waiting for the owner's click (a paid cloud voice) or failed.</summary>
internal enum QuickSoundState { Off, NoVoice, Making, Ready, NeedsClick, Failed }

// Quick sounds while Martlet thinks (docs/VOICE_LATENCY.md): short clips in Martlet's own voice, made once per voice and
// character with the reply's own voice path (ConversationRuntime.SynthesizeAsync) and kept in quick-sounds\ in the data folder,
// and the rules that play one in front of a slow reply (QuickSoundWatcher). A paid cloud voice makes them only on the owner's
// click.
internal sealed partial class LiveConversationController
{
    private QuickSoundGate? quickGate;
    private readonly object quickLock = new();
    private QuickSoundSet? quickSet;
    private QuickSoundState quickState;
    private string? quickProblem, quickKey, quickVoice;
    private CancellationTokenSource? quickMaking;
    private ConversationRuntime? quickRuntime;
    private ConversationCredentialSource? quickCredentials;
    private ConversationAuthorization? quickAuthorization;

    /// <summary>The quick sound rules for this conversation: options, cooldown and which clip comes next.</summary>
    internal QuickSoundGate QuickGate => LazyInitializer.EnsureInitialized(ref quickGate, () => new QuickSoundGate(clock));

    /// <summary>Companion › Voice › Quick sounds while Martlet thinks (off by default). Turning it on makes the clips for the
    /// current voice when they are missing and the voice is free to use (a paired host's).</summary>
    internal QuickSoundOptions QuickSounds
    {
        get => QuickGate.Options;
        set
        {
            QuickGate.Options = value;
            FollowQuickSounds();
        }
    }

    /// <summary>Raised (on a background thread) when the quick sounds' state changes.</summary>
    internal event Action? QuickSoundsChanged;

    /// <summary>Where quick sounds stand now: state, the voice in words, how many clips are ready and why not.</summary>
    internal (QuickSoundState State, string? Voice, int Clips, string? Problem) QuickSoundStatus
    {
        get { lock (quickLock) return (quickState, quickVoice, quickSet?.Clips.Count ?? 0, quickProblem); }
    }

    /// <summary>Makes the quick sounds for the current voice now, also with a paid cloud voice (the owner's click on Companion ›
    /// Voice: one short request per clip). Returns false when quick sounds are off or there is no voice.</summary>
    internal bool MakeQuickSounds()
    {
        var configured = Configuration;
        var voice = QuickSoundVoice(configured);
        if (!QuickGate.Options.Enabled || voice is not { } chosen) return false;
        lock (quickLock)
        {
            quickKey = chosen.Key;
            quickVoice = chosen.Words;
            quickProblem = null;
            quickState = QuickSoundState.Making;
        }
        QuickSoundsChanged?.Invoke();
        StartMakingQuickSounds(configured!, chosen.Key, chosen.Words);
        return true;
    }

    // The voice replies speak with now (identity key and words) and whether it is a paid cloud voice; null without one.
    private static (string Key, string Words, bool Paid)? QuickSoundVoice(LiveConversationConfiguration? configured)
    {
        if (configured is null || configured.Unavailable(voice: true, microphone: false) is not null ||
            QuickSoundLibrary.Voice(configured.Routes.FirstOrDefault(r => r.Role == SetupRole.Tts && r.Enabled == true)) is not { } voice)
            return null;
        return (QuickSoundLibrary.Key(voice.Identity, QuickSoundLibrary.Character(configured.Persona?.Id)), voice.Words, voice.Paid);
    }

    // Follows the setting and the voice: the clips kept for the voice and character are read, or made when missing and the voice
    // is free to use; a paid cloud voice waits for the owner's click.
    private void FollowQuickSounds()
    {
        var configured = Configuration;
        var voice = QuickGate.Options.Enabled ? QuickSoundVoice(configured) : null;
        var make = false;
        lock (quickLock)
        {
            var was = (quickState, quickKey);
            if (!QuickGate.Options.Enabled || voice is null)
            {
                Interlocked.Exchange(ref quickMaking, null)?.Cancel();
                quickSet = null;
                quickKey = null;
                quickVoice = null;
                quickProblem = null;
                quickState = QuickGate.Options.Enabled ? QuickSoundState.NoVoice : QuickSoundState.Off;
                if (was == (quickState, quickKey)) return;
            }
            else
            {
                var (key, words, paid) = voice.Value;
                if (key == quickKey && quickState is QuickSoundState.Ready or QuickSoundState.Making or QuickSoundState.Failed) return;
                Interlocked.Exchange(ref quickMaking, null)?.Cancel();
                quickKey = key;
                quickVoice = words;
                quickProblem = null;
                quickSet = dataDirectory is null ? null : QuickSoundLibrary.Load(dataDirectory, key);
                quickState = quickSet is not null ? QuickSoundState.Ready : paid ? QuickSoundState.NeedsClick : QuickSoundState.Making;
                make = quickState == QuickSoundState.Making;
            }
        }
        QuickSoundsChanged?.Invoke();
        if (make) StartMakingQuickSounds(configured!, voice!.Value.Key, voice.Value.Words);
    }

    private void StartMakingQuickSounds(LiveConversationConfiguration configured, string key, string words)
    {
        var stop = new CancellationTokenSource();
        Interlocked.Exchange(ref quickMaking, stop)?.Cancel();
        Task.Run(() => MakeQuickSoundsAsync(configured, key, words, stop.Token)).Forget();
    }

    // Says each quick sound once with the reply's own voice, never beside a reply (the voice is the reply's first), and keeps
    // the clips for this voice and character.
    private async Task MakeQuickSoundsAsync(LiveConversationConfiguration configured, string key, string words, CancellationToken token)
    {
        var began = clock.GetTimestamp();
        var phrases = QuickSoundPhrases.For(configured.SpeakingEngine());
        try
        {
            var authorization = new ConversationAuthorization(configured, voice: true, microphone: false, clock,
                () => Configuration?.Revision == configured.Revision, settings.LoadAsync, vault, token);
            Volatile.Write(ref quickAuthorization, authorization);
            var output = new SpeechOutput(configured.SpeechSelection(), configured.SpeechOutput(), LiveConversationConfiguration.SpeechLimits);
            List<QuickSoundClip> clips = [];
            var segment = 0;
            foreach (var phrase in phrases)
            {
                while (Replying) await Task.Delay(TimeSpan.FromMilliseconds(200), clock, token).ConfigureAwait(false);
                var pcm = await QuickRuntime().SynthesizeAsync(output, configured.HostSpeechTarget(),
                    phrase, ++segment, authorization, token, configured.ElevenLabsVoiceTarget()).ConfigureAwait(false);
                if (QuickSoundAudio.Prepare(pcm) is { Length: > 0 } clip) clips.Add(new(phrase, clip));
            }
            if (clips.Count == 0) throw new InvalidOperationException("the voice said nothing that could be kept");
            var set = new QuickSoundSet(key, words, clock.GetUtcNow(), clips);
            // Kept in the data folder for the next start; a controller without one (tests) keeps them in memory only.
            var kept = dataDirectory is null || QuickSoundLibrary.Save(dataDirectory, set);
            lock (quickLock)
            {
                if (quickKey != key) return;
                quickSet = set;
                quickState = QuickSoundState.Ready;
                quickProblem = kept ? null : "they couldn't be kept on this PC, so they are made again next time";
            }
            ErrorLog.Info($"Quick sounds: made {clips.Count} with {words} in {clock.GetElapsedTime(began).TotalMilliseconds:0} ms " +
                $"({string.Join(" ", clips.Select(c => $"\"{c.Text}\" {c.Duration.TotalMilliseconds:0} ms"))}).");
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            var why = error is LiveActionException live ? live.Code : error.Message.TrimEnd('.');
            lock (quickLock)
            {
                if (quickKey != key) return;
                quickState = QuickSoundState.Failed;
                quickProblem = why;
            }
            ErrorLog.Warn($"Quick sounds: couldn't make them with {words} ({why}).");
        }
        finally
        {
            Volatile.Write(ref quickAuthorization, null);
            QuickSoundsChanged?.Invoke();
        }
    }

    // The quick sounds' own runtime: the reply's voice destinations, with credentials only for the making's own one-use
    // authorization.
    private ConversationRuntime QuickRuntime()
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            var credentials = quickCredentials ??= new(() => Volatile.Read(ref quickAuthorization));
            return quickRuntime ??= runtimeFactory?.Invoke(credentials, clock) ??
                ConversationRuntime.Create(credentials, playbackDevices, clock: clock,
                    hostSpeech: dataDirectory is null ? null : new HostSpeechClient(dataDirectory));
        }
    }

    private async Task DisposeQuickSoundsAsync()
    {
        Interlocked.Exchange(ref quickMaking, null)?.Cancel();
        ConversationRuntime? owned;
        lock (gate) owned = quickRuntime;
        if (owned is not null) await owned.DisposeAsync().ConfigureAwait(false);
    }

    // A spoken reply to the user gets a quick sound when it is slow to start (QuickSoundWatcher): counted from the moment it is
    // confirmed (at once, or when a reply started early is taken), never while it is held, never for a song.
    private void QuickSoundWhenSlow(LiveConversationOperation operation, ConversationTurn turn)
    {
        if (!QuickGate.Options.Enabled || !turn.Spoken || operation.FloorReply is null || operation.Playback == PlaybackMode.Song) return;
        IReadOnlyList<QuickSoundClip> clips;
        lock (quickLock) clips = quickState == QuickSoundState.Ready && quickSet is { } set ? set.Clips : [];
        if (clips.Count == 0) return;
        var configured = operation.Authorization.Configuration;
        bool refused;
        lock (gate) refused = reasoningRefused.Contains(configured.ToolModelKey());
        var thinking = configured.Routes.SingleOrDefault(r => r.Role == SetupRole.Llm);
        var reasoning = refused || GenerationSettings.ThinkingSteps(configured.Generation) && thinking is not null &&
            GenerationSupport.Use(thinking.RouteType, thinking.Origin, GenerationSetting.Reasoning) != GenerationSettingUse.Unused;
        Task.Run(async () =>
        {
            var outcome = await QuickSoundWatcher.WatchAsync(turn, QuickGate, clips, () => !turn.Held, reasoning, clock).ConfigureAwait(false);
            if (outcome.Played)
                ErrorLog.Info($"Quick sound: \"{outcome.Clip}\" {outcome.AfterConfirmed?.TotalMilliseconds:0} ms after the reply was " +
                    $"confirmed ({outcome.Why}).");
        }).Forget();
    }
}
