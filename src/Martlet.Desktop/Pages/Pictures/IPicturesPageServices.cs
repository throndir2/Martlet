using System.Text.Json.Nodes;
using System.Windows.Controls;
using Martlet.Core.Cluster;
using Martlet.Core.Pictures;
using Martlet.Core.Settings;
using Martlet.HostClient;

namespace Martlet.Desktop;

/// <summary>Everything Companion › Picture making (<see cref="PicturesPage"/> and <see cref="PicturesPageModel"/>) reads and starts
/// in the app. MainWindow gives it to the page when it makes the page (MainWindow.PicturesPage.cs). MainWindow keeps the work the
/// page shows (rule 4 of Pages\README.md): saving the list and a place's workflow, Connect, the keys, Add a cloud provider, setting
/// up the Picture making role, Check and Draw a test picture (MainWindow.Pictures.cs). The key boxes go to the window as they are
/// (<see cref="PasswordBox"/>), so a key never becomes a string on the way.</summary>
internal interface IPicturesPageServices
{
    // ---------- what the page reads ----------

    /// <summary>Martlet's data folder, or null before Martlet is set up.</summary>
    string? DataDirectory { get; }

    /// <summary>The saved settings (the Thinking route and the profile), or null before they are loaded.</summary>
    AppSettings? Settings { get; }

    /// <summary>The place whose Picture making role is being set up now (a host ID, or <see cref="MainWindow.PicturesThisPc"/>), or null.</summary>
    string? Pending { get; }

    /// <summary>Why the last setup failed, and on which place, or null.</summary>
    string? Failure { get; }

    string? FailedOn { get; }

    /// <summary>What the last Check or Draw a test picture found, or null.</summary>
    string? CheckResult { get; }

    /// <summary>Whether Check or Draw a test picture runs now, and which.</summary>
    bool Checking { get; }

    bool Testing { get; }

    /// <summary>What the last Add of a cloud provider did, or why it didn't (never a key), or null. Choosing another provider clears it.</summary>
    string? CloudState { get; set; }

    /// <summary>What Connect found on the ComfyUI place with <paramref name="memberKey"/>: its words and its status, or null.</summary>
    string? ComfyState(string memberKey);

    JsonObject? ComfyStatus(string memberKey);

    // ---------- your computers ----------

    /// <summary>This PC's pairing with its own host service, or null.</summary>
    PairedHost? ThisPcHost();

    /// <summary>The paired host with <paramref name="hostId"/>, or null.</summary>
    PairedHost? FindHost(string? hostId);

    /// <summary>Whether the last check of <paramref name="host"/> found the role <paramref name="kind"/> there.</summary>
    bool Offers(PairedHost host, string kind);

    /// <summary>Why <paramref name="hostId"/> can't run the role <paramref name="roleKind"/> for <paramref name="job"/>, or null.</summary>
    string? CannotHand(string hostId, string roleKind, string job);

    /// <summary>Whether the last check reached <paramref name="hostId"/> (null: not checked yet).</summary>
    bool? Reachable(string hostId);

    /// <summary>Whether <paramref name="hostId"/> was checked.</summary>
    bool Checked(string hostId);

    /// <summary>Whether the list can add <paramref name="host"/> (your own computer that can run the role).</summary>
    bool CanAdd(PairedHost host);

    /// <summary>Why this PC can't run the Picture making role, or null.</summary>
    string? ThisPcCannotDraw();

    // ---------- what the page starts ----------

    /// <summary>Check (or, with <paramref name="test"/>, Draw a test picture).</summary>
    void Check(bool test);

    /// <summary>Sets the Picture making role up on <paramref name="host"/> (null: this PC), after a confirmation.</summary>
    void SetUp(PairedHost? host);

    void ConnectComfy(PoolMember member);

    void LoadWorkflow(PoolMember member);

    void SaveWorkflow(PoolMember member, PictureWorkflow workflow, string checkpoint);

    void SaveKey(PoolMember member, PasswordBox key);

    void AddCloud(string provider, string model, PasswordBox key, bool consent);

    /// <summary>The list changed in the list control: a cloud place that left the list takes its own key with it.</summary>
    Task ListSaved(PoolList before, PoolList after);

    /// <summary>Tells the window that the page has edits that are not saved yet.</summary>
    void MarkEdited();

    /// <summary>Draws the page again after the click, while it is still open.</summary>
    void RedrawLater();
}
