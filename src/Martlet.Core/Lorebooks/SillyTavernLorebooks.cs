using System.Text;
using System.Text.Json;
using Martlet.Core.Contracts;
using Martlet.Core.Settings;

namespace Martlet.Core.Lorebooks;

/// <summary>A lorebook read from a file, with how many entries could not be used.</summary>
public sealed record LorebookImport(Lorebook Book, int SkippedEntries, string Format);

/// <summary>Reads SillyTavern World Info files, character cards with an embedded character book (PNG, JSON or CHARX, read by
/// <see cref="CharacterCardReader"/>) and NovelAI lorebooks, and writes SillyTavern World Info JSON.</summary>
public static class SillyTavernLorebooks
{
    public const int MaximumFileBytes = 32 * 1024 * 1024;
    private static readonly byte[] PngSignature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    /// <summary>Reads a lorebook file. Throws <see cref="ContractException"/> with a message for the user when it has none.</summary>
    public static LorebookImport Import(Stream input, string fallbackName, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        Span<byte> head = stackalloc byte[8];
        var read = input.ReadAtLeast(head, head.Length, throwOnEndOfStream: false);
        input.Seek(0, SeekOrigin.Begin);
        if (head[..read].StartsWith(PngSignature) || head[..read].StartsWith("PK\u0003\u0004"u8))
        {
            CharacterCard card;
            try { card = CharacterCardReader.Read(input, token); }
            catch (CharacterCardException error) { throw new ContractException(ErrorCode.InvalidContract, error.Message); }
            catch (Exception error) when (error is JsonException or FormatException or InvalidDataException)
            {
                throw new ContractException(ErrorCode.InvalidContract, CharacterCardReader.Damaged);
            }
            return card.Lorebook ?? throw new ContractException(ErrorCode.InvalidContract,
                $"The character card \"{card.DisplayName}\" has no lorebook inside it.");
        }
        ContractRules.Require(input.Length is > 0 and <= MaximumFileBytes,
            $"Choose a lorebook file of at most {MaximumFileBytes / (1024 * 1024)} MB.", ErrorCode.PayloadTooLarge);
        var bytes = new byte[input.Length];
        input.ReadExactly(bytes);
        var json = bytes.AsSpan();
        if (json.StartsWith("\uFEFF"u8)) json = json[3..];
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json.ToArray(), new JsonDocumentOptions
            {
                AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip, MaxDepth = 64
            });
        }
        catch (JsonException)
        {
            throw new ContractException(ErrorCode.InvalidContract,
                "That file isn't a lorebook. Choose a SillyTavern World Info JSON, or a character card (PNG, JSON or CHARX) with a lorebook.");
        }
        using (document)
            return FromJson(document.RootElement, fallbackName);
    }

    /// <summary>The lorebook embedded in a character card's <c>character_book</c>, or null when it has no usable entries.</summary>
    internal static LorebookImport? FromCard(JsonElement book, string characterName)
    {
        try
        {
            var name = Str(book, "name") is { Length: > 0 } named ? named
                : characterName.Length > 0 ? characterName + " lore" : "Character lore";
            var import = CharacterBook(book, name, "character card");
            return import.Book.Entries.Count > 0 ? import : null;
        }
        catch (ContractException) { return null; }
    }

    private static LorebookImport FromJson(JsonElement root, string fallbackName)
    {
        ContractRules.Require(root.ValueKind == JsonValueKind.Object,
            "That file isn't a SillyTavern lorebook (World Info) or a character card with a lorebook.");
        if (Prop(root, "data") is { ValueKind: JsonValueKind.Object } data)
        {
            var character = Str(data, "nickname") is { Length: > 0 } nickname ? nickname : Str(data, "name");
            if (Prop(data, "character_book") is { ValueKind: JsonValueKind.Object } cardBook)
                return CharacterBook(cardBook, Str(cardBook, "name") is { Length: > 0 } named ? named
                    : character is { Length: > 0 } ? character + " lore" : fallbackName, "character card");
            throw new ContractException(ErrorCode.InvalidContract,
                $"The character card{(character is { Length: > 0 } ? $" \"{Clean(character, 64, false)}\"" : "")} has no lorebook inside it.");
        }
        if (Prop(root, "character_book") is { ValueKind: JsonValueKind.Object } book)
            return CharacterBook(book, Str(book, "name") is { Length: > 0 } named ? named
                : Str(root, "name") is { Length: > 0 } character ? character + " lore" : fallbackName, "character card");
        if (Prop(root, "entries") is not { } entries)
            throw new ContractException(ErrorCode.InvalidContract,
                "That file isn't a SillyTavern lorebook (World Info) or a character card with a lorebook.");
        var name = Str(root, "name") is { Length: > 0 } rootName ? rootName : fallbackName;
        if (entries.ValueKind == JsonValueKind.Object)
            return WorldInfo(entries.EnumerateObject().Select(item => item.Value), name, Str(root, "description"));
        ContractRules.Require(entries.ValueKind == JsonValueKind.Array, "The lorebook's entries are unreadable.");
        var first = entries.EnumerateArray().FirstOrDefault(item => item.ValueKind == JsonValueKind.Object);
        if (first.ValueKind == JsonValueKind.Object && Prop(first, "keys") is not null && Prop(first, "key") is null)
            return Prop(first, "content") is null && Prop(first, "text") is not null
                ? NovelAi(entries, name)
                : CharacterBook(root, name, "character book");
        return WorldInfo(entries.EnumerateArray(), name, Str(root, "description"));
    }

    private static LorebookImport WorldInfo(IEnumerable<JsonElement> source, string name, string? description)
    {
        var entries = new List<LorebookEntry>();
        var skipped = 0;
        foreach (var item in source)
        {
            if (item.ValueKind != JsonValueKind.Object) { skipped++; continue; }
            var selective = Bool(item, "selective") ?? true;
            var probability = Bool(item, "useProbability") ?? true ? Int(item, "probability") ?? 100 : 100;
            Add(entries, ref skipped, new()
            {
                Uid = Int(item, "uid") ?? entries.Count,
                Title = Str(item, "comment") ?? "",
                Keys = Strings(item, "key"),
                SecondaryKeys = selective ? Strings(item, "keysecondary") : [],
                SecondaryLogic = Logic(Int(item, "selectiveLogic")),
                Content = Str(item, "content") ?? "",
                Constant = Bool(item, "constant") ?? false,
                Enabled = !(Bool(item, "disable") ?? false),
                Order = Int(item, "order") ?? 100,
                Position = Int(item, "position") == 0 ? LorebookPosition.BeforePersona : LorebookPosition.AfterPersona,
                Probability = probability,
                CaseSensitive = Bool(item, "caseSensitive"),
                MatchWholeWords = Bool(item, "matchWholeWords"),
                ScanDepth = Int(item, "scanDepth"),
                ExcludeRecursion = Bool(item, "excludeRecursion") ?? false,
                PreventRecursion = Bool(item, "preventRecursion") ?? false
            });
        }
        return Finish(name, description, entries, skipped, "SillyTavern World Info");
    }

    private static LorebookImport CharacterBook(JsonElement book, string name, string format)
    {
        var entries = new List<LorebookEntry>();
        var skipped = 0;
        if (Prop(book, "entries") is { ValueKind: JsonValueKind.Array } source)
            foreach (var item in source.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) { skipped++; continue; }
                var extensions = Prop(item, "extensions") is { ValueKind: JsonValueKind.Object } found ? found : default;
                var regex = Bool(item, "use_regex") ?? false;
                var keys = Strings(item, "keys");
                var secondary = Bool(item, "selective") ?? false ? Strings(item, "secondary_keys") : [];
                var useProbability = Bool(extensions, "useProbability") ?? true;
                var position = Int(extensions, "position") is { } numeric
                    ? numeric == 0 ? LorebookPosition.BeforePersona : LorebookPosition.AfterPersona
                    : Str(item, "position") == "before_char" ? LorebookPosition.BeforePersona : LorebookPosition.AfterPersona;
                Add(entries, ref skipped, new()
                {
                    Uid = Int(item, "id") ?? entries.Count,
                    Title = Str(item, "comment") is { Length: > 0 } comment ? comment : Str(item, "name") ?? "",
                    Keys = regex ? keys.Select(AsRegex).ToArray() : keys,
                    SecondaryKeys = regex ? secondary.Select(AsRegex).ToArray() : secondary,
                    SecondaryLogic = Logic(Int(extensions, "selectiveLogic")),
                    Content = Str(item, "content") ?? "",
                    Constant = Bool(item, "constant") ?? false,
                    Enabled = Bool(item, "enabled") ?? true,
                    Order = Int(item, "insertion_order") ?? 100,
                    Position = position,
                    Probability = useProbability ? Int(extensions, "probability") ?? 100 : 100,
                    CaseSensitive = Bool(item, "case_sensitive") ?? Bool(extensions, "case_sensitive"),
                    MatchWholeWords = Bool(extensions, "match_whole_words"),
                    ScanDepth = Int(extensions, "scan_depth"),
                    ExcludeRecursion = Bool(extensions, "exclude_recursion") ?? false,
                    PreventRecursion = Bool(extensions, "prevent_recursion") ?? false
                });
            }
        return Finish(name, Str(book, "description"), entries, skipped, format);
    }

    private static LorebookImport NovelAi(JsonElement source, string name)
    {
        var entries = new List<LorebookEntry>();
        var skipped = 0;
        foreach (var item in source.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) { skipped++; continue; }
            Add(entries, ref skipped, new()
            {
                Uid = entries.Count,
                Title = Str(item, "displayName") ?? "",
                Keys = Strings(item, "keys"),
                Content = Str(item, "text") ?? "",
                Enabled = Bool(item, "enabled") ?? true
            });
        }
        return Finish(name, null, entries, skipped, "NovelAI lorebook");
    }

    private static string AsRegex(string key) => LorebookScanner.IsRegexKey(key) ? key : "/" + key + "/i";

    private static void Add(List<LorebookEntry> entries, ref int skipped, LorebookEntry raw)
    {
        var content = Clean(raw.Content, int.MaxValue, true).Trim();
        var keys = Keys(raw.Keys);
        if (entries.Count >= Lorebook.MaximumEntries || content.Length is 0 or > LorebookEntry.MaximumContentCharacters ||
            keys.Length == 0 && !raw.Constant)
        {
            skipped++;
            return;
        }
        var uid = raw.Uid >= 0 && entries.All(entry => entry.Uid != raw.Uid) ? raw.Uid
            : entries.Count == 0 ? 0 : entries.Max(entry => entry.Uid) + 1;
        var entry = raw with
        {
            Uid = uid,
            Title = Clean(raw.Title, LorebookEntry.MaximumTitleCharacters, false).Trim(),
            Keys = keys,
            SecondaryKeys = Keys(raw.SecondaryKeys),
            SecondaryLogic = Enum.IsDefined(raw.SecondaryLogic) ? raw.SecondaryLogic : LorebookSecondaryLogic.AndAny,
            Content = content,
            Order = Math.Clamp(raw.Order, -1_000_000, 1_000_000),
            Probability = Math.Clamp(raw.Probability, 0, 100),
            ScanDepth = raw.ScanDepth is { } depth ? Math.Clamp(depth, 0, LorebookLibrary.MaximumScanDepth) : null
        };
        entry.Validate();
        entries.Add(entry);
    }

    private static string[] Keys(IEnumerable<string> keys) => keys
        .Select(key => Clean(key, LorebookEntry.MaximumKeyCharacters, false).Trim())
        .Where(key => key.Length > 0).Distinct(StringComparer.Ordinal).Take(LorebookEntry.MaximumKeys).ToArray();

    private static LorebookImport Finish(string name, string? description, List<LorebookEntry> entries, int skipped, string format)
    {
        var cleanName = Clean(name, Lorebook.MaximumNameCharacters, false).Trim();
        var book = new Lorebook
        {
            Id = Guid.NewGuid(),
            Name = cleanName.Length == 0 ? "Imported lorebook" : cleanName,
            Description = Clean(description ?? "", Lorebook.MaximumDescriptionCharacters, true).Trim(),
            Activation = LorebookActivation.AllPersonas,
            Entries = entries
        };
        book.Validate();
        return new(book, skipped, format);
    }

    /// <summary>SillyTavern World Info JSON that SillyTavern imports as a lorebook.</summary>
    public static byte[] Export(Lorebook book)
    {
        ArgumentNullException.ThrowIfNull(book);
        book.Validate();
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
        {
            writer.WriteStartObject();
            writer.WriteString("name", book.Name);
            if (book.Description.Length > 0) writer.WriteString("description", book.Description);
            writer.WriteStartObject("entries");
            for (var index = 0; index < book.Entries.Count; index++)
            {
                var entry = book.Entries[index];
                writer.WriteStartObject(entry.Uid.ToString(System.Globalization.CultureInfo.InvariantCulture));
                writer.WriteNumber("uid", entry.Uid);
                WriteStrings(writer, "key", entry.Keys);
                WriteStrings(writer, "keysecondary", entry.SecondaryKeys);
                writer.WriteString("comment", entry.Title);
                writer.WriteString("content", entry.Content);
                writer.WriteBoolean("constant", entry.Constant);
                writer.WriteBoolean("vectorized", false);
                writer.WriteBoolean("selective", true);
                writer.WriteNumber("selectiveLogic", (int)entry.SecondaryLogic);
                writer.WriteBoolean("addMemo", true);
                writer.WriteNumber("order", entry.Order);
                writer.WriteNumber("position", entry.Position == LorebookPosition.BeforePersona ? 0 : 1);
                writer.WriteBoolean("disable", !entry.Enabled);
                writer.WriteBoolean("excludeRecursion", entry.ExcludeRecursion);
                writer.WriteBoolean("preventRecursion", entry.PreventRecursion);
                writer.WriteBoolean("delayUntilRecursion", false);
                writer.WriteNumber("probability", entry.Probability);
                writer.WriteBoolean("useProbability", true);
                writer.WriteNumber("depth", 4);
                writer.WriteString("group", "");
                writer.WriteBoolean("groupOverride", false);
                writer.WriteNumber("groupWeight", 100);
                WriteNullable(writer, "scanDepth", entry.ScanDepth);
                WriteNullable(writer, "caseSensitive", entry.CaseSensitive);
                WriteNullable(writer, "matchWholeWords", entry.MatchWholeWords);
                writer.WriteNull("useGroupScoring");
                writer.WriteString("automationId", "");
                writer.WriteNull("role");
                writer.WriteNumber("sticky", 0);
                writer.WriteNumber("cooldown", 0);
                writer.WriteNumber("delay", 0);
                writer.WriteNumber("displayIndex", index);
                writer.WriteEndObject();
            }
            writer.WriteEndObject();
            writer.WriteEndObject();
        }
        return stream.ToArray();
    }

    private static void WriteStrings(Utf8JsonWriter writer, string name, IEnumerable<string> values)
    {
        writer.WriteStartArray(name);
        foreach (var value in values) writer.WriteStringValue(value);
        writer.WriteEndArray();
    }

    private static void WriteNullable(Utf8JsonWriter writer, string name, int? value)
    {
        if (value is { } number) writer.WriteNumber(name, number);
        else writer.WriteNull(name);
    }

    private static void WriteNullable(Utf8JsonWriter writer, string name, bool? value)
    {
        if (value is { } flag) writer.WriteBoolean(name, flag);
        else writer.WriteNull(name);
    }

    private static LorebookSecondaryLogic Logic(int? value) => value is >= 0 and <= 3 ? (LorebookSecondaryLogic)value.Value : LorebookSecondaryLogic.AndAny;

    private static string Clean(string? text, int maximum, bool multiline)
    {
        if (string.IsNullOrEmpty(text)) return "";
        var builder = new StringBuilder(Math.Min(text.Length, maximum));
        foreach (var c in text)
        {
            if (builder.Length >= maximum) break;
            if (c is '\n' or '\r' or '\t') builder.Append(multiline ? c : ' ');
            else if (!char.IsControl(c)) builder.Append(c);
        }
        if (builder.Length > 0 && char.IsHighSurrogate(builder[^1])) builder.Length--;
        return builder.ToString();
    }

    private static JsonElement? Prop(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) &&
        value.ValueKind is not JsonValueKind.Null and not JsonValueKind.Undefined ? value : null;

    private static string? Str(JsonElement element, string name) => Prop(element, name) switch
    {
        { ValueKind: JsonValueKind.String } value => value.GetString(),
        { ValueKind: JsonValueKind.Number } value => value.GetRawText(),
        _ => null
    };

    private static bool? Bool(JsonElement element, string name) => Prop(element, name) switch
    {
        { ValueKind: JsonValueKind.True } => true,
        { ValueKind: JsonValueKind.False } => false,
        { ValueKind: JsonValueKind.Number } value => value.TryGetDouble(out var number) && number != 0,
        { ValueKind: JsonValueKind.String } value => bool.TryParse(value.GetString(), out var flag) ? flag : null,
        _ => null
    };

    private static int? Int(JsonElement element, string name) => Prop(element, name) switch
    {
        { ValueKind: JsonValueKind.Number } value when value.TryGetDouble(out var number) && double.IsFinite(number) =>
            (int)Math.Clamp(Math.Round(number), int.MinValue, int.MaxValue),
        { ValueKind: JsonValueKind.String } value when double.TryParse(value.GetString(), System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out var number) && double.IsFinite(number) =>
            (int)Math.Clamp(Math.Round(number), int.MinValue, int.MaxValue),
        _ => null
    };

    private static string[] Strings(JsonElement element, string name) => Prop(element, name) switch
    {
        { ValueKind: JsonValueKind.Array } array => array.EnumerateArray()
            .Where(item => item.ValueKind is JsonValueKind.String or JsonValueKind.Number)
            .Select(item => item.ValueKind == JsonValueKind.String ? item.GetString()! : item.GetRawText()).ToArray(),
        { ValueKind: JsonValueKind.String } value => value.GetString()!.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries),
        _ => []
    };
}
