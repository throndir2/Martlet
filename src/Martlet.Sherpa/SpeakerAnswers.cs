using System.Text.Json;

namespace Martlet.Sherpa;

/// <summary>A host's Voice recognition role (contract <c>martlet.voice-recognition-relay</c> 1.0, docs/VOICES.md#where-voice-recognition-runs)
/// answers with the fields of <see cref="SpeakerAnalysis"/> and <see cref="HeardSpeaker"/> in JSON. The role runs the same models and
/// steps as <see cref="SpeakerEngine"/>, so its voiceprints match the voice list like this PC's. Every answer is checked before it is
/// used: 256 finite numbers of length 1 per voiceprint, times and seconds within the audio, sane counts. A bad answer throws
/// <see cref="SherpaException"/>, and the caller passes that computer over.</summary>
public static class SpeakerAnswers
{
    public const int Dimension = 256;
    /// <summary>The most speakers one answer may name (the engine finds up to three per ten seconds of at most a minute).</summary>
    public const int MaximumSpeakers = 32;
    private const double Slack = 0.5;

    /// <summary>The samples as 16 kHz mono PCM16 little-endian, the role's audio format.</summary>
    public static byte[] Pcm16(ReadOnlySpan<float> samples)
    {
        var bytes = new byte[samples.Length * 2];
        for (var i = 0; i < samples.Length; i++)
        {
            var value = (int)MathF.Round(samples[i] * 32768f);
            System.Buffers.Binary.BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(i * 2, 2), (short)Math.Clamp(value, short.MinValue, short.MaxValue));
        }
        return bytes;
    }

    /// <summary>The role's answer to <c>analyze</c> for <paramref name="seconds"/> of audio, checked.</summary>
    public static SpeakerAnalysis Analysis(JsonElement answer, double seconds)
    {
        Object(answer);
        var limit = seconds + Slack;
        if (!answer.TryGetProperty("speakers", out var list) || list.ValueKind != JsonValueKind.Array || list.GetArrayLength() > MaximumSpeakers)
            throw Invalid("speakers");
        var speakers = new List<HeardSpeaker>();
        foreach (var speaker in list.EnumerateArray())
        {
            if (speaker.ValueKind != JsonValueKind.Object) throw Invalid("speakers");
            var local = Whole(speaker, "local", 0, 4096);
            var voiceprint = Voiceprint(speaker.TryGetProperty("voiceprint", out var print) ? print : default, "voiceprint")
                ?? throw Invalid("voiceprint");
            var clean = Number(speaker, "clean_seconds", 0, limit);
            var start = Number(speaker, "start", 0, limit);
            var end = Number(speaker, "end", start, limit);
            speakers.Add(new(local, voiceprint, clean, start, end));
        }
        if (speakers.Select(s => s.Local).Distinct().Count() != speakers.Count) throw Invalid("speakers");
        var voices = Whole(answer, "voices", speakers.Count, MaximumSpeakers * 4);
        if (!answer.TryGetProperty("overlap", out var overlap) || overlap.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            throw Invalid("overlap");
        var whole = Voiceprint(answer.TryGetProperty("whole", out var all) ? all : default, "whole");
        var speech = Number(answer, "speech_seconds", 0, limit);
        return new(speakers.OrderByDescending(s => s.CleanSeconds).ToArray(), voices, overlap.GetBoolean(), whole, speech);
    }

    /// <summary>The role's answer to <c>voiceprint</c>: one voiceprint of all the audio, or null when it was too short.</summary>
    public static float[]? Voiceprint(JsonElement answer)
    {
        Object(answer);
        return Voiceprint(answer.TryGetProperty("voiceprint", out var print) ? print : default, "voiceprint");
    }

    /// <summary>The role's answer to <c>status</c>: its state ("ready" or "loading"), where it runs ("cuda" or "cpu") and its model.</summary>
    public static (string State, string? Provider, string? Model) Status(JsonElement answer)
    {
        Object(answer);
        static string? Text(JsonElement answer, string name) =>
            answer.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String && value.GetString() is { Length: > 0 and <= 200 } text ? text : null;
        var state = Text(answer, "state");
        if (state is not ("ready" or "loading")) throw Invalid("state");
        var provider = Text(answer, "provider");
        if (provider is not (null or "cuda" or "cpu")) throw Invalid("provider");
        return (state, provider, Text(answer, "model"));
    }

    // An object, and not the role's error answer.
    private static void Object(JsonElement answer)
    {
        if (answer.ValueKind != JsonValueKind.Object) throw Invalid("answer");
        if (answer.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.Object)
            throw new SherpaException("The Voice recognition role couldn't recognize the voices: " +
                (error.TryGetProperty("summary", out var summary) && summary.ValueKind == JsonValueKind.String ? summary.GetString() : "it failed") + ".");
    }

    private static float[]? Voiceprint(JsonElement value, string name)
    {
        if (value.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null) return null;
        if (value.ValueKind != JsonValueKind.Array || value.GetArrayLength() != Dimension) throw Invalid(name);
        var vector = new float[Dimension];
        var i = 0;
        double norm = 0;
        foreach (var item in value.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Number || !item.TryGetDouble(out var number) || !double.IsFinite(number) || Math.Abs(number) > 1.0001)
                throw Invalid(name);
            vector[i++] = (float)number;
            norm += number * number;
        }
        // Normalized to length 1, as the engine makes every voiceprint.
        if (Math.Abs(Math.Sqrt(norm) - 1) > 0.01) throw Invalid(name);
        return vector;
    }

    private static int Whole(JsonElement owner, string name, int least, int most) =>
        owner.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number) &&
        number >= least && number <= most ? number : throw Invalid(name);

    private static double Number(JsonElement owner, string name, double least, double most) =>
        owner.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number) &&
        double.IsFinite(number) && number >= least - 1e-6 && number <= most ? number : throw Invalid(name);

    private static SherpaException Invalid(string field) =>
        new($"The Voice recognition role's answer was invalid ({field}).");
}
