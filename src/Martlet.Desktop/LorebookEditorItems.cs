using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using Martlet.Core.Contracts;
using Martlet.Core.Lorebooks;
using Martlet.Core.Settings;

namespace Martlet.Desktop;

internal abstract class EditorItem : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null, params string[] also)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        Raise(name!);
        foreach (var other in also) Raise(other);
        return true;
    }

    protected void Raise(string name) => PropertyChanged?.Invoke(this, new(name));
}

/// <summary>One persona's checkbox for a lorebook used only with selected personas.</summary>
internal sealed class LorebookPersonaChoice(Guid id, string name, bool isChecked) : EditorItem
{
    private bool isChecked = isChecked;
    public Guid Id { get; } = id;
    public string Name { get; } = name;
    public bool IsChecked { get => isChecked; set => Set(ref isChecked, value); }
}

/// <summary>An editable lorebook entry. Text fields stay as typed until the library is built, which reports what is invalid.</summary>
internal sealed class LorebookEntryItem : EditorItem
{
    private string title = "", keys = "", secondaryKeys = "", content = "", order = "100", probability = "100", scanDepth = "";
    private bool enabled = true, constant, excludeRecursion, preventRecursion;
    private bool? caseSensitive, wholeWords;
    private int logicIndex, positionIndex = (int)LorebookPosition.AfterPersona;

    public int Uid { get; init; }
    public string Title { get => title; set => Set(ref title, value, nameof(Display)); }
    public string Keys { get => keys; set => Set(ref keys, value, nameof(Display)); }
    public string SecondaryKeys { get => secondaryKeys; set => Set(ref secondaryKeys, value); }
    public string Content { get => content; set => Set(ref content, value, nameof(Size)); }
    public string Order { get => order; set => Set(ref order, value); }
    public string Probability { get => probability; set => Set(ref probability, value); }
    public string ScanDepth { get => scanDepth; set => Set(ref scanDepth, value); }
    public bool Enabled { get => enabled; set => Set(ref enabled, value, nameof(Display)); }
    public bool Constant { get => constant; set => Set(ref constant, value, nameof(Display)); }
    public bool ExcludeRecursion { get => excludeRecursion; set => Set(ref excludeRecursion, value); }
    public bool PreventRecursion { get => preventRecursion; set => Set(ref preventRecursion, value); }
    public bool? CaseSensitive { get => caseSensitive; set => Set(ref caseSensitive, value); }
    public bool? WholeWords { get => wholeWords; set => Set(ref wholeWords, value); }
    public int LogicIndex { get => logicIndex; set => Set(ref logicIndex, value); }
    public int PositionIndex { get => positionIndex; set => Set(ref positionIndex, value); }

    public string Label => Title.Trim().Length > 0 ? Title.Trim() : Keys.Trim().Length > 0 ? Keys.Trim() : "(new entry)";
    public string Display => (Enabled ? "" : "[off] ") + (Constant ? "[always] " : "") + Label;
    public string Size => $"{Content.Length:N0} characters";

    public bool Matches(string search) =>
        Title.Contains(search, StringComparison.OrdinalIgnoreCase) || Keys.Contains(search, StringComparison.OrdinalIgnoreCase) ||
        SecondaryKeys.Contains(search, StringComparison.OrdinalIgnoreCase) || Content.Contains(search, StringComparison.OrdinalIgnoreCase);

    public static LorebookEntryItem From(LorebookEntry entry) => new()
    {
        Uid = entry.Uid,
        title = entry.Title,
        keys = string.Join(", ", entry.Keys),
        secondaryKeys = string.Join(", ", entry.SecondaryKeys),
        content = entry.Content,
        order = entry.Order.ToString(CultureInfo.CurrentCulture),
        probability = entry.Probability.ToString(CultureInfo.CurrentCulture),
        scanDepth = entry.ScanDepth?.ToString(CultureInfo.CurrentCulture) ?? "",
        enabled = entry.Enabled,
        constant = entry.Constant,
        excludeRecursion = entry.ExcludeRecursion,
        preventRecursion = entry.PreventRecursion,
        caseSensitive = entry.CaseSensitive,
        wholeWords = entry.MatchWholeWords,
        logicIndex = (int)entry.SecondaryLogic,
        positionIndex = (int)entry.Position
    };

    public LorebookEntryItem Copy(int uid) => From(ToEntry("") with { Uid = uid, Title = Title.Trim().Length > 0 ? Title.Trim() + " copy" : "" });

    public LorebookEntry ToEntry(string book)
    {
        var where = $"Lorebook \"{book}\", entry \"{Label}\": ";
        var entry = new LorebookEntry
        {
            Uid = Uid,
            Title = Title.Trim(),
            Keys = SplitKeys(Keys),
            SecondaryKeys = SplitKeys(SecondaryKeys),
            SecondaryLogic = (LorebookSecondaryLogic)Math.Clamp(LogicIndex, 0, 3),
            Content = Content.Replace("\r\n", "\n").Trim(),
            Constant = Constant,
            Enabled = Enabled,
            Order = Whole(Order, where + "order must be a whole number.") ?? 100,
            Position = (LorebookPosition)Math.Clamp(PositionIndex, 0, 1),
            Probability = Whole(Probability, where + "chance must be a whole number from 0 to 100.") ?? 100,
            CaseSensitive = CaseSensitive,
            MatchWholeWords = WholeWords,
            ScanDepth = Whole(ScanDepth, where + "scan depth must be empty or a whole number."),
            ExcludeRecursion = ExcludeRecursion,
            PreventRecursion = PreventRecursion
        };
        try { entry.Validate(); }
        catch (ContractException error) { throw new ContractException(error.Code, where + error.Message); }
        return entry;
    }

    private static int? Whole(string text, string message)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        return int.TryParse(text.Trim(), NumberStyles.Integer, CultureInfo.CurrentCulture, out var value)
            ? value : throw new ContractException(ErrorCode.InvalidContract, message);
    }

    /// <summary>Comma-separated keys; a comma inside a /regex/ key stays part of it.</summary>
    internal static string[] SplitKeys(string text)
    {
        var keys = new List<string>();
        string? pending = null;
        foreach (var part in text.Split(','))
        {
            var piece = pending is null ? part : pending + "," + part;
            if (piece.TrimStart().StartsWith('/') && !LorebookScanner.IsRegexKey(piece))
            {
                pending = piece;
                continue;
            }
            pending = null;
            if (piece.Trim() is { Length: > 0 } key && !keys.Contains(key)) keys.Add(key);
        }
        if (pending?.Trim() is { Length: > 0 } rest && !keys.Contains(rest)) keys.Add(rest);
        return keys.ToArray();
    }
}

/// <summary>An editable lorebook with its entries and persona choices.</summary>
internal sealed class LorebookItem : EditorItem
{
    private string name = "", description = "";
    private int activationIndex = (int)LorebookActivation.AllPersonas;

    public Guid Id { get; init; } = Guid.NewGuid();
    public string Name { get => name; set => Set(ref name, value, nameof(Display)); }
    public string Description { get => description; set => Set(ref description, value); }
    public int ActivationIndex
    {
        get => activationIndex;
        set => Set(ref activationIndex, value, nameof(Display), nameof(PersonasEnabled));
    }
    public bool PersonasEnabled => ActivationIndex == (int)LorebookActivation.SelectedPersonas;
    public ObservableCollection<LorebookEntryItem> Entries { get; } = [];
    public ObservableCollection<LorebookPersonaChoice> Personas { get; } = [];
    /// <summary>Selected personas that aren't in this profile's settings any more; kept so saving doesn't drop them.</summary>
    public IReadOnlyList<Guid> OtherPersonaIds { get; init; } = [];

    public string Display => (Name.Trim().Length > 0 ? Name.Trim() : "(unnamed lorebook)") + (LorebookActivation)ActivationIndex switch
    {
        LorebookActivation.Off => " (off)",
        LorebookActivation.SelectedPersonas => " (selected personas)",
        _ => ""
    } + $" - {Entries.Count} {(Entries.Count == 1 ? "entry" : "entries")}";

    public LorebookItem() => Entries.CollectionChanged += (_, _) => Raise(nameof(Display));

    public int NextUid() => Entries.Count == 0 ? 0 : Entries.Max(entry => entry.Uid) + 1;

    public static LorebookItem From(Lorebook book, IReadOnlyList<PersonaProfile> personas)
    {
        var item = new LorebookItem
        {
            Id = book.Id,
            name = book.Name,
            description = book.Description,
            activationIndex = (int)book.Activation,
            OtherPersonaIds = book.PersonaIds.Where(id => personas.All(persona => persona.Id != id)).ToArray()
        };
        foreach (var persona in personas)
            item.Personas.Add(new(persona.Id, persona.Name, book.PersonaIds.Contains(persona.Id)));
        foreach (var entry in book.Entries)
            item.Entries.Add(LorebookEntryItem.From(entry));
        return item;
    }

    public Lorebook ToBook()
    {
        var book = new Lorebook
        {
            Id = Id,
            Name = Name.Trim(),
            Description = Description.Replace("\r\n", "\n").Trim(),
            Activation = (LorebookActivation)Math.Clamp(ActivationIndex, 0, 2),
            PersonaIds = Personas.Where(persona => persona.IsChecked).Select(persona => persona.Id).Concat(OtherPersonaIds).Distinct().ToArray(),
            Entries = Entries.Select(entry => entry.ToEntry(Name.Trim())).ToArray()
        };
        try { book.Validate(); }
        catch (ContractException error) { throw new ContractException(error.Code, $"Lorebook \"{book.Name}\": {error.Message}"); }
        return book;
    }
}
