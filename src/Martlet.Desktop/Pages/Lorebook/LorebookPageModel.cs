using Martlet.Core.Lorebooks;
using Martlet.Core.Settings;

namespace Martlet.Desktop;

/// <summary>What Companion › Lorebook says, without WPF: the Now line and each lorebook's line.</summary>
internal static class LorebookPageModel
{
    /// <summary>How many lorebooks there are and how many are on for <paramref name="persona"/>, with their entries.</summary>
    internal static string Now(LorebookLibrary library, PersonaProfile? persona)
    {
        var active = library.Books.Where(book => book.AppliesTo(persona?.Id)).ToArray();
        var entries = active.SelectMany(book => book.Entries).Where(entry => entry.Enabled).ToArray();
        return library.Books.Count == 0
            ? "No lorebooks yet: import one from SillyTavern or a character card, or write your own."
            : active.Length == 0
                ? $"{library.Books.Count} {(library.Books.Count == 1 ? "lorebook" : "lorebooks")}, none enabled for {persona?.Name ?? "this persona"}."
                : $"{active.Length} of {library.Books.Count} {(library.Books.Count == 1 ? "lorebook" : "lorebooks")} enabled for {persona?.Name ?? "this persona"}, with " +
                  $"{entries.Length} {(entries.Length == 1 ? "entry" : "entries")}" +
                  (entries.Count(entry => entry.Constant) is var constant and > 0 ? $", {constant} always on" : "") + ".";
    }

    /// <summary>A lorebook's line: for which personas it is on, its entries and whether the current persona uses it.</summary>
    internal static string BookLine(Lorebook book, Guid? persona, IReadOnlyList<PersonaProfile> personas)
    {
        var scope = book.Activation switch
        {
            LorebookActivation.Off => "Off",
            LorebookActivation.AllPersonas => "On for every persona",
            _ => "Only with " + (book.PersonaIds.Select(id => personas.FirstOrDefault(p => p.Id == id)?.Name).OfType<string>().ToArray()
                is { Length: > 0 } names ? string.Join(", ", names) : "no current persona")
        };
        return $"{scope}, {book.Entries.Count} {(book.Entries.Count == 1 ? "entry" : "entries")}" +
            (book.AppliesTo(persona) ? ", used by the current persona." : ".");
    }
}
