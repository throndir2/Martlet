using System.Text.Json;

namespace Martlet.Avatar.Hosting;

/// <summary>A VRoid Hub look this PC used: the model's name, creator, credit and conditions of use, so Martlet can show them
/// with the character without asking VRoid Hub each time. Only these words are kept; the model file itself never is.</summary>
public sealed record VRoidHubLook(string Id, string Name, string Author, string PageUrl, IReadOnlyList<string> Conditions, bool CreditRequired,
    string? ThumbnailUrl, DateTimeOffset UsedAt)
{
    public string Path => RemoteCharacterModels.VRoidHubPath(Id);
    public string Credit => $"{Name} by {Author} (VRoid Hub)";

    public static VRoidHubLook From(CharacterModelCandidate candidate, DateTimeOffset now) =>
        new(candidate.Id, candidate.Name, candidate.Author, candidate.PageUrl, candidate.Conditions, candidate.CreditRequired, candidate.ThumbnailUrl, now);
}

/// <summary>The VRoid Hub looks used on the owner's computers (vroid-hub-looks.json in the data folder), newest first: the same on
/// all of them (the household setting <see cref="SharedKey"/>), so a VRoid Hub character shows its credit on every computer.</summary>
public static class VRoidHubLooks
{
    public const string FileName = "vroid-hub-looks.json";
    public const string SharedKey = "vroid-hub-looks";
    public const int MaximumLooks = 64;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private static readonly JsonSerializerOptions ShareJson = new(JsonSerializerDefaults.Web);
    private static readonly object Gate = new();

    /// <summary>The looks as they travel: compact, newest first.</summary>
    public static string Share(string dataDirectory) => JsonSerializer.Serialize(Load(dataDirectory), ShareJson);

    /// <summary>Takes another computer's looks (<see cref="Share"/>'s text) in place of this PC's. False when they can't be read.</summary>
    public static bool ReplaceAll(string dataDirectory, string shared)
    {
        VRoidHubLook[]? looks;
        try { looks = JsonSerializer.Deserialize<VRoidHubLook[]>(shared, Json); }
        catch (JsonException) { return false; }
        if (looks is null) return false;
        lock (Gate) Write(dataDirectory, [.. Valid(looks).OrderByDescending(l => l.UsedAt).Take(MaximumLooks)]);
        return true;
    }

    private static IEnumerable<VRoidHubLook> Valid(IEnumerable<VRoidHubLook> looks) =>
        looks.Where(l => l is not null && RemoteCharacterModels.IsVRoidHubId(l.Id) && l.Name is { Length: > 0 } && l.Conditions is not null);

    public static IReadOnlyList<VRoidHubLook> Load(string? dataDirectory)
    {
        if (dataDirectory is null) return [];
        try
        {
            var path = System.IO.Path.Combine(dataDirectory, FileName);
            if (!File.Exists(path) || new FileInfo(path).Length > 1024 * 1024) return [];
            return JsonSerializer.Deserialize<VRoidHubLook[]>(File.ReadAllBytes(path), Json) is { } looks ? [.. Valid(looks)] : [];
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException) { return []; }
    }

    public static VRoidHubLook? Find(string? dataDirectory, string? path) =>
        RemoteCharacterModels.VRoidHubId(path) is { } id ? Load(dataDirectory).FirstOrDefault(l => l.Id == id) : null;

    public static void Remember(string? dataDirectory, VRoidHubLook look)
    {
        if (dataDirectory is null) return;
        lock (Gate) Write(dataDirectory, Load(dataDirectory).Where(l => l.Id != look.Id).Prepend(look).OrderByDescending(l => l.UsedAt).Take(MaximumLooks).ToArray());
    }

    public static void Forget(string? dataDirectory, string id)
    {
        if (dataDirectory is null) return;
        lock (Gate) Write(dataDirectory, Load(dataDirectory).Where(l => l.Id != id).ToArray());
    }

    private static void Write(string dataDirectory, IReadOnlyList<VRoidHubLook> looks)
    {
        Directory.CreateDirectory(dataDirectory);
        var path = System.IO.Path.Combine(dataDirectory, FileName);
        var temporary = path + $".{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllBytes(temporary, JsonSerializer.SerializeToUtf8Bytes(looks, Json));
            File.Move(temporary, path, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
