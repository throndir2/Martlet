using System.Windows;
using System.Windows.Controls;
using Martlet.Core.Platforms;
using Martlet.Core.Settings;

namespace Martlet.Desktop;

/// <summary>Everything Companion's job pages (Thinking, Listening and Voice; <see cref="JobPage"/>) read and start in the app.
/// MainWindow gives it to the page when it makes the page (MainWindow.JobPage.cs). MainWindow keeps the work the page shows
/// (rule 4 of Pages\README.md): saving a route and a cloud provider, handing a job to a computer, the setup actions and the
/// checks. The cards that keep their own state in the window or that other pages also show (each place's card, the pool list,
/// the talk cards, If Thinking fails, the voices...) come from the window as builders. The key box goes to the window as it is
/// (<see cref="PasswordBox"/>), so a key never becomes a string on the way.</summary>
internal interface IJobPageServices
{
    // ---------- what the page reads ----------

    /// <summary>The saved settings (the routes and the audio devices), or null before they are loaded.</summary>
    AppSettings? Settings { get; }

    /// <summary>This PC's own host service, or null when it has none.</summary>
    PairedHost? ThisPcHost();

    /// <summary>The computer your Martlet network does <paramref name="job"/> on, or null (no network, or no choice yet).</summary>
    string? NetworkHost(string job);

    /// <summary>The place a page shows after the owner chose one in Where it runs, or null for the place in use.</summary>
    MainWindow.JobPlace? ShownPlace(CompanionTab section);

    /// <summary>The problem that stops <paramref name="job"/> now, or null.</summary>
    JobCoverage? Problem(string job);

    /// <summary>A route's place and model in words, for the Now line ("Ollama on this PC: Gemma 4 E2B").</summary>
    string RouteName(SetupRoute route);

    /// <summary>What your Martlet network does for <paramref name="job"/>, when a host does it for the network.</summary>
    string? NetworkJobNote(string job);

    /// <summary>Whether the chosen speakers (<paramref name="output"/>) or microphone are missing.</summary>
    bool AudioMissing(bool output);

    /// <summary>Whether Martlet learns and recognizes voices.</summary>
    bool VoiceRecognitionOn { get; }

    /// <summary>Whether the page offers a free NVIDIA Build key (no hosted provider has one yet).</summary>
    bool OffersFreeKey { get; }

    /// <summary>Whether the recommended setup says nothing can reply now.</summary>
    bool CannotReply { get; }

    /// <summary>Whether Add your key opened Thinking to fill in a free key now.</summary>
    bool FocusFreeKey { get; }

    /// <summary>The model app This PC shows for Thinking: the one chosen, else the default for what is installed and found.
    /// Looks for model apps on this PC once when it hasn't yet (loopback only).</summary>
    MainWindow.LocalApp ModelApp(SetupRoute? route);

    /// <summary>Your paired computers (not the ones friends share).</summary>
    IReadOnlyList<PairedHost> PairedHosts();

    /// <summary>The hosts friends share with this PC.</summary>
    IReadOnlyList<PairedHost> SharedHosts { get; }

    /// <summary>The last check of a host, or null when it isn't checked yet.</summary>
    HostCheck? Check(string hostId);

    /// <summary>Whether a host's platform and hardware can run a role, and why not.</summary>
    PlatformCheck? HostCan(string hostId, string roleKind);

    /// <summary>Why a job can't go to a host now, or null.</summary>
    string? CannotHand(string hostId, string roleKind, string job);

    /// <summary>Whether this PC may change the roles on a host.</summary>
    bool ChangesRolesOn(PairedHost host);

    /// <summary>The model abilities Martlet measured.</summary>
    ModelAbilities SavedModelAbilities();

    // ---------- cards and lines the window makes ----------

    UIElement JobPoolCard(CompanionTab section, SetupRole role, SetupRoute? route);
    UIElement? SituationFixtureCard();
    UIElement LocalAppCard(SetupRoute? route, MainWindow.LocalApp app);
    UIElement LocalThinkingCard(SetupRoute? route);
    UIElement LocalServerCard(SetupRoute? route);
    UIElement VoiceEnginesCard(SetupRoute? route, PairedHost? thisPc, bool onThisPc);
    UIElement LocalListeningCard(SetupRoute? route, PairedHost? thisPc);
    UIElement ChatterboxStyleCard();
    UIElement FallbackCard();
    UIElement VoicesCard(SetupRoute? route);
    UIElement SpeakRepliesCard();
    UIElement QuickSoundsCard();
    UIElement TalkModeCard();
    UIElement EchoCard();
    UIElement PcAudioCard();
    UIElement? OldKeysCard(CompanionTab section, SetupRole role);
    UIElement TextModelLine(SetupRoute? route);
    IEnumerable<UIElement> ToolsTestControls(SetupRoute? route);
    UIElement SituationNowLine();
    UIElement? StandInLine(SetupRoute? route);
    IEnumerable<UIElement> ElevenLabsFields(SetupRoute? route);

    /// <summary>An option picker (MainWindow.OptionPicker.cs), which keeps the option shown while the window lives.</summary>
    UIElement OptionPicker(string id, string heading, IReadOnlyList<PickerOption> options, int? rows = null);

    // ---------- what the page starts ----------

    /// <summary>Shows a place's card on a page (Where it runs) and draws the page again.</summary>
    void ShowPlace(CompanionTab section, MainWindow.JobPlace place);

    /// <summary>Shows <paramref name="key"/> in an option picker the next time it draws.</summary>
    void ShowPickerOption(string pickerId, string key);

    /// <summary>The free key's box got the focus once, so Add your key stops asking for it.</summary>
    void FreeKeyFocused();

    void OpenCompanion(CompanionTab tab);
    void OpenDevices();
    void RunNodeAction(NodeAction action);

    /// <summary>Opens a role's settings on a host (its model, GPU or CPU...).</summary>
    void ChangeRoleOn(PairedHost host, string roleKind);

    /// <summary>Opens Add your key for a free NVIDIA Build key; <paramref name="nobody"/>: nothing replies without it.</summary>
    void OpenFreeKey(bool nobody);

    /// <summary>Opens NVIDIA Build's key page in the browser, saying so in <paramref name="status"/>.</summary>
    void OpenKeyPage(TextBlock? status);

    void FocusWhenShown(Control box);

    /// <summary>Hands a job to a host ("host:&lt;id&gt;").</summary>
    Task AssignJob(HostJob job, string key);

    /// <summary>Uses a cloud provider for a job, after its consent box; the key box goes as it is.</summary>
    void SaveCloud(HostJob job, MainWindow.CloudProvider provider, string baseUrl, string model, string? voice, PasswordBox key, bool consent);

    /// <summary>Something on the page was edited, so a redraw waits.</summary>
    void MarkEdited();
}
