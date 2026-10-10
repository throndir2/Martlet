using System.Text.Json;
using System.Text.Json.Serialization;

namespace Martlet.Core.Sync;

/// <summary>Which account the settings files in Martlet's data folder hold now (docs/ACCOUNTS.md, "Account settings"). The files
/// in the data folder (settings.json, talk-preferences.json, the theme, the character...) are the settings in use: those of the
/// account signed in on this computer. Each account also keeps its own copy of its settings in its folder
/// (<c>accounts\&lt;32 hex&gt;\shared-settings.json</c>), and switching account first records the files into the outgoing
/// account's copy, then gives the files the incoming account's (<see cref="SharedSettingsNode.AdoptAsync(CancellationToken)"/>).
/// <c>accounts\working-copy.json</c> names the account whose settings the files hold, so a switch cut short (Martlet closed
/// half-way) is finished at the next start rather than taken for changes made here.</summary>
public static class AccountWorkingCopy
{
    public const string DirectoryName = "accounts";
    public const string FileName = "working-copy.json";
    public const int SchemaVersion1 = 1;
    private const int MaximumBytes = 4_096;

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        RespectNullableAnnotations = true,
        MaxDepth = 4
    };

    private sealed record Marker
    {
        public required int SchemaVersion { get; init; }
        public required Guid Account { get; init; }
        public required DateTimeOffset Since { get; init; }
    }

    /// <summary>The folder of <paramref name="account"/> in the data folder <paramref name="dataDirectory"/>:
    /// <c>accounts\&lt;32 lowercase hex&gt;</c>.</summary>
    public static string Folder(string dataDirectory, Guid account)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);
        if (account == Guid.Empty) throw new ArgumentException("An account ID is required.", nameof(account));
        return Path.Combine(dataDirectory, DirectoryName, account.ToString("N"));
    }

    /// <summary>The account whose settings the files in <paramref name="dataDirectory"/> hold, and since when; null when no
    /// account was ever signed in here (the files are from before accounts) or the marker can't be read.</summary>
    public static (Guid Account, DateTimeOffset Since)? Load(string dataDirectory)
    {
        try
        {
            var path = Path.Combine(dataDirectory, DirectoryName, FileName);
            if (!File.Exists(path) || new FileInfo(path).Length > MaximumBytes) return null;
            var marker = JsonSerializer.Deserialize<Marker>(File.ReadAllBytes(path), Json);
            return marker is { SchemaVersion: SchemaVersion1 } && marker.Account != Guid.Empty ? (marker.Account, marker.Since) : null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or NotSupportedException or
            InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>Records that the files in <paramref name="dataDirectory"/> hold <paramref name="account"/>'s settings.</summary>
    public static void Save(string dataDirectory, Guid account, DateTimeOffset now)
    {
        if (account == Guid.Empty) throw new ArgumentException("An account ID is required.", nameof(account));
        var folder = Path.Combine(dataDirectory, DirectoryName);
        Directory.CreateDirectory(folder);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new Marker { SchemaVersion = SchemaVersion1, Account = account, Since = now.ToUniversalTime() }, Json);
        var path = Path.Combine(folder, FileName);
        var temporary = Path.Combine(folder, $"working-copy.{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllBytes(temporary, bytes);
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    /// <summary>Gives the first account signed in on a data folder from before accounts the settings that folder shared until now:
    /// the account entries of the household copy (<c>shared-settings.json</c>; the personalities, replies, reminders and the rest,
    /// see <see cref="SettingScopes"/>) and what this computer last saw of each, so that account's first sync goes on where the
    /// folder left off and takes nothing for a change made here. Does nothing when the account already has a copy. Returns
    /// whether it seeded one.</summary>
    public static bool Seed(string dataDirectory, string accountFolder)
    {
        if (File.Exists(Path.Combine(accountFolder, SharedSettingsState.FileName))) return false;
        if (!File.Exists(Path.Combine(dataDirectory, SharedSettingsState.FileName))) return false;
        var (document, observed) = SharedSettingsState.Load(dataDirectory);
        SharedSettingsState.Save(accountFolder, document.WithoutSecrets().Only(SettingScopes.IsAccountKey),
            observed.Where(o => SettingScopes.IsAccountKey(o.Key)).ToDictionary(o => o.Key, o => o.Value, StringComparer.Ordinal));
        return true;
    }
}
