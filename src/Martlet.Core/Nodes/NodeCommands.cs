using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Martlet.Core.Contracts;

namespace Martlet.Core.Nodes;

/// <summary>The commands one Martlet computer can ask another to run through the host's paired gateway. The list is closed:
/// a host's agent runs nothing else, and every argument is checked here on both ends.</summary>
public static class NodeCommandKinds
{
    /// <summary>Bring that computer to at least Martlet <c>version</c>: its app (from the official GitHub Release) and its
    /// host service.</summary>
    public const string Update = "martlet.update";
    /// <summary>Show the host service's status (<c>martlet-host status</c>).</summary>
    public const string Status = "host.status";
    /// <summary>Read what a role needs (<c>martlet-host describe role</c>): its terms, secrets and choices.</summary>
    public const string DescribeRole = "host.describe-role";
    /// <summary>Install a role with its non-secret <c>choice.VAR</c> arguments and <c>secret.name</c> secrets.</summary>
    public const string AddRole = "host.add-role";
    /// <summary>Stop a role and unpublish it (its data stays).</summary>
    public const string RemoveRole = "host.remove-role";

    public static readonly IReadOnlyList<string> All = [Update, Status, DescribeRole, AddRole, RemoveRole];
}

public enum NodeCommandState { Queued, Running, Succeeded, Failed, Canceled }

/// <summary>One command and what became of it. Secrets are never part of it: the gateway holds them in memory only until
/// the host's agent takes the command.</summary>
public sealed record NodeCommand
{
    public required string Id { get; init; }
    public required string Kind { get; init; }
    public IReadOnlyDictionary<string, string> Arguments { get; init; } = new Dictionary<string, string>();
    public required string RequestedBy { get; init; }
    public required DateTimeOffset RequestedAt { get; init; }
    public required NodeCommandState State { get; init; }
    public required DateTimeOffset UpdatedAt { get; init; }
    public string? ClaimedBy { get; init; }
    public string? Summary { get; init; }
    /// <summary>The last lines of its output (at most <see cref="NodeCommandRules.MaximumOutputLines"/>).</summary>
    public IReadOnlyList<string> Output { get; init; } = [];
    /// <summary>How many output lines it produced in all, so a reader can tell which lines in <see cref="Output"/> are new.</summary>
    public int OutputTotal { get; init; }
    public int? ExitCode { get; init; }
    public bool CancelRequested { get; init; }
    /// <summary>Whether it was sent with secrets (which a restarted gateway no longer has).</summary>
    public bool HasSecrets { get; init; }

    [JsonIgnore] public bool Finished => State is NodeCommandState.Succeeded or NodeCommandState.Failed or NodeCommandState.Canceled;
}

/// <summary>The host's agent: the Martlet app on the host computer that runs its commands, as its gateway last saw it.</summary>
public sealed record NodeAgentInfo
{
    public required string DeviceId { get; init; }
    public required DateTimeOffset SeenAt { get; init; }
    public string? Version { get; init; }
    public IReadOnlyList<string> Kinds { get; init; } = [];
}

/// <summary>The bounds and argument rules both ends enforce, and the JSON both ends write.</summary>
public static partial class NodeCommandRules
{
    public const int MaximumOutputLines = 120;
    public const int MaximumLineCharacters = 1000;
    public const int MaximumSummaryCharacters = 400;
    public const int MaximumArguments = 16;
    public const int MaximumSecrets = 8;
    public const int MaximumSecretCharacters = 4096;
    public const int MaximumArgumentCharacters = 200;
    /// <summary>The largest request body any command endpoint accepts.</summary>
    public const int MaximumRequestBytes = 160 * 1024;
    /// <summary>The largest response a command endpoint sends.</summary>
    public const int MaximumResponseBytes = 256 * 1024;

    public static readonly JsonSerializerOptions Json = CreateJson();

    [GeneratedRegex(@"\A[0-9]{1,6}\.[0-9]{1,6}\.[0-9]{1,6}\z")]
    private static partial Regex VersionPattern();

    [GeneratedRegex(@"\A[a-z0-9][a-z0-9-]{0,31}\z")]
    private static partial Regex RolePattern();

    [GeneratedRegex(@"\Achoice\.[A-Za-z_][A-Za-z0-9_]{0,31}\z")]
    private static partial Regex ChoicePattern();

    [GeneratedRegex(@"\Asecret\.[a-z0-9][a-z0-9_-]{0,31}\z")]
    private static partial Regex SecretPattern();

    [GeneratedRegex(@"\A[A-Za-z0-9_-]{22}\z")]
    private static partial Regex IdPattern();

    [GeneratedRegex(@"\A[A-Za-z0-9][A-Za-z0-9._-]{0,63}\z")]
    private static partial Regex DevicePattern();

    public static bool IsId(string? value) => value is not null && IdPattern().IsMatch(value);

    public static bool IsDevice(string? value) => value is not null && DevicePattern().IsMatch(value);

    public static bool IsVersion(string? value) => value is not null && VersionPattern().IsMatch(value);

    public static bool IsRole(string? value) => value is not null && RolePattern().IsMatch(value);

    /// <summary>Checks a command's kind, arguments and secrets; throws <see cref="ContractException"/> when anything is off.</summary>
    public static void Validate(string kind, IReadOnlyDictionary<string, string> arguments, IReadOnlyDictionary<string, string> secrets)
    {
        ContractRules.Require(NodeCommandKinds.All.Contains(kind), "Unknown command.");
        ContractRules.Require(arguments.Count <= MaximumArguments && secrets.Count <= MaximumSecrets, "Too many arguments.");
        foreach (var (name, value) in arguments)
            ContractRules.Require(SingleLine(value, MaximumArgumentCharacters),
                $"Argument {name} must be one line of at most {MaximumArgumentCharacters} characters.");
        foreach (var (name, value) in secrets)
            ContractRules.Require(SecretPattern().IsMatch(name) && value.Length > 0 && SingleLine(value, MaximumSecretCharacters), "Invalid secret.");
        var names = arguments.Keys.ToHashSet(StringComparer.Ordinal);
        switch (kind)
        {
            case NodeCommandKinds.Update:
                ContractRules.Require(names.SetEquals(["version"]) && IsVersion(arguments["version"]) && secrets.Count == 0,
                    "An update names one Martlet version.");
                break;
            case NodeCommandKinds.Status:
                ContractRules.Require(names.Count == 0 && secrets.Count == 0, "Status takes no arguments.");
                break;
            case NodeCommandKinds.DescribeRole or NodeCommandKinds.RemoveRole:
                ContractRules.Require(names.SetEquals(["role"]) && IsRole(arguments["role"]) && secrets.Count == 0, "Name one role.");
                break;
            case NodeCommandKinds.AddRole:
                ContractRules.Require(names.Contains("role") && IsRole(arguments["role"]), "Name one role.");
                ContractRules.Require(names.All(n => n == "role" || ChoicePattern().IsMatch(n)), "Adding a role takes only choice.VAR arguments.");
                break;
        }
    }

    /// <summary>Keeps one output line within bounds (control characters dropped, long lines cut).</summary>
    public static string Line(string? text)
    {
        var clean = new string((text ?? "").Select(c => c == '\t' ? ' ' : c).Where(c => !char.IsControl(c)).ToArray());
        return clean.Length <= MaximumLineCharacters ? clean : clean[..(MaximumLineCharacters - 3)] + "...";
    }

    public static string? Summary(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var clean = Line(text).Trim();
        return clean.Length <= MaximumSummaryCharacters ? clean : clean[..(MaximumSummaryCharacters - 3)] + "...";
    }

    /// <summary>Reads one command written with <see cref="Json"/>, checking its identifiers and bounds.</summary>
    public static NodeCommand Read(JsonElement element)
    {
        NodeCommand? command;
        try { command = element.Deserialize<NodeCommand>(Json); }
        catch (Exception error) when (error is JsonException or NotSupportedException or InvalidOperationException)
        {
            throw new ContractException(ErrorCode.InvalidContract, "The command is malformed.");
        }
        ContractRules.Require(command is not null && IsId(command.Id) && NodeCommandKinds.All.Contains(command.Kind) &&
            IsDevice(command.RequestedBy) && (command.ClaimedBy is null || IsDevice(command.ClaimedBy)) &&
            Enum.IsDefined(command.State) && command.Arguments.Count <= MaximumArguments &&
            command.Output.Count <= MaximumOutputLines && command.OutputTotal >= command.Output.Count &&
            command.Output.All(line => line.Length <= MaximumLineCharacters) &&
            (command.Summary is null || command.Summary.Length <= MaximumSummaryCharacters), "The command is malformed.");
        return command!;
    }

    private static bool SingleLine(string value, int maximum) =>
        value.Length <= maximum && !value.Any(char.IsControl);

    private static JsonSerializerOptions CreateJson()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            MaxDepth = 8
        };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower, allowIntegerValues: false));
        return options;
    }
}
