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

/// <summary>Where songs come from on this PC. Until Companion › Voice › Singing's client is part of this build, only the
/// FIXTURE - NOT AI song maker is available (when <see cref="FixtureVariable"/> is 1 before Martlet starts, for automated
/// checks); otherwise singing isn't set up. In fixture mode songs also play into <see cref="SilentSongOutput"/>, so automated
/// checks never sound.</summary>
internal sealed class DesktopSongSource : ISongSource
{
    internal const string FixtureVariable = "MARTLET_SINGING_FIXTURE";

    internal static bool Fixture => Environment.GetEnvironmentVariable(FixtureVariable) == "1";

    public bool SetUp => Fixture;

    public (SongSetup? Setup, string? Problem) Current() => SetUp
        ? (new SongSetup(new FixtureSongMaker(TimeSpan.FromMilliseconds(400)), "fixture-voice", SongQuality.Fast, SongVoiceMatch.SoulX,
            "this PC (FIXTURE - NOT AI)"), null)
        : (null, "singing isn't set up on any of your computers (Companion › Voice › Singing).");

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
    private readonly Func<double, CancellationToken, Task>? mouth;
    private readonly TimeProvider clock;
    private SongPlayer? player;
    private SongStopRecord? last;
    private string? pendingNote;
    private CancellationTokenSource? mouthLoop;
    private int statusPending;
    private readonly object statusGate = new();

    internal ConversationSinging(string? dataDirectory, ISongSource? source, IPlaybackDeviceFactory? devices, SpokenTextFeed? captions = null,
        Func<double, CancellationToken, Task>? mouth = null, TimeProvider? clock = null)
    {
        this.dataDirectory = dataDirectory;
        Source = source;
        this.devices = devices;
        this.captions = captions;
        this.mouth = mouth;
        this.clock = clock ?? TimeProvider.System;
        Library = dataDirectory is null ? null : SongLibrary.In(dataDirectory);
    }

    internal ISongSource? Source { get; }
    internal SongLibrary? Library { get; }
    /// <summary>Raised on any thread when a song starts, its heard line changes or it stops.</summary>
    internal event Action? Changed;

    /// <summary>Singing is set up and songs can be kept: the song tools are offered.</summary>
    internal bool Offered => Source?.SetUp == true && Library is not null;

    internal SongPlayer? Player { get { lock (gate) return player; } }
    /// <summary>The song shown in the talk window: the one playing or last played, or the newest made since Martlet started.</summary>
    internal StoredSong? Current { get { lock (gate) return current; } }
    private StoredSong? current;

    /// <summary>A song job finished and kept <paramref name="song"/>: it's the one the talk window offers to play.</summary>
    internal void Made(StoredSong song)
    {
        lock (gate) if (player is not { Active: true }) current = song;
        OnChanged();
    }
    /// <summary>A song is playing (or still ending).</summary>
    internal bool Playing { get { lock (gate) return player is { Active: true }; } }
    internal SongStopRecord? Last { get { lock (gate) return last; } }

    /// <summary>Plays <paramref name="song"/> from <paramref name="from"/> through <paramref name="output"/>: a song already playing
    /// stops at once. <paramref name="talking"/> says when Martlet speaks (the song ducks, and a lead-in vamps).</summary>
    internal (SongPlayer? Player, string? Problem) Play(StoredSong song, string? from, OutputSelection output, Func<bool> talking)
    {
        if (devices is null || Library is null) return (null, "Martlet can't play sound here.");
        var audio = Library.LoadAudio(song);
        if (audio is null) return (null, $"The song {song.Id} couldn't be read on this PC.");
        var map = song.Map();
        SongStopRecord? previous;
        lock (gate) previous = last?.SongId == song.Id ? last : null;
        var (target, problem) = SongTransport.Resolve(map, from, previous);
        if (target is null) return (null, problem);
        var plan = SongTransport.PlanStart(map, target);
        SongPlayer? stopping;
        var started = new SongPlayer(song, map, audio, plan, devices, output, talking, captions);
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
        ErrorLog.Info($"Singing: {record.SongId} stopped at {SongLibrary.Clock(record.At)} ({cause}" +
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

    // The character's mouth follows the vocals that are heard while the song plays.
    private void StartMouth(SongPlayer playing)
    {
        if (mouth is null) return;
        CancellationTokenSource loop;
        lock (gate)
        {
            mouthLoop?.Cancel();
            mouthLoop = loop = new();
        }
        _ = Task.Run(async () =>
        {
            double sent = 0;
            try
            {
                while (!loop.IsCancellationRequested && playing.Active)
                {
                    // While Martlet talks over the song, its speech moves the mouth instead.
                    if (!playing.Mixer.Ducked)
                    {
                        var level = Math.Round(playing.Mouth, 3);
                        if (Math.Abs(level - sent) > 0.02 || level == 0 && sent != 0)
                        {
                            await mouth(level, loop.Token).ConfigureAwait(false);
                            sent = level;
                        }
                    }
                    await Task.Delay(TimeSpan.FromMilliseconds(33), loop.Token).ConfigureAwait(false);
                }
            }
            catch (Exception error) when (error is OperationCanceledException or IOException or InvalidOperationException or TimeoutException) { }
            if (sent != 0 && !loop.IsCancellationRequested)
                try { await mouth(0, CancellationToken.None).ConfigureAwait(false); }
                catch (Exception error) when (error is OperationCanceledException or IOException or InvalidOperationException or TimeoutException) { }
        });
    }

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
            updatedAt = clock.GetUtcNow(), offered = Offered, songs = Library?.List().Count ?? 0,
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
