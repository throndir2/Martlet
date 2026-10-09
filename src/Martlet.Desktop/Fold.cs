using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;

namespace Martlet.Desktop;

/// <summary>A section you open and close (an <see cref="Expander"/> with the theme's chevron), for settings most people leave as
/// they are. Its automation ID is Fold-<c>id</c>, and it stays open or closed while Martlet runs, also when the page is built
/// again.</summary>
internal static class Fold
{
    private static readonly Dictionary<string, bool> Open = new(StringComparer.Ordinal);

    internal static Expander Create(string header, string id, bool expanded, params UIElement[] children)
    {
        var body = new StackPanel { Margin = new Thickness(6, 0, 0, 0) };
        foreach (var child in children) body.Children.Add(child);
        var fold = new Expander
        {
            Header = header, Content = body, IsExpanded = Open.GetValueOrDefault(id, expanded), Margin = new Thickness(-6, 6, 0, 0)
        };
        AutomationProperties.SetAutomationId(fold, "Fold-" + id);
        AutomationProperties.SetName(fold, header);
        // The events bubble, so a section inside this one doesn't count.
        fold.Expanded += (_, e) => { if (e.OriginalSource == fold) Open[id] = true; };
        fold.Collapsed += (_, e) => { if (e.OriginalSource == fold) Open[id] = false; };
        return fold;
    }

    /// <summary>Forgets every section's state, so each starts as its page builds it (for tests).</summary>
    internal static void Reset() => Open.Clear();
}
