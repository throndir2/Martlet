using System.Text.Json;
using System.Text.Json.Serialization;
using Martlet.Core.Contracts;
using Martlet.Core.Sync;

namespace Martlet.Core.Settings;

/// <summary>The memory brief (docs/MEMORY.md, "The memory brief"): a short profile of the people Martlet talks with, written by
/// a Thinking pool member from the remembered facts, or by the owner. It goes with the first message of each conversation. Kept
/// in memory-brief.json in Martlet's data folder (the account's working copy) and shared between the owner's computers as the
/// account setting <see cref="SharedKey"/>. <see cref="Basis"/> names the facts it was written from (their ID and revision, and
/// the name of each person), so every computer can tell when facts changed enough to write it again. The text is private: it
/// never goes to logs or status files.</summary>
public sealed record MemoryBriefFile
{
    public const string FileName = "memory-brief.json";
    public const string SharedKey = "memory-brief";
    public const int SchemaVersion1 = 1;
    /// <summary>The longest brief kept.</summary>
    public const int MaximumCharacters = 1_500;
    /// <summary>The most facts and names a brief records as its basis.</summary>
    public const int MaximumBasis = 400;
    public const int MaximumBasisEntry = 120;
    public const string ByThinking = "thinking", ByOwner = "owner";

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        MaxDepth = 8
    };

    public int SchemaVersion { get; init; } = SchemaVersion1;
    /// <summary>Whether Martlet writes the brief and sends it (Companion › Memory › Memory brief; on by default).</summary>
    public bool On { get; init; } = true;
    /// <summary>The brief, or null when none is written yet.</summary>
    public string? Text { get; init; }
    /// <summary><see cref="ByThinking"/> or <see cref="ByOwner"/>: Martlet never replaces the owner's own text on its own.</summary>
    public string? By { get; init; }
    public DateTimeOffset? WrittenAt { get; init; }
    /// <summary>How many facts it was written from.</summary>
    public int Facts { get; init; }
    /// <summary>The facts it was written from ("&lt;fact ID&gt;:&lt;revision&gt;") and the names of the people in it
    /// ("~&lt;voice ID&gt;=&lt;name&gt;").</summary>
    public IReadOnlyList<string> Basis { get; init; } = [];

    [JsonIgnore] public bool Owned => By == ByOwner;

    public static string PathOf(string directory) => Path.Combine(directory, FileName);

    /// <summary>The saved brief, or the default (on, nothing written) when there is none or it can't be read.</summary>
    public static MemoryBriefFile Load(string directory) => TryLoad(directory) ?? new();

    /// <summary>The saved brief, or null when there is none or it can't be read (or a newer Martlet wrote it).</summary>
    public static MemoryBriefFile? TryLoad(string directory)
    {
        try { return Parse(File.ReadAllText(PathOf(directory))); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>Saves <paramref name="brief"/> in one step (a temporary file, then a rename).</summary>
    public static void Save(string directory, MemoryBriefFile brief)
    {
        ArgumentNullException.ThrowIfNull(brief);
        brief.Validate();
        Directory.CreateDirectory(directory);
        var path = PathOf(directory);
        var temporary = Path.Combine(directory, $"memory-brief.{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllText(temporary, brief.Write());
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    /// <summary>The canonical JSON form, as it is saved and shared.</summary>
    public string Write() => JsonSerializer.Serialize(this with { Basis = [.. Basis] }, Json);

    /// <summary>Reads a brief, or null when it isn't one this Martlet can use (a newer Martlet's, or broken).</summary>
    public static MemoryBriefFile? Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            var brief = JsonSerializer.Deserialize<MemoryBriefFile>(json, Json);
            if (brief is null) return null;
            brief = brief with { Basis = brief.Basis ?? [] };
            brief.Validate();
            return brief;
        }
        catch (Exception error) when (error is JsonException or ContractException or NotSupportedException) { return null; }
    }

    public void Validate()
    {
        ContractRules.Require(SchemaVersion == SchemaVersion1, "The memory brief was written by a newer Martlet.");
        ContractRules.Require(Text is null || Text.Length is > 0 and <= MaximumCharacters && !Text.Any(c => char.IsControl(c) && c != '\n'),
            "The memory brief is too long or has control characters.");
        ContractRules.Require(By is null or ByThinking or ByOwner, "The memory brief names an unknown writer.");
        ContractRules.Require(Facts >= 0 && Basis is { Count: <= MaximumBasis } &&
            Basis.All(entry => entry is { Length: > 0 and <= MaximumBasisEntry } && !entry.Any(char.IsControl)),
            "The memory brief's basis is out of bounds.");
    }

    /// <summary>The shared setting <see cref="SharedKey"/> backed by memory-brief.json in <paramref name="directory"/>.
    /// <paramref name="followed"/> runs after this computer took another computer's brief (or an account's, on a switch).</summary>
    public static ISharedSection Section(string directory, Action? followed = null) => new DelegateSection(SharedKey, "Memory brief",
        _ =>
        {
            var saved = TryLoad(directory);
            var path = PathOf(directory);
            DateTimeOffset? changed = File.Exists(path) ? new DateTimeOffset(File.GetLastWriteTimeUtc(path), TimeSpan.Zero) : null;
            return Task.FromResult<SharedLocal?>(new((saved ?? new()).Write(), null, saved is null, changed));
        },
        (setting, _) =>
        {
            if (Parse(setting.Value) is not { } brief)
                return Task.FromResult(SharedApply.Waiting("It was written by a newer Martlet. Update this PC to use it."));
            try { Save(directory, brief); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                return Task.FromResult(SharedApply.Waiting("It couldn't be saved on this PC."));
            }
            followed?.Invoke();
            return Task.FromResult(SharedApply.Done);
        },
        () => new MemoryBriefFile().Write());

    public override string ToString() =>
        $"MemoryBriefFile {{ On = {On}, Text = {(Text is null ? "none" : $"[{Text.Length} characters]")}, By = {By}, Facts = {Facts} }}";
}
