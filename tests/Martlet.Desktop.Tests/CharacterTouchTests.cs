using System.Collections.Concurrent;
using Martlet.Avatar.Hosting;
using Martlet.Avatars;
using Xunit;

namespace Martlet.Desktop.Tests;

public sealed class CharacterTouchTests
{
    private static CharacterTouch Touch(double y = 0.5, string[]? areas = null, string[]? drawables = null, string? bone = null,
        bool hair = false) => new(0.5, y, areas ?? [], drawables ?? [], bone, bone, hair, null, null);

    [Theory]
    [InlineData(new[] { "Head" }, new string[0], null, false, 0.9, "head")]
    [InlineData(new[] { "Body" }, new[] { "D_HAIR_FRONT" }, null, false, 0.1, "body")]
    [InlineData(new string[0], new[] { "ArtMesh12", "D_HAIR_FRONT" }, null, false, 0.9, "hair")]
    [InlineData(new string[0], new[] { "D_FACE_00" }, null, false, 0.9, "face")]
    [InlineData(new string[0], new[] { "ArmL_01" }, null, false, 0.1, "arm")]
    [InlineData(new string[0], new[] { "左手" }, null, false, 0.1, "hand")]
    [InlineData(new string[0], new string[0], "head", true, 0.9, "hair")]
    [InlineData(new string[0], new string[0], "leftEye", false, 0.9, "face")]
    [InlineData(new string[0], new string[0], "rightIndexProximal", false, 0.9, "hand")]
    [InlineData(new string[0], new string[0], "leftLowerArm", false, 0.9, "arm")]
    [InlineData(new string[0], new string[0], "rightToes", false, 0.1, "foot")]
    [InlineData(new string[0], new string[0], "leftUpperLeg", false, 0.1, "leg")]
    [InlineData(new string[0], new string[0], "upperChest", false, 0.1, "body")]
    [InlineData(new string[0], new[] { "ArtMesh3" }, null, false, 0.1, "head")]
    [InlineData(new string[0], new[] { "ArtMesh3" }, null, false, 0.5, "body")]
    [InlineData(new string[0], new[] { "ArtMesh3" }, null, false, 0.8, "leg")]
    public void Coarse_zone_comes_from_the_bone_then_hit_areas_then_drawables_then_height(string[] areas, string[] drawables,
        string? bone, bool hair, double y, string zone)
    {
        var touch = Touch(y, areas, drawables, bone, hair);
        Assert.Equal(zone, touch.CoarseZone);
        Assert.Contains(zone, CharacterTouch.Zones);
    }

    [Fact]
    public void A_touch_crosses_the_renderer_pipe_without_its_derived_fields_and_bad_ones_are_invalid()
    {
        var touch = new CharacterTouch(0.25, 0.1, ["Head"], ["D_FACE", "ArtMesh1"], null, null, false, null, null);
        var message = RendererProtocol.Message("touch", Guid.NewGuid(), touch);
        Assert.False(message.Data.TryGetProperty("coarseZone", out _));
        Assert.False(message.Data.TryGetProperty("isValid", out _));
        var read = RendererProtocol.Data<CharacterTouch>(message);
        Assert.Equal(touch.HitAreas, read.HitAreas);
        Assert.Equal(touch.Drawables, read.Drawables);
        Assert.Equal("head", read.CoarseZone);
        Assert.True(read.IsValid);
        Assert.False((touch with { X = double.NaN }).IsValid);
        Assert.False((touch with { Drawables = [.. Enumerable.Range(0, 9).Select(i => $"D{i}")] }).IsValid);
        Assert.False((touch with { Bone = "head\n" }).IsValid);
        Assert.False(read.Held);
        Assert.False(message.Data.TryGetProperty("held", out _));
        var held = RendererProtocol.Data<CharacterTouch>(RendererProtocol.Message("touch", Guid.NewGuid(), touch with { HeldMilliseconds = 750 }));
        Assert.Equal(750, held.HeldMilliseconds);
        Assert.True(held.Held && held.IsValid);
        Assert.False((touch with { HeldMilliseconds = 599 }).Held);
        Assert.False((touch with { HeldMilliseconds = -1 }).IsValid);
        Assert.False((touch with { HeldMilliseconds = CharacterTouch.MaximumHeldMilliseconds + 1 }).IsValid);
    }

    [Theory]
    [InlineData("head", new[] { "Idle", "Tap@Head", "TapBody" }, "Tap@Head")]
    [InlineData("hair", new[] { "Idle", "TapBody", "tap_head" }, "tap_head")]
    [InlineData("body", new[] { "Idle", "Tap@Head", "TapBody" }, "TapBody")]
    [InlineData("arm", new[] { "Idle", "TapArm", "TapBody" }, "TapArm")]
    [InlineData("leg", new[] { "Idle", "Touch" }, "Touch")]
    [InlineData("face", new[] { "Idle", "TapBody" }, null)]
    public void A_tap_plays_the_models_own_tap_motion_for_that_part(string zone, string[] groups, string? expected) =>
        Assert.Equal(expected, AvatarController.TouchMotion(groups, zone));

    [Fact]
    public async Task A_tap_raises_Touched_and_plays_the_tap_motion_else_a_gesture_without_asking_a_model()
    {
        using var scope = new AvatarHostingTests.Scope();
        var renderer = new Renderer(["Idle", "TapBody"]);
        await using var avatar = new AvatarController(createRenderer: () => renderer, allowControlledClock: true);
        await avatar.ShowAsync(scope.Profile() with { LipSync = AvatarLipSync.Loudness }, default);
        var touched = new ConcurrentQueue<CharacterTouch>();
        avatar.Touched += touched.Enqueue;

        renderer.Tap(Touch(0.5, ["Body"]));
        await Until(() => renderer.Actions.Count >= 1);
        Assert.Single(touched);
        Assert.Equal(new RendererAction("motion", "TapBody"), renderer.Actions.Single());
        Assert.Equal("body", avatar.LastTouch!.CoarseZone);

        // The head has no tap motion and the model can't tilt: the nod after it plays.
        renderer.Refuse.Add("tilt");
        renderer.Tap(Touch(0.1, ["Head"]));
        await Until(() => renderer.Actions.Count >= 3);
        Assert.Equal([new("gesture", "tilt"), new RendererAction("gesture", "nod")], renderer.Actions.Skip(1));
        Assert.Equal(2, touched.Count);
    }

    private static async Task Until(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!condition()) await Task.Delay(5, timeout.Token);
    }

    private sealed class Renderer(string[] motions) : IAvatarRenderer
    {
        private readonly Guid activation = Guid.NewGuid();
        private readonly TaskCompletionSource exited = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal ConcurrentQueue<RendererAction> Queue { get; } = new();
        internal List<RendererAction> Actions => [.. Queue];
        internal ConcurrentBag<string> Refuse { get; } = [];
        public RendererCapabilities? Capabilities { get; private set; }
        public bool HasExited { get; private set; }
        public Task Exited => exited.Task;
        public event Action<string>? Requested { add { } remove { } }
        public event Action<CharacterTouch>? Touched;
        internal void Tap(CharacterTouch touch) => Touched?.Invoke(touch);
        public Task StartAsync(AvatarProfile profile, string revision, RendererPlacement? placement, bool voiceMuted, CancellationToken token)
        {
            Capabilities = new(revision.ToLowerInvariant(), [new("Jaw", -10, 10, 0, ["Mouth"])],
                new RendererModelSummary(1, 1, [], [], motions, 0, false, true));
            return Task.CompletedTask;
        }
        public Task<RendererMessage> SendAsync<T>(string kind, T data, CancellationToken token, TimeSpan? timeout = null)
        {
            var started = true;
            if (kind == "action" && data is RendererAction action)
            {
                Queue.Enqueue(action);
                started = !Refuse.Contains(action.Name);
            }
            return Task.FromResult(RendererProtocol.Message("ok", activation, new { started }));
        }
        public ValueTask DisposeAsync() { HasExited = true; exited.TrySetResult(); return ValueTask.CompletedTask; }
    }
}
