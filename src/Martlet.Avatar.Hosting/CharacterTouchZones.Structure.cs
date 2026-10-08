namespace Martlet.Avatar.Hosting;

public static partial class CharacterTouchZones
{
    /// <summary>A tail, wings and animal ears follow all of the model's own part for them, in areas along it, instead of their
    /// boxes: they swing on their own and often hide behind the body at rest, where no box around what shows can hold them.</summary>
    public static IReadOnlyList<string> Appendages { get; } = ["tail", "wings", "animal_ears"];

    // The body part (TouchZoneDetection's part words) each zone kind that follows the model's own parts is named by.
    private static readonly Dictionary<string, string> FollowWords = new(StringComparer.Ordinal)
    {
        ["tail"] = "tail", ["wings"] = "wings", ["animal_ears"] = "animal_ears", ["hair"] = "hair"
    };

    /// <summary>The longest stretch of a part one area of a zone that follows it covers, as a share of the page's height: a tail
    /// as long as the character's legs becomes about six areas.</summary>
    public const double AreaLength = 0.12;

    // The order zones are bound in: those that follow the model's own parts first, so they take their parts before other zones'
    // boxes do.
    private static int FollowOrder(string id) => Appendages.ToList().IndexOf(id) is >= 0 and var at ? at : id == "hair" ? Appendages.Count : int.MaxValue;

    /// <summary>The zone following the model's own part for it, or null when it follows nothing. A tail, wings or animal ears
    /// follow the model's own parts named for them (by its DisplayInfo file or its physics) that their boxes hold some of or lie
    /// near, and an unnamed swinging part their boxes hold some of (the smallest one holding each; a tail or wings never one rooted
    /// in the head, animal ears only one rooted above the eyes): all of it, in areas from its root to its tip, and nothing else.
    /// Hair keeps its boxes and also follows, in areas of their own, the swinging hair its boxes don't hold (a ponytail). A part
    /// another zone follows (<paramref name="model"/>'s taken parts) isn't followed again.</summary>
    private static CharacterTouchZone? Follow(CharacterTouchZone zone, IReadOnlyList<CharacterTouchZoneArea> boxes, TouchZoneBox? crop, ModelStructure model)
    {
        if (!FollowWords.TryGetValue(zone.Id, out var word) || model.Groups.Count == 0) return null;
        var appendage = word != "hair";
        TouchZoneBox Page(TouchZoneBox box) => crop is null ? box : box.Within(crop);
        // What the zone holds: what its boxes hold, and what it followed before.
        var boxed = boxes.SelectMany(a => a.Drawables).ToHashSet(StringComparer.Ordinal);
        var held = boxed.Concat(zone.AllAreas.Where(a => a.FromModel).SelectMany(a => a.Drawables)).ToHashSet(StringComparer.Ordinal);
        var places = (boxes.Count > 0 ? boxes.Select(a => a.Box) : zone.AllAreas.Select(a => a.Box)).Select(b => Grown(Page(b), 0.1)).ToArray();
        bool Holds(ModelGroup group) => group.Drawables.Any(d => held.Contains(d.Id));
        // Hair follows only swinging hair its boxes hold less than half of (a ponytail), so the hair they hold stays theirs.
        bool Outside(ModelGroup group) => group.Drawables.Count(d => boxed.Contains(d.Id)) * 2 < group.Drawables.Count;
        var taken = model.Groups.Where(g => !model.Taken.Contains(g.Key) && g.Part == word && (appendage || g.Swings && Outside(g)) &&
            (Holds(g) || places.Any(p => p.Overlaps(g.Rest)))).ToList();
        if (appendage)
        {
            // Unnamed swinging parts the boxes hold some of, the smallest first, each only for what the ones before don't cover.
            var covered = taken.SelectMany(g => g.Drawables).Select(d => d.Id).ToHashSet(StringComparer.Ordinal);
            var unnamed = model.Groups.Where(g => !model.Taken.Contains(g.Key) && g.Part is null && g.Swings && model.RootFits(g, word)).ToArray();
            foreach (var group in unnamed.Where(Holds).OrderBy(g => g.Drawables.Count))
            {
                if (group.Drawables.All(d => !held.Contains(d.Id) || covered.Contains(d.Id))) continue;
                taken.Add(group);
                covered.UnionWith(group.Drawables.Select(d => d.Id));
            }
            // A swinging part that holds one taken and only adds to its root end is the same part, whole (a tail's sway that
            // moves all of it, beside a hip sway that moves only its lower part).
            for (var i = 0; i < taken.Count; i++)
                if (taken[i].Swings && unnamed.Where(g => !taken.Contains(g) && Extends(g, taken[i])).MaxBy(g => g.Drawables.Count) is { } whole)
                    taken[i] = whole;
        }
        if (taken.Count == 0) return null;
        var areas = appendage ? new List<CharacterTouchZoneArea>() : [.. boxes];
        var seen = appendage ? new HashSet<string>(StringComparer.Ordinal) : new HashSet<string>(boxed, StringComparer.Ordinal);
        var followed = new List<ModelGroup>();
        foreach (var group in taken)
        {
            var members = group.Drawables.Where(d => seen.Add(d.Id)).ToList();
            var room = MaximumAreas - areas.Count;
            if (members.Count == 0 || room <= 0) continue;
            areas.AddRange(Segments(members, crop, room));
            followed.Add(group);
        }
        if (followed.Count == 0) return null;
        model.Taken.UnionWith(followed.Select(g => g.Key));
        var names = followed.Select(g => g.Name).Where(n => n.Length > 0).Distinct(StringComparer.Ordinal).Take(3).ToArray();
        return Compose(zone, areas, names.Length > 0 ? string.Join(" / ", names) : "the model's own " + word.Replace('_', ' '));
    }

    // Whether `whole` holds all of `part` and only adds drawables at its root end, fewer than half as many again.
    private static bool Extends(ModelGroup whole, ModelGroup part)
    {
        var ids = part.Drawables.Select(d => d.Id).ToHashSet(StringComparer.Ordinal);
        var extra = whole.Drawables.Count - ids.Count;
        return extra > 0 && extra * 2 <= ids.Count && whole.Drawables.Count(d => ids.Contains(d.Id)) == ids.Count &&
            whole.Drawables.Take(extra).All(d => !ids.Contains(d.Id));
    }

    // A part's drawables, root first, as areas from its root to its tip: about AreaLength of the page's height each, at most
    // `room`, each with a share of the drawables in order.
    private static IEnumerable<CharacterTouchZoneArea> Segments(IReadOnlyList<RendererDrawableBox> members, TouchZoneBox? crop, int room)
    {
        var rest = Union(members);
        var length = Math.Max(rest.Height, rest.Width * PageAspect);
        var count = Math.Clamp((int)Math.Round(length / AreaLength), 1, Math.Min(room, members.Count));
        for (var k = 0; k < count; k++)
        {
            var chunk = members.Skip(k * members.Count / count).Take((k + 1) * members.Count / count - k * members.Count / count).ToArray();
            var box = Union(chunk);
            yield return new()
            {
                Box = (crop is null ? box : box.Relative(crop)).Clamped(), Drawables = [.. chunk.Select(d => d.Id)], FromModel = true
            };
        }
    }

    private static TouchZoneBox Union(IEnumerable<RendererDrawableBox> drawables)
    {
        var all = drawables.ToArray();
        double left = all.Min(d => d.Left), top = all.Min(d => d.Top);
        return new(left, top, Math.Max(0.002, all.Max(d => d.Right) - left), Math.Max(0.002, all.Max(d => d.Bottom) - top));
    }

    private static TouchZoneBox Grown(TouchZoneBox box, double share) =>
        new(box.X - share * box.Width, box.Y - share * box.Height, box.Width * (1 + 2 * share), box.Height * (1 + 2 * share));

    private static bool Overlaps(this TouchZoneBox a, TouchZoneBox b) =>
        a.X < b.X + b.Width && b.X < a.X + a.Width && a.Y < b.Y + b.Height && b.Y < a.Y + a.Height;

    /// <summary>A group of the model's own drawables a zone can follow: the body part its names say (null when they say none),
    /// its name, its drawables root first (fractions of the page), where they are at rest, and whether its physics swings it.</summary>
    private sealed record ModelGroup(string Key, string? Part, string Name, IReadOnlyList<RendererDrawableBox> Drawables, TouchZoneBox Rest, bool Swings);

    /// <summary>What the probe tells of the model's own parts a zone can follow: its swinging parts (<see cref="RendererChain"/>)
    /// and the parts its DisplayInfo file names a tail, wings, animal ears or hair that nothing swings (one group per part, in
    /// order along it), with where the face is, and the groups zones already follow.</summary>
    private sealed class ModelStructure
    {
        internal List<ModelGroup> Groups { get; } = [];
        internal HashSet<string> Taken { get; } = new(StringComparer.Ordinal);
        // Where the eyes and the chin are on the page (fractions of its height), when the probe has the face.
        private double? eyes, chin;

        internal static ModelStructure Read(RendererZoneProbe probe, TouchZoneBox? crop)
        {
            var structure = new ModelStructure();
            var drawables = new Dictionary<string, RendererDrawableBox>(StringComparer.Ordinal);
            foreach (var d in probe.Drawables ?? [])
                if (d is { Id.Length: > 0 } && d.Right > d.Left && d.Bottom > d.Top && double.IsFinite(d.Left) && double.IsFinite(d.Top) &&
                    double.IsFinite(d.Right) && double.IsFinite(d.Bottom)) drawables.TryAdd(d.Id, d);
            if (drawables.Count == 0) return structure;
            var frame = crop ?? new(0, 0, 1, 1);
            var hints = TouchZoneDetection.Hints(probe, frame);
            if (hints?.Face is { } face)
            {
                structure.eyes = frame.Y + face.Y * frame.Height;
                // The chin is about 0.42 face widths below the eye line (the first guess's proportions), in the page's heights.
                structure.chin = structure.eyes + 0.42 * face.Width * frame.Width * PageAspect;
            }
            var named = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
            foreach (var piece in hints?.Pieces ?? [])
                if (piece.Id is { } id) named.TryAdd(id, piece.Parts);
            var chained = new HashSet<string>(StringComparer.Ordinal);
            var n = 0;
            foreach (var chain in (probe.Chains ?? []).Where(c => c is { IsValid: true }).Take(RendererChain.MaximumChains))
            {
                var members = chain.Drawables.Distinct(StringComparer.Ordinal).Select(id => drawables.GetValueOrDefault(id))
                    .OfType<RendererDrawableBox>().ToArray();
                n++;
                if (members.Length == 0) continue;
                var part = TouchZoneDetection.PartName(chain.Name).Part ?? MostNamed(members, named);
                structure.Groups.Add(new($"chain:{n}", part, chain.Name ?? "", members, Union(members), true));
                if (part is not null) chained.UnionWith(members.Select(m => m.Id));
            }
            // The parts named for what a zone can follow that nothing swings: one group per model part, along its longer side.
            var names = (probe.Parts ?? []).Where(p => p is { Id.Length: > 0 }).GroupBy(p => p.Id, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.First().Name, StringComparer.Ordinal);
            foreach (var word in FollowWords.Values.Distinct(StringComparer.Ordinal))
                foreach (var part in drawables.Values.Where(d => !chained.Contains(d.Id) && named.TryGetValue(d.Id, out var says) && says.Contains(word))
                    .GroupBy(d => d.Part ?? "", StringComparer.Ordinal))
                {
                    var rest = Union(part);
                    var members = part.OrderBy(d => rest.Height >= rest.Width * PageAspect ? (d.Top + d.Bottom) / 2 : (d.Left + d.Right) / 2).ToArray();
                    structure.Groups.Add(new($"part:{word}:{part.Key}", word, names.GetValueOrDefault(part.Key) ?? "", members, rest, false));
                }
            return structure;
        }

        // The body part most of a group's drawables are named for (each drawable's nearest named part), when at least half are.
        private static string? MostNamed(IReadOnlyList<RendererDrawableBox> members, IReadOnlyDictionary<string, IReadOnlyList<string>> named) =>
            members.Select(m => named.TryGetValue(m.Id, out var parts) && parts.Count > 0 ? parts[0] : null).OfType<string>()
                .GroupBy(p => p, StringComparer.Ordinal).OrderByDescending(g => g.Count()).FirstOrDefault() is { } most &&
            most.Count() * 2 >= members.Count ? most.Key : null;

        /// <summary>Whether an unnamed swinging part can be <paramref name="word"/>'s: a tail or wings are never rooted in the head
        /// (above the chin), animal ears only above the eyes. Without the face, any.</summary>
        internal bool RootFits(ModelGroup group, string word)
        {
            var root = group.Drawables[0];
            var y = (root.Top + root.Bottom) / 2;
            return word switch
            {
                "tail" or "wings" => chin is not { } below || y > below,
                "animal_ears" => eyes is not { } line || y < line,
                _ => true
            };
        }
    }
}
