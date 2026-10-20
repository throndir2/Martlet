using System.Text.Json;

namespace Martlet.Core.Singing;

/// <summary>Companion › Singing's older choices on this PC (singing.json in the data directory; it stays on this PC: docs/CLUSTER.md,
/// "What stays with each computer"): whether Martlet sings at all
/// (<paramref name="Off"/>: the Off choice from before the pool lists; the role stays set up) and <paramref name="Host"/>, the
/// paired computer the desktop last saw running Singing (written when it checks its computers), so
/// the desktop's SongClient.IsSetUp answers without the network before the lists are made. The quality and the voice match
/// are now each pool member's own settings (pools.json, shared); <see cref="OlderVoiceMatch"/> is the voice match the page
/// chose before, read once to make the voice match list.</summary>
public sealed record SingingPreferences(string? Host = null, bool Off = false)
{
    public const string FileName = "singing.json";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    /// <summary>The voice match Companion › Singing used for every song before each computer had its own.</summary>
    public SongVoiceMatch OlderVoiceMatch { get; init; } = SongVoiceMatch.SoulX;

    private sealed record Document(int Version, string? Quality = null, string? VoiceMatch = null, string? Host = null, bool? Off = null);

    /// <summary>The saved choices, or the defaults (on, no computer yet) when none are saved or the file can't be read.</summary>
    public static SingingPreferences Load(string dataDirectory)
    {
        try
        {
            var bytes = File.ReadAllBytes(Path.Combine(dataDirectory, FileName));
            if (bytes.Length > 4_096) return new();
            var document = JsonSerializer.Deserialize<Document>(bytes, Json);
            if (document is not { Version: 1 }) return new();
            return new(document.Host is { Length: > 0 and <= 128 } host ? host : null, document.Off == true)
            {
                OlderVoiceMatch = document.VoiceMatch == "vevosing" ? SongVoiceMatch.VevoSing : SongVoiceMatch.SoulX
            };
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException) { return new(); }
    }

    public void Save(string dataDirectory)
    {
        Directory.CreateDirectory(dataDirectory);
        var path = Path.Combine(dataDirectory, FileName);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllBytes(temporary, JsonSerializer.SerializeToUtf8Bytes(new Document(1, null,
                OlderVoiceMatch == SongVoiceMatch.VevoSing ? "vevosing" : null, Host, Off ? true : null), Json));
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }
}
