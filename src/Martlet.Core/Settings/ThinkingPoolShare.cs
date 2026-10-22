using System.Buffers.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Martlet.Core.Contracts;

namespace Martlet.Core.Settings;

/// <summary>The part of Companion › Background thinking that is the same on all your computers (the <c>thinking-pool</c> shared
/// setting): the cloud providers and the servers on your network (endpoint members not on this PC, such as NVIDIA Build or
/// OpenRouter), in order, each on or off with its slots and its choices, and their API keys. Paired computers join each PC's
/// pool by themselves and models in Ollama on this PC are that PC's own, so they stay in each PC's thinking-pool.json.</summary>
public sealed record SharedThinkingPool
{
    public const string Key = "thinking-pool";
    public const int SchemaVersion1 = 1;

    public int SchemaVersion { get; init; } = SchemaVersion1;
    public IReadOnlyList<SharedPoolEndpoint> Members { get; init; } = [];
}

/// <summary>One shared Thinking pool member: an OpenAI-compatible endpoint that is not on the computer itself.
/// <see cref="Key"/> is the index of its API key in the setting's key bundle (<see cref="ThinkingPoolShare.Bundle"/>), or null
/// when it has no key of its own (a server without a key, or one that borrows Thinking's key for the same address).</summary>
public sealed record SharedPoolEndpoint
{
    public required string Origin { get; init; }
    public required string Model { get; init; }
    public bool On { get; init; } = true;
    public int? Slots { get; init; }
    public int? Key { get; init; }
    public bool QuickJobs { get; init; } = true;
    public bool LongJobs { get; init; } = true;
    public bool Media { get; init; }
    public bool Answers { get; init; }
    public ThinkingSmarts? Smarts { get; init; }

    [JsonIgnore] public string MemberKey => $"endpoint:{Origin}|{Model}";
}

/// <summary>The Thinking pool's choices for the whole pool that are the same on all your computers (the
/// <c>thinking-pool-choices</c> shared setting): use the conversation model while the pool is empty, Backup for slow replies
/// and its wait, and Busy pool. They travel apart from the members, so a PC that only changed a choice never replaces the cloud
/// models another PC added.</summary>
public sealed record SharedThinkingPoolChoices
{
    public const string Key = "thinking-pool-choices";
    public const int SchemaVersion1 = 1;

    public int SchemaVersion { get; init; } = SchemaVersion1;
    public bool UseConversationModelWhenEmpty { get; init; } = true;
    public bool BackupThinking { get; init; }
    public int? BackupDelayMs { get; init; }
    public bool PreemptLowerPriority { get; init; } = true;
    public int RaisePriorityAfterStops { get; init; } = ThinkingPoolSettings.DefaultRaiseAfterStops;
    public int RetriesOnFailure { get; init; } = ThinkingPoolSettings.DefaultRetries;

    /// <summary>Whether these are Martlet's defaults (a default never replaces a choice made on another computer).</summary>
    [JsonIgnore] public bool IsDefault => this == new SharedThinkingPoolChoices();
}

/// <summary>Reads the shared part of a Thinking pool and makes a PC's pool follow it, keeping that PC's own members (paired
/// computers and models in Ollama on this PC) and their choices.</summary>
public static class ThinkingPoolShare
{
    private const int MaximumMembers = 2 * DeepThinkingSettings.MaxPlaces;
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter<ThinkingSmarts>(JsonNamingPolicy.SnakeCaseLower) },
        MaxDepth = 8
    };

    /// <summary>Whether <paramref name="member"/> is shared with your other computers: an endpoint that is not on this PC.</summary>
    public static bool IsShared(DeepThinkingSettings member) => ThinkingPoolSettings.IsExternal(member);

    /// <summary>The shared part of <paramref name="pool"/> and the distinct API keys its members use, in the order members first
    /// use them. <paramref name="key"/> reads a member's own key (null when it has none or it can't be read).</summary>
    public static (SharedThinkingPool Pool, IReadOnlyList<string> Keys) From(ThinkingPoolSettings pool, Func<DeepThinkingSettings, string?> key)
    {
        ArgumentNullException.ThrowIfNull(pool);
        ArgumentNullException.ThrowIfNull(key);
        var keys = new List<string>();
        var members = new List<SharedPoolEndpoint>();
        foreach (var (member, on) in pool.Members.Select(m => (m, true)).Concat(pool.OffMembers.Select(m => (m, false))))
        {
            if (!IsShared(member)) continue;
            int? index = null;
            if (member.CredentialId is not null && key(member) is { Length: > 0 } value)
            {
                index = keys.IndexOf(value);
                if (index < 0)
                {
                    keys.Add(value);
                    index = keys.Count - 1;
                }
            }
            members.Add(new()
            {
                Origin = member.Origin!, Model = member.ModelId!, On = on, Slots = member.Slots, Key = index,
                QuickJobs = pool.TakesQuickJobs(member.Key), LongJobs = pool.TakesLongJobs(member.Key),
                Media = pool.MediaAllowed.Contains(member.Key, StringComparer.Ordinal), Answers = pool.Answers(member.Key),
                Smarts = pool.Smarts.TryGetValue(member.Key, out var smarts) ? smarts : null
            });
        }
        return (new() { Members = members }, keys);
    }

    /// <summary><paramref name="local"/> with its shared members replaced by <paramref name="shared"/>'s, after its own members.
    /// Rules of the PC's own members stay; a shared member that left takes its rules with it. <paramref name="credential"/> gives
    /// the credential ID of a member's key on this PC (null: no key of its own). Throws when the result isn't a valid pool.</summary>
    public static ThinkingPoolSettings Apply(ThinkingPoolSettings local, SharedThinkingPool shared, Func<SharedPoolEndpoint, Guid?> credential,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(local);
        ArgumentNullException.ThrowIfNull(shared);
        ArgumentNullException.ThrowIfNull(credential);
        Validate(shared);
        var replaced = local.All.Where(IsShared).Select(m => m.Key).Concat(shared.Members.Select(m => m.MemberKey))
            .ToHashSet(StringComparer.Ordinal);
        var incoming = shared.Members.Select(s => (Shared: s, Member: new DeepThinkingSettings
        {
            Place = DeepThinkingPlace.Endpoint, Origin = s.Origin, ModelId = s.Model, CredentialId = credential(s), Slots = s.Slots,
            ChosenAt = local.All.FirstOrDefault(m => m.Key == s.MemberKey)?.ChosenAt ?? now
        })).ToArray();
        var gone = local.All.Where(IsShared).Select(m => m.Key).Where(k => shared.Members.All(s => s.MemberKey != k)).ToHashSet(StringComparer.Ordinal);
        IReadOnlyList<string> Rule(IReadOnlyList<string> list, Func<SharedPoolEndpoint, bool> pick) =>
            [.. list.Where(k => !replaced.Contains(k)), .. shared.Members.Where(pick).Select(s => s.MemberKey)];
        var smarts = local.Smarts.Where(s => !replaced.Contains(s.Key)).ToDictionary(s => s.Key, s => s.Value, StringComparer.Ordinal);
        foreach (var member in shared.Members)
            if (member.Smarts is { } level) smarts[member.MemberKey] = level;
        var next = local with
        {
            Members = [.. local.Members.Where(m => !IsShared(m)), .. incoming.Where(i => i.Shared.On).Select(i => i.Member)],
            OffMembers = [.. local.OffMembers.Where(m => !IsShared(m)), .. incoming.Where(i => !i.Shared.On).Select(i => i.Member)],
            NoQuickJobs = Rule(local.NoQuickJobs, s => !s.QuickJobs),
            NoLongJobs = Rule(local.NoLongJobs, s => !s.LongJobs),
            MediaAllowed = Rule(local.MediaAllowed, s => s.Media),
            AnswersForConversation = Rule(local.AnswersForConversation, s => s.Answers),
            Smarts = smarts,
            // A kind that ran on chosen members keeps the others; one that left is no longer chosen.
            RunsOnByKind = local.RunsOnByKind.ToDictionary(r => r.Key, r => r.Value.Members.Any(gone.Contains)
                ? r.Value with { Members = [.. r.Value.Members.Where(k => !gone.Contains(k))] } : r.Value, StringComparer.Ordinal)
        };
        next.Validate();
        return next;
    }

    public static string Write(SharedThinkingPool pool) => JsonSerializer.Serialize(pool, Json);

    /// <summary>The choices for the whole pool that <paramref name="pool"/> shares with your other computers.</summary>
    public static SharedThinkingPoolChoices ChoicesOf(ThinkingPoolSettings pool)
    {
        ArgumentNullException.ThrowIfNull(pool);
        return new()
        {
            UseConversationModelWhenEmpty = pool.UseConversationModelWhenEmpty, BackupThinking = pool.BackupThinking,
            BackupDelayMs = pool.BackupDelayMs, PreemptLowerPriority = pool.PreemptLowerPriority,
            RaisePriorityAfterStops = pool.RaisePriorityAfterStops, RetriesOnFailure = pool.RetriesOnFailure
        };
    }

    /// <summary><paramref name="local"/> with <paramref name="choices"/>; its members and their rules stay. Throws when the result
    /// isn't a valid pool.</summary>
    public static ThinkingPoolSettings Apply(ThinkingPoolSettings local, SharedThinkingPoolChoices choices)
    {
        ArgumentNullException.ThrowIfNull(local);
        ArgumentNullException.ThrowIfNull(choices);
        var next = local with
        {
            UseConversationModelWhenEmpty = choices.UseConversationModelWhenEmpty, BackupThinking = choices.BackupThinking,
            BackupDelayMs = choices.BackupDelayMs, PreemptLowerPriority = choices.PreemptLowerPriority,
            RaisePriorityAfterStops = choices.RaisePriorityAfterStops, RetriesOnFailure = choices.RetriesOnFailure
        };
        next.Validate();
        return next;
    }

    public static string WriteChoices(SharedThinkingPoolChoices choices) => JsonSerializer.Serialize(choices, Json);

    /// <summary>The choices another computer wrote, or null when a newer Martlet wrote them.</summary>
    public static SharedThinkingPoolChoices? ParseChoices(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<SharedThinkingPoolChoices>(json, Json) is { SchemaVersion: SharedThinkingPoolChoices.SchemaVersion1 } choices
                ? choices : null;
        }
        catch (Exception error) when (error is JsonException or NotSupportedException or InvalidOperationException) { return null; }
    }

    /// <summary>The shared part another computer wrote, or null when a newer Martlet wrote it. Throws when it is invalid.</summary>
    public static SharedThinkingPool? Parse(string json)
    {
        SharedThinkingPool? pool;
        try { pool = JsonSerializer.Deserialize<SharedThinkingPool>(json, Json); }
        catch (Exception error) when (error is JsonException or NotSupportedException or InvalidOperationException) { return null; }
        if (pool is null || pool.SchemaVersion != SharedThinkingPool.SchemaVersion1) return null;
        Validate(pool);
        return pool;
    }

    private static void Validate(SharedThinkingPool pool)
    {
        ContractRules.Require(pool.Members is { Count: <= MaximumMembers } && pool.Members.All(m => m is not null),
            $"The shared Thinking pool has at most {MaximumMembers} cloud providers and servers.");
        foreach (var member in pool.Members)
        {
            var origin = ChatCompletionsSetup.BaseUri(member.Origin ?? "");
            ContractRules.Require(!origin.IsLoopback, "A shared Thinking pool member can't be on the computer itself.");
            ChatCompletionsSetup.ModelId(member.Model ?? "");
            ContractRules.Require(member.Slots is null or >= 1 and <= DeepThinkingSettings.MaxPlaces && member.Key is null or >= 0 and < MaximumMembers &&
                (member.Smarts is null || Enum.IsDefined(member.Smarts.Value)), "A shared Thinking pool member is invalid.");
        }
        ContractRules.Require(pool.Members.Select(m => m.MemberKey).Distinct(StringComparer.Ordinal).Count() == pool.Members.Count,
            "The shared Thinking pool lists a member twice.");
    }

    /// <summary>The API keys as one shared secret (base64url of a JSON list, which the shared settings accept), or null for none.
    /// Throws when they are too long to share together.</summary>
    public static string? Bundle(IReadOnlyList<string> keys)
    {
        ArgumentNullException.ThrowIfNull(keys);
        if (keys.Count == 0) return null;
        var bundle = Base64Url.EncodeToString(JsonSerializer.SerializeToUtf8Bytes(keys));
        ContractRules.Require(Sync.SharedSettings.IsSecret(bundle),
            "The Thinking pool's cloud API keys are too long to share together. Remove a provider with a different key.");
        return bundle;
    }

    /// <summary>The API keys in a shared secret (<see cref="Bundle"/>); empty for none. Throws when it isn't a key bundle.</summary>
    public static IReadOnlyList<string> Unbundle(string? secret)
    {
        if (secret is null) return [];
        try
        {
            var keys = JsonSerializer.Deserialize<string[]>(Base64Url.DecodeFromChars(secret));
            if (keys is { Length: > 0 and <= MaximumMembers } && keys.All(k => k is { Length: > 0 and <= 1024 } && !k.Any(c => char.IsControl(c) || char.IsWhiteSpace(c))))
                return keys;
        }
        catch (Exception error) when (error is JsonException or FormatException or NotSupportedException) { }
        throw new ContractException(ErrorCode.InvalidContract, "The Thinking pool's shared API keys can't be read. Update this PC if another computer runs a newer Martlet.");
    }
}
