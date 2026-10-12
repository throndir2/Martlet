using System.Text;
using System.Text.Json;
using Martlet.Avatar.Hosting;
using Martlet.Avatar.RendererHost;
using Martlet.Avatars;
using Martlet.Mcp;

namespace Martlet.Desktop.Tests;

public sealed class VrmAnimationTests
{
    private static JsonElement Json(string text) => JsonDocument.Parse(text).RootElement.Clone();

    // A self-authored VRMA clip: hips (node 0) and right upper arm (node 1) in a T-pose skeleton, `seconds` long, the arm turned and,
    // with `hips`, the hips moved. `change` edits the glTF JSON before it is written.
    internal static byte[] Clip(double seconds = 1.5, bool hips = true, Action<Dictionary<string, object?>>? change = null)
    {
        var floats = new List<float> { 0, (float)seconds, 0, 0, -0.64f, 0.77f, 0, 0, -0.64f, 0.77f };
        if (hips) floats.AddRange([0, 0.9f, 0, 0.3f, 0.8f, 0]);
        var bin = floats.SelectMany(BitConverter.GetBytes).ToArray();
        var accessors = new List<object>
        {
            new { bufferView = 0, byteOffset = 0, componentType = 5126, count = 2, type = "SCALAR", min = new[] { 0.0 }, max = new[] { seconds } },
            new { bufferView = 0, byteOffset = 8, componentType = 5126, count = 2, type = "VEC4" }
        };
        var channels = new List<object> { new { sampler = 0, target = new { node = 1, path = "rotation" } } };
        var samplers = new List<object> { new { input = 0, output = 1, interpolation = "LINEAR" } };
        if (hips)
        {
            accessors.Add(new { bufferView = 0, byteOffset = 40, componentType = 5126, count = 2, type = "VEC3" });
            channels.Add(new { sampler = 1, target = new { node = 0, path = "translation" } });
            samplers.Add(new { input = 0, output = 2, interpolation = "LINEAR" });
        }
        var document = new Dictionary<string, object?>
        {
            ["asset"] = new { version = "2.0" },
            ["extensionsUsed"] = new[] { "VRMC_vrm_animation" },
            ["extensions"] = new { VRMC_vrm_animation = new { specVersion = "1.0", humanoid = new { humanBones = new { hips = new { node = 0 }, rightUpperArm = new { node = 1 } } } } },
            ["nodes"] = new object[] { new { name = "hips", translation = new[] { 0, 0.9, 0 }, children = new[] { 1 } }, new { name = "arm", translation = new[] { -0.2, 0.5, 0 } } },
            ["scene"] = 0, ["scenes"] = new[] { new { nodes = new[] { 0 } } },
            ["buffers"] = new object[] { new { byteLength = bin.Length } },
            ["bufferViews"] = new[] { new { buffer = 0, byteOffset = 0, byteLength = bin.Length } },
            ["accessors"] = accessors, ["animations"] = new[] { new { channels, samplers } }
        };
        change?.Invoke(document);
        return Glb(JsonSerializer.SerializeToUtf8Bytes(document), bin);
    }

    private static byte[] Glb(byte[] json, byte[] bin)
    {
        var jsonSize = (json.Length + 3) / 4 * 4;
        var binSize = (bin.Length + 3) / 4 * 4;
        var glb = new byte[12 + 8 + jsonSize + 8 + binSize];
        void U32(int at, uint value) => BitConverter.GetBytes(value).CopyTo(glb, at);
        U32(0, 0x46546C67); U32(4, 2); U32(8, (uint)glb.Length);
        U32(12, (uint)jsonSize); U32(16, 0x4E4F534A);
        Array.Fill(glb, (byte)' ', 20, jsonSize);
        json.CopyTo(glb, 20);
        U32(20 + jsonSize, (uint)binSize); U32(24 + jsonSize, 0x004E4942);
        bin.CopyTo(glb, 28 + jsonSize);
        return glb;
    }

    [Fact]
    public void A_clip_is_read_for_its_length_and_bones_and_only_real_VRMA_clips_pass()
    {
        var summary = VrmAnimationClip.Inspect(Clip());
        Assert.Equal(1.5, summary.Duration);
        Assert.Equal(["rightUpperArm"], summary.Bones);
        Assert.True(summary.MovesHips);
        Assert.False(VrmAnimationClip.Inspect(Clip(hips: false)).MovesHips);

        void Rejects(byte[] bytes, string reason) => Assert.Contains(reason, Assert.Throws<InvalidDataException>(() => VrmAnimationClip.Inspect(bytes)).Message);
        Rejects(new byte[64], "binary glTF 2.0");
        Rejects(new byte[VrmAnimationClip.MaximumBytes + 4], "20 bytes to 4 MB");
        Rejects(Clip(change: d => d["extensionsUsed"] = Array.Empty<string>()), "not a VRM Animation");
        Rejects(Clip(change: d => d["buffers"] = new object[] { new { byteLength = 40, uri = "https://example.invalid/a.bin" } }), "inside the file");
        Rejects(Clip(change: d => d["images"] = new object[] { new { uri = "a.png" } }), "must not carry pictures");
        Rejects(Clip(change: d => d["extensionsRequired"] = new[] { "KHR_draco_mesh_compression" }), "requires an extension");
        Rejects(Clip(seconds: 200), "0.01 to 120 seconds");
        Rejects(Clip(change: d => d["animations"] = new[] { new { channels = Array.Empty<object>(), samplers = new[] { new { input = 0, output = 1, interpolation = "CUBICSPLINE" } } } }),
            "LINEAR or STEP");
        Rejects(Clip(change: d => d["animations"] = Array.Empty<object>()), "no animation");
    }

    [Fact]
    public void Clip_names_are_bounded_and_made_from_file_names()
    {
        Assert.True(VrmAnimationClip.IsValidName("kept-" + new string('a', 64)));
        Assert.False(VrmAnimationClip.IsValidName(""));
        Assert.False(VrmAnimationClip.IsValidName(" wave"));
        Assert.False(VrmAnimationClip.IsValidName("wa\nve"));
        Assert.False(VrmAnimationClip.IsValidName(new string('a', VrmAnimationClip.MaximumNameLength + 1)));
        Assert.Equal("wave hello", VrmAnimationClip.NameOf(@"C:\clips\wave hello.vrma"));
        Assert.Equal("clip", VrmAnimationClip.NameOf(@"C:\clips\.vrma"));
        Assert.Equal(VrmAnimationClip.MaximumNameLength, VrmAnimationClip.NameOf(new string('b', 300) + ".vrma").Length);
    }

    [Fact]
    public void The_pages_answer_about_a_clip_is_kept_bounded_and_typed()
    {
        var result = CharacterAnimationReading.From(Json($$"""
            {"kept":true,"started":true,"name":"wave","duration":2.51234,"clips":["wave",3,"bad\nname",{{string.Join(",", Enumerable.Range(0, 20).Select(i => $"\"c{i}\""))}}],
             "reason":"{{new string('x', 400)}}","extra":1}
            """), "fallback");
        Assert.True(result.Kept);
        Assert.True(result.Started);
        Assert.Equal("wave", result.Name);
        Assert.Equal(2.512, result.Duration);
        Assert.Equal(VrmAnimationClip.MaximumKept - 2, result.Clips.Count);
        Assert.Equal("wave", result.Clips[0]);
        Assert.Equal(300, result.Reason!.Length);
        var refused = CharacterAnimationReading.From(Json("""{"kept":false,"started":"yes","name":" x","duration":-1}"""), "given");
        Assert.Equal(("given", false, false, 0.0), (refused.Name, refused.Kept, refused.Started, refused.Duration));
        Assert.Null(refused.Reason);
        Assert.Equal("The renderer didn't answer.", CharacterAnimationReading.From(Json("[1]"), "x").Reason);

        var reading = Json(CharacterAnimationReading.Json(result with { Reason = null }, 5, "wave.vrma"));
        Assert.Equal(5, reading.GetProperty("n").GetInt32());
        Assert.Equal("wave.vrma", reading.GetProperty("file").GetString());
        Assert.False(reading.TryGetProperty("reason", out _));
    }

    [Fact]
    public void The_overlay_surface_takes_a_clip_from_a_file_and_reads_the_answer()
    {
        Assert.Equal(("wave", @"C:\clips\wave.vrma", true, true),
            CharacterViewport.AnimationRequest("""animation:{"path":"C:\\clips\\wave.vrma","name":"wave"}"""));
        Assert.Equal(("wave", null, false, false), CharacterViewport.AnimationRequest("""animation:{"name":"wave","play":false,"inPlace":false}"""));
        Assert.Throws<ArgumentException>(() => CharacterViewport.AnimationRequest("""animation:{"path":"wave.vrma"}"""));
        Assert.Throws<ArgumentException>(() => CharacterViewport.AnimationRequest("animation:{}"));
        Assert.Throws<ArgumentException>(() => CharacterViewport.AnimationRequest("animation:nope"));

        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var viewport = new CharacterViewport { LastAnimation = """{"n":2,"name":"wave","started":true}""" };
                Assert.True(Json(viewport.Reading()).GetProperty("animation").GetProperty("started").GetBoolean());
            }
            catch (Exception error) { failure = error; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null) throw failure;
    }

    [Fact]
    public void The_pose_reading_says_which_clip_plays_and_which_are_kept()
    {
        var reading = Json(CharacterPoseReading.From(Json("""
            {"id":3,"found":true,"renderer":"Vrm","idle":true,"bones":{},
             "animation":{"name":"wave","time":0.512345,"duration":2.5,"weight":"all","extra":1},"clips":["wave","bow",7,""]}
            """), 3));
        var animation = reading.GetProperty("animation");
        Assert.Equal("wave", animation.GetProperty("name").GetString());
        Assert.Equal(0.5123, animation.GetProperty("time").GetDouble());
        Assert.False(animation.TryGetProperty("weight", out _));
        Assert.False(animation.TryGetProperty("extra", out _));
        Assert.Equal(["wave", "bow"], reading.GetProperty("clips").EnumerateArray().Select(c => c.GetString()));
        var none = Json(CharacterPoseReading.From(Json("""{"id":4,"found":true,"renderer":"Vrm","animation":null}"""), 4));
        Assert.Equal(JsonValueKind.Null, none.GetProperty("animation").ValueKind);

        var summary = Json(JsonSerializer.Serialize(DesktopAutomation.PoseSummary([
            Json("""{"found":true,"animation":{"name":"wave","weight":0.2}}"""), Json("""{"found":true,"animation":{"name":"wave","weight":1}}"""),
            Json("""{"found":true,"animation":null}""")])));
        Assert.Equal(["wave"], summary.GetProperty("animations").EnumerateArray().Select(a => a.GetString()));
        Assert.Equal(1, summary.GetProperty("animationWeight").GetProperty("most").GetDouble());
    }

    [Fact]
    public async Task Martlet_gives_the_showing_character_a_clip_plays_it_by_name_and_knows_when_a_new_model_forgot_it()
    {
        using var scope = new AvatarHostingTests.Scope();
        var renderer = new Renderer();
        await using var avatar = new AvatarController(createRenderer: () => renderer, allowControlledClock: true);
        ICharacterAnimations animations = avatar;
        Assert.Null(await animations.LoadAnimationAsync("kept-1", Clip()));
        await avatar.ShowAsync(scope.Profile() with { LipSync = AvatarLipSync.Loudness }, default);

        var clip = Clip();
        var loaded = await animations.LoadAnimationAsync("kept-1", clip, play: false);
        var sent = Assert.Single(renderer.Sent);
        Assert.Equal(("kept-1", false, true), (sent.Name, sent.Play, sent.InPlace));
        Assert.Equal(clip, Convert.FromBase64String(sent.Clip!));
        Assert.True(loaded!.Kept);
        Assert.False(loaded.Started);
        Assert.True(animations.HasAnimation("kept-1"));
        Assert.False(animations.HasAnimation("kept-2"));

        var played = await animations.PlayAnimationAsync("kept-1");
        Assert.True(played!.Started);
        Assert.Equal(new RendererAnimation("kept-1"), renderer.Sent.Last());
        Assert.Contains("Played the VRMA clip \"kept-1\"", avatar.LastAction);
        Assert.False((await animations.PlayAnimationAsync("nothing"))!.Started);
        Assert.Contains("No clip has that name", avatar.LastAction);
        await animations.LoadAnimationAsync("walk", Clip(), inPlace: false);
        Assert.False(renderer.Sent.Last().InPlace);

        await Assert.ThrowsAsync<ArgumentException>(() => animations.LoadAnimationAsync(" padded", clip));
        await Assert.ThrowsAsync<InvalidDataException>(() => animations.LoadAnimationAsync("noise", new byte[64]));

        // Hidden (or another model), the character forgets its clips; Martlet says so once.
        var forgotten = 0;
        animations.AnimationsForgotten += () => forgotten++;
        await avatar.StopAsync();
        Assert.Equal(1, forgotten);
        Assert.False(animations.HasAnimation("kept-1"));
        await avatar.ShowAsync(scope.Profile() with { LipSync = AvatarLipSync.Loudness }, default);
        Assert.False(animations.HasAnimation("kept-1"));
        Assert.Equal(1, forgotten);
    }

    private sealed class Renderer : IAvatarRenderer
    {
        private readonly Guid activation = Guid.NewGuid();
        private readonly List<string> kept = [];
        internal List<RendererAnimation> Sent { get; } = [];
        public RendererCapabilities? Capabilities { get; private set; }
        public bool HasExited { get; private set; }
        public Task Exited => Task.CompletedTask;
        public event Action<string>? Requested { add { } remove { } }
        public Task StartAsync(AvatarProfile profile, string revision, RendererPlacement? placement, bool voiceMuted, CancellationToken token)
        {
            HasExited = false;
            kept.Clear();
            Capabilities = new(revision.ToLowerInvariant(), [new("aa", 0, 1, 0, ["Mouth"])]);
            return Task.CompletedTask;
        }
        public Task<RendererMessage> SendAsync<T>(string kind, T data, CancellationToken token, TimeSpan? timeout = null)
        {
            if (data is not RendererAnimation animation) return Task.FromResult(RendererProtocol.Message("ok", activation, new { }));
            Assert.Equal("animation", kind);
            Sent.Add(animation);
            // As the page does: a clip is kept by its name, and a kept one plays.
            if (animation.Clip is not null && !kept.Contains(animation.Name)) kept.Add(animation.Name);
            var has = kept.Contains(animation.Name);
            return Task.FromResult(RendererProtocol.Message("animation", activation, new RendererAnimationResult(has, has && animation.Play,
                animation.Name, has ? 1.5 : 0, [.. kept], has ? null : "No clip has that name now; give the clip again.")));
        }
        public ValueTask DisposeAsync() { HasExited = true; return ValueTask.CompletedTask; }
    }
}
