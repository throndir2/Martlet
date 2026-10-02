using System.Diagnostics;
using Martlet.Avatar.Audio2Face;
using Martlet.Core.Audio;
using Martlet.Core.Contracts;

namespace Martlet.Mcp;

/// <summary>audio2face_check: sends a short synthesized speech-like test signal (never microphone audio, never played) to an
/// Audio2Face service on a numeric loopback endpoint through the production <see cref="Audio2FaceAdapter"/>, the same client
/// the gateway's Audio2Face relay uses, and reports what came back. It works with either Audio2Face engine of the host role
/// (NVIDIA's NIM or Martlet's local open-source SDK service), or any service exposing that protocol.</summary>
internal static class Audio2FaceCheck
{
    internal static readonly int[] SampleRates = [16_000, 24_000, 44_100, 48_000];

    internal static async Task<object> RunAsync(string? endpoint, int? seconds, int? sampleRate, CancellationToken cancellation)
    {
        var rate = sampleRate ?? 24_000;
        var length = seconds ?? 2;
        if (!SampleRates.Contains(rate)) throw new ArgumentException("sampleRate must be 16000, 24000, 44100 or 48000.");
        if (length is < 1 or > 10) throw new ArgumentException("seconds must be 1 to 10.");
        if (!Uri.TryCreate(endpoint ?? "http://127.0.0.1:52000", UriKind.Absolute, out var uri))
            throw new ArgumentException("endpoint must be an absolute http://127.0.0.1:<port> address.");
        var options = new Audio2FaceOptions { Endpoint = uri };
        options.Validate();

        var clip = Clip(rate, length);
        using var authorization = new Audio2FaceAuthorization(clip, options, DateTimeOffset.UtcNow.AddSeconds(60),
            allowGeneratedSpeechAnalysis: true);
        var watch = Stopwatch.StartNew();
        double? firstFrameMs = null;
        var frames = 0;
        long firstOffset = -1, lastOffset = -1;
        var peaks = new Dictionary<string, double>(StringComparer.Ordinal);
        try
        {
            await foreach (var frame in new Audio2FaceAdapter(options).AnimateAsync(clip, authorization, cancellation))
            {
                firstFrameMs ??= watch.Elapsed.TotalMilliseconds;
                frames++;
                if (firstOffset < 0) firstOffset = frame.SampleOffset;
                lastOffset = frame.SampleOffset;
                foreach (var (name, value) in frame.Blendshapes)
                    peaks[name] = Math.Max(peaks.GetValueOrDefault(name), value);
            }
        }
        catch (Audio2FaceException error)
        {
            return new { ok = false, endpoint = uri.ToString(), sampleRate = rate, seconds = length, failure = error.Failure.ToString(),
                framesBeforeFailure = frames, elapsedMs = Math.Round(watch.Elapsed.TotalMilliseconds) };
        }
        var span = lastOffset > firstOffset ? (double)(lastOffset - firstOffset) / rate : 0;
        return new
        {
            ok = frames > 0,
            endpoint = uri.ToString(),
            sampleRate = rate,
            seconds = length,
            frames,
            framesPerSecond = span > 0 ? Math.Round((frames - 1) / span, 1) : 0,
            firstFrameSeconds = Math.Round((double)firstOffset / rate, 3),
            lastFrameSeconds = Math.Round((double)lastOffset / rate, 3),
            channels = peaks.Count,
            movingChannels = peaks.Count(p => p.Value > 0.01),
            jawOpenPeak = Math.Round(peaks.GetValueOrDefault("jawOpen"), 3),
            strongest = peaks.OrderByDescending(p => p.Value).Take(6).ToDictionary(p => p.Key, p => Math.Round(p.Value, 3)),
            firstFrameMs = firstFrameMs is { } first ? Math.Round(first) : (double?)null,
            elapsedMs = Math.Round(watch.Elapsed.TotalMilliseconds)
        };
    }

    // Four "syllables" a second: a 140 Hz glottal pulse train shaped by vowel formants, with short pauses between them.
    private static GeneratedSpeechClip Clip(int rate, int seconds)
    {
        var ids = new CorrelationIds { SessionId = Guid.NewGuid(), TurnId = Guid.NewGuid(), RequestId = Guid.NewGuid() };
        var format = new PcmFormat { SampleRate = rate, Channels = 1, Encoding = PcmEncoding.Signed16LittleEndian };
        double[][] vowels = [[730, 1090, 2440], [270, 2290, 3010], [570, 840, 2410], [300, 870, 2240]];
        var samples = new short[rate * seconds];
        for (var i = 0; i < samples.Length; i++)
        {
            var t = (double)i / rate;
            var syllable = (int)(t * 4);
            var phase = t * 4 - syllable;
            var envelope = phase < 0.7 ? Math.Sin(Math.PI * phase / 0.7) : 0;
            var vowel = vowels[syllable % vowels.Length];
            double value = 0;
            for (var harmonic = 1; harmonic * 140 < Math.Min(4000, rate / 2); harmonic++)
            {
                var frequency = harmonic * 140.0;
                var gain = vowel.Sum(formant => 1 / (1 + Math.Pow((frequency - formant) / 90, 2))) / harmonic;
                value += gain * Math.Sin(2 * Math.PI * frequency * t);
            }
            samples[i] = (short)Math.Clamp(value * envelope * 6000, short.MinValue, short.MaxValue);
        }
        var frames = new List<PcmFrame>();
        var frameSamples = Math.Min(PcmFrame.MaxDataBytes / 2, rate / 10);
        for (var start = 0; start < samples.Length; start += frameSamples)
        {
            var count = Math.Min(frameSamples, samples.Length - start);
            var bytes = new byte[count * 2];
            Buffer.BlockCopy(samples, start * 2, bytes, 0, bytes.Length);
            frames.Add(new PcmFrame(ids, 1, frames.Count, start, format, bytes));
        }
        return new GeneratedSpeechClip(frames);
    }
}
