using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using Martlet.Gateway;
using Martlet.Gateway.Persistence;

namespace Martlet.Gateway.Persistence.Tests;

public sealed class StorageTests : NativeTest
{
    [Fact]
    public void Format_rejects_invalid_roles_keys_budgets_and_collection_boundaries()
    {
        var clock = new Clock();
        using var host = new NativeAuthority(Store, clock, create: true);
        var credential = host.Issue();
        host.Credentials.Authenticate(host.Signed(credential));
        var cipher = StoreFormat.Unwrap(File.ReadAllBytes(Path.Combine(Store, "authority.bin")));
        var clear = WindowsProtection.Unprotect(cipher);
        try
        {
            using var document = StoreFormat.Read(clear);
            var record = Assert.Single(document.State.Credentials);
            var nonce = Assert.Single(record.Nonces);
            var invalid = new[]
            {
                record with { Roles = [(GatewayRole)100] },
                record with { Roles = [GatewayRole.Voice, GatewayRole.Voice] },
                record with { SigningKey = new byte[31] },
                record with { CredentialId = "foreign-id" },
                record with { RemainingTicks = 0 },
                record with { RemainingTicks = GatewayCredentialStore.MaximumLifetime.Ticks + 1 },
                record with { Nonces = [nonce, nonce] },
                record with { Nonces = Enumerable.Repeat(nonce, 1025).ToArray() },
                record with { Nonces = [nonce with { ExpiresAt = clock.GetUtcNow().AddMinutes(5) }] }
            };
            foreach (var rejected in invalid)
                Assert.Throws<GatewayPersistenceException>(() =>
                    StoreFormat.Write(document with { State = document.State with { Credentials = [rejected] } }));
            Assert.Throws<GatewayPersistenceException>(() => StoreFormat.Write(document with
            {
                State = document.State with { Credentials = Enumerable.Repeat(record, 129).ToArray() }
            }));
            var json = Encoding.UTF8.GetString(clear);
            Assert.Throws<GatewayPersistenceException>(() => StoreFormat.Read(Encoding.UTF8.GetBytes(
                json.Insert(1, "\"Unknown\":true,"))));
            Assert.Throws<GatewayPersistenceException>(() => StoreFormat.Read(Encoding.UTF8.GetBytes(
                json.Replace("\"Version\":1", "\"Version\":1,\"Version\":1", StringComparison.Ordinal))));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(clear);
            CryptographicOperations.ZeroMemory(cipher);
        }
    }
    [Fact]
    public void Changed_os_boot_requires_explicit_device_reset()
    {
        var clock = new Clock();
        using (var host = new NativeAuthority(Store, clock, create: true))
        {
            host.Issue();
            host.Clean();
        }
        var boot = Guid.NewGuid();
        Assert.Equal(GatewayPersistenceFailure.RecoveryRequired, Assert.Throws<GatewayPersistenceException>(() =>
            WindowsAuthorityStore.Open(Store, false, clock.GetUtcNow(), null, clock, boot)).Failure);
        using var reset = WindowsAuthorityStore.Open(Store, true, clock.GetUtcNow(), null, clock, boot);
        Assert.Empty(reset.Initial.Credentials);
    }
    [Fact]
    public void Actual_full_capacity_protected_checkpoint_fits_sixteen_MiB_and_roundtrips()
    {
        var clock = new Clock();
        var now = clock.GetUtcNow();
        using var host = new NativeAuthority(Store, clock, create: true);
        var registrations = Enumerable.Range(0, GatewayCredentialStore.MaximumRegistrations).Select(index =>
            new StoredGatewayCredential(
                Base64Url.Encode(RandomNumberGenerator.GetBytes(16)), new string('a', 64), new string('<', 64),
                [GatewayRole.Voice, GatewayRole.Perception, GatewayRole.Memory],
                RandomNumberGenerator.GetBytes(32), now, now.AddDays(90), TimeSpan.FromDays(90).Ticks,
                Base64Url.Encode(RandomNumberGenerator.GetBytes(16)),
                Enumerable.Range(0, GatewayCredentialStore.MaximumNoncesPerCredential).Select(_ =>
                    new StoredGatewayNonce(Base64Url.Encode(RandomNumberGenerator.GetBytes(24)),
                        now.AddMinutes(4).AddTicks(1))).ToArray())).ToArray();
        using var checkpoint = new GatewayCheckpoint(now, registrations, clock.GetTimestamp(), clock.TimestampFrequency);
        var stopwatch = Stopwatch.StartNew();
        host.Storage.Commit(checkpoint);
        var envelope = File.ReadAllBytes(Path.Combine(Store, "authority.bin"));
        Assert.InRange(envelope.Length, 1, StoreFormat.MaximumBytes);
        var cipher = StoreFormat.Unwrap(envelope);
        var clear = WindowsProtection.Unprotect(cipher);
        try
        {
            using var decoded = StoreFormat.Read(clear);
            Assert.Equal(128, decoded.State.Credentials.Length);
            Assert.All(decoded.State.Credentials, credential => Assert.Equal(1024, credential.Nonces.Length));
            var encoded = StoreFormat.Write(decoded);
            try { Assert.Equal(clear, encoded); }
            finally { CryptographicOperations.ZeroMemory(encoded); }
            Console.WriteLine($"Full-capacity protected checkpoint: {envelope.Length} bytes, {stopwatch.ElapsedMilliseconds} ms.");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(cipher);
            CryptographicOperations.ZeroMemory(clear);
        }
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("null")]
    [InlineData("{\"Version\":1,\"Version\":1}")]
    [InlineData("{\"Version\":2}")]
    public void Malformed_documents_are_rejected_without_echo(string json)
    {
        var error = Assert.Throws<GatewayPersistenceException>(() => StoreFormat.Read(Encoding.UTF8.GetBytes(json)));
        Assert.Equal(GatewayPersistenceFailure.InvalidState, error.Failure);
        Assert.DoesNotContain(json, error.Message);
    }

    [Fact]
    public void Foreign_protocol_and_prototype_envelope_are_rejected_without_translation()
    {
        using var authority = new NativeAuthority(Store, new Clock(), create: true);
        authority.Issue();
        var bytes = File.ReadAllBytes(Path.Combine(Store, "authority.bin"));
        var cipher = StoreFormat.Unwrap(bytes);
        var clear = WindowsProtection.Unprotect(cipher);
        try
        {
            using var document = StoreFormat.Read(clear);
            Assert.Throws<GatewayPersistenceException>(() => StoreFormat.Write(document with { Protocol = "trust" }));
            Assert.Throws<GatewayPersistenceException>(() => StoreFormat.Write(document with { Algorithm = "bearer" }));
            Assert.Throws<GatewayPersistenceException>(() => StoreFormat.Write(document with { Version = 2 }));
            "MARTLET1"u8.CopyTo(bytes);
            Assert.Throws<GatewayPersistenceException>(() => StoreFormat.Unwrap(bytes));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(cipher);
            CryptographicOperations.ZeroMemory(clear);
        }
    }

    [Fact]
    public void Concurrent_owner_plain_disposal_and_unknown_files_fail_closed()
    {
        var clock = new Clock();
        using (var host = new NativeAuthority(Store, clock, create: true))
        {
            Assert.Equal(GatewayPersistenceFailure.StoreBusy,
                Assert.Throws<GatewayPersistenceException>(() => new NativeAuthority(Store, clock)).Failure);
            host.Issue();
        }
        Assert.Equal(GatewayPersistenceFailure.RecoveryRequired,
            Assert.Throws<GatewayPersistenceException>(() => new NativeAuthority(Store, clock)).Failure);
        File.WriteAllText(Path.Combine(Store, "backup.bin"), "fixture");
        Assert.Equal(GatewayPersistenceFailure.RecoveryRequired,
            Assert.Throws<GatewayPersistenceException>(() => new NativeAuthority(Store, clock, reset: true)).Failure);
    }

    [Fact]
    public void Broadened_acl_is_refused_without_repair()
    {
        using (var host = new NativeAuthority(Store, new Clock(), create: true)) host.Clean();
        var directory = new DirectoryInfo(Store);
        var security = directory.GetAccessControl();
        security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.WorldSid, null),
            FileSystemRights.Read, AccessControlType.Allow));
        directory.SetAccessControl(security);
        Assert.Equal(GatewayPersistenceFailure.InsecureStorage,
            Assert.Throws<GatewayPersistenceException>(() => new NativeAuthority(Store, new Clock())).Failure);
        Assert.Contains(directory.GetAccessControl().GetAccessRules(true, true, typeof(SecurityIdentifier))
            .Cast<FileSystemAccessRule>(), rule => rule.IdentityReference.Value == "S-1-1-0");
    }

    [Fact]
    public void Hardlinked_state_is_refused()
    {
        using (var host = new NativeAuthority(Store, new Clock(), create: true)) host.Clean();
        Assert.True(CreateHardLink(Path.Combine(Root, "link.bin"), Path.Combine(Store, "authority.bin"), IntPtr.Zero));
        Assert.Equal(GatewayPersistenceFailure.InsecureStorage,
            Assert.Throws<GatewayPersistenceException>(() => new NativeAuthority(Store, new Clock())).Failure);
    }

    [Theory]
    [InlineData("cipher")]
    [InlineData("truncated")]
    [InlineData("oversized")]
    public void Corruption_never_becomes_new_authority(string kind)
    {
        using (var host = new NativeAuthority(Store, new Clock(), create: true)) host.Clean();
        var path = Path.Combine(Store, "authority.bin");
        var bytes = File.ReadAllBytes(path);
        if (kind == "cipher") bytes[^1] ^= 1;
        if (kind == "truncated") bytes = bytes[..20];
        if (kind == "oversized") bytes = new byte[StoreFormat.MaximumBytes + 1];
        File.WriteAllBytes(path, bytes);
        Assert.Throws<GatewayPersistenceException>(() => new NativeAuthority(Store, new Clock()));
        Assert.Throws<GatewayPersistenceException>(() => new NativeAuthority(Store, new Clock(), reset: true));
        Assert.Equal(bytes, File.ReadAllBytes(path));
    }

    [Theory]
    [InlineData((int)StoreStep.FenceFlushed)]
    [InlineData((int)StoreStep.PendingFlushed)]
    [InlineData((int)StoreStep.Replaced)]
    [InlineData((int)StoreStep.CheckpointCommitted)]
    public void Interrupted_initialization_is_never_implicitly_restarted(int step)
    {
        Assert.ThrowsAny<Exception>(() => new NativeAuthority(Store, new Clock(), create: true, fault: observed =>
        {
            if (observed == (StoreStep)step) throw new IOException("fixture");
        }));
        Assert.Throws<GatewayPersistenceException>(() => new NativeAuthority(Store, new Clock()));
        Assert.Throws<GatewayPersistenceException>(() => new NativeAuthority(Store, new Clock(), create: true));
    }

    [Theory]
    [InlineData((int)StoreStep.PendingFlushed, "revoke")]
    [InlineData((int)StoreStep.Replaced, "revoke")]
    [InlineData((int)StoreStep.CheckpointCommitted, "revoke")]
    [InlineData((int)StoreStep.PendingFlushed, "nonce")]
    [InlineData((int)StoreStep.Replaced, "nonce")]
    [InlineData((int)StoreStep.CheckpointCommitted, "nonce")]
    [InlineData((int)StoreStep.PendingFlushed, "rotate")]
    [InlineData((int)StoreStep.Replaced, "rotate")]
    [InlineData((int)StoreStep.CheckpointCommitted, "rotate")]
    [InlineData((int)StoreStep.PendingFlushed, "issue")]
    [InlineData((int)StoreStep.Replaced, "issue")]
    [InlineData((int)StoreStep.CheckpointCommitted, "issue")]
    [InlineData((int)StoreStep.BeforeCleanFenceRemoval, "close")]
    public async Task Actual_process_interruption_requires_explicit_device_reset(int step, string action)
    {
        var clock = new Clock();
        using (var host = new NativeAuthority(Store, clock, create: true))
        {
            host.Issue();
            host.Clean();
        }
        var sdkRoot = Environment.GetEnvironmentVariable("DOTNET_ROOT");
        Assert.False(string.IsNullOrWhiteSpace(sdkRoot));
        var start = new ProcessStartInfo(Path.Combine(sdkRoot!, "dotnet.exe"))
        {
            UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true
        };
        start.ArgumentList.Add(typeof(StorageTests).Assembly.Location);
        foreach (var arg in new[] { "--crash-probe", Store, step.ToString(), action })
            start.ArgumentList.Add(arg);
        using var process = Process.Start(start)!;
        try
        {
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
            Assert.Equal(71, process.ExitCode);
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
            }
        }
        Assert.Equal(GatewayPersistenceFailure.RecoveryRequired,
            Assert.Throws<GatewayPersistenceException>(() => new NativeAuthority(Store, new Clock())).Failure);
        using var recovered = new NativeAuthority(Store, new Clock(), reset: true);
        Assert.Empty(recovered.Credentials.ListRegistrations());
        recovered.Clean();
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateHardLinkW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLink(string link, string existing, IntPtr security);

    [Fact]
    public async Task Junction_ancestor_is_denied_without_creating_a_store()
    {
        var target = Path.Combine(Root, "target");
        var alias = Path.Combine(Root, "alias");
        Directory.CreateDirectory(target);
        var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("ComSpec")!)
        {
            UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true
        };
        start.Arguments = $"/d /c mklink /J \"{alias}\" \"{target}\"";
        using var process = Process.Start(start)!;
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(0, process.ExitCode);
        try
        {
            Assert.Equal(GatewayPersistenceFailure.InsecureStorage, Assert.Throws<GatewayPersistenceException>(() =>
                new NativeAuthority(Path.Combine(alias, "store"), new Clock(), create: true)).Failure);
            Assert.False(Directory.Exists(Path.Combine(target, "store")));
        }
        finally { Directory.Delete(alias); }
    }
}
