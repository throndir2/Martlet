using System.Text;
using Martlet.Gateway.Persistence;

namespace Martlet.Gateway.Persistence.Portable.Tests;

public sealed class MacFileSystemTests
{
    [Fact]
    public void Linux_open_flags_translate_to_macos_flags_and_unknown_flags_are_refused()
    {
        Assert.Equal(0x100000 | 0x100 | 0x1000000,
            MacFileSystem.TranslateFlags(LinuxFileSystem.Directory | LinuxFileSystem.NoFollow | LinuxFileSystem.CloseOnExec));
        Assert.Equal(2 | 0x200 | 0x800 | 0x4,
            MacFileSystem.TranslateFlags(LinuxFileSystem.ReadWrite | LinuxFileSystem.Create | LinuxFileSystem.Exclusive | LinuxFileSystem.NonBlocking));
        Assert.Equal(0, MacFileSystem.TranslateFlags(0));
        var unknown = Assert.Throws<GatewayPersistenceException>(() => MacFileSystem.TranslateFlags(0x400000));
        Assert.Equal(GatewayPersistenceFailure.UnsupportedPlatform, unknown.Failure);
    }

    [Theory]
    [InlineData("authority.bin", true)]
    [InlineData(".", true)]
    [InlineData("..", false)]
    [InlineData("a/b", false)]
    [InlineData("", false)]
    public void Only_one_name_beneath_a_held_directory_is_opened(string name, bool allowed) =>
        Assert.Equal(allowed, MacFileSystem.SingleName(name));

    [Fact]
    public void Stat_identity_reads_the_64_bit_inode_layout()
    {
        var stat = new byte[144];
        BitConverter.GetBytes(0x01000004u).CopyTo(stat, 0);
        BitConverter.GetBytes((ushort)0x8180).CopyTo(stat, 4);
        BitConverter.GetBytes((ushort)1).CopyTo(stat, 6);
        BitConverter.GetBytes(123456789UL).CopyTo(stat, 8);
        BitConverter.GetBytes(501u).CopyTo(stat, 16);
        BitConverter.GetBytes(4096L).CopyTo(stat, 96);
        var identity = MacFileSystem.Identity(stat);
        Assert.Equal(123456789UL, identity.Inode);
        Assert.Equal(0x01000004UL, identity.Mount);
        Assert.Equal(1u, identity.DeviceMajor);
        Assert.Equal(4u, identity.DeviceMinor);
        Assert.Equal(501u, identity.User);
        Assert.Equal((ushort)0x8180, identity.Mode);
        Assert.Equal(1u, identity.Links);
        Assert.Equal(4096, identity.Length);
    }

    [Theory]
    [InlineData("apfs", 0x1000u, true)]
    [InlineData("apfs", 0x1001u, false)]
    [InlineData("apfs", 0u, false)]
    [InlineData("smbfs", 0x1000u, false)]
    [InlineData("hfs", 0x1000u, false)]
    [InlineData("msdos", 0x1000u, false)]
    public void Only_local_read_write_apfs_is_admitted(string type, uint flags, bool admitted)
    {
        var statfs = new byte[2168];
        BitConverter.GetBytes(flags).CopyTo(statfs, 64);
        Encoding.ASCII.GetBytes(type).CopyTo(statfs, 72);
        Assert.Equal(admitted, MacFileSystem.AdmittedVolume(statfs));
    }

    [Theory]
    [InlineData(2, GatewayPersistenceFailure.StoreMissing)]
    [InlineData(62, GatewayPersistenceFailure.InsecureStorage)]
    [InlineData(18, GatewayPersistenceFailure.InsecureStorage)]
    [InlineData(17, GatewayPersistenceFailure.RecoveryRequired)]
    [InlineData(45, GatewayPersistenceFailure.UnsupportedPlatform)]
    [InlineData(28, GatewayPersistenceFailure.StorageFailed)]
    public void Macos_errors_map_to_the_same_failures_as_linux(int error, GatewayPersistenceFailure failure) =>
        Assert.Equal(failure, MacFileSystem.NativeFailure(error).Failure);

    [Fact]
    public void Arm64_linux_numbers_directory_and_no_follow_differently()
    {
        var flags = LinuxFileSystem.Directory | LinuxFileSystem.NoFollow | LinuxFileSystem.CloseOnExec | LinuxFileSystem.NonBlocking;
        Assert.Equal(0x4000 | 0x8000 | 0x80000 | 0x800, LinuxFileSystem.NativeFlags(flags, arm64: true));
        Assert.Equal(LinuxFileSystem.ReadWrite | LinuxFileSystem.Create | LinuxFileSystem.Exclusive,
            LinuxFileSystem.NativeFlags(LinuxFileSystem.ReadWrite | LinuxFileSystem.Create | LinuxFileSystem.Exclusive, arm64: true));
    }

    [Fact]
    public void The_native_backend_is_selected_per_system()
    {
        Assert.Equal(OperatingSystem.IsLinux() || OperatingSystem.IsMacOS(), PosixFileSystem.Supported);
        if (!PosixFileSystem.Supported)
            Assert.Equal(GatewayPersistenceFailure.UnsupportedPlatform,
                Assert.Throws<GatewayPersistenceException>(PosixFileSystem.Create).Failure);
    }
}
