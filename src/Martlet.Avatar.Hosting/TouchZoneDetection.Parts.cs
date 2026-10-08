namespace Martlet.Avatar.Hosting;

public static partial class TouchZoneDetection
{
    /// <summary>How far a close-up from the model's own named parts reaches into the part next to it (a share of the snapshot's
    /// height).</summary>
    public const double RegionOverlap = 0.03;

    // At most this many of a model's parts are read, and a part's parents are followed at most this deep.
    private const int MaximumParts = 1024, MaximumDepth = 32;

    // Body parts that come in pairs, so their drawables get a side.
    private static readonly HashSet<string> Paired = new(StringComparer.Ordinal) { "eyes", "cheeks", "ears", "animal_ears", "arms", "hands", "legs", "feet" };

    // Body parts that can be as long as the character (hair, a tail, wings).
    private static readonly HashSet<string> Long = new(StringComparer.Ordinal) { "hair", "tail", "wings" };

    private static readonly Dictionary<string, (string Plural, string One)> PartLabels = new(StringComparer.Ordinal)
    {
        ["eyes"] = ("eyes", "eye"), ["cheeks"] = ("cheeks", "cheek"), ["ears"] = ("ears", "ear"), ["animal_ears"] = ("animal ears", "animal ear"),
        ["arms"] = ("arms", "arm"), ["hands"] = ("hands", "hand"), ["legs"] = ("legs", "leg"), ["feet"] = ("feet", "foot"),
        ["torso"] = ("upper body", "upper body"), ["lower_body"] = ("lower body", "lower body")
    };

    /// <summary>The body part a part's name says, in any language (<see cref="PartWords"/>: the longest word wins, so 马尾, a
    /// ponytail, is hair and 尾巴 a tail), and the side the name says: -1 for 左, L or left, 1 for 右, R or right, else 0. A side
    /// in a name only groups a limb's drawables, because rigs disagree on whose left it is.</summary>
    public static (string? Part, int Side) PartName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return (null, 0);
        var tokens = Tokens(name);
        string? part = null;
        var longest = 0;
        foreach (var (candidate, words) in PartWords)
            foreach (var word in words)
                if (word.Length > longest && Says(name, tokens, word)) (part, longest) = (candidate, word.Length);
        var left = name.Contains('左') || name.Contains("왼", StringComparison.Ordinal) || tokens.Any(t => t is "l" or "left");
        var right = name.Contains('右') || name.Contains("오른", StringComparison.Ordinal) || tokens.Any(t => t is "r" or "right");
        return (part, (right ? 1 : 0) - (left ? 1 : 0));
    }

    // Whether a name says a word: a Latin word (or words in a row, "lower body") starts its words, any other is found in it.
    private static bool Says(string name, List<string> tokens, string word)
    {
        if (!word.All(char.IsAscii)) return name.Contains(word, StringComparison.Ordinal);
        var phrase = word.Split(' ');
        for (var i = 0; i + phrase.Length <= tokens.Count; i++)
        {
            var all = true;
            for (var k = 0; k < phrase.Length && all; k++) all = tokens[i + k].StartsWith(phrase[k], StringComparison.Ordinal);
            if (all) return true;
        }
        return false;
    }

    // The drawables in the parts the model's own part names call body parts, what the hints tell of each body part (one area
    // per side for a part that comes in pairs), the body's middle, and how many parts the model has and names.
    private static (List<ZoneHintPiece> Pieces, List<ZoneHintArea> Areas, double? Middle, int Count, int Named) NamedParts(RendererZoneProbe probe,
        IReadOnlyList<RendererDrawableBox> drawables, TouchZoneBox crop, bool faces)
    {
        var parts = new Dictionary<string, RendererModelPart>(StringComparer.Ordinal);
        foreach (var part in (probe.Parts ?? []).Take(MaximumParts))
            if (!string.IsNullOrEmpty(part.Id)) parts.TryAdd(part.Id, part);
        var named = parts.Values.Count(p => !string.IsNullOrWhiteSpace(p.Name));
        var placed = drawables.Where(d => d.Part is { } part && parts.ContainsKey(part)).ToArray();
        if (placed.Length == 0) return ([], [], null, parts.Count, named);
        TouchZoneBox Box(RendererDrawableBox d) => new TouchZoneBox(d.Left, d.Top, d.Right - d.Left, d.Bottom - d.Top).Relative(crop);
        var figure = Union(drawables.Select(Box))!;
        // A drawable's parts, nearest first.
        var chains = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        List<string> Chain(string id)
        {
            if (chains.TryGetValue(id, out var known)) return known;
            var chain = new List<string>();
            for (var at = id; chain.Count < MaximumDepth && !chain.Contains(at) && parts.TryGetValue(at, out var part);)
            {
                chain.Add(at);
                if (part.Parent is not { } parent) break;
                at = parent;
            }
            return chains[id] = chain;
        }
        var reach = new Dictionary<string, TouchZoneBox>(StringComparer.Ordinal);
        foreach (var drawable in placed)
        {
            var box = Box(drawable);
            foreach (var id in Chain(drawable.Part!)) reach[id] = reach.TryGetValue(id, out var had) ? Merge(had, box) : box;
        }
        // What each part names, by its name, else its ID. A group as tall as the whole character names nothing, feet or hands as
        // long as a leg or an arm are that leg or arm (足 and 脚 are a whole leg in many Japanese rigs), and a body as tall as three
        // heads is the whole body, not the upper body.
        var meaning = new Dictionary<string, (string? Part, int Side)>(StringComparer.Ordinal);
        foreach (var (id, part) in parts)
        {
            var (what, side) = PartName(part.Name);
            if (what is null || side == 0)
            {
                var (byId, idSide) = PartName(id);
                what ??= byId;
                if (side == 0) side = idSide;
            }
            if (what is not null && reach.TryGetValue(id, out var extent))
            {
                if (!Long.Contains(what) && extent.Height >= 0.85 * figure.Height) what = null;
                else if (what == "feet" && extent.Height > 0.3 * figure.Height) what = "legs";
                else if (what == "hands" && extent.Height > 0.25 * figure.Height) what = "arms";
            }
            meaning[id] = (what, side);
        }
        // A body part taller than three heads holds the legs too (体 in many rigs): it isn't the upper body.
        var heads = Union(meaning.Where(m => m.Value.Part is "head" or "face" && reach.ContainsKey(m.Key)).Select(m => reach[m.Key]));
        foreach (var (id, (what, side)) in meaning.ToArray())
            if (what == "torso" && reach.TryGetValue(id, out var extent) && extent.Height > (heads is null ? 0.6 * figure.Height : 3 * heads.Height))
                meaning[id] = (null, side);
        var found = new List<(TouchZoneBox Box, List<string> Parts, int Side, string Id)>();
        foreach (var drawable in placed)
        {
            var keys = new List<string>();
            var side = 0;
            foreach (var id in Chain(drawable.Part!))
            {
                var (what, says) = meaning[id];
                if (what is not null && !keys.Contains(what)) keys.Add(what);
                if (side == 0) side = says;
            }
            if (keys.Count > 0) found.Add((Box(drawable), keys, side, drawable.Id));
        }
        if (found.Count == 0) return ([], [], null, parts.Count, named);

        TouchZoneBox? Of(params string[] keys) => Union(found.Where(p => p.Parts.Any(keys.Contains)).Select(p => p.Box));
        var middle = (Of("head", "face", "eyes", "nose", "mouth") ?? Of("neck") ?? Union(found.Where(p => p.Parts[0] == "torso").Select(p => p.Box)) ?? figure).CenterX;
        // Each drawable of a part that comes in pairs is on the side of the picture it lies on. Two groups named for sides (左臂 and
        // 右臂) each go to the side they lie on, so a limb across the middle stays whole; a drawable across the middle has no side.
        var sides = new string?[found.Count];
        foreach (var key in Paired)
        {
            var members = Enumerable.Range(0, found.Count).Where(i => found[i].Parts.FirstOrDefault(Paired.Contains) == key).ToArray();
            if (members.Length == 0) continue;
            var leftNamed = Union(members.Where(i => found[i].Side < 0).Select(i => found[i].Box));
            var rightNamed = Union(members.Where(i => found[i].Side > 0).Select(i => found[i].Box));
            var apart = leftNamed is not null && rightNamed is not null ? rightNamed.CenterX - leftNamed.CenterX : 0;
            foreach (var i in members)
            {
                var box = found[i].Box;
                // -1 on the picture's left, 1 on its right, 0 neither.
                var picture = Math.Abs(apart) >= 0.02 ? Math.Sign(apart) * found[i].Side
                    : middle - box.X >= 0.25 * box.Width && Right(box) - middle >= 0.25 * box.Width || Math.Abs(box.CenterX - middle) < 0.01 ? 0
                    : Math.Sign(box.CenterX - middle);
                sides[i] = picture == 0 ? null : (picture > 0) == faces ? "left" : "right";
            }
        }
        var pieces = found.Select((p, i) => new ZoneHintPiece(p.Box, p.Parts, sides[i], p.Id)).ToList();
        var areas = new List<ZoneHintArea>();
        foreach (var (key, _) in PartWords)
        {
            var with = pieces.Where(p => p.Parts.Contains(key)).ToArray();
            if (with.Length == 0) continue;
            var sided = with.Where(p => p.Side is not null).ToArray();
            if (Paired.Contains(key) && sided.Length > 0)
            {
                foreach (var side in new[] { "right", "left" })
                    if (Union(sided.Where(p => p.Side == side).Select(p => p.Box)) is { } box && Clip(box) is { } seen)
                        areas.Add(new(key, seen, sided.Count(p => p.Side == side), side));
            }
            else if (Union(with.Select(p => p.Box)) is { } box && Clip(box) is { } seen) areas.Add(new(key, seen, with.Length));
        }
        return (pieces, areas, middle, parts.Count, named);
    }

    // What the hints call a body part: "hair", "upper body", or with a side "the character's left arm".
    private static string AreaName(ZoneHintArea area)
    {
        var (plural, one) = PartLabels.TryGetValue(area.Part, out var label) ? label : (area.Part, area.Part);
        return area.Side is null ? plural : $"the character's {area.Side} {one}";
    }

    /// <summary>Where the model's own named parts put the zone <paramref name="id"/> (fractions of the snapshot), and what that is
    /// ("neck", "mouth", "left leg (its top)"); null when the model names nothing for it (or has no <see cref="ZoneHints.Pieces"/>).
    /// A zone of its own part is on that part (the lips on the mouth, an eye on that eye, the tail on the tail); others are a
    /// share of a larger part: the forehead high on the face, the chest high on the upper body, the waist low on it, the hips
    /// from the bottom of the upper body to where the legs meet (and a hip on its side of them), a thigh at the top of its leg,
    /// a knee in its middle and a foot at its bottom. Left and right are the character's own.</summary>
    public static (TouchZoneBox Box, string What)? NamedPlace(string id, ZoneHints? hints)
    {
        if (hints is not { Named: true }) return null;
        var faces = hints.Bones.Count > 0 ? hints.FacesViewer ?? true : true;
        var middle = hints.Middle ?? 0.5;
        var side = id.EndsWith("_left", StringComparison.Ordinal) ? "left" : id.EndsWith("_right", StringComparison.Ordinal) ? "right" : null;
        var kind = side is null ? id : id[..id.LastIndexOf('_')];
        var limb = side is null ? "" : side + " ";
        TouchZoneBox? Sided(TouchZoneBox? box) => box is null || side is null ? box : Half(box, side == "left", middle, faces);
        (TouchZoneBox Box, string What)? At(TouchZoneBox? box, string what) => box is { Width: > 0, Height: > 0 } place ? (place, what) : null;
        // The side of a leg toward the legs' middle.
        TouchZoneBox Inner(TouchZoneBox band) => band.CenterX > middle ? band with { Width = 0.5 * band.Width } : band with { X = band.CenterX, Width = 0.5 * band.Width };
        var face = Whole(hints, "face");
        var head = HeadPlace(hints);
        var torso = Own(hints, "torso");
        var hips = HipsPlace(hints);
        var chest = Whole(hints, "chest") ?? (torso is null ? null : Band(torso, 0, 0.5));
        var leg = side is null ? null : Leg(hints, side, middle, faces);
        return kind switch
        {
            "top_of_head" => At(head is null ? null : Band(head, 0, 0.3), "head (its top)"),
            "forehead" => face is not null ? At(Band(face, 0.05, 0.45), "face (its top)") : At(head is null ? null : Band(head, 0.2, 0.5), "head (its forehead)"),
            "face" => face is not null ? At(face, "face") : At(head is null ? null : Band(head, 0.25, 1), "head (its face)"),
            "eye" => At(Whole(hints, "eyes", side), $"{limb}eye") ?? At(Whole(hints, "eyes") is { } eyes ? Sided(eyes) : null, $"eyes (its {limb}side)") ??
                At(face is null ? null : Sided(Band(face, 0.3, 0.7)), $"face (its {limb}eye)"),
            "cheek" => At(Whole(hints, "cheeks", side), $"{limb}cheek") ?? At(face is null ? null : Sided(Band(face, 0.45, 0.85)), $"face (its {limb}cheek)"),
            "nose" => At(Whole(hints, "nose"), "nose") ?? At(face is null ? null : Central(Band(face, 0.45, 0.8), 0.34), "face (its nose)"),
            "lips" => At(Whole(hints, "mouth"), "mouth") ?? At(face is null ? null : Central(Band(face, 0.65, 0.92), 0.5), "face (its mouth)"),
            "chin" => At(face is not null ? Central(Band(face, 0.8, 1), 0.5) : head is null ? null : Central(Band(head, 0.85, 1), 0.5), "face (its chin)"),
            // Ears high on the head are animal ears.
            "ear" => head is not null && Whole(hints, "ears") is { } ears && ears.CenterY > head.Y + 0.35 * head.Height ? At(Whole(hints, "ears", side), $"{limb}ear") : null,
            "animal_ears" => At(Whole(hints, "animal_ears"), "animal ears"),
            "neck" => At(Whole(hints, "neck") is { } neck ? BelowFace(neck, face) : null, "neck"),
            "chest" => At(chest, Whole(hints, "chest") is null ? "upper body (its chest)" : "chest"),
            "breast" => At(chest is null ? null : Sided(Band(chest, 0.25, 1)), $"chest (its {limb}side)"),
            "stomach" => At(torso is null ? null : Central(Band(torso, 0.5, 1), 0.7), "upper body (its stomach)"),
            "navel" => At(torso is null ? null : Central(Band(torso, 0.65, 1), 0.4), "upper body (its navel)"),
            "waist" => At(Whole(hints, "waist"), "waist") ?? At(torso is null ? null : Band(torso, 0.65, 1.1), "upper body (its waist)"),
            "upper_arm" or "forearm" => At(Whole(hints, "arms", side), $"{limb}arm"),
            "hand" => At(Whole(hints, "hands", side), $"{limb}hand") ?? At(Whole(hints, "arms", side), $"{limb}arm"),
            "hips" => At(hips, "hips"),
            "hip" => At(hips is null ? null : Sided(hips), $"hips (its {limb}side)"),
            "groin" => At(hips is null ? null : Central(Band(hips, 0.5, 1), 0.4), "hips (where the legs meet)"),
            "buttocks" => At(hips is null ? null : Band(hips, 0.3, 1), "hips"),
            "thigh" => At(leg is null ? null : Band(leg, 0, 0.42), $"{limb}leg (its top)"),
            "inner_thigh" => At(leg is null ? null : Inner(Band(leg, 0.05, 0.42)), $"{limb}leg (its inner top)"),
            "knee" => At(leg is null ? null : Band(leg, 0.42, 0.66), $"{limb}leg (its middle)"),
            "calf" => At(leg is null ? null : Band(leg, 0.58, 0.88), $"{limb}leg (its lower part)"),
            "foot" => At(Whole(hints, "feet", side), $"{limb}foot") ?? At(leg is null ? null : Band(leg, 0.84, 1), $"{limb}leg (its foot)"),
            "tail" => At(Whole(hints, "tail"), "tail"),
            "wings" => At(Whole(hints, "wings"), "wings"),
            "skirt_hem" => At(Whole(hints, "skirt") is { } skirt ? Band(skirt, 0.75, 1) : null, "skirt (its hem)"),
            _ => null
        };
    }

    /// <summary>A close-up's window from the model's own named parts (fractions of the snapshot), reaching a little
    /// (<see cref="RegionOverlap"/>) into the part next to it; null when the model names nothing for it. The head holds its face,
    /// ears and the hair around it; the upper body goes from the neck down, with the arms and hands; the lower body goes from the
    /// bottom of the upper body (so it always holds the hips and groin) down to the feet, with any skirt.</summary>
    public static TouchZoneBox? NamedRegion(string region, ZoneHints? hints)
    {
        if (hints is not { Named: true }) return null;
        TouchZoneBox? Of(params string[] keys) => Union(keys.Select(key => Whole(hints, key)).OfType<TouchZoneBox>());
        switch (region)
        {
            case "head":
                return HeadPlace(hints) is { } head ? FromEdges(head.X, head.Y, Right(head), Bottom(head) + RegionOverlap) : null;
            case "upper_body":
                if (Of("neck", "torso", "chest", "waist") is null || Of("neck", "torso", "chest", "waist", "arms", "hands") is not { } upper) return null;
                return FromEdges(upper.X, upper.Y - RegionOverlap, Right(upper), Bottom(upper) + RegionOverlap);
            case "lower_body":
                if (Union(new[] { Of("lower_body", "hips", "legs", "feet", "skirt"), HipsPlace(hints) }.OfType<TouchZoneBox>()) is not { } lower) return null;
                var top = Own(hints, "torso") is { } torso ? Math.Min(lower.Y, Bottom(torso)) : lower.Y;
                return FromEdges(lower.X, top - RegionOverlap, Right(lower), Bottom(lower));
            default:
                return null;
        }
    }

    // The head by the model's own named parts: its head, face, eyes, nose, mouth and cheeks, with its ears and the hair around
    // them (not a ponytail that hangs below the face).
    private static TouchZoneBox? HeadPlace(ZoneHints hints)
    {
        if (Union(new[] { "head", "face", "eyes", "nose", "mouth", "cheeks" }.Select(key => Whole(hints, key)).OfType<TouchZoneBox>()) is not { } core)
            return null;
        return Union(hints.Pieces.Where(p => (p.Parts.Contains("ears") || p.Parts.Contains("animal_ears") || p.Parts.Contains("hair")) &&
            p.Box.CenterY < Bottom(core) && Bottom(p.Box) > core.Y - core.Height).Select(p => p.Box).Append(core));
    }

    // The hips by the model's own named parts: its hips when it names them, else from the bottom of its upper body down to where
    // its legs meet, as wide as both legs.
    private static TouchZoneBox? HipsPlace(ZoneHints hints)
    {
        if (Whole(hints, "hips") is { } hips) return hips;
        var legs = Whole(hints, "legs", "left") is { } left && Whole(hints, "legs", "right") is { } right ? Merge(left, right) : Whole(hints, "legs");
        if (legs is null) return Whole(hints, "lower_body") is { } lower ? Band(lower, 0, 0.3) : null;
        var top = Own(hints, "torso") is { } torso ? Bottom(torso) - 0.05 * torso.Height : legs.Y - 0.15 * legs.Height;
        top = Math.Clamp(top, legs.Y - 0.4 * legs.Height, legs.Y + 0.05 * legs.Height);
        return FromEdges(legs.X, top, Right(legs), legs.Y + 0.12 * legs.Height);
    }

    // One leg by the model's own named parts: the leg on that side, else that side's half of the legs.
    private static TouchZoneBox? Leg(ZoneHints hints, string side, double middle, bool faces) =>
        Whole(hints, "legs", side) ?? (Whole(hints, "legs") is { } legs ? Half(legs, side == "left", middle, faces) : null);

    // A neck's drawables often go up behind the face: the neck a touch can reach starts just above the bottom of the face.
    private static TouchZoneBox BelowFace(TouchZoneBox neck, TouchZoneBox? face)
    {
        if (face is null) return neck;
        var top = Math.Max(neck.Y, Bottom(face) - 0.1 * face.Height);
        return Bottom(neck) - top >= 0.3 * neck.Height ? FromEdges(neck.X, top, Right(neck), Bottom(neck)) : neck;
    }

    // The drawables of a body part (on one side), and those whose nearest named part it is.
    private static TouchZoneBox? Whole(ZoneHints hints, string part, string? side = null) =>
        Union(hints.Pieces.Where(p => p.Parts.Contains(part) && (side is null || p.Side == side)).Select(p => p.Box));

    private static TouchZoneBox? Own(ZoneHints hints, string part) => Union(hints.Pieces.Where(p => p.Parts[0] == part).Select(p => p.Box));

    // A box clearly misses where the model's own parts put its zone: its middle is off that place, and less than half of it lies
    // there (with a quarter of the place's size around it).
    private static bool Misses(TouchZoneBox box, TouchZoneBox place) =>
        !place.Contains(box.CenterX, box.CenterY) && Grown(place, 0.25).Covers(box) < 0.5;

    // A box reaches well past where the model's own parts put its zone: it holds most of that place, but most of it lies elsewhere.
    private static bool Overreaches(TouchZoneBox box, TouchZoneBox place) => box.Covers(place) >= 0.7 && Grown(place, 0.25).Covers(box) < 0.4;

    private static TouchZoneBox? Union(IEnumerable<TouchZoneBox> boxes)
    {
        TouchZoneBox? union = null;
        foreach (var box in boxes) union = union is null ? box : Merge(union, box);
        return union;
    }

    private static TouchZoneBox Merge(TouchZoneBox a, TouchZoneBox b) =>
        FromEdges(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Max(Right(a), Right(b)), Math.Max(Bottom(a), Bottom(b)));

    // Where two boxes overlap, or null.
    private static TouchZoneBox? Overlap(TouchZoneBox a, TouchZoneBox b)
    {
        double left = Math.Max(a.X, b.X), top = Math.Max(a.Y, b.Y), right = Math.Min(Right(a), Right(b)), bottom = Math.Min(Bottom(a), Bottom(b));
        return right - left > 0.002 && bottom - top > 0.002 ? new(left, top, right - left, bottom - top) : null;
    }

    private static TouchZoneBox FromEdges(double left, double top, double right, double bottom) =>
        new(left, top, Math.Max(0.002, right - left), Math.Max(0.002, bottom - top));

    private static double Right(TouchZoneBox box) => box.X + box.Width;
    private static double Bottom(TouchZoneBox box) => box.Y + box.Height;

    // The part of a box from top to bottom (shares of its height, from its top).
    private static TouchZoneBox Band(TouchZoneBox box, double top, double bottom) => new(box.X, box.Y + top * box.Height, box.Width, (bottom - top) * box.Height);

    // The middle share of a box's width.
    private static TouchZoneBox Central(TouchZoneBox box, double share) => new(box.CenterX - share * box.Width / 2, box.Y, share * box.Width, box.Height);

    // The character's own left (or right) half of a box, split at the body's middle: facing the viewer, its left is on the
    // picture's right.
    private static TouchZoneBox Half(TouchZoneBox box, bool characterLeft, double middle, bool faces)
    {
        var split = Math.Clamp(middle, box.X + 0.2 * box.Width, Right(box) - 0.2 * box.Width);
        return characterLeft == faces ? FromEdges(split, box.Y, Right(box), Bottom(box)) : FromEdges(box.X, box.Y, split, Bottom(box));
    }
}
