using Martlet.Core.Contracts;

namespace Martlet.Core.Voices;

/// <summary>Several recordings joined into one voice: the joined recording (mono PCM16 WAV), its transcript and where each
/// recording lies in it.</summary>
public sealed record JoinedVoiceRecording(byte[] Wave, string Transcript, int SampleRate, int DurationMilliseconds,
    IReadOnlyList<SpeakingVoiceClip> Clips)
{
    public override string ToString() => $"{nameof(JoinedVoiceRecording)} {{ {Clips.Count} recordings, content omitted }}";
}

/// <summary>Makes one voice from several recordings. Engines that learn from several recordings (XTTS-v2, GPT-SoVITS) get each
/// one back from <see cref="SpeakingVoice.Clips"/>; every other engine hears the joined recording, where they follow one
/// another after a <see cref="SpeakingVoiceLibrary.ClipPauseMilliseconds"/> pause.</summary>
public static class SpeakingVoiceRecordings
{
    /// <summary>Joins <paramref name="recordings"/> (each a mono 16-bit PCM WAV with its exact words) in order, after a short
    /// pause each, at the highest sample rate among them (others are resampled). Throws <see cref="ContractException"/>
    /// naming the recording that is not a usable WAV, too short, or when together they are too long or too large.</summary>
    public static JoinedVoiceRecording Join(IReadOnlyList<(ReadOnlyMemory<byte> Wave, string Transcript)> recordings)
    {
        ArgumentNullException.ThrowIfNull(recordings);
        ContractRules.Require(recordings.Count is >= 2 and <= SpeakingVoiceLibrary.MaximumClips,
            $"Choose 2 to {SpeakingVoiceLibrary.MaximumClips} recordings for one voice.");
        var parts = new List<(short[] Samples, int Rate, string Transcript)>();
        for (var i = 0; i < recordings.Count; i++)
        {
            var (wave, said) = recordings[i];
            var words = said?.Trim() ?? "";
            ContractRules.Require(SpeakingVoiceLibrary.IsTranscript(words), $"Type exactly what recording {i + 1} says.");
            short[] samples;
            PcmWaveInfo info;
            try { samples = PcmWaveInfo.Samples(wave.Span, SpeakingVoiceLibrary.MaximumAudioBytes, out info); }
            catch (ContractException)
            {
                throw new ContractException(ErrorCode.AudioFormatUnsupported,
                    $"Recording {i + 1} isn't a usable WAV: use a mono 16-bit PCM WAV at 16, 22.05, 24, 44.1 or 48 kHz, up to 4 MB.");
            }
            ContractRules.Require(info.DurationMilliseconds >= SpeakingVoiceLibrary.MinimumClipMilliseconds,
                $"Recording {i + 1} is shorter than {SpeakingVoiceLibrary.MinimumClipMilliseconds / 1000d:0.#} seconds.",
                ErrorCode.AudioFormatUnsupported);
            parts.Add((samples, info.SampleRate, words));
        }

        var rate = parts.Max(p => p.Rate);
        var pause = (int)((long)rate * SpeakingVoiceLibrary.ClipPauseMilliseconds / 1000);
        var resampled = parts.Select(p => (Samples: Resample(p.Samples, p.Rate, rate), p.Transcript)).ToArray();
        var total = resampled.Sum(p => (long)p.Samples.Length) + (long)pause * (resampled.Length - 1);
        var duration = Math.Ceiling(total * 1000d / rate);
        ContractRules.Require(duration <= SpeakingVoiceLibrary.MaximumDurationMilliseconds && 44 + total * 2 <= SpeakingVoiceLibrary.MaximumAudioBytes,
            $"Together the recordings are {duration / 1000:0.#} seconds with the pauses between them; keep them to " +
            $"{SpeakingVoiceLibrary.MaximumDurationMilliseconds / 1000} seconds in all.", ErrorCode.PayloadTooLarge);

        var joined = new short[total];
        var clips = new List<SpeakingVoiceClip>();
        var at = 0;
        foreach (var (samples, words) in resampled)
        {
            if (clips.Count > 0) at += pause;
            samples.CopyTo(joined, at);
            clips.Add(new() { Transcript = words, StartSample = at, SampleCount = samples.Length });
            at += samples.Length;
        }
        var transcript = SpeakingVoiceLibrary.JoinedTranscript(clips);
        ContractRules.Require(SpeakingVoiceLibrary.IsTranscript(transcript),
            "Together the transcripts are too long (at most 4,096 characters).", ErrorCode.PayloadTooLarge);
        return new(PcmWaveInfo.Write(rate, joined), transcript, rate, (int)duration, clips);
    }

    /// <summary>Whether <paramref name="voice"/>'s clips lie within <paramref name="recording"/> (its joined recording).</summary>
    public static bool Fits(SpeakingVoice voice, PcmWaveInfo recording) =>
        voice is { Clips: { Count: > 1 } clips, SampleRate: int rate } && rate == recording.SampleRate &&
        (long)clips[^1].StartSample + clips[^1].SampleCount <= recording.SampleCount;

    /// <summary>Band-limited (windowed-sinc) resampling of mono PCM16 from <paramref name="from"/> Hz to <paramref name="to"/> Hz.</summary>
    public static short[] Resample(short[] input, int from, int to)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (from == to || input.Length == 0) return input;
        const int Zeros = 16;
        var cutoff = Math.Min(1d, (double)to / from);
        var half = (int)Math.Ceiling(Zeros / cutoff);
        var output = new short[(int)((long)input.Length * to / from)];
        for (var i = 0; i < output.Length; i++)
        {
            var position = (double)i * from / to;
            var center = (int)Math.Floor(position);
            var sum = 0d;
            for (var k = Math.Max(0, center - half + 1); k <= Math.Min(input.Length - 1, center + half); k++)
            {
                var x = (position - k) * cutoff;
                if (Math.Abs(x) >= Zeros) continue;
                var sinc = x == 0 ? 1d : Math.Sin(Math.PI * x) / (Math.PI * x);
                var window = 0.42 + 0.5 * Math.Cos(Math.PI * x / Zeros) + 0.08 * Math.Cos(2 * Math.PI * x / Zeros);
                sum += input[k] * sinc * window * cutoff;
            }
            output[i] = (short)Math.Clamp(Math.Round(sum), short.MinValue, short.MaxValue);
        }
        return output;
    }
}
