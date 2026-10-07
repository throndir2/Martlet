using System.Collections.Concurrent;
using Martlet.Avatar.Hosting;
using Martlet.Avatars;
using Xunit;

namespace Martlet.Desktop.Tests;

public sealed class CharacterStrokeTests
{
    private static CharacterTouch Hit(string drawable) => new(0.5, 0.2, [], [drawable], null, null, false, null, null);

    private static string? Zone(CharacterTouch touch) => touch.Drawables.FirstOrDefault();

    // A path along x (aspect 1) through the given points, one sample every stepMs, each hitting the zone named beside it.
    private static StrokeSample[] Path(int stepMs, params (double X, string? Zone)[] points) =>
        [.. points.Select((p, i) => new StrokeSample(p.X, 0.2, i * stepMs, p.Zone is null ? null : Hit(p.Zone)))];

    [Fact]
    public void A_stroke_lists_the_zones_it_crossed_in_order_and_its_main_zone()
    {
        var summary = CharacterStrokes.Summarize(Path(40, (0.40, "hair"), (0.42, "hair"), (0.44, "face"), (0.46, null), (0.48, "face"),
            (0.50, "face"), (0.52, "hair")), 1, Zone);
        Assert.Equal(["hair", "face", "hair"], summary.Zones);
        Assert.Equal(["hair", "face"], summary.Distinct);
        // A tie goes to the zone crossed first.
        Assert.Equal("hair", summary.Main);
        Assert.Equal(1, summary.Passes);
        Assert.Equal(6, summary.Hits);
        Assert.Equal(7, summary.Samples);
        Assert.Equal(240, summary.Ms);
        Assert.Equal(0.12, summary.Length, 3);
    }

    [Fact]
    public void Turning_back_counts_passes_but_jitter_does_not()
    {
        // Out and back twice: 4 passes. Wobbles smaller than the turn distance don't count.
        var back = Path(60, (0.40, "hair"), (0.45, "hair"), (0.50, "hair"), (0.45, "hair"), (0.40, "hair"), (0.45, "hair"), (0.50, "hair"),
            (0.45, "hair"), (0.40, "hair"));
        Assert.Equal(4, CharacterStrokes.Summarize(back, 1, Zone).Passes);
        var wobble = Path(60, (0.40, "hair"), (0.45, "hair"), (0.445, "hair"), (0.50, "hair"), (0.495, "hair"), (0.55, "hair"));
        Assert.Equal(1, CharacterStrokes.Summarize(wobble, 1, Zone).Passes);
        Assert.Equal("back and forth", (CharacterStrokes.Summarize(back, 1, Zone) with { Pace = "steady" }).Manner);
    }

    [Fact]
    public void Pace_is_slow_for_a_caress_quick_for_a_rub_or_fast_swipe_and_steady_between()
    {
        // 0.2 page heights over a second: a slow caress.
        var slow = CharacterStrokes.Summarize(Path(250, (0.40, "hair"), (0.45, "hair"), (0.50, "hair"), (0.55, "hair"), (0.60, "hair")), 1, Zone);
        Assert.Equal("slow", slow.Pace);
        Assert.Equal("slowly", slow.Manner);
        // 0.3 over 0.4 s: steady.
        var steady = CharacterStrokes.Summarize(Path(100, (0.30, "hair"), (0.375, "hair"), (0.45, "hair"), (0.525, "hair"), (0.60, "hair")), 1, Zone);
        Assert.Equal("steady", steady.Pace);
        Assert.Null(steady.Manner);
        // 0.5 over 0.2 s: a quick swipe.
        Assert.Equal("quick", CharacterStrokes.Summarize(Path(50, (0.2, "body"), (0.325, "body"), (0.45, "body"), (0.575, "body"), (0.7, "body")), 1, Zone).Pace);
        // Short back and forth, 4 passes in 0.48 s: a rub, quick though not far.
        var rub = CharacterStrokes.Summarize(Path(60, (0.40, "face"), (0.43, "face"), (0.46, "face"), (0.43, "face"), (0.40, "face"), (0.43, "face"),
            (0.46, "face"), (0.43, "face"), (0.40, "face")), 1, Zone);
        Assert.Equal(4, rub.Passes);
        Assert.Equal("quick", rub.Pace);
        Assert.Equal("quickly back and forth", rub.Manner);
    }

    [Fact]
    public void The_pages_aspect_makes_sideways_distance_count_in_page_heights()
    {
        var path = Path(500, (0.4, "hair"), (0.5, "hair"));
        Assert.Equal(0.1, CharacterStrokes.Summarize(path, 1, Zone).Length, 4);
        Assert.Equal(0.2, CharacterStrokes.Summarize(path, 2, Zone).Length, 4);
    }

    [Fact]
    public void The_tracker_reports_each_new_zone_at_once_and_the_summary_at_the_end()
    {
        var tracker = new CharacterStrokeTracker();
        var (entered, ended) = tracker.Add(new CharacterStroke(1, "move", 1, Path(40, (0.40, null), (0.42, "hair"), (0.44, "hair"))), Zone);
        Assert.Equal(["hair"], entered.Select(e => e.Zone));
        Assert.Null(ended);
        (entered, ended) = tracker.Add(new CharacterStroke(1, "move", 1,
            [new(0.46, 0.2, 120, Hit("hair")), new(0.48, 0.2, 160, Hit("face"))]), Zone);
        Assert.Equal(["face"], entered.Select(e => e.Zone));
        (entered, ended) = tracker.Add(new CharacterStroke(1, "end", 1, [new(0.50, 0.2, 200, Hit("face"))]), Zone);
        Assert.Empty(entered);
        Assert.NotNull(ended);
        Assert.Equal(["hair", "face"], ended.Zones);
        Assert.Equal(6, ended.Samples);
        Assert.Equal(0, tracker.Current);
        // A late batch of a stroke that ended is ignored; another stroke starts over.
        Assert.Empty(tracker.Add(new CharacterStroke(1, "move", 1, [new(0.5, 0.2, 240, Hit("body"))]), Zone).Entered);
        Assert.Equal(["body"], tracker.Add(new CharacterStroke(2, "move", 1, [new(0.5, 0.2, 0, Hit("body"))]), Zone).Entered.Select(e => e.Zone));
    }

    [Fact]
    public void Strokes_and_changes_cross_the_renderer_pipe_and_bad_ones_are_invalid()
    {
        var stroke = new CharacterStroke(3, "end", 1.5, [new(0.4, 0.2, 0, Hit("D_HAIR")), new(0.5, 0.2, 40)]);
        var read = RendererProtocol.Data<CharacterStroke>(RendererProtocol.Message("stroke", Guid.NewGuid(), stroke));
        Assert.True(read.IsValid);
        Assert.Equal("hair", read.Samples[0].Touch!.CoarseZone);
        Assert.Null(read.Samples[1].Touch);
        Assert.False((stroke with { Phase = "begin" }).IsValid);
        Assert.False((stroke with { Id = 0 }).IsValid);
        Assert.False((stroke with { Samples = [.. Enumerable.Range(0, 65).Select(i => new StrokeSample(0.5, 0.5, i))] }).IsValid);
        Assert.False((stroke with { Samples = [new(double.NaN, 0.5, 0)] }).IsValid);

        var change = new RendererPhysical("moved", -300, 20, 1920, "DISPLAY1", "DISPLAY2", 420, 420);
        var moved = RendererProtocol.Data<RendererPhysical>(RendererProtocol.Message("physical", Guid.NewGuid(), change));
        Assert.True(moved.IsValid);
        Assert.True(moved.OtherScreen);
        Assert.False((change with { Kind = "spun" }).IsValid);
        Assert.False((change with { ToScreen = "DISPLAY\n2" }).IsValid);
    }

    private static OverlayState At(double left, double top = 100, string screen = "DISPLAY1", double scale = 420, double zoom = 1,
        double x = 0, double y = 0) => new(left, top, screen, scale, zoom, x, y, 1920);

    [Fact]
    public void A_drag_or_a_spin_of_the_wheel_settles_into_one_change()
    {
        var tracker = new OverlayChangeTracker(800);
        tracker.Note("moved", At(100), At(110), 0);
        tracker.Note("moved", At(110), At(160), 300);
        tracker.Note("zoomed", At(160), At(160, scale: 460, zoom: 1.21), 400, (0.5, 0.2));
        tracker.Note("zoomed", At(160, scale: 460), At(160, scale: 560, zoom: 1.46), 500, null);
        Assert.Empty(tracker.Due(1000));
        var due = tracker.Due(1100);
        Assert.Equal("moved", Assert.Single(due).Kind);
        Assert.Equal(100, due[0].Before.Left);
        Assert.Equal(160, due[0].After.Left);
        var zoom = Assert.Single(tracker.Due(1300));
        Assert.Equal(420, zoom.Before.Scale);
        Assert.Equal(560, zoom.After.Scale);
        Assert.Equal((0.5, 0.2), zoom.Anchor);
        Assert.False(tracker.Pending);
    }

    [Fact]
    public void Going_home_or_resetting_the_zoom_replaces_what_came_before_and_no_change_is_dropped()
    {
        var tracker = new OverlayChangeTracker(800);
        tracker.Note("moved", At(100), At(300), 0);
        tracker.Note("zoomed", At(300), At(300, scale: 500), 100);
        tracker.Note("home", At(300, scale: 500), At(1400), 200);
        Assert.Equal(["home"], tracker.Due(5000).Select(c => c.Kind));

        tracker.Note("zoomed", At(0), At(0, scale: 600, zoom: 2), 0);
        tracker.Note("panned", At(0, zoom: 2), At(0, zoom: 2, x: 0.3), 50);
        tracker.Note("zoom_reset", At(0, scale: 600, zoom: 2), At(0), 100);
        Assert.Equal(["zoom_reset"], tracker.Due(5000).Select(c => c.Kind));

        // Dragged away and back: nothing changed, so nothing is said.
        tracker.Note("moved", At(100), At(400), 0);
        tracker.Note("moved", At(400), At(101), 100);
        Assert.Empty(tracker.Due(5000));
        // Another monitor counts even at the same coordinates.
        tracker.Note("moved", At(100), At(100, screen: "DISPLAY2"), 0);
        Assert.Single(tracker.Due(5000));
    }

    [Theory]
    [InlineData(-50, 0, "DISPLAY1", "DISPLAY1", "a little to the left")]
    [InlineData(400, 10, "DISPLAY1", "DISPLAY1", "to the right")]
    [InlineData(0, -500, "DISPLAY1", "DISPLAY1", "up")]
    [InlineData(900, 900, "DISPLAY1", "DISPLAY1", "a long way down and to the right")]
    [InlineData(-200, 0, "DISPLAY1", "DISPLAY2", "to their other monitor")]
    public void A_move_is_said_by_how_far_and_which_way_or_another_monitor(double dx, double dy, string from, string to, string detail) =>
        Assert.Equal(detail, CharacterPhysicalWords.MoveDetail(new RendererPhysical("moved", dx, dy, 1920, from, to)));

    [Fact]
    public void A_zoom_says_what_it_closed_in_on()
    {
        var zoom = new RendererPhysical("zoomed", ZoomFrom: 420, ZoomTo: 900);
        Assert.Equal("in on your face", CharacterPhysicalWords.ZoomDetail(zoom, "your face"));
        Assert.Equal("in", CharacterPhysicalWords.ZoomDetail(zoom, null));
        Assert.Equal("out", CharacterPhysicalWords.ZoomDetail(zoom with { ZoomTo = 300 }, "your face"));
        Assert.Equal(("Zoomed", "in on your face"), CharacterPhysicalWords.Describe(zoom, "your face"));
        Assert.Equal(("Zoomed", "back out to normal"), CharacterPhysicalWords.Describe(zoom with { Kind = "zoom_reset" }, null));
        Assert.Equal(("Moved", "back to your usual spot"), CharacterPhysicalWords.Describe(new("home"), null));
        Assert.Equal(("Moved", "to their other monitor"), CharacterPhysicalWords.Describe(new("moved", 10, 0, 1920, "DISPLAY1", "DISPLAY2"), null));
        Assert.Equal(("Panned", "to your hair"), CharacterPhysicalWords.Describe(new("panned"), "your hair"));
    }

    [Fact]
    public void A_stroke_is_recorded_once_for_each_pass_with_its_pace()
    {
        var summary = new StrokeSummary(["hair"], "hair", 1600, 0.4, 0.25, "slow", 4, 30, 32);
        Assert.Equal(("slowly", 4), CharacterPhysicalWords.Stroke(summary));
        Assert.Equal(((string?)null, 8), CharacterPhysicalWords.Stroke(summary with { Pace = "steady", Passes = 20 }));
        Assert.Equal(("quickly", 1), CharacterPhysicalWords.Stroke(summary with { Pace = "quick", Passes = 1 }));
    }

    [Fact]
    public async Task The_avatar_passes_on_strokes_and_changes_only_while_the_character_shows()
    {
        using var scope = new AvatarHostingTests.Scope();
        var renderer = new Renderer();
        await using var avatar = new AvatarController(createRenderer: () => renderer, allowControlledClock: true);
        var strokes = new ConcurrentQueue<CharacterStroke>();
        var changes = new ConcurrentQueue<RendererPhysical>();
        avatar.Stroked += strokes.Enqueue;
        avatar.PhysicalChanged += changes.Enqueue;
        await avatar.ShowAsync(scope.Profile() with { LipSync = AvatarLipSync.Loudness }, default);
        renderer.Stroke(new CharacterStroke(1, "end", 1, [new(0.5, 0.2, 0)]));
        renderer.Change(new RendererPhysical("zoomed", ZoomFrom: 1, ZoomTo: 2));
        Assert.Single(strokes);
        Assert.Single(changes);
        await avatar.StopAsync();
        renderer.Stroke(new CharacterStroke(2, "end", 1, [new(0.5, 0.2, 0)]));
        Assert.Single(strokes);
    }

    private sealed class Renderer : IAvatarRenderer
    {
        private readonly Guid activation = Guid.NewGuid();
        private readonly TaskCompletionSource exited = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public RendererCapabilities? Capabilities { get; private set; }
        public bool HasExited { get; private set; }
        public Task Exited => exited.Task;
        public event Action<string>? Requested { add { } remove { } }
        public event Action<CharacterStroke>? Stroked;
        public event Action<RendererPhysical>? Physical;
        internal void Stroke(CharacterStroke stroke) => Stroked?.Invoke(stroke);
        internal void Change(RendererPhysical change) => Physical?.Invoke(change);
        public Task StartAsync(AvatarProfile profile, string revision, RendererPlacement? placement, bool voiceMuted, CancellationToken token)
        {
            Capabilities = new(revision.ToLowerInvariant(), [new("Jaw", -10, 10, 0, ["Mouth"])]);
            return Task.CompletedTask;
        }
        public Task<RendererMessage> SendAsync<T>(string kind, T data, CancellationToken token, TimeSpan? timeout = null) =>
            Task.FromResult(RendererProtocol.Message("ok", activation, new { started = true }));
        public ValueTask DisposeAsync() { HasExited = true; exited.TrySetResult(); return ValueTask.CompletedTask; }
    }
}
