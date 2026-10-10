using System.Text.Json;
using System.Text.Json.Serialization;

namespace Martlet.Core.Planning;

/// <summary>What a model took in Ollama once loaded, as Ollama reported it (<c>/api/ps</c>): <see cref="Bytes"/> in all and
/// <see cref="GraphicsBytes"/> of that on the graphics card, at the context it was given. <see cref="Host"/> is the Ollama server's
/// address (this PC's is http://127.0.0.1:11434).</summary>
public sealed record MeasuredModelUse
{
    public required string Host { get; init; }
    public required string Model { get; init; }
    public required long Bytes { get; init; }
    public required long GraphicsBytes { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? ContextTokens { get; init; }
    /// <summary>The model's digest when Ollama said it: a new download of the same tag makes the measurement old.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Digest { get; init; }
    public required DateTimeOffset MeasuredAt { get; init; }

    /// <summary>The whole model is on the graphics card.</summary>
    [JsonIgnore]
    public bool OnGraphicsCard => GraphicsBytes >= Bytes;
}

/// <summary>The memory local models really took, for each Ollama server and model (model-memory.json in the data directory, kept
/// with each computer like model-limits.json). A measured number replaces the estimate (<see cref="LocalModelMemory"/>) for that
/// server and model. Martlet saves it after Ollama on this PC loads a model (the talk window's warm-up, Test model, Load model),
/// never while a reply is on its way.</summary>
public sealed record MeasuredModelMemory
{
    public const string FileName = "model-memory.json";
    public const int MaximumModels = 64;

    public int SchemaVersion { get; init; } = 1;
    public IReadOnlyList<MeasuredModelUse> Models { get; init; } = [];

    /// <summary>The measurement for <paramref name="model"/> on <paramref name="host"/>; a name without a tag means ":latest".</summary>
    public MeasuredModelUse? Find(string? host, string? model) => host is null || model is null ? null :
        Models.FirstOrDefault(m => string.Equals(m.Host, host, StringComparison.OrdinalIgnoreCase) && SameModel(m.Model, model));

    /// <summary>Every measurement of <paramref name="model"/>, newest first, whatever the host.</summary>
    public IEnumerable<MeasuredModelUse> FindAll(string? model) => model is null ? [] :
        Models.Where(m => SameModel(m.Model, model)).OrderByDescending(m => m.MeasuredAt);

    /// <summary>This list with <paramref name="use"/> first, replacing an earlier one for the same host and model.</summary>
    public MeasuredModelMemory With(MeasuredModelUse use)
    {
        ArgumentNullException.ThrowIfNull(use);
        return this with
        {
            Models = [use, .. Models.Where(m => !string.Equals(m.Host, use.Host, StringComparison.OrdinalIgnoreCase) || !SameModel(m.Model, use.Model))
                .Take(MaximumModels - 1)]
        };
    }

    /// <summary>Whether <paramref name="use"/> says something new: a first measurement, another size, context or digest.</summary>
    public bool Changes(MeasuredModelUse use) => Find(use.Host, use.Model) is not { } known ||
        known.Bytes != use.Bytes || known.GraphicsBytes != use.GraphicsBytes || known.ContextTokens != use.ContextTokens ||
        !string.Equals(known.Digest, use.Digest, StringComparison.OrdinalIgnoreCase);

    /// <summary>Saves <paramref name="use"/> in <paramref name="directory"/> when it says something new. True when saved.</summary>
    public static bool Record(string? directory, MeasuredModelUse use)
    {
        if (directory is null || !Valid(use)) return false;
        var known = Load(directory);
        return known.Changes(use) && known.With(use).Save(directory);
    }

    private static bool SameModel(string a, string b) => string.Equals(Tagged(a), Tagged(b), StringComparison.OrdinalIgnoreCase);

    private static string Tagged(string name) => name.Contains(':', StringComparison.Ordinal) ? name : name + ":latest";

    private static bool Valid(MeasuredModelUse? m) => m is { Host.Length: > 0 and <= 2048, Model.Length: > 0 and <= 256, Bytes: > 0, GraphicsBytes: >= 0 } &&
        m.GraphicsBytes <= m.Bytes * 2 && m.Digest is null or { Length: <= 128 } && m.ContextTokens is null or > 0 and <= 100_000_000;

    public static MeasuredModelMemory Load(string? directory)
    {
        if (directory is null) return new();
        try
        {
            var path = Path.Combine(directory, FileName);
            if (!File.Exists(path)) return new();
            var loaded = JsonSerializer.Deserialize<MeasuredModelMemory>(File.ReadAllText(path));
            return loaded is { SchemaVersion: 1 } ? new() { Models = [.. (loaded.Models ?? []).Where(Valid).Take(MaximumModels)] } : new();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or NotSupportedException) { return new(); }
    }

    public bool Save(string? directory)
    {
        if (directory is null) return false;
        try
        {
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, FileName);
            var temporary = Path.Combine(directory, $"model-memory.{Guid.NewGuid():N}.tmp");
            try
            {
                File.WriteAllText(temporary, JsonSerializer.Serialize(this with { Models = [.. Models.Where(Valid).Take(MaximumModels)] },
                    new JsonSerializerOptions { WriteIndented = true }));
                File.Move(temporary, path, overwrite: true);
            }
            finally
            {
                if (File.Exists(temporary)) File.Delete(temporary);
            }
            return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return false; }
    }
}
