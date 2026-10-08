using System.IO;
using System.Text.Json;
using Martlet.Audio;
using Martlet.Conversation;
using Martlet.Core.Singing;

namespace Martlet.Desktop;

/// <summary>What a new song is made with: the song maker, the voice (a <c>SpeakingVoice.Id</c>) and Companion › Voice ›
/// Singing's choices, and where it is made (for the log).</summary>
internal sealed record SongSetup(ISongMaker Maker, string VoiceId, SongQuality Quality, SongVoiceMatch VoiceMatch, string Where);

/// <summary>Where songs come from: whether singing is set up (the song tools are offered only then, so this changes only when
/// the owner changes the setup) and, for a new song, the maker and choices or why there are none.</summary>
internal interface ISongSource
{
    bool SetUp { get; }
    (SongSetup? Setup, string? Problem) Current();
}

/// <summary>Where songs come from on this PC: Companion › Singing's computer through <see cref="SongClient"/> (or the
/// FIXTURE - NOT AI song maker when <c>MARTLET_SINGING_FIXTURE=1</c>, for automated checks), sung in the voice Martlet speaks
/// with and the card's quality and voice match (singing.json). Whether singing is set up is read without the network
/// (<see cref="SongClient.IsSetUp"/>) and kept for a few seconds, so asking for every reply costs nothing. In fixture mode songs
/// also play into <see cref="SilentSongOutput"/>, so automated checks never sound.</summary>
internal sealed class DesktopSongSource(string dataDirectory) : ISongSource
{
    private sealed record Known(bool SetUp, long At);
    private Known? cached;

    internal static bool Fixture => Environment.GetEnvironmentVariable(SongClient.FixtureVariable) == "1";

    public bool SetUp
    {
        get
        {
            var now = System.Diagnostics.Stopwatch.GetTimestamp();
            if (Volatile.Read(ref cached) is { } known && System.Diagnostics.Stopwatch.GetElapsedTime(known.At, now) < TimeSpan.FromSeconds(5))
                return known.SetUp;
            var setUp = SongClient.IsSetUp(dataDirectory);
            Volatile.Write(ref cached, new Known(setUp, now));
            return setUp;
        }
    }

    public (SongSetup? Setup, string? Problem) Current()
    {
        if (!SongClient.IsSetUp(dataDirectory)) return (null, "singing isn't set up on any of your computers (Companion › Singing).");
        var voice = SongClient.SpeakingVoiceId(dataDirectory) ?? (Fixture ? "fixture-voice" : null);
        if (voice is null) return (null, "there's no voice to sing with yet (Companion › Voice).");
        var choices = SingingPreferences.Load(dataDirectory);
        return (new SongSetup(SongClient.For(dataDirectory), voice, choices.Quality, choices.VoiceMatch,
            Fixture ? "this PC (FIXTURE - NOT AI)" : choices.Host ?? "the singing computer"), null);
    }

    public override string ToString() => nameof(DesktopSongSource);
}

/// <summary>FIXTURE: an output that takes a song's audio at real-time pace and plays nothing, for automated checks of the
/// desktop's singing (MARTLET_SINGING_FIXTURE=1); positions, captions and stops behave as on real speakers.</summary>
internal sealed class SilentSongOutput : IPlaybackDeviceFactory
{
    public IPlaybackDevice Open(OutputSelection selection, Martlet.Core.Audio.PcmFormat format, CancellationToken cancellationToken) =>
        new Device(format.SampleRate);

    private sealed class Device(int rate) : IPlaybackDevice
    {
        private long written;
        private System.Diagnostics.Stopwatch? clock;
        public PlaybackDeviceInfo Info { get; } = new(rate, 2, 16, DeviceSampleEncoding.IntegerPcm, rate / 20, false);
        private long Consumed => clock is null ? 0 : Math.Min(written, (long)(clock.Elapsed.TotalSeconds * rate));
        public int GetPadding(CancellationToken cancellationToken) => (int)(written - Consumed);
        public int Write(ReadOnlySpan<byte> pcm, CancellationToken cancellationToken)
        {
            var frames = Math.Clamp(pcm.Length / 4, 0, Info.BufferCapacitySamples - GetPadding(cancellationToken));
            written += frames;
            return frames;
        }
        public void Start(CancellationToken cancellationToken) => clock = System.Diagnostics.Stopwatch.StartNew();
        public void StopAndReset() { }
        public void Dispose() { }
    }
}

/// <summary>Where a song's mouth goes: the character (its mouth mapping, or its loudness mouth). <see cref="SingAsync"/> runs on
/// the song's playback clock; <see cref="RestAsync"/> closes the mouth when it stops.</summary>
internal interface ISongFace
{
    Task SingAsync(long output, IReadOnlyDictionary<string, double> shape, double level, CancellationToken token);
    Task RestAsync(CancellationToken token);
    /// <summary>How the mouth reached the character last ("mapped mouth shapes", "mouth opening"), or null.</summary>
    string? Route { get; }
}

/// <summary>Runs a song's vocals once through Audio2Face for its mouth track: the blendshape frames in song time and where
/// they were made, or null when none is reachable.</summary>
internal delegate Task<(IReadOnlyList<(TimeSpan At, IReadOnlyDictionary<string, double> Weights)> Faces, string Where)?> SongFaceAnalysis(
    short[] vocals, int sampleRate, CancellationToken token);

/// <summary>Martlet singing in the conversation: the song library in the data directory, the song playing (one at a time,
/// through Martlet's voice output), where the last one stopped and why, and the note about it that waits for the conversation.
/// Writes songs-status.json (states, times, line numbers and sections; never titles or words) for MCP's songs_status.</summary>
internal sealed class ConversationSinging : IAsyncDisposable
{
    internal const string StatusFile = "songs-status.json";
    private readonly object gate = new();
    private readonly string? dataDirectory;
    private readonly IPlaybackDeviceFactory? devices;
    private readonly SpokenTextFeed? captions;
    private readonly ISongFace? face;
    private readonly SongFaceAnalysis? analysis;
    private readonly TimeProvider clock;
    private SongPlayer? player;
    private SongStopRecord? last;
    private string? pendingNote;
    private CancellationTokenSource? mouthLoop;
    private int statusPending;
    private readonly object statusGate = new();

    internal ConversationSinging(string? dataDirectory, ISongSource? source, IPlaybackDeviceFactory? devices, SpokenTextFeed? captions = null,
        ISongFace? face = null, SongFaceAnalysis? analysis = null, TimeProvider? clock = null)
    {
        this.dataDirectory = dataDirectory;
        Source = source;
        this.devices = devices;
        this.captions = captions;
        this.face = face;
        this.analysis = analysis;
        this.clock = clock ?? TimeProvider.System;
    }

    /// <summary>The data directory songs are kept in (as creations), or null where they can't be.</summary>
    internal string? DataDirectory => dataDirectory;

    private double voiceVolume = PcmGain.Full;
    /// <summary>Companion › Voice › Voice volume (0 to 1): songs play at it, and one playing now follows a change at once.</summary>
    internal double VoiceVolume
    {
        get => Volatile.Read(ref voiceVolume);
        set => Volatile.Write(ref voiceVolume, PcmGain.Clamp(value));
    }

    /// <summary>The mouth track and sung words for a song just made, from its vocals stem (never the mix): Audio2Face run once
    /// over the vocals when one is reachable, otherwise visemes timed from the sung words (the song maker's, or the words of each
    /// line spread over its singing), otherwise the vocals' loudness; and how well it follows the vocal onsets.</summary>
    internal async Task<(SongMouthTrack Mouth, IReadOnlyList<SongWordTime> Words, bool Estimated, SongMouthTiming Timing)> MouthAsync(
        SongResult result, CancellationToken token)
    {
        var vocals = SongAudio.Mono(result.Vocals);
        var envelope = new VocalEnvelope(vocals, result.Vocals.SampleRate);
        var map = SongMap.Of(result);
        IReadOnlyList<SongWordTime> given = [.. result.Words.Select(word => new SongWordTime(word.Start, word.End, word.Text))];
        var estimated = given.Count == 0;
        var words = estimated ? SongMouthTrack.Spread(map, envelope) : given;
        SongMouthTrack? track = null;
        if (analysis is not null && await analysis(vocals, result.Vocals.SampleRate, token).ConfigureAwait(false) is { Faces.Count: > 0 } faces)
            track = SongMouthTrack.FromFrames(faces.Faces, map.Duration, faces.Where);
        track ??= map.Lines.Count > 0 ? SongMouthTrack.FromVisemes(map, envelope, words, estimated) : SongMouthTrack.FromLoudness(envelope, map.Duration);
        return (track, words, estimated, track.Measure(envelope));
    }

    internal ISongSource? Source { get; }
    /// <summary>Raised on any thread when a song starts, its heard line changes or it stops.</summary>
    internal event Action? Changed;

    /// <summary>Singing is set up and songs can be kept: the song tools are offered.</summary>
    internal bool Offered => Source?.SetUp == true && dataDirectory is not null;

    internal SongPlayer? Player { get { lock (gate) return player; } }
    /// <summary>The song shown in the talk window: the one playing or last played, or the newest made since Martlet started.</summary>
    internal StoredSong? Current { get { lock (gate) return current; } }
    private StoredSong? current;

    /// <summary>A song job finished and kept <paramref name="song"/>: the talk window shows it until Martlet sings it.</summary>
    internal void Made(StoredSong song)
    {
        lock (gate) if (player is not { Active: true }) current = song;
        OnChanged();
    }
    /// <summary>A song is playing (or still ending).</summary>
    internal bool Playing { get { lock (gate) return player is { Active: true }; } }
    internal SongStopRecord? Last { get { lock (gate) return last; } }

    /// <summary>Plays <paramref name="song"/> (with its <paramref name="audio"/> and <paramref name="mouth"/> track) from
    /// <paramref name="from"/> through <paramref name="output"/>: a song already playing stops at once. <paramref name="talking"/>
    /// says when Martlet speaks (the song ducks, and a lead-in vamps).</summary>
    internal (SongPlayer? Player, string? Problem) Play(StoredSong song, SongAudio audio, SongMouthTrack? mouth, string? from, OutputSelection output,
        Func<bool> talking)
    {
        if (devices is null) return (null, "Martlet can't play sound here.");
        var map = song.Map();
        SongStopRecord? previous;
        lock (gate) previous = last?.SongId == song.Id ? last : null;
        var (target, problem) = SongTransport.Resolve(map, from, previous);
        if (target is null) return (null, problem);
        var plan = SongTransport.PlanStart(map, target);
        SongPlayer? stopping;
        var started = new SongPlayer(song, map, audio, plan, devices, output, talking, captions, mouth: mouth, words: song.WordTimes(),
            volume: () => VoiceVolume);
        lock (gate)
        {
            stopping = player;
            player = started;
            current = song;
        }
        if (stopping is not null) StopPlayer(stopping);
        started.Changed += OnChanged;
        _ = WatchAsync(started);
        started.Start();
        StartMouth(started);
        ErrorLog.Info($"Singing: {song.Id} started {plan.Target.Describe}" + (plan.Top ? "." :
            $" with {plan.LeadInBars} bar(s) of lead-in ({plan.LeadIn.TotalSeconds:0.00} s, fade-in {plan.FadeIn.TotalMilliseconds:0} ms)."));
        OnChanged();
        return (started, null);
    }

    // A song replaced by another stops at once; its record isn't news.
    private static void StopPlayer(SongPlayer stopping) => _ = stopping.DisposeAsync().AsTask();

    /// <summary>Stops the song playing: records where it is and why (the note for the conversation waits unless Martlet
    /// stopped it itself, since its tool result says so). Null when nothing was playing.</summary>
    internal SongStopRecord? Stop(SongStopCause cause, bool musical, string? words = null, string? reason = null)
    {
        SongPlayer? current;
        lock (gate) current = player is { Active: true } ? player : null;
        var record = current?.Stop(cause, musical, words, reason);
        if (record is null) return null;
        lock (gate)
        {
            last = record;
            pendingNote = cause == SongStopCause.Martlet ? null : record.Note();
        }
        var plan = current!.Mixer.Stopping;
        ErrorLog.Info($"Singing: {record.SongId} stopped at {SongClock.Of(record.At)} ({cause}" +
            (plan is null ? ")." : $", {(plan.Musical ? "musical" : "quick")} stop, silent {plan.AfterRequest.TotalSeconds:0.00} s after the request)."));
        OnChanged();
        return record;
    }

    /// <summary>What the user said once they paused, after a quick check of their words stopped the song: the note quotes all of
    /// it.</summary>
    internal void Heard(string words)
    {
        lock (gate)
        {
            if (last is not { Cause: SongStopCause.UserWords } record || clock.GetUtcNow() - Stamp > TimeSpan.FromSeconds(15)) return;
            last = record with { Words = words.Trim() };
            pendingNote = last.Note();
        }
    }

    private DateTimeOffset Stamp { get; set; }

    /// <summary>The note about where the last song stopped (or that it ended), waiting for the end of the conversation.</summary>
    internal string? PendingNote { get { lock (gate) return pendingNote; } }

    /// <summary>The note went into the conversation (its exchange was kept).</summary>
    internal void NoteDelivered(string note)
    {
        lock (gate) if (pendingNote == note) pendingNote = null;
    }

    /// <summary>Where the song playing is, for what was heard while it played: its title and position, or null.</summary>
    internal (string Title, string Where)? Now()
    {
        lock (gate)
            return player is { Active: true, State: not (SongPlaybackState.Stopping) } current
                ? (current.Song.Title, current.Map.Describe(current.Position)) : null;
    }

    private async Task WatchAsync(SongPlayer watched)
    {
        var record = await watched.Completion.ConfigureAwait(false);
        lock (gate)
        {
            // Played to its end or failed: nobody stopped it, so the conversation hears of it now (a stop was noted already).
            if (record.Cause is SongStopCause.Ended or SongStopCause.Failed && ReferenceEquals(player, watched))
            {
                last = record;
                pendingNote = record.Note();
            }
        }
        if (record.Cause == SongStopCause.Failed)
            ErrorLog.Warn($"Singing: {record.SongId} couldn't play on this PC ({watched.Failure}).");
        else if (record.Cause == SongStopCause.Ended) ErrorLog.Info($"Singing: {record.SongId} played to its end.");
        OnChanged();
    }

    private void OnChanged()
    {
        lock (gate) Stamp = clock.GetUtcNow();
        WriteStatus();
        Changed?.Invoke();
    }

    // The character's mouth follows the song's mouth track at the song time being heard, on its playback clock (every 20 ms).
    private void StartMouth(SongPlayer playing)
    {
        if (face is null) return;
        CancellationTokenSource loop;
        lock (gate)
        {
            mouthLoop?.Cancel();
            mouthLoop = loop = new();
        }
        _ = Task.Run(async () =>
        {
            long sent = -1;
            var moved = false;
            try
            {
                while (!loop.IsCancellationRequested && playing.Active)
                {
                    var started = System.Diagnostics.Stopwatch.GetTimestamp();
                    var (output, _, shape, level) = playing.MouthNow;
                    // While Martlet talks over the song, its speech moves the mouth instead.
                    if (!playing.Mixer.Ducked && output > sent && (level > 0 || moved))
                    {
                        await face.SingAsync(output, shape, level, loop.Token).ConfigureAwait(false);
                        sent = output;
                        moved = level > 0;
                        Interlocked.Increment(ref mouthFrames);
                        Interlocked.Add(ref mouthTicks, System.Diagnostics.Stopwatch.GetTimestamp() - started);
                    }
                    var spent = System.Diagnostics.Stopwatch.GetElapsedTime(started);
                    await Task.Delay(spent < TimeSpan.FromMilliseconds(15) ? TimeSpan.FromMilliseconds(20) - spent : TimeSpan.FromMilliseconds(5),
                        loop.Token).ConfigureAwait(false);
                }
            }
            catch (Exception error) when (error is OperationCanceledException || RendererFailures.Is(error, loop.Token))
            {
                if (RendererFailures.Is(error, loop.Token)) RendererFailures.Log("Singing: the character's mouth stopped following the song", error);
            }
            if (!loop.IsCancellationRequested)
                try { await face.RestAsync(CancellationToken.None).ConfigureAwait(false); }
                catch (Exception error) when (RendererFailures.Is(error, CancellationToken.None)) { }
        });
    }

    private long mouthFrames, mouthTicks;

    /// <summary>songs-status.json: the library's size, the song playing (state, position, line number and section, lead-in, vamps,
    /// ducking) and where the last one stopped and why; never a title or words.</summary>
    private void WriteStatus()
    {
        if (dataDirectory is null || Interlocked.Exchange(ref statusPending, 1) != 0) return;
        Task.Run(() =>
        {
            Interlocked.Exchange(ref statusPending, 0);
            var status = Status();
            lock (statusGate)
            {
                try
                {
                    var path = Path.Combine(dataDirectory, StatusFile);
                    File.WriteAllText(path + ".tmp", status);
                    File.Move(path + ".tmp", path, overwrite: true);
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
            }
        });
    }

    internal string Status()
    {
        SongPlayer? current;
        SongStopRecord? stopped;
        bool note;
        lock (gate)
        {
            current = player;
            stopped = last;
            note = pendingNote is not null;
        }
        object? Playing(SongPlayer p)
        {
            var plan = p.Plan;
            var stopping = p.Mixer.Stopping;
            return new
            {
                songId = p.Song.Id, state = p.State.ToString(), positionSeconds = Math.Round(p.Position.TotalSeconds, 2),
                durationSeconds = Math.Round(p.Map.Duration.TotalSeconds, 2), line = p.Line + 1, lines = p.Map.Lines.Count,
                section = p.Line is { } index ? p.Map.Lines[index].Section : null, from = Describe(plan),
                leadInBars = plan.LeadInBars, leadInSeconds = Math.Round(plan.LeadIn.TotalSeconds, 2),
                fadeInMs = Math.Round(plan.FadeIn.TotalMilliseconds), vamps = p.Mixer.Vamps, ducked = p.Mixer.Ducked,
                lipSync = new
                {
                    source = p.MouthTrack.Source.ToString(), frames = p.MouthTrack.Frames, channels = p.MouthTrack.Channels.Count,
                    sent = Interlocked.Read(ref mouthFrames), route = face?.Route,
                    averageSendMs = Interlocked.Read(ref mouthFrames) == 0 ? (double?)null : Math.Round(
                        Interlocked.Read(ref mouthTicks) * 1000.0 / System.Diagnostics.Stopwatch.Frequency / Interlocked.Read(ref mouthFrames), 1)
                },
                stop = stopping is null ? null : new
                {
                    musical = stopping.Musical, requestedSeconds = Math.Round(stopping.Requested.TotalSeconds, 2),
                    vocalsEndSeconds = Math.Round(stopping.VocalsEnd.TotalSeconds, 2), vocalsFadeMs = Math.Round(stopping.VocalsFade.TotalMilliseconds),
                    backingFromSeconds = Math.Round(stopping.BackingFrom.TotalSeconds, 2), backingFadeMs = Math.Round(stopping.BackingFade.TotalMilliseconds),
                    silentAfterSeconds = Math.Round(stopping.AfterRequest.TotalSeconds, 2)
                },
                failure = p.Failure?.ToString()
            };
        }
        return JsonSerializer.Serialize(new
        {
            updatedAt = clock.GetUtcNow(), offered = Offered, songs = dataDirectory is null ? 0 : SongCreations.List(dataDirectory).Count,
            output = devices is SilentSongOutput ? "silent fixture output (MARTLET_SINGING_FIXTURE)" : devices is null ? "none" : "Martlet's voice output",
            playing = current is null ? null : Playing(current),
            lastStop = stopped is null ? null : new
            {
                songId = stopped.SongId, atSeconds = Math.Round(stopped.At.TotalSeconds, 2), line = stopped.Line + 1, lines = stopped.Lines,
                section = stopped.Section, nextLine = stopped.NextLine + 1, cause = stopped.Cause.ToString(), ended = stopped.Ended,
                wordsCharacters = stopped.Words?.Length, reason = stopped.Cause == SongStopCause.Button ? stopped.Reason : null
            },
            noteWaiting = note
        });
    }

    // How a start is described in status (target kind and line number, never words).
    private static string Describe(SongStartPlan plan) => plan.Top ? "start"
        : plan.Target.Line is { } line ? $"line {line + 1}" : $"{plan.Target.Time?.TotalSeconds:0.0} s";

    public async ValueTask DisposeAsync()
    {
        SongPlayer? current;
        lock (gate)
        {
            current = player;
            player = null;
            mouthLoop?.Cancel();
        }
        if (current is not null) await current.DisposeAsync().ConfigureAwait(false);
    }

    public override string ToString() => nameof(ConversationSinging);
}
