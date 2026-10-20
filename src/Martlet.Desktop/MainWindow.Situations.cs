using System.IO;
using System.Net.Http;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Threading;
using Martlet.Audio;
using Martlet.Audio.Windows;
using Martlet.HostClient;
using Martlet.Core.Cluster;
using Martlet.Core.Planning;
using Martlet.Core.Settings;
using Martlet.Providers;
using Martlet.Providers.Ollama;

namespace Martlet.Desktop;

/// <summary>Situations (docs/RECOMMENDATION_DESIGN.md, stage 2): this companion PC changes between the Normal, While gaming and
/// Host away plans by itself. A game watch (<see cref="GameWatch"/>, only while the owner said this PC is used for games) and the
/// presence record (<see cref="PresenceWatch"/>: a host missing after 30 seconds) feed the production rules
/// (<see cref="LiveSituations.Decide"/>). When live Thinking moves, every conversation on this PC uses the situation's route
/// (<see cref="LiveSituation.Current"/>) from its next reply: an open talk window switches between replies, so a reply in progress
/// is never slowed or stopped. While gaming, the card's model stays loaded while the game leaves room (Martlet keeps Ollama's
/// keep-alive fresh) and is unloaded only when the card is short of memory. Nothing here runs on a reply's path: a 5-second
/// timer on the UI thread and a 3-second game look on a thread-pool thread. situation.json says what was decided, for MCP.</summary>
public partial class MainWindow
{
    private readonly DispatcherTimer situationTimer = new(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(5) };
    private static readonly TimeSpan SituationCardEvery = TimeSpan.FromSeconds(15);
    /// <summary>Shorter than Ollama's default five-minute keep-alive, so a model kept loaded for a quick change back stays loaded.</summary>
    private static readonly TimeSpan SituationKeepLoaded = TimeSpan.FromMinutes(4);
    private GameWatch? gameWatch;
    private SituationDecision? situation;
    private SituationFacts? situationFacts;
    private string? situationApplied, situationFailed, situationReported, situationCard, situationLocalModel;
    private long situationCardAt, situationTouchedAt, situationLocalAt;
    private bool situationCardBusy, situationUnloaded, situationStarted;
    private TextBlock? situationNowText;
    private readonly SimulatedSituation? simulatedSituation = SimulatedSituation.FromEnvironment();

    private void StartSituations()
    {
        if (situationStarted || closing) return;
        situationStarted = true;
        PresenceChanged += SituationPresenceChanged;
        situationTimer.Tick += (_, _) => ObserveSituation();
        situationTimer.Start();
        if (simulatedSituation is not null) ErrorLog.Info($"Situations: FIXTURE ({SimulatedSituation.Variable}) {simulatedSituation.Describe()}.");
        ObserveSituation();
    }

    private void StopSituations()
    {
        situationTimer.Stop();
        PresenceChanged -= SituationPresenceChanged;
        gameWatch?.Dispose();
        gameWatch = null;
        LiveSituation.Current = null;
        LiveSituation.Gaming = false;
        CurrentActivity.Game = null;
    }

    private void SituationPresenceChanged(PresenceChange change) => ObserveSituation();

    /// <summary>Decides the situation again from what this PC knows now and follows it: a new Thinking route for the
    /// conversation (it switches between replies), the status line, situation.json and, while gaming, the card's model.</summary>
    private void ObserveSituation()
    {
        if (closing) return;
        if (Role != DeviceRole.Companion)
        {
            // A host PC doesn't talk: no situation, no moved route, no game watch (the role can change while Martlet runs).
            FollowGameWatch(false);
            LiveSituation.Gaming = false;
            CurrentActivity.Game = null;
            if (situationApplied is not null || LiveSituation.Current is not null)
            {
                situationApplied = null;
                LiveSituation.Current = null;
            }
            situation = null;
            return;
        }
        var facts = SituationFactsNow();
        // The game watch runs only while it can matter: live Thinking on this PC's card, or this PC's own host service, or memory,
        // which keeps what was said in a game for that game (CurrentActivity). Only the first decides the situation.
        var ownHost = WorkSharingRoster.Shared.OwnHostId ?? homeHosts.FirstOrDefault(h => h.Method == HostSetupMethod.ThisPcDocker)?.HostId;
        var forSituation = facts.PlaysGames && simulatedSituation is null && (facts.Home == LiveHome.ThisPc || ownHost is not null);
        var forMemory = homeSettings?.Memory?.Enabled == true && simulatedSituation is null;
        FollowGameWatch(forSituation || forMemory);
        facts = facts with { Game = simulatedSituation is { } fixture ? fixture.Game : forSituation ? gameWatch?.Game : null };
        CurrentActivity.Game = simulatedSituation is { } simulated ? simulated.Game : forMemory ? gameWatch?.Game : null;
        situationFacts = facts;
        var decision = LiveSituations.Decide(facts);
        LiveSituation.Gaming = facts.PlaysGames && facts.Game is not null;
        if (decision.Key != situation?.Key)
            ErrorLog.Info($"Situations: {decision.Title}{(simulatedSituation is null ? "" : " (FIXTURE)")}. {decision.Why}");
        if (decision.Situation != Martlet.Core.Planning.Situation.Gaming)
        {
            situationUnloaded = false;
            situationCard = null;
        }
        situation = decision;
        FollowSituationRoute(decision);
        if (situationNowText is not null) situationNowText.Text = SituationLine();
        if (decision.Situation == Martlet.Core.Planning.Situation.Gaming && simulatedSituation is null) KeepSituationCard(decision, facts);
        if (facts.Home == LiveHome.Host && simulatedSituation is null) FindSituationLocalModel();
        ReportSituation(decision, facts);
    }
}

/// <summary>FIXTURE for checking situations through MCP without a game or a host that goes away: with <see cref="Variable"/> set
/// before Martlet starts, Companion › Thinking shows buttons that start a simulated game and make the Thinking host simulated
/// away (a game is never looked for). "1" uses this PC's real setup; "gaming" (live Thinking on this PC's gemma4:e2b, gpu-box with
/// qwen3:8b, NVIDIA Build as the backup) and "host" (live Thinking on gpu-box, desk-host with gemma4:e4b, NVIDIA Build, this
/// PC's gemma4:e2b) use made-up computers, so their decisions only show: the conversation keeps its route. Add ",game" or
/// ",away" to start that way. Nothing is played, contacted or saved.</summary>
internal sealed class SimulatedSituation
{
    internal const string Variable = "MARTLET_SIMULATE_SITUATION";
    internal const string GameName = "FIXTURE game";

    /// <summary>"gaming" or "host" (made-up computers), or null for this PC's real setup.</summary>
    internal string? Network { get; private init; }
    internal string? Game { get; set; }
    internal bool Away { get; set; }

    internal static SimulatedSituation? FromEnvironment() => Parse(Environment.GetEnvironmentVariable(Variable));

    internal static SimulatedSituation? Parse(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var parts = value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(p => p.ToLowerInvariant()).ToArray();
        return new()
        {
            Network = parts.FirstOrDefault(p => p is "gaming" or "host"),
            Game = parts.Contains("game") ? GameName : null,
            Away = parts.Contains("away")
        };
    }

    internal string Describe() => (Network is null ? "this PC's setup" : $"the made-up '{Network}' network") +
        $", {(Game is null ? "no game" : "a simulated game")}, {(Away ? "the Thinking host simulated away" : "every host answers")}";

    /// <summary>The made-up network's facts.</summary>
    internal static SituationFacts Facts(string network, bool backupAllowed) => network == "host"
        ? new()
        {
            Home = LiveHome.Host, HomeHost = "gpu-box", HomeName = "gpu-box", HomeModel = "qwen3:8b", PlaysGames = true,
            Hosts = [new("desk-host", "desk-host", "gemma4:e4b")], Backup = "NVIDIA Build",
            BackupModel = ChatCompletionsEndpointCatalog.NvidiaBuildDefaultModelId, BackupAllowed = backupAllowed, LocalModel = "gemma4:e2b"
        }
        : new()
        {
            Home = LiveHome.ThisPc, HomeModel = "gemma4:e2b", PlaysGames = true, Hosts = [new("gpu-box", "gpu-box", "qwen3:8b")],
            Backup = "NVIDIA Build", BackupModel = ChatCompletionsEndpointCatalog.NvidiaBuildDefaultModelId, BackupAllowed = backupAllowed,
            LocalModel = "gemma4:e2b"
        };
}
