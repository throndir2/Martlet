using System.Security.Cryptography;
using Martlet.Gateway;
using Martlet.Gateway.Persistence;

namespace Martlet.Gateway.Persistence.Portable.Tests;

public sealed class OwnerTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Real_canonical_pinned_loopback_owner_retains_nonce_and_key_over_linux_boundary(bool failNonce, bool renew)
    {
        using var fileSystem = new FakeLinuxFileSystem();
        await OwnerScenario.RunAsync(Authority.Path, fileSystem, failNonce, renew);
    }

    [Theory]
    [InlineData((int)GatewayStorageBackend.WindowsCurrentUserDpapi)]
    [InlineData((int)GatewayStorageBackend.LinuxServicePermissions)]
    [InlineData(99)]
    public async Task Default_No_remains_inert_even_with_invalid_platform_backend_or_path(int backend)
    {
        await using var owner = DurableGatewayHost.CreateNew("not-a-path", "invalid id",
            new("https://127.0.0.1:9443"), (GatewayStorageBackend)backend, ThrowingWorkers(), new OwnerScenario.Audit());
        Assert.False(owner.Enabled);
        Assert.Null(owner.Identity);

        static IEnumerable<IGatewayWorker> ThrowingWorkers()
        {
            yield return Fail();
            static IGatewayWorker Fail() => throw new InvalidOperationException();
        }
    }

    [Fact]
    public void Linux_custody_is_not_a_Windows_fallback()
    {
        var unavailable = OperatingSystem.IsWindows()
            ? GatewayStorageBackend.LinuxServicePermissions : GatewayStorageBackend.WindowsCurrentUserDpapi;
        BackendTests.Failure(GatewayPersistenceFailure.UnsupportedPlatform, () =>
            DurableGatewayHost.OpenExisting("invalid-path", new("https://127.0.0.1:9443"),
                unavailable, [], new OwnerScenario.Audit(), LocalGatewayDecision.Enable));
    }

    [Theory]
    [InlineData("write")]
    [InlineData("fsync:file")]
    [InlineData("fsync:directory")]
    [InlineData("rename:staging.bin:pending.bin:False")]
    [InlineData("rename:pending.bin:authority.bin:True")]
    public void Native_failure_closes_admission_and_never_silently_recreates_state(string operation)
    {
        using var fs = new FakeLinuxFileSystem();
        var clock = new Clock();
        using (var host = new Authority(fs, clock, true))
        {
            host.Issue();
            fs.Fault = call =>
            {
                if (call == operation) throw new GatewayPersistenceException(GatewayPersistenceFailure.StorageFailed);
            };
            Assert.Equal("auth.storage", Assert.Throws<GatewayProtocolException>(() => host.Issue("new-device")).Failure.Code);
            Assert.Equal("auth.closed", Assert.Throws<GatewayProtocolException>(() => host.Issue("third-device")).Failure.Code);
        }
        fs.Fault = null;
        using var recovered = new Authority(fs, clock);
        Assert.Equal(operation == "rename:pending.bin:authority.bin:True" ? 2 : 1,
            recovered.Credentials.ListRegistrations().Count);
    }

    [Fact]
    public void Linux_plaintext_envelope_roundtrips_full_capacity_and_rejects_modified_bytes()
    {
        using var fs = new FakeLinuxFileSystem();
        var clock = new Clock();
        using var host = new Authority(fs, clock, true);
        var now = clock.GetUtcNow();
        var records = Enumerable.Range(0, GatewayCredentialStore.MaximumRegistrations).Select(_ =>
            new StoredGatewayCredential(Base64Url.Encode(RandomNumberGenerator.GetBytes(16)), new string('a', 64),
                new string('<', 64), [GatewayRole.Voice, GatewayRole.Perception, GatewayRole.Memory],
                RandomNumberGenerator.GetBytes(32), now, new PairedDeviceLifetime(), 0, null,
                Enumerable.Range(0, GatewayCredentialStore.MaximumNoncesPerCredential).Select(_ =>
                    new StoredGatewayNonce(Base64Url.Encode(RandomNumberGenerator.GetBytes(24)), now.AddMinutes(4))).ToArray())).ToArray();
        using var checkpoint = new GatewayCheckpoint(now, records, clock.GetTimestamp(), clock.TimestampFrequency);
        host.Store.Commit(checkpoint);
        var bytes = fs.Store.Children["authority.bin"].Bytes;
        Assert.InRange(bytes.Length, 1, StoreFormat.MaximumBytes);
        var envelope = new LinuxPermissionEnvelope();
        using var decoded = envelope.Decode(bytes);
        Assert.Equal(128, decoded.State.Credentials.Length);
        Assert.All(decoded.State.Credentials, record => Assert.Equal(1024, record.Nonces.Length));
        Assert.True(bytes.AsSpan(0, 8).SequenceEqual("MRTLUP02"u8));
        var copy = envelope.Encode(decoded);
        try { Assert.Equal(bytes, copy); }
        finally { CryptographicOperations.ZeroMemory(copy); }
        bytes[17] ^= 1;
        BackendTests.Failure(GatewayPersistenceFailure.InvalidState, () => envelope.Decode(bytes));
    }
}
