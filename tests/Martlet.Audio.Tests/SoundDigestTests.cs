using System.Buffers.Binary;
using Martlet.Core.Contracts;

namespace Martlet.Audio.Tests;

public sealed class SoundDigestTests
{
    private static byte[] Tone(double seconds, double amplitude = 0.3, double frequency = 440)
    {
        var count = (int)(seconds * PcSoundBuffer.SampleRate);
        var pcm = new byte[count * 2];
        for (var i = 0; i < count; i++)
            BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan(i * 2),
                (short)Math.Round(amplitude * 32767 * Math.Sin(2 * Math.PI * frequency * i / PcSoundBuffer.SampleRate)));
        return pcm;
    }

    // Appends one second at a time, moving the clock along with it, like the PC capture does.
    private static void Play(PcSoundBuffer buffer, ManualTime time, double seconds, double amplitude = 0.3)
    {
        for (var s = 0; s < seconds; s++)
        {
            time.Advance(TimeSpan.FromSeconds(1));
            buffer.Append(Tone(1, amplitude));
        }
    }

    [Fact]
    public void Buffer_keeps_sound_only_while_recording_and_forgets_it_when_turned_off()
    {
        var time = new ManualTime();
        var buffer = new PcSoundBuffer(TimeSpan.FromSeconds(5), time);
        buffer.Append(Tone(1));
        Assert.Equal(TimeSpan.Zero, buffer.Buffered);
        Assert.Equal(0, buffer.LastAppendedAt);
        buffer.Recording = true;
        Play(buffer, time, 7);
        Assert.Equal(TimeSpan.FromSeconds(5), buffer.Buffered);
        Assert.Equal(time.GetTimestamp(), buffer.LastAppendedAt);
        Assert.Equal(5 * PcSoundBuffer.SampleRate, buffer.Latest(TimeSpan.FromSeconds(30)).Length);
        buffer.Recording = false;
        Assert.Equal(TimeSpan.Zero, buffer.Buffered);
        Assert.Empty(buffer.Latest(TimeSpan.FromSeconds(5)));
        Assert.Equal(0, buffer.LastAppendedAt);
    }

    [Fact]
    public void Latest_takes_the_newest_samples_and_leaves_out_what_came_before_a_moment()
    {
        var time = new ManualTime();
        var buffer = new PcSoundBuffer(TimeSpan.FromSeconds(10), time) { Recording = true };
        time.Advance(TimeSpan.FromSeconds(1));
        buffer.Append(Tone(2, 0.5));
        time.Advance(TimeSpan.FromSeconds(2));
        var quiet = new byte[2 * PcSoundBuffer.SampleRate * 2];
        buffer.Append(quiet);
        var newest = buffer.Latest(TimeSpan.FromSeconds(2));
        Assert.Equal(2 * PcSoundBuffer.SampleRate, newest.Length);
        Assert.All(newest, sample => Assert.Equal(0f, sample));
        var all = buffer.Latest(TimeSpan.FromSeconds(4));
        Assert.Equal(4 * PcSoundBuffer.SampleRate, all.Length);
        Assert.Contains(all.Take(2 * PcSoundBuffer.SampleRate), sample => Math.Abs(sample) > 0.4f);
        // Only what came after a moment one second before the newest sound.
        var since = buffer.Latest(TimeSpan.FromSeconds(4), time.GetTimestamp() - TimeSpan.TicksPerSecond);
        Assert.Equal(PcSoundBuffer.SampleRate, since.Length);
    }

    [Fact]
    public async Task The_pc_capture_keeps_the_last_seconds_for_the_digest_only_while_its_buffer_records()
    {
        var time = new SimulatedClock();
        var buffer = new PcSoundBuffer(clock: time) { Recording = true };
        await RecordAsync(new PcAudioCaptureFactory(new ToneSources(time), time, buffer));
        Assert.InRange(buffer.Buffered.TotalSeconds, 2.8, 3.05);
        var clip = buffer.Latest(TimeSpan.FromSeconds(1));
        var rms = Math.Sqrt(clip.Average(s => (double)s * s));
        Assert.InRange(rms, 0.2, 0.25); // a 0.3 tone, mixed to mono and resampled to 16 kHz
        Assert.True(SoundDigest.ActiveShare(clip) > 0.95);

        var off = new PcSoundBuffer(clock: time);
        var pcm = await RecordAsync(new PcAudioCaptureFactory(new ToneSources(time), time, off));
        Assert.True(pcm.Length > 0);
        Assert.Equal(TimeSpan.Zero, off.Buffered);
        Assert.Null(new PcAudioCaptureFactory(new ToneSources(time), time).Sound);
    }

    [Fact]
    public void A_tagger_s_labels_become_one_line_about_the_sound_that_is_not_speech()
    {
        Assert.Equal("Music: pop and guitar with singing, happy; laughter",
            SoundDigest.Line([new("Music", 0.9), new("Speech", 0.8), new("Pop music", 0.5), new("Singing", 0.45), new("Guitar", 0.3),
                new("Happy music", 0.3), new("Laughter", 0.25), new("Inside, small room", 0.4), new("Piano", 0.05)]));
        Assert.Equal("Music: a capella", SoundDigest.Line([new("Music", 0.8), new("A capella", 0.77), new("Choir", 0.4)]));
        Assert.Equal("Music", SoundDigest.Line([new("Music", 0.6)]));
        Assert.Equal("cat, caterwaul", SoundDigest.Line([new("Animal", 0.95), new("Cat", 0.94), new("Domestic animals, pets", 0.93),
            new("Caterwaul", 0.19)]));
        Assert.Equal("gunshot, explosion, applause", SoundDigest.Line([new("Gunshot, gunfire", 0.7), new("Explosion", 0.5),
            new("Applause", 0.4), new("Clapping", 0.3)]));
        Assert.Null(SoundDigest.Line([new("Speech", 0.9), new("Male speech, man speaking", 0.6), new("Narration, monologue", 0.3)]));
        Assert.Null(SoundDigest.Line([new("Silence", 0.9)]));
        Assert.Null(SoundDigest.Line([new("Laughter", 0.1)]));
        Assert.Null(SoundDigest.Line([]));
    }

    [Fact]
    public void A_model_s_answer_becomes_one_clean_line_and_none_means_no_line()
    {
        Assert.Null(SoundDigest.Clean(null));
        Assert.Null(SoundDigest.Clean("  "));
        Assert.Null(SoundDigest.Clean("none"));
        Assert.Null(SoundDigest.Clean("None."));
        Assert.Equal("Upbeat J-pop with female vocals; crowd laughing",
            SoundDigest.Clean("\"Upbeat J-pop with female vocals; crowd laughing\"\nThe speaker says hello."));
        var line = SoundDigest.Clean(new string('a', 400));
        Assert.Equal(SoundDigest.MaximumLine, line!.Length);
        Assert.EndsWith("…", line);
        Assert.Contains("none", SoundDigest.Prompt, StringComparison.Ordinal);
        Assert.Contains("Never transcribe", SoundDigest.Prompt, StringComparison.Ordinal);
    }

    [Fact]
    public void Active_share_and_wave_describe_the_clip()
    {
        Assert.Equal(0, SoundDigest.ActiveShare(new float[PcSoundBuffer.SampleRate]));
        var half = new float[PcSoundBuffer.SampleRate];
        for (var i = 0; i < half.Length / 2; i++) half[i] = (float)(0.2 * Math.Sin(i * 0.1));
        Assert.Equal(0.5, SoundDigest.ActiveShare(half), 2);
        var wave = SoundDigest.Wave([0f, 0.5f, -1f]);
        Assert.Equal(44 + 6, wave.Length);
        Assert.Equal("RIFF"u8.ToArray(), wave[..4]);
        Assert.Equal(PcSoundBuffer.SampleRate, BinaryPrimitives.ReadInt32LittleEndian(wave.AsSpan(24)));
        Assert.Equal(1, BinaryPrimitives.ReadInt16LittleEndian(wave.AsSpan(22)));
        Assert.Equal(16384, BinaryPrimitives.ReadInt16LittleEndian(wave.AsSpan(46)));
        Assert.Equal(-32767, BinaryPrimitives.ReadInt16LittleEndian(wave.AsSpan(48)));
    }

    private sealed class Judge(SoundJudgeKind kind = SoundJudgeKind.Cpu) : ISoundJudge
    {
        public string Name => kind == SoundJudgeKind.Pool ? "listener-model" : "CPU sound tagger";
        public SoundJudgeKind Kind => kind;
        public int Calls;
        public int LastLength;
        public TaskCompletionSource<string?> Answer = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<string?> DescribeAsync(float[] clip, CancellationToken cancellationToken)
        {
            Calls++;
            LastLength = clip.Length;
            return Answer.Task.WaitAsync(cancellationToken);
        }
    }

    private static readonly SoundDigestOptions Manual = new() { Interval = Timeout.InfiniteTimeSpan };

    [Fact]
    public async Task The_digest_judges_a_clip_while_something_plays_and_posts_one_line_without_waiting_for_anyone()
    {
        var time = new ManualTime();
        var buffer = new PcSoundBuffer(clock: time);
        var judge = new Judge();
        var posted = new List<SoundDigestLine>();
        using var digest = new SoundDigestScheduler(buffer, () => judge, () => false, posted.Add, Manual);
        Assert.Equal(SoundDigestStep.Idle, digest.Tick());
        digest.On = true;
        Assert.True(buffer.Recording);
        Assert.Equal(SoundDigestStep.NoSound, digest.Tick());
        Play(buffer, time, 2);
        Assert.Equal(SoundDigestStep.TooShort, digest.Tick());
        Play(buffer, time, 10);
        Assert.Equal(SoundDigestStep.Started, digest.Tick());
        Assert.True(SpinWait.SpinUntil(() => Volatile.Read(ref judge.Calls) == 1, 5000));
        Assert.Equal(10 * PcSoundBuffer.SampleRate, judge.LastLength);
        Assert.Equal(SoundDigestStep.Busy, digest.Tick());
        time.Advance(TimeSpan.FromMilliseconds(180));
        judge.Answer.SetResult("Music: pop with singing");
        await digest.Idle;
        var line = Assert.Single(posted);
        Assert.Equal("Music: pop with singing", line.Text);
        Assert.Equal("CPU sound tagger", line.Judge);
        Assert.Equal(TimeSpan.FromMilliseconds(180), line.Took);
        Assert.Same(line, digest.Fresh);
        var status = digest.Status;
        Assert.True(status.On);
        Assert.Equal(SoundJudgeKind.Cpu, status.JudgeKind);
        Assert.Equal((1, 1), (status.Runs, status.Lines));
        // Not again until the cadence comes round.
        judge.Answer = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Play(buffer, time, 5);
        Assert.Equal(SoundDigestStep.Idle, digest.Tick());
        Play(buffer, time, 5);
        Assert.Equal(SoundDigestStep.Started, digest.Tick());
        judge.Answer.SetResult("none");
        await digest.Idle;
        Assert.Single(posted);
        // A line is fresh only for its maximum age.
        time.Advance(digest.Options.MaximumAge);
        Assert.Null(digest.Fresh);
        digest.On = false;
        Assert.False(buffer.Recording);
        Assert.Equal(TimeSpan.Zero, buffer.Buffered);
    }

    [Fact]
    public void The_digest_skips_silence_and_martlet_s_own_voice_and_needs_a_judge()
    {
        var time = new ManualTime();
        var buffer = new PcSoundBuffer(clock: time);
        var judge = new Judge();
        ISoundJudge? current = null;
        var speaking = false;
        using var digest = new SoundDigestScheduler(buffer, () => current, () => speaking, _ => { }, Manual) { On = true };
        Play(buffer, time, 10, amplitude: 0.001);
        Assert.Equal(SoundDigestStep.Silent, digest.Tick());
        Play(buffer, time, 10);
        Assert.Equal(SoundDigestStep.NoJudge, digest.Tick());
        current = judge;
        Play(buffer, time, 10);
        speaking = true;
        Assert.Equal(SoundDigestStep.MartletSpeaking, digest.Tick());
        speaking = false;
        // What played while Martlet spoke (and its echo tail) is never judged: wait for enough sound after it.
        Play(buffer, time, 3);
        Assert.Equal(SoundDigestStep.TooShort, digest.Tick());
        Play(buffer, time, 2);
        Assert.Equal(SoundDigestStep.Started, digest.Tick());
        Assert.True(SpinWait.SpinUntil(() => Volatile.Read(ref judge.Calls) == 1, 5000));
        Assert.Equal(4 * PcSoundBuffer.SampleRate, judge.LastLength);
        Assert.Equal(0, digest.Status.Lines);
        Assert.True(digest.Status.Skipped >= 3);
    }

    [Fact]
    public async Task A_judge_past_its_deadline_is_canceled_and_its_late_line_is_dropped()
    {
        var time = new ManualTime();
        var buffer = new PcSoundBuffer(clock: time);
        var judge = new Judge(SoundJudgeKind.Pool);
        var posted = new List<SoundDigestLine>();
        using var digest = new SoundDigestScheduler(buffer, () => judge, () => false, posted.Add, Manual) { On = true };
        Play(buffer, time, 10);
        Assert.Equal(SoundDigestStep.Started, digest.Tick());
        var first = judge.Answer;
        time.Advance(digest.Options.Deadline);
        Assert.Equal(SoundDigestStep.Dropped, digest.Tick());
        await digest.Idle;
        first.TrySetResult("Too late");
        Assert.Empty(posted);
        Assert.Equal(1, digest.Status.Dropped);
        Assert.Equal(SoundJudgeKind.Pool, digest.Status.JudgeKind);
        Assert.Equal("listener-model", digest.Status.Judge);
    }

    // ---------- a fixture loopback on a simulated clock (like pc_audio_check's) ----------

    private sealed class SimulatedClock : TimeProvider
    {
        private long now = 10_000_000_000;
        public long Now { get => Volatile.Read(ref now); set => Volatile.Write(ref now, value); }
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => Now;
    }

    private sealed class ToneSources(SimulatedClock clock) : IPcAudioSourceFactory
    {
        public IPcAudioSource Open(CancellationToken cancellationToken) => new Tone48k(clock);
    }

    // 48 kHz stereo PCM16, a 440 Hz tone at 0.3 for three seconds; each poll is 10 ms later.
    private sealed class Tone48k(SimulatedClock clock) : IPcAudioSource
    {
        private const int Rate = 48_000, Packet = Rate / 100;
        private long position;
        private readonly long origin = clock.Now;
        public bool WithoutMartlet => true;
        public CaptureSourceFormat Format { get; } = new(Rate, 2, 16, DeviceSampleEncoding.IntegerPcm);
        public void Start() { }
        public void Stop() { }
        public void Dispose() { }

        public CapturePacket Read(Span<byte> destination)
        {
            var start = position;
            position += Packet;
            clock.Now = origin + position * TimeSpan.TicksPerSecond / Rate;
            for (var i = 0; i < Packet; i++)
            {
                var value = (short)Math.Round(0.3 * short.MaxValue * Math.Sin(2 * Math.PI * 440 * (start + i) / Rate));
                BinaryPrimitives.WriteInt16LittleEndian(destination[(i * 4)..], value);
                BinaryPrimitives.WriteInt16LittleEndian(destination[(i * 4 + 2)..], value);
            }
            return new(Packet * 4);
        }
    }

    private static async Task<byte[]> RecordAsync(ICaptureDeviceFactory devices)
    {
        var session = Guid.NewGuid();
        await using var capture = new MicrophoneCapture(session, devices);
        var request = new CaptureRequest(new CorrelationIds { SessionId = session, TurnId = Guid.NewGuid(), RequestId = Guid.NewGuid() }, 1,
            new InputSelection(InputPolicy.FollowDefaultOnNextPress), TimeSpan.FromSeconds(3), DateTimeOffset.UtcNow.AddSeconds(25));
        var run = capture.Press(request, new CaptureAuthorization(request, true), CancellationToken.None);
        await run.Completion.WaitAsync(TimeSpan.FromSeconds(60));
        await run.DeviceRelease.WaitAsync(TimeSpan.FromSeconds(10));
        using var utterance = run.TakeUtterance() ?? throw new InvalidOperationException("Nothing was recorded.");
        var pcm = new byte[utterance.ByteCount];
        utterance.CopyPcmTo(pcm);
        return pcm;
    }
}
