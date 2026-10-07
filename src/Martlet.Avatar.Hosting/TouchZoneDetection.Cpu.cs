using System.Text;

namespace Martlet.Avatar.Hosting;

public static partial class TouchZoneDetection
{
    // Left and right pairs, and parts that are always above others on a standing character.
    private static readonly (string Left, string Right)[] Pairs =
    [
        ("cheek_left", "cheek_right"), ("ear_left", "ear_right"), ("shoulder_left", "shoulder_right"), ("breast_left", "breast_right"),
        ("upper_arm_left", "upper_arm_right"), ("forearm_left", "forearm_right"), ("hand_left", "hand_right"), ("thigh_left", "thigh_right"),
        ("inner_thigh_left", "inner_thigh_right"), ("knee_left", "knee_right"), ("calf_left", "calf_right"), ("foot_left", "foot_right")
    ];

    private static readonly (string Above, string Below)[] Heights =
    [
        ("top_of_head", "forehead"), ("forehead", "nose"), ("nose", "lips"), ("lips", "chin"), ("forehead", "chin"), ("chin", "neck"),
        ("neck", "chest"), ("collarbone", "chest"), ("chest", "stomach"), ("chest", "navel"), ("stomach", "groin"), ("navel", "groin"),
        ("thigh_left", "knee_left"), ("thigh_right", "knee_right"), ("knee_left", "calf_left"), ("knee_right", "calf_right"),
        ("calf_left", "foot_left"), ("calf_right", "foot_right")
    ];

    // Zones that can be thin or wispy (a tail, a staff, horns): never shrunk to their pixels, never called empty.
    private static readonly HashSet<string> Thin = new(StringComparer.Ordinal)
    {
        "hair", "tail", "held_item", "horns", "glasses_or_hat", "skirt_hem", "wings", "animal_ears"
    };

    private static readonly string[] HeadMiddle = ["face", "nose", "lips", "chin", "forehead"];
    private static readonly string[] BodyMiddle = ["neck", "collarbone", "chest", "stomach", "navel", "groin", "hips"];

    /// <summary>What the CPU can tell for certain, applied to every box: kept inside the picture, shrunk to the character's
    /// pixels inside it (stray strands left out), and each left and right pair swapped back when it is the wrong way round for
    /// a character that faces the viewer (or away). Returns what it swapped.</summary>
    public static List<string> Tidy(Dictionary<string, TouchZoneBox> zones, ZonePixels snapshot, bool? facesViewer)
    {
        foreach (var id in zones.Keys.ToArray()) zones[id] = Fit(id, zones[id], snapshot);
        var notes = new List<string>();
        if (facesViewer is not { } faces) return notes;
        foreach (var (left, right) in Pairs)
            if (zones.TryGetValue(left, out var l) && zones.TryGetValue(right, out var r) &&
                (faces ? l.CenterX < r.CenterX - 0.01 : l.CenterX > r.CenterX + 0.01))
            {
                (zones[left], zones[right]) = (r, l);
                notes.Add($"swapped {left} and {right}");
            }
        return notes;
    }

    /// <summary>A zone's box kept inside the picture and shrunk to the character's pixels inside it (stray strands left out);
    /// thin zones (hair, a tail, a held item...) are only kept inside the picture.</summary>
    public static TouchZoneBox Fit(string id, TouchZoneBox box, ZonePixels snapshot)
    {
        box = box.Clamped();
        if (Thin.Contains(id)) return box;
        var area = PixelRect.Of(box, snapshot.Width, snapshot.Height);
        var (share, bounds) = TouchZonePictures.Opaque(snapshot, area);
        return share >= EmptyShare && bounds is { } inside && (inside.Width < area.Width || inside.Height < area.Height)
            ? inside.Fraction(snapshot.Width, snapshot.Height) : box;
    }

    /// <summary>What the CPU finds wrong with the boxes about to be checked (<paramref name="marks"/>), for the check's message:
    /// a box over the background, a part lower than one it should be above, one side alone on the wrong side, and a box that
    /// misses where the model's own skeleton puts its part (positions as fractions of <paramref name="crop"/>).</summary>
    public static List<string> Problems(IReadOnlyList<ZoneMark> marks, IReadOnlyDictionary<string, TouchZoneBox> zones, ZonePixels snapshot,
        bool? facesViewer, ZoneHints? hints, TouchZoneBox crop)
    {
        var problems = new List<string>();
        static string Name(ZoneMark mark) => $"box {mark.Number} ({mark.Id})";
        ZoneMark? Marked(string id) => marks.FirstOrDefault(m => m.Id == id);
        foreach (var mark in marks)
            if (!Thin.Contains(mark.Id) &&
                TouchZonePictures.Opaque(snapshot, PixelRect.Of(zones[mark.Id], snapshot.Width, snapshot.Height)).Share < EmptyShare)
                problems.Add($"{Name(mark)} covers almost none of the character");
        foreach (var (above, below) in Heights)
            if (Marked(above) is { } upper && Marked(below) is { } lower && zones[above].CenterY > zones[below].CenterY + 0.005)
                problems.Add($"{Name(upper)} is lower than {Name(lower)}, but it should be above it");
        if (facesViewer is { } faces)
            foreach (var (left, right) in Pairs)
            {
                if (Midline(zones, left) is not { } middle) continue;
                string near = faces ? "right" : "left", far = faces ? "left" : "right";
                if (Marked(left) is { } l && !zones.ContainsKey(right) && (faces ? zones[left].CenterX < middle - 0.05 : zones[left].CenterX > middle + 0.05))
                    problems.Add($"{Name(l)} is on the picture's {far} side, but the character's left side is on the picture's {near}");
                if (Marked(right) is { } r && !zones.ContainsKey(left) && (faces ? zones[right].CenterX > middle + 0.05 : zones[right].CenterX < middle - 0.05))
                    problems.Add($"{Name(r)} is on the picture's {near} side, but the character's right side is on the picture's {far}");
            }
        foreach (var (id, point) in Expected(hints))
            if (Marked(id) is { } mark && crop.Contains(point.X, point.Y) && !Grown(zones[id], 0.15).Contains(point.X, point.Y))
                problems.Add($"{Name(mark)} misses where the model's own skeleton puts it, at {Point((point.X - crop.X) / crop.Width, (point.Y - crop.Y) / crop.Height)}");
        return problems;
    }

    // The middle of the face (for the face's pairs) or of the body (for the rest), from the zones found there.
    private static double? Midline(IReadOnlyDictionary<string, TouchZoneBox> zones, string pair)
    {
        var middle = (pair.StartsWith("cheek", StringComparison.Ordinal) || pair.StartsWith("ear", StringComparison.Ordinal) ? HeadMiddle : BodyMiddle)
            .Where(zones.ContainsKey).Select(id => zones[id].CenterX).ToArray();
        return middle.Length == 0 ? null : middle.Average();
    }

    private static TouchZoneBox Grown(TouchZoneBox box, double share) =>
        new(box.X - box.Width * share, box.Y - box.Height * share, box.Width * (1 + 2 * share), box.Height * (1 + 2 * share));

    // Where the model's own skeleton (a VRM's humanoid bones) puts the middle of a zone.
    private static IEnumerable<(string Id, ZoneHintPoint Point)> Expected(ZoneHints? hints)
    {
        if (hints is null || hints.Bones.Count == 0) yield break;
        var bones = hints.Bones.GroupBy(b => b.Name, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        ZoneHintPoint? At(string a, string? b = null, double t = 0.5) =>
            !bones.TryGetValue(a, out var p) ? null : b is null ? p : bones.TryGetValue(b, out var q) ? new(a, p.X + (q.X - p.X) * t, p.Y + (q.Y - p.Y) * t) : null;
        var found = new List<(string, ZoneHintPoint?)>();
        foreach (var side in new[] { "left", "right" })
            found.AddRange(
            [
                ($"shoulder_{side}", At($"{side}UpperArm")), ($"upper_arm_{side}", At($"{side}UpperArm", $"{side}LowerArm")),
                ($"forearm_{side}", At($"{side}LowerArm", $"{side}Hand")), ($"hand_{side}", At($"{side}LowerArm", $"{side}Hand", 1.35)),
                ($"thigh_{side}", At($"{side}UpperLeg", $"{side}LowerLeg")), ($"knee_{side}", At($"{side}LowerLeg")),
                ($"calf_{side}", At($"{side}LowerLeg", $"{side}Foot")), ($"foot_{side}", At($"{side}Foot", $"{side}Toes") ?? At($"{side}Foot"))
            ]);
        found.AddRange([("neck", At("neck", "head")), ("chest", At("upperChest") ?? At("chest")), ("stomach", At("spine")), ("hips", At("hips"))]);
        foreach (var (id, point) in found)
            if (point is { } at) yield return (id, at);
    }

    // The last word: a box that still misses where the model's own skeleton puts its part moves there (keeping its size), and
    // a box over almost none of the character, which no touch could land in, is dropped.
    private static string[] Finish(Dictionary<string, TouchZoneBox> zones, ZonePixels snapshot, ZoneHints? hints)
    {
        var notes = new List<string>();
        foreach (var (id, point) in Expected(hints).ToArray())
            if (zones.TryGetValue(id, out var box) && !Grown(box, 0.15).Contains(point.X, point.Y))
            {
                zones[id] = new TouchZoneBox(point.X - box.Width / 2, point.Y - box.Height / 2, box.Width, box.Height).Clamped();
                notes.Add($"moved {id} onto the model's own skeleton");
            }
        foreach (var id in zones.Keys.ToArray())
            if (TouchZonePictures.Opaque(snapshot, PixelRect.Of(zones[id], snapshot.Width, snapshot.Height)).Share < (Thin.Contains(id) ? 0.002 : 0.01))
            {
                zones.Remove(id);
                notes.Add($"dropped {id}, which covered almost none of the character");
            }
        return [.. notes];
    }

    // A part the model found can be a close-up's window: a real box mostly on the character.
    private static bool Sensible(TouchZoneBox box, TouchZoneBox figure) =>
        box.Valid && box.Width >= 0.03 && box.Height >= 0.03 && figure.Covers(box) >= 0.5;

    // A part's window when the model didn't find it: from the skeleton's neck and hips, else from the character's outline.
    private static TouchZoneBox Fallback(string region, TouchZoneBox figure, ZoneHints? hints)
    {
        double top = figure.Y, height = figure.Height;
        double? Bone(string name) => hints?.Bones.FirstOrDefault(b => b.Name == name)?.Y;
        var (neck, hips) = (Bone("neck"), Bone("hips"));
        var (from, to) = region switch
        {
            "head" => (top, neck is { } n ? n + 0.02 : top + 0.32 * height),
            "upper_body" => (neck is { } n ? n - 0.03 : top + 0.18 * height, hips is { } h ? h + 0.06 : top + 0.62 * height),
            _ => (hips is { } h ? h - 0.06 : top + 0.5 * height, top + height)
        };
        from = Math.Clamp(from, 0, 0.95);
        to = Math.Min(1, Math.Max(to, from + 0.05));
        return new(figure.X, from, figure.Width, to - from);
    }

    // A close-up's window: the part with room around it, at least a sixth of the picture, inside the picture.
    private static TouchZoneBox Crop(TouchZoneBox box, ZonePixels snapshot)
    {
        double w = snapshot.Width, h = snapshot.Height;
        double x0 = box.X * w, y0 = box.Y * h, x1 = (box.X + box.Width) * w, y1 = (box.Y + box.Height) * h;
        var pad = Margin * Math.Max(x1 - x0, y1 - y0);
        (x0, y0, x1, y1) = (x0 - pad, y0 - pad, x1 + pad, y1 + pad);
        var least = 0.18 * Math.Max(w, h);
        if (x1 - x0 < least) (x0, x1) = ((x0 + x1 - least) / 2, (x0 + x1 + least) / 2);
        if (y1 - y0 < least) (y0, y1) = ((y0 + y1 - least) / 2, (y0 + y1 + least) / 2);
        (x0, y0, x1, y1) = (Math.Floor(Math.Max(0, x0)), Math.Floor(Math.Max(0, y0)), Math.Ceiling(Math.Min(w, x1)), Math.Ceiling(Math.Min(h, y1)));
        // Whole pixels, so the picture composed for it covers exactly this window.
        return new(x0 / w, y0 / h, Math.Max(1, x1 - x0) / w, Math.Max(1, y1 - y0) / h);
    }

    // ---------- what the model itself tells ----------

    private static readonly (string Part, string[] Words)[] PartWords =
    [
        ("hair", ["hair", "bang", "ahoge", "ponytail", "twintail", "braid", "髪", "头发", "頭髮"]),
        ("face", ["face", "顔", "脸", "臉"]),
        ("eyes", ["eye", "目", "眼", "瞳"]),
        ("mouth", ["mouth", "lip", "口", "嘴", "唇"]),
        ("cheeks", ["cheek", "頬", "腮"]),
        ("ears", ["ear", "耳"]),
        ("neck", ["neck", "首", "脖"]),
        ("arms", ["arm", "elbow", "sleeve", "腕", "臂", "袖"]),
        ("hands", ["hand", "finger", "palm", "thumb", "手", "指"]),
        ("legs", ["leg", "thigh", "knee", "calf", "stocking", "sock", "脚", "腿", "膝"]),
        ("feet", ["foot", "feet", "toe", "shoe", "boot", "靴", "鞋"]),
        ("tail", ["tail", "尻尾", "しっぽ", "尾"]),
        ("wings", ["wing", "翼", "羽"]),
        ("skirt", ["skirt", "スカート", "裙"])
    ];

    /// <summary>What the renderer's zones probe says, as fractions of the snapshot that sat at <paramref name="crop"/> on the page:
    /// the VRM humanoid bones, the Live2D drawables whose IDs name a body part (merged per part) and which way the character
    /// faces (from its shoulders, arms or legs). Null without a probe.</summary>
    public static ZoneHints? Hints(RendererZoneProbe? probe, TouchZoneBox crop)
    {
        if (probe is null || !(crop.Width > 0) || !(crop.Height > 0)) return null;
        var bones = (probe.Bones ?? []).Where(b => double.IsFinite(b.X) && double.IsFinite(b.Y) && !string.IsNullOrEmpty(b.Bone))
            .Select(b => new ZoneHintPoint(b.Bone, (b.X - crop.X) / crop.Width, (b.Y - crop.Y) / crop.Height)).ToArray();
        var drawables = (probe.Drawables ?? []).Where(d => d.Right > d.Left && d.Bottom > d.Top && !string.IsNullOrEmpty(d.Id)).ToArray();
        var areas = new List<ZoneHintArea>();
        foreach (var (part, words) in PartWords)
        {
            var named = drawables.Where(d => Names(d.Id, words)).ToArray();
            if (named.Length == 0) continue;
            double left = named.Min(d => d.Left), top = named.Min(d => d.Top);
            var box = new TouchZoneBox(left, top, named.Max(d => d.Right) - left, named.Max(d => d.Bottom) - top).Relative(crop);
            if (Clip(box) is { } seen) areas.Add(new(part, seen, named.Length));
        }
        double? Apart(string left, string right) =>
            bones.FirstOrDefault(b => b.Name == left) is { } l && bones.FirstOrDefault(b => b.Name == right) is { } r ? l.X - r.X : null;
        bool? faces = (Apart("leftUpperArm", "rightUpperArm") ?? Apart("leftShoulder", "rightShoulder") ?? Apart("leftUpperLeg", "rightUpperLeg")) is { } dx &&
            Math.Abs(dx) > 0.02 ? dx > 0 : null;
        return new(bones, areas, faces);
    }

    // Whether a drawable's ID names one of the words: a Latin word starts one of its words (HairBack, D_HAIR_FRONT_00, eyeL),
    // any other is found anywhere in it.
    private static bool Names(string id, string[] words)
    {
        var tokens = Tokens(id);
        return words.Any(word => word.All(char.IsAscii) ? tokens.Any(t => t.StartsWith(word, StringComparison.Ordinal)) : id.Contains(word, StringComparison.Ordinal));
    }

    private static List<string> Tokens(string id)
    {
        var tokens = new List<string>();
        var current = new StringBuilder();
        for (var i = 0; i < id.Length; i++)
        {
            var c = id[i];
            if (!char.IsAsciiLetter(c))
            {
                Flush();
                continue;
            }
            if (char.IsUpper(c) && current.Length > 0 && char.IsLower(id[i - 1])) Flush();
            current.Append(char.ToLowerInvariant(c));
        }
        Flush();
        return tokens;

        void Flush()
        {
            if (current.Length == 0) return;
            tokens.Add(current.ToString());
            current.Clear();
        }
    }
}
