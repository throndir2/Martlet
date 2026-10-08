using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Martlet.Avatar.Hosting;
using Martlet.Conversation;

namespace Martlet.Desktop;

/// <summary>What one character profile (Companion › Profiles) keeps on this PC, as the owner left it while the profile was in
/// use: where its character stands on the desktop (<see cref="Placement"/>: its spot, size, monitor and lock; null when no place
/// was saved), where it looks (<see cref="GazeUsual"/>, null for as the personality decides, and <see cref="GazeFree"/>: whether
/// it may change that in its replies) and which touches stop it while it talks (<see cref="TouchInterrupts"/>). These are the same
/// choices Martlet keeps on each PC without profiles (character-placement.json and talk-preferences.json).</summary>
internal sealed record CharacterProfileLocal(RendererPlacement? Placement, GazeMode? GazeUsual = null, bool GazeFree = true,
    TouchInterrupts TouchInterrupts = TouchInterrupts.Any);

/// <summary>What the character profiles keep on this PC (<see cref="CharacterProfileLocal"/>, by profile ID) and the profile whose
/// choices this PC uses now (<see cref="InUse"/>: the one switched to here, or followed after a switch on another computer; null
/// before any). Martlet's own place, eyes and touch choice are that profile's while Martlet is that character.</summary>
internal sealed record CharacterProfilesHere(IReadOnlyDictionary<Guid, CharacterProfileLocal> Profiles, Guid? InUse = null)
{
    internal static CharacterProfilesHere Empty { get; } = new(new Dictionary<Guid, CharacterProfileLocal>());

    /// <summary>What the profile <paramref name="id"/> keeps on this PC, or null.</summary>
    internal CharacterProfileLocal? For(Guid id) => Profiles.GetValueOrDefault(id);

    /// <summary>The same, with what the profile <paramref name="id"/> keeps set (or with null forgotten).</summary>
    internal CharacterProfilesHere With(Guid id, CharacterProfileLocal? kept)
    {
        var profiles = Profiles.ToDictionary();
        if (kept is null) profiles.Remove(id);
        else profiles[id] = kept;
        return this with { Profiles = profiles };
    }

    internal CharacterProfilesHere Using(Guid? id) => this with { InUse = id };

    /// <summary>Only what the saved <paramref name="profiles"/> keep: a removed profile's choices go, and so does its use.</summary>
    internal CharacterProfilesHere Of(IReadOnlyCollection<Guid> profiles) =>
        new(Profiles.Where(entry => profiles.Contains(entry.Key)).ToDictionary(), InUse is { } id && profiles.Contains(id) ? id : null);

    /// <summary>What changed on this PC (<paramref name="now"/>) goes to the profile Martlet is now (<paramref name="current"/>, as
    /// Companion › Profiles shows it in use): when this PC uses its choices, or when it keeps none here yet (it then takes on what
    /// this PC uses). Null when it keeps choices of its own that this PC doesn't use now (Martlet became it by hand): they stay.</summary>
    internal CharacterProfilesHere? Remember(Guid current, CharacterProfileLocal now) =>
        InUse == current || For(current) is null ? With(current, now).Using(current) : null;

    /// <summary>Switching to the profile <paramref name="id"/> (here, or on another computer): this PC uses its choices from now
    /// on. <c>Kept</c> is what it keeps here to put back, or null when it keeps nothing yet and takes on what this PC uses now
    /// (<paramref name="now"/>). What the other profiles keep stays.</summary>
    internal (CharacterProfilesHere Next, CharacterProfileLocal? Kept) Switch(Guid id, CharacterProfileLocal now) =>
        For(id) is { } kept ? (Using(id), kept) : (With(id, now).Using(id), null);
}

/// <summary>
/// What the character profiles keep on this PC (<see cref="CharacterProfilesHere"/>), saved locally in
/// character-profiles-local.json and never shared with the other Martlet computers: each computer has its own screens, and keeps
/// its own place, eyes and touch choice for each profile.
/// </summary>
internal static class CharacterProfileLocalStore
{
    internal const string FileName = "character-profiles-local.json";

    private static readonly JsonSerializerOptions Json = new()
    {
        Converters = { new JsonStringEnumConverter<TouchInterrupts>(JsonNamingPolicy.CamelCase) }
    };

    private sealed record Saved(Dictionary<Guid, CharacterProfileLocal>? Profiles, Guid? InUse = null);

    /// <summary>What the profiles keep on this PC; nothing when nothing is saved or the file can't be read. An entry with a place
    /// that isn't usable keeps its other choices without the place.</summary>
    internal static CharacterProfilesHere Load(string? directory)
    {
        if (directory is null) return CharacterProfilesHere.Empty;
        try
        {
            var path = Path.Combine(directory, FileName);
            if (!File.Exists(path)) return CharacterProfilesHere.Empty;
            var saved = JsonSerializer.Deserialize<Saved>(File.ReadAllText(path), Json);
            var profiles = (saved?.Profiles ?? []).Where(entry => entry.Key != Guid.Empty && entry.Value is not null)
                .ToDictionary(entry => entry.Key, entry => Clean(entry.Value));
            return new(profiles, saved?.InUse is { } id && id != Guid.Empty ? id : null);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        {
            ErrorLog.Warn("What your character profiles keep on this PC couldn't be read; switching profiles keeps the current place, eyes and touches.", error);
            return CharacterProfilesHere.Empty;
        }
    }

    /// <summary>Saves what the profiles keep on this PC (with nothing kept and none in use, the file goes). False when it couldn't
    /// be saved.</summary>
    internal static bool Save(string? directory, CharacterProfilesHere here)
    {
        if (directory is null) return false;
        var path = Path.Combine(directory, FileName);
        try
        {
            if (here.Profiles.Count == 0 && here.InUse is null)
            {
                File.Delete(path);
                return true;
            }
            Directory.CreateDirectory(directory);
            var temporary = Path.Combine(directory, $"character-profiles-local.{Guid.NewGuid():N}.tmp");
            try
            {
                var profiles = here.Profiles.Where(entry => entry.Key != Guid.Empty).ToDictionary(entry => entry.Key, entry => Clean(entry.Value));
                File.WriteAllText(temporary, JsonSerializer.Serialize(new Saved(profiles, here.InUse), Json));
                File.Move(temporary, path, overwrite: true);
            }
            finally
            {
                if (File.Exists(temporary)) File.Delete(temporary);
            }
            return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            ErrorLog.Warn("What a character profile keeps on this PC couldn't be saved.", error);
            return false;
        }
    }

    private static CharacterProfileLocal Clean(CharacterProfileLocal kept) => kept with
    {
        Placement = kept.Placement is { IsValid: true } place ? place : null,
        GazeUsual = kept.GazeUsual is { } usual && Enum.IsDefined(usual) ? usual : null,
        TouchInterrupts = Enum.IsDefined(kept.TouchInterrupts) ? kept.TouchInterrupts : TouchInterrupts.Any
    };
}
