using System.Text.Json;
using System.Text.Json.Serialization;

namespace Martlet.Core.Planning;

/// <summary>How soon one Thinking model's first words came on one server, from the replies Martlet timed (the desktop log's
/// <c>Reply latency</c> lines): <see cref="Samples"/> are the last replies' times from the Thinking request to its first words,
/// in milliseconds, oldest first. <see cref="Host"/> is the server's origin (Ollama on this PC is http://127.0.0.1:11434, a
/// paired host is its gateway's origin, a cloud provider its address).</summary>
public sealed record MeasuredFirstWord
{
    public required string Host { get; init; }
    public required string Model { get; init; }
    public required IReadOnlyList<int> Samples { get; init; }
    public required DateTimeOffset MeasuredAt { get; init; }

    /// <summary>The middle of <see cref="Samples"/>: a model's first reply after it loaded (seconds late) doesn't count much.</summary>
    [JsonIgnore]
    public int Ms
    {
        get
        {
            var sorted = Samples.Order().ToArray();
            return sorted.Length == 0 ? 0 : sorted.Length % 2 == 1 ? sorted[sorted.Length / 2]
                : (sorted[sorted.Length / 2 - 1] + sorted[sorted.Length / 2]) / 2;
        }
    }
}

/// <summary>The measured first word of each Thinking model on each server (model-speed.json in the data directory, kept with each
/// computer like model-memory.json). A measured number replaces the planner's estimate (<see cref="ComponentOption.FirstWordMs"/>;
/// <see cref="FootprintCatalog.WithMeasured"/>). The desktop adds a reply's time after the reply ended, never on the reply's path.</summary>
public sealed record MeasuredFirstWords
{
    public const string FileName = "model-speed.json";
    public const int MaximumModels = 64;
    /// <summary>How many recent replies each model keeps.</summary>
    public const int MaximumSamples = 9;
    /// <summary>A time longer than this is a model loading or a failure, not a first word.</summary>
    public const int MaximumMs = 120_000;

    public int SchemaVersion { get; init; } = 1;
    public IReadOnlyList<MeasuredFirstWord> Models { get; init; } = [];

    /// <summary>The key a server is kept under: its origin (scheme, host and port), so http://127.0.0.1:11434/v1 and
    /// http://127.0.0.1:11434 are the same server.</summary>
    public static string? HostKey(string? origin) =>
        Uri.TryCreate(origin, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" ? uri.GetLeftPart(UriPartial.Authority) : null;

    /// <summary>The measurement for <paramref name="model"/> on <paramref name="host"/> (any form of the origin).</summary>
    public MeasuredFirstWord? Find(string? host, string? model) => HostKey(host) is not { } key || model is null ? null :
        Models.FirstOrDefault(m => string.Equals(m.Host, key, StringComparison.OrdinalIgnoreCase) && SameModel(m.Model, model));

    /// <summary>Every measurement of <paramref name="model"/>, newest first, whatever the server.</summary>
    public IEnumerable<MeasuredFirstWord> FindAll(string? model) => model is null ? [] :
        Models.Where(m => SameModel(m.Model, model)).OrderByDescending(m => m.MeasuredAt);

    /// <summary>This list with one more reply of <paramref name="model"/> on <paramref name="host"/> that took
    /// <paramref name="ms"/> to its first words; the same list when the time or the names aren't usable.</summary>
    public MeasuredFirstWords With(string host, string model, int ms, DateTimeOffset at)
    {
        if (HostKey(host) is not { } key || model is not { Length: > 0 and <= 256 } || ms is <= 0 or > MaximumMs) return this;
        var before = Find(key, model);
        var samples = (before?.Samples ?? []).Append(ms).TakeLast(MaximumSamples).ToArray();
        var measured = new MeasuredFirstWord { Host = key, Model = model, Samples = samples, MeasuredAt = at.ToUniversalTime() };
        return this with
        {
            Models = [measured, .. Models.Where(m => m != before).Take(MaximumModels - 1)]
        };
    }

    /// <summary>Adds one reply's time in <paramref name="directory"/>'s model-speed.json. True when saved.</summary>
    public static bool Record(string? directory, string host, string model, int ms, DateTimeOffset at)
    {
        if (directory is null) return false;
        lock (Gate)
        {
            var known = Load(directory);
            var next = known.With(host, model, ms, at);
            return !ReferenceEquals(next, known) && next.Save(directory);
        }
    }

    // Replies end one at a time, but two saves must never read the same file and lose a reply.
    private static readonly object Gate = new();

    private static bool SameModel(string a, string b) => string.Equals(Tagged(a), Tagged(b), StringComparison.OrdinalIgnoreCase);

    private static string Tagged(string name) => name.Contains(':', StringComparison.Ordinal) || name.Contains('/', StringComparison.Ordinal)
        ? name : name + ":latest";

    private static bool Valid(MeasuredFirstWord? m) => m is { Host.Length: > 0 and <= 2048, Model.Length: > 0 and <= 256, Samples.Count: > 0 and <= MaximumSamples } &&
        m.Samples.All(s => s is > 0 and <= MaximumMs);

    public static MeasuredFirstWords Load(string? directory)
    {
        if (directory is null) return new();
        try
        {
            var path = Path.Combine(directory, FileName);
            if (!File.Exists(path) || new FileInfo(path).Length > 262_144) return new();
            var loaded = JsonSerializer.Deserialize<MeasuredFirstWords>(File.ReadAllText(path));
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
            var temporary = Path.Combine(directory, $"model-speed.{Guid.NewGuid():N}.tmp");
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
