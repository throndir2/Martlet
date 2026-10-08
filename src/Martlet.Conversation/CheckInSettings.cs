using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Martlet.Core.Contracts;

namespace Martlet.Conversation;

/// <summary>The owner's choice for a built-in check-in: on or off, and how often it runs (one of
/// <see cref="CheckIns.EveryChoices"/>, in minutes).</summary>
public sealed record CheckInChoice(bool On, int EveryMinutes);

/// <summary>One of the owner's own check-ins (Companion › Check-ins › Your own check-ins): its name, what to check
/// (<see cref="Task"/>, sent with the Check-in: your own prompt), what it gets to know (<see cref="Facts"/>) and what happens
/// with its answer (<see cref="CheckInOutcome.Note"/> or <see cref="CheckInOutcome.Say"/>).</summary>
public sealed record CustomCheckIn
{
    /// <summary>"c1" to "c99".</summary>
    public required string Id { get; init; }
    public required string Name { get; init; }
    public bool On { get; init; }
    public int EveryMinutes { get; init; } = 30;
    public string Task { get; init; } = "";
    public CheckInFacts Facts { get; init; } = CheckInFacts.Conversation;
    public CheckInOutcome Outcome { get; init; } = CheckInOutcome.Note;
}

/// <summary>Companion › Check-ins on this PC (check-ins.json in the data folder; never shared, because each PC shows its own
/// character and runs its own conversation): the owner's choices for the built-in check-ins (absent: on, at their default
/// pace) and their own check-ins. A file that can't be read leaves the defaults.</summary>
public sealed record CheckInSettings
{
    public const string FileName = "check-ins.json";
    private const int MaxFileBytes = 64 * 1024;
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public int SchemaVersion { get; init; } = 1;

    /// <summary>The built-in check-ins the owner changed, by ID.</summary>
    public IReadOnlyDictionary<string, CheckInChoice> BuiltIn { get => builtIn; init => builtIn = value ?? new Dictionary<string, CheckInChoice>(); }
    private readonly IReadOnlyDictionary<string, CheckInChoice> builtIn = new Dictionary<string, CheckInChoice>();

    /// <summary>The owner's own check-ins, in the order they were added; at most <see cref="CheckIns.MaximumCustom"/>.</summary>
    public IReadOnlyList<CustomCheckIn> Custom { get => custom; init => custom = value ?? []; }
    private readonly IReadOnlyList<CustomCheckIn> custom = [];

    /// <summary>The owner's choice for the built-in check-in <paramref name="id"/>, or null (its default).</summary>
    public CheckInChoice? Choice(string id) => BuiltIn.GetValueOrDefault(id);

    /// <summary>These settings with the built-in check-in <paramref name="id"/> on or off and its pace.</summary>
    public CheckInSettings With(string id, bool on, int everyMinutes) => this with
    {
        BuiltIn = new Dictionary<string, CheckInChoice>(BuiltIn, StringComparer.Ordinal) { [id] = new(on, everyMinutes) }
    };

    /// <summary>These settings with <paramref name="checkIn"/> added, or in place of the one with its ID.</summary>
    public CheckInSettings With(CustomCheckIn checkIn) => this with
    {
        Custom = Custom.Any(c => c.Id == checkIn.Id) ? [.. Custom.Select(c => c.Id == checkIn.Id ? checkIn : c)] : [.. Custom, checkIn]
    };

    /// <summary>These settings without the owner's check-in <paramref name="id"/>.</summary>
    public CheckInSettings Without(string id) => this with { Custom = [.. Custom.Where(c => c.Id != id)] };

    /// <summary>The ID for a new check-in of the owner's: the lowest "c1".."c99" not in use, or null when all are.</summary>
    public string? NewId() =>
        Enumerable.Range(1, 99).Select(n => "c" + n).FirstOrDefault(id => Custom.All(c => c.Id != id));

    public void Validate()
    {
        ContractRules.Require(SchemaVersion == 1, "check-ins.json comes from a newer Martlet.", ErrorCode.UnsupportedVersion);
        foreach (var (id, choice) in BuiltIn)
        {
            ContractRules.Require(CheckIns.BuiltIn.Any(c => c.Id == id), $"Martlet has no check-in \"{id}\".");
            ContractRules.Require(choice is not null && CheckIns.EveryChoices.Contains(choice.EveryMinutes),
                "A check-in runs every 2, 5, 10, 15, 30, 60 or 120 minutes.");
        }
        ContractRules.Require(Custom.Count <= CheckIns.MaximumCustom, $"You can have at most {CheckIns.MaximumCustom} check-ins of your own.");
        ContractRules.Require(Custom.Select(c => c?.Id).Distinct(StringComparer.Ordinal).Count() == Custom.Count, "Two of your check-ins have the same ID.");
        foreach (var checkIn in Custom)
        {
            ContractRules.Require(checkIn is { Id: { Length: >= 2 and <= 3 } id } && id[0] == 'c' && id[1..].All(char.IsAsciiDigit) && id[1] != '0',
                "A check-in of your own has an ID from c1 to c99.");
            ContractRules.Require(checkIn.Name is { Length: > 0 and <= CheckIns.MaximumNameCharacters } name && !string.IsNullOrWhiteSpace(name) &&
                !name.Any(char.IsControl), $"A check-in's name is 1-{CheckIns.MaximumNameCharacters} characters on one line.");
            ContractRules.Require(checkIn.Task is { Length: <= CheckIns.MaximumTaskCharacters } task &&
                !task.Any(c => char.IsControl(c) && c is not '\n' and not '\r' and not '\t'),
                $"What a check-in checks is at most {CheckIns.MaximumTaskCharacters} characters.");
            ContractRules.Require(CheckIns.EveryChoices.Contains(checkIn.EveryMinutes), "A check-in runs every 2, 5, 10, 15, 30, 60 or 120 minutes.");
            ContractRules.Require(checkIn.Outcome is CheckInOutcome.Note or CheckInOutcome.Say,
                "A check-in of your own reminds Martlet in its next reply or has Martlet bring it up.");
            ContractRules.Require(((int)checkIn.Facts & ~127) == 0, "A check-in of your own gets only the facts Martlet offers.");
        }
    }

    /// <summary>The saved check-ins, or the defaults when there is no file or it can't be read.</summary>
    public static CheckInSettings Load(string? directory) => Read(directory).Settings;

    /// <summary>The saved check-ins and the file's state: none, loaded or unreadable (the defaults).</summary>
    public static (CheckInSettings Settings, string State) Read(string? directory)
    {
        if (directory is null) return (new(), "none");
        var path = Path.Combine(directory, FileName);
        try
        {
            if (!File.Exists(path)) return (new(), "none");
            if (new FileInfo(path).Length > MaxFileBytes) return (new(), "unreadable");
            var loaded = JsonSerializer.Deserialize<CheckInSettings>(File.ReadAllText(path), Json);
            if (loaded is null) return (new(), "unreadable");
            loaded.Validate();
            return (loaded, "loaded");
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or NotSupportedException or
            ContractException or ArgumentException)
        {
            return (new(), "unreadable");
        }
    }

    /// <summary>Saves check-ins.json atomically; false when the data folder can't be written.</summary>
    public bool Save(string? directory)
    {
        Validate();
        if (directory is null) return false;
        try
        {
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, FileName);
            var temporary = Path.Combine(directory, $"check-ins.{Guid.NewGuid():N}.tmp");
            try
            {
                File.WriteAllText(temporary, JsonSerializer.Serialize(this, Json));
                File.Move(temporary, path, overwrite: true);
            }
            finally
            {
                if (File.Exists(temporary)) File.Delete(temporary);
            }
            return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return false; }
    }
}
