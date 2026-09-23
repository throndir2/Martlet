using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Martlet.Gateway;
using Martlet.Gateway.Persistence;
using Martlet.Gateway.Persistence.Portable.Tests;

[assembly: CollectionBehavior(DisableTestParallelization = true)]
[assembly: SupportedOSPlatform("linux")]

namespace Martlet.Gateway.Persistence.Linux.Tests;

public sealed class NativeTests : IDisposable
{
    private readonly string path;
    private readonly LinuxFileSystem fileSystem;

    public NativeTests()
    {
        Assert.True(OperatingSystem.IsLinux() && RuntimeInformation.ProcessArchitecture == Architecture.X64,
            "NOT RUN: native Linux x86_64 acceptance requires a separately authorized Linux host; no platform skip substitutes.");
        var parent = Environment.GetEnvironmentVariable("MARTLET_LINUX_TEST_PARENT");
        Assert.False(string.IsNullOrEmpty(parent),
            "NOT RUN: provide an explicitly authorized existing private ext4 parent; tests never provision a UID or repair permissions.");
        path = parent!.TrimEnd('/') + "/martlet-native-" + Guid.NewGuid().ToString("N");
        fileSystem = new LinuxFileSystem();
    }

    [Fact]
    public async Task Real_handle_permissions_cooperative_lock_and_persistent_reopen()
    {
        using (var store = Create())
        {
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(path));
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path + "/authority.bin"));
            Assert.Equal(0, await Child("--lock", path));
            Assert.NotEqual(Guid.Empty, fileSystem.BootIdentity());
        }
        using var reopened = Open();
        Assert.Equal("fixture-host", reopened.HostId);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public Task Actual_linux_owner_pinned_pairing_replay_renewal_and_revocation(bool failNonce, bool renew) =>
        OwnerScenario.RunAsync(path, null, failNonce, renew);

    [Theory]
    [InlineData((int)StoreStep.StagingFlushed)]
    [InlineData((int)StoreStep.PendingFlushed)]
    [InlineData((int)StoreStep.Replaced)]
    [InlineData((int)StoreStep.CheckpointCommitted)]
    public async Task Actual_subprocess_interruption_reopens_same_authority(int step)
    {
        using (var store = Create()) { }
        Assert.Equal(71, await Child("--crash", path, step.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        using var recovered = Open();
        Assert.Equal(step == (int)StoreStep.StagingFlushed ? 0 : 1, recovered.Initial.Credentials.Length);
    }

    [Fact]
    public void Actual_symlink_and_unknown_entries_fail_closed_without_following()
    {
        using (var store = Create()) { }
        var bytes = File.ReadAllBytes(path + "/authority.bin");
        File.CreateSymbolicLink(path + "/staging.bin", "authority.bin");
        Assert.Equal(GatewayPersistenceFailure.InsecureStorage, Assert.Throws<GatewayPersistenceException>(() => Open()).Failure);
        Assert.Equal(bytes, File.ReadAllBytes(path + "/authority.bin"));
        File.Delete(path + "/staging.bin");
        File.WriteAllBytes(path + "/foreign.bin", [1, 2, 3]);
        try
        {
            Assert.Equal(GatewayPersistenceFailure.RecoveryRequired, Assert.Throws<GatewayPersistenceException>(() => Open()).Failure);
            Assert.Equal(new byte[] { 1, 2, 3 }, File.ReadAllBytes(path + "/foreign.bin"));
        }
        finally { File.Delete(path + "/foreign.bin"); }
        CryptographicOperations.ZeroMemory(bytes);
    }

    private AuthorityStore Create()
    {
        using var certificate = HostCertificate.Create(DateTimeOffset.UtcNow.AddSeconds(-1));
        var bytes = certificate.Export(X509ContentType.Pkcs12);
        try
        {
            return AuthorityStore.Create(LinuxOwnedDirectory.Open(path, true, fileSystem), new LinuxPermissionEnvelope(),
                fileSystem.BootIdentity(), "fixture-host", bytes, DateTimeOffset.UtcNow, null);
        }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    private AuthorityStore Open() => AuthorityStore.Open(LinuxOwnedDirectory.Open(path, false, fileSystem),
        new LinuxPermissionEnvelope(), fileSystem.BootIdentity(), DateTimeOffset.UtcNow, null);

    private static async Task<int> Child(params string[] arguments)
    {
        var root = Environment.GetEnvironmentVariable("DOTNET_ROOT");
        Assert.False(string.IsNullOrEmpty(root), "Use the explicitly selected test SDK/runtime host.");
        var start = new ProcessStartInfo(System.IO.Path.Combine(root!, "dotnet"))
        {
            UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true
        };
        start.ArgumentList.Add(typeof(NativeTests).Assembly.Location);
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        try
        {
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
            await Task.WhenAll(stdout, stderr);
            return process.ExitCode;
        }
        finally
        {
            if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); }
        }
    }

    public void Dispose()
    {
        foreach (var name in new[] { "owner.lock", "authority.bin", "running", "staging.bin", "pending.bin" })
            File.Delete(path + "/" + name);
        if (System.IO.Directory.Exists(path)) System.IO.Directory.Delete(path, recursive: false);
    }
}

internal static class Program
{
    private static int Main(string[] args)
    {
        if (!OperatingSystem.IsLinux() || args.Length is < 2 or > 3) return 2;
        var fs = new LinuxFileSystem();
        if (args[0] == "--lock" && args.Length == 2)
        {
            try { using var directory = LinuxOwnedDirectory.Open(args[1], false, fs); return 3; }
            catch (GatewayPersistenceException error) when (error.Failure == GatewayPersistenceFailure.StoreBusy) { return 0; }
        }
        if (args[0] != "--crash" || args.Length != 3 || !Enum.TryParse<StoreStep>(args[2], out var step) ||
            !Enum.IsDefined(step)) return 2;
        using var store = AuthorityStore.Open(LinuxOwnedDirectory.Open(args[1], false, fs),
            new LinuxPermissionEnvelope(), fs.BootIdentity(), DateTimeOffset.UtcNow,
            observed => { if (observed == step) Environment.Exit(71); });
        var encoded = store.CopyCertificate();
        try
        {
            using var certificate = HostCertificate.Load(encoded);
            var identity = GatewayHostIdentity.FromCertificate(store.HostId, certificate);
            var credentials = new GatewayCredentialStore(identity, TimeProvider.System, store.Initial, store, store.InitialSameBoot);
            try { credentials.Issue("fixture-device", "Fixture", [GatewayRole.Voice]); }
            finally { credentials.Close(); }
        }
        finally { CryptographicOperations.ZeroMemory(encoded); }
        return 3;
    }
}
