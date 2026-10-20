using Martlet.Core.Lorebooks;
using Martlet.Core.Settings;

namespace Martlet.Desktop;

/// <summary>The services MainWindow gives Companion › Lorebook (<see cref="LorebookPage"/> in Pages\Lorebook). The window keeps the
/// lorebooks the page read last (homeLore): a settings sync and the lorebook editor clear them.</summary>
public partial class MainWindow : ILorebookPageServices
{
    private LorebookLoadResult? homeLore;
    private LorebookPage? lorebookPage;

    private LorebookPage LorebookPage => lorebookPage ??= new(this);

    LorebookStore? ILorebookPageServices.Store => lorebooks;
    AppSettings? ILorebookPageServices.Settings => homeSettings;
    LorebookLoadResult? ILorebookPageServices.Loaded
    {
        get => homeLore;
        set => homeLore = value;
    }
    CancellationToken ILorebookPageServices.Lifetime => lifetime.Token;
    bool ILorebookPageServices.Closing => closing;
    bool ILorebookPageServices.IsOpen => openTab == CompanionTab.Lorebook;
    void ILorebookPageServices.Redraw() => RenderTab();
    void ILorebookPageServices.OpenEditor(bool import) => OpenLorebooksAsync(import).Forget();
    void ILorebookPageServices.ShowAction(string text) => ActionText.Text = text;
}
