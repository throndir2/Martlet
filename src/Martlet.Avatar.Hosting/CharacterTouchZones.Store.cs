using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Martlet.Core.Contracts;

namespace Martlet.Avatar.Hosting;

/// <summary>The JSON store (character-touch-zones.json): loading, checking and saving the per-model settings, and the copy shared with the owner's other computers.</summary>
public static partial class CharacterTouchZones
{
    // ---------- storage ----------

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true,
        MaxDepth = 10
    };

    private sealed record Document(int Version, IReadOnlyList<CharacterTouchZoneSettings> Models);

    // Version 2: Martlet notices is on by default. Version 1 files are still read (UpgradedFromVersion1).
    private const int DocumentVersion = 2;

    public static string Path(string dataDirectory) => System.IO.Path.Combine(dataDirectory, FileName);

    /// <summary>Where the snapshot the model's zones were found in is kept (a PNG).</summary>
    public static string SnapshotPath(string dataDirectory, string modelId) =>
        System.IO.Path.Combine(dataDirectory, SnapshotFolder, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(modelId)))[..24].ToLowerInvariant() + ".png");

    public static CharacterTouchZoneSettings? Load(string dataDirectory, string modelId) =>
        LoadAll(dataDirectory).FirstOrDefault(m => m.ModelId == modelId);

    public static IReadOnlyList<CharacterTouchZoneSettings> LoadAll(string dataDirectory)
    {
        try
        {
            var path = Path(dataDirectory);
            if (!File.Exists(path) || new FileInfo(path).Length > MaximumListBytes) return [];
            var document = JsonSerializer.Deserialize<Document>(File.ReadAllBytes(path), Json);
            if (document is not { Version: 1 or DocumentVersion, Models: { } models }) return [];
            var valid = models.Where(m => m is { ModelId: not null, Zones: not null } && m.Zones.All(z => z is { Id: not null, Box: not null } &&
                (z.Areas is null || z.Areas.All(a => a is { Box: not null, Drawables: not null, Bones: not null, Nodes: not null }))));
            return document.Version == 1 ? [.. valid.Select(UpgradedFromVersion1)] : [.. valid];
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or NotSupportedException) { return []; }
    }

    /// <summary>A model saved before Martlet notices was on by default: when the owner never turned it on for any of its zones,
    /// it is on for all of them now (the old default was off); otherwise the owner's choices stay.</summary>
    public static CharacterTouchZoneSettings UpgradedFromVersion1(CharacterTouchZoneSettings model) => model.Zones.Any(z => z.Reaction.Notices)
        ? model : model with { Zones = [.. model.Zones.Select(z => z with { Reaction = z.Reaction with { Notices = true } })] };

    /// <summary>Why <paramref name="settings"/> can't be saved, or null.</summary>
    public static string? Problem(CharacterTouchZoneSettings settings)
    {
        if (string.IsNullOrWhiteSpace(settings.ModelId)) return "The model is unknown.";
        if (settings.Zones.Count > MaximumZones) return $"A model can have at most {MaximumZones} zones.";
        foreach (var zone in settings.Zones)
        {
            if (string.IsNullOrWhiteSpace(zone.Id) || zone.Id.Length > 40) return "A zone needs a short ID.";
            if (!zone.Box.Valid) return $"{zone.Name}'s box must lie inside the picture.";
            if (zone.Areas is { Count: > MaximumAreas }) return $"A zone can have at most {MaximumAreas} areas.";
            if (zone.Areas?.Any(a => a?.Box is not { Valid: true }) == true) return $"{zone.Name}'s areas must lie inside the picture.";
            if (zone.Follows is { Length: > 256 }) return "What a zone follows needs a shorter name.";
            if (zone.Label is { Length: > MaximumLabelLength }) return $"A zone's name can be at most {MaximumLabelLength} characters.";
            if (zone.Reaction.Narration is { Length: > MaximumNarrationLength }) return $"What a touch tells the character can be at most {MaximumNarrationLength} characters.";
            if (zone.Reaction.Actions is { Count: > MaximumActions }) return $"A zone plays at most {MaximumActions} reactions.";
            if (zone.Reaction.Actions?.Any(a => string.IsNullOrWhiteSpace(a) || a.Length > CharacterTouchReaction.MaximumEntryLength) == true)
                return $"{zone.Name}'s reactions need short names.";
            if (!double.IsFinite(zone.Reaction.CooldownSeconds) || zone.Reaction.CooldownSeconds is < 0 or > CharacterTouchReaction.MaximumCooldown)
                return $"A zone's rest must be 0 to {CharacterTouchReaction.MaximumCooldown:0} seconds.";
        }
        return settings.Zones.Select(z => z.Id).Distinct(StringComparer.Ordinal).Count() == settings.Zones.Count ? null : "Two zones have the same ID.";
    }

    private static readonly SemaphoreSlim Gate = new(1, 1);

    public static async Task<CharacterTouchZoneSettings> SaveAsync(string dataDirectory, CharacterTouchZoneSettings settings, DateTimeOffset now,
        CancellationToken token = default)
    {
        if (Problem(settings) is { } problem) throw new ContractException(ErrorCode.InvalidContract, problem);
        settings = settings with { UpdatedAt = now.ToUniversalTime() };
        await Gate.WaitAsync(token);
        try
        {
            var models = LoadAll(dataDirectory).Where(m => m.ModelId != settings.ModelId).Append(settings)
                .OrderByDescending(m => m.UpdatedAt).Take(MaximumModels).OrderBy(m => m.ModelId, StringComparer.Ordinal).ToArray();
            var bytes = JsonSerializer.SerializeToUtf8Bytes(new Document(DocumentVersion, models), Json);
            ContractRules.Require(bytes.Length <= MaximumListBytes, "The touch zones are too large.", ErrorCode.PayloadTooLarge);
            Directory.CreateDirectory(dataDirectory);
            var temporary = System.IO.Path.Combine(dataDirectory, $"character-touch-zones.{Guid.NewGuid():N}.tmp");
            try
            {
                await File.WriteAllBytesAsync(temporary, bytes, token);
                File.Move(temporary, Path(dataDirectory), overwrite: true);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
            return settings;
        }
        finally { Gate.Release(); }
    }

    /// <summary>Saves the settings of every model at once (another computer's touch zones, <see cref="ReplaceAllAsync"/>).</summary>
    private static async Task WriteAllAsync(string dataDirectory, IReadOnlyList<CharacterTouchZoneSettings> models, CancellationToken token)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new Document(DocumentVersion, models), Json);
        ContractRules.Require(bytes.Length <= MaximumListBytes, "The touch zones are too large.", ErrorCode.PayloadTooLarge);
        Directory.CreateDirectory(dataDirectory);
        var temporary = System.IO.Path.Combine(dataDirectory, $"character-touch-zones.{Guid.NewGuid():N}.tmp");
        try
        {
            await File.WriteAllBytesAsync(temporary, bytes, token);
            File.Move(temporary, Path(dataDirectory), overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    // ---------- shared with the owner's other computers ----------

    /// <summary>The household setting that carries every model's touch zones (found by vision, guessed, or drawn and changed by
    /// the owner) to the owner's other computers, so the character reacts the same on all of them and each model is found once.
    /// The pictures (the snapshot and what the vision model saw) stay on the computer that made them.</summary>
    public const string SharedKey = "character-touch-zones";
    /// <summary>The most the shared touch zones take: the newest models that fit (a model with 32 zones takes about 107 KB).</summary>
    public const int MaximumShareBytes = 8 * 1024 * 1024;
    private static readonly JsonSerializerOptions ShareJson = new(Json) { WriteIndented = false };

    /// <summary>Every model's zones as they travel: the file's document, compact, sorted by model; the newest models that fit in
    /// <see cref="MaximumShareBytes"/>.</summary>
    public static string Share(string dataDirectory)
    {
        var models = LoadAll(dataDirectory).OrderByDescending(m => m.UpdatedAt).ToList();
        while (true)
        {
            var text = JsonSerializer.Serialize(new Document(DocumentVersion, [.. models.OrderBy(m => m.ModelId, StringComparer.Ordinal)]), ShareJson);
            if (models.Count == 0 || System.Text.Encoding.UTF8.GetByteCount(text) <= MaximumShareBytes) return text;
            models.RemoveAt(models.Count - 1);
        }
    }

    /// <summary>Takes another computer's touch zones (<see cref="Share"/>'s text) in place of this PC's. False when they were saved
    /// by a newer Martlet or can't be read.</summary>
    public static async Task<bool> ReplaceAllAsync(string dataDirectory, string shared, CancellationToken token = default)
    {
        Document? document;
        try { document = JsonSerializer.Deserialize<Document>(shared, Json); }
        catch (Exception error) when (error is JsonException or NotSupportedException) { return false; }
        if (document is not { Version: 1 or DocumentVersion, Models: { } models }) return false;
        var valid = models.Where(m => m is { ModelId: not null, Zones: not null } && m.Zones.All(z => z is { Id: not null, Box: not null } &&
            (z.Areas is null || z.Areas.All(a => a is { Box: not null, Drawables: not null, Bones: not null, Nodes: not null }))) && Problem(m) is null);
        CharacterTouchZoneSettings[] kept = [.. (document.Version == 1 ? valid.Select(UpgradedFromVersion1) : valid)
            .OrderByDescending(m => m.UpdatedAt).Take(MaximumModels).OrderBy(m => m.ModelId, StringComparer.Ordinal)];
        await Gate.WaitAsync(token);
        try { await WriteAllAsync(dataDirectory, kept, token); }
        finally { Gate.Release(); }
        return true;
    }
}
