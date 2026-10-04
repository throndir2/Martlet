using Martlet.Core.Singing;

namespace Martlet.Core.Tests;

public sealed class SongContractsTests
{
    private static SongRequest Request(int duration = 20) => new()
    {
        Lyrics = "[verse]\nMorning light is on the window\nCoffee steaming by the door\n\n[chorus]\nSing it with me, sing it slowly",
        Style = "gentle acoustic pop ballad, warm female vocal",
        VoiceId = "7fa3706cd44bca97ca7dabac88ed34822492952938a0cd34dcdffeb02e22bffe",
        DurationSeconds = duration,
        Seed = 42
    };

    [Fact]
    public void ValidRequestPassesAndBoundsAreEnforced()
    {
        Request().Validate();
        Assert.Equal(SongErrorCodes.RequestInvalid,
            Assert.Throws<SongException>(() => (Request() with { DurationSeconds = 14 }).Validate()).Code);
        Assert.Throws<SongException>(() => (Request() with { DurationSeconds = 181 }).Validate());
        Assert.Throws<SongException>(() => (Request() with { VoiceId = "C:\\voices\\jane.wav" }).Validate());
        Assert.Throws<SongException>(() => (Request() with { VoiceId = "https://example.com/a.wav" }).Validate());
        Assert.Throws<SongException>(() => (Request() with { Lyrics = new string('a', SongRequest.MaximumLyricsCharacters + 1) }).Validate());
        Assert.Throws<SongException>(() => (Request() with { Style = "" }).Validate());
        Assert.Throws<SongException>(() => (Request() with { Bpm = 300 }).Validate());
        Assert.Throws<SongException>(() => (Request() with { Language = "english" }).Validate());
        (Request() with { Bpm = 92, Key = "F# minor", Language = "ja", Quality = SongQuality.HighQuality,
            VoiceMatch = SongVoiceMatch.VevoSing }).Validate();
    }

    [Fact]
    public async Task FixtureMakesThreeAlignedTracksWithProgressAndLyrics()
    {
        var stages = new List<SongStage>();
        var progress = new SynchronousProgress(p => stages.Add(p.Stage));
        var maker = new FixtureSongMaker(TimeSpan.Zero);
        Assert.True((await maker.GetAvailabilityAsync(CancellationToken.None)).Fixture);
        var song = await maker.GenerateAsync(Request(), progress, CancellationToken.None);

        Assert.True(song.Engine.Fixture);
        Assert.Equal(20, song.Duration.TotalSeconds, 3);
        Assert.Equal(song.Mix.Frames, song.Vocals.Frames);
        Assert.Equal(song.Mix.Frames, song.Backing.Frames);
        Assert.Equal((2, 1, 2), (song.Mix.Channels, song.Vocals.Channels, song.Backing.Channels));
        Assert.All([song.Mix, song.Vocals, song.Backing], t => Assert.Equal(SongTrack.SampleRateHz, t.SampleRate));
        Assert.Equal(3, song.LyricTimestamps.Count);
        Assert.Equal("Morning light is on the window", song.LyricTimestamps[0].Text);
        Assert.Equal(["verse", "verse", "chorus"], song.LyricTimestamps.Select(l => l.Section));
        Assert.True(song.LyricTimestamps[1].Start > song.LyricTimestamps[0].Start);
        Assert.Equal(4, song.BeatsPerBar);
        Assert.Equal(96, song.Bpm);
        Assert.Equal(TimeSpan.Zero, song.Beats[0]);
        Assert.Equal(60.0 / 96, (song.Beats[1] - song.Beats[0]).TotalSeconds, 6);
        Assert.True(song.Beats[^1] < song.Duration && song.Beats[^1] > song.Duration - TimeSpan.FromSeconds(1));
        Assert.All(song.Downbeats, d => Assert.Contains(d, song.Beats));
        Assert.All(song.LyricTimestamps, l => Assert.Contains(l.Start, song.Downbeats));
        Assert.Equal([SongStage.Queued, SongStage.WritingMusic, SongStage.Separating, SongStage.MatchingVoice,
            SongStage.Mixing, SongStage.Aligning, SongStage.Delivering, SongStage.Completed], stages);
        Assert.Equal(6, song.StageTimings.Count);
        Assert.Contains(song.Vocals.Pcm16.ToArray(), b => b != 0);

        var wave = song.Vocals.ToWave();
        Assert.Equal("RIFF"u8.ToArray(), wave[..4]);
        Assert.Equal(44 + song.Vocals.Pcm16.Length, wave.Length);
    }

    [Fact]
    public void FixtureIsDeterministicAndVoiceDependent()
    {
        var a = FixtureSongMaker.Compose(Request());
        var b = FixtureSongMaker.Compose(Request());
        var other = FixtureSongMaker.Compose(Request() with { VoiceId = "another-voice" });
        Assert.Equal(a.Vocals.Pcm16.ToArray(), b.Vocals.Pcm16.ToArray());
        Assert.NotEqual(a.Vocals.Pcm16.ToArray(), other.Vocals.Pcm16.ToArray());
    }

    [Fact]
    public void LyricsParseIntoNumberedSections()
    {
        var lines = SongLyrics.Parse("Intro words\n[Verse]\nA\n\n[chorus]\nB\n[verse]\nC\n[Chorus]\nD\n[bridge 1]\nE\n[]\n");
        Assert.Equal([("", "Intro words"), ("verse", "A"), ("chorus", "B"), ("verse 2", "C"), ("chorus 2", "D"),
            ("bridge 1", "E")], lines);
    }

    [Fact]
    public async Task FixtureHonoursCancellation()
    {
        using var stop = new CancellationTokenSource();
        await stop.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new FixtureSongMaker(TimeSpan.FromSeconds(5)).GenerateAsync(Request(), null, stop.Token));
    }

    private sealed class SynchronousProgress(Action<SongProgress> report) : IProgress<SongProgress>
    {
        public void Report(SongProgress value) => report(value);
    }
}
