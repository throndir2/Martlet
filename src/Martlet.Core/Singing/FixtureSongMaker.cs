using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace Martlet.Core.Singing;

/// <summary>
/// FIXTURE - NOT AI: a deterministic <see cref="ISongMaker"/> for tests and plumbing checks. It "sings" a sine melody (one
/// note per word, pitched from the voice ID) over sine chord pads with a click on every beat, starts every lyric line on a
/// downbeat of a straight 4/4 grid, reports every stage, honours cancellation and returns the same three 48 kHz tracks,
/// lyric lines with sections and beat grid a real song has. Nothing it makes is music from a model, and
/// <see cref="SongEngineIdentity.Fixture"/> is true.
/// </summary>
public sealed class FixtureSongMaker(TimeSpan? stageDelay = null) : ISongMaker
{
    public const string Generator = "FIXTURE - NOT AI tone song";
    private static readonly SongStage[] Stages =
    [
        SongStage.WritingMusic, SongStage.Separating, SongStage.MatchingVoice, SongStage.Mixing, SongStage.Aligning,
        SongStage.Delivering
    ];
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

    /// <summary>The song's three tracks, lyric lines and beat grid, computed synchronously (deterministic for a request).</summary>
    public static SongResult Compose(SongRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        request.Validate();
        var seed = request.Seed ?? (long)(BinaryPrimitives.ReadUInt32LittleEndian(
            SHA256.HashData(Encoding.UTF8.GetBytes(request.Lyrics + "\n" + request.Style))));
        var rate = SongTrack.SampleRateHz;
        var frames = request.DurationSeconds * rate;
        var parsed = SongLyrics.Parse(request.Lyrics);
        if (parsed.Count == 0) parsed = [("", request.Lyrics.Trim())];
        // The voice decides the singer's pitch, so different voices give audibly different fixtures.
        var voiceHash = SHA256.HashData(Encoding.UTF8.GetBytes(request.VoiceId));
        var root = 196.0 * Math.Pow(2, (voiceHash[0] % 12) / 12.0);
        const int beatsPerBar = 4;
        var bpm = request.Bpm ?? 96;
        var beat = 60.0 / bpm;
        var bar = beat * beatsPerBar;
        // One bar of intro, then each line takes whole bars and starts on a downbeat.
        var barsPerLine = Math.Clamp((int)Math.Floor((request.DurationSeconds - bar) / bar / (parsed.Count + 1)), 1, 2);
        int[] scale = [0, 2, 4, 5, 7, 9, 11, 12];
        int[][] chords = [[0, 4, 7], [9, 12, 16], [5, 9, 12], [7, 11, 14]];

        var beats = new List<TimeSpan>();
        var downbeats = new List<TimeSpan>();
        for (var b = 0; b * beat < request.DurationSeconds; b++)
        {
            beats.Add(TimeSpan.FromSeconds(b * beat));
            if (b % beatsPerBar == 0) downbeats.Add(TimeSpan.FromSeconds(b * beat));
        }
        var lyricLines = new List<SongLyricLine>();
        var words = new List<SongLyricWord>();
        for (var l = 0; l < parsed.Count; l++)
        {
            var start = bar * (1 + l * barsPerLine);
            if (start >= request.DurationSeconds - 1) break;
            var end = Math.Min(start + bar * barsPerLine - beat / 2, request.DurationSeconds);
            lyricLines.Add(new SongLyricLine(TimeSpan.FromSeconds(start), TimeSpan.FromSeconds(end), parsed[l].Text,
                parsed[l].Section));
            // One sung note per word, spread evenly over the line.
            var lineWords = parsed[l].Text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var each = (end - start) / lineWords.Length;
            for (var w = 0; w < lineWords.Length; w++)
                words.Add(new SongLyricWord(TimeSpan.FromSeconds(start + w * each), TimeSpan.FromSeconds(start + (w + 1) * each - 0.02),
                    lineWords[w], lyricLines.Count - 1));
        }

        var vocals = new short[frames];
        var backing = new short[frames * 2];
        var mix = new short[frames * 2];
        for (var n = 0; n < frames; n++)
        {
            var t = (double)n / rate;
            var chord = chords[(int)(t / bar) % chords.Length];
            var pad = 0.0;
            foreach (var step in chord)
                pad += Math.Sin(2 * Math.PI * (root / 2) * Math.Pow(2, step / 12.0) * t);
            // A short click on every beat, louder on the downbeat, so the grid is audible and measurable.
            var beatIndex = (int)(t / beat);
            var sinceBeat = t - beatIndex * beat;
            var click = sinceBeat < 0.03
                ? Math.Sin(2 * Math.PI * 1_000 * t) * (1 - sinceBeat / 0.03) * (beatIndex % beatsPerBar == 0 ? 0.25 : 0.12)
                : 0;
            pad = pad * 0.06 + click;
            var voice = 0.0;
            for (var w = 0; w < words.Count; w++)
            {
                var from = words[w].Start.TotalSeconds;
                var to = words[w].End.TotalSeconds;
                if (t < from || t >= to) continue;
                var degree = scale[(int)((seed + w * 3 + words[w].LineIndex) % scale.Length)];
                var phase = t - from;
                var envelope = Math.Min(1, phase / 0.03) * Math.Min(1, (to - t) / 0.05);
                var vibrato = 1 + 0.004 * Math.Sin(2 * Math.PI * 5.5 * t);
                voice = 0.22 * envelope * Math.Sin(2 * Math.PI * root * Math.Pow(2, degree / 12.0) * vibrato * t);
                break;
            }
            var fade = Math.Min(1, (request.DurationSeconds - t) / 1.0);
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
            BeatsPerBar = beatsPerBar,
            Beats = beats,
            Downbeats = downbeats,
            LyricTimestamps = lyricLines,
            Words = words,
            WordTimingSource = "fixture"
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
