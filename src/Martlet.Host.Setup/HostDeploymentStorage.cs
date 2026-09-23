using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Martlet.Host.Setup;

public sealed class LocalHostDeploymentStore
{
    private readonly ISetupDirectoryCommitter committer;
    public string RootPath { get; }
    public LocalHostDeploymentStore(string existingPrivateRoot)
        : this(existingPrivateRoot, new PlatformSetupDirectoryCommitter())
    {
        DeploymentRules.Require(OperatingSystem.IsLinux(), HostDeploymentFailure.StorageFailure);
    }
    internal LocalHostDeploymentStore(string existingPrivateRoot, ISetupDirectoryCommitter committer)
    {
        LocalArtifactAcquisitionStorage.ValidateDirectory(existingPrivateRoot);
        RootPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(existingPrivateRoot));
        this.committer = committer;
    }

    private string JournalPath(HostDeploymentBundle bundle) =>
        Path.Combine(RootPath, bundle.ProjectName + ".configuration.json");
    private string LeasePath(HostDeploymentBundle bundle) => JournalPath(bundle) + ".lease";
    private (string Stage, string Final) Paths(HostDeploymentBundle bundle)
    {
        var name = bundle.ProjectName + "-" + bundle.Fingerprint;
        return (Path.Combine(RootPath, "." + name + ".staging"), Path.Combine(RootPath, name));
    }
    private void ValidateRoot(string? identity = null)
    {
        LocalArtifactAcquisitionStorage.ValidateDirectory(RootPath);
        if (identity is not null)
            DeploymentRules.Require(ArtifactAcquisitionFileIdentity.DirectoryIdentity(RootPath) == identity,
                HostDeploymentFailure.StorageConflict);
    }

    internal async ValueTask<DeploymentStorageSnapshot> InspectAsync(HostDeploymentBundle bundle,
        CancellationToken cancellationToken, ConfigurationLease? held = null)
    {
        ValidateRoot();
        var parent = ArtifactAcquisitionFileIdentity.DirectoryIdentity(RootPath);
        var journalPath = JournalPath(bundle);
        var journalIdentity = ArtifactOwnedJournalIO.InspectFile(journalPath);
        DeploymentRules.Require(ArtifactOwnedJournalIO.InspectFile(journalPath + ".pending") is null,
            HostDeploymentFailure.StorageConflict);
        var bytes = await ArtifactOwnedJournalIO.ReadAsync(journalPath, DeploymentJournalCodec.MaximumBytes,
            () => ValidateRoot(parent), cancellationToken).ConfigureAwait(false);
        var journal = bytes is null ? null : DeploymentJournalCodec.Read(bytes.Content);
        var marker = ArtifactOwnedJournalIO.InspectFile(LeasePath(bundle));
        if (journal is not null)
        {
            DeploymentRules.Require(journal.Identity == bundle.Definition.Identity && journal.ParentIdentity == parent &&
                marker?.Identity == journal.LeaseIdentity && marker?.Bytes == 64, HostDeploymentFailure.StorageConflict);
            if (held is null)
            {
                using var probe = OpenLease(bundle, parent, journal);
                probe.Validate();
            }
            else held.Validate();
        }
        else if (held is null)
            DeploymentRules.Require(marker is null, HostDeploymentFailure.StorageConflict);
        var paths = Paths(bundle);
        var revision = journal?.Revisions.SingleOrDefault(row => row.BundleFingerprint == bundle.Fingerprint);
        DeploymentRules.Require(journal is null || revision is not null ||
            journal.Revisions.Length < 16 && journal.Revisions.All(row => row.State == DeploymentJournalState.Published),
            HostDeploymentFailure.StorageConflict);
        var stage = DirectoryIdentity(paths.Stage);
        var final = DirectoryIdentity(paths.Final);
        var state = new List<string>();
        if (revision is null || revision.State == DeploymentJournalState.Prepared)
            DeploymentRules.Require(stage is null && final is null, HostDeploymentFailure.StorageConflict);
        else
        {
            DeploymentRules.Require(stage is null != (final is null) &&
                (stage ?? final) == revision.DirectoryIdentity &&
                (final is null || revision.State is DeploymentJournalState.Publishing or DeploymentJournalState.Published) &&
                (revision.State != DeploymentJournalState.Published || final is not null), HostDeploymentFailure.StorageConflict);
            var root = final is not null ? paths.Final : paths.Stage;
            var expected = bundle.Files.ToDictionary(file => file.Name, StringComparer.Ordinal);
            DeploymentRules.Require(revision.Files.Keys.All(expected.ContainsKey), HostDeploymentFailure.StorageConflict);
            foreach (var entry in Directory.EnumerateFileSystemEntries(root).Take(33))
                DeploymentRules.Require(revision.Files.ContainsKey(Path.GetFileName(entry)), HostDeploymentFailure.StorageConflict);
            foreach (var file in bundle.Files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!revision.Files.TryGetValue(file.Name, out var identity))
                {
                    DeploymentRules.Require(final is null && revision.State == DeploymentJournalState.Staged,
                        HostDeploymentFailure.StorageConflict);
                    continue;
                }
                var path = Path.Combine(root, file.Name);
                var metadata = ArtifactOwnedJournalIO.InspectFile(path);
                DeploymentRules.Require(metadata?.Identity == identity && metadata?.Bytes <= file.Length &&
                    (revision.State == DeploymentJournalState.Staged || metadata?.Bytes == file.Length),
                    HostDeploymentFailure.StorageConflict);
                var content = await ArtifactOwnedJournalIO.ReadAsync(path, file.Length,
                    () => { ValidateRoot(parent); held?.Validate(); }, cancellationToken).ConfigureAwait(false);
                DeploymentRules.Require(content is not null &&
                    content.Content.Span.SequenceEqual(file.Content.Span[..content.Content.Length]) &&
                    ArtifactOwnedJournalIO.InspectFile(path) == metadata, HostDeploymentFailure.StorageConflict);
                state.Add(file.Name + ":" + identity + ":" + content!.Version);
            }
            DeploymentRules.Require(DirectoryIdentity(root) == revision.DirectoryIdentity, HostDeploymentFailure.StorageConflict);
        }
        ValidateRoot(parent);
        DeploymentRules.Require(ArtifactOwnedJournalIO.InspectFile(journalPath + ".pending") is null,
            HostDeploymentFailure.StorageConflict);
        var latest = await ArtifactOwnedJournalIO.ReadAsync(journalPath, DeploymentJournalCodec.MaximumBytes,
            () => ValidateRoot(parent), cancellationToken).ConfigureAwait(false);
        DeploymentRules.Require(latest?.Version == bytes?.Version &&
            ArtifactOwnedJournalIO.InspectFile(journalPath) == journalIdentity, HostDeploymentFailure.StorageConflict);
        return new(parent, bytes?.Version, journal, revision, marker?.Identity, paths.Stage, paths.Final,
            FingerprintBuilder.Create(["host-config-storage-v2", parent, bytes?.Version ?? "",
                journalIdentity?.Identity ?? "", marker?.Identity ?? "",
                stage ?? "", final ?? "", .. state]));
    }

    internal async ValueTask PublishAsync(HostDeploymentBundle bundle, DeploymentStorageSnapshot preview,
        Func<CancellationToken, ValueTask> checkCurrent, CancellationToken cancellationToken)
    {
        ValidateRoot(preview.ParentIdentity);
        using var lease = OpenLease(bundle, preview.ParentIdentity, preview.Journal);
        var current = await InspectAsync(bundle, cancellationToken, lease).ConfigureAwait(false);
        // Initial marker creation is this operation's only difference from the preview.
        DeploymentRules.Require(current.JournalVersion == preview.JournalVersion &&
            current.ParentIdentity == preview.ParentIdentity &&
            (preview.Journal is null || current.Fingerprint == preview.Fingerprint), HostDeploymentFailure.PreviewChanged);
        var journal = current.Journal ?? new DeploymentJournal
        {
            FormatVersion = 2, Purpose = "HostDeploymentConfiguration", Identity = bundle.Definition.Identity,
            ParentIdentity = current.ParentIdentity, LeaseIdentity = lease.Identity, LeaseNonce = lease.Nonce,
            Revisions = [], IntegritySha256 = ""
        };
        var revision = current.Document ?? new DeploymentRevision
        {
            BundleFingerprint = bundle.Fingerprint, State = DeploymentJournalState.Prepared,
            DirectoryIdentity = null, Files = []
        };
        var version = current.JournalVersion;
        async ValueTask Persist(CancellationToken token)
        {
            await checkCurrent(token).ConfigureAwait(false);
            journal = journal with { Revisions = journal.Revisions.Where(row => row.BundleFingerprint != bundle.Fingerprint)
                .Append(revision).OrderBy(row => row.BundleFingerprint, StringComparer.Ordinal).ToArray() };
            var written = await ArtifactOwnedJournalIO.WriteAsync(JournalPath(bundle), DeploymentJournalCodec.MaximumBytes,
                version, DeploymentJournalCodec.Write(journal), lease.Validate, committer, token).ConfigureAwait(false);
            version = written.Version;
        }
        if (current.Document is null) await Persist(cancellationToken).ConfigureAwait(false);
        if (revision.State == DeploymentJournalState.Prepared)
        {
            await checkCurrent(cancellationToken).ConfigureAwait(false);
            lease.Validate();
            ArtifactAcquisitionFileIdentity.CreateImageDirectoryExclusive(current.StagingPath);
            revision = revision with { DirectoryIdentity = DirectoryIdentity(current.StagingPath), State = DeploymentJournalState.Staged };
            committer.Commit(RootPath);
            await Persist(cancellationToken).ConfigureAwait(false);
        }
        if (revision.State == DeploymentJournalState.Staged)
        {
            foreach (var file in bundle.Files)
            {
                await checkCurrent(cancellationToken).ConfigureAwait(false);
                lease.Validate();
                DeploymentRules.Require(DirectoryIdentity(current.StagingPath) == revision.DirectoryIdentity,
                    HostDeploymentFailure.StorageConflict);
                var path = Path.Combine(current.StagingPath, file.Name);
                if (!revision.Files.ContainsKey(file.Name))
                {
                    using var empty = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
                    var identity = ArtifactAcquisitionFileIdentity.Read(empty.SafeFileHandle).Identity;
                    empty.Flush(true);
                    revision = revision with { Files = new(revision.Files, StringComparer.Ordinal) { [file.Name] = identity } };
                    await Persist(cancellationToken).ConfigureAwait(false);
                }
                await using var stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.Read,
                    0, FileOptions.Asynchronous | FileOptions.WriteThrough);
                var metadata = ArtifactAcquisitionFileIdentity.Read(stream.SafeFileHandle);
                DeploymentRules.Require(metadata.Identity == revision.Files[file.Name] && metadata.Bytes <= file.Length,
                    HostDeploymentFailure.StorageConflict);
                var prefix = new byte[(int)metadata.Bytes];
                await stream.ReadExactlyAsync(prefix, cancellationToken).ConfigureAwait(false);
                DeploymentRules.Require(prefix.AsSpan().SequenceEqual(file.Content.Span[..prefix.Length]),
                    HostDeploymentFailure.StorageConflict);
                await stream.WriteAsync(file.Content[prefix.Length..], cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(true);
            }
            committer.Commit(current.StagingPath);
            _ = await InspectAsync(bundle, cancellationToken, lease).ConfigureAwait(false);
            revision = revision with { State = DeploymentJournalState.Publishing };
            await Persist(cancellationToken).ConfigureAwait(false);
        }
        if (revision.State == DeploymentJournalState.Publishing)
        {
            await checkCurrent(cancellationToken).ConfigureAwait(false);
            _ = await InspectAsync(bundle, cancellationToken, lease).ConfigureAwait(false);
            if (DirectoryIdentity(current.FinalPath) is null)
            {
                lease.Validate();
                Directory.Move(current.StagingPath, current.FinalPath);
            }
            committer.Commit(RootPath);
            revision = revision with { State = DeploymentJournalState.Published };
            await Persist(cancellationToken).ConfigureAwait(false);
        }
        await checkCurrent(cancellationToken).ConfigureAwait(false);
        committer.Commit(current.FinalPath);
        committer.Commit(RootPath);
        var final = await InspectAsync(bundle, cancellationToken, lease).ConfigureAwait(false);
        DeploymentRules.Require(final.Document?.State == DeploymentJournalState.Published, HostDeploymentFailure.StorageConflict);
    }

    private ConfigurationLease OpenLease(HostDeploymentBundle bundle, string parent, DeploymentJournal? journal)
    {
        ValidateRoot(parent);
        var handle = ArtifactAcquisitionFileIdentity.OpenDirectory(RootPath);
        FileStream? stream = null;
        var locked = false;
        try
        {
            stream = ArtifactAcquisitionFileIdentity.OpenImageLease(LeasePath(bundle), create: journal is null);
            locked = ArtifactAcquisitionFileIdentity.TryLockImageLease(stream.SafeFileHandle);
            DeploymentRules.Require(locked, HostDeploymentFailure.StorageConflict);
            var identity = ArtifactAcquisitionFileIdentity.Read(stream.SafeFileHandle).Identity;
            var nonce = journal?.LeaseNonce ?? Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
            if (journal is null)
            {
                stream.Write(Encoding.ASCII.GetBytes(nonce));
                stream.Flush(true);
                committer.Commit(RootPath);
            }
            else DeploymentRules.Require(identity == journal.LeaseIdentity, HostDeploymentFailure.StorageConflict);
            var lease = new ConfigurationLease(this, LeasePath(bundle), parent, handle, stream, identity, nonce);
            lease.Validate();
            return lease;
        }
        catch
        {
            try { if (locked && stream is not null) ArtifactAcquisitionFileIdentity.UnlockImageLease(stream.SafeFileHandle); }
            finally { stream?.Dispose(); handle.Dispose(); }
            throw;
        }
    }

    private static string? DirectoryIdentity(string path)
    {
        try
        {
            var attributes = File.GetAttributes(path);
            DeploymentRules.Require((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint | FileAttributes.Device))
                == FileAttributes.Directory, HostDeploymentFailure.StorageConflict);
            return ArtifactAcquisitionFileIdentity.DirectoryIdentity(path);
        }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
    }

    internal sealed class ConfigurationLease(LocalHostDeploymentStore owner, string path, string parent,
        SafeFileHandle parentHandle, FileStream stream, string identity, string nonce) : IDisposable
    {
        internal string Identity => identity;
        internal string Nonce => nonce;
        internal void Validate()
        {
            owner.ValidateRoot(parent);
            DeploymentRules.Require(ArtifactAcquisitionFileIdentity.ReadDirectory(parentHandle) == parent &&
                ArtifactOwnedJournalIO.InspectFile(path) == new ArtifactAcquisitionFileMetadata(identity, 64),
                HostDeploymentFailure.StorageConflict);
            var bytes = new byte[64];
            stream.Position = 0;
            stream.ReadExactly(bytes);
            DeploymentRules.Require(Encoding.ASCII.GetString(bytes) == nonce, HostDeploymentFailure.StorageConflict);
        }
        public void Dispose()
        {
            try { ArtifactAcquisitionFileIdentity.UnlockImageLease(stream.SafeFileHandle); }
            finally { stream.Dispose(); parentHandle.Dispose(); }
        }
    }
}
