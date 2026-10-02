using System.Text.Json.Serialization;
using Martlet.Core.Contracts;

namespace Martlet.Core.Lorebooks;

/// <summary>How an entry's optional secondary keys filter a primary-key match (SillyTavern's selective logic, same order).</summary>
public enum LorebookSecondaryLogic { AndAny, NotAll, NotAny, AndAll }

/// <summary>Where a triggered entry goes in the instructions, relative to the persona.</summary>
public enum LorebookPosition { BeforePersona, AfterPersona }

/// <summary>Which conversations use a lorebook: none, every persona, or only the listed personas.</summary>
public enum LorebookActivation { Off, AllPersonas, SelectedPersonas }

/// <summary>One World Info entry: when a key appears in the scanned conversation, its content is added for that reply.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Skip)]
public sealed record LorebookEntry : IContract
{
    public const int MaximumTitleCharacters = 256;
    public const int MaximumContentCharacters = 32_768;
    public const int MaximumKeys = 64;
    public const int MaximumKeyCharacters = 256;

    public required int Uid { get; init; }
    public string Title { get; init; } = "";
    public IReadOnlyList<string> Keys { get; init; } = [];
    public IReadOnlyList<string> SecondaryKeys { get; init; } = [];
    public LorebookSecondaryLogic SecondaryLogic { get; init; } = LorebookSecondaryLogic.AndAny;
    public string Content { get; init; } = "";
    /// <summary>Always included while the lorebook is on, without any key.</summary>
    public bool Constant { get; init; }
    public bool Enabled { get; init; } = true;
    /// <summary>Higher orders win the budget first and are placed later (closer to the reply).</summary>
    public int Order { get; init; } = 100;
    public LorebookPosition Position { get; init; } = LorebookPosition.AfterPersona;
    /// <summary>Chance (0-100) that a matched entry is actually included.</summary>
    public int Probability { get; init; } = 100;
    /// <summary>Null uses the library setting.</summary>
    public bool? CaseSensitive { get; init; }
    /// <summary>Null uses the library setting.</summary>
    public bool? MatchWholeWords { get; init; }
    /// <summary>Null uses the library setting.</summary>
    public int? ScanDepth { get; init; }
    /// <summary>Only the conversation can trigger this entry, never another entry's content.</summary>
    public bool ExcludeRecursion { get; init; }
    /// <summary>This entry's content never triggers other entries.</summary>
    public bool PreventRecursion { get; init; }

    [JsonIgnore]
    public string Label => Title.Length > 0 ? Title : Keys.Count > 0 ? string.Join(", ", Keys) : $"Entry {Uid}";

    public void Validate()
    {
        ContractRules.Require(Uid >= 0, "Lorebook entry ids must not be negative.");
        ContractRules.Text(Title, MaximumTitleCharacters);
        ContractRules.Text(Content, MaximumContentCharacters);
        ValidateKeys(Keys, "keys");
        ValidateKeys(SecondaryKeys, "secondary keys");
        ContractRules.Defined(SecondaryLogic);
        ContractRules.Defined(Position);
        ContractRules.Require(Probability is >= 0 and <= 100, "Lorebook entry probability must be 0-100.");
        ContractRules.Require(Order is >= -1_000_000 and <= 1_000_000, "Lorebook entry order must be between -1,000,000 and 1,000,000.");
        ContractRules.Require(ScanDepth is null or >= 0 and <= LorebookLibrary.MaximumScanDepth,
            $"Lorebook entry scan depth must be 0-{LorebookLibrary.MaximumScanDepth} messages.");
    }

    private static void ValidateKeys(IReadOnlyList<string>? keys, string name)
    {
        ContractRules.Require(keys is { Count: <= MaximumKeys }, $"A lorebook entry has at most {MaximumKeys} {name}.");
        foreach (var key in keys!)
        {
            ContractRules.Text(key, MaximumKeyCharacters);
            ContractRules.Require(!string.IsNullOrWhiteSpace(key) && !key.Contains('\n') && !key.Contains('\r'),
                $"Lorebook {name} must be nonempty single lines.");
        }
    }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Skip)]
public sealed record Lorebook : IContract
{
    public const int MaximumNameCharacters = 128;
    public const int MaximumDescriptionCharacters = 4_096;
    public const int MaximumEntries = 2_000;
    public const int MaximumPersonas = 64;

    public required Guid Id { get; init; }
    public required string Name { get; init; }
    public string Description { get; init; } = "";
    public LorebookActivation Activation { get; init; } = LorebookActivation.AllPersonas;
    /// <summary>The personas that use this lorebook when <see cref="Activation"/> is SelectedPersonas.</summary>
    public IReadOnlyList<Guid> PersonaIds { get; init; } = [];
    public IReadOnlyList<LorebookEntry> Entries { get; init; } = [];

    public bool AppliesTo(Guid? persona) => Activation switch
    {
        LorebookActivation.AllPersonas => true,
        LorebookActivation.SelectedPersonas => persona is { } id && PersonaIds.Contains(id),
        _ => false
    };

    public void Validate()
    {
        ContractRules.Require(Id != Guid.Empty, "A lorebook requires a nonempty identity.");
        ContractRules.Require(Name is { Length: > 0 and <= MaximumNameCharacters } && Name == Name.Trim() && !Name.Any(char.IsControl),
            $"Lorebook names must be 1-{MaximumNameCharacters} visible characters without leading or trailing spaces.");
        ContractRules.Text(Description, MaximumDescriptionCharacters);
        ContractRules.Defined(Activation);
        ContractRules.Require(PersonaIds is { Count: <= MaximumPersonas } && PersonaIds.All(id => id != Guid.Empty) &&
            PersonaIds.Distinct().Count() == PersonaIds.Count, $"A lorebook lists at most {MaximumPersonas} distinct personas.");
        ContractRules.Require(Entries is { Count: <= MaximumEntries }, $"A lorebook has at most {MaximumEntries} entries.");
        var uids = new HashSet<int>();
        foreach (var entry in Entries!)
        {
            ContractRules.Require(entry is not null, "A lorebook entry cannot be null.");
            entry!.Validate();
            ContractRules.Require(uids.Add(entry.Uid), $"Lorebook '{Name}' has two entries with id {entry.Uid}.");
        }
    }
}

/// <summary>Every lorebook on this PC plus the World Info scan settings they share.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Skip)]
public sealed record LorebookLibrary : IContract
{
    public const int CurrentSchemaVersion = 1;
    public const int MaximumBooks = 64;
    public const int MaximumScanDepth = 17;
    public const int MinimumBudgetUtf8Bytes = 256;
    public const int MaximumBudgetUtf8Bytes = 12_288;
    public const int DefaultBudgetUtf8Bytes = 4_096;

    public required int SchemaVersion { get; init; }
    /// <summary>How many of the latest messages are searched for keys, counting the one being answered.</summary>
    public int ScanDepth { get; init; } = 2;
    /// <summary>Most UTF-8 bytes of triggered entry content added to one reply's instructions.</summary>
    public int BudgetUtf8Bytes { get; init; } = DefaultBudgetUtf8Bytes;
    /// <summary>Triggered entries' content can trigger further entries.</summary>
    public bool Recursive { get; init; } = true;
    public bool CaseSensitive { get; init; }
    public bool MatchWholeWords { get; init; } = true;
    public IReadOnlyList<Lorebook> Books { get; init; } = [];

    public static LorebookLibrary Create() => new() { SchemaVersion = CurrentSchemaVersion };

    public void Validate()
    {
        ContractRules.Require(SchemaVersion == CurrentSchemaVersion,
            "This lorebook file was saved by a newer Martlet. Update Martlet to use it; the file was not changed.", ErrorCode.UnsupportedVersion);
        ContractRules.Require(ScanDepth is >= 0 and <= MaximumScanDepth, $"Scan depth must be 0-{MaximumScanDepth} messages.");
        ContractRules.Require(BudgetUtf8Bytes is >= MinimumBudgetUtf8Bytes and <= MaximumBudgetUtf8Bytes,
            $"The lorebook budget must be {MinimumBudgetUtf8Bytes:N0}-{MaximumBudgetUtf8Bytes:N0} UTF-8 bytes.");
        ContractRules.Require(Books is { Count: <= MaximumBooks }, $"At most {MaximumBooks} lorebooks are supported.");
        var ids = new HashSet<Guid>();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var book in Books!)
        {
            ContractRules.Require(book is not null, "A lorebook cannot be null.");
            book!.Validate();
            ContractRules.Require(ids.Add(book.Id), "Lorebook identities must be unique.");
            ContractRules.Require(names.Add(book.Name), $"Two lorebooks are named '{book.Name}'. Lorebook names must be unique.");
        }
    }

    /// <summary>A lorebook name not used yet, based on <paramref name="basis"/>.</summary>
    public string UniqueName(string? basis, Guid? except = null)
    {
        var clean = string.Join(' ', new string((basis ?? "").Select(c => char.IsControl(c) ? ' ' : c).ToArray())
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (clean.Length == 0) clean = "Lorebook";
        for (var index = 1; ; index++)
        {
            var suffix = index == 1 ? "" : $" {index}";
            var candidate = clean[..Math.Min(clean.Length, Lorebook.MaximumNameCharacters - suffix.Length)].TrimEnd() + suffix;
            if (!Books.Any(book => book.Id != except && string.Equals(book.Name, candidate, StringComparison.OrdinalIgnoreCase)))
                return candidate;
        }
    }
}
