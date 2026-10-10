using Martlet.Core.Contracts;
using Martlet.Core.Lorebooks;
using Martlet.Core.Settings;

namespace Martlet.Core.Sharing;

/// <summary>What a person's own device does with another account's shared character, in that person's own account settings
/// (only the receiving device writes them). The personality and profile change in <see cref="CompanionSettings"/> and the
/// lorebooks in the <see cref="LorebookLibrary"/>, each saved by its own store:
/// <list type="bullet">
/// <item><see cref="UseCopy"/> and <see cref="CopyLorebooks"/>: a copy with new IDs, this account's own character with its own
/// memories.</item>
/// <item><see cref="Join"/> and <see cref="MirrorLorebooks"/>: the mirror of a character shared together, with the same IDs as the
/// owner's, so it uses that character's memory space. Run again after each sync, they keep it the same as the owner's.</item>
/// <item><see cref="Leave"/> and <see cref="DropLorebooks"/>: the mirror goes.</item>
/// </list></summary>
public static class SharedCharacters
{
    /// <summary>Adds a copy of <paramref name="shared"/>: a new personality and character profile with new IDs and names made
    /// unique. Not yet switched to. Throws <see cref="ContractException"/> when the account has no room for it.</summary>
    public static (CompanionSettings Companion, CharacterProfile Added) UseCopy(CompanionSettings companion, SharedCharacter shared)
    {
        ArgumentNullException.ThrowIfNull(companion);
        ArgumentNullException.ThrowIfNull(shared);
        RequireRoom(companion, persona: true, profile: true);
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
        return (next, profile);
    }

    /// <summary>Adds copies of <paramref name="shared"/>'s lorebooks with new IDs and names made unique, on only for the copy's
    /// personality <paramref name="persona"/>.</summary>
    public static LorebookLibrary CopyLorebooks(LorebookLibrary lorebooks, SharedCharacter shared, Guid persona)
    {
        ArgumentNullException.ThrowIfNull(lorebooks);
        ArgumentNullException.ThrowIfNull(shared);
        var books = lorebooks;
        foreach (var book in shared.Lorebooks)
            books = books with
            {
                Books = [.. books.Books, book with
                {
                    Id = Guid.NewGuid(), Name = books.UniqueName(book.Name), PersonaIds = [persona], Activation = LorebookActivation.SelectedPersonas
                }]
            };
        books.Validate();
        return books;
    }

    /// <summary>Adds the mirror of <paramref name="shared"/> (a character shared together), or makes the mirror already here the
    /// same as the owner's: the same profile and personality IDs, names made unique among this account's own. Returns the same
    /// instance when nothing changes. Throws <see cref="ContractException"/> when there is no room.</summary>
    public static CompanionSettings Join(CompanionSettings companion, SharedCharacter shared)
    {
        ArgumentNullException.ThrowIfNull(companion);
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
        if (ReferenceEquals(persona, existingPersona) && existingProfile == profile) return companion;
        RequireRoom(companion, existingPersona is null, existingProfile is null);
        PersonaProfile[] personas = existingPersona is null ? [.. companion.Personas, persona]
            : [.. companion.Personas.Select(p => p.Id == persona.Id ? persona : p)];
        CharacterProfile[] characters = existingProfile is null ? [.. companion.CharacterList, profile]
            : [.. companion.CharacterList.Select(c => c.Id == profile.Id ? profile : c)];
        var next = companion with { Personas = personas, Characters = characters };
        next.Validate();
        return next;
    }

    /// <summary>Makes the lorebooks on only for the mirror's personality exactly the owner's (the same IDs, names made unique
    /// among this account's own). Returns the same instance when nothing changes.</summary>
    public static LorebookLibrary MirrorLorebooks(LorebookLibrary lorebooks, SharedCharacter shared)
    {
        ArgumentNullException.ThrowIfNull(lorebooks);
        ArgumentNullException.ThrowIfNull(shared);
        var persona = shared.Persona.Id;
        var mine = lorebooks.Books.Where(b => IsOnlyFor(b, persona)).ToArray();
        var taken = lorebooks with { Books = [.. lorebooks.Books.Where(b => !IsOnlyFor(b, persona))] };
        var others = taken.Books;
        var wanted = new List<Lorebook>();
        foreach (var book in shared.Lorebooks)
        {
            var named = book with { Name = taken.UniqueName(book.Name), PersonaIds = [persona], Activation = LorebookActivation.SelectedPersonas };
            wanted.Add(named);
            taken = taken with { Books = [.. taken.Books, named] };
        }
        if (mine.Length == wanted.Count && mine.OrderBy(b => b.Id).Zip(wanted.OrderBy(b => b.Id)).All(pair => Same(pair.First, pair.Second)))
            return lorebooks;
        var books = lorebooks with { Books = [.. others, .. wanted] };
        books.Validate();
        return books;
    }

    /// <summary>Takes away the mirror <paramref name="character"/>: its profile and its personality (unless it is the only one or
    /// another profile uses it). When it was in use, the account's first personality is in use instead. <c>Persona</c> is the
    /// personality that went (its lorebooks go with <see cref="DropLorebooks"/>), or null.</summary>
    public static (CompanionSettings Companion, Guid? Persona) Leave(CompanionSettings companion, Guid character)
    {
        ArgumentNullException.ThrowIfNull(companion);
        if (companion.CharacterList.FirstOrDefault(c => c.Id == character) is not { } profile) return (companion, null);
        var next = companion.RemoveCharacter(character);
        if (next.Personas.Count <= 1 || next.CharacterList.Any(c => c.PersonaId == profile.PersonaId)) return (next, null);
        return (next.Remove(profile.PersonaId), profile.PersonaId);
    }

    /// <summary>The lorebooks without those on only for <paramref name="persona"/>; the same instance when there are none.</summary>
    public static LorebookLibrary DropLorebooks(LorebookLibrary lorebooks, Guid persona)
    {
        ArgumentNullException.ThrowIfNull(lorebooks);
        return lorebooks.Books.Any(b => IsOnlyFor(b, persona)) ? lorebooks with { Books = [.. lorebooks.Books.Where(b => !IsOnlyFor(b, persona))] } : lorebooks;
    }

    private static void RequireRoom(CompanionSettings companion, bool persona, bool profile)
    {
        if (persona)
            ContractRules.Require(companion.Personas.Count < CompanionSettings.MaximumPersonas,
                $"There is no room for another personality: an account keeps at most {CompanionSettings.MaximumPersonas}. Remove one first.");
        if (profile)
            ContractRules.Require(companion.CharacterList.Count < CompanionSettings.MaximumCharacters,
                $"There is no room for another character: an account keeps at most {CompanionSettings.MaximumCharacters}. Remove one first.");
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
