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
/// talks to together (<see cref="Joined"/>) and whether new facts about this person go to the household memory space
/// (<see cref="NewFactsAboutMe"/>). Other accounts' devices read it to show Household characters, to use a copy and to keep a
/// character shared together the same as its owner's. Snake-case JSON, schema 1, at most <see cref="MaximumCharacters"/>
/// characters, <see cref="MaximumJoined"/> joined characters and <see cref="MaximumBytes"/> bytes.
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

/// <summary>What a person's own device does with another account's shared character, in that person's own account settings
/// (only the receiving device can write them): <see cref="UseCopy"/> makes a copy with new IDs, <see cref="Join"/> adds or
/// updates the mirror of a character shared together (the same IDs, so it uses that character's memory space) and
/// <see cref="Leave"/> takes a mirror away.</summary>
public static class SharedCharacters
{
    /// <summary>Adds a copy of <paramref name="shared"/>: a new personality, character profile and lorebooks with new IDs and
    /// names made unique, so it is this account's own character with its own memories. Not yet switched to. Throws
    /// <see cref="ContractException"/> when the account has no room for it.</summary>
    public static (CompanionSettings Companion, LorebookLibrary Lorebooks, CharacterProfile Added) UseCopy(
        CompanionSettings companion, LorebookLibrary lorebooks, SharedCharacter shared)
    {
        ArgumentNullException.ThrowIfNull(companion);
        ArgumentNullException.ThrowIfNull(lorebooks);
        ArgumentNullException.ThrowIfNull(shared);
        ContractRules.Require(companion.Personas.Count < CompanionSettings.MaximumPersonas,
            $"There is no room for another personality: an account keeps at most {CompanionSettings.MaximumPersonas}. Remove one first.");
        ContractRules.Require(companion.CharacterList.Count < CompanionSettings.MaximumCharacters,
            $"There is no room for another character: an account keeps at most {CompanionSettings.MaximumCharacters}. Remove one first.");
        var persona = new PersonaProfile
        {
            Id = Guid.NewGuid(), ConfigurationRevision = Guid.NewGuid(),
            Name = Unique(shared.Persona.Name, companion.Personas.Select(p => p.Name), PersonaProfile.MaximumNameCharacters),
            Text = shared.Persona.Text, Breaks = SpeechBreaks.Normalize(shared.Persona.Breaks)
        };
        var profile = new CharacterProfile
        {
            Id = Guid.NewGuid(), PersonaId = persona.Id, ModelId = shared.ModelId, VoiceId = shared.VoiceId,
            Name = Unique(shared.Name, companion.CharacterList.Select(c => c.Name), CharacterProfile.MaximumNameCharacters)
        };
        var next = companion with { Personas = [.. companion.Personas, persona], Characters = [.. companion.CharacterList, profile] };
        next.Validate();
        var books = lorebooks;
        foreach (var book in shared.Lorebooks)
            books = books with
            {
                Books = [.. books.Books, book with
                {
                    Id = Guid.NewGuid(), Name = books.UniqueName(book.Name), PersonaIds = [persona.Id], Activation = LorebookActivation.SelectedPersonas
                }]
            };
        books.Validate();
        return (next, books, profile);
    }

    /// <summary>Adds the mirror of <paramref name="shared"/> (a character shared together), or makes the mirror already here the
    /// same as the owner's: the same profile, personality and lorebook IDs, with names made unique among this account's own.
    /// Returns the same instances when nothing changes. Throws <see cref="ContractException"/> when there is no room.</summary>
    public static (CompanionSettings Companion, LorebookLibrary Lorebooks) Join(CompanionSettings companion, LorebookLibrary lorebooks, SharedCharacter shared)
    {
        ArgumentNullException.ThrowIfNull(companion);
        ArgumentNullException.ThrowIfNull(lorebooks);
        ArgumentNullException.ThrowIfNull(shared);
        var existingPersona = companion.Personas.FirstOrDefault(p => p.Id == shared.Persona.Id);
        var personaName = Unique(shared.Persona.Name, companion.Personas.Where(p => p.Id != shared.Persona.Id).Select(p => p.Name),
            PersonaProfile.MaximumNameCharacters);
        var breaks = SpeechBreaks.Normalize(shared.Persona.Breaks);
        var persona = existingPersona is not null && existingPersona.Name == personaName && existingPersona.Text == shared.Persona.Text &&
            existingPersona.Breaks == breaks
            ? existingPersona
            : new PersonaProfile { Id = shared.Persona.Id, ConfigurationRevision = Guid.NewGuid(), Name = personaName, Text = shared.Persona.Text, Breaks = breaks };
        var existingProfile = companion.CharacterList.FirstOrDefault(c => c.Id == shared.Id);
        var profile = new CharacterProfile
        {
            Id = shared.Id, PersonaId = persona.Id, ModelId = shared.ModelId, VoiceId = shared.VoiceId,
            Name = Unique(shared.Name, companion.CharacterList.Where(c => c.Id != shared.Id).Select(c => c.Name), CharacterProfile.MaximumNameCharacters)
        };
        var next = companion;
        if (!ReferenceEquals(persona, existingPersona) || existingProfile != profile)
        {
            if (existingPersona is null)
                ContractRules.Require(companion.Personas.Count < CompanionSettings.MaximumPersonas,
                    $"There is no room for another personality: an account keeps at most {CompanionSettings.MaximumPersonas}. Remove one first.");
            if (existingProfile is null)
                ContractRules.Require(companion.CharacterList.Count < CompanionSettings.MaximumCharacters,
                    $"There is no room for another character: an account keeps at most {CompanionSettings.MaximumCharacters}. Remove one first.");
            PersonaProfile[] personas = existingPersona is null ? [.. companion.Personas, persona]
                : [.. companion.Personas.Select(p => p.Id == persona.Id ? persona : p)];
            CharacterProfile[] characters = existingProfile is null ? [.. companion.CharacterList, profile]
                : [.. companion.CharacterList.Select(c => c.Id == profile.Id ? profile : c)];
            next = companion with { Personas = personas, Characters = characters };
            next.Validate();
        }

        // The mirror's lorebooks are exactly the owner's lorebooks for this personality.
        var mine = lorebooks.Books.Where(b => IsOnlyFor(b, persona.Id)).ToArray();
        var taken = lorebooks with { Books = [.. lorebooks.Books.Where(b => !IsOnlyFor(b, persona.Id))] };
        var others = taken.Books;
        var wanted = new List<Lorebook>();
        foreach (var book in shared.Lorebooks)
        {
            var named = book with { Name = taken.UniqueName(book.Name), PersonaIds = [persona.Id], Activation = LorebookActivation.SelectedPersonas };
            wanted.Add(named);
            taken = taken with { Books = [.. taken.Books, named] };
        }
        if (mine.Length == wanted.Count && mine.OrderBy(b => b.Id).Zip(wanted.OrderBy(b => b.Id)).All(pair => Same(pair.First, pair.Second)))
            return (next, lorebooks);
        var books = lorebooks with { Books = [.. others, .. wanted] };
        books.Validate();
        return (next, books);
    }

    /// <summary>Takes away the mirror <paramref name="character"/>: its profile, its personality (unless it is the only one or
    /// another profile uses it) and its lorebooks. When it was in use, the account's first personality is in use instead.</summary>
    public static (CompanionSettings Companion, LorebookLibrary Lorebooks) Leave(CompanionSettings companion, LorebookLibrary lorebooks, Guid character)
    {
        ArgumentNullException.ThrowIfNull(companion);
        ArgumentNullException.ThrowIfNull(lorebooks);
        if (companion.CharacterList.FirstOrDefault(c => c.Id == character) is not { } profile) return (companion, lorebooks);
        var next = companion.RemoveCharacter(character);
        var personaGoes = next.Personas.Count > 1 && next.CharacterList.All(c => c.PersonaId != profile.PersonaId);
        if (personaGoes) next = next.Remove(profile.PersonaId);
        var books = personaGoes && lorebooks.Books.Any(b => IsOnlyFor(b, profile.PersonaId))
            ? lorebooks with { Books = [.. lorebooks.Books.Where(b => !IsOnlyFor(b, profile.PersonaId))] }
            : lorebooks;
        return (next, books);
    }

    private static bool IsOnlyFor(Lorebook book, Guid persona) =>
        book.Activation == LorebookActivation.SelectedPersonas && book.PersonaIds.Count == 1 && book.PersonaIds[0] == persona;

    private static bool Same(Lorebook a, Lorebook b) =>
        a.Id == b.Id && a.Name == b.Name && a.Description == b.Description && a.Activation == b.Activation &&
        a.PersonaIds.SequenceEqual(b.PersonaIds) && a.Entries.Count == b.Entries.Count &&
        a.Entries.Zip(b.Entries).All(pair => SameEntry(pair.First, pair.Second));

    private static bool SameEntry(LorebookEntry a, LorebookEntry b) =>
        a with { Keys = Array.Empty<string>(), SecondaryKeys = Array.Empty<string>() } ==
        b with { Keys = Array.Empty<string>(), SecondaryKeys = Array.Empty<string>() } &&
        a.Keys.SequenceEqual(b.Keys) && a.SecondaryKeys.SequenceEqual(b.SecondaryKeys);

    /// <summary><paramref name="name"/>, or with " 2", " 3"... when one of <paramref name="taken"/> has it already.</summary>
    internal static string Unique(string name, IEnumerable<string> taken, int maximum)
    {
        var used = new HashSet<string>(taken, StringComparer.OrdinalIgnoreCase);
        if (!used.Contains(name)) return name;
        for (var i = 2; ; i++)
        {
            var suffix = $" {i}";
            var candidate = name[..Math.Min(name.Length, maximum - suffix.Length)].TrimEnd() + suffix;
            if (!used.Contains(candidate)) return candidate;
        }
    }
}
