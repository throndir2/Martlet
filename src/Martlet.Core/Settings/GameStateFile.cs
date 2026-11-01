using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Martlet.Core.Contracts;
using Martlet.Core.Sync;

namespace Martlet.Core.Settings;

/// <summary>The fields of a game's state (docs/CONVERSATION.md, "Where you are in a game"): their names, the labels the model and
/// the context board use, and how long a value may be. <see cref="All"/> is the order of the board line: place and goal first.</summary>
public static class GameFields
{
    public const string Place = "place", Goal = "goal", Doing = "doing", CharacterName = "character_name", CharacterClass = "character_class",
        CharacterLevel = "character_level", Build = "build", Equipment = "equipment", Lately = "lately", Progress = "progress";

    /// <summary>Every field, in the board line's order.</summary>
    public static IReadOnlyList<string> All { get; } =
        [Place, Goal, Doing, CharacterName, CharacterClass, CharacterLevel, Build, Equipment, Lately, Progress];

    /// <summary>Fields that change from minute to minute: an old value of one is not shown again (see GameProgress.EarlierAge).</summary>
    public static bool Fleeting(string field) => field is Doing or Lately;

    /// <summary>The most characters of a value.</summary>
    public static int MaximumCharacters(string field) => field == Lately ? 300 : 160;

    /// <summary>The label the model writes and the board shows, such as "Where" for <see cref="Place"/>.</summary>
    public static string Label(string field) => field switch
    {
        Place => "Where",
        Goal => "Goal",
        Doing => "Doing",
        CharacterName => "Character",
        CharacterClass => "Class",
        CharacterLevel => "Level",
        Build => "Build",
        Equipment => "Equipment",
        Lately => "Lately",
        Progress => "Progress",
        _ => throw new ArgumentOutOfRangeException(nameof(field), field, "Not a game state field.")
    };

    /// <summary>The field a label or a field name means ("Where", "place", "Gear"...), or null.</summary>
    public static string? Find(string? label)
    {
        var key = (label ?? "").Trim().Trim('*', '"', ' ').Replace(' ', '_').ToLowerInvariant();
        return key switch
        {
            "where" or "place" or "location" or "area" => Place,
            "goal" or "quest" or "objective" or "mission" => Goal,
            "doing" or "now" or "activity" => Doing,
            "character" or "character_name" or "name" or "player" or "player_character" => CharacterName,
            "class" or "character_class" or "job" or "role" or "race" => CharacterClass,
            "level" or "character_level" or "lvl" => CharacterLevel,
            "build" or "skills" or "stats" => Build,
            "equipment" or "gear" or "items" or "weapons" => Equipment,
            "lately" or "recent" or "events" => Lately,
            "progress" => Progress,
            _ => null
        };
    }
}

/// <summary>One fact of a game's state: its value, when it was seen or set, and whether the user set it (the user's word wins over
/// what the screen showed).</summary>
public sealed record GameFact(string Value, DateTimeOffset At, bool ByUser = false);

/// <summary>One playthrough of a game: its name, whether it is the game's current playthrough, when it last changed and its facts
/// by field (<see cref="GameFields"/>). A missing field is unknown.</summary>
public sealed record GamePlaythrough(string Game, string Playthrough, bool Current, DateTimeOffset UpdatedAt,
    IReadOnlyDictionary<string, GameFact> Facts);

/// <summary>The saved game state (game-state.json in the data folder, the account's working copy): each game's playthroughs and
/// their facts, and whether Martlet saves it at all. It travels between the owner's computers as the account setting
/// <see cref="SharedKey"/>. Only <c>GameProgress</c> (Martlet.Conversation) changes it; this type owns its path, bounds, load, save
/// and shared section. With <see cref="Save"/> off, the file keeps no games.</summary>
public sealed record GameStateFile
{
    public const string FileName = "game-state.json";
    public const string SharedKey = "game-state";
    public const int SchemaVersion1 = 1;
    /// <summary>The name of a game's first playthrough.</summary>
    public const string MainPlaythrough = "main";
    public const int MaximumGames = 24, MaximumPlaythroughsPerGame = 4, MaximumGameCharacters = 80, MaximumPlaythroughCharacters = 40;

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        MaxDepth = 8
    };

    public int SchemaVersion { get; init; } = SchemaVersion1;
    /// <summary>Whether Martlet saves the game state (Companion › App guides › Game state; on by default).</summary>
    public bool Save { get; init; } = true;
    public IReadOnlyList<GamePlaythrough> Playthroughs { get; init; } = [];

    public static string PathOf(string directory) => Path.Combine(directory, FileName);

    /// <summary>A short key for a game and playthrough, for automation IDs: lower-case letters, digits and '-'.</summary>
    public static string KeyOf(string game, string? playthrough = null)
    {
        var text = new StringBuilder();
        foreach (var c in (playthrough is null ? game : game + "-" + playthrough).ToLowerInvariant())
            if (c is >= 'a' and <= 'z' or >= '0' and <= '9') text.Append(c);
            else if (text.Length > 0 && text[^1] != '-') text.Append('-');
        var key = text.ToString().Trim('-');
        return key.Length == 0 ? "game" : key.Length <= 48 ? key : key[..48].TrimEnd('-');
    }

    /// <summary>The saved state, or the default (saving on, no games) when there is none or it can't be read.</summary>
    public static GameStateFile Load(string directory) => TryLoad(directory) ?? new();

    /// <summary>The saved state, or null when there is none or it can't be read (or a newer Martlet wrote it).</summary>
    public static GameStateFile? TryLoad(string directory)
    {
        try { return Parse(File.ReadAllText(PathOf(directory))); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>Saves <paramref name="state"/> in one step (a temporary file, then a rename). With saving off, no games are written.</summary>
    public static void Write(string directory, GameStateFile state)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (!state.Save) state = state with { Playthroughs = [] };
        state.Validate();
        Directory.CreateDirectory(directory);
        var path = PathOf(directory);
        var temporary = Path.Combine(directory, $"game-state.{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllText(temporary, state.ToJson());
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
        SettingsFiles.NotifySaved(path);
    }

    /// <summary>The canonical JSON form, as it is saved and shared.</summary>
    public string ToJson() => JsonSerializer.Serialize(this, Json);

    /// <summary>Reads a state, or null when it isn't one this Martlet can use (a newer Martlet's, or broken).</summary>
    public static GameStateFile? Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            var state = JsonSerializer.Deserialize<GameStateFile>(json, Json);
            if (state is null) return null;
            state = state with
            {
                Playthroughs = [.. (state.Playthroughs ?? []).Select(p => p with { Facts = p.Facts ?? new Dictionary<string, GameFact>() })]
            };
            state.Validate();
            return state;
        }
        catch (Exception error) when (error is JsonException or ContractException or NotSupportedException or ArgumentException) { return null; }
    }

    public void Validate()
    {
        ContractRules.Require(SchemaVersion == SchemaVersion1, "The game state was written by a newer Martlet.");
        var games = Playthroughs.GroupBy(p => p.Game, StringComparer.OrdinalIgnoreCase).ToArray();
        ContractRules.Require(games.Length <= MaximumGames, "The game state keeps too many games.");
        foreach (var game in games)
        {
            ContractRules.Require(game.Count() <= MaximumPlaythroughsPerGame && game.Count(p => p.Current) <= 1 &&
                game.Select(p => p.Playthrough).Distinct(StringComparer.OrdinalIgnoreCase).Count() == game.Count(),
                "A game in the game state has too many playthroughs, two current ones or two with one name.");
            foreach (var playthrough in game)
            {
                ContractRules.Require(Name(playthrough.Game, MaximumGameCharacters) && Name(playthrough.Playthrough, MaximumPlaythroughCharacters),
                    "A game or playthrough name in the game state is empty, too long or has control characters.");
                ContractRules.Require(playthrough.Facts.All(f => GameFields.All.Contains(f.Key) && f.Value is { } fact &&
                    Name(fact.Value, GameFields.MaximumCharacters(f.Key))), "A fact in the game state is unknown, empty or too long.");
            }
        }

        static bool Name(string? text, int maximum) =>
            text is { Length: > 0 } && text.Length <= maximum && !string.IsNullOrWhiteSpace(text) && !text.Any(char.IsControl);
    }

    /// <summary>The account setting <see cref="SharedKey"/> backed by game-state.json in <paramref name="directory"/>.
    /// <paramref name="followed"/> runs after this computer took another computer's state (or an account's, on a switch).</summary>
    public static ISharedSection Section(string directory, Action? followed = null) => new DelegateSection(SharedKey, "Game state",
        _ =>
        {
            var saved = TryLoad(directory);
            var path = PathOf(directory);
            DateTimeOffset? changed = File.Exists(path) ? new DateTimeOffset(File.GetLastWriteTimeUtc(path), TimeSpan.Zero) : null;
            return Task.FromResult<SharedLocal?>(new((saved ?? new()).ToJson(), null, saved is null, changed));
        },
        (setting, _) =>
        {
            if (Parse(setting.Value) is not { } state)
                return Task.FromResult(SharedApply.Waiting("It was written by a newer Martlet; update this PC to use it."));
            try { Write(directory, state); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                return Task.FromResult(SharedApply.Waiting("It couldn't be saved on this PC."));
            }
            followed?.Invoke();
            return Task.FromResult(SharedApply.Done);
        },
        () => new GameStateFile().ToJson());

    public override string ToString() =>
        $"GameStateFile {{ Save = {Save}, Games = {Playthroughs.Select(p => p.Game).Distinct(StringComparer.OrdinalIgnoreCase).Count()}, " +
        $"Playthroughs = {Playthroughs.Count} }}";
}
