using System.Buffers.Binary;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Martlet.LocalStt.Tests;

public sealed class PathOwnershipTests : IDisposable
{
    private readonly string root = Directory.CreateDirectory(
        Path.Combine(Path.GetTempPath(), "Martlet.LocalStt.PathTests", Guid.NewGuid().ToString("N"))).FullName;

    public void Dispose() => Directory.Delete(root, recursive: true);

    [Fact]
    public void Directory_lease_without_file_handles_blocks_root_and_ancestor_rename()
    {
        var parent = Directory.CreateDirectory(Path.Combine(root, "parent")).FullName;
        var empty = Directory.CreateDirectory(Path.Combine(parent, "empty")).FullName;
        using (var lease = new WindowsDirectoryLease(empty))
        {
            Assert.Throws<IOException>(() => Directory.Move(empty, empty + "-moved"));
            Assert.Throws<IOException>(() => Directory.Move(parent, parent + "-moved"));
        }
        Directory.Move(empty, empty + "-moved");
    }

    [Fact]
    public async Task Verified_package_holds_directory_and_file_identity_until_disposal()
    {
        using var fixture = new PackageFixture();
        var result = await fixture.Verifier().VerifyForLaunchAsync(CancellationToken.None);
        await using (var package = Assert.IsType<VerifiedLocalSttPackage>(result.Package))
        {
            Assert.Throws<IOException>(() => Directory.Move(fixture.RuntimeDirectory, fixture.RuntimeDirectory + "-moved"));
            Assert.Throws<IOException>(() => File.Move(fixture.ExecutablePath, fixture.ExecutablePath + "-moved"));
            Assert.Throws<IOException>(() => File.WriteAllText(fixture.ModelPath, "changed"));
        }
        File.WriteAllText(fixture.ModelPath, "released");
    }

    [Fact]
    public void Native_handle_validation_rejects_a_different_path_and_hard_link_identity()
    {
        var path = Path.Combine(root, "original.txt");
        File.WriteAllText(path, "inert private fixture");
        using (var stream = File.OpenRead(path))
            Assert.Throws<LocalPathException>(() =>
                WindowsLocalPath.Validate(stream.SafeFileHandle, Path.Combine(root, "different.txt"), directory: false));
        var alias = Path.Combine(root, "alias.txt");
        if (!CreateHardLinkW(alias, path, IntPtr.Zero))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        using var linked = File.OpenRead(alias);
        Assert.Throws<LocalPathException>(() => WindowsLocalPath.Validate(linked.SafeFileHandle, alias, directory: false));
        Assert.Throws<LocalPathException>(() => WindowsLocalPath.DeleteOwnedFile(path));
        Assert.True(File.Exists(path));
    }

    [Fact]
    public void Real_junction_is_refused_before_any_followed_directory_is_owned()
    {
        var target = Directory.CreateDirectory(Path.Combine(root, "target")).FullName;
        var junction = Path.Combine(root, "junction");
        CreateJunction(junction, target);
        try
        {
            Assert.Throws<LocalPathException>(() => new PhysicalLocalPathInspector().AssertSafeExisting(junction, true));
            Assert.Throws<LocalPathException>(() => new WindowsDirectoryLease(junction));
            using var handle = WindowsLocalPath.Open(junction, directory: true, delete: false);
            Assert.Throws<LocalPathException>(() => WindowsLocalPath.Validate(handle, junction, directory: true));
        }
        finally
        {
            Directory.Delete(junction);
        }
        Assert.True(Directory.Exists(target));
    }

    [Fact]
    public async Task Preexisting_operation_directory_is_never_cleaned_or_overwritten()
    {
        var operation = Guid.NewGuid();
        var directory = Directory.CreateDirectory(Path.Combine(root, operation.ToString("N"))).FullName;
        var sentinel = Path.Combine(directory, "input.wav");
        await File.WriteAllTextAsync(sentinel, "unowned");
        using var audio = CanonicalWaveAudio.FromWave(LocalSttTestData.Wave());
        var created = await new EphemeralLocalSttWorkspaceFactory(root, new PhysicalLocalPathInspector())
            .CreateAsync(operation, audio, CancellationToken.None);
        Assert.Equal(WorkspaceStatus.UnsafePath, created.Status);
        Assert.Equal("unowned", await File.ReadAllTextAsync(sentinel));
    }

    [Fact]
    public async Task Workspace_pins_parent_identity_and_rejects_linked_transcripts_without_deleting_the_target()
    {
        using var audio = CanonicalWaveAudio.FromWave(LocalSttTestData.Wave());
        var created = await new EphemeralLocalSttWorkspaceFactory(root, new PhysicalLocalPathInspector())
            .CreateAsync(Guid.NewGuid(), audio, CancellationToken.None);
        var workspace = Assert.IsType<EphemeralLocalSttWorkspace>(created.Workspace);
        Assert.Throws<IOException>(() => Directory.Move(workspace.WorkingDirectory, workspace.WorkingDirectory + "-moved"));
        var target = Path.Combine(root, "unowned-transcript.txt");
        await File.WriteAllTextAsync(target, "unowned text");
        var linked = workspace.TranscriptPrefixPath + ".txt";
        if (!CreateHardLinkW(linked, target, IntPtr.Zero))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        Assert.Equal(WorkspaceStatus.UnsafePath, (await workspace.ReadTranscriptAsync(CancellationToken.None)).Status);
        Assert.Equal(WorkspaceStatus.UnsafePath, await workspace.CleanupAsync());
        Assert.Equal(WorkspaceStatus.UnsafePath, await workspace.CleanupAsync());
        Assert.Equal("unowned text", await File.ReadAllTextAsync(target));
        Assert.False(File.Exists(workspace.AudioPath));
    }

    [Fact]
    public async Task Oversized_physical_transcript_is_not_read_and_cleanup_still_removes_it()
    {
        using var audio = CanonicalWaveAudio.FromWave(LocalSttTestData.Wave());
        var created = await new EphemeralLocalSttWorkspaceFactory(root, new PhysicalLocalPathInspector())
            .CreateAsync(Guid.NewGuid(), audio, CancellationToken.None);
        var workspace = Assert.IsType<EphemeralLocalSttWorkspace>(created.Workspace);
        await File.WriteAllBytesAsync(workspace.TranscriptPrefixPath + ".txt",
            new byte[LocalSttPackageManifest.MaximumTranscriptBytes + 1]);
        Assert.Equal(WorkspaceStatus.OutputLimit, (await workspace.ReadTranscriptAsync(CancellationToken.None)).Status);
        Assert.Equal(WorkspaceStatus.Ready, await workspace.CleanupAsync());
        Assert.False(Directory.Exists(workspace.WorkingDirectory));
    }

    [Theory]
    [InlineData(@"C:\package.\runtime")]
    [InlineData(@"C:\package \runtime")]
    public void Root_aliases_with_trimmed_components_are_refused(string path) =>
        Assert.Throws<LocalPathException>(() => LocalPathRules.NormalizeRoot(path));

    [Theory]
    [InlineData("success")]
    [InlineData("canceled")]
    [InlineData("io")]
    public async Task Physical_transcript_reader_clears_its_scratch_copy_on_every_exit(string outcome)
    {
        var path = Path.Combine(root, "private.txt");
        await File.WriteAllTextAsync(path, "private fixture transcript");
        using var cancellation = new CancellationTokenSource();
        await using var stream = new InterruptedFileStream(path, outcome, cancellation);
        var scratch = new byte[LocalSttPackageManifest.MaximumTranscriptBytes + 1];
        if (outcome == "success")
        {
            var result = await EphemeralLocalSttWorkspace.ReadBoundedTranscriptAsync(stream, scratch, cancellation.Token);
            Assert.Equal("private fixture transcript", Encoding.UTF8.GetString(result.Bytes!));
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(result.Bytes!);
        }
        else if (outcome == "canceled")
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                EphemeralLocalSttWorkspace.ReadBoundedTranscriptAsync(stream, scratch, cancellation.Token));
        }
        else
        {
            await Assert.ThrowsAsync<IOException>(() =>
                EphemeralLocalSttWorkspace.ReadBoundedTranscriptAsync(stream, scratch, cancellation.Token));
        }
        Assert.True(stream.ReadCalls > 0);
        Assert.All(scratch, value => Assert.Equal(0, value));
    }

    private sealed class InterruptedFileStream(
        string path, string outcome, CancellationTokenSource cancellation)
        : FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.Asynchronous)
    {
        internal int ReadCalls { get; private set; }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (ReadCalls > 0 && outcome == "io")
                throw new IOException("Injected partial physical transcript read failure.");
            var count = await base.ReadAsync(buffer[..Math.Min(buffer.Length, 4)], cancellationToken);
            ReadCalls++;
            if (outcome == "canceled")
                cancellation.Cancel();
            return count;
        }
    }

    private static void CreateJunction(string path, string target)
    {
        Directory.CreateDirectory(path);
        var substitute = Encoding.Unicode.GetBytes(@"\??\" + target);
        var print = Encoding.Unicode.GetBytes(target);
        var buffer = new byte[16 + substitute.Length + 2 + print.Length + 2];
        BinaryPrimitives.WriteUInt32LittleEndian(buffer, 0xA0000003);
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(4), checked((ushort)(buffer.Length - 8)));
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(10), checked((ushort)substitute.Length));
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(12), checked((ushort)(substitute.Length + 2)));
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(14), checked((ushort)print.Length));
        substitute.CopyTo(buffer, 16);
        print.CopyTo(buffer, 18 + substitute.Length);
        using var handle = CreateFileW(path, 0x40000000, 7, IntPtr.Zero, 3, 0x02200000, IntPtr.Zero);
        if (handle.IsInvalid || !DeviceIoControl(handle, 0x900A4, buffer, buffer.Length, IntPtr.Zero, 0, out _, IntPtr.Zero))
            throw new Win32Exception(Marshal.GetLastWin32Error());
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLinkW(string path, string existing, IntPtr security);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string path, uint access, uint share, IntPtr security,
        uint creation, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(SafeFileHandle handle, uint code, byte[] input, int inputSize,
        IntPtr output, int outputSize, out int returned, IntPtr overlapped);
}
