using System.Security.Cryptography;

namespace Martlet.Updates;

internal static class BoundedIo
{
    private static readonly uint[] CrcTable = CreateCrcTable();
    internal static FileStream OpenRead(string path)
    {
        LocalPaths.NoReparse(path);
        return new(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.SequentialScan);
    }

    internal static byte[] Read(Stream source, int maximum, CancellationToken token)
    {
        using var output = new MemoryStream();
        var buffer = new byte[65536];
        while (true)
        {
            token.ThrowIfCancellationRequested();
            var count = source.Read(buffer, 0, Math.Min(buffer.Length, maximum + 1 - (int)output.Length));
            if (count == 0) return output.ToArray();
            if (output.Length + count > maximum) throw new StagingException(StagingFailure.CapacityExceeded);
            output.Write(buffer, 0, count);
        }
    }

    internal static void CopyAndHash(Stream input, Stream? output, long expectedBytes, string expectedHash,
        CancellationToken token, Action? beforeWrite = null, uint? expectedCrc = null)
    {
        var buffer = new byte[65536];
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        long length = 0;
        uint crc = uint.MaxValue;
        while (true)
        {
            token.ThrowIfCancellationRequested();
            var count = input.Read(buffer, 0, (int)Math.Min(buffer.Length, expectedBytes - length + 1));
            if (count == 0) break;
            length += count;
            if (length > expectedBytes) throw new StagingException(StagingFailure.CorruptArchive);
            hash.AppendData(buffer, 0, count);
            if (expectedCrc is not null)
                for (var i = 0; i < count; i++) crc = CrcTable[(crc ^ buffer[i]) & 255] ^ (crc >> 8);
            if (output is not null)
            {
                beforeWrite?.Invoke();
                token.ThrowIfCancellationRequested();
                output.Write(buffer, 0, count);
            }
        }
        if (length != expectedBytes || Convert.ToHexStringLower(hash.GetHashAndReset()) != expectedHash ||
            expectedCrc is not null && ~crc != expectedCrc)
            throw new StagingException(StagingFailure.CorruptArchive);
    }

    private static uint[] CreateCrcTable()
    {
        var table = new uint[256];
        for (uint i = 0; i < table.Length; i++)
        {
            var c = i;
            for (var bit = 0; bit < 8; bit++) c = (c >> 1) ^ ((c & 1) == 0 ? 0 : 0xedb88320);
            table[i] = c;
        }
        return table;
    }
}
