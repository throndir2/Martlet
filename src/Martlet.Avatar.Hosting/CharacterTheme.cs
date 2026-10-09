using System.Buffers.Binary;
using System.Text.Json;
using System.Text.Json.Serialization;
using Martlet.Avatars;
using Martlet.Core.Contracts;

namespace Martlet.Avatar.Hosting;

/// <summary>The twelve colors every Martlet window is drawn with. Each is a resource named <c>&lt;role&gt;Brush</c>.</summary>
public static class ThemeRoles
{
    public const string Canvas = "Canvas", Surface = "Surface", Soft = "Soft", Text = "Text", Muted = "Muted", Border = "Border",
        Accent = "Accent", OnAccent = "OnAccent", Focus = "Focus", Success = "Success", Warning = "Warning", Glow = "Glow";

    public static IReadOnlyList<string> All { get; } = [Canvas, Surface, Soft, Text, Muted, Border, Accent, OnAccent, Focus, Success, Warning, Glow];

    /// <summary>The backgrounds text is read on.</summary>
    public static IReadOnlyList<string> Backgrounds { get; } = [Canvas, Surface, Soft];
}

/// <summary>One complete palette: a #RRGGBB color for every <see cref="ThemeRoles"/> role, light or dark.</summary>
public sealed record ThemePalette
{
    public required bool Dark { get; init; }
    public required IReadOnlyDictionary<string, string> Colors { get; init; }

    public string this[string role] => Colors[role];

    /// <summary>A palette from colors by role, or null when a role is missing or isn't #RRGGBB.</summary>
    public static ThemePalette? From(bool dark, IReadOnlyDictionary<string, string>? colors)
    {
        if (colors is null) return null;
        var normalized = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var role in ThemeRoles.All)
        {
            if (!colors.TryGetValue(role, out var value) || !ThemeColor.TryParse(value, out var hex)) return null;
            normalized[role] = hex;
        }
        return new() { Dark = dark, Colors = normalized };
    }

    public bool IsValid => From(Dark, Colors) is not null;

    /// <summary>"Canvas #F7F4F9, Surface #FFFFFF, ..." in role order.</summary>
    public string Describe() => string.Join(", ", ThemeRoles.All.Select(role => $"{role} {Colors[role]}"));

    internal ThemePalette With(string role, Oklch color) =>
        this with { Colors = new Dictionary<string, string>(Colors, StringComparer.Ordinal) { [role] = color.Hex } };
}

/// <summary>One of a character model's main colors: <see cref="Hex"/>, how much of the model's textures it covers
/// (<see cref="Share"/>, 0-1) and what kind of color it is: <c>vivid</c>, <c>muted</c>, <c>neutral</c> (white, gray, black)
/// or <c>skin</c>.</summary>
public sealed record CharacterSwatch(string Hex, double Share, string Kind)
{
    public const string Vivid = "vivid", Muted = "muted", Neutral = "neutral", Skin = "skin";

    [JsonIgnore]
    public Oklch Color => Oklch.FromHex(Hex);

    [JsonIgnore]
    public string Name => Kind == Skin ? "skin tone" : ThemeColor.Name(Color);
}

/// <summary>Decoded pixels of one picture of a model (top-down BGRA, 4 bytes a pixel), weighted against the others.</summary>
public sealed record CharacterPixels(byte[] Bgra, int Width, int Height, double Weight = 1);

/// <summary>A picture inside a model's files: a texture the model is painted with, or a thumbnail of the character.</summary>
public sealed record CharacterImage(string Name, byte[] Bytes, bool Thumbnail);

/// <summary>A character model's main colors, read from its textures: alpha-weighted pixels grouped into at most
/// <see cref="MaximumSwatches"/> colors with deterministic k-means in OKLab.</summary>
public static class CharacterColors
{
    public const int MaximumSwatches = 10;
    /// <summary>Raised when the way colors are read changes, so saved colors are read again.</summary>
    public const int AnalyzerVersion = 2;
    private const int SamplesPerImage = 400_000;

    public static IReadOnlyList<CharacterSwatch> Analyze(IEnumerable<CharacterPixels> images)
    {
        var bins = new Dictionary<int, double[]>();
        foreach (var image in images)
        {
            if (image.Width <= 0 || image.Height <= 0 || image.Bgra.Length < image.Width * image.Height * 4 || image.Weight <= 0) continue;
            var step = Math.Max(1, (int)Math.Sqrt((double)image.Width * image.Height / SamplesPerImage));
            for (var y = 0; y < image.Height; y += step)
                for (var x = 0; x < image.Width; x += step)
                {
                    var i = (y * image.Width + x) * 4;
                    // Nearly opaque pixels only: soft edges and overlays (blush, shadows) blend into what is under them.
                    if (image.Bgra[i + 3] < 200) continue;
                    byte b = image.Bgra[i], g = image.Bgra[i + 1], r = image.Bgra[i + 2];
                    var key = (r >> 3) << 10 | (g >> 3) << 5 | b >> 3;
                    if (!bins.TryGetValue(key, out var bin)) bins[key] = bin = new double[4];
                    bin[0] += image.Weight;
                    bin[1] += r * image.Weight;
                    bin[2] += g * image.Weight;
                    bin[3] += b * image.Weight;
                }
        }
        if (bins.Count == 0) return [];
        var points = bins.OrderBy(b => b.Key).Select(b =>
        {
            var w = b.Value[0];
            var lab = Oklch.Lab(Oklch.Linear(b.Value[1] / w), Oklch.Linear(b.Value[2] / w), Oklch.Linear(b.Value[3] / w));
            return (W: w, L: lab.L, A: lab.A, B: lab.B);
        }).ToArray();
        var total = points.Sum(p => p.W);
        var main = Merge(Cluster(points, Math.Min(12, points.Length)))
            .Where(c => c.W / total >= 0.008).OrderByDescending(c => c.W).Take(MaximumSwatches - MaximumAccents).ToList();
        if (main.Count == 0) main.Add(points.MaxBy(p => p.W));
        // Small vivid touches (eyes, trims, ribbons) vanish into the big clusters, yet they are often what makes a character's
        // look: they are clustered on their own and kept when they aren't one of the main colors already.
        var vivid = points.Where(p => Math.Sqrt(p.A * p.A + p.B * p.B) >= 0.07).ToArray();
        IEnumerable<(double W, double L, double A, double B)> accents = vivid.Length == 0 ? [] : Merge(Cluster(vivid, Math.Min(6, vivid.Length)))
            .Where(c => c.W / total >= 0.002 && main.All(m => Math.Sqrt(Distance2(m, (c.L, c.A, c.B))) >= 0.06))
            .OrderByDescending(c => c.W * (c.A * c.A + c.B * c.B)).Take(MaximumAccents);
        return main.Concat(accents).OrderByDescending(c => c.W).Select(c =>
        {
            var color = Oklch.FromLab((c.L, c.A, c.B));
            return new CharacterSwatch(color.Hex, Math.Round(c.W / total, 4), Kind(Oklch.FromHex(color.Hex)));
        }).ToArray();
    }

    private const int MaximumAccents = 3;

    // Nearly the same color twice is one color.
    private static List<(double W, double L, double A, double B)> Merge(List<(double W, double L, double A, double B)> clusters)
    {
        while (clusters.Count > 1)
        {
            var (first, second, distance) = Closest(clusters);
            if (distance >= 0.055) break;
            var a = clusters[first];
            var b = clusters[second];
            var w = a.W + b.W;
            clusters[first] = (w, (a.L * a.W + b.L * b.W) / w, (a.A * a.W + b.A * b.W) / w, (a.B * a.W + b.B * b.W) / w);
            clusters.RemoveAt(second);
        }
        return clusters;
    }

    /// <summary>What kind of color it is. Skin: the light, softly saturated oranges faces and hands are painted in.</summary>
    public static string Kind(Oklch color) =>
        color.C < 0.03 ? CharacterSwatch.Neutral
        : color.H is >= 20 and <= 80 && color.C <= 0.11 && color.L is >= 0.62 and <= 0.97 ? CharacterSwatch.Skin
        : color.C >= 0.09 ? CharacterSwatch.Vivid
        : CharacterSwatch.Muted;

    private static List<(double W, double L, double A, double B)> Cluster((double W, double L, double A, double B)[] points, int k)
    {
        // Deterministic seeding: the most common color, then each time the color that is both common and far from the chosen ones.
        var centers = new List<(double L, double A, double B)> { Center(points.MaxBy(p => p.W)) };
        var nearest = points.Select(p => Distance2(p, centers[0])).ToArray();
        while (centers.Count < k)
        {
            var best = -1;
            var score = 0.0;
            for (var i = 0; i < points.Length; i++)
                if (points[i].W * nearest[i] > score) (best, score) = (i, points[i].W * nearest[i]);
            if (best < 0) break;
            centers.Add(Center(points[best]));
            for (var i = 0; i < points.Length; i++) nearest[i] = Math.Min(nearest[i], Distance2(points[i], centers[^1]));
        }
        var assignment = new int[points.Length];
        for (var round = 0; round < 40; round++)
        {
            var changed = false;
            for (var i = 0; i < points.Length; i++)
            {
                var best = 0;
                var distance = double.MaxValue;
                for (var c = 0; c < centers.Count; c++)
                {
                    var d = Distance2(points[i], centers[c]);
                    if (d < distance) (best, distance) = (c, d);
                }
                changed |= assignment[i] != best;
                assignment[i] = best;
            }
            var sums = new double[centers.Count, 4];
            for (var i = 0; i < points.Length; i++)
            {
                var p = points[i];
                sums[assignment[i], 0] += p.W;
                sums[assignment[i], 1] += p.L * p.W;
                sums[assignment[i], 2] += p.A * p.W;
                sums[assignment[i], 3] += p.B * p.W;
            }
            for (var c = 0; c < centers.Count; c++)
                if (sums[c, 0] > 0) centers[c] = (sums[c, 1] / sums[c, 0], sums[c, 2] / sums[c, 0], sums[c, 3] / sums[c, 0]);
            if (!changed && round > 0) break;
        }
        var result = new List<(double W, double L, double A, double B)>();
        for (var c = 0; c < centers.Count; c++)
        {
            var weight = 0.0;
            for (var i = 0; i < points.Length; i++) if (assignment[i] == c) weight += points[i].W;
            if (weight > 0) result.Add((weight, centers[c].L, centers[c].A, centers[c].B));
        }
        return result;
    }

    private static (double L, double A, double B) Center((double W, double L, double A, double B) p) => (p.L, p.A, p.B);

    private static double Distance2((double W, double L, double A, double B) p, (double L, double A, double B) c) =>
        (p.L - c.L) * (p.L - c.L) + (p.A - c.A) * (p.A - c.A) + (p.B - c.B) * (p.B - c.B);

    private static (int First, int Second, double Distance) Closest(List<(double W, double L, double A, double B)> clusters)
    {
        (int, int, double) best = (0, 1, double.MaxValue);
        for (var i = 0; i < clusters.Count; i++)
            for (var j = i + 1; j < clusters.Count; j++)
            {
                var d = Math.Sqrt(Distance2(clusters[i], (clusters[j].L, clusters[j].A, clusters[j].B)));
                if (d < best.Item3) best = (i, j, d);
            }
        return best;
    }

    /// <summary>The pictures inside a model's files: a Live2D model's textures (as its model3.json lists them), or a VRM
    /// model's base color textures and its thumbnail. Pictures that aren't there are left out.</summary>
    public static IReadOnlyList<CharacterImage> Images(AvatarRenderer renderer, string entry, IReadOnlyList<AvatarAsset> assets) =>
        renderer == AvatarRenderer.Vrm ? VrmImages(assets.FirstOrDefault()?.Bytes ?? []) : Live2DImages(entry, assets);

    private static IReadOnlyList<CharacterImage> Live2DImages(string entry, IReadOnlyList<AvatarAsset> assets)
    {
        var model = assets.FirstOrDefault(a => a.Name == entry);
        if (model is null) return [];
        var images = new List<CharacterImage>();
        try
        {
            using var document = JsonDocument.Parse(model.Bytes, new JsonDocumentOptions { MaxDepth = 16 });
            if (document.RootElement.TryGetProperty("FileReferences", out var files) && files.ValueKind == JsonValueKind.Object &&
                files.TryGetProperty("Textures", out var textures) && textures.ValueKind == JsonValueKind.Array)
                foreach (var texture in textures.EnumerateArray().Take(16))
                    if (texture.ValueKind == JsonValueKind.String && assets.FirstOrDefault(a => a.Name == texture.GetString()) is { } asset)
                        images.Add(new(asset.Name, asset.Bytes, false));
        }
        catch (JsonException) { }
        return images;
    }

    private static IReadOnlyList<CharacterImage> VrmImages(byte[] glb)
    {
        var images = new List<CharacterImage>();
        try
        {
            if (glb.Length < 28 || BinaryPrimitives.ReadUInt32LittleEndian(glb) != 0x46546C67) return images;
            var jsonLength = (int)BinaryPrimitives.ReadUInt32LittleEndian(glb.AsSpan(12));
            if (jsonLength <= 0 || 20 + jsonLength + 8 > glb.Length || BinaryPrimitives.ReadUInt32LittleEndian(glb.AsSpan(16)) != 0x4E4F534A) return images;
            var binStart = 20 + jsonLength;
            var binLength = (int)BinaryPrimitives.ReadUInt32LittleEndian(glb.AsSpan(binStart));
            if (BinaryPrimitives.ReadUInt32LittleEndian(glb.AsSpan(binStart + 4)) != 0x004E4942 || binLength < 0 || binStart + 8 + binLength > glb.Length)
                return images;
            var bin = glb.AsMemory(binStart + 8, binLength);
            using var document = JsonDocument.Parse(glb.AsMemory(20, jsonLength), new JsonDocumentOptions { MaxDepth = 64 });
            var root = document.RootElement;
            byte[]? Image(int index)
            {
                if (!root.TryGetProperty("images", out var list) || index < 0 || index >= list.GetArrayLength()) return null;
                var image = list[index];
                if (!image.TryGetProperty("bufferView", out var viewIndex) || !root.TryGetProperty("bufferViews", out var views)) return null;
                var view = views[viewIndex.GetInt32()];
                var offset = view.TryGetProperty("byteOffset", out var o) ? o.GetInt32() : 0;
                var length = view.GetProperty("byteLength").GetInt32();
                return offset < 0 || length <= 0 || offset + length > bin.Length ? null : bin.Slice(offset, length).ToArray();
            }
            var used = new SortedSet<int>();
            if (root.TryGetProperty("materials", out var materials) && root.TryGetProperty("textures", out var textures))
                foreach (var material in materials.EnumerateArray())
                    if (material.TryGetProperty("pbrMetallicRoughness", out var pbr) && pbr.TryGetProperty("baseColorTexture", out var color) &&
                        color.TryGetProperty("index", out var texture) && texture.GetInt32() is var t && t >= 0 && t < textures.GetArrayLength() &&
                        textures[t].TryGetProperty("source", out var source))
                        used.Add(source.GetInt32());
            foreach (var index in used.Take(24))
                if (Image(index) is { } bytes) images.Add(new($"image {index}", bytes, false));
            if (root.TryGetProperty("extensions", out var extensions) && extensions.TryGetProperty("VRMC_vrm", out var vrm) &&
                vrm.TryGetProperty("meta", out var meta) && meta.TryGetProperty("thumbnailImage", out var thumbnail) &&
                Image(thumbnail.GetInt32()) is { } picture)
                images.Add(new("thumbnail", picture, true));
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or KeyNotFoundException or IndexOutOfRangeException or
            FormatException or ArgumentException) { }
        return images;
    }
}

/// <summary>One contrast a palette must keep: <see cref="Role"/> on <see cref="On"/>, at least <see cref="Minimum"/>:1.</summary>
public sealed record ThemeCheck(string Role, string On, double Ratio, double Minimum)
{
    public bool Ok => Ratio + 1e-9 >= Minimum;
}

/// <summary>Martlet's own rules for a character's light and dark palettes, and the checks every palette is held to: dark backgrounds with light text in a dark palette and the other way round in a light one, text at least
/// 7:1 on every background, secondary text, accent, success and warning at least 4.5:1, text on the accent 4.5:1, and borders
/// and focus rings 3:1 (WCAG 2).</summary>
public static class CharacterThemeRules
{
    public const double TextRatio = 7, BodyRatio = 4.5, UiRatio = 3;

    /// <summary>The colors a palette is built from, each an actual color of the model: the background tint (the hue that
    /// covers most of the model) and how strongly it may tint (0-1, by how much of the model it covers), the accent candidates
    /// (its most vivid colors, best first: each palette uses the one that needs the least change to stay readable in it), and
    /// its lightest and darkest neutral colors when it has them.</summary>
    public sealed record Sources(CharacterSwatch? Tint, double TintStrength, IReadOnlyList<CharacterSwatch> Accents, CharacterSwatch? Light,
        CharacterSwatch? Darkest)
    {
        /// <summary>The model's most vivid color.</summary>
        public CharacterSwatch Accent => Accents[0];
    }

    private sealed record Family(List<CharacterSwatch> Members)
    {
        public double Share => Members.Sum(m => m.Share);
        // Pale tints (cream, pastels near white), dull colors and specks don't make an accent; a bright yellow does.
        public double Vividness => Members.Where(Candidate).Sum(m => m.Share * Math.Pow(m.Color.C, 1.5));
        public double Hue
        {
            get
            {
                double x = 0, y = 0;
                foreach (var m in Members)
                {
                    var c = m.Color;
                    x += m.Share * c.C * Math.Cos(c.H * Math.PI / 180);
                    y += m.Share * c.C * Math.Sin(c.H * Math.PI / 180);
                }
                var h = Math.Atan2(y, x) * 180 / Math.PI;
                return h < 0 ? h + 360 : h;
            }
        }
    }

    private static bool Candidate(CharacterSwatch swatch) =>
        swatch.Share >= 0.003 && swatch.Color is { C: >= 0.045 } color && !(color.L > 0.9 && color.C < 0.1);

    private static readonly CharacterSwatch MartletPink = new("#A52D64", 0, CharacterSwatch.Vivid);

    public static Sources Pick(IReadOnlyList<CharacterSwatch> swatches)
    {
        var families = new List<Family>();
        foreach (var swatch in swatches.Where(s => s.Kind != CharacterSwatch.Skin && s.Color.C >= 0.012).OrderByDescending(s => s.Share))
        {
            var family = families.FirstOrDefault(f => ThemeColor.HueDistance(f.Hue, swatch.Color.H) < 30);
            if (family is null) families.Add(new([swatch]));
            else family.Members.Add(swatch);
        }
        var tint = families.MaxBy(f => f.Share);
        CharacterSwatch Representative(Family f) => f.Members.Where(Candidate).DefaultIfEmpty(f.Members[0]).MaxBy(m => m.Share * Math.Pow(m.Color.C, 1.5))!;
        var accents = families.Where(f => f.Vividness > 0).OrderByDescending(f => f.Vividness).Take(3).Select(Representative).ToList();
        if (accents.Count == 0)
            accents.Add(tint is not null ? Representative(tint) : swatches.Where(s => s.Color.C >= 0.02).MaxBy(s => s.Color.C) ?? MartletPink);
        var light = swatches.Where(s => s.Kind != CharacterSwatch.Skin && s.Color is { L: >= 0.88, C: <= 0.05 }).MaxBy(s => s.Share);
        var darkest = swatches.Where(s => s.Color is { L: <= 0.34, C: <= 0.08 }).MaxBy(s => s.Share);
        return new(tint?.Members.MaxBy(m => m.Share), tint is null ? 0 : Math.Clamp(tint.Share / 0.3, 0.15, 1), accents, light, darkest);
    }

    // A muted accent gets a little more color so it still reads as the accent; a vivid one is used as it is.
    private static Oklch AccentBase(Oklch color) => color.WithC(Math.Max(color.C, Math.Min(0.08, color.C * 1.5)));

    /// <summary>How well an accent candidate suits a palette: 1 when it is readable on the backgrounds as it is, less the more
    /// its lightness has to change, and much less for a yellow that would turn olive on a light palette.</summary>
    private static double Fitness(Oklch color, IReadOnlyList<string> backgrounds, bool dark)
    {
        var reached = Reach(AccentBase(color), backgrounds, BodyRatio + 0.1, dark);
        var fit = Math.Exp(-Math.Abs(reached.L - color.L) / 0.25);
        if (!dark && color.H is >= 80 and <= 135 && reached.L < 0.7) fit *= 0.35;
        return fit;
    }

    private static readonly double[] RankWeights = [1, 0.7, 0.5];

    /// <summary>The accent a palette uses: the candidate that suits both palettes best (favoring the most vivid), so light and
    /// dark share it; a palette it suits badly (it would have to change a lot) takes the one that suits that palette best.</summary>
    private static Oklch ChooseAccent(Sources sources, bool dark)
    {
        var light = Backgrounds(sources, false).Values.Select(c => c.Hex).ToArray();
        var darkBackgrounds = Backgrounds(sources, true).Values.Select(c => c.Hex).ToArray();
        var scored = sources.Accents.Select((s, rank) => (s.Color, Weight: RankWeights[Math.Min(rank, RankWeights.Length - 1)],
            Light: Fitness(s.Color, light, false), Dark: Fitness(s.Color, darkBackgrounds, true))).ToArray();
        var shared = scored.MaxBy(c => c.Weight * Math.Sqrt(c.Light * c.Dark));
        var fit = dark ? shared.Dark : shared.Light;
        return fit >= 0.35 ? shared.Color : scored.MaxBy(c => c.Weight * (dark ? c.Dark : c.Light)).Color;
    }

    private static (double Hue, double Chroma) Tint(Sources sources)
    {
        var color = sources.Tint?.Color ?? sources.Accent.Color with { C = 0.02 };
        // A hue that covers little of the model only hints at the backgrounds.
        return (color.H, Math.Min(color.C, 0.12) * sources.TintStrength);
    }

    /// <summary>The backgrounds (Canvas, Surface, Soft) of a palette: near white or near black, tinted with the model's
    /// main hue; a dark palette's canvas is the model's own darkest color when it shares that
    /// hue.</summary>
    private static Dictionary<string, Oklch> Backgrounds(Sources sources, bool dark)
    {
        var (tint, tintC) = Tint(sources);
        var scale = Math.Max(1, sources.TintStrength);
        var colors = new Dictionary<string, Oklch>();
        if (!dark)
        {
            colors[ThemeRoles.Canvas] = new(0.975, Math.Clamp(tintC * 0.25, 0.003, 0.014 * scale), tint);
            colors[ThemeRoles.Surface] = new(0.995, Math.Min(colors[ThemeRoles.Canvas].C * 0.4, 0.004 * scale), tint);
            colors[ThemeRoles.Soft] = new(0.935, Math.Clamp(tintC * 0.6, 0.008, 0.036 * scale), tint);
        }
        else
        {
            var canvas = sources.Darkest is { } darkest && (darkest.Color.C < 0.02 || ThemeColor.HueDistance(darkest.Color.H, tint) < 40)
                ? new Oklch(Math.Clamp(darkest.Color.L, 0.17, 0.23), Math.Clamp(Math.Max(darkest.Color.C, tintC * 0.4), 0.006, 0.035 * scale),
                    darkest.Color.C < 0.02 ? tint : darkest.Color.H)
                : new Oklch(0.2, Math.Clamp(tintC * 0.4, 0.006, 0.03 * scale), tint);
            colors[ThemeRoles.Canvas] = canvas;
            colors[ThemeRoles.Surface] = canvas with { L = canvas.L + 0.055, C = Math.Min(canvas.C + 0.004, 0.04) };
            colors[ThemeRoles.Soft] = canvas with { L = canvas.L + 0.11, C = Math.Min(canvas.C + 0.01, 0.045) };
        }
        return colors;
    }

    /// <summary>The rule-based palette for a model with these colors.</summary>
    public static ThemePalette Build(IReadOnlyList<CharacterSwatch> swatches, bool dark)
    {
        var sources = Pick(swatches);
        var (tint, tintC) = Tint(sources);
        var colors = Backgrounds(sources, dark);
        var backgrounds = colors.Values.Select(c => c.Hex).ToArray();
        var accentSource = ChooseAccent(sources, dark);
        var glowSource = sources.Accents.FirstOrDefault(s => ThemeColor.HueDistance(s.Color.H, accentSource.H) >= 45)?.Color
            ?? (sources.Tint is { } tinted && ThemeColor.HueDistance(tinted.Color.H, accentSource.H) >= 45 && tinted.Color.C >= 0.01 ? tinted.Color : accentSource);
        var accentBase = AccentBase(accentSource);
        var successHue = ThemeColor.HueDistance(accentSource.H, 150) < 25 ? 170.0 : 150.0;
        var warningHue = ThemeColor.HueDistance(accentSource.H, 65) < 25 ? 45.0 : 65.0;
        var lighter = dark;
        Oklch Fit(Oklch color, double ratio, IReadOnlyList<string>? on = null) => Reach(color, on ?? backgrounds, ratio, lighter);
        if (!dark)
        {
            colors[ThemeRoles.Text] = Fit(sources.Darkest is { } text
                ? new Oklch(Math.Clamp(text.Color.L, 0.22, 0.34), Math.Min(text.Color.C, 0.05), text.Color.C < 0.01 ? tint : text.Color.H)
                : new(0.29, Math.Clamp(tintC * 0.5, 0.01, 0.04), tint), TextRatio + 0.1);
            colors[ThemeRoles.Muted] = Fit(new(0.48, Math.Clamp(tintC * 0.6, 0.012, 0.045), tint), BodyRatio + 0.1);
            colors[ThemeRoles.Border] = Fit(new(0.6, Math.Clamp(tintC * 0.7, 0.015, 0.06), tint), UiRatio + 0.1);
            colors[ThemeRoles.Accent] = Fit(accentBase, BodyRatio + 0.1);
            colors[ThemeRoles.OnAccent] = colors[ThemeRoles.Surface];
            colors[ThemeRoles.Focus] = Fit(colors[ThemeRoles.Accent].WithL(colors[ThemeRoles.Accent].L - 0.07), UiRatio + 0.1);
            colors[ThemeRoles.Success] = Fit(new(0.5, 0.12, successHue), BodyRatio + 0.1);
            colors[ThemeRoles.Warning] = Fit(new(0.55, 0.13, warningHue), BodyRatio + 0.1);
            colors[ThemeRoles.Glow] = new(0.88, Math.Clamp(glowSource.C * 0.5, 0.035, 0.08), glowSource.H);
        }
        else
        {
            colors[ThemeRoles.Text] = Fit(sources.Light is { } text
                ? text.Color with { C = Math.Min(text.Color.C, 0.03) }
                : new(0.95, Math.Clamp(tintC * 0.2, 0.005, 0.015), tint), TextRatio + 0.1);
            colors[ThemeRoles.Muted] = Fit(new(0.8, Math.Clamp(tintC * 0.5, 0.01, 0.04), tint), BodyRatio + 0.1);
            colors[ThemeRoles.Border] = Fit(new(0.6, Math.Clamp(tintC * 0.7, 0.015, 0.06), tint), UiRatio + 0.1);
            var accent = Fit(accentBase, BodyRatio + 0.1);
            var onAccent = new Oklch(0.2, Math.Min(accentSource.C * 0.4, 0.05), accentSource.H);
            while (ThemeColor.Contrast(onAccent.Hex, accent.Hex) < BodyRatio + 0.1 && accent.L < 0.98) accent = accent.WithL(accent.L + 0.01);
            colors[ThemeRoles.Accent] = accent;
            colors[ThemeRoles.OnAccent] = onAccent;
            colors[ThemeRoles.Focus] = Fit(accent.WithL(Math.Min(0.97, accent.L + 0.07)), UiRatio + 0.1);
            colors[ThemeRoles.Success] = Fit(new(0.8, 0.12, successHue), BodyRatio + 0.1);
            colors[ThemeRoles.Warning] = Fit(new(0.84, 0.12, warningHue + 10), BodyRatio + 0.1);
            colors[ThemeRoles.Glow] = new(0.36, Math.Clamp(glowSource.C * 0.6, 0.04, 0.09), glowSource.H);
        }
        var palette = new ThemePalette
        {
            Dark = dark,
            Colors = ThemeRoles.All.ToDictionary(role => role, role => colors[role].Hex, StringComparer.Ordinal)
        };
        return Repair(palette).Palette;
    }

    /// <summary>Moves <paramref name="color"/>'s lightness (keeping its hue and chroma) until it has
    /// <paramref name="ratio"/>:1 against every color in <paramref name="on"/>; unchanged when it already has.</summary>
    public static Oklch Reach(Oklch color, IReadOnlyList<string> on, double ratio, bool lighter)
    {
        var current = color;
        for (var i = 0; i < 120 && !Passes(current, on, ratio); i++)
        {
            var next = current.WithL(current.L + (lighter ? 0.01 : -0.01));
            if (next.L == current.L) break;
            current = next;
        }
        return current;
    }

    private static bool Passes(Oklch color, IReadOnlyList<string> on, double ratio)
    {
        var hex = color.Hex;
        return on.All(background => ThemeColor.Contrast(hex, background) >= ratio);
    }

    /// <summary>Every contrast the palette must keep, with the ratio it has.</summary>
    public static IReadOnlyList<ThemeCheck> Check(ThemePalette palette)
    {
        var checks = new List<ThemeCheck>();
        void Add(string role, string on, double minimum) => checks.Add(new(role, on, Math.Round(ThemeColor.Contrast(palette[role], palette[on]), 2), minimum));
        foreach (var background in ThemeRoles.Backgrounds)
        {
            Add(ThemeRoles.Text, background, TextRatio);
            foreach (var role in new[] { ThemeRoles.Muted, ThemeRoles.Accent, ThemeRoles.Success, ThemeRoles.Warning }) Add(role, background, BodyRatio);
        }
        Add(ThemeRoles.OnAccent, ThemeRoles.Accent, BodyRatio);
        foreach (var background in new[] { ThemeRoles.Surface, ThemeRoles.Canvas })
        {
            Add(ThemeRoles.Border, background, UiRatio);
            Add(ThemeRoles.Focus, background, UiRatio);
        }
        return checks;
    }

    /// <summary>Whether the palette's backgrounds are as dark (or light) as its kind needs.</summary>
    public static IReadOnlyList<string> Problems(ThemePalette palette)
    {
        var problems = WrongBackgrounds(palette)
            .Select(role => palette.Dark ? $"{role} is too light for a dark palette" : $"{role} is too dark for a light palette").ToList();
        problems.AddRange(Check(palette).Where(c => !c.Ok).Select(c => $"{c.Role} on {c.On} is {c.Ratio:0.##}:1 (needs {c.Minimum:0.#}:1)"));
        return problems;
    }

    /// <summary>The backgrounds (Canvas, Surface, Soft) too light for a dark palette, or too dark for a light one.</summary>
    public static IReadOnlyList<string> WrongBackgrounds(ThemePalette palette) =>
        BackgroundBounds.Where(bound => Oklch.FromHex(palette[bound.Role]).L is var l && (palette.Dark ? l > bound.Dark : l < bound.Light))
            .Select(bound => bound.Role).ToArray();

    // The lightness a light palette's backgrounds need at least, and a dark palette's at most.
    private static readonly (string Role, double Light, double Dark)[] BackgroundBounds =
        [(ThemeRoles.Canvas, 0.88, 0.32), (ThemeRoles.Surface, 0.9, 0.38), (ThemeRoles.Soft, 0.82, 0.46)];

    /// <summary>The palette made to keep every rule, changing as little as it can: a background on the wrong side is made
    /// light (or dark) in its own hue, and a color without enough contrast is made lighter or darker in its own hue. Returns
    /// what was changed, in words.</summary>
    public static (ThemePalette Palette, IReadOnlyList<string> Fixes) Repair(ThemePalette palette)
    {
        var fixes = new List<string>();
        var result = palette;
        foreach (var (role, light, darkest) in BackgroundBounds)
        {
            var color = Oklch.FromHex(result[role]);
            if (result.Dark && color.L > darkest)
            {
                result = result.With(role, color with { L = role == ThemeRoles.Canvas ? 0.2 : role == ThemeRoles.Surface ? 0.255 : 0.31, C = Math.Min(color.C, 0.04) });
                fixes.Add($"{role} was too light for a dark palette");
            }
            else if (!result.Dark && color.L < light)
            {
                result = result.With(role, color with { L = role == ThemeRoles.Canvas ? 0.975 : role == ThemeRoles.Surface ? 0.995 : 0.935, C = Math.Min(color.C, 0.035) });
                fixes.Add($"{role} was too dark for a light palette");
            }
        }
        var backgrounds = ThemeRoles.Backgrounds.Select(role => result[role]).ToArray();
        var lighter = result.Dark;
        // Body text keeps a hint of the character's hue, never a strong color: long reading in it tires.
        foreach (var (role, most) in new[] { (ThemeRoles.Text, 0.05), (ThemeRoles.Muted, 0.08) })
        {
            var color = Oklch.FromHex(result[role]);
            if (color.C <= most + 0.005) continue;
            result = result.With(role, color.WithC(most));
            fixes.Add($"{role} was too colorful to read comfortably");
        }
        foreach (var (role, ratio) in new[] { (ThemeRoles.Text, TextRatio), (ThemeRoles.Muted, BodyRatio), (ThemeRoles.Accent, BodyRatio),
            (ThemeRoles.Success, BodyRatio), (ThemeRoles.Warning, BodyRatio) })
        {
            var color = Oklch.FromHex(result[role]);
            if (Passes(color, backgrounds, ratio)) continue;
            result = result.With(role, Reach(color, backgrounds, ratio + 0.05, lighter));
            fixes.Add($"{role} was made {(lighter ? "lighter" : "darker")} to stand out on the backgrounds");
        }
        var accent = Oklch.FromHex(result[ThemeRoles.Accent]);
        var onAccent = Oklch.FromHex(result[ThemeRoles.OnAccent]);
        if (ThemeColor.Contrast(accent.Hex, onAccent.Hex) < BodyRatio)
        {
            var fixedOn = Reach(onAccent, [accent.Hex], BodyRatio + 0.05, onAccent.L >= accent.L);
            if (!Passes(fixedOn, [accent.Hex], BodyRatio)) fixedOn = Reach(onAccent, [accent.Hex], BodyRatio + 0.05, onAccent.L < accent.L);
            result = result.With(ThemeRoles.OnAccent, fixedOn);
            fixes.Add("OnAccent was changed to stand out on Accent");
            if (!Passes(fixedOn, [accent.Hex], BodyRatio))
            {
                result = result.With(ThemeRoles.Accent, Reach(accent, [fixedOn.Hex], BodyRatio + 0.05, !lighter));
                fixes.Add("Accent was changed so text on it stands out");
            }
        }
        var frames = new[] { result[ThemeRoles.Surface], result[ThemeRoles.Canvas] };
        foreach (var role in new[] { ThemeRoles.Border, ThemeRoles.Focus })
        {
            var color = Oklch.FromHex(result[role]);
            if (Passes(color, frames, UiRatio)) continue;
            result = result.With(role, Reach(color, frames, UiRatio + 0.05, lighter));
            fixes.Add($"{role} was made {(lighter ? "lighter" : "darker")} to show on the backgrounds");
        }
        var glow = Oklch.FromHex(result[ThemeRoles.Glow]);
        if (result.Dark && glow.L > 0.5 || !result.Dark && glow.L < 0.75)
        {
            result = result.With(ThemeRoles.Glow, glow.WithL(result.Dark ? 0.36 : 0.88));
            fixes.Add($"Glow was made {(result.Dark ? "darker" : "lighter")} to sit behind the mascot");
        }
        return (result, fixes);
    }
}

/// <summary>One model's colors and palettes as saved in <c>character-themes.json</c> on this PC, by the model's ID.</summary>
public sealed record CharacterThemeEntry
{
    public required string ModelId { get; init; }
    public int Analyzer { get; init; }
    public required IReadOnlyList<CharacterSwatch> Swatches { get; init; }
    public DateTimeOffset UpdatedAt { get; init; }
}

/// <summary><c>character-themes.json</c>: each character model's colors (read once from its textures), for the newest
/// <see cref="MaximumModels"/> models.</summary>
public static class CharacterThemes
{
    public const string FileName = "character-themes.json";
    public const int MaximumModels = 32;
    private const int MaximumBytes = 1024 * 1024;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private static readonly SemaphoreSlim Gate = new(1, 1);

    private sealed record Document(int Version, IReadOnlyList<CharacterThemeEntry> Models);

    public static string Path(string dataDirectory) => System.IO.Path.Combine(dataDirectory, FileName);

    public static CharacterThemeEntry? Load(string dataDirectory, string modelId) =>
        LoadAll(dataDirectory).FirstOrDefault(m => m.ModelId == modelId);

    /// <summary>Every saved model; a damaged file reads as none.</summary>
    public static IReadOnlyList<CharacterThemeEntry> LoadAll(string dataDirectory)
    {
        try
        {
            var path = Path(dataDirectory);
            if (!File.Exists(path) || new FileInfo(path).Length > MaximumBytes) return [];
            var document = JsonSerializer.Deserialize<Document>(File.ReadAllBytes(path), Json);
            return document is { Version: 1, Models: { } models }
                ? models.Where(m => m is { ModelId: not null, Swatches: not null } && Martlet.Core.Characters.CharacterModelLibrary.IsSha256(m.ModelId) &&
                    m.Swatches.All(s => s is not null && ThemeColor.TryParse(s.Hex, out _))).ToArray()
                : [];
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or NotSupportedException or FormatException) { return []; }
    }

    /// <summary>Saves one model's entry. Throws <see cref="ContractException"/> when it can't be saved.</summary>
    public static async Task<CharacterThemeEntry> SaveAsync(string dataDirectory, CharacterThemeEntry entry, DateTimeOffset now, CancellationToken token = default)
    {
        ContractRules.Require(Martlet.Core.Characters.CharacterModelLibrary.IsSha256(entry.ModelId), "The model's ID is invalid.");
        ContractRules.Require(entry.Swatches.Count <= CharacterColors.MaximumSwatches, "Too many colors.");
        entry = entry with { UpdatedAt = now.ToUniversalTime() };
        await Gate.WaitAsync(token);
        try
        {
            var models = LoadAll(dataDirectory).Where(m => m.ModelId != entry.ModelId).Append(entry)
                .OrderByDescending(m => m.UpdatedAt).Take(MaximumModels).OrderBy(m => m.ModelId, StringComparer.Ordinal).ToArray();
            var bytes = JsonSerializer.SerializeToUtf8Bytes(new Document(1, models), Json);
            ContractRules.Require(bytes.Length <= MaximumBytes, "The character themes are too large.", ErrorCode.PayloadTooLarge);
            Directory.CreateDirectory(dataDirectory);
            var temporary = System.IO.Path.Combine(dataDirectory, $"character-themes.{Guid.NewGuid():N}.tmp");
            try
            {
                await File.WriteAllBytesAsync(temporary, bytes, token);
                File.Move(temporary, Path(dataDirectory), overwrite: true);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
            return entry;
        }
        finally { Gate.Release(); }
    }
}
