using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Martlet.Core.Contracts;
using Martlet.Core.Lorebooks;
using Martlet.Core.Settings;
using Martlet.Core.Sync;

namespace Martlet.Core.Sharing;

/// <summary>How an account shares one of its characters with the household (docs/ACCOUNTS.md, "Sharing"). A character that
/// isn't shared is private and isn't listed at all.</summary>
public enum CharacterShareMode
{
    /// <summary>Household members can use a copy: its personality, look, voice and lorebooks go to their account, with its own
    /// memories.</summary>
    Copy,
    /// <summary>Household members talk to the same character; it remembers everyone in its own memory space.</summary>
    Together
}

/// <summary>A shared character's personality, as the owner has it now.</summary>
public sealed record SharedPersona
{
    public required Guid Id { get; init; }
    public required string Name { get; init; }
    public required string Text { get; init; }
    public SpeechBreaks? Breaks { get; init; }
}

/// <summary>One character an account shares with the household: its profile ID and name, how it is shared, its personality,
/// its look and voice (household items named by their shared IDs, so the emotes come with the look) and the lorebooks that
/// are on only for its personality.</summary>
public sealed record SharedCharacter
{
    public required Guid Id { get; init; }
    public required CharacterShareMode Mode { get; init; }
    public required string Name { get; init; }
    public required SharedPersona Persona { get; init; }
    public string? ModelId { get; init; }
    public string? VoiceId { get; init; }
    public IReadOnlyList<Lorebook> Lorebooks { get; init; } = [];

    /// <summary>The memory space of a character shared together (<see cref="MemorySpaceId.Character"/>).</summary>
    [JsonIgnore]
    public string Space => MemorySpaceId.Character(Id);

    /// <summary>A short, stable key for automation IDs: the first 8 hex digits of its ID (as <see cref="CharacterProfile.Key"/>).</summary>
    [JsonIgnore]
    public string Key => Id.ToString("N")[..8];
}

/// <summary>A character another account shares together that this account talks to: a mirror in this account's characters
/// with the same IDs.</summary>
public sealed record JoinedCharacter
{
    public required Guid AccountId { get; init; }
    public required Guid CharacterId { get; init; }
}

/// <summary>
/// What one account shares with the household: the household shared-settings entry <c>sharing.&lt;account 32 hex&gt;</c>
/// (<see cref="Key"/>). Only that account's devices write it, members as well as admins. It lists the characters the account
/// shares (<see cref="Characters"/>, a snapshot its devices refresh after each change), the characters of other accounts it
/// talks to together (<see cref="Joined"/>) and *Share new memories about me* (<see cref="NewFactsAboutMe"/>: new facts about
/// this person go to the household memory space). The entry is the one place that holds this choice, because it works on every
/// PC, also before the account reaches the account directory; its devices copy it to the directory entry's
/// <c>sharing.memories_about_me</c> when the account is there. Other accounts' devices read this entry to show Household
/// characters, to use a copy and to keep a character shared together the same as its owner's. Snake-case JSON, schema 1, at most
/// <see cref="MaximumCharacters"/> characters, <see cref="MaximumJoined"/> joined characters and <see cref="MaximumBytes"/> bytes.
/// </summary>
public sealed record HouseholdSharing
{
    public const int SchemaVersion1 = 1;
    public const string KeyPrefix = "sharing.";
    public const int MaximumCharacters = CompanionSettings.MaximumCharacters;
    public const int MaximumJoined = 64;
    public const int MaximumBytes = 256 * 1024;

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower, allowIntegerValues: false) },
        MaxDepth = 32
    };

    public int SchemaVersion { get; init; } = SchemaVersion1;
    public required Guid AccountId { get; init; }
    public bool NewFactsAboutMe { get; init; }
    public IReadOnlyList<SharedCharacter> Characters { get; init; } = [];
    public IReadOnlyList<JoinedCharacter> Joined { get; init; } = [];

    /// <summary>Nothing shared yet.</summary>
    public static HouseholdSharing Empty(Guid account) => new() { AccountId = account };

    /// <summary>The household entry's name for <paramref name="account"/>: "sharing." and 32 lowercase hex digits.</summary>
    public static string Key(Guid account) => KeyPrefix + account.ToString("N");

    /// <summary>The account a <c>sharing.&lt;32 hex&gt;</c> entry belongs to, or null for any other key.</summary>
    public static Guid? AccountOf(string? key) =>
        key is { Length: 40 } && key.StartsWith(KeyPrefix, StringComparison.Ordinal) &&
        key[KeyPrefix.Length..].All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f') &&
        Guid.TryParseExact(key[KeyPrefix.Length..], "N", out var id) && id != Guid.Empty ? id : null;

    public static bool IsKey(string? key) => AccountOf(key) is not null;

    public SharedCharacter? Find(Guid character) => Characters.FirstOrDefault(c => c.Id == character);

    /// <summary>How this account shares <paramref name="character"/>, or null when it is private.</summary>
    public CharacterShareMode? ModeOf(Guid character) => Find(character)?.Mode;

    /// <summary>Whether this account talks to <paramref name="character"/> of another account together.</summary>
    public bool HasJoined(Guid character) => Joined.Any(j => j.CharacterId == character);

    /// <summary>Shares the profile <paramref name="character"/> as <paramref name="mode"/> (null makes it private again), with a
    /// snapshot of it as <paramref name="companion"/> and <paramref name="lorebooks"/> have it now. Throws
    /// <see cref="ContractException"/> when it isn't one of this account's own profiles or the entry would be too large.</summary>
    public HouseholdSharing WithMode(Guid character, CharacterShareMode? mode, CompanionSettings companion, LorebookLibrary lorebooks)
    {
        ArgumentNullException.ThrowIfNull(companion);
        ArgumentNullException.ThrowIfNull(lorebooks);
        var others = Characters.Where(c => c.Id != character).ToArray();
        if (mode is not { } chosen) return Checked(this with { Characters = others });
        var profile = companion.CharacterList.FirstOrDefault(c => c.Id == character);
        ContractRules.Require(profile is not null, "Select one of your character profiles.");
        ContractRules.Require(!HasJoined(character), "Only the owner of a character shared together can share it.");
        var next = this with { Characters = [.. others, Snapshot(profile!, chosen, companion, lorebooks)] };
        try { return Checked(next); }
        catch (ContractException error) when (error.Code == ErrorCode.PayloadTooLarge)
        {
            throw new ContractException(ErrorCode.PayloadTooLarge,
                "Its lorebooks are too large to share with the household. Make them smaller, then share it again.");
        }
    }

    /// <summary>The shared characters as <paramref name="companion"/> and <paramref name="lorebooks"/> have them now: an edited
    /// character is shared as it is now, and a removed one is no longer shared. When the new snapshots don't fit, the
    /// characters keep their last snapshots.</summary>
    public HouseholdSharing Refresh(CompanionSettings companion, LorebookLibrary lorebooks)
    {
        ArgumentNullException.ThrowIfNull(companion);
        ArgumentNullException.ThrowIfNull(lorebooks);
        CharacterProfile? Present(Guid id) =>
            companion.CharacterList.FirstOrDefault(c => c.Id == id) is { } profile && companion.Personas.Any(p => p.Id == profile.PersonaId) ? profile : null;
        var next = this with
        {
            Characters = [.. Characters.Select(shared => Present(shared.Id) is { } profile ? Snapshot(profile, shared.Mode, companion, lorebooks) : null)
                .OfType<SharedCharacter>()]
        };
        return Fits(next) ? next : this with { Characters = [.. Characters.Where(c => Present(c.Id) is not null)] };
    }

    public HouseholdSharing WithNewFactsAboutMe(bool on) => this with { NewFactsAboutMe = on };

    /// <summary>Records that this account talks to <paramref name="character"/> of <paramref name="account"/> together.</summary>
    public HouseholdSharing WithJoined(Guid account, Guid character) => HasJoined(character) ? this
        : Checked(this with { Joined = [.. Joined, new JoinedCharacter { AccountId = account, CharacterId = character }] });

    public HouseholdSharing WithoutJoined(Guid character) => this with { Joined = [.. Joined.Where(j => j.CharacterId != character)] };

    private static SharedCharacter Snapshot(CharacterProfile profile, CharacterShareMode mode, CompanionSettings companion, LorebookLibrary lorebooks)
    {
        var persona = companion.Personas.Single(p => p.Id == profile.PersonaId);
        return new SharedCharacter
        {
            Id = profile.Id,
            Mode = mode,
            Name = profile.Name,
            Persona = new SharedPersona { Id = persona.Id, Name = persona.Name, Text = persona.Text, Breaks = persona.Breaks },
            ModelId = profile.ModelId,
            VoiceId = profile.VoiceId,
            // Only the lorebooks that are on for this personality go with it, without the owner's other personas.
            Lorebooks = [.. lorebooks.Books.Where(book => book.Activation == LorebookActivation.SelectedPersonas && book.PersonaIds.Contains(persona.Id))
                .Select(book => book with { PersonaIds = [persona.Id] })]
        };
    }

    private static bool Fits(HouseholdSharing sharing)
    {
        try
        {
            Checked(sharing);
            return true;
        }
        catch (ContractException) { return false; }
    }

    private static HouseholdSharing Checked(HouseholdSharing sharing)
    {
        sharing.Validate();
        ContractRules.Require(Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(sharing, Json)) <= MaximumBytes,
            "What you share with the household is too large.", ErrorCode.PayloadTooLarge);
        return sharing;
    }

    public void Validate()
    {
        ContractRules.Require(SchemaVersion == SchemaVersion1, "This was shared by a newer Martlet.", ErrorCode.UnsupportedVersion);
        ContractRules.Require(AccountId != Guid.Empty, "Household sharing needs an account.");
        ContractRules.Require(Characters is { Count: <= MaximumCharacters }, $"An account shares at most {MaximumCharacters} characters.");
        ContractRules.Require(Joined is { Count: <= MaximumJoined }, $"An account talks to at most {MaximumJoined} shared characters.");
        foreach (var item in Characters!)
        {
            ContractRules.Require(item is { Persona: not null, Lorebooks: not null }, "A shared character is incomplete.");
            var character = item!;
            ContractRules.Defined(character.Mode);
            var persona = character.Persona!;
            new CharacterProfile { Id = character.Id, Name = character.Name, PersonaId = persona.Id, ModelId = character.ModelId, VoiceId = character.VoiceId }
                .Validate();
            new PersonaProfile
            {
                Id = persona.Id, ConfigurationRevision = persona.Id, Name = persona.Name, Text = persona.Text, Breaks = persona.Breaks
            }.Validate();
            var books = character.Lorebooks!;
            ContractRules.Require(books.Count <= LorebookLibrary.MaximumBooks, "A shared character has too many lorebooks.");
            foreach (var book in books)
            {
                ContractRules.Require(book is not null, "A shared lorebook is missing.");
                book!.Validate();
            }
            ContractRules.Require(books.Select(b => b.Id).Distinct().Count() == books.Count, "A shared lorebook is listed twice.");
        }
        ContractRules.Require(Characters.Select(c => c.Id).Distinct().Count() == Characters.Count, "A shared character is listed twice.");
        ContractRules.Require(Joined!.All(j => j is not null && j.AccountId != Guid.Empty && j.CharacterId != Guid.Empty) &&
            Joined.Select(j => j.CharacterId).Distinct().Count() == Joined.Count, "A joined character is invalid or listed twice.");
    }

    /// <summary>The entry's value: snake-case JSON within <see cref="MaximumBytes"/>.</summary>
    public string Write() => JsonSerializer.Serialize(Checked(this), Json);

    /// <summary>Reads an entry's value; null when it can't be used (malformed, too large or written by a newer Martlet).</summary>
    public static HouseholdSharing? Read(string? json)
    {
        if (json is null || Encoding.UTF8.GetByteCount(json) > MaximumBytes) return null;
        try
        {
            var sharing = JsonSerializer.Deserialize<HouseholdSharing>(json, Json);
            sharing?.Validate();
            return sharing;
        }
        catch (Exception error) when (error is JsonException or NotSupportedException or InvalidOperationException or ContractException or
            ArgumentException or FormatException)
        {
            return null;
        }
    }

    /// <summary>Every account's usable entry among <paramref name="entries"/> (a household document's keys and values), by
    /// account. An entry whose value names another account than its key is ignored.</summary>
    public static IReadOnlyDictionary<Guid, HouseholdSharing> All(IEnumerable<(string Key, string Value)> entries)
    {
        var all = new Dictionary<Guid, HouseholdSharing>();
        foreach (var (key, value) in entries)
            if (AccountOf(key) is { } account && Read(value) is { } sharing && sharing.AccountId == account)
                all[account] = sharing;
        return all;
    }
}

