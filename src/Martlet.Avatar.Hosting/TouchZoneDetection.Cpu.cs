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
        foreach (var mark in marks)
            if (NamedPlace(mark.Id, hints) is { } place && Clip(place.Box.Relative(crop)) is { } seen)
            {
                var at = $"{Point(seen.X, seen.Y)} to {Point(seen.X + seen.Width, seen.Y + seen.Height)}";
                if (Misses(zones[mark.Id], place.Box)) problems.Add($"{Name(mark)} is off the model's own {place.What}, which lies at {at}");
                else if (Overreaches(zones[mark.Id], place.Box)) problems.Add($"{Name(mark)} reaches well past the model's own {place.What}, which lies at {at}");
            }
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

    // The last word: a box that still misses where the model's own skeleton puts its part moves there (keeping its size), a box
    // that misses where the model's own named parts put its zone moves there (one that reaches well past them is limited to
    // them), and a box over almost none of the character, which no touch could land in, is dropped.
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
            if (NamedPlace(id, hints) is { } place)
            {
                var box = zones[id];
                if (Misses(box, place.Box))
                {
                    zones[id] = Fit(id, place.Box, snapshot);
                    notes.Add($"moved {id} onto the model's own {place.What}");
                }
                else if (Overreaches(box, place.Box) && Overlap(box, Grown(place.Box, 0.25)) is { } inside)
                {
                    zones[id] = Fit(id, inside, snapshot);
                    notes.Add($"limited {id} to the model's own {place.What}");
                }
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

    // ---------- zones that must be found, worked out ----------

    /// <summary>Works out each zone in <paramref name="required"/> that is still missing: where the model's own named parts put it
    /// (<paramref name="hints"/>: the neck on its neck, the lips on its mouth, the hips between its upper body and its legs...),
    /// else from the zones around it: the lips and
    /// ears from the face, the neck below the chin, the chest from the breasts (or between the neck and the stomach), the breasts
    /// from the chest, the waist at the navel, the hips over the thighs, the groin between the thighs, the buttocks low on the
    /// hips and each inner thigh from its thigh (one of a pair from the other, mirrored). Without those it uses the parts' windows
    /// (<paramref name="regions"/>: head, upper_body and lower_body, fractions of the snapshot, and whether the model found them)
    /// and the character's outline (<paramref name="figure"/>); lower-body zones only when there is a lower body. Each box is
    /// fitted to the character's pixels. Left and right are the character's own: facing the viewer (or unknown), its left is on
    /// the picture's right. Returns one note per zone worked out, saying from what.</summary>
    public static List<string> Derive(Dictionary<string, TouchZoneBox> zones, IReadOnlyCollection<string> required, ZonePixels snapshot,
        bool? facesViewer, IReadOnlyDictionary<string, (TouchZoneBox Box, bool Found)> regions, TouchZoneBox figure, ZoneHints? hints = null)
    {
        var notes = new List<string>();
        if (required.All(zones.ContainsKey)) return notes;
        var faces = facesViewer ?? true;
        TouchZoneBox? Has(string id) => zones.TryGetValue(id, out var box) ? box : null;
        TouchZoneBox Region(string id, double top, double height) => regions.TryGetValue(id, out var region) ? region.Box
            : new(figure.X, figure.Y + top * figure.Height, figure.Width, height * figure.Height);
        var head = Region("head", 0, 0.3);
        var upper = Region("upper_body", 0.18, 0.44);
        var lower = Region("lower_body", 0.5, 0.5);
        void Put(string id, TouchZoneBox box, string from)
        {
            if (!required.Contains(id) || zones.ContainsKey(id)) return;
            zones[id] = Fit(id, box.Clamped(), snapshot);
            notes.Add($"{id} from {from}");
        }
        // Where the model's own named parts put a zone comes first: it doesn't depend on the boxes the vision model got wrong.
        foreach (var id in required)
            if (NamedPlace(id, hints) is { } place) Put(id, place.Box, "the model's own " + place.What);
        static TouchZoneBox Edges(double left, double top, double right, double bottom) =>
            new(left, top, Math.Max(0.005, right - left), Math.Max(0.005, bottom - top));
        static TouchZoneBox Join(params TouchZoneBox[] boxes) =>
            Edges(boxes.Min(b => b.X), boxes.Min(b => b.Y), boxes.Max(b => b.X + b.Width), boxes.Max(b => b.Y + b.Height));
        static TouchZoneBox Mirror(TouchZoneBox box, double middle) => new(2 * middle - box.X - box.Width, box.Y, box.Width, box.Height);
        // One side of a box: the character's left is on the picture's right when it faces you.
        TouchZoneBox Side(TouchZoneBox box, bool characterLeft)
        {
            var width = box.Width * 0.48;
            return new(characterLeft == faces ? box.X + box.Width - width : box.X, box.Y, width, box.Height);
        }
        // One of a pair from the other, mirrored about middle; else from the box given.
        void Pair(string id, string other, double middle, TouchZoneBox box, string from)
        {
            if (Has(other) is { } partner) Put(id, Mirror(partner, middle), $"{other}, mirrored");
            else Put(id, box, from);
        }

        // The head: the face (found, else its own parts, else the middle of the head's lower part) gives the lips, ears and neck.
        var faceParts = new[] { "forehead", "nose", "lips", "chin", "cheek_left", "cheek_right" }.Select(Has).OfType<TouchZoneBox>().ToArray();
        var (face, faceFrom) = Has("face") is { } found ? (found, "the face")
            : faceParts.Length >= 2 ? (Join(faceParts), "the face's parts")
            : (Edges(head.CenterX - 0.25 * head.Width, head.Y + 0.35 * head.Height, head.CenterX + 0.25 * head.Width, head.Y + 0.9 * head.Height), "the head");
        Put("lips", Edges(face.CenterX - 0.17 * face.Width, face.Y + 0.68 * face.Height, face.CenterX + 0.17 * face.Width, face.Y + 0.82 * face.Height), faceFrom);
        foreach (var left in new[] { true, false })
        {
            // Ears stick out past the face's sides.
            var x = left == faces ? face.X + 0.98 * face.Width : face.X - 0.3 * face.Width;
            Pair(left ? "ear_left" : "ear_right", left ? "ear_right" : "ear_left", face.CenterX,
                Edges(x, face.Y + 0.25 * face.Height, x + 0.32 * face.Width, face.Y + 0.6 * face.Height), faceFrom);
        }
        var neckTop = Has("chin") is { } chin ? chin.Y + chin.Height : face.Y + face.Height;
        var neckBottom = Has("collarbone")?.Y ?? Has("chest")?.Y ?? 0;
        if (neckBottom < neckTop + 0.1 * face.Height || neckBottom > neckTop + face.Height) neckBottom = neckTop + 0.35 * face.Height;
        Put("neck", Edges(face.CenterX - 0.2 * face.Width, neckTop, face.CenterX + 0.2 * face.Width, neckBottom), Has("chin") is null ? faceFrom : "the chin");

        // The torso: its middle and width from the zones down its middle, else the upper body's window.
        var middles = BodyMiddle.Select(Has).OfType<TouchZoneBox>().ToArray();
        var middle = middles.Length > 0 ? middles.Average(b => b.CenterX) : upper.CenterX;
        var torso = (Has("stomach") ?? Has("chest") ?? Has("waist"))?.Width ?? 0.45 * upper.Width;
        var breasts = new[] { Has("breast_left"), Has("breast_right") }.OfType<TouchZoneBox>().ToArray();
        TouchZoneBox chest;
        if (Has("chest") is { } chestFound) chest = chestFound;
        else if (breasts.Length > 0)
        {
            var both = breasts.Length == 2 ? Join(breasts) : Join(breasts[0], Mirror(breasts[0], middle));
            chest = Edges(both.X - 0.08 * both.Width, both.Y - 0.35 * both.Height, both.X + 1.08 * both.Width, both.Y + both.Height);
            Put("chest", chest, "the breasts");
        }
        else
        {
            var top = Has("neck") is { } neck ? neck.Y + neck.Height : upper.Y + 0.1 * upper.Height;
            var bottom = Has("stomach")?.Y ?? Has("navel")?.Y ?? 0;
            if (bottom < top + 0.05 * upper.Height) bottom = top + 0.3 * upper.Height;
            // The chest is wider than the stomach below it.
            chest = Edges(middle - 0.65 * torso, top, middle + 0.65 * torso, bottom);
            Put("chest", chest, Has("neck") is null ? "the upper body" : "the neck and stomach");
        }
        var bust = Edges(chest.X, chest.Y + 0.3 * chest.Height, chest.X + chest.Width, chest.Y + chest.Height);
        Pair("breast_left", "breast_right", chest.CenterX, Side(bust, characterLeft: true), "the chest");
        Pair("breast_right", "breast_left", chest.CenterX, Side(bust, characterLeft: false), "the chest");
        var (stomach, navel) = (Has("stomach"), Has("navel"));
        var waistY = navel?.CenterY ?? (stomach is { } s ? s.Y + 0.75 * s.Height : chest.Y + 1.6 * chest.Height);
        var waistHeight = stomach is { } belly ? 0.8 * belly.Height : 0.4 * chest.Height;
        Put("waist", Edges(middle - 0.6 * torso, waistY - waistHeight / 2, middle + 0.6 * torso, waistY + waistHeight / 2),
            navel is not null ? "the navel" : stomach is not null ? "the stomach" : "the chest");

        // The lower body, only when the character has one: the hips over the thighs, the groin between them, the buttocks low on
        // the hips and the inner side of each thigh.
        var legs = Regions[2].Zones.Any(zones.ContainsKey) || regions.TryGetValue("lower_body", out var legsFound) && legsFound.Found;
        if (!legs)
        {
            var skipped = new[] { "hips", "groin", "buttocks", "inner_thigh_left", "inner_thigh_right" }.Where(id => required.Contains(id) && !zones.ContainsKey(id)).ToArray();
            if (skipped.Length > 0) notes.Add($"not {string.Join(", ", skipped)}: the character shows no lower body");
            return notes;
        }
        var thighs = new[] { Has("thigh_left"), Has("thigh_right") }.OfType<TouchZoneBox>().ToArray();
        var legsMiddle = thighs.Length == 2 ? thighs.Average(t => t.CenterX) : Has("groin")?.CenterX ?? middle;
        TouchZoneBox hips;
        string hipsFrom;
        if (Has("hips") is { } hipsFound) (hips, hipsFrom) = (hipsFound, "the hips");
        else if (thighs.Length > 0)
        {
            // Thighs often flare wider than the hips: the hips take three quarters of their width, over the legs' middle, from the
            // waist (kept a fair way above the thighs) to just below the thighs' top.
            var both = thighs.Length == 2 ? Join(thighs) : Join(thighs[0], Mirror(thighs[0], middle));
            var top = Math.Clamp(Has("waist") is { } waist ? waist.Y + waist.Height : both.Y - 0.3 * both.Height,
                both.Y - 0.6 * both.Height, both.Y - 0.2 * both.Height);
            (hips, hipsFrom) = (Edges(legsMiddle - 0.375 * both.Width, top, legsMiddle + 0.375 * both.Width, both.Y + 0.05 * both.Height), "the thighs");
            Put("hips", hips, hipsFrom);
        }
        else
        {
            (hips, hipsFrom) = (Edges(lower.CenterX - 0.3 * lower.Width, lower.Y, lower.CenterX + 0.3 * lower.Width, lower.Y + 0.2 * lower.Height), "the lower body");
            Put("hips", hips, hipsFrom);
        }
        var crotch = thighs.Length > 0 ? Math.Max(hips.Y + hips.Height, thighs.Min(t => t.Y) + 0.15 * thighs.Max(t => t.Height)) : hips.Y + hips.Height;
        Put("groin", Edges(legsMiddle - 0.15 * hips.Width, hips.Y + 0.45 * hips.Height, legsMiddle + 0.15 * hips.Width, crotch),
            thighs.Length > 0 ? hipsFrom == "the hips" ? "the hips and thighs" : "the thighs" : hipsFrom);
        Put("buttocks", Edges(hips.X, hips.Y + 0.4 * hips.Height, hips.X + hips.Width, hips.Y + 1.15 * hips.Height), hipsFrom);
        foreach (var left in new[] { true, false })
        {
            var (id, thighId) = left ? ("inner_thigh_left", "thigh_left") : ("inner_thigh_right", "thigh_right");
            if (Has(thighId) is { } thigh)
            {
                // The inner side faces the legs' middle.
                var (from, to) = thigh.CenterX > legsMiddle ? (thigh.X, thigh.X + 0.45 * thigh.Width) : (thigh.X + 0.55 * thigh.Width, thigh.X + thigh.Width);
                Put(id, Edges(from, thigh.Y + 0.1 * thigh.Height, to, thigh.Y + 0.75 * thigh.Height), thighId);
            }
            else
                Pair(id, left ? "inner_thigh_right" : "inner_thigh_left", legsMiddle,
                    Side(Edges(lower.CenterX - 0.2 * lower.Width, lower.Y + 0.12 * lower.Height, lower.CenterX + 0.2 * lower.Width, lower.Y + 0.4 * lower.Height), left),
                    "the lower body");
        }
        return notes;
    }

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

    // The body parts a Live2D model's own names can say, in English, Chinese, Japanese and Korean rigging words. A drawable ID
    // names every part one of its words names; a part's name names the part of its longest word, so 马尾 (a ponytail) is hair
    // and 尾巴 a tail, 手臂 an arm and 手 a hand. Equal words go to the part listed first.
    private static readonly (string Part, string[] Words)[] PartWords =
    [
        ("hair", ["hair", "bang", "fringe", "ahoge", "ponytail", "twintail", "pigtail", "braid", "sideburn", "髪", "发", "髮", "头发", "頭髮",
            "頭髪", "刘海", "劉海", "呆毛", "马尾", "馬尾", "辫", "辮", "鬓", "前髪", "後ろ髪", "横髪", "もみあげ", "アホ毛", "ポニーテール",
            "ツインテール", "머리카락", "앞머리", "뒷머리", "옆머리"]),
        ("animal_ears", ["cat ear", "animal ear", "fox ear", "dog ear", "wolf ear", "bunny ear", "rabbit ear", "catear", "kemomimi", "nekomimi",
            "猫耳", "兽耳", "獣耳", "狐耳", "犬耳", "狼耳", "兔耳", "うさ耳", "ケモミミ", "けもみみ", "ネコミミ", "ウサミミ", "동물귀", "고양이귀"]),
        ("ears", ["ear", "耳", "귀"]),
        ("head", ["head", "頭", "头", "あたま", "머리"]),
        ("face", ["face", "eyebrow", "brow", "顔", "脸", "臉", "面部", "輪郭", "眉", "かお", "얼굴", "눈썹"]),
        ("eyes", ["eye", "iris", "pupil", "lash", "目", "眼", "瞳", "睫", "まつげ", "눈"]),
        ("nose", ["nose", "鼻", "코"]),
        ("mouth", ["mouth", "lip", "teeth", "tooth", "tongue", "口", "嘴", "唇", "牙", "齿", "歯", "舌", "嘴角", "くち", "입", "입술"]),
        ("cheeks", ["cheek", "blush", "頬", "颊", "頰", "腮", "脸颊", "臉頰", "ほお", "볼"]),
        ("neck", ["neck", "首", "脖", "颈", "頸", "목"]),
        ("chest", ["chest", "breast", "bust", "boob", "oppai", "胸", "乳", "乳首", "おっぱい", "가슴"]),
        ("waist", ["waist", "belly", "stomach", "navel", "abdomen", "腰", "腹", "肚", "へそ", "허리", "배꼽"]),
        ("torso", ["body", "torso", "upperbody", "upper body", "shirt", "jacket", "coat", "blouse", "vest", "collar", "身体", "躯干", "軀幹",
            "胴", "体", "身", "上半身", "上衣", "衣服", "外套", "衬衫", "夹克", "领带", "領帶", "领口", "領口", "领子", "衣领", "襟", "服",
            "シャツ", "ジャケット", "ネクタイ", "몸", "상체", "상의"]),
        ("arms", ["arm", "elbow", "sleeve", "shoulder", "腕", "臂", "肘", "袖", "肩", "胳膊", "手臂", "袖口", "二の腕", "팔", "어깨", "소매"]),
        ("hands", ["hand", "finger", "palm", "thumb", "fist", "wrist", "glove", "手", "指", "掌", "拳", "手腕", "手首", "手袋", "手套", "손",
            "손목", "장갑"]),
        ("lower_body", ["lowerbody", "lower body", "下半身", "하체"]),
        ("hips", ["hip", "butt", "pelvis", "crotch", "groin", "pants", "shorts", "trouser", "panties", "panty", "underwear", "臀", "屁股",
            "胯", "裆", "裤", "褲", "尻", "お尻", "パンツ", "ズボン", "엉덩이", "바지"]),
        ("legs", ["leg", "thigh", "knee", "calf", "shin", "stocking", "sock", "tights", "腿", "膝", "太もも", "太腿", "ふともも", "すね",
            "ふくらはぎ", "袜", "靴下", "ニーソ", "タイツ", "다리", "허벅지", "무릎"]),
        ("feet", ["foot", "feet", "toe", "shoe", "boot", "heel", "sandal", "足", "脚", "鞋", "靴", "足首", "つま先", "ブーツ", "발", "발목", "신발"]),
        ("tail", ["tail", "尾", "尾巴", "尻尾", "しっぽ", "シッポ", "꼬리"]),
        ("wings", ["wing", "翼", "翅", "羽", "날개"]),
        ("skirt", ["skirt", "スカート", "裙", "치마", "스커트"])
    ];

    /// <summary>What the renderer's zones probe says, as fractions of the snapshot that sat at <paramref name="crop"/> on the page:
    /// the VRM humanoid bones, the Live2D drawables whose IDs name a body part (merged per part), the drawables in the parts the
    /// model's own part names call body parts (its DisplayInfo file's names, else the part IDs; <see cref="ZoneHints.Pieces"/>)
    /// and which way the character faces (from its shoulders, arms or legs). Null without a probe.</summary>
    public static ZoneHints? Hints(RendererZoneProbe? probe, TouchZoneBox crop)
    {
        if (probe is null || !(crop.Width > 0) || !(crop.Height > 0)) return null;
        var bones = (probe.Bones ?? []).Where(b => double.IsFinite(b.X) && double.IsFinite(b.Y) && !string.IsNullOrEmpty(b.Bone))
            .Select(b => new ZoneHintPoint(b.Bone, (b.X - crop.X) / crop.Width, (b.Y - crop.Y) / crop.Height)).ToArray();
        var drawables = (probe.Drawables ?? []).Where(d => d.Right > d.Left && d.Bottom > d.Top && !string.IsNullOrEmpty(d.Id) &&
            double.IsFinite(d.Left) && double.IsFinite(d.Top) && double.IsFinite(d.Right) && double.IsFinite(d.Bottom)).ToArray();
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
        // A Live2D character faces the viewer (RunAsync reads it so too).
        var parts = NamedParts(probe, drawables, crop, bones.Length > 0 ? faces ?? true : true);
        // The part names place a body part better than drawable IDs do.
        return new(bones, [.. parts.Areas, .. areas.Where(a => parts.Areas.All(n => n.Part != a.Part))], faces)
        {
            Pieces = parts.Pieces, Middle = parts.Middle, ModelParts = parts.Count, NamedModelParts = parts.Named
        };
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
