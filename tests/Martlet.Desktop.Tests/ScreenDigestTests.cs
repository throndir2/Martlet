using Martlet.Conversation;
using Martlet.Core.Settings;
using Martlet.Core.Tests;
using Martlet.Desktop;
using Martlet.Providers;

namespace Martlet.Desktop.Tests;

public sealed class ScreenDigestTests
{
    private static byte[] Solid(int width, int height, byte blue, byte green, byte red)
    {
        var pixels = new byte[width * height * 4];
        for (var i = 0; i < pixels.Length; i += 4) { pixels[i] = blue; pixels[i + 1] = green; pixels[i + 2] = red; pixels[i + 3] = 255; }
        return pixels;
    }

    private sealed class FakeThinker : IScreenDigestThinker
    {
        public bool CanSee { get; set; } = true;
        public List<ScreenDigestJob> Jobs { get; } = [];
        public TaskCompletionSource<string?>? Answer { get; set; }
        public string? Reply { get; set; } = "They switched from VS Code to a boss fight; health dropped to 20%.";
        public Exception? Failure { get; set; }

        public async Task<string?> DigestAsync(ScreenDigestJob job, CancellationToken cancellation)
        {
            Jobs.Add(job);
            if (Failure is { } failure) throw failure;
            if (Answer is { } answer) return await answer.Task.WaitAsync(cancellation);
            return Reply;
        }
    }

    private sealed class FakeBoard : IScreenDigestBoard
    {
        public List<(string Text, DateTimeOffset At, TimeSpan MaximumAge)> Posts { get; } = [];
        public int Cleared { get; private set; }
        public void Post(string text, DateTimeOffset at, TimeSpan maximumAge) => Posts.Add((text, at, maximumAge));
        public void Clear() => Cleared++;
    }

    private static void See(ScreenDigester digester, string title, double change, byte red, string? text = null) =>
        digester.Observe(64, 36, title, change, text, () => Solid(64, 36, 0, 0, red));

    [Fact]
    public void RingKeepsOnlyChangedPicturesDownscaledAndBounded()
    {
        var ring = new ScreenDigestRing(new ScreenDigestTiming { PanelEdge = 32, MaximumFrames = 3, Window = TimeSpan.FromSeconds(20) });
        var at = new DateTimeOffset(2026, 10, 7, 9, 0, 0, TimeSpan.Zero);
        var read = 0;
        Func<byte[]?> pixels = () => { read++; return Solid(128, 64, 10, 20, 30); };

        Assert.True(ring.Observe(128, 64, "Code", 1.0, at, null, pixels));
        // Hardly changed, same window: skipped without reading the pixels.
        Assert.False(ring.Observe(128, 64, "Code", 0.001, at.AddSeconds(3), null, pixels));
        Assert.Equal(1, read);
        // Same picture, another window: kept.
        Assert.True(ring.Observe(128, 64, "Game", 0.001, at.AddSeconds(6), null, pixels));
        Assert.True(ring.Observe(128, 64, "Game", 0.5, at.AddSeconds(9), "HP 20", pixels));
        Assert.True(ring.Observe(128, 64, "Game", 0.5, at.AddSeconds(12), null, pixels));
        Assert.Equal(3, ring.Count);
        Assert.Equal(32, ring.Frames[0].Width);
        Assert.Equal(16, ring.Frames[0].Height);
        Assert.Equal(new byte[] { 10, 20, 30, 255 }, ring.Frames[0].CopyPixels()![..4]);
        Assert.Equal("HP 20", ring.Frames[1].Text);

        // Old pictures go, but the newest stays: it is still what the screen shows.
        ring.Prune(at.AddMinutes(5));
        Assert.Equal(1, ring.Count);
        var newest = ring.Frames[0];
        ring.Clear();
        Assert.Equal(0, ring.Count);
        Assert.Null(newest.CopyPixels());
    }

    [Fact]
    public void SheetPicksOldestAndNewestAndLaysThemOut()
    {
        var at = DateTimeOffset.UnixEpoch;
        var frames = Enumerable.Range(0, 7).Select(i => new ScreenDigestFrame(Solid(20, 10, 0, 0, (byte)(i * 30)), 20, 10, $"w{i}",
            at.AddSeconds(i * 3), null)).ToArray();
        var picked = ScreenDigestSheet.Pick(frames, 4);
        Assert.Equal(new[] { "w0", "w2", "w4", "w6" }, picked.Select(f => f.Title).ToArray());
        Assert.Equal(2, ScreenDigestSheet.Pick(frames[..2], 4).Count);

        var four = ScreenDigestSheet.Compose(picked)!.Value;
        Assert.Equal((44, 24), (four.Width, four.Height));
        // Bottom right holds the newest picture.
        var o = ((14 + 5) * 44 + 24 + 10) * 4;
        Assert.Equal(180, four.Pixels[o + 2]);
        var two = ScreenDigestSheet.Compose(picked.Take(2).ToArray())!.Value;
        Assert.Equal((44, 10), (two.Width, two.Height));
        Assert.Equal(new[] { "left", "right" }, ScreenDigestSheet.Places(2));

        var image = ScreenDigestSheet.Encode(four.Pixels, four.Width, four.Height);
        Assert.Equal(ImageMediaType.Jpeg, image.MediaType);
        Assert.Equal((44, 24), (image.Width, image.Height));
    }

    [Fact]
    public void MessageNamesThePicturesAndTheirText()
    {
        var now = new DateTimeOffset(2026, 10, 7, 9, 0, 20, TimeSpan.Zero);
        var picked = new[]
        {
            new ScreenDigestFrame([], 1, 1, "Program.cs - Visual Studio Code", now.AddSeconds(-18), "dotnet build"),
            new ScreenDigestFrame([], 1, 1, "Elden \"Ring\"", now, "HP 20%")
        };
        var message = ScreenDigestPrompt.Message(null, picked, now)!;
        Assert.Contains("2 small screenshots", message);
        Assert.Contains("last 18 seconds", message);
        Assert.Contains("left 18 s ago (window \"Program.cs - Visual Studio Code\")", message);
        Assert.Contains("right just now (window \"Elden Ring\")", message);
        Assert.Contains("[pass]", message);
        Assert.Contains("\nleft: dotnet build\nright: HP 20%", message);

        var emptied = new PromptSettings { Overrides = new Dictionary<string, string> { [PromptCatalog.ScreenDigest] = " " } };
        Assert.Null(ScreenDigestPrompt.Message(emptied, picked, now));
    }

    [Theory]
    [InlineData("[pass]", null)]
    [InlineData("  ", null)]
    [InlineData("Note: They opened Discord. [seen: a chat app]", "They opened Discord.")]
    [InlineData("\"They switched games.\"", "They switched games.")]
    [InlineData("- Health dropped to 20%.\n- A boss appeared.\n- Third line.", "Health dropped to 20%. A boss appeared.")]
    public void ParseKeepsOneOrTwoCleanLines(string reply, string? expected) => Assert.Equal(expected, ScreenDigestPrompt.Parse(reply));

    [Fact]
    public void ParseBoundsTheLength() =>
        Assert.Equal(ScreenDigestPrompt.MaximumLength, ScreenDigestPrompt.Parse(new string('a', 500))!.Length);

    [Fact]
    public async Task ChangedPicturesBecomeOneNoteOnTheBoard()
    {
        var clock = new ManualClock();
        var thinker = new FakeThinker();
        var board = new FakeBoard();
        var digester = new ScreenDigester(thinker, board, clock);
        See(digester, "Code", 1, 10);
        Assert.Equal(0, digester.Status.Frames);
        digester.Turn(true);
        See(digester, "Code", 1, 10);
        Assert.Null(digester.Tick());
        clock.Advance(TimeSpan.FromSeconds(3));
        See(digester, "Game", 0.4, 200);
        await digester.Tick()!;

        var job = Assert.Single(thinker.Jobs);
        Assert.Equal(2, job.Frames);
        Assert.Equal("changes", job.Reason);
        Assert.Equal(ImageMediaType.Jpeg, job.Picture.MediaType);
        var post = Assert.Single(board.Posts);
        Assert.Equal("Screen over the last 3 s: " + thinker.Reply, post.Text);
        Assert.Equal(TimeSpan.FromSeconds(45), post.MaximumAge);
        var status = digester.Status;
        Assert.Equal((1, 1, thinker.Reply), (status.Jobs, status.Posted, status.LastText));
        Assert.StartsWith("Screen summary: 2 pictures kept; last summary just now", ScreenDigester.Line(status));

        // Nothing new since: no job. A new picture waits for the spacing.
        clock.Advance(TimeSpan.FromSeconds(20));
        Assert.Null(digester.Tick());
        See(digester, "Game", 0.4, 90);
        clock.Advance(TimeSpan.FromSeconds(1));
        await digester.Tick()!;
        Assert.Equal(2, thinker.Jobs.Count);
    }

    [Fact]
    public async Task SpeakingAsksOnlyWhenNoFreshSummaryIsThere()
    {
        var clock = new ManualClock();
        var thinker = new FakeThinker();
        var board = new FakeBoard();
        var digester = new ScreenDigester(thinker, board, clock, new ScreenDigestTiming { Every = TimeSpan.FromMinutes(5) });
        digester.Turn(true);
        See(digester, "Code", 1, 10);
        clock.Advance(TimeSpan.FromSeconds(3));
        See(digester, "Game", 1, 200);
        await digester.UserSpeaking()!;
        Assert.Equal("speech", thinker.Jobs[0].Reason);

        clock.Advance(TimeSpan.FromSeconds(5));
        See(digester, "Game", 1, 100);
        // The summary is still fresh.
        Assert.Null(digester.UserSpeaking());
        clock.Advance(TimeSpan.FromSeconds(10));
        await digester.UserSpeaking()!;
        Assert.Equal(2, thinker.Jobs.Count);
        // Every (5 minutes) holds the timed summaries back.
        See(digester, "Game", 1, 50);
        Assert.Null(digester.Tick());
    }

    [Fact]
    public async Task AStaleSummaryIsDroppedAndNothingWaits()
    {
        var clock = new ManualClock();
        var thinker = new FakeThinker { Answer = new() };
        var board = new FakeBoard();
        var digester = new ScreenDigester(thinker, board, clock);
        digester.Turn(true);
        See(digester, "Code", 1, 10);
        See(digester, "Game", 1, 200);
        var job = digester.Tick()!;
        for (var i = 0; thinker.Jobs.Count == 0; i++) { Assert.True(i < 2000, "first job never asked"); await Task.Delay(5); }
        Assert.True(digester.Status.Running);
        Assert.Null(digester.Tick());
        // An answer that comes after the stale limit is dropped.
        clock.Advance(TimeSpan.FromSeconds(25), fireTimers: false);
        thinker.Answer.SetResult("Too late.");
        await job.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Empty(board.Posts);
        Assert.Equal(1, digester.Status.Dropped);

        // A job still running at the stale limit is stopped.
        thinker.Answer = new();
        See(digester, "Game", 1, 90);
        var second = digester.Tick()!;
        for (var i = 0; thinker.Jobs.Count < 2; i++) { Assert.True(i < 2000, "second job never asked"); await Task.Delay(5); }
        clock.Advance(TimeSpan.FromSeconds(21));
        await second.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(2, digester.Status.Dropped);
        Assert.Empty(board.Posts);
    }

    [Fact]
    public async Task OffOrBlindOrFailedMeansNoJobs()
    {
        var clock = new ManualClock();
        var thinker = new FakeThinker { CanSee = false };
        var board = new FakeBoard();
        var digester = new ScreenDigester(thinker, board, clock);
        digester.Turn(true);
        See(digester, "Code", 1, 10);
        See(digester, "Game", 1, 200);
        Assert.Null(digester.Tick());
        Assert.False(digester.Status.On);
        Assert.Equal("", ScreenDigester.Line(digester.Status));

        thinker.CanSee = true;
        See(digester, "Code", 1, 10);
        See(digester, "Game", 1, 200);
        thinker.Failure = new InvalidOperationException("no model");
        await digester.Tick()!;
        Assert.Equal(1, digester.Status.Failed);
        Assert.Contains("failed", ScreenDigester.Line(digester.Status));
        See(digester, "Code", 1, 30);
        clock.Advance(TimeSpan.FromSeconds(30));
        Assert.Null(digester.Tick());
        clock.Advance(TimeSpan.FromSeconds(31));
        thinker.Failure = null;
        See(digester, "Game", 1, 60);
        await digester.Tick()!;
        Assert.Equal(1, digester.Status.Posted);

        // Turning it off lets every picture go and takes the note off the board.
        digester.Turn(false);
        Assert.Equal(0, digester.Status.Frames);
        Assert.Equal(1, board.Cleared);
        See(digester, "Code", 1, 10);
        Assert.Equal(0, digester.Status.Frames);
    }

    [Fact]
    public async Task ThePoolRunsSummariesOnlyOnAMemberThatSees()
    {
        BackgroundPlace text = new("host:a", "a"), eyes = new("host:b", "b") { Can = ThinkingCapability.Text | ThinkingCapability.Vision };
        var members = new List<BackgroundPlace> { text };
        ThinkingJob? ran = null;
        string? where = null;
        var board = new ThinkingJobBoard(new BackgroundPlaces(), () => members, (m, job, _) =>
        {
            (ran, where) = (job, m.Id);
            return Task.FromResult(ThinkingAnswer.Done("They opened a game."));
        });
        var thinker = new PoolScreenDigestThinker(() => new ThinkingPool(board));
        var sheet = ScreenDigestSheet.Encode(Solid(16, 8, 1, 2, 3), 16, 8);
        var job = new ScreenDigestJob("What changed?", sheet, 2, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, "changes");

        Assert.False(thinker.CanSee);
        await Assert.ThrowsAsync<InvalidOperationException>(() => thinker.DigestAsync(job, CancellationToken.None));
        members.Add(eyes);
        Assert.True(thinker.CanSee);
        Assert.Equal("They opened a game.", await thinker.DigestAsync(job, CancellationToken.None));
        Assert.Equal("host:b", where);
        Assert.Equal(ThinkingJobKind.Digest, ran!.Kind);
        Assert.Same(sheet, ran.Image);
        Assert.True(ran.DropWhenStale);
        Assert.True(ran.Timeout < new ScreenDigestTiming().Stale);
    }

    [Fact]
    public void ScreenSummaryIsOnByDefaultAndSaved()
    {
        Assert.True(new TalkPreferences().ScreenSummary);
        var directory = Directory.CreateTempSubdirectory("martlet-digest-").FullName;
        try
        {
            Assert.True((new TalkPreferences() with { ScreenSummary = false }).Save(directory));
            Assert.False(TalkPreferences.Load(directory).ScreenSummary);
        }
        finally { Directory.Delete(directory, true); }
    }
}
