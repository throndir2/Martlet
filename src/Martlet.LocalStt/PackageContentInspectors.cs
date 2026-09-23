using System.Buffers.Binary;
using System.Collections.Immutable;
using System.IO.Compression;
using System.Security.Cryptography;

namespace Martlet.LocalStt;

internal static class RestrictedRuntimeArchive
{
    internal const int MaximumCompressionRatio = 100;
    private const int MaximumPathCharacters = 512;
    private const int MaximumSegmentCharacters = 128;
    private static readonly HashSet<string> ReservedWindowsNames =
        CreateReservedWindowsNames();

    internal static ImmutableArray<PackageFileDocument> Verify(
        Stream archive,
        LocalSttPackageManifest manifest,
        LocalSttImportEvidence evidence,
        Func<RuntimeFileDocument, FileStream>? createOutput,
        Action? beforeWrite,
        CancellationToken cancellationToken)
    {
        try
        {
            ValidateDirectoryBounds(archive, manifest.Document.Provisioning.MaximumArchiveEntries, cancellationToken);
            archive.Position = 0;
            using var zip = new ZipArchive(
                archive,
                ZipArchiveMode.Read,
                leaveOpen: true);
            var maximumEntries = manifest.Document.Provisioning.MaximumArchiveEntries;
            ProvisioningGuard.Require(
                zip.Entries.Count is > 0 &&
                zip.Entries.Count <= maximumEntries,
                LocalSttProvisioningFailure.ArchiveLimitExceeded);

            var byName = new Dictionary<string, ZipArchiveEntry>(
                StringComparer.Ordinal);
            var caseNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            long expandedBytes = 0;
            long compressedBytes = 0;
            foreach (var entry in zip.Entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var isDirectory = entry.FullName.EndsWith(
                    "/",
                    StringComparison.Ordinal);
                var normalized = isDirectory
                    ? entry.FullName[..^1]
                    : entry.FullName;
                ValidatePath(normalized);
                ProvisioningGuard.Require(
                    caseNames.Add(normalized) &&
                    byName.TryAdd(entry.FullName, entry),
                    LocalSttProvisioningFailure.ArchiveUnsafeEntry);
                ValidateEntryType(entry, isDirectory);
                ProvisioningGuard.Require(
                    entry.Length >= 0 &&
                    entry.CompressedLength >= 0 &&
                    (!isDirectory ||
                        entry.Length == 0 &&
                        entry.CompressedLength == 0),
                    LocalSttProvisioningFailure.ArchiveCorrupt);
                expandedBytes = checked(expandedBytes + entry.Length);
                compressedBytes = checked(
                    compressedBytes + entry.CompressedLength);
                ProvisioningGuard.Require(
                    expandedBytes <= manifest.Document.Provisioning
                        .MaximumExpandedRuntimeBytes,
                    LocalSttProvisioningFailure.ArchiveLimitExceeded);
                if (!isDirectory && entry.Length > 0)
                {
                    var compressed = Math.Max(1, entry.CompressedLength);
                    ProvisioningGuard.Require(
                        entry.Length <= checked(
                            compressed * MaximumCompressionRatio),
                        LocalSttProvisioningFailure.ArchiveLimitExceeded);
                }
                foreach (var name in caseNames)
                {
                    var parent = name;
                    while (parent.LastIndexOf('/') is var separator && separator >= 0)
                    {
                        parent = parent[..separator];
                        ProvisioningGuard.Require(!zip.Entries.Any(entry =>
                            !entry.FullName.EndsWith('/') &&
                            string.Equals(entry.FullName, parent, StringComparison.OrdinalIgnoreCase)),
                            LocalSttProvisioningFailure.ArchiveUnsafeEntry);
                    }
                }
            }
            if (expandedBytes > 0)
            {
                var compressed = Math.Max(1, compressedBytes);
                ProvisioningGuard.Require(
                    expandedBytes <= checked(
                        compressed * MaximumCompressionRatio),
                    LocalSttProvisioningFailure.ArchiveLimitExceeded);
            }

            var pins = evidence.RuntimeFiles.ToDictionary(
                pin => pin.ArchiveEntry,
                StringComparer.Ordinal);
            var receipts = ImmutableArray.CreateBuilder<PackageFileDocument>(
                manifest.Document.Runtime.Files.Length);
            var imports = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
            foreach (var file in manifest.Document.Runtime.Files
                         .OrderBy(file => file.InstalledName, StringComparer.Ordinal))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!byName.TryGetValue(
                        file.ArchiveEntry,
                        out var entry) ||
                    !pins.TryGetValue(
                        file.ArchiveEntry,
                        out var pin) ||
                    entry.Length != pin.Bytes ||
                    entry.Length <= 0)
                    throw new LocalSttProvisioningException(
                        LocalSttProvisioningFailure.RuntimeFileMismatch);
                using var input = entry.Open();
                using var output = createOutput?.Invoke(file);
                var header = PackageContentReader.CopyAndHash(
                    input,
                    output,
                    pin.Bytes,
                    pin.Sha256,
                    LocalSttProvisioningFailure.RuntimeFileMismatch,
                    beforeWrite,
                    cancellationToken,
                    checked((int)pin.Bytes));
                imports.Add(file.InstalledName, PortableExecutableInspector.Verify(
                    header,
                    pin.Bytes,
                    file.Purpose == RuntimeFilePurpose.Dependency,
                    cancellationToken));
                output?.Flush(flushToDisk: true);
                receipts.Add(new()
                {
                    Path = $"payload/runtime/{file.InstalledName}",
                    Bytes = pin.Bytes,
                    Sha256 = pin.Sha256,
                    Purpose = file.Purpose == RuntimeFilePurpose.Executable
                        ? "runtime-executable"
                        : "runtime-dependency"
                });
            }
            PortableExecutableInspector.VerifyDependencyGraph(imports);
            return receipts.MoveToImmutable();
        }
        catch (LocalSttProvisioningException)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception error) when (
            error is InvalidDataException or
                EndOfStreamException or
                NotSupportedException or
                OverflowException)
        {
            throw new LocalSttProvisioningException(
                LocalSttProvisioningFailure.ArchiveCorrupt);
        }
    }

    private static void ValidateDirectoryBounds(Stream archive, int maximumEntries, CancellationToken cancellationToken)
    {
        ProvisioningGuard.Require(archive.CanSeek && archive.Length >= 22,
            LocalSttProvisioningFailure.ArchiveCorrupt);
        var tail = new byte[(int)Math.Min(archive.Length, 65_557)];
        archive.Position = archive.Length - tail.Length;
        archive.ReadExactly(tail);
        var end = -1;
        for (var index = tail.Length - 22; index >= 0; index--)
            if (BinaryPrimitives.ReadUInt32LittleEndian(tail.AsSpan(index)) == 0x06054b50 &&
                index + 22 + BinaryPrimitives.ReadUInt16LittleEndian(tail.AsSpan(index + 20)) == tail.Length)
            {
                end = index;
                break;
            }
        ProvisioningGuard.Require(end >= 0, LocalSttProvisioningFailure.ArchiveCorrupt);
        var record = tail.AsSpan(end);
        var count = BinaryPrimitives.ReadUInt16LittleEndian(record[10..]);
        var size = BinaryPrimitives.ReadUInt32LittleEndian(record[12..]);
        var offset = BinaryPrimitives.ReadUInt32LittleEndian(record[16..]);
        ProvisioningGuard.Require(count > 0 && count <= maximumEntries &&
            size <= maximumEntries * (46 + 512 + 4096 + 4096),
            LocalSttProvisioningFailure.ArchiveLimitExceeded);
        ProvisioningGuard.Require(BinaryPrimitives.ReadUInt32LittleEndian(record[4..]) == 0 &&
            BinaryPrimitives.ReadUInt16LittleEndian(record[8..]) == count &&
            (ulong)offset + size == (ulong)(archive.Length - tail.Length + end),
            LocalSttProvisioningFailure.ArchiveCorrupt);
        var central = new byte[size];
        archive.Position = offset;
        archive.ReadExactly(central);
        var position = 0;
        var ranges = new List<(long Start, long End)>();
        var local = new byte[30];
        for (var entry = 0; entry < count; entry++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ProvisioningGuard.Require(position <= central.Length - 46,
                LocalSttProvisioningFailure.ArchiveCorrupt);
            var item = central.AsSpan(position);
            var flags = BinaryPrimitives.ReadUInt16LittleEndian(item[8..]);
            var method = BinaryPrimitives.ReadUInt16LittleEndian(item[10..]);
            var nameBytes = BinaryPrimitives.ReadUInt16LittleEndian(item[28..]);
            var extraBytes = BinaryPrimitives.ReadUInt16LittleEndian(item[30..]);
            var commentBytes = BinaryPrimitives.ReadUInt16LittleEndian(item[32..]);
            var compressed = BinaryPrimitives.ReadUInt32LittleEndian(item[20..]);
            var localOffset = BinaryPrimitives.ReadUInt32LittleEndian(item[42..]);
            ProvisioningGuard.Require(BinaryPrimitives.ReadUInt32LittleEndian(item) == 0x02014b50 &&
                (flags & ~0x080e) == 0 && method is 0 or 8 &&
                nameBytes is > 0 and <= 512 && extraBytes <= 4096 && commentBytes <= 4096 &&
                BinaryPrimitives.ReadUInt16LittleEndian(item[34..]) == 0 &&
                46 + nameBytes + extraBytes + commentBytes <= item.Length &&
                (ulong)localOffset + 30 <= offset,
                LocalSttProvisioningFailure.ArchiveCorrupt);
            archive.Position = localOffset;
            archive.ReadExactly(local);
            var localExtra = BinaryPrimitives.ReadUInt16LittleEndian(local.AsSpan(28));
            var payloadEnd = (long)localOffset + 30 + nameBytes + localExtra + compressed;
            ProvisioningGuard.Require(BinaryPrimitives.ReadUInt32LittleEndian(local) == 0x04034b50 &&
                BinaryPrimitives.ReadUInt16LittleEndian(local.AsSpan(6)) == flags &&
                BinaryPrimitives.ReadUInt16LittleEndian(local.AsSpan(8)) == method &&
                BinaryPrimitives.ReadUInt16LittleEndian(local.AsSpan(26)) == nameBytes &&
                localExtra <= 4096 && payloadEnd <= offset &&
                ranges.All(range => payloadEnd <= range.Start || localOffset >= range.End),
                LocalSttProvisioningFailure.ArchiveCorrupt);
            var localName = new byte[nameBytes];
            archive.ReadExactly(localName);
            ProvisioningGuard.Require(localName.AsSpan().SequenceEqual(item.Slice(46, nameBytes)),
                LocalSttProvisioningFailure.ArchiveCorrupt);
            ranges.Add((localOffset, payloadEnd));
            position += 46 + nameBytes + extraBytes + commentBytes;
        }
        ProvisioningGuard.Require(position == central.Length,
            LocalSttProvisioningFailure.ArchiveCorrupt);
    }

    private static void ValidateEntryType(
        ZipArchiveEntry entry,
        bool isDirectory)
    {
        const int UnixFileTypeMask = 0xF000;
        const int UnixRegularFile = 0x8000;
        const int UnixDirectory = 0x4000;
        var unixType = (entry.ExternalAttributes >> 16) & UnixFileTypeMask;
        var attributes = (FileAttributes)(entry.ExternalAttributes & 0xffff);
        ProvisioningGuard.Require(
            (attributes & (FileAttributes.ReparsePoint |
                FileAttributes.Device)) == 0,
            LocalSttProvisioningFailure.ArchiveUnsafeEntry);
        ProvisioningGuard.Require(!attributes.HasFlag(FileAttributes.Directory) || isDirectory,
            LocalSttProvisioningFailure.ArchiveUnsafeEntry);
        if (unixType == 0)
            return;
        ProvisioningGuard.Require(
            isDirectory
                ? unixType == UnixDirectory
                : unixType == UnixRegularFile,
            LocalSttProvisioningFailure.ArchiveUnsafeEntry);
    }

    private static void ValidatePath(string path)
    {
        ProvisioningGuard.Require(
            path.Length is >= 1 and <= MaximumPathCharacters &&
            path[0] != '/' &&
            !path.Contains('\\') &&
            !path.Contains(':') &&
            !path.Contains('\0'),
            LocalSttProvisioningFailure.ArchiveUnsafeEntry);
        foreach (var segment in path.Split('/'))
        {
            ProvisioningGuard.Require(
                segment.Length is >= 1 and <= MaximumSegmentCharacters &&
                segment is not "." and not ".." &&
                segment.All(character =>
                    char.IsAsciiLetterOrDigit(character) ||
                    character is '.' or '_' or '-') &&
                segment[^1] is not '.' and not ' ',
                LocalSttProvisioningFailure.ArchiveUnsafeEntry);
            var stem = segment.Split('.')[0];
            ProvisioningGuard.Require(
                !ReservedWindowsNames.Contains(stem),
                LocalSttProvisioningFailure.ArchiveUnsafeEntry);
        }
    }

    private static HashSet<string> CreateReservedWindowsNames()
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "CON", "PRN", "AUX", "NUL", "CLOCK$"
        };
        for (var index = 1; index <= 9; index++)
        {
            names.Add($"COM{index}");
            names.Add($"LPT{index}");
        }
        return names;
    }
}

internal static class PackageContentReader
{
    internal static byte[] CopyAndHash(
        Stream input,
        Stream? output,
        long expectedBytes,
        string expectedSha256,
        LocalSttProvisioningFailure mismatch,
        Action? beforeWrite,
        CancellationToken cancellationToken,
        int capturedHeaderBytes = 0)
    {
        using (var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
        {
            var buffer = new byte[65_536];
            var header = new byte[Math.Min(
                capturedHeaderBytes,
                checked((int)Math.Min(expectedBytes, int.MaxValue)))];
            long total = 0;
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var read = input.Read(buffer, 0, buffer.Length);
                if (read == 0)
                    break;
                total = checked(total + read);
                ProvisioningGuard.Require(
                    total <= expectedBytes,
                    mismatch);
                var previous = total - read;
                if (previous < header.Length)
                {
                    var count = Math.Min(
                        read,
                        header.Length - checked((int)previous));
                    buffer.AsSpan(0, count).CopyTo(
                        header.AsSpan(checked((int)previous)));
                }
                hash.AppendData(buffer, 0, read);
                if (output is not null)
                {
                    beforeWrite?.Invoke();
                    output.Write(buffer, 0, read);
                }
            }
            ProvisioningGuard.Require(total == expectedBytes, mismatch);
            var actual = Convert.ToHexStringLower(hash.GetHashAndReset());
            ProvisioningGuard.Require(
                ProvisioningWire.FixedHashEquals(actual, expectedSha256),
                mismatch);
            return header;
        }
    }
}

internal static class PortableExecutableInspector
{
    private const ushort Amd64Machine = 0x8664;
    private const ushort Pe32PlusMagic = 0x020b;
    private const ushort ExecutableImage = 0x0002;
    private const ushort DynamicLibrary = 0x2000;

    internal static string[] Verify(
        byte[] content,
        long fileBytes,
        bool expectDynamicLibrary,
        CancellationToken cancellationToken = default)
    {
        ReadOnlySpan<byte> header = content;
        ProvisioningGuard.Require(
            fileBytes >= 256 &&
            header.Length >= 256 &&
            header[0] == (byte)'M' &&
            header[1] == (byte)'Z',
            LocalSttProvisioningFailure.UnsupportedBinary);
        var peOffset = BinaryPrimitives.ReadInt32LittleEndian(
            header.Slice(0x3c, sizeof(int)));
        ProvisioningGuard.Require(
            peOffset is >= 0x40 &&
            peOffset <= header.Length - 26 &&
            header[peOffset] == (byte)'P' &&
            header[peOffset + 1] == (byte)'E' &&
            header[peOffset + 2] == 0 &&
            header[peOffset + 3] == 0,
            LocalSttProvisioningFailure.UnsupportedBinary);
        var coff = header[(peOffset + 4)..];
        var machine = BinaryPrimitives.ReadUInt16LittleEndian(coff);
        var sectionCount = BinaryPrimitives.ReadUInt16LittleEndian(coff[2..]);
        var optionalHeaderBytes =
            BinaryPrimitives.ReadUInt16LittleEndian(coff[16..]);
        var characteristics =
            BinaryPrimitives.ReadUInt16LittleEndian(coff[18..]);
        ProvisioningGuard.Require(
            machine == Amd64Machine &&
            sectionCount is >= 1 and <= 96 &&
            optionalHeaderBytes >= 112 &&
            peOffset + 24 + optionalHeaderBytes <= header.Length &&
            BinaryPrimitives.ReadUInt16LittleEndian(
                header[(peOffset + 24)..]) == Pe32PlusMagic &&
            (characteristics & ExecutableImage) != 0 &&
            ((characteristics & DynamicLibrary) != 0) ==
                expectDynamicLibrary,
            LocalSttProvisioningFailure.UnsupportedBinary);
        return PeImportTables.Read(content, cancellationToken);
    }

    internal static void VerifyDependencyGraph(IReadOnlyDictionary<string, string[]> imports)
    {
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var active = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in imports.Keys)
            Visit(name);

        void Visit(string name)
        {
            if (visited.Contains(name)) return;
            ProvisioningGuard.Require(active.Add(name), LocalSttProvisioningFailure.UnsupportedBinary);
            foreach (var dependency in imports[name])
            {
                if (imports.ContainsKey(dependency))
                    Visit(dependency);
                else
                    ProvisioningGuard.Require(PeImportTables.IsSystemContract(dependency),
                        LocalSttProvisioningFailure.UnsupportedBinary);
            }
            active.Remove(name);
            visited.Add(name);
        }
    }
}

internal static class PeImportTables
{
    private sealed record Section(uint Address, uint VirtualBytes, uint Offset, uint Bytes);

    internal static bool IsSystemContract(string name) =>
        name.ToLowerInvariant() is "kernel32.dll" or "kernelbase.dll" or "ntdll.dll" or
            "user32.dll" or "gdi32.dll" or "advapi32.dll" or "shell32.dll" or "ole32.dll" or
            "oleaut32.dll" or "winmm.dll" or "imm32.dll" or "setupapi.dll" or "version.dll" or
            "bcrypt.dll" or "ws2_32.dll" or "msvcrt.dll" or "ucrtbase.dll" or "wintrust.dll" ||
        name.StartsWith("api-ms-win-", StringComparison.OrdinalIgnoreCase) ||
        name.StartsWith("ext-ms-win-", StringComparison.OrdinalIgnoreCase);

    internal static string[] Read(byte[] data, CancellationToken cancellationToken)
    {
        // Inspect bounded archive bytes; never map executable code or search machine DLLs.
        try
        {
            var work = 0;
            var pe = checked((int)U32(0x3c));
            var optional = pe + 24;
            var count = U16(pe + 6);
            var optionalBytes = U16(pe + 20);
            var directoryCount = U32(optional + 108);
            Require(directoryCount <= 16 && optionalBytes >= 112 + directoryCount * 8);
            var headerBytes = U32(optional + 60);
            var sectionTable = optional + optionalBytes;
            Require(headerBytes <= data.Length && sectionTable + count * 40 <= headerBytes);
            var sections = new List<Section>();
            for (var index = 0; index < count; index++)
            {
                var offset = sectionTable + index * 40;
                var section = new Section(U32(offset + 12), U32(offset + 8), U32(offset + 20), U32(offset + 16));
                Require((ulong)section.Offset + section.Bytes <= (ulong)data.Length &&
                    (section.Bytes == 0 || section.Offset >= headerBytes) &&
                    section.Address >= headerBytes &&
                    (ulong)section.Address + Math.Max(section.VirtualBytes, section.Bytes) <= uint.MaxValue);
                foreach (var prior in sections)
                    Require(!Overlap(section.Address, Math.Max(section.VirtualBytes, section.Bytes),
                        prior.Address, Math.Max(prior.VirtualBytes, prior.Bytes)) &&
                        !Overlap(section.Offset, section.Bytes, prior.Offset, prior.Bytes));
                sections.Add(section);
            }
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            ReadDirectory(1, delay: false);
            ReadDirectory(13, delay: true);
            return names.Order(StringComparer.OrdinalIgnoreCase).ToArray();

            void ReadDirectory(int index, bool delay)
            {
                if (directoryCount <= index) return;
                var rva = U32(optional + 112 + index * 8);
                var size = U32(optional + 116 + index * 8);
                Require((rva == 0) == (size == 0));
                if (rva == 0) return;
                var stride = delay ? 32 : 20;
                Require(size >= stride && size <= 4096 * stride);
                var start = Map(rva, checked((int)size));
                for (var position = 0; position + stride <= size; position += stride)
                {
                    Budget();
                    var descriptor = start + position;
                    if (data.AsSpan(descriptor, stride).IndexOfAnyExcept((byte)0) < 0)
                        return;
                    if (delay) Require(U32(descriptor) == 1);
                    var nameRva = U32(descriptor + (delay ? 4 : 12));
                    var name = Text(nameRva, 128);
                    Require(name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) &&
                        name.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-') &&
                        name.Length > 4);
                    names.Add(name);
                    var lookup = U32(descriptor + (delay ? 16 : 0));
                    var address = U32(descriptor + (delay ? 12 : 16));
                    if (delay) Require(lookup != 0);
                    if (lookup == 0) lookup = address;
                    Require(lookup != 0 && address != 0);
                    var symbols = ReadThunks(lookup);
                    ValidateAddressTable(address, symbols);
                }
                Require(false);
            }

            int ReadThunks(uint rva)
            {
                for (var index = 0; index < 4096; index++)
                {
                    Budget();
                    var offset = Map(checked(rva + (uint)index * 8), 8);
                    var thunk = BinaryPrimitives.ReadUInt64LittleEndian(data.AsSpan(offset, 8));
                    if (thunk == 0) return index;
                    if ((thunk & 0x8000000000000000UL) != 0)
                        Require((thunk & 0x7fffffffffff0000UL) == 0);
                    else
                    {
                        Require(thunk <= uint.MaxValue);
                        Map((uint)thunk, 2);
                        Text(checked((uint)thunk + 2), 512);
                    }
                }
                Require(false);
                return 0;
            }

            void ValidateAddressTable(uint rva, int symbols)
            {
                for (var index = 0; index <= symbols; index++)
                {
                    Budget();
                    var offset = Map(checked(rva + (uint)index * 8), 8);
                    var value = BinaryPrimitives.ReadUInt64LittleEndian(data.AsSpan(offset, 8));
                    // Bound/delay IAT entries can be helper VAs, not name RVAs.
                    Require((value == 0) == (index == symbols));
                }
            }

            string Text(uint rva, int maximum)
            {
                var characters = new List<char>();
                for (var index = 0; index <= maximum; index++)
                {
                    Budget();
                    var value = data[Map(checked(rva + (uint)index), 1)];
                    if (value == 0)
                    {
                        Require(characters.Count > 0);
                        return new string(characters.ToArray());
                    }
                    Require(value is >= 33 and <= 126 && index < maximum);
                    characters.Add((char)value);
                }
                throw new LocalSttProvisioningException(LocalSttProvisioningFailure.UnsupportedBinary);
            }

            int Map(uint rva, int length)
            {
                foreach (var section in sections)
                    if (rva >= section.Address &&
                        (ulong)rva + (uint)length <= (ulong)section.Address + section.Bytes)
                        return checked((int)(section.Offset + rva - section.Address));
                Require(false);
                return 0;
            }

            void Budget()
            {
                cancellationToken.ThrowIfCancellationRequested();
                Require(++work <= 1_048_576);
            }
        }
        catch (Exception error) when (error is ArgumentOutOfRangeException or OverflowException)
        {
            throw new LocalSttProvisioningException(LocalSttProvisioningFailure.UnsupportedBinary);
        }
        uint U32(int offset) => BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset, 4));
        ushort U16(int offset) => BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(offset, 2));
    }

    private static bool Overlap(uint start, uint length, uint other, uint otherLength) =>
        length != 0 && otherLength != 0 && (ulong)start < (ulong)other + otherLength &&
        (ulong)other < (ulong)start + length;

    private static void Require(bool condition) =>
        ProvisioningGuard.Require(condition, LocalSttProvisioningFailure.UnsupportedBinary);
}

internal static class WhisperModelInspector
{
    private const uint GgmlMagic = 0x67676d6c;
    internal const int HeaderBytes = 48;

    internal static void Verify(ReadOnlySpan<byte> header, long modelBytes)
    {
        ProvisioningGuard.Require(
            modelBytes >= HeaderBytes &&
            header.Length >= HeaderBytes &&
            BinaryPrimitives.ReadUInt32LittleEndian(header) == GgmlMagic,
            LocalSttProvisioningFailure.ModelInvalid);
        Span<int> values = stackalloc int[11];
        for (var index = 0; index < values.Length; index++)
            values[index] = BinaryPrimitives.ReadInt32LittleEndian(
                header.Slice(4 + index * sizeof(int), sizeof(int)));
        var nVocab = values[0];
        var nAudioContext = values[1];
        var nAudioState = values[2];
        var nAudioHeads = values[3];
        var nAudioLayers = values[4];
        var nTextContext = values[5];
        var nTextState = values[6];
        var nTextHeads = values[7];
        var nTextLayers = values[8];
        var nMels = values[9];
        var ftype = values[10];
        ProvisioningGuard.Require(
            nVocab is >= 1_000 and <= 1_000_000 &&
            nAudioContext is >= 1 and <= 65_536 &&
            nAudioState is >= 1 and <= 65_536 &&
            nAudioHeads is >= 1 and <= 1_024 &&
            nAudioLayers is >= 1 and <= 1_024 &&
            nTextContext is >= 1 and <= 65_536 &&
            nTextState is >= 1 and <= 65_536 &&
            nTextHeads is >= 1 and <= 1_024 &&
            nTextLayers is >= 1 and <= 1_024 &&
            nMels is >= 1 and <= 512 &&
            ftype is >= 0 and <= 32 &&
            nAudioState % nAudioHeads == 0 &&
            nTextState % nTextHeads == 0,
            LocalSttProvisioningFailure.ModelInvalid);
    }
}
