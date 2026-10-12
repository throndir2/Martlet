using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace Martlet.Core.Motion;

/// <summary>
/// FIXTURE - NOT AI: a deterministic <see cref="IMotionMaker"/> for tests and plumbing checks. It "makes" a VRMA clip in which
/// the hips sway, the left arm hangs down and the right arm waves, with a speed and size taken from the description's hash.
/// Nothing it makes comes from a model, and <see cref="MotionResult.Fixture"/> is true.
/// </summary>
public sealed class FixtureMotionMaker(TimeSpan? delay = null) : IMotionMaker
{
    public const string Engine = "fixture";
    public const string Model = "FIXTURE - NOT AI wave";
    public const int Fps = 30;
    private readonly TimeSpan wait = delay ?? TimeSpan.FromMilliseconds(20);

    public string Where => "the FIXTURE - NOT AI motion maker";

    public Task<MotionMakerAvailability> GetAvailabilityAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(new MotionMakerAvailability(true, null, Where, Fixture: true));
    }

    public async Task<MotionResult> GenerateAsync(MotionRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        request.Validate();
        var started = DateTimeOffset.UtcNow;
        await Task.Delay(wait, cancellationToken).ConfigureAwait(false);
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(request.Prompt.Trim() + "\n" + request.Seed));
        var seed = request.Seed ?? (BinaryPrimitives.ReadInt64LittleEndian(hash) & MotionRequest.MaximumSeed);
        var frames = Math.Max(2, (int)Math.Round(request.Seconds * Fps));
        return new()
        {
            Vrma = Vrma(frames, hash), Frames = frames, Fps = Fps, Seconds = frames / (double)Fps, Seed = seed,
            Engine = Engine, Model = Model, Where = Where, Fixture = true, Took = DateTimeOffset.UtcNow - started
        };
    }

    /// <summary>A VRMA clip of <paramref name="frames"/> frames at <see cref="Fps"/>: hips, spine, head and both upper arms of a
    /// VRM humanoid in its T-pose rest, animated by values from <paramref name="hash"/> (at least 4 bytes).</summary>
    public static byte[] Vrma(int frames, ReadOnlySpan<byte> hash)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(frames, 2);
        var wave = 1.5 + hash[0] % 16 / 8.0;
        var sway = (3 + hash[1] % 5) * Math.PI / 180;
        var lift = (50 + hash[2] % 20) * Math.PI / 180;
        var bob = 0.005 + hash[3] % 8 / 1000.0;
        var times = new float[frames];
        var hipsTranslation = new float[frames * 3];
        var hips = new float[frames * 4];
        var spine = new float[frames * 4];
        var head = new float[frames * 4];
        var left = new float[frames * 4];
        var right = new float[frames * 4];
        for (var i = 0; i < frames; i++)
        {
            var t = i / (double)Fps;
            times[i] = (float)t;
            hipsTranslation[i * 3 + 1] = (float)(0.95 + bob * Math.Sin(t * Math.PI * 2));
            Put(hips, i, 0, 1, 0, sway * Math.Sin(t * Math.PI));
            Put(spine, i, 0, 0, 1, sway / 2 * Math.Sin(t * Math.PI));
            Put(head, i, 1, 0, 0, sway / 2 * Math.Sin(t * Math.PI * 2));
            Put(left, i, 0, 0, 1, -70 * Math.PI / 180);
            Put(right, i, 0, 0, 1, -lift - 20 * Math.PI / 180 * Math.Sin(t * Math.PI * 2 * wave));
        }

        using var binary = new MemoryStream();
        var views = new JsonArray();
        var accessors = new JsonArray();
        int Add(float[] values, string type, bool bounds = false)
        {
            var offset = (int)binary.Length;
            var bytes = new byte[values.Length * 4];
            for (var i = 0; i < values.Length; i++) BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(i * 4), values[i]);
            binary.Write(bytes);
            views.Add(new JsonObject { ["buffer"] = 0, ["byteOffset"] = offset, ["byteLength"] = values.Length * 4 });
            var accessor = new JsonObject
            {
                ["bufferView"] = views.Count - 1, ["componentType"] = 5126, ["count"] = frames, ["type"] = type
            };
            if (bounds)
            {
                accessor["min"] = new JsonArray(values.Min());
                accessor["max"] = new JsonArray(values.Max());
            }
            accessors.Add(accessor);
            return accessors.Count - 1;
        }
        var input = Add(times, "SCALAR", bounds: true);
        var samplers = new JsonArray();
        var channels = new JsonArray();
        void Channel(int node, string path, float[] values)
        {
            samplers.Add(new JsonObject { ["input"] = input, ["output"] = Add(values, path == "translation" ? "VEC3" : "VEC4"), ["interpolation"] = "LINEAR" });
            channels.Add(new JsonObject { ["sampler"] = samplers.Count - 1, ["target"] = new JsonObject { ["node"] = node, ["path"] = path } });
        }
        Channel(0, "translation", hipsTranslation);
        Channel(0, "rotation", hips);
        Channel(1, "rotation", spine);
        Channel(2, "rotation", head);
        Channel(3, "rotation", left);
        Channel(4, "rotation", right);

        var json = new JsonObject
        {
            ["asset"] = new JsonObject { ["version"] = "2.0", ["generator"] = "Martlet " + Model },
            ["extensionsUsed"] = new JsonArray("VRMC_vrm_animation"),
            ["extensions"] = new JsonObject
            {
                ["VRMC_vrm_animation"] = new JsonObject
                {
                    ["specVersion"] = "1.0",
                    ["humanoid"] = new JsonObject
                    {
                        ["humanBones"] = new JsonObject
                        {
                            ["hips"] = new JsonObject { ["node"] = 0 }, ["spine"] = new JsonObject { ["node"] = 1 },
                            ["head"] = new JsonObject { ["node"] = 2 }, ["leftUpperArm"] = new JsonObject { ["node"] = 3 },
                            ["rightUpperArm"] = new JsonObject { ["node"] = 4 }
                        }
                    }
                }
            },
            ["scene"] = 0,
            ["scenes"] = new JsonArray(new JsonObject { ["nodes"] = new JsonArray(0) }),
            ["nodes"] = new JsonArray(
                new JsonObject { ["name"] = "hips", ["translation"] = new JsonArray(0, 0.95, 0), ["children"] = new JsonArray(1) },
                new JsonObject { ["name"] = "spine", ["translation"] = new JsonArray(0, 0.15, 0), ["children"] = new JsonArray(2, 3, 4) },
                new JsonObject { ["name"] = "head", ["translation"] = new JsonArray(0, 0.45, 0) },
                new JsonObject { ["name"] = "leftUpperArm", ["translation"] = new JsonArray(0.2, 0.35, 0) },
                new JsonObject { ["name"] = "rightUpperArm", ["translation"] = new JsonArray(-0.2, 0.35, 0) }),
            ["animations"] = new JsonArray(new JsonObject { ["name"] = Model, ["samplers"] = samplers, ["channels"] = channels }),
            ["bufferViews"] = views,
            ["accessors"] = accessors,
            ["buffers"] = new JsonArray(new JsonObject { ["byteLength"] = binary.Length })
        };
        return Glb(Encoding.UTF8.GetBytes(json.ToJsonString()), binary.ToArray());
    }

    // A unit quaternion (x, y, z, w) for a rotation of angle radians about the axis (ax, ay, az).
    private static void Put(float[] target, int frame, double ax, double ay, double az, double angle)
    {
        var s = Math.Sin(angle / 2);
        target[frame * 4] = (float)(ax * s);
        target[frame * 4 + 1] = (float)(ay * s);
        target[frame * 4 + 2] = (float)(az * s);
        target[frame * 4 + 3] = (float)Math.Cos(angle / 2);
    }

    /// <summary>A glTF binary (GLB 2.0) of a JSON chunk (padded with spaces) and a BIN chunk (padded with zeros).</summary>
    public static byte[] Glb(byte[] json, byte[] binary)
    {
        var jsonLength = (json.Length + 3) & ~3;
        var binaryLength = (binary.Length + 3) & ~3;
        var total = 12 + 8 + jsonLength + (binary.Length > 0 ? 8 + binaryLength : 0);
        var bytes = new byte[total];
        "glTF"u8.CopyTo(bytes);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4), 2);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(8), (uint)total);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(12), (uint)jsonLength);
        "JSON"u8.CopyTo(bytes.AsSpan(16));
        json.CopyTo(bytes, 20);
        bytes.AsSpan(20 + json.Length, jsonLength - json.Length).Fill((byte)' ');
        if (binary.Length > 0)
        {
            var at = 20 + jsonLength;
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(at), (uint)binaryLength);
            "BIN\0"u8.CopyTo(bytes.AsSpan(at + 4));
            binary.CopyTo(bytes, at + 8);
        }
        return bytes;
    }
}
