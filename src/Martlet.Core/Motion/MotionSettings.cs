using System.Text.Json;
using System.Text.Json.Serialization;
using Martlet.Core.Cluster;

namespace Martlet.Core.Motion;

/// <summary>Where Martlet makes body motions: on the first of your paired computers that runs Martlet's <c>motion</c> host role
/// (<see cref="Automatic"/>, the default, so adding the role turns motion on), on one chosen computer (<see cref="Host"/>), or
/// nowhere (<see cref="Off"/>).</summary>
public enum MotionPlace { Automatic, Off, Host }

/// <summary>The motion choice (motion.json in the data directory, the <c>motion</c> shared setting, so it is the same on all your
/// computers). Nonsecret: a place and a host ID only. A computer that isn't paired with the chosen host keeps the choice and
/// says why it can't make motions there.</summary>
public sealed record MotionSettings
{
    public const string FileName = "motion.json";
    public const string SharedKey = "motion";
    /// <summary>The host role kind (role.conf gateway_kind and the cluster plan's role kind).</summary>
    public const string RoleKind = "motion";
    private static readonly JsonSerializerOptions Canonical = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        RespectNullableAnnotations = true,
        MaxDepth = 4,
        Converters = { new JsonStringEnumConverter<MotionPlace>(JsonNamingPolicy.SnakeCaseLower) }
    };
    private static readonly JsonSerializerOptions Indented = new(Canonical) { WriteIndented = true };

    public int SchemaVersion { get; init; } = 1;
    public MotionPlace Place { get; init; }
    /// <summary>The paired computer whose motion role makes motions (<see cref="MotionPlace.Host"/> only).</summary>
    public string? HostId { get; init; }

    [JsonIgnore] public bool On => Place != MotionPlace.Off;
    [JsonIgnore] public bool IsDefault => Place == MotionPlace.Automatic && HostId is null;

    /// <summary>Where motions are made, in words.</summary>
    public string Describe() => Place switch
    {
        MotionPlace.Host => $"{HostId}'s Motion role",
        MotionPlace.Off => "nowhere (off)",
        _ => "the first of your computers that runs the Motion role"
    };

    /// <summary>The computer that makes motions: the chosen one, or (automatic) the first host in the shared plan that was last
    /// seen running the motion role and is in <paramref name="paired"/> (this PC's pairings other than hosts a friend shares,
    /// or null to accept any); null when motion is off or no such computer is known. Reads files only; never the network.</summary>
    public string? MotionHost(ClusterPlan plan, IReadOnlyCollection<string>? paired)
    {
        ArgumentNullException.ThrowIfNull(plan);
        return Place switch
        {
            MotionPlace.Host => HostId,
            MotionPlace.Off => null,
            _ => plan.Nodes.Where(n => !n.Removed && n.Roles.Any(r => r.Kind == RoleKind) && (paired is null || paired.Contains(n.HostId)))
                .Select(n => n.HostId).Order(StringComparer.Ordinal).FirstOrDefault()
        };
    }

    private bool Valid() => SchemaVersion == 1 && Enum.IsDefined(Place) &&
        (Place == MotionPlace.Host
            ? HostId is { Length: > 0 and <= 128 } && HostId.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-')
            : HostId is null);

    /// <summary>Throws <see cref="Contracts.ContractException"/> when the choice isn't usable as it is.</summary>
    public void Validate() => Contracts.ContractRules.Require(Valid(), "The motion choice is invalid.");

    /// <summary>The canonical JSON every computer writes for the same choice, for sharing.</summary>
    public string Share() => JsonSerializer.Serialize(this, Canonical);

    /// <summary>A choice another computer shared (<see cref="Share"/>); null when it isn't one this Martlet reads.</summary>
    public static MotionSettings? Parse(string json)
    {
        try
        {
            var parsed = JsonSerializer.Deserialize<MotionSettings>(json, Canonical);
            return parsed is not null && parsed.Valid() ? parsed : null;
        }
        catch (Exception error) when (error is JsonException or NotSupportedException or ArgumentException) { return null; }
    }

    public static MotionSettings Load(string? directory) => Read(directory).Settings;

    /// <summary>The saved choice and the file's state: none, loaded or unreadable (which reads as the default).</summary>
    public static (MotionSettings Settings, string State) Read(string? directory)
    {
        if (directory is null) return (new(), "none");
        try
        {
            var path = Path.Combine(directory, FileName);
            if (!File.Exists(path)) return (new(), "none");
            if (new FileInfo(path).Length > 16_384) return (new(), "unreadable");
            return Parse(File.ReadAllText(path)) is { } loaded ? (loaded, "loaded") : (new(), "unreadable");
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return (new(), "unreadable"); }
    }

    /// <summary>Saves motion.json atomically; false when the choice is invalid or the data folder can't be written.</summary>
    public bool Save(string? directory)
    {
        if (directory is null || !Valid()) return false;
        try
        {
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, FileName);
            var temporary = Path.Combine(directory, $"motion.{Guid.NewGuid():N}.tmp");
            try
            {
                File.WriteAllText(temporary, JsonSerializer.Serialize(this, Indented));
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
