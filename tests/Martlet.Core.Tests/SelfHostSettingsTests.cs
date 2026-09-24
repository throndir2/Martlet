using System.Text;
using Martlet.Core.Contracts;
using Martlet.Core.Settings;
using Martlet.Credentials.Windows;

namespace Martlet.Core.Tests;

public sealed class SelfHostSettingsTests : IDisposable
{
    private readonly string directory = Path.Combine(
        Path.GetTempPath(), "Martlet.SelfHost.Settings", Guid.NewGuid().ToString("N"));

    private SettingsStore Store => new(directory);

    [Fact]
    public void CurrentOpenAiAccessibleSummariesPreserveExistingSetupAndStatusContract()
    {
        var settings = SetupSettings.SelectRoute(SetupSettings.Begin(null), SetupRole.Stt, "whisper-1", null);
        var route = settings.Setup!.Routes.Single();
        Assert.Contains("Stt: route selected; consent missing or invalidated", SetupSettings.Describe(settings));
        settings = SetupSettings.ReplaceRoute(settings, route with { Consent = route.Selection() });
        Assert.Contains("Stt: route selected; destination choice recorded, NOT per-turn authorization; credential not configured",
            SetupSettings.Describe(settings));
        Assert.Contains("Stt: route selected; destination selected, not per-turn permission; key not configured",
            SetupStatus.From(settings)!.Describe());
    }

    [Fact]
    public void VersionedSelfHostRoutesAreClosedOffByDefaultAndRequireExactEvidence()
    {
        var initial = SetupSettings.Begin(null);
        var settings = initial with
        {
            Profile = initial.Profile with { Kind = ProfileKind.SelfHosted }
        };
        settings = SetupSettings.ConfigureGatewayEndpoint(
            settings, SetupRouteType.GatewayOllama, Endpoint(), "fixture-llm");
        var llm = settings.Setup!.Routes.Single();
        Assert.False(llm.Enabled);
        Assert.Null(llm.CredentialId);
        Assert.Null(llm.GatewaySnapshot);
        Assert.Null(llm.Consent);
        Assert.Throws<ContractException>(() =>
            SetupSettings.SetRouteEnabled(settings, SetupRole.Llm, enabled: true, recordSelection: true));

        settings = SetupSettings.ApplyGatewaySnapshot(settings, SetupRole.Llm,
            Snapshot(SetupRouteType.GatewayOllama, "fixture-llm"));
        llm = settings.Setup!.Routes.Single();
        Assert.NotNull(llm.GatewaySnapshot);
        Assert.False(llm.Enabled);
        Assert.Throws<ContractException>(() =>
            SetupSettings.SetRouteEnabled(settings, SetupRole.Llm, enabled: true, recordSelection: true));

        var paired = SetupSettings.ReplaceRoute(settings,
            llm with { CredentialId = Guid.NewGuid(), GatewayDeviceId = "fixture-device", ConfigurationRevision = Guid.NewGuid() });
        paired = SetupSettings.SetRouteEnabled(
            paired, SetupRole.Llm, enabled: true, recordSelection: true);
        var enabled = paired.Setup!.Routes.Single();
        Assert.True(enabled.Enabled);
        Assert.Equal(enabled.Selection(), enabled.Consent);
        Assert.Equal(2, enabled.Consent!.SchemaVersion);
        Assert.True(enabled.Consent.AllowNetworkDisclosure);
        Assert.False(enabled.Consent.AllowLocalProcess);
        Assert.False(enabled.Consent.AllowReferenceAudio);
        Assert.NotNull(enabled.Consent.SelectionSha256);

        var changedPin = enabled with
        {
            Gateway = enabled.Gateway! with
            {
                SpkiFingerprint = "sha256:" + new string('f', 64)
            }
        };
        Assert.Throws<ContractException>(() => changedPin.Validate());
    }

    [Fact]
    public async Task HistoricalOpenAiSettingsUpgradeAtomicallyWithoutChangingRouteBehavior()
    {
        var current = SetupSettings.SelectRoute(
            SetupSettings.Begin(null), SetupRole.Llm, "gpt-4.1-2025-04-14", null);
        var route = current.Setup!.Routes.Single();
        current = SetupSettings.ReplaceRoute(current, route with { Consent = route.Selection() });
        var historical = current with
        {
            SchemaVersion = 4,
            Setup = current.Setup!.DowngradeOpenAiForHistoricalSettings()
        };
        historical.Validate();
        var original = ContractJson.Write(historical);
        Directory.CreateDirectory(directory);
        await File.WriteAllBytesAsync(Store.FilePath, original);

        var loaded = await Store.LoadAsync();
        Assert.Equal(4, loaded.Settings!.SchemaVersion);
        Assert.Equal(1, loaded.Settings.Setup!.SchemaVersion);
        var upgraded = SetupSettings.Begin(loaded.Settings);
        Assert.Equal(AppSettings.CurrentSchemaVersion, upgraded.SchemaVersion);
        Assert.Equal(SetupSettings.CurrentSchemaVersion, upgraded.Setup!.SchemaVersion);
        var upgradedRoute = upgraded.Setup.Routes.Single();
        Assert.Equal(SetupRouteType.OpenAi, upgradedRoute.RouteType);
        Assert.True(upgradedRoute.Enabled);
        Assert.Equal(route.ModelId, upgradedRoute.ModelId);
        Assert.Equal(upgradedRoute.Selection(), upgradedRoute.Consent);

        var saved = await Store.SaveAsync(upgraded, loaded.Revision);
        Assert.True(saved.Saved);
        Assert.Equal(4, saved.MigratedFromSchemaVersion);
        Assert.Equal(original, await File.ReadAllBytesAsync(
            Path.Combine(directory, saved.SnapshotFileName!)));
    }

    [Fact]
    public async Task RecoveryPreservesCurrentPairingButClearsActionAndProbeEvidence()
    {
        var settings = ConfiguredSelfHost();
        Assert.True((await Store.SaveAsync(settings, null)).Saved);
        var backup = Path.Combine(directory, "self-host.martlet-config");
        await Store.CreateConfigurationSnapshotAsync(backup);
        var current = await Store.LoadAsync();
        var changed = current.Settings! with
        {
            Setup = current.Settings.Setup! with { Checkpoint = SetupStep.Review }
        };
        Assert.True((await Store.SaveAsync(changed, current.Revision)).Saved);

        var plan = await Store.PreviewConfigurationRestoreAsync(backup);
        var candidate = SettingsJson.Read(Encoding.UTF8.GetBytes(plan.CandidateJson));
        Assert.Equal(AppSettings.CurrentSchemaVersion, candidate.SchemaVersion);
        Assert.Equal(SetupStep.Destinations, candidate.Setup!.Checkpoint);
        Assert.All(candidate.Setup.Routes, route =>
        {
            Assert.False(route.Enabled);
            Assert.Equal(changed.Setup!.Routes.Single(item => item.Role == route.Role).CredentialId, route.CredentialId);
            Assert.Null(route.Consent);
            Assert.Null(route.GatewaySnapshot);
            Assert.Null(route.Reference);
            Assert.Null(route.LocalStt);
        });
        Assert.Empty(candidate.Setup.PendingRemovals);
        Assert.Empty(candidate.Setup.RetainedGatewayCredentials!);
    }

    [Fact]
    public async Task GatewayVaultTargetBindsProfileRouteHostPinRoleAndOpaqueReference()
    {
        using var native = new FakeCredentialNative();
        var store = Store;
        var service = new SetupService(store, new WindowsCredentialStore(native));
        var settings = SetupSettings.ConfigureGatewayEndpoint(
            SetupSettings.Begin(null), SetupRouteType.GatewayOllama,
            Endpoint(), "fixture-llm");
        settings = SetupSettings.ApplyGatewaySnapshot(settings, SetupRole.Llm,
            Snapshot(SetupRouteType.GatewayOllama, "fixture-llm"));
        settings = SetupSettings.ReplaceRoute(settings, settings.Setup!.Routes.Single() with { GatewayDeviceId = "fixture-device" });
        using var secret = new SecretLease("v1.synthetic.gateway.credential");
        var saved = await service.ReplaceCredentialAsync(
            settings, null, SetupRole.Llm, secret);
        Assert.True(saved.Save.Saved);
        var route = saved.Settings.Setup!.Routes.Single();
        Assert.NotNull(route.CredentialId);
        var write = Assert.Single(native.Events);
        Assert.StartsWith($"write Martlet/v3/{settings.Profile.Id:N}/gateway/GatewayOllama/", write, StringComparison.Ordinal);
        Assert.EndsWith(route.CredentialId!.Value.ToString("N"), write, StringComparison.Ordinal);
        Assert.DoesNotContain(Endpoint().SpkiFingerprint, write, StringComparison.Ordinal);
        Assert.DoesNotContain("synthetic.gateway.credential", await File.ReadAllTextAsync(store.FilePath), StringComparison.Ordinal);

        var movedRoute = route with
        {
            Gateway = route.Gateway! with
            {
                HostId = "other-host",
                SpkiFingerprint = "sha256:" + new string('f', 64)
            },
            Consent = null,
            ConfigurationRevision = Guid.NewGuid()
        };
        var moved = saved.Settings with
        {
            Setup = saved.Settings.Setup! with { Routes = [movedRoute] }
        };
        Assert.False((await store.SaveAsync(moved, saved.Save.Revision)).Saved);

        var detached = await service.DetachCredentialAsync(
            saved.Settings, saved.Save.Revision, SetupRole.Llm);
        var pending = Assert.Single(detached.Settings.Setup!.PendingRemovals);
        Assert.Equal(SetupRouteType.GatewayOllama, pending.Scope!.RouteType);
        var wrongScope = detached.Settings with
        {
            Setup = detached.Settings.Setup with
            {
                PendingRemovals =
                [
                    pending with
                    {
                        Scope = pending.Scope with
                        {
                            HostId = "wrong-host",
                            SpkiFingerprint = "sha256:" + new string('e', 64)
                        }
                    }
                ]
            }
        };
        Assert.False((await store.SaveAsync(wrongScope, detached.Save.Revision)).Saved);
        Assert.Throws<ContractException>(() => SetupSettings.ConfigureGatewayEndpoint(
            detached.Settings,
            SetupRouteType.GatewayOllama,
            Endpoint() with { HostId = "other-host" },
            "fixture-llm"));
    }

    private static AppSettings ConfiguredSelfHost()
    {
        var settings = SetupSettings.Begin(null);
        settings = settings with { Profile = settings.Profile with { Kind = ProfileKind.SelfHosted } };
        settings = SetupSettings.ConfigureGatewayEndpoint(
            settings, SetupRouteType.GatewayOllama, Endpoint(), "fixture-llm");
        settings = SetupSettings.ApplyGatewaySnapshot(settings, SetupRole.Llm,
            Snapshot(SetupRouteType.GatewayOllama, "fixture-llm"));
        var llm = settings.Setup!.Routes.Single(item => item.Role == SetupRole.Llm);
        settings = SetupSettings.ReplaceRoute(settings,
            llm with { CredentialId = Guid.NewGuid(), GatewayDeviceId = "fixture-device", ConfigurationRevision = Guid.NewGuid() });
        settings = SetupSettings.SetRouteEnabled(settings, SetupRole.Llm, true, true);

        settings = SetupSettings.ConfigureGatewayEndpoint(
            settings, SetupRouteType.GatewayF5, Endpoint(), "f5-weights");
        settings = SetupSettings.ApplyGatewaySnapshot(settings, SetupRole.Tts,
            Snapshot(SetupRouteType.GatewayF5, "f5-weights"));
        var tts = settings.Setup!.Routes.Single(item => item.Role == SetupRole.Tts);
        settings = SetupSettings.ReplaceRoute(settings,
            tts with
            {
                CredentialId = Guid.NewGuid(),
                GatewayDeviceId = "fixture-device",
                Reference = Reference(tts.GatewaySnapshot!.DestinationId),
                ConfigurationRevision = Guid.NewGuid()
            });
        settings = SetupSettings.SetRouteEnabled(settings, SetupRole.Tts, true, true);

        settings = SetupSettings.ConfigureLocalStt(settings, LocalPackage());
        settings = SetupSettings.SetRouteEnabled(settings, SetupRole.Stt, true, true);
        settings.Validate();
        return settings;
    }

    private static GatewayEndpointSettings Endpoint() => new()
    {
        SchemaVersion = 1,
        Origin = "https://192.168.1.20:7443",
        HostId = "fixture-host",
        SpkiFingerprint = "sha256:" + new string('a', 64),
        DeviceRole = SelfHostSetup.GatewayRole
    };

    private static GatewayRouteSnapshot Snapshot(SetupRouteType type, string modelId) => new()
    {
        SchemaVersion = 1,
        RouteType = type,
        RegistryId = SelfHostSetup.RegistryId,
        RegistryVersion = SelfHostSetup.RegistryVersion,
        RouteId = type == SetupRouteType.GatewayOllama
            ? SelfHostSetup.OllamaRouteId
            : SelfHostSetup.F5RouteId,
        Path = type == SetupRouteType.GatewayOllama
            ? "/martlet/v1/inference/ollama-chat"
            : "/martlet/v1/inference/f5-synthesis",
        ContractId = type == SetupRouteType.GatewayOllama
            ? SelfHostSetup.OllamaContractId
            : SelfHostSetup.F5ContractId,
        ContractVersion = "1.0",
        DestinationId = "fixture-destination",
        WorkerId = type == SetupRouteType.GatewayOllama ? "ollama-worker" : "f5-worker",
        WorkerPackageRevision = "fixture-package-v1",
        AdapterVersion = "1.0.0",
        ModelId = modelId,
        ModelRevision = "fixture-model-r1",
        ModelSha256 = new string('b', 64),
        ArtifactIdentitySha256 = "sha256:" + new string('c', 64),
        MaximumRequestBytes = type == SetupRouteType.GatewayOllama ? 32 * 1024 : 5_700_000,
        MaximumInputBytes = type == SetupRouteType.GatewayOllama ? 16_384 : 4 * 1024 * 1024,
        MaximumOutputBytes = 4 * 1024 * 1024,
        MaximumEventBytes = 16 * 1024,
        MaximumEvents = 1024,
        MaximumStreamBytes = 4 * 1024 * 1024,
        MaximumDurationSeconds = 60,
        Cancellation = GatewayCancellationMode.RequestAbort,
        ObservedAtUtc = new DateTimeOffset(2026, 9, 22, 8, 0, 0, TimeSpan.Zero),
        ProbeRevision = Guid.NewGuid()
    };

    private static F5ReferenceSettings Reference(string destination) => new()
    {
        SchemaVersion = 1,
        PresetId = Guid.NewGuid(),
        PresetName = "Fixture voice",
        ReferenceRevision = new string('d', 64),
        AudioSha256 = new string('e', 64),
        TranscriptRevision = new string('f', 64),
        StoreRevision = 1,
        ProcessingDestinationId = destination,
        RightsAcknowledgementId = Guid.NewGuid(),
        RightsStatementVersion = SelfHostSetup.F5RightsStatementVersion,
        AppliedAtUtc = new DateTimeOffset(2026, 9, 22, 8, 0, 0, TimeSpan.Zero),
        ApplyRevision = Guid.NewGuid()
    };

    private static LocalSttPackageSettings LocalPackage() => new()
    {
        SchemaVersion = 1,
        PackageRoot = Path.Combine(Path.GetTempPath(), "Martlet.LocalStt.Package"),
        PackageId = "whisper-cpp-base-en",
        ManifestSha256 = new string('1', 64),
        ModelId = "whisper-base-en",
        ModelSha256 = new string('2', 64),
        ExecutableArchiveSha256 = new string('3', 64),
        Language = "en",
        NetworkPolicy = "no_network",
        RightsRevision = "fixture-rights-v1",
        RightsReviewed = true,
        DeniedEgressRequired = true,
        VerifiedAtUtc = new DateTimeOffset(2026, 9, 22, 8, 0, 0, TimeSpan.Zero),
        VerificationRevision = Guid.NewGuid()
    };

    public void Dispose()
    {
        if (Directory.Exists(directory))
            Directory.Delete(directory, recursive: true);
    }
}
