using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Martlet.Core.Contracts;
using Martlet.Core.Sync;

namespace Martlet.Core.Settings;

/// <summary>Finding by meaning (docs/SEARCH.md, "Finding by meaning"): whether Martlet uses an embedding model away from the
/// reply's first word (when remembering and tidying find related facts, in the memory find tools and in search_guide) and which
/// one. Kept in meaning-search.json in Martlet's data folder and shared between the owner's computers as the household setting
/// <see cref="SharedKey"/>, so every computer makes the same choice. The vectors themselves are derived data that each computer
/// makes for itself.</summary>
public sealed partial record MeaningSearchFile
{
    public const string FileName = "meaning-search.json";
    public const string SharedKey = "meaning-search";
    public const int SchemaVersion1 = 1;
    /// <summary>No embeddings: memory, guides and reports are found by their words only.</summary>
    public const string Off = "off";
    /// <summary>The model in Ollama on this PC, on its processor only, so it never takes graphics memory from the conversation's
    /// model.</summary>
    public const string ThisPc = "this-pc";
    public const string DefaultModel = "nomic-embed-text";
    /// <summary>The download size of <see cref="DefaultModel"/> in Ollama, for the question before it is downloaded.</summary>
    public const string DefaultModelSize = "274 MB";

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        MaxDepth = 4
    };

    public int SchemaVersion { get; init; } = SchemaVersion1;
    /// <summary><see cref="ThisPc"/> (the default) or <see cref="Off"/>.</summary>
    public string Place { get; init; } = ThisPc;
    /// <summary>The Ollama model's name.</summary>
    public string Model { get; init; } = DefaultModel;

    [JsonIgnore] public bool On => Place != Off;

    public static string PathOf(string directory) => Path.Combine(directory, FileName);

    /// <summary>The saved choice, or the default (on, <see cref="DefaultModel"/> on this PC) when there is none or it can't be read.</summary>
    public static MeaningSearchFile Load(string directory) => TryLoad(directory) ?? new();

    public static MeaningSearchFile? TryLoad(string directory)
    {
        try { return Parse(File.ReadAllText(PathOf(directory))); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>Saves <paramref name="choice"/> in one step (a temporary file, then a rename).</summary>
    public static void Save(string directory, MeaningSearchFile choice)
    {
        ArgumentNullException.ThrowIfNull(choice);
        choice.Validate();
        Directory.CreateDirectory(directory);
        var temporary = Path.Combine(directory, $"meaning-search.{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllText(temporary, choice.Write());
            File.Move(temporary, PathOf(directory), overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    public string Write() => JsonSerializer.Serialize(this, Json);

    /// <summary>Reads a choice, or null when it isn't one this Martlet can use (a newer Martlet's, or broken).</summary>
    public static MeaningSearchFile? Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            var choice = JsonSerializer.Deserialize<MeaningSearchFile>(json, Json);
            choice?.Validate();
            return choice;
        }
        catch (Exception error) when (error is JsonException or ContractException or NotSupportedException) { return null; }
    }

    /// <summary>Whether <paramref name="model"/> is a usable Ollama model name (letters, digits, <c>._:/-</c>).</summary>
    public static bool IsModelName(string? model) => model is { Length: > 0 and <= 128 } && ModelName().IsMatch(model);

    public void Validate()
    {
        ContractRules.Require(SchemaVersion == SchemaVersion1, "Finding by meaning was set up by a newer Martlet.");
        ContractRules.Require(Place is Off or ThisPc, "Finding by meaning names an unknown place.");
        ContractRules.Require(IsModelName(Model), "Finding by meaning names a model Ollama can't have.");
    }

    /// <summary>The shared setting <see cref="SharedKey"/> backed by meaning-search.json in <paramref name="directory"/>.
    /// <paramref name="followed"/> runs after this computer took another computer's choice.</summary>
    public static ISharedSection Section(string directory, Action? followed = null) => new DelegateSection(SharedKey, "Finding by meaning",
        _ =>
        {
            var saved = TryLoad(directory);
            var path = PathOf(directory);
            DateTimeOffset? changed = File.Exists(path) ? new DateTimeOffset(File.GetLastWriteTimeUtc(path), TimeSpan.Zero) : null;
            return Task.FromResult<SharedLocal?>(new((saved ?? new()).Write(), null, saved is null, changed));
        },
        (setting, _) =>
        {
            if (Parse(setting.Value) is not { } choice)
                return Task.FromResult(SharedApply.Waiting("It was chosen on a newer Martlet; update this PC to use it."));
            try { Save(directory, choice); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                return Task.FromResult(SharedApply.Waiting("It couldn't be saved on this PC."));
            }
            followed?.Invoke();
            return Task.FromResult(SharedApply.Done);
        },
        () => new MeaningSearchFile().Write());

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9._:/\-]*$", RegexOptions.CultureInvariant)]
    private static partial Regex ModelName();
}
