using System.Buffers.Binary;
using Martlet.Core.Audio;
using Martlet.Core.Singing;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace Martlet.Audio.Windows;

/// <summary>Turns a song a cloud provider made (MP3, FLAC, WAV or raw PCM) into the 48 kHz stereo 16-bit track Singing works with
/// (<see cref="SongTrack"/>), or a provider's mono vocals into 48 kHz mono. FLAC is decoded by Martlet itself (<see cref="FlacCodec"/>),
/// PCM WAV by NAudio's managed reader, anything else by Windows (Media Foundation). Only what the owner's own provider sent is
/// decoded, on the PC that asked for it; the result is resampled to 48 kHz when needed. Songs are at most 200 seconds.</summary>
public static class SongAudioImport
{
    /// <summary>The longest song Singing keeps (a provider may give a little more than it was asked for).</summary>
    public static readonly TimeSpan MaximumLength = TimeSpan.FromSeconds(200);

    /// <summary>Decodes <paramref name="audio"/> to a 48 kHz track with <paramref name="channels"/> channels (1 or 2).
    /// <paramref name="rawPcmRate"/>: the sample rate of raw mono 16-bit PCM without a header (ElevenLabs' <c>pcm_*</c> formats);
    /// null for a file with a header. Throws <see cref="SongException"/> (<c>song.failed</c>) when it can't be decoded. Blocks:
    /// call it off the UI thread.</summary>
    public static SongTrack Decode(ReadOnlyMemory<byte> audio, int channels, SongTrackKind kind, int? rawPcmRate = null)
    {
        if (channels is not (1 or 2)) throw new ArgumentOutOfRangeException(nameof(channels));
        if (audio.IsEmpty) throw new SongException(SongErrorCodes.Failed, "The cloud provider sent no audio.");
        float[] samples;
        int rate, sourceChannels;
        try
        {
            if (rawPcmRate is { } pcmRate)
            {
                (samples, rate, sourceChannels) = (Pcm16ToFloat(audio.Span), pcmRate, 1);
            }
            else if (FlacCodec.IsFlac(audio.Span))
            {
                var flac = FlacCodec.Decode(audio.Span);
                (samples, rate, sourceChannels) = (Pcm16ToFloat(flac.Pcm16), flac.SampleRate, flac.Channels);
            }
            else
            {
                using var reader = Open(audio);
                (samples, rate, sourceChannels) = ReadAll(reader.ToSampleProvider(), reader.WaveFormat);
            }
        }
        catch (Exception error) when (error is not SongException and not OutOfMemoryException)
        {
            throw new SongException(SongErrorCodes.Failed, $"The cloud provider's audio couldn't be decoded ({error.GetType().Name}).", error);
        }
        if (sourceChannels < 1 || rate is < 8_000 or > 384_000 || samples.Length < sourceChannels)
            throw new SongException(SongErrorCodes.Failed, "The cloud provider's audio is empty or in a shape Martlet can't use.");
        var frames = samples.Length / sourceChannels;
        // Down or up to the channels asked for: the mean for mono, the first two (or a copy of one) for stereo.
        var shaped = new float[frames * channels];
        for (var i = 0; i < frames; i++)
        {
            if (channels == 1)
            {
                float sum = 0;
                for (var c = 0; c < sourceChannels; c++) sum += samples[i * sourceChannels + c];
                shaped[i] = sum / sourceChannels;
            }
            else
            {
                shaped[2 * i] = samples[i * sourceChannels];
                shaped[2 * i + 1] = samples[i * sourceChannels + (sourceChannels > 1 ? 1 : 0)];
            }
        }
        if (rate != SongTrack.SampleRateHz) shaped = Resample(shaped, frames, channels, rate, SongTrack.SampleRateHz);
        var maximum = (int)(MaximumLength.TotalSeconds * SongTrack.SampleRateHz) * channels;
        if (shaped.Length > maximum) shaped = shaped[..maximum];
        var pcm = new byte[shaped.Length / channels * channels * 2];
        for (var i = 0; i < pcm.Length / 2; i++)
        {
            var sample = shaped[i];
            BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan(2 * i),
                float.IsFinite(sample) ? (short)Math.Round(Math.Clamp(sample, -1f, 1f) * short.MaxValue) : (short)0);
        }
        return new SongTrack(kind, SongTrack.SampleRateHz, channels, pcm);
    }

    /// <summary>A 48 kHz mono track as 16-bit PCM WAV (to send a song's vocals to a provider).</summary>
    public static byte[] Wave(SongTrack track)
    {
        ArgumentNullException.ThrowIfNull(track);
        return track.ToWave();
    }

    /// <summary>A voice's recording (a mono 16-bit PCM WAV) as the MP3 sample a provider's vocal clone wants (Mureka: 15-30 seconds):
    /// the recording repeated, with a quarter second between, until it lasts at least <paramref name="seconds"/>; encoded by Windows.</summary>
    public static byte[] VoiceSampleMp3(ReadOnlyMemory<byte> recordingWav, double seconds = 16)
    {
        using var reader = new WaveFileReader(new MemoryStream(recordingWav.ToArray(), writable: false));
        var format = reader.WaveFormat;
        if (format.Encoding != WaveFormatEncoding.Pcm || format.BitsPerSample != 16 || format.Channels != 1)
            throw new SongException(SongErrorCodes.VoiceMissing, "The voice's recording isn't a mono 16-bit WAV.");
        using var recording = new MemoryStream();
        reader.CopyTo(recording);
        var once = recording.ToArray();
        if (once.Length < 2) throw new SongException(SongErrorCodes.VoiceMissing, "The voice's recording is empty.");
        var gap = new byte[format.SampleRate / 2 & ~1];
        using var looped = new MemoryStream();
        while (looped.Length < (long)(seconds * format.AverageBytesPerSecond))
        {
            looped.Write(once);
            looped.Write(gap);
        }
        NAudio.MediaFoundation.MediaFoundationApi.Startup();
        using var source = new RawSourceWaveStream(new MemoryStream(looped.ToArray()), format);
        using var output = new MemoryStream();
        MediaFoundationEncoder.EncodeToMp3(source, output, 128_000);
        return output.ToArray();
    }

    private static WaveStream Open(ReadOnlyMemory<byte> audio)
    {
        var span = audio.Span;
        if (span.Length >= 12 && span[..4].SequenceEqual("RIFF"u8) && span[8..12].SequenceEqual("WAVE"u8))
        {
            WaveFileReader? wave = null;
            try
            {
                wave = new WaveFileReader(new MemoryStream(audio.ToArray(), writable: false));
                if (wave.WaveFormat.Encoding is WaveFormatEncoding.Pcm or WaveFormatEncoding.IeeeFloat || wave.WaveFormat is WaveFormatExtensible)
                    return wave;
            }
            catch (Exception error) when (error is FormatException or ArgumentException or InvalidOperationException or EndOfStreamException) { }
            wave?.Dispose();
        }
        return new StreamMediaFoundationReader(new MemoryStream(audio.ToArray(), writable: false),
            new MediaFoundationReader.MediaFoundationReaderSettings { RequestFloatOutput = true });
    }

    private static (float[] Samples, int Rate, int Channels) ReadAll(ISampleProvider provider, WaveFormat format)
    {
        var channels = format.Channels;
        var limit = (long)(MaximumLength.TotalSeconds + 5) * format.SampleRate * channels;
        var buffer = new List<float>(format.SampleRate * channels * 60);
        var chunk = new float[format.SampleRate * channels];
        int read;
        while ((read = provider.Read(chunk.AsSpan())) > 0 && buffer.Count < limit) buffer.AddRange(chunk.AsSpan(0, read));
        return ([.. buffer], format.SampleRate, channels);
    }

    private static float[] Pcm16ToFloat(ReadOnlySpan<byte> pcm)
    {
        var samples = new float[pcm.Length / 2];
        for (var i = 0; i < samples.Length; i++) samples[i] = BinaryPrimitives.ReadInt16LittleEndian(pcm[(2 * i)..]) / 32768f;
        return samples;
    }

    private static float[] Resample(float[] interleaved, int frames, int channels, int from, int to)
    {
        var resampler = new WdlResamplingSampleProvider(new Interleaved(interleaved, from, channels), to);
        var expected = (int)Math.Ceiling((long)frames * to / (double)from) * channels;
        var output = new float[expected + 4096 * channels];
        var count = 0;
        while (count < output.Length)
        {
            var read = resampler.Read(output.AsSpan(count, Math.Min(4096 * channels, output.Length - count)));
            if (read <= 0) break;
            count += read;
        }
        return output[..Math.Min(count, expected)];
    }

    private sealed class Interleaved(float[] samples, int rate, int channels) : ISampleProvider
    {
        private int position;

        public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(rate, channels);

        public int Read(Span<float> buffer)
        {
            var read = Math.Min(buffer.Length, samples.Length - position);
            samples.AsSpan(position, read).CopyTo(buffer);
            position += read;
            return read;
        }
    }
}
