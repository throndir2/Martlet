using Martlet.Conversation.Guides;

namespace Martlet.Desktop;

/// <summary>Everything Companion › App guides (<see cref="AppGuidesPage"/> and <see cref="AppGuidesPageModel"/>) reads and starts in
/// the app. MainWindow gives it to the page when it makes the page (MainWindow.AppGuidesPage.cs). MainWindow keeps the work the page
/// shows (rule 4 of Pages\README.md): the guide library and the watch of the program in front, reading up (Read up now, Read again)
/// and Read more, which the speech bubble starts too (MainWindow.AppGuides.cs).</summary>
internal interface IAppGuidesPageServices
{
    // ---------- what the page reads ----------

    /// <summary>The guide library, or null until Martlet's data folder can be used.</summary>
    AppGuideService? Guides { get; }

    /// <summary>Ends when the window closes.</summary>
    CancellationToken Token { get; }

    /// <summary>What the last thing done on the page said (AppGuidesResult), or null. Reading up sets it too.</summary>
    string? Result { get; set; }

    // ---------- what the page starts ----------

    /// <summary>Read up now (or Read again): in the conversation that runs, as its background job, else in the window on its own.</summary>
    Task ReadUp(AppGuideEntry entry);

    /// <summary>Read more: more pages of the app's wiki join its guide.</summary>
    void ReadMore(AppGuideEntry entry);

    /// <summary>Shows <paramref name="text"/> on the window's action line.</summary>
    void Say(string text);

    /// <summary>Draws the open Companion page again.</summary>
    void Redraw();

    /// <summary>Draws the page again while it is still open.</summary>
    void RedrawIfOpen();
}
