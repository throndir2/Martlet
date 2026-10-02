using System.Windows;
using System.Windows.Controls;
using Martlet.Core.Lorebooks;

namespace Martlet.Desktop;

/// <summary>Companion › Lorebook: what is in the lorebooks, which are on for the current persona, quick on/off, and the editor.</summary>
public partial class MainWindow
{
    private LorebookLoadResult? homeLore;
    private bool loadingLore;

    private void RenderLorebookTab(Panel page)
    {
        if (lorebooks is null)
        {
            page.Children.Add(PageNowCard("Lorebooks are unavailable until Martlet's data folder can be used.", null));
            return;
        }
        if (homeLore is not { } lore)
        {
            page.Children.Add(PageNowCard("Reading your lorebooks...", null));
            LoadHomeLoreAsync().Forget();
            return;
        }

        var persona = homeSettings?.Companion?.ActivePersona;
        var library = lore.Library;
        var active = library.Books.Where(book => book.AppliesTo(persona?.Id)).ToArray();
        var entries = active.SelectMany(book => book.Entries).Where(entry => entry.Enabled).ToArray();
        page.Children.Add(PageNowCard(lore.Error ?? (library.Books.Count == 0
            ? "No lorebooks yet. Import one from SillyTavern or a character card, or write your own."
            : active.Length == 0
                ? $"{library.Books.Count} {(library.Books.Count == 1 ? "lorebook" : "lorebooks")}, none on for {persona?.Name ?? "this persona"}."
                : $"{active.Length} of {library.Books.Count} {(library.Books.Count == 1 ? "lorebook" : "lorebooks")} on for {persona?.Name ?? "this persona"}: " +
                  $"{entries.Length} {(entries.Length == 1 ? "entry" : "entries")}" +
                  (entries.Count(entry => entry.Constant) is var constant and > 0 ? $", {constant} always on" : "") + "."), null));

        var books = new List<UIElement>
        {
            Heading("Lorebooks"),
            Note("When a keyword from an entry comes up in what you or Martlet just said, that entry's text is added to what " +
                "Martlet knows for the reply, within a budget; always-on entries are added every time. Lorebooks are saved on this PC and go " +
                "to your Thinking model only as triggered entries.", new Thickness(0, 0, 0, 8))
        };
        if (lore.Loaded)
            foreach (var book in library.Books)
                books.Add(BookRow(book, persona?.Id));
        books.Add(Row(
            PageButton("Edit lorebooks", () => OpenLorebooksAsync().Forget(), primary: true, id: "OpenLorebooks"),
            lore.Loaded ? PageButton("Import a lorebook", () => OpenLorebooksAsync(import: true).Forget(), id: "ImportLorebook") : null));
        page.Children.Add(Card(books.ToArray()));

        page.Children.Add(Card(Heading("From SillyTavern"),
            Note("Import SillyTavern World Info files (JSON) or the lorebook inside a SillyTavern or Chub character card (PNG, JSON or " +
                "CHARX), and export any lorebook back as World Info. Keywords, filter keywords and rules, always-on entries, order, " +
                "placement before or after the persona, chance, scan depth, whole-word and case options, /regex/ keys and recursion " +
                "carry over. A character card imported as a persona brings its keyword entries along as a lorebook for that persona.",
                new Thickness(0, 0, 0, 4))));
    }

    private UIElement BookRow(Lorebook book, Guid? persona)
    {
        var personas = homeSettings?.Companion?.Personas ?? [];
        var scope = book.Activation switch
        {
            LorebookActivation.Off => "Off",
            LorebookActivation.AllPersonas => "On for every persona",
            _ => "Only with " + (book.PersonaIds.Select(id => personas.FirstOrDefault(p => p.Id == id)?.Name).OfType<string>().ToArray()
                is { Length: > 0 } names ? string.Join(", ", names) : "no current persona")
        };
        var row = new DockPanel { Margin = new Thickness(0, 4, 0, 4), LastChildFill = true };
        var on = book.Activation != LorebookActivation.Off;
        var toggle = PageButton(on ? "Turn off" : "Turn on", () => SetLorebookOnAsync(book.Id, !on).Forget(), id: "LorebookToggle-" + book.Id.ToString("N"));
        toggle.Margin = new Thickness(10, 0, 0, 0);
        DockPanel.SetDock(toggle, Dock.Right);
        row.Children.Add(toggle);
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock { Text = book.Name, FontSize = 15, TextWrapping = TextWrapping.Wrap });
        text.Children.Add(Note($"{scope}. {book.Entries.Count} {(book.Entries.Count == 1 ? "entry" : "entries")}" +
            (book.AppliesTo(persona) ? ", used by the current persona." : "."), new Thickness(0, 0, 0, 0)));
        row.Children.Add(text);
        return row;
    }

    /// <summary>Turns one lorebook on or off right away. Turning on keeps its persona choice when it had one.</summary>
    private async Task SetLorebookOnAsync(Guid id, bool on)
    {
        if (lorebooks is null || closing) return;
        var saved = await lorebooks.UpdateAsync(library => library with
        {
            Books = library.Books.Select(book => book.Id != id ? book : book with
            {
                Activation = !on ? LorebookActivation.Off
                    : book.PersonaIds.Count > 0 ? LorebookActivation.SelectedPersonas : LorebookActivation.AllPersonas
            }).ToArray()
        });
        homeLore = saved.Saved ? new(saved.Library, saved.Revision, null) : null;
        if (!saved.Saved && !closing) ActionText.Text = "The lorebook was not changed. " + saved.Error;
        if (!closing && openTab == CompanionTab.Lorebook) RenderTab();
    }

    private async Task LoadHomeLoreAsync()
    {
        if (lorebooks is null || loadingLore) return;
        loadingLore = true;
        try { homeLore = await lorebooks.LoadAsync(lifetime.Token); }
        catch (OperationCanceledException) { return; }
        finally { loadingLore = false; }
        if (!closing && openTab == CompanionTab.Lorebook) RenderTab();
    }
}
