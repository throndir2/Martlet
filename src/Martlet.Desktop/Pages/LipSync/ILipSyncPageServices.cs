using Martlet.Avatar.Hosting;
using Martlet.Core.Cluster;
using Martlet.Core.Platforms;

namespace Martlet.Desktop;

/// <summary>Everything Companion › Lip sync (<see cref="LipSyncPage"/> and <see cref="LipSyncPageModel"/>) reads and starts in the
/// app. MainWindow gives it to the page when it makes the page (MainWindow.LipSyncPage.cs). It has no WPF types, so the page model
/// can use it too. MainWindow keeps the work the page shows (rule 4 of Pages\README.md): making lip-sync's list from the older
/// choice, keeping the list and the older choice in step, checking this PC's own Audio2Face service and setting Audio2Face up
/// (MainWindow.LipSyncPool.cs, MainWindow.Coverage.cs and MainWindow.WhoDoesWhat.cs). The shared pool list and its save are
/// builders of their own (see <see cref="LipSyncPage"/>).</summary>
internal interface ILipSyncPageServices
{
    // ---------- what the page reads ----------

    /// <summary>Martlet's data folder (pools.json is in it), or null before Martlet is set up.</summary>
    string? DataDirectory { get; }

    /// <summary>The character's own lip-sync choice (Auto, voice loudness or Audio2Face only), or null without a character.</summary>
    AvatarLipSync? CharacterLipSync { get; }

    /// <summary>Whether this PC's own Audio2Face service answered its last check (null: not checked yet).</summary>
    bool? OwnServiceAnswers { get; }

    /// <summary>This PC's own Audio2Face service address: the list's This PC setting (of <paramref name="member"/>, else the saved
    /// list's This PC), or the character's endpoint.</summary>
    Uri OwnServiceEndpoint(PoolMember? member = null);

    /// <summary>The character's own Audio2Face address (the default for This PC's setting).</summary>
    Uri CharacterEndpoint { get; }

    /// <summary>The coverage check's lip-sync problem, or null.</summary>
    JobCoverage? Problem { get; }

    /// <summary>This PC: its Docker Desktop, graphics cards and processor, as last read.</summary>
    MachineInfo Machine { get; }

    // ---------- your computers ----------

    /// <summary>This PC's pairing with its own host service, or null.</summary>
    PairedHost? ThisPcHost();

    /// <summary>The paired host with <paramref name="hostId"/>, or null.</summary>
    PairedHost? FindHost(string? hostId);

    /// <summary>The last check of <paramref name="hostId"/>, or null before its first check.</summary>
    HostCheck? CheckOf(string hostId);

    /// <summary>Whether a host answered its last check (the background sync check when sync is on, else Check connection); null
    /// before it was checked.</summary>
    bool? Reachable(string hostId);

    /// <summary>Whether <paramref name="hostId"/> runs Audio2Face for lip-sync (null: not checked yet).</summary>
    bool? RunsAudio2Face(string hostId);

    /// <summary>Why <paramref name="hostId"/> can't run the role <paramref name="roleKind"/> for <paramref name="job"/>, or null.</summary>
    string? CannotHand(string hostId, string roleKind, string job);

    /// <summary>Whether this PC changes <paramref name="host"/>'s roles from here.</summary>
    bool ChangesRolesOn(PairedHost host);

    // ---------- what the page starts ----------

    /// <summary>Lip-sync's list; made once from the older choices when there is none yet. Null while there is none.</summary>
    PoolList? EnsurePool();

    /// <summary>After the list changed on the page: the older choice follows its first member that is on.</summary>
    Task FollowListAsync(PoolList after);

    /// <summary>Shows <paramref name="text"/> in the window's status line.</summary>
    void ShowStatus(string text);

    /// <summary>Forgets the last check of this PC's own Audio2Face service and checks it again.</summary>
    void CheckOwnService();

    /// <summary>Runs <paramref name="action"/> on a paired computer in a run window.</summary>
    void LaunchOnHost(PairedHost host, HostAction action);

    /// <summary>Checks the paired computer <paramref name="hostId"/> (the Devices map's Check it).</summary>
    void CheckHost(string hostId);

    /// <summary>Sets up Martlet's host service on this PC with Audio2Face, which then does lip-sync.</summary>
    void SetUpThisPc();

    /// <summary>Lets <paramref name="hostId"/> do lip-sync, installing Audio2Face there first when it doesn't run it.</summary>
    void UseHost(string hostId);
}
