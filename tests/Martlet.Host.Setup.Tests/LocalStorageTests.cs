using System.Text;

namespace Martlet.Host.Setup.Tests;

public sealed class LocalStorageTests
{
    [Theory]
    [InlineData("CON.json")]
    [InlineData("nul.json")]
    [InlineData("AUX.json")]
    [InlineData("PRN.json")]
    [InlineData("CONIN$.json")]
    [InlineData("CONOUT$.json")]
    [InlineData("CLOCK$.json")]
    [InlineData("COM1.json")]
    [InlineData("LPT9.json")]
    [InlineData("COM\u00b9.json")]
    [InlineData("LPT\u00b2.json")]
    [InlineData("COM\u00b3.json")]
    [InlineData("NUL .json")]
    [InlineData("NUL.json\\review.json")]
    [InlineData("directory.\\review.json")]
    [InlineData("directory \\review.json")]
    public void Windows_device_aliases_and_ambiguous_components_are_rejected_before_IO(string relativePath)
    {
        if (!OperatingSystem.IsWindows()) return;
        using var store = new ReviewStore();
        var path = Path.Combine(Path.GetDirectoryName(store.Path)!, relativePath);
        var error = Assert.Throws<SetupException>(() =>
            new LocalSetupFileSystem(path, new RecordingDirectoryCommitter()));
        Assert.Equal(SetupFailure.InvalidConfiguration, error.Failure);
        Assert.Equal(new[] { store.UnrelatedPath }, Directory.GetFiles(Path.GetDirectoryName(store.Path)!));
        Assert.Empty(Directory.GetDirectories(Path.GetDirectoryName(store.Path)!));
    }

    [Theory]
    [InlineData("{not-json")]
    [InlineData("{}")]
    [InlineData("{\"formatVersion\":2,\"formatVersion\":2}")]
    [InlineData("{\"\\uD800\":0}")]
    [InlineData("{\"extra\":\"\\uD800\"}")]
    public async Task Malformed_real_journal_is_preserved(string content)
    {
        using var store = new ReviewStore();
        var bytes = Encoding.UTF8.GetBytes(content);
        File.WriteAllBytes(store.Path, bytes);
        var error = await Assert.ThrowsAsync<SetupException>(
            async () => await store.Coordinator.PreviewReviewAsync(store.Plan));
        Assert.Equal(SetupFailure.JournalCorrupt, error.Failure);
        Assert.Equal(bytes, File.ReadAllBytes(store.Path));
        Assert.Equal("preserve-user-data", File.ReadAllText(store.UnrelatedPath));
    }

    [Fact]
    public async Task Invalid_UTF8_real_journal_is_preserved()
    {
        using var store = new ReviewStore();
        byte[] bytes = [0x7b, 0x22, 0xff, 0x22, 0x3a, 0x30, 0x7d];
        File.WriteAllBytes(store.Path, bytes);
        var error = await Assert.ThrowsAsync<SetupException>(
            async () => await store.Coordinator.PreviewReviewAsync(store.Plan));
        Assert.Equal(SetupFailure.JournalCorrupt, error.Failure);
        Assert.Equal(bytes, File.ReadAllBytes(store.Path));
    }

    [Fact]
    public async Task Foreign_pending_file_is_preserved_and_blocks_writes()
    {
        using var store = new ReviewStore();
        var pending = store.Path + ".pending";
        File.WriteAllText(pending, "foreign-or-interrupted");
        var error = await Assert.ThrowsAsync<SetupException>(
            async () => await store.FileSystem.WriteAtomicAsync(null, "new"u8.ToArray(), CancellationToken.None));
        Assert.Equal(SetupFailure.JournalConcurrentChange, error.Failure);
        Assert.Equal("foreign-or-interrupted", File.ReadAllText(pending));
        Assert.False(File.Exists(store.Path));
    }

    [Fact]
    public async Task Concurrent_actual_writers_commit_at_most_one_expected_version()
    {
        using var store = new ReviewStore();
        var other = new LocalSetupFileSystem(store.Path, new RecordingDirectoryCommitter());
        var first = await store.FileSystem.WriteAtomicAsync(null, "initial"u8.ToArray(), CancellationToken.None);
        using var start = new ManualResetEventSlim();
        async Task<bool> Write(ISetupFileSystem fileSystem, string value)
        {
            start.Wait();
            try
            {
                await fileSystem.WriteAtomicAsync(first.Version, Encoding.UTF8.GetBytes(value), CancellationToken.None);
                return true;
            }
            catch (SetupException error) when (error.Failure is SetupFailure.JournalConcurrentChange or SetupFailure.JournalIoFailure)
            {
                return false;
            }
        }
        var a = Task.Run(() => Write(store.FileSystem, "one"));
        var b = Task.Run(() => Write(other, "two"));
        start.Set();
        Assert.Single(await Task.WhenAll(a, b), success => success);
        Assert.Contains(File.ReadAllText(store.Path), new[] { "one", "two" });
        Assert.False(File.Exists(store.Path + ".pending"));
    }

    [Fact]
    public async Task Pre_canceled_write_and_oversized_read_preserve_bytes()
    {
        using var store = new ReviewStore();
        File.WriteAllBytes(store.Path, new byte[SetupJournalCodec.MaximumBytes + 1]);
        var before = File.ReadAllBytes(store.Path);
        var error = await Assert.ThrowsAsync<SetupException>(
            async () => await store.FileSystem.ReadAsync(SetupJournalCodec.MaximumBytes, CancellationToken.None));
        Assert.Equal(SetupFailure.JournalTooLarge, error.Failure);
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            async () => await store.FileSystem.WriteAtomicAsync(null, "new"u8.ToArray(), canceled.Token));
        Assert.Equal(before, File.ReadAllBytes(store.Path));
    }

    [Fact]
    public async Task Atomic_replace_flushes_exact_parent_and_leaves_no_pending_file()
    {
        var directory = CreateOwnedDirectory();
        try
        {
            var journalPath = Path.Combine(directory, "setup-journal-v1.json");
            var committer = new RecordingDirectoryCommitter();
            var fileSystem = new LocalSetupFileSystem(journalPath, committer);
            var first = await fileSystem.WriteAtomicAsync(null, "first"u8.ToArray(), CancellationToken.None);
            var second = await fileSystem.WriteAtomicAsync(first.Version, "second"u8.ToArray(), CancellationToken.None);
            var read = await fileSystem.ReadAsync(1024, CancellationToken.None);

            Assert.Equal("second", Encoding.UTF8.GetString(read!.Content.Span));
            Assert.Equal(second.Version, read.Version);
            Assert.Equal(new[] { directory, directory }, committer.Calls);
            Assert.False(File.Exists(journalPath + ".pending"));
            Assert.Equal(new[] { "setup-journal-v1.json" },
                Directory.GetFiles(directory).Select(Path.GetFileName).ToArray());
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Directory_commit_failure_is_explicit_and_committed_bytes_remain_reconcilable()
    {
        var directory = CreateOwnedDirectory();
        try
        {
            var journalPath = Path.Combine(directory, "setup-journal-v1.json");
            var committer = new RecordingDirectoryCommitter { Fail = true };
            var fileSystem = new LocalSetupFileSystem(journalPath, committer);

            var error = await Assert.ThrowsAsync<SetupException>(
                async () => await fileSystem.WriteAtomicAsync(
                    null, "uncertain-commit"u8.ToArray(), CancellationToken.None));
            var read = await fileSystem.ReadAsync(1024, CancellationToken.None);

            Assert.Equal(SetupFailure.JournalIoFailure, error.Failure);
            Assert.Equal("uncertain-commit", Encoding.UTF8.GetString(read!.Content.Span));
            Assert.Single(committer.Calls);
            Assert.False(File.Exists(journalPath + ".pending"));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static string CreateOwnedDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "Martlet.Host.Setup.Tests." + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}
