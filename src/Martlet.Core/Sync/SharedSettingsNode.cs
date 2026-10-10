using System.Text.Json;
using System.Text.Json.Serialization;
using Martlet.Core.Contracts;

namespace Martlet.Core.Sync;

/// <summary>What this computer has for one shared setting right now: its canonical JSON <paramref name="Value"/>, the secret it
/// uses (an API key) or null, whether it is still Martlet's default (a default never overrides what another computer chose), and
/// when it last changed here, as far as this computer can tell (a key's write time in Windows Credential Manager, or the file's
/// time). <paramref name="ChangedAt"/> only matters the first time this computer shares the setting.</summary>
public sealed record SharedLocal(string Value, string? Secret = null, bool IsDefault = false, DateTimeOffset? ChangedAt = null)
{
    public string? SecretSha256 => Secret is null ? null : SharedSettings.Sha256(Secret);

    internal string Digest => SharedSettings.ContentDigest(Value, SecretSha256);

    public override string ToString() => $"Shared local value ({Value.Length} characters{(Secret is null ? "" : ", secret redacted")})";
}

/// <summary>The outcome of making this computer follow a shared setting: applied, or why not yet (it is retried).</summary>
public sealed record SharedApply(bool Applied, string? Note)
{
    public static SharedApply Done { get; } = new(true, null);
    public static SharedApply Waiting(string note) => new(false, note);
}

/// <summary>One kind of setting this computer shares: how to read what it has and how to make it match another computer's.</summary>
public interface ISharedSection
{
    string Key { get; }
    /// <summary>The setting in words, for status lines ("Thinking", "Character").</summary>
    string Title { get; }
    /// <summary>What this computer has, or null when it has nothing it can share (for example a job not set up yet).</summary>
    Task<SharedLocal?> ReadAsync(CancellationToken token);
    /// <summary>Makes this computer use <paramref name="setting"/> (with its <paramref name="secret"/>).</summary>
    Task<SharedApply> ApplyAsync(SharedSetting setting, string? secret, CancellationToken token);
    /// <summary>The value an account that never chose this setting starts with (canonical JSON, as <see cref="ReadAsync"/> gives
    /// it), or null to keep what this computer has (<see cref="SharedSettingsNode.AdoptAsync(CancellationToken)"/>).</summary>
    string? Default => null;
}

/// <summary>A setting this computer took from another computer during a sync.</summary>
public sealed record SharedSettingsChange(string Key, string Title, string By, DateTimeOffset At);

/// <summary>What one sync did: the merged copy (with the secrets this computer knows, ready to give the hosts), the settings it
/// took from elsewhere, the settings changed here that it recorded, and the settings it could not follow yet with why.</summary>
public sealed record SharedSettingsResult(SharedSettings Document, IReadOnlyList<SharedSettingsChange> Applied,
    IReadOnlyList<string> Recorded, IReadOnlyDictionary<string, string> Waiting);

/// <summary>This computer's copy of the shared settings and, per setting, a digest of the value it had when it last looked
/// (<see cref="Observed"/>), so a change made here is told apart from one it took from elsewhere. Kept in
/// shared-settings.json in Martlet's data folder without any secret.</summary>
public sealed record SharedSettingsState
{
    public const string FileName = "shared-settings.json";
    public const int SchemaVersion1 = 1;
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        RespectNullableAnnotations = true,
        MaxDepth = 8
    };

    public required int SchemaVersion { get; init; }
    public required JsonElement Document { get; init; }
    public required IReadOnlyDictionary<string, string> Observed { get; init; }

    /// <summary>This computer's saved copy and observations; an unreadable file starts empty (the hosts' copies restore the
    /// settings, and each setting here is compared with them as on a first sync).</summary>
    public static (SharedSettings Document, Dictionary<string, string> Observed) Load(string directory)
    {
        try
        {
            var bytes = File.ReadAllBytes(Path.Combine(directory, FileName));
            var state = JsonSerializer.Deserialize<SharedSettingsState>(bytes, Json);
            if (state is not { SchemaVersion: SchemaVersion1, Observed: not null }) return (SharedSettings.Empty, []);
            var document = SharedSettings.Parse(JsonSerializer.SerializeToUtf8Bytes(state.Document)).WithoutSecrets();
            var observed = state.Observed.Where(o => SharedSettings.IsKey(o.Key) && o.Value is { Length: 64 })
                .ToDictionary(o => o.Key, o => o.Value, StringComparer.Ordinal);
            return (document, observed);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or ContractException or
            NotSupportedException or InvalidOperationException)
        {
            return (SharedSettings.Empty, []);
        }
    }

    public static void Save(string directory, SharedSettings document, IReadOnlyDictionary<string, string> observed)
    {
        Directory.CreateDirectory(directory);
        using var parsed = JsonDocument.Parse(document.WithoutSecrets().Write());
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new SharedSettingsState
        {
            SchemaVersion = SchemaVersion1, Document = parsed.RootElement,
            Observed = new SortedDictionary<string, string>(observed.ToDictionary(), StringComparer.Ordinal)
        }, Json);
        var path = Path.Combine(directory, FileName);
        var temporary = Path.Combine(directory, $"shared-settings.{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllBytes(temporary, bytes);
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }
}

/// <summary>Keeps one computer's settings in step with the owner's other computers. Each sync it merges the hosts' copies into
/// its own, records the settings changed here since it last looked (stamped now, so they win over older changes elsewhere),
/// and makes this computer follow every setting that is newer elsewhere. The first time a setting is shared from here (after
/// an update, or on a new computer) there is no record of when it changed, so the time it last changed here decides: a
/// setting another computer changed later wins, and a value that is still Martlet's default never overrides one. A setting
/// this computer can't follow yet (a missing key, a voice not installed here) keeps its current value and is tried again on
/// the next sync, without being recorded as a change here. Sections are called on the caller's context (the desktop's UI
/// thread), one at a time.</summary>
public sealed class SharedSettingsNode
{
    private readonly string directory;
    private readonly string device;
    private readonly IReadOnlyList<ISharedSection> sections;
    private readonly Action? invalidate;
    private readonly bool account;
    private readonly Dictionary<string, string> observed;
    private readonly Dictionary<string, string> secrets = new(StringComparer.Ordinal);
    /// <summary>Entries applied that still read back differently; not applied again until they change.</summary>
    private readonly Dictionary<string, string> mismatched = new(StringComparer.Ordinal);

    /// <param name="account">An account's node (docs/ACCOUNTS.md, "Account settings"): its copy is that account's whole set of
    /// settings, which a switch gives back to the files. So every setting the files hold that the copy lacks is recorded, a
    /// default one at the lowest revision (any choice made anywhere wins over it), and a setting <see cref="AdoptAsync(CancellationToken)"/>
    /// gives the files is recorded too. A household node records a default only as a change made here.</param>
    public SharedSettingsNode(string directory, string device, IReadOnlyList<ISharedSection> sections, Action? invalidate = null,
        bool account = false)
    {
        ContractRules.Identifier(device);
        ContractRules.Require(sections.Select(s => s.Key).All(SharedSettings.IsKey) &&
            sections.Select(s => s.Key).Distinct(StringComparer.Ordinal).Count() == sections.Count, "Shared setting names must be unique.");
        this.directory = directory;
        this.device = device;
        this.sections = sections;
        this.invalidate = invalidate;
        this.account = account;
        (Document, observed) = SharedSettingsState.Load(directory);
    }

    /// <summary>The lowest revision: an account's setting recorded only so its copy is whole (a default, or what the files held
    /// when the account first took them), which any choice made anywhere replaces.</summary>
    public const long KeptRevision = 1;

    /// <summary>What <c>shared-settings.json</c> records for a setting this computer's files don't hold yet: an account's
    /// setting <see cref="AdoptAsync"/> could not give the files yet. Such a setting is never recorded as a change made here
    /// until the files hold the account's value.</summary>
    public const string AdoptingDigest = "0000000000000000000000000000000000000000000000000000000000000000";

    /// <summary>The folder this node keeps its copy in.</summary>
    public string Folder => directory;

    /// <summary>The settings this computer's files don't hold yet (<see cref="AdoptingDigest"/>).</summary>
    public IReadOnlyList<string> Adopting =>
        [.. sections.Select(s => s.Key).Where(key => observed.GetValueOrDefault(key) == AdoptingDigest)];

    /// <summary>The merged copy, with the secrets this computer knows.</summary>
    public SharedSettings Document { get; private set; }

    public IReadOnlyList<ISharedSection> Sections => sections;

    /// <summary>Settings this computer could not follow yet, with why.</summary>
    public IReadOnlyDictionary<string, string> Waiting { get; private set; } = new Dictionary<string, string>();

    /// <summary>Merges <paramref name="copies"/> (the hosts'), records changes made here and, with <paramref name="apply"/>,
    /// follows what is newer elsewhere. Saves this computer's copy (without secrets) afterwards.</summary>
    public async Task<SharedSettingsResult> SyncAsync(IEnumerable<SharedSettings> copies, bool apply, DateTimeOffset now, CancellationToken token)
    {
        var document = Document;
        foreach (var copy in copies)
        {
            foreach (var secret in copy.Secrets) secrets[secret.Sha256] = secret.Value;
            document = SharedSettings.Merge(document, copy);
        }
        invalidate?.Invoke();
        var locals = new Dictionary<string, SharedLocal?>(StringComparer.Ordinal);
        var recorded = new List<string>();
        var waiting = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var section in sections)
        {
            if (observed.GetValueOrDefault(section.Key) == AdoptingDigest)
            {
                // An account's setting the files don't hold yet: give it to them first; what they have now is not this
                // account's, so it is never recorded.
                var (adopted, adoptedLocal, adoptedDocument) = await AdoptSectionAsync(section, document, waiting, token);
                document = adoptedDocument;
                if (adopted) locals[section.Key] = adoptedLocal;
                continue;
            }
            SharedLocal? local;
            try { local = await section.ReadAsync(token); }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                // This PC keeps what it has for a setting it can't read or share, rather than take another computer's.
                waiting[section.Key] = error is ContractException ? error.Message : $"Couldn't read it here: {error.Message}";
                continue;
            }
            locals[section.Key] = local;
            if (local is null) continue;
            if (local.Secret is { } own) secrets[SharedSettings.Sha256(own)] = own;
            var digest = local.Digest;
            var entry = document.Find(section.Key);
            try
            {
                if (!observed.TryGetValue(section.Key, out var seen))
                {
                    if (entry?.Holds(local.Value, local.SecretSha256) != true)
                    {
                        if (!local.IsDefault && Evidence(local, now) is var evidence && (entry is null || evidence > entry.Revision))
                        {
                            document = document.Put(section.Key, local.Value, local.Secret, device, local.ChangedAt ?? now, evidence);
                            recorded.Add(section.Key);
                        }
                        // An account's copy holds every setting it has, so a switch can give it back; a default one at the
                        // lowest revision, so it never replaces a choice made anywhere.
                        else if (account && entry is null)
                        {
                            document = document.Put(section.Key, local.Value, local.Secret, device, local.ChangedAt ?? now, KeptRevision);
                            recorded.Add(section.Key);
                        }
                    }
                    observed[section.Key] = digest;
                }
                else if (seen != digest)
                {
                    if (entry?.Holds(local.Value, local.SecretSha256) != true)
                    {
                        document = document.Put(section.Key, local.Value, local.Secret, device, now);
                        recorded.Add(section.Key);
                    }
                    observed[section.Key] = digest;
                }
            }
            // A setting that can't be shared (too large, for example) is noticed again when it changes, and this PC keeps it
            // rather than taking another computer's; the rest still sync.
            catch (ContractException error)
            {
                observed[section.Key] = digest;
                locals.Remove(section.Key);
                waiting[section.Key] = $"It can't be shared from this PC: {error.Message}";
            }
        }
        document = document.WithSecrets(secrets.Values);

        var applied = new List<SharedSettingsChange>();
        if (apply)
            foreach (var section in sections)
            {
                if (document.Find(section.Key) is not { } entry || !locals.TryGetValue(section.Key, out var local)) continue;
                if (local is not null && entry.Holds(local.Value, local.SecretSha256))
                {
                    mismatched.Remove(section.Key);
                    continue;
                }
                var version = $"{entry.Revision}/{entry.UpdatedBy}";
                if (mismatched.TryGetValue(section.Key, out var tried) && tried == version)
                {
                    waiting[section.Key] = "It is set up differently on this PC.";
                    continue;
                }
                var secret = entry.SecretSha256 is { } sha ? secrets.GetValueOrDefault(sha) : null;
                if (entry.SecretSha256 is not null && secret is null)
                {
                    waiting[section.Key] = "Its API key hasn't reached this PC yet.";
                    continue;
                }
                SharedApply result;
                try { result = await section.ApplyAsync(entry, secret, token); }
                catch (Exception error) when (error is not OperationCanceledException) { result = SharedApply.Waiting(error.Message); }
                if (!result.Applied)
                {
                    waiting[section.Key] = result.Note ?? "Not yet.";
                    continue;
                }
                invalidate?.Invoke();
                SharedLocal? after = null;
                try { after = await section.ReadAsync(token); }
                catch (Exception error) when (error is not OperationCanceledException) { }
                if (after is not null)
                {
                    observed[section.Key] = after.Digest;
                    if (after.Secret is { } own) secrets[SharedSettings.Sha256(own)] = own;
                }
                if (after is null || !entry.Holds(after.Value, after.SecretSha256))
                {
                    mismatched[section.Key] = version;
                    waiting[section.Key] = "It is set up differently on this PC.";
                    continue;
                }
                mismatched.Remove(section.Key);
                applied.Add(new(section.Key, section.Title, entry.UpdatedBy, entry.UpdatedAt));
            }

        Document = document;
        Waiting = waiting;
        SharedSettingsState.Save(directory, document, observed);
        return new(document, applied, recorded, waiting);
    }

    /// <summary>Makes this computer's files hold this node's settings, as when an account signs in here: every section takes its
    /// entry in this copy, or its <see cref="ISharedSection.Default"/> when the copy has none (a section without a default, or
    /// holding exactly it, keeps what this computer has). What the files held before (another account's settings) is never
    /// recorded as a change of this node's: a section that can't take its value yet waits, is tried again on every
    /// <see cref="SyncAsync"/>, and is recorded only once the files hold it. Safe to run again. Returns the sections still
    /// waiting, with why.</summary>
    public Task<IReadOnlyDictionary<string, string>> AdoptAsync(CancellationToken token) => AdoptAsync([], token);

    /// <summary>As <see cref="AdoptAsync(CancellationToken)"/>, after merging <paramref name="copies"/> (the hosts' copies of
    /// this account's settings) into this node's copy, without looking at the files: what they hold is not this account's.</summary>
    public async Task<IReadOnlyDictionary<string, string>> AdoptAsync(IEnumerable<SharedSettings> copies, CancellationToken token)
    {
        var document = Document;
        foreach (var copy in copies)
        {
            foreach (var secret in copy.Secrets) secrets[secret.Sha256] = secret.Value;
            document = SharedSettings.Merge(document, copy);
        }
        Document = document.WithSecrets(secrets.Values);
        foreach (var section in sections) observed[section.Key] = AdoptingDigest;
        mismatched.Clear();
        SharedSettingsState.Save(directory, Document, observed);
        invalidate?.Invoke();
        var waiting = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var section in sections)
            if (await AdoptSectionAsync(section, Document, waiting, token) is { Document: { } kept }) Document = kept;
        Waiting = waiting;
        SharedSettingsState.Save(directory, Document, observed);
        return waiting;
    }

    // Gives one section this node's value (its entry, else its default) and records what then reads back as seen; an account's
    // node also records it in its copy when the copy had none, so the copy is whole (the same default comes back next time).
    private async Task<(bool Adopted, SharedLocal? Local, SharedSettings Document)> AdoptSectionAsync(ISharedSection section, SharedSettings document,
        Dictionary<string, string> waiting, CancellationToken token)
    {
        var entry = document.Find(section.Key);
        SharedLocal? local;
        try { local = await section.ReadAsync(token); }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            waiting[section.Key] = error is ContractException ? error.Message : $"Couldn't read it here: {error.Message}";
            return (false, null, document);
        }
        // Without an entry the files take the default (they hold another account's value, whatever it looks like), unless they
        // hold nothing for this setting yet (a new computer's files are not made just for a default) or exactly the default.
        var value = entry?.Value ?? (local is null ? null : section.Default);
        var holds = local is not null && (entry is not null ? entry.Holds(local.Value, local.SecretSha256)
            : string.Equals(local.Value, value, StringComparison.Ordinal) && local.Secret is null);
        if (value is not null && !holds)
        {
            var secret = entry?.SecretSha256 is { } sha ? secrets.GetValueOrDefault(sha) : null;
            if (entry?.SecretSha256 is not null && secret is null)
            {
                waiting[section.Key] = "Its API key hasn't reached this PC yet.";
                return (false, null, document);
            }
            var setting = entry ?? new SharedSetting
            {
                Key = section.Key, Value = value, Revision = 1, UpdatedAt = DateTimeOffset.UnixEpoch, UpdatedBy = device
            };
            SharedApply result;
            try { result = await section.ApplyAsync(setting, secret, token); }
            catch (Exception error) when (error is not OperationCanceledException) { result = SharedApply.Waiting(error.Message); }
            if (!result.Applied)
            {
                waiting[section.Key] = result.Note ?? "Not yet.";
                return (false, null, document);
            }
            invalidate?.Invoke();
            try { local = await section.ReadAsync(token); }
            catch (Exception error) when (error is not OperationCanceledException) { local = null; }
        }
        if (local is null) observed.Remove(section.Key);
        else
        {
            observed[section.Key] = local.Digest;
            if (local.Secret is { } own) secrets[SharedSettings.Sha256(own)] = own;
        }
        mismatched.Remove(section.Key);
        if (account && entry is null && local is not null)
            document = document.Put(section.Key, local.Value, local.Secret, device, DateTimeOffset.UtcNow, KeptRevision);
        return (true, local, document);
    }

    /// <summary>Records <paramref name="value"/> for <paramref name="key"/>, an entry this computer has no section for (asking
    /// another computer to become a host PC, for example: "role.desktop-b"), stamped as changed here now, so it wins over
    /// anything older. Call it between syncs; the next <see cref="SyncAsync"/> gives it to the hosts.</summary>
    public void Put(string key, string value, DateTimeOffset now)
    {
        ContractRules.Require(SharedSettings.IsKey(key) && sections.All(s => s.Key != key),
            "Only another computer's entry can be written this way.");
        Document = Document.Put(key, value, null, device, now);
        SharedSettingsState.Save(directory, Document, observed);
    }

    /// <summary>Stamps every setting this computer has (default or not) as changed now, so it becomes the one every computer
    /// uses. Takes effect on the next <see cref="SyncAsync"/> together with the hosts' copies.</summary>
    public async Task<int> ClaimAllAsync(DateTimeOffset now, CancellationToken token)
    {
        invalidate?.Invoke();
        var document = Document;
        var count = 0;
        foreach (var section in sections)
        {
            if (observed.GetValueOrDefault(section.Key) == AdoptingDigest) continue;
            if (await section.ReadAsync(token) is not { } local) continue;
            if (local.Secret is { } own) secrets[SharedSettings.Sha256(own)] = own;
            document = document.Put(section.Key, local.Value, local.Secret, device, now);
            observed[section.Key] = local.Digest;
            count++;
        }
        Document = document;
        SharedSettingsState.Save(directory, document, observed);
        return count;
    }

    // A first share's revision: when the setting last changed here, never later than now and at least 1.
    private static long Evidence(SharedLocal local, DateTimeOffset now) =>
        Math.Clamp(local.ChangedAt?.ToUnixTimeMilliseconds() ?? 1, 1, Math.Max(1, now.ToUnixTimeMilliseconds()));
}
