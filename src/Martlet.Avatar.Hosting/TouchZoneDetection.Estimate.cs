using System.Globalization;

namespace Martlet.Avatar.Hosting;

public static partial class TouchZoneDetection
{
    // The first guess's proportions: a standing character facing the viewer, measured on the bundled Hiyori (about six heads
    // tall), in face widths (the width of the face the renderer found). Across from the middle of the face (head zones) or of
    // the body, the character's left side's for a pair (facing the viewer it is on the picture's right); down from the eye line.
    // Below the chin the body is stretched to the character's own height.
    private const double ChinLine = 0.42, HipLine = 3.25, SoleLine = 6.6;

    private static readonly Dictionary<string, (bool Head, double X, double Half, double Top, double Bottom)> Proportions = new(StringComparer.Ordinal)
    {
        ["top_of_head"] = (true, 0, 0.38, -0.85, -0.45), ["hair"] = (true, 0, 0.7, -0.88, -0.15), ["forehead"] = (true, 0, 0.3, -0.4, -0.14),
        ["face"] = (true, 0, 0.45, -0.42, 0.42), ["eye"] = (true, 0.24, 0.12, -0.12, 0.12), ["cheek"] = (true, 0.3, 0.1, 0.08, 0.27),
        ["nose"] = (true, 0, 0.05, 0.13, 0.24), ["lips"] = (true, 0, 0.1, 0.23, 0.34), ["chin"] = (true, 0, 0.14, 0.32, 0.44),
        ["ear"] = (true, 0.52, 0.08, -0.05, 0.22), ["neck"] = (false, 0, 0.15, 0.36, 0.64), ["collarbone"] = (false, 0, 0.45, 0.6, 0.8),
        ["shoulder"] = (false, 0.6, 0.18, 0.6, 0.95), ["chest"] = (false, 0, 0.6, 0.7, 1.6), ["breast"] = (false, 0.27, 0.25, 1.1, 1.6),
        ["stomach"] = (false, 0, 0.4, 1.65, 2.45), ["navel"] = (false, 0, 0.07, 2.2, 2.4), ["waist"] = (false, 0, 0.47, 2.1, 2.6),
        ["hips"] = (false, 0, 0.62, 2.65, HipLine), ["hip"] = (false, 0.31, 0.3, 2.65, HipLine), ["groin"] = (false, 0, 0.14, 3.05, 3.4),
        ["buttocks"] = (false, 0, 0.58, 2.85, 3.4)
    };

    // The joints of the arms and legs, in the same face widths (the character's left side's).
    private static readonly (string Joint, double X, double Y)[] JointLines =
    [
        ("shoulder", 0.66, 0.72), ("elbow", 0.78, 1.74), ("wrist", 0.9, 2.66), ("fingers", 1.0, 3.12),
        ("hip", 0.3, HipLine), ("knee", 0.27, 4.45), ("ankle", 0.25, 5.95), ("toes", 0.25, SoleLine)
    ];

    // The VRM humanoid bone at each joint (with "left" or "right" before it).
    private static readonly (string Joint, string Bone)[] JointBones =
    [
        ("shoulder", "UpperArm"), ("elbow", "LowerArm"), ("wrist", "Hand"), ("hip", "UpperLeg"), ("knee", "LowerLeg"), ("ankle", "Foot"), ("toes", "Toes")
    ];

    // Each part of a limb: from one joint to the next (its share of the way) and half its thickness, in face widths.
    private static readonly Dictionary<string, (string From, string To, double Start, double End, double Half)> Limbs = new(StringComparer.Ordinal)
    {
        ["upper_arm"] = ("shoulder", "elbow", 0.1, 1, 0.16), ["forearm"] = ("elbow", "wrist", 0, 1, 0.15), ["hand"] = ("wrist", "fingers", 0, 1.05, 0.17),
        ["thigh"] = ("hip", "knee", 0, 0.92, 0.25), ["calf"] = ("knee", "ankle", 0.15, 1, 0.17), ["foot"] = ("ankle", "toes", -0.05, 1.05, 0.2)
    };

    private const string FromNamedParts = "the model's own named parts", FromProportions = "the body's proportions", FromSkeleton = "the model's skeleton";

    /// <summary>A first guess at the zones <paramref name="options"/> looks for (by default <see cref="Defaults"/>) in
    /// <paramref name="snapshot"/>, with no vision model: nothing is sent. The model's own named parts place what they can
    /// (<see cref="NamedPlace"/>: a Live2D model's eyes, mouth, neck, arms, legs...), its skeleton places the arms and legs (a
    /// VRM's humanoid bones) and the body's proportions place the rest: the head's zones around the face the renderer found
    /// (<see cref="ZoneHints.Face"/>, else the model's own named face, else the top of the character's outline) and the body's
    /// below it, measured on the bundled Hiyori and stretched to the character's height (to its skeleton's hips, or to the
    /// bottom of an outline that reaches down the legs). The hair stops just below the chin. A tail, wings or animal ears the
    /// model's own part names show are zones too (unless <see cref="ZoneDetectionOptions.MaximumSpecial"/> is 0). Each box is
    /// then fitted to the
    /// character's pixels, and a box over almost none of the character (or mostly off the picture: a bust shows no legs) is
    /// dropped. Detect zones then has the Thinking model find the zones. Returns the zones (fractions of the snapshot; null when
    /// none could be placed) and one line per source, with no failure and no requests.</summary>
    public static ZoneDetectionResult Estimate(ZonePixels snapshot, ZoneHints? hints, ZoneDetectionOptions? options = null)
    {
        options ??= new();
        if (TouchZonePictures.OpaqueBounds(snapshot, new(0, 0, snapshot.Width, snapshot.Height)) is not { } outline)
            return new(null, null, 0, ["the picture shows none of the character, so no zones were placed"]);
        var body = EstimateBody(snapshot, outline, hints);
        var picture = new TouchZoneBox(0, 0, 1, 1);
        var zones = new Dictionary<string, TouchZoneBox>(StringComparer.Ordinal);
        var sources = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var id in options.Zones.Concat(options.Required).Distinct(StringComparer.Ordinal).OrderBy(CharacterTouchZones.Order))
        {
            // A zone mostly off the picture (the legs of a bust drawn to its bottom edge) isn't there; one partly off is cut at its edge.
            if (Guess(id, hints, body, zones) is not { } guess || picture.Covers(guess.Box) < 0.5 || Overlap(guess.Box, picture) is not { } inside) continue;
            zones[id] = inside.Clamped();
            if (!sources.TryGetValue(guess.From, out var placed)) sources[guess.From] = placed = [];
            placed.Add(id);
        }
        // What the model's own part names say is special to it (its tail, wings or animal ears) is a zone too.
        if (options.MaximumSpecial > 0)
            foreach (var (id, box, _) in NamedSpecialPlaces(hints, zones))
            {
                if (picture.Covers(box) < 0.5 || Overlap(box, picture) is not { } inside) continue;
                zones[id] = inside.Clamped();
                if (!sources.TryGetValue(FromNamedParts, out var named)) sources[FromNamedParts] = named = [];
                named.Add(id);
            }
        var steps = new List<string> { body.Describe() };
        steps.AddRange(sources.Select(s => $"from {s.Key}: {string.Join(", ", s.Value)}"));
        var notes = Tidy(zones, snapshot, hints is { Bones.Count: > 0 } ? hints.FacesViewer : true);
        notes.AddRange(Finish(zones, snapshot, hints));
        if (notes.Count > 0) steps.Add("then " + string.Join("; ", notes));
        var result = Zones(zones);
        return new(result.Count == 0 ? null : result, null, 0, steps);
    }

    // Where the first guess puts a zone, and from what: the model's own named part for it, else the joints of a limb (its
    // skeleton's, else the proportions'), else the proportions around the face or down the body.
    private static (TouchZoneBox Box, string From)? Guess(string id, ZoneHints? hints, EstimatedBody body, IReadOnlyDictionary<string, TouchZoneBox> zones)
    {
        var side = id.EndsWith("_left", StringComparison.Ordinal) ? "left" : id.EndsWith("_right", StringComparison.Ordinal) ? "right" : null;
        var kind = side is null ? id : id[..id.LastIndexOf('_')];
        if (hints is { Named: true })
        {
            // A named arm is split along its length from the shoulder: the upper arm, the forearm, then the hand (unless named).
            if (side is not null && kind is "upper_arm" or "forearm" or "hand" && Whole(hints, "arms", side) is { } arm)
                return (kind == "hand" && Whole(hints, "hands", side) is { } named ? named
                    : ArmPart(kind, arm, body.At($"shoulder_{side}"), body.UnitY / body.Unit), FromNamedParts);
            if (kind == "hair" && Whole(hints, "hair") is { } hair) return (body.HeadHigh(hair), FromNamedParts);
            if (NamedPlace(id, hints) is { } place) return (place.Box, FromNamedParts);
        }
        if (kind == "hair" && hints?.Areas.FirstOrDefault(a => a.Part == "hair") is { } drawn) return (body.HeadHigh(drawn.Box), "the model's own hair drawables");
        if (side is not null && Limbs.TryGetValue(kind, out var limb)) return (body.Limb(side, limb), body.JointsFrom);
        if (side is not null && kind == "knee") return (body.Knee(side), body.JointsFrom);
        if (side is not null && kind == "inner_thigh")
        {
            // The inner side of the thigh, toward the legs' middle.
            var thigh = zones.TryGetValue($"thigh_{side}", out var found) ? found : body.Limb(side, Limbs["thigh"]);
            var (from, to) = thigh.CenterX > body.Middle ? (thigh.X, thigh.X + 0.45 * thigh.Width) : (thigh.X + 0.55 * thigh.Width, Right(thigh));
            return (FromEdges(from, thigh.Y + 0.1 * thigh.Height, to, thigh.Y + 0.75 * thigh.Height), "the thighs");
        }
        return Proportions.TryGetValue(kind, out var shape) ? (body.Shape(shape, side), shape.Head ? body.FaceFrom : FromProportions) : null;
    }

    // A part of a named arm, along its longer side (in pixels: aspect is the picture's width over its height) from the end nearer
    // the shoulder: the upper arm is the first half, the forearm the next share and the hand the far end. Across a slanted arm, the
    // upper arm keeps to the shoulder's side and the forearm and hand to the other.
    private static TouchZoneBox ArmPart(string kind, TouchZoneBox arm, (double X, double Y) shoulder, double aspect)
    {
        var (from, to) = kind switch { "upper_arm" => (0.0, 0.48), "forearm" => (0.42, 0.85), _ => (0.8, 1.0) };
        var tall = arm.Height >= arm.Width * aspect;
        var (length, across) = tall ? (arm.Height, arm.Width * aspect) : (arm.Width * aspect, arm.Height);
        var share = across > 0.3 * length ? kind == "hand" ? 0.45 : 0.62 : 1;
        // Shares of the box from its left (or top) edge.
        (double Start, double End) Along(bool fromStart) => fromStart ? (from, to) : (1 - to, 1 - from);
        (double Start, double End) Across(bool nearStart) => (kind == "upper_arm") == nearStart ? (0, share) : (1 - share, 1);
        var (x, y) = tall
            ? (Across(Math.Abs(shoulder.X - arm.X) <= Math.Abs(shoulder.X - Right(arm))), Along(Math.Abs(shoulder.Y - arm.Y) <= Math.Abs(shoulder.Y - Bottom(arm))))
            : (Along(Math.Abs(shoulder.X - arm.X) <= Math.Abs(shoulder.X - Right(arm))), Across(Math.Abs(shoulder.Y - arm.Y) <= Math.Abs(shoulder.Y - Bottom(arm))));
        return FromEdges(arm.X + x.Start * arm.Width, arm.Y + y.Start * arm.Height, arm.X + x.End * arm.Width, arm.Y + y.End * arm.Height);
    }

    // What the first guess places zones by: the face, the body's middle, how far the body below the chin is stretched to the
    // character, and the joints of its arms and legs.
    private static EstimatedBody EstimateBody(ZonePixels snapshot, PixelRect outline, ZoneHints? hints)
    {
        var figure = outline.Fraction(snapshot.Width, snapshot.Height);
        var body = new EstimatedBody { Faces = (hints is { Bones.Count: > 0 } ? hints.FacesViewer : true) ?? true };
        if (hints?.Face is { } anchor && anchor.Width >= 0.02 && anchor.Width <= 1.5 * figure.Width && figure.Contains(anchor.X, anchor.Y))
            (body.FaceX, body.EyeY, body.Unit, body.FaceFrom) = (anchor.X, anchor.Y, anchor.Width, "the face the renderer found");
        else if (((hints is { Named: true } ? Whole(hints, "face") : null) ?? hints?.Areas.FirstOrDefault(a => a.Part == "face" && a.Side is null)?.Box) is { } face)
            (body.FaceX, body.EyeY, body.Unit, body.FaceFrom) = (face.CenterX, face.Y + 0.5 * face.Height, face.Width, "the model's own face");
        else
        {
            var (x, y, width) = OutlineFace(snapshot, outline);
            (body.FaceX, body.EyeY, body.Unit, body.FaceFrom) = (x, y, width, "the top of the character's outline");
        }
        body.UnitY = body.Unit * snapshot.Width / snapshot.Height;

        var bones = hints?.Bones.GroupBy(b => b.Name, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal) ?? [];
        ZoneHintPoint? Bone(string name) => bones.TryGetValue(name, out var bone) ? bone : null;
        double? Between(string a, string b) => Bone(a) is { } p && Bone(b) is { } q ? (p.X + q.X) / 2 : null;
        body.Middle = Between("leftUpperLeg", "rightUpperLeg") ?? Between("leftUpperArm", "rightUpperArm") ?? Bone("hips")?.X ?? hints?.Middle ?? body.FaceX;
        if (Bone("leftUpperLeg") is { } leftLeg && Bone("rightUpperLeg") is { } rightLeg)
        {
            var hips = ((leftLeg.Y + rightLeg.Y) / 2 - body.EyeY) / body.UnitY;
            body.Stretch = Math.Clamp((hips - ChinLine) / (HipLine - ChinLine), 0.5, 2);
        }
        else
        {
            // An outline that reaches well down the legs (more than five face widths below the eyes) shows the whole body, its
            // bottom the soles; a shorter one (a bust, a half body) keeps the proportions as they are.
            var reach = (figure.Y + figure.Height - body.EyeY) / body.UnitY;
            if (reach >= 5) body.Stretch = Math.Clamp((reach - ChinLine) / (SoleLine - ChinLine), 0.75, 1.35);
        }
        foreach (var side in new[] { "left", "right" })
        {
            foreach (var (joint, x, y) in JointLines) body.Joints[$"{joint}_{side}"] = (body.Middle + body.Across(side, x), body.Y(y));
            foreach (var (joint, bone) in JointBones)
                if (Bone(side + bone) is { } at)
                {
                    body.Joints[$"{joint}_{side}"] = (at.X, at.Y);
                    body.Skeleton = true;
                }
            if (Bone(side + "Hand") is { } hand && Bone(side + "LowerArm") is { } elbow)
                body.Joints[$"fingers_{side}"] = (hand.X + 0.5 * (hand.X - elbow.X), hand.Y + 0.5 * (hand.Y - elbow.Y));
            if (Bone(side + "Foot") is { } foot && Bone(side + "Toes") is null) body.Joints[$"toes_{side}"] = (foot.X, foot.Y + 0.5 * body.UnitY);
        }
        return body;
    }

    // The face from the top of the character's outline, as the Live2D renderer guesses a face it can't find: the middle 80% of the
    // character's pixels from 4% to 14% of its height down span about the face's width, and the eyes are about that far below
    // its top. The middle of the eye line (fractions of the snapshot) and the face's width (a fraction of the snapshot's width).
    private static (double X, double Y, double Width) OutlineFace(ZonePixels snapshot, PixelRect outline)
    {
        var xs = new List<int>();
        int from = outline.Y + (int)(0.04 * outline.Height), to = Math.Max(from + 1, outline.Y + (int)Math.Ceiling(0.14 * outline.Height));
        for (var y = from; y < Math.Min(to, snapshot.Height); y++)
            for (var x = outline.X; x < outline.Right; x++)
                if (snapshot.Bgra[(y * snapshot.Width + x) * 4 + 3] > TouchZonePictures.OpaqueAlpha) xs.Add(x);
        double low = outline.X, high = outline.Right;
        if (xs.Count >= 4)
        {
            xs.Sort();
            (low, high) = (xs[(int)(0.1 * xs.Count)], xs[Math.Max(0, (int)Math.Ceiling(0.9 * xs.Count) - 1)] + 1);
        }
        var head = Math.Clamp(Math.Min(high - low, 0.6 * outline.Height), 1, outline.Width);
        return ((low + high) / 2 / snapshot.Width, (outline.Y + 0.92 * head) / snapshot.Height, head / snapshot.Width);
    }

    private sealed class EstimatedBody
    {
        // The middle of the face's eye line, the face's width (a fraction of the snapshot's width) and the same length as a
        // fraction of its height.
        internal double FaceX, EyeY, Unit, UnitY;
        internal string FaceFrom = "";
        // The middle of the body from left to right, and how far the body below the chin is stretched.
        internal double Middle, Stretch = 1;
        internal bool Faces = true, Skeleton;
        internal readonly Dictionary<string, (double X, double Y)> Joints = new(StringComparer.Ordinal);

        internal string JointsFrom => Skeleton ? FromSkeleton : FromProportions;

        // How far down a line of the proportions (face widths below the eye line) is, as a fraction of the snapshot.
        internal double Y(double line) => EyeY + (line <= ChinLine ? line : ChinLine + (line - ChinLine) * Stretch) * UnitY;

        // How far across the character's side of a pair is (face widths), as a fraction of the snapshot.
        internal double Across(string? side, double x) => side is null ? 0 : ((side == "left") == Faces ? x : -x) * Unit;

        internal (double X, double Y) At(string joint) => Joints.TryGetValue(joint, out var at) ? at : (Middle, EyeY);

        // Hair that hangs below the jaw (long hair, twin tails, a ponytail) is still hair, but its zone stops just below the chin,
        // so it doesn't take in the body.
        internal TouchZoneBox HeadHigh(TouchZoneBox hair) =>
            Y(0.6) is var jaw && jaw > hair.Y + 0.01 && jaw < Bottom(hair) ? FromEdges(hair.X, hair.Y, Right(hair), jaw) : hair;

        internal TouchZoneBox Shape((bool Head, double X, double Half, double Top, double Bottom) shape, string? side)
        {
            var x = (shape.Head ? FaceX : Middle) + Across(side, shape.X);
            return FromEdges(x - shape.Half * Unit, Y(shape.Top), x + shape.Half * Unit, Y(shape.Bottom));
        }

        // A part of a limb: a box around its share of the way between two joints, as thick as the limb across it (a hanging limb
        // is that wide, a level one that tall, and a little more each way at its ends).
        internal TouchZoneBox Limb(string side, (string From, string To, double Start, double End, double Half) limb)
        {
            var (a, b) = (At($"{limb.From}_{side}"), At($"{limb.To}_{side}"));
            double ax = a.X + (b.X - a.X) * limb.Start, ay = a.Y + (b.Y - a.Y) * limb.Start;
            double bx = a.X + (b.X - a.X) * limb.End, by = a.Y + (b.Y - a.Y) * limb.End;
            double dx = (bx - ax) / Unit, dy = (by - ay) / UnitY, length = Math.Sqrt(dx * dx + dy * dy);
            var (across, along) = length > 1e-9 ? (Math.Abs(dy) / length, Math.Abs(dx) / length) : (1.0, 1.0);
            double w = limb.Half * Math.Max(across, 0.3) * Unit, h = limb.Half * Math.Max(along, 0.3) * UnitY;
            return FromEdges(Math.Min(ax, bx) - w, Math.Min(ay, by) - h, Math.Max(ax, bx) + w, Math.Max(ay, by) + h);
        }

        internal TouchZoneBox Knee(string side)
        {
            var (x, y) = At($"knee_{side}");
            return FromEdges(x - 0.17 * Unit, y - 0.2 * UnitY, x + 0.17 * Unit, y + 0.2 * UnitY);
        }

        internal string Describe() => string.Create(CultureInfo.InvariantCulture,
            $"a first guess with no AI: the face from {FaceFrom} ({Unit:0.###} of the picture wide, its eyes {EyeY:0.###} down), ") +
            (Skeleton ? "the arms and legs from the model's skeleton" : "the body from its proportions") +
            (Math.Abs(Stretch - 1) < 0.005 ? "" : string.Create(CultureInfo.InvariantCulture, $", stretched {Stretch:0.##} times below the chin to the character's height"));
    }
}
