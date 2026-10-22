using System.Text.Json;
using System.Text.Json.Serialization;
using Martlet.Core.Contracts;
using Martlet.Core.Sync;

namespace Martlet.Core.Settings;

/// <summary>Show advanced settings (docs/UI_DESIGN.md, "Essentials and advanced"): whether the desktop shows every page or only
/// the pages a regular user needs. Off by default. Kept in advanced-settings.json in Martlet's data folder and shared between the
/// person's computers as the account setting <see cref="SharedKey"/>, so the same pages show on every computer they use.</summary>
public sealed record AdvancedSettingsFile
{
    public const string FileName = "advanced-settings.json";
    public const string SharedKey = "advanced-settings";
    public const int SchemaVersion1 = 1;

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        MaxDepth = 4
    };

    public int SchemaVersion { get; init; } = SchemaVersion1;
    /// <summary>Show every page (true) or only the essentials (false, the default).</summary>
    public bool Show { get; init; }

    public static string PathOf(string directory) => Path.Combine(directory, FileName);

    /// <summary>The saved choice, or the default (only the essentials) when there is none or it can't be read.</summary>
    public static AdvancedSettingsFile Load(string? directory) => directory is null ? new() : TryLoad(directory) ?? new();

    public static AdvancedSettingsFile? TryLoad(string directory)
    {
        try { return Parse(File.ReadAllText(PathOf(directory))); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>Saves <paramref name="choice"/> in one step (a temporary file, then a rename).</summary>
    public static void Save(string directory, AdvancedSettingsFile choice)
    {
        ArgumentNullException.ThrowIfNull(choice);
        choice.Validate();
        Directory.CreateDirectory(directory);
        var temporary = Path.Combine(directory, $"advanced-settings.{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllText(temporary, choice.Write());
            File.Move(temporary, PathOf(directory), overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
        SettingsFiles.NotifySaved(PathOf(directory));
    }

    public string Write() => JsonSerializer.Serialize(this, Json);

    /// <summary>Reads a choice, or null when it isn't one this Martlet can use (a newer Martlet's, or broken).</summary>
    public static AdvancedSettingsFile? Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            var choice = JsonSerializer.Deserialize<AdvancedSettingsFile>(json, Json);
            choice?.Validate();
            return choice;
        }
        catch (Exception error) when (error is JsonException or ContractException or NotSupportedException) { return null; }
    }

    public void Validate() => ContractRules.Require(SchemaVersion == SchemaVersion1, "Show advanced settings was set by a newer Martlet.");

    /// <summary>The shared setting <see cref="SharedKey"/> backed by advanced-settings.json in <paramref name="directory"/>.
    /// <paramref name="followed"/> runs after this computer took another computer's choice.</summary>
    public static ISharedSection Section(string directory, Action? followed = null) => new DelegateSection(SharedKey, "Show advanced settings",
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
        () => new AdvancedSettingsFile().Write());
}
