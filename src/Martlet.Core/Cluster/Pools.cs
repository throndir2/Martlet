using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Martlet.Core.Cluster;

/// <summary>What one pool member is: <see cref="ThisPc"/> (each companion PC itself: its own engine or its own host service),
/// <see cref="Computer"/> (one paired computer; its host picks the graphics card), <see cref="Gpu"/> (one graphics card of a
/// paired computer), <see cref="Address"/> (a service the owner runs at an http or https address, such as their own ComfyUI)
/// or <see cref="Cloud"/> (a cloud provider with its model; its key stays in each PC's credential store,
/// <see cref="PoolKeys"/>). In pools.json: "this-pc", "computer", "gpu", "address" or "cloud".</summary>
public enum PoolMemberKind { ThisPc, Computer, Gpu, Address, Cloud }

/// <summary>The owner agreed that requests to a cloud member leave this PC and may cost money. <see cref="Digest"/> binds the
/// agreement to the area and the member's identity (<see cref="PoolMember.Key"/>: provider, address and model), so a changed
/// member needs a new agreement.</summary>
public sealed record PoolConsent
{
    public required string Digest { get; init; }
    public required DateTimeOffset At { get; init; }
}

/// <summary>One member of a pool. Identity: <see cref="Kind"/> with <see cref="HostId"/> and <see cref="Card"/> (a computer or a
/// card), <see cref="Address"/> (a service the owner runs) or <see cref="Provider"/>, <see cref="Origin"/> and
/// <see cref="Model"/> (a cloud provider). <see cref="Settings"/> holds the area's technology choices for this member (engine,
/// model, voice, workflow, slots...; nonsecret; the keys are the area's own, <see cref="PoolSettingKeys"/>). A member that is
/// <see cref="Off"/> keeps its place and settings and takes no work. <see cref="OnlyFor"/> keeps it for some companion PCs
/// (their device IDs); empty: every one may use it.</summary>
public sealed record PoolMember
{
    public const string ThisPcKey = "this-pc";
    public const int MaximumCard = 8;

    public required PoolMemberKind Kind { get; init; }
    public string? HostId { get; init; }
    /// <summary>A <see cref="PoolMemberKind.Gpu"/> member's card on its computer, 1 to <see cref="MaximumCard"/> (1 is the first
    /// NVIDIA card, as the host's routes count them in <c>gpus</c>).</summary>
    public int? Card { get; init; }
    /// <summary>An <see cref="PoolMemberKind.Address"/> member's http or https address.</summary>
    public string? Address { get; init; }
    /// <summary>A cloud member's provider: "openai", "elevenlabs", "chat-completions", "openrouter", "nvidia-build"...</summary>
    public string? Provider { get; init; }
    /// <summary>A cloud member's API base address, for providers that take one (an OpenAI-compatible endpoint); else null.</summary>
    public string? Origin { get; init; }
    /// <summary>The model: part of a cloud member's identity; for other members the area may use it or
    /// <see cref="Settings"/>.</summary>
    public string? Model { get; init; }
    public bool Off { get; init; }
    public IReadOnlyList<string> OnlyFor { get; init; } = [];
    public IReadOnlyDictionary<string, string> Settings { get; init; } = Empty;
    /// <summary>A cloud member's agreement (<see cref="PoolConsent"/>); null for other kinds.</summary>
    public PoolConsent? Consent { get; init; }

    private static readonly IReadOnlyDictionary<string, string> Empty = new SortedDictionary<string, string>(StringComparer.Ordinal);

    /// <summary>The member's stable key: its lane key in <see cref="WorkQueue"/> (the <c>hostOf</c> of a pool request) and its
    /// key in <see cref="PoolKeys"/>. "this-pc", "host:&lt;id&gt;", "host:&lt;id&gt;#gpu&lt;n&gt;", "address:&lt;url&gt;" or
    /// "cloud:&lt;provider&gt;[@&lt;origin&gt;][/&lt;model&gt;]".</summary>
    [JsonIgnore]
    public string Key => Kind switch
    {
        PoolMemberKind.ThisPc => ThisPcKey,
        PoolMemberKind.Computer => "host:" + HostId,
        PoolMemberKind.Gpu => $"host:{HostId}#gpu{Card}",
        PoolMemberKind.Address => "address:" + Address,
        _ => "cloud:" + Provider + (Origin is null ? "" : "@" + Origin) + (Model is null ? "" : "/" + Model)
    };

    /// <summary>The member's name in plain words: "This PC", "desk-host", "desk-host, card 2", "http://lab:8188/",
    /// "openai (gpt-4o-mini-tts)".</summary>
    [JsonIgnore]
    public string Name => Kind switch
    {
        PoolMemberKind.ThisPc => "This PC",
        PoolMemberKind.Computer => HostId!,
        PoolMemberKind.Gpu => $"{HostId}, card {Card}",
        PoolMemberKind.Address => Address!,
        _ => Provider + (Model is null ? "" : $" ({Model})")
    };

    /// <summary>Whether the member runs on one of the owner's paired computers (a computer or one of its cards).</summary>
    [JsonIgnore] public bool OnHost => Kind is PoolMemberKind.Computer or PoolMemberKind.Gpu;

    public static PoolMember ThisPc() => new() { Kind = PoolMemberKind.ThisPc };
    public static PoolMember Computer(string hostId) => new() { Kind = PoolMemberKind.Computer, HostId = hostId };
    public static PoolMember Gpu(string hostId, int card) => new() { Kind = PoolMemberKind.Gpu, HostId = hostId, Card = card };
    public static PoolMember Service(string address) => new() { Kind = PoolMemberKind.Address, Address = address };
    public static PoolMember Cloud(string provider, string? model, string? origin = null) =>
        new() { Kind = PoolMemberKind.Cloud, Provider = provider, Model = model, Origin = origin };

    /// <summary>The member's setting <paramref name="key"/>, or null.</summary>
    public string? Setting(string key) => Settings.TryGetValue(key, out var value) ? value : null;

    /// <summary>The member with setting <paramref name="key"/> set to <paramref name="value"/> (null removes it).</summary>
    public PoolMember WithSetting(string key, string? value)
    {
        var next = new SortedDictionary<string, string>(Settings.ToDictionary(), StringComparer.Ordinal);
        if (value is null) next.Remove(key);
        else next[key] = value;
        return this with { Settings = next };
    }

    /// <summary>Whether companion PC <paramref name="device"/> may use the member.</summary>
    public bool Allows(string device) => OnlyFor.Count == 0 || OnlyFor.Contains(device, StringComparer.Ordinal);

    /// <summary>The digest a cloud member's agreement for <paramref name="area"/> must carry.</summary>
    public string ConsentDigest(string area) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes("martlet.pool-consent.v1\n" + area + "\n" + Key)));

    /// <summary>Whether the member may take <paramref name="area"/>'s work: always for a member that isn't in the cloud, and for
    /// a cloud member only with the owner's agreement to exactly this member.</summary>
    public bool Consented(string area) => Kind != PoolMemberKind.Cloud || Consent?.Digest == ConsentDigest(area);

    /// <summary>The member with the owner's agreement for <paramref name="area"/>, given at <paramref name="at"/>.</summary>
    public PoolMember WithConsent(string area, DateTimeOffset at) => this with { Consent = new() { Digest = ConsentDigest(area), At = at } };

    internal PoolMember Normalized() => this with
    {
        OnlyFor = [.. OnlyFor.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)],
        Settings = new SortedDictionary<string, string>(Settings.ToDictionary(), StringComparer.Ordinal),
        Consent = Kind == PoolMemberKind.Cloud ? Consent : null
    };

    internal bool Valid()
    {
        var identity = Kind switch
        {
            PoolMemberKind.ThisPc => HostId is null && Card is null && Address is null && Provider is null && Origin is null,
            PoolMemberKind.Computer => PoolText.Name(HostId) && Card is null && Address is null && Provider is null && Origin is null,
            PoolMemberKind.Gpu => PoolText.Name(HostId) && Card is >= 1 and <= MaximumCard && Address is null && Provider is null && Origin is null,
            PoolMemberKind.Address => PoolText.Url(Address) && HostId is null && Card is null && Provider is null && Origin is null,
            PoolMemberKind.Cloud => PoolText.Name(Provider) && (Origin is null || PoolText.Url(Origin)) && HostId is null && Card is null &&
                Address is null,
            _ => false
        };
        return identity && (Model is null || PoolText.Value(Model, 200)) && OnlyFor is { Count: <= PoolSettings.MaximumMembers } &&
            OnlyFor.All(PoolText.Name) && Settings is { Count: <= 32 } &&
            Settings.All(s => PoolText.Name(s.Key) && s.Key.Length <= 48 && PoolText.Value(s.Value, 1024)) &&
            (Consent is null || Consent.Digest is { Length: 64 });
    }
}

/// <summary>The setting keys areas share for the same idea. An area may add its own keys (letters, digits, '.', '_', '-').
/// Never put a secret in a setting: a cloud key goes in the credential store (<see cref="PoolKeys"/>).</summary>
public static class PoolSettingKeys
{
    /// <summary>The engine or host role the member runs for the area ("chatterbox", "parakeet", "audio2face"...).</summary>
    public const string Engine = "engine";
    public const string Model = "model";
    public const string Voice = "voice";
    /// <summary>Pictures: "z-image-turbo", "checkpoint" or "custom".</summary>
    public const string Workflow = "workflow";
    /// <summary>A file in the data directory that holds a larger choice (a custom ComfyUI workflow), by its file name.</summary>
    public const string File = "file";
    public const string Checkpoint = "checkpoint";
    /// <summary>How many requests the member runs at once.</summary>
    public const string Slots = "slots";
    /// <summary>A service this PC runs itself, at a loopback address (lip-sync's own Audio2Face service).</summary>
    public const string Endpoint = "endpoint";
}

/// <summary>An area whose requests go through a pool: its ID (also its <see cref="WorkQueue"/> lane), the member kinds it
/// takes, and what an empty list means. An optional area is off when no member is on. A <see cref="Required"/> area then runs
/// its <see cref="Fallback"/> on this PC's processor (lip-sync: the voice's loudness). <see cref="Shared"/>: the list is the
/// same on all the owner's computers (pools.json, the <c>pools</c> shared setting); otherwise each PC keeps its own
/// (pools-local.json). <see cref="ConversationFirst"/>: the conversation's own route is always tried first and is not a member
/// (Thinking: the list holds only the computers and providers to use when it is busy).</summary>
public sealed record PoolArea
{
    public required string Id { get; init; }
    public required string Title { get; init; }
    /// <summary>Where the list is shown and edited, in the app's words ("Companion › Voice").</summary>
    public required string Page { get; init; }
    public required IReadOnlyList<PoolMemberKind> Kinds { get; init; }
    public bool Required { get; init; }
    /// <summary>What a required area does with an empty list, in plain words.</summary>
    public string? Fallback { get; init; }
    public bool Shared { get; init; } = true;
    public bool ConversationFirst { get; init; }
    /// <summary>The host role a computer or card member runs for the area (stt, audio2face, ocr...), or null when a member's
    /// <see cref="PoolSettingKeys.Engine"/> setting says it (Speaking: the voice engine).</summary>
    public string? HostRole { get; init; }

    /// <summary>What an empty list means, in plain words: "Off" or the fallback.</summary>
    [JsonIgnore] public string WhenEmpty => Required ? Fallback ?? "This PC's processor" : "Off";

    public bool Takes(PoolMemberKind kind) => Kinds.Contains(kind);
}

/// <summary>The areas whose requests go through a pool. Each area's session owns its own entry here.</summary>
public static class PoolAreas
{
    private static readonly PoolMemberKind[] Local = [PoolMemberKind.ThisPc, PoolMemberKind.Computer, PoolMemberKind.Gpu];
    private static readonly PoolMemberKind[] AllKinds =
        [PoolMemberKind.ThisPc, PoolMemberKind.Computer, PoolMemberKind.Gpu, PoolMemberKind.Address, PoolMemberKind.Cloud];

    public static readonly PoolArea Speaking = new()
    {
        Id = ClusterJobs.Speaking, Title = "Speaking", Page = "Companion › Voice",
        Kinds = [.. Local, PoolMemberKind.Cloud]
    };
    public static readonly PoolArea Listening = new()
    {
        Id = ClusterJobs.Listening, Title = "Listening", Page = "Companion › Listening", HostRole = "stt",
        Kinds = [.. Local, PoolMemberKind.Cloud]
    };
    /// <summary>Thinking: the conversation's own model always goes first (its prompt cache), so the list only names where a
    /// reply goes when that model is busy. Empty (the default): a reply waits for its own model.</summary>
    public static readonly PoolArea Thinking = new()
    {
        Id = ClusterJobs.Thinking, Title = "Thinking", Page = "Companion › Thinking", HostRole = "ollama", ConversationFirst = true,
        Kinds = [PoolMemberKind.Computer, PoolMemberKind.Gpu]
    };
    public static readonly PoolArea LipSync = new()
    {
        Id = ClusterJobs.LipSync, Title = "Lip-sync", Page = "Companion › Lip-sync", HostRole = "audio2face", Kinds = Local,
        Required = true, Fallback = "Voice loudness on this PC"
    };
    public static readonly PoolArea Pictures = new()
    {
        Id = "pictures", Title = "Pictures", Page = "Companion › Pictures", HostRole = "pictures", Kinds = AllKinds, Shared = false
    };
    public static readonly PoolArea Singing = new()
    {
        Id = "singing", Title = "Singing", Page = "Companion › Singing", Kinds = Local
    };
    public static readonly PoolArea Vision = new()
    {
        Id = "vision", Title = "Vision", Page = "Companion › Vision", Kinds = [.. Local, PoolMemberKind.Cloud]
    };
    public static readonly PoolArea Hearing = new()
    {
        Id = "hearing", Title = "Hearing", Page = "Companion › Hearing", Kinds = [.. Local, PoolMemberKind.Cloud]
    };
    public static readonly PoolArea Reading = new()
    {
        Id = "reading", Title = "Reading", Page = "Companion › Reading", HostRole = "ocr", Kinds = Local
    };

    public static IReadOnlyList<PoolArea> All { get; } = [Speaking, Listening, Thinking, LipSync, Pictures, Singing, Vision, Hearing, Reading];

    public static PoolArea? Find(string? id) => All.FirstOrDefault(a => a.Id == id);
}

/// <summary>One area's ordered members.</summary>
public sealed record PoolList
{
    public required string Area { get; init; }
    public IReadOnlyList<PoolMember> Members { get; init; } = [];

    /// <summary>The member with <paramref name="key"/>, or null.</summary>
    public PoolMember? Find(string key) => Members.FirstOrDefault(m => m.Key == key);

    /// <summary>Whether no member is on (for an optional area: off).</summary>
    [JsonIgnore] public bool NoneOn => Members.All(m => m.Off);

    /// <summary>The list with <paramref name="member"/> added at the end, or replacing the member with its key in place.</summary>
    public PoolList With(PoolMember member)
    {
        ArgumentNullException.ThrowIfNull(member);
        var index = Members.ToList().FindIndex(m => m.Key == member.Key);
        return this with { Members = index < 0 ? [.. Members, member] : [.. Members.Select((m, i) => i == index ? member : m)] };
    }

    public PoolList Without(string key) => this with { Members = [.. Members.Where(m => m.Key != key)] };

    /// <summary>The list with the member with <paramref name="key"/> moved <paramref name="by"/> places (negative: earlier).</summary>
    public PoolList Move(string key, int by)
    {
        var list = Members.ToList();
        var index = list.FindIndex(m => m.Key == key);
        if (index < 0) return this;
        var to = Math.Clamp(index + by, 0, list.Count - 1);
        var member = list[index];
        list.RemoveAt(index);
        list.Insert(to, member);
        return this with { Members = list };
    }
}

/// <summary>The pools: for each area that has one, its ordered members. Two files with this shape: pools.json (the areas whose
/// list is the same on all the owner's computers, <see cref="PoolArea.Shared"/>; the <c>pools</c> shared setting) and
/// pools-local.json (the areas each PC chooses for itself; never shared). An area missing from its file was never set up as a
/// pool: the area makes its list once from its older choices (<see cref="PoolMigration"/>) and saves it. An area with an
/// empty list is off, or runs its fallback. Nonsecret: host IDs, addresses, providers, models and settings only.</summary>
public sealed record PoolSettings
{
    public const string FileName = "pools.json";
    public const string LocalFileName = "pools-local.json";
    public const string SharedKey = "pools";
    public const int MaximumMembers = 32;
    private static readonly JsonSerializerOptions Canonical = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        RespectNullableAnnotations = true,
        MaxDepth = 10,
        Converters = { new JsonStringEnumConverter<PoolMemberKind>(JsonNamingPolicy.KebabCaseLower, allowIntegerValues: false) }
    };
    private static readonly JsonSerializerOptions Indented = new(Canonical) { WriteIndented = true };

    public int SchemaVersion { get; init; } = 1;
    public IReadOnlyList<PoolList> Pools { get; init; } = [];

    /// <summary>Whether <paramref name="area"/> has a list (it may be empty).</summary>
    public bool Has(string area) => Pools.Any(p => p.Area == area);

    /// <summary><paramref name="area"/>'s list; an empty list when it has none.</summary>
    public PoolList Pool(string area) => Pools.FirstOrDefault(p => p.Area == area) ?? new() { Area = area };

    public PoolSettings With(PoolList list) => Normalized(this with { Pools = [.. Pools.Where(p => p.Area != list.Area), list] });

    [JsonIgnore] public bool IsDefault => Pools.Count == 0;

    // Lists sorted by area and settings by key, members in the owner's order: every computer writes the same JSON for the same
    // choices.
    private static PoolSettings Normalized(PoolSettings settings) => settings with
    {
        Pools = [.. settings.Pools.Select(p => p with { Members = [.. p.Members.Select(m => m.Normalized())] })
            .OrderBy(p => p.Area, StringComparer.Ordinal)]
    };

    private bool Valid() => SchemaVersion == 1 && Pools is { Count: <= 32 } &&
        Pools.All(p => p is not null && PoolText.Name(p.Area) && p.Members is { Count: <= MaximumMembers } &&
            p.Members.All(m => m is not null && m.Valid()) && p.Members.Select(m => m.Key).Distinct(StringComparer.Ordinal).Count() == p.Members.Count) &&
        Pools.Select(p => p.Area).Distinct(StringComparer.Ordinal).Count() == Pools.Count;

    /// <summary>The canonical JSON every computer writes for the same lists, for sharing.</summary>
    public string Share() => JsonSerializer.Serialize(Normalized(this), Canonical);

    /// <summary>Lists another computer shared (<see cref="Share"/>); null when they aren't ones this Martlet reads.</summary>
    public static PoolSettings? Parse(string json)
    {
        try
        {
            var parsed = JsonSerializer.Deserialize<PoolSettings>(json, Canonical);
            return parsed is not null && parsed.Valid() ? Normalized(parsed) : null;
        }
        catch (Exception error) when (error is JsonException or NotSupportedException or ArgumentException or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>The file for areas that are <paramref name="shared"/> (pools.json) or this PC's own (pools-local.json).</summary>
    public static string File(bool shared) => shared ? FileName : LocalFileName;

    /// <summary>Whether the file exists in <paramref name="directory"/>.</summary>
    public static bool Saved(string? directory, bool shared) => directory is not null && System.IO.File.Exists(Path.Combine(directory, File(shared)));

    public static PoolSettings Load(string? directory, bool shared = true)
    {
        if (directory is null) return new();
        try
        {
            var path = Path.Combine(directory, File(shared));
            return System.IO.File.Exists(path) && new FileInfo(path).Length <= 262_144 ? Parse(System.IO.File.ReadAllText(path)) ?? new() : new();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return new(); }
    }

    /// <summary><paramref name="area"/>'s list from the file it lives in; null when the area has none yet.</summary>
    public static PoolList? LoadFor(string? directory, PoolArea area)
    {
        ArgumentNullException.ThrowIfNull(area);
        var settings = Load(directory, area.Shared);
        return settings.Has(area.Id) ? settings.Pool(area.Id) : null;
    }

    /// <summary>Saves <paramref name="list"/> into the file of <paramref name="area"/>, keeping the other areas' lists.</summary>
    public static bool SaveFor(string? directory, PoolArea area, PoolList list)
    {
        ArgumentNullException.ThrowIfNull(area);
        ArgumentNullException.ThrowIfNull(list);
        if (list.Area != area.Id) throw new ArgumentException("The list belongs to another area.", nameof(list));
        return Load(directory, area.Shared).With(list).Save(directory, area.Shared);
    }

    public bool Save(string? directory, bool shared = true)
    {
        if (directory is null || !Valid()) return false;
        try
        {
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, File(shared));
            var temporary = Path.Combine(directory, $"pools.{Guid.NewGuid():N}.tmp");
            try
            {
                System.IO.File.WriteAllText(temporary, JsonSerializer.Serialize(Normalized(this), Indented));
                System.IO.File.Move(temporary, path, overwrite: true);
            }
            finally
            {
                if (System.IO.File.Exists(temporary)) System.IO.File.Delete(temporary);
            }
            return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return false; }
    }
}

/// <summary>This PC's keys for cloud pool members (pool-keys.json; never shared): for each area and member key, the credential
/// ID of the key in Windows Credential Manager. A cloud member with no entry borrows the key of the area's own route for the
/// same provider, when it has one; with neither it is skipped on this PC (its page says to add the key here).</summary>
public sealed record PoolKeys
{
    public const string FileName = "pool-keys.json";
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower, WriteIndented = true };

    public int SchemaVersion { get; init; } = 1;
    /// <summary>"&lt;area&gt; &lt;member key&gt;" to credential ID.</summary>
    public IReadOnlyDictionary<string, Guid> Keys { get; init; } = new Dictionary<string, Guid>(StringComparer.Ordinal);

    private static string Name(string area, string member) => area + " " + member;

    public Guid? For(string area, string member) => Keys.TryGetValue(Name(area, member), out var id) ? id : null;

    public PoolKeys With(string area, string member, Guid? id)
    {
        var next = new Dictionary<string, Guid>(Keys, StringComparer.Ordinal);
        if (id is { } value && value != Guid.Empty) next[Name(area, member)] = value;
        else next.Remove(Name(area, member));
        return this with { Keys = next };
    }

    public static PoolKeys Load(string? directory)
    {
        if (directory is null) return new();
        try
        {
            var path = Path.Combine(directory, FileName);
            if (!File.Exists(path) || new FileInfo(path).Length > 65_536) return new();
            var keys = JsonSerializer.Deserialize<PoolKeys>(File.ReadAllText(path), Json);
            return keys is { SchemaVersion: 1, Keys: not null } ? keys with { Keys = new Dictionary<string, Guid>(keys.Keys, StringComparer.Ordinal) } : new();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or NotSupportedException) { return new(); }
    }

    public bool Save(string? directory)
    {
        if (directory is null) return false;
        try
        {
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, FileName);
            var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
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

/// <summary>The members a companion PC tries for one area's request, first to last (<see cref="PoolRouting.Order"/>).
/// <see cref="Configured"/>: the area has a list (else it still uses its older choices). <see cref="Off"/>: an optional area
/// with no member on. <see cref="Fallback"/>: a required area with no member that can take the request now, so it runs its
/// fallback on this PC's processor.</summary>
public sealed record PoolOrder(PoolArea Area, IReadOnlyList<PoolMember> Members, bool Configured, bool NoneOn)
{
    public bool Off => NoneOn && !Area.Required;
    public bool Fallback => Members.Count == 0 && Area.Required;
}

/// <summary>Which pool members a companion PC tries, in order. Deterministic and instant (no network). A request then goes
/// through <see cref="WorkQueue"/> with the area's ID as its lane and each member's <see cref="PoolMember.Key"/> as its
/// computer (<c>hostOf</c>), so cloud members and addresses queue the same way as host IDs: a busy member (a computer turning a
/// second request away, a cloud provider's rate limit: <see cref="PoolRefusals"/>) is passed over for the next, and when every
/// one is busy the request waits for whichever frees first.</summary>
public static class PoolRouting
{
    /// <summary>The members of <paramref name="list"/> that companion PC <paramref name="device"/> tries for
    /// <paramref name="area"/>, in the owner's order: on, a kind the area takes, kept for this PC or for every one, agreed to
    /// (a cloud member), and <paramref name="usable"/> now (the area's own check: paired, runs the engine, has a key).</summary>
    public static PoolOrder Order(PoolArea area, PoolList? list, string device, Func<PoolMember, bool>? usable = null)
    {
        ArgumentNullException.ThrowIfNull(area);
        var members = list?.Members ?? [];
        var order = members.Where(m => !m.Off && area.Takes(m.Kind) && m.Allows(device) && m.Consented(area.Id) && (usable?.Invoke(m) ?? true))
            .ToArray();
        return new(area, order, list is not null, members.All(m => m.Off));
    }

    /// <summary>The paired computers (host IDs) a host request of <paramref name="area"/> tries, first to last: the list's members
    /// that run the engine (<paramref name="planned"/>, the route's own computer, or one of <paramref name="runs"/>), each
    /// computer once. This PC is this PC's own host service (<paramref name="own"/>); a card is its computer (a host runs one
    /// route per engine). An area whose conversation goes first (Thinking) puts <paramref name="planned"/> first. When no
    /// member can take it, <paramref name="planned"/> still does (an area is turned off by turning its route off).</summary>
    public static IReadOnlyList<string> Hosts(PoolArea area, PoolList list, string device, string planned, string? own, IReadOnlySet<string> runs)
    {
        ArgumentNullException.ThrowIfNull(area);
        ArgumentNullException.ThrowIfNull(runs);
        string? Resolve(PoolMember member)
        {
            var id = member.Kind switch
            {
                PoolMemberKind.ThisPc => own,
                PoolMemberKind.Computer or PoolMemberKind.Gpu => member.HostId,
                _ => null
            };
            return id is not null && (id == planned || runs.Contains(id)) ? id : null;
        }
        var order = Order(area, list, device, m => Resolve(m) is not null).Members.Select(m => Resolve(m)!);
        if (area.ConversationFirst) order = order.Prepend(planned);
        var hosts = order.Distinct(StringComparer.Ordinal).ToList();
        return hosts.Count == 0 ? [planned] : hosts;
    }
}

/// <summary>What a cloud or address member's failure before its first answer means in <see cref="WorkQueue"/>: a rate limit
/// (429) or a full server (503) is busy: try the next member, then wait; a missing or refused key (401, 403), a timeout,
/// another server error or no connection is unavailable: try the next; anything else (a bad request) is a real failure.</summary>
public static class PoolRefusals
{
    public static WorkRefusal Http(int status) => status switch
    {
        429 or 503 => WorkRefusal.Busy,
        401 or 403 or 404 or 408 or >= 500 => WorkRefusal.Unavailable,
        _ => WorkRefusal.None
    };

    /// <summary>A request that never reached the member (no connection, a timeout): unavailable.</summary>
    public static WorkRefusal Network(Exception error) =>
        error is HttpRequestException or IOException or TimeoutException ? WorkRefusal.Unavailable : WorkRefusal.None;
}

/// <summary>Makes an area's first list from its older choices, once, so no choice the owner made is lost.</summary>
public static class PoolMigration
{
    /// <summary>The Speaking, Listening or Thinking list from Devices › Sharing work (work-sharing.json) and the area's own
    /// choice <paramref name="current"/> (its route as a member: a computer, this PC or a cloud provider; null when it has
    /// none). Not shared: only <paramref name="current"/> (Thinking: nothing; its own model always goes first). Shared: the
    /// computers in the chosen order (this-pc is each companion PC's own host service), then <paramref name="current"/>, then
    /// this PC's own host service, then the other computers in <paramref name="runs"/> that run the engine, fewest jobs first.
    /// Computers the job never used are left out, except <paramref name="current"/>. Each computer kept for some companion PCs
    /// keeps that (<see cref="PoolMember.OnlyFor"/>).</summary>
    public static PoolList FromWorkSharing(PoolArea area, WorkSharingSettings legacy, PoolMember? current, IReadOnlyList<WorkPlace> runs)
    {
        ArgumentNullException.ThrowIfNull(area);
        ArgumentNullException.ThrowIfNull(legacy);
        ArgumentNullException.ThrowIfNull(runs);
        var rules = legacy.Job(area.Id);
        List<PoolMember> members = [];
        void Add(PoolMember member, bool always = false)
        {
            if (members.Any(m => m.Key == member.Key) || !area.Takes(member.Kind)) return;
            if (member.HostId is { } host)
            {
                if (!always && rules.Never.Contains(host, StringComparer.Ordinal)) return;
                member = member with { OnlyFor = legacy.OnlyFor(host) };
            }
            members.Add(member);
        }
        if (area.ConversationFirst)
        {
            if (rules.Shares)
                foreach (var id in rules.Order.Concat(runs.OrderBy(p => p.Jobs).ThenBy(p => p.HostId, StringComparer.Ordinal).Select(p => p.HostId)))
                    if (id != WorkSharingSettings.ThisPc && id != current?.HostId) Add(PoolMember.Computer(id));
            return new() { Area = area.Id, Members = members };
        }
        if (!rules.Shares)
        {
            if (current is not null) Add(current, always: true);
            return new() { Area = area.Id, Members = members };
        }
        foreach (var id in rules.Order) Add(id == WorkSharingSettings.ThisPc ? PoolMember.ThisPc() : PoolMember.Computer(id));
        if (current is not null) Add(current, always: true);
        if (current is not null && runs.Any(p => p.Own)) Add(PoolMember.ThisPc());
        foreach (var place in runs.Where(p => !p.Own).OrderBy(p => p.Jobs).ThenBy(p => p.HostId, StringComparer.Ordinal))
            Add(PoolMember.Computer(place.HostId));
        return new() { Area = area.Id, Members = members };
    }
}

internal static class PoolText
{
    internal static bool Name(string? text) => text is { Length: > 0 and <= 64 } && char.IsAsciiLetterOrDigit(text[0]) &&
        text.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-');

    internal static bool Url(string? text) => text is { Length: > 0 and <= 256 } &&
        Uri.TryCreate(text, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" && string.IsNullOrEmpty(uri.UserInfo);

    internal static bool Value(string? text, int maximum) => text is not null && text.Length <= maximum && !text.Any(char.IsControl);
}
