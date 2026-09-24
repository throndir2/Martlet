using System.Text;
using System.Text.Json;
using Martlet.Gateway.Host.Linux;
using Martlet.Gateway.Persistence;
using Martlet.Gateway.Persistence.Portable.Tests;

namespace Martlet.Gateway.Host.Linux.Tests;

public sealed class ConfigurationTests
{
    internal static byte[] Config(string origin = "https://127.0.0.1:9443", string mode = "loopback") =>
        JsonSerializer.SerializeToUtf8Bytes(new
        {
            schemaVersion = 1, hostId = "fixture-host", stateDirectory = "/srv/martlet/store",
            storageBackend = "linuxServicePermissions", binding = new { mode, origin },
            serviceUid = 1000, serviceGid = 1000
        });

    internal static void WriteConfig(FakeLinuxFileSystem fs, byte[] bytes)
    {
        var parent = fs.OpenRoot();
        var srv = fs.OpenAt(parent, "srv", 0x10000 | 0x20000 | 0x80000, 0, 0xe);
        var dir = fs.OpenAt(srv, "martlet", 0x10000 | 0x20000 | 0x80000, 0, 0xe);
        if (!fs.Parent.Children.ContainsKey("host.json"))
        {
            var fd = fs.OpenAt(dir, "host.json", 2 | 0x40 | 0x80 | 0x20000 | 0x80000, 0x180, 0xf);
            fs.Close(fd);
        }
        fs.Parent.Children["host.json"].Bytes = bytes.ToArray();
        fs.Close(dir);
        fs.Close(srv);
        fs.Close(parent);
    }

    [Theory]
    [InlineData("\"schemaVersion\":1", "\"schemaVersion\":2")]
    [InlineData("\"schemaVersion\":1", "\"schemaVersion\":1,\"schemaVersion\":1")]
    [InlineData("\"schemaVersion\":1", "\"schemaVersion\":1,\"approved\":true")]
    [InlineData("\"serviceUid\":1000", "\"serviceUid\":0")]
    [InlineData("\"serviceUid\":1000", "\"serviceUid\":\"1000\"")]
    [InlineData("\"hostId\":\"fixture-host\"", "\"hostId\":null")]
    [InlineData("/srv/martlet/store", "/srv/../store")]
    [InlineData("linuxServicePermissions", "windowsCurrentUserDpapi")]
    [InlineData("https://127.0.0.1:9443", "https://0.0.0.0:9443")]
    [InlineData("https://127.0.0.1:9443", "https://localhost:9443")]
    [InlineData("https://127.0.0.1:9443", "https://192.168.1.1:9443")]
    [InlineData("https://127.0.0.1:9443", "https://127.0.0.2:9443")]
    public void Invalid_configuration_is_not_coerced(string before, string after) =>
        Assert.Throws<HostInputException>(() => HostConfiguration.Parse(
            Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(Config()).Replace(before, after, StringComparison.Ordinal))));

    [Theory]
    [InlineData("https://10.2.3.4:9443")]
    [InlineData("https://172.16.0.1:9443")]
    [InlineData("https://192.168.1.2:9443")]
    [InlineData("https://[fd12::1]:9443")]
    public void Private_origin_requires_explicit_mode(string origin) =>
        Assert.Equal(origin, HostConfiguration.Parse(Config(origin, "privateIp")).Binding.Origin.CanonicalOrigin);

    [Fact]
    public void Default_No_binding_factories_are_inert_even_with_invalid_inputs()
    {
        Assert.False(DurableGatewayHost.CreateNewForBinding(null!, null!, null!, default, null!, null!).Enabled);
        Assert.False(DurableGatewayHost.OpenExistingForBinding(null!, null!, default, null!, null!, null!).Enabled);
        Assert.False(DurableGatewayHost.RebindForLocalHost(null!, null!, default, null!, null!, null!).Enabled);
    }

    [Theory]
    [InlineData("/srv/martlet")]
    [InlineData("/srv/martlet/host.json")]
    [InlineData("/srv/martlet/service-approval.json")]
    [InlineData("/srv/martlet/service-approval.staging")]
    [InlineData("/srv/martlet/service-approval.staging/identity")]
    public async Task Control_file_collisions_fail_before_creating_state(string state)
    {
        using var platform = new FixturePlatform();
        ConfigurationTests.WriteConfig(platform.Fs, Encoding.UTF8.GetBytes(
            Encoding.UTF8.GetString(ConfigurationTests.Config()).Replace("/srv/martlet/store", state, StringComparison.Ordinal)));
        using var output = new StringWriter();
        platform.Terminal = new("yes");
        Assert.Equal(2, await platform.Run("init", output));
        Assert.Equal(0, platform.Opens);
        Assert.Single(platform.Fs.Parent.Children);
    }

    [Fact]
    public void Passive_config_and_receipt_custody_never_create_authority_or_lock()
    {
        using var fs = new FakeLinuxFileSystem();
        WriteConfig(fs, Config());
        fs.Calls.Clear();
        using (var dir = new LinuxControlDirectory("/srv/martlet/host.json", fs))
        {
            var config = HostConfiguration.Parse(dir.Read(LinuxControlDirectory.Config, 8192)!);
            config.CheckIdentity(dir);
            Assert.Null(dir.Read(LinuxControlDirectory.Approval, 8192));
            Assert.Single(fs.Parent.Children);
        }
        Assert.Equal(0, fs.OpenHandles);
        Assert.DoesNotContain(fs.Calls, call => call.StartsWith("write:", StringComparison.Ordinal) || call == "lock");
    }

    [Fact]
    public void Receipt_is_bound_to_bytes_uid_gid_and_identity_and_removed_without_touching_state()
    {
        using var fs = new FakeLinuxFileSystem();
        WriteConfig(fs, Config());
        using var dir = new LinuxControlDirectory("/srv/martlet/host.json", fs);
        var config = HostConfiguration.Parse(Config());
        var identity = new GatewayHostIdentity { HostId = "fixture-host", SpkiFingerprint = "sha256:" + new string('a', 64) };
        dir.WriteApproval(ServiceApproval.Create(config, identity));
        var approval = ServiceApproval.Parse(dir.Read(LinuxControlDirectory.Approval, 8192)!);
        approval.Check(config, dir);
        Assert.Equal(identity, approval.Identity);
        Assert.Throws<HostApprovalException>(() => (approval with { ServiceGid = 1001 }).Check(config, dir));
        Assert.Throws<HostApprovalException>(() => (approval with { HostId = "different" }).Check(config, dir));
        WriteConfig(fs, Config().Concat(new byte[] { 32 }).ToArray());
        Assert.Throws<HostApprovalException>(() => approval.Check(config, dir));
        var changed = HostConfiguration.Parse(fs.Parent.Children["host.json"].Bytes);
        Assert.Throws<HostApprovalException>(() => approval.Check(changed, dir));
        dir.RemoveApproval();
        Assert.Null(dir.Read(LinuxControlDirectory.Approval, 8192));
        Assert.Single(fs.Parent.Children);
    }

    [Theory]
    [InlineData("file-mode")]
    [InlineData("file-owner")]
    [InlineData("file-links")]
    [InlineData("file-acl")]
    [InlineData("parent-mode")]
    [InlineData("parent-acl")]
    [InlineData("filesystem")]
    public void Unsafe_custody_fails_without_repair(string problem)
    {
        using var fs = new FakeLinuxFileSystem();
        WriteConfig(fs, Config());
        var file = fs.Parent.Children["host.json"];
        switch (problem)
        {
            case "file-mode": file.Identity = file.Identity with { Mode = 0x81a4 }; break;
            case "file-owner": file.Identity = file.Identity with { User = 2000 }; break;
            case "file-links": file.Identity = file.Identity with { Links = 2 }; break;
            case "file-acl": file.Acl = true; break;
            case "parent-mode": fs.Parent.Identity = fs.Parent.Identity with { Mode = 0x41ed }; break;
            case "parent-acl": fs.Parent.Acl = true; break;
            case "filesystem": fs.SupportedFileSystem = false; break;
        }
        var bytes = file.Bytes.ToArray();
        Assert.Throws<GatewayPersistenceException>(() =>
        {
            using var dir = new LinuxControlDirectory("/srv/martlet/host.json", fs);
            dir.Read(LinuxControlDirectory.Config, 8192);
        });
        Assert.Equal(bytes, file.Bytes);
        Assert.Equal(0, fs.OpenHandles);
    }

    [Fact]
    public void Staging_leftover_is_not_overwritten_or_mistaken_for_approval()
    {
        using var fs = new FakeLinuxFileSystem();
        WriteConfig(fs, Config());
        using var dir = new LinuxControlDirectory("/srv/martlet/host.json", fs);
        var bytes = ServiceApproval.Create(HostConfiguration.Parse(Config()),
            new() { HostId = "fixture-host", SpkiFingerprint = "sha256:" + new string('a', 64) });
        fs.Fault = call => { if (call == "rename:service-approval.staging:service-approval.json") throw new IOException(); };
        // The injected write failure leaves the exact staging inode for explicit reconciliation.
        fs.TransferLimit = 0;
        Assert.Throws<GatewayPersistenceException>(() => dir.WriteApproval(bytes));
        fs.TransferLimit = int.MaxValue;
        Assert.Throws<GatewayPersistenceException>(() => dir.WriteApproval(bytes));
        Assert.Null(dir.Read(LinuxControlDirectory.Approval, 8192));
    }
}
