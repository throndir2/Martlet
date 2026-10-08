using System.Globalization;

namespace Martlet.Avatar.Hosting;

/// <summary>What one detection request asks for: where the big parts are, a close-up's zones, or a check of drawn boxes.</summary>
public enum ZoneAskKind { Parts, Zones, Check }

/// <summary>A numbered box drawn on a check picture: its zone and its box as fractions of the picture sent.</summary>
public sealed record ZoneMark(int Number, string Id, TouchZoneBox Box);

/// <summary>One request of a detection: the picture the vision model gets (flattened, scaled, with the grid and, for a check, the
/// boxes drawn), its instructions and message, where the picture lies in the snapshot (<see cref="Region"/>, fractions), the
/// zones asked for and the boxes drawn.</summary>
public sealed record ZoneAsk(ZoneAskKind Kind, string Step, string Instructions, string Text, ZonePixels Picture, TouchZoneBox Region,
    IReadOnlyList<string> Ids, IReadOnlyList<ZoneMark> Marks);

/// <summary>A point the model itself measured (a VRM humanoid bone), as fractions of the snapshot.</summary>
public sealed record ZoneHintPoint(string Name, double X, double Y);

/// <summary>Where the model's own parts named for a body part lie (Live2D drawables whose IDs name hair, a face, hands..., or the
/// drawables in the parts its DisplayInfo file names so, such as 头, 前发 or 右腿), as fractions of the snapshot, and for a part
/// that comes in pairs which side it is on: the character's own "left" or "right" (null for both or the middle).</summary>
public sealed record ZoneHintArea(string Part, TouchZoneBox Box, int Drawables, string? Side = null);

/// <summary>A drawable of a Live2D model in parts the model's own part names call body parts (from its DisplayInfo file, else the
/// part IDs), as fractions of the snapshot: its box, those body parts (nearest part first: an eye's drawable in 左眼 inside 头 is
/// eyes, then head) and, for a part that comes in pairs, its side, the character's own "left" or "right", told by where it lies
/// and which way the character faces (a name's 左 or L only groups a limb's drawables; null in the middle).</summary>
public sealed record ZoneHintPiece(TouchZoneBox Box, IReadOnlyList<string> Parts, string? Side);

/// <summary>What the renderer's probe says about the character in the snapshot: its VRM bones and its named Live2D parts, and
/// whether it faces the viewer (its left on the picture's right; null when that can't be told).</summary>
public sealed record ZoneHints(IReadOnlyList<ZoneHintPoint> Bones, IReadOnlyList<ZoneHintArea> Areas, bool? FacesViewer)
{
    public bool Empty => Bones.Count == 0 && Areas.Count == 0;
    /// <summary>The drawables in the parts the model's own part names call body parts; empty for a model whose part names say
    /// nothing (or a VRM). With them, the close-ups hold their parts and boxes that miss their part move onto it.</summary>
    public IReadOnlyList<ZoneHintPiece> Pieces { get; init; } = [];
    /// <summary>The middle of the character's body from left to right (a fraction of the snapshot), from its named head, neck or
    /// upper body; null without <see cref="Pieces"/>.</summary>
    public double? Middle { get; init; }
    /// <summary>How many parts the Live2D model has, and how many its DisplayInfo file names.</summary>
    public int ModelParts { get; init; }
    public int NamedModelParts { get; init; }
    public bool Named => Pieces.Count > 0;
    /// <summary>How many body parts the model's own names place (hair, face, arms...).</summary>
    public int NamedParts => Areas.Select(a => a.Part).Distinct(StringComparer.Ordinal).Count();
}

/// <summary>How a detection goes: the longer side of each picture sent, how far a close-up may zoom in, how many check rounds
/// each part gets and the zones it must end with (asked for again on the whole character when the close-ups missed them, then
/// worked out from the zones around them; with Include intimate zones on, <see cref="TouchZoneDetection.Erogenous"/>).</summary>
public sealed record ZoneDetectionOptions
{
    public int Edge { get; init; } = 1024;
    public double MaximumZoom { get; init; } = 4;
    public int Checks { get; init; } = 2;
    public IReadOnlyList<string> Required { get; init; } = [];
}

/// <summary>A detection's progress: what it does now, the zones found so far (fractions of the snapshot) and the requests made.</summary>
public sealed record ZoneDetectionProgress(string Text, IReadOnlyList<CharacterTouchZone> Zones, int Requests);

/// <summary>What a detection found (fractions of the snapshot; null when nothing could be read), why it stopped early (the
/// failure of the request it stopped at, with the zones found until then; null when no request failed), how many requests it
/// made and one line per step.</summary>
public sealed record ZoneDetectionResult(IReadOnlyList<CharacterTouchZone>? Zones, string? Failure, int Requests, IReadOnlyList<string> Steps);

/// <summary>A part of the character a close-up shows, and the zones found in it.</summary>
public sealed record ZoneRegion(string Id, string What, IReadOnlyList<string> Zones);

/// <summary>Finding touch zones with a vision model, step by step, with the CPU doing what it can tell for certain. One picture of
/// the whole character with a grid finds the head, upper body and lower body (and a tail, wings or a held item); a close-up of
/// each part finds its zones; then the model checks each part's boxes, drawn and numbered on the close-up, and corrects them,
/// for a few rounds or until it says they are right. Between steps the CPU shrinks each box to the character's pixels, swaps
/// left and right back when a pair is the wrong way round, and lists problems for the next check: a box over the background,
/// a chin above a nose, a box that misses where the model's own skeleton puts the part.</summary>
public static partial class TouchZoneDetection
{
    /// <summary>How much room a close-up leaves around its part (a share of the part's longer side).</summary>
    public const double Margin = 0.12;
    /// <summary>A correction that moves no edge more than this (a share of the picture checked) is noise, not a change.</summary>
    public const double Noise = 0.02;
    /// <summary>A box with less than this share of the character's pixels covers the background.</summary>
    public const double EmptyShare = 0.04;

    public static IReadOnlyList<ZoneRegion> Regions { get; } =
    [
        new("head", "the character's head",
            ["top_of_head", "hair", "forehead", "face", "cheek_left", "cheek_right", "nose", "lips", "chin", "ear_left", "ear_right",
             "animal_ears", "horns", "glasses_or_hat"]),
        new("upper_body", "the character's upper body and arms",
            ["neck", "shoulder_left", "shoulder_right", "collarbone", "chest", "breast_left", "breast_right", "stomach", "navel", "waist",
             "lower_back", "upper_arm_left", "upper_arm_right", "forearm_left", "forearm_right", "hand_left", "hand_right"]),
        new("lower_body", "the character's lower body and legs",
            ["hips", "groin", "buttocks", "thigh_left", "thigh_right", "inner_thigh_left", "inner_thigh_right", "knee_left", "knee_right",
             "calf_left", "calf_right", "foot_left", "foot_right", "skirt_hem"])
    ];

    /// <summary>Zones found on the whole character with the parts: they can be anywhere.</summary>
    public static IReadOnlyList<string> Extras { get; } = ["tail", "wings", "held_item"];

    /// <summary>The intimate (erogenous) zones a detection always ends with while Include intimate zones is on: asked for again on
    /// the whole character when the close-ups missed them (or a model left them out), else worked out from the zones around them.</summary>
    public static IReadOnlyList<string> Erogenous { get; } =
    [
        "neck", "lips", "ear_left", "ear_right", "chest", "breast_left", "breast_right", "waist", "hips", "groin", "buttocks",
        "inner_thigh_left", "inner_thigh_right"
    ];

    /// <summary>The step that asks again, on the whole character, for zones that must be found and the close-ups missed.</summary>
    public const string MissingStep = "missing";

    private static readonly (string Id, string What)[] Parts =
    [
        ("head", "the whole head with its hair, ears, hat or horns"),
        ("upper_body", "from the neck down to the waist, with both arms and hands"),
        ("lower_body", "from the hips down to the feet, with any skirt"),
        ("tail", "a tail, if it has one"),
        ("wings", "wings, if it has them"),
        ("held_item", "something it holds, if it holds anything")
    ];

    public static IReadOnlyList<string> PartIds { get; } = [.. Parts.Select(p => p.Id)];

    private static readonly Dictionary<string, string> Where = new(StringComparer.Ordinal)
    {
        ["top_of_head"] = "the top of the head, where a head pat lands", ["hair"] = "all of the hair",
        ["forehead"] = "the forehead, between the bangs or hairline and the eyebrows", ["face"] = "the face with the eyes",
        ["cheek_left"] = "the character's left cheek", ["cheek_right"] = "the character's right cheek", ["nose"] = "the nose",
        ["lips"] = "the mouth and lips", ["chin"] = "the chin", ["ear_left"] = "the character's left ear (a human ear)",
        ["ear_right"] = "the character's right ear (a human ear)", ["animal_ears"] = "animal ears on the head", ["horns"] = "horns",
        ["glasses_or_hat"] = "glasses or a hat", ["neck"] = "the neck", ["shoulder_left"] = "the character's left shoulder",
        ["shoulder_right"] = "the character's right shoulder", ["collarbone"] = "the collarbone, just below the neck", ["chest"] = "the chest",
        ["breast_left"] = "the character's left breast", ["breast_right"] = "the character's right breast",
        ["stomach"] = "the stomach, below the chest", ["navel"] = "the navel (belly button)", ["waist"] = "the waist and both sides",
        ["lower_back"] = "the lower back, if it shows",
        ["upper_arm_left"] = "the character's left upper arm, from the shoulder to the elbow",
        ["upper_arm_right"] = "the character's right upper arm, from the shoulder to the elbow",
        ["forearm_left"] = "the character's left forearm, from the elbow to the wrist",
        ["forearm_right"] = "the character's right forearm, from the elbow to the wrist",
        ["hand_left"] = "the character's left hand", ["hand_right"] = "the character's right hand", ["hips"] = "the hips",
        ["groin"] = "the groin, where the legs meet", ["buttocks"] = "the buttocks, if they show",
        ["thigh_left"] = "the character's left thigh, from the hip to the knee", ["thigh_right"] = "the character's right thigh, from the hip to the knee",
        ["inner_thigh_left"] = "the inner side of the character's left thigh", ["inner_thigh_right"] = "the inner side of the character's right thigh",
        ["knee_left"] = "the character's left knee", ["knee_right"] = "the character's right knee",
        ["calf_left"] = "the character's left calf, from the knee to the ankle", ["calf_right"] = "the character's right calf, from the knee to the ankle",
        ["foot_left"] = "the character's left foot", ["foot_right"] = "the character's right foot",
        ["skirt_hem"] = "the bottom edge of a skirt or dress", ["tail"] = "a tail", ["wings"] = "wings", ["held_item"] = "something the character holds"
    };

    /// <summary>What a zone is, as the vision model is told ("the character's left cheek").</summary>
    public static string Describe(string id) => Where.TryGetValue(id, out var where) ? where : CharacterTouchZones.Kind(id)?.Label.ToLowerInvariant() ?? id;

    // ---------- what the vision model is told ----------

    private const string Only = "Only locate them; don't describe or judge the character.";
    private const string Sides = "Left and right are the CHARACTER's own: when it faces you, its left side is on the right of the picture.";
    private const string Grid = "The picture has a grid of tenths: the numbers along its top edge are fractions of its width (0 at the left " +
        "edge, 1 at the right edge) and the numbers along its left edge are fractions of its height (0 at the top, 1 at the bottom). " +
        "Read positions from the grid.";
    private const string Format = "Give each box as fractions of THIS picture, with two decimals: left and right as fractions of its width, " +
        "top and bottom as fractions of its height.";
    private const string Covered = "A zone that clothing or hair covers is still there: box where that part of the body is under it.";

    /// <summary>Step 1, with the whole character: where its head, upper body and lower body are, and a tail, wings or a held item.</summary>
    public static string PartsInstructions =>
        "You find the parts of a character (a 2D or 3D avatar) in a picture, for a touch-reaction feature. " + Only + " " + Grid + " " +
        Format + " Leave out parts it doesn't have or you can't see. Answer with JSON only, no other text, in this form:\n" +
        "{\"parts\":[{\"id\":\"head\",\"left\":0.30,\"top\":0.02,\"right\":0.62,\"bottom\":0.25}]}";

    /// <summary>Step 2, with a close-up of one part: its zones.</summary>
    public static string ZonesInstructions =>
        "You find body zones on a close-up picture of part of a character (a 2D or 3D avatar), for a touch-reaction feature. " + Only + " " +
        Sides + " " + Grid + " Give each zone you can see a tight box around just that zone. " + Covered + " " + Format +
        " Leave out zones you can't see. Answer with JSON only, no other text, in this form:\n" +
        "{\"zones\":[{\"id\":\"forehead\",\"left\":0.40,\"top\":0.18,\"right\":0.62,\"bottom\":0.27}]}";

    /// <summary>Step 3, the same close-up with the boxes found so far drawn and numbered: right, corrected, gone or added.</summary>
    public static string CheckInstructions =>
        "You check boxes that mark body zones on a picture of a character (a 2D or 3D avatar), for a touch-reaction feature. " + Only + " " +
        Sides + " " + Grid + " Each box is drawn in a color, with its number in a filled tag at one of its corners. A box is right when it " +
        "tightly covers its zone and little else. Answer ok true for a box that is right. For a box that is off, answer ok false with " +
        "a corrected box. " + Format + " " + Covered + " Answer visible false for a zone that isn't there. You may add a listed zone that has no box yet, " +
        "by its id. Answer with JSON only, no other text, in this form:\n" +
        "{\"zones\":[{\"n\":1,\"ok\":true},{\"n\":2,\"ok\":false,\"left\":0.41,\"top\":0.20,\"right\":0.60,\"bottom\":0.31}," +
        "{\"n\":3,\"visible\":false},{\"id\":\"ear_left\",\"left\":0.70,\"top\":0.30,\"right\":0.78,\"bottom\":0.42}]}";

    /// <summary>After the close-ups, with the whole character again: only the zones that must be found (the intimate ones, with
    /// Include intimate zones on) that the close-ups missed.</summary>
    public static string MissingInstructions =>
        "You find body zones on a picture of a whole character (a 2D or 3D avatar), for a touch-reaction feature. " + Only + " " + Sides +
        " " + Grid + " Every zone listed is on this character: give each one a tight box. " + Covered + " Put a zone on the far side of the " +
        "body (such as the buttocks of a character that faces you) where it would be. " + Format + " Answer with JSON only, no other text, " +
        "in this form:\n{\"zones\":[{\"id\":\"hips\",\"left\":0.36,\"top\":0.47,\"right\":0.64,\"bottom\":0.55}]}";

    /// <summary>The message that goes with the whole character's picture.</summary>
    public static string PartsText(ZoneHints? hints) =>
        "Parts (id - what):\n" + string.Join("\n", Parts.Select(p => $"{p.Id} - {p.What}")) + HintsText(hints, new(0, 0, 1, 1));

    /// <summary>The message that goes with a close-up of <paramref name="region"/> (<paramref name="crop"/>, fractions of the snapshot).</summary>
    public static string ZonesText(ZoneRegion region, ZoneHints? hints, TouchZoneBox crop) =>
        $"This close-up shows {region.What}.\nZones (id - what):\n" + string.Join("\n", region.Zones.Select(id => $"{id} - {Describe(id)}")) +
        HintsText(hints, crop);

    /// <summary>The message that goes with the whole character when zones that must be found are <paramref name="missing"/>.</summary>
    public static string MissingText(IEnumerable<string> missing, ZoneHints? hints) =>
        "This picture shows the whole character.\nZones (id - what):\n" + string.Join("\n", missing.Select(id => $"{id} - {Describe(id)}")) +
        HintsText(hints, new(0, 0, 1, 1));

    /// <summary>The message that goes with a check: the numbered boxes, the zones without one and what the CPU found wrong.</summary>
    public static string CheckText(string what, IReadOnlyList<ZoneMark> marks, IEnumerable<string> missing, IReadOnlyList<string> problems,
        ZoneHints? hints, TouchZoneBox crop)
    {
        var text = $"This picture shows {what}. The boxes (number = id - what):\n" +
            string.Join("\n", marks.Select(m => $"{m.Number} = {m.Id} - {Describe(m.Id)}"));
        var absent = missing.ToArray();
        if (absent.Length > 0)
            text += "\nZones without a box (add them if you can see them):\n" + string.Join("\n", absent.Select(id => $"{id} - {Describe(id)}"));
        if (problems.Count > 0) text += "\nMartlet measured these problems; check them first:\n- " + string.Join("\n- ", problems);
        return text + HintsText(hints, crop);
    }

    // What the model itself says about the character inside crop, in the picture's own fractions.
    private static string HintsText(ZoneHints? hints, TouchZoneBox crop)
    {
        if (hints is null || hints.Empty) return "";
        var lines = new List<string>();
        var points = hints.Bones.Where(b => BoneWords.ContainsKey(b.Name) && crop.Contains(b.X, b.Y))
            .Select(b => $"{BoneWords[b.Name]} {Point((b.X - crop.X) / crop.Width, (b.Y - crop.Y) / crop.Height)}").ToArray();
        if (points.Length > 0)
            lines.Add("Measured from the model's own skeleton (exact points, fractions of this picture): " + string.Join("; ", points) + ".");
        var areas = hints.Areas.Select(a => (a, Seen: Clip(a.Box.Relative(crop)))).Where(a => a.Seen is not null)
            .Select(a => $"{AreaName(a.a)} {Point(a.Seen!.X, a.Seen.Y)} to {Point(a.Seen.X + a.Seen.Width, a.Seen.Y + a.Seen.Height)}").ToArray();
        if (areas.Length > 0)
            lines.Add("The model's own parts, by the names in its files, lie here (fractions of this picture; a name can mislead): " +
                string.Join("; ", areas) + ".");
        return lines.Count == 0 ? "" : "\n" + string.Join("\n", lines);
    }

    private static string Point(double x, double y) => string.Create(CultureInfo.InvariantCulture, $"({x:0.00}, {y:0.00})");

    // The part of box inside the picture (0..1), or null when none of it is.
    private static TouchZoneBox? Clip(TouchZoneBox box)
    {
        double x1 = Math.Clamp(box.X, 0, 1), y1 = Math.Clamp(box.Y, 0, 1);
        double x2 = Math.Clamp(box.X + box.Width, 0, 1), y2 = Math.Clamp(box.Y + box.Height, 0, 1);
        return x2 - x1 > 0.002 && y2 - y1 > 0.002 ? new(x1, y1, x2 - x1, y2 - y1) : null;
    }

    // The VRM humanoid bones worth naming, as the vision model is told them. A bone's point is where it starts: the upper arm at
    // the shoulder joint, the lower arm at the elbow, the hand at the wrist, the lower leg at the knee, the foot at the ankle.
    private static readonly Dictionary<string, string> BoneWords = new(StringComparer.Ordinal)
    {
        ["head"] = "head (base of the skull)", ["neck"] = "neck (its base)", ["upperChest"] = "upper chest", ["chest"] = "chest",
        ["spine"] = "spine (the stomach)", ["hips"] = "hips", ["jaw"] = "jaw", ["leftEye"] = "the character's left eye",
        ["rightEye"] = "the character's right eye", ["leftUpperArm"] = "the character's left shoulder joint",
        ["rightUpperArm"] = "the character's right shoulder joint", ["leftLowerArm"] = "the character's left elbow",
        ["rightLowerArm"] = "the character's right elbow", ["leftHand"] = "the character's left wrist", ["rightHand"] = "the character's right wrist",
        ["leftUpperLeg"] = "the character's left hip joint", ["rightUpperLeg"] = "the character's right hip joint",
        ["leftLowerLeg"] = "the character's left knee", ["rightLowerLeg"] = "the character's right knee",
        ["leftFoot"] = "the character's left ankle", ["rightFoot"] = "the character's right ankle",
        ["leftToes"] = "the character's left toes", ["rightToes"] = "the character's right toes"
    };
}
