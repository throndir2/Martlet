using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Martlet.Core.Settings;

public enum CharacterCardFormat { TavernV1, CharacterCardV2, CharacterCardV3 }

/// <summary>A character from SillyTavern, Chub (CharacterHub), RisuAI, Agnai and other Tavern-compatible apps: a TavernAI V1,
/// Character Card V2 (<c>chara_card_v2</c>) or V3 (<c>chara_card_v3</c>) card. Only the fields that say who the character is
/// are kept; creator notes, tags, assets and app extensions are not used in prompts.</summary>
public sealed partial record CharacterCard
{
    public required CharacterCardFormat Format { get; init; }
    public required string Name { get; init; }
    public string Nickname { get; init; } = "";
    public string Creator { get; init; } = "";
    public string Description { get; init; } = "";
    public string Personality { get; init; } = "";
    public string Scenario { get; init; } = "";
    public string FirstMessage { get; init; } = "";
    public string ExampleMessages { get; init; } = "";
    public string SystemPrompt { get; init; } = "";
    public string PostHistoryInstructions { get; init; } = "";
    /// <summary>Enabled lorebook entries marked constant (always in the prompt).</summary>
    public IReadOnlyList<string> AlwaysOnLore { get; init; } = [];
    /// <summary>Enabled lorebook entries that only apply when a keyword appears; they go to a lorebook, not the persona text.</summary>
    public int KeywordLoreEntries { get; init; }
    /// <summary>The card's embedded character book as a Martlet lorebook (every entry, including always-on ones), if it has one.</summary>
    public Lorebooks.LorebookImport? Lorebook { get; init; }

    /// <summary>The name the character goes by: a V3 nickname replaces <c>{{char}}</c> instead of the full name.</summary>
    public string DisplayName => Nickname.Length > 0 ? Nickname : Name;

    public string FormatName => Format switch
    {
        CharacterCardFormat.TavernV1 => "Tavern V1 card",
        CharacterCardFormat.CharacterCardV2 => "Character Card V2",
        _ => "Character Card V3"
    };

    private sealed record Section(string Title, string Label, string Body, int Priority);

    /// <summary>Turns the card into a Martlet persona name and persona text that fits the given budget. Sections are kept by
    /// priority (description, personality, scenario, system prompt, always-on lore, post-history instructions, example
    /// dialogue, greeting) and shown in card order; one that doesn't fit is shortened at a sentence or word boundary or left
    /// out, and reported.</summary>
    public CharacterCardPersona ToPersona(string? fallbackName, int maximumCharacters, int maximumUtf8Bytes)
    {
        var name = PersonaName(DisplayName) ?? PersonaName(fallbackName) ?? "Imported character";
        var character = DisplayName.Length > 0 ? DisplayName : name;
        string Fill(string text) => Clean(Macros(text, character));

        var sections = new[]
        {
            new Section("system prompt", "", Fill(SystemPrompt), 4),
            new Section("description", "", Fill(Description), 1),
            new Section("personality", "Personality: ", Fill(Personality), 2),
            new Section("scenario", "Scenario: ", Fill(Scenario), 3),
            new Section("always-on lore", "Background:\n", Fill(string.Join("\n\n", AlwaysOnLore)), 5),
            new Section("post-history instructions", "", Fill(PostHistoryInstructions), 6),
            new Section("example dialogue", "Example dialogue:\n", Fill(ExampleMessages), 7),
            new Section("greeting", "Greeting:\n", Fill(FirstMessage), 8)
        }.Where(section => section.Body.Length > 0).ToArray();

        const string Separator = "\n\n";
        var chosen = new string?[sections.Length];
        var characters = Math.Max(0, maximumCharacters);
        var bytes = Math.Max(0, maximumUtf8Bytes);
        var shortened = new List<string>();
        var leftOut = new List<string>();

        bool Whole(int index)
        {
            var full = sections[index].Label + sections[index].Body;
            var cost = Encoding.UTF8.GetByteCount(full) + Separator.Length;
            if (full.Length + Separator.Length > characters || cost > bytes) return false;
            chosen[index] = full;
            characters -= full.Length + Separator.Length;
            bytes -= cost;
            return true;
        }

        // Whole sections first, in priority order. Once an essential section (description, personality, scenario or system
        // prompt) doesn't fit whole, the optional ones wait so they can't take the room it needs.
        var deferred = new List<int>();
        var essentialWaiting = false;
        foreach (var index in Enumerable.Range(0, sections.Length).OrderBy(i => sections[i].Priority))
        {
            if ((essentialWaiting && sections[index].Priority > EssentialPriority) || !Whole(index))
            {
                deferred.Add(index);
                essentialWaiting |= sections[index].Priority <= EssentialPriority;
            }
        }
        foreach (var index in deferred)
        {
            if (Whole(index)) continue;
            var section = sections[index];
            var roomCharacters = characters - section.Label.Length - Separator.Length;
            var roomBytes = bytes - Encoding.UTF8.GetByteCount(section.Label) - Separator.Length;
            if (Math.Min(roomCharacters, roomBytes / 3) >= MinimumShortenedCharacters ||
                (section.Priority == 1 && roomCharacters > 1 && roomBytes > 3))
            {
                var cut = section.Label + Shorten(section.Body, roomCharacters, roomBytes);
                chosen[index] = cut;
                characters -= cut.Length + Separator.Length;
                bytes -= Encoding.UTF8.GetByteCount(cut) + Separator.Length;
                shortened.Add(section.Title);
            }
            else leftOut.Add(section.Title);
        }
        return new(name, string.Join(Separator, chosen.OfType<string>()), Ordered(shortened, sections), Ordered(leftOut, sections));
    }

    private const int EssentialPriority = 4;
    private const int MinimumShortenedCharacters = 240;

    private static IReadOnlyList<string> Ordered(List<string> titles, Section[] sections) =>
        sections.Select(section => section.Title).Where(titles.Contains).ToArray();

    /// <summary>A persona name: one line of visible characters, at most 64, or null when nothing is left.</summary>
    internal static string? PersonaName(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var builder = new StringBuilder();
        foreach (var c in RemoveLoneSurrogates(raw))
        {
            if (char.IsWhiteSpace(c)) { if (builder.Length > 0 && builder[^1] != ' ') builder.Append(' '); }
            else if (!char.IsControl(c)) builder.Append(c);
        }
        var name = builder.ToString().Trim();
        if (name.Length > PersonaProfile.MaximumNameCharacters)
        {
            var end = PersonaProfile.MaximumNameCharacters;
            if (char.IsHighSurrogate(name[end - 1])) end--;
            name = name[..end].TrimEnd();
        }
        return name.Length > 0 ? name : null;
    }

    /// <summary>Replaces the Tavern macros a card's text uses: <c>{{char}}</c>/<c>&lt;BOT&gt;</c> become the character's name,
    /// <c>{{user}}</c>/<c>&lt;USER&gt;</c> become "the user", and <c>{{original}}</c> and <c>&lt;START&gt;</c> markers are removed.</summary>
    internal static string Macros(string text, string character) => MacroPattern().Replace(text, match =>
    {
        var macro = match.Value.Trim('{', '}', '<', '>', ' ').ToLowerInvariant();
        return macro switch
        {
            "char" or "bot" => character,
            "user" => StartsSentence(text, match.Index) ? "The user" : "the user",
            _ => ""
        };
    });

    private static bool StartsSentence(string text, int index)
    {
        for (var i = index - 1; i >= 0; i--)
        {
            var c = text[i];
            if (c is ' ' or '\t' or '"' or '*' or '(' or '\u201C' or '_') continue;
            return c is '\n' or '\r' or '.' or '!' or '?';
        }
        return true;
    }

    /// <summary>Normalizes card text for a persona: Unix newlines, no control characters or lone surrogates, at most one blank
    /// line in a row and no surrounding whitespace.</summary>
    internal static string Clean(string text)
    {
        var builder = new StringBuilder(text.Length);
        foreach (var c in RemoveLoneSurrogates(text.Replace("\r\n", "\n").Replace('\r', '\n')))
            if (!char.IsControl(c) || c is '\n' or '\t') builder.Append(c);
        var lines = builder.ToString().Split('\n').Select(line => line.TrimEnd());
        return BlankLines().Replace(string.Join('\n', lines), "\n\n").Trim();
    }

    private static string RemoveLoneSurrogates(string text)
    {
        var builder = new StringBuilder(text.Length);
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (char.IsHighSurrogate(c) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
            {
                builder.Append(c).Append(text[++i]);
            }
            else if (!char.IsSurrogate(c)) builder.Append(c);
        }
        return builder.ToString();
    }

    /// <summary>Cuts text to fit, at a paragraph, sentence or word boundary near the limit, and marks the cut with an ellipsis.</summary>
    internal static string Shorten(string text, int maximumCharacters, int maximumUtf8Bytes)
    {
        var characters = maximumCharacters - 1;
        var bytes = maximumUtf8Bytes - 3;
        if (characters <= 0 || bytes <= 0) return "";
        var end = 0;
        foreach (var rune in text.EnumerateRunes())
        {
            if (end + rune.Utf16SequenceLength > characters || bytes - rune.Utf8SequenceLength < 0) break;
            end += rune.Utf16SequenceLength;
            bytes -= rune.Utf8SequenceLength;
        }
        if (end >= text.Length) return text;
        var cut = text[..end];
        var floor = cut.Length * 3 / 5;
        var boundary = cut.LastIndexOf("\n\n", StringComparison.Ordinal);
        if (boundary < floor) boundary = Math.Max(cut.LastIndexOf(". ", StringComparison.Ordinal),
            Math.Max(cut.LastIndexOf("! ", StringComparison.Ordinal), cut.LastIndexOf("? ", StringComparison.Ordinal))) + 1;
        if (boundary < floor) boundary = cut.LastIndexOfAny([' ', '\n', '\t']);
        if (boundary >= floor) cut = cut[..boundary];
        return cut.TrimEnd() + "\u2026";
    }

    [GeneratedRegex(@"\{\{\s*(?:char|user|original)\s*\}\}|<(?:bot|char|user|start)>", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex MacroPattern();

    [GeneratedRegex(@"\n{3,}", RegexOptions.CultureInvariant)]
    private static partial Regex BlankLines();
}

/// <summary>A card turned into a Martlet persona, with the sections that had to be shortened or left out to fit.</summary>
public sealed record CharacterCardPersona(string Name, string Text, IReadOnlyList<string> Shortened, IReadOnlyList<string> LeftOut);

/// <summary>A file that is not a usable character card, with a message for the user.</summary>
public sealed class CharacterCardException(string message) : Exception(message);

/// <summary>Reads character cards the way SillyTavern and Chub save them: a PNG/APNG image with the card in a <c>ccv3</c>
/// (preferred) or <c>chara</c> text chunk as base64 UTF-8 JSON, a plain JSON card, or a CHARX archive's <c>card.json</c>.
/// Image pixels are never decoded; non-text PNG chunks are skipped.</summary>
public static class CharacterCardReader
{
    public const int MaximumCardBytes = 8 * 1024 * 1024;

    internal const string NoCardInImage =
        "This image has no character card inside it. Character card PNGs from SillyTavern or Chub carry the character's details " +
        "in the file; a resaved, converted or screenshotted copy loses them. Use the original PNG, or its JSON or CHARX export.";
    internal const string Unsupported =
        "This isn't a character card. Choose a PNG card image, a JSON card or a CHARX file from SillyTavern, Chub (CharacterHub), " +
        "RisuAI or another Tavern-compatible app.";
    internal const string Damaged =
        "This character card's data is damaged or unreadable. Download the card again, or use its JSON or CHARX export.";

    private static readonly byte[] PngSignature = [137, 80, 78, 71, 13, 10, 26, 10];

    public static CharacterCard Read(Stream input, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        Span<byte> head = stackalloc byte[12];
        var read = ReadAtMost(input, head);
        var start = head[..read];
        input.Seek(0, SeekOrigin.Begin);
        if (start.StartsWith(PngSignature))
            return ParseFirst(ReadPng(input, token));
        if (start.StartsWith("PK\u0003\u0004"u8))
            return ParseFirst([ReadCharx(input, token)]);
        if (start.StartsWith((ReadOnlySpan<byte>)[0xFF, 0xD8, 0xFF]) || (read == 12 && start[..4].SequenceEqual("RIFF"u8) && start[8..12].SequenceEqual("WEBP"u8)))
            throw new CharacterCardException("Only PNG card images carry a character card. Download the card as PNG, JSON or CHARX instead.");
        if (input.Length > MaximumCardBytes) throw new CharacterCardException(Unsupported);
        return ParseFirst([ReadBounded(input, token)]);
    }

    private static int ReadAtMost(Stream input, Span<byte> buffer)
    {
        var total = 0;
        while (total < buffer.Length)
        {
            var read = input.Read(buffer[total..]);
            if (read == 0) break;
            total += read;
        }
        return total;
    }

    private static CharacterCard ParseFirst(IReadOnlyList<byte[]> payloads)
    {
        CharacterCardException? rejected = null;
        foreach (var payload in payloads)
        {
            try { return Parse(Decode(payload)); }
            catch (CharacterCardException error) { rejected ??= error; }
            catch (Exception error) when (error is JsonException or FormatException) { }
        }
        throw rejected ?? new CharacterCardException(Damaged);
    }

    /// <summary>The card candidates in a PNG, best first: the <c>ccv3</c> chunk, then the <c>chara</c> chunk.</summary>
    private static IReadOnlyList<byte[]> ReadPng(Stream input, CancellationToken token)
    {
        input.Seek(PngSignature.Length, SeekOrigin.Begin);
        byte[]? ccv3 = null, chara = null;
        var header = new byte[8];
        while (ReadAtMost(input, header) == header.Length)
        {
            token.ThrowIfCancellationRequested();
            var length = BinaryPrimitives.ReadUInt32BigEndian(header);
            var type = Encoding.ASCII.GetString(header, 4, 4);
            if (type == "IEND" || length > int.MaxValue) break;
            if (type is "tEXt" or "zTXt" or "iTXt" && length <= MaximumCardBytes)
            {
                var data = new byte[length];
                if (ReadAtMost(input, data) != data.Length) break;
                if (TextChunk(type, data) is { } chunk)
                {
                    if (chunk.Keyword == "ccv3") ccv3 ??= chunk.Value;
                    else chara ??= chunk.Value;
                }
            }
            else input.Seek(length, SeekOrigin.Current);
            input.Seek(4, SeekOrigin.Current);
        }
        var found = new[] { ccv3, chara }.OfType<byte[]>().ToArray();
        return found.Length > 0 ? found : throw new CharacterCardException(NoCardInImage);
    }

    private static (string Keyword, byte[] Value)? TextChunk(string type, byte[] data)
    {
        var separator = Array.IndexOf(data, (byte)0);
        if (separator <= 0) return null;
        var keyword = Encoding.Latin1.GetString(data, 0, separator);
        if (keyword is not ("ccv3" or "chara")) return null;
        var rest = data.AsSpan(separator + 1);
        switch (type)
        {
            case "tEXt":
                return (keyword, rest.ToArray());
            case "zTXt":
                return rest.Length > 1 && rest[0] == 0 ? (keyword, Inflate(rest[1..].ToArray())) : null;
            default:
                if (rest.Length < 2) return null;
                var compressed = rest[0] == 1;
                rest = rest[2..];
                for (var skip = 0; skip < 2; skip++)
                {
                    var end = rest.IndexOf((byte)0);
                    if (end < 0) return null;
                    rest = rest[(end + 1)..];
                }
                return (keyword, compressed ? Inflate(rest.ToArray()) : rest.ToArray());
        }
    }

    private static byte[] Inflate(byte[] compressed)
    {
        using var inflater = new ZLibStream(new MemoryStream(compressed), CompressionMode.Decompress);
        return ReadBounded(inflater, CancellationToken.None);
    }

    private static byte[] ReadCharx(Stream input, CancellationToken token)
    {
        using var archive = new ZipArchive(input, ZipArchiveMode.Read, leaveOpen: true);
        var entry = archive.GetEntry("card.json")
            ?? throw new CharacterCardException("This CHARX file has no card.json, so it holds no character card.");
        if (entry.Length > MaximumCardBytes)
            throw new CharacterCardException("This character card is too large to import (over 8 MB of card text).");
        using var stream = entry.Open();
        return ReadBounded(stream, token);
    }

    private static byte[] ReadBounded(Stream input, CancellationToken token)
    {
        using var output = new MemoryStream();
        var buffer = new byte[81_920];
        int read;
        while ((read = input.Read(buffer)) > 0)
        {
            token.ThrowIfCancellationRequested();
            output.Write(buffer, 0, read);
            if (output.Length > MaximumCardBytes)
                throw new CharacterCardException("This character card is too large to import (over 8 MB of card text).");
        }
        return output.ToArray();
    }

    /// <summary>The UTF-8 JSON in a payload: PNG chunks hold base64 (some older tools wrote the JSON itself).</summary>
    private static byte[] Decode(byte[] payload)
    {
        var text = payload.AsSpan();
        if (text.StartsWith((ReadOnlySpan<byte>)[0xEF, 0xBB, 0xBF])) text = text[3..];
        text = text.Trim(" \t\r\n"u8);
        if (text.Length > 0 && text[0] == (byte)'{') return text.ToArray();
        var base64 = new StringBuilder(text.Length + 3);
        foreach (var b in text)
        {
            var c = (char)b;
            if (char.IsWhiteSpace(c)) continue;
            base64.Append(c switch { '-' => '+', '_' => '/', _ => c });
        }
        while (base64.Length % 4 != 0) base64.Append('=');
        var decoded = Convert.FromBase64String(base64.ToString());
        return decoded.AsSpan().StartsWith((ReadOnlySpan<byte>)[0xEF, 0xBB, 0xBF]) ? decoded[3..] : decoded;
    }

    internal static CharacterCard Parse(byte[] json)
    {
        using var document = JsonDocument.Parse(json, new JsonDocumentOptions
        {
            AllowTrailingCommas = true,
            CommentHandling = JsonCommentHandling.Skip,
            MaxDepth = 64
        });
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object) throw new CharacterCardException(Unsupported);
        var spec = Text(root, "spec");
        var hasData = root.TryGetProperty("data", out var nested) && nested.ValueKind == JsonValueKind.Object;
        var format = spec switch
        {
            "chara_card_v3" => CharacterCardFormat.CharacterCardV3,
            "chara_card_v2" => CharacterCardFormat.CharacterCardV2,
            _ when hasData && nested.TryGetProperty("name", out _) => CharacterCardFormat.CharacterCardV2,
            _ => CharacterCardFormat.TavernV1
        };
        var data = format != CharacterCardFormat.TavernV1 && hasData ? nested : root;

        var lore = new List<string>();
        var keywordLore = 0;
        Lorebooks.LorebookImport? lorebook = null;
        if (data.TryGetProperty("character_book", out var book) && book.ValueKind == JsonValueKind.Object &&
            book.TryGetProperty("entries", out var entries) && entries.ValueKind == JsonValueKind.Array)
        {
            foreach (var entry in entries.EnumerateArray())
            {
                if (entry.ValueKind != JsonValueKind.Object || !Flag(entry, "enabled", true)) continue;
                var content = Text(entry, "content").Trim();
                if (content.Length == 0) continue;
                if (Flag(entry, "constant", false)) lore.Add(content);
                else keywordLore++;
            }
            var nickname = Text(data, "nickname").Trim();
            lorebook = Lorebooks.SillyTavernLorebooks.FromCard(book, nickname.Length > 0 ? nickname : First(data, "name", "char_name").Trim());
        }

        var card = new CharacterCard
        {
            Format = format,
            Name = First(data, "name", "char_name").Trim(),
            Nickname = Text(data, "nickname").Trim(),
            Creator = Text(data, "creator").Trim(),
            Description = First(data, "description", "char_persona"),
            Personality = Text(data, "personality"),
            Scenario = First(data, "scenario", "world_scenario"),
            FirstMessage = First(data, "first_mes", "char_greeting"),
            ExampleMessages = First(data, "mes_example", "example_dialogue"),
            SystemPrompt = Text(data, "system_prompt"),
            PostHistoryInstructions = Text(data, "post_history_instructions"),
            AlwaysOnLore = lore,
            KeywordLoreEntries = keywordLore,
            Lorebook = lorebook
        };
        if (card.Name.Length == 0 && card.Description.Trim().Length == 0 && card.Personality.Trim().Length == 0)
            throw new CharacterCardException(Unsupported);
        return card;
    }

    private static string Text(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";

    private static string First(JsonElement element, string property, string legacy)
    {
        var value = Text(element, property);
        return value.Trim().Length > 0 ? value : Text(element, legacy);
    }

    private static bool Flag(JsonElement element, string property, bool fallback) =>
        element.TryGetProperty(property, out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? value.GetBoolean() : fallback;
}
