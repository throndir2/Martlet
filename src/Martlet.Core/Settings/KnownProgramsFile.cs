using System.Text.Json;
using System.Text.Json.Serialization;
using Martlet.Core.Contracts;
using Martlet.Core.Sync;

namespace Martlet.Core.Settings;

/// <summary>One program Martlet knows (docs/APP_GUIDES.md, "Known programs"): its program file's name without .exe (as Windows
/// names the process, such as StarRail), its usual name (Honkai: Star Rail), whether it is a game, how Martlet knows
/// (<see cref="KnownProgramsFile.ByFiles"/>, <see cref="KnownProgramsFile.ByWeb"/> or <see cref="KnownProgramsFile.ByOwner"/>)
/// and when it learned it.</summary>
public sealed record KnownProgram(string Program, string Name, bool Game, string By, DateTimeOffset At);

/// <summary>The programs Martlet knows: what each program the user opens is (a game or an app) and its usual name, learned from
/// its files (a game library or a game engine), from a web search a Thinking pool member read, or from the owner. Kept in
/// known-programs.json in Martlet's data folder and shared between the owner's computers as the household setting
/// <see cref="SharedKey"/>, so a program looked up on one computer is never looked up again on another. Two computers that
/// learned different programs keep both: taking another computer's list keeps the programs only this computer knows, and the
/// next sync shares them.</summary>
public sealed record KnownProgramsFile
{
    public const string FileName = "known-programs.json";
    public const string SharedKey = "known-programs";
    public const int SchemaVersion1 = 1;
    /// <summary>The most programs kept; the oldest go first.</summary>
    public const int MaximumPrograms = 1_000;
    public const int MaximumProgram = 80;
    public const int MaximumName = 80;
    /// <summary>Known from its files: a game library folder or a game engine's files beside it.</summary>
    public const string ByFiles = "files";
    /// <summary>Known from a web search that a Thinking pool member read.</summary>
    public const string ByWeb = "web";
    /// <summary>The owner said so.</summary>
    public const string ByOwner = "owner";

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        MaxDepth = 8
    };

    public int SchemaVersion { get; init; } = SchemaVersion1;
    public IReadOnlyList<KnownProgram> Programs { get; init; } = [];

    public static string PathOf(string directory) => Path.Combine(directory, FileName);

    /// <summary>The program whose file is <paramref name="program"/> (ignoring case and a trailing .exe), or null.</summary>
    public KnownProgram? Find(string? program)
    {
        var key = Key(program);
        return key.Length == 0 ? null : Programs.FirstOrDefault(p => string.Equals(p.Program, key, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>This list with <paramref name="program"/> in place of the same program (the oldest go when it is full).</summary>
    public KnownProgramsFile With(KnownProgram program)
    {
        ArgumentNullException.ThrowIfNull(program);
        var clean = program with { Program = Key(program.Program), Name = CleanName(program.Name) };
        var rest = Programs.Where(p => !string.Equals(p.Program, clean.Program, StringComparison.OrdinalIgnoreCase));
        return this with { Programs = Trim([.. rest, clean]) };
    }

    /// <summary>Both lists in one: for a program both know, the one learned later (the owner's own choice over Martlet's).</summary>
    public static KnownProgramsFile Merge(KnownProgramsFile first, KnownProgramsFile second)
    {
        ArgumentNullException.ThrowIfNull(first);
        ArgumentNullException.ThrowIfNull(second);
        var merged = new Dictionary<string, KnownProgram>(StringComparer.OrdinalIgnoreCase);
        foreach (var program in first.Programs.Concat(second.Programs))
            if (!merged.TryGetValue(program.Program, out var known) || Wins(program, known)) merged[program.Program] = program;
        return new() { Programs = Trim(merged.Values) };

        static bool Wins(KnownProgram candidate, KnownProgram known) =>
            (candidate.By == ByOwner) != (known.By == ByOwner) ? candidate.By == ByOwner : candidate.At > known.At;
    }

    /// <summary>The saved list, or an empty one when there is none or it can't be read.</summary>
    public static KnownProgramsFile Load(string directory) => TryLoad(directory) ?? new();

    /// <summary>The saved list, or null when there is none or it can't be read (or a newer Martlet wrote it).</summary>
    public static KnownProgramsFile? TryLoad(string directory)
    {
        try { return Parse(File.ReadAllText(PathOf(directory))); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>Saves <paramref name="known"/> in one step (a temporary file, then a rename).</summary>
    public static void Save(string directory, KnownProgramsFile known)
    {
        ArgumentNullException.ThrowIfNull(known);
        known.Validate();
        Directory.CreateDirectory(directory);
        var path = PathOf(directory);
        var temporary = Path.Combine(directory, $"known-programs.{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllText(temporary, known.Write());
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
        SettingsFiles.NotifySaved(path);
    }

    /// <summary>The canonical JSON form, as it is saved and shared: the programs in order of their file names.</summary>
    public string Write() => JsonSerializer.Serialize(this with
    {
        Programs = [.. Programs.OrderBy(p => p.Program, StringComparer.OrdinalIgnoreCase).ThenBy(p => p.Program, StringComparer.Ordinal)]
    }, Json);

    /// <summary>Reads a list, or null when it isn't one this Martlet can use (a newer Martlet's, or broken).</summary>
    public static KnownProgramsFile? Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            var known = JsonSerializer.Deserialize<KnownProgramsFile>(json, Json);
            if (known is null) return null;
            known = known with { Programs = known.Programs ?? [] };
            known.Validate();
            return known;
        }
        catch (Exception error) when (error is JsonException or ContractException or NotSupportedException) { return null; }
    }

    public void Validate()
    {
        ContractRules.Require(SchemaVersion == SchemaVersion1, "The known programs were written by a newer Martlet.");
        ContractRules.Require(Programs is { Count: <= MaximumPrograms }, "There are too many known programs.");
        ContractRules.Require(Programs.All(p => p is not null && Key(p.Program) == p.Program && p.Program.Length > 0 &&
            CleanName(p.Name) == p.Name && p.Name.Length > 0 && p.By is ByFiles or ByWeb or ByOwner),
            "A known program has no name, a name that is too long or an unknown source.");
        ContractRules.Require(Programs.Select(p => p.Program).Distinct(StringComparer.OrdinalIgnoreCase).Count() == Programs.Count,
            "A program is known twice.");
    }

    /// <summary>A program file's name as the list keeps it: without .exe, on one line, at most <see cref="MaximumProgram"/>
    /// characters; empty when there is none.</summary>
    public static string Key(string? program)
    {
        var name = CleanName(program, MaximumProgram + 4);
        if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) name = name[..^4].TrimEnd();
        return name.Length > MaximumProgram ? name[..MaximumProgram].TrimEnd() : name;
    }

    /// <summary>A usual name as the list keeps it: on one line, without control characters, at most <see cref="MaximumName"/>
    /// characters.</summary>
    public static string CleanName(string? name, int maximum = MaximumName)
    {
        var line = string.Join(' ', new string([.. (name ?? "").Select(c => char.IsControl(c) ? ' ' : c)])
            .Split(' ', StringSplitOptions.RemoveEmptyEntries));
        return line.Length > maximum ? line[..maximum].TrimEnd() : line;
    }

    private static IReadOnlyList<KnownProgram> Trim(IEnumerable<KnownProgram> programs)
    {
        var all = programs.ToList();
        return all.Count <= MaximumPrograms ? all : [.. all.OrderByDescending(p => p.By == ByOwner).ThenByDescending(p => p.At).Take(MaximumPrograms)];
    }

    /// <summary>The shared setting <see cref="SharedKey"/> backed by known-programs.json in <paramref name="directory"/>.
    /// Taking another computer's list keeps the programs only this computer knows: then it says so for one sync, and the next
    /// sync shares the list with them. <paramref name="followed"/> runs after this computer took another computer's list.</summary>
    public static ISharedSection Section(string directory, Action? followed = null) => new DelegateSection(SharedKey, "Known programs",
        _ =>
        {
            var saved = TryLoad(directory);
            var path = PathOf(directory);
            DateTimeOffset? changed = File.Exists(path) ? new DateTimeOffset(File.GetLastWriteTimeUtc(path), TimeSpan.Zero) : null;
            return Task.FromResult<SharedLocal?>(new((saved ?? new()).Write(), null, saved is not { Programs.Count: > 0 }, changed));
        },
        (setting, _) =>
        {
            if (Parse(setting.Value) is not { } theirs)
                return Task.FromResult(SharedApply.Waiting("It was written by a newer Martlet. Update this PC to use it."));
            var merged = Merge(theirs, Load(directory));
            try { Save(directory, merged); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                return Task.FromResult(SharedApply.Waiting("It couldn't be saved on this PC."));
            }
            followed?.Invoke();
            return Task.FromResult(merged.Write() == theirs.Write() ? SharedApply.Done
                : SharedApply.Waiting("This PC knows more programs; the next sync shares them with your other computers."));
        },
        () => new KnownProgramsFile().Write());

    public override string ToString() => $"KnownProgramsFile {{ Programs = {Programs.Count} }}";
}
