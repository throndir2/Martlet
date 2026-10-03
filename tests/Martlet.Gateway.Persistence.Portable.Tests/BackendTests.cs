using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Martlet.Gateway;
using Martlet.Gateway.Persistence;

namespace Martlet.Gateway.Persistence.Portable.Tests;

internal sealed class Clock : TimeProvider
{
    internal DateTimeOffset Utc = DateTimeOffset.UtcNow.AddSeconds(-1);
    internal long Ticks;
    public override DateTimeOffset GetUtcNow() => Utc;
    public override long GetTimestamp() => Ticks;
    public override long TimestampFrequency => TimeSpan.TicksPerSecond;
    internal void Advance(TimeSpan duration)
    {
        Utc += duration;
        Ticks += duration.Ticks;
    }
}

internal sealed class Authority : IDisposable
{
    internal const string Path = "/srv/martlet/store";
    internal AuthorityStore Store { get; }
    internal GatewayCredentialStore Credentials { get; }
    internal GatewayHostIdentity Identity { get; }
    private readonly Clock clock;

    internal Authority(FakeLinuxFileSystem fs, Clock clock, bool create = false,
        Action<StoreStep>? fault = null, Action<LinuxStoreStep>? nativeFault = null)
    {
        this.clock = clock;
        var directory = LinuxOwnedDirectory.Open(Path, create, fs, nativeFault);
        if (create)
        {
            using var certificate = HostCertificate.Create(clock.GetUtcNow());
            var bytes = certificate.Export(X509ContentType.Pkcs12);
            try { Store = AuthorityStore.Create(directory, new LinuxPermissionEnvelope(), fs.Boot,
                "fixture-host", bytes, clock.GetUtcNow(), fault, clock); }
            finally { CryptographicOperations.ZeroMemory(bytes); }
        }
        else Store = AuthorityStore.Open(directory, new LinuxPermissionEnvelope(), fs.Boot, clock.GetUtcNow(), fault);
        var encoded = Store.CopyCertificate();
        try
        {
            using var certificate = HostCertificate.Load(encoded);
            Identity = GatewayHostIdentity.FromCertificate(Store.HostId, certificate);
            Credentials = new(Identity, clock, Store.Initial, Store, Store.InitialSameBoot);
        }
        catch { Store.Dispose(); throw; }
        finally { CryptographicOperations.ZeroMemory(encoded); }
    }

    internal IssuedDeviceCredential Issue(string device = "fixture-device") =>
        Credentials.Issue(device, "Fixture", [GatewayRole.Voice]);

    internal GatewaySignedRequest Sign(IssuedDeviceCredential credential)
    {
        Assert.True(Base64Url.TryDecode(credential.Secret.Reveal(), 32, out var secret));
        var key = SHA256.HashData(secret);
        CryptographicOperations.ZeroMemory(secret);
        try
        {
            var timestamp = DateTimeOffset.FromUnixTimeSeconds(clock.GetUtcNow().ToUnixTimeSeconds());
            var nonce = Base64Url.Encode(RandomNumberGenerator.GetBytes(24));
            var bytes = GatewayRequestSigner.Canonical(Identity.HostId, credential.CredentialId,
                "GET", "/martlet/v1/version", "voice", timestamp.ToUnixTimeSeconds(), nonce, SHA256.HashData([]));
            return new()
            {
                CredentialId = credential.CredentialId, Nonce = nonce, Timestamp = timestamp, Role = GatewayRole.Voice,
                CanonicalBytes = bytes, Signature = HMACSHA256.HashData(key, bytes)
            };
        }
        finally { CryptographicOperations.ZeroMemory(key); }
    }

    public void Dispose() { Credentials.Close(); Store.Dispose(); }
}

public sealed class BackendTests
{
    [Fact]
    public void Directory_contract_checks_flags_permissions_lock_and_short_transfers()
    {
        using var fs = new FakeLinuxFileSystem { TransferLimit = 3 };
        using (var directory = LinuxOwnedDirectory.Open(Authority.Path, true, fs))
        {
            Assert.Equal(0x41c0, fs.Store.Identity.Mode);
            Assert.Equal(0x8180, fs.Store.Children["owner.lock"].Identity.Mode);
            directory.WriteNew("running", [1, 2, 3, 4, 5, 6, 7]);
            Assert.Equal(new byte[] { 1, 2, 3, 4, 5, 6, 7 }, directory.Read("running", 20));
            Failure(GatewayPersistenceFailure.StoreBusy, () => LinuxOwnedDirectory.Open(Authority.Path, false, fs));
        }
        using var reopened = LinuxOwnedDirectory.Open(Authority.Path, false, fs);
        Assert.Equal(1u, fs.Store.Children["owner.lock"].Identity.Links);
    }

    [Theory]
    [InlineData("/srv/martlet/../store")]
    [InlineData("/srv//martlet/store")]
    [InlineData("/srv/martlet/store/")]
    [InlineData("srv/martlet/store")]
    [InlineData("/store")]
    [InlineData("/srv/martlet/\0store")]
    public void Noncanonical_paths_have_no_native_side_effects(string path)
    {
        using var fs = new FakeLinuxFileSystem();
        Failure(GatewayPersistenceFailure.InvalidPath, () => LinuxOwnedDirectory.Open(path, true, fs));
        Assert.Empty(fs.Calls);
    }

    [Theory]
    [InlineData("uid")]
    [InlineData("mode")]
    [InlineData("acl")]
    [InlineData("filesystem")]
    public void Unsafe_parent_or_unsupported_filesystem_is_not_repaired(string kind)
    {
        using var fs = new FakeLinuxFileSystem();
        if (kind == "uid") fs.Parent.Identity = fs.Parent.Identity with { User = 2222 };
        if (kind == "mode") fs.Parent.Identity = fs.Parent.Identity with { Mode = 0x41ff };
        if (kind == "acl") fs.Parent.Acl = true;
        if (kind == "filesystem") fs.SupportedFileSystem = false;
        var previous = fs.Parent.Identity;
        Assert.Throws<GatewayPersistenceException>(() => LinuxOwnedDirectory.Open(Authority.Path, true, fs));
        Assert.Equal(previous, fs.Parent.Identity);
        Assert.DoesNotContain(fs.Calls, call => call.StartsWith("mkdir:", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("uid")]
    [InlineData("mode")]
    [InlineData("hardlink")]
    [InlineData("symlink")]
    [InlineData("fifo")]
    [InlineData("device")]
    [InlineData("mount")]
    [InlineData("acl")]
    public void Unsafe_file_is_rejected_before_read_and_preserved(string kind)
    {
        using var fs = new FakeLinuxFileSystem();
        using (var host = new Authority(fs, new Clock(), true)) host.Issue();
        var node = fs.Store.Children["authority.bin"];
        node.Identity = kind switch
        {
            "uid" => node.Identity with { User = 2222 },
            "mode" => node.Identity with { Mode = 0x81a4 },
            "hardlink" => node.Identity with { Links = 2 },
            "symlink" => node.Identity with { Mode = 0xa180 },
            "fifo" => node.Identity with { Mode = 0x1180 },
            "device" => node.Identity with { Mode = 0x2180 },
            "mount" => node.Identity with { Mount = 222 },
            _ => node.Identity
        };
        if (kind == "acl") node.Acl = true;
        var previous = node.Bytes.ToArray();
        fs.Calls.Clear();
        Failure(GatewayPersistenceFailure.InsecureStorage, () => new Authority(fs, new Clock()));
        Assert.DoesNotContain("read", fs.Calls);
        Assert.Equal(previous, node.Bytes);
    }

    [Theory]
    [InlineData("parent")]
    [InlineData("store")]
    [InlineData("lock")]
    public void Changed_held_identity_blocks_live_commits(string target)
    {
        using var fs = new FakeLinuxFileSystem();
        using var host = new Authority(fs, new Clock(), true);
        var node = target switch { "parent" => fs.Parent, "store" => fs.Store, _ => fs.Store.Children["owner.lock"] };
        node.Identity = node.Identity with { Inode = node.Identity.Inode + 1000 };
        Assert.Equal("auth.storage", Assert.Throws<GatewayProtocolException>(() => host.Issue()).Failure.Code);
    }

    [Fact]
    public void Unknown_files_and_existing_directory_are_never_adopted_or_removed()
    {
        using var fs = new FakeLinuxFileSystem();
        using (var host = new Authority(fs, new Clock(), true)) host.Issue();
        fs.Put("foreign.bin", [1, 2, 3]);
        Failure(GatewayPersistenceFailure.RecoveryRequired, () => new Authority(fs, new Clock()));
        Failure(GatewayPersistenceFailure.InsecureStorage, () => new Authority(fs, new Clock(), true));
        Assert.Equal(new byte[] { 1, 2, 3 }, fs.Store.Children["foreign.bin"].Bytes);
    }

    [Fact]
    public void Atomic_steps_flush_both_file_contents_and_directory_entries_in_order()
    {
        using var fs = new FakeLinuxFileSystem();
        using var host = new Authority(fs, new Clock(), true);
        fs.Calls.Clear();
        host.Issue();
        var operations = fs.Calls.Where(call => call.StartsWith("fsync:", StringComparison.Ordinal) ||
            call.StartsWith("rename:", StringComparison.Ordinal)).ToArray();
        Assert.Equal(["fsync:file", "fsync:directory",
            "rename:staging.bin:pending.bin:False", "fsync:directory",
            "rename:pending.bin:authority.bin:True", "fsync:file", "fsync:directory"], operations);
    }

    public static IEnumerable<object[]> InterruptionCases()
    {
        foreach (var step in Enum.GetValues<LinuxStoreStep>())
        foreach (var operation in new[] { "issue", "revoke", "rotate", "nonce" })
        foreach (var crash in new[] { false, true })
            yield return [(int)step, operation, crash];
    }

    [Theory]
    [MemberData(nameof(InterruptionCases))]
    public void Interrupted_native_boundaries_recover_without_reset_or_resurrecting_revocation(int step, string operation, bool crash)
    {
        using var fs = new FakeLinuxFileSystem();
        var clock = new Clock();
        IssuedDeviceCredential credential;
        GatewaySignedRequest request;
        var armed = false;
        using (var host = new Authority(fs, clock, true, nativeFault: observed =>
        {
            if (armed && (int)observed == step) throw new IOException("Synthetic interruption.");
        }))
        {
            credential = host.Issue();
            request = host.Sign(credential);
            armed = true;
            Assert.Equal("auth.storage", Assert.Throws<GatewayProtocolException>(() =>
            {
                switch (operation)
                {
                    case "issue": host.Issue("new-device"); break;
                    case "revoke": host.Credentials.RevokeDevice("fixture-device"); break;
                    case "rotate": host.Credentials.Rotate(credential.CredentialId, TimeSpan.FromMinutes(1)); break;
                    default: host.Credentials.Authenticate(request); break;
                }
            }).Failure.Code);
        }
        if (crash) fs.Crash();
        var prepared = !crash || step != (int)LinuxStoreStep.PreparedRenamed;
        using var recovered = new Authority(fs, clock);
        var records = recovered.Credentials.ListRegistrations();
        Assert.Equal(prepared ? operation switch { "issue" or "rotate" => 2, "revoke" => 0, _ => 1 } : 1, records.Count);
        if (operation == "nonce" && prepared)
            Assert.Equal("auth.replay", Assert.Throws<GatewayProtocolException>(() => recovered.Credentials.Authenticate(request)).Failure.Code);
        if (operation == "revoke" && prepared)
            Assert.Equal("auth.invalid", Assert.Throws<GatewayProtocolException>(() => recovered.Credentials.Authenticate(request)).Failure.Code);
        Assert.DoesNotContain("pending.bin", fs.Store.Children.Keys);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Permanent_pairing_replay_and_clock_failures_are_not_boot_expiry(bool changedBoot)
    {
        using var fs = new FakeLinuxFileSystem();
        var clock = new Clock();
        IssuedDeviceCredential credential;
        GatewaySignedRequest request;
        using (var host = new Authority(fs, clock, true))
        {
            credential = host.Issue();
            request = host.Sign(credential);
            host.Credentials.Authenticate(request);
        }
        if (changedBoot) fs.Boot = Guid.NewGuid();
        using (var reopened = new Authority(fs, clock))
        {
            Assert.IsType<PairedDeviceLifetime>(Assert.Single(reopened.Credentials.ListRegistrations()).Lifetime);
            Assert.Equal("auth.replay", Assert.Throws<GatewayProtocolException>(() => reopened.Credentials.Authenticate(request)).Failure.Code);
        }
        clock.Utc -= TimeSpan.FromSeconds(1);
        using (var held = new Authority(fs, clock))
            Assert.Single(held.Credentials.ListRegistrations());
        clock.Utc -= GatewayCredentialStore.MaximumClockStepBack;
        Failure(GatewayPersistenceFailure.ClockUnavailable, () => new Authority(fs, clock));
        clock.Advance(TimeSpan.FromDays(3650));
        using var yearsLater = new Authority(fs, clock);
        Assert.Single(yearsLater.Credentials.ListRegistrations());
        yearsLater.Credentials.Authenticate(yearsLater.Sign(credential));
    }

    [Theory]
    [InlineData("authority")]
    [InlineData("pending")]
    [InlineData("foreign-stage")]
    [InlineData("windows")]
    [InlineData("legacy")]
    public void Corrupt_or_foreign_state_is_preserved_without_fallback(string kind)
    {
        using var fs = new FakeLinuxFileSystem();
        var clock = new Clock();
        using (var host = new Authority(fs, clock, true)) host.Issue();
        if (kind == "authority") fs.Store.Children["authority.bin"].Bytes[^1] ^= 1;
        if (kind == "pending") fs.Put("pending.bin", [1, 2, 3]);
        if (kind == "windows") "MRTLHM02"u8.CopyTo(fs.Store.Children["authority.bin"].Bytes);
        if (kind == "legacy") "MRTLHM01"u8.CopyTo(fs.Store.Children["authority.bin"].Bytes);
        if (kind == "foreign-stage")
        {
            using var other = new FakeLinuxFileSystem();
            using (var host = new Authority(other, clock, true)) host.Issue();
            fs.Put("staging.bin", other.Store.Children["authority.bin"].Bytes);
        }
        var before = fs.Store.Children.ToDictionary(pair => pair.Key, pair => pair.Value.Bytes.ToArray());
        Assert.Throws<GatewayPersistenceException>(() => new Authority(fs, clock));
        Assert.Equal(before.Keys.Order(), fs.Store.Children.Keys.Order());
        foreach (var entry in before) Assert.Equal(entry.Value, fs.Store.Children[entry.Key].Bytes);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(3)]
    public void Only_owned_unprepared_partial_stage_is_discarded_after_valid_authority(int length)
    {
        using var fs = new FakeLinuxFileSystem();
        var clock = new Clock();
        using (var host = new Authority(fs, clock, true)) host.Issue();
        fs.Put("staging.bin", new byte[length]);
        using var recovered = new Authority(fs, clock);
        Assert.Single(recovered.Credentials.ListRegistrations());
        Assert.DoesNotContain("staging.bin", fs.Store.Children.Keys);
    }

    [Fact]
    public void Opposite_backend_magic_is_a_distinct_preserved_compatibility_failure()
    {
        var bytes = "MRTLUP02"u8.ToArray();
        Failure(GatewayPersistenceFailure.StorageBackendMismatch, () => StoreFormat.Unwrap(bytes));
        Failure(GatewayPersistenceFailure.StorageBackendMismatch, () => new LinuxPermissionEnvelope().Decode("MRTLHM02"u8.ToArray()));
    }

    internal static void Failure(GatewayPersistenceFailure expected, Action action) =>
        Assert.Equal(expected, Assert.Throws<GatewayPersistenceException>(action).Failure);
}
