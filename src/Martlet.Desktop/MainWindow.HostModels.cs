using System.IO;
using System.Text.Json;
using Martlet.Avatar.Audio2Face.Remote;
using Martlet.Core.Contracts;
using Martlet.Core.Platforms;
using Martlet.Core.Settings;
using Martlet.F5;

namespace Martlet.Desktop;

/// <summary>Which of this PC's saved choices point at a host route that now serves another model: the owner changed that role's
/// settings on the host (here or from another computer). A route names its model, and a host answers only for the model it
/// serves, so each would fail until it follows.</summary>
internal static class HostModelFollow
{
    /// <summary>The job routes handed to <paramref name="hostId"/> whose route it now advertises with another model.</summary>
    internal static IReadOnlyList<(SetupRoute Saved, HostRoute Now)> Jobs(AppSettings? settings, string hostId, IReadOnlyList<HostRoute> routes) =>
        (settings?.Setup?.Routes ?? [])
            .Where(r => r.Enabled != false && SelfHostSetup.IsGateway(r.RouteType) && r.Gateway?.HostId == hostId && r.GatewaySnapshot is not null)
            .Select(r => (Saved: r, Now: routes.FirstOrDefault(a => a.RouteId == r.GatewaySnapshot!.RouteId)))
            .Where(p => p.Now is not null && p.Now.ModelId != p.Saved.GatewaySnapshot!.ModelId)
            .Select(p => (p.Saved, p.Now!))
            .ToList();

    /// <summary>Deep thinking following its host route's new model on <paramref name="hostId"/> (the place it thinks on or one of
    /// its other places), or null when it doesn't think there or the model is the same.</summary>
    internal static DeepThinkingSettings? Deep(DeepThinkingSettings deep, string hostId, IReadOnlyList<HostRoute> routes)
    {
        DeepThinkingSettings Follow(DeepThinkingSettings place) =>
            place.Place == DeepThinkingPlace.Host && place.HostId == hostId &&
            routes.FirstOrDefault(r => r.RouteId == place.HostRoute) is { } route && route.ModelId != place.ModelId
                ? place with { ModelId = route.ModelId }
                : place;
        var before = deep.Places;
        var places = before.Select(Follow).ToArray();
        return places.Zip(before).All(pair => ReferenceEquals(pair.First, pair.Second)) ? null : places[0].WithPool(places.Skip(1));
    }

    /// <summary>The job a saved host route does (Speaking by the engine its route names), when it is one of the jobs.</summary>
    internal static HostJob? JobFor(SetupRoute saved) =>
        saved.Role == SetupRole.Tts
            ? SpeechEngines.ForRoute(saved.GatewaySnapshot?.RouteId) is { } engine ? HostJob.SpeakingFor(engine) : null
            : HostJob.For(saved.Role) is { } job && job.RouteId == saved.GatewaySnapshot?.RouteId ? job : null;
}

/// <summary>Changing what a paired host's roles run (model, GPU or CPU, graphics card...) from this PC, and following a new
/// model once the host serves it.</summary>
public partial class MainWindow
{
    /// <summary>Whether this PC changes <paramref name="host"/>'s roles from here: it knows how to reach it, and the host isn't a
    /// phone or tablet, whose roles are switched on on the device itself.</summary>
    private bool ChangesRolesOn(PairedHost host) =>
        host.CanLaunch && PlatformCatalog.ManagesRolesRemotely(PlatformDevice.FromHost(host.HostId, HardwareStore?.Find(host.HostId)));

    /// <summary>Opens a role's settings on a paired host ("host/role" from the Devices map): the role's dialog shows what it runs
    /// with now, and once the host applies the change, the run's check of the host (<see cref="CheckHostsAsync"/>) has Deep
    /// thinking and the jobs this PC hands it follow a new model.</summary>
    private void ChangeHostRole(string? argument)
    {
        if (argument?.Split('/') is not [var id, var kind] || FindHost(id) is not { } host) return;
        LaunchOnHost(host, HostAction.Change(kind));
    }

    /// <summary>Follows new models on <paramref name="hostIds"/> after their last check: Deep thinking (this PC's own choice)
    /// and the jobs handed to them think, listen and speak with the model each host serves now.</summary>
    private async Task FollowHostModelsAsync(IReadOnlyList<string> hostIds)
    {
        if (store is null || setupService is null || closing) return;
        var events = new List<string>();
        FollowDeepThinkingModels(hostIds, events);
        if (hostIds.Any(id => hostChecks.GetValueOrDefault(id)?.Routes is { } routes && HostModelFollow.Jobs(homeSettings, id, routes).Count > 0))
        {
            ChangeTurns.Turn? turn = null;
            try
            {
                turn = await changes.TakeAsync(lifetime.Token);
                await FollowJobModelsAsync(hostIds, events);
            }
            catch (OperationCanceledException) { }
            finally { turn?.Dispose(); }
        }
        if (events.Count == 0 || closing) return;
        ActionText.Text = string.Join(" ", events);
        RenderHome();
        if (DevicesPage.IsVisible) RenderMap();
    }

    /// <summary>Saves Deep thinking's new model when its host route serves another one now (deep-thinking.json).</summary>
    private void FollowDeepThinkingModels(IEnumerable<string> hostIds, List<string> events)
    {
        if (store is null || closing) return;
        var deep = ThinkingPoolSettings.Load(store.DataDirectory).Places;
        foreach (var id in hostIds)
        {
            if (hostChecks.GetValueOrDefault(id) is not { Reachable: true, Routes: { } routes } ||
                HostModelFollow.Deep(deep, id, routes) is not { } next)
                continue;
            try
            {
                if (!ThinkingPoolSettings.SavePlaces(store.DataDirectory, next)) continue;
            }
            catch (ContractException) { continue; }
            var was = deep.Places.First(p => p.HostId == id).ModelId;
            var now = next.Places.First(p => p.HostId == id).ModelId;
            deep = next;
            conversation?.ReloadThinkingPool();
            ErrorLog.Info($"Thinking pool: {id} now thinks with {now} (was {was}).");
            events.Add($"The Thinking pool on {id} now thinks with {now}.");
            if (openTab == CompanionTab.DeepThinking && !tabEdited) RenderTab();
        }
    }

    /// <summary>Saves each job's route again with the model its host serves now (the caller holds the
    /// <see cref="ChangeTurns"/> turn). Speaking keeps the voice it speaks with. Returns whether a route changed.</summary>
    private async Task<bool> FollowJobModelsAsync(IEnumerable<string> hostIds, List<string> events)
    {
        var changed = false;
        foreach (var id in hostIds)
        {
            if (closing || FindHost(id) is not { } host || hostChecks.GetValueOrDefault(id) is not { Reachable: true, Routes: { } routes })
                continue;
            foreach (var (saved, now) in HostModelFollow.Jobs(homeSettings, id, routes))
            {
                if (HostModelFollow.JobFor(saved) is not { } job) continue;
                try
                {
                    await SaveJobHostAsync(job, host, now, reference: job.RouteType == SetupRouteType.GatewayF5 ? saved.Reference : null);
                    changed = true;
                    ErrorLog.Info($"{job.Title}: {id} now serves {now.ModelId} (was {saved.GatewaySnapshot!.ModelId}); following it.");
                    events.Add($"{job.Title} on {id} now uses {now.ModelId}.");
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException or
                    ContractException or JsonException or ArgumentException or F5Exception)
                {
                    // Checks run every few seconds: say it once for each model it can't follow.
                    if (modelFollowFailures.Add($"{id}/{now.RouteId}/{now.ModelId}"))
                        ErrorLog.Warn($"{job.Title} couldn't follow {id}'s new model {now.ModelId}: {error.Message}");
                }
            }
        }
        return changed;
    }

    private readonly HashSet<string> modelFollowFailures = new(StringComparer.Ordinal);
}
