using Martlet.Conversation.Guides;

namespace Martlet.Desktop;

/// <summary>The services MainWindow gives Companion › App guides (<see cref="AppGuidesPage"/> in Pages\AppGuides). The work the page
/// shows stays in the window (MainWindow.AppGuides.cs).</summary>
public partial class MainWindow : IAppGuidesPageServices
{
    private AppGuidesPage? appGuidesPage;

    /// <summary>The App guides page, made once: it keeps the Add an app boxes and the status lines it updates while Martlet reads up.</summary>
    private AppGuidesPage AppGuidesPage => appGuidesPage ??= new(this);

    AppGuideService? IAppGuidesPageServices.Guides => appGuides;
    CancellationToken IAppGuidesPageServices.Token => lifetime.Token;
    string? IAppGuidesPageServices.Result
    {
        get => appGuidesResult;
        set => appGuidesResult = value;
    }

    Task IAppGuidesPageServices.ReadUp(AppGuideEntry entry) => ReadUpFromPageAsync(entry);
    void IAppGuidesPageServices.ReadMore(AppGuideEntry entry) => ReadMoreFromPageAsync(entry).Forget();
    void IAppGuidesPageServices.Say(string text) => ActionText.Text = text;
    void IAppGuidesPageServices.Redraw() => RenderTab();
    void IAppGuidesPageServices.RedrawIfOpen()
    {
        if (!closing && openTab == CompanionTab.AppGuides) RenderTab();
    }
}
