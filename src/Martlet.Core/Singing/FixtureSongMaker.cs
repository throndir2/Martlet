using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace Martlet.Core.Singing;

/// <summary>
/// FIXTURE - NOT AI: a deterministic <see cref="ISongMaker"/> for tests and plumbing checks. It "sings" a sine melody (one
/// note per lyric syllable group, pitched from the voice ID) over sine chord pads, reports every stage, honours
/// cancellation and returns the same three 48 kHz tracks and lyric timestamps a real song has. Nothing it makes is music
/// from a model, and <see cref="SongEngineIdentity.Fixture"/> is true.
/// </summary>
public sealed class FixtureSongMaker(TimeSpan? stageDelay = null) : ISongMaker
{
    public const string Generator = "FIXTURE - NOT AI tone song";
    private static readonly SongStage[] Stages =
        [SongStage.WritingMusic, SongStage.Separating, SongStage.MatchingVoice, SongStage.Mixing, SongStage.Delivering];
    private readonly TimeSpan delay = stageDelay ?? TimeSpan.FromMilliseconds(20);

    public Task<SongMakerAvailability> GetAvailabilityAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(new SongMakerAvailability(true, null, null,
            [SongQuality.Fast, SongQuality.HighQuality], [SongVoiceMatch.SoulX, SongVoiceMatch.VevoSing], Fixture: true));
    }

    public async Task<SongResult> GenerateAsync(SongRequest request, IProgress<SongProgress>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        request.Validate();
        cancellationToken.ThrowIfCancellationRequested();
        progress?.Report(new SongProgress(SongStage.Queued, 0, 0));
        var timings = new List<SongStageTiming>();
        for (var i = 0; i < Stages.Length; i++)
        {
            progress?.Report(new SongProgress(Stages[i], (double)i / Stages.Length));
            var started = System.Diagnostics.Stopwatch.GetTimestamp();
            await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            timings.Add(new SongStageTiming(Stages[i], System.Diagnostics.Stopwatch.GetElapsedTime(started)));
        }
        var result = Compose(request);
        progress?.Report(new SongProgress(SongStage.Completed, 1));
        return result with { StageTimings = timings };
    }

    /// <summary>The song's three tracks and lyric timestamps, computed synchronously (deterministic for a request).</summary>
    public static SongResult Compose(SongRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        request.Validate();
        var seed = request.Seed ?? (long)(BinaryPrimitives.ReadUInt32LittleEndian(
            SHA256.HashData(Encoding.UTF8.GetBytes(request.Lyrics + "\n" + request.Style))));
        var rate = SongTrack.SampleRateHz;
        var frames = request.DurationSeconds * rate;
        var lines = request.Lyrics.Split('\n')
            .Select(line => line.Trim())
            .Where(line => line.Length > 0 && !(line.StartsWith('[') && line.EndsWith(']')))
            .ToArray();
        if (lines.Length == 0) lines = [request.Lyrics.Trim()];
        // The voice decides the singer's pitch, so different voices give audibly different fixtures.
        var voiceHash = SHA256.HashData(Encoding.UTF8.GetBytes(request.VoiceId));
        var root = 196.0 * Math.Pow(2, (voiceHash[0] % 12) / 12.0);
        var bpm = request.Bpm ?? 96;
        var beat = 60.0 / bpm;
        var lineSeconds = Math.Max(2.0, Math.Min(6.0, (double)request.DurationSeconds / (lines.Length + 1)));
        var intro = Math.Min(4.0, lineSeconds);
        int[] scale = [0, 2, 4, 5, 7, 9, 11, 12];
        int[][] chords = [[0, 4, 7], [9, 12, 16], [5, 9, 12], [7, 11, 14]];

        var vocals = new short[frames];
        var backing = new short[frames * 2];
        var mix = new short[frames * 2];
        var lyricLines = new List<SongLyricLine>();
        for (var l = 0; l < lines.Length; l++)
        {
            var start = intro + l * lineSeconds;
            if (start >= request.DurationSeconds - 1) break;
            var end = Math.Min(start + lineSeconds * 0.9, request.DurationSeconds);
            lyricLines.Add(new SongLyricLine(TimeSpan.FromSeconds(start), TimeSpan.FromSeconds(end), lines[l]));
        }
        for (var n = 0; n < frames; n++)
        {
            var t = (double)n / rate;
            var chord = chords[(int)(t / (beat * 4)) % chords.Length];
            var pad = 0.0;
            foreach (var step in chord)
                pad += Math.Sin(2 * Math.PI * (root / 2) * Math.Pow(2, step / 12.0) * t);
            pad *= 0.06;
            var voice = 0.0;
            foreach (var line in lyricLines)
            {
                var from = line.Start.TotalSeconds;
                var to = line.End!.Value.TotalSeconds;
                if (t < from || t >= to) continue;
                var noteLength = beat;
                var note = (int)((t - from) / noteLength);
                var degree = scale[(int)((seed + note * 3 + line.Text.Length) % scale.Length)];
                var phase = (t - from) - note * noteLength;
                var envelope = Math.Min(1, phase / 0.03) * Math.Min(1, (noteLength - phase) / 0.05);
                var vibrato = 1 + 0.004 * Math.Sin(2 * Math.PI * 5.5 * t);
                voice = 0.22 * envelope * Math.Sin(2 * Math.PI * root * Math.Pow(2, degree / 12.0) * vibrato * t);
                break;
            }
            var fade = Math.Min(1, Math.Min(t / 0.5, (request.DurationSeconds - t) / 1.0));
            var left = pad * (1.0 + 0.2 * Math.Sin(2 * Math.PI * 0.1 * t)) * fade;
            var right = pad * (1.0 - 0.2 * Math.Sin(2 * Math.PI * 0.1 * t)) * fade;
            voice *= fade;
            vocals[n] = Sample(voice);
            backing[2 * n] = Sample(left);
            backing[2 * n + 1] = Sample(right);
            mix[2 * n] = Sample(left + voice);
            mix[2 * n + 1] = Sample(right + voice);
        }
        var jobId = "fixture-" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(
            $"{seed}\n{request.VoiceId}\n{request.DurationSeconds}\n{request.Lyrics}")))[..16];
        return new SongResult
        {
            JobId = jobId,
            VoiceId = request.VoiceId,
            Mix = new SongTrack(SongTrackKind.Mix, rate, 2, Bytes(mix)),
            Vocals = new SongTrack(SongTrackKind.Vocals, rate, 1, Bytes(vocals)),
            Backing = new SongTrack(SongTrackKind.Backing, rate, 2, Bytes(backing)),
            Engine = new SongEngineIdentity(Generator, "FIXTURE - NOT AI", "FIXTURE - NOT AI", request.Quality,
                request.VoiceMatch, Fixture: true),
            Seed = seed,
            Bpm = bpm,
            Key = request.Key,
            LyricTimestamps = lyricLines
        };
    }

    private static short Sample(double value) => (short)Math.Round(Math.Clamp(value, -1, 1) * 32_767);

    private static byte[] Bytes(short[] samples)
    {
        var bytes = new byte[samples.Length * 2];
        for (var i = 0; i < samples.Length; i++)
            BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(2 * i), samples[i]);
        return bytes;
    }
}
