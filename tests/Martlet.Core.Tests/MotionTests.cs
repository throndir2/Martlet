using System.Buffers.Binary;
using System.Text;
using System.Text.Json.Nodes;
using Martlet.Core.Cluster;
using Martlet.Core.Motion;
using Martlet.Core.Sync;

namespace Martlet.Core.Tests;

public sealed class MotionTests
{
    [Fact]
    public void Requests_are_held_to_their_bounds()
    {
        new MotionRequest { Prompt = "A person waves with the right hand." }.Validate();
        new MotionRequest { Prompt = "A person jumps.", Seconds = MotionRequest.MaximumSeconds, Seed = MotionRequest.MaximumSeed }.Validate();
        Assert.Equal((1, 12, 4), (MotionRequest.MinimumSeconds, MotionRequest.MaximumSeconds, MotionRequest.DefaultSeconds));
        foreach (var bad in new[]
        {
            new MotionRequest { Prompt = "" }, new MotionRequest { Prompt = "hi" }, new MotionRequest { Prompt = "12345 67" },
            new MotionRequest { Prompt = "A person\nwaves." }, new MotionRequest { Prompt = new string('a', 401) },
            new MotionRequest { Prompt = string.Join(' ', Enumerable.Repeat("step", 61)) },
            new MotionRequest { Prompt = "A person bows.", Seconds = 0.5 }, new MotionRequest { Prompt = "A person bows.", Seconds = 12.5 },
            new MotionRequest { Prompt = "A person bows.", Seconds = double.NaN }, new MotionRequest { Prompt = "A person bows.", Seed = -1 },
            new MotionRequest { Prompt = "A person bows.", Seed = (long)int.MaxValue + 1 }
        })
            Assert.Equal(MotionErrorCodes.RequestInvalid, Assert.Throws<MotionException>(bad.Validate).Code);
    }

    [Fact]
    public async Task The_fixture_maker_returns_a_repeatable_vrma_clip()
    {
        var maker = new FixtureMotionMaker(TimeSpan.Zero);
        Assert.True((await maker.GetAvailabilityAsync(CancellationToken.None)).Fixture);
        var request = new MotionRequest { Prompt = "A person waves.", Seconds = 2, Seed = 5 };
        var first = await maker.GenerateAsync(request, CancellationToken.None);
        var again = await maker.GenerateAsync(request, CancellationToken.None);
        Assert.Equal(first.Vrma, again.Vrma);
        Assert.Equal((60, 30, 2.0, 5L, "fixture", true), (first.Frames, first.Fps, first.Seconds, first.Seed!.Value, first.Engine, first.Fixture));
        Assert.True(MotionFiles.IsVrma(first.Vrma));
        MotionFiles.Require(first.Vrma, "the fixture");

        var json = JsonNode.Parse(Encoding.UTF8.GetString(first.Vrma, 20, (int)BinaryPrimitives.ReadUInt32LittleEndian(first.Vrma.AsSpan(12))))!;
        Assert.Equal("1.0", json["extensions"]!["VRMC_vrm_animation"]!["specVersion"]!.ToString());
        Assert.Equal(["hips", "spine", "head", "leftUpperArm", "rightUpperArm"],
            json["extensions"]!["VRMC_vrm_animation"]!["humanoid"]!["humanBones"]!.AsObject().Select(p => p.Key));
        Assert.Equal(6, json["animations"]![0]!["channels"]!.AsArray().Count);
        Assert.Equal(60, json["accessors"]![0]!["count"]!.GetValue<int>());
        Assert.NotEqual(first.Vrma, (await maker.GenerateAsync(request with { Prompt = "A person bows." }, CancellationToken.None)).Vrma);
        await Assert.ThrowsAsync<MotionException>(() => maker.GenerateAsync(new MotionRequest { Prompt = "x" }, CancellationToken.None));
    }

    [Fact]
    public void Only_vrma_clips_pass()
    {
        var clip = FixtureMotionMaker.Vrma(10, new byte[32]);
        Assert.True(MotionFiles.IsVrma(clip));
        Assert.False(MotionFiles.IsVrma(clip.AsSpan(0, clip.Length - 4)));
        Assert.False(MotionFiles.IsVrma(FixtureMotionMaker.Glb(Encoding.UTF8.GetBytes("{\"asset\":{\"version\":\"2.0\"}}"), [])));
        Assert.False(MotionFiles.IsVrma("not a clip at all, not a clip"u8));
        Assert.Equal(MotionErrorCodes.Failed, Assert.Throws<MotionException>(() => MotionFiles.Require([1, 2, 3], "x")).Code);
    }

    [Fact]
    public void Settings_default_to_automatic_and_find_the_motion_computer_from_files()
    {
        var now = DateTimeOffset.UtcNow;
        var plan = ClusterPlan.Empty
            .Observe("gpu-b", "https://192.168.1.31:9443", [new() { Kind = "motion", Model = "hy-motion-1.0-lite" }], false, "desk-a", now)
            .Observe("gpu-a", "https://192.168.1.30:9443", [new() { Kind = "pictures", Model = "z-image-turbo" }], false, "desk-a", now)
            .Observe("gpu-c", "https://192.168.1.32:9443", [new() { Kind = "motion", Model = "hy-motion-1.0" }], false, "desk-a", now);
        var automatic = new MotionSettings();
        Assert.True(automatic.IsDefault && automatic.On);
        Assert.Equal("gpu-b", automatic.MotionHost(plan, null));
        Assert.Equal("gpu-c", automatic.MotionHost(plan, ["gpu-a", "gpu-c"]));
        Assert.Null(automatic.MotionHost(plan, ["gpu-a"]));
        Assert.Null(automatic.MotionHost(plan.Observe("gpu-b", null, [], true, "desk-a", now.AddSeconds(1)), ["gpu-b"]));
        Assert.Equal("gpu-x", new MotionSettings { Place = MotionPlace.Host, HostId = "gpu-x" }.MotionHost(plan, []));
        Assert.Null(new MotionSettings { Place = MotionPlace.Off }.MotionHost(plan, null));
        Assert.Null(MotionSettings.Parse("{\"schema_version\":1,\"place\":\"host\"}"));
        Assert.Null(MotionSettings.Parse("{\"schema_version\":1,\"place\":\"off\",\"host_id\":\"gpu-b\"}"));
        Assert.Null(MotionSettings.Parse("{\"schema_version\":2}"));
        Assert.Null(MotionSettings.Parse("not json"));
    }

    [Fact]
    public void A_choice_made_on_one_computer_is_the_same_on_another()
    {
        var a = Directory.CreateTempSubdirectory("martlet-motion-a-").FullName;
        var b = Directory.CreateTempSubdirectory("martlet-motion-b-").FullName;
        try
        {
            Assert.Equal("none", MotionSettings.Read(b).State);
            var chosen = new MotionSettings { Place = MotionPlace.Host, HostId = "gpu-b" };
            Assert.True(chosen.Save(a));
            // Computer A shares motion.json as the "motion" shared setting; computer B, which never chose, takes it.
            var document = SharedSettings.Empty.Put(MotionSettings.SharedKey, MotionSettings.Load(a).Share(), null, "desk-a", DateTimeOffset.UtcNow);
            var arrived = SharedSettings.Parse(SharedSettings.Merge(SharedSettings.Empty, document).Write()).Find(MotionSettings.SharedKey)!;
            Assert.True(MotionSettings.Parse(arrived.Value)!.Save(b));
            Assert.Equal(("loaded", MotionPlace.Host, "gpu-b"), (MotionSettings.Read(b).State, MotionSettings.Load(b).Place, MotionSettings.Load(b).HostId));
            Assert.Equal(MotionSettings.Load(a).Share(), MotionSettings.Load(b).Share());
            Assert.True(SharedSettings.IsKey(MotionSettings.SharedKey));
            // Turning it off on B travels back the same way.
            Assert.True(new MotionSettings { Place = MotionPlace.Off }.Save(b));
            Assert.Equal(MotionPlace.Off, MotionSettings.Parse(MotionSettings.Load(b).Share())!.Place);
        }
        finally
        {
            Directory.Delete(a, true);
            Directory.Delete(b, true);
        }
    }
}
