using System.Text.Json;

namespace Martlet.Core.Sync;

/// <summary>The one pattern of a shared setting backed by files in the data folder (#955): its value is what the store shares, its
/// time is when the newest of its files last changed, it is default while nothing was chosen, and taking another computer's value
/// is the store's save. A section built here holds no UI: what the app does after it took a value happens once the sync finished
/// (the desktop's AfterSettingsAppliedAsync, by key), so the sections can be tested with two simulated computers.</summary>
public static class FileSection
{
    /// <summary>The newest write time of <paramref name="paths"/>, or null when none of them exists.</summary>
    public static DateTimeOffset? Changed(params string[] paths)
    {
        DateTimeOffset? newest = null;
        foreach (var path in paths)
        {
            try
            {
                if (!File.Exists(path)) continue;
                var at = new DateTimeOffset(File.GetLastWriteTimeUtc(path), TimeSpan.Zero);
                if (newest is null || at > newest) newest = at;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }
        return newest;
    }

    /// <summary>A section whose value is the store's own shared text. <paramref name="share"/> gives it (null: nothing to share here
    /// yet); <paramref name="isDefault"/> says nothing was chosen; <paramref name="apply"/> saves another computer's value and says
    /// why not when it can't (it is retried).</summary>
    public static ISharedSection Text(string key, string title, string[] paths, Func<string?> share, Func<bool> isDefault,
        Func<string, SharedApply> apply, Func<string?>? defaultValue = null) => new DelegateSection(key, title,
        _ => Task.FromResult(share() is { } value ? new SharedLocal(value, null, isDefault(), Changed(paths)) : null),
        (setting, _) => Task.FromResult(apply(setting.Value)), defaultValue);

    /// <summary><see cref="Text"/> for a store with a canonical form: <paramref name="parse"/> reads another computer's value (null:
    /// a newer Martlet's), <paramref name="save"/> saves it.</summary>
    public static ISharedSection Store<T>(string key, string title, string[] paths, Func<T> load, Func<T, string> share, Func<T, bool> isDefault,
        Func<string, T?> parse, Func<T, bool> save, Func<string?>? defaultValue = null) where T : class =>
        Text(key, title, paths, () => share(load()), () => isDefault(load()), value =>
            parse(value) is not { } taken ? Newer
            : save(taken) ? SharedApply.Done : NotSaved, defaultValue);

    /// <summary>A section whose value is a small record of <typeparamref name="T"/> as JSON (snake case, the form the desktop's own
    /// sections always shared, so a computer on an older Martlet still reads it).</summary>
    public static ISharedSection Json<T>(string key, string title, string[] paths, Func<T?> read, Func<T, bool> isDefault,
        Func<T, SharedApply> apply, T? defaultValue = null) where T : class =>
        Text(key, title, paths, () => read() is { } value ? Write(value) : null, () => read() is not { } value || isDefault(value),
            value => Read<T>(value) is { } taken ? apply(taken) : Newer, defaultValue is null ? null : () => Write(defaultValue));

    private static readonly JsonSerializerOptions Web = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower, allowIntegerValues: false) }
    };

    public static string Write<T>(T value) => JsonSerializer.Serialize(value, Web);

    /// <summary>The value, or null when it can't be read as <typeparamref name="T"/>.</summary>
    public static T? Read<T>(string? json) where T : class
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try { return JsonSerializer.Deserialize<T>(json, Web); }
        catch (Exception error) when (error is JsonException or NotSupportedException) { return null; }
    }

    public static SharedApply Newer => SharedApply.Waiting("It was chosen on a newer Martlet; update this PC to use it.");
    public static SharedApply NotSaved => SharedApply.Waiting("It couldn't be saved on this PC.");
}
