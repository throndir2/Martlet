using System.Windows;
using System.Windows.Controls;
using Martlet.Core.Lorebooks;

namespace Martlet.Desktop;

/// <summary>Companion › Lorebook, the UI only: what is in the lorebooks, which are on for the current persona, quick on/off, and the
/// editor. What each line says is in <see cref="LorebookPageModel"/>; what the page needs from the app is
/// <see cref="ILorebookPageServices"/>.</summary>
internal sealed class LorebookPage(ILorebookPageServices services)
{
    private bool loading;

    /// <summary>Draws the page's cards into <paramref name="page"/>.</summary>
    internal void Render(Panel page)
    {
        if (services.Store is null)
        {
            page.Children.Add(PageNowCard("Lorebooks are unavailable until Martlet's data folder can be used.", null));
            return;
        }
        if (services.Loaded is not { } lore)
        {
            page.Children.Add(PageNowCard("Reading your lorebooks...", null));
            LoadAsync().Forget();
            return;
        }

        var persona = services.Settings?.Companion?.ActivePersona;
        var library = lore.Library;
        page.Children.Add(PageNowCard(lore.Error ?? LorebookPageModel.Now(library, persona), null));

        var books = new List<UIElement>
        {
            Heading("Lorebooks"),
            HelpTip.Explain("Lore entries are added to replies when their keywords come up. Triggered entries are sent to the Thinking model.",
                new Thickness(0, 0, 0, 8), "LorebookTab", "lorebooks", wiki: "Personality-and-Characters#lorebooks")
        };
        if (lore.Loaded)
            foreach (var book in library.Books)
                books.Add(BookRow(book, persona?.Id));
        books.Add(Row(
            PageButton("Edit lorebooks", () => services.OpenEditor(import: false), primary: true, id: "OpenLorebooks"),
            lore.Loaded ? PageButton("Import a lorebook", () => services.OpenEditor(import: true), id: "ImportLorebook") : null));
        page.Children.Add(Card(books.ToArray()));

        page.Children.Add(Card(Heading("From SillyTavern"),
            HelpTip.Explain("Import SillyTavern World Info or character cards. You can also export any lorebook back to World Info.",
                new Thickness(0, 0, 0, 4), "LorebookSillyTavern", "SillyTavern lorebooks", wiki: "Personality-and-Characters#lorebooks")));
    }

    private UIElement BookRow(Lorebook book, Guid? persona)
    {
        var row = new DockPanel { Margin = new Thickness(0, 4, 0, 4), LastChildFill = true };
        var on = book.Activation != LorebookActivation.Off;
        var toggle = PageButton(on ? "Turn off" : "Turn on", () => SetOnAsync(book.Id, !on).Forget(), id: "LorebookToggle-" + book.Id.ToString("N"));
        toggle.Margin = new Thickness(10, 0, 0, 0);
        DockPanel.SetDock(toggle, Dock.Right);
        row.Children.Add(toggle);
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock { Text = book.Name, FontSize = 15, TextWrapping = TextWrapping.Wrap });
        text.Children.Add(Note(LorebookPageModel.BookLine(book, persona, services.Settings?.Companion?.Personas ?? []), new Thickness(0, 0, 0, 0)));
        row.Children.Add(text);
        return row;
    }

    /// <summary>Turns one lorebook on or off right away. Turning on keeps its persona choice when it had one.</summary>
    private async Task SetOnAsync(Guid id, bool on)
    {
        if (services.Store is not { } lorebooks || services.Closing) return;
        var saved = await lorebooks.UpdateAsync(library => library with
        {
            Books = library.Books.Select(book => book.Id != id ? book : book with
            {
                Activation = !on ? LorebookActivation.Off
                    : book.PersonaIds.Count > 0 ? LorebookActivation.SelectedPersonas : LorebookActivation.AllPersonas
            }).ToArray()
        });
        services.Loaded = saved.Saved ? new(saved.Library, saved.Revision, null) : null;
        if (!saved.Saved && !services.Closing) services.ShowAction("Couldn't update the lorebook: " + saved.Error);
        if (!services.Closing && services.IsOpen) services.Redraw();
    }

    private async Task LoadAsync()
    {
        if (services.Store is not { } lorebooks || loading) return;
        loading = true;
        try { services.Loaded = await lorebooks.LoadAsync(services.Lifetime); }
        catch (OperationCanceledException) { return; }
        finally { loading = false; }
        if (!services.Closing && services.IsOpen) services.Redraw();
    }
}
