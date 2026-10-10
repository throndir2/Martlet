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
        public int Looks;
        public int Songs;

        public Task<SingerState?> Look(CancellationToken token)
        {
            Interlocked.Increment(ref Looks);
            if (Down) throw new HttpRequestException("no answer");
            return Task.FromResult(Sings ? new SingerState(Singing ? "busy" : "ready", Waiting,
                Vevo ? [SongVoiceMatch.SoulX, SongVoiceMatch.VevoSing] : [SongVoiceMatch.SoulX]) : null);
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

    private static Task<SungOn<string>> Sing(IReadOnlyList<Singer> pool, SongVoiceMatch match = SongVoiceMatch.SoulX) =>
        SingingPool.RunAsync(new WorkQueue(), pool, s => s.Id, (s, t) => s.Look(t), (s, t) => s.Sing(t), match, null, null,
            CancellationToken.None);

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
    public async Task Busy_offline_and_unmatched_computers_are_passed_over_for_a_free_one()
    {
        Singer singing = new("m1-host") { Singing = true }, off = new("m2-host") { Down = true },
            plain = new("m3-host") { Sings = false }, soulOnly = new("m4-host"), vevo = new("m5-host") { Vevo = true };
        var sung = await Sing([singing, off, plain, soulOnly, vevo], SongVoiceMatch.VevoSing);
        Assert.Equal(("m5-host", false), (sung.HostId, sung.Queued));
        Assert.Equal(0, singing.Songs + soulOnly.Songs);
        var none = await Assert.ThrowsAsync<SongException>(() => Sing([soulOnly], SongVoiceMatch.VevoSing));
        Assert.Equal(SongErrorCodes.VoiceMatchUnavailable, none.Code);
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
        Assert.Equal(("m4-host", 1), SingingPool.Pick(["m3-host", "m4-host"], states, SongVoiceMatch.SoulX));
        Assert.StartsWith("busy: 1 song", SingingPool.Verdict("m4-host", states["m4-host"], SongVoiceMatch.SoulX));

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
        using var document = System.Text.Json.JsonDocument.Parse("""{"state":"busy","queue":2,"voice_matches":["soulx","vevosing"]}""");
        var state = SingerState.Parse(document.RootElement);
        Assert.Equal(3, state.Ahead);
        Assert.Contains(SongVoiceMatch.VevoSing, state.VoiceMatches);
        Assert.Equal(WorkRefusal.Busy, SingingPool.Classify(new SongException(SongErrorCodes.Busy, "full")));
        Assert.Equal(WorkRefusal.Unavailable, SingingPool.Classify(new HttpRequestException("gone")));
        Assert.Equal(WorkRefusal.None, SingingPool.Classify(new SongException(SongErrorCodes.RequestInvalid, "bad")));
    }
}
