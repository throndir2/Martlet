using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Martlet.Core.Contracts;

namespace Martlet.Avatar.Hosting;

/// <summary>Reading the vision model's answer and binding zones to the model's drawables, bones and parts.</summary>
public static partial class CharacterTouchZones
{
    // ---------- the vision model's answer ----------

    /// <summary>The zones in a vision model's answer about a whole <paramref name="width"/> by <paramref name="height"/> picture
    /// (the fixture's stand-in answer and MCP's simulated one), with their boxes as fractions of the picture: JSON with boxes as
    /// arrays or named edges, a bare list, boxes keyed by zone, or Qwen-style <c>bbox_2d</c> grounding in pixels or 0..1000; an
    /// answer cut off part way keeps the zones it finished. Unknown zones (unless <paramref name="special"/>: then a zone special to
    /// the character keeps its own ID and name, as <see cref="TouchZoneDetection.SpecialId"/> reads it) and boxes that can't be
    /// placed are left out; null when nothing could be read. Detection itself asks step by step (<see cref="TouchZoneDetection"/>).</summary>
    public static IReadOnlyList<CharacterTouchZone>? Parse(string? answer, int width, int height, bool special = false)
    {
        if (width <= 0 || height <= 0) return null;
        var zones = (special ? TouchZoneDetection.ReadAnyZones(answer, width, height)
                : TouchZoneDetection.ReadBoxes(answer, width, height, Normalize).Select(z => (Id: z.Key, Label: (string?)null, Box: z.Value)))
            .Take(MaximumZones).Select(z => new CharacterTouchZone { Id = z.Id, Label = z.Label, Box = z.Box.Clamped(), Enabled = true }).ToArray();
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
    /// breast: the chest; the groin: the hips). A Live2D area also takes the pieces of its own feature that its box leaves out
    /// (<see cref="Pieces"/>). A tail, wings or animal ears then follow the model's own part for them (one it names so, or one
    /// its physics swings that the box holds some of), all of it even where it hides behind the body at rest, in areas along
    /// it; hair also follows the swinging hair its box doesn't hold (a ponytail). A drawable a zone follows so belongs to that
    /// zone only (<see cref="Follow"/>). <paramref name="crop"/> is where the snapshot sat on the page (null: the boxes are
    /// already fractions of the page).</summary>
    public static IReadOnlyList<CharacterTouchZone> Bind(IReadOnlyList<CharacterTouchZone> zones, TouchZoneBox? crop, RendererZoneProbe? probe)
    {
        if (probe is null) return zones;
        var joints = (probe.Bones ?? []).Where(b => double.IsFinite(b.X) && double.IsFinite(b.Y) && !string.IsNullOrEmpty(b.Bone)).ToArray();
        var parts = BoneParts(joints);
        var model = ModelStructure.Read(probe, crop);
        // Each area bound by its box, then given the pieces of its feature its box leaves out, shared out among all the areas.
        var boxed = zones.Select(zone => zone.AllAreas.Where(a => !a.FromModel).Select(a => BindArea(zone.Id, a, crop, probe, joints, parts)).ToList()).ToArray();
        var flat = boxed.SelectMany((areas, i) => areas.Select(a => new CharacterTouchZone { Id = zones[i].Id, Box = a.Box, Drawables = a.Drawables, Bones = a.Bones }))
            .ToArray();
        var pieces = Pieces(flat, crop, probe);
        var at = 0;
        foreach (var areas in boxed)
            for (var k = 0; k < areas.Count; k++) areas[k] = areas[k] with { Drawables = pieces[at++].Drawables };
        var bound = new CharacterTouchZone[zones.Count];
        // Zones that follow the model's own parts go first, so a tail takes its parts before another zone's box does.
        foreach (var i in Enumerable.Range(0, zones.Count).OrderBy(i => FollowOrder(zones[i].Id)))
        {
            var zone = zones[i];
            bound[i] = Follow(zone, boxed[i], crop, model) ?? Compose(zone, boxed[i].Count > 0 ? boxed[i]
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

    /// <summary>The zones with the pieces of their own feature that lie by their box (Live2D). A vision box drawn tight around a
    /// feature leaves some of its drawables out (an eye's white and lashes, a blush wider than the cheek), and a bigger zone that
    /// frames it (the hair, around the whole face) would then get them alone. The model's own parts group a feature's pieces, so
    /// a drawable that no smaller zone has yet is also linked to a zone of the feature its own part (or the nearest part it sits
    /// in that is linked to any zone, through the drawables mostly inside a box) belongs to. A part belongs to one feature (one
    /// zone, or a left and a right one) when nearly all of its linked drawables' area (<see cref="OneFeature"/>) lies in that
    /// feature's zones, a zone whose box holds another of them not counting: a stray lash tip in a cheek's box doesn't stop an
    /// eye's part lending its lashes, but the whole head with its eyes and mouth lends nothing. A part the model names (its
    /// DisplayInfo name or ID, or a part it sits in: <see cref="TouchZoneDetection.PartName"/>) lends only to zones of what it
    /// names (<see cref="BodyPartZones"/>): 頬, the cheeks, never to an ear whose box takes in a piece of the cheek. The
    /// drawable's middle must lie in
    /// the zone's box grown by half its size (<see cref="Around"/>) and it must be at most four times the box's size; with both
    /// sides, the nearer one takes it. It never removes a link.</summary>
    private static IReadOnlyList<CharacterTouchZone> Pieces(IReadOnlyList<CharacterTouchZone> zones, TouchZoneBox? crop, RendererZoneProbe probe)
    {
        var drawables = (probe.Drawables ?? []).Where(d => d.Right > d.Left && d.Bottom > d.Top && !string.IsNullOrEmpty(d.Part))
            .DistinctBy(d => d.Id, StringComparer.Ordinal).ToArray();
        if (drawables.Length == 0 || zones.Count == 0) return zones;
        var parents = new Dictionary<string, string>(StringComparer.Ordinal);
        var names = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var part in probe.Parts ?? [])
        {
            if (string.IsNullOrEmpty(part.Id)) continue;
            if (!string.IsNullOrEmpty(part.Parent)) parents.TryAdd(part.Id, part.Parent);
            if (!string.IsNullOrEmpty(part.Name)) names.TryAdd(part.Id, part.Name);
        }
        // A part, then the parts it sits in.
        IEnumerable<string> Lineage(string part)
        {
            string? at = part;
            for (var depth = 0; at is not null && depth < 64; depth++, at = parents.GetValueOrDefault(at)) yield return at;
        }
        // The body part the model's own names say a drawable's part (or the nearest part it sits in that says one) is: by its
        // DisplayInfo name, else its ID (目 or PartEye: the eyes).
        string? Says(string part) => Lineage(part).Select(p => TouchZoneDetection.PartName(names.GetValueOrDefault(p)).Part ??
            TouchZoneDetection.PartName(p).Part).FirstOrDefault(said => said is not null);
        static TouchZoneBox Bounds(RendererDrawableBox d) => new(d.Left, d.Top, d.Right - d.Left, d.Bottom - d.Top);
        var pages = zones.Select(zone => crop is null ? zone.Box : zone.Box.Within(crop)).ToArray();
        var owners = new Dictionary<string, List<int>>(StringComparer.Ordinal);
        for (var i = 0; i < zones.Count; i++)
            foreach (var id in zones[i].Drawables)
            {
                if (!owners.TryGetValue(id, out var list)) owners[id] = list = [];
                list.Add(i);
            }
        // How much drawable area links each part to each zone, through its own drawables and those of the parts inside it.
        var linked = new Dictionary<string, Dictionary<int, double>>(StringComparer.Ordinal);
        foreach (var d in drawables)
            if (owners.TryGetValue(d.Id, out var of))
                foreach (var part in Lineage(d.Part!))
                {
                    if (!linked.TryGetValue(part, out var weights)) linked[part] = weights = [];
                    foreach (var i in of) weights[i] = weights.GetValueOrDefault(i) + Bounds(d).Area;
                }
        var added = zones.Select(_ => new List<string>()).ToArray();
        foreach (var d in drawables)
        {
            if (Lineage(d.Part!).FirstOrDefault(linked.ContainsKey) is not { } part) continue;
            var weights = linked[part];
            var finest = weights.Keys.Where(i => !weights.Keys.Any(j => j != i && Inside(pages[j], pages[i]))).ToArray();
            if (owners.TryGetValue(d.Id, out var of) && of.Any(finest.Contains)) continue;
            var total = finest.Sum(i => weights[i]);
            var feature = finest.GroupBy(i => Feature(zones[i].Id), StringComparer.Ordinal).MaxBy(g => g.Sum(i => weights[i]));
            if (feature is null || !(total > 0) || feature.Sum(i => weights[i]) < OneFeature * total) continue;
            // A part the model names lends only to zones of what it names: the cheek's blush never goes to an ear whose box
            // takes in a piece of the cheek.
            if (Says(d.Part!) is { } said && !(BodyPartZones.TryGetValue(said, out var kinds) && kinds.Contains(feature.Key, StringComparer.Ordinal))) continue;
            var box = Bounds(d);
            var near = feature.Where(i => box.Area <= 4 * pages[i].Area && Around(pages[i]).Contains(box.CenterX, box.CenterY))
                .OrderBy(i => pages[i].Distance(box.CenterX, box.CenterY)).ThenBy(i => pages[i].Area).Select(i => (int?)i).FirstOrDefault();
            if (near is { } at) added[at].Add(d.Id);
        }
        return zones.Select((zone, i) => added[i].Count == 0 ? zone
            : zone with { Drawables = [.. zone.Drawables.Concat(added[i]).Distinct(StringComparer.Ordinal).Take(256)] }).ToArray();
    }

    /// <summary>The share of a part's linked drawable area one feature must hold for the part to lend it its other pieces.</summary>
    private const double OneFeature = 0.9;

    /// <summary>The zones (without their side) each body part a model's own part names can say (<see cref="TouchZoneDetection.PartName"/>)
    /// may lend its pieces to.</summary>
    private static readonly Dictionary<string, string[]> BodyPartZones = new(StringComparer.Ordinal)
    {
        ["hair"] = ["hair", "top_of_head"], ["animal_ears"] = ["animal_ears"], ["ears"] = ["ear"],
        ["head"] = ["top_of_head", "hair", "forehead", "face"], ["face"] = ["face", "forehead", "cheek", "chin"], ["eyes"] = ["eye"],
        ["nose"] = ["nose"], ["mouth"] = ["lips"], ["cheeks"] = ["cheek"], ["neck"] = ["neck", "collarbone"],
        ["chest"] = ["chest", "breast", "collarbone"], ["waist"] = ["waist", "stomach", "navel", "lower_back"],
        ["torso"] = ["chest", "breast", "collarbone", "stomach", "navel", "waist", "lower_back", "shoulder"],
        ["arms"] = ["upper_arm", "forearm", "shoulder"], ["hands"] = ["hand", "held_item"],
        ["lower_body"] = ["hips", "hip", "groin", "buttocks", "thigh", "inner_thigh", "skirt_hem"], ["hips"] = ["hips", "hip", "groin", "buttocks"],
        ["legs"] = ["thigh", "inner_thigh", "knee", "calf"], ["feet"] = ["foot"], ["tail"] = ["tail"], ["wings"] = ["wings"], ["skirt"] = ["skirt_hem"]
    };

    /// <summary>A zone's feature without its side ("eye" for eye_left and eye_right).</summary>
    private static string Feature(string id) => id.EndsWith("_left", StringComparison.Ordinal) ? id[..^5]
        : id.EndsWith("_right", StringComparison.Ordinal) ? id[..^6] : id;

    /// <summary>Whether <paramref name="inner"/> is a smaller box mostly inside <paramref name="outer"/> (an eye's box in the
    /// hair's, which frames the face).</summary>
    private static bool Inside(TouchZoneBox inner, TouchZoneBox outer) => inner.Area < outer.Area && outer.Covers(inner) >= MostlyInside;

    /// <summary>A zone's box grown by half its size on each side: around it, where the pieces of its feature that a tight box
    /// leaves out lie.</summary>
    private static TouchZoneBox Around(TouchZoneBox box) => new(box.X - box.Width / 2, box.Y - box.Height / 2, box.Width * 2, box.Height * 2);

    /// <summary>Newly found zones merged with the saved ones: a zone found again keeps the owner's label (else the name it was
    /// found with), choice, reaction and
    /// whether the owner added it. A zone the owner added (<see cref="CharacterTouchZone.Added"/>) that wasn't found again stays
    /// where it was; any other zone not found again is dropped, unless <paramref name="keepUnfound"/> (a detection still
    /// running over the first guess: its zones not found yet stay too). <paramref name="whole"/>: the snapshot framed the
    /// character whole.</summary>
    public static CharacterTouchZoneSettings Detected(CharacterTouchZoneSettings? saved, string modelId, IReadOnlyList<CharacterTouchZone> found,
        TouchZoneBox? crop, RendererZoneProbe? probe, DateTimeOffset now, bool whole = false, bool keepUnfound = false)
    {
        var merged = found.Select(zone => saved?.Zones.FirstOrDefault(z => z.Id == zone.Id) is { } old
            ? zone with { Label = old.Label ?? zone.Label, Enabled = old.Enabled, Reaction = old.Reaction, Added = old.Added } : zone).ToList();
        // Its boxes were fractions of the earlier snapshot: they stay on the same place of the page in the new one.
        TouchZoneBox Rebased(TouchZoneBox box) => saved?.Crop is { } was && crop is { } next && was != next ? box.Within(was).Relative(next).Clamped() : box;
        merged.AddRange(saved?.Zones.Where(z => (z.Added || keepUnfound) && merged.All(m => m.Id != z.Id))
            .Select(z => z with { Box = Rebased(z.Box), Areas = z.Areas?.Select(a => a with { Box = Rebased(a.Box) }).ToArray() }) ?? []);
        // Bound together, so each zone's pieces are shared out among all of them (see Pieces), and a part a zone follows is its own.
        var bound = Bind(merged, crop, probe);
        return new()
        {
            ModelId = modelId, DetectedBy = CharacterTouchZoneSettings.ByVision, DetectedAt = now.ToUniversalTime(), UpdatedAt = now.ToUniversalTime(),
            IncludeIntimate = saved?.IncludeIntimate ?? true, Crop = crop, Whole = whole, Zones = [.. bound.OrderBy(z => Order(z.Id))]
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
}
