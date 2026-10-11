using System.IO;
using System.Net.Http;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Threading;
using Martlet.Audio;
using Martlet.Audio.Windows;
using Martlet.Avatar.Audio2Face.Remote;
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
            if (situationApplied is not null || LiveSituation.Current is not null)
            {
                situationApplied = null;
                LiveSituation.Current = null;
            }
            situation = null;
            return;
        }
        var facts = SituationFactsNow();
        // The game watch runs only while it can matter: live Thinking on this PC's card, or this PC's own host service.
        var ownHost = WorkSharingRoster.OwnHostId ?? homeHosts.FirstOrDefault(h => h.Method == HostSetupMethod.ThisPcDocker)?.HostId;
        FollowGameWatch(facts.PlaysGames && simulatedSituation is null && (facts.Home == LiveHome.ThisPc || ownHost is not null));
        facts = facts with { Game = simulatedSituation is { } fixture ? fixture.Game : gameWatch?.Game };
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
        var saved = homeSettings?.Setup?.Routes.FirstOrDefault(r => r.Role == SetupRole.Llm);
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
        var own = WorkSharingRoster.OwnHostId ?? homeHosts.FirstOrDefault(h => h.Method == HostSetupMethod.ThisPcDocker)?.HostId;
        var route = homeSettings?.Setup?.Routes.FirstOrDefault(r => r.Role == SetupRole.Llm && r.Enabled != false);
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
        var listed = WorkSharingRoster.Pool(directory, PoolAreas.Thinking)?.Members.Where(m => !m.Off && m.HostId is not null)
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
        var route = homeSettings?.Setup?.Routes.FirstOrDefault(r => r.Role == SetupRole.Llm);
        if (situationCardBusy || route is null || !IsLocalOllama(route) ||
            situationCardAt != 0 && System.Diagnostics.Stopwatch.GetElapsedTime(situationCardAt) < SituationCardEvery) return;
        situationCardAt = System.Diagnostics.Stopwatch.GetTimestamp();
        situationCardBusy = true;
        var model = route.ModelId;
        // While no other place can answer, the conversation uses the model itself (its talk window keeps it warm).
        var inUse = decision.Place?.Kind == LivePlaceKind.ThisPc;
        var touch = !inUse && (situationTouchedAt == 0 || System.Diagnostics.Stopwatch.GetElapsedTime(situationTouchedAt) >= SituationKeepLoaded);
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
        : $"Situation: {now.Title}{(simulatedSituation is null ? "" : " (FIXTURE)")}. {now.Why}";

    private TextBlock SituationNowLine()
    {
        var line = new TextBlock { Text = SituationLine(), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0) };
        line.SetResourceReference(StyleProperty, "Muted");
        AutomationProperties.SetAutomationId(line, "ThinkingSituationNow");
        return situationNowText = line;
    }

    /// <summary>FIXTURE (MARTLET_SIMULATE_SITUATION): Companion › Thinking's buttons that start or end a simulated game and make the
    /// Thinking host simulated away or back. They change nothing saved.</summary>
    private Border? SituationFixtureCard()
    {
        if (simulatedSituation is not { } fixture) return null;
        var game = PageButton(fixture.Game is null ? "Simulate a game" : "End the simulated game", () =>
        {
            fixture.Game = fixture.Game is null ? SimulatedSituation.GameName : null;
            ObserveSituation();
            RenderTab();
        }, id: "SituationSimulateGame");
        var away = PageButton(fixture.Away ? "The simulated host answers again" : "Simulate the Thinking host away", () =>
        {
            fixture.Away = !fixture.Away;
            ObserveSituation();
            RenderTab();
        }, id: "SituationSimulateAway");
        return Card(Heading("FIXTURE: situations"),
            Note($"{SimulatedSituation.Variable} is set: {fixture.Describe()}. Nothing is played, contacted or saved.", new Thickness(0, 0, 0, 0)),
            Row(game, away));
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
        return SituationPlans.For(build.Request, recommendation, Name, FootprintCatalog.Default.WithServed(build.Request.ServedModels), backup,
            SituationPreferences.BackupAllowed(store?.DataDirectory));
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
