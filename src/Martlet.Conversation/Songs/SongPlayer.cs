using Martlet.Audio;
using Martlet.Core.Audio;
using Martlet.Core.Contracts;

namespace Martlet.Conversation;

/// <summary>Where a song's playback is: opening the output, the band's lead-in before the line, singing, stopping (the musical
/// or quick fade), stopped by someone, played to its end, or failed on this PC.</summary>
public enum SongPlaybackState { Starting, LeadIn, Singing, Stopping, Stopped, Finished, Failed }

/// <summary>Plays one song through Martlet's voice output: the same output device as speech (its own stream beside it, so
/// Windows mixes them and its volume for Martlet applies to both), rendered by <see cref="SongMixer"/> from the separate backing
/// and vocals. It follows what is heard (the device's padding), so <see cref="Position"/>, <see cref="Line"/>, the captions and
/// the character's mouth match the speakers. While <paramref name="talking"/> says Martlet is speaking, the song is ducked and a
/// lead-in vamps. <see cref="Stop"/> records where it stopped and why at once; the audio then ends musically or quickly.</summary>
public sealed class SongPlayer : IAsyncDisposable
{
    public static readonly PcmFormat Format = new() { SampleRate = 48_000, Channels = 2, Encoding = PcmEncoding.Signed16LittleEndian };
    private readonly object gate = new();
    private readonly IPlaybackDeviceFactory devices;
    private readonly OutputSelection output;
    private readonly Func<bool> talking;
    private readonly SpokenTextFeed? captions;
    private readonly TimeSpan wait;
    private readonly CancellationTokenSource lifetime = new();
    private readonly TaskCompletionSource<SongStopRecord> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly IReadOnlyList<SongWordTime> words;
    private readonly int[][] lineWords;
    private TaskCompletionSource? caption;
    private Task? pump;
    private SongStopRecord? record;
    private SongPlaybackState state = SongPlaybackState.Starting;
    private TimeSpan position;
    private long heardOutput;
    private int? line = -1;
    private int sungWords = -1;
    private string captionText = "";
    private double mouth;
    private IReadOnlyDictionary<string, double> shape = new Dictionary<string, double>();
    private ErrorCode? failure;

    /// <param name="talking">Whether Martlet is saying something right now (a reply or remark), read every 10 ms.</param>
    /// <param name="captions">The line being sung goes here word by word as it is heard (the speech bubble and subtitles).</param>
    /// <param name="mouth">The song's mouth track (made from its vocals when it was made); its vocals' loudness without one.</param>
    /// <param name="words">The sung words with their times, for word-by-word captions; whole lines without them.</param>
    /// <param name="pumpWait">How long the pump waits between device checks (MCP's check runs it faster than real time).</param>
    public SongPlayer(StoredSong song, SongMap map, SongAudio audio, SongStartPlan plan, IPlaybackDeviceFactory devices,
        OutputSelection output, Func<bool> talking, SpokenTextFeed? captions = null, TimeSpan? pumpWait = null,
        SongMouthTrack? mouth = null, IReadOnlyList<SongWordTime>? words = null)
    {
        ArgumentNullException.ThrowIfNull(song);
        ArgumentNullException.ThrowIfNull(devices);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(talking);
        output.Validate();
        Song = song;
        Map = map;
        Envelope = new VocalEnvelope(audio.Vocals, audio.SampleRate);
        Mixer = new SongMixer(audio, map, Envelope, plan);
        MouthTrack = mouth ?? SongMouthTrack.FromLoudness(Envelope, map.Duration);
        this.words = words ?? [];
        // Each line's words: those that start (a moment before) its next line.
        lineWords = new int[map.Lines.Count][];
        for (var l = 0; l < map.Lines.Count; l++)
        {
            var from = map.Lines[l].Start - TimeSpan.FromMilliseconds(250);
            var to = l + 1 < map.Lines.Count ? map.Lines[l + 1].Start - TimeSpan.FromMilliseconds(250) : map.Duration;
            lineWords[l] = [.. Enumerable.Range(0, this.words.Count).Where(w => this.words[w].Start >= from && this.words[w].Start < to)];
        }
        this.devices = devices;
        this.output = output;
        this.talking = talking;
        this.captions = captions;
        wait = pumpWait ?? TimeSpan.FromMilliseconds(10);
    }

    public StoredSong Song { get; }
    public SongMap Map { get; }
    public SongMixer Mixer { get; }
    public VocalEnvelope Envelope { get; }
    public SongMouthTrack MouthTrack { get; }
    public SongStartPlan Plan => Mixer.Plan;
    public SongPlaybackState State { get { lock (gate) return state; } }
    /// <summary>The song time being heard.</summary>
    public TimeSpan Position { get { lock (gate) return position; } }
    /// <summary>How many frames the output has played (the playback clock the mouth is timed against; it never goes back,
    /// even when the band repeats a bar).</summary>
    public long HeardOutput { get { lock (gate) return heardOutput; } }
    /// <summary>The line being heard (0-based), or null in an instrumental stretch.</summary>
    public int? Line { get { lock (gate) return line < 0 ? null : line; } }
    /// <summary>The line being sung, up to the word being heard (the whole line without word times).</summary>
    public string Caption { get { lock (gate) return captionText; } }
    /// <summary>How open the character's mouth is for what is heard (0 to 1): the mouth track at the song time heard, closed while
    /// the vocals are muted (a lead-in) and fading with them.</summary>
    public double Mouth { get { lock (gate) return mouth; } }
    /// <summary>The mouth's shape for what is heard: the mouth track's blendshapes at the song time heard, scaled like
    /// <see cref="Mouth"/>, with the playback clock it belongs to.</summary>
    public (long Output, TimeSpan At, IReadOnlyDictionary<string, double> Shape, double Level) MouthNow
    {
        get { lock (gate) return (heardOutput, position, shape, mouth); }
    }
    public SongStopRecord? Record { get { lock (gate) return record; } }
    public ErrorCode? Failure { get { lock (gate) return failure; } }
    public bool Active => !completion.Task.IsCompleted;
    /// <summary>Completes with where and why the song stopped (or that it ended).</summary>
    public Task<SongStopRecord> Completion => completion.Task;
    /// <summary>Raised on the player's thread when the heard line or state changes.</summary>
    public event Action? Changed;
    /// <summary>Raised on the player's thread each time what is heard moves on: the playback clock, the song time and the mouth's
    /// opening for it.</summary>
    public event Action<long, TimeSpan, double>? Played;

    public void Start()
    {
        lock (gate)
        {
            if (pump is not null) throw new InvalidOperationException("The song already started.");
            pump = Task.Factory.StartNew(Run, lifetime.Token, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        }
    }

    /// <summary>Stops the song (once): records where it is heard now and why, and ends it musically or with a quick fade.
    /// Returns the record, or null when it had already stopped.</summary>
    public SongStopRecord? Stop(SongStopCause cause, bool musical, string? words = null, string? reason = null)
    {
        SongStopRecord? stopped;
        lock (gate)
        {
            if (record is not null || completion.Task.IsCompleted)
            {
                // A quick stop may still hurry a musical one along.
                if (!musical && state == SongPlaybackState.Stopping) Mixer.Stop(position, musical: false);
                return null;
            }
            record = stopped = RecordAt(position, cause, words, reason);
            state = SongPlaybackState.Stopping;
        }
        Mixer.Stop(stopped.At, musical);
        Changed?.Invoke();
        return stopped;
    }

    private SongStopRecord RecordAt(TimeSpan at, SongStopCause cause, string? words = null, string? reason = null)
    {
        var index = Map.LineAt(at);
        var sung = index is { } i ? Map.Lines[i] : null;
        return new(Song.Id, Song.Title, at, Map.Duration, index, sung?.Section is { Length: > 0 } section ? section : null, sung?.Text,
            index is null ? Map.NextLine(at) : null, Map.Lines.Count, cause, words, reason);
    }

    private void Run()
    {
        var token = lifetime.Token;
        IPlaybackDevice? device = null;
        try
        {
            device = devices.Open(output, Format, token);
            var capacity = device.Info.BufferCapacitySamples;
            if (capacity <= 0 || capacity > Format.SampleRate) throw new ContractException(ErrorCode.AudioFormatUnsupported, "The output's buffer is out of bounds.");
            var chunk = Math.Min(capacity, Format.SampleRate / 50);
            var samples = new short[chunk * 2];
            var bytes = new byte[chunk * 4];
            long committed = 0;
            var started = false;
            while (true)
            {
                token.ThrowIfCancellationRequested();
                var padding = device.GetPadding(token);
                Heard(committed - Math.Clamp(padding, 0, committed));
                var speaking = talking();
                Mixer.Hold = speaking;
                Mixer.Ducked = speaking;
                if (Mixer.Finished && padding == 0) break;
                var room = capacity - padding;
                while (room > 0 && !Mixer.Finished)
                {
                    var frames = Mixer.Render(samples, Math.Min(room, chunk));
                    if (frames == 0) break;
                    Buffer.BlockCopy(samples, 0, bytes, 0, frames * 4);
                    var offset = 0;
                    while (offset < frames)
                    {
                        var written = device.Write(bytes.AsSpan(offset * 4, (frames - offset) * 4), token);
                        if (written <= 0) throw new ContractException(ErrorCode.AudioPlaybackFailed, "The output took no audio.");
                        offset += written;
                    }
                    committed += frames;
                    room -= frames;
                    if (!started)
                    {
                        device.Start(token);
                        started = true;
                    }
                }
                if (!started && Mixer.Finished) break;
                if (wait > TimeSpan.Zero) token.WaitHandle.WaitOne(wait);
            }
            Heard(committed);
            Finish(null);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { Finish(null); }
        catch (ContractException error) { Finish(error.Code); }
        catch (Exception error) when (error is InvalidOperationException or IOException or System.Runtime.InteropServices.COMException)
        {
            Finish(ErrorCode.AudioPlaybackFailed);
        }
        finally
        {
            if (device is not null)
            {
                try { device.StopAndReset(); }
                catch (Exception error) when (error is ContractException or InvalidOperationException or System.Runtime.InteropServices.COMException) { }
                finally { device.Dispose(); }
            }
        }
    }

    // What the speakers have played so far: the song time, the line, the state, the captions and the mouth follow it.
    private void Heard(long played)
    {
        var at = Mixer.SourceAt(played);
        int? heard;
        bool changed;
        double level;
        lock (gate)
        {
            heardOutput = played;
            position = at;
            heard = Map.LineAt(at);
            var next = state is SongPlaybackState.Stopping or SongPlaybackState.Stopped or SongPlaybackState.Finished or SongPlaybackState.Failed
                ? state : Mixer.Singing && (Plan.Top || at >= Plan.VocalsFrom) ? SongPlaybackState.Singing : SongPlaybackState.LeadIn;
            // Karaoke: the line so far, word by word (the whole line without word times); nothing while the vocals are muted.
            var sung = -1;
            var text = "";
            if (heard is { } index && next == SongPlaybackState.Singing)
            {
                var times = lineWords[index];
                if (times.Length == 0) text = Map.Lines[index].Text;
                else
                {
                    sung = times.Count(w => words[w].Start <= at + TimeSpan.FromMilliseconds(30));
                    text = string.Join(' ', times.Take(Math.Max(1, sung)).Select(w => words[w].Text));
                }
            }
            changed = heard != line || next != state;
            if (heard != line || sung != sungWords || text != captionText)
            {
                caption?.TrySetResult();
                caption = null;
                if (text.Length > 0 && captions is not null)
                {
                    caption = new(TaskCreationOptions.RunContinuationsAsynchronously);
                    captions.Post(text, caption.Task);
                }
                changed |= text != captionText;
            }
            line = heard ?? -1;
            sungWords = sung;
            captionText = text;
            state = next;
            // The mouth follows the vocals as they are heard: muted vocals (a lead-in, a stop's fade) close it.
            var gain = Math.Clamp(Mixer.VocalGainAt(at), 0, 1);
            var weights = MouthTrack.At(at);
            shape = gain >= 0.999 ? weights : weights.ToDictionary(item => item.Key, item => item.Value * gain, StringComparer.Ordinal);
            mouth = level = MouthTrack.LevelAt(at) * gain;
        }
        Played?.Invoke(played, at, level);
        if (changed) Changed?.Invoke();
    }

    private void Finish(ErrorCode? error)
    {
        SongStopRecord result;
        lock (gate)
        {
            caption?.TrySetResult();
            caption = null;
            mouth = 0;
            failure = error;
            record ??= RecordAt(position, error is not null ? SongStopCause.Failed
                : lifetime.IsCancellationRequested ? SongStopCause.Replaced : SongStopCause.Ended);
            state = error is not null ? SongPlaybackState.Failed : record.Cause == SongStopCause.Ended ? SongPlaybackState.Finished : SongPlaybackState.Stopped;
            result = record;
        }
        completion.TrySetResult(result);
        Changed?.Invoke();
    }

    /// <summary>Stops at once (a quick fade where there's time) and releases the output.</summary>
    public async ValueTask DisposeAsync()
    {
        Stop(SongStopCause.Replaced, musical: false);
        Task? running;
        lock (gate) running = pump;
        if (running is not null)
        {
            await Task.WhenAny(completion.Task, Task.Delay(TimeSpan.FromSeconds(1))).ConfigureAwait(false);
            lifetime.Cancel();
            await running.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }
        else Finish(null);
        lifetime.Dispose();
    }

    public override string ToString() => $"{nameof(SongPlayer)} {Song.Id} ({State})";
}
