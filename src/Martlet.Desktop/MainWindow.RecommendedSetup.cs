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
    /// review window; on a PC alone, Set it all up for me (the default setup planned for this PC's hardware).</summary>
    private async Task OpenRecommendedSetupAsync()
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
        var scan = await ScanRecommendedSetupAsync();
        if (closing || scan is not { } found) return;
        ActionText.Text = found.Recommendation.AlreadyOptimal ? RecommendedSetupReview.OptimalTitle
            : $"Martlet recommends {found.Recommendation.Changes.Count} change{(found.Recommendation.Changes.Count == 1 ? "" : "s")}. Review them before anything changes.";
        ShowRecommendedSetup(found.Build, found.Recommendation);
    }

    /// <summary>Reads what this PC knows about your computers (on the UI thread; it contacts nothing), then plans on a
    /// thread-pool thread so the window and the conversation never wait for it.</summary>
    private async Task<(SetupRequestBuild Build, NetworkRecommendation Recommendation)?> ScanRecommendedSetupAsync()
    {
        if (ReferenceEquals(machine, MachineInfo.Unknown)) await ReadMachineAsync();
        if (closing) return null;
        var build = RecommendedSetupInputs.Request(RecommendedSetupSources());
        try
        {
            var recommendation = await Task.Run(() => NetworkRecommender.Recommend(build.Request, FootprintCatalog.Default), lifetime.Token);
            ErrorLog.Info($"Recommended setup: {build.Request.Machines.Count} computer(s) planned; " + (recommendation.AlreadyOptimal
                ? "they already use the recommended setup."
                : $"{recommendation.Changes.Count} change(s) recommended ({recommendation.Changes.Count(c => c.Benefit == SetupChangeBenefit.Required)} needed), " +
                  $"setup {recommendation.Fingerprint}."));
            return (build, recommendation);
        }
        catch (OperationCanceledException) { return null; }
    }

    private SetupSources RecommendedSetupSources()
    {
        if (SimulatedRecommendedSetup.Active)
            return SimulatedRecommendedSetup.Sources(DateTimeOffset.UtcNow) with
            {
                ConfiguredProviders = ConfiguredProviders(), Off = RecommendedSetupMemory.Load(store?.DataDirectory).OffParts,
                Choices = RecommendedSetupChoices(store?.DataDirectory)
            };
        var inputs = Inputs();
        var directory = store?.DataDirectory;
        var poolSettings = ThinkingPoolSettings.Load(directory);
        var pool = poolSettings.Places.Places.Where(p => p.OnHostRole && p.HostId is not null).Select(p => p.HostId!).ToArray();
        return RecommendedSetupInputs.Sources(inputs, NetworkMap.Build(inputs), ClusterDevice, OwnHostId(), ThisPcDiskFreeGb(),
            offlineFor: OfflineFor, sharing: directory is null ? null : WorkSharingSettings.Load(directory), thinkingPool: pool,
            poolOptOut: poolSettings.LeftByOwner, voiceEngine: SpeakingEngineChoice.Current.HostRoleKind, configuredProviders: ConfiguredProviders(),
            off: RecommendedSetupMemory.Load(directory).OffParts, choices: RecommendedSetupChoices(directory));
    }

    /// <summary>This PC's choices for the parts it sets on their Companion pages (Vision, Reading, Hearing, Smart home), read
    /// from the data folder: what the review says about them.</summary>
    private IReadOnlyList<PartChoice> RecommendedSetupChoices(string? directory) => RecommendedSetupInputs.Choices(Talk.Watch, Talk.HearVoice,
        SenseModels.Load(directory), Martlet.Core.Reading.ReadingSettings.Load(directory), HomePreferences.Load(directory).Address);

    private void ShowRecommendedSetup(SetupRequestBuild build, NetworkRecommendation recommendation)
    {
        var review = RecommendedSetupReview.From(recommendation, build);
        var window = new RecommendedSetupWindow(review, RecommendedPrepare(recommendation, build.Names), RecommendedApply(recommendation));
        if (IsVisible) window.Owner = this;
        window.Declined += DeclineRecommendedSetup;
        window.PartOff += TurnRecommendedPartOff;
        window.OpenThinking += use => OpenFreeKey(use, fromReview: true);
        window.Closed += (_, _) =>
        {
            if (ReferenceEquals(recommendedSetupWindow, window)) recommendedSetupWindow = null;
            if (window.Outcome is not null || review.AlreadyOptimal) ClearRecommendedNotice();
        };
        recommendedSetupWindow = window;
        window.Show();
        window.Activate();
    }

    // ---------- the reconfiguration (Prepare and Apply) ----------

    /// <summary>What Reconfigure needs first, from the executor's preflight (<see cref="PrepareRecommendedSetupAsync"/>): each
    /// change's state, the terms Reconfigure accepts and the keys it asks for. Reconfigure waits while another run is active.</summary>
    private Func<CancellationToken, Task<RecommendedSetupPreflightView>>? RecommendedPrepare(NetworkRecommendation recommendation,
        IReadOnlyDictionary<string, string> names) =>
        async cancel =>
        {
            SetupRunPreflight preflight;
            try { preflight = await PrepareRecommendedSetupAsync(recommendation, cancel); }
            catch (InvalidOperationException error)
            {
                ErrorLog.Warn("Recommended setup: couldn't check what the change needs.", error);
                return new RecommendedSetupPreflightView([], false, "Martlet couldn't check what the change needs: " + error.Message);
            }
            var problem = setupApplying ? "Martlet is already reconfiguring your computers. Background tasks shows its progress; check again when it's done."
                : preflight.CanApply ? null
                : "Martlet can't make any of these changes from here. Make them at each computer, or connect it on the Devices page.";
            return new RecommendedSetupPreflightView([.. preflight.Items.Select(i => i.Text).Where(t => t.Length > 0)], problem is null, problem,
                preflight)
            {
                Terms = [.. preflight.WithTerms.Select(i => i.Terms!).Distinct(StringComparer.Ordinal)],
                Secrets = [.. preflight.Unanswered.Select(need => new RecommendedSetupSecretField(SecretKey(need),
                    $"{need.Name} for {names.GetValueOrDefault(need.MachineId) ?? need.MachineId}", need.Prompt))]
            };
        };

    /// <summary>Reconfigure in the review: starts applying the recommendation on every computer as a background task
    /// (<see cref="ReconfigureAsync"/>), with the keys typed in the review, over <c>owner</c> (the review, which then closes).
    /// Returns null once it started, else why it couldn't start. Choosing Reconfigure accepts the terms shown; the executor
    /// records them.</summary>
    private Func<Window, RecommendedSetupPreflightView, IReadOnlyDictionary<string, string>, string?>? RecommendedApply(
        NetworkRecommendation recommendation) => (owner, view, typed) =>
        {
            if (view.Run is not SetupRunPreflight preflight) return "Martlet couldn't start the reconfiguration.";
            if (setupApplying || HostRunWindow.IsRunningTitled(SetupRunTitle))
                return "Martlet is already reconfiguring your computers. Background tasks shows its progress.";
            try
            {
                foreach (var need in preflight.Unanswered)
                    if (typed.GetValueOrDefault(SecretKey(need)) is { Length: > 0 } value) preflight = preflight.WithSecret(need, value);
            }
            catch (ArgumentException error)
            {
                return "Martlet couldn't use a key you typed: " + error.Message;
            }
            ReconfigureAsync(owner, recommendation, preflight).Forget();
            return null;
        };

    private static string SecretKey(SetupSecretNeed need) => $"{need.Index}/{need.MachineId}/{need.RoleKind}/{need.Name}";

    // ---------- automatic checks ----------

    /// <summary>A computer came back or stayed away: check again in the background. Nothing visible happens when the setup is
    /// already right or not worth asking about (one log line says so).</summary>
    private async Task AutoScanRecommendedSetupAsync(string reason)
    {
        if (closing || Role != DeviceRole.Companion || store is null || recommendedScanning || !InMartletNetwork()) return;
        recommendedScanning = true;
        try
        {
            // Never on the reply path: wait while Martlet replies or hears you (at most 10 minutes), then plan off the UI thread.
            for (var waited = 0; Talking && waited < 300 && !closing; waited++) await Task.Delay(TimeSpan.FromSeconds(2), lifetime.Token);
            if (closing || Talking) return;
            if (await ScanRecommendedSetupAsync() is { } found) FollowRecommendation(found.Recommendation, reason);
        }
        catch (OperationCanceledException) { }
        finally { recommendedScanning = false; }
    }

    private void FollowRecommendation(NetworkRecommendation recommendation, string reason)
    {
        var (step, why) = SetupAskRule.Decide(recommendation, RecommendedSetupMemory.Load(store?.DataDirectory), Role == DeviceRole.Companion,
            TimeSpan.FromSeconds(ReminderIdleSeconds()));
        ErrorLog.Info($"Recommended setup: checked after {reason}; {(step == SetupAskStep.Ask ? "asks here" : step == SetupAskStep.Wait ? "waits" : "nothing to ask")}: {why}.");
        switch (step)
        {
            case SetupAskStep.Ask:
                recommendedPending = null;
                AskRecommendedSetup(recommendation, reason);
                break;
            case SetupAskStep.Wait:
                recommendedPending = (reason, DateTimeOffset.UtcNow);
                FollowPendingRecommendation();
                break;
            default:
                recommendedPending = null;
                ClearRecommendedNotice();
                break;
        }
    }

    /// <summary>Shows Home's notice, and a notification while Martlet's window is hidden, once per recommended setup.</summary>
    private void AskRecommendedSetup(NetworkRecommendation recommendation, string reason)
    {
        var known = recommendedNotice?.Recommendation.Fingerprint == recommendation.Fingerprint;
        recommendedNotice = (recommendation, reason);
        RenderHealth();
        if (!known && (!IsVisible || WindowState == WindowState.Minimized))
            tray?.ShowNotice("A better setup is ready for your computers", $"{Sentence(reason)}. Open Martlet and choose Review on Home.");
    }

    /// <summary>Checks every 30 seconds whether someone uses this PC again, while a suggestion waits; then checks the setup again
    /// (it may have changed) and asks. A suggestion older than <see cref="SetupAskRule.Keep"/> is dropped.</summary>
    private void FollowPendingRecommendation()
    {
        if (recommendedTimer is null)
        {
            recommendedTimer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher) { Interval = TimeSpan.FromSeconds(30) };
            recommendedTimer.Tick += (_, _) =>
            {
                if (closing || recommendedPending is not { } pending)
                {
                    recommendedTimer.Stop();
                    return;
                }
                if (DateTimeOffset.UtcNow - pending.At > SetupAskRule.Keep)
                {
                    recommendedPending = null;
                    recommendedTimer.Stop();
                    ErrorLog.Info("Recommended setup: nobody used this PC within an hour of the check; it doesn't ask about it.");
                    return;
                }
                if (TimeSpan.FromSeconds(ReminderIdleSeconds()) < SetupAskRule.InUse && !recommendedScanning)
                {
                    recommendedPending = null;
                    recommendedTimer.Stop();
                    AutoScanRecommendedSetupAsync(pending.Reason).Forget();
                }
            };
        }
        if (!recommendedTimer.IsEnabled) recommendedTimer.Start();
    }

    /// <summary>The review's Off for an optional part: saved on this PC (recommended-setup.json), then the review opens again
    /// with a recommendation planned without it (or with it again).</summary>
    private void TurnRecommendedPartOff(PlanComponent part, bool off)
    {
        var directory = store?.DataDirectory;
        if (!RecommendedSetupMemory.Load(directory).WithOff(part, off).Save(directory))
        {
            ActionText.Text = "Martlet couldn't save that choice on this PC.";
            return;
        }
        ErrorLog.Info($"Recommended setup: {ComponentRanking.Name(part)} is {(off ? "off" : "on again")} on this PC's recommendation.");
        Dispatcher.BeginInvoke(() => OpenRecommendedSetupAsync().Forget());
    }

    /// <summary>Not now (in the review or on Home's notice): this PC doesn't ask about the same recommended setup again.</summary>
    private void DeclineRecommendedSetup(string fingerprint)
    {
        var directory = store?.DataDirectory;
        if (fingerprint.Length > 0 && !RecommendedSetupMemory.Load(directory).Decline(fingerprint, DateTimeOffset.UtcNow).Save(directory))
            ErrorLog.Warn("Recommended setup: couldn't save that you declined it; Martlet may ask again.");
        ErrorLog.Info($"Recommended setup: declined setup {fingerprint} on this PC.");
        ClearRecommendedNotice();
        ActionText.Text = "Nothing changed. Choose Recommended setup on Home whenever you like.";
    }

    private void ClearRecommendedNotice()
    {
        if (recommendedNotice is null) return;
        recommendedNotice = null;
        RenderHealth();
    }

    /// <summary>Home's notice while a better setup waits, or null. When nobody can do Thinking in it, Martlet can't reply: the
    /// notice is a warning and offers the free key (FreeKeyPrompt) when no hosted provider has one.</summary>
    private HealthIssue? RecommendedSetupIssue()
    {
        if (recommendedNotice is not { } notice) return null;
        var changes = notice.Recommendation.Changes;
        var needed = changes.Count(c => c.Benefit == SetupChangeBenefit.Required);
        var first = changes.OrderBy(c => c.Benefit).FirstOrDefault()?.Summary;
        HealthFix review = new("review", "Review", () => OpenRecommendedSetupAsync().Forget(), Passive: true);
        HealthFix decline = new("decline", "Not now", () => DeclineRecommendedSetup(notice.Recommendation.Fingerprint));
        if (notice.Recommendation.CannotReply)
        {
            var offerKey = FreeKeyPrompt.Shows(ConfiguredProviders());
            return new("recommended-setup", HealthLevel.Warning, FreeKeyPrompt.ProblemTitle,
                $"{Sentence(notice.Reason)}. {FreeKeyPrompt.Problem(offerKey)}",
                offerKey
                    ? [new("free-key", FreeKeyPrompt.AddLabel, () => OpenFreeKey(FreeKeyUse.Thinking, fromReview: false), Passive: true),
                       new("free-key-get", FreeKeyPrompt.GetLabel, () => OpenKeyPageFrom(null)), review, decline]
                    : [new("thinking", FreeKeyPrompt.OpenThinkingLabel, () => OpenFreeKey(FreeKeyUse.None, fromReview: false), Passive: true),
                       review, decline]);
        }
        return new("recommended-setup", HealthLevel.Notice, "A better setup is ready for your computers",
            $"{Sentence(notice.Reason)}. Martlet found {changes.Count} change{(changes.Count == 1 ? "" : "s")}" +
            (needed > 0 ? $" ({needed} needed)" : "") + (first is null ? "." : $", such as: {first}") + " Nothing changes until you review it.",
            [review, decline]);
    }

    // ---------- the free API key (FreeKeyPrompt) ----------

    /// <summary>Companion › Thinking, ready for a free NVIDIA Build key the owner pastes: at A cloud provider for Thinking itself
    /// (<see cref="FreeKeyUse.Thinking"/>), or at If Thinking fails (<see cref="FreeKeyUse.Fallback"/>). From the review
    /// (<paramref name="fromReview"/>), the review opens again with the new setup once a key is saved.</summary>
    private void OpenFreeKey(FreeKeyUse use, bool fromReview)
    {
        if (closing) return;
        if (!IsVisible || WindowState == WindowState.Minimized) ShowFromTray();
        OpenCompanion(CompanionTab.Thinking);
        if (openTab != CompanionTab.Thinking) return;
        if (use != FreeKeyUse.None)
        {
            if (use == FreeKeyUse.Thinking) tabPlace[CompanionTab.Thinking] = JobPlace.Cloud;
            freeKeyPreset = use;
            freeKeyFocus = true;
            RenderTab();
        }
        reviewAfterKey = fromReview;
        ActionText.Text = use switch
        {
            FreeKeyUse.Thinking => "Paste your NVIDIA Build key under A cloud provider, tick the box, then choose Use NVIDIA Build.",
            FreeKeyUse.Fallback => "Paste your NVIDIA Build key under If Thinking fails, tick the box, then choose Use as fallback.",
            _ => "Choose where Thinking runs in Companion › Thinking."
        };
    }

    /// <summary>Get a free key: NVIDIA Build's key page in the browser; why it couldn't open shows in <paramref name="status"/>
    /// (or Home's action line).</summary>
    private void OpenKeyPageFrom(TextBlock? status)
    {
        var text = FreeKeyPrompt.OpenKeyPage() ?? FreeKeyPrompt.OpenedText;
        if (status is not null) status.Text = text;
        else ActionText.Text = text;
    }

    /// <summary>The key box Add your key leads to: the cursor goes there, and the page scrolls to it, once it shows.</summary>
    private static void FocusWhenShown(Control box)
    {
        void Once(object sender, RoutedEventArgs e)
        {
            box.Loaded -= Once;
            box.Focus();
            box.BringIntoView();
        }
        box.Loaded += Once;
    }

    /// <summary>A hosted provider's key was saved or removed (Home draws again after every save): the recommended setup can change,
    /// so plan again. The review opens again when the owner came from it, or when it is open; Home's notice follows the new setup.</summary>
    private void FollowProviderKeys()
    {
        var now = string.Join(",", ConfiguredProviders().Order(StringComparer.Ordinal));
        if (knownProviders is null || knownProviders == now)
        {
            knownProviders = now;
            return;
        }
        knownProviders = now;
        if (closing || Role != DeviceRole.Companion) return;
        ErrorLog.Info("Recommended setup: your API keys changed, so Martlet plans again.");
        if (reviewAfterKey || recommendedSetupWindow is { IsLoaded: true })
        {
            reviewAfterKey = false;
            ReopenRecommendedSetupAsync().Forget();
        }
        else if (recommendedNotice is not null && InMartletNetwork()) AutoScanRecommendedSetupAsync("your API keys changed").Forget();
    }

    private async Task ReopenRecommendedSetupAsync()
    {
        recommendedSetupWindow?.Close();
        await OpenRecommendedSetupAsync();
    }
}
