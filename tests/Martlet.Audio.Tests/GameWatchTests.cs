namespace Martlet.Audio.Tests;

// The game watch behind the While gaming plan (docs/RECOMMENDATION_DESIGN.md, stage 2).
public sealed class GameWatchTests
{
    private sealed class Apps : IPcActivitySource
    {
        internal string Front { get; set; } = "browser";
        internal int Looks { get; private set; }
        internal bool Disposed { get; private set; }
        internal bool Fail { get; set; }

        public IReadOnlyList<PcAppLevel> Levels()
        {
            if (Fail) throw new InvalidOperationException("an audio device went away");
            Looks++;
            return [new("chrome", 0.2f), new("eldenring", Front == "game" ? 0.3f : 0f)];
        }

        public IReadOnlyList<PcAppFacts> Facts(IReadOnlyCollection<string> apps) =>
        [
            new("chrome", Titles: ["Lofi beats - YouTube - Google Chrome"], Foreground: Front == "browser"),
            new("eldenring", @"D:\SteamLibrary\steamapps\common\ELDEN RING\Game\eldenring.exe", ["ELDEN RING\u2122"],
                Foreground: Front == "game", FullScreen: Front == "game", Gpu: Front == "game" ? 90 : 5)
        ];

        public void Dispose() => Disposed = true;
    }

    [Fact]
    public void A_game_is_found_held_through_a_switch_and_ends_after_the_hold()
    {
        var clock = new ManualTime();
        var apps = new Apps();
        using var watch = new GameWatch(() => apps, clock);
        var changes = new List<string?>();
        watch.Changed += changes.Add;
        watch.On = true;
        clock.Advance(TimeSpan.Zero);
        Assert.Null(watch.Game);

        apps.Front = "game";
        clock.Advance(GameWatch.Every);
        Assert.Equal("ELDEN RING", watch.Game);

        apps.Front = "browser";
        clock.Advance(TimeSpan.FromSeconds(60));
        Assert.Equal("ELDEN RING", watch.Game);

        clock.Advance(TimeSpan.FromSeconds(33));
        Assert.Null(watch.Game);
        Assert.Equal(["ELDEN RING", null], changes);
        Assert.True(apps.Looks >= 3);
    }

    [Fact]
    public void Turning_it_off_forgets_the_game_and_closes_the_source()
    {
        var apps = new Apps { Front = "game" };
        using var watch = new GameWatch(() => apps, new ManualTime(), manual: true) { On = true };
        watch.Look();
        Assert.Equal("ELDEN RING", watch.Game);
        string? last = "none";
        watch.Changed += game => last = game;
        watch.On = false;
        Assert.Null(watch.Game);
        Assert.Null(last);
        Assert.True(apps.Disposed);
        watch.Look();
        Assert.Null(watch.Game);
    }

    [Fact]
    public void A_failing_source_keeps_the_game_until_its_hold_ends()
    {
        var clock = new ManualTime();
        var apps = new Apps { Front = "game" };
        using var watch = new GameWatch(() => apps, clock, manual: true) { On = true };
        watch.Look();
        apps.Fail = true;
        clock.Advance(TimeSpan.FromSeconds(30));
        watch.Look();
        Assert.Equal("ELDEN RING", watch.Game);
        Assert.Contains("audio device", watch.Problem);
    }
}
