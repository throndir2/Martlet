using System.Windows.Controls;
using Martlet.Core.Settings;
using Martlet.Providers;

namespace Martlet.Desktop;

/// <summary>Everything the shared cards of Companion › Thinking (<see cref="ThinkingCards"/>: Model app, Your model app on this
/// PC, Ollama on this PC, If Thinking fails and the FIXTURE situations card) read and start in the app. MainWindow implements it
/// (MainWindow.ThinkingCards.cs). MainWindow keeps the work the cards show (rule 4 of Pages\README.md): looking for model apps,
/// asking, testing and saving a model app (MainWindow.LocalServers.cs), installing Ollama and downloading, testing and using a
/// model (MainWindow.LocalThinking.cs), saving the fallback (MainWindow.Fallback.cs) and the situations (MainWindow.Situations.cs).
/// The key boxes go to the window as they are (<see cref="PasswordBox"/>), so a key never becomes a string on the way.</summary>
internal interface IThinkingCardsServices
{
    // ---------- what the cards read ----------

    /// <summary>The saved settings (the Thinking route and the fallback), or null before they are loaded.</summary>
    AppSettings? Settings { get; }

    /// <summary>This PC's facts (its best graphics card).</summary>
    MachineInfo Machine { get; }

    /// <summary>The models this PC's Ollama has, or null when it wasn't checked yet.</summary>
    IReadOnlyList<string>? OllamaModels { get; }

    /// <summary>Whether the page already checked Ollama once without being asked.</summary>
    bool OllamaAutoChecked { get; set; }

    /// <summary>The last test of a model in this PC's Ollama, or null.</summary>
    MainWindow.LocalModelTestOutcome? LocalModelTest { get; }

    /// <summary>Shows the last Ollama model test again in the card (the window calls it after a test).</summary>
    Action? ShowLocalTest { set; }

    /// <summary>The model apps found on this PC, or null when Martlet hasn't looked yet.</summary>
    IReadOnlyList<LocalModelServer>? LocalServers { get; }

    /// <summary>Whether Martlet looks for model apps on this PC now.</summary>
    bool LookingForLocalServers { get; }

    /// <summary>The model apps found on this PC other than Ollama.</summary>
    IReadOnlyList<LocalModelServer> OtherLocalServers();

    /// <summary>The name of the model app at <paramref name="baseUrl"/> on this PC, for a sentence.</summary>
    string LocalServerName(string baseUrl);

    /// <summary>The app chosen in the Model app picker this time.</summary>
    MainWindow.LocalApp? LocalApp { set; }

    /// <summary>The model app picked in the list (its base URL), or <see cref="ThinkingCards.AnotherAddress"/>, or null.</summary>
    string? LocalServerPicked { get; set; }

    /// <summary>What the address typed under Another address said when asked for its models, or null.</summary>
    (string BaseUrl, LocalServerAnswer Answer)? LocalServerAsked { get; }

    /// <summary>The last test of a model in a model app on this PC, or null.</summary>
    MainWindow.LocalServerTestOutcome? LocalServerTest { get; }

    /// <summary>Shows the last model app test again in the card (the window calls it after a test).</summary>
    Action? ShowLocalServerTest { set; }

    /// <summary>Whether Add your key asks to put the cursor in the fallback's key box now.</summary>
    bool FreeKeyForFallback { get; }

    /// <summary>Where a key for the provider at <paramref name="origin"/> is saved already anywhere in Martlet ("the Thinking
    /// pool"), or null; saving without a new key then uses a copy of it.</summary>
    string? SavedKeyWhere(string? origin);

    /// <summary>The FIXTURE situation (MARTLET_SIMULATE_SITUATION), or null when it isn't set.</summary>
    SimulatedSituation? SimulatedSituation { get; }

    // ---------- the window's builders ----------

    /// <summary>The window's option picker in a card (MainWindow.OptionPicker.cs). A null <paramref name="rows"/> is the window's
    /// usual number of rows.</summary>
    Border OptionPicker(string id, string heading, string? note, IReadOnlyList<PickerOption> options, string? shown = null,
        Action<string>? chosen = null, bool details = true, int? rows = null);

    /// <summary>The window's option picker without its card.</summary>
    StackPanel OptionPickerBody(string id, IReadOnlyList<PickerOption> options, int? rows = null);

    /// <summary>Makes picker <paramref name="id"/> show option <paramref name="key"/>.</summary>
    void ShowInPicker(string id, string key);

    /// <summary>Puts the cursor in <paramref name="box"/> once it shows, and stops Add your key asking for it again.</summary>
    void FocusFreeKey(Control box);

    // ---------- what the cards start ----------

    void LookForLocalServers();

    void AskLocalServer(string typed, PasswordBox key);

    void TestLocalServer(string? baseUrl, string problem, string model, PasswordBox key);

    void SaveLocalServer(string? baseUrl, string problem, string model, PasswordBox key);

    void AddOllamaModelFile();

    /// <summary>Checks this PC's Ollama. <paramref name="quiet"/>: the page's own check when it opens.</summary>
    void CheckOllama(bool quiet = false);

    void PullOllamaModel(string model);

    void InstallOllama(string model);

    void TestLocalModel(string model);

    void SaveLocalThinking(string model);

    /// <summary>Saves the fallback, or turns it off when <paramref name="provider"/> is null.</summary>
    void SaveFallback(MainWindow.CloudProvider? provider, string url, string model, PasswordBox key, bool consent);

    /// <summary>Looks at the situation again (after a FIXTURE button).</summary>
    void ObserveSituation();

    /// <summary>Tells the window that the page has edits that are not saved yet.</summary>
    void MarkEdited();

    /// <summary>Draws the open page again.</summary>
    void Redraw();
}
