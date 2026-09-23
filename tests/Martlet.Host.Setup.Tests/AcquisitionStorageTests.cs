using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace Martlet.Host.Setup.Tests;

public sealed class AcquisitionStorageTests
{
    private static readonly CancellationToken Ct = CancellationToken.None;

    [Fact]
    public async Task Construction_and_inspection_are_passive_and_probe_exact_directory()
    {
        using var files = new StorageFiles();
        var probe = new StorageProbe(123_456_789);
        var storage = files.CreateStorage(probe);
        Assert.Empty(Directory.GetFileSystemEntries(files.Root));
        var snapshot = await storage.InspectAsync(files.Paths, Ct);
        Assert.Equal(123_456_789, snapshot.AvailableBytes);
        Assert.Equal([files.Root], probe.Paths);
        Assert.Empty(Directory.GetFileSystemEntries(files.Root));
        Assert.Null(snapshot.PartialIdentity);
        Assert.Null(snapshot.FinalIdentity);
        Assert.Null(snapshot.QuarantineIdentity);
        Assert.Null(snapshot.JournalContent);
        Assert.False(snapshot.LeaseExists);
    }

    [Fact]
    public async Task Absent_journal_content_remains_nullable_null_when_snapshot_is_forwarded()
    {
        using var files = new StorageFiles();
        var original = await files.CreateStorage().InspectAsync(files.Paths, Ct);
        var forwarded = new ArtifactAcquisitionStorageSnapshot(
            1, original.PartialExists, original.PartialBytes,
            original.FinalExists, original.FinalBytes, original.JournalVersion,
            original.JournalContent, original.JournalPendingExists,
            original.PartialIdentity, original.FinalIdentity,
            original.QuarantineExists, original.QuarantineBytes,
            original.QuarantineIdentity, original.LeaseExists);
        Assert.False(original.JournalContent.HasValue);
        Assert.False(forwarded.JournalContent.HasValue);
        Assert.Null(forwarded.JournalVersion);
        Assert.Equal(original.Fingerprint, forwarded.Fingerprint);
    }

    [Fact]
    public async Task Real_free_space_observation_is_nonnegative()
    {
        using var files = new StorageFiles();
        var snapshot = await files.CreateStorage(new PlatformArtifactFreeSpaceProbe())
            .InspectAsync(files.Paths, Ct);
        Assert.True(snapshot.AvailableBytes >= 0);
        Assert.Empty(Directory.GetFileSystemEntries(files.Root));
    }

    [Fact]
    public async Task Partial_creation_resume_readback_and_finalize_preserve_physical_identity()
    {
        using var files = new StorageFiles();
        var storage = files.CreateStorage();
        await using var lease = await storage.AcquireLeaseAsync(files.Paths, Guid.NewGuid(), Ct);
        var bytes = new byte[90_000];
        RandomNumberGenerator.Fill(bytes);
        string identity;
        await using (var writer = await storage.OpenPartialAsync(files.Paths, 0, null, Ct))
        {
            identity = writer.Identity;
            await writer.WriteAsync(bytes.AsMemory(0, 40_000), Ct);
            await writer.FlushToDiskAsync(Ct);
            Assert.Equal(40_000, writer.Position);
        }
        var initial = await storage.InspectAsync(files.Paths, Ct);
        Assert.Equal(identity, initial.PartialIdentity);
        await Failure(ArtifactAcquisitionFailure.PartialConflict,
            async () => await storage.OpenPartialAsync(files.Paths, 0, null, Ct));
        await using (var writer = await storage.OpenPartialAsync(files.Paths, 40_000, identity, Ct))
        {
            Assert.Equal(identity, writer.Identity);
            await writer.WriteAsync(bytes.AsMemory(40_000), Ct);
            await writer.FlushToDiskAsync(Ct);
        }
        Assert.Equal(Hash(bytes), await storage.ComputePartialSha256Async(files.Paths, bytes.Length, Ct));
        await storage.FinalizeAsync(files.Paths, identity, bytes.Length, Ct);
        var final = await storage.InspectAsync(files.Paths, Ct);
        Assert.False(final.PartialExists);
        Assert.Equal(identity, final.FinalIdentity);
        Assert.Equal(Hash(bytes), await storage.ComputeFinalSha256Async(files.Paths, bytes.Length, Ct));
        Assert.Equal(bytes, File.ReadAllBytes(files.Paths.DestinationPath));
        Assert.NotEmpty(files.Committer.Calls);
    }

    [Fact]
    public async Task All_mutation_requires_a_lease_and_disposal_removes_only_own_marker()
    {
        using var files = new StorageFiles();
        var storage = files.CreateStorage();
        await Failure(ArtifactAcquisitionFailure.JournalChanged,
            async () => await storage.OpenPartialAsync(files.Paths, 0, null, Ct));
        await Failure(ArtifactAcquisitionFailure.JournalChanged,
            async () => await storage.WriteJournalAsync(files.Paths, null, "{}"u8.ToArray(), Ct));
        await Failure(ArtifactAcquisitionFailure.JournalChanged,
            async () => await storage.FinalizeAsync(files.Paths, "unowned", 0, Ct));
        await Failure(ArtifactAcquisitionFailure.JournalChanged,
            async () => await storage.DeleteOwnedPartialAsync(files.Paths, "unowned", 0, Ct));
        await Failure(ArtifactAcquisitionFailure.JournalChanged,
            async () => await storage.QuarantineOwnedFinalAsync(files.Paths, "unowned", 0, Hash([]), Ct));
        var owner = Guid.NewGuid();
        var lease = await storage.AcquireLeaseAsync(files.Paths, owner, Ct);
        Assert.Equal(owner.ToString("N"), File.ReadAllText(files.Paths.LeasePath));
        Assert.True((await storage.InspectAsync(files.Paths, Ct)).LeaseExists);
        await lease.DisposeAsync();
        await lease.DisposeAsync();
        Assert.Empty(Directory.GetFileSystemEntries(files.Root));
    }

    [Fact]
    public async Task Lease_blocks_other_instances_same_owner_and_abandoned_markers_without_theft()
    {
        using var files = new StorageFiles();
        var first = files.CreateStorage();
        var second = files.CreateStorage();
        var owner = Guid.NewGuid();
        await using (await first.AcquireLeaseAsync(files.Paths, owner, Ct))
        {
            await Failure(ArtifactAcquisitionFailure.JournalChanged,
                async () => await second.AcquireLeaseAsync(files.Paths, owner, Ct));
            await Failure(ArtifactAcquisitionFailure.JournalChanged,
                async () => await first.AcquireLeaseAsync(files.Paths, Guid.NewGuid(), Ct));
            await Failure(ArtifactAcquisitionFailure.JournalChanged,
                async () => await second.OpenPartialAsync(files.Paths, 0, null, Ct));
        }
        File.WriteAllText(files.Paths.LeasePath, "stale-foreign-owner");
        File.SetLastWriteTimeUtc(files.Paths.LeasePath, DateTime.UtcNow.AddYears(-1));
        await Failure(ArtifactAcquisitionFailure.JournalChanged,
            async () => await first.AcquireLeaseAsync(files.Paths, owner, Ct));
        Assert.Equal("stale-foreign-owner", File.ReadAllText(files.Paths.LeasePath));
    }

    [Fact]
    public async Task Replaced_or_modified_lease_is_preserved_and_stops_writes()
    {
        foreach (var replace in new[] { false, true })
        {
            using var files = new StorageFiles();
            var storage = files.CreateStorage();
            var lease = await storage.AcquireLeaseAsync(files.Paths, Guid.NewGuid(), Ct);
            if (replace)
                File.Move(files.Paths.LeasePath, files.Paths.LeasePath + ".saved");
            var foreign = Guid.NewGuid().ToString("N");
            File.WriteAllText(files.Paths.LeasePath, foreign);
            await Failure(ArtifactAcquisitionFailure.JournalChanged,
                async () => await storage.OpenPartialAsync(files.Paths, 0, null, Ct));
            await Failure(ArtifactAcquisitionFailure.JournalChanged,
                async () => await lease.DisposeAsync());
            Assert.Equal(foreign, File.ReadAllText(files.Paths.LeasePath));
            Assert.False(File.Exists(files.Paths.PartialPath));
        }
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("replaced")]
    [InlineData("modified")]
    public async Task Read_only_lease_validation_refuses_missing_replaced_or_modified_marker(string change)
    {
        using var files = new StorageFiles();
        var storage = files.CreateStorage();
        var owner = Guid.NewGuid();
        var lease = await storage.AcquireLeaseAsync(files.Paths, owner, Ct);
        await storage.ValidateLeaseAsync(files.Paths, Ct);
        var saved = files.Paths.LeasePath + ".saved";
        if (change is "missing" or "replaced")
            File.Move(files.Paths.LeasePath, saved);
        if (change == "replaced")
            File.WriteAllText(files.Paths.LeasePath, owner.ToString("N"));
        if (change == "modified")
            File.WriteAllText(files.Paths.LeasePath, Guid.NewGuid().ToString("N"));
        var before = Directory.GetFiles(files.Root).ToDictionary(
            path => path, File.ReadAllBytes);
        await Failure(ArtifactAcquisitionFailure.JournalChanged,
            async () => await storage.ValidateLeaseAsync(files.Paths, Ct));
        Assert.Equal(before.Keys.Order(), Directory.GetFiles(files.Root).Order());
        foreach (var entry in before)
            Assert.Equal(entry.Value, File.ReadAllBytes(entry.Key));
        if (change is "missing" or "replaced")
        {
            if (File.Exists(files.Paths.LeasePath))
                File.Delete(files.Paths.LeasePath);
            File.Move(saved, files.Paths.LeasePath);
        }
        else
            File.WriteAllText(files.Paths.LeasePath, owner.ToString("N"));
        await lease.DisposeAsync();
    }

    [Fact]
    public async Task Read_only_lease_validation_requires_active_same_instance_lease_and_honors_cancellation()
    {
        using var files = new StorageFiles();
        var storage = files.CreateStorage();
        await Failure(ArtifactAcquisitionFailure.JournalChanged,
            async () => await storage.ValidateLeaseAsync(files.Paths, Ct));
        await using var lease = await storage.AcquireLeaseAsync(files.Paths, Guid.NewGuid(), Ct);
        await Failure(ArtifactAcquisitionFailure.JournalChanged,
            async () => await files.CreateStorage().ValidateLeaseAsync(files.Paths, Ct));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await storage.ValidateLeaseAsync(files.Paths, new CancellationToken(true)));
        await storage.ValidateLeaseAsync(files.Paths, Ct);
        Assert.Equal([files.Paths.LeasePath], Directory.GetFiles(files.Root));
    }

    [Fact]
    public async Task Cleanup_requires_identity_and_length_even_for_empty_files()
    {
        using var files = new StorageFiles();
        var storage = files.CreateStorage();
        await using var lease = await storage.AcquireLeaseAsync(files.Paths, Guid.NewGuid(), Ct);
        var identity = await WritePartial(storage, files.Paths, []);
        await Failure(ArtifactAcquisitionFailure.PartialConflict,
            async () => await storage.DeleteOwnedPartialAsync(files.Paths, identity, 1, Ct));
        File.Move(files.Paths.PartialPath, files.Paths.PartialPath + ".saved");
        File.WriteAllBytes(files.Paths.PartialPath, []);
        var replacement = await storage.InspectAsync(files.Paths, Ct);
        Assert.NotEqual(identity, replacement.PartialIdentity);
        await Failure(ArtifactAcquisitionFailure.PartialConflict,
            async () => await storage.DeleteOwnedPartialAsync(files.Paths, identity, 0, Ct));
        await Failure(ArtifactAcquisitionFailure.ResumeStateMismatch,
            async () => await storage.OpenPartialAsync(files.Paths, 0, identity, Ct));
        Assert.True(File.Exists(files.Paths.PartialPath));
        await storage.DeleteOwnedPartialAsync(files.Paths, replacement.PartialIdentity!, 0, Ct);
        Assert.False(File.Exists(files.Paths.PartialPath));
    }

    [Fact]
    public async Task Finalize_never_overwrites_and_refuses_identity_replacement()
    {
        using var files = new StorageFiles();
        var storage = files.CreateStorage();
        await using var lease = await storage.AcquireLeaseAsync(files.Paths, Guid.NewGuid(), Ct);
        var bytes = "owned-partial"u8.ToArray();
        var identity = await WritePartial(storage, files.Paths, bytes);
        File.WriteAllText(files.Paths.DestinationPath, "foreign-final");
        await Failure(ArtifactAcquisitionFailure.FinalConflict,
            async () => await storage.FinalizeAsync(files.Paths, identity, bytes.Length, Ct));
        Assert.Equal("foreign-final", File.ReadAllText(files.Paths.DestinationPath));
        File.Delete(files.Paths.DestinationPath);
        File.Move(files.Paths.PartialPath, files.Paths.PartialPath + ".saved");
        File.WriteAllBytes(files.Paths.PartialPath, bytes);
        await Failure(ArtifactAcquisitionFailure.ResumeStateMismatch,
            async () => await storage.FinalizeAsync(files.Paths, identity, bytes.Length, Ct));
        Assert.False(File.Exists(files.Paths.DestinationPath));
    }

    [Fact]
    public async Task Quarantine_binds_corrupt_bytes_hash_identity_and_one_exact_sibling()
    {
        using var files = new StorageFiles();
        var storage = files.CreateStorage();
        await using var lease = await storage.AcquireLeaseAsync(files.Paths, Guid.NewGuid(), Ct);
        var bytes = "owned-corrupt-content"u8.ToArray();
        var identity = await WritePartial(storage, files.Paths, bytes);
        await storage.FinalizeAsync(files.Paths, identity, bytes.Length, Ct);
        await Failure(ArtifactAcquisitionFailure.FinalConflict, async () =>
            await storage.QuarantineOwnedFinalAsync(files.Paths, identity, bytes.Length, Hash([]), Ct));
        await Failure(ArtifactAcquisitionFailure.FinalConflict, async () =>
            await storage.QuarantineOwnedFinalAsync(files.Paths, "foreign", bytes.Length, Hash(bytes), Ct));
        await Failure(ArtifactAcquisitionFailure.FinalConflict, async () =>
            await storage.QuarantineOwnedFinalAsync(files.Paths, identity, bytes.Length - 1, Hash(bytes), Ct));
        File.WriteAllText(files.Paths.QuarantinePath, "foreign-quarantine");
        await Failure(ArtifactAcquisitionFailure.FinalConflict, async () =>
            await storage.QuarantineOwnedFinalAsync(files.Paths, identity, bytes.Length, Hash(bytes), Ct));
        Assert.Equal("foreign-quarantine", File.ReadAllText(files.Paths.QuarantinePath));
        File.Delete(files.Paths.QuarantinePath);
        var moved = await storage.QuarantineOwnedFinalAsync(files.Paths, identity, bytes.Length, Hash(bytes), Ct);
        Assert.Equal(identity, moved.Identity);
        Assert.Equal(bytes.Length, moved.Bytes);
        Assert.False(File.Exists(files.Paths.DestinationPath));
        Assert.Equal(bytes, File.ReadAllBytes(files.Paths.QuarantinePath));
        var snapshot = await storage.InspectAsync(files.Paths, Ct);
        Assert.True(snapshot.QuarantineExists);
        Assert.Equal(identity, snapshot.QuarantineIdentity);
        Assert.Equal(bytes.Length, snapshot.QuarantineBytes);
        var newIdentity = await WritePartial(storage, files.Paths, "replacement"u8.ToArray());
        Assert.NotEqual(identity, newIdentity);
    }

    [Fact]
    public async Task Zero_byte_owned_corrupt_final_can_be_quarantined()
    {
        using var files = new StorageFiles();
        var storage = files.CreateStorage();
        await using var lease = await storage.AcquireLeaseAsync(files.Paths, Guid.NewGuid(), Ct);
        var identity = await WritePartial(storage, files.Paths, []);
        await storage.FinalizeAsync(files.Paths, identity, 0, Ct);
        var result = await storage.QuarantineOwnedFinalAsync(files.Paths, identity, 0, Hash([]), Ct);
        Assert.Equal(0, result.Bytes);
        Assert.Equal(identity, result.Identity);
    }

    [Fact]
    public async Task Commit_failure_after_quarantine_preserves_recoverable_identity_and_bytes()
    {
        using var files = new StorageFiles();
        var storage = files.CreateStorage();
        await using var lease = await storage.AcquireLeaseAsync(files.Paths, Guid.NewGuid(), Ct);
        var bytes = "corrupt-retained-bytes"u8.ToArray();
        var identity = await WritePartial(storage, files.Paths, bytes);
        await storage.FinalizeAsync(files.Paths, identity, bytes.Length, Ct);
        files.Committer.FailOnCall = files.Committer.Calls.Count + 1;
        await Failure(ArtifactAcquisitionFailure.StorageFailed, async () =>
            await storage.QuarantineOwnedFinalAsync(files.Paths, identity, bytes.Length, Hash(bytes), Ct));
        var snapshot = await storage.InspectAsync(files.Paths, Ct);
        Assert.False(snapshot.FinalExists);
        Assert.Equal(identity, snapshot.QuarantineIdentity);
        Assert.Equal(bytes.Length, snapshot.QuarantineBytes);
        Assert.Equal(bytes, File.ReadAllBytes(files.Paths.QuarantinePath));
        var recovered = await storage.QuarantineOwnedFinalAsync(
            files.Paths, identity, bytes.Length, Hash(bytes), Ct);
        Assert.Equal(identity, recovered.Identity);
        Assert.Equal(bytes.Length, recovered.Bytes);
        Assert.False(File.Exists(files.Paths.DestinationPath));
        Assert.Equal(bytes, File.ReadAllBytes(files.Paths.QuarantinePath));
    }

    [Fact]
    public async Task Same_size_in_place_corruption_preserves_identity_but_replaced_file_does_not()
    {
        using var files = new StorageFiles();
        var storage = files.CreateStorage();
        await using var lease = await storage.AcquireLeaseAsync(files.Paths, Guid.NewGuid(), Ct);
        var original = "original"u8.ToArray();
        var corrupt = "corrupt!"u8.ToArray();
        var identity = await WritePartial(storage, files.Paths, original);
        await storage.FinalizeAsync(files.Paths, identity, original.Length, Ct);
        using (var stream = new FileStream(files.Paths.DestinationPath, FileMode.Open, FileAccess.Write))
            stream.Write(corrupt);
        Assert.Equal(identity, (await storage.InspectAsync(files.Paths, Ct)).FinalIdentity);
        Assert.Equal(Hash(corrupt), await storage.ComputeFinalSha256Async(files.Paths, corrupt.Length, Ct));
        await Failure(ArtifactAcquisitionFailure.FinalConflict, async () =>
            await storage.QuarantineOwnedFinalAsync(files.Paths, identity, original.Length, Hash(original), Ct));
        File.Move(files.Paths.DestinationPath, files.Paths.DestinationPath + ".saved");
        File.WriteAllBytes(files.Paths.DestinationPath, corrupt);
        Assert.NotEqual(identity, (await storage.InspectAsync(files.Paths, Ct)).FinalIdentity);
        await Failure(ArtifactAcquisitionFailure.FinalConflict, async () =>
            await storage.QuarantineOwnedFinalAsync(files.Paths, identity, corrupt.Length, Hash(corrupt), Ct));
        Assert.False(File.Exists(files.Paths.QuarantinePath));
        Assert.Equal(corrupt, File.ReadAllBytes(files.Paths.DestinationPath));
    }

    [Fact]
    public async Task Retained_quarantine_recovery_refuses_replacement_changed_bytes_and_new_final()
    {
        using var files = new StorageFiles();
        var storage = files.CreateStorage();
        await using var lease = await storage.AcquireLeaseAsync(files.Paths, Guid.NewGuid(), Ct);
        var original = "original"u8.ToArray();
        var corrupt = "corrupt!"u8.ToArray();
        var identity = await WritePartial(storage, files.Paths, original);
        await storage.FinalizeAsync(files.Paths, identity, original.Length, Ct);
        await storage.QuarantineOwnedFinalAsync(files.Paths, identity, original.Length, Hash(original), Ct);
        using (var stream = new FileStream(files.Paths.QuarantinePath, FileMode.Open, FileAccess.Write))
            stream.Write(corrupt);
        await Failure(ArtifactAcquisitionFailure.FinalConflict, async () =>
            await storage.QuarantineOwnedFinalAsync(files.Paths, identity, original.Length, Hash(original), Ct));
        File.Move(files.Paths.QuarantinePath, files.Paths.QuarantinePath + ".saved");
        File.WriteAllBytes(files.Paths.QuarantinePath, original);
        await Failure(ArtifactAcquisitionFailure.FinalConflict, async () =>
            await storage.QuarantineOwnedFinalAsync(files.Paths, identity, original.Length, Hash(original), Ct));
        File.Delete(files.Paths.QuarantinePath);
        File.Move(files.Paths.QuarantinePath + ".saved", files.Paths.QuarantinePath);
        using (var stream = new FileStream(files.Paths.QuarantinePath, FileMode.Open, FileAccess.Write))
            stream.Write(original);
        File.WriteAllBytes(files.Paths.DestinationPath, original);
        await Failure(ArtifactAcquisitionFailure.FinalConflict, async () =>
            await storage.QuarantineOwnedFinalAsync(files.Paths, identity, original.Length, Hash(original), Ct));
        Assert.Equal(original, File.ReadAllBytes(files.Paths.QuarantinePath));
        Assert.Equal(original, File.ReadAllBytes(files.Paths.DestinationPath));
    }

    [Fact]
    public async Task Lease_commit_failure_preserves_marker_and_never_adopts_it()
    {
        using var files = new StorageFiles();
        var storage = files.CreateStorage();
        var owner = Guid.NewGuid();
        files.Committer.FailOnCall = 1;
        await Failure(ArtifactAcquisitionFailure.StorageFailed,
            async () => await storage.AcquireLeaseAsync(files.Paths, owner, Ct));
        Assert.Equal(owner.ToString("N"), File.ReadAllText(files.Paths.LeasePath));
        await Failure(ArtifactAcquisitionFailure.JournalChanged,
            async () => await storage.AcquireLeaseAsync(files.Paths, owner, Ct));
        Assert.False(File.Exists(files.Paths.PartialPath));
    }

    [Fact]
    public async Task Journal_is_bounded_exact_CAS_and_foreign_pending_is_preserved()
    {
        using var files = new StorageFiles();
        var storage = files.CreateStorage();
        await using var lease = await storage.AcquireLeaseAsync(files.Paths, Guid.NewGuid(), Ct);
        var first = await storage.WriteJournalAsync(files.Paths, null, "{\"revision\":1}"u8.ToArray(), Ct);
        var second = await storage.WriteJournalAsync(files.Paths, first.Version, "{\"revision\":2}"u8.ToArray(), Ct);
        await Failure(ArtifactAcquisitionFailure.JournalChanged,
            async () => await storage.WriteJournalAsync(files.Paths, first.Version, "{}"u8.ToArray(), Ct));
        Assert.Equal(second.Version, (await storage.InspectAsync(files.Paths, Ct)).JournalVersion);
        Assert.False(File.Exists(files.Paths.JournalPath + ".pending"));
        File.WriteAllText(files.Paths.JournalPath + ".pending", "foreign-pending");
        Assert.True((await storage.InspectAsync(files.Paths, Ct)).JournalPendingExists);
        await Failure(ArtifactAcquisitionFailure.JournalChanged,
            async () => await storage.WriteJournalAsync(files.Paths, second.Version, "{}"u8.ToArray(), Ct));
        Assert.Equal("foreign-pending", File.ReadAllText(files.Paths.JournalPath + ".pending"));
        await Failure(ArtifactAcquisitionFailure.JournalTooLarge, async () =>
            await storage.WriteJournalAsync(files.Paths, second.Version,
                new byte[ArtifactAcquisitionJournalCodec.MaximumBytes + 1], Ct));
    }

    [Fact]
    public async Task Oversized_journal_is_preserved_and_refused_before_allocating_content()
    {
        using var files = new StorageFiles();
        var bytes = new byte[ArtifactAcquisitionJournalCodec.MaximumBytes + 1];
        File.WriteAllBytes(files.Paths.JournalPath, bytes);
        await Failure(ArtifactAcquisitionFailure.JournalTooLarge,
            async () => await files.CreateStorage().InspectAsync(files.Paths, Ct));
        Assert.Equal(bytes.Length, new FileInfo(files.Paths.JournalPath).Length);
    }

    [Fact]
    public async Task Cancellation_before_lease_does_not_create_any_file()
    {
        using var files = new StorageFiles();
        var storage = files.CreateStorage();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await storage.AcquireLeaseAsync(files.Paths, Guid.NewGuid(), new CancellationToken(true)));
        Assert.Empty(Directory.GetFileSystemEntries(files.Root));
    }

    [Fact]
    public async Task Hard_linked_payload_journal_quarantine_and_lease_are_refused()
    {
        foreach (var name in new[] { "partial", "final", "journal", "quarantine", "lease", "pending" })
        {
            using var files = new StorageFiles();
            var path = name switch
            {
                "partial" => files.Paths.PartialPath,
                "final" => files.Paths.DestinationPath,
                "journal" => files.Paths.JournalPath,
                "quarantine" => files.Paths.QuarantinePath,
                "lease" => files.Paths.LeasePath,
                _ => files.Paths.JournalPath + ".pending"
            };
            var source = Path.Combine(files.Root, "foreign-source");
            File.WriteAllText(source, "foreign-canary");
            CreateHardLink(path, source);
            await Failure(ArtifactAcquisitionFailure.DestinationInvalid,
                async () => await files.CreateStorage().InspectAsync(files.Paths, Ct));
            Assert.Equal("foreign-canary", File.ReadAllText(source));
            Assert.Equal("foreign-canary", File.ReadAllText(path));
        }
    }

    [Fact]
    public async Task Hard_link_added_after_open_stops_writer_and_cleanup()
    {
        using var files = new StorageFiles();
        var storage = files.CreateStorage();
        await using var lease = await storage.AcquireLeaseAsync(files.Paths, Guid.NewGuid(), Ct);
        var identity = await WritePartial(storage, files.Paths, "original"u8.ToArray());
        var alias = Path.Combine(files.Root, "foreign-alias");
        CreateHardLink(alias, files.Paths.PartialPath);
        await Failure(ArtifactAcquisitionFailure.DestinationInvalid,
            async () => await storage.OpenPartialAsync(files.Paths, 8, identity, Ct));
        await Failure(ArtifactAcquisitionFailure.DestinationInvalid,
            async () => await storage.DeleteOwnedPartialAsync(files.Paths, identity, 8, Ct));
        Assert.Equal("original", File.ReadAllText(alias));
        File.Delete(alias);
    }

    [Fact]
    public async Task Directory_at_any_payload_boundary_is_rejected()
    {
        using var files = new StorageFiles();
        var storage = files.CreateStorage();
        Directory.CreateDirectory(files.Paths.PartialPath);
        await Failure(ArtifactAcquisitionFailure.DestinationInvalid,
            async () => await storage.InspectAsync(files.Paths, Ct));
        Assert.True(Directory.Exists(files.Paths.PartialPath));
    }

    [Fact]
    public async Task Reparse_ancestor_after_construction_is_refused()
    {
        using var files = new StorageFiles();
        var storage = files.CreateStorage();
        var original = files.Root + ".original";
        Directory.Move(files.Root, original);
        try
        {
            if (OperatingSystem.IsWindows())
            {
                // Junction creation is local and requires no symlink privilege.
                var info = new System.Diagnostics.ProcessStartInfo("cmd.exe")
                {
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                info.ArgumentList.Add("/c");
                info.ArgumentList.Add("mklink");
                info.ArgumentList.Add("/J");
                info.ArgumentList.Add(files.Root);
                info.ArgumentList.Add(original);
                using var process = System.Diagnostics.Process.Start(info)!;
                await process.WaitForExitAsync();
                Assert.Equal(0, process.ExitCode);
            }
            else
                Directory.CreateSymbolicLink(files.Root, original);
            await Failure(ArtifactAcquisitionFailure.DestinationInvalid,
                async () => await storage.InspectAsync(files.Paths, Ct));
            await Failure(ArtifactAcquisitionFailure.DestinationInvalid,
                async () => await storage.AcquireLeaseAsync(files.Paths, Guid.NewGuid(), Ct));
            Assert.Empty(Directory.GetFileSystemEntries(original));
        }
        finally
        {
            if (Directory.Exists(files.Root))
                Directory.Delete(files.Root);
            Directory.Move(original, files.Root);
        }
    }

    [Theory]
    [InlineData(@"\\server\share\root")]
    [InlineData(@"\\?\C:\root")]
    [InlineData(@"\\.\C:\root")]
    [InlineData(@"C:\root:ads")]
    [InlineData(@"C:\NUL")]
    [InlineData(@"C:\COM1.txt")]
    [InlineData(@"C:\COM¹")]
    [InlineData(@"C:\root.")]
    [InlineData(@"C:\root ")]
    public void Windows_DOS_ADS_UNC_and_device_paths_are_rejected(string path)
    {
        if (!OperatingSystem.IsWindows()) return;
        var error = Assert.Throws<ArtifactAcquisitionException>(() =>
            new LocalArtifactAcquisitionStorage(path, new RecordingDirectoryCommitter()));
        Assert.Equal(ArtifactAcquisitionFailure.DestinationInvalid, error.Failure);
    }

    [Fact]
    public void Missing_root_is_never_created()
    {
        using var files = new StorageFiles();
        var missing = Path.Combine(files.Root, "missing");
        Assert.Throws<ArtifactAcquisitionException>(() =>
            new LocalArtifactAcquisitionStorage(missing, files.Committer));
        Assert.False(Directory.Exists(missing));
    }

    [Fact]
    public async Task Forged_nonchild_paths_are_refused_without_touching_files()
    {
        using var files = new StorageFiles();
        var destination = Path.Combine(files.Root, "..", "outside.bin");
        var paths = new ArtifactAcquisitionPaths(files.Root, destination,
            destination + ".partial", destination + ".acquisition.json");
        await Failure(ArtifactAcquisitionFailure.DestinationInvalid,
            async () => await files.CreateStorage().InspectAsync(paths, Ct));
        Assert.Empty(Directory.GetFileSystemEntries(files.Root));
    }

    private static async Task<string> WritePartial(LocalArtifactAcquisitionStorage storage,
        ArtifactAcquisitionPaths paths, byte[] bytes)
    {
        await using var writer = await storage.OpenPartialAsync(paths, 0, null, Ct);
        await writer.WriteAsync(bytes, Ct);
        await writer.FlushToDiskAsync(Ct);
        return writer.Identity;
    }

    private static async Task Failure(ArtifactAcquisitionFailure failure, Func<Task> action)
    {
        var error = await Assert.ThrowsAsync<ArtifactAcquisitionException>(action);
        Assert.Equal(failure, error.Failure);
    }

    private static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    private static void CreateHardLink(string path, string existing)
    {
        if (OperatingSystem.IsWindows())
            Assert.True(CreateHardLinkWindows(path, existing, IntPtr.Zero),
                $"CreateHardLink failed: {Marshal.GetLastPInvokeError()}");
        else
            Assert.Equal(0, LinkLinux(existing, path));
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateHardLinkW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLinkWindows(string path, string existing, IntPtr security);

    [DllImport("libc", EntryPoint = "link", SetLastError = true)]
    private static extern int LinkLinux(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string existing,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string path);

    private sealed class StorageProbe(long available) : IArtifactFreeSpaceProbe
    {
        internal List<string> Paths { get; } = [];
        public long GetAvailableBytes(string absoluteDirectory)
        {
            Paths.Add(absoluteDirectory);
            return available;
        }
    }

    private sealed class StorageFiles : IDisposable
    {
        internal string Root { get; } = Path.Combine(Directory.GetCurrentDirectory(),
            ".acquisition-storage-" + Guid.NewGuid().ToString("N"));
        internal RecordingDirectoryCommitter Committer { get; } = new();
        internal ArtifactAcquisitionPaths Paths { get; }

        internal StorageFiles()
        {
            Directory.CreateDirectory(Root);
            var destination = Path.Combine(Root, "artifact-test.bin");
            Paths = new(Root, destination, destination + ".partial", destination + ".acquisition.json");
        }

        internal LocalArtifactAcquisitionStorage CreateStorage(IArtifactFreeSpaceProbe? probe = null) =>
            new(Root, Committer, probe ?? new StorageProbe(long.MaxValue));

        public void Dispose() => Directory.Delete(Root, recursive: true);
    }
}
