using System.IO.Compression;
using System.Text;

namespace Martlet.Updates;

// Validate bounded central AND local records before ZipArchive allocates its entry table.
// A deliberately restricted classic ZIP dialect, not a general-purpose ZIP importer.
internal static class RestrictedZip
{
    private sealed record Header(string Name, ushort Flags, ushort Method, uint Crc, uint Compressed,
        uint Expanded, uint Offset);

    internal static Dictionary<string, uint> Inspect(Stream stream, CandidateManifest manifest,
        StagingLimits limits, CancellationToken token)
    {
        if (stream.Length != manifest.ArchiveBytes || stream.Length < 22)
            throw new StagingException(StagingFailure.CorruptArchive);
        using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);
        stream.Position = stream.Length - 22;
        if (reader.ReadUInt32() != 0x06054b50 || reader.ReadUInt16() != 0 || reader.ReadUInt16() != 0)
            throw new StagingException(StagingFailure.CorruptArchive);
        var count = reader.ReadUInt16();
        if (count != reader.ReadUInt16() || count != manifest.Files.Length || count > limits.MaximumEntries)
            throw new StagingException(StagingFailure.CorruptArchive);
        var centralBytes = reader.ReadUInt32();
        var centralOffset = reader.ReadUInt32();
        if (reader.ReadUInt16() != 0 || centralOffset + (long)centralBytes != stream.Length - 22)
            throw new StagingException(StagingFailure.UnsafeEntry);
        stream.Position = centralOffset;
        var headers = new List<Header>(count);
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < count; index++)
        {
            token.ThrowIfCancellationRequested();
            if (stream.Position + 46 > stream.Length - 22 || reader.ReadUInt32() != 0x02014b50)
                throw new StagingException(StagingFailure.CorruptArchive);
            var creator = reader.ReadUInt16();
            var needed = reader.ReadUInt16();
            var flags = reader.ReadUInt16();
            var method = reader.ReadUInt16();
            reader.ReadUInt32(); // DOS timestamp is inert; never restored.
            var crc = reader.ReadUInt32();
            var compressed = reader.ReadUInt32();
            var expanded = reader.ReadUInt32();
            var nameLength = reader.ReadUInt16();
            var extra = reader.ReadUInt16();
            var comment = reader.ReadUInt16();
            var disk = reader.ReadUInt16();
            var internalAttributes = reader.ReadUInt16();
            var attributes = reader.ReadUInt32();
            var offset = reader.ReadUInt32();
            var unixType = (attributes >> 16) & 0xf000;
            if (creator >> 8 is not (0 or 3) || needed is not (10 or 20) ||
                (flags & ~0x080e) != 0 || method is not (0 or 8) ||
                extra != 0 || comment != 0 ||
                disk != 0 || internalAttributes > 1 || (attributes & 0xffff & ~0x21u) != 0 ||
                unixType is not (0 or 0x8000))
                throw new StagingException(StagingFailure.UnsafeEntry);
            if (nameLength is 0 or > 240 || stream.Position + nameLength > stream.Length - 22)
                throw new StagingException(StagingFailure.CapacityExceeded);
            var nameBytes = reader.ReadBytes(nameLength);
            if (nameBytes.Length != nameLength || nameBytes.Any(b => b > 127))
                throw new StagingException(StagingFailure.UnsafeEntry);
            var name = Encoding.ASCII.GetString(nameBytes);
            LocalPaths.Entry(name);
            if (!names.Add(name)) throw new StagingException(StagingFailure.UnsafeEntry);
            if (expanded > limits.MaximumFileBytes || expanded > (long)compressed * limits.MaximumCompressionRatio)
                throw new StagingException(StagingFailure.CapacityExceeded);
            if (method == 0 && compressed != expanded)
                throw new StagingException(StagingFailure.CorruptArchive);
            headers.Add(new(name, flags, method, crc, compressed, expanded, offset));
        }
        if (stream.Position != centralOffset + (long)centralBytes)
            throw new StagingException(StagingFailure.CorruptArchive);
        long next = 0;
        foreach (var header in headers.OrderBy(h => h.Offset))
        {
            token.ThrowIfCancellationRequested();
            if (header.Offset != next || next + 30 > centralOffset)
                throw new StagingException(StagingFailure.UnsafeEntry);
            stream.Position = header.Offset;
            if (reader.ReadUInt32() != 0x04034b50 || reader.ReadUInt16() is not (10 or 20) ||
                reader.ReadUInt16() != header.Flags || reader.ReadUInt16() != header.Method)
                throw new StagingException(StagingFailure.CorruptArchive);
            reader.ReadUInt32();
            var crc = reader.ReadUInt32();
            var compressed = reader.ReadUInt32();
            var expanded = reader.ReadUInt32();
            var nameLength = reader.ReadUInt16();
            var extra = reader.ReadUInt16();
            if (nameLength != header.Name.Length || extra != 0 ||
                !reader.ReadBytes(nameLength).AsSpan().SequenceEqual(Encoding.ASCII.GetBytes(header.Name)))
                throw new StagingException(StagingFailure.UnsafeEntry);
            var descriptor = (header.Flags & 8) != 0;
            if (descriptor ? crc != 0 || compressed != 0 || expanded != 0 :
                crc != header.Crc || compressed != header.Compressed || expanded != header.Expanded)
                throw new StagingException(StagingFailure.CorruptArchive);
            next = stream.Position + header.Compressed + (descriptor ? 16 : 0);
            if (next > centralOffset) throw new StagingException(StagingFailure.CorruptArchive);
            if (descriptor)
            {
                stream.Position = next - 16;
                if (reader.ReadUInt32() != 0x08074b50 || reader.ReadUInt32() != header.Crc ||
                    reader.ReadUInt32() != header.Compressed || reader.ReadUInt32() != header.Expanded)
                    throw new StagingException(StagingFailure.CorruptArchive);
            }
        }
        if (next != centralOffset) throw new StagingException(StagingFailure.UnsafeEntry);
        var byName = headers.ToDictionary(h => h.Name, StringComparer.Ordinal);
        foreach (var file in manifest.Files)
            if (!byName.TryGetValue(file.Path, out var header) || header.Expanded != file.Bytes)
                throw new StagingException(StagingFailure.CorruptArchive);
        stream.Position = 0;
        return byName.ToDictionary(h => h.Key, h => h.Value.Crc, StringComparer.Ordinal);
    }

    internal static void VerifyContent(Stream stream, VerifiedCandidate candidate, StagingLimits limits,
        CancellationToken token, Action<string, Stream, PayloadFile, uint>? extract = null)
    {
        var crc = Inspect(stream, candidate.Manifest, limits, token);
        using var zip = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true);
        var entries = zip.Entries.ToDictionary(e => e.FullName, StringComparer.Ordinal);
        byte[]? internalBytes = null;
        byte[]? sums = null;
        foreach (var file in candidate.Manifest.Files)
        {
            token.ThrowIfCancellationRequested();
            var entry = entries[file.Path];
            using var input = entry.Open();
            if (extract is not null) extract(file.Path, input, file, crc[file.Path]);
            else BoundedIo.CopyAndHash(input, null, file.Bytes, file.Sha256, token, expectedCrc: crc[file.Path]);
            if (file.Path is "manifest.json" or "SHA256SUMS.txt")
            {
                if (file.Bytes > Wire.MaximumManifestBytes) throw new StagingException(StagingFailure.CapacityExceeded);
                using var metadata = entry.Open();
                var bytes = BoundedIo.Read(metadata, Wire.MaximumManifestBytes, token);
                if (Wire.Hash(bytes) != file.Sha256) throw new StagingException(StagingFailure.CorruptArchive);
                if (file.Path == "manifest.json") internalBytes = bytes;
                else sums = bytes;
            }
        }
        VerifyInternal(candidate.Manifest, internalBytes!, sums!);
    }

    private static void VerifyInternal(CandidateManifest candidate, byte[] bytes, byte[] sums)
    {
        var payload = Wire.Read<InternalPayloadManifest>(bytes, Wire.MaximumManifestBytes);
        if (payload.SchemaVersion != 1) throw new StagingException(StagingFailure.IncompatibleFormat);
        if (payload.Channel != "INTERNAL DEVELOPMENT ONLY - UNSIGNED" ||
            payload.ApplicationVersion != candidate.ApplicationVersion || payload.Rid != candidate.Rid ||
            payload.SdkVersion is not { Length: > 0 and <= 48 } || !System.Version.TryParse(payload.SdkVersion, out _) ||
            payload.RuntimeVersion is not { Length: > 0 and <= 48 } || !System.Version.TryParse(payload.RuntimeVersion, out _) ||
            !Wire.IsHex(payload.SourceCommit, 40) || payload.Files.Length != candidate.Files.Length - 2)
            throw new StagingException(StagingFailure.InvalidManifest);
        var expected = candidate.Files.Where(f => f.Path is not ("manifest.json" or "SHA256SUMS.txt"))
            .Select(f => f with { Path = f.Path.Replace('/', '\\') }).OrderBy(f => f.Path, StringComparer.Ordinal).ToArray();
        if (!expected.SequenceEqual(payload.Files)) throw new StagingException(StagingFailure.InvalidManifest);
        var expectedSums = string.Concat(expected.Select(f => $"{f.Sha256}  {f.Path}\n")) +
            $"{Wire.Hash(bytes)}  manifest.json\n";
        if (!Encoding.UTF8.GetBytes(expectedSums).AsSpan().SequenceEqual(sums))
            throw new StagingException(StagingFailure.InvalidManifest);
    }
}
