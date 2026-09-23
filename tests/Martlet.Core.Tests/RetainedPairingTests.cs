using System.Text;
using System.Text.Json.Nodes;
using Martlet.Core.Contracts;
using Martlet.Core.Settings;
using Martlet.Credentials.Windows;

namespace Martlet.Core.Tests;

public sealed class RetainedPairingTests : IDisposable
{
    private readonly string directory = Path.Combine(AppContext.BaseDirectory, "retained-pairings", Guid.NewGuid().ToString("N"));
    private SettingsStore Store => new(directory);
    private string Snapshot => Path.Combine(directory, "selected.martlet-config");
    private static GatewayEndpointSettings Endpoint => new()
    {
        SchemaVersion = 1, Origin = "https://127.0.0.1:7443", HostId = "private-host-canary",
        SpkiFingerprint = "sha256:" + new string('a', 64), DeviceRole = "voice"
    };

    private async Task<AppSettings> Prepare(FakeCredentialNative native)
    {
        var settings = SetupSettings.ConfigureGatewayEndpoint(SetupSettings.Begin(null),
            SetupRouteType.GatewayOllama, Endpoint, "private-model-canary");
        settings = SetupSettings.ReplaceRoute(settings, settings.Setup!.Routes.Single() with { GatewayDeviceId = "device-canary" });
        using var secret = new SecretLease("synthetic-secret-canary");
        var saved = await new SetupService(Store, new WindowsCredentialStore(native))
            .ReplaceCredentialAsync(settings, null, SetupRole.Llm, secret);
        Assert.True(saved.Save.Saved, saved.Summary);
        return saved.Settings;
    }

    private async Task WriteSnapshot(AppSettings settings) =>
        await File.WriteAllBytesAsync(Snapshot, ConfigurationSnapshot.Create(ContractJson.Write(settings)));

    private async Task<AppSettings> Restore()
    {
        var plan = await Store.PreviewConfigurationRestoreAsync(Snapshot);
        await Store.RestoreConfigurationAsync(plan, plan.Approve(plan.SnapshotDigest, plan.Destination, plan.ExpectedRevision));
        return (await Store.LoadAsync()).Settings!;
    }

    [Theory]
    [InlineData("same")]
    [InlineData("host")]
    [InlineData("pin")]
    [InlineData("origin")]
    public async Task SnapshotIdentitiesNeverReplaceCurrentPairing(string changed)
    {
        using var native = new FakeCredentialNative();
        var current = await Prepare(native);
        var original = current.Setup!.Routes.Single();
        var importedEndpoint = changed switch
        {
            "host" => Endpoint with { HostId = "snapshot-host" },
            "pin" => Endpoint with { SpkiFingerprint = "sha256:" + new string('b', 64) },
            "origin" => Endpoint with { Origin = "https://127.0.0.1:7444" },
            _ => Endpoint
        };
        var forged = original with
        {
            Gateway = importedEndpoint, Origin = importedEndpoint.Origin,
            CredentialId = Guid.NewGuid(), GatewayDeviceId = "snapshot-device"
        };
        var snapshot = current with { Setup = current.Setup with
        {
            Routes = [forged],
            RetainedGatewayCredentials = [RetainedGatewayCredential.From(forged) with { CredentialId = Guid.NewGuid() }]
        } };
        await WriteSnapshot(snapshot);
        native.Events.Clear();
        var restored = await Restore();
        var route = restored.Setup!.Routes.Single();
        Assert.False(route.Enabled);
        Assert.Null(route.Consent);
        Assert.Null(route.GatewaySnapshot);
        Assert.Empty(native.Events);
        Assert.Single(native.Keys);
        Assert.DoesNotContain(restored.Setup.RetainedGatewayCredentials!, item => item.CredentialId == forged.CredentialId);
        if (changed == "same")
        {
            Assert.Equal(original.CredentialId, route.CredentialId);
            Assert.Equal(original.GatewayDeviceId, route.GatewayDeviceId);
            Assert.Empty(restored.Setup.RetainedGatewayCredentials!);
        }
        else
        {
            Assert.Null(route.CredentialId);
            var retained = Assert.Single(restored.Setup.RetainedGatewayCredentials!);
            Assert.Equal(RetainedGatewayCredential.From(original), retained);
            Assert.Contains("Saved pairing retained; route not connected/mismatched", SetupSettings.Describe(restored));
            Assert.Throws<ContractException>(() => SetupSettings.ReconnectRetainedGateway(restored, retained.CredentialId));
            var selection = SetupSettings.ConfigureGatewayEndpoint(restored, SetupRouteType.GatewayOllama, Endpoint, original.ModelId);
            var reconnected = SetupSettings.ReconnectRetainedGateway(selection, retained.CredentialId);
            Assert.True((await Store.SaveAsync(reconnected, (await Store.LoadAsync()).Revision)).Saved);
            Assert.Equal(original.CredentialId, reconnected.Setup!.Routes.Single().CredentialId);
            Assert.Empty(native.Events);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DetachedOrDeletedCredentialCannotBeResurrected(bool delete)
    {
        using var native = new FakeCredentialNative();
        var current = await Prepare(native);
        await WriteSnapshot(current);
        var service = new SetupService(Store, new WindowsCredentialStore(native));
        var detached = await service.DetachCredentialAsync(current, (await Store.LoadAsync()).Revision, SetupRole.Llm);
        if (delete)
            Assert.True((await service.RemoveDetachedAsync(detached.Settings, detached.Save.Revision,
                detached.Settings.Setup!.PendingRemovals.Single())).Save.Saved);
        native.Events.Clear();
        var restored = await Restore();
        Assert.Null(restored.Setup!.Routes.Single().CredentialId);
        Assert.Empty(restored.Setup.RetainedGatewayCredentials!);
        Assert.Equal(delete ? 0 : 1, restored.Setup.PendingRemovals.Count);
        Assert.Empty(native.Events);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MissingVaultAndRestartDoNotForgetPairing(bool missing)
    {
        using var native = new FakeCredentialNative();
        var current = await Prepare(native);
        var route = current.Setup!.Routes.Single();
        await WriteSnapshot(current);
        if (missing) new WindowsCredentialStore(native).Delete(CredentialBinding.For(current, SetupRole.Llm, route.CredentialId!.Value));
        native.Events.Clear();
        var restored = await Restore();
        var freshStore = new SettingsStore(directory);
        Assert.Equal(route.CredentialId, (await freshStore.LoadAsync()).Settings!.Setup!.Routes.Single().CredentialId);
        Assert.Empty(native.Events);
        var checkedState = new SetupService(freshStore, new WindowsCredentialStore(native)).CheckCredential(restored, SetupRole.Llm);
        Assert.Equal(missing ? CredentialError.Missing : CredentialError.None, checkedState);
        Assert.Equal(route.CredentialId, (await freshStore.LoadAsync()).Settings!.Setup!.Routes.Single().CredentialId);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RestoreRechecksCurrentAndSourceBytes(bool source)
    {
        using var native = new FakeCredentialNative();
        var settings = await Prepare(native);
        await WriteSnapshot(settings);
        var plan = await Store.PreviewConfigurationRestoreAsync(Snapshot);
        if (source) await File.AppendAllTextAsync(Snapshot, "\n");
        else
            Assert.True((await Store.SaveAsync(settings with { Setup = settings.Setup! with { Checkpoint = SetupStep.Review } },
                plan.ExpectedRevision)).Saved);
        var bytes = await File.ReadAllBytesAsync(Store.FilePath);
        native.Events.Clear();
        await Assert.ThrowsAsync<RecoveryException>(() => Store.RestoreConfigurationAsync(plan,
            plan.Approve(plan.SnapshotDigest, plan.Destination, plan.ExpectedRevision)));
        Assert.Equal(bytes, await File.ReadAllBytesAsync(Store.FilePath));
        Assert.Empty(native.Events);
    }

    [Fact]
    public async Task RetainedAccountingIsBoundedScopedAndExplicitlyDeleted()
    {
        using var native = new FakeCredentialNative();
        var settings = await Prepare(native);
        var active = settings.Setup!.Routes.Single();
        var retained = RetainedGatewayCredential.From(active);
        var duplicate = settings with { Setup = settings.Setup with { RetainedGatewayCredentials = [retained] } };
        Assert.Throws<ContractException>(() => duplicate.Validate());
        var kept = settings with { Setup = settings.Setup with { Routes = [], RetainedGatewayCredentials = [retained] } };
        var saved = await Store.SaveAsync(kept, (await Store.LoadAsync()).Revision);
        Assert.True(saved.Saved);
        Assert.False((await Store.SaveAsync(kept with { Setup = kept.Setup! with { RetainedGatewayCredentials = [] } }, saved.Revision)).Saved);
        Assert.False((await Store.SaveAsync(kept with { Setup = kept.Setup! with
        {
            RetainedGatewayCredentials = [retained with { Scope = retained.Scope with { DeviceId = "different-device" } }]
        } }, saved.Revision)).Saved);
        var overflow = kept with { Setup = kept.Setup! with
        {
            RetainedGatewayCredentials = Enumerable.Range(0, 17).Select(_ => retained with { CredentialId = Guid.NewGuid() }).ToArray()
        } };
        Assert.Throws<ContractException>(() => overflow.Validate());
        var detached = SetupSettings.DetachRetainedGateway(kept, retained.CredentialId);
        var detachment = await Store.SaveAsync(detached, saved.Revision);
        Assert.True(detachment.Saved);
        var service = new SetupService(Store, new WindowsCredentialStore(native));
        Assert.True((await service.RemoveDetachedAsync(detached, detachment.Revision,
            detached.Setup!.PendingRemovals.Single())).Save.Saved);
        Assert.Empty(native.Keys);
    }

    [Fact]
    public async Task DiagnosticsAndStrictJsonDoNotExposePrivatePairingFields()
    {
        using var native = new FakeCredentialNative();
        var settings = await Prepare(native);
        var status = SetupStatus.From(settings)!;
        var diagnostics = status.Describe() + Encoding.UTF8.GetString(ContractJson.Write(status));
        foreach (var canary in new[] { "private-host-canary", "127.0.0.1", Endpoint.SpkiFingerprint,
            "private-model-canary", "device-canary", "synthetic-secret-canary" })
            Assert.DoesNotContain(canary, diagnostics);
        var json = JsonNode.Parse(ContractJson.Write(settings))!;
        json["setup"]!["routes"]![0]!["gateway"]!["unexpected"] = true;
        Assert.Throws<ContractException>(() => SettingsJson.Read(Encoding.UTF8.GetBytes(json.ToJsonString())));
    }

    public void Dispose()
    {
        if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
    }
}
