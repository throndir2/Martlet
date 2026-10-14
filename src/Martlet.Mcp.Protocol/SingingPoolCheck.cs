using System.Net.Http;
using Martlet.Core.Cluster;
using Martlet.Core.Singing;

namespace Martlet.Mcp;

/// <summary>singing_pool_check: rehearses the production singing pools (<see cref="SingingPool"/> through
/// <see cref="WorkQueue"/>, what the desktop's SongClient runs: a song's music on the singing pool, then its voice match on the
/// voice match pool) with simulated singing computers that answer the singing service's status (state, line, voice matches,
/// the job kinds it makes) and make a job as a host does: a full line refuses with singing.busy, a computer that is off doesn't
/// answer. Also puts a simulated song's music and voice match together (<see cref="SongVoice"/>). NOT real hosts or models;
/// nothing leaves the process.</summary>
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
        /// <summary>Its Martlet makes a song's music and voice match apart (false: an older Martlet, whole songs only).</summary>
        public bool Splits { get; init; } = true;
        public int Looks;
        public int Songs;

        public Task<SingerState?> Look(CancellationToken token)
        {
            Interlocked.Increment(ref Looks);
            if (Down) throw new HttpRequestException($"{Id} didn't answer.");
            return Task.FromResult(Sings ? new SingerState(Singing ? "busy" : "ready", Waiting,
                Vevo ? [SongVoiceMatch.SoulX, SongVoiceMatch.VevoSing] : [SongVoiceMatch.SoulX])
                { Kinds = Splits ? ["song", "music", "match"] : ["song"] } : null);
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

    // One job on a simulated pool: a song's music, or (match) its voice match.
    private static async Task<Outcome> SingAsync(IReadOnlyList<Singer> pool, bool match = false)
    {
        try
        {
            var sung = await SingingPool.RunAsync(new WorkQueue(), match ? SingingPool.MatchLane : SingingPool.Lane, pool, s => s.Id,
                (s, t) => s.Look(t), match ? SingingPool.MatchRefusal : SingingPool.MusicRefusal, (s, _, t) => s.Sing(t), null,
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

        var list = new PoolList
        {
            Area = PoolAreas.Singing.Id,
            Members = [PoolMember.ThisPc(), PoolMember.Computer("m1-host") with { Off = true }, PoolMember.Gpu("m4-host", 2),
                PoolMember.Computer("friend-host"), PoolMember.Computer("m3-host") with { OnlyFor = ["desk-9"] }]
        };
        var listed = SingingPool.Members(list, "desk-2", "m3-host", "desk-host", ["m1-host", "m3-host", "m4-host", "desk-host"], []);
        var empty = SingingPool.Members(new PoolList { Area = PoolAreas.Singing.Id }, "desk-2", "m3-host", "desk-host", ["m3-host"], []);
        Step("A singing pool list (pools.json) wins: its members in order, this PC as its own host service, a card as its computer; " +
            "a member turned off, not paired here (a friend's host) or kept for another companion PC is left out; an empty list is off",
            listed.Select(m => m.HostId).SequenceEqual(["desk-host", "m4-host"]) && listed[1].Member.Key == "host:m4-host#gpu2" && empty.Count == 0,
            new { listed = listed.Select(m => new { m.Member.Key, m.HostId }), emptyListMembers = empty.Count });

        Singer first = new("m3-host"), second = new("m4-host");
        var free = await SingAsync([first, second]);
        Step("The first computer sings nothing now: it takes the song and no other computer is asked",
            free is { Host: "m3-host", Queued: false } && second.Looks == 0, new { free, asked = new { m3 = first.Looks, m4 = second.Looks } });

        Singer singing = new("m3-host") { Singing = true }, idle = new("m4-host");
        var passed = await SingAsync([singing, idle]);
        Step("The first computer sings another song: the next free one takes it at once",
            passed is { Host: "m4-host", Queued: false } && singing.Songs == 0 &&
            SingingPool.Pick(["m3-host", "m4-host"], States([singing, idle]))?.HostId == "m4-host",
            new { passed, verdicts = new[] { SingingPool.Verdict("m3-host", singing.Look(cancellation).Result) } });

        var voiceList = new PoolList
        {
            Area = PoolAreas.VoiceMatch.Id,
            Members = [SingingPool.WithVoiceMatch(PoolMember.Computer("m4-host"), SongVoiceMatch.VevoSing), PoolMember.ThisPc()]
        };
        var voices = SingingPool.Members(voiceList, "desk-2", null, "desk-host", ["m3-host", "m4-host", "desk-host"], [], PoolAreas.VoiceMatch);
        Step("The voice match has its own list: other computers than the music's, in its own order, each with its own model " +
            "(SoulX-Singer unless it chooses VevoSing); a music member writes in high quality unless it chooses fast",
            voices.Select(m => m.HostId).SequenceEqual(["m4-host", "desk-host"]) &&
            SingingPool.VoiceMatch(voices[0].Member) == SongVoiceMatch.VevoSing && SingingPool.VoiceMatch(voices[1].Member) == SongVoiceMatch.SoulX &&
            SingingPool.Quality(PoolMember.ThisPc()) == SongQuality.HighQuality &&
            SingingPool.Quality(SingingPool.WithQuality(PoolMember.ThisPc(), SongQuality.Fast)) == SongQuality.Fast,
            new { voices = voices.Select(m => new { m.Member.Key, m.HostId, model = SingingPool.VoiceMatch(m.Member).ToString() }) });

        Singer older = new("m3-host") { Splits = false }, newer = new("m4-host");
        var apart = await SingAsync([older, newer], match: true);
        var olderMusic = await SingAsync([new Singer("m3-host") { Splits = false }]);
        Step("A voice match passes over a computer whose Martlet can't make it apart; that computer still writes music (whole songs)",
            apart.Host == "m4-host" && older.Songs == 0 && olderMusic.Host == "m3-host",
            new { apart, olderMusic, verdict = SingingPool.Verdict("m3-host", older.Look(cancellation).Result, match: true) });

        Singer soulOnly = new("m3-host"), vevo = new("m4-host") { Vevo = true };
        var vevoMember = SingingPool.WithVoiceMatch(PoolMember.Computer("m3-host"), SongVoiceMatch.VevoSing);
        var soulWith = SingingPool.MatchWith(vevoMember, soulOnly.Look(cancellation).Result);
        var vevoWith = SingingPool.MatchWith(vevoMember, vevo.Look(cancellation).Result);
        Step("A member that chose VevoSing matches with VevoSing where it is set up, and with SoulX-Singer where it isn't yet",
            soulWith == SongVoiceMatch.SoulX && vevoWith == SongVoiceMatch.VevoSing, new { soulWith = soulWith.ToString(), vevoWith = vevoWith.ToString() });

        var music = FixtureMusic(frames: 4_800, backing: 30_000, vocals: 0);
        var voiced = SongVoice.WithVoice(music, new SongVoiceMatchResult("match-1", Mono(4_800, 10_000), "fixture-converter",
            SongVoiceMatch.SoulX, true, [new SongStageTiming(SongStage.MatchingVoice, TimeSpan.FromSeconds(1))]));
        var peak = MaxAbs(voiced.Mix.Pcm16.Span);
        Step("The music and the voice match are put together on this PC: mix = backing + matched vocals, the three tracks scaled " +
            "together when the mix would clip, and the song names its voice match",
            voiced.Mix.Frames == 4_800 && voiced.Vocals.Frames == 4_800 && peak <= (int)(SongVoice.Peak * short.MaxValue) + 1 && peak > 30_000 &&
            voiced.Engine.VoiceMatch == SongVoiceMatch.SoulX && voiced.Engine.Converter == "fixture-converter" &&
            music.Engine.VoiceMatch is null,
            new { peak, converter = voiced.Engine.Converter, stages = voiced.StageTimings.Select(t => t.Stage.ToString()) });

        Singer off = new("m3-host") { Down = true }, plain = new("m1-host") { Sings = false }, on = new("m4-host");
        var around = await SingAsync([off, plain, on]);
        Step("A computer that doesn't answer and one without Singing are passed over", around.Host == "m4-host", around);

        Singer longLine = new("m3-host") { Singing = true, Waiting = 2 }, shortLine = new("m4-host") { Singing = true };
        var line = await SingAsync([longLine, shortLine]);
        Step("Every computer is busy: the song waits in line on the one with the fewest songs before it",
            line is { Host: "m4-host", Queued: true, Ahead: 1 } && longLine.Songs == 0 &&
            SingingPool.Pick(["m3-host", "m4-host"], States([longLine, shortLine])) == ("m4-host", 1), line);

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

    // A song's music as a music job returns it (no voice match): constant stereo backing and mono vocals.
    private static SongResult FixtureMusic(int frames, short backing, short vocals) => new()
    {
        JobId = "music-1", VoiceId = "fixture-voice",
        Mix = new SongTrack(SongTrackKind.Mix, SongTrack.SampleRateHz, 2, Constant(frames * 2, (short)(backing + vocals))),
        Vocals = Mono(frames, vocals),
        Backing = new SongTrack(SongTrackKind.Backing, SongTrack.SampleRateHz, 2, Constant(frames * 2, backing)),
        Engine = new SongEngineIdentity("fixture-generator", "fixture-separator", "", SongQuality.HighQuality, null, true),
        Seed = 1, StageTimings = [new SongStageTiming(SongStage.WritingMusic, TimeSpan.FromSeconds(2))]
    };

    private static SongTrack Mono(int frames, short value) => new(SongTrackKind.Vocals, SongTrack.SampleRateHz, 1, Constant(frames, value));

    private static byte[] Constant(int samples, short value)
    {
        var pcm = new byte[samples * 2];
        for (var i = 0; i < samples; i++) System.Buffers.Binary.BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan(2 * i), value);
        return pcm;
    }

    private static int MaxAbs(ReadOnlySpan<byte> pcm)
    {
        var peak = 0;
        for (var i = 0; i + 1 < pcm.Length; i += 2)
            peak = Math.Max(peak, Math.Abs((int)System.Buffers.Binary.BinaryPrimitives.ReadInt16LittleEndian(pcm[i..])));
        return peak;
    }
}
