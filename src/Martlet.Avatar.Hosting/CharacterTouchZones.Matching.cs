using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Martlet.Core.Contracts;

namespace Martlet.Avatar.Hosting;

/// <summary>Matching a touch to a zone: the zone's areas, the parts that follow the model, and where the touch landed.</summary>
public static partial class CharacterTouchZones
{
    // ---------- matching a touch ----------

    // The rough parts of the body (CharacterTouch.BoneZone of a VRM bone) each group's zones lie on.
    private static readonly string[] HeadParts = ["head", "face", "hair"], TorsoParts = ["body"], ArmParts = ["arm", "hand"],
        LowerBodyParts = ["leg", "foot", "body"];

    /// <summary>Whether a zone lies on the part of the body a VRM <paramref name="bone"/> moves, on the same side: the head's
    /// zones on the head bone, the left hand's on the left hand or arm, the hips' on the hips or a leg, and so on. A zone Martlet
    /// doesn't know lies on every part.</summary>
    public static bool OnPart(string zoneId, string bone)
    {
        if (Parts(zoneId) is { } parts && CharacterTouch.BoneZone(bone) is { } part && !parts.Contains(part, StringComparer.Ordinal)) return false;
        // Left and right are the character's own, in zone IDs and VRM bone names alike.
        var side = zoneId.EndsWith("_left", StringComparison.Ordinal) ? "left" : zoneId.EndsWith("_right", StringComparison.Ordinal) ? "right" : null;
        return side is null || !(bone.StartsWith("left", StringComparison.Ordinal) || bone.StartsWith("right", StringComparison.Ordinal)) ||
            bone.StartsWith(side, StringComparison.Ordinal);
    }

    // The rough parts of the body (CharacterTouch.BoneZone of a VRM bone) a zone lies on; null for every part (a zone Martlet
    // doesn't know).
    private static string[]? Parts(string zoneId) => zoneId switch
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

    /// <summary>Every zone a touch landed in, the matched one (<paramref name="match"/>) first. Zones can overlap, and a touch
    /// where they do is on each of them: every other zone in use with a box that holds the point <see cref="Match"/> compares
    /// (<see cref="TouchPoint"/>, for a tail that swung, where it is now; any of its areas, <see cref="CharacterTouchZone.Holds"/>),
    /// smallest first, that lies on the same
    /// part of the body as the touch (a VRM touch's bone, <see cref="OnPart"/>, else the matched zone's part): a hand held in
    /// front of the hips or raised to the face touches the hand, not what is behind it. A zone whose box frames a smaller touched
    /// zone's (holds most of it, <see cref="Inside"/>: the hair's box around an eye, a chest's around a breast) is left out, as
    /// the finer zone says where the touch landed. A touch matched only by its rough zone is on that zone alone; none without a
    /// match.</summary>
    public static IReadOnlyList<CharacterTouchZone> Touched(CharacterTouchZoneSettings? settings, CharacterTouch touch, TouchZoneMatch? match)
    {
        if (match is null) return [];
        if (settings is null || match.How == "coarse") return [match.Zone];
        TouchZoneBox Page(CharacterTouchZone zone) => settings.Crop is { } crop ? zone.Box.Within(crop) : zone.Box;
        var (x, y, _) = TouchPoint(settings, touch, match);
        var matched = Parts(match.Zone.Id);
        bool SamePart(string zoneId) => touch.Bone is { } bone ? OnPart(zoneId, bone)
            : matched is null || Parts(zoneId) is not { } parts || parts.Intersect(matched, StringComparer.Ordinal).Any();
        var under = settings.Zones.Where(z => settings.Active(z) && z.Id != match.Zone.Id && z.Holds(settings.Crop, x, y) && SamePart(z.Id))
            .OrderBy(z => z.Box.Area).ToArray();
        CharacterTouchZone[] all = [match.Zone, .. under];
        return [match.Zone, .. under.Where(z => !all.Any(other => other.Id != z.Id && Inside(Page(other), Page(z))))];
    }

    /// <summary>The zone a touch landed in: first a touched VRM spring-bone joint or Live2D drawable a zone follows by the model's
    /// own parts (a tail's, wherever it swung: <see cref="CharacterTouchZone.Follows"/>; not traced to the rest pose, where the
    /// part hung somewhere else); else the topmost touched drawable that belongs to a zone in use, or,
    /// unless the touch is on hair (<see cref="CharacterTouch.Hair"/>), a drawable under it that a smaller zone inside one of its
    /// zones has (an overlay such as a blush drawn over an eye's lower half is the eye; the bangs over an eye are hair). Of that
    /// drawable's zones, the smallest whose box holds the point, else the nearest; a zone whose box lies mostly inside another of
    /// them also wins when the point lies by its box (<see cref="Around"/>: an eye's lash just above a tight eye box); a zone in
    /// use whose box holds the point and lies mostly inside the drawable's smallest zone counts too when none of its own
    /// drawables are under the touch, as a finer part of it (a cheek, or the skin by the nose, on the face's skin that the hair's
    /// box takes in; on hair, only a zone with no drawables of its own, such as a forehead under the bangs). Then hair, then for a
    /// touched VRM bone the smallest zone in use on that
    /// part of the body (<see cref="OnPart"/>) whose box holds the point (a bone moves a whole part, such as the head, and its
    /// zones are found in a picture in the same pose), else the zone on that part that holds the bone, nearest the point (the
    /// part has moved since); without a bone, the smallest box in use that holds the point; then the touch's rough zone (a found
    /// zone of that kind, else that kind's default zone). A zone's box holds the point when one of its areas' boxes does
    /// (<see cref="CharacterTouchZone.Holds"/>; an area that follows the model's own parts only by its parts). The point is where
    /// the touched point of the character was in the rest pose the zones were found in (<see cref="TouchPoint"/>), so a touch
    /// lands on the same zone however the model moves. For a zone with several areas the match says which. Null when nothing
    /// fits (a touch only on zones that aren't in use).</summary>
    public static TouchZoneMatch? Match(CharacterTouchZoneSettings? settings, CharacterTouch touch)
    {
        var active = settings?.Zones.Where(settings.Active).ToArray() ?? [];
        var crop = settings?.Crop;
        TouchZoneBox Page(CharacterTouchZone zone) => crop is { } c ? zone.Box.Within(c) : zone.Box;
        var (x, y, traced) = TouchPoint(settings, touch);
        bool Holds(CharacterTouchZone zone) => zone.Holds(crop, x, y);
        TouchZoneMatch Found(CharacterTouchZone zone, string how, string? piece = null) => new(zone, how, traced, AreaOf(zone, crop, x, y, piece));
        CharacterTouchZone Best(IEnumerable<CharacterTouchZone> zones)
        {
            var list = zones.ToArray();
            return list.Where(Holds).OrderBy(z => z.Box.Area).FirstOrDefault() ??
                list.OrderBy(z => Page(z).Distance(x, y)).ThenBy(z => z.Box.Area).First();
        }
        TouchZoneMatch Followed(CharacterTouchZone zone, string how, string piece) => new(zone, how, false, AreaOf(zone, crop, x, y, piece));
        // A VRM spring-bone joint a zone follows by the model's own parts (a tail's or animal ears', wherever they swung) is that
        // zone's alone, before hair: animal ears hang from the head as hair does. Its rest point is where it hung in the rest
        // pose, so the match is not traced.
        if (touch.Node is { } joint && active.FirstOrDefault(z => z.Areas?.Any(a => a.FromModel && a.Nodes.Contains(joint, StringComparer.Ordinal)) == true) is { } swings)
            return Followed(swings, "node", joint);
        foreach (var drawable in touch.Drawables)
        {
            // A drawable a zone follows by the model's own parts (a tail's, wherever it swung) is that zone's alone. Its rest
            // point is where it hung in the rest pose (often behind the body), so the match is not traced.
            if (active.FirstOrDefault(z => z.Areas?.Any(a => a.FromModel && a.Drawables.Contains(drawable, StringComparer.Ordinal)) == true) is { } follows)
                return Followed(follows, "drawable", drawable);
            var owners = active.Where(z => z.Drawables.Contains(drawable, StringComparer.Ordinal)).ToArray();
            if (owners.Length == 0) continue;
            var piece = drawable;
            // Through an overlay that only bigger zones have (the top of a blush drawn over an eye's lower half, the face's
            // shading), the piece under it of a smaller zone inside one of them is what was touched. Hair drawn over a
            // feature stays hair (the bangs over an eye).
            if (!touch.Hair)
                foreach (var under in touch.Drawables.SkipWhile(d => d != drawable).Skip(1))
                {
                    var holders = active.Where(z => z.Drawables.Contains(under, StringComparer.Ordinal)).ToArray();
                    if (!holders.Any(z => !owners.Contains(z) && owners.Any(owner => Inside(Page(z), Page(owner))))) continue;
                    (piece, owners) = (under, holders);
                    break;
                }
            // A finer zone inside the piece's most specific owner: one whose box holds the point while none of its own pieces
            // is under the touch (a cheek on the face's skin, which the hair's box takes in) and, when the touch is on hair, one
            // with no pieces of its own (a forehead under the bangs; a lock in front of an ear stays hair), or an owner whose
            // box lies by the point (an eye's lash just above its tight box).
            var specific = owners.MinBy(z => z.Box.Area)!;
            var finer = active.Where(z => z.Bones.Count == 0 && !z.Drawables.Any(own => touch.Drawables.Contains(own, StringComparer.Ordinal)) &&
                (!touch.Hair || z.Drawables.Count == 0) && Holds(z) && Inside(Page(z), Page(specific))).ToArray();
            var by = owners.Where(z => Around(Page(z)).Contains(x, y) && owners.Any(owner => Inside(Page(z), Page(owner))));
            var best = owners.Concat(finer).Where(Holds).Concat(by).OrderBy(z => z.Box.Area).FirstOrDefault() ?? Best(owners);
            return Found(best, best.Drawables.Contains(piece, StringComparer.Ordinal) ? "drawable" : "box", piece);
        }
        if (touch.Hair && active.FirstOrDefault(z => z.Id == "hair") is { } hair) return Found(hair, "hair");
        if (touch.Bone is { } bone)
        {
            var part = active.Where(z => OnPart(z.Id, bone)).ToArray();
            if (part.Where(Holds).OrderBy(z => z.Box.Area).FirstOrDefault() is { } under)
                return Found(under, under.Bones.Contains(bone, StringComparer.Ordinal) ? "bone" : "box");
            var owners = part.Where(z => z.Bones.Contains(bone, StringComparer.Ordinal)).ToArray();
            if (owners.Length > 0) return Found(Best(owners), "bone");
        }
        else if (active.Where(Holds).OrderBy(z => z.Box.Area).FirstOrDefault() is { } boxed) return Found(boxed, "box");
        if (!Coarse.TryGetValue(touch.CoarseZone, out var candidates)) return null;
        foreach (var id in candidates)
            if (active.FirstOrDefault(z => z.Id == id) is { } found) return new(found, "coarse");
        // Not found on this model (or not detected yet): the rough zone's default, unless the owner turned that zone off.
        var fallback = candidates[0];
        if (settings?.Zones.FirstOrDefault(z => z.Id == fallback) is { } off && !settings.Active(off)) return null;
        return new(new() { Id = fallback, Box = new(0, 0, 1, 1) }, "coarse");
    }

    // Which of a zone's areas a touch landed in, for a zone with several: the one that holds the touched drawable or joint
    // (`piece`), else the smallest whose box holds the point (not one that follows the model's own parts); null for a zone with one.
    private static int? AreaOf(CharacterTouchZone zone, TouchZoneBox? crop, double x, double y, string? piece)
    {
        var areas = zone.AllAreas;
        if (areas.Count <= 1) return null;
        if (piece is not null)
            for (var i = 0; i < areas.Count; i++)
                if (areas[i].Drawables.Contains(piece, StringComparer.Ordinal) || areas[i].Nodes.Contains(piece, StringComparer.Ordinal)) return i;
        return Enumerable.Range(0, areas.Count).Where(i => !areas[i].FromModel && (crop is null ? areas[i].Box : areas[i].Box.Within(crop)).Contains(x, y))
            .OrderBy(i => areas[i].Box.Area).Cast<int?>().FirstOrDefault();
    }

    /// <summary>The point of the page a touch's zone boxes compare with, and whether it is traced to the rest pose: where the
    /// touched point of the character was in its rest pose (the pose the zones' picture shows, traced on the touched mesh, so
    /// it stays on the same spot of the skin however the head turns to the mouse, nods or tilts, or an idle motion moves the
    /// body), else where the touch landed. For a <paramref name="match"/> that is not traced (a touch on a part that swings on
    /// its own, such as a tail, which hung somewhere else in the rest pose; not a rough one), where the touch landed. Zones found
    /// with the character framed whole compare with that framing (<see cref="CharacterTouch.RestWholeX"/>, else
    /// <see cref="CharacterTouch.WholeX"/>).</summary>
    public static (double X, double Y, bool Traced) TouchPoint(CharacterTouchZoneSettings? settings, CharacterTouch touch, TouchZoneMatch? match = null) =>
        match is { Traced: false, How: not "coarse" }
            ? settings is { Whole: true } && touch is { WholeX: { } landedX, WholeY: { } landedY } ? (landedX, landedY, false) : (touch.X, touch.Y, false)
        : settings is { Whole: true }
            ? touch is { RestWholeX: { } restX, RestWholeY: { } restY } ? (restX, restY, true)
                : touch is { WholeX: { } wholeX, WholeY: { } wholeY } ? (wholeX, wholeY, false) : (touch.X, touch.Y, false)
            : touch is { RestX: { } pageX, RestY: { } pageY } ? (pageX, pageY, true) : (touch.X, touch.Y, false);
}
