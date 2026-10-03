using System.Buffers.Binary;
using Concentus.Structs;
using NVorbis;

namespace Martlet.Audio.Windows;

/// <summary>Decoded sound, read as interleaved float samples.</summary>
internal interface IDecodedAudio : IDisposable
{
    int SampleRate { get; }
    int Channels { get; }
    /// <summary>How long the file says it is (null when it doesn't say).</summary>
    TimeSpan? Length { get; }
    /// <summary>The rate the sound was recorded at when the codec always decodes at a higher one (Opus decodes at 48 kHz),
    /// else null.</summary>
    int? RecordedRate => null;
    /// <summary>Reads whole frames into <paramref name="buffer"/>; 0 at the end.</summary>
    int Read(Span<float> buffer);
}

/// <summary>The sound in an Ogg file (.ogg, .oga, .opus): which codec its first audio stream uses.</summary>
internal enum OggCodec { Vorbis, Opus, Other }

/// <summary>Reads Ogg Vorbis and Ogg Opus recordings in managed code, on this PC only: NVorbis for Vorbis and Concentus' managed
/// Opus decoder for Opus (never a native opus.dll). A desktop app can't rely on Windows for Ogg: Media Foundation has no Ogg
/// reader of its own, and the Web Media Extensions' reader serves Store apps. Any damage in the file surfaces as
/// <see cref="InvalidDataException"/>.</summary>
internal static class OggAudio
{
    private const int MaximumPacketBytes = 1 << 20;

    /// <summary>The codec of the first Vorbis or Opus stream in the Ogg file at <paramref name="path"/> (its stream serial in
    /// <paramref name="serial"/>), or <see cref="OggCodec.Other"/> when it has neither.</summary>
    internal static OggCodec Identify(string path, out int serial)
    {
        serial = 0;
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        // Every stream starts with a beginning-of-stream page carrying its codec's first header; they all come first.
        for (var pages = 0; pages < 16; pages++)
        {
            if (OggPage.Read(stream) is not { Beginning: true } page) break;
            var body = page.Body;
            if (body.Length >= 7 && body[0] == 1 && body.AsSpan(1, 6).SequenceEqual("vorbis"u8))
            {
                serial = page.Serial;
                return OggCodec.Vorbis;
            }
            if (body.Length >= 19 && body.AsSpan(0, 8).SequenceEqual("OpusHead"u8))
            {
                serial = page.Serial;
                return OggCodec.Opus;
            }
        }
        return OggCodec.Other;
    }

    internal static bool IsOgg(ReadOnlySpan<byte> header) => header.StartsWith("OggS"u8);

    internal static IDecodedAudio Open(string path, OggCodec codec, int serial) => codec switch
    {
        OggCodec.Vorbis => new VorbisAudio(path),
        OggCodec.Opus => new OpusAudio(path, serial),
        _ => throw new NotSupportedException("Martlet reads Ogg files with Vorbis or Opus sound.")
    };

    private static InvalidDataException Damaged(string codec, Exception inner) =>
        new($"The {codec} sound in this file couldn't be decoded.", inner);

    private static bool Translatable(Exception error) =>
        error is not (OutOfMemoryException or OperationCanceledException or InvalidDataException or EndOfStreamException);

    /// <summary>One Ogg page: its flags, granule position, stream serial and body (the packet segments, joined).</summary>
    private readonly record struct OggPage(byte Flags, long Granule, int Serial, byte[] Body, byte[] Lacing)
    {
        internal bool Continued => (Flags & 1) != 0;
        internal bool Beginning => (Flags & 2) != 0;

        /// <summary>The next page, or null at the end of the file (a cut-off last page ends it too).</summary>
        internal static OggPage? Read(Stream stream)
        {
            Span<byte> header = stackalloc byte[27];
            var read = stream.ReadAtLeast(header, header.Length, throwOnEndOfStream: false);
            if (read == 0) return null;
            if (read < header.Length) return null;
            if (!header[..4].SequenceEqual("OggS"u8) || header[4] != 0) throw new InvalidDataException("This isn't an Ogg page.");
            var lacing = new byte[header[26]];
            if (stream.ReadAtLeast(lacing, lacing.Length, throwOnEndOfStream: false) < lacing.Length) return null;
            var size = 0;
            foreach (var value in lacing) size += value;
            var body = new byte[size];
            if (stream.ReadAtLeast(body, body.Length, throwOnEndOfStream: false) < body.Length) return null;
            return new OggPage(header[5], BinaryPrimitives.ReadInt64LittleEndian(header[6..]),
                BinaryPrimitives.ReadInt32LittleEndian(header[14..]), body, lacing);
        }
    }

    /// <summary>The packets of one Ogg stream, in order. Pages of other streams are skipped; a packet whose continuation was
    /// lost is dropped.</summary>
    private sealed class OggPackets(Stream stream, int serial)
    {
        private readonly Queue<byte[]> ready = new();
        private readonly MemoryStream partial = new();

        internal byte[]? Next()
        {
            while (ready.Count == 0)
            {
                if (OggPage.Read(stream) is not { } page) return null;
                if (page.Serial != serial) continue;
                if (!page.Continued) partial.SetLength(0);
                var offset = 0;
                foreach (var value in page.Lacing)
                {
                    partial.Write(page.Body, offset, value);
                    offset += value;
                    if (partial.Length > MaximumPacketBytes) throw new InvalidDataException("An Ogg packet is too large.");
                    if (value < 255)
                    {
                        ready.Enqueue(partial.ToArray());
                        partial.SetLength(0);
                    }
                }
            }
            return ready.Dequeue();
        }
    }

    private sealed class VorbisAudio : IDecodedAudio
    {
        private readonly VorbisReader reader;
        private bool ended;

        internal VorbisAudio(string path)
        {
            var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            try
            {
                reader = new VorbisReader(stream, closeOnDispose: true);
                SampleRate = reader.SampleRate;
                Channels = reader.Channels;
            }
            catch (Exception error) when (Translatable(error))
            {
                stream.Dispose();
                throw Damaged("Vorbis", error);
            }
            catch
            {
                stream.Dispose();
                throw;
            }
        }

        public int SampleRate { get; }
        public int Channels { get; }

        public TimeSpan? Length
        {
            get
            {
                try { return reader.TotalTime > TimeSpan.Zero ? reader.TotalTime : null; }
                catch (Exception error) when (Translatable(error)) { return null; }
            }
        }

        public int Read(Span<float> buffer)
        {
            if (ended) return 0;
            try
            {
                var read = reader.ReadSamples(buffer[..(buffer.Length / Channels * Channels)]);
                if (read == 0) ended = true;
                return read / Channels * Channels;
            }
            catch (Exception error) when (Translatable(error)) { throw Damaged("Vorbis", error); }
        }

        public void Dispose() => reader.Dispose();
    }

    /// <summary>Ogg Opus (RFC 7845), decoded at Opus' own 48 kHz (Concentus' reduced-rate output garbles some streams) without
    /// the encoder's pre-skip and the padding after the last granule, with the header's output gain.</summary>
    private sealed class OpusAudio : IDecodedAudio
    {
        private const int Rate = 48_000;
        private const int MaximumFrame = Rate * 120 / 1000;
        private readonly FileStream stream;
        private readonly OggPackets packets;
        private readonly OpusDecoder? single;
        private readonly OpusMSDecoder? multiple;
        private readonly float gain;
        private readonly float[] decoded;
        private int start, end;
        private long skip;
        private long remaining = long.MaxValue;

        internal OpusAudio(string path, int serial)
        {
            stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            try
            {
                packets = new OggPackets(stream, serial);
                var head = packets.Next() ?? throw new InvalidDataException("This Opus file has no header.");
                if (head.Length < 19 || !head.AsSpan(0, 8).SequenceEqual("OpusHead"u8) || head[8] >> 4 != 0)
                    throw new InvalidDataException("This Opus file's header isn't one Martlet reads.");
                Channels = head[9];
                var preSkip = BinaryPrimitives.ReadUInt16LittleEndian(head.AsSpan(10));
                var inputRate = BinaryPrimitives.ReadUInt32LittleEndian(head.AsSpan(12));
                var outputGain = BinaryPrimitives.ReadInt16LittleEndian(head.AsSpan(16));
                var family = head[18];
                if (Channels == 0) throw new InvalidDataException("This Opus file has no channels.");
                RecordedRate = inputRate is > 0 and < Rate ? (int)inputRate : null;
                // Concentus' managed decoders directly: its factory may load a native opus.dll found on this PC instead.
#pragma warning disable CS0618
                if (family == 0)
                {
                    if (Channels > 2) throw new InvalidDataException("This Opus file's channel layout isn't one Martlet reads.");
                    single = new OpusDecoder(Rate, Channels);
                }
                else
                {
                    if (head.Length < 21 + Channels) throw new InvalidDataException("This Opus file's channel layout is incomplete.");
                    var streams = head[19];
                    var coupled = head[20];
                    if (streams == 0 || coupled > streams || streams + coupled > 255)
                        throw new InvalidDataException("This Opus file's channel layout isn't one Martlet reads.");
                    multiple = new OpusMSDecoder(Rate, Channels, streams, coupled, head.AsSpan(21, Channels).ToArray());
                }
#pragma warning restore CS0618
                gain = outputGain == 0 ? 1f : MathF.Pow(10f, outputGain / (20f * 256f));
                skip = preSkip;
                if (LastGranule(stream, serial) is { } last && last > preSkip)
                {
                    remaining = last - preSkip;
                    Length = TimeSpan.FromSeconds(remaining / (double)Rate);
                }
                stream.Position = 0;
                packets = new OggPackets(stream, serial);
                // The identification header, then the comment header; sound starts with the third packet.
                for (var header = 0; header < 2; header++)
                    if (packets.Next() is null) throw new InvalidDataException("This Opus file has no sound.");
                decoded = new float[MaximumFrame * Channels];
            }
            catch (Exception error) when (Translatable(error))
            {
                stream.Dispose();
                throw Damaged("Opus", error);
            }
            catch
            {
                stream.Dispose();
                throw;
            }
        }

        public int SampleRate => Rate;
        public int Channels { get; }
        public TimeSpan? Length { get; }
        /// <summary>The rate the encoder was given, when lower than 48 kHz (a voice message's 16 kHz, say): nothing above it was
        /// recorded, so Martlet keeps the recording at the accepted rate nearest it.</summary>
        public int? RecordedRate { get; }

        public int Read(Span<float> buffer)
        {
            var written = 0;
            var room = buffer.Length / Channels * Channels;
            while (written < room && remaining > 0)
            {
                if (start == end && !DecodeNext()) break;
                var frames = Math.Min(Math.Min((end - start) / Channels, (room - written) / Channels), remaining);
                var count = (int)frames * Channels;
                decoded.AsSpan(start, count).CopyTo(buffer[written..]);
                start += count;
                written += count;
                remaining -= frames;
            }
            return written;
        }

        /// <summary>Decodes the next packet into <see cref="decoded"/>, dropping what the pre-skip still covers. False at the end.</summary>
        private bool DecodeNext()
        {
            while (true)
            {
                var packet = packets.Next();
                if (packet is null) return false;
                if (packet.Length == 0) continue;
                int frames;
                try
                {
                    frames = single is not null
                        ? single.Decode(packet, decoded, MaximumFrame, false)
                        : multiple!.DecodeMultistream(packet, decoded, MaximumFrame, false);
                }
                catch (Exception error) when (Translatable(error)) { throw Damaged("Opus", error); }
                if (frames <= 0) continue;
                (start, end) = (0, frames * Channels);
                if (gain != 1f)
                    for (var i = 0; i < end; i++) decoded[i] *= gain;
                if (skip > 0)
                {
                    var dropped = (int)Math.Min(skip, frames);
                    skip -= dropped;
                    start = dropped * Channels;
                }
                if (start < end) return true;
            }
        }

        /// <summary>The granule position of the stream's last page (where its sound ends), from the end of the file.</summary>
        private static long? LastGranule(FileStream stream, int serial)
        {
            const int Tail = 128 * 1024;
            var length = stream.Length;
            var from = Math.Max(0, length - Tail);
            var tail = new byte[length - from];
            stream.Position = from;
            stream.ReadExactly(tail);
            for (var at = tail.Length - 27; at >= 0; at--)
            {
                if (!tail.AsSpan(at, 4).SequenceEqual("OggS"u8) || tail[at + 4] != 0) continue;
                if (BinaryPrimitives.ReadInt32LittleEndian(tail.AsSpan(at + 14)) != serial) continue;
                var granule = BinaryPrimitives.ReadInt64LittleEndian(tail.AsSpan(at + 6));
                if (granule >= 0) return granule;
            }
            return null;
        }

        public void Dispose() => stream.Dispose();
    }
}
