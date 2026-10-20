using Martlet.Avatars;

namespace Martlet.Desktop;

/// <summary>Everything Companion › Motion (<see cref="MotionPage"/> and <see cref="MotionPageModel"/>) reads and starts in the app.
/// MainWindow gives it to the page when it makes the page (MainWindow.MotionPage.cs). MainWindow keeps the work the page shows
/// (rule 4 of Pages\README.md): making the Motion list from the older choice, setting the motion role up and waiting for it
/// (MainWindow.Motion.cs).</summary>
internal interface IMotionPageServices
{
    // ---------- what the page reads ----------

    /// <summary>Martlet's data folder, or null before Martlet is set up.</summary>
    string? DataDirectory { get; }

    /// <summary>The kind of the character shown (else the chosen one), or null when there is none.</summary>
    AvatarRenderer? ShownRenderer { get; }

    /// <summary>The computer Motion is being set up on now (a host ID, or <see cref="MainWindow.MotionThisPc"/>), or null.</summary>
    string? Pending { get; }

    /// <summary>Why the last setup on the computer with <paramref name="key"/> failed, or null.</summary>
    string? Failure(string key);

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

    /// <summary>The model the last check of <paramref name="hostId"/> saw its Motion role offer, or null.</summary>
    string? OfferedModel(string hostId);

    /// <summary>Whether the list can add <paramref name="host"/> (your own computer that can run the role).</summary>
    bool CanAdd(PairedHost host);

    /// <summary>Why this PC can't make motions, or null.</summary>
    string? ThisPcCannotMove();

    // ---------- what the page starts ----------

    /// <summary>Makes the Motion list once from the older choice, when there is no list yet.</summary>
    void EnsurePool();

    /// <summary>Opens Companion › Emotes and motions.</summary>
    void OpenEmotes();

    /// <summary>Sets the motion role up on <paramref name="host"/> (null: this PC); <paramref name="key"/> follows the setup.</summary>
    void SetUp(PairedHost? host, string key);

    /// <summary>Shows the Settings of the list member with <paramref name="memberKey"/> when the list draws again.</summary>
    void ShowSettings(string memberKey);
}
