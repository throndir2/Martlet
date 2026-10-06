using System.Text;

namespace Martlet.Discord.Calls;

/// <summary>A rectangle in a frame's pixels.</summary>
public readonly record struct PixelRect(int X, int Y, int Width, int Height)
{
    public int Right => X + Width;
    public int Bottom => Y + Height;
    public bool IsEmpty => Width <= 0 || Height <= 0;

    /// <summary>This rectangle kept inside a <paramref name="width"/> x <paramref name="height"/> frame.</summary>
    public PixelRect Clamp(int width, int height)
    {
        var left = Math.Clamp(X, 0, width);
        var top = Math.Clamp(Y, 0, height);
        return new(left, top, Math.Clamp(Right, 0, width) - left, Math.Clamp(Bottom, 0, height) - top);
    }
}

/// <summary>How Discord shows someone speaking: a green ring around an avatar (the voice channel's member list, a DM call's
/// avatars) or a green border around a video or avatar tile (the call grid).</summary>
public enum SpeakingKind { Ring, Tile }

/// <summary>One speaking indicator and where that person's name is drawn: to the right of a ring (member list) or at the
/// bottom-left inside a tile; <see cref="Below"/> is a second place under a ring (a call stage), tried when the first has none.</summary>
public sealed record SpeakingMark(SpeakingKind Kind, PixelRect Box, PixelRect NameArea, PixelRect? Below);

/// <summary>A line of text read from a picture, with where it is (in that picture's pixels).</summary>
public sealed record TextLine(string Text, PixelRect Box);

/// <summary>Reads text in a BGRA picture, on this PC (Windows' own OCR); tests use a fake.</summary>
public interface ICallTextReader
{
    /// <summary>The lines in a <paramref name="width"/> x <paramref name="height"/> BGRA32 picture; empty when none or when
    /// reading isn't possible here.</summary>
    Task<IReadOnlyList<TextLine>> ReadAsync(byte[] bgra, int width, int height, CancellationToken token);
}

/// <summary>Finds Discord's speaking indicators in a picture of the Discord window: pixels of Discord's speaking green
/// (#23A55A, older #3BA55D/#43B581) joined into shapes, kept when they are hollow (a ring or a border, never a filled status
/// dot or button) and large enough. Pure pixel arithmetic on this PC; the picture is never kept or sent.</summary>
public static class DiscordSpeakingDetector
{
    /// <summary>Smallest ring (pixels across) that counts: Discord's member-list avatars are 24 px with a 2 px ring.</summary>
    public const int MinimumRing = 16;
    /// <summary>Smallest tile border (pixels across) that counts.</summary>
    public const int MinimumTile = 80;
    /// <summary>At most this many indicators per picture (a big call shows a few speakers at once).</summary>
    public const int MaximumMarks = 8;

    /// <summary>Discord's speaking green, with the tolerance anti-aliasing and scaling need; never yellow-green or teal.</summary>
    public static bool IsSpeakingGreen(byte blue, byte green, byte red) =>
        green >= 110 && green - red >= 55 && green - blue >= 25 && blue + 10 >= red && red <= 140;

    public static IReadOnlyList<SpeakingMark> Find(ReadOnlySpan<byte> bgra, int width, int height)
    {
        if (width <= 0 || height <= 0 || bgra.Length < width * height * 4) return [];
        var mask = new bool[width * height];
        for (var i = 0; i < mask.Length; i++)
            mask[i] = IsSpeakingGreen(bgra[i * 4], bgra[i * 4 + 1], bgra[i * 4 + 2]);
        var seen = new bool[mask.Length];
        var marks = new List<SpeakingMark>();
        var stack = new Stack<int>();
        for (var start = 0; start < mask.Length; start++)
        {
            if (!mask[start] || seen[start]) continue;
            int left = width, top = height, right = -1, bottom = -1, count = 0;
            seen[start] = true;
            stack.Push(start);
            while (stack.Count > 0)
            {
                var index = stack.Pop();
                int x = index % width, y = index / width;
                count++;
                left = Math.Min(left, x); right = Math.Max(right, x);
                top = Math.Min(top, y); bottom = Math.Max(bottom, y);
                // 8-connected, so a thin anti-aliased ring stays one shape.
                for (var dy = -1; dy <= 1; dy++)
                for (var dx = -1; dx <= 1; dx++)
                {
                    int nx = x + dx, ny = y + dy;
                    if (nx < 0 || ny < 0 || nx >= width || ny >= height) continue;
                    var next = ny * width + nx;
                    if (!mask[next] || seen[next]) continue;
                    seen[next] = true;
                    stack.Push(next);
                }
            }
            var box = new PixelRect(left, top, right - left + 1, bottom - top + 1);
            if (Classify(mask, width, box, count) is { } kind) marks.Add(Mark(kind, box, width, height));
        }
        // The largest first: a tile border beats a ring inside it.
        return marks.OrderByDescending(mark => mark.Box.Width * mark.Box.Height).Take(MaximumMarks).ToArray();
    }

    private static SpeakingKind? Classify(bool[] mask, int width, PixelRect box, int count)
    {
        var across = Math.Min(box.Width, box.Height);
        if (across < MinimumRing) return null;
        var fill = count / (double)(box.Width * box.Height);
        // Hollow: a ring is about a fifth green, a border far less; a status dot or a button is mostly green.
        if (fill > 0.45) return null;
        // Its middle is not green.
        var inner = new PixelRect(box.X + box.Width / 4, box.Y + box.Height / 4, box.Width / 2, box.Height / 2);
        var green = 0;
        for (var y = inner.Y; y < inner.Bottom; y++)
        for (var x = inner.X; x < inner.Right; x++)
            if (mask[y * width + x]) green++;
        if (green > inner.Width * inner.Height / 10) return null;
        var aspect = box.Width / (double)box.Height;
        if (aspect is >= 0.8 and <= 1.25 && across < MinimumTile * 2 && fill > 0.08) return SpeakingKind.Ring;
        if (across >= MinimumTile && aspect is >= 0.5 and <= 3.0) return SpeakingKind.Tile;
        return null;
    }

    private static SpeakingMark Mark(SpeakingKind kind, PixelRect box, int width, int height)
    {
        if (kind == SpeakingKind.Tile)
        {
            // Discord writes the name at the tile's bottom-left, inside the border.
            var strip = Math.Clamp(box.Height / 5, 24, 56);
            return new(kind, box, new PixelRect(box.X + 4, box.Bottom - strip - 2, box.Width * 3 / 5, strip).Clamp(width, height), null);
        }
        var size = box.Height;
        var right = new PixelRect(box.Right + 2, box.Y - size / 4, Math.Min(size * 9, 360), size + size / 2).Clamp(width, height);
        var below = new PixelRect(box.X - size / 2, box.Bottom + 2, size * 2, Math.Max(18, size / 2)).Clamp(width, height);
        return new(kind, box, right, below.IsEmpty ? null : below);
    }
}

/// <summary>Reads the name beside each speaking indicator (Windows' OCR on small crops, upscaled for small text) and turns it
/// into a display name; the owner's own name is left out (their tile lights up when they or Martlet, through their microphone,
/// talk).</summary>
public sealed class DiscordSpeakerReader(ICallTextReader reader)
{
    /// <summary>The names of the people speaking in the picture, most prominent first; empty when nobody is.</summary>
    public async Task<IReadOnlyList<string>> SpeakingAsync(byte[] bgra, int width, int height, string? ownerName,
        CancellationToken token)
    {
        var names = new List<string>();
        foreach (var mark in DiscordSpeakingDetector.Find(bgra, width, height))
        {
            var name = await ReadAsync(bgra, width, height, mark.NameArea, mark, token).ConfigureAwait(false);
            if (name is null && mark.Below is { } below) name = await ReadAsync(bgra, width, height, below, mark, token).ConfigureAwait(false);
            if (name is null || DiscordSpeakerNames.Same(name, ownerName) || names.Contains(name, StringComparer.OrdinalIgnoreCase)) continue;
            names.Add(name);
        }
        return names;
    }

    private async Task<string?> ReadAsync(byte[] bgra, int width, int height, PixelRect area, SpeakingMark mark, CancellationToken token)
    {
        if (area.Width < 8 || area.Height < 8) return null;
        // Windows' OCR misses small text at some sizes: enlarged twice, then three times when that read nothing.
        foreach (var scale in area.Height < 48 ? new[] { 2, 3 } : [1, 2])
        {
            var crop = Crop(bgra, width, area, scale);
            try
            {
                var lines = await reader.ReadAsync(crop, area.Width * scale, area.Height * scale, token).ConfigureAwait(false);
                // Back to the picture's pixels, beside the mark.
                var placed = lines.Select(line => line with
                {
                    Box = new(area.X + line.Box.X / scale, area.Y + line.Box.Y / scale, line.Box.Width / scale, line.Box.Height / scale)
                }).ToArray();
                if (DiscordSpeakerNames.Pick(mark, placed) is { } name) return name;
            }
            finally { Array.Clear(crop); }
        }
        return null;
    }

    /// <summary>The <paramref name="area"/> of a BGRA picture, enlarged <paramref name="scale"/> times (nearest pixel), as grey
    /// dark-on-light text: Discord's dark theme draws light names on dark grey, which Windows' OCR reads poorly.</summary>
    public static byte[] Crop(byte[] bgra, int width, PixelRect area, int scale = 1)
    {
        var grey = new byte[area.Width * area.Height];
        long sum = 0;
        for (var y = 0; y < area.Height; y++)
        for (var x = 0; x < area.Width; x++)
        {
            var source = ((area.Y + y) * width + area.X + x) * 4;
            var value = (byte)((bgra[source] * 29 + bgra[source + 1] * 150 + bgra[source + 2] * 77) >> 8);
            grey[y * area.Width + x] = value;
            sum += value;
        }
        var invert = grey.Length > 0 && sum / grey.Length < 128;
        var w = area.Width * scale;
        var result = new byte[w * area.Height * scale * 4];
        for (var y = 0; y < area.Height * scale; y++)
        for (var x = 0; x < w; x++)
        {
            var value = grey[y / scale * area.Width + x / scale];
            if (invert) value = (byte)(255 - value);
            var target = (y * w + x) * 4;
            result[target] = result[target + 1] = result[target + 2] = value;
            result[target + 3] = 255;
        }
        Array.Clear(grey);
        return result;
    }
}

/// <summary>Display names as Discord draws them, cleaned up from OCR.</summary>
public static class DiscordSpeakerNames
{
    public const int MaximumLength = 32;

    /// <summary>The line nearest the mark's middle (beside a ring) or the lowest line (inside a tile), as a name.</summary>
    public static string? Pick(SpeakingMark mark, IReadOnlyList<TextLine> lines)
    {
        var middle = mark.Box.Y + mark.Box.Height / 2.0;
        var ordered = mark.Kind == SpeakingKind.Tile
            ? lines.OrderByDescending(line => line.Box.Bottom)
            : lines.OrderBy(line => Math.Abs(line.Box.Y + line.Box.Height / 2.0 - middle)).ThenBy(line => line.Box.X);
        foreach (var line in ordered)
            if (Clean(line.Text) is { } name) return name;
        return null;
    }

    /// <summary>A display name from OCR text: letters, digits and the punctuation names use; badges, icons and stray marks
    /// dropped; null when nothing name-like is left.</summary>
    public static string? Clean(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var kept = new StringBuilder();
        foreach (var c in text.Trim())
        {
            if (char.IsLetterOrDigit(c) || c is '.' or '_' or '-' or '\'' || c == ' ' && kept.Length > 0 && kept[^1] != ' ') kept.Append(c);
            if (kept.Length >= MaximumLength) break;
        }
        var name = kept.ToString().Trim(' ', '.', '-', '_', '\'');
        return name.Count(char.IsLetter) >= 2 ? name : null;
    }

    /// <summary>Whether two names are the same person (case and spacing ignored).</summary>
    public static bool Same(string? a, string? b) =>
        !string.IsNullOrWhiteSpace(a) && !string.IsNullOrWhiteSpace(b) &&
        string.Equals(Squash(a), Squash(b), StringComparison.OrdinalIgnoreCase);

    private static string Squash(string text) => new(text.Where(char.IsLetterOrDigit).ToArray());
}

/// <summary>Who spoke an utterance: each picture taken while the call was heard counts a vote for every name lit then; the
/// name seen in most pictures wins (the latest on a tie). Nobody seen means "someone".</summary>
public sealed class DiscordSpeakerVote
{
    private readonly Dictionary<string, (int Votes, int Last)> votes = new(StringComparer.OrdinalIgnoreCase);
    private int samples;

    public int Samples => samples;

    public void Add(IReadOnlyList<string> speaking)
    {
        samples++;
        foreach (var name in speaking.Distinct(StringComparer.OrdinalIgnoreCase))
            votes[name] = ((votes.TryGetValue(name, out var had) ? had.Votes : 0) + 1, samples);
    }

    public string? Winner => votes.Count == 0 ? null
        : votes.OrderByDescending(entry => entry.Value.Votes).ThenByDescending(entry => entry.Value.Last).First().Key;
}

/// <summary>A line heard from the call, as it goes to Thinking (after the PC-audio marker) and shows in the talk window.</summary>
public static class DiscordCallLine
{
    public const string Someone = "Someone";

    /// <summary>"Alice in the call: hey Jane", or "Someone in the call: ..." when nobody could be told apart.</summary>
    public static string Format(string? speaker, string text) =>
        $"{DiscordSpeakerNames.Clean(speaker) ?? Someone} in the call: {text.Trim()}";

    /// <summary>Someone in the call said one of Martlet's names: it answers without waiting its turn.</summary>
    public static bool Addressed(string text, IEnumerable<string> names) => DiscordAddressing.NameSaid(text, names);
}
