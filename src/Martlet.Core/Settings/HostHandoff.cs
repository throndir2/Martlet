using Martlet.Core.Contracts;

namespace Martlet.Core.Settings;

/// <summary>Hands one job (the STT, LLM or TTS role) to a paired Martlet host's gateway route, or back to the route it
/// used before. The host route reuses the pairing's device credential; a replaced cloud key is detached (listed for
/// explicit removal), never orphaned, and handing the job back reattaches it.</summary>
public static class HostHandoff
{
    /// <summary>The role's route becomes the host's enabled gateway route with the owner's recorded selection. A GatewayF5
    /// route also carries the applied reference voice (<paramref name="reference"/>), which it needs to be enabled.</summary>
    public static AppSettings ToHost(AppSettings settings, SetupRouteType routeType, GatewayEndpointSettings endpoint,
        Guid credentialId, string deviceId, GatewayRouteSnapshot snapshot, F5ReferenceSettings? reference = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(endpoint);
        ArgumentNullException.ThrowIfNull(snapshot);
        settings.Validate();
        ContractRules.Require(settings.Setup is not null, "Complete Setup once before handing a job to a host.");
        var named = SelfHostSetup.Gateway(routeType);
        var previous = settings.Setup!.Routes.SingleOrDefault(route => route.Role == named.Role);
        var route = new SetupRoute
        {
            RouteSchemaVersion = 1, RouteType = routeType, Enabled = true, Role = named.Role, ProviderAlias = named.Alias,
            Origin = endpoint.Origin, ModelId = snapshot.ModelId, CredentialId = credentialId,
            ConfigurationRevision = Guid.NewGuid(), Gateway = endpoint, GatewayDeviceId = deviceId, GatewaySnapshot = snapshot,
            Reference = reference
        };
        route = route with { Consent = route.Selection() };
        return SetupSettings.QueueReplacedCredential(Replace(settings, route), previous);
    }

    /// <summary>Restores the route the role used before it went to a host (saved verbatim, with its recorded selection).
    /// Its detached key is reattached when it is still listed for removal; otherwise the route comes back without a key.</summary>
    public static AppSettings Back(AppSettings settings, SetupRoute saved)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(saved);
        settings.Validate();
        ContractRules.Require(settings.Setup is not null && !SelfHostSetup.IsGateway(saved.RouteType),
            "Only a route that did not use a host can be restored.");
        var setup = settings.Setup!;
        var route = saved;
        if (saved.CredentialId is { } id)
        {
            var detached = setup.PendingRemovals.FirstOrDefault(item => item.CredentialId == id && item.Role == saved.Role);
            if (detached is not null)
                setup = setup with { PendingRemovals = setup.PendingRemovals.Where(item => item != detached).ToArray() };
            else
            {
                route = saved with { CredentialId = null, Consent = null, ConfigurationRevision = Guid.NewGuid() };
                route = route with { Consent = route.Selection() };
            }
        }
        return Replace(settings with { Setup = setup }, route);
    }

    // Unlike SetupSettings.ReplaceRoute this may change the route type while the role's replaced key is listed for removal.
    private static AppSettings Replace(AppSettings settings, SetupRoute route)
    {
        route.Validate();
        var updated = settings with
        {
            Setup = settings.Setup! with
            {
                Routes = settings.Setup.Routes.Where(item => item.Role != route.Role).Append(route).OrderBy(item => item.Role).ToArray()
            }
        };
        updated.Validate();
        return updated;
    }
}
