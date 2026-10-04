using System.Buffers.Binary;
using System.Globalization;
using System.Text.Json;
using Martlet.Core.Audio;

namespace Martlet.Core.Creations;

/// <summary>A FIXTURE - NOT AI creation kind for checks (MCP's <c>creations_check</c>) and tests: a generated test tone kept
/// as FLAC (<c>audio</c>) with optional JSON notes (<c>notes</c>). It is never registered in Martlet itself.</summary>
public static class FixtureCreations
{
    public const string KindName = "fixture";
    public const int SampleRate = 48_000;

    public static CreationKind Kind { get; } = new()
    {
        Name = KindName,
        Noun = "test tone",
        Plural = "test tones",
        Verb = "play",
        Glyph = "\uE9E9",
        Assets =
        [
            new("audio", [FlacCodec.MediaType], 16L * 1024 * 1024),
            new("notes", ["application/json"], 64 * 1024, Required: false)
        ],
        MaximumBytes = 16L * 1024 * 1024 + 64 * 1024,
        AutoCleanup = true,
        OptionsHint = "For a test tone: {\"start_ms\": 0}.",
        Describe = creation =>
        {
            var seconds = (creation.Duration ?? TimeSpan.Zero).TotalSeconds.ToString("0.#", CultureInfo.InvariantCulture);
            var hz = creation.Metadata is { } metadata && metadata.TryGetProperty("frequency_hz", out var value) ? $" at {value.GetInt32()} Hz" : "";
            return $"FIXTURE - NOT AI: a {seconds} s test tone{hz}";
        },
        Details = creation => [new("Notes", creation.Text ?? ""), new("Tone", creation.Summary ?? "")]
    };

    /// <summary>A test-tone creation: <paramref name="seconds"/> of a sine at <paramref name="frequencyHz"/> (stereo, 48 kHz,
    /// with a quiet second half so FLAC has silence to save, and <paramref name="noise"/> of hiss so it compresses less), as
    /// FLAC, and its notes.</summary>
    public static CreationDraft Draft(CreationAuthor author, string title, double seconds, int frequencyHz = 440, int channels = 2, int noise = 0)
    {
        var pcm = Tone(seconds, frequencyHz, channels, noise);
        var notes = JsonSerializer.SerializeToUtf8Bytes(new { kind = "fixture", frequency_hz = frequencyHz, note = "FIXTURE - NOT AI" });
        return new()
        {
            Kind = KindName,
            Title = title,
            Summary = $"A {frequencyHz} Hz test tone (FIXTURE - NOT AI).",
            Text = "[intro]\nA test tone, not made by AI.\n[outro]\nSilence.",
            Duration = TimeSpan.FromSeconds(seconds),
            Metadata = JsonSerializer.SerializeToElement(new { frequency_hz = frequencyHz, sample_rate = SampleRate, channels }),
            CreatedBy = author,
            Assets = [new("audio", FlacCodec.MediaType, FlacCodec.Encode(pcm, SampleRate, channels)), new("notes", "application/json", notes)]
        };
    }

    /// <summary>Interleaved 16-bit PCM of a sine (with <paramref name="noise"/> of hiss) for the first half and silence for
    /// the second.</summary>
    public static byte[] Tone(double seconds, int frequencyHz, int channels, int noise = 0)
    {
        var frames = (int)(seconds * SampleRate);
        var random = new Random(frequencyHz);
        var pcm = new byte[frames * 2 * channels];
        for (var i = 0; i < frames; i++)
        {
            var value = i < frames / 2
                ? (short)(Math.Sin(2 * Math.PI * frequencyHz * i / SampleRate) * 8_000 + (noise > 0 ? random.Next(-noise, noise) : 0))
                : (short)0;
            for (var c = 0; c < channels; c++)
                BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan((i * channels + c) * 2), c == 1 ? (short)(value / 2) : value);
        }
        return pcm;
    }

    /// <summary>"Plays" a test tone: reads and decodes its audio here and says what it would play, from <c>start_ms</c>.
    /// Nothing is played.</summary>
    public static ICreationHandler Handler { get; } = new CreationHandler(async (action, token) =>
    {
        var bytes = await action.Assets.ReadAsync("audio", token);
        if (bytes is null) return new("Its audio hasn't reached this computer yet. Say you'll do it in a moment.", true);
        var audio = FlacCodec.Decode(bytes);
        var start = action.Options.ValueKind == JsonValueKind.Object && action.Options.TryGetProperty("start_ms", out var value) &&
            value.TryGetInt64(out var ms) ? Math.Max(0, ms) : 0;
        return new(string.Create(CultureInfo.InvariantCulture,
            $"FIXTURE - NOT AI: played \"{action.Creation.Title}\" from {start} ms ({audio.Frames} frames, {audio.Channels} channels, " +
            $"{audio.Duration.TotalSeconds:0.##} s)."));
    });
}
