using Martlet.Core.Audio;
using Martlet.Core.Contracts;

namespace Martlet.Avatar.Audio2Face;

/// <summary>Runs a song's vocals once through an Audio2Face service, offline, for its mouth track: in clips of at most 20 seconds
/// (the service takes at most 90 seconds a request) that end where the vocals are quietest near their end, so no word is cut.</summary>
public static class Audio2FaceSong
{
    public static TimeSpan ClipLimit => TimeSpan.FromSeconds(20);

    /// <summary>The blendshape frames for <paramref name="vocals"/> (mono, <paramref name="sampleRate"/> Hz), in song time.</summary>
    public static async Task<IReadOnlyList<(TimeSpan At, IReadOnlyDictionary<string, double> Weights)>> AnalyzeAsync(short[] vocals,
        int sampleRate, Audio2FaceOptions options, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(vocals);
        ArgumentNullException.ThrowIfNull(options);
        var faces = new List<(TimeSpan, IReadOnlyDictionary<string, double>)>();
        var format = new PcmFormat { SampleRate = sampleRate, Channels = 1, Encoding = PcmEncoding.Signed16LittleEndian };
        var ids = new CorrelationIds { SessionId = Guid.NewGuid(), TurnId = Guid.NewGuid(), RequestId = Guid.NewGuid() };
        var step = sampleRate / 10;
        foreach (var (from, to) in Clips(vocals, sampleRate))
        {
            var frames = new List<PcmFrame>();
            for (long at = from, sequence = 0; at < to; at += step, sequence++)
            {
                var count = (int)Math.Min(step, to - at);
                frames.Add(new PcmFrame(ids, 0, sequence, at, format,
                    System.Runtime.InteropServices.MemoryMarshal.AsBytes(vocals.AsSpan((int)at, count))));
            }
            var clip = new GeneratedSpeechClip(frames);
            using var permission = new Audio2FaceAuthorization(clip, options, DateTimeOffset.UtcNow.AddSeconds(110), allowGeneratedSpeechAnalysis: true);
            await foreach (var face in new Audio2FaceAdapter(options).AnimateAsync(clip, permission, token).ConfigureAwait(false))
                faces.Add((TimeSpan.FromSeconds((double)face.SampleOffset / sampleRate), face.Blendshapes));
        }
        return faces;
    }

    /// <summary>Clips of at most <see cref="ClipLimit"/>, each ending at the quietest 10 ms in its last two seconds.</summary>
    public static IEnumerable<(long From, long To)> Clips(short[] vocals, int sampleRate)
    {
        ArgumentNullException.ThrowIfNull(vocals);
        long from = 0;
        while (from < vocals.Length)
        {
            var limit = Math.Min(vocals.Length, from + (long)(ClipLimit.TotalSeconds * sampleRate));
            var to = limit;
            if (limit < vocals.Length)
            {
                var window = sampleRate / 100;
                var quietest = double.MaxValue;
                for (var at = limit - 2L * sampleRate; at + window <= limit; at += window)
                {
                    double sum = 0;
                    for (var i = at; i < at + window; i++) sum += (double)vocals[i] * vocals[i];
                    if (sum < quietest)
                    {
                        quietest = sum;
                        to = at + window;
                    }
                }
            }
            yield return (from, to);
            from = to;
        }
    }
}
