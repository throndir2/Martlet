using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Martlet.Core.Planning;
using Martlet.Core.Settings;

namespace Martlet.Desktop;

/// <summary>Recommendations that keep up with the times (docs/RECOMMENDATION_DESIGN.md, "Staying current"): after the daily model
/// catalog refresh, a computer coming back or staying away, or Martlet starting (a hardware change since it last ran), Martlet
/// plans again in the background, off the reply path. When a job has a clearly better choice, Home's Recommended setup button says
/// <em>A better setup is available</em>; Martlet never applies it by itself. A model its server retired is the exception: Home
/// proposes its replacement at once (Use ...), for Thinking and for If Thinking fails.</summary>
public partial class MainWindow
{
    internal const string BetterSetupTitle = RecommendedSetupReview.BetterTitle;

    /// <summary>The recommendation whose suggestions Home's Recommended setup button announces, until the owner reviews it.</summary>
    private NetworkRecommendation? betterSetup;
    private string? betterSetupReason;
    private bool betterChecking;

    /// <summary>The replacement Martlet proposes for each retired route ("origin|model"), worked out off the UI thread.</summary>
    private readonly Dictionary<string, string?> retiredReplacements = new(StringComparer.Ordinal);
    private bool retiredChecking;

    // ---------- A better setup is available ----------

    /// <summary>Plans again in the background (never while Martlet replies or hears you) and says on Home's Recommended setup
    /// button whether a better setup is available. Unlike a computer coming back, it asks nothing: the button only says so.</summary>
    private async Task CheckBetterSetupAsync(string reason)
    {
        if (closing || Role != DeviceRole.Companion || store is null || betterChecking || recommendedScanning ||
            !InMartletNetwork() && !SimulatedRecommendedSetup.Active) return;
        betterChecking = true;
        try
        {
            for (var waited = 0; Talking && waited < 300 && !closing; waited++) await Task.Delay(TimeSpan.FromSeconds(2), lifetime.Token);
            if (closing || Talking) return;
            if (await ScanRecommendedSetupAsync() is { } found) FollowBetterSetup(found.Recommendation, reason);
        }
        catch (OperationCanceledException) { }
        finally { betterChecking = false; }
    }

    /// <summary>Home's button follows <paramref name="recommendation"/>: it says a better setup is available while the
    /// recommendation has suggestions the owner hasn't seen in a review (<see cref="RecommendedSetupMemory"/> keeps the ones seen).</summary>
    private void FollowBetterSetup(NetworkRecommendation recommendation, string reason)
    {
        var seen = RecommendedSetupMemory.Load(store?.DataDirectory).WasDeclined(BetterKey(recommendation));
        var before = betterSetup?.SuggestionsFingerprint;
        betterSetup = recommendation.BetterSetupAvailable && !seen ? recommendation : null;
        betterSetupReason = betterSetup is null ? null : reason;
        if (before != betterSetup?.SuggestionsFingerprint)
            ErrorLog.Info(betterSetup is { } better
                ? $"Recommended setup: a better setup is available after {reason}: {string.Join(" ", better.Suggestions.Select(s => s.Text))}"
                : $"Recommended setup: checked after {reason}; no better setup to show" +
                  (recommendation.BetterSetupAvailable ? " (you saw this one in a review)." : "."));
        RenderBetterSetup();
    }

    private static string BetterKey(NetworkRecommendation recommendation) => "better:" + recommendation.SuggestionsFingerprint;

    /// <summary>The owner reviewed the better setup: Home stops saying so until another one is found.</summary>
    private void SeenBetterSetup(NetworkRecommendation recommendation)
    {
        var directory = store?.DataDirectory;
        if (!RecommendedSetupMemory.Load(directory).Decline(BetterKey(recommendation), DateTimeOffset.UtcNow).Save(directory))
            ErrorLog.Warn("Recommended setup: couldn't save that you saw the better setup; Home may say it again.");
        if (betterSetup is not null)
        {
            betterSetup = null;
            betterSetupReason = null;
            RenderBetterSetup();
        }
    }

    /// <summary>Home's Recommended setup button: "A better setup is available" under its label while one waits.</summary>
    private void RenderBetterSetup()
    {
        var shown = betterSetup is not null;
        BetterSetupText.Visibility = shown ? Visibility.Visible : Visibility.Collapsed;
        AutomationProperties.SetName(RecommendedSetupButton, shown ? "Recommended setup: a better setup is available" : "Recommended setup");
        RecommendedSetupButton.ToolTip = shown
            ? $"{BetterSetupTitle} ({betterSetupReason}). {string.Join(" ", betterSetup!.Suggestions.Select(s => s.Text))} Nothing changes until you review it."
            : "Work out the best use of all your computers and review the changes before anything changes";
    }

    /// <summary>The review's lock for <paramref name="job"/>: saved with the recommendation preferences (shared by your computers),
    /// then the review opens again planned with it. Unlocking remembers <paramref name="today"/>'s choice, so a later change by hand
    /// locks the job again.</summary>
    private void LockRecommendedJob(string job, bool locked, string? today)
    {
        var preferences = RecommendationPreferences.Load(store?.DataDirectory).WithLock(job, locked, today);
        ErrorLog.Info($"Recommended setup: {NetworkRecommender.JobTitle(job)} is {(locked ? "locked: Martlet keeps your choice" : "unlocked: Martlet may choose")}.");
        SaveRecommendationPreferences(preferences, reopen: true);
    }

    /// <summary>Reconfigure started: each unlocked job remembers the choice Martlet sets up (<see cref="RecommendationPreferences.SetUp"/>).</summary>
    private void RememberSetUp(NetworkRecommendation recommendation)
    {
        var preferences = RecommendationPreferences.Load(store?.DataDirectory);
        var next = preferences.SetUp(recommendation.Target.Jobs);
        if (next != preferences && next.Share() != preferences.Share()) SaveRecommendationPreferences(next, reopen: false);
    }

    // ---------- retired models ----------

    /// <summary>Finds, off the UI thread, whether the Thinking model or If Thinking fails' model is retired on its server: what a
    /// reply or a test found (HTTP 410), Martlet's list, or the model catalog's expiration date, which it then keeps in
    /// model-abilities.json like a reply's finding. It works out each retired model's replacement (the provider's own suggestion,
    /// else the smartest current model the catalog lists there that takes the same inputs) and Home proposes it at once.</summary>
    private async Task FollowRetiredAsync()
    {
        if (retiredChecking || closing || store is null) return;
        var routes = new List<(string Origin, string Model)>();
        if (homeSettings?.Setup?.Routes.FirstOrDefault(r => r.Role == SetupRole.Llm) is { RouteType: SetupRouteType.ChatCompletions } thinking)
            routes.Add((thinking.Origin, thinking.ModelId));
        if (homeSettings?.ThinkingFallback is { } fallback) routes.Add((fallback.Origin, fallback.ModelId));
        if (routes.Count == 0) return;
        retiredChecking = true;
        try
        {
            var directory = store.DataDirectory;
            var catalogs = ModelCatalogs;
            var now = DateTimeOffset.UtcNow;
            var found = await Task.Run(() =>
            {
                ModelCatalog? catalog = null;
                try { catalog = catalogs?.Load(); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException) { }
                var abilities = ModelAbilities.Load(directory);
                var today = DateOnly.FromDateTime(now.UtcDateTime);
                var expired = routes.Where(r => abilities.Find(r.Origin, r.Model)?.Retired is null &&
                    RetiredModels.Expired(catalog, r.Origin, r.Model, today)).ToList();
                foreach (var (origin, model) in expired)
                    abilities = abilities.With(new() { Origin = origin, ModelId = model, Retired = now, Source = CatalogExpiry, CheckedAt = now });
                var replacements = routes.Select(r => (r.Origin, r.Model,
                    Replacement: ChatCompletionsEndpointCatalog.RetiredOn(r.Origin, r.Model, abilities) is null ? null
                        : RetiredModels.Replacement(catalog, r.Origin, r.Model, abilities, today))).ToList();
                return (Expired: expired, Replacements: replacements);
            }, lifetime.Token);
            if (closing) return;
            foreach (var (origin, model, replacement) in found.Replacements)
            {
                var key = origin + "|" + model;
                if (replacement is not null && retiredReplacements.GetValueOrDefault(key) != replacement)
                    ErrorLog.Info($"{model} is retired on {origin}; Martlet proposes {replacement} instead.");
                retiredReplacements[key] = replacement;
            }
            foreach (var (origin, model) in found.Expired)
                RecordModelAbility(new() { Origin = origin, ModelId = model, Retired = now, Source = CatalogExpiry, CheckedAt = now });
            QueueHealth();
        }
        catch (OperationCanceledException) { }
        finally { retiredChecking = false; }
    }

    /// <summary>Where Martlet found a model retired when the model catalog said it (OpenRouter's expiration date passed).</summary>
    internal const string CatalogExpiry = "the model catalog (its expiration date passed)";

    /// <summary>The model Martlet proposes instead of <paramref name="model"/>, retired on <paramref name="origin"/>: the one worked
    /// out with the catalog, else the provider's own suggestion; null when there is none.</summary>
    private string? RetiredReplacement(string origin, string model, RetiredModel retired) =>
        retiredReplacements.GetValueOrDefault(origin + "|" + model) ?? retired.Suggestion;

    /// <summary>The fixes on Home's issue about the retired Thinking model: Use the replacement (it saves Thinking with that model
    /// on the same server and key), then Change thinking.</summary>
    private HealthFix[] RetiredThinkingFixes(SetupRoute llm, RetiredModel retired) =>
        RetiredReplacement(llm.Origin, llm.ModelId, retired) is { } replacement
            ? [new("use-replacement", $"Use {replacement}", () => UseRetiredReplacementAsync(llm.Origin, replacement).Forget()),
               OpenThinking("Change thinking")]
            : [OpenThinking("Change thinking")];

    private HealthFix OpenThinking(string label) => new("open-thinking", label, () => OpenCompanion(CompanionTab.Thinking), Passive: true);

    /// <summary>The sentence on Home's issue: which model Martlet proposes instead, or the provider's remedy.</summary>
    private string RetiredProposal(string origin, string model, RetiredModel retired) =>
        RetiredReplacement(origin, model, retired) is { } replacement
            ? $"Martlet proposes {replacement}{(replacement == retired.Suggestion ? $", {retired.Server}'s current choice" : ", the smartest current model there that takes the same inputs")}. Choose Use {replacement}."
            : retired.Remedy;

    private async Task UseRetiredReplacementAsync(string origin, string replacement)
    {
        if (closing) return;
        if (!await SaveSectionRouteAsync(HostJob.Thinking, settings => ChatCompletionsSetup.SelectRoute(settings, origin, replacement), null,
                $"Thinking now uses {replacement}, in place of the model its server retired."))
            return;
        ErrorLog.Info($"Thinking now uses {replacement} on {origin}: the model before was retired (you chose the replacement Martlet proposed).");
        CheckNewModelContextAsync().Forget();
        QueueHealth();
    }

    /// <summary>Home's issue when If Thinking fails' model is retired on its server, with its replacement; null otherwise.</summary>
    private HealthIssue? RetiredFallbackIssue(ModelAbilities abilities)
    {
        if (homeSettings?.ThinkingFallback is not { } fallback ||
            ChatCompletionsEndpointCatalog.RetiredOn(fallback.Origin, fallback.ModelId, abilities) is not { } retired) return null;
        var replacement = RetiredReplacement(fallback.Origin, fallback.ModelId, retired);
        var provider = FallbackProviders.FirstOrDefault(p => p.BaseUrl == fallback.Origin) ?? new CloudProvider(retired.Server, fallback.Origin, true, null, false);
        HealthFix[] fixes = replacement is null
            ? [OpenThinking("Change the fallback")]
            : [new("use-fallback-replacement", $"Use {replacement}",
                () => SaveFallbackAsync(provider, fallback.Origin, replacement, new PasswordBox(), consent: true).Forget()),
               OpenThinking("Change the fallback")];
        return new("fallback-retired", HealthLevel.Warning, "Your backup Thinking model was retired",
            $"If Thinking fails, Martlet asks {fallback.ModelId}, but {retired.Server} retired it" +
            (retired.Since is { } since ? $" ({retired.Source}, {since.LocalDateTime:d MMM})" : "") + ". " +
            RetiredProposal(fallback.Origin, fallback.ModelId, retired), fixes);
    }
}
