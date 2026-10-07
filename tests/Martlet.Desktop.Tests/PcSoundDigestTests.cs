using System.Text.Json;
using Martlet.Audio;
using Martlet.Desktop;

namespace Martlet.Desktop.Tests;

public sealed class PcSoundDigestTests
{
    [Fact]
    public void Describe_pc_sounds_is_on_by_default_and_saved_like_the_other_listening_choices()
    {
        Assert.True(new TalkPreferences().DescribePcSounds);
        Assert.True(TalkPreferences.Load(null).DescribePcSounds);
        var directory = Directory.CreateTempSubdirectory("martlet-sound-digest-").FullName;
        try
        {
            File.WriteAllText(Path.Combine(directory, "talk-preferences.json"), "{\"HearPc\":true,\"Version\":4}");
            Assert.True(TalkPreferences.Load(directory).DescribePcSounds);
            Assert.True((new TalkPreferences() with { DescribePcSounds = false }).Save(directory));
            Assert.False(TalkPreferences.Load(directory).DescribePcSounds);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public void Companion_says_whether_it_runs_which_judge_describes_the_sound_and_the_last_line()
    {
        var clock = TimeProvider.System;
        var on = new TalkPreferences() with { HearPc = true };
        var cpu = (CpuSoundJudge.Label, (SoundJudgeKind?)SoundJudgeKind.Cpu);
        Assert.Equal("Works while Hear what this PC plays is on.", MainWindow.SoundDigestStatus(new TalkPreferences(), true, null, cpu, clock));
        Assert.Equal("Off. Only the words this PC plays reach Thinking.",
            MainWindow.SoundDigestStatus(on with { DescribePcSounds = false }, true, null, cpu, clock));
        Assert.Equal("Martlet can't hear what this PC plays here.", MainWindow.SoundDigestStatus(on, false, null, cpu, clock));
        var idle = MainWindow.SoundDigestStatus(on, true, null, cpu, clock);
        Assert.Contains("CPU sound tagger on this PC", idle, StringComparison.Ordinal);
        Assert.Contains("Runs while Martlet hears this PC.", idle, StringComparison.Ordinal);
        Assert.EndsWith("No line yet.", idle, StringComparison.Ordinal);
        var line = new SoundDigestLine("Music: pop with singing", "qwen-omni", SoundJudgeKind.Pool, clock.GetTimestamp(), TimeSpan.FromMilliseconds(420));
        var status = new SoundDigestStatus(true, "qwen-omni", SoundJudgeKind.Pool, line, 1, 1, 0, 0, SoundDigestStep.Started, line.Took);
        var running = MainWindow.SoundDigestStatus(on, true, status, ("qwen-omni", SoundJudgeKind.Pool), clock);
        Assert.Contains("qwen-omni in the Thinking pool hears a short clip.", running, StringComparison.Ordinal);
        Assert.Contains("Describing what plays now.", running, StringComparison.Ordinal);
        Assert.Contains("Last: \"Music: pop with singing\" (0 s ago, qwen-omni, 420 ms).", running, StringComparison.Ordinal);
        Assert.Contains("No judge", MainWindow.SoundDigestStatus(on, true, null, (null, null), clock), StringComparison.Ordinal);
    }

    private sealed class FixedJudge(string? line) : ISoundJudge
    {
        public string Name => CpuSoundJudge.Label;
        public SoundJudgeKind Kind => SoundJudgeKind.Cpu;
        public Task<string?> DescribeAsync(float[] clip, CancellationToken cancellationToken) => Task.FromResult(line);
    }

    private sealed class Pool : ISoundJudge
    {
        public string Name => "listener-model";
        public SoundJudgeKind Kind => SoundJudgeKind.Pool;
        public Task<string?> DescribeAsync(float[] clip, CancellationToken cancellationToken) => Task.FromResult<string?>("Applause");
    }

    [Fact]
    public async Task A_pool_member_that_hears_gets_a_digest_job_with_the_clip_and_the_prompt()
    {
        var text = new Martlet.Conversation.BackgroundPlace("host:text", "text-box") { Model = "qwen3" };
        var ears = new Martlet.Conversation.BackgroundPlace("this-pc", "this PC")
        {
            Model = "gemma4:e4b",
            Can = Martlet.Conversation.ThinkingCapability.Text | Martlet.Conversation.ThinkingCapability.Audio
        };
        Martlet.Conversation.ThinkingJob? seen = null;
        IReadOnlyList<Martlet.Conversation.BackgroundPlace> members = [text];
        var pool = new ThinkingPool(new Martlet.Conversation.ThinkingJobBoard(new Martlet.Conversation.BackgroundPlaces(), () => members,
            (member, job, _) =>
            {
                seen = job;
                return Task.FromResult(Martlet.Conversation.ThinkingAnswer.Done("Upbeat J-pop with singing; crowd laughing"));
            }));
        Assert.Null(PoolSoundJudge.For(pool));
        Assert.Null(PoolSoundJudge.For(null));
        members = [text, ears];
        var judge = PoolSoundJudge.For(pool)!;
        Assert.Equal("gemma4:e4b on this PC", judge.Name);
        Assert.Equal(SoundJudgeKind.Pool, judge.Kind);
        var clip = new float[3 * PcSoundBuffer.SampleRate];
        for (var i = 0; i < clip.Length; i++) clip[i] = (float)(0.2 * Math.Sin(i * 0.1));
        Assert.Equal("Upbeat J-pop with singing; crowd laughing", await judge.DescribeAsync(clip, CancellationToken.None));
        Assert.NotNull(seen);
        Assert.Equal(Martlet.Conversation.ThinkingJobKind.Digest, seen!.Kind);
        Assert.Equal(SoundDigest.Prompt, seen.Text);
        Assert.True(seen.DropWhenStale);
        Assert.False(seen.Reasoning);
        Assert.Equal(PcSoundBuffer.SampleRate, seen.Audio!.Format.SampleRate);
        Assert.Equal(TimeSpan.FromSeconds(3), seen.Audio.Duration);
    }

    [Fact]
    public async Task The_digest_posts_its_line_to_the_board_and_keeps_no_line_or_sound_on_disk()
    {
        var directory = Directory.CreateTempSubdirectory("martlet-sound-digest-").FullName;
        try
        {
            var buffer = new PcSoundBuffer();
            var posted = new List<SoundDigestLine>();
            var board = new Martlet.Conversation.ContextBoard();
            ISoundJudge? pool = null;
            using var digest = new PcSoundDigest(buffer, () => false, () => pool, board, directory,
                new FixedJudge("Music: J-pop with singing; laughter"), options: new() { Interval = Timeout.InfiniteTimeSpan });
            Assert.Equal(SoundJudgeKind.Cpu, digest.Status.JudgeKind);
            pool = new Pool();
            Assert.Equal(SoundJudgeKind.Pool, digest.Status.JudgeKind);
            pool = null;
            digest.Posted += posted.Add;
            digest.On = true;
            var tone = new byte[10 * PcSoundBuffer.SampleRate * 2];
            for (var i = 0; i < tone.Length / 2; i++)
                System.Buffers.Binary.BinaryPrimitives.WriteInt16LittleEndian(tone.AsSpan(i * 2), (short)(8000 * Math.Sin(i * 0.2)));
            buffer.Append(tone);
            Assert.Equal(SoundDigestStep.Started, digest.Scheduler.Tick());
            await digest.Scheduler.Idle;
            Assert.Equal("Music: J-pop with singing; laughter", Assert.Single(posted).Text);
            Assert.Equal("Music: J-pop with singing; laughter", digest.Fresh?.Text);
            var note = Assert.Single(board.Snapshot(DateTimeOffset.UtcNow).Notes);
            Assert.Equal(Martlet.Conversation.ContextBoard.Sound, note.Source);
            Assert.Equal("Sound playing on this PC besides speech: Music: J-pop with singing; laughter.", note.Text);
            Assert.Equal(TimeSpan.FromSeconds(45), note.MaxAge);
            var json = File.ReadAllText(Path.Combine(directory, PcSoundDigest.StatusFile));
            Assert.DoesNotContain("J-pop", json, StringComparison.Ordinal);
            using (var document = JsonDocument.Parse(json))
            {
                var root = document.RootElement;
                Assert.True(root.GetProperty("on").GetBoolean());
                Assert.Equal("cpu", root.GetProperty("judgeKind").GetString());
                Assert.Equal(1, root.GetProperty("lines").GetInt32());
                Assert.Equal(JsonValueKind.String, root.GetProperty("lastAt").ValueKind);
            }
            digest.On = false;
            Assert.Equal(TimeSpan.Zero, buffer.Buffered);
            Assert.Empty(board.Snapshot(DateTimeOffset.UtcNow).Notes);
            Assert.False(JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, PcSoundDigest.StatusFile))).RootElement.GetProperty("on").GetBoolean());
        }
        finally { Directory.Delete(directory, recursive: true); }
    }
}
