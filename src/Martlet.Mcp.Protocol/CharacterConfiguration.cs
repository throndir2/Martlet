using System.IO;
using System.Text;
using Martlet.Core.Characters;
using Martlet.Core.Contracts;
using Martlet.Core.Lorebooks;
using Martlet.Core.Settings;
using Martlet.Core.Voices;

namespace Martlet.Mcp;

/// <summary>Martlet's characters for MCP clients (characters_list, character_create, character_update, character_use and
/// character_delete): a character is a personality (a persona: name and instructions) and, when it has one, its character
/// profile (Companion › Profiles), which adds a look (one of the shared character models, or the built-in one) and a voice
/// (one of the speaking voices). Changes go through the same settings code the desktop's Personality and Profiles pages use,
/// and a running Martlet follows them by itself. Unlike the diagnostic tools, these return names and, when asked, the
/// personality text, because they edit them.</summary>
internal static class CharacterConfiguration
{
    /// <summary>The fewest characters a card's personality may be shortened to, as the desktop's card import allows.</summary>
    private const int MinimumCardCharacters = 500;

    internal static async Task<object> ListAsync(string directory, bool includeText, CancellationToken token)
    {
        var loaded = await new SettingsStore(directory).LoadAsync(token);
        if (loaded.Error is not null) return new { state = "unreadable", problem = loaded.Error.Summary };
        var companion = CompanionSettings.Begin(loaded.Settings).Companion!;
        var looks = Looks(directory);
        var voices = Voices(directory);
        var firstRun = loaded.State == SettingsLoadState.FirstRun;
        return new
        {
            state = firstRun ? "first-run" : "loaded",
            note = firstRun ? "Nothing is saved yet: this is Martlet's default personality. The first change saves it." : null,
            dataDirectory = directory,
            active = new
            {
                personality = companion.ActivePersona.Name,
                profile = companion.CharacterList.FirstOrDefault(c => c.Id == companion.ActiveCharacterId)?.Name
            },
            characters = companion.Personas.Select(persona => Describe(companion, persona, looks, voices, includeText, firstRun)).ToArray(),
            looks = new[] { new { id = CharacterProfile.BuiltInModel, name = "Martlet's built-in character", renderer = "live2d", ready = true } }
                .Concat(looks.Live.Select(m => new
                {
                    id = m.Id, name = m.Name ?? SharedKey(m.Id), renderer = m.Renderer ?? "", ready = Martlet.Avatar.Hosting.SharedCharacterModels.IsComplete(directory, m)
                })).ToArray(),
            voices = voices.Live.Select(v => new { id = v.Id, name = v.Name ?? v.Id, chosen = voices.ChosenVoice?.Id == v.Id }).ToArray(),
            limits = new
            {
                personalities = CompanionSettings.MaximumPersonas, profiles = CompanionSettings.MaximumCharacters,
                nameCharacters = PersonaProfile.MaximumNameCharacters, personalityCharacters = PersonaProfile.MaximumTextCharacters,
                allPersonalitiesCharacters = CompanionSettings.MaximumAggregateTextCharacters,
                personalityCharactersLeft = CompanionSettings.MaximumAggregateTextCharacters - companion.Personas.Sum(p => p.Text.Length)
            }
        };
    }

    internal static async Task<object> CreateAsync(string directory, string? name, string? personality, string? cardPath, string? look,
        string? voice, bool? profile, bool? use, CancellationToken token)
    {
        if (name is null && cardPath is null) throw new ArgumentException("Give the character a name, or a cardPath to import a character card.");
        if (personality is not null) PersonaProfile.ValidateText(personality);
        CharacterCard? card = null;
        if (cardPath is not null)
        {
            if (!Path.IsPathFullyQualified(cardPath)) throw new ArgumentException("cardPath must be an absolute path.");
            var read = await new CompanionSettingsService(new SettingsStore(directory)).ImportCardAsync(cardPath, token);
            if (!read.Succeeded) throw new InvalidOperationException(read.Summary);
            card = read.Card!;
        }
        var lookId = look is null ? null : ResolveLook(directory, look);
        var voiceId = voice is null ? null : ResolveVoice(directory, voice);
        var makeProfile = profile ?? true;
        if (!makeProfile && (lookId is not null || voiceId is not null))
            throw new ArgumentException("A look or a voice needs a character profile; leave profile on.");
        var switchTo = use ?? true;
        CharacterCardPersona? fromCard = null;
        PersonaProfile? created = null;
        CharacterProfile? addedProfile = null;
        var saved = await ChangeAsync(directory, companion =>
        {
            ContractRules.Require(companion.Personas.Count < CompanionSettings.MaximumPersonas,
                $"Martlet keeps at most {CompanionSettings.MaximumPersonas} personalities. Delete one first (character_delete).");
            string personaName, text;
            if (card is not null)
            {
                var (characters, bytes) = Room(companion);
                ContractRules.Require(characters >= MinimumCardCharacters,
                    "There isn't enough room for this card. Shorten or delete another personality, then try again.");
                fromCard = card.ToPersona(Path.GetFileNameWithoutExtension(cardPath), characters, bytes);
                personaName = name?.Trim() ?? UniqueName(companion.Personas.Select(p => p.Name), fromCard.Name, PersonaProfile.MaximumNameCharacters);
                text = personality ?? fromCard.Text;
            }
            else
            {
                personaName = name!.Trim();
                text = personality ?? "";
            }
            ContractRules.Require(!companion.Personas.Any(p => string.Equals(p.Name, personaName, StringComparison.OrdinalIgnoreCase)),
                $"A character named '{personaName}' already exists. Use character_update to change it, or choose another name.");
            var previous = companion.ActivePersonaId;
            var next = companion.Add(personaName);
            next = next.Update(next.ActivePersonaId, personaName, text);
            created = next.ActivePersona;
            if (!switchTo) next = next.Select(previous);
            if (makeProfile)
            {
                var profileName = UniqueName(next.CharacterList.Select(c => c.Name), personaName, CharacterProfile.MaximumNameCharacters);
                next = next.AddCharacter(profileName, created.Id, lookId, voiceId, out var added);
                addedProfile = added;
                if (switchTo) next = next.SelectCharacter(added.Id);
            }
            return next;
        }, token);
        object? lore = null;
        if (card?.Lorebook is { } import && created is { } persona)
            lore = await KeepCardLoreAsync(directory, import, persona.Id, token);
        return new
        {
            created = Describe(saved.Companion!, saved.Companion!.Personas.Single(p => p.Id == created!.Id), Looks(directory), Voices(directory),
                includeText: false, firstRun: false),
            active = switchTo,
            card = card is null ? null : new
            {
                format = card.FormatName, name = card.DisplayName, shortened = fromCard!.Shortened, leftOut = fromCard.LeftOut,
                personalityFromCard = personality is null, lorebook = lore
            },
            followUp = FollowUp(addedProfile, switchTo)
        };
    }

    internal static async Task<object> UpdateAsync(string directory, string reference, string? name, string? personality, string? look,
        string? voice, CancellationToken token)
    {
        if (name is null && personality is null && look is null && voice is null)
            throw new ArgumentException("Give at least one of name, personality, look or voice to change.");
        if (personality is not null) PersonaProfile.ValidateText(personality);
        var lookId = look is null ? null : IsKeep(look) ? "" : ResolveLook(directory, look);
        var voiceId = voice is null ? null : IsKeep(voice) ? "" : ResolveVoice(directory, voice);
        Guid personaId = default;
        var changedProfile = false;
        var saved = await ChangeAsync(directory, companion =>
        {
            var (persona, profile) = Find(companion, reference);
            personaId = persona.Id;
            var newName = name?.Trim();
            var next = companion.Update(persona.Id, newName ?? persona.Name, personality ?? persona.Text);
            if (profile is null && (lookId is { Length: > 0 } || voiceId is { Length: > 0 }))
            {
                var profileName = UniqueName(next.CharacterList.Select(c => c.Name), newName ?? persona.Name, CharacterProfile.MaximumNameCharacters);
                next = next.AddCharacter(profileName, persona.Id, NullIfEmpty(lookId), NullIfEmpty(voiceId), out _);
                changedProfile = true;
            }
            else if (profile is not null)
            {
                // The profile follows the personality's new name when it had the personality's name.
                var profileName = newName is not null && string.Equals(profile.Name, persona.Name, StringComparison.OrdinalIgnoreCase)
                    ? newName : profile.Name;
                var updated = next.UpdateCharacter(profile.Id, profileName, profile.PersonaId,
                    lookId is null ? profile.ModelId : NullIfEmpty(lookId), voiceId is null ? profile.VoiceId : NullIfEmpty(voiceId));
                changedProfile = !ReferenceEquals(updated, next);
                next = updated;
            }
            return next;
        }, token);
        var companion = saved.Companion!;
        var profileNow = ProfileOf(companion, companion.Personas.Single(p => p.Id == personaId));
        return new
        {
            updated = Describe(companion, companion.Personas.Single(p => p.Id == personaId), Looks(directory), Voices(directory), includeText: false,
                firstRun: false),
            followUp = changedProfile && profileNow is not null && companion.ActiveCharacterId == profileNow.Id
                ? FollowUp(profileNow, true) : null
        };
    }

    internal static async Task<object> UseAsync(string directory, string reference, CancellationToken token)
    {
        CharacterProfile? used = null;
        var saved = await ChangeAsync(directory, companion =>
        {
            var (persona, profile) = Find(companion, reference);
            used = profile;
            return profile is not null ? companion.SelectCharacter(profile.Id) : companion.Select(persona.Id);
        }, token);
        return new
        {
            active = new
            {
                personality = saved.Companion!.ActivePersona.Name,
                profile = used?.Name
            },
            followUp = FollowUp(used, true)
        };
    }

    internal static async Task<object> DeleteAsync(string directory, string reference, bool profileOnly, CancellationToken token)
    {
        string? removedPersona = null, removedProfile = null;
        var removedProfiles = new List<string>();
        var saved = await ChangeAsync(directory, companion =>
        {
            var (persona, profile) = Find(companion, reference);
            if (profileOnly)
            {
                ContractRules.Require(profile is not null, $"'{persona.Name}' has no character profile; its personality stays.");
                removedProfile = profile!.Name;
                return companion.RemoveCharacter(profile.Id);
            }
            ContractRules.Require(companion.Personas.Count > 1, "Martlet keeps at least one personality. Make another character first.");
            removedPersona = persona.Name;
            removedProfiles.AddRange(companion.CharacterList.Where(c => c.PersonaId == persona.Id).Select(c => c.Name));
            return companion.Remove(persona.Id);
        }, token);
        return new
        {
            removed = new { personality = removedPersona, profiles = removedProfile is null ? removedProfiles.ToArray() : [removedProfile] },
            active = new
            {
                personality = saved.Companion!.ActivePersona.Name,
                profile = saved.Companion.CharacterList.FirstOrDefault(c => c.Id == saved.Companion.ActiveCharacterId)?.Name
            }
        };
    }

    /// <summary>Applies <paramref name="change"/> to the newest saved personalities and saves them as the desktop does, trying
    /// again when another save lands in between. Returns the saved settings.</summary>
    private static async Task<AppSettings> ChangeAsync(string directory, Func<CompanionSettings, CompanionSettings> change,
        CancellationToken token)
    {
        var store = new SettingsStore(directory);
        for (var attempt = 1; ; attempt++)
        {
            var loaded = await store.LoadAsync(token);
            if (loaded.Error is not null) throw new InvalidOperationException(loaded.Error.Summary);
            var begun = CompanionSettings.Begin(loaded.Settings);
            var next = change(begun.Companion!);
            if (ReferenceEquals(next, begun.Companion)) return begun;
            var merged = begun with { Companion = next };
            var save = await store.SaveAsync(merged, loaded.Revision, token);
            if (save.Saved) return merged;
            if (save.Error?.Code != ErrorCode.SettingsConflict || attempt == 3)
                throw new InvalidOperationException(save.Error?.Summary ?? "Martlet couldn't save its settings.");
        }
    }

    /// <summary>A personality or character profile by ID, profile key (8 hex digits) or name, ignoring case. A profile's name
    /// wins over a personality's.</summary>
    private static (PersonaProfile Persona, CharacterProfile? Profile) Find(CompanionSettings companion, string reference)
    {
        var wanted = reference.Trim();
        bool Same(string text) => string.Equals(text, wanted, StringComparison.OrdinalIgnoreCase);
        if (companion.CharacterList.FirstOrDefault(c => Same(c.Id.ToString()) || Same(c.Key)) is { } byId)
            return (companion.Personas.Single(p => p.Id == byId.PersonaId), byId);
        if (companion.Personas.FirstOrDefault(p => Same(p.Id.ToString())) is { } personaById)
            return (personaById, ProfileOf(companion, personaById));
        if (companion.CharacterList.FirstOrDefault(c => Same(c.Name)) is { } byName)
            return (companion.Personas.Single(p => p.Id == byName.PersonaId), byName);
        if (companion.Personas.FirstOrDefault(p => Same(p.Name)) is { } personaByName)
            return (personaByName, ProfileOf(companion, personaByName));
        throw new ArgumentException($"No character is called '{wanted}'. Characters: " +
            string.Join(", ", companion.Personas.Select(p => p.Name)) + ".");
    }

    /// <summary>The profile of a personality: the one with its name, else its only one.</summary>
    private static CharacterProfile? ProfileOf(CompanionSettings companion, PersonaProfile persona)
    {
        var own = companion.CharacterList.Where(c => c.PersonaId == persona.Id).ToArray();
        return own.FirstOrDefault(c => string.Equals(c.Name, persona.Name, StringComparison.OrdinalIgnoreCase)) ?? (own.Length == 1 ? own[0] : null);
    }

    private static object Describe(CompanionSettings companion, PersonaProfile persona, CharacterModelLibrary looks, SpeakingVoiceLibrary voices,
        bool includeText, bool firstRun)
    {
        var profile = ProfileOf(companion, persona);
        return new
        {
            name = persona.Name,
            id = firstRun ? null : persona.Id.ToString(),
            active = persona.Id == companion.ActivePersonaId,
            personalityCharacters = persona.Text.Length,
            personality = includeText ? persona.Text : null,
            profile = profile is null ? null : new
            {
                name = profile.Name, id = profile.Id.ToString(), key = profile.Key, active = profile.Id == companion.ActiveCharacterId,
                look = profile.ModelId is null ? "keep (whatever Martlet shows)" : LookName(profile.ModelId, looks),
                lookId = profile.ModelId,
                voice = profile.VoiceId is null ? "keep (whatever Martlet speaks with)" : voices.Find(profile.VoiceId) is { Removed: false } v
                    ? v.Name ?? v.Id : "missing",
                voiceId = profile.VoiceId
            },
            otherProfiles = companion.CharacterList.Where(c => c.PersonaId == persona.Id && c.Id != profile?.Id)
                .Select(c => new { name = c.Name, id = c.Id.ToString(), key = c.Key }).ToArray()
        };
    }

    /// <summary>What the running app still does for a profile's look and voice: they switch when it is used in Martlet.</summary>
    private static object? FollowUp(CharacterProfile? profile, bool active)
    {
        if (profile is null || !active || (profile.ModelId is null && profile.VoiceId is null)) return null;
        return new
        {
            why = "Martlet now uses this personality. Its look and voice change on this PC when the profile is used in Martlet.",
            inMartlet = "Companion › Profiles › Use, on Home, or on Martlet's icon by the clock.",
            withTools = new object[]
            {
                new { name = "ui_connect" },
                new { name = "ui_click", arguments = new { id = "NavCompanion" } },
                new { name = "ui_click", arguments = new { id = "CompanionTab-Profiles" } },
                new { name = "ui_click", arguments = new { id = "CharacterProfileUse-" + profile.Key }, needs = "--allow-ui-effects" }
            }
        };
    }

    private static object? KeepCardLoreResult(Lorebook? book, string? problem) =>
        book is null && problem is null ? null : new { entries = book?.Entries.Count ?? 0, name = book?.Name, problem };

    /// <summary>Keeps a card's keyword lorebook entries in a lorebook for the new personality, as the desktop's card import does
    /// (always-on entries are already in the personality).</summary>
    private static async Task<object?> KeepCardLoreAsync(string directory, LorebookImport import, Guid persona, CancellationToken token)
    {
        var entries = import.Book.Entries.Where(entry => !entry.Constant).ToArray();
        if (entries.Length == 0) return null;
        var folder = Martlet.Core.Sync.AccountWorkingCopy.Load(directory) is { } signedIn
            ? Martlet.Core.Sync.AccountWorkingCopy.Folder(directory, signedIn.Account) : directory;
        Lorebook? kept = null;
        var result = await new LorebookStore(folder).UpdateAsync(library =>
        {
            ContractRules.Require(library.Books.Count < LorebookLibrary.MaximumBooks,
                $"At most {LorebookLibrary.MaximumBooks} lorebooks are supported. Delete one in Lorebooks.");
            kept = import.Book with
            {
                Id = Guid.NewGuid(), Name = library.UniqueName(import.Book.Name), Activation = LorebookActivation.SelectedPersonas,
                PersonaIds = [persona], Entries = entries
            };
            var next = library with { Books = library.Books.Append(kept).ToArray() };
            next.Validate();
            return next;
        }, token);
        return KeepCardLoreResult(result.Saved ? kept : null, result.Saved ? null : result.Error ?? "The card's lorebook wasn't saved.");
    }

    /// <summary>Room for a card's personality: the per-personality limit, or less when the others leave less of the combined
    /// limit.</summary>
    private static (int Characters, int Bytes) Room(CompanionSettings companion) =>
        (Math.Min(PersonaProfile.MaximumTextCharacters, CompanionSettings.MaximumAggregateTextCharacters - companion.Personas.Sum(p => p.Text.Length)),
            Math.Min(PersonaProfile.MaximumTextUtf8Bytes,
                CompanionSettings.MaximumAggregateTextUtf8Bytes - companion.Personas.Sum(p => Encoding.UTF8.GetByteCount(p.Text))));

    private static string UniqueName(IEnumerable<string> taken, string basis, int maximum)
    {
        var names = new HashSet<string>(taken, StringComparer.OrdinalIgnoreCase);
        for (var index = 1; index <= 64; index++)
        {
            var suffix = index == 1 ? "" : $" {index}";
            var candidate = basis[..Math.Min(basis.Length, maximum - suffix.Length)].TrimEnd() + suffix;
            if (!names.Contains(candidate)) return candidate;
        }
        throw new InvalidOperationException("No unique name is left; choose another name.");
    }

    private static bool IsKeep(string value) => value.Trim().Equals("keep", StringComparison.OrdinalIgnoreCase);
    private static string? NullIfEmpty(string? value) => string.IsNullOrEmpty(value) ? null : value;
    private static string SharedKey(string id) => id.Length >= 16 ? Martlet.Avatar.Hosting.SharedCharacterModels.Key(id) : id;

    private static CharacterModelLibrary Looks(string directory) =>
        Martlet.Avatar.Hosting.SharedCharacterModels.Load(directory) ?? CharacterModelLibrary.Empty;

    private static SpeakingVoiceLibrary Voices(string directory)
    {
        try { return SpeakingVoiceLibrary.Parse(File.ReadAllBytes(Path.Combine(directory, "speaking-voices.json"))); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ContractException)
        {
            return SpeakingVoiceLibrary.Empty.Seed(Martlet.F5.F5SharedVoices.Starters);
        }
    }

    private static string LookName(string modelId, CharacterModelLibrary looks) =>
        modelId == CharacterProfile.BuiltInModel ? "Martlet's built-in character"
            : looks.Live.FirstOrDefault(m => m.Id == modelId) is { } model ? model.Name ?? SharedKey(model.Id) : "missing";

    /// <summary>A look by "builtin", ID, key (16 hex digits) or name.</summary>
    private static string ResolveLook(string directory, string look)
    {
        var wanted = look.Trim();
        if (wanted.Equals(CharacterProfile.BuiltInModel, StringComparison.OrdinalIgnoreCase) ||
            wanted.Equals("built-in", StringComparison.OrdinalIgnoreCase))
            return CharacterProfile.BuiltInModel;
        var library = Looks(directory);
        return library.Live.FirstOrDefault(m => string.Equals(m.Id, wanted, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(SharedKey(m.Id), wanted, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(m.Name, wanted, StringComparison.OrdinalIgnoreCase))?.Id
            ?? throw new ArgumentException($"No look is called '{wanted}'. Looks: builtin" +
                string.Concat(library.Live.Select(m => ", " + (m.Name ?? SharedKey(m.Id)))) +
                ". Add a new look (a Live2D or VRM model) in Martlet: Companion › Character › Add a character.");
    }

    /// <summary>A speaking voice by ID or name.</summary>
    private static string ResolveVoice(string directory, string voice)
    {
        var wanted = voice.Trim();
        var library = Voices(directory);
        return library.Live.FirstOrDefault(v => string.Equals(v.Id, wanted, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(v.Name, wanted, StringComparison.OrdinalIgnoreCase))?.Id
            ?? throw new ArgumentException($"No voice is called '{wanted}'. Voices: " +
                (library.Live.Count == 0 ? "none" : string.Join(", ", library.Live.Select(v => v.Name ?? v.Id))) +
                ". Add a new voice from a recording in Martlet: Companion › Voice › Voices.");
    }
}
