using Martlet.Core.Contracts;
using Martlet.Core.Lorebooks;
using Martlet.Core.Settings;
using Martlet.Core.Sharing;
using Martlet.Core.Sync;

namespace Martlet.Core.Tests;

/// <summary>Sharing characters with the household (docs/ACCOUNTS.md, "Sharing"): the household entry sharing.&lt;account&gt;,
/// using a copy, and the mirror of a character shared together.</summary>
public sealed class HouseholdSharingTests
{
    private static readonly Guid Sam = Guid.Parse("5a3f0c9e-8b7d-4e21-a6c3-b2f1d0e9a8b7");
    private static readonly Guid Alex = Guid.Parse("0b1c2d3e-4f50-6172-8394-a5b6c7d8e9f0");

    private static (CompanionSettings Companion, LorebookLibrary Lorebooks, CharacterProfile Aria) SamsCharacters()
    {
        var companion = CompanionSettings.Create().Add("Aria");
        var aria = companion.ActivePersonaId;
        companion = companion.Update(aria, "Aria", "Aria is cheerful and loves astronomy.", new SpeechBreaks { ShortEndingWords = 3 })
            .AddCharacter("Aria", aria, "model-aria", "voice-aria", out var profile);
        var lorebooks = LorebookLibrary.Create() with
        {
            Books =
            [
                new Lorebook
                {
                    Id = Guid.NewGuid(), Name = "Aria's stars", Activation = LorebookActivation.SelectedPersonas,
                    PersonaIds = [aria, companion.Personas[0].Id],
                    Entries = [new LorebookEntry { Uid = 0, Keys = ["Vega"], Content = "Vega is Aria's favourite star." }]
                },
                new Lorebook { Id = Guid.NewGuid(), Name = "Sam's world", Activation = LorebookActivation.AllPersonas },
                new Lorebook { Id = Guid.NewGuid(), Name = "Martlet only", Activation = LorebookActivation.SelectedPersonas, PersonaIds = [companion.Personas[0].Id] }
            ]
        };
        return (companion, lorebooks, profile);
    }

    [Fact]
    public void The_entry_key_names_its_account_and_only_lowercase_32_hex_is_a_key()
    {
        Assert.Equal("sharing.5a3f0c9e8b7d4e21a6c3b2f1d0e9a8b7", HouseholdSharing.Key(Sam));
        Assert.Equal(Sam, HouseholdSharing.AccountOf(HouseholdSharing.Key(Sam)));
        Assert.True(SharedSettings.IsKey(HouseholdSharing.Key(Sam)));
        foreach (var key in new[] { "sharing.5A3F0C9E8B7D4E21A6C3B2F1D0E9A8B7", "sharing.", "sharing.00000000000000000000000000000000",
                     "companion", "reminders.desk-1", "sharing.5a3f0c9e-8b7d-4e21-a6c3-b2f1d0e9a8b7", null })
            Assert.Null(HouseholdSharing.AccountOf(key));
    }

    [Fact]
    public void Sharing_takes_a_snapshot_of_the_personality_look_voice_and_its_own_lorebooks_and_round_trips()
    {
        var (companion, lorebooks, aria) = SamsCharacters();
        var sharing = HouseholdSharing.Empty(Sam).WithMode(aria.Id, CharacterShareMode.Copy, companion, lorebooks);
        var shared = Assert.Single(sharing.Characters);
        Assert.Equal((aria.Id, CharacterShareMode.Copy, "Aria", "model-aria", "voice-aria"), (shared.Id, shared.Mode, shared.Name, shared.ModelId, shared.VoiceId));
        Assert.Equal("Aria is cheerful and loves astronomy.", shared.Persona.Text);
        Assert.Equal(3, shared.Persona.Breaks!.ShortEndingWords);
        // Only the lorebook that is on for Aria goes with her, without Sam's other personality.
        var book = Assert.Single(shared.Lorebooks);
        Assert.Equal("Aria's stars", book.Name);
        Assert.Equal([aria.PersonaId], book.PersonaIds);

        var read = HouseholdSharing.Read(sharing.Write());
        Assert.NotNull(read);
        Assert.Equal(sharing.Write(), read!.Write());
        Assert.Contains("\"mode\":\"copy\"", sharing.Write());
        Assert.Equal(CharacterShareMode.Copy, read.ModeOf(aria.Id));

        var together = read.WithMode(aria.Id, CharacterShareMode.Together, companion, lorebooks);
        Assert.Equal(CharacterShareMode.Together, together.ModeOf(aria.Id));
        Assert.Equal(MemorySpaceId.Character(aria.Id), together.Characters[0].Space);
        Assert.Null(together.WithMode(aria.Id, null, companion, lorebooks).ModeOf(aria.Id));
        Assert.Throws<ContractException>(() => sharing.WithMode(Guid.NewGuid(), CharacterShareMode.Copy, companion, lorebooks));
    }

    [Fact]
    public void Refresh_follows_edits_and_drops_removed_characters()
    {
        var (companion, lorebooks, aria) = SamsCharacters();
        var sharing = HouseholdSharing.Empty(Sam).WithMode(aria.Id, CharacterShareMode.Together, companion, lorebooks);
        var edited = companion.Update(aria.PersonaId, "Aria", "Aria now loves comets.").UpdateCharacter(aria.Id, "Aria Star", aria.PersonaId, "model-2", null);
        var refreshed = sharing.Refresh(edited, lorebooks);
        Assert.Equal(("Aria Star", "Aria now loves comets.", "model-2", (string?)null),
            (refreshed.Characters[0].Name, refreshed.Characters[0].Persona.Text, refreshed.Characters[0].ModelId, refreshed.Characters[0].VoiceId));
        Assert.Equal(CharacterShareMode.Together, refreshed.Characters[0].Mode);
        Assert.Empty(sharing.Refresh(companion.RemoveCharacter(aria.Id), lorebooks).Characters);
    }

    [Fact]
    public void Too_large_lorebooks_are_refused_with_a_plain_reason()
    {
        var (companion, lorebooks, aria) = SamsCharacters();
        var big = lorebooks with
        {
            Books = [.. lorebooks.Books, new Lorebook
            {
                Id = Guid.NewGuid(), Name = "Encyclopedia", Activation = LorebookActivation.SelectedPersonas, PersonaIds = [aria.PersonaId],
                Entries = [.. Enumerable.Range(0, 12).Select(i => new LorebookEntry { Uid = i, Keys = [$"k{i}"], Content = new string('x', 30_000) })]
            }]
        };
        var error = Assert.Throws<ContractException>(() => HouseholdSharing.Empty(Sam).WithMode(aria.Id, CharacterShareMode.Copy, companion, big));
        Assert.Equal(ErrorCode.PayloadTooLarge, error.Code);
        Assert.Contains("lorebooks are too large", error.Message);

        // A refresh that no longer fits keeps the last snapshot.
        var sharing = HouseholdSharing.Empty(Sam).WithMode(aria.Id, CharacterShareMode.Copy, companion, lorebooks);
        Assert.Equal(sharing.Write(), sharing.Refresh(companion, big).Write());
    }

    [Fact]
    public void Unusable_or_mismatched_entries_are_ignored()
    {
        var (companion, lorebooks, aria) = SamsCharacters();
        var sams = HouseholdSharing.Empty(Sam).WithMode(aria.Id, CharacterShareMode.Copy, companion, lorebooks).Write();
        var all = HouseholdSharing.All(
        [
            (HouseholdSharing.Key(Sam), sams),
            (HouseholdSharing.Key(Alex), sams),
            ("sharing.0b1c2d3e4f5061728394a5b6c7d8e9f1", "{\"schema_version\":2,\"account_id\":\"0b1c2d3e-4f50-6172-8394-a5b6c7d8e9f1\"}"),
            ("companion", sams),
            ("sharing.1b1c2d3e4f5061728394a5b6c7d8e9f0", "not json")
        ]);
        Assert.Equal([Sam], all.Keys);
        Assert.Null(HouseholdSharing.Read("{\"account_id\":\"" + Sam + "\",\"characters\":[{\"id\":\"" + aria.Id + "\",\"mode\":\"forever\"}]}"));
    }

    [Fact]
    public void Use_a_copy_makes_new_IDs_unique_names_and_its_own_lorebooks()
    {
        var (samsCompanion, samsLorebooks, aria) = SamsCharacters();
        var shared = HouseholdSharing.Empty(Sam).WithMode(aria.Id, CharacterShareMode.Copy, samsCompanion, samsLorebooks).Characters[0];
        // Alex already has a personality, a character and a lorebook with the same names.
        var alex = CompanionSettings.Create().Add("Aria");
        alex = alex.AddCharacter("Aria", alex.ActivePersonaId, null, null, out _);
        var alexBooks = LorebookLibrary.Create() with { Books = [new Lorebook { Id = Guid.NewGuid(), Name = "Aria's stars" }] };

        var (companion, added) = SharedCharacters.UseCopy(alex, shared);
        var lorebooks = SharedCharacters.CopyLorebooks(alexBooks, shared, added.PersonaId);
        Assert.NotEqual(aria.Id, added.Id);
        Assert.NotEqual(aria.PersonaId, added.PersonaId);
        Assert.Equal(("Aria 2", "model-aria", "voice-aria"), (added.Name, added.ModelId, added.VoiceId));
        var persona = companion.Personas.Single(p => p.Id == added.PersonaId);
        Assert.Equal(("Aria 2", "Aria is cheerful and loves astronomy."), (persona.Name, persona.Text));
        Assert.Equal(alex.ActivePersonaId, companion.ActivePersonaId);
        var copied = lorebooks.Books.Single(b => b.PersonaIds.Contains(added.PersonaId));
        Assert.NotEqual(shared.Lorebooks[0].Id, copied.Id);
        Assert.Equal("Aria's stars 2", copied.Name);
        Assert.Equal("Vega is Aria's favourite star.", copied.Entries[0].Content);
        companion.Validate();
        lorebooks.Validate();
    }

    [Fact]
    public void A_mirror_has_the_same_IDs_follows_the_owner_and_leaves_cleanly()
    {
        var (samsCompanion, samsLorebooks, aria) = SamsCharacters();
        var sharing = HouseholdSharing.Empty(Sam).WithMode(aria.Id, CharacterShareMode.Together, samsCompanion, samsLorebooks);
        var alex = CompanionSettings.Create();
        var alexBooks = LorebookLibrary.Create() with { Books = [new Lorebook { Id = Guid.NewGuid(), Name = "Alex's notes" }] };

        var joined = SharedCharacters.Join(alex, sharing.Characters[0]);
        var joinedBooks = SharedCharacters.MirrorLorebooks(alexBooks, sharing.Characters[0]);
        var mirror = joined.CharacterList.Single();
        Assert.Equal((aria.Id, aria.PersonaId), (mirror.Id, mirror.PersonaId));
        Assert.Equal(sharing.Characters[0].Lorebooks[0].Id, joinedBooks.Books.Single(b => b.PersonaIds.Contains(aria.PersonaId)).Id);

        // Nothing changed: the same instances come back.
        Assert.Same(joined, SharedCharacters.Join(joined, sharing.Characters[0]));
        Assert.Same(joinedBooks, SharedCharacters.MirrorLorebooks(joinedBooks, sharing.Characters[0]));

        // The owner edits Aria and drops her lorebook; the mirror follows.
        var edited = samsCompanion.Update(aria.PersonaId, "Aria", "Aria now loves comets.");
        var noBooks = samsLorebooks with { Books = [.. samsLorebooks.Books.Where(b => !b.PersonaIds.Contains(aria.PersonaId))] };
        var owners = sharing.Refresh(edited, noBooks).Characters[0];
        var followed = SharedCharacters.Join(joined, owners);
        var followedBooks = SharedCharacters.MirrorLorebooks(joinedBooks, owners);
        Assert.Equal("Aria now loves comets.", followed.Personas.Single(p => p.Id == aria.PersonaId).Text);
        Assert.DoesNotContain(followedBooks.Books, b => b.PersonaIds.Contains(aria.PersonaId));
        Assert.Contains(followedBooks.Books, b => b.Name == "Alex's notes");

        // Leaving while Alex talks to Aria: Alex's own personality is in use again, and Alex's things stay.
        var inUse = followed.SelectCharacter(aria.Id);
        var (left, persona) = SharedCharacters.Leave(inUse, aria.Id);
        Assert.Equal(aria.PersonaId, persona);
        var leftBooks = SharedCharacters.DropLorebooks(joinedBooks, persona!.Value);
        Assert.Null(left.Characters);
        Assert.Equal(alex.Personas.Single().Id, left.ActivePersonaId);
        Assert.DoesNotContain(left.Personas, p => p.Id == aria.PersonaId);
        Assert.Equal(["Alex's notes"], leftBooks.Books.Select(b => b.Name));
    }

    [Fact]
    public void Joined_characters_and_the_about_me_choice_are_kept_and_a_joined_character_cannot_be_shared_again()
    {
        var (companion, lorebooks, aria) = SamsCharacters();
        var alexs = HouseholdSharing.Empty(Alex).WithJoined(Sam, aria.Id).WithNewFactsAboutMe(true);
        Assert.Same(alexs, alexs.WithJoined(Sam, aria.Id));
        var read = HouseholdSharing.Read(alexs.Write())!;
        Assert.True(read.HasJoined(aria.Id));
        Assert.True(read.NewFactsAboutMe);
        Assert.Contains("\"new_facts_about_me\":true", alexs.Write());
        Assert.Throws<ContractException>(() => read.WithMode(aria.Id, CharacterShareMode.Copy, companion, lorebooks));
        Assert.False(read.WithoutJoined(aria.Id).HasJoined(aria.Id));
    }
}
