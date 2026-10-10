using System.Net.Http;
using Martlet.Core.Cluster;
using Martlet.Core.Singing;

namespace Martlet.Mcp;

/// <summary>singing_pool_check: rehearses the production singing pool (<see cref="SingingPool"/> through
/// <see cref="WorkQueue"/>, what the desktop's SongClient runs) with simulated singing computers that answer the singing
/// service's status (state, line, voice matches) and make a song as a host does: a full line refuses with singing.busy, a
/// computer that is off doesn't answer. NOT real hosts or models; nothing leaves the process.</summary>
internal static class SingingPoolCheck
{
    /// <summary>A simulated computer with the singing role.</summary>
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
            if (Down) throw new HttpRequestException($"{Id} didn't answer.");
            return Task.FromResult(Sings ? new SingerState(Singing ? "busy" : "ready", Waiting,
                Vevo ? [SongVoiceMatch.SoulX, SongVoiceMatch.VevoSing] : [SongVoiceMatch.SoulX]) : null);
        }

        public async Task<string> Sing(CancellationToken token)
        {
            if (Down) throw new HttpRequestException($"{Id} didn't answer.");
            if (Waiting >= 4) throw new SongException(SongErrorCodes.Busy, "Too many songs are waiting; try again later.");
            if (FailsWith is { } code) throw new SongException(code, $"The song failed on {Id}.");
            Interlocked.Increment(ref Songs);
            await Task.Delay(10, token);
            if (StopsMidSong) throw new SongException(SongErrorCodes.Unavailable, $"{Id} stopped answering.");
            return Id;
        }
    }

    private sealed record Outcome(string? Host, bool Queued, int Ahead, string? Failure, string? Problem);

    private static async Task<Outcome> SingAsync(IReadOnlyList<Singer> pool, SongVoiceMatch match = SongVoiceMatch.SoulX)
    {
        try
        {
            var sung = await SingingPool.RunAsync(new WorkQueue(), pool, s => s.Id, (s, t) => s.Look(t), (s, t) => s.Sing(t), match, null,
                null, CancellationToken.None);
            return new(sung.HostId, sung.Queued, sung.Ahead, null, null);
        }
        catch (SongException error) { return new(null, false, 0, error.Code, error.Message); }
    }

    private static IReadOnlyDictionary<string, SingerState?> States(IEnumerable<Singer> pool) =>
        pool.Where(s => !s.Down).ToDictionary(s => s.Id, s => s.Look(CancellationToken.None).Result, StringComparer.Ordinal);

    internal static async Task<object> RunAsync(CancellationToken cancellation)
    {
        List<object> steps = [];
        var ok = true;
        void Step(string name, bool passed, object detail)
        {
            ok &= passed;
            steps.Add(new { name, passed, detail });
        }

        var order = SingingPool.Order("m3-host", ["m1-host", "m3-host", "m4-host", "desk-host"],
            [new WorkPlace("m1-host", Jobs: 2), new WorkPlace("m4-host", Jobs: 0), new WorkPlace("friend-host")]);
        Step("Order: the computer Martlet sings on first, then the plan's singers (fewest jobs first), then the other paired computers; " +
            "a computer not paired here (a host a friend shares is never given) is left out",
            order.SequenceEqual(["m3-host", "m4-host", "m1-host", "desk-host"]), new { order });

        Singer first = new("m3-host"), second = new("m4-host");
        var free = await SingAsync([first, second]);
        Step("The first computer sings nothing now: it takes the song and no other computer is asked",
            free is { Host: "m3-host", Queued: false } && second.Looks == 0, new { free, asked = new { m3 = first.Looks, m4 = second.Looks } });

        Singer singing = new("m3-host") { Singing = true }, idle = new("m4-host");
        var passed = await SingAsync([singing, idle]);
        Step("The first computer sings another song: the next free one takes it at once",
            passed is { Host: "m4-host", Queued: false } && singing.Songs == 0 &&
            SingingPool.Pick(["m3-host", "m4-host"], States([singing, idle]), SongVoiceMatch.SoulX)?.HostId == "m4-host",
            new { passed, verdicts = new[] { SingingPool.Verdict("m3-host", singing.Look(cancellation).Result, SongVoiceMatch.SoulX) } });

        Singer soulOnly = new("m3-host"), vevo = new("m4-host") { Vevo = true };
        var matched = await SingAsync([soulOnly, vevo], SongVoiceMatch.VevoSing);
        Step("A VevoSing song passes over a computer without VevoSing", matched.Host == "m4-host" && soulOnly.Songs == 0,
            new { matched, verdict = SingingPool.Verdict("m3-host", soulOnly.Look(cancellation).Result, SongVoiceMatch.VevoSing) });

        var nowhere = await SingAsync([new Singer("m3-host")], SongVoiceMatch.VevoSing);
        Step("No computer has VevoSing: the song fails with singing.voice_match_unavailable (the conversation sings with SoulX-Singer instead)",
            nowhere.Failure == SongErrorCodes.VoiceMatchUnavailable, nowhere);

        Singer off = new("m3-host") { Down = true }, plain = new("m1-host") { Sings = false }, on = new("m4-host");
        var around = await SingAsync([off, plain, on]);
        Step("A computer that doesn't answer and one without Singing are passed over", around.Host == "m4-host", around);

        Singer longLine = new("m3-host") { Singing = true, Waiting = 2 }, shortLine = new("m4-host") { Singing = true };
        var line = await SingAsync([longLine, shortLine]);
        Step("Every computer is busy: the song waits in line on the one with the fewest songs before it",
            line is { Host: "m4-host", Queued: true, Ahead: 1 } && longLine.Songs == 0 &&
            SingingPool.Pick(["m3-host", "m4-host"], States([longLine, shortLine]), SongVoiceMatch.SoulX) == ("m4-host", 1), line);

        Singer full = new("m3-host") { Singing = true, Waiting = 4 }, fuller = new("m4-host") { Singing = true, Waiting = 4 };
        var refused = await SingAsync([full, fuller]);
        Step("Every line is full: singing.busy, and no song is made", refused.Failure == SongErrorCodes.Busy && full.Songs + fuller.Songs == 0,
            refused);

        Singer failing = new("m3-host") { FailsWith = SongErrorCodes.Failed }, spare = new("m4-host");
        var failed = await SingAsync([failing, spare]);
        Step("A song that fails on its computer (song.failed) is not made again elsewhere",
            failed.Failure == SongErrorCodes.Failed && spare.Looks == 0, failed);

        Singer dropping = new("m3-host") { StopsMidSong = true }, steady = new("m4-host");
        var moved = await SingAsync([dropping, steady]);
        Step("A computer that stops answering during the song: the song is made again on the next",
            moved.Host == "m4-host" && dropping.Songs == 1 && steady.Songs == 1, moved);

        return new { ok, fixture = "simulated singing computers (NOT real hosts or models)", steps };
    }
}
