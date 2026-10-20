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

/// <summary>More of MainWindow.Situations.cs, in a file of its own so that each file stays under 20 KB.</summary>
public partial class MainWindow
{
    /// <summary>Turns the game watch on while the owner said this PC is used for games, off otherwise.</summary>
    private void FollowGameWatch(bool on)
    {
        if (on && gameWatch is null)
        {
            gameWatch = new GameWatch(() => new WindowsPcActivitySource());
            gameWatch.Changed += game => Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
            {
                if (closing) return;
                ErrorLog.Info(game is null ? "Situations: the game on this PC ended." : $"Situations: a game runs on this PC ({game}).");
                ObserveSituation();
            });
        }
        if (gameWatch is not null) gameWatch.On = on;
    }

    /// <summary>Gives every conversation on this PC the situation's Thinking route when live Thinking moves, and the saved route
    /// again when it comes back. An open talk window follows once nothing runs and nobody talks. The route is made again when
    /// what it is made from changes (the saved Thinking route, the host's pairing and route, the If Thinking fails settings).</summary>
    private void FollowSituationRoute(SituationDecision decision)
    {
        var saved = homeSettings.ThinkingRoute();
        // A FIXTURE network's hosts are made up, so its decisions only show; the conversation keeps its route.
        var wanted = decision.Moves && saved is not null && simulatedSituation?.Network is null
            ? $"{decision.Key}|{saved.ConfigurationRevision}|{SituationIdentity(decision.Place!)}" : null;
        if (wanted == situationApplied) return;
        SituationOverride? moved = null;
        if (wanted is not null)
        {
            var route = SituationRoute(decision.Place!);
            if (route is null)
            {
                if (situationFailed != wanted) ErrorLog.Warn($"Situations: Martlet couldn't use {decision.Place!.Text} for live Thinking yet; it tries again.");
                situationFailed = wanted;
                wanted = null;
            }
            else moved = new(decision.Key, saved!.ConfigurationRevision, route, decision.Place!.Text)
            {
                Fallback = decision.Place!.Kind == LivePlaceKind.Hosted ? homeSettings?.ThinkingFallback : null
            };
        }
        if (wanted == situationApplied) return;
        situationApplied = wanted;
        LiveSituation.Current = moved;
        openConversation?.ReloadWhenQuiet(moved is null ? "Live Thinking goes back to its usual route." : $"Live Thinking moves to {moved.Text}.");
    }

    /// <summary>What <paramref name="place"/>'s route is made from, so a change there (a host paired again, a new key for If
    /// Thinking fails) makes the route again. Never a secret: IDs, addresses and fingerprints.</summary>
    private string SituationIdentity(LivePlace place) => place.Kind switch
    {
        LivePlaceKind.Host => homeHosts.FirstOrDefault(h => h.HostId == place.HostId) is { } host
            ? $"{host.Pairing.Origin}|{host.Pairing.SpkiFingerprint}|{host.Pairing.DeviceId}|{host.Pairing.CredentialId}|" +
              (hostChecks.GetValueOrDefault(host.HostId)?.Routes?.FirstOrDefault(r => r.RouteId == HostRoute.OllamaChatRouteId) is { } route
                  ? $"{route.DestinationId}|{route.ModelId}|{route.ModelSha256}|{route.MaximumRequestBytes}" : "")
            : "",
        LivePlaceKind.Hosted => homeSettings?.ThinkingFallback is { } fallback
            ? $"{fallback.Origin}|{fallback.ModelId}|{fallback.CredentialId}|{fallback.ConfigurationRevision}" : "",
        _ => place.Model
    };

    /// <summary>The Thinking route for <paramref name="place"/>, from what this PC holds: the host's pairing and the Ollama chat
    /// route its last check advertised, the If Thinking fails endpoint, or Ollama on this PC. Null when it can't be made now.</summary>
    private SetupRoute? SituationRoute(LivePlace place)
    {
        switch (place.Kind)
        {
            case LivePlaceKind.Host:
                if (homeHosts.FirstOrDefault(h => h.HostId == place.HostId) is not { } host ||
                    hostChecks.GetValueOrDefault(host.HostId)?.Routes?.FirstOrDefault(r => r.RouteId == HostRoute.OllamaChatRouteId &&
                        r.ModelId == place.Model) is not { } route)
                    return null;
                var endpoint = new GatewayEndpointSettings
                {
                    SchemaVersion = 1, Origin = host.Pairing.Origin, HostId = host.HostId,
                    SpkiFingerprint = host.Pairing.SpkiFingerprint, DeviceRole = SelfHostSetup.GatewayRole
                };
                try
                {
                    return SituationRoutes.Host(endpoint, HostPairingCredential.ToGuid(host.Pairing.CredentialId), host.Pairing.DeviceId,
                        route.Snapshot(SetupRouteType.GatewayOllama));
                }
                catch (Exception error) when (error is InvalidOperationException or Martlet.Core.Contracts.ContractException) { return null; }
            case LivePlaceKind.Hosted:
                return homeSettings?.ThinkingFallback is { } fallback ? SituationRoutes.Hosted(fallback) : null;
            default:
                return SituationRoutes.Local(LocalOllamaBaseUrl, place.Model);
        }
    }

    /// <summary>What the situation rules read, all known on this PC without a request (or the FIXTURE network's).</summary>
    private SituationFacts SituationFactsNow()
    {
        var directory = store?.DataDirectory;
        var playsGames = SituationPreferences.PlaysGames(directory, ClusterDevice, () => GamesHere);
        var backupAllowed = SituationPreferences.BackupAllowed(directory);
        var away = presenceWatch.Hosts(HostPresence.Clock.GetUtcNow())
            .Where(h => h.State is NodePresenceState.Missing or NodePresenceState.Away or NodePresenceState.Returning)
            .Select(h => h.HostId).ToHashSet(StringComparer.Ordinal);
        if (simulatedSituation?.Network is { } network)
        {
            var fixture = SimulatedSituation.Facts(network, backupAllowed);
            if (simulatedSituation.Away && fixture.Hosts.Count > 0) away.Add(fixture.HomeHost ?? fixture.Hosts[0].HostId);
            return fixture with { Away = away };
        }
        var own = WorkSharingRoster.Shared.OwnHostId ?? homeHosts.FirstOrDefault(h => h.Method == HostSetupMethod.ThisPcDocker)?.HostId;
        var route = homeSettings.ThinkingRoute() is { Enabled: not false } enabled ? enabled : null;
        var home = LiveHome.None;
        string? homeHost = null;
        if (route is not null)
        {
            if (route.RouteType == SetupRouteType.ChatCompletions && LocalModelServers.IsOnThisComputer(route.Origin)) home = LiveHome.ThisPc;
            else if (route.RouteType == SetupRouteType.GatewayOllama && route.Gateway is { } gateway)
            {
                home = gateway.HostId == own ? LiveHome.ThisPc : LiveHome.Host;
                if (home == LiveHome.Host) homeHost = gateway.HostId;
            }
            else home = LiveHome.Online;
        }
        if (simulatedSituation is { Away: true } && homeHost is not null) away.Add(homeHost);
        // Other paired hosts with a Thinking chat model that answer, the Thinking list's order first (When the Thinking model is busy).
        var listed = WorkSharingRoster.Shared.Pool(directory, PoolAreas.Thinking)?.Members.Where(m => !m.Off && m.HostId is not null)
            .Select(m => m.HostId!).ToList() ?? [];
        var hosts = homeHosts.Where(h => !h.Shared && h.HostId != own && h.HostId != homeHost && !HostPresence.IsOffline(h.HostId))
            .Select(h => (Host: h, Route: hostChecks.GetValueOrDefault(h.HostId) is { Reachable: true, Routes: { } routes }
                ? routes.FirstOrDefault(r => r.RouteId == HostRoute.OllamaChatRouteId) : null))
            .Where(p => p.Route is not null)
            .OrderBy(p => listed.IndexOf(p.Host.HostId) is var at and >= 0 ? at : int.MaxValue).ThenBy(p => p.Host.HostId, StringComparer.Ordinal)
            .Select(p => new LiveHost(p.Host.HostId, p.Host.HostId, p.Route!.ModelId)).ToList();
        var (backup, backupModel) = SituationBackup();
        return new()
        {
            Home = home, HomeHost = homeHost, HomeName = homeHost, HomeModel = route?.ModelId, PlaysGames = playsGames, Away = away,
            Hosts = hosts, Backup = backup, BackupModel = backupModel, BackupAllowed = backupAllowed,
            LocalModel = home == LiveHome.ThisPc && route is not null && IsLocalOllama(route) ? route.ModelId : situationLocalModel
        };
    }

    /// <summary>The hosted backup: If Thinking fails, when it isn't on this PC and has the key it needs. Its name and model, or
    /// nulls.</summary>
    private (string? Name, string? Model) SituationBackup()
    {
        if (homeSettings?.ThinkingFallback is not { } fallback || LocalModelServers.IsOnThisComputer(fallback.Origin)) return (null, null);
        var named = ChatCompletionsEndpointCatalog.Named(fallback.Origin);
        if (named is not null && fallback.CredentialId is null) return (null, null);
        return (named?.Name ?? (Uri.TryCreate(fallback.Origin, UriKind.Absolute, out var uri) ? uri.Host : fallback.Origin), fallback.ModelId);
    }

    /// <summary>The model Ollama on this PC has for Thinking while a host does it (for Host away): read in the background every 5
    /// minutes, the recommended models first.</summary>
    private void FindSituationLocalModel()
    {
        if (situationLocalAt != 0 && System.Diagnostics.Stopwatch.GetElapsedTime(situationLocalAt) < TimeSpan.FromMinutes(5)) return;
        situationLocalAt = System.Diagnostics.Stopwatch.GetTimestamp();
        Task.Run(async () =>
        {
            var models = await LocalOllama.ServedModelsAsync(TimeSpan.FromSeconds(3), lifetime.Token).ConfigureAwait(false);
            var model = models is null ? null
                : LocalChatModels.Select(m => m.Id).FirstOrDefault(id => LocalOllama.Serves(models, id)) ?? models.FirstOrDefault(ServedModels.Chats);
            await Dispatcher.InvokeAsync(() =>
            {
                if (closing || model == situationLocalModel) return;
                situationLocalModel = model;
                ObserveSituation();
            });
        }).Forget();
    }

    /// <summary>While gaming, every 15 seconds: when the card is short of memory, unloads Thinking's model in Ollama on this PC
    /// (once a game); otherwise keeps a loaded model loaded (Ollama's keep-alive), so the change back is quick. Loopback only.</summary>
    private void KeepSituationCard(SituationDecision decision, SituationFacts facts)
    {
        var route = homeSettings.ThinkingRoute();
        if (situationCardBusy || route is null || !IsLocalOllama(route) ||
            situationCardAt != 0 && System.Diagnostics.Stopwatch.GetElapsedTime(situationCardAt) < SituationCardEvery) return;
        situationCardAt = System.Diagnostics.Stopwatch.GetTimestamp();
        situationCardBusy = true;
        var model = route.ModelId;
        // While no other place can answer, the conversation uses the model itself (its talk window keeps it warm). After Free up
        // this PC, Martlet rests: it doesn't load the model again by itself.
        var inUse = decision.Place?.Kind == LivePlaceKind.ThisPc;
        var touch = resting is null && !inUse &&
            (situationTouchedAt == 0 || System.Diagnostics.Stopwatch.GetElapsedTime(situationTouchedAt) >= SituationKeepLoaded);
        var unloaded = situationUnloaded;
        Task.Run(async () =>
        {
            var (note, unload, touched) = await SituationCardAsync(model, unloaded, touch, lifetime.Token).ConfigureAwait(false);
            await Dispatcher.InvokeAsync(() =>
            {
                situationCardBusy = false;
                if (closing) return;
                if (unload) situationUnloaded = true;
                if (touched) situationTouchedAt = System.Diagnostics.Stopwatch.GetTimestamp();
                if (note == situationCard) return;
                situationCard = note;
                if (situation is { } now && situationFacts is { } known) ReportSituation(now, known);
            });
        }).Forget();
    }

    private static async Task<(string Note, bool Unloaded, bool Touched)> SituationCardAsync(string model, bool unloaded, bool touch,
        CancellationToken token)
    {
        try
        {
            using var client = new HttpClient(new SocketsHttpHandler { UseProxy = false, ConnectTimeout = TimeSpan.FromSeconds(3) })
            {
                Timeout = Timeout.InfiniteTimeSpan
            };
            var memory = await LocalDeepThinking.GraphicsMemoryAsync(token).ConfigureAwait(false);
            var loaded = await OllamaSideBySide.LoadedAsync(client, LocalOllama.OriginUri, token).ConfigureAwait(false);
            var mine = loaded is null ? null : OllamaSideBySide.Find(loaded, model);
            if (memory is not { TotalBytes: > 0, UsedBytes: { } used })
                return ($"Martlet can't read how much graphics memory is free, so {model} stays as Ollama keeps it.", false, false);
            var free = $"{Gb(memory.TotalBytes - used)} GB free of {Gb(memory.TotalBytes)} GB";
            if (LiveSituations.ShortOfMemory(memory.TotalBytes, used))
            {
                if (mine is null || unloaded) return ($"{free}: the game needs the card's memory, and {model} isn't loaded.", false, false);
                var done = await OllamaSideBySide.LoadAsync(client, LocalOllama.OriginUri, model, unload: true, TimeSpan.FromSeconds(30), token)
                    .ConfigureAwait(false);
                if (done) ErrorLog.Info($"Situations: the game needs the graphics card's memory ({free}), so Martlet unloaded {model}. " +
                    "It loads again when the game ends.");
                return ($"{free}: the game needs the card's memory, so Martlet unloaded {model}.", done, false);
            }
            var touched = false;
            if (mine is not null && touch)
                touched = await OllamaSideBySide.LoadAsync(client, LocalOllama.OriginUri, model, unload: false, TimeSpan.FromSeconds(30), token)
                    .ConfigureAwait(false);
            return ($"{free}: {model} {(mine is null ? "isn't loaded" : "stays loaded, so the change back is quick")}.", false, touched);
        }
        catch (OperationCanceledException) { return ("Martlet stopped checking the graphics card.", false, false); }
        catch (Exception error) when (error is HttpRequestException or IOException or InvalidOperationException)
        {
            return ($"Martlet couldn't check the graphics card ({error.GetType().Name}).", false, false);
        }
    }

    private static string Gb(long bytes) => (bytes / 1073741824d).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>Companion › Thinking's line on the situation now ("While gaming: A game (Elden Ring) runs on this PC, so live
    /// Thinking leaves its graphics card for gpu-box (qwen3:8b). ...").</summary>
    private string SituationLine() => situation is not { } now ? "Situation: Normal."
        : $"Situation: {now.Title}{(simulatedSituation is null ? "" : " (FIXTURE)")} — {now.Why}";

    private TextBlock SituationNowLine()
    {
        var line = new TextBlock { Text = SituationLine(), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0) };
        line.SetResourceReference(StyleProperty, "Muted");
        AutomationProperties.SetAutomationId(line, "ThinkingSituationNow");
        return situationNowText = line;
    }

    /// <summary>Writes situation.json when what it says changed. A failed write only costs that status.</summary>
    private void ReportSituation(SituationDecision decision, SituationFacts facts)
    {
        if (store is null) return;
        var now = LiveSituation.Current;
        var report = new SituationReport
        {
            UpdatedAt = DateTimeOffset.UtcNow, Situation = decision.Situation,
            Normal = facts.Home switch
            {
                LiveHome.None => "not set up",
                LiveHome.ThisPc => $"this PC's {facts.HomeModel}",
                LiveHome.Host => $"{facts.HomeName} ({facts.HomeModel})",
                _ => $"online ({facts.HomeModel})"
            },
            Now = decision.Place?.Text, NowKind = decision.Place?.Kind, NowHost = decision.Place?.HostId,
            Order = [.. decision.Order.Select(p => p.Text)], Why = decision.Why,
            Applied = now is not null && now.Key == decision.Key, PlaysGames = facts.PlaysGames, Game = facts.Game,
            Away = [.. facts.Away.Order(StringComparer.Ordinal)], BackupAllowed = facts.BackupAllowed,
            Online = SituationPreferences.Online(store.DataDirectory), Card = situationCard, Simulated = simulatedSituation is not null
        };
        var text = string.Join("|", report.Situation, report.Now, report.Applied, report.Game, string.Join(",", report.Away), report.Card,
            report.Why, report.Online);
        if (text == situationReported) return;
        situationReported = text;
        try { report.Save(store.DataDirectory); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            ErrorLog.Warn($"Couldn't save {SituationReport.FileName}: {error.Message}");
        }
    }

    /// <summary>Recommended setup's three plans for <paramref name="recommendation"/>.</summary>
    private IReadOnlyList<SituationPlan> SituationPlansFor(SetupRequestBuild build, NetworkRecommendation recommendation)
    {
        string Name(string? id) => id is null or "" ? "your companion PCs" : build.Names.GetValueOrDefault(id)
            ?? build.Request.Machines.FirstOrDefault(m => m.Specs.Id == id)?.Specs.Name ?? id;
        var (backup, _) = SituationBackup();
        return SituationPlans.For(build.Request, recommendation, Name, PlanningCatalog.Current.WithServed(build.Request.ServedModels), backup,
            SituationPreferences.BackupAllowed(store?.DataDirectory));
    }
}
