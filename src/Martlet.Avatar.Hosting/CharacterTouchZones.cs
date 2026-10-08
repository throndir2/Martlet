using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Martlet.Core.Contracts;

namespace Martlet.Avatar.Hosting;

public enum TouchZoneGroup { Head, Torso, Arms, LowerBody, Extras }

/// <summary>One body zone Martlet knows: its ID (the vision model's name for it), label, group, whether it is intimate (only
/// used with Include intimate zones on), what telling the character says, and the default reaction: one gesture or emote from
/// each slot, the first of its names the model has.</summary>
public sealed record TouchZoneKind(string Id, string Label, TouchZoneGroup Group, bool Intimate, string Narration,
    IReadOnlyList<IReadOnlyList<string>> Defaults);

/// <summary>A box as fractions 0..1 of a picture (origin top-left, +y down).</summary>
public sealed record TouchZoneBox(double X, double Y, double Width, double Height)
{
    [JsonIgnore] public double Area => Width * Height;
    [JsonIgnore] public double CenterX => X + Width / 2;
    [JsonIgnore] public double CenterY => Y + Height / 2;
    [JsonIgnore] public bool Valid => double.IsFinite(X) && double.IsFinite(Y) && double.IsFinite(Width) && double.IsFinite(Height) &&
        Width > 0.002 && Height > 0.002 && X >= -0.01 && Y >= -0.01 && X + Width <= 1.01 && Y + Height <= 1.01;
    public bool Contains(double x, double y) => x >= X && x <= X + Width && y >= Y && y <= Y + Height;

    /// <summary>How far a point is from this box (0 inside it), in the same fractions.</summary>
    public double Distance(double x, double y) =>
        Math.Sqrt(Math.Pow(Math.Max(0, Math.Max(X - x, x - (X + Width))), 2) + Math.Pow(Math.Max(0, Math.Max(Y - y, y - (Y + Height))), 2));

    /// <summary>The fraction of <paramref name="other"/>'s area inside this box.</summary>
    public double Covers(TouchZoneBox other)
    {
        var w = Math.Min(X + Width, other.X + other.Width) - Math.Max(X, other.X);
        var h = Math.Min(Y + Height, other.Y + other.Height) - Math.Max(Y, other.Y);
        return w <= 0 || h <= 0 || other.Area <= 0 ? 0 : w * h / other.Area;
    }

    /// <summary>This box (a fraction of <paramref name="frame"/>) as a fraction of what <paramref name="frame"/> is a fraction of.</summary>
    public TouchZoneBox Within(TouchZoneBox frame) =>
        new(frame.X + X * frame.Width, frame.Y + Y * frame.Height, Width * frame.Width, Height * frame.Height);

    /// <summary>This box (a fraction of what <paramref name="frame"/> is a fraction of) as a fraction of <paramref name="frame"/>;
    /// the reverse of <see cref="Within"/>.</summary>
    public TouchZoneBox Relative(TouchZoneBox frame) =>
        new((X - frame.X) / frame.Width, (Y - frame.Y) / frame.Height, Width / frame.Width, Height / frame.Height);

    public TouchZoneBox Clamped()
    {
        double x = Math.Clamp(X, 0, 0.99), y = Math.Clamp(Y, 0, 0.99);
        return new(x, y, Math.Clamp(Width, 0.01, 1 - x), Math.Clamp(Height, 0.01, 1 - y));
    }
}

/// <summary>What touching a zone does: the gestures, emotes and motions it plays (their <see cref="CharacterActionSource.Id"/>s;
/// null plays the zone's default, an empty list nothing), whether Martlet notices it (<see cref="Notices"/>, on by default: the
/// touch goes to the Thinking model, with what the user says or as a short reply of its own, <see cref="Narration"/> an optional
/// hint in the owner's words) and how long the zone then rests.</summary>
public sealed record CharacterTouchReaction
{
    public const double DefaultCooldown = 4, MaximumCooldown = 600;
    public IReadOnlyList<string>? Actions { get; init; }
    /// <summary>Martlet notices touches on this zone (on unless the owner turns it off; was "Tell the character", saved as
    /// <c>tell</c> before).</summary>
    public bool Notices { get; init; } = true;
    // Settings saved before Martlet notices replaced Tell the character keep working.
    [JsonInclude, JsonPropertyName("tell")]
    private bool? Tell
    {
        get => null;
        init { if (value is { } tell) Notices = tell; }
    }
    public string? Narration { get; init; }
    public double CooldownSeconds { get; init; } = DefaultCooldown;
}

/// <summary>One area of a zone: its box in the snapshot (fractions of the picture) and the Live2D drawables, VRM humanoid bones
/// and VRM nodes (spring-bone joints, such as a tail's) that lie in it, so it follows the model as it moves. An area whose
/// parts come from the model itself (<see cref="FromModel"/>: a part the model names for the zone, or a part its physics swings,
/// such as a tail that hangs behind the legs at rest) follows those parts wherever they move; its box only says where they
/// were in the snapshot, so it never matches a touch by itself.</summary>
public sealed record CharacterTouchZoneArea
{
    public required TouchZoneBox Box { get; init; }
    public IReadOnlyList<string> Drawables { get; init; } = [];
    public IReadOnlyList<string> Bones { get; init; } = [];
    public IReadOnlyList<string> Nodes { get; init; } = [];
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool FromModel { get; init; }
}

/// <summary>One zone found on a model: its box in the snapshot (fractions of the picture), the Live2D drawables or VRM bones that
/// lie in it (so it follows the model as it moves), whether it is used, its reaction, and whether the owner added it. A zone can
/// have several areas (<see cref="Areas"/>: a tail in segments along its length, or boxes the owner drew); its box then holds them
/// all, and its drawables and bones are all of theirs.</summary>
public sealed record CharacterTouchZone
{
    public required string Id { get; init; }
    public string? Label { get; init; }
    public required TouchZoneBox Box { get; init; }
    public IReadOnlyList<string> Drawables { get; init; } = [];
    public IReadOnlyList<string> Bones { get; init; } = [];
    public bool Enabled { get; init; } = true;
    public CharacterTouchReaction Reaction { get; init; } = new();
    /// <summary>The owner added this zone (Add zone): Detect again looks for it too, beside
    /// <see cref="TouchZoneDetection.Defaults"/>, and keeps it where it is when it can't place it.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool Added { get; init; }
    /// <summary>The zone's areas when it has several (or one whose parts come from the model itself); null for a zone that is
    /// only its box.</summary>
    public IReadOnlyList<CharacterTouchZoneArea>? Areas { get; init; }
    /// <summary>What of the model the zone's areas follow, by its own names (such as 尾巴, or the physics settings that swing
    /// it), when they come from the model itself; null otherwise.</summary>
    public string? Follows { get; init; }

    [JsonIgnore] public string Name => Label ?? CharacterTouchZones.Kind(Id)?.Label ?? Id;

    /// <summary>Every area of the zone: its <see cref="Areas"/>, or for a zone that is only its box, that box (with the zone's
    /// drawables and bones) as its one area.</summary>
    [JsonIgnore]
    public IReadOnlyList<CharacterTouchZoneArea> AllAreas => Areas is { Count: > 0 } areas ? areas : [new() { Box = Box, Drawables = Drawables, Bones = Bones }];

    /// <summary>Whether a box of the zone holds a point of the page (<paramref name="x"/>, <paramref name="y"/>, fractions;
    /// <paramref name="crop"/> is where the snapshot sat on the page, null when the boxes are fractions of the page). An area whose
    /// parts come from the model itself moves away from its box, so its box never holds a touch.</summary>
    public bool Holds(TouchZoneBox? crop, double x, double y) =>
        AllAreas.Any(a => !a.FromModel && (crop is null ? a.Box : a.Box.Within(crop)).Contains(x, y));
}

/// <summary>One model's touch zones (Companion › Touch › Touch zones). <see cref="Crop"/> is where the snapshot the zones were
/// found in sat on the renderer page (fractions of the page), so a zone's box can be compared with a touch.</summary>
public sealed record CharacterTouchZoneSettings
{
    public const string ByVision = "vision", ByOwner = "owner", ByEstimate = "estimate";
    public required string ModelId { get; init; }
    /// <summary>Who placed the zones: the Thinking model (<see cref="ByVision"/>), Martlet's first guess from the character's
    /// picture with no AI (<see cref="ByEstimate"/>), or the owner (<see cref="ByOwner"/>).</summary>
    public string? DetectedBy { get; init; }
    public DateTimeOffset? DetectedAt { get; init; }
    public DateTimeOffset UpdatedAt { get; init; }
    public bool IncludeIntimate { get; init; } = true;
    public TouchZoneBox? Crop { get; init; }
    /// <summary>The snapshot framed the character whole (no zoom, no pan): boxes compare with where a touch lands in that
    /// framing (<see cref="CharacterTouch.WholeX"/>), whatever the view is now.</summary>
    public bool Whole { get; init; }
    public IReadOnlyList<CharacterTouchZone> Zones { get; init; } = [];

    /// <summary>Whether touching <paramref name="zone"/> does anything: it is on, and an intimate zone only with Include intimate zones.</summary>
    public bool Active(CharacterTouchZone zone) => zone.Enabled && (IncludeIntimate || CharacterTouchZones.Kind(zone.Id)?.Intimate != true);
}

/// <summary>A Live2D drawable's bounds on the renderer page (fractions of the page), from the renderer's zones probe, and the ID of
/// the model's part it belongs to (null when the renderer can't say).</summary>
public sealed record RendererDrawableBox(string Id, double Left, double Top, double Right, double Bottom, string? Part = null);
/// <summary>A VRM humanoid bone's place on the renderer page (fractions of the page).</summary>
public sealed record RendererBonePoint(string Bone, double X, double Y);
/// <summary>A Live2D model's own part (a group of drawables and parts): its ID, the name its DisplayInfo file (*.cdi3.json) gives
/// it, in any language (null when it gives none), and the ID of the part it belongs to (null for a top part).</summary>
public sealed record RendererModelPart(string Id, string? Name = null, string? Parent = null);
/// <summary>A part of a Live2D model that swings on its own (a tail, a ponytail, a skirt, ears), from its physics file: the names
/// of the physics settings that swing it (尾巴, Hair Front...; null when they have none), its drawables from the one that moves
/// least (its root) to the one that moves most (its tip), and where it can reach (its bounds at rest and swung both ways,
/// fractions of the page).</summary>
public sealed record RendererChain(string? Name, string[] Drawables, double Left, double Top, double Right, double Bottom)
{
    public const int MaximumChains = 48, MaximumDrawables = 256;

    /// <summary>A name of bounded length, at least one drawable, bounded lists and finite bounds.</summary>
    [JsonIgnore]
    public bool IsValid => (Name is null || Name.Length <= 256) && Drawables is { Length: > 0 and <= MaximumDrawables } &&
        Drawables.All(id => id is { Length: > 0 and <= 256 }) && double.IsFinite(Left) && double.IsFinite(Top) && double.IsFinite(Right) &&
        double.IsFinite(Bottom);
}
/// <summary>A chain of a VRM's spring-bone joints (hair, a tail, a skirt, ears): its name (its first joint's node's name) and
/// each joint's node and place on the page (fractions), from its root to its tip.</summary>
public sealed record RendererSpring(string? Name, RendererBonePoint[] Joints)
{
    public const int MaximumSprings = 64, MaximumJoints = 64;

    /// <summary>A name of bounded length and at least one joint with a short name and a finite place.</summary>
    [JsonIgnore]
    public bool IsValid => (Name is null || Name.Length <= 256) && Joints is { Length: > 0 and <= MaximumJoints } &&
        Joints.All(j => j is { Bone.Length: > 0 and <= 256 } && double.IsFinite(j.X) && double.IsFinite(j.Y));
}
/// <summary>The zones probe's reply: where the showing model's drawables (Live2D) or humanoid bones (VRM) are now. A whole
/// picture's probe also says where the face was in it (<see cref="Face"/>, from the renderer's face anchor), which the eye
/// measurement crops around, a Live2D model's own parts (<see cref="Parts"/>), so its part names can find and check zones, and
/// the parts of it that swing on their own (a Live2D model's <see cref="Chains"/>, a VRM's <see cref="Springs"/>), so a zone on
/// a tail follows all of the tail.</summary>
public sealed record RendererZoneProbe(RendererDrawableBox[]? Drawables = null, RendererBonePoint[]? Bones = null, RendererFace? Face = null,
    RendererModelPart[]? Parts = null, RendererChain[]? Chains = null, RendererSpring[]? Springs = null);

/// <summary>Which zone a touch landed in and how it was found ("drawable", "node", "bone", "hair", "box" or "coarse"), and for a
/// zone with several areas, which of them (its index in <see cref="CharacterTouchZone.AllAreas"/>).</summary>
public sealed record TouchZoneMatch(CharacterTouchZone Zone, string How, int? Area = null);

/// <summary>Touch zones: the body zones Martlet knows, the vision model's request and how its answer is read, binding zones to a
/// model's drawables or bones, matching a touch to a zone, the reaction it plays, and the per-model settings kept in
/// character-touch-zones.json (snapshots in character-touch-zones\) on this PC.</summary>
public static partial class CharacterTouchZones
{
    public const string FileName = "character-touch-zones.json";
    public const string SnapshotFolder = "character-touch-zones";
    public const int MaximumModels = 32, MaximumZones = 64, MaximumBytes = 4 * 1024 * 1024, MaximumActions = 3;
    /// <summary>At most this many areas make up one zone.</summary>
    public const int MaximumAreas = 8;
    public const int MaximumNarrationLength = 160, MaximumLabelLength = 40;
    // A drawable belongs to a zone when this much of its bounds lies inside the zone's box.
    public const double MostlyInside = 0.6;
    /// <summary>Every intimate zone kind in plain words, left and right together (Include intimate zones and the touch
    /// temperament's Intimate parts name them so).</summary>
    public const string IntimateParts = "mouth, ears, neck, chest and breasts, waist and sides, hips, groin, buttocks and inner thighs";

    private static readonly string[][] HeadPat = [["lean_in", "tilt"], ["smile", "happy"]];
    private static readonly string[][] Face = [["tilt", "nod"], ["smile", "happy"]];
    private static readonly string[][] Eye = [["flinch", "surprise"], ["pout", "tilt"]];
    private static readonly string[][] Cheek = [["blush"], ["shy", "smile"]];
    private static readonly string[][] Intimate = [["blush"], ["flinch", "surprise", "gasp"]];
    private static readonly string[][] Tickle = [["flinch", "surprise"], ["giggle", "laugh", "chuckle"]];
    private static readonly string[][] Arm = [["tilt", "nod"], ["smile", "happy"]];
    private static readonly string[][] Hand = [["smile", "happy"], ["nod"]];
    private static readonly string[][] Leg = [["surprise", "flinch"], ["pout", "tilt"]];
    private static readonly string[][] Extra = [["tilt"], ["smile", "happy"]];
    private static readonly string[][] Startle = [["surprise", "flinch"], ["blush"]];

    private static TouchZoneKind Z(string id, string label, TouchZoneGroup group, string narration, string[][] defaults, bool intimate = false) =>
        new(id, label, group, intimate, narration, defaults);

    /// <summary>Every zone Martlet knows, in the order the vision model is asked for them. Detect zones looks for
    /// <see cref="TouchZoneDetection.Defaults"/> and the ones the owner added; Add zone offers the rest. Left and right are the
    /// character's own.</summary>
    public static readonly IReadOnlyList<TouchZoneKind> Kinds =
    [
        Z("top_of_head", "Top of head", TouchZoneGroup.Head, "*gently pats your head*", HeadPat),
        Z("hair", "Hair", TouchZoneGroup.Head, "*runs fingers through your hair*", HeadPat),
        Z("forehead", "Forehead", TouchZoneGroup.Head, "*pokes your forehead*", Face),
        Z("face", "Face", TouchZoneGroup.Head, "*touches your face*", Face),
        Z("eye_left", "Left eye", TouchZoneGroup.Head, "*touches near your eye*", Eye),
        Z("eye_right", "Right eye", TouchZoneGroup.Head, "*touches near your eye*", Eye),
        Z("cheek_left", "Left cheek", TouchZoneGroup.Head, "*pokes your cheek*", Cheek),
        Z("cheek_right", "Right cheek", TouchZoneGroup.Head, "*pokes your cheek*", Cheek),
        Z("nose", "Nose", TouchZoneGroup.Head, "*boops your nose*", Face),
        Z("lips", "Mouth", TouchZoneGroup.Head, "*touches your lips*", Intimate, true),
        Z("chin", "Chin", TouchZoneGroup.Head, "*tilts your chin up*", Face),
        Z("ear_left", "Left ear", TouchZoneGroup.Head, "*touches your ear*", Intimate, true),
        Z("ear_right", "Right ear", TouchZoneGroup.Head, "*touches your ear*", Intimate, true),
        Z("neck", "Neck", TouchZoneGroup.Torso, "*touches your neck*", Intimate, true),
        Z("shoulder_left", "Left shoulder", TouchZoneGroup.Torso, "*taps your shoulder*", Arm),
        Z("shoulder_right", "Right shoulder", TouchZoneGroup.Torso, "*taps your shoulder*", Arm),
        Z("collarbone", "Collarbone", TouchZoneGroup.Torso, "*touches your collarbone*", Startle),
        Z("chest", "Chest", TouchZoneGroup.Torso, "*touches your chest*", Intimate, true),
        Z("breast_left", "Left breast", TouchZoneGroup.Torso, "*touches your chest*", Intimate, true),
        Z("breast_right", "Right breast", TouchZoneGroup.Torso, "*touches your chest*", Intimate, true),
        Z("stomach", "Stomach", TouchZoneGroup.Torso, "*pokes your tummy*", Tickle),
        Z("navel", "Navel", TouchZoneGroup.Torso, "*pokes your belly button*", Tickle),
        Z("waist", "Waist and sides", TouchZoneGroup.Torso, "*tickles your sides*", Intimate, true),
        Z("lower_back", "Lower back", TouchZoneGroup.Torso, "*touches your lower back*", Tickle),
        Z("upper_arm_left", "Left upper arm", TouchZoneGroup.Arms, "*taps your arm*", Arm),
        Z("upper_arm_right", "Right upper arm", TouchZoneGroup.Arms, "*taps your arm*", Arm),
        Z("forearm_left", "Left forearm", TouchZoneGroup.Arms, "*touches your arm*", Arm),
        Z("forearm_right", "Right forearm", TouchZoneGroup.Arms, "*touches your arm*", Arm),
        Z("hand_left", "Left hand", TouchZoneGroup.Arms, "*holds your hand*", Hand),
        Z("hand_right", "Right hand", TouchZoneGroup.Arms, "*holds your hand*", Hand),
        Z("hips", "Hips", TouchZoneGroup.LowerBody, "*touches your hips*", Intimate, true),
        Z("hip_left", "Left hip", TouchZoneGroup.LowerBody, "*touches your hip*", Intimate, true),
        Z("hip_right", "Right hip", TouchZoneGroup.LowerBody, "*touches your hip*", Intimate, true),
        Z("groin", "Groin", TouchZoneGroup.LowerBody, "*touches you between the legs*", Intimate, true),
        Z("buttocks", "Buttocks", TouchZoneGroup.LowerBody, "*pats your bottom*", Intimate, true),
        Z("thigh_left", "Left thigh", TouchZoneGroup.LowerBody, "*touches your thigh*", Leg),
        Z("thigh_right", "Right thigh", TouchZoneGroup.LowerBody, "*touches your thigh*", Leg),
        Z("inner_thigh_left", "Left inner thigh", TouchZoneGroup.LowerBody, "*touches your inner thigh*", Intimate, true),
        Z("inner_thigh_right", "Right inner thigh", TouchZoneGroup.LowerBody, "*touches your inner thigh*", Intimate, true),
        Z("knee_left", "Left knee", TouchZoneGroup.LowerBody, "*taps your knee*", Leg),
        Z("knee_right", "Right knee", TouchZoneGroup.LowerBody, "*taps your knee*", Leg),
        Z("calf_left", "Left calf", TouchZoneGroup.LowerBody, "*touches your leg*", Leg),
        Z("calf_right", "Right calf", TouchZoneGroup.LowerBody, "*touches your leg*", Leg),
        Z("foot_left", "Left foot", TouchZoneGroup.LowerBody, "*tickles your foot*", Tickle),
        Z("foot_right", "Right foot", TouchZoneGroup.LowerBody, "*tickles your foot*", Tickle),
        Z("animal_ears", "Animal ears", TouchZoneGroup.Extras, "*strokes your ears*", HeadPat),
        Z("tail", "Tail", TouchZoneGroup.Extras, "*strokes your tail*", Startle),
        Z("horns", "Horns", TouchZoneGroup.Extras, "*touches your horns*", Extra),
        Z("wings", "Wings", TouchZoneGroup.Extras, "*strokes your wings*", Startle),
        Z("glasses_or_hat", "Glasses or hat", TouchZoneGroup.Extras, "*adjusts your glasses*", Extra),
        Z("skirt_hem", "Skirt hem", TouchZoneGroup.Extras, "*tugs at your skirt*", Intimate),
        Z("held_item", "Held item", TouchZoneGroup.Extras, "*touches what you're holding*", Extra)
    ];

    private static readonly Dictionary<string, string> Aliases = new(StringComparer.Ordinal)
    {
        ["head"] = "top_of_head", ["top_head"] = "top_of_head", ["crown"] = "top_of_head", ["eyes"] = "face", ["face_eyes"] = "face",
        ["mouth"] = "lips", ["lip"] = "lips", ["sides"] = "waist", ["waist_sides"] = "waist", ["belly"] = "stomach",
        ["butt"] = "buttocks", ["crotch"] = "groin", ["pelvis"] = "groin", ["between_legs"] = "groin", ["genitals"] = "groin",
        ["genital_area"] = "groin", ["pubic_area"] = "groin", ["glasses"] = "glasses_or_hat", ["hat"] = "glasses_or_hat", ["skirt"] = "skirt_hem",
        ["left_cheek"] = "cheek_left", ["right_cheek"] = "cheek_right", ["left_ear"] = "ear_left", ["right_ear"] = "ear_right",
        ["left_shoulder"] = "shoulder_left", ["right_shoulder"] = "shoulder_right", ["left_hand"] = "hand_left",
        ["right_hand"] = "hand_right", ["left_foot"] = "foot_left", ["right_foot"] = "foot_right", ["left_knee"] = "knee_left",
        ["right_knee"] = "knee_right", ["left_thigh"] = "thigh_left", ["right_thigh"] = "thigh_right", ["left_breast"] = "breast_left",
        ["right_breast"] = "breast_right", ["left_upper_arm"] = "upper_arm_left", ["right_upper_arm"] = "upper_arm_right",
        ["left_forearm"] = "forearm_left", ["right_forearm"] = "forearm_right", ["left_calf"] = "calf_left", ["right_calf"] = "calf_right",
        ["left_inner_thigh"] = "inner_thigh_left", ["right_inner_thigh"] = "inner_thigh_right", ["left_eye"] = "eye_left",
        ["right_eye"] = "eye_right", ["left_hip"] = "hip_left", ["right_hip"] = "hip_right",
        // Plain names for the parts of a limb: the lower arm is the forearm, the upper leg the thigh and the lower leg the calf.
        ["left_lower_arm"] = "forearm_left", ["right_lower_arm"] = "forearm_right", ["lower_arm_left"] = "forearm_left",
        ["lower_arm_right"] = "forearm_right", ["left_upper_leg"] = "thigh_left", ["right_upper_leg"] = "thigh_right",
        ["upper_leg_left"] = "thigh_left", ["upper_leg_right"] = "thigh_right", ["left_lower_leg"] = "calf_left",
        ["right_lower_leg"] = "calf_right", ["lower_leg_left"] = "calf_left", ["lower_leg_right"] = "calf_right"
    };

    // A's rough zones, when no found zone matched: the found zones to try, else the first one's default reaction.
    private static readonly Dictionary<string, string[]> Coarse = new(StringComparer.Ordinal)
    {
        ["head"] = ["top_of_head", "hair", "forehead"], ["hair"] = ["hair", "top_of_head"], ["face"] = ["face", "forehead", "nose"],
        ["body"] = ["stomach", "navel", "lower_back"], ["arm"] = ["upper_arm_right", "upper_arm_left", "forearm_right", "forearm_left"],
        ["hand"] = ["hand_right", "hand_left"], ["leg"] = ["thigh_right", "thigh_left", "knee_right", "knee_left", "calf_right", "calf_left"],
        ["foot"] = ["foot_right", "foot_left"]
    };

    public static TouchZoneKind? Kind(string id) => Kinds.FirstOrDefault(k => k.Id == id);

    /// <summary>A zone ID as Martlet knows it (lower case, underscores, common names mapped), or null when it isn't one.</summary>
    public static string? Normalize(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        var slug = string.Join("_", name.Trim().ToLowerInvariant().Split([' ', '-', '/', '.', ',', '&'], StringSplitOptions.RemoveEmptyEntries));
        slug = slug.Replace("_and_", "_").Replace("_or_", "_or_");
        if (Kind(slug) is not null) return slug;
        if (Aliases.TryGetValue(slug, out var alias)) return alias;
        return slug == "glasses_hat" ? "glasses_or_hat" : null;
    }

    // ---------- the vision model's answer ----------

    /// <summary>The zones in a vision model's answer about a whole <paramref name="width"/> by <paramref name="height"/> picture
    /// (the fixture's stand-in answer and MCP's simulated one), with their boxes as fractions of the picture: JSON with boxes as
    /// arrays or named edges, a bare list, boxes keyed by zone, or Qwen-style <c>bbox_2d</c> grounding in pixels or 0..1000; an
    /// answer cut off part way keeps the zones it finished. Unknown zones and boxes that can't be placed are left out; null when
    /// nothing could be read. Detection itself asks step by step (<see cref="TouchZoneDetection"/>).</summary>
    public static IReadOnlyList<CharacterTouchZone>? Parse(string? answer, int width, int height)
    {
        if (width <= 0 || height <= 0) return null;
        var zones = TouchZoneDetection.ReadBoxes(answer, width, height, Normalize).Take(MaximumZones)
            .Select(z => new CharacterTouchZone { Id = z.Key, Box = z.Value.Clamped(), Enabled = true }).ToArray();
        return zones.Length == 0 ? null : zones;
    }

    // ---------- binding zones to the model ----------

    // The renderer page's shape (width to height: the overlay's 3:4 frame with the room beside it), so distances on it are even.
    private const double PageAspect = 1.5;

    // The next humanoid bone along the body after each one (the first of these the model has): a bone moves the part from its
    // joint to that bone's joint (the shin bone: the knee to the ankle). The head's part reaches up to between the eyes.
    private static readonly Dictionary<string, string[]> NextBones = new(StringComparer.Ordinal)
    {
        ["hips"] = ["spine"], ["spine"] = ["chest", "upperChest", "neck"], ["chest"] = ["upperChest", "neck"], ["upperChest"] = ["neck"],
        ["neck"] = ["head"],
        ["leftShoulder"] = ["leftUpperArm"], ["leftUpperArm"] = ["leftLowerArm"], ["leftLowerArm"] = ["leftHand"], ["leftHand"] = ["leftMiddleProximal"],
        ["rightShoulder"] = ["rightUpperArm"], ["rightUpperArm"] = ["rightLowerArm"], ["rightLowerArm"] = ["rightHand"], ["rightHand"] = ["rightMiddleProximal"],
        ["leftUpperLeg"] = ["leftLowerLeg"], ["leftLowerLeg"] = ["leftFoot"], ["leftFoot"] = ["leftToes"],
        ["rightUpperLeg"] = ["rightLowerLeg"], ["rightLowerLeg"] = ["rightFoot"], ["rightFoot"] = ["rightToes"]
    };

    // The part each humanoid bone in the probe moves, from its joint to the next bone's (just its joint for a last bone such as a
    // toe or a finger tip). The hips move the pelvis: from their joint down through the middle of the hip joints to the crotch, a
    // quarter of the way on to the knees. The eyes and the jaw are left out: they lie inside the head's part.
    private static List<(string Bone, double FromX, double FromY, double ToX, double ToY)> BoneParts(IReadOnlyList<RendererBonePoint> joints)
    {
        var at = new Dictionary<string, RendererBonePoint>(StringComparer.Ordinal);
        foreach (var joint in joints) at.TryAdd(joint.Bone, joint);
        var parts = new List<(string, double, double, double, double)>();
        foreach (var joint in at.Values)
        {
            if (joint.Bone is "leftEye" or "rightEye" or "jaw") continue;
            var next = NextBones.TryGetValue(joint.Bone, out var names) ? names.Select(n => at.GetValueOrDefault(n)).FirstOrDefault(n => n is not null) : null;
            (double X, double Y)? to = null;
            if (joint.Bone == "hips" && Middle("leftUpperLeg", "rightUpperLeg") is { } hip)
                to = Middle("leftLowerLeg", "rightLowerLeg") is { } knee ? (hip.X + (knee.X - hip.X) / 4, hip.Y + (knee.Y - hip.Y) / 4)
                    : (2 * hip.X - joint.X, 2 * hip.Y - joint.Y);
            else if (next is not null) to = (next.X, next.Y);
            else if (joint.Bone == "head" && Middle("leftEye", "rightEye") is { } eyes) to = eyes;
            var (toX, toY) = to ?? (joint.X, joint.Y);
            parts.Add((joint.Bone, joint.X, joint.Y, toX, toY));
        }
        return parts;

        (double X, double Y)? Middle(string left, string right) =>
            at.TryGetValue(left, out var l) && at.TryGetValue(right, out var r) ? ((l.X + r.X) / 2, (l.Y + r.Y) / 2) : null;
    }

    // How far a point of the page is from a bone's part (a line from its joint to the next bone's), with the page's width
    // counted in its height.
    private static double Distance((string Bone, double FromX, double FromY, double ToX, double ToY) part, double x, double y)
    {
        double ax = part.FromX * PageAspect, ay = part.FromY, dx = part.ToX * PageAspect - ax, dy = part.ToY - ay, px = x * PageAspect, py = y;
        var length = dx * dx + dy * dy;
        var t = length > 0 ? Math.Clamp(((px - ax) * dx + (py - ay) * dy) / length, 0, 1) : 0;
        return Math.Sqrt(Math.Pow(px - (ax + t * dx), 2) + Math.Pow(py - (ay + t * dy), 2));
    }

    // Whether a bone's part (a line from its joint to the next bone's) crosses a box.
    private static bool Crosses((string Bone, double FromX, double FromY, double ToX, double ToY) part, TouchZoneBox box)
    {
        double dx = part.ToX - part.FromX, dy = part.ToY - part.FromY, enter = 0, leave = 1;
        foreach (var (p, q) in new[] { (-dx, part.FromX - box.X), (dx, box.X + box.Width - part.FromX), (-dy, part.FromY - box.Y),
            (dy, box.Y + box.Height - part.FromY) })
        {
            if (p == 0) { if (q < 0) return false; continue; }
            var r = q / p;
            if (p < 0) enter = Math.Max(enter, r); else leave = Math.Min(leave, r);
            if (enter > leave) return false;
        }
        return true;
    }

    /// <summary>The zones with the drawables (Live2D), humanoid bones and spring-bone joints (VRM) that lie in each of their areas:
    /// a drawable whose bounds are mostly (<see cref="MostlyInside"/>) inside an area's box; a bone of the zone's own part of the
    /// body (<see cref="OnPart"/>) whose joint is inside the box or whose part (from its joint to the next bone's) crosses it, else
    /// those whose part crosses the box grown by half its size, else the one whose part passes nearest (a cheek: the head; a
    /// breast: the chest; the groin: the hips). A tail, wings or animal ears then follow the model's own part for them (one it
    /// names so, or one its physics swings that the box holds some of), all of it even where it hides behind the body at rest,
    /// in areas along it; hair also follows the swinging hair its box doesn't hold (a ponytail). A drawable a zone follows so
    /// belongs to that zone only (<see cref="Follow"/>). <paramref name="crop"/> is where the snapshot sat on the page (null:
    /// the boxes are already fractions of the page).</summary>
    public static IReadOnlyList<CharacterTouchZone> Bind(IReadOnlyList<CharacterTouchZone> zones, TouchZoneBox? crop, RendererZoneProbe? probe)
    {
        if (probe is null) return zones;
        var joints = (probe.Bones ?? []).Where(b => double.IsFinite(b.X) && double.IsFinite(b.Y) && !string.IsNullOrEmpty(b.Bone)).ToArray();
        var parts = BoneParts(joints);
        var model = ModelStructure.Read(probe, crop);
        var bound = new CharacterTouchZone[zones.Count];
        // Zones that follow the model's own parts go first, so a tail takes its parts before another zone's box does.
        foreach (var i in Enumerable.Range(0, zones.Count).OrderBy(i => FollowOrder(zones[i].Id)))
        {
            var zone = zones[i];
            var boxes = zone.AllAreas.Where(a => !a.FromModel).Select(a => BindArea(zone.Id, a, crop, probe, joints, parts)).ToList();
            bound[i] = Follow(zone, boxes, crop, model) ?? Compose(zone, boxes.Count > 0 ? boxes
                : [BindArea(zone.Id, new() { Box = zone.Box }, crop, probe, joints, parts)], null);
        }
        // A drawable a zone follows by the model's own parts belongs to that zone only: other zones' boxes let it go.
        var followed = bound.SelectMany(z => z.AllAreas.Where(a => a.FromModel).Select(a => (z.Id, a.Drawables)))
            .SelectMany(f => f.Drawables.Select(d => (d, f.Id))).GroupBy(f => f.d, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First().Id, StringComparer.Ordinal);
        if (followed.Count == 0) return bound;
        return [.. bound.Select(zone => zone.AllAreas.Any(a => !a.FromModel && a.Drawables.Any(d => followed.TryGetValue(d, out var by) && by != zone.Id))
            ? Compose(zone, [.. zone.AllAreas.Select(a => a.FromModel ? a
                : a with { Drawables = [.. a.Drawables.Where(d => !followed.TryGetValue(d, out var by) || by == zone.Id)] })], zone.Follows)
            : zone)];
    }

    // One area's drawables, bones and joints, from its box (see Bind).
    private static CharacterTouchZoneArea BindArea(string zoneId, CharacterTouchZoneArea area, TouchZoneBox? crop, RendererZoneProbe probe,
        IReadOnlyList<RendererBonePoint> joints, List<(string Bone, double FromX, double FromY, double ToX, double ToY)> parts)
    {
        var page = crop is null ? area.Box : area.Box.Within(crop);
        var drawables = (probe.Drawables ?? []).Where(d => d.Right > d.Left && d.Bottom > d.Top &&
                page.Covers(new(d.Left, d.Top, d.Right - d.Left, d.Bottom - d.Top)) >= MostlyInside)
            .Select(d => d.Id).Distinct(StringComparer.Ordinal).Take(256).ToArray();
        var bones = joints.Where(b => page.Contains(b.X, b.Y)).Select(b => b.Bone).Concat(parts.Where(p => Crosses(p, page)).Select(p => p.Bone))
            .Where(b => OnPart(zoneId, b)).Distinct(StringComparer.Ordinal).Take(32).ToArray();
        if (bones.Length == 0)
        {
            var near = parts.Where(p => OnPart(zoneId, p.Bone)).ToList();
            if (near.Count == 0) near = parts;
            var grown = new TouchZoneBox(page.X - page.Width / 2, page.Y - page.Height / 2, page.Width * 2, page.Height * 2);
            bones = near.Where(p => Crosses(p, grown)).Select(p => p.Bone).Distinct(StringComparer.Ordinal).Take(32).ToArray();
            if (bones.Length == 0 && near.Count > 0) bones = [near.MinBy(p => Distance(p, page.CenterX, page.CenterY)).Bone];
        }
        return area with { Drawables = drawables, Bones = bones, Nodes = [], FromModel = false };
    }

    /// <summary>A zone made of <paramref name="areas"/>: a zone with one area of its own box is only that box; otherwise its box
    /// holds all its areas, and its drawables and bones are all of theirs.</summary>
    public static CharacterTouchZone Compose(CharacterTouchZone zone, IReadOnlyList<CharacterTouchZoneArea> areas, string? follows)
    {
        if (areas.Count == 1 && areas[0] is { FromModel: false, Nodes.Count: 0 } only)
            return zone with { Box = only.Box, Drawables = only.Drawables, Bones = only.Bones, Areas = null, Follows = null };
        double left = areas.Min(a => a.Box.X), top = areas.Min(a => a.Box.Y);
        return zone with
        {
            Box = new TouchZoneBox(left, top, areas.Max(a => a.Box.X + a.Box.Width) - left, areas.Max(a => a.Box.Y + a.Box.Height) - top).Clamped(),
            Drawables = [.. areas.SelectMany(a => a.Drawables).Distinct(StringComparer.Ordinal)],
            Bones = [.. areas.SelectMany(a => a.Bones).Distinct(StringComparer.Ordinal)],
            Areas = [.. areas], Follows = follows
        };
    }

    /// <summary>Newly found zones merged with the saved ones: a zone found again keeps the owner's label, choice, reaction and
    /// whether the owner added it. A zone the owner added (<see cref="CharacterTouchZone.Added"/>) that wasn't found again stays
    /// where it was; any other zone not found again is dropped, unless <paramref name="keepUnfound"/> (a detection still
    /// running over the first guess: its zones not found yet stay too). <paramref name="whole"/>: the snapshot framed the
    /// character whole.</summary>
    public static CharacterTouchZoneSettings Detected(CharacterTouchZoneSettings? saved, string modelId, IReadOnlyList<CharacterTouchZone> found,
        TouchZoneBox? crop, RendererZoneProbe? probe, DateTimeOffset now, bool whole = false, bool keepUnfound = false)
    {
        var merged = found.Select(zone => saved?.Zones.FirstOrDefault(z => z.Id == zone.Id) is { } old
            ? zone with { Label = old.Label, Enabled = old.Enabled, Reaction = old.Reaction, Added = old.Added } : zone).ToList();
        // Its boxes were fractions of the earlier snapshot: they stay on the same place of the page in the new one.
        TouchZoneBox Rebased(TouchZoneBox box) => saved?.Crop is { } was && crop is { } next && was != next ? box.Within(was).Relative(next).Clamped() : box;
        merged.AddRange(saved?.Zones.Where(z => (z.Added || keepUnfound) && merged.All(m => m.Id != z.Id))
            .Select(z => z with { Box = Rebased(z.Box), Areas = z.Areas?.Select(a => a with { Box = Rebased(a.Box) }).ToArray() }) ?? []);
        return new()
        {
            ModelId = modelId, DetectedBy = CharacterTouchZoneSettings.ByVision, DetectedAt = now.ToUniversalTime(), UpdatedAt = now.ToUniversalTime(),
            IncludeIntimate = saved?.IncludeIntimate ?? true, Crop = crop, Whole = whole, Zones = [.. Bind(merged, crop, probe).OrderBy(z => Order(z.Id))]
        };
    }

    /// <summary><paramref name="settings"/> bound again to the model with the probe of the picture its zones are on
    /// (<paramref name="probe"/>, kept with the picture: <see cref="SnapshotProbePath"/>), after the owner moved, added or changed
    /// zones: each area takes the drawables and bones in its box again, and a zone on a tail follows all of it (<see cref="Bind"/>).
    /// Unchanged without a probe of that same picture.</summary>
    public static CharacterTouchZoneSettings Rebind(CharacterTouchZoneSettings settings, TouchZoneProbeFile? probe) =>
        probe is null || settings.Crop is not { } crop || TouchZoneDetection.Moved(crop, probe.Crop) > 1e-6 ? settings
            : settings with { Zones = Bind(settings.Zones, crop, probe.Probe) };

    /// <summary>A first guess at a model's zones (<see cref="TouchZoneDetection.Estimate"/>, fractions of a snapshot framed whole
    /// that sat at <paramref name="crop"/> on the page), bound and merged with the saved ones as detected zones are, and marked
    /// <see cref="CharacterTouchZoneSettings.ByEstimate"/>.</summary>
    public static CharacterTouchZoneSettings Estimated(CharacterTouchZoneSettings? saved, string modelId, IReadOnlyList<CharacterTouchZone> guessed,
        TouchZoneBox? crop, RendererZoneProbe? probe, DateTimeOffset now) =>
        Detected(saved, modelId, guessed, crop, probe, now, whole: true) with { DetectedBy = CharacterTouchZoneSettings.ByEstimate };

    /// <summary>Where a zone comes in <see cref="Kinds"/> (a zone Martlet doesn't know comes last).</summary>
    public static int Order(string id)
    {
        for (var i = 0; i < Kinds.Count; i++)
            if (Kinds[i].Id == id) return i;
        return int.MaxValue;
    }

    // ---------- matching a touch ----------

    // The rough parts of the body (CharacterTouch.BoneZone of a VRM bone) each group's zones lie on.
    private static readonly string[] HeadParts = ["head", "face", "hair"], TorsoParts = ["body"], ArmParts = ["arm", "hand"],
        LowerBodyParts = ["leg", "foot", "body"];

    /// <summary>Whether a zone lies on the part of the body a VRM <paramref name="bone"/> moves, on the same side: the head's
    /// zones on the head bone, the left hand's on the left hand or arm, the hips' on the hips or a leg, and so on. A zone Martlet
    /// doesn't know lies on every part.</summary>
    public static bool OnPart(string zoneId, string bone)
    {
        string[]? parts = zoneId switch
        {
            "neck" => ["body", "head", "face"],
            "shoulder_left" or "shoulder_right" => ["body", "arm"],
            "animal_ears" or "horns" or "glasses_or_hat" => HeadParts,
            "tail" or "wings" or "skirt_hem" => ["body", "leg"],
            "held_item" => ArmParts,
            _ => Kind(zoneId)?.Group switch
            {
                TouchZoneGroup.Head => HeadParts, TouchZoneGroup.Torso => TorsoParts, TouchZoneGroup.Arms => ArmParts,
                TouchZoneGroup.LowerBody => LowerBodyParts, _ => null
            }
        };
        if (parts is not null && CharacterTouch.BoneZone(bone) is { } part && !parts.Contains(part, StringComparer.Ordinal)) return false;
        // Left and right are the character's own, in zone IDs and VRM bone names alike.
        var side = zoneId.EndsWith("_left", StringComparison.Ordinal) ? "left" : zoneId.EndsWith("_right", StringComparison.Ordinal) ? "right" : null;
        return side is null || !(bone.StartsWith("left", StringComparison.Ordinal) || bone.StartsWith("right", StringComparison.Ordinal)) ||
            bone.StartsWith(side, StringComparison.Ordinal);
    }

    /// <summary>The zone a touch landed in: the topmost touched drawable that belongs to a zone in use (an area that follows the
    /// model's own parts, such as a tail, wherever it moved; with several, the smallest area whose box holds the point, else the
    /// nearest), hair, a touched VRM spring-bone joint an area follows, then for a touched VRM bone the smallest area of a zone in
    /// use on that part of the body (<see cref="OnPart"/>) whose box holds the point (a bone moves a whole part, such as the head,
    /// and its zones are found in a picture in the same pose), else the area on that part that holds the bone, nearest the point
    /// (the part has moved since); without a bone, the smallest area box in use that holds the point; then the touch's rough zone
    /// (a found zone of that kind, else that kind's default zone). An area that follows the model's own parts moves away from its
    /// box, so its box never holds a touch. Null when nothing fits (a touch only on zones that aren't in use).</summary>
    public static TouchZoneMatch? Match(CharacterTouchZoneSettings? settings, CharacterTouch touch)
    {
        var active = settings?.Zones.Where(settings.Active).ToArray() ?? [];
        TouchZoneBox Page(TouchZoneBox box) => settings?.Crop is { } crop ? box.Within(crop) : box;
        // Zones found with the character framed whole compare with where the touch lands in that framing.
        var (x, y) = settings is { Whole: true } && touch is { WholeX: { } wholeX, WholeY: { } wholeY } ? (wholeX, wholeY) : (touch.X, touch.Y);
        static IEnumerable<ZoneArea> Areas(IEnumerable<CharacterTouchZone> zones) => zones.SelectMany(z => z.AllAreas.Select((a, i) => new ZoneArea(z, i, a)));
        ZoneArea? Under(IEnumerable<ZoneArea> areas) =>
            areas.Where(t => !t.Area.FromModel && Page(t.Area.Box).Contains(x, y)).OrderBy(t => t.Area.Box.Area).Cast<ZoneArea?>().FirstOrDefault();
        ZoneArea Best(IReadOnlyList<ZoneArea> owners) => owners.FirstOrDefault(t => t.Area.FromModel) is { Zone: not null } follows ? follows
            : Under(owners) ?? owners.OrderBy(t => Page(t.Area.Box).Distance(x, y)).ThenBy(t => t.Area.Box.Area).First();
        foreach (var drawable in touch.Drawables)
        {
            var owners = Areas(active).Where(t => t.Area.Drawables.Contains(drawable, StringComparer.Ordinal)).ToArray();
            if (owners.Length > 0) return Best(owners).Match("drawable");
        }
        if (touch.Hair && active.FirstOrDefault(z => z.Id == "hair") is { } hair) return new(hair, "hair");
        if (touch.Node is { } node && Areas(active).Where(t => t.Area.Nodes.Contains(node, StringComparer.Ordinal)).ToArray() is { Length: > 0 } swung)
            return Best(swung).Match("node");
        if (touch.Bone is { } bone)
        {
            var part = Areas(active.Where(z => OnPart(z.Id, bone))).ToArray();
            if (Under(part) is { } under) return under.Match(under.Area.Bones.Contains(bone, StringComparer.Ordinal) ? "bone" : "box");
            var owners = part.Where(t => t.Area.Bones.Contains(bone, StringComparer.Ordinal)).ToArray();
            if (owners.Length > 0) return Best(owners).Match("bone");
        }
        else if (Under(Areas(active)) is { } boxed) return boxed.Match("box");
        if (!Coarse.TryGetValue(touch.CoarseZone, out var candidates)) return null;
        foreach (var id in candidates)
            if (active.FirstOrDefault(z => z.Id == id) is { } found) return new(found, "coarse");
        // Not found on this model (or not detected yet): the rough zone's default, unless the owner turned that zone off.
        var fallback = candidates[0];
        if (settings?.Zones.FirstOrDefault(z => z.Id == fallback) is { } off && !settings.Active(off)) return null;
        return new(new() { Id = fallback, Box = new(0, 0, 1, 1) }, "coarse");
    }

    // An area of a zone and where it comes among the zone's areas.
    private readonly record struct ZoneArea(CharacterTouchZone Zone, int Index, CharacterTouchZoneArea Area)
    {
        public TouchZoneMatch Match(string how) => new(Zone, how, Zone.AllAreas.Count > 1 ? Index : null);
    }

    // ---------- reactions ----------

    /// <summary>What touching <paramref name="zone"/> plays on the model: the owner's choice, or for each default slot the first of
    /// its names the model has (its own expressions and motions before Martlet's gestures). Only emotes and motions in use.</summary>
    public static IReadOnlyList<CharacterActionSource> Plan(CharacterTouchZone zone, CharacterActionCatalog? catalog) => React(zone, catalog, null, 0).Actions;

    /// <summary>What a touch on <paramref name="zone"/> plays, in this order of precedence: the owner's own pick for the zone, the
    /// persona's <paramref name="temperament"/> for the zone kind or its group (escalated after <paramref name="repeats"/> touches
    /// in a row), else the zone's built-in default reaction. How long the eyes then turn to the mouse pointer always comes from
    /// the temperament.</summary>
    public static TouchReactionPlan React(CharacterTouchZone zone, CharacterActionCatalog? catalog, CharacterTouchTemperament? temperament, int repeats)
    {
        var attitude = CharacterTouchTemperaments.Attitude(temperament, zone.Id);
        var entry = CharacterTouchTemperaments.Entry(temperament, zone.Id);
        var look = entry?.LookSeconds ?? 0;
        if (zone.Reaction.Actions is { } chosen)
        {
            var entries = catalog?.Entries.Where(e => e.Action.Enabled).ToArray() ?? [];
            return new(chosen.Select(id => entries.FirstOrDefault(e => e.Source.Id == id).Source).OfType<CharacterActionSource>().Take(MaximumActions).ToArray(),
                0, attitude, TouchReactionPlan.FromOwner, LookSeconds: look);
        }
        if (temperament is not null && entry is not null)
        {
            var (words, escalated) = CharacterTouchTemperaments.Words(temperament, entry, repeats);
            return new(CharacterTouchTemperaments.Resolve(words, catalog), entry.LingerSeconds, attitude, TouchReactionPlan.FromTemperament, escalated, look);
        }
        return new(DefaultPlan(zone, catalog));
    }

    private static IReadOnlyList<CharacterActionSource> DefaultPlan(CharacterTouchZone zone, CharacterActionCatalog? catalog)
    {
        if (catalog is null) return [];
        var entries = catalog.Entries.Where(e => e.Action.Enabled).ToArray();
        var plan = new List<CharacterActionSource>();
        foreach (var slot in Kind(zone.Id)?.Defaults ?? [])
            foreach (var word in slot)
            {
                var hit = entries.Where(e => Named(e, word)).OrderBy(e => e.Source.Kind == CharacterActionKind.Gesture ? 1 : 0)
                    .Select(e => e.Source).FirstOrDefault(s => !plan.Contains(s));
                if (hit is null) continue;
                plan.Add(hit);
                break;
            }
        return plan;
    }

    private static bool Named((CharacterActionSource Source, CharacterAction Action) entry, string word) =>
        string.Equals(entry.Action.Tag, word, StringComparison.OrdinalIgnoreCase) ||
        (entry.Source.Kind == CharacterActionKind.Gesture && string.Equals(entry.Source.Name, word, StringComparison.OrdinalIgnoreCase)) ||
        CharacterActions.Slug(entry.Source.Name) == word;

    /// <summary>The owner's own words for a touch on a zone Martlet notices (a hint that goes with it), or null: none, the zone's
    /// built-in line, or Martlet doesn't notice the zone.</summary>
    public static string? Narration(CharacterTouchZone zone) => zone.Reaction.Notices && zone.Reaction.Narration is { Length: > 0 } own &&
        own != Kind(zone.Id)?.Narration ? own : null;

    /// <summary>Whether a quick tap on <paramref name="zone"/> is a pat (the top of the head, the hair, animal ears) rather than a poke.</summary>
    public static bool Pats(CharacterTouchZone zone) => zone.Id is "top_of_head" or "hair" or "animal_ears";

    /// <summary>Where <paramref name="zone"/> is, as the character hears it ("the top of your head", "your left cheek").</summary>
    public static string Part(CharacterTouchZone zone) => zone.Label is null ? zone.Id switch
    {
        "top_of_head" => "the top of your head",
        "face" => "your face",
        "waist" => "your sides",
        "held_item" => "what you're holding",
        "skirt_hem" => "the hem of your skirt",
        _ => "your " + zone.Name.ToLowerInvariant()
    } : "your " + zone.Name.ToLowerInvariant();

    // ---------- storage ----------

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true,
        MaxDepth = 10
    };

    private sealed record Document(int Version, IReadOnlyList<CharacterTouchZoneSettings> Models);

    // Version 2: Martlet notices is on by default. Version 1 files are still read (UpgradedFromVersion1).
    private const int DocumentVersion = 2;

    public static string Path(string dataDirectory) => System.IO.Path.Combine(dataDirectory, FileName);

    /// <summary>Where the snapshot the model's zones were found in is kept (a PNG).</summary>
    public static string SnapshotPath(string dataDirectory, string modelId) =>
        System.IO.Path.Combine(dataDirectory, SnapshotFolder, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(modelId)))[..24].ToLowerInvariant() + ".png");

    public static CharacterTouchZoneSettings? Load(string dataDirectory, string modelId) =>
        LoadAll(dataDirectory).FirstOrDefault(m => m.ModelId == modelId);

    public static IReadOnlyList<CharacterTouchZoneSettings> LoadAll(string dataDirectory)
    {
        try
        {
            var path = Path(dataDirectory);
            if (!File.Exists(path) || new FileInfo(path).Length > MaximumBytes) return [];
            var document = JsonSerializer.Deserialize<Document>(File.ReadAllBytes(path), Json);
            if (document is not { Version: 1 or DocumentVersion, Models: { } models }) return [];
            var valid = models.Where(m => m is { ModelId: not null, Zones: not null } && m.Zones.All(z => z is { Id: not null, Box: not null } &&
                (z.Areas is null || z.Areas.All(a => a is { Box: not null, Drawables: not null, Bones: not null, Nodes: not null }))));
            return document.Version == 1 ? [.. valid.Select(UpgradedFromVersion1)] : [.. valid];
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or NotSupportedException) { return []; }
    }

    /// <summary>A model saved before Martlet notices was on by default: when the owner never turned it on for any of its zones,
    /// it is on for all of them now (the old default was off); otherwise the owner's choices stay.</summary>
    public static CharacterTouchZoneSettings UpgradedFromVersion1(CharacterTouchZoneSettings model) => model.Zones.Any(z => z.Reaction.Notices)
        ? model : model with { Zones = [.. model.Zones.Select(z => z with { Reaction = z.Reaction with { Notices = true } })] };

    /// <summary>Why <paramref name="settings"/> can't be saved, or null.</summary>
    public static string? Problem(CharacterTouchZoneSettings settings)
    {
        if (string.IsNullOrWhiteSpace(settings.ModelId)) return "The model is unknown.";
        if (settings.Zones.Count > MaximumZones) return $"A model can have at most {MaximumZones} zones.";
        foreach (var zone in settings.Zones)
        {
            if (string.IsNullOrWhiteSpace(zone.Id) || zone.Id.Length > 40) return "A zone needs a short ID.";
            if (!zone.Box.Valid) return $"{zone.Name}'s box must lie inside the picture.";
            if (zone.Areas is { Count: > MaximumAreas }) return $"A zone can have at most {MaximumAreas} areas.";
            if (zone.Areas?.Any(a => a?.Box is not { Valid: true }) == true) return $"{zone.Name}'s areas must lie inside the picture.";
            if (zone.Follows is { Length: > 256 }) return "What a zone follows needs a shorter name.";
            if (zone.Label is { Length: > MaximumLabelLength }) return $"A zone's name can be at most {MaximumLabelLength} characters.";
            if (zone.Reaction.Narration is { Length: > MaximumNarrationLength }) return $"What a touch tells the character can be at most {MaximumNarrationLength} characters.";
            if (zone.Reaction.Actions is { Count: > MaximumActions }) return $"A zone plays at most {MaximumActions} emotes or gestures.";
            if (!double.IsFinite(zone.Reaction.CooldownSeconds) || zone.Reaction.CooldownSeconds is < 0 or > CharacterTouchReaction.MaximumCooldown)
                return $"A zone's rest must be 0 to {CharacterTouchReaction.MaximumCooldown:0} seconds.";
        }
        return settings.Zones.Select(z => z.Id).Distinct(StringComparer.Ordinal).Count() == settings.Zones.Count ? null : "Two zones have the same ID.";
    }

    private static readonly SemaphoreSlim Gate = new(1, 1);

    public static async Task<CharacterTouchZoneSettings> SaveAsync(string dataDirectory, CharacterTouchZoneSettings settings, DateTimeOffset now,
        CancellationToken token = default)
    {
        if (Problem(settings) is { } problem) throw new ContractException(ErrorCode.InvalidContract, problem);
        settings = settings with { UpdatedAt = now.ToUniversalTime() };
        await Gate.WaitAsync(token);
        try
        {
            var models = LoadAll(dataDirectory).Where(m => m.ModelId != settings.ModelId).Append(settings)
                .OrderByDescending(m => m.UpdatedAt).Take(MaximumModels).OrderBy(m => m.ModelId, StringComparer.Ordinal).ToArray();
            var bytes = JsonSerializer.SerializeToUtf8Bytes(new Document(DocumentVersion, models), Json);
            ContractRules.Require(bytes.Length <= MaximumBytes, "The touch zones are too large.", ErrorCode.PayloadTooLarge);
            Directory.CreateDirectory(dataDirectory);
            var temporary = System.IO.Path.Combine(dataDirectory, $"character-touch-zones.{Guid.NewGuid():N}.tmp");
            try
            {
                await File.WriteAllBytesAsync(temporary, bytes, token);
                File.Move(temporary, Path(dataDirectory), overwrite: true);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
            return settings;
        }
        finally { Gate.Release(); }
    }

    /// <summary>Where the pictures the vision model saw in the model's last detection are kept, with <see cref="SentFile"/>.</summary>
    public static string SentFolder(string dataDirectory, string modelId) => System.IO.Path.ChangeExtension(SnapshotPath(dataDirectory, modelId), null) + "-sent";

    public const string SentFile = "sent.json";

    /// <summary>What the model's last detection sent, or null before one ran (or when it can't be read).</summary>
    public static TouchZoneSent? LoadSent(string dataDirectory, string modelId)
    {
        try
        {
            var path = System.IO.Path.Combine(SentFolder(dataDirectory, modelId), SentFile);
            return File.Exists(path) && new FileInfo(path).Length <= MaximumBytes ? JsonSerializer.Deserialize<TouchZoneSent>(File.ReadAllBytes(path), Json) : null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or NotSupportedException) { return null; }
    }

    /// <summary>Clears the model's sent folder before a new detection, so it holds only what that one sends.</summary>
    public static string ClearSent(string dataDirectory, string modelId)
    {
        var folder = SentFolder(dataDirectory, modelId);
        if (Directory.Exists(folder))
            foreach (var file in Directory.EnumerateFiles(folder)) File.Delete(file);
        Directory.CreateDirectory(folder);
        return folder;
    }

    public static async Task SaveSentAsync(string dataDirectory, string modelId, TouchZoneSent sent, CancellationToken token = default)
    {
        var folder = SentFolder(dataDirectory, modelId);
        Directory.CreateDirectory(folder);
        await File.WriteAllBytesAsync(System.IO.Path.Combine(folder, SentFile), JsonSerializer.SerializeToUtf8Bytes(sent, Json), token);
    }

    /// <summary>What the renderer's probe told of the model when its last detection took its picture, kept with the pictures.</summary>
    public const string ProbeFile = "probe.json";

    public static async Task SaveProbeAsync(string dataDirectory, string modelId, TouchZoneProbeFile probe, CancellationToken token = default)
    {
        var folder = SentFolder(dataDirectory, modelId);
        Directory.CreateDirectory(folder);
        await File.WriteAllBytesAsync(System.IO.Path.Combine(folder, ProbeFile), JsonSerializer.SerializeToUtf8Bytes(probe, Json), token);
    }

    /// <summary>The probe the model's last detection took its picture with (<see cref="ProbeFile"/>), or null.</summary>
    public static TouchZoneProbeFile? LoadProbe(string dataDirectory, string modelId) => ReadProbe(System.IO.Path.Combine(SentFolder(dataDirectory, modelId), ProbeFile));

    /// <summary>A <see cref="ProbeFile"/>, or null when it is missing or can't be read.</summary>
    public static TouchZoneProbeFile? ReadProbe(string path)
    {
        try
        {
            return File.Exists(path) && new FileInfo(path).Length <= MaximumBytes ? JsonSerializer.Deserialize<TouchZoneProbeFile>(File.ReadAllBytes(path), Json) : null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or NotSupportedException) { return null; }
    }
    public static async Task SaveSnapshotAsync(string dataDirectory, string modelId, byte[] png, CancellationToken token = default)
    {
        var path = SnapshotPath(dataDirectory, modelId);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        await File.WriteAllBytesAsync(path, png, token);
    }

    /// <summary>Where the probe of the snapshot the model's zones are on is kept (where the snapshot sat on the page and where the
    /// model's drawables, bones, own parts and swinging parts were in it), so the zones can be bound again when the owner moves,
    /// adds or changes one (<see cref="Rebind"/>).</summary>
    public static string SnapshotProbePath(string dataDirectory, string modelId) =>
        System.IO.Path.ChangeExtension(SnapshotPath(dataDirectory, modelId), ".probe.json");

    /// <summary>The probe of the snapshot the model's zones are on, or null.</summary>
    public static TouchZoneProbeFile? LoadSnapshotProbe(string dataDirectory, string modelId) => ReadProbe(SnapshotProbePath(dataDirectory, modelId));

    /// <summary>Keeps the probe of the snapshot just saved for the model (<see cref="SnapshotProbePath"/>); without one, a probe
    /// kept for an earlier snapshot is deleted, so it is never taken for this one's.</summary>
    public static async Task SaveSnapshotProbeAsync(string dataDirectory, string modelId, TouchZoneProbeFile? probe, CancellationToken token = default)
    {
        var path = SnapshotProbePath(dataDirectory, modelId);
        if (probe is null)
        {
            if (File.Exists(path)) File.Delete(path);
            return;
        }
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        await File.WriteAllBytesAsync(path, JsonSerializer.SerializeToUtf8Bytes(probe, Json), token);
    }
}

/// <summary>One picture the vision model was sent while finding zones: its file in the sent folder, the step, what it asked
/// (Parts, Zones or Check), its size in pixels and bytes, and its media type.</summary>
public sealed record TouchZoneSentPicture(string File, string Step, string Kind, int Width, int Height, int Bytes, string MediaType);

/// <summary>Where a detection's snapshot sat on the renderer page (fractions of the page) and what the renderer's probe told of the
/// model then: its drawables (with their parts), bones and own parts (character-touch-zones\&lt;model&gt;-sent\probe.json).</summary>
public sealed record TouchZoneProbeFile(TouchZoneBox Crop, RendererZoneProbe Probe);

/// <summary>What a detection sent to the vision model: when, whether a FIXTURE stood in for it, how many requests it made, each
/// picture, one line per step and whether it saved zones (so its pictures belong to the snapshot under the boxes)
/// (character-touch-zones\&lt;model&gt;-sent\sent.json).</summary>
public sealed record TouchZoneSent(DateTimeOffset At, bool Fixture, int Requests, IReadOnlyList<TouchZoneSentPicture> Pictures, IReadOnlyList<string> Steps,
    bool Saved = false)
{
    /// <summary>One plain line: how many pictures, how large, and what they showed.</summary>
    public string Describe()
    {
        var largest = Pictures.Count == 0 ? 0 : Pictures.Max(p => Math.Max(p.Width, p.Height));
        var closeUps = Pictures.Where(p => p.Kind == nameof(ZoneAskKind.Zones) && p.Step != TouchZoneDetection.MissingStep)
            .Select(p => p.Step.Replace('_', ' ')).Distinct().ToArray();
        var checks = Pictures.Count(p => p.Kind == nameof(ZoneAskKind.Check));
        var again = Pictures.Any(p => p.Step == TouchZoneDetection.MissingStep);
        var parts = closeUps.Length switch
        {
            0 => "",
            1 => closeUps[0],
            _ => string.Join(", ", closeUps[..^1]) + " and " + closeUps[^1]
        };
        return (Fixture ? "FIXTURE - NOT AI answered these. " : "") +
            $"Thinking saw {Pictures.Count} picture{(Pictures.Count == 1 ? "" : "s")} on {At.ToLocalTime().ToString("g", CultureInfo.CurrentCulture)}, " +
            $"up to {largest} pixels on the longer side, each on a plain backdrop with a grid of tenths" +
            (parts.Length > 0 ? $": the whole character, then close-ups of the {parts}" : "") +
            (checks > 0 ? $", with its boxes drawn and numbered for {checks} check{(checks == 1 ? "" : "s")}" : "") +
            (again ? ", and the whole character again for the zones the close-ups missed" : "") + ".";
    }
}