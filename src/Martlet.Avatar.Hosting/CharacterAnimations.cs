using System.Text.Json;

namespace Martlet.Avatar.Hosting;

/// <summary>
/// Gives the showing VRM character a VRM Animation clip (<c>.vrma</c>: binary glTF with the <c>VRMC_vrm_animation</c> 1.0
/// extension) and plays it, sent as "animation". With <paramref name="Clip"/> (the file's bytes in base64) the renderer reads
/// the clip and keeps it as <paramref name="Name"/> while the model stays loaded (replacing a clip of that name; at most
/// <see cref="VrmAnimationClip.MaximumKept"/>, the oldest goes first); without it, it plays the clip it keeps by that name.
/// With <paramref name="Play"/> the clip plays once: it blends in over the idle pose, then gives the body back to the idle
/// pose, breathing and sway by its end. With <paramref name="InPlace"/> (the default) the hips stay at rest across the ground
/// and only move up and down, so a clip that walks or drifts keeps the character in its window. Replied to with
/// <see cref="RendererAnimationResult"/>; a clip the renderer can't use never stops the character.
/// </summary>
public sealed record RendererAnimation(string Name, string? Clip = null, bool Play = true, bool InPlace = true);

/// <summary>
/// What the renderer did with a <see cref="RendererAnimation"/>: whether it keeps a clip named <paramref name="Name"/> now
/// (<paramref name="Kept"/>), whether the clip started (<paramref name="Started"/>), its length in seconds (0 when unknown), the
/// names of every clip it keeps now for the showing model (<paramref name="Clips"/>, oldest first) and, when it didn't start,
/// why (<paramref name="Reason"/>: for example the character isn't a VRM, no clip has that name or the file isn't a usable clip).
/// </summary>
public sealed record RendererAnimationResult(bool Kept, bool Started, string Name, double Duration, IReadOnlyList<string> Clips,
    string? Reason = null);

/// <summary>What a VRMA clip holds, read from its glTF JSON: its length in seconds, the humanoid bones its channels turn (in the
/// order they first appear) and whether it moves the hips.</summary>
public sealed record VrmAnimationClipSummary(double Duration, IReadOnlyList<string> Bones, bool MovesHips);

/// <summary>
/// VRM Animation clips played on a showing VRM character. Martlet's desktop implements it (the character overlay); a clip
/// belongs to the model it was given to, so it is gone when another model loads, the character hides or its renderer ends:
/// <see cref="AnimationsForgotten"/> then fires and <see cref="HasAnimation"/> says false, and the caller gives the clip again.
/// </summary>
public interface ICharacterAnimations
{
    /// <summary>Gives the showing character the clip <paramref name="clip"/> (a <c>.vrma</c> file's bytes, at most
    /// <see cref="VrmAnimationClip.MaximumBytes"/>) to keep as <paramref name="name"/> (<see cref="VrmAnimationClip.IsValidName"/>),
    /// and with <paramref name="play"/> plays it once. With <paramref name="inPlace"/> (the default) the hips stay at rest across
    /// the ground and keep only their up and down movement, so the character stays in its window. Returns what the renderer did,
    /// or null while no character shows.</summary>
    /// <exception cref="ArgumentException">The name isn't valid.</exception>
    /// <exception cref="InvalidDataException">The bytes aren't a usable VRMA clip (<see cref="VrmAnimationClip.Inspect"/>).</exception>
    Task<RendererAnimationResult?> LoadAnimationAsync(string name, ReadOnlyMemory<byte> clip, bool play = true, bool inPlace = true,
        CancellationToken token = default);

    /// <summary>Plays the clip the showing character keeps as <paramref name="name"/> once. Returns what the renderer did
    /// (<see cref="RendererAnimationResult.Started"/>), or null while no character shows.</summary>
    Task<RendererAnimationResult?> PlayAnimationAsync(string name, CancellationToken token = default);

    /// <summary>Whether the showing character keeps a clip named <paramref name="name"/> (one given to it with
    /// <see cref="LoadAnimationAsync"/> since its model loaded).</summary>
    bool HasAnimation(string name);

    /// <summary>Raised (on any thread) when the clips the character kept are gone: another model loaded, the character hid or
    /// its renderer ended.</summary>
    event Action? AnimationsForgotten;
}

/// <summary>Checks VRM Animation clips (<c>.vrma</c>) before they go to the renderer, with the same bounds the renderer's page
/// uses: a binary glTF 2.0 file of at most <see cref="MaximumBytes"/> with the <c>VRMC_vrm_animation</c> extension, all its data
/// inside the file (no URLs, no pictures), LINEAR or STEP keys and a first animation of at most <see cref="MaximumSeconds"/>.</summary>
public static class VrmAnimationClip
{
    public const int MaximumBytes = 4 * 1024 * 1024;
    public const int MaximumNameLength = 128;
    public const int MaximumKept = 16;
    public const double MaximumSeconds = 120;
    public const string Extension = "VRMC_vrm_animation";
    private const int MaximumJsonBytes = 1024 * 1024;

    /// <summary>A clip name: 1 to <see cref="MaximumNameLength"/> characters, no control characters and no spaces at its ends.</summary>
    public static bool IsValidName(string? name) => name is { Length: > 0 and <= MaximumNameLength } && name.Trim() == name &&
        !name.Any(char.IsControl);

    /// <summary>A clip name made from a file's name (without its folder and extension), or "clip" when nothing is left.</summary>
    public static string NameOf(string path)
    {
        var name = new string([.. Path.GetFileNameWithoutExtension(path ?? "").Where(c => !char.IsControl(c))]).Trim();
        if (name.Length > MaximumNameLength) name = name[..MaximumNameLength].TrimEnd();
        return name.Length == 0 ? "clip" : name;
    }

    /// <summary>Reads what a clip holds; throws <see cref="InvalidDataException"/> with a plain reason for bytes that aren't a
    /// usable VRMA clip.</summary>
    public static VrmAnimationClipSummary Inspect(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length is < 20 or > MaximumBytes) throw Invalid($"A VRMA clip is 20 bytes to {MaximumBytes / 1024 / 1024} MB.");
        static uint U32(ReadOnlySpan<byte> from, int at) => System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(from[at..]);
        if (U32(bytes, 0) != 0x46546C67 || U32(bytes, 4) != 2 || U32(bytes, 8) != (uint)bytes.Length) throw Invalid("A VRMA clip is a binary glTF 2.0 file.");
        var length = U32(bytes, 12);
        if (U32(bytes, 16) != 0x4E4F534A || length > MaximumJsonBytes || 20 + length > (uint)bytes.Length)
            throw Invalid("The clip's glTF JSON is missing or too large.");
        JsonDocument document;
        try { document = JsonDocument.Parse(bytes.Slice(20, (int)length).ToArray(), new JsonDocumentOptions { MaxDepth = 64 }); }
        catch (JsonException) { throw Invalid("The clip's glTF JSON can't be read."); }
        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) throw Invalid("The clip's glTF JSON can't be read.");
            if (!Strings(root, "extensionsUsed").Contains(Extension))
                throw Invalid("This glTF file is not a VRM Animation: it has no VRMC_vrm_animation extension.");
            if (Strings(root, "extensionsRequired").FirstOrDefault(e => e != Extension) is { } required)
                throw Invalid($"The clip requires an extension Martlet can't read: {Clip(required)}.");
            if (!root.TryGetProperty("extensions", out var extensions) || extensions.ValueKind != JsonValueKind.Object ||
                !extensions.TryGetProperty(Extension, out var extension) || extension.ValueKind != JsonValueKind.Object ||
                !extension.TryGetProperty("humanoid", out var humanoid) || humanoid.ValueKind != JsonValueKind.Object ||
                !humanoid.TryGetProperty("humanBones", out var humanBones) || humanBones.ValueKind != JsonValueKind.Object)
                throw Invalid("The clip has no humanoid bones.");
            var bones = new Dictionary<int, string>();
            foreach (var bone in humanBones.EnumerateObject())
                if (bone.Value.ValueKind == JsonValueKind.Object && bone.Value.TryGetProperty("node", out var node) && node.TryGetInt32(out var index))
                    bones.TryAdd(index, bone.Name);
            Items(root, "nodes", 1024);
            var accessors = Items(root, "accessors", 4096);
            if (Items(root, "buffers", 16).Any(b => b.ValueKind != JsonValueKind.Object || b.TryGetProperty("uri", out _)))
                throw Invalid("A VRMA clip keeps all its data inside the file.");
            if (root.TryGetProperty("images", out var images) && (images.ValueKind != JsonValueKind.Array || images.GetArrayLength() > 0))
                throw Invalid("A VRMA clip must not carry pictures.");
            var animations = Items(root, "animations", 64);
            if (animations.Count == 0 || animations[0].ValueKind != JsonValueKind.Object) throw Invalid("The clip has no animation.");
            foreach (var animation in animations)
                if (animation.ValueKind == JsonValueKind.Object &&
                    Items(animation, "samplers", 4096).Any(s => s.ValueKind == JsonValueKind.Object && s.TryGetProperty("interpolation", out var i) &&
                        i.ValueKind == JsonValueKind.String && i.GetString() == "CUBICSPLINE"))
                    throw Invalid("Martlet plays VRMA clips with LINEAR or STEP keys only.");
            var samplers = Items(animations[0], "samplers", 4096);
            var duration = 0.0;
            foreach (var sampler in samplers)
                if (sampler.ValueKind == JsonValueKind.Object && sampler.TryGetProperty("input", out var input) && input.TryGetInt32(out var at) &&
                    at >= 0 && at < accessors.Count && accessors[at].ValueKind == JsonValueKind.Object &&
                    accessors[at].TryGetProperty("max", out var max) && max.ValueKind == JsonValueKind.Array && max.GetArrayLength() > 0 &&
                    max[0].TryGetDouble(out var end))
                    duration = Math.Max(duration, end);
            if (!double.IsFinite(duration) || duration is < 0.01 or > MaximumSeconds)
                throw Invalid($"The clip's length must be 0.01 to {MaximumSeconds:0} seconds.");
            var moved = new List<string>();
            var hips = false;
            foreach (var channel in Items(animations[0], "channels", 4096))
            {
                if (channel.ValueKind != JsonValueKind.Object || !channel.TryGetProperty("target", out var target) ||
                    target.ValueKind != JsonValueKind.Object || !target.TryGetProperty("node", out var node) || !node.TryGetInt32(out var index) ||
                    !bones.TryGetValue(index, out var bone) || !target.TryGetProperty("path", out var path)) continue;
                if (path.GetString() == "translation" && bone == "hips") hips = true;
                else if (path.GetString() == "rotation" && !moved.Contains(bone)) moved.Add(bone);
            }
            if (moved.Count == 0 && !hips) throw Invalid("The clip moves no humanoid bone.");
            return new(Math.Round(duration, 3), moved, hips);
        }
    }

    private static List<string> Strings(JsonElement owner, string property) =>
        [.. Items(owner, property, 64).Where(e => e.ValueKind == JsonValueKind.String).Select(e => e.GetString()!)];

    private static List<JsonElement> Items(JsonElement owner, string property, int maximum)
    {
        if (!owner.TryGetProperty(property, out var value)) return [];
        if (value.ValueKind != JsonValueKind.Array || value.GetArrayLength() > maximum) throw Invalid($"The clip has too many {property}.");
        return [.. value.EnumerateArray()];
    }

    private static string Clip(string text) => new([.. text.Take(64).Where(c => !char.IsControl(c))]);

    private static InvalidDataException Invalid(string reason) => new(reason);
}
