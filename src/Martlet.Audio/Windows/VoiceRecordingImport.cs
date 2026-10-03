using System.Buffers.Binary;
using System.Globalization;
using System.Runtime.InteropServices;
using Martlet.Core.Contracts;
using Martlet.Core.Voices;
using NAudio.Wave;

namespace Martlet.Audio.Windows;

/// <summary>A recording ready to become a voice: the exact mono 16-bit PCM WAV Martlet keeps (<see cref="Wave"/>), what the
/// owner chose (<see cref="SourceFormat"/>, usually its file extension) and whether Martlet converted it.</summary>
public sealed record VoiceRecording(byte[] Wave, string SourceFormat, int SourceChannels, int SourceSampleRate, int SampleRate,
    int DurationMilliseconds, bool Converted)
{
    public string Describe()
    {
        var seconds = (DurationMilliseconds / 1000d).ToString("0.0", CultureInfo.InvariantCulture);
        var kept = Converted
            ? $"Martlet converts it to a mono 16-bit WAV at {VoiceRecordingImport.Rate(SampleRate)}."
            : "It is already a WAV Martlet can use as it is.";
        return $"{SourceFormat}, {seconds} seconds. {kept}";
    }

    public override string ToString() =>
        $"Voice recording {{ SourceFormat = {SourceFormat}, SampleRate = {SampleRate}, DurationMilliseconds = {DurationMilliseconds}, Converted = {Converted}, audio = omitted }}";
}

/// <summary>Why a recording can't become a voice, in words for the owner (never the file's path or name).</summary>
public sealed class VoiceRecordingException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>Turns almost any audio or video file the owner chooses into the narrow WAV every voice engine and paired computer
/// accepts (<see cref="PcmWaveInfo"/>: mono 16-bit PCM at 16, 22.05, 24, 44.1 or 48 kHz, from half a second, the shortest
/// recording one of several may be, to 30 seconds; a voice from one recording needs at least 1 second). A WAV that is
/// already acceptable is kept byte for byte. Ogg Vorbis and Ogg Opus (.ogg, .oga, .opus: voice messages, for example) are
/// decoded by Martlet itself (<see cref="OggAudio"/>), since Windows reads no Ogg in a desktop app. Anything else is decoded on
/// this PC by Windows (Media Foundation: MP3, M4A/AAC, FLAC, WMA, ALAC, WebM where Windows has those codecs, the sound of
/// MP4/MOV/MKV videos, and WAV in any PCM or float layout; AIFF through NAudio's reader), mixed to mono and resampled when its
/// rate isn't one of the accepted ones. Only the
/// resulting PCM WAV is stored or shared: hosts and the gateway still accept nothing else, so no codec runs on audio another
/// computer sent. Decoding stops just past the 30-second limit, so a long file is refused without decoding all of it.</summary>
public static class VoiceRecordingImport
{
    public static readonly IReadOnlyList<int> SampleRates = [16_000, 22_050, 24_000, 44_100, 48_000];

    /// <summary>The open-file dialog filter: common audio and video files first, then any file Windows may still read.</summary>
    public const string DialogFilter =
        "Audio and video files|*.wav;*.mp3;*.m4a;*.aac;*.flac;*.ogg;*.oga;*.opus;*.wma;*.webm;*.weba;*.mp4;*.m4v;*.mov;*.mkv;" +
        "*.avi;*.wmv;*.3gp;*.3g2;*.amr;*.ac3;*.ec3;*.aif;*.aiff;*.aifc;*.caf;*.adts|All files (*.*)|*.*";

    private const int MinimumSourceRate = 4_000;
    private const int MaximumSourceRate = 384_000;
    private const int MaximumSourceChannels = 32;

    /// <summary>Reads <paramref name="path"/> and returns the WAV Martlet keeps. Throws <see cref="VoiceRecordingException"/>
    /// when it can't be used. Blocks while decoding: call it off the UI thread.</summary>
    public static VoiceRecording Prepare(string path, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        string full;
        try { full = Path.GetFullPath(path); }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new VoiceRecordingException("That isn't a file path. Choose the recording again.", error);
        }
        var kind = Kind(full);
        try
        {
            var file = new FileInfo(full);
            if (!file.Exists) throw new VoiceRecordingException("There is no recording at that path. Choose it again.");
            if (file.Length == 0) throw new VoiceRecordingException("This file is empty.");
            if (file.Length <= SpeakingVoiceLibrary.MaximumAudioBytes)
            {
                var bytes = File.ReadAllBytes(full);
                if (Usable(bytes) is { } wave)
                    return new(bytes, kind, 1, wave.SampleRate, wave.SampleRate, wave.DurationMilliseconds, false);
            }
            cancellationToken.ThrowIfCancellationRequested();
            return Decode(full, kind, cancellationToken);
        }
        catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException)
        {
            throw new VoiceRecordingException("There is no recording at that path. Choose it again.", error);
        }
        catch (UnauthorizedAccessException error)
        {
            throw new VoiceRecordingException("Martlet isn't allowed to open this file.", error);
        }
        catch (IOException error)
        {
            // An IOException's message usually names the file, and this text is shown and returned to automation.
            throw new VoiceRecordingException("Couldn't read this file. Close any app that is using it and try again.", error);
        }
    }

    /// <summary>A sample rate in words, for example "44.1 kHz".</summary>
    public static string Rate(int sampleRate) =>
        (sampleRate / 1000d).ToString("0.###", CultureInfo.InvariantCulture) + " kHz";

    /// <summary>The accepted rate a recording at <paramref name="sourceRate"/> is kept at: its own when accepted, else the
    /// lowest accepted rate above it (so nothing it holds is lost), at most 48 kHz.</summary>
    public static int TargetRate(int sourceRate) =>
        SampleRates.Contains(sourceRate) ? sourceRate : SampleRates.FirstOrDefault(rate => rate >= sourceRate, 48_000);

    private static PcmWaveInfo? Usable(byte[] bytes)
    {
        try
        {
            var wave = PcmWaveInfo.Inspect(bytes, SpeakingVoiceLibrary.MaximumAudioBytes);
            return wave.DurationMilliseconds is >= SpeakingVoiceLibrary.MinimumClipMilliseconds and
                <= SpeakingVoiceLibrary.MaximumDurationMilliseconds ? wave : null;
        }
        catch (ContractException) { return null; }
    }

    private static string Kind(string path)
    {
        var extension = Path.GetExtension(path).TrimStart('.');
        return extension.Length is > 0 and <= 8 && extension.All(char.IsAsciiLetterOrDigit)
            ? extension.ToUpperInvariant()
            : "Audio";
    }

    private static VoiceRecording Decode(string path, string kind, CancellationToken cancellationToken)
    {
        float[] mono;
        int frames, rate, channels, recorded;
        TimeSpan? reported;
        OggCodec? ogg = null;
        try
        {
            using var reader = Open(path, ref kind, out ogg);
            rate = reader.SampleRate;
            channels = reader.Channels;
            if (rate is < MinimumSourceRate or > MaximumSourceRate || channels is < 1 or > MaximumSourceChannels)
                throw new VoiceRecordingException($"Martlet can't use sound at {Rate(rate)} with {channels} channels.");
            recorded = reader.RecordedRate is { } lower and >= MinimumSourceRate and < MaximumSourceRate ? Math.Min(lower, rate) : rate;
            reported = reader.Length;
            var limit = checked((int)((long)rate * SpeakingVoiceLibrary.MaximumDurationMilliseconds / 1000));
            mono = new float[limit + 1];
            var buffer = new float[channels * 4096];
            frames = 0;
            while (frames <= limit)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var read = reader.Read(buffer.AsSpan(0, Math.Min(buffer.Length, (limit + 1 - frames) * channels)));
                if (read <= 0) break;
                var whole = read / channels;
                for (var frame = 0; frame < whole; frame++)
                {
                    var sum = 0f;
                    for (var channel = 0; channel < channels; channel++) sum += buffer[frame * channels + channel];
                    mono[frames + frame] = sum / channels;
                }
                frames += whole;
            }
            if (frames > limit)
                throw new VoiceRecordingException(reported is { } length && length.TotalMilliseconds > SpeakingVoiceLibrary.MaximumDurationMilliseconds
                    ? $"This recording is {Length(length)} long. A voice takes at most 30 seconds: choose or cut a shorter clip."
                    : "This recording is longer than 30 seconds. A voice takes at most 30 seconds: choose or cut a shorter clip.");
        }
        catch (Exception error) when (error is COMException or InvalidOperationException or ArgumentException or FormatException or
            NotSupportedException or InvalidDataException or EndOfStreamException or InvalidCastException or OverflowException)
        {
            throw new VoiceRecordingException(ogg switch
            {
                OggCodec.Vorbis or OggCodec.Opus =>
                    $"Martlet couldn't decode the {ogg} sound in this file. It may be damaged or cut short: try another copy or format.",
                OggCodec.Other =>
                    "This Ogg file holds a kind of sound Martlet can't read. Ogg files with Vorbis or Opus sound work, as do MP3, M4A, WAV and FLAC.",
                _ => "Windows can't read sound from this file. Try an MP3, M4A, WAV, FLAC or OGG recording, or a video with sound."
            }, error);
        }

        var milliseconds = frames == 0 ? 0 : (int)Math.Ceiling(frames * 1000d / rate);
        if (milliseconds < SpeakingVoiceLibrary.MinimumClipMilliseconds)
            throw new VoiceRecordingException(frames == 0
                ? ogg is null ? "Windows found no sound in this file." : "Martlet found no sound in this file. It may be cut short."
                : $"This recording is only {(milliseconds / 1000d).ToString("0.0", CultureInfo.InvariantCulture)} seconds long. " + TooShort);

        // Kept at the accepted rate nearest what was recorded: a voice message Opus decodes at 48 kHz stays at 16 kHz.
        var target = TargetRate(recorded);
        ReadOnlySpan<float> output = target == rate ? mono.AsSpan(0, frames) : Resample(mono, frames, rate, target);
        var wave = Wave(output, target);
        if (!wave.AsSpan(44).ContainsAnyExcept((byte)0))
            throw new VoiceRecordingException("This recording is silent. Choose one where the voice can be heard.");
        var info = Usable(wave) ?? throw new VoiceRecordingException("This recording is too short. " + TooShort);
        return new(wave, kind, channels, recorded, target, info.DurationMilliseconds, true);
    }

    private const string TooShort = "A recording needs at least half a second, and a voice from one recording at least 1 second.";

    private static IDecodedAudio Open(string path, ref string kind, out OggCodec? ogg)
    {
        ogg = null;
        Span<byte> header = stackalloc byte[12];
        int length;
        using (var probe = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            length = probe.ReadAtLeast(header, header.Length, throwOnEndOfStream: false);
        header = header[..length];
        // Ogg Vorbis and Opus are decoded in managed code (Windows reads no Ogg in a desktop app); any other Ogg sound still
        // goes to Windows, in case this PC has a decoder for it.
        if (OggAudio.IsOgg(header))
        {
            ogg = OggAudio.Identify(path, out var serial);
            if (ogg is OggCodec.Vorbis or OggCodec.Opus)
            {
                var codec = ogg.ToString()!;
                if (!kind.Equals(codec, StringComparison.OrdinalIgnoreCase)) kind = $"{kind} ({codec})";
                return OggAudio.Open(path, ogg.Value, serial);
            }
        }
        // Uncompressed WAV and AIFF are parsed by NAudio's managed readers (no codec); a compressed WAV or anything else goes
        // to Windows' own decoders.
        if (length == 12 && header[..4].SequenceEqual("FORM"u8) && (header[8..].SequenceEqual("AIFF"u8) || header[8..].SequenceEqual("AIFC"u8)))
            return new WaveStreamAudio(new AiffFileReader(path));
        if (length == 12 && header[..4].SequenceEqual("RIFF"u8) && header[8..].SequenceEqual("WAVE"u8))
        {
            WaveFileReader? wave = null;
            try
            {
                wave = new WaveFileReader(path);
                if (wave.WaveFormat.Encoding is WaveFormatEncoding.Pcm or WaveFormatEncoding.IeeeFloat ||
                    wave.WaveFormat is WaveFormatExtensible)
                {
                    _ = wave.ToSampleProvider();
                    return new WaveStreamAudio(wave);
                }
            }
            catch (Exception error) when (error is FormatException or ArgumentException or InvalidOperationException or
                InvalidDataException or EndOfStreamException) { }
            wave?.Dispose();
        }
        return new WaveStreamAudio(new MediaFoundationReader(path,
            new MediaFoundationReader.MediaFoundationReaderSettings { RequestFloatOutput = true }));
    }

    /// <summary>A file NAudio or Windows (Media Foundation) reads.</summary>
    private sealed class WaveStreamAudio : IDecodedAudio
    {
        private readonly WaveStream reader;
        private readonly ISampleProvider samples;

        internal WaveStreamAudio(WaveStream reader)
        {
            this.reader = reader;
            try { samples = reader.ToSampleProvider(); }
            catch
            {
                reader.Dispose();
                throw;
            }
        }

        public int SampleRate => reader.WaveFormat.SampleRate;
        public int Channels => reader.WaveFormat.Channels;
        public TimeSpan? Length => Reported(reader);
        public int Read(Span<float> buffer) => samples.Read(buffer);
        public void Dispose() => reader.Dispose();
    }

    private static TimeSpan? Reported(WaveStream reader)
    {
        try { return reader.TotalTime > TimeSpan.Zero ? reader.TotalTime : null; }
        catch (Exception error) when (error is InvalidOperationException or NotSupportedException or COMException or OverflowException)
        {
            return null;
        }
    }

    private static string Length(TimeSpan length) => length.TotalHours >= 1
        ? $"{(int)length.TotalHours} h {length.Minutes} min"
        : length.TotalMinutes >= 1
            ? $"{(int)length.TotalMinutes} min {length.Seconds} s"
            : $"{Math.Ceiling(length.TotalSeconds):0} seconds";

    private static float[] Resample(float[] mono, int frames, int from, int to)
    {
        var resampler = new NAudio.Wave.SampleProviders.WdlResamplingSampleProvider(new FloatSamples(mono, frames, from), to);
        var expected = (int)Math.Ceiling((long)frames * to / (double)from);
        var output = new float[expected + 4096];
        var count = 0;
        while (count < output.Length)
        {
            var read = resampler.Read(output.AsSpan(count, Math.Min(4096, output.Length - count)));
            if (read <= 0) break;
            count += read;
        }
        return output[..Math.Min(count, expected)];
    }

    private static byte[] Wave(ReadOnlySpan<float> samples, int rate)
    {
        var data = checked(samples.Length * 2);
        var wave = new byte[44 + data];
        "RIFF"u8.CopyTo(wave);
        BinaryPrimitives.WriteUInt32LittleEndian(wave.AsSpan(4), (uint)(36 + data));
        "WAVE"u8.CopyTo(wave.AsSpan(8));
        "fmt "u8.CopyTo(wave.AsSpan(12));
        BinaryPrimitives.WriteUInt32LittleEndian(wave.AsSpan(16), 16);
        BinaryPrimitives.WriteUInt16LittleEndian(wave.AsSpan(20), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(wave.AsSpan(22), 1);
        BinaryPrimitives.WriteInt32LittleEndian(wave.AsSpan(24), rate);
        BinaryPrimitives.WriteInt32LittleEndian(wave.AsSpan(28), rate * 2);
        BinaryPrimitives.WriteUInt16LittleEndian(wave.AsSpan(32), 2);
        BinaryPrimitives.WriteUInt16LittleEndian(wave.AsSpan(34), 16);
        "data"u8.CopyTo(wave.AsSpan(36));
        BinaryPrimitives.WriteUInt32LittleEndian(wave.AsSpan(40), (uint)data);
        for (var index = 0; index < samples.Length; index++)
        {
            var sample = samples[index];
            var value = float.IsFinite(sample) ? (short)Math.Round(Math.Clamp(sample, -1f, 1f) * short.MaxValue) : (short)0;
            BinaryPrimitives.WriteInt16LittleEndian(wave.AsSpan(44 + index * 2), value);
        }
        return wave;
    }

    private sealed class FloatSamples(float[] samples, int count, int rate) : ISampleProvider
    {
        private int position;

        public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(rate, 1);

        public int Read(Span<float> buffer)
        {
            var read = Math.Min(buffer.Length, count - position);
            samples.AsSpan(position, read).CopyTo(buffer);
            position += read;
            return read;
        }
    }
}
