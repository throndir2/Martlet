using System.Windows;
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
        if (!InMartletNetwork())
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
        var inputs = Inputs();
        var directory = store?.DataDirectory;
        var poolSettings = ThinkingPoolSettings.Load(directory);
        var pool = poolSettings.Places.Places.Where(p => p.OnHostRole && p.HostId is not null).Select(p => p.HostId!).ToArray();
        return RecommendedSetupInputs.Sources(inputs, NetworkMap.Build(inputs), ClusterDevice, OwnHostId(), ThisPcDiskFreeGb(),
            offlineFor: OfflineFor, sharing: directory is null ? null : WorkSharingSettings.Load(directory), thinkingPool: pool,
            poolOptOut: poolSettings.LeftByOwner, voiceEngine: SpeakingEngineChoice.Current.HostRoleKind, configuredProviders: ConfiguredProviders(),
            offlineGrace: directory is null ? null : TimeSpan.FromMinutes(NodePresenceSettings.AwayMinutes(directory)));
    }

    private void ShowRecommendedSetup(SetupRequestBuild build, NetworkRecommendation recommendation)
    {
        var review = RecommendedSetupReview.From(recommendation, build);
        var window = new RecommendedSetupWindow(review, RecommendedPrepare(recommendation), RecommendedApply(recommendation));
        if (IsVisible) window.Owner = this;
        window.Declined += DeclineRecommendedSetup;
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

    /// <summary>What Reconfigure needs first, from the executor's preflight; null while this Martlet can't reconfigure.</summary>
    private Func<CancellationToken, Task<RecommendedSetupPreflightView>>? RecommendedPrepare(NetworkRecommendation recommendation) => null;

    /// <summary>Applies the recommendation on every computer with progress; null while this Martlet can't reconfigure.</summary>
    private Func<RecommendedSetupPreflightView, IReadOnlyDictionary<string, string>, IProgress<string>, Task<string>>? RecommendedApply(
        NetworkRecommendation recommendation) => null;

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

    /// <summary>Home's notice while a better setup waits, or null.</summary>
    private HealthIssue? RecommendedSetupIssue()
    {
        if (recommendedNotice is not { } notice) return null;
        var changes = notice.Recommendation.Changes;
        var needed = changes.Count(c => c.Benefit == SetupChangeBenefit.Required);
        var first = changes.OrderBy(c => c.Benefit).FirstOrDefault()?.Summary;
        return new("recommended-setup", HealthLevel.Notice, "A better setup is ready for your computers",
            $"{Sentence(notice.Reason)}. Martlet found {changes.Count} change{(changes.Count == 1 ? "" : "s")}" +
            (needed > 0 ? $" ({needed} needed)" : "") + (first is null ? "." : $", such as: {first}") + " Nothing changes until you review it.",
            [new("review", "Review", () => OpenRecommendedSetupAsync().Forget(), Passive: true),
             new("decline", "Not now", () => DeclineRecommendedSetup(notice.Recommendation.Fingerprint))]);
    }
}
