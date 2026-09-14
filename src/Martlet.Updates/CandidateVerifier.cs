namespace Martlet.Updates;

internal sealed class CandidateVerifier(UpdateTrustPolicy trust, StagingLimits limits)
{
    internal VerifiedCandidate ReadEnvelope(byte[] bytes, InstalledVersionFacts installed)
    {
        var candidate = ReadPackage(bytes);
        var manifest = candidate.Manifest;
        if (manifest.Rid != installed.Rid) throw new StagingException(StagingFailure.IncompatibleRid);
        if (Wire.Version(manifest.ApplicationVersion) <= Wire.Version(installed.Version))
            throw new StagingException(StagingFailure.InvalidVersion);
        if (installed.SettingsSchemaVersion < manifest.SettingsMinimumReader ||
            installed.SettingsSchemaVersion > manifest.SettingsMaximumReader)
            throw new StagingException(StagingFailure.IncompatibleSettings);
        return candidate;
    }

    // Package authenticity is independent of the initial strictly-newer installation policy.
    // Only the transaction engine may use this for an already recorded previous selection.
    internal VerifiedCandidate ReadPackage(byte[] bytes)
    {
        trust.RequireConfigured();
        var envelope = Wire.Read<CandidateEnvelope>(bytes, Wire.MaximumEnvelopeBytes, canonical: true);
        if (envelope.Manifest.Length > Wire.MaximumManifestBytes || envelope.Signature.Length > 512)
            throw new StagingException(StagingFailure.CapacityExceeded);
        var manifest = Wire.Read<CandidateManifest>(envelope.Manifest, Wire.MaximumManifestBytes, canonical: true);
        trust.Verify(manifest, envelope.Manifest, envelope.Signature);
        if (manifest.FormatVersion != 1 || manifest.MinimumReaderFormat != 1 || manifest.Application != "Martlet.Update")
            throw new StagingException(StagingFailure.IncompatibleFormat);
        if (manifest.Rid != "win-x64") throw new StagingException(StagingFailure.IncompatibleRid);
        Wire.Version(manifest.ApplicationVersion);
        if (manifest.SettingsMinimumReader < 1 || manifest.SettingsMaximumReader < manifest.SettingsMinimumReader ||
            manifest.SettingsMaximumReader > 1024)
            throw new StagingException(StagingFailure.IncompatibleSettings);
        if (!Wire.IsHash(manifest.ArchiveSha256)) throw new StagingException(StagingFailure.InvalidManifest);
        if (manifest.ArchiveBytes < 22 || manifest.ArchiveBytes > limits.MaximumArchiveBytes)
            throw new StagingException(StagingFailure.CapacityExceeded);
        var directories = LocalPaths.Inventory(manifest.Files, limits);
        var expanded = manifest.Files.Sum(f => f.Bytes);
        if (expanded > limits.MaximumExpandedBytes) throw new StagingException(StagingFailure.CapacityExceeded);
        return new(manifest, envelope.Manifest, bytes.ToArray(), expanded, directories);
    }

    internal void VerifyArchive(Stream archive, VerifiedCandidate candidate, CancellationToken token)
    {
        archive.Position = 0;
        BoundedIo.CopyAndHash(archive, null, candidate.Manifest.ArchiveBytes, candidate.Manifest.ArchiveSha256, token);
        RestrictedZip.VerifyContent(archive, candidate, limits, token);
    }
}
