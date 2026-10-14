using Martlet.Core.Cluster;
using Martlet.Core.Singing;

namespace Martlet.Core.Tests;

public sealed class SingingPoolTests
{
    private sealed class Singer(string id)
    {
        public string Id { get; } = id;
        public bool Sings { get; init; } = true;
        public bool Down { get; init; }
        public bool Vevo { get; init; }
        public bool Singing { get; init; }
        public int Waiting { get; init; }
        public string? FailsWith { get; init; }
        public bool StopsMidSong { get; init; }
        public bool Splits { get; init; } = true;
        public int Looks;
        public int Songs;

        public Task<SingerState?> Look(CancellationToken token)
        {
            Interlocked.Increment(ref Looks);
            if (Down) throw new HttpRequestException("no answer");
            return Task.FromResult(Sings ? new SingerState(Singing ? "busy" : "ready", Waiting,
                Vevo ? [SongVoiceMatch.SoulX, SongVoiceMatch.VevoSing] : [SongVoiceMatch.SoulX])
                { Kinds = Splits ? ["song", "music", "match"] : ["song"] } : null);
        }

        public async Task<string> Sing(CancellationToken token)
        {
            if (Waiting >= 4) throw new SongException(SongErrorCodes.Busy, "full");
            if (FailsWith is { } code) throw new SongException(code, "failed");
            Interlocked.Increment(ref Songs);
            await Task.Yield();
            if (StopsMidSong) throw new HttpRequestException("stopped answering");
            return Id;
        }
    }

    private static Task<SungOn<string>> Sing(IReadOnlyList<Singer> pool, bool match = false) =>
        SingingPool.RunAsync(new WorkQueue(), match ? SingingPool.MatchLane : SingingPool.Lane, pool, s => s.Id, (s, t) => s.Look(t),
            match ? SingingPool.MatchRefusal : SingingPool.MusicRefusal, (s, _, t) => s.Sing(t), null, null, CancellationToken.None);

    [Fact]
    public void Order_puts_the_saved_computer_first_then_the_plans_singers_then_the_rest_of_the_paired_ones()
    {
        var order = SingingPool.Order("m3-host", ["m1-host", "m3-host", "m4-host", "desk-host"],
            [new WorkPlace("m1-host", Jobs: 2), new WorkPlace("m4-host"), new WorkPlace("friend-host")]);
        Assert.Equal(["m3-host", "m4-host", "m1-host", "desk-host"], order);
        Assert.Equal(["m1-host"], SingingPool.Order("gone-host", ["m1-host"], []));
    }

    [Fact]
    public void A_pool_list_wins_over_the_older_choices_and_an_empty_one_is_off()
    {
        var list = new PoolList
        {
            Area = PoolAreas.Singing.Id,
            Members = [PoolMember.Computer("m4-host"), PoolMember.ThisPc(), PoolMember.Computer("m1-host") with { Off = true },
                PoolMember.Computer("friend-host"), PoolMember.Gpu("m3-host", 2) with { OnlyFor = ["desk-9"] }]
        };
        var members = SingingPool.Members(list, "desk-2", "m3-host", "desk-host", ["m1-host", "m3-host", "m4-host", "desk-host"], []);
        Assert.Equal(["m4-host", "desk-host"], members.Select(m => m.HostId));
        Assert.Equal("this-pc", members[1].Member.Key);
        Assert.Empty(SingingPool.Members(new PoolList { Area = PoolAreas.Singing.Id }, "desk-2", "m3-host", null, ["m3-host"], []));
        var older = SingingPool.Members(null, "desk-2", "m3-host", "desk-host", ["desk-host", "m3-host"], []);
        Assert.Equal(["host:m3-host", "this-pc"], older.Select(m => m.Member.Key));
        Assert.Equal("singing", SingingPool.Lane);
    }

    [Fact]
    public async Task A_free_first_computer_takes_the_song_and_no_other_is_asked()
    {
        Singer first = new("m3-host"), second = new("m4-host");
        var sung = await Sing([first, second]);
        Assert.Equal(("m3-host", false), (sung.HostId, sung.Queued));
        Assert.Equal(0, second.Looks);
    }

    [Fact]
    public async Task Busy_offline_and_too_old_computers_are_passed_over_for_a_free_one()
    {
        Singer singing = new("m1-host") { Singing = true }, off = new("m2-host") { Down = true },
            plain = new("m3-host") { Sings = false }, older = new("m4-host") { Splits = false }, newer = new("m5-host");
        var matched = await Sing([singing, off, plain, older, newer], match: true);
        Assert.Equal(("m5-host", false), (matched.HostId, matched.Queued));
        Assert.Equal(0, singing.Songs + older.Songs);
        // A computer whose Martlet is too old to make a voice match apart still writes music (it makes the whole song there).
        var none = await Assert.ThrowsAsync<SongException>(() => Sing([new Singer("m4-host") { Splits = false }], match: true));
        Assert.Equal(SongErrorCodes.Unavailable, none.Code);
        Assert.Equal("m4-host", (await Sing([new Singer("m4-host") { Splits = false }])).HostId);
    }

    [Fact]
    public void Each_member_has_its_own_quality_and_model_high_quality_and_SoulX_by_default()
    {
        var member = PoolMember.Computer("m3-host");
        Assert.Equal(SongQuality.HighQuality, SingingPool.Quality(member));
        Assert.Equal(SongQuality.HighQuality, new SongRequest { Lyrics = "la", Style = "pop", VoiceId = "v1" }.Quality);
        var fast = SingingPool.WithQuality(member, SongQuality.Fast);
        Assert.Equal(SongQuality.Fast, SingingPool.Quality(fast));
        Assert.Empty(SingingPool.WithQuality(fast, SongQuality.HighQuality).Settings);
        Assert.Equal(SongVoiceMatch.SoulX, SingingPool.VoiceMatch(member));
        var vevo = SingingPool.WithVoiceMatch(member, SongVoiceMatch.VevoSing);
        Assert.Equal("vevosing", vevo.Setting(SingingPool.ModelSetting));
        // VevoSing where it is set up; SoulX-Singer where it isn't yet.
        Assert.Equal(SongVoiceMatch.VevoSing, SingingPool.MatchWith(vevo, new SingerState("ready", 0, [SongVoiceMatch.SoulX, SongVoiceMatch.VevoSing])));
        Assert.Equal(SongVoiceMatch.SoulX, SingingPool.MatchWith(vevo, new SingerState("ready", 0, [SongVoiceMatch.SoulX])));
        Assert.Equal("voice-match", SingingPool.MatchLane);
        Assert.True(PoolAreas.VoiceMatch.Required);
        Assert.Contains(PoolAreas.VoiceMatch, PoolAreas.All);
    }

    [Fact]
    public void A_songs_music_and_voice_match_are_put_together_without_clipping()
    {
        const int frames = 480;
        static byte[] Constant(int samples, short value)
        {
            var pcm = new byte[samples * 2];
            for (var i = 0; i < samples; i++) System.Buffers.Binary.BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan(2 * i), value);
            return pcm;
        }
        var music = new SongResult
        {
            JobId = "music-1", VoiceId = "v1",
            Mix = new SongTrack(SongTrackKind.Mix, 48_000, 2, Constant(frames * 2, 1_000)),
            Vocals = new SongTrack(SongTrackKind.Vocals, 48_000, 1, Constant(frames, 1_000)),
            Backing = new SongTrack(SongTrackKind.Backing, 48_000, 2, Constant(frames * 2, 30_000)),
            Engine = new SongEngineIdentity("ace-step", "demucs", "", SongQuality.HighQuality, null, false), Seed = 7,
            StageTimings = [new(SongStage.WritingMusic, TimeSpan.FromSeconds(5)), new(SongStage.Completed, TimeSpan.FromSeconds(6))]
        };
        // The matched vocals are shorter than the backing: the rest is silence.
        var match = new SongVoiceMatchResult("match-1", new SongTrack(SongTrackKind.Vocals, 48_000, 1, Constant(frames - 80, 10_000)),
            "soulx-singer-svc", SongVoiceMatch.SoulX, false,
            [new(SongStage.MatchingVoice, TimeSpan.FromSeconds(3)), new(SongStage.Completed, TimeSpan.FromSeconds(4))]);
        var song = SongVoice.WithVoice(music, match);
        short At(SongTrack track, int sample) => System.Buffers.Binary.BinaryPrimitives.ReadInt16LittleEndian(track.Pcm16.Span[(2 * sample)..]);
        Assert.Equal(frames, song.Mix.Frames);
        Assert.Equal(frames, song.Vocals.Frames);
        // 30,000 + 10,000 would clip: all three tracks are scaled by the same factor, to 0.98 of full scale.
        var scale = SongVoice.Peak * short.MaxValue / 40_000;
        Assert.InRange(At(song.Mix, 0), (int)(40_000 * scale) - 1, (int)(40_000 * scale) + 1);
        Assert.InRange(At(song.Vocals, 0), (int)(10_000 * scale) - 1, (int)(10_000 * scale) + 1);
        Assert.Equal(0, At(song.Vocals, frames - 1));
        Assert.Equal(At(song.Backing, 2 * (frames - 1)), At(song.Mix, 2 * (frames - 1)));
        Assert.Equal((SongVoiceMatch?)SongVoiceMatch.SoulX, song.Engine.VoiceMatch);
        Assert.Equal("soulx-singer-svc", song.Engine.Converter);
        Assert.Equal("music-1", song.JobId);
        Assert.Equal(TimeSpan.FromSeconds(10), song.StageTimings.Single(t => t.Stage == SongStage.Completed).Duration);
        Assert.Equal(3, song.StageTimings.Count);
    }

    [Fact]
    public async Task When_every_computer_is_busy_the_song_waits_on_the_shortest_line_and_full_lines_refuse()
    {
        Singer longLine = new("m3-host") { Singing = true, Waiting = 2 }, shortLine = new("m4-host") { Singing = true };
        var sung = await Sing([longLine, shortLine]);
        Assert.Equal(("m4-host", true, 1), (sung.HostId, sung.Queued, sung.Ahead));
        var states = new Dictionary<string, SingerState?>
        {
            ["m3-host"] = await longLine.Look(default), ["m4-host"] = await shortLine.Look(default)
        };
        Assert.Equal(("m4-host", 1), SingingPool.Pick(["m3-host", "m4-host"], states));
        Assert.StartsWith("busy: 1 job", SingingPool.Verdict("m4-host", states["m4-host"]));

        Singer full = new("m3-host") { Singing = true, Waiting = 4 }, fuller = new("m4-host") { Singing = true, Waiting = 4 };
        var refused = await Assert.ThrowsAsync<SongException>(() => Sing([full, fuller]));
        Assert.Equal(SongErrorCodes.Busy, refused.Code);
    }

    [Fact]
    public async Task A_failed_song_is_not_made_again_but_a_computer_that_stops_answering_is_replaced()
    {
        Singer failing = new("m3-host") { FailsWith = SongErrorCodes.Failed }, spare = new("m4-host");
        var failed = await Assert.ThrowsAsync<SongException>(() => Sing([failing, spare]));
        Assert.Equal(SongErrorCodes.Failed, failed.Code);
        Assert.Equal(0, spare.Looks);

        Singer dropping = new("m3-host") { StopsMidSong = true }, steady = new("m4-host");
        var sung = await Sing([dropping, steady]);
        Assert.Equal("m4-host", sung.HostId);
        Assert.Equal((1, 1), (dropping.Songs, steady.Songs));
    }

    [Fact]
    public void Status_parses_state_line_and_voice_matches()
    {
        using var document = System.Text.Json.JsonDocument.Parse("""{"state":"busy","queue":2,"voice_matches":["soulx","vevosing"],"kinds":["song","music","match"]}""");
        var state = SingerState.Parse(document.RootElement);
        Assert.Equal(3, state.Ahead);
        Assert.Contains(SongVoiceMatch.VevoSing, state.VoiceMatches);
        Assert.True(state.Splits);
        using var older = System.Text.Json.JsonDocument.Parse("""{"state":"ready","queue":0,"voice_matches":["soulx"]}""");
        Assert.False(SingerState.Parse(older.RootElement).Splits);
        Assert.Equal(WorkRefusal.Busy, SingingPool.Classify(new SongException(SongErrorCodes.Busy, "full")));
        Assert.Equal(WorkRefusal.Unavailable, SingingPool.Classify(new HttpRequestException("gone")));
        Assert.Equal(WorkRefusal.None, SingingPool.Classify(new SongException(SongErrorCodes.RequestInvalid, "bad")));
    }
}
