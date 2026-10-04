using Martlet.Conversation;
using Martlet.Core.Singing;

namespace Martlet.Conversation.Tests;

public sealed class SongTests
{
    private const string Lyrics = "[verse]\nOne\nTwo\nThree\nFour\n[chorus]\nFive\nSix\n[verse]\nSeven\nEight\n[chorus]\nNine\nTen";

    private static (SongMap Map, SongAudio Audio, SongResult Result) Fixture()
    {
        var result = FixtureSongMaker.Compose(new SongRequest
        {
            Lyrics = Lyrics, Style = "pop", VoiceId = "fixture-voice", DurationSeconds = 40, Bpm = 96, Seed = 1
        });
        return (SongMap.Of(result), SongAudio.From(result), result);
    }

    [Fact]
    public void FromResolvesSectionsLinesTimesAndResume()
    {
        var (map, _, _) = Fixture();
        Assert.True(SongTransport.Resolve(map, null, null).Target!.Top);
        Assert.Equal(4, SongTransport.Resolve(map, "the chorus", null).Target!.Line);
        Assert.Equal(6, SongTransport.Resolve(map, "second verse", null).Target!.Line);
        Assert.Equal(2, SongTransport.Resolve(map, "line:3", null).Target!.Line);
        Assert.NotNull(SongTransport.Resolve(map, "bridge", null).Problem);
        var stopped = new SongStopRecord("3fa2c19b0d71", "T", TimeSpan.FromSeconds(8.5), map.Duration, 2, "verse", "Three", null, 10,
            SongStopCause.UserWords, "stop singing");
        Assert.Equal(2, SongTransport.Resolve(map, "resume", stopped).Target!.Line);
        Assert.Equal(0, SongTransport.Resolve(map, "resume section", stopped).Target!.Line);
        var ended = stopped with { Cause = SongStopCause.Ended, Line = null };
        Assert.True(SongTransport.Resolve(map, "resume", ended).Target!.Top);
    }

    [Fact]
    public void LeadInEntersOnADownbeatBeforeTheLineWithTheVocalsMuted()
    {
        var (map, audio, _) = Fixture();
        var plan = SongTransport.PlanStart(map, new(3, null, "line 4"));
        Assert.Contains(plan.Entry, map.Downbeats);
        Assert.Equal(1, plan.LeadInBars);
        Assert.True(plan.LeadIn >= map.Bar / 2);
        var mixer = new SongMixer(new(new short[audio.Backing.Length], audio.Vocals), map, null, plan);
        var output = new short[(int)((plan.VocalsFrom - plan.Entry - SongMixer.VocalRamp).TotalSeconds * 48_000) * 2];
        Assert.Equal(output.Length / 2, mixer.Render(output, output.Length / 2));
        Assert.All(output, sample => Assert.Equal(0, sample));
    }

    [Fact]
    public void MusicalStopEndsTheWordThenFadesTheBandFromTheNextBeat()
    {
        var (map, audio, _) = Fixture();
        var envelope = new VocalEnvelope(audio.Vocals, audio.SampleRate);
        var stop = SongTransport.PlanStop(map, envelope, TimeSpan.FromSeconds(8.5), TimeSpan.FromSeconds(8.55), musical: true);
        Assert.True(stop.VocalsEnd - stop.Requested <= SongTransport.MusicalStopWindow);
        Assert.Contains(stop.BackingFrom, map.Beats);
        Assert.Equal(map.Beat, stop.BackingFade);
        var quick = SongTransport.PlanStop(map, envelope, TimeSpan.FromSeconds(8.5), TimeSpan.FromSeconds(8.55), musical: false);
        Assert.InRange((quick.SilentAt - TimeSpan.FromSeconds(8.85)).Duration(), TimeSpan.Zero, TimeSpan.FromMilliseconds(1));
    }

    [Fact]
    public void StopNoteSaysWhereAndWhyAndHowToResume()
    {
        var note = new SongStopRecord("song-abcdef", "Morning Light", TimeSpan.FromSeconds(22), TimeSpan.FromSeconds(60), 3, "verse",
            "When you're laughing like before", null, 12, SongStopCause.UserWords, "okay okay Martlet, stop singing").Note();
        Assert.Contains("at 0:22, in verse line 4 of 12, \"When you're laughing like before\"", note);
        Assert.Contains("because the user said: \"okay okay Martlet, stop singing\"", note);
        Assert.Contains("from=resume restarts that line", note);
    }

    [Fact]
    public void WrittenSongIsReadFromTheLyricsStep()
    {
        var arguments = new SingArguments("a cat", null, null, 60);
        var (song, problem) = SongTools.ParseWritten("TITLE: Biscuit\nSTYLE: ukulele pop\nBPM: 96\nKEY: G major\nLYRICS:\n" + Lyrics, arguments);
        Assert.Null(problem);
        Assert.Equal("Biscuit", song!.Title);
        Assert.Equal(96, song.Bpm);
        Assert.Equal("G major", song.Key);
        Assert.Equal(10, SongLyrics.Parse(song.Lyrics).Count);
        Assert.NotNull(SongTools.ParseWritten("I can't do that.", arguments).Problem);
        Assert.Null(SongTools.ParseSing("{\"about\":\"a cat\",\"duration\":500}").Problem);
        Assert.Equal(180, SongTools.ParseSing("{\"about\":\"a cat\",\"duration\":500}").Arguments!.Seconds);
    }

    [Fact]
    public void MouthTrackFollowsTheVocalOnsetsAndKeepsWithTheSong()
    {
        var (map, audio, _) = Fixture();
        var envelope = new VocalEnvelope(audio.Vocals, audio.SampleRate);
        var mouth = SongMouthTrack.FromVisemes(map, envelope, null);
        Assert.Equal(SongMouthSource.Visemes, mouth.Source);
        var timing = mouth.Measure(envelope);
        Assert.True(timing.Good, $"{timing}");
        Assert.Equal(0, mouth.LevelAt(TimeSpan.FromSeconds(1)));
        var again = SongMouthTrack.FromJson(mouth.ToJson())!;
        Assert.Equal(mouth.Frames, again.Frames);
        Assert.Equal(mouth.LevelAt(TimeSpan.FromSeconds(10.3)), again.LevelAt(TimeSpan.FromSeconds(10.3)));
        Assert.Equal(["closed", "ou", "open"], Visemes.Of("moon").Select(unit => unit.Name));
        Assert.Equal(["closed", "ee"], Visemes.Of("me").Select(unit => unit.Name));
        Assert.Equal(["open", "oh", "closed"], Visemes.Of("home").Select(unit => unit.Name));
    }

    [Fact]
    public async Task SongsAreKeptAsCreationsWithTheirTracksMapAndMouth()
    {
        var directory = Path.Combine(Path.GetTempPath(), "martlet-songs-" + Guid.NewGuid().ToString("N"));
        try
        {
            var (map, audio, result) = Fixture();
            var envelope = new VocalEnvelope(audio.Vocals, audio.SampleRate);
            var words = SongMouthTrack.Spread(map, envelope);
            var mouth = SongMouthTrack.FromVisemes(map, envelope, words, estimated: true);
            var registry = new Martlet.Core.Creations.CreationRegistry();
            registry.Register(SongCreations.Kind);
            var author = new Martlet.Core.Creations.CreationAuthor { Device = "desktop-test", Voice = "fixture-voice" };
            var creation = await Martlet.Core.Creations.CreationStore.AddAsync(directory,
                SongCreations.Draft(result, "Biscuit", "a cat", Lyrics, "pop", words, true, mouth, author), registry, DateTimeOffset.UtcNow,
                CancellationToken.None);
            var (song, loaded, track, problem) = await SongCreations.LoadAsync(directory, creation.Key, CancellationToken.None);
            Assert.Null(problem);
            Assert.Equal(creation.Key, song!.Id);
            Assert.Equal(10, song.Lines.Count);
            Assert.Equal(words.Count, song.Words.Count);
            Assert.Equal(result.Downbeats.Count, song.Downbeats.Count);
            Assert.Equal(audio.Vocals, loaded!.Vocals);
            Assert.Equal(audio.Backing, loaded.Backing);
            Assert.Equal(SongMouthSource.Visemes, track!.Source);
            Assert.Contains(song.Id, SongTools.Ready(song));
            Assert.Equal(["Verse", "Chorus", "Verse", "Chorus"], SongCreations.Sections(Lyrics).Select(section => section.Heading));
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }
}
