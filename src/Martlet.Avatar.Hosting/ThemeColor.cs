using System.Globalization;

namespace Martlet.Avatar.Hosting;

/// <summary>A color in OKLCH, the perceptual space Martlet's character themes are made in: lightness 0 (black) to 1 (white),
/// chroma 0 (gray) to about 0.37 (the most vivid sRGB colors) and hue in degrees (about 29 red, 70 orange, 110 yellow, 142
/// green, 195 cyan, 264 blue, 328 magenta). <see cref="Hex"/> is the nearest sRGB color: the same lightness and hue with as
/// much of the chroma as sRGB can show.</summary>
public readonly record struct Oklch(double L, double C, double H)
{
    public static Oklch FromRgb(double r, double g, double b) => FromLab(Lab(Linear(r), Linear(g), Linear(b)));

    public static Oklch FromHex(string hex)
    {
        if (!ThemeColor.TryParse(hex, out var normalized)) throw new FormatException("Colors are written #RRGGBB.");
        var value = int.Parse(normalized.AsSpan(1), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        return FromRgb((value >> 16) & 0xFF, (value >> 8) & 0xFF, value & 0xFF);
    }

    public static Oklch FromLab((double L, double A, double B) lab)
    {
        var c = Math.Sqrt(lab.A * lab.A + lab.B * lab.B);
        var h = c < 1e-9 ? 0 : Math.Atan2(lab.B, lab.A) * 180 / Math.PI;
        return new(lab.L, c, h < 0 ? h + 360 : h);
    }

    public (double L, double A, double B) ToLab() =>
        (L, C * Math.Cos(H * Math.PI / 180), C * Math.Sin(H * Math.PI / 180));

    public Oklch WithL(double lightness) => this with { L = Math.Clamp(lightness, 0, 1) };
    public Oklch WithC(double chroma) => this with { C = Math.Max(0, chroma) };

    /// <summary>#RRGGBB, keeping lightness and hue and reducing chroma until sRGB can show it.</summary>
    public string Hex
    {
        get
        {
            var (r, g, b) = Rgb(this);
            if (!InGamut(r, g, b))
            {
                double low = 0, high = C;
                for (var i = 0; i < 24; i++)
                {
                    var middle = (low + high) / 2;
                    var (mr, mg, mb) = Rgb(this with { C = middle });
                    if (InGamut(mr, mg, mb)) low = middle; else high = middle;
                }
                (r, g, b) = Rgb(this with { C = low });
            }
            return string.Create(CultureInfo.InvariantCulture, $"#{Encode(r):X2}{Encode(g):X2}{Encode(b):X2}");
        }
    }

    /// <summary>Lightness as WCAG measures it (relative luminance) of <see cref="Hex"/>.</summary>
    public double Luminance => ThemeColor.Luminance(Hex);

    internal static double Linear(double channel)
    {
        var c = channel / 255;
        return c <= 0.04045 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
    }

    private static int Encode(double linear)
    {
        linear = Math.Clamp(linear, 0, 1);
        var c = linear <= 0.0031308 ? linear * 12.92 : 1.055 * Math.Pow(linear, 1 / 2.4) - 0.055;
        return (int)Math.Round(Math.Clamp(c, 0, 1) * 255);
    }

    internal static (double L, double A, double B) Lab(double r, double g, double b)
    {
        var l = Math.Cbrt(0.4122214708 * r + 0.5363325363 * g + 0.0514459929 * b);
        var m = Math.Cbrt(0.2119034982 * r + 0.6806995451 * g + 0.1073969566 * b);
        var s = Math.Cbrt(0.0883024619 * r + 0.2817188376 * g + 0.6299787005 * b);
        return (0.2104542553 * l + 0.7936177850 * m - 0.0040720468 * s,
            1.9779984951 * l - 2.4285922050 * m + 0.4505937099 * s,
            0.0259040371 * l + 0.7827717662 * m - 0.8086757660 * s);
    }

    private static (double R, double G, double B) Rgb(Oklch color)
    {
        var (lightness, a, b) = color.ToLab();
        var l = lightness + 0.3963377774 * a + 0.2158037573 * b;
        var m = lightness - 0.1055613458 * a - 0.0638541728 * b;
        var s = lightness - 0.0894841775 * a - 1.2914855480 * b;
        l = l * l * l;
        m = m * m * m;
        s = s * s * s;
        return (4.0767416621 * l - 3.3077115913 * m + 0.2309699292 * s,
            -1.2684380046 * l + 2.6097574011 * m - 0.3413193965 * s,
            -0.0041960863 * l - 0.7034186147 * m + 1.7076147010 * s);
    }

    private static bool InGamut(double r, double g, double b) =>
        r is >= -1e-4 and <= 1 + 1e-4 && g is >= -1e-4 and <= 1 + 1e-4 && b is >= -1e-4 and <= 1 + 1e-4;

    public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"oklch({L:0.###} {C:0.###} {H:0.#})");
}

public static class ThemeColor
{
    /// <summary>Normalizes <c>#rrggbb</c>, <c>rrggbb</c> or <c>#rgb</c> to upper-case <c>#RRGGBB</c>.</summary>
    public static bool TryParse(string? text, out string hex)
    {
        hex = "";
        var value = text?.Trim().TrimStart('#') ?? "";
        if (value.Length == 3 && value.All(char.IsAsciiHexDigit)) value = string.Concat(value.Select(c => $"{c}{c}"));
        if (value.Length != 6 || !value.All(char.IsAsciiHexDigit)) return false;
        hex = "#" + value.ToUpperInvariant();
        return true;
    }

    /// <summary>WCAG 2 relative luminance of a #RRGGBB color.</summary>
    public static double Luminance(string hex)
    {
        if (!TryParse(hex, out var normalized)) throw new FormatException("Colors are written #RRGGBB.");
        var value = int.Parse(normalized.AsSpan(1), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        return 0.2126 * Oklch.Linear((value >> 16) & 0xFF) + 0.7152 * Oklch.Linear((value >> 8) & 0xFF) + 0.0722 * Oklch.Linear(value & 0xFF);
    }

    /// <summary>WCAG 2 contrast ratio between two #RRGGBB colors, 1 to 21.</summary>
    public static double Contrast(string first, string second)
    {
        var a = Luminance(first);
        var b = Luminance(second);
        return (Math.Max(a, b) + 0.05) / (Math.Min(a, b) + 0.05);
    }

    /// <summary>The smallest difference between two hues, 0 to 180 degrees.</summary>
    public static double HueDistance(double first, double second)
    {
        var difference = Math.Abs(first - second) % 360;
        return difference > 180 ? 360 - difference : difference;
    }

    /// <summary>A plain-words name for a color ("dark grayish blue", "light pink", "near black"), for the owner.</summary>
    public static string Name(Oklch color)
    {
        if (color.C < 0.025)
            return color.L switch
            {
                >= 0.95 => "white", >= 0.85 => "off-white", >= 0.7 => "light gray", >= 0.5 => "gray", >= 0.3 => "dark gray",
                >= 0.18 => "near black", _ => "black"
            };
        var h = color.H;
        var hue = h switch
        {
            < 15 or >= 350 => color.L > 0.72 ? "pink" : "crimson",
            < 40 => color.L > 0.72 ? "salmon" : "red",
            < 70 => color.L < 0.55 ? "brown" : "orange",
            < 100 => color.L < 0.55 ? "olive brown" : color.L > 0.85 ? "cream" : "amber",
            < 120 => color.L < 0.55 ? "olive" : "yellow",
            < 165 => "green",
            < 210 => "teal",
            < 245 => "azure",
            < 285 => "blue",
            < 315 => "purple",
            _ => color.L > 0.72 ? "pink" : "magenta"
        };
        var shade = color.L switch { >= 0.82 => "light ", <= 0.35 => "dark ", _ => "" };
        var muted = color.C < 0.06 ? "grayish " : color.C > 0.16 ? "vivid " : "";
        return shade + muted + hue;
    }
}
