using System.Text.Json;
using System.Text.Json.Serialization;

namespace Martlet.Core.Creations;

/// <summary>The record of the one move of the creations kept in a data folder before accounts (creations-moved.json in
/// &lt;data&gt;\accounts\): the account that got them, when, how many live creations the list had and how many asset files
/// moved.</summary>
public sealed record CreationMove
{
    public int Version { get; init; } = 1;
    public required Guid Account { get; init; }
    public required DateTimeOffset MovedAt { get; init; }
    public required int Creations { get; init; }
    public required int Assets { get; init; }
}

/// <summary>Creations per account (docs/ACCOUNTS.md): each account keeps its creations in its own folder,
/// &lt;data&gt;\accounts\&lt;32 hex&gt;\, with the same files <see cref="CreationStore"/> keeps in a data folder. The creations kept
/// in the data folder itself before accounts move once into the folder of the first account that signs in there (on an updated
/// desktop, the owner), and <see cref="MarkerFile"/> records that move.</summary>
public static class CreationAccounts
{
    public const string AccountsDirectoryName = "accounts";
    public const string MarkerFile = "creations-moved.json";
    private const int MaximumMarkerBytes = 4096;

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true
    };

    /// <summary>The folder of <paramref name="account"/>: &lt;data&gt;\accounts\&lt;32 hex&gt;.</summary>
    public static string Folder(string dataDirectory, Guid account) =>
        Path.Combine(dataDirectory, AccountsDirectoryName, account.ToString("N"));

    public static string MarkerPath(string dataDirectory) => Path.Combine(dataDirectory, AccountsDirectoryName, MarkerFile);

    /// <summary>The recorded move, or null when the creations in this data folder have not moved to an account yet.</summary>
    public static CreationMove? Moved(string dataDirectory)
    {
        try
        {
            var path = MarkerPath(dataDirectory);
            if (!File.Exists(path) || new FileInfo(path).Length > MaximumMarkerBytes) return null;
            return JsonSerializer.Deserialize<CreationMove>(File.ReadAllBytes(path), Json);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or NotSupportedException) { return null; }
    }

    /// <summary>Moves the creations kept in <paramref name="dataDirectory"/> into <paramref name="accountDirectory"/> (the folder
    /// of <paramref name="account"/>) unless they already moved, and records the move. Later accounts get nothing: they start
    /// with their own, empty creations. Returns the recorded move (an earlier one when they already moved). Throws what
    /// <see cref="CreationStore.MoveAsync"/> throws, without recording a move, so the next sign-in tries again.</summary>
    public static async Task<CreationMove> MoveDataFolderCreationsOnceAsync(string dataDirectory, Guid account, string accountDirectory,
        DateTimeOffset now, CancellationToken token)
    {
        if (Moved(dataDirectory) is { } done) return done;
        var (creations, assets) = await CreationStore.MoveAsync(dataDirectory, accountDirectory, token);
        var move = new CreationMove { Account = account, MovedAt = now.ToUniversalTime(), Creations = creations, Assets = assets };
        var path = MarkerPath(dataDirectory);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await File.WriteAllBytesAsync(temporary, JsonSerializer.SerializeToUtf8Bytes(move, Json), token);
            File.Move(temporary, path, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
        return move;
    }
}
