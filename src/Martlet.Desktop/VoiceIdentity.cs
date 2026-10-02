using System.IO;
using System.Text.Json;
using Martlet.Audio;

namespace Martlet.Desktop;

// A numeric voiceprint only; no enrollment audio is ever written to disk.
internal sealed record Voiceprint(float[] Embedding, float Threshold, float Consistency, DateTimeOffset CreatedAt, double SpeechSeconds);

internal sealed class VoiceIdentityException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>Local Voice ID: the bundled speaker encoder plus the saved voiceprint in voice-id.json.</summary>
internal sealed class VoiceIdentity
{
    internal const string FileName = "voice-id.json";
    internal const string ModelId = "resemblyzer-ge2e-v1";
    internal const float MinimumThreshold = 0.5f, MaximumThreshold = 0.95f;
    private static readonly Lazy<SpeakerEncoder> encoder = new(() =>
    {
        using var stream = typeof(VoiceIdentity).Assembly.GetManifestResourceStream("Martlet.Desktop.SpeakerEncoder.bin")
            ?? throw new VoiceIdentityException("Voice ID is missing from this build.");
        return SpeakerEncoder.Load(stream);
    }, LazyThreadSafetyMode.ExecutionAndPublication);
    private readonly object gate = new();
    private readonly string? directory;
    private Voiceprint? current;

    internal VoiceIdentity(string? directory) => this.directory = directory;

    internal static SpeakerEncoder Encoder => encoder.Value;
    internal string? DataDirectory => directory;
    internal bool Available => directory is not null;
    internal Voiceprint? Current { get { lock (gate) return current; } }
    internal string? LoadError { get; private set; }
    internal event Action? Changed;

    internal void Load()
    {
        if (directory is null) return;
        try
        {
            var path = Path.Combine(directory, FileName);
            Voiceprint? loaded = null;
            if (File.Exists(path))
            {
                var file = JsonSerializer.Deserialize<VoiceprintFile>(File.ReadAllText(path))
                    ?? throw new InvalidDataException("Empty Voice ID file.");
                if (file.Schema != 1 || file.Model != ModelId || file.Embedding is not { Length: SpeakerEncoder.EmbeddingSize } ||
                    file.Embedding.Any(value => !float.IsFinite(value)) || !float.IsFinite(file.Threshold) ||
                    file.Threshold is < MinimumThreshold or > MaximumThreshold)
                    throw new InvalidDataException("Unsupported or damaged Voice ID file.");
                loaded = new(file.Embedding, file.Threshold, file.Consistency, file.CreatedAt, file.SpeechSeconds);
            }
            lock (gate) current = loaded;
            LoadError = null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException or JsonException)
        {
            lock (gate) current = null;
            LoadError = "Couldn't read your Voice ID. Enroll again or check file access.";
        }
        Changed?.Invoke();
    }

    internal void Save(Voiceprint voiceprint)
    {
        if (directory is null) throw new VoiceIdentityException("No local data directory is available.");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, FileName);
        var temporary = Path.Combine(directory, $"voice-id.{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(new VoiceprintFile
            {
                Schema = 1, Model = ModelId, CreatedAt = voiceprint.CreatedAt, Threshold = voiceprint.Threshold,
                Consistency = voiceprint.Consistency, SpeechSeconds = voiceprint.SpeechSeconds, Embedding = voiceprint.Embedding
            }));
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
        lock (gate) current = voiceprint;
        LoadError = null;
        Changed?.Invoke();
    }

    internal void SetThreshold(float threshold)
    {
        var existing = Current ?? throw new VoiceIdentityException("Enroll your voice first.");
        Save(existing with { Threshold = Math.Clamp(threshold, MinimumThreshold, MaximumThreshold) });
    }

    internal void Delete()
    {
        if (directory is not null) File.Delete(Path.Combine(directory, FileName));
        lock (gate) current = null;
        LoadError = null;
        Changed?.Invoke();
    }

    internal static string Describe(SpeakerCheck check, float threshold) => check.Verdict switch
    {
        SpeakerVerdict.User when check.OtherVoiceDetected => "Voice ID recognized you, but another voice was also heard.",
        SpeakerVerdict.User => "Voice ID recognized you.",
        SpeakerVerdict.OtherSpeaker => "Voice ID heard someone else, so Martlet ignored it.",
        _ => "Voice ID needs a little more speech."
    };

    private sealed class VoiceprintFile
    {
        public int Schema { get; set; }
        public string? Model { get; set; }
        public DateTimeOffset CreatedAt { get; set; }
        public float Threshold { get; set; }
        public float Consistency { get; set; }
        public double SpeechSeconds { get; set; }
        public float[]? Embedding { get; set; }
    }
}
