using System.Windows.Controls;
using Martlet.Core.Settings;
using Martlet.Providers;

namespace Martlet.Desktop;

/// <summary>The services MainWindow gives the shared cards of Companion › Thinking (<see cref="ThinkingCards"/> in Pages\Thinking).
/// The work the cards show stays in the window (MainWindow.LocalServers.cs, MainWindow.LocalThinking.cs, MainWindow.Fallback.cs
/// and MainWindow.Situations.cs).</summary>
public partial class MainWindow : IThinkingCardsServices
{
    private ThinkingCards? thinkingCards;

    /// <summary>The Thinking cards, made once: they keep what is typed in their boxes while the window lives.</summary>
    private ThinkingCards ThinkingCards => thinkingCards ??= new(this);

    // The job pages (MainWindow.JobPages.cs) still call the cards by these names.
    private Border LocalAppCard(SetupRoute? route, LocalApp chosen) => ThinkingCards.LocalApp(route, chosen);
    private Border LocalServerCard(SetupRoute? route) => ThinkingCards.LocalServer(route);
    private Border LocalThinkingCard(SetupRoute? route) => ThinkingCards.LocalThinking(route);
    private Border FallbackCard() => ThinkingCards.Fallback();
    private Border? SituationFixtureCard() => ThinkingCards.SituationFixture();

    AppSettings? IThinkingCardsServices.Settings => homeSettings;
    MachineInfo IThinkingCardsServices.Machine => machine;
    IReadOnlyList<string>? IThinkingCardsServices.OllamaModels => ollamaModels;
    bool IThinkingCardsServices.OllamaAutoChecked
    {
        get => ollamaAutoChecked;
        set => ollamaAutoChecked = value;
    }
    LocalModelTestOutcome? IThinkingCardsServices.LocalModelTest => localModelTest;
    Action? IThinkingCardsServices.ShowLocalTest { set => showLocalTest = value; }
    IReadOnlyList<LocalModelServer>? IThinkingCardsServices.LocalServers => localServers;
    bool IThinkingCardsServices.LookingForLocalServers => lookingForLocalServers;
    IReadOnlyList<LocalModelServer> IThinkingCardsServices.OtherLocalServers() => OtherLocalServers();
    string IThinkingCardsServices.LocalServerName(string baseUrl) => LocalServerName(baseUrl);
    LocalApp? IThinkingCardsServices.LocalApp { set => localApp = value; }
    string? IThinkingCardsServices.LocalServerPicked
    {
        get => localServerPicked;
        set => localServerPicked = value;
    }
    (string BaseUrl, LocalServerAnswer Answer)? IThinkingCardsServices.LocalServerAsked => localServerAsked;
    LocalServerTestOutcome? IThinkingCardsServices.LocalServerTest => localServerTest;
    Action? IThinkingCardsServices.ShowLocalServerTest { set => showLocalServerTest = value; }
    bool IThinkingCardsServices.FreeKeyForFallback => freeKeyFocus && freeKeyPreset == FreeKeyUse.Fallback;
    SimulatedSituation? IThinkingCardsServices.SimulatedSituation => simulatedSituation;

    Border IThinkingCardsServices.OptionPicker(string id, string heading, string? note, IReadOnlyList<PickerOption> options, string? shown,
        Action<string>? chosen, bool details, int? rows) => OptionPicker(id, heading, note, options, shown, chosen, details, rows ?? PickerRows);
    StackPanel IThinkingCardsServices.OptionPickerBody(string id, IReadOnlyList<PickerOption> options, int? rows) =>
        OptionPickerBody(id, options, rows: rows ?? PickerRows);
    void IThinkingCardsServices.ShowInPicker(string id, string key) => pickerShown[id] = key;
    void IThinkingCardsServices.FocusFreeKey(Control box)
    {
        freeKeyFocus = false;
        FocusWhenShown(box);
    }

    void IThinkingCardsServices.LookForLocalServers() => LookForLocalServersAsync().Forget();
    void IThinkingCardsServices.AskLocalServer(string typed, PasswordBox key) => AskLocalServerAsync(typed, key).Forget();
    void IThinkingCardsServices.TestLocalServer(string? baseUrl, string problem, string model, PasswordBox key) =>
        TestLocalServerAsync(baseUrl, problem, model, key).Forget();
    void IThinkingCardsServices.SaveLocalServer(string? baseUrl, string problem, string model, PasswordBox key) =>
        SaveLocalServerAsync(baseUrl, problem, model, key).Forget();
    void IThinkingCardsServices.AddOllamaModelFile() => AddOllamaModelFileAsync().Forget();
    void IThinkingCardsServices.CheckOllama(bool quiet) => CheckOllamaAsync(quiet).Forget();
    void IThinkingCardsServices.PullOllamaModel(string model) => PullOllamaModelAsync(model).Forget();
    void IThinkingCardsServices.InstallOllama(string model) => InstallOllamaAsync(model).Forget();
    void IThinkingCardsServices.TestLocalModel(string model) => TestLocalModelAsync(model).Forget();
    void IThinkingCardsServices.SaveLocalThinking(string model) => SaveLocalThinkingAsync(model).Forget();
    void IThinkingCardsServices.SaveFallback(CloudProvider? provider, string url, string model, PasswordBox key, bool consent) =>
        SaveFallbackAsync(provider, url, model, key, consent).Forget();
    void IThinkingCardsServices.ObserveSituation() => ObserveSituation();
    void IThinkingCardsServices.MarkEdited() => tabEdited = true;
    void IThinkingCardsServices.Redraw() => RenderTab();
}
