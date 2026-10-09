using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Martlet.Core.Contracts;

namespace Martlet.Conversation;

/// <summary>The owner's choice for a built-in check-in: on or off, how often it runs (one of <see cref="CheckIns.EveryChoices"/>,
/// in minutes), and what they changed on its card (null: the built-in default).</summary>
public sealed record CheckInChoice(bool On, int EveryMinutes)
{
    public CheckInFacts? Facts { get; init; }
    public CheckInConditions? Conditions { get; init; }
    public CheckInOutcome? Outcome { get; init; }
    public ThinkingCapability? Needs { get; init; }
    public bool? Screenshot { get; init; }
    public CheckInRecording? Recording { get; init; }
    public int? RecordingSeconds { get; init; }
    public string? Script { get; init; }
}

/// <summary>One of the owner's own check-ins (Companion › Check-ins › Your own check-ins): its name, what to check
/// (<see cref="Task"/>, sent with the Check-ins: each check prompt), what it gets to know (<see cref="Facts"/>), when it runs
/// (<see cref="Conditions"/>), what it gathers for each run (a <see cref="Screenshot"/>, a <see cref="Recording"/>, the output of
/// a <see cref="Script"/>), what a Thinking pool member must handle to take it (<see cref="Needs"/>) and what happens with its
/// answer (<see cref="Outcome"/>). It can do all that a built-in check-in does.</summary>
public sealed record CustomCheckIn
{
    /// <summary>"c1" to "c99".</summary>
    public required string Id { get; init; }
    public required string Name { get; init; }
    public bool On { get; init; }
    public int EveryMinutes { get; init; } = 30;
    public string Task { get; init; } = "";
    public CheckInFacts Facts { get; init; } = CheckInFacts.Conversation;
    public CheckInConditions Conditions { get; init; }
    public CheckInOutcome Outcome { get; init; } = CheckInOutcome.Note;
    /// <summary>What a Thinking pool member must handle besides text: <see cref="ThinkingCapability.Vision"/> (pictures) and
    /// <see cref="ThinkingCapability.Audio"/> (recordings). Only members that can are given the check-in. A screenshot adds
    /// pictures and a recording adds recordings on their own (<see cref="CheckIns.Needs(CustomCheckIn)"/>).</summary>
    public ThinkingCapability Needs { get; init; } = ThinkingCapability.Text;
    /// <summary>A picture of the screen (private windows painted over) goes with each run.</summary>
    public bool Screenshot { get; init; }
    /// <summary>The last <see cref="RecordingSeconds"/> of the microphone or of what this PC plays go with each run.</summary>
    public CheckInRecording Recording { get; init; }
    /// <summary>How long the recording is: one of <see cref="CheckIns.RecordingChoices"/>.</summary>
    public int RecordingSeconds { get; init; } = 10;
    /// <summary>A Windows PowerShell script Martlet runs on this PC before each run (empty: none); its output goes with the
    /// check. Only the owner writes it, on the Check-ins page.</summary>
    public string Script { get; init; } = "";
}

/// <summary>Companion › Check-ins on this PC (check-ins.json in the data folder; never shared, because each PC shows its own
/// character and runs its own conversation): the owner's choices for the built-in check-ins (absent: on, at their default
/// pace) and their own check-ins. A file that can't be read leaves the defaults.</summary>
public sealed record CheckInSettings
{
    public const string FileName = "check-ins.json";
    // Eight check-ins with long tasks and scripts, every character escaped (\u0027), stay well below it.
    private const int MaxFileBytes = 1024 * 1024;
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
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

    /// <summary>These settings with the built-in check-in <paramref name="id"/> on or off and its pace (what else the owner
    /// changed on its card stays).</summary>
    public CheckInSettings With(string id, bool on, int everyMinutes) =>
        With(id, (Choice(id) ?? new(on, everyMinutes)) with { On = on, EveryMinutes = everyMinutes });

    /// <summary>These settings with the owner's <paramref name="choice"/> for the built-in check-in <paramref name="id"/>.</summary>
    public CheckInSettings With(string id, CheckInChoice choice) => this with
    {
        BuiltIn = new Dictionary<string, CheckInChoice>(BuiltIn, StringComparer.Ordinal) { [id] = choice }
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
                $"A check-in runs every {CheckIns.EveryChoicesText} minutes.");
            Check(choice!.Facts ?? CheckInFacts.None, choice.Conditions ?? CheckInConditions.None, choice.Outcome ?? CheckInOutcome.Note,
                choice.Needs ?? ThinkingCapability.Text, choice.Recording ?? CheckInRecording.None, choice.RecordingSeconds ?? 10,
                choice.Script ?? "");
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
            ContractRules.Require(CheckIns.EveryChoices.Contains(checkIn.EveryMinutes), $"A check-in runs every {CheckIns.EveryChoicesText} minutes.");
            Check(checkIn.Facts, checkIn.Conditions, checkIn.Outcome, checkIn.Needs, checkIn.Recording, checkIn.RecordingSeconds, checkIn.Script);
        }
    }

    private static void Check(CheckInFacts facts, CheckInConditions conditions, CheckInOutcome outcome, ThinkingCapability needs,
        CheckInRecording recording, int recordingSeconds, string? script)
    {
        ContractRules.Require(Enum.IsDefined(outcome), "A check-in reminds Martlet, has it bring something up, turns off emotes or moves the eyes.");
        ContractRules.Require(((int)facts & ~1023) == 0, "A check-in gets only the facts Martlet offers.");
        ContractRules.Require(((int)conditions & ~1023) == 0, "A check-in waits only for the conditions Martlet offers.");
        ContractRules.Require(((int)needs & ~(int)(ThinkingCapability.Text | ThinkingCapability.Vision | ThinkingCapability.Audio)) == 0,
            "A check-in needs only text, pictures or recordings.");
        ContractRules.Require(Enum.IsDefined(recording), "A check-in records the microphone, what the PC plays or nothing.");
        ContractRules.Require(CheckIns.RecordingChoices.Contains(recordingSeconds),
            $"A check-in's recording is {string.Join(", ", CheckIns.RecordingChoices.SkipLast(1))} or {CheckIns.RecordingChoices[^1]} seconds long.");
        ContractRules.Require(script is { Length: <= CheckIns.MaximumScriptCharacters } && !script.Contains('\0'),
            $"A check-in's script is at most {CheckIns.MaximumScriptCharacters} characters.");
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
