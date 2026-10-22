using Martlet.Conversation;
using Martlet.Core.Installation;
using Martlet.Core.Settings;

namespace Martlet.Desktop;

/// <summary>Everything Companion › Background thinking (<see cref="ThinkingPoolPage"/> and <see cref="ThinkingPoolPageModel"/>) reads and
/// changes in the app. MainWindow gives it to the page when it makes the page (MainWindow.ThinkingPool.cs); the page uses
/// nothing else of MainWindow. It has no WPF types, so the page model can use it too.</summary>
internal interface IThinkingPoolPageServices
{
    // ---------- what the page reads ----------

    /// <summary>Martlet's data folder (thinking-pool.json is in it), or null before Martlet is set up.</summary>
    string? DataDirectory { get; }

    /// <summary>The saved settings (the Thinking route, the reply settings and the profile), or null before they are loaded.</summary>
    AppSettings? Settings { get; }

    /// <summary>This PC's paired computers, as the Devices map lists them.</summary>
    IReadOnlyList<PairedHost> PairedHosts { get; }

    /// <summary>The last check of the paired computer <paramref name="hostId"/>, or null before its first check.</summary>
    HostCheck? CheckOf(string hostId);

    /// <summary>What the paired computer <paramref name="hostId"/> said about its hardware, or null.</summary>
    HostHardware? HardwareOf(string hostId);

    /// <summary>What each model reads and writes, as saved (the conversation's pool reads the same file).</summary>
    ModelAbilities SavedModelAbilities();

    /// <summary>The models Ollama on this PC has, or null before Ollama was checked.</summary>
    IReadOnlyList<string>? OllamaModels { get; }

    /// <summary>This PC's hardware (its graphics cards).</summary>
    MachineInfo Machine { get; }

    /// <summary>Whether this PC is a Martlet host (a paired computer's auto-join rule reads it).</summary>
    bool IsHost { get; }

    /// <summary>Whether the model of <paramref name="route"/> turned down tools.</summary>
    bool ToolsRefused(SetupRoute route);

    /// <summary>Why the role <paramref name="roleKind"/> can't be set up on <paramref name="hostId"/> for <paramref name="job"/>, or
    /// null when Martlet may set it up there.</summary>
    string? CannotHand(string hostId, string roleKind, string job);

    /// <summary>Whether this PC changes <paramref name="host"/>'s roles from here.</summary>
    bool ChangesRolesOn(PairedHost host);

    /// <summary>The live conversation's pool members whose providers limit requests now; empty without a conversation.</summary>
    IReadOnlyList<ThinkingPoolCooling> PoolCooling();

    /// <summary>How fast the live conversation's recent replies began, or null without a conversation.</summary>
    FirstWordTimes? FirstWords { get; }

    /// <summary>The live conversation's pool now (stopped, raised and retried counts), or null without a conversation.</summary>
    ThinkingPoolStatus? PoolStatusNow();

    // ---------- what the page does ----------

    /// <summary>Whether the window is closing: the page then starts nothing new.</summary>
    bool Closing { get; }

    /// <summary>Cancelled when the window closes.</summary>
    CancellationToken Lifetime { get; }

    /// <summary>Shows <paramref name="text"/> in the window's status line.</summary>
    void ShowStatus(string text);

    /// <summary>Draws the open Companion page again.</summary>
    void Redraw();

    /// <summary>Draws the page again when the window isn't closing and the Thinking pool page is still open.</summary>
    void RedrawIfOpen();

    /// <summary>Marks the open page as edited, so a background change doesn't draw it again under the owner's typing.</summary>
    void MarkEdited();

    /// <summary>Makes <paramref name="autoSave"/> the open page's auto-save, saved at once when another page opens.</summary>
    void UseAutoSave(AutoSave autoSave);

    /// <summary>Makes the live conversation read thinking-pool.json again.</summary>
    void ReloadThinkingPool();

    /// <summary>Saves the reply settings <paramref name="update"/> makes of the newest saved ones. False: try again shortly.</summary>
    Task<bool> SaveRepliesAsync(Func<GenerationSettings?, GenerationSettings?> update, Action<GenerationSettings?> saved, string done);

    /// <summary>Runs <paramref name="action"/> on a paired computer in a run window; its summary, or null when it stopped.</summary>
    Task<string?> RunHostActionAsync(PairedHost host, HostAction action, IReadOnlyDictionary<string, string>? answers = null);

    /// <summary>Checks <paramref name="host"/> now and keeps the result as its last check.</summary>
    Task<HostCheck> CheckHostAsync(PairedHost host);

    /// <summary>Runs one of the Devices map's actions (Add a computer, Check computers).</summary>
    void RunNodeAction(NodeAction action);

    /// <summary>Asks Ollama on this PC which models it has and says so in the status line.</summary>
    Task CheckOllamaAsync();

    /// <summary>Asks the owner <paramref name="question"/>; true when they answer <paramref name="yes"/>.</summary>
    bool Confirm(string question, string title, string yes, string no, string questionId);
}
