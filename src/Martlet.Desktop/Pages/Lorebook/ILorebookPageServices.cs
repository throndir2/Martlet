using Martlet.Core.Lorebooks;
using Martlet.Core.Settings;

namespace Martlet.Desktop;

/// <summary>Everything Companion › Lorebook (<see cref="LorebookPage"/>) reads and starts in the app. MainWindow gives it to the page
/// (MainWindow.LorebookPage.cs). MainWindow keeps the lorebooks it read last (<see cref="Loaded"/>), because a settings sync or the
/// lorebook editor clears them so that the page reads them again.</summary>
internal interface ILorebookPageServices
{
    /// <summary>The lorebook store, or null while Martlet's data folder can't be used.</summary>
    LorebookStore? Store { get; }

    /// <summary>The saved settings (the personas), or null before they are loaded.</summary>
    AppSettings? Settings { get; }

    /// <summary>The lorebooks the page read last, or null to read them again.</summary>
    LorebookLoadResult? Loaded { get; set; }

    /// <summary>Ends when the window closes.</summary>
    CancellationToken Lifetime { get; }

    /// <summary>Whether the window is closing.</summary>
    bool Closing { get; }

    /// <summary>Whether Companion › Lorebook is the open page.</summary>
    bool IsOpen { get; }

    /// <summary>Draws the open page again.</summary>
    void Redraw();

    /// <summary>Opens the lorebook editor, or (<paramref name="import"/>) its import.</summary>
    void OpenEditor(bool import);

    /// <summary>Shows <paramref name="text"/> in the window's action line.</summary>
    void ShowAction(string text);
}
