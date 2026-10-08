using System.Text.Json.Serialization;

namespace Martlet.Avatar.Hosting;

/// <summary>
/// Where the character was tapped (a left click that didn't drag or pan), sent unprompted on the renderer's request pipe as a
/// "touch" message after the page's hit test found the character there. <paramref name="X"/> and <paramref name="Y"/> are
/// fractions 0..1 of the renderer page (the WebView's whole client area: the overlay's full drawing including the transparent
/// room beside the character's frame, or the whole camera-view window), origin top-left, +y down; the same area a snapshot
/// captures before cropping.
/// Live2D: <paramref name="HitAreas"/> are the model3.json HitAreas (their Name, else Id) whose mesh contains the point and
/// <paramref name="Drawables"/> the IDs of the visible meshes under it, topmost first (at most 8).
/// VRM: <paramref name="Bone"/> is the humanoid bone (VRM name such as <c>head</c> or <c>leftUpperArm</c>) of the mesh skinned
/// most to the hit triangle, or of its nearest humanoid ancestor; <paramref name="Node"/> the actual node (a spring-bone hair
/// joint, say); <paramref name="Hair"/> whether that node is a non-humanoid descendant of the head; <paramref name="Mesh"/> and
/// <paramref name="Material"/> the hit mesh and material names. <paramref name="HeldMilliseconds"/>: how long the press lasted
/// (0 when unknown); <see cref="HoldMilliseconds"/> or more is a hold. <paramref name="WholeX"/> and <paramref name="WholeY"/>:
/// where the same point of the character sits with the character framed whole (no zoom, no pan), as fractions of the page;
/// touch zones found in a whole-character snapshot compare with these, so a zoomed or panned view still lands right.
/// </summary>
public sealed record CharacterTouch(double X, double Y, IReadOnlyList<string> HitAreas, IReadOnlyList<string> Drawables, string? Bone,
    string? Node, bool Hair, string? Mesh, string? Material, int HeldMilliseconds = 0, double? WholeX = null, double? WholeY = null)
{
    public const int MaximumHitAreas = 32, MaximumDrawables = 8, MaximumName = 256;
    /// <summary>A press held at least this long without moving is a hold, not a tap.</summary>
    public const int HoldMilliseconds = 600;
    /// <summary>The longest press the renderer still counts as touching the character.</summary>
    public const int MaximumHeldMilliseconds = 10_000;

    /// <summary>The press was held (<see cref="HoldMilliseconds"/> or longer) rather than a quick tap.</summary>
    [JsonIgnore]
    public bool Held => HeldMilliseconds >= HoldMilliseconds;

    /// <summary>The touch zones <see cref="CoarseZone"/> reports.</summary>
    public static IReadOnlyList<string> Zones { get; } = ["head", "hair", "face", "body", "arm", "hand", "leg", "foot"];

    /// <summary>Where a point of the page (fractions, +y down) sits with the character framed whole (no zoom, no pan), for a view
    /// zoomed by <paramref name="zoom"/> and panned by <paramref name="x"/>, <paramref name="y"/> in a frame spanning
    /// <paramref name="frame"/> of the page's width: the renderers draw the frame's clip space as fitted * zoom + (x * frame, y).</summary>
    public static (double X, double Y) Unframed(double pageX, double pageY, double zoom, double x, double y, double frame)
    {
        zoom = zoom > 0 && double.IsFinite(zoom) ? zoom : 1;
        double nx = (2 * pageX - 1 - x * frame) / zoom, ny = (1 - 2 * pageY - y) / zoom;
        return ((nx + 1) / 2, (1 - ny) / 2);
    }

    // Checked in this order, so "hair" and "face" win over the head they sit on, and a hand over its arm.
    private static readonly (string Zone, string[] Words)[] Keywords =
    [
        ("hair", ["hair", "bang", "ahoge", "ponytail", "twintail", "braid", "髪", "kami"]),
        ("face", ["face", "cheek", "mouth", "nose", "eye", "brow", "顔", "頬", "口", "目"]),
        ("head", ["head", "頭", "耳"]),
        ("hand", ["hand", "finger", "palm", "thumb", "手", "指"]),
        ("arm", ["arm", "elbow", "sleeve", "shoulder", "腕", "肩"]),
        ("foot", ["foot", "feet", "toe", "shoe", "boot", "足"]),
        ("leg", ["leg", "thigh", "knee", "calf", "stocking", "sock", "脚", "腿"]),
        ("body", ["body", "chest", "breast", "bust", "torso", "belly", "stomach", "waist", "hip", "skirt", "neck", "collar",
            "cloth", "shirt", "dress", "体", "胸", "腰", "服"]),
    ];

    /// <summary>A coarse, built-in name for where the character was touched: "head", "hair", "face", "body", "arm", "hand", "leg"
    /// or "foot". From the VRM bone, else the Live2D hit areas, else the drawables' IDs (fuzzy, any language Martlet knows),
    /// else the point's height on the page.</summary>
    [JsonIgnore]
    public string CoarseZone => Hair ? "hair" : BoneZone(Bone) ?? HitAreas.Select(NameZone).FirstOrDefault(z => z is not null) ??
        Drawables.Select(NameZone).FirstOrDefault(z => z is not null) ?? (Y < 0.25 ? "head" : Y < 0.62 ? "body" : "leg");

    /// <summary>Finite fractions near the page, bounded lists and names without control characters.</summary>
    [JsonIgnore]
    public bool IsValid => double.IsFinite(X) && double.IsFinite(Y) && X is >= -0.01 and <= 1.01 && Y is >= -0.01 and <= 1.01 &&
        WholeX is null or (>= -4 and <= 5) && WholeY is null or (>= -4 and <= 5) &&
        HeldMilliseconds is >= 0 and <= MaximumHeldMilliseconds && HitAreas is { Count: <= MaximumHitAreas } && Drawables is { Count: <= MaximumDrawables } &&
        HitAreas.Concat(Drawables).All(Safe) && new[] { Bone, Node, Mesh, Material }.All(name => name is null || Safe(name));

    private static bool Safe(string? name) => name is { Length: > 0 and <= MaximumName } && !name.Any(char.IsControl);

    /// <summary>The rough part a VRM humanoid bone moves ("head", "face", "hand", "arm", "foot", "leg" or "body"), or null
    /// without a bone.</summary>
    internal static string? BoneZone(string? bone)
    {
        if (string.IsNullOrEmpty(bone)) return null;
        var name = bone.ToLowerInvariant();
        if (name is "jaw" or "lefteye" or "righteye") return "face";
        if (name == "head") return "head";
        if (name.Contains("hand") || name.Contains("thumb") || name.Contains("index") || name.Contains("middle") ||
            name.Contains("ring") || name.Contains("little")) return "hand";
        if (name.Contains("arm")) return "arm";
        if (name.Contains("foot") || name.Contains("toes")) return "foot";
        if (name.Contains("leg")) return "leg";
        return "body";
    }

    private static string? NameZone(string name)
    {
        var lower = name.ToLowerInvariant();
        foreach (var (zone, words) in Keywords)
            if (words.Any(word => lower.Contains(word, StringComparison.Ordinal))) return zone;
        return null;
    }
}
