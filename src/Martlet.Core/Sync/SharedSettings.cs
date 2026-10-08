using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Martlet.Core.Contracts;

namespace Martlet.Core.Sync;

/// <summary>One setting Martlet keeps the same on every computer (for example how it thinks, or its character), as a
/// last-writer-wins register. <see cref="Value"/> is the setting's canonical JSON text ("null" while it is off or empty); a
/// setting that uses a secret (its API key) names it by <see cref="SecretSha256"/>, and the secret itself travels in
/// <see cref="SharedSettings.Secrets"/>. Hosts never look inside the value, so a newer Martlet's settings pass through them.</summary>
public sealed record SharedSetting
{
    public required string Key { get; init; }
    public required string Value { get; init; }
    public string? SecretSha256 { get; init; }
    public required long Revision { get; init; }
    public required DateTimeOffset UpdatedAt { get; init; }
    public required string UpdatedBy { get; init; }

    /// <summary>Whether this setting holds exactly <paramref name="value"/> with the secret <paramref name="secretSha256"/>.</summary>
    public bool Holds(string value, string? secretSha256) =>
        string.Equals(Value, value, StringComparison.Ordinal) && string.Equals(SecretSha256, secretSha256, StringComparison.Ordinal);

    internal string Content => Value + "\n" + SecretSha256;
}

/// <summary>A secret a shared setting uses (an API key), named by the SHA-256 of its UTF-8 text.</summary>
public sealed record SharedSecret
{
    public required string Sha256 { get; init; }
    public required string Value { get; init; }

    public override string ToString() => $"Shared secret {Sha256[..Math.Min(8, Sha256.Length)]} [redacted]";
}

/// <summary>The settings that make Martlet one companion across the owner's computers: one last-writer-wins entry per setting,
/// stamped with a hybrid revision (<see cref="NextRevision"/>), the time and the writer's device ID. <see cref="Merge"/> keeps,
/// per setting, the entry with the highest (revision, writer, content); it is commutative, associative and idempotent, so every
/// copy converges whatever order changes arrive in, and a change made while a computer was offline wins only if it is newer.
/// Secrets are pooled by SHA-256 and only those a setting still uses are kept. Paired hosts keep a copy (0600) and give it only
/// to paired devices over their signed, pinned connection; desktops keep their copy without the secrets, which stay in
/// Windows Credential Manager.</summary>
public sealed record SharedSettings
{
    public const int SchemaVersion1 = 1;
    public const int MaximumBytes = 2 * 1024 * 1024;
    public const int MaximumSettings = 64;
    public const int MaximumValueBytes = 1024 * 1024;
    public const int MaximumSecretLength = 1024;
    private const long MaximumRevision = long.MaxValue / 4;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectNullableAnnotations = true,
        MaxDepth = 8
    };

    public required int SchemaVersion { get; init; }
    public required IReadOnlyList<SharedSetting> Settings { get; init; }
    public IReadOnlyList<SharedSecret> Secrets { get; init; } = [];

    public static SharedSettings Empty { get; } = new() { SchemaVersion = SchemaVersion1, Settings = [] };

    /// <summary>The newest stamp in the document (0 when empty).</summary>
    [JsonIgnore]
    public long Revision => Settings.Select(s => s.Revision).DefaultIfEmpty(0).Max();

    public SharedSetting? Find(string key) => Settings.FirstOrDefault(s => s.Key == key);

    /// <summary>The secret named <paramref name="sha256"/>, when this copy carries it.</summary>
    public string? Secret(string? sha256) => sha256 is null ? null : Secrets.FirstOrDefault(s => s.Sha256 == sha256)?.Value;

    /// <summary>A revision newer than everything in this copy and, normally, than anything written before now.</summary>
    public long NextRevision(DateTimeOffset now) => Math.Min(MaximumRevision, Math.Max(Revision + 1, now.ToUnixTimeMilliseconds()));

    /// <summary>Records <paramref name="value"/> (and its <paramref name="secret"/>) for <paramref name="key"/>, stamped by
    /// <paramref name="by"/> at <paramref name="at"/> with <paramref name="revision"/> (the next revision when null).</summary>
    public SharedSettings Put(string key, string value, string? secret, string by, DateTimeOffset at, long? revision = null)
    {
        ContractRules.Require(secret is null || IsSecret(secret), "A shared secret is invalid.");
        var sha = secret is null ? null : Sha256(secret);
        var setting = new SharedSetting
        {
            Key = key, Value = value, SecretSha256 = sha, Revision = revision ?? NextRevision(at),
            UpdatedAt = at.ToUniversalTime(), UpdatedBy = by
        };
        Validate(setting);
        var secrets = secret is null ? Secrets : Secrets.Append(new SharedSecret { Sha256 = sha!, Value = secret });
        return Bounded(Settings.Where(s => s.Key != key).Append(setting), secrets);
    }

    /// <summary>Adds the secrets among <paramref name="secrets"/> that a setting here uses and this copy lacks.</summary>
    public SharedSettings WithSecrets(IEnumerable<string> secrets)
    {
        var used = Settings.Select(s => s.SecretSha256).OfType<string>().ToHashSet(StringComparer.Ordinal);
        var added = secrets.Where(IsSecret).Select(value => new SharedSecret { Sha256 = Sha256(value), Value = value })
            .Where(s => used.Contains(s.Sha256));
        return Bounded(Settings, Secrets.Concat(added));
    }

    /// <summary>This copy without any secret, as a desktop keeps it on disk.</summary>
    public SharedSettings WithoutSecrets() => Secrets.Count == 0 ? this : this with { Secrets = [] };

    /// <summary>Joins two copies: per setting the entry with the newest (revision, writer, content) wins; the secrets of both are
    /// pooled and only those a winning entry uses are kept.</summary>
    public static SharedSettings Merge(SharedSettings left, SharedSettings right)
    {
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);
        var settings = left.Settings.Concat(right.Settings).GroupBy(s => s.Key, StringComparer.Ordinal)
            .Select(group => group.Aggregate((a, b) => Newer(a, b) ? a : b));
        return Bounded(settings, left.Secrets.Concat(right.Secrets));
    }

    private static bool Newer(SharedSetting a, SharedSetting b) =>
        a.Revision != b.Revision ? a.Revision > b.Revision
        : a.UpdatedBy != b.UpdatedBy ? string.CompareOrdinal(a.UpdatedBy, b.UpdatedBy) > 0
        : string.CompareOrdinal(a.Content, b.Content) >= 0;

    // The newest settings (by revision) up to the limit, sorted by key, and only the secrets they use, sorted by SHA-256, so
    // equal content always writes equal bytes. Settings come before the entries about one computer (pc.<device>,
    // role.<device>), which are never removed: a computer retired long ago drops out before any setting does.
    private static SharedSettings Bounded(IEnumerable<SharedSetting> settings, IEnumerable<SharedSecret> secrets)
    {
        var kept = settings.OrderBy(s => IsDeviceKey(s.Key) ? 1 : 0).ThenByDescending(s => s.Revision)
            .ThenBy(s => s.Key, StringComparer.Ordinal).Take(MaximumSettings)
            .OrderBy(s => s.Key, StringComparer.Ordinal).ToArray();
        var used = kept.Select(s => s.SecretSha256).OfType<string>().ToHashSet(StringComparer.Ordinal);
        var pool = secrets.Where(s => used.Contains(s.Sha256)).GroupBy(s => s.Sha256, StringComparer.Ordinal)
            .Select(g => g.First()).OrderBy(s => s.Sha256, StringComparer.Ordinal).ToArray();
        return new() { SchemaVersion = SchemaVersion1, Settings = kept, Secrets = pool };
    }

    /// <summary>Lower-case hex SHA-256 of <paramref name="text"/>'s UTF-8 bytes.</summary>
    public static string Sha256(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    /// <summary>Identifies one setting's content (its value and which secret it uses), as a computer records what it has.</summary>
    public static string ContentDigest(string value, string? secretSha256) => Sha256(value + "\n" + secretSha256);

    /// <summary>The prefix of the entry each computer keeps about itself ("pc.desktop-a").</summary>
    public const string DevicePrefix = "pc.";

    /// <summary>The prefix of the entry that says whether a computer is a companion or a host PC ("role.desktop-a"): that
    /// computer records its own choice there, and any other computer may write it to make it switch.</summary>
    public const string RolePrefix = "role.";

    /// <summary>The prefix of the entry where each computer keeps the reminders set on it and what it did about anyone's
    /// ("reminders.desktop-a"): only that computer writes it.</summary>
    public const string RemindersPrefix = "reminders.";

    /// <summary>The prefix of the entry where a computer publishes the run that applies the recommended setup to all your
    /// computers ("setup-run.desktop-a", <see cref="Cluster.SetupRun"/>): only that computer writes it.</summary>
    public const string SetupRunPrefix = "setup-run.";

    /// <summary>Whether <paramref name="key"/> is about one computer (its own entry, its role, its reminders or its setup run)
    /// rather than a setting.</summary>
    public static bool IsDeviceKey(string key) =>
        key.StartsWith(DevicePrefix, StringComparison.Ordinal) || key.StartsWith(RolePrefix, StringComparison.Ordinal) ||
        key.StartsWith(RemindersPrefix, StringComparison.Ordinal) || key.StartsWith(SetupRunPrefix, StringComparison.Ordinal);

    public static bool IsKey(string? key) => key is { Length: > 0 and <= 64 } && char.IsAsciiLetterLower(key[0]) &&
        key.All(c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c is '-' or '.');

    /// <summary>A secret as a shared setting may carry it: 1-1,024 ASCII letters, digits, dots, underscores or hyphens (the form
    /// Martlet accepts for API keys).</summary>
    public static bool IsSecret(string? value) => value is { Length: > 0 and <= MaximumSecretLength } &&
        value.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.');

    private static bool IsSha256(string? value) => value is { Length: 64 } && value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static void Validate(SharedSetting setting)
    {
        ContractRules.Require(setting is not null, "A shared setting is missing.");
        ContractRules.Require(IsKey(setting!.Key), "A shared setting's name is invalid.");
        ContractRules.Require(setting.Revision is > 0 and <= MaximumRevision, "A shared setting's revision is out of range.");
        ContractRules.Identifier(setting.UpdatedBy);
        ContractRules.Require(setting.SecretSha256 is null || IsSha256(setting.SecretSha256), "A shared setting's secret name is invalid.");
        ContractRules.Require(setting.Value is not null && Utf8Bytes(setting.Value) is > 0 and <= MaximumValueBytes,
            $"The shared setting {setting.Key} is empty or too large.", ErrorCode.PayloadTooLarge);
        try
        {
            using var _ = JsonDocument.Parse(setting.Value!, new JsonDocumentOptions { MaxDepth = 64 });
        }
        catch (JsonException)
        {
            throw new ContractException(ErrorCode.InvalidContract, $"The shared setting {setting.Key} is not valid JSON.");
        }
    }

    private static int Utf8Bytes(string value)
    {
        try { return StrictUtf8.GetByteCount(value); }
        catch (EncoderFallbackException) { return -1; }
    }

    public void Validate()
    {
        ContractRules.Require(SchemaVersion == SchemaVersion1, "These shared settings were written by a newer Martlet.", ErrorCode.UnsupportedVersion);
        ContractRules.Require(Settings is { Count: <= MaximumSettings } && Secrets is not null, "The shared settings list too many settings.");
        foreach (var setting in Settings!) Validate(setting);
        ContractRules.Require(Settings.Select(s => s.Key).Distinct(StringComparer.Ordinal).Count() == Settings.Count,
            "The shared settings list a setting twice.");
        foreach (var secret in Secrets!)
            ContractRules.Require(secret is not null && IsSecret(secret.Value) && secret.Sha256 == Sha256(secret.Value),
                "A shared secret is invalid.");
    }

    public byte[] Write()
    {
        Validate();
        var bytes = JsonSerializer.SerializeToUtf8Bytes(Bounded(Settings, Secrets), Json);
        ContractRules.Require(bytes.Length <= MaximumBytes, "The shared settings are too large.", ErrorCode.PayloadTooLarge);
        return bytes;
    }

    public static SharedSettings Parse(ReadOnlySpan<byte> bytes)
    {
        ContractRules.Require(bytes.Length is > 0 and <= MaximumBytes, "The shared settings are empty or too large.", ErrorCode.PayloadTooLarge);
        SharedSettings? settings;
        try { settings = JsonSerializer.Deserialize<SharedSettings>(bytes, Json); }
        catch (Exception error) when (error is JsonException or NotSupportedException or InvalidOperationException or FormatException)
        {
            throw new ContractException(ErrorCode.InvalidContract, "The shared settings are malformed.");
        }
        ContractRules.Require(settings is not null, "The shared settings are empty.");
        settings!.Validate();
        return Bounded(settings.Settings, settings.Secrets);
    }

    /// <summary>Identifies the content (settings and the secrets carried), to tell whether a copy is current.</summary>
    public string Digest() => Convert.ToHexStringLower(SHA256.HashData(Write()));

    /// <summary>Identifies the settings alone (not which secrets a copy carries), for status and logs.</summary>
    public string SettingsDigest() => WithoutSecrets().Digest();

    public override string ToString() => $"Shared settings r{Revision} ({Settings.Count} settings, {Secrets.Count} secrets)";
}
