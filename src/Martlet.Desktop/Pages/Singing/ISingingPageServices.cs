using System.Windows.Controls;
using Martlet.Core.Cluster;
using Martlet.Core.Singing;
using Martlet.HostClient;

namespace Martlet.Desktop;

/// <summary>Everything Companion › Singing (<see cref="SingingPage"/> and <see cref="SingingPageModel"/>) reads and starts in the app.
/// MainWindow gives it to the page when it makes the page (MainWindow.SingingPage.cs). MainWindow keeps the work the page shows
/// (rule 4 of Pages\README.md): making the lists from the older choice, saving a member, reading each computer's singing service,
/// setting Singing up, saving a key and adding a cloud provider (MainWindow.Singing.cs and MainWindow.SingingCloud.cs). The key boxes
/// go to the window as they are (<see cref="PasswordBox"/>), so a key never becomes a string on the way.</summary>
internal interface ISingingPageServices
{
    // ---------- what the page reads ----------

    /// <summary>Martlet's data folder, or null before Martlet is set up.</summary>
    string? DataDirectory { get; }

    /// <summary>This PC's pairing with its own host service, or null.</summary>
    PairedHost? ThisPcHost();

    /// <summary>The paired host with <paramref name="hostId"/>, or null.</summary>
    PairedHost? FindHost(string? hostId);

    /// <summary>Your own paired computers (not friends' hosts).</summary>
    IReadOnlyList<PairedHost> OwnHosts();

    /// <summary>Whether the last check of <paramref name="host"/> found the role <paramref name="kind"/> there.</summary>
    bool Offers(PairedHost host, string kind);

    /// <summary>Whether the last check reached <paramref name="hostId"/> (null: not checked yet).</summary>
    bool? Reachable(string hostId);

    /// <summary>Why <paramref name="hostId"/> can't run the role <paramref name="roleKind"/> for <paramref name="job"/>, or null.</summary>
    string? CannotHand(string hostId, string roleKind, string job);

    /// <summary>Whether a list can add <paramref name="host"/> (your own computer that can run Singing).</summary>
    bool CanAdd(PairedHost host);

    /// <summary>Why this PC can't sing, or null.</summary>
    string? ThisPcCannotSing();

    /// <summary>What <paramref name="hostId"/>'s singing service last reported (null when it couldn't be read); false before a read.</summary>
    bool TryService(string hostId, out SongClient.SingingService? service);

    /// <summary>Reads the singing service of each of <paramref name="hosts"/> not read yet; the page is drawn again after each read.</summary>
    void ReadServices(IEnumerable<PairedHost> hosts);

    /// <summary>The computer Singing (or <see cref="PendingAddOn"/>) is being set up on now, or null.</summary>
    string? PendingHost { get; }

    SingingAddOn? PendingAddOn { get; }

    /// <summary>Why the last setup on the computer with <paramref name="key"/> failed, or null.</summary>
    string? Failure(string key);

    /// <summary>Why adding <paramref name="addOnId"/> on the computer with <paramref name="key"/> failed, or null.</summary>
    string? AddOnFailure(string key, string addOnId);

    /// <summary>What the last Add of a cloud provider did in the list <paramref name="areaId"/>, or null.</summary>
    string? CloudState(string areaId);

    // ---------- what the page starts ----------

    /// <summary>Makes the music and voice match lists once from Companion › Singing's older choice.</summary>
    void EnsurePools(SingingPreferences preferences);

    /// <summary>Sets Singing up on <paramref name="host"/> (null: this PC), or adds <paramref name="addOn"/> there.</summary>
    void SetUp(PairedHost? host, SingingAddOn? addOn);

    /// <summary>Saves <paramref name="member"/>'s settings in the list of <paramref name="area"/> and says <paramref name="done"/>.</summary>
    void SaveMember(PoolArea area, PoolMember member, string done);

    /// <summary>Opens the Settings of the member with <paramref name="key"/> in the list of <paramref name="area"/>.</summary>
    void Expand(PoolArea area, string key);

    void SaveKey(string provider, PasswordBox key);

    void AddCloud(PoolArea area, string provider, string model, PasswordBox key, bool consent);

    /// <summary>Forgets what the last Add did in the list <paramref name="areaId"/>.</summary>
    void ClearCloudState(string areaId);

    /// <summary>Tells the window that the page has edits that are not saved yet.</summary>
    void MarkEdited();

    /// <summary>Draws the page again after the click, while it is still open.</summary>
    void RedrawLater();
}
