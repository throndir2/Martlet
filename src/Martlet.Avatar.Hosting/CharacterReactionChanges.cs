using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Martlet.Core.Contracts;

namespace Martlet.Avatar.Hosting;

/// <summary>One change the character made itself to how it reacts to touches (the How I react check-in, with the Touch reactions
/// tools), for one persona, until <see cref="Until"/> or until someone undoes it. It never edits the owner's zones or temperament:
/// it applies over them while it lasts (<see cref="CharacterReactionChanges.Temperament"/>, <see cref="CharacterReactionChanges.Zone"/>).
/// <see cref="Kind"/> <see cref="KindFeeling"/>: how it feels about a category or a zone (<see cref="Attitude"/>, and the reaction
/// words it plays there, <see cref="Words"/>; null: the attitude's own); <see cref="KindReactions"/>: what one zone plays
/// (<see cref="Reactions"/>, entries as a zone's reaction list holds them); <see cref="KindMood"/>: every touch
/// <see cref="Shift"/> steps more (or less) liked. <see cref="Why"/> is the character's own short reason, shown to the owner on
/// Companion › Touch.</summary>
public sealed record CharacterReactionChange
{
    public const string KindFeeling = "feeling", KindReactions = "reactions", KindMood = "mood";
    public const string ByOwner = "owner", ByCharacter = "character", ByReplaced = "replaced", ByReset = "reset";
    public required string Id { get; init; }
    public required Guid PersonaId { get; init; }
    public required string Kind { get; init; }
    /// <summary>A category (<see cref="CharacterTouchTemperaments.GroupIds"/>), a zone ID, or <see cref="CharacterReactionChanges.All"/>
    /// for a mood.</summary>
    public required string Target { get; init; }
    public int? Attitude { get; init; }
    public IReadOnlyList<string>? Words { get; init; }
    public IReadOnlyList<string>? Reactions { get; init; }
    public int Shift { get; init; }
    public required string Why { get; init; }
    public required DateTimeOffset At { get; init; }
    public required DateTimeOffset Until { get; init; }
    /// <summary>The check-in that made it.</summary>
    public string? By { get; init; }
    /// <summary>When that check-in's run started, so a run's changes can be counted.</summary>
    public DateTimeOffset? Run { get; init; }
    public DateTimeOffset? EndedAt { get; init; }
    /// <summary>Who ended it early: <see cref="ByOwner"/>, <see cref="ByCharacter"/>, <see cref="ByReplaced"/> (a newer change of
    /// the same kind and target) or <see cref="ByReset"/>.</summary>
    public string? EndedBy { get; init; }

    public bool ActiveAt(DateTimeOffset now) => EndedAt is null && Until > now;
    public override string ToString() => $"{nameof(CharacterReactionChange)} {Id} {Kind} {Target}";
}

/// <summary>
/// The character's own changes to how it reacts to touches (docs/CONVERSATION.md#how-i-react): kept in
/// character-reaction-changes.json on this PC, never shared, never written over the owner's zones or temperament. Each change is
/// bounded: at most <see cref="MaximumPerRun"/> in one check-in run and <see cref="MaximumPerDay"/> in a day, at most
/// <see cref="MaximumActive"/> at once, each lasts <see cref="MinimumHours"/> to <see cref="MaximumHours"/> hours, and a feeling
/// moves at most <see cref="MaximumShift"/> steps from what the owner's temperament says. The owner sees and undoes them on
/// Companion › Touch.
/// </summary>
public static class CharacterReactionChanges
{
    public const string FileName = "character-reaction-changes.json";
    /// <summary>The target of a mood: every touch.</summary>
    public const string All = "all";
    public const int MaximumPerRun = 4, MaximumPerDay = 12, MaximumActive = 8, MaximumKept = 64, MaximumShift = 2, MaximumWhy = 200;
    public const int MaximumBytes = 256 * 1024;
    public const double MinimumHours = 0.25, MaximumHours = 72, DefaultHours = 6;
    /// <summary>What a reaction list entry starts with for a voice sound ("sound:laugh").</summary>
    public const string SoundPrefix = "sound:";

    /// <summary>The persona's changes in effect at <paramref name="now"/>, oldest first.</summary>
    public static IReadOnlyList<CharacterReactionChange> Active(IReadOnlyList<CharacterReactionChange> changes, Guid personaId, DateTimeOffset now) =>
        [.. changes.Where(c => c.PersonaId == personaId && c.ActiveAt(now)).OrderBy(c => c.At)];

    /// <summary>The category of a zone: an intimate kind's is <see cref="CharacterTouchTemperaments.IntimateId"/>, a kind's body
    /// group's, and a zone special to the character the extras'.</summary>
    public static string CategoryOf(string zoneId) => CharacterTouchZones.Kind(zoneId) is { } kind
        ? CharacterTouchTemperaments.GroupOf(kind) : CharacterTouchTemperaments.GroupId(TouchZoneGroup.Extras);

    public static bool IsCategory(string target) => CharacterTouchTemperaments.GroupIds.Any(g => g.Id == target);

    private static int Clamp(int attitude) => Math.Clamp(attitude, CharacterTouchTemperaments.MinimumAttitude, CharacterTouchTemperaments.MaximumAttitude);

    /// <summary>What the owner's <paramref name="temperament"/> says about <paramref name="target"/> (a category or a zone), as an
    /// attitude: 0 (neutral) where it says nothing.</summary>
    public static int BaseAttitude(CharacterTouchTemperament? temperament, string target) => IsCategory(target)
        ? temperament?.Groups.TryGetValue(target, out var group) == true ? group.Attitude : 0
        : CharacterTouchTemperaments.Entry(temperament, target)?.Attitude ?? 0;

    /// <summary>The temperament with the <paramref name="active"/> changes over it: the feelings they set (on the category, and on
    /// each zone of it the temperament covers on its own) and a mood's shift. What the touch line says the character feels, and how
    /// repeated touches escalate, follow it. With no change in effect it is <paramref name="temperament"/> itself; with changes
    /// and no temperament (the built-in reactions), one that covers only what they changed.</summary>
    public static CharacterTouchTemperament? Temperament(CharacterTouchTemperament? temperament, IReadOnlyList<CharacterReactionChange> active, Guid personaId)
    {
        var feelings = active.Where(c => c.Kind is CharacterReactionChange.KindFeeling or CharacterReactionChange.KindMood).OrderBy(c => c.At).ToArray();
        if (feelings.Length == 0) return temperament;
        var groups = new Dictionary<string, TouchTemperamentEntry>(temperament?.Groups ?? new Dictionary<string, TouchTemperamentEntry>(), StringComparer.Ordinal);
        var zones = new Dictionary<string, TouchTemperamentEntry>(temperament?.Zones ?? new Dictionary<string, TouchTemperamentEntry>(), StringComparer.Ordinal);
        static TouchTemperamentEntry Felt(TouchTemperamentEntry entry, int attitude, IReadOnlyList<string>? words) =>
            entry with { Attitude = attitude, Reactions = words };
        foreach (var change in feelings)
        {
            if (change.Kind == CharacterReactionChange.KindMood)
            {
                // Every category shifts (an intimate one only when the temperament has it: else intimate zones follow their body group).
                foreach (var (_, id, _) in CharacterTouchTemperaments.GroupIds)
                {
                    if (!groups.TryGetValue(id, out var entry))
                    {
                        if (id == CharacterTouchTemperaments.IntimateId) continue;
                        entry = new TouchTemperamentEntry();
                    }
                    var shifted = Clamp(entry.Attitude + change.Shift);
                    if (shifted != entry.Attitude || !groups.ContainsKey(id)) groups[id] = Felt(entry, shifted, shifted == entry.Attitude ? entry.Reactions : null);
                }
                foreach (var (id, entry) in zones.ToArray())
                {
                    var shifted = Clamp(entry.Attitude + change.Shift);
                    if (shifted != entry.Attitude) zones[id] = Felt(entry, shifted, null);
                }
                continue;
            }
            var attitude = Clamp(change.Attitude ?? 0);
            if (IsCategory(change.Target))
            {
                groups[change.Target] = Felt(groups.GetValueOrDefault(change.Target) ?? new TouchTemperamentEntry(), attitude, change.Words);
                foreach (var (id, entry) in zones.ToArray())
                    if (CategoryOf(id) == change.Target) zones[id] = Felt(entry, attitude, change.Words);
            }
            else
            {
                var now = new CharacterTouchTemperament { PersonaId = personaId, Groups = groups, Zones = zones };
                zones[change.Target] = Felt(CharacterTouchTemperaments.Entry(now, change.Target) ?? new TouchTemperamentEntry(), attitude, change.Words);
            }
        }
        return (temperament ?? new CharacterTouchTemperament { PersonaId = personaId, Source = CharacterReactionChange.ByCharacter }) with
        {
            Groups = groups, Zones = zones
        };
    }

    /// <summary>The zone with the <paramref name="active"/> changes over its reaction list, or the zone itself when none covers
    /// it. The newest change that covers the zone decides: a list the character chose for it plays as chosen; a feeling about it
    /// or its category plays its reaction words (or its attitude's own) on the model; a mood that changes how much the zone is
    /// liked plays that attitude's own. <paramref name="temperament"/> is the owner's (without the changes).</summary>
    public static CharacterTouchZone Zone(CharacterTouchZone zone, CharacterTouchTemperament? temperament, IReadOnlyList<CharacterReactionChange> active,
        CharacterActionCatalog? catalog)
    {
        if (active.Count == 0) return zone;
        var category = CategoryOf(zone.Id);
        var attitude = CharacterTouchTemperaments.Entry(temperament, zone.Id)?.Attitude ?? 0;
        IReadOnlyList<string>? words = null, list = null;
        var changed = false;
        foreach (var change in active.OrderBy(c => c.At))
        {
            switch (change.Kind)
            {
                case CharacterReactionChange.KindMood:
                    var shifted = Clamp(attitude + change.Shift);
                    if (shifted == attitude) break;
                    (attitude, words, list, changed) = (shifted, null, null, true);
                    break;
                case CharacterReactionChange.KindFeeling when change.Target == zone.Id || change.Target == category:
                    (attitude, words, list, changed) = (Clamp(change.Attitude ?? attitude), change.Words, null, true);
                    break;
                case CharacterReactionChange.KindReactions when change.Target == zone.Id:
                    (list, changed) = (change.Reactions, true);
                    break;
            }
        }
        if (!changed) return zone;
        if (list is null)
        {
            if (catalog is null) return zone;
            list = [.. CharacterTouchTemperaments.Resolve(words ?? CharacterTouchTemperaments.DefaultReactions(attitude), catalog).Select(s => s.Id)];
        }
        return zone with { Reaction = zone.Reaction with { Actions = list } };
    }

    /// <summary>The zones with the <paramref name="active"/> changes over their reaction lists.</summary>
    public static CharacterTouchZoneSettings? Zones(CharacterTouchZoneSettings? settings, CharacterTouchTemperament? temperament,
        IReadOnlyList<CharacterReactionChange> active, CharacterActionCatalog? catalog) =>
        settings is null || active.Count == 0 ? settings
            : settings with { Zones = [.. settings.Zones.Select(z => Zone(z, temperament, active, catalog))] };

    /// <summary>What a category, a zone or a mood's target is called: "Head and face", the zone's name, "every touch".</summary>
    public static string Label(string target, CharacterTouchZoneSettings? zones = null) =>
        target == All ? "every touch"
            : CharacterTouchTemperaments.GroupIds.Where(g => g.Id == target).Select(g => g.Label).FirstOrDefault() is { } group ? group
            : zones?.Zones.FirstOrDefault(z => z.Id == target)?.Name ?? CharacterTouchZones.Kind(target)?.Label ?? target;

    /// <summary>What a change does, in a few words: "Head and face: dislikes (pout, sweat)", "Tail: plays Smile, sound:laugh",
    /// "Every touch: one step less liked".</summary>
    public static string Describe(CharacterReactionChange change, CharacterTouchZoneSettings? zones = null, CharacterActionCatalog? catalog = null)
    {
        var label = Label(change.Target, zones);
        label = char.ToUpperInvariant(label[0]) + label[1..];
        return change.Kind switch
        {
            CharacterReactionChange.KindMood => $"{label}: {Steps(Math.Abs(change.Shift))} {(change.Shift < 0 ? "less" : "more")} liked",
            CharacterReactionChange.KindReactions => $"{label}: plays " + (change.Reactions is { Count: > 0 } list
                ? string.Join(", ", list.Select(entry => Name(entry, catalog))) : "nothing"),
            _ => $"{label}: {CharacterTouchTemperaments.AttitudeWord(change.Attitude ?? 0)}" +
                (change.Words is { } words ? words.Count == 0 ? " (no reaction)" : $" ({string.Join(", ", words)})" : "")
        };
    }

    private static string Steps(int steps) => steps == 1 ? "one step" : $"{steps} steps";

    /// <summary>A reaction list entry's name: the emote's or motion's name on the model, or the entry itself.</summary>
    public static string Name(string entry, CharacterActionCatalog? catalog) =>
        catalog?.Entries.FirstOrDefault(e => e.Source.Id == entry) is { Source: { } source } ? source.Name : entry;

    /// <summary>When a change ends, as the owner reads it: "until 6:05 PM", "until Friday 9:00 AM".</summary>
    public static string Until(CharacterReactionChange change, DateTimeOffset now)
    {
        var until = change.Until.ToLocalTime();
        var today = now.ToLocalTime().Date;
        return "until " + (until.Date == today ? until.ToString("t", CultureInfo.CurrentCulture)
            : until.Date == today.AddDays(1) ? "tomorrow " + until.ToString("t", CultureInfo.CurrentCulture)
            : until.ToString("dddd t", CultureInfo.CurrentCulture));
    }

    /// <summary>A new change's ID: "r" and 6 hex digits.</summary>
    public static string NewId() => "r" + Convert.ToHexString(RandomNumberGenerator.GetBytes(3)).ToLowerInvariant();

    /// <summary>One line of the character's own words, at most <see cref="MaximumWhy"/> characters, or null when empty.</summary>
    public static string? Reason(string? why)
    {
        if (string.IsNullOrWhiteSpace(why)) return null;
        var line = string.Join(' ', why.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Select(w => new string(w.Where(c => !char.IsControl(c)).ToArray())))
            .Trim();
        return line.Length == 0 ? null : line.Length <= MaximumWhy ? line : line[..(MaximumWhy - 3)].TrimEnd() + "...";
    }

    /// <summary>The changes with every one of <paramref name="personaId"/>'s (all personas' when null) that is in effect ended by
    /// <paramref name="by"/> at <paramref name="now"/>; <paramref name="id"/> ends only that one.</summary>
    public static IReadOnlyList<CharacterReactionChange> End(IReadOnlyList<CharacterReactionChange> changes, Guid? personaId, string by,
        DateTimeOffset now, string? id = null) =>
        [.. changes.Select(c => c.ActiveAt(now) && (personaId is null || c.PersonaId == personaId) && (id is null || c.Id == id)
            ? c with { EndedAt = now.ToUniversalTime(), EndedBy = by } : c)];

    /// <summary>The changes with <paramref name="change"/> added: one in effect of the same persona, kind and target ends
    /// (<see cref="CharacterReactionChange.ByReplaced"/>), and only the newest <see cref="MaximumKept"/> are kept (those in
    /// effect first).</summary>
    public static IReadOnlyList<CharacterReactionChange> With(IReadOnlyList<CharacterReactionChange> changes, CharacterReactionChange change, DateTimeOffset now)
    {
        var next = changes.Select(c => c.ActiveAt(now) && c.PersonaId == change.PersonaId && c.Kind == change.Kind && c.Target == change.Target
            ? c with { EndedAt = now.ToUniversalTime(), EndedBy = CharacterReactionChange.ByReplaced } : c).Append(change).ToArray();
        return [.. next.OrderByDescending(c => c.ActiveAt(now)).ThenByDescending(c => c.At).Take(MaximumKept).OrderBy(c => c.At)];
    }

    /// <summary>Why the changes can't be saved, or null.</summary>
    public static string? Problem(IReadOnlyList<CharacterReactionChange> changes)
    {
        if (changes.Count > MaximumKept) return $"At most {MaximumKept} changes are kept.";
        foreach (var change in changes)
        {
            if (string.IsNullOrWhiteSpace(change.Id) || change.Id.Length > 16) return "A change needs a short ID.";
            if (change.Kind is not (CharacterReactionChange.KindFeeling or CharacterReactionChange.KindReactions or CharacterReactionChange.KindMood))
                return $"A change can't be \"{change.Kind}\".";
            if (string.IsNullOrWhiteSpace(change.Target) || change.Target.Length > 64) return "A change needs a target.";
            if (Reason(change.Why) is not { } why || why != change.Why) return "A change needs a one-line reason of at most " + MaximumWhy + " characters.";
            if (change.Until <= change.At || change.Until - change.At > TimeSpan.FromHours(MaximumHours) + TimeSpan.FromMinutes(1))
                return $"A change lasts at most {MaximumHours} hours.";
            if (change.Attitude is { } attitude && attitude != Clamp(attitude)) return "A change's feeling is out of range.";
            if (Math.Abs(change.Shift) > MaximumShift) return $"A mood shifts at most {MaximumShift} steps.";
            if (change.Words?.Count > CharacterTouchTemperaments.MaximumReactions || change.Words?.Any(w => CharacterTouchTemperaments.Word(w) != w) == true)
                return "A change's reaction words must be from the vocabulary.";
            if (change.Reactions?.Count > CharacterTouchZones.MaximumActions || change.Reactions?.Any(r => string.IsNullOrWhiteSpace(r) || r.Length > 200) == true)
                return $"A zone plays at most {CharacterTouchZones.MaximumActions} things.";
        }
        return null;
    }

    // ---------- storage ----------

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true,
        MaxDepth = 8
    };

    private const int FileVersion = 1;
    private sealed record Document(int Version, IReadOnlyList<CharacterReactionChange> Changes);

    public static string Path(string dataDirectory) => System.IO.Path.Combine(dataDirectory, FileName);

    /// <summary>Every change kept (in effect, and the newest that ended), oldest first; none when the file is missing,
    /// unreadable or saved by a newer Martlet. Changes that can't be read are left out.</summary>
    public static IReadOnlyList<CharacterReactionChange> Load(string dataDirectory)
    {
        try
        {
            var path = Path(dataDirectory);
            if (!File.Exists(path) || new FileInfo(path).Length > MaximumBytes) return [];
            var document = JsonSerializer.Deserialize<Document>(File.ReadAllBytes(path), Json);
            if (document is not { Version: FileVersion, Changes: { } changes }) return [];
            return [.. changes.Where(c => c is not null && Problem([c]) is null).OrderBy(c => c.At).TakeLast(MaximumKept)];
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        {
            return [];
        }
    }

    private static readonly SemaphoreSlim Gate = new(1, 1);

    /// <summary>Changes the file in one step: <paramref name="change"/> gets what it holds now and returns the changes to save
    /// (checked: a problem throws <see cref="ContractException"/> and nothing is written), or null to write nothing. Returns what
    /// the file holds afterwards.</summary>
    public static async Task<IReadOnlyList<CharacterReactionChange>> UpdateAsync(string dataDirectory,
        Func<IReadOnlyList<CharacterReactionChange>, IReadOnlyList<CharacterReactionChange>?> change, CancellationToken token = default)
    {
        await Gate.WaitAsync(token);
        try
        {
            var now = Load(dataDirectory);
            if (change(now) is not { } next) return now;
            if (Problem(next) is { } problem) throw new ContractException(ErrorCode.InvalidContract, problem);
            var bytes = JsonSerializer.SerializeToUtf8Bytes(new Document(FileVersion, next), Json);
            ContractRules.Require(bytes.Length <= MaximumBytes, "The character's reaction changes are too large.", ErrorCode.PayloadTooLarge);
            Directory.CreateDirectory(dataDirectory);
            var temporary = System.IO.Path.Combine(dataDirectory, $"character-reaction-changes.{Guid.NewGuid():N}.tmp");
            try
            {
                await File.WriteAllBytesAsync(temporary, bytes, token);
                File.Move(temporary, Path(dataDirectory), overwrite: true);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
            return next;
        }
        finally { Gate.Release(); }
    }
}
