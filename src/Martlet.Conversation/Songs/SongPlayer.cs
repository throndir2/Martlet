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
    private TaskCompletionSource? caption;
    private Task? pump;
    private SongStopRecord? record;
    private SongPlaybackState state = SongPlaybackState.Starting;
    private TimeSpan position;
    private int? line = -1;
    private double mouth;
    private ErrorCode? failure;

    /// <param name="talking">Whether Martlet is saying something right now (a reply or remark), read every 10 ms.</param>
    /// <param name="captions">Each sung line goes here as it is heard (the speech bubble and subtitles).</param>
    /// <param name="pumpWait">How long the pump waits between device checks (MCP's check runs it faster than real time).</param>
    public SongPlayer(StoredSong song, SongMap map, SongAudio audio, SongStartPlan plan, IPlaybackDeviceFactory devices,
        OutputSelection output, Func<bool> talking, SpokenTextFeed? captions = null, TimeSpan? pumpWait = null)
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
    public SongStartPlan Plan => Mixer.Plan;
    public SongPlaybackState State { get { lock (gate) return state; } }
    /// <summary>The song time being heard.</summary>
    public TimeSpan Position { get { lock (gate) return position; } }
    /// <summary>The line being heard (0-based), or null in an instrumental stretch.</summary>
    public int? Line { get { lock (gate) return line < 0 ? null : line; } }
    /// <summary>How open the character's mouth is for what is heard (0 to 1).</summary>
    public double Mouth { get { lock (gate) return mouth; } }
    public SongStopRecord? Record { get { lock (gate) return record; } }
    public ErrorCode? Failure { get { lock (gate) return failure; } }
    public bool Active => !completion.Task.IsCompleted;
    /// <summary>Completes with where and why the song stopped (or that it ended).</summary>
    public Task<SongStopRecord> Completion => completion.Task;
    /// <summary>Raised on the player's thread when the heard line or state changes.</summary>
    public event Action? Changed;

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
        lock (gate)
        {
            position = at;
            heard = Map.LineAt(at);
            var next = state is SongPlaybackState.Stopping or SongPlaybackState.Stopped or SongPlaybackState.Finished or SongPlaybackState.Failed
                ? state : Mixer.Singing && (Plan.Top || at >= Plan.VocalsFrom) ? SongPlaybackState.Singing : SongPlaybackState.LeadIn;
            changed = heard != line || next != state;
            if (heard != line)
            {
                caption?.TrySetResult();
                caption = null;
                if (heard is { } index && state != SongPlaybackState.Stopping && captions is not null)
                {
                    caption = new(TaskCreationOptions.RunContinuationsAsynchronously);
                    captions.Post(Map.Lines[index].Text, caption.Task);
                }
            }
            line = heard ?? -1;
            state = next;
            mouth = VocalEnvelope.Mouth(Envelope.At(at) * Mixer.VocalGain * Mixer.CurrentDuck);
        }
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
