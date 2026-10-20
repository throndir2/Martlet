using System.Text.Json;
using Martlet.Core.Contracts;
using Martlet.Core.Pictures;
using Martlet.Core.Settings;
using Martlet.Core.Sync;

namespace Martlet.Core.Cluster;

/// <summary>The API keys of cloud and server members of the pool lists (pool-keys.json: Speaking, Listening, Picture making,
/// Vision and Hearing), carried to the owner's other computers as the household setting <see cref="SharedKey"/> (#805): its value
/// names which member of which area has a key of its own, its secret bundles the distinct keys (as the Thinking pool does,
/// <see cref="ThinkingPoolShare.Bundle"/>). Each computer keeps the keys in its own credential store (Windows Credential Manager)
/// under the binding the area uses; pool-keys.json keeps which credential each member uses on this computer.</summary>
public static class PoolKeyShare
{
    public const string SharedKey = "pool-keys";
    public const int SchemaVersion1 = 1;

    /// <summary>The areas whose members can have a key of their own in pool-keys.json.</summary>
    public static IReadOnlyList<PoolArea> Areas { get; } =
        [PoolAreas.Speaking, PoolAreas.Listening, PoolAreas.PictureMaking, PoolAreas.Vision, PoolAreas.Hearing];

    /// <summary>One member with a key of its own: its area, its member key and the index of its key in the bundle.</summary>
    public sealed record Entry(string Area, string Member, int Key);

    private sealed record Document(int SchemaVersion, IReadOnlyList<Entry> Members);

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { MaxDepth = 6 };

    /// <summary>Where <paramref name="member"/>'s own key of <paramref name="area"/> is kept in the credential store, with the binding
    /// the area reads it with (the same as each page writes it); null for a member that can't have one.</summary>
    public static CredentialBinding? Binding(PoolArea area, PoolMember member, Guid profile, Guid credential)
    {
        ArgumentNullException.ThrowIfNull(area);
        ArgumentNullException.ThrowIfNull(member);
        if (area == PoolAreas.Speaking || area == PoolAreas.Listening)
        {
            if (member.Kind != PoolMemberKind.Cloud) return null;
            var role = area == PoolAreas.Speaking ? SetupRole.Tts : SetupRole.Stt;
            return member.Provider == "elevenlabs"
                ? new CredentialBinding(profile, credential, role, SetupRouteType.ElevenLabs, ElevenLabsSetup.Alias, ElevenLabsSetup.Origin)
                : new CredentialBinding(profile, credential, role, SetupRouteType.OpenAi, OpenAiSetup.Alias(role), OpenAiSetup.Origin);
        }
        if (area == PoolAreas.PictureMaking)
            return PictureMakingPoolMembers.Place(member, null, null) is { Cloud: true } place ? place.Binding(profile, credential) : null;
        if (area == PoolAreas.Vision || area == PoolAreas.Hearing)
        {
            var origin = member.Kind == PoolMemberKind.Cloud ? SenseLists.BaseUrl(member) : member.Kind == PoolMemberKind.Address ? member.Address?.TrimEnd('/') : null;
            return origin is null ? null : new DeepThinkingSettings { Place = DeepThinkingPlace.Endpoint, Origin = origin, ModelId = "key" }.Binding(profile, credential);
        }
        return null;
    }

    /// <summary>The members with a key of their own and the distinct keys, as this computer has them.</summary>
    public static (IReadOnlyList<Entry> Entries, IReadOnlyList<string> Keys) Collect(string directory, Guid profile, ICredentialStore vault)
    {
        var saved = PoolKeys.Load(directory);
        List<Entry> entries = [];
        List<string> keys = [];
        foreach (var area in Areas)
        {
            if (PoolSettings.LoadFor(directory, area) is not { } list) continue;
            foreach (var member in list.Members)
            {
                if (saved.For(area.Id, member.Key) is not { } id || Binding(area, member, profile, id) is not { } binding) continue;
                if (Read(vault, binding) is not { } key) continue;
                var index = keys.IndexOf(key);
                if (index < 0)
                {
                    keys.Add(key);
                    index = keys.Count - 1;
                }
                entries.Add(new(area.Id, member.Key, index));
            }
        }
        return ([.. entries.OrderBy(e => e.Area, StringComparer.Ordinal).ThenBy(e => e.Member, StringComparer.Ordinal)], keys);
    }

    public static string Share(IReadOnlyList<Entry> entries) => JsonSerializer.Serialize(new Document(SchemaVersion1, entries), Json);

    /// <summary>A travelled list, or null when it is a newer Martlet's or can't be read.</summary>
    public static IReadOnlyList<Entry>? Parse(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        try
        {
            var document = JsonSerializer.Deserialize<Document>(value, Json);
            if (document is not { SchemaVersion: SchemaVersion1, Members: { Count: <= 5 * PoolSettings.MaximumMembers } members } ||
                members.Any(e => e is null || Areas.All(a => a.Id != e.Area) || e.Member is not { Length: > 0 and <= 1024 } || e.Key < 0))
                return null;
            return members;
        }
        catch (Exception error) when (error is JsonException or NotSupportedException) { return null; }
    }

    /// <summary>The shared setting backed by pool-keys.json and the pool lists in <paramref name="directory"/>, with the keys in
    /// <paramref name="vault"/>. A computer that already has the same key for a member keeps it; a new one is saved; a member whose
    /// key is no longer shared loses it here (the credential is removed when no other member uses it).</summary>
    public static ISharedSection Section(string directory, ICredentialStore vault) => new SecretSection(SharedKey, "Pool API keys",
        async token =>
        {
            var settings = (await new SettingsStore(directory).LoadAsync(token).ConfigureAwait(false)).Settings;
            var path = Path.Combine(directory, PoolKeys.FileName);
            DateTimeOffset? changed = File.Exists(path) ? new DateTimeOffset(File.GetLastWriteTimeUtc(path), TimeSpan.Zero) : null;
            if (settings is null) return null;
            var (entries, keys) = Collect(directory, settings.Profile.Id, vault);
            return new SharedLocal(Share(entries), keys.Count == 0 ? null : ThinkingPoolShare.Bundle(keys), entries.Count == 0, changed);
        },
        async (setting, secret, token) =>
        {
            if (Parse(setting.Value) is not { } entries) return SharedApply.Waiting("They were saved by a newer Martlet; update this PC to use them.");
            IReadOnlyList<string> keys;
            try { keys = ThinkingPoolShare.Unbundle(secret); }
            catch (ContractException error) { return SharedApply.Waiting(error.Message); }
            if (entries.Any(e => e.Key >= keys.Count)) return SharedApply.Waiting("Their API keys haven't reached this PC yet.");
            var settings = (await new SettingsStore(directory).LoadAsync(token).ConfigureAwait(false)).Settings;
            if (settings is null) return SharedApply.Waiting("Set up Martlet on this PC first.");
            var profile = settings.Profile.Id;
            var saved = PoolKeys.Load(directory);
            var next = saved;
            List<CredentialBinding> written = [], gone = [];
            var lists = Areas.ToDictionary(a => a.Id, a => PoolSettings.LoadFor(directory, a));
            foreach (var entry in entries)
            {
                var area = PoolAreas.Find(entry.Area)!;
                if (lists[area.Id]?.Find(entry.Member) is not { } member)
                    return SharedApply.Waiting($"The {area.Title} list hasn't reached this PC yet.");
                var old = saved.For(area.Id, member.Key);
                if (old is { } kept && Binding(area, member, profile, kept) is { } keptBinding && Read(vault, keptBinding) == keys[entry.Key]) continue;
                var fresh = Guid.NewGuid();
                if (Binding(area, member, profile, fresh) is not { } binding) continue;
                using (var lease = new SecretLease(keys[entry.Key]))
                    if (vault.Write(binding, lease) is var error && error != CredentialError.None)
                    {
                        foreach (var made in written) vault.Delete(made);
                        return SharedApply.Waiting("A pool key couldn't be saved on this PC (" + error + ").");
                    }
                written.Add(binding);
                if (old is { } replaced && Binding(area, member, profile, replaced) is { } replacedBinding) gone.Add(replacedBinding);
                next = next.With(area.Id, member.Key, fresh);
            }
            foreach (var (name, id) in saved.Keys)
            {
                var space = name.IndexOf(' ');
                if (space < 0 || entries.Any(e => e.Area + " " + e.Member == name)) continue;
                var area = PoolAreas.Find(name[..space]);
                var member = area is null ? null : lists.GetValueOrDefault(area.Id)?.Find(name[(space + 1)..]);
                // A key of a member the shared lists no longer have stays until its list changes here (the page removes it then).
                if (area is null || member is null) continue;
                next = next.With(area.Id, member.Key, null);
                if (Binding(area, member, profile, id) is { } binding) gone.Add(binding);
            }
            if (!ReferenceEquals(next, saved) && !next.Save(directory))
            {
                foreach (var made in written) vault.Delete(made);
                return SharedApply.Waiting("This PC's pool keys couldn't be saved. Check access to Martlet's data folder.");
            }
            foreach (var binding in gone.Where(b => !next.Keys.Values.Contains(b.CredentialId))) vault.Delete(binding);
            return SharedApply.Done;
        });

    private static string? Read(ICredentialStore vault, CredentialBinding binding)
    {
        using var read = vault.Read(binding);
        if (read.Error != CredentialError.None || read.Secret is null) return null;
        string? value = null;
        read.Secret.Use(chars => value = new string(chars));
        return value;
    }
}
