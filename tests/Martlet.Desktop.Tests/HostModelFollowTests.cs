using Martlet.Avatar.Audio2Face.Remote;
using Martlet.Core.Settings;
using Martlet.Desktop;

namespace Martlet.Desktop.Tests;

// Changing a role's model on a host (Change model / Change settings, here or on another computer): what this PC hands that host
// follows the model it serves now, because a host answers only for the model a route names.
public sealed class HostModelFollowTests
{
    private static readonly Guid Pairing = Guid.NewGuid();

    private static GatewayEndpointSettings Endpoint(string host) => new()
    {
        SchemaVersion = 1, Origin = host == "diva" ? "https://192.168.1.20:9443" : "https://192.168.1.30:9443", HostId = host,
        SpkiFingerprint = "sha256:" + new string('a', 64),
        DeviceRole = "voice"
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

    private static HostRoute Route(string routeId, string model) => new(routeId, "/p", "c", "1.0", "d", "w", "1", model, "r",
        new string('a', 64), new string('b', 64), 1, 1, 1, 1, 1, 1, TimeSpan.FromSeconds(30), "request_abort");

    [Fact]
    public void Jobs_on_a_host_follow_the_model_it_serves_now()
    {
        var settings = HostHandoff.ToHost(SetupSettings.Begin(null), SetupRouteType.GatewayOllama, Endpoint("diva"), Pairing, "desktop-test",
            Snapshot(SetupRouteType.GatewayOllama, "gemma4-e4b"));
        settings = HostHandoff.ToHost(settings, SetupRouteType.GatewayStt, Endpoint("diva"), Pairing, "desktop-test",
            Snapshot(SetupRouteType.GatewayStt, "small"));

        // Thinking's model changed there; Listening's didn't.
        HostRoute[] routes = [Route(HostRoute.OllamaChatRouteId, "gemma4-26b"), Route(Audio2FaceHostConnection.TranscriptionRouteId, "small")];
        var (saved, now) = Assert.Single(HostModelFollow.Jobs(settings, "diva", routes));
        Assert.Equal((SetupRole.Llm, "gemma4-e4b", "gemma4-26b"), (saved.Role, saved.GatewaySnapshot!.ModelId, now.ModelId));
        Assert.Same(HostJob.Thinking, HostModelFollow.JobFor(saved));
        Assert.Same(HostJob.Listening, HostModelFollow.JobFor(settings.Setup!.Routes.Single(r => r.Role == SetupRole.Stt)));

        // Nothing moves for the same model, another host, or a host still loading the new model (it keeps the old route).
        Assert.Empty(HostModelFollow.Jobs(settings, "diva", [Route(HostRoute.OllamaChatRouteId, "gemma4-e4b")]));
        Assert.Empty(HostModelFollow.Jobs(settings, "other-host", routes));
        Assert.Empty(HostModelFollow.Jobs(settings, "diva", [Route(HostRoute.DeepThinkingRouteId, "gemma4-26b")]));
        Assert.Empty(HostModelFollow.Jobs(null, "diva", routes));
    }

    [Fact]
    public void Deep_thinking_on_a_host_follows_its_roles_new_model()
    {
        var deep = new DeepThinkingSettings
        {
            Place = DeepThinkingPlace.Host, ModelId = "gemma4:e4b", HostId = "diva", HostOrigin = "https://192.168.1.20:9443",
            HostSpkiFingerprint = "sha256:" + new string('a', 64), HostDeviceId = "desktop-test", HostCredentialId = Pairing,
            HostRouteId = SelfHostSetup.DeepThinkingRouteId, ChosenAt = DateTimeOffset.UtcNow
        };
        HostRoute[] routes = [Route(HostRoute.OllamaChatRouteId, "qwen2.5:7b"), Route(HostRoute.DeepThinkingRouteId, "gemma4:26b")];
        var next = HostModelFollow.Deep(deep, "diva", routes);
        Assert.NotNull(next);
        Assert.Equal(deep with { ModelId = "gemma4:26b" }, next);
        next.Validate();
        Assert.Null(HostModelFollow.Deep(next, "diva", routes));
        Assert.Null(HostModelFollow.Deep(deep, "other-host", routes));
        Assert.Null(HostModelFollow.Deep(new DeepThinkingSettings(), "diva", routes));
        // Thinking's Ollama changing doesn't move a think on the Deep thinking role...
        Assert.Null(HostModelFollow.Deep(deep, "diva", [Route(HostRoute.OllamaChatRouteId, "llama3.1:8b"), Route(HostRoute.DeepThinkingRouteId, "gemma4:e4b")]));
        // ...but does move one saved on that computer's Ollama, before it had the Deep thinking role.
        var onOllama = deep with { HostRouteId = null, ModelId = "qwen2.5:7b" };
        Assert.Equal("llama3.1:8b", HostModelFollow.Deep(onOllama, "diva", [Route(HostRoute.OllamaChatRouteId, "llama3.1:8b")])?.ModelId);
    }

    [Fact]
    public void Deep_thinking_follows_a_new_model_on_any_computer_it_thinks_on()
    {
        DeepThinkingSettings Role(string host, string model) => new()
        {
            Place = DeepThinkingPlace.Host, ModelId = model, HostId = host, HostOrigin = $"https://{host}.local:9443",
            HostSpkiFingerprint = "sha256:" + new string('a', 64), HostDeviceId = "desktop-test", HostCredentialId = Pairing,
            HostRouteId = SelfHostSetup.DeepThinkingRouteId
        };
        var deep = Role("diva", "gemma4:e4b").WithPool([Role("ripley", "qwen3-8b")]);
        var next = HostModelFollow.Deep(deep, "ripley", [Route(HostRoute.DeepThinkingRouteId, "qwen3-14b")]);
        Assert.NotNull(next);
        next.Validate();
        Assert.Equal("gemma4:e4b", next.ModelId);
        Assert.Equal("qwen3-14b", Assert.Single(next.Pool!).ModelId);
        Assert.Null(HostModelFollow.Deep(next, "ripley", [Route(HostRoute.DeepThinkingRouteId, "qwen3-14b")]));
        Assert.Null(HostModelFollow.Deep(deep, "imouto", [Route(HostRoute.DeepThinkingRouteId, "qwen3-14b")]));
    }
}
