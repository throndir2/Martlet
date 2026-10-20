using Martlet.Core.Settings;

namespace Martlet.Desktop;

/// <summary>The services MainWindow gives Companion › Replies (<see cref="RepliesPage"/> in Pages\Replies). The saving of the reply
/// settings stays in the window (MainWindow.Replies.cs), and the context check too (MainWindow.ContextCheck.cs).</summary>
public partial class MainWindow : IRepliesPageServices
{
    AppSettings? IRepliesPageServices.Settings => homeSettings;
    string IRepliesPageServices.PlaceName(SetupRoute route) => PlaceName(route);
    string IRepliesPageServices.ContextStatus(SetupRoute route, string place) =>
        ContextStatus(route, homeSettings?.Generation, SavedModelLimits(), place);

    void IRepliesPageServices.OpenCompanion(CompanionTab tab) => OpenCompanion(tab);
    void IRepliesPageServices.UseAutoSave(AutoSave autoSave) => tabAutoSave = autoSave;
    void IRepliesPageServices.ShowContextStatusWith(Action show) => showContextStatus = show;
    void IRepliesPageServices.RefreshContextStatus() => showContextStatus?.Invoke();
    void IRepliesPageServices.CheckContext() => CheckContextFromRepliesAsync().Forget();
    // Thinking longer is saved with the reply settings but set on Deep thinking: it is kept as saved.
    Task<bool> IRepliesPageServices.Save(GenerationSettings generation, Action<GenerationSettings?> saved) =>
        SaveRepliesAsync(old => GenerationSettings.Normalize(generation with { ThinkLonger = old?.ThinkLonger }), saved,
            "Reply settings saved; reload an open conversation to use them.");
    void IRepliesPageServices.MarkEdited() => tabEdited = true;
    void IRepliesPageServices.Say(string text) => ActionText.Text = text;
}
