using System.Buffers.Binary;
using System.Numerics;
using System.Security.Cryptography;
using Martlet.Core.Contracts;

namespace Martlet.Core.Audio;

/// <summary>Interleaved signed 16-bit little-endian PCM decoded from FLAC.</summary>
public sealed record FlacAudio(byte[] Pcm16, int SampleRate, int Channels)
{
    public long Frames => Pcm16.Length / (2L * Channels);
    public TimeSpan Duration => TimeSpan.FromSeconds((double)Frames / SampleRate);
}

/// <summary>A small, dependency-free FLAC encoder and decoder for 16-bit mono or stereo PCM, so Martlet's creations keep
/// audio losslessly at about half the size of WAV. The encoder writes standard FLAC (STREAMINFO with the audio's MD5, fixed
/// 4096-sample blocks, constant, verbatim or fixed-predictor subframes with partitioned Rice residuals and the best of
/// independent, left/side, right/side or mid/side stereo), which any FLAC player reads. The decoder reads any 16-bit FLAC
/// (fixed and LPC subframes, wasted bits, both Rice codings and escapes), checks every frame's CRC and the MD5, and throws
/// <see cref="ContractException"/> for anything else.</summary>
public static class FlacCodec
{
    public const string MediaType = "audio/flac";
    private const int BlockSize = 4096;
    private const int MaximumPartitionOrder = 8;
    private const int BitsPerSample = 16;

    /// <summary>Encodes interleaved signed 16-bit little-endian PCM.</summary>
    public static byte[] Encode(ReadOnlySpan<byte> pcm16, int sampleRate, int channels)
    {
        ContractRules.Require(channels is 1 or 2, "FLAC audio must be mono or stereo.", ErrorCode.AudioFormatUnsupported);
        ContractRules.Require(sampleRate is >= 1 and <= 655_350, "The sample rate is out of range.", ErrorCode.AudioFormatUnsupported);
        ContractRules.Require(pcm16.Length > 0 && pcm16.Length % (2 * channels) == 0, "The audio is not interleaved 16-bit PCM.",
            ErrorCode.AudioFormatUnsupported);
        var frames = pcm16.Length / (2 * channels);
        var writer = new BitWriter(pcm16.Length / 2 + 1024);
        writer.Write(0x664C6143, 32); // fLaC
        writer.Write(0x80, 8); // last metadata block, STREAMINFO
        writer.Write(34, 24);
        var block = Math.Max(16, Math.Min(BlockSize, frames));
        writer.Write((uint)block, 16);
        writer.Write((uint)block, 16);
        writer.Write(0, 24); // minimum frame size unknown
        writer.Write(0, 24); // maximum frame size unknown
        writer.Write((uint)sampleRate, 20);
        writer.Write((uint)(channels - 1), 3);
        writer.Write(BitsPerSample - 1, 5);
        writer.Write((uint)((long)frames >> 32), 4);
        writer.Write((uint)frames, 32);
        foreach (var b in MD5.HashData(pcm16)) writer.Write(b, 8);

        var left = new int[BlockSize];
        var right = new int[BlockSize];
        var mid = new int[BlockSize];
        var side = new int[BlockSize];
        var residual = new int[BlockSize];
        var frameNumber = 0u;
        for (var start = 0; start < frames; start += BlockSize, frameNumber++)
        {
            var n = Math.Min(BlockSize, frames - start);
            for (var i = 0; i < n; i++)
            {
                var offset = (start + i) * 2 * channels;
                left[i] = BinaryPrimitives.ReadInt16LittleEndian(pcm16[offset..]);
                if (channels == 2) right[i] = BinaryPrimitives.ReadInt16LittleEndian(pcm16[(offset + 2)..]);
            }
            var frameStart = writer.ByteLength;
            int assignment;
            Plan[] plans;
            if (channels == 1)
            {
                assignment = 0;
                plans = [Choose(left.AsSpan(0, n), BitsPerSample, residual)];
            }
            else
            {
                for (var i = 0; i < n; i++)
                {
                    mid[i] = (left[i] + right[i]) >> 1;
                    side[i] = left[i] - right[i];
                }
                var l = Choose(left.AsSpan(0, n), BitsPerSample, residual);
                var r = Choose(right.AsSpan(0, n), BitsPerSample, residual);
                var m = Choose(mid.AsSpan(0, n), BitsPerSample, residual);
                var s = Choose(side.AsSpan(0, n), BitsPerSample + 1, residual);
                long independent = l.Bits + r.Bits, leftSide = l.Bits + s.Bits, rightSide = r.Bits + s.Bits, midSide = m.Bits + s.Bits;
                var best = Math.Min(Math.Min(independent, leftSide), Math.Min(rightSide, midSide));
                (assignment, plans) = best == independent ? (1, new[] { l, r })
                    : best == leftSide ? (8, new[] { l, s })
                    : best == rightSide ? (9, new[] { s, r })
                    : (10, new[] { m, s });
            }

            writer.Write(0x3FFE, 14);
            writer.Write(0, 1);
            writer.Write(0, 1); // fixed block size
            writer.Write(n == BlockSize ? 12u : 7u, 4);
            writer.Write(0, 4); // sample rate from STREAMINFO
            writer.Write((uint)assignment, 4);
            writer.Write(4, 3); // 16 bits per sample
            writer.Write(0, 1);
            WriteCodedNumber(writer, frameNumber);
            if (n != BlockSize) writer.Write((uint)(n - 1), 16);
            writer.Write(Crc8(writer.Bytes(frameStart, writer.ByteLength - frameStart)), 8);

            for (var c = 0; c < plans.Length; c++)
            {
                var samples = (assignment, c) switch
                {
                    (0 or 1, 0) or (8, 0) => left,
                    (1, 1) or (9, 1) => right,
                    (8, 1) or (9, 0) or (10, 1) => side,
                    _ => mid
                };
                WriteSubframe(writer, samples.AsSpan(0, n), plans[c], residual);
            }
            writer.Align();
            var crc = Crc16(writer.Bytes(frameStart, writer.ByteLength - frameStart));
            writer.Write(crc, 16);
        }
        return writer.ToArray();
    }

    /// <summary>Decodes a 16-bit FLAC stream into interleaved signed 16-bit little-endian PCM.</summary>
    public static FlacAudio Decode(ReadOnlySpan<byte> flac)
    {
        try { return DecodeCore(flac); }
        catch (Exception error) when (error is IndexOutOfRangeException or ArgumentOutOfRangeException or OverflowException)
        {
            throw new ContractException(ErrorCode.AudioFormatUnsupported, "The FLAC audio is damaged.");
        }
    }

    /// <summary>Whether <paramref name="bytes"/> start like a FLAC stream.</summary>
    public static bool IsFlac(ReadOnlySpan<byte> bytes) => bytes.Length >= 4 && bytes[..4].SequenceEqual("fLaC"u8);

    private static FlacAudio DecodeCore(ReadOnlySpan<byte> flac)
    {
        static void Require(bool condition, string message)
        {
            if (!condition) throw new ContractException(ErrorCode.AudioFormatUnsupported, message);
        }
        Require(IsFlac(flac), "This isn't FLAC audio.");
        var reader = new BitReader(flac.ToArray(), 4);
        int sampleRate = 0, channels = 0, bits = 0;
        long totalFrames = 0;
        byte[]? md5 = null;
        bool last;
        do
        {
            last = reader.Read(1) == 1;
            var type = (int)reader.Read(7);
            var length = (int)reader.Read(24);
            if (type == 0)
            {
                Require(length == 34, "The FLAC stream information is damaged.");
                reader.Skip(16 + 16 + 24 + 24);
                sampleRate = (int)reader.Read(20);
                channels = (int)reader.Read(3) + 1;
                bits = (int)reader.Read(5) + 1;
                totalFrames = ((long)reader.Read(4) << 32) | reader.Read(32);
                md5 = new byte[16];
                for (var i = 0; i < 16; i++) md5[i] = (byte)reader.Read(8);
            }
            else reader.Skip(length * 8L);
        } while (!last);
        Require(md5 is not null, "The FLAC audio has no stream information.");
        Require(bits == BitsPerSample, "Only 16-bit FLAC audio is supported.");
        Require(channels is 1 or 2, "Only mono or stereo FLAC audio is supported.");
        Require(sampleRate > 0, "The FLAC audio has no sample rate.");
        Require(totalFrames is >= 0 and <= int.MaxValue / 4, "The FLAC audio is too long.");

        var pcm = new byte[totalFrames > 0 ? totalFrames * 2 * channels : 1 << 20];
        var written = 0;
        var decoded = new int[2][];
        while (!reader.AtEnd)
        {
            var frameStart = reader.BytePosition;
            Require(reader.Read(14) == 0x3FFE, "The FLAC audio is damaged.");
            Require(reader.Read(1) == 0, "The FLAC audio is damaged.");
            var variable = reader.Read(1) == 1;
            var sizeCode = (int)reader.Read(4);
            var rateCode = (int)reader.Read(4);
            var assignment = (int)reader.Read(4);
            var sizeBits = (int)reader.Read(3);
            Require(reader.Read(1) == 0, "The FLAC audio is damaged.");
            ReadCodedNumber(ref reader, variable ? 7 : 6);
            var n = sizeCode switch
            {
                1 => 192,
                >= 2 and <= 5 => 576 << (sizeCode - 2),
                6 => (int)reader.Read(8) + 1,
                7 => (int)reader.Read(16) + 1,
                >= 8 => 256 << (sizeCode - 8),
                _ => -1
            };
            Require(n > 0, "The FLAC audio is damaged.");
            if (rateCode == 12) reader.Skip(8);
            else if (rateCode is 13 or 14) reader.Skip(16);
            Require(rateCode != 15, "The FLAC audio is damaged.");
            Require(sizeBits is 0 or 4, "Only 16-bit FLAC audio is supported.");
            var frameChannels = assignment < 8 ? assignment + 1 : 2;
            Require(assignment <= 10 && frameChannels == channels, "The FLAC audio changes its channels.");
            var headerEnd = reader.BytePosition;
            Require(reader.Read(8) == Crc8(flac.Slice(frameStart, headerEnd - frameStart)), "The FLAC audio is damaged.");

            for (var c = 0; c < channels; c++)
            {
                var sideChannel = (assignment == 8 && c == 1) || (assignment == 9 && c == 0) || (assignment == 10 && c == 1);
                if (decoded[c] is null || decoded[c].Length < n) decoded[c] = new int[Math.Max(n, BlockSize)];
                ReadSubframe(ref reader, decoded[c].AsSpan(0, n), BitsPerSample + (sideChannel ? 1 : 0));
            }
            reader.Align();
            var crcEnd = reader.BytePosition;
            Require(reader.Read(16) == Crc16(flac.Slice(frameStart, crcEnd - frameStart)), "The FLAC audio is damaged.");

            var needed = written + n * 2 * channels;
            if (needed > pcm.Length)
            {
                Require(totalFrames == 0 && needed <= int.MaxValue / 2, "The FLAC audio is longer than it says.");
                Array.Resize(ref pcm, Math.Max(needed, pcm.Length * 2));
            }
            for (var i = 0; i < n; i++)
            {
                int a = decoded[0][i], b = channels == 2 ? decoded[1][i] : 0;
                (a, b) = assignment switch
                {
                    8 => (a, a - b),
                    9 => (a + b, b),
                    10 => (((a << 1) | (b & 1)) + b >> 1, ((a << 1) | (b & 1)) - b >> 1),
                    _ => (a, b)
                };
                Require(a is >= short.MinValue and <= short.MaxValue && b is >= short.MinValue and <= short.MaxValue,
                    "The FLAC audio is damaged.");
                BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan(written), (short)a);
                written += 2;
                if (channels == 2)
                {
                    BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan(written), (short)b);
                    written += 2;
                }
            }
        }
        Require(totalFrames == 0 || written == totalFrames * 2 * channels, "The FLAC audio is shorter than it says.");
        if (written != pcm.Length) Array.Resize(ref pcm, written);
        Require(md5!.All(b => b == 0) || MD5.HashData(pcm).AsSpan().SequenceEqual(md5), "The FLAC audio doesn't match its checksum.");
        return new(pcm, sampleRate, channels);
    }

    // ---------- encoding ----------

    private readonly record struct Plan(int Kind, int Order, int PartitionOrder, int[]? Parameters, int Bps, long Bits)
    {
        internal const int Constant = 0, Verbatim = 1, Fixed = 2;
    }

    // The cheapest subframe for one channel: constant, a fixed predictor with its best partitioned Rice residual, or verbatim.
    private static Plan Choose(ReadOnlySpan<int> samples, int bps, int[] residual)
    {
        var n = samples.Length;
        var constant = true;
        for (var i = 1; i < n && constant; i++) constant = samples[i] == samples[0];
        if (constant) return new(Plan.Constant, 0, 0, null, bps, 8 + bps);
        var bestOrder = 0;
        var bestSum = long.MaxValue;
        for (var order = 0; order <= Math.Min(4, n - 1); order++)
        {
            long sum = 0;
            for (var i = order; i < n; i++) sum += Math.Abs((long)Predict(samples, i, order));
            if (sum < bestSum)
            {
                bestSum = sum;
                bestOrder = order;
            }
        }
        for (var i = bestOrder; i < n; i++) residual[i] = Predict(samples, i, bestOrder);
        var (partitionOrder, parameters, residualBits) = PlanResidual(residual.AsSpan(0, n), bestOrder);
        var fixedBits = 8 + (long)bestOrder * bps + residualBits;
        var verbatimBits = 8 + (long)n * bps;
        return fixedBits < verbatimBits
            ? new(Plan.Fixed, bestOrder, partitionOrder, parameters, bps, fixedBits)
            : new(Plan.Verbatim, 0, 0, null, bps, verbatimBits);
    }

    private static int Predict(ReadOnlySpan<int> x, int i, int order) => order switch
    {
        0 => x[i],
        1 => x[i] - x[i - 1],
        2 => x[i] - 2 * x[i - 1] + x[i - 2],
        3 => x[i] - 3 * x[i - 1] + 3 * x[i - 2] - x[i - 3],
        _ => x[i] - 4 * x[i - 1] + 6 * x[i - 2] - 4 * x[i - 3] + x[i - 4]
    };

    private static uint ZigZag(int value) => (uint)((value << 1) ^ (value >> 31));

    // Picks the partition order and each partition's Rice parameter by estimated size; returns the estimate in bits.
    private static (int Order, int[] Parameters, long Bits) PlanResidual(ReadOnlySpan<int> residual, int predictorOrder)
    {
        var n = residual.Length;
        (int, int[], long) best = (0, [], long.MaxValue);
        for (var order = 0; order <= MaximumPartitionOrder; order++)
        {
            var partitions = 1 << order;
            if (n % partitions != 0 || (n >> order) <= predictorOrder) break;
            var size = n >> order;
            var parameters = new int[partitions];
            long bits = 6;
            for (var p = 0; p < partitions; p++)
            {
                var from = p == 0 ? predictorOrder : p * size;
                var to = (p + 1) * size;
                long sum = 0;
                for (var i = from; i < to; i++) sum += ZigZag(residual[i]);
                var count = to - from;
                var (k, cost) = RiceParameter(sum, count);
                parameters[p] = k;
                bits += cost;
            }
            bits += (long)partitions * (parameters.Any(k => k > 14) ? 5 : 4);
            if (bits < best.Item3) best = (order, parameters, bits);
        }
        return best;
    }

    private static (int K, long Bits) RiceParameter(long sum, int count)
    {
        if (count == 0) return (0, 0);
        var mean = sum / count;
        var estimate = mean <= 0 ? 0 : Math.Min(30, BitOperations.Log2((ulong)mean));
        var bestK = estimate;
        var bestBits = long.MaxValue;
        for (var k = Math.Max(0, estimate - 1); k <= Math.Min(30, estimate + 1); k++)
        {
            var bits = (long)count * (k + 1) + (sum >> k);
            if (bits < bestBits)
            {
                bestBits = bits;
                bestK = k;
            }
        }
        return (bestK, bestBits);
    }

    private static void WriteSubframe(BitWriter writer, ReadOnlySpan<int> samples, Plan plan, int[] residual)
    {
        writer.Write(0, 1);
        switch (plan.Kind)
        {
            case Plan.Constant:
                writer.Write(0, 6);
                writer.Write(0, 1);
                writer.WriteSigned(samples[0], plan.Bps);
                return;
            case Plan.Verbatim:
                writer.Write(1, 6);
                writer.Write(0, 1);
                foreach (var sample in samples) writer.WriteSigned(sample, plan.Bps);
                return;
        }
        writer.Write((uint)(8 | plan.Order), 6);
        writer.Write(0, 1);
        for (var i = 0; i < plan.Order; i++) writer.WriteSigned(samples[i], plan.Bps);
        var n = samples.Length;
        for (var i = plan.Order; i < n; i++) residual[i] = Predict(samples, i, plan.Order);
        var wide = plan.Parameters!.Any(k => k > 14);
        writer.Write(wide ? 1u : 0u, 2);
        writer.Write((uint)plan.PartitionOrder, 4);
        var size = n >> plan.PartitionOrder;
        for (var p = 0; p < plan.Parameters!.Length; p++)
        {
            var k = plan.Parameters[p];
            writer.Write((uint)k, wide ? 5 : 4);
            var from = p == 0 ? plan.Order : p * size;
            for (var i = from; i < (p + 1) * size; i++) writer.WriteRice(ZigZag(residual[i]), k);
        }
    }

    private static void WriteCodedNumber(BitWriter writer, uint value)
    {
        if (value < 0x80)
        {
            writer.Write(value, 8);
            return;
        }
        var bytes = value < 0x800 ? 2 : value < 0x10000 ? 3 : value < 0x200000 ? 4 : value < 0x4000000 ? 5 : 6;
        var lead = (uint)(0xFF << (8 - bytes)) & 0xFF;
        writer.Write(lead | (value >> (6 * (bytes - 1))), 8);
        for (var i = bytes - 2; i >= 0; i--) writer.Write(0x80 | ((value >> (6 * i)) & 0x3F), 8);
    }

    // ---------- decoding ----------

    private static void ReadCodedNumber(ref BitReader reader, int maximumBytes)
    {
        var first = reader.Read(8);
        var extra = first < 0x80 ? 0 : first < 0xC0 ? -1 : first < 0xE0 ? 1 : first < 0xF0 ? 2 : first < 0xF8 ? 3 : first < 0xFC ? 4 : first < 0xFE ? 5 : 6;
        if (extra < 0 || extra + 1 > maximumBytes) throw new ContractException(ErrorCode.AudioFormatUnsupported, "The FLAC audio is damaged.");
        for (var i = 0; i < extra; i++)
            if ((reader.Read(8) & 0xC0) != 0x80) throw new ContractException(ErrorCode.AudioFormatUnsupported, "The FLAC audio is damaged.");
    }

    private static void ReadSubframe(ref BitReader reader, Span<int> output, int bps)
    {
        static void Require(bool condition)
        {
            if (!condition) throw new ContractException(ErrorCode.AudioFormatUnsupported, "The FLAC audio is damaged.");
        }
        Require(reader.Read(1) == 0);
        var type = (int)reader.Read(6);
        var wasted = 0;
        if (reader.Read(1) == 1) wasted = (int)reader.ReadUnary() + 1;
        Require(wasted < bps);
        bps -= wasted;
        var n = output.Length;
        if (type == 0)
        {
            var value = reader.ReadSigned(bps);
            output.Fill(value);
        }
        else if (type == 1)
        {
            for (var i = 0; i < n; i++) output[i] = reader.ReadSigned(bps);
        }
        else if (type is >= 8 and <= 12)
        {
            var order = type - 8;
            Require(order <= n);
            for (var i = 0; i < order; i++) output[i] = reader.ReadSigned(bps);
            ReadResidual(ref reader, output, order);
            for (var i = order; i < n; i++)
                output[i] += order switch
                {
                    0 => 0,
                    1 => output[i - 1],
                    2 => 2 * output[i - 1] - output[i - 2],
                    3 => 3 * output[i - 1] - 3 * output[i - 2] + output[i - 3],
                    _ => 4 * output[i - 1] - 6 * output[i - 2] + 4 * output[i - 3] - output[i - 4]
                };
        }
        else if (type >= 32)
        {
            var order = type - 31;
            Require(order <= n);
            for (var i = 0; i < order; i++) output[i] = reader.ReadSigned(bps);
            var precision = (int)reader.Read(4) + 1;
            Require(precision != 16);
            var shift = reader.ReadSigned(5);
            Require(shift >= 0);
            Span<int> coefficients = stackalloc int[order];
            for (var i = 0; i < order; i++) coefficients[i] = reader.ReadSigned(precision);
            ReadResidual(ref reader, output, order);
            for (var i = order; i < n; i++)
            {
                long sum = 0;
                for (var j = 0; j < order; j++) sum += (long)coefficients[j] * output[i - 1 - j];
                output[i] += (int)(sum >> shift);
            }
        }
        else Require(false);
        if (wasted > 0)
            for (var i = 0; i < n; i++) output[i] <<= wasted;
    }

    private static void ReadResidual(ref BitReader reader, Span<int> output, int predictorOrder)
    {
        static void Require(bool condition)
        {
            if (!condition) throw new ContractException(ErrorCode.AudioFormatUnsupported, "The FLAC audio is damaged.");
        }
        var method = (int)reader.Read(2);
        Require(method is 0 or 1);
        var parameterBits = method == 0 ? 4 : 5;
        var escape = method == 0 ? 15 : 31;
        var partitionOrder = (int)reader.Read(4);
        var n = output.Length;
        var partitions = 1 << partitionOrder;
        Require(n % partitions == 0 && (n >> partitionOrder) >= predictorOrder);
        var size = n >> partitionOrder;
        for (var p = 0; p < partitions; p++)
        {
            var k = (int)reader.Read(parameterBits);
            var from = p == 0 ? predictorOrder : p * size;
            var to = (p + 1) * size;
            if (k == escape)
            {
                var raw = (int)reader.Read(5);
                for (var i = from; i < to; i++) output[i] = raw == 0 ? 0 : reader.ReadSigned(raw);
            }
            else
            {
                for (var i = from; i < to; i++)
                {
                    var u = (reader.ReadUnary() << k) | reader.Read(k);
                    output[i] = (int)(u >> 1) ^ -(int)(u & 1);
                }
            }
        }
    }

    // ---------- checksums ----------

    private static readonly byte[] Crc8Table = BuildCrc8();
    private static readonly ushort[] Crc16Table = BuildCrc16();

    private static byte[] BuildCrc8()
    {
        var table = new byte[256];
        for (var i = 0; i < 256; i++)
        {
            var crc = (byte)i;
            for (var bit = 0; bit < 8; bit++) crc = (byte)((crc & 0x80) != 0 ? (crc << 1) ^ 0x07 : crc << 1);
            table[i] = crc;
        }
        return table;
    }

    private static ushort[] BuildCrc16()
    {
        var table = new ushort[256];
        for (var i = 0; i < 256; i++)
        {
            var crc = (ushort)(i << 8);
            for (var bit = 0; bit < 8; bit++) crc = (ushort)((crc & 0x8000) != 0 ? (crc << 1) ^ 0x8005 : crc << 1);
            table[i] = crc;
        }
        return table;
    }

    private static uint Crc8(ReadOnlySpan<byte> bytes)
    {
        byte crc = 0;
        foreach (var b in bytes) crc = Crc8Table[crc ^ b];
        return crc;
    }

    private static uint Crc16(ReadOnlySpan<byte> bytes)
    {
        ushort crc = 0;
        foreach (var b in bytes) crc = (ushort)((crc << 8) ^ Crc16Table[(crc >> 8) ^ b]);
        return crc;
    }

    // ---------- bits ----------

    private sealed class BitWriter(int capacity)
    {
        private byte[] buffer = new byte[Math.Max(64, capacity)];
        private int length;
        private ulong accumulator;
        private int pending;

        internal int ByteLength => length;

        internal ReadOnlySpan<byte> Bytes(int start, int count) => buffer.AsSpan(start, count);

        internal void Write(uint value, int count)
        {
            if (count == 0) return;
            accumulator = (accumulator << count) | (value & (count == 32 ? uint.MaxValue : (1u << count) - 1));
            pending += count;
            while (pending >= 8)
            {
                pending -= 8;
                Append((byte)(accumulator >> pending));
            }
            accumulator &= (1UL << pending) - 1;
        }

        internal void WriteSigned(int value, int count) => Write((uint)value, count);

        internal void WriteRice(uint value, int k)
        {
            var quotient = value >> k;
            while (quotient >= 32)
            {
                Write(0, 32);
                quotient -= 32;
            }
            Write(1, (int)quotient + 1);
            Write(value, k);
        }

        internal void Align()
        {
            if (pending > 0) Write(0, 8 - pending);
        }

        internal byte[] ToArray()
        {
            Align();
            return buffer.AsSpan(0, length).ToArray();
        }

        private void Append(byte value)
        {
            if (length == buffer.Length) Array.Resize(ref buffer, buffer.Length * 2);
            buffer[length++] = value;
        }
    }

    private struct BitReader(byte[] data, int start)
    {
        private long position = start * 8L;

        internal readonly bool AtEnd => position >= data.Length * 8L;
        internal readonly int BytePosition => (int)(position >> 3);

        internal void Skip(long bits)
        {
            position += bits;
            if (position > data.Length * 8L) throw new IndexOutOfRangeException();
        }

        internal void Align() => position = (position + 7) & ~7L;

        internal uint Read(int count)
        {
            if (count == 0) return 0;
            if (position + count > data.Length * 8L) throw new IndexOutOfRangeException();
            ulong value = 0;
            var index = (int)(position >> 3);
            var offset = (int)(position & 7);
            var needed = offset + count;
            var bytes = (needed + 7) >> 3;
            for (var i = 0; i < bytes; i++) value = (value << 8) | data[index + i];
            value >>= bytes * 8 - needed;
            position += count;
            return (uint)(value & (count == 32 ? uint.MaxValue : (1UL << count) - 1));
        }

        internal int ReadSigned(int count)
        {
            if (count == 0) return 0;
            var value = Read(count);
            var shift = 32 - count;
            return (int)(value << shift) >> shift;
        }

        internal uint ReadUnary()
        {
            uint zeros = 0;
            while (true)
            {
                var index = (int)(position >> 3);
                if (index >= data.Length) throw new IndexOutOfRangeException();
                var offset = (int)(position & 7);
                var current = (byte)(data[index] << offset);
                if (current == 0)
                {
                    zeros += (uint)(8 - offset);
                    position += 8 - offset;
                    continue;
                }
                var leading = BitOperations.LeadingZeroCount((uint)current) - 24;
                zeros += (uint)leading;
                position += leading + 1;
                return zeros;
            }
        }
    }
}
