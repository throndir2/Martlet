using Martlet.Core.Cluster;
using Martlet.HostClient;

namespace Martlet.Desktop;

/// <summary>Everything Companion › Reading (<see cref="ReadingPage"/> and <see cref="ReadingPageModel"/>) reads and starts in the
/// app. MainWindow gives it to the page when it makes the page (MainWindow.ReadingPage.cs). It has no WPF types, so the page model
/// can use it too. MainWindow keeps the work the page shows (rule 4 of Pages\README.md): checking Windows OCR, saving the Reading
/// list, setting up the Reading role and Read my screen now (MainWindow.Reading.cs); Devices uses the same list routines.</summary>
internal interface IReadingPageServices
{
    // ---------- what the page reads ----------

    /// <summary>Martlet's data folder, or null before Martlet is set up.</summary>
    string? DataDirectory { get; }

    /// <summary>How you talk with Martlet now (talk-preferences.json): how often Martlet reads the screen.</summary>
    TalkPreferences Talk { get; }

    /// <summary>Whether Windows can read text on this PC (null: not checked yet), and whether the check runs now.</summary>
    bool? WindowsReads { get; }

    bool WindowsChecking { get; }

    /// <summary>The place whose Reading role is being set up now (a host ID, or <see cref="MainWindow.ReadingThisPc"/>), or null.</summary>
    string? Pending { get; }

    /// <summary>Why the last setup failed on <paramref name="place"/>, or null.</summary>
    string? Failure(string place);

    /// <summary>The model a place's pills show until it is set up there (its key), or null.</summary>
    string? PickedModel(string place);

    /// <summary>Whether Read my screen now runs, what it found last and the text it read (null: none yet).</summary>
    bool Testing { get; }

    string? TestState { get; }

    string? TestText { get; }

    /// <summary>Whether the comparison table with <paramref name="id"/> is open.</summary>
    bool Comparing(string id);

    // ---------- your computers ----------

    /// <summary>This PC's pairing with its own host service, or null.</summary>
    PairedHost? ThisPcHost();

    /// <summary>The paired host with <paramref name="hostId"/>, or null.</summary>
    PairedHost? FindHost(string? hostId);

    /// <summary>The host a friend shares with this PC with <paramref name="hostId"/>, or null.</summary>
    PairedHost? FindSharedHost(string? hostId);

    /// <summary>The last check of <paramref name="hostId"/>, or null when it isn't checked yet.</summary>
    HostCheck? Check(string hostId);

    /// <summary>Whether the last check of <paramref name="host"/> found the role <paramref name="kind"/> there.</summary>
    bool Offers(PairedHost host, string kind);

    /// <summary>Why <paramref name="hostId"/> can't run the role <paramref name="roleKind"/> for <paramref name="job"/>, or null.</summary>
    string? CannotHand(string hostId, string roleKind, string job);

    /// <summary>Whether <paramref name="hostId"/>'s hardware report shows an NVIDIA graphics card that its containers can use
    /// (null: no report).</summary>
    bool? HostHasNvidia(string hostId);

    /// <summary>Whether this PC has an NVIDIA graphics card (null: this PC isn't measured yet).</summary>
    bool? ThisPcHasNvidia { get; }

    // ---------- what the page starts ----------

    /// <summary>Checks whether Windows can read text on this PC, then draws the page again.</summary>
    void CheckWindows();

    /// <summary>Saves <paramref name="next"/> as how you talk and, with <paramref name="render"/>, draws the page again.</summary>
    void SaveTalk(TalkPreferences next, bool render = false);

    /// <summary>Saves <paramref name="next"/> in the Reading list (by its key) and says <paramref name="done"/>.</summary>
    void SaveMember(PoolMember next, string done);

    /// <summary>Shows <paramref name="modelKey"/> in the pills of <paramref name="place"/> until it is set up there.</summary>
    void PickModel(string place, string modelKey);

    /// <summary>Opens or closes the comparison table with <paramref name="id"/>.</summary>
    void ToggleCompare(string id);

    /// <summary>Sets the Reading role up on <paramref name="host"/> (null: this PC) with <paramref name="model"/>, or switches it
    /// to that model (<paramref name="changing"/>), after a confirmation.</summary>
    void SetUp(PairedHost? host, OptionalExtras.ReadingRoleModel model, bool changing);

    /// <summary>Reads the whole screen once with the Reading list (Read my screen now).</summary>
    void ReadScreenNow();

    /// <summary>Draws the open Companion page again.</summary>
    void Redraw();
}
