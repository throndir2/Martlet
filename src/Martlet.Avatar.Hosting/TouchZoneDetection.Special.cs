using System.Globalization;

namespace Martlet.Avatar.Hosting;

public static partial class TouchZoneDetection
{
    /// <summary>The most zones special to the character a detection adds unless told otherwise
    /// (<see cref="ZoneDetectionOptions.MaximumSpecial"/>).</summary>
    public const int DefaultSpecial = 6;

    /// <summary>The step that asks, on the whole character, what is special about it.</summary>
    public const string SpecialStep = "special";

    /// <summary>What can be special about a character, in plain words.</summary>
    public const string SpecialExamples = "animal ears, a tail, wings, a hat or a bow";

    // The extras Martlet knows that something special can be by any common name: an animal's ears, a tail, wings, horns and a skirt
    // keep Martlet's own zone and its reactions. Anything else is a zone of its own, named as the vision model sees it (a hat,
    // glasses, a sword).
    private static readonly HashSet<string> SpecialKinds = new(StringComparer.Ordinal) { "animal_ears", "tail", "wings", "horns", "skirt_hem" };

    // The extras a Live2D model's own part names can place: they become zones even when the vision model doesn't list them.
    private static readonly string[] NamedSpecial = ["animal_ears", "tail", "wings"];

    // Words for ordinary body parts: something special named only one of these is a body part, which the default zones and the
    // ones the owner adds cover.
    private static readonly HashSet<string> BodyWords = BodyWordsOf();

    // Words for intimate things: something special with one of these in its name isn't added. The intimate zones Martlet knows,
    // which follow Include intimate zones, cover those.
    private static readonly HashSet<string> IntimateWords = new(StringComparer.Ordinal)
    {
        "bra", "panties", "panty", "underwear", "lingerie", "nipple", "nipples", "genital", "genitals", "crotch", "groin", "pussy",
        "vagina", "penis", "boob", "boobs", "breast", "breasts", "butt", "buttock", "buttocks", "bum", "cleavage", "nude", "naked"
    };

    private static HashSet<string> BodyWordsOf()
    {
        var words = new HashSet<string>(StringComparer.Ordinal)
        {
            "head", "body", "torso", "belly", "back", "skin", "arm", "arms", "leg", "legs", "finger", "fingers", "toe", "toes",
            "eyebrow", "eyebrows", "eyelash", "eyelashes", "lip", "teeth", "tooth", "tongue", "mouth", "feet", "calves", "shoulders"
        };
        foreach (var kind in CharacterTouchZones.Kinds.Where(k => k.Group != TouchZoneGroup.Extras))
        {
            var stem = kind.Id.EndsWith("_left", StringComparison.Ordinal) ? kind.Id[..^5]
                : kind.Id.EndsWith("_right", StringComparison.Ordinal) ? kind.Id[..^6] : kind.Id;
            words.UnionWith([kind.Id, stem, stem + "s"]);
        }
        return words;
    }

    /// <summary>Whether a zone is special to its character: an extra Martlet knows (animal ears, a tail, wings, horns, a hat...)
    /// or a zone of its own that the vision model found and named (a hair ribbon, a halo, a sword).</summary>
    public static bool IsSpecial(string id) => CharacterTouchZones.Kind(id) is not { } kind || kind.Group == TouchZoneGroup.Extras;

    /// <summary>The zone that something the vision model calls special to the character is: an extra Martlet knows by its own ID;
    /// an animal's ears, a tail, wings, horns or a skirt by any common name; else a zone of its own named after it
    /// (<see cref="CharacterTouchZones.Slug"/>). Null for an ordinary body part (the default zones and the ones the owner adds
    /// cover those) and for anything intimate.</summary>
    public static string? SpecialId(string? name)
    {
        if (CharacterTouchZones.Slug(name) is not { } slug || Intimate(slug)) return null;
        if (CharacterTouchZones.Kind(slug) is { } exact) return exact.Group == TouchZoneGroup.Extras ? slug : null;
        var known = CharacterTouchZones.Normalize(slug);
        if (known is not null && SpecialKinds.Contains(known)) return known;
        if (known is not null && CharacterTouchZones.Kind(known)?.Group != TouchZoneGroup.Extras) return null;
        return BodyWords.Contains(slug) ? null : slug;
    }

    // Whether a name says something intimate.
    private static bool Intimate(string? name) => CharacterTouchZones.Slug(name) is { } slug && slug.Split('_').Any(IntimateWords.Contains);

    // The name a special zone shows with: the vision model's own words for it (a zone of its own always has a name), or null for
    // an extra Martlet knows that it calls by that extra's own name ("tail", "wing", "animal ears").
    private static string? SpecialLabel(string id, string? said)
    {
        var words = string.Join(" ", (said ?? "").Replace('_', ' ').Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        static string Stem(string? slug) => slug?.TrimEnd('s') ?? "";
        if (CharacterTouchZones.Kind(id) is { } kind)
            return words.Length == 0 || Stem(CharacterTouchZones.Slug(words)) is var stem && (stem == Stem(id) || stem == Stem(CharacterTouchZones.Slug(kind.Label)))
                ? null : Capital(words);
        return Capital(words.Length == 0 ? id.Replace('_', ' ') : words);
    }

    private static string Capital(string words)
    {
        if (words.Length > CharacterTouchZones.MaximumLabelLength) words = words[..CharacterTouchZones.MaximumLabelLength].TrimEnd();
        return char.ToUpper(words[0], CultureInfo.InvariantCulture) + words[1..];
    }

    /// <summary>Step 4, with the whole character again: what is special about it, at most <paramref name="maximum"/> things.</summary>
    public static string SpecialInstructions(int maximum) =>
        "You find what is special about a character (a 2D or 3D avatar) in a picture, for a touch-reaction feature: the things on it " +
        "that someone could touch and that a plain human figure doesn't have, such as animal ears, a tail, wings, horns, a halo, a hat, " +
        "a hair ribbon or bow, glasses, headphones, a scarf, a cape, or something it holds. " + Only + " " + Grid + " " + Format +
        $" List at most {maximum}, the most noticeable first. Give each one a short id (lower case, words joined with _), a short name " +
        "in plain words and a tight box. Leave out ordinary body parts (hair, face, eyes, human ears, arms, hands, legs, feet and so on: " +
        "they are found separately), plain everyday clothes such as a shirt, trousers or shoes, and anything intimate. Answer with JSON " +
        "only, no other text, in this form:\n" +
        "{\"special\":[{\"id\":\"cat_ears\",\"name\":\"cat ears\",\"left\":0.30,\"top\":0.02,\"right\":0.62,\"bottom\":0.12}]}\n" +
        "When nothing on it is special, answer {\"special\":[]}.";

    /// <summary>The message that goes with the whole character when asking what is special about it, with the special zones found
    /// on it before (<paramref name="before"/>, ID and name), so that it keeps their IDs.</summary>
    public static string SpecialText(ZoneHints? hints, IReadOnlyList<(string Id, string Name)> before) =>
        "This picture shows the whole character." + (before.Count == 0 ? ""
            : "\nFound on it before (give each one the same id when you see it again):\n" +
              string.Join("\n", before.Select(b => $"{b.Id} - {b.Name.ToLowerInvariant()}"))) +
        HintsText(hints, new(0, 0, 1, 1));

    /// <summary>The zones special to the character in an answer of the special step about a <paramref name="width"/> by
    /// <paramref name="height"/> picture, in the order given: each as <see cref="SpecialId"/> reads its ID (else its name), with the
    /// name the model gave it (null for an extra called by its own name) and its box as fractions of the picture. At most
    /// <paramref name="maximum"/>; body parts, intimate things and zones already <paramref name="placed"/> are left out.</summary>
    public static List<(string Id, string? Label, TouchZoneBox Box)> ReadSpecial(string? answer, int width, int height,
        IReadOnlyCollection<string> placed, int maximum)
    {
        var raw = new List<(string, double[])>();
        var labels = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var entry in ZoneAnswers.Entries(answer))
        {
            if (raw.Count >= maximum) break;
            var (name, said) = (ZoneAnswers.Name(entry), ZoneAnswers.Label(entry));
            if (ZoneAnswers.RawBox(entry) is not { } box || Intimate(name) || Intimate(said) || Read(name, said) is not { } id ||
                placed.Contains(id) || labels.ContainsKey(id)) continue;
            raw.Add((id, box));
            labels[id] = SpecialLabel(id, said ?? name);
        }
        return [.. ZoneAnswers.Scale(raw, width, height).Select(z => (z.Key, labels[z.Key], z.Box))];
    }

    // The zone an entry names: its ID read as special, else (an ID such as "ears" that is a body part) its name only when that names
    // an extra Martlet knows ("cat ears": animal ears), never a zone of its own ("long pink hair" is still the hair).
    private static string? Read(string? name, string? said)
    {
        if (CharacterTouchZones.Slug(name) is null) return SpecialId(said);
        return SpecialId(name) ?? (SpecialId(said) is { } named && SpecialKinds.Contains(named) ? named : null);
    }

    /// <summary>The zones in an answer about a whole picture: each a zone Martlet knows or, special to the character, a zone as
    /// <see cref="SpecialId"/> reads it, with its name. The FIXTURE - NOT AI stand-in's zones, so it can answer what is special.</summary>
    internal static List<(string Id, string? Label, TouchZoneBox Box)> ReadAnyZones(string? answer, int width, int height)
    {
        var raw = new List<(string, double[])>();
        var labels = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var entry in ZoneAnswers.Entries(answer))
        {
            var (name, said) = (ZoneAnswers.Name(entry), ZoneAnswers.Label(entry));
            var id = CharacterTouchZones.Normalize(name) is { } known && !IsSpecial(known) ? known : Intimate(name) || Intimate(said) ? null : Read(name, said);
            if (ZoneAnswers.RawBox(entry) is not { } box || id is null || labels.ContainsKey(id)) continue;
            raw.Add((id, box));
            labels[id] = IsSpecial(id) ? SpecialLabel(id, said ?? name) : null;
        }
        return [.. ZoneAnswers.Scale(raw, width, height).Select(z => (z.Key, labels[z.Key], z.Box))];
    }

    // The extras the model's own part names place (its tail, wings or animal ears) that aren't zones yet.
    private static List<(string Id, TouchZoneBox Box, string What)> NamedSpecialPlaces(ZoneHints? hints, IReadOnlyDictionary<string, TouchZoneBox> zones)
    {
        var places = new List<(string, TouchZoneBox, string)>();
        foreach (var id in NamedSpecial)
            if (!zones.ContainsKey(id) && NamedPlace(id, hints) is { } place) places.Add((id, place.Box, place.What));
        return places;
    }
}
