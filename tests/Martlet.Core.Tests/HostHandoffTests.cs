using Martlet.Core.Settings;

namespace Martlet.Core.Tests;

// Who does what: jobs move between their Setup route and paired hosts, and back, through the real settings store.
public sealed class HostHandoffTests : IDisposable
{
    private readonly string directory = Path.Combine(AppContext.BaseDirectory, "host-handoff", Guid.NewGuid().ToString("N"));
    private SettingsStore Store => new(directory);
    private static readonly Guid Pairing = Guid.NewGuid();

    private static GatewayEndpointSettings Endpoint(string host = "gpu-a") => new()
    {
        SchemaVersion = 1, Origin = host == "gpu-a" ? "https://192.168.1.20:9443" : "https://192.168.1.30:9443", HostId = host,
        SpkiFingerprint = "sha256:" + new string(host == "gpu-a" ? 'a' : 'b', 64), DeviceRole = "voice"
    };

    private static GatewayRouteSnapshot Snapshot(SetupRouteType type, string model)
    {
        var named = SelfHostSetup.Gateway(type);
        return new()
        {
            SchemaVersion = 1, RouteType = type, RegistryId = SelfHostSetup.RegistryId, RegistryVersion = SelfHostSetup.RegistryVersion,
            RouteId = named.RouteId, Path = named.Path, ContractId = named.ContractId, ContractVersion = "1.0", DestinationId = "host",
            WorkerId = "relay", WorkerPackageRevision = "1.0.0", AdapterVersion = "1.0.0", ModelId = model, ModelRevision = "r1",
            ModelSha256 = new string('c', 64), ArtifactIdentitySha256 = "sha256:" + new string('d', 64), MaximumRequestBytes = 1_400_000,
            MaximumInputBytes = 960_000, MaximumOutputBytes = 16_384, MaximumEventBytes = 16_384, MaximumEvents = 16,
            MaximumStreamBytes = 262_144, MaximumDurationSeconds = 60, Cancellation = GatewayCancellationMode.RequestAbort,
            ObservedAtUtc = DateTimeOffset.UtcNow, ProbeRevision = Guid.NewGuid()
        };
    }

    private static AppSettings Keyed(AppSettings settings, SetupRole role, string model, string? voice = null)
    {
        settings = SetupSettings.SelectRoute(settings, role, model, voice);
        var route = settings.Setup!.Routes.Single(r => r.Role == role);
        settings = SetupSettings.ReplaceRoute(settings, route.WithCredential(Guid.NewGuid()));
        return SetupSettings.SetRouteEnabled(settings, role, true, true);
    }

    private async Task<string> SaveAsync(AppSettings settings, string? revision)
    {
        var saved = await Store.SaveAsync(settings, revision);
        Assert.True(saved.Saved, saved.Error?.Summary);
        return saved.Revision!;
    }

    [Fact]
    public async Task Listening_and_thinking_share_one_host_pairing_and_listening_returns_with_its_key()
    {
        var settings = Keyed(Keyed(SetupSettings.Begin(null), SetupRole.Stt, "gpt-4o-mini-transcribe"), SetupRole.Llm, "gpt-4.1-mini");
        var revision = await SaveAsync(settings, null);
        var cloudStt = settings.Setup!.Routes.Single(r => r.Role == SetupRole.Stt);

        var listening = HostHandoff.ToHost(settings, SetupRouteType.GatewayStt, Endpoint(), Pairing, "desktop-test",
            Snapshot(SetupRouteType.GatewayStt, "small"));
        revision = await SaveAsync(listening, revision);
        var stt = listening.Setup!.Routes.Single(r => r.Role == SetupRole.Stt);
        Assert.Equal((SetupRouteType.GatewayStt, true, "small", Pairing), (stt.RouteType!.Value, stt.Enabled == true, stt.ModelId, stt.CredentialId!.Value));
        Assert.Equal(stt.Selection(), stt.Consent);
        Assert.Contains(listening.Setup.PendingRemovals, item => item.CredentialId == cloudStt.CredentialId);

        // The same host also thinks: both roles use that pairing's one device credential.
        var both = HostHandoff.ToHost(listening, SetupRouteType.GatewayOllama, Endpoint(), Pairing, "desktop-test",
            Snapshot(SetupRouteType.GatewayOllama, "llama3.2-3b"));
        revision = await SaveAsync(both, revision);
        Assert.Equal(2, both.Setup!.Routes.Count(r => r.CredentialId == Pairing));

        // Listening moves to another host while thinking stays; then back to the cloud key, reattached.
        var moved = HostHandoff.ToHost(both, SetupRouteType.GatewayStt, Endpoint("gpu-b"), Guid.NewGuid(), "desktop-test",
            Snapshot(SetupRouteType.GatewayStt, "large-v3-turbo"));
        revision = await SaveAsync(moved, revision);
        var back = HostHandoff.Back(moved, cloudStt);
        await SaveAsync(back, revision);
        Assert.Equal(cloudStt, back.Setup!.Routes.Single(r => r.Role == SetupRole.Stt));
        Assert.DoesNotContain(back.Setup.PendingRemovals, item => item.CredentialId == cloudStt.CredentialId);
        Assert.Equal(SetupRouteType.GatewayOllama, back.Setup.Routes.Single(r => r.Role == SetupRole.Llm).RouteType);
    }

    [Fact]
    public void A_key_removed_meanwhile_comes_back_without_a_key_and_other_credentials_stay_unique()
    {
        var settings = Keyed(SetupSettings.Begin(null), SetupRole.Stt, "gpt-4o-mini-transcribe");
        var cloud = settings.Setup!.Routes.Single();
        var host = HostHandoff.ToHost(settings, SetupRouteType.GatewayStt, Endpoint(), Pairing, "desktop-test",
            Snapshot(SetupRouteType.GatewayStt, "small"));
        var removed = host with { Setup = host.Setup! with { PendingRemovals = [] } };
        var back = HostHandoff.Back(removed, cloud).Setup!.Routes.Single();
        Assert.Null(back.CredentialId);
        Assert.Equal(back.Selection(), back.Consent);

        // A different device credential on another host is not a shared pairing.
        var other = HostHandoff.ToHost(host, SetupRouteType.GatewayOllama, Endpoint("gpu-b"), Guid.NewGuid(), "desktop-test",
            Snapshot(SetupRouteType.GatewayOllama, "llama3.2-3b"));
        var shared = other with { Setup = other.Setup! with
        {
            Routes = other.Setup.Routes.Select(r => r.Role == SetupRole.Llm ? r with { CredentialId = Pairing, Consent = null } : r).ToArray()
        } };
        Assert.Throws<Martlet.Core.Contracts.ContractException>(() => shared.Validate());
    }

    public void Dispose()
    {
        if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
    }
}
