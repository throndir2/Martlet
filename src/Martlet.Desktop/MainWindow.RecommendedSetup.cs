using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Martlet.Core.Cluster;
using Martlet.Core.Planning;
using Martlet.Core.Settings;

namespace Martlet.Desktop;

/// <summary>Home's Recommended setup: the best use of all your computers from the network recommender
/// (<see cref="NetworkRecommender"/>): companion PCs stay light because they often run games, every graphics card runs at most
/// one language model, and the jobs are shared between the pools. The review window shows what changes on each computer and
/// why; Reconfigure applies it on every computer. A PC in no Martlet network gets Set it all up for me instead (the same
/// recommendation for one PC). When a computer comes back or stays away, every companion PC checks again in the background,
/// never while Martlet replies or hears you; the one someone is using (<see cref="SetupAskRule"/>) asks on Home, and in a
/// notification while Martlet's window is hidden, unless the owner declined that setup on this PC before.</summary>
public partial class MainWindow
{
    private RecommendedSetupWindow? recommendedSetupWindow;
    private bool recommendedScanning;
    /// <summary>The suggestion Home shows ("A better setup is ready for your computers"), until reviewed and applied, declined
    /// or found no longer worth asking.</summary>
    private (NetworkRecommendation Recommendation, string Reason)? recommendedNotice;
    /// <summary>A suggestion found while nobody used this PC: asked about when someone does, within <see cref="SetupAskRule.Keep"/>.</summary>
    private (string Reason, DateTimeOffset At)? recommendedPending;
    private DispatcherTimer? recommendedTimer;
    /// <summary>Add your key (FreeKeyPrompt): Companion › Thinking shows NVIDIA Build first where the key goes
    /// (<see cref="freeKeyPreset"/>: A cloud provider or If Thinking fails) and puts the cursor in its key box once
    /// (<see cref="freeKeyFocus"/>); <see cref="reviewAfterKey"/>: it came from the review, which opens again once a key is saved.
    /// <see cref="knownProviders"/>: the providers with a saved key when Home last drew.</summary>
    private FreeKeyUse freeKeyPreset;
    private bool freeKeyFocus, reviewAfterKey;
    private string? knownProviders;

    /// <summary>Checks again in the background when a computer comes back or stays away (going missing has its own notice).</summary>
    private void InitializeRecommendedSetup() => PresenceChanged += change =>
    {
        if (change.Kind is PresenceChangeKind.CameBack or PresenceChangeKind.StayedAway)
            AutoScanRecommendedSetupAsync($"{change.Name} {(change.Kind == PresenceChangeKind.CameBack ? "came back" : "stayed away")}").Forget();
    };

    private void RecommendedSetup_Click(object sender, RoutedEventArgs e) => OpenRecommendedSetupAsync().Forget();

    /// <summary>Home's Recommended setup button and the notice's Review: in a Martlet network, a fresh recommendation in the
    /// review window; on a PC alone, Set it all up for me (the default setup planned for this PC's hardware). With
    /// <paramref name="use"/> (the review's Use the suggestion for a locked job), the recommendation makes that job's suggestion
    /// this once, so Reconfigure applies it; the job stays locked.</summary>
    private async Task OpenRecommendedSetupAsync(string? use = null)
    {
        if (closing || Role != DeviceRole.Companion) return;
        if (recommendedSetupWindow is { IsLoaded: true } open)
        {
            open.Activate();
            return;
        }
        // The FIXTURE (SimulatedRecommendedSetup) plans its own network, also on a PC alone.
        if (!InMartletNetwork() && !SimulatedRecommendedSetup.Active)
        {
            await SetUpDefaultsAsync();
            return;
        }
        ActionText.Text = "Working out the recommended setup for your computers...";
        var scan = await ScanRecommendedSetupAsync(use);
        if (closing || scan is not { } found) return;
        try { ShowRecommendedSetup(found.Build, found.Recommendation); }
        catch (Exception error) when (!ErrorLog.IsFatal(error))
        {
            // Never a click that seems to do nothing: Home says the review couldn't open, and the log keeps why.
            ErrorLog.Error("Recommended setup: couldn't open the review.", error);
            ActionText.Text = $"Martlet couldn't open the recommended setup: {error.Message} Details are in Martlet's log.";
            return;
        }
        ActionText.Text = found.Recommendation.AlreadyOptimal
            ? found.Recommendation.BetterSetupAvailable ? BetterSetupTitle : RecommendedSetupReview.OptimalTitle
            : $"Martlet recommends {found.Recommendation.Changes.Count} change{(found.Recommendation.Changes.Count == 1 ? "" : "s")}; review them before anything changes.";
    }

    /// <summary>The catalog the last recommendation planned with: Martlet's options with the numbers measured on your computers
    /// (<see cref="FootprintCatalog.WithMeasured"/>), so the review shows the same numbers.</summary>
    private FootprintCatalog recommendedCatalog = FootprintCatalog.Default;

    /// <summary>Martlet's options with what it measured (model-memory.json and model-speed.json in <paramref name="directory"/>):
    /// the first words of the replies Martlet timed and the memory Ollama reported, here and on paired hosts. Reads two small
    /// files; call it off the UI thread.</summary>
    internal static FootprintCatalog MeasuredCatalog(string? directory) =>
        PlanningCatalog.Current.WithMeasured(MeasuredModelMemory.Load(directory), MeasuredFirstWords.Load(directory));

    /// <summary>Reads what this PC knows about your computers (on the UI thread; it contacts nothing), then plans on a
    /// thread-pool thread with the numbers measured on your computers, so the window and the conversation never wait for it.
    /// <paramref name="use"/>: a locked job whose suggestion the recommendation makes this once.</summary>
    private async Task<(SetupRequestBuild Build, NetworkRecommendation Recommendation)?> ScanRecommendedSetupAsync(string? use = null)
    {
        if (ReferenceEquals(machine, MachineInfo.Unknown)) await ReadMachineAsync();
        if (closing) return null;
        var sources = RecommendedSetupSources();
        if (!SimulatedRecommendedSetup.Active) sources = sources with { ServedModels = await FindServedModelsAsync() };
        if (closing) return null;
        var build = RecommendedSetupInputs.Request(sources);
        if (use is not null && !build.Request.Unlocked.Contains(use))
            build = build with { Request = build.Request with { Unlocked = [.. build.Request.Unlocked, use] } };
        var directory = store?.DataDirectory;
        try
        {
            await PlanningOptionsAsync();
            var (recommendation, catalog) = await Task.Run(() =>
            {
                var measured = MeasuredCatalog(directory);
                return (NetworkRecommender.Recommend(build.Request, measured), measured);
            }, lifetime.Token);
            recommendedCatalog = catalog;
            ErrorLog.Info($"Recommended setup: {build.Request.Machines.Count} computer(s) planned with your preferences ({sources.Preferences.Describe()}" +
                (build.Games.Count == 0 ? "" : "; games on " + string.Join(", ", build.Games.Select(g => $"{g.Name} {(g.Plays ? "yes" : "no")}"))) + "); " +
                (recommendation.AlreadyOptimal
                ? "they already use the recommended setup."
                : $"{recommendation.Changes.Count} change(s) recommended ({recommendation.Changes.Count(c => c.Benefit == SetupChangeBenefit.Required)} needed), " +
                  $"setup {recommendation.Fingerprint}.") +
                (catalog.Measurements.Count == 0 ? "" : $" Measured: {string.Join("; ", catalog.Measurements.Select(m => m.Describe()))}.") +
                (recommendation.Suggestions.Count == 0 ? "" : $" Suggestions: {string.Join(" ", recommendation.Suggestions.Select(s =>
                    $"{s.Text}{(s.Applied ? " (in the recommendation)" : s.Locked ? " (locked: shown only)" : "")}"))}"));
            return (build, recommendation);
        }
        catch (OperationCanceledException) { return null; }
    }

    private SetupSources RecommendedSetupSources()
    {
        var preferences = RecommendationPreferences.Load(store?.DataDirectory);
        if (SimulatedRecommendedSetup.Active)
        {
            // The FIXTURE's own PC is this PC, so its games answer is this PC's.
            var fixture = SimulatedRecommendedSetup.Sources(DateTimeOffset.UtcNow);
            return RecommendedSetupInputs.WithPreferences(fixture with
            {
                Computers = [.. fixture.Computers.Select(c => c.ThisPc ? c with { Device = ClusterDevice } : c)],
                ConfiguredProviders = ConfiguredProviders(), Off = RecommendedSetupMemory.Load(store?.DataDirectory).OffParts,
                Choices = RecommendedSetupChoices(store?.DataDirectory)
            }, preferences, GamesHere);
        }
        var inputs = Inputs();
        var directory = store?.DataDirectory;
        var poolSettings = ThinkingPoolSettings.Load(directory);
        var pool = poolSettings.Places.Places.Where(p => p.OnHostRole && p.HostId is not null).Select(p => p.HostId!).ToArray();
        return RecommendedSetupInputs.Sources(inputs, NetworkMap.Build(inputs), ClusterDevice, OwnHostId(), ThisPcDiskFreeGb(),
            offlineFor: OfflineFor, sharing: directory is null ? null : WorkSharingSettings.Load(directory), thinkingPool: pool,
            poolOptOut: poolSettings.LeftByOwner, voiceEngine: SpeakingEngineChoice.Current.HostRoleKind, configuredProviders: ConfiguredProviders(),
            off: RecommendedSetupMemory.Load(directory).OffParts, choices: RecommendedSetupChoices(directory),
            pools: directory is null ? null : PoolSettings.Load(directory), preferences: preferences, gamesHere: GamesHere)
            with { OnlinePool = RecommendedSetupInputs.OnlinePool(poolSettings) };
    }
}
