using Martlet.Core.Sync;
using Martlet.Desktop;
using Martlet.Memory;
using Xunit;

namespace Martlet.Desktop.Tests;

/// <summary>Sharing memories with the household (docs/ACCOUNTS.md, "Sharing"): where a new fact about this person goes, and the
/// copies given to another account's space.</summary>
public sealed class MemorySharingTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);

    private static MemoryFact Fact(string content, string? voiceId, MemoryRetention? retention = null)
    {
        var provenance = MemoryProvenance.Conversation(Guid.NewGuid(), Start);
        return new()
        {
            Id = Guid.NewGuid(), Revision = 3, Content = content, CreatedAtUtc = Start, UpdatedAtUtc = Start,
            CreatedFrom = provenance, LastModifiedBy = provenance, Retention = retention ?? MemoryRetention.UntilDeleted(), VoiceId = voiceId
        };
    }

    [Fact]
    public void A_new_fact_about_me_goes_to_the_household_only_when_I_share_new_memories_about_me()
    {
        var mine = new HashSet<string>(["v-sam"], StringComparer.Ordinal);
        Assert.Equal(MemorySpaceId.Household, MemorySharing.SpaceForNewFact("v-sam", mine, shareAboutMe: true));
        Assert.Null(MemorySharing.SpaceForNewFact("v-sam", mine, shareAboutMe: false));
        Assert.Null(MemorySharing.SpaceForNewFact("v-alex", mine, shareAboutMe: true));
        Assert.Null(MemorySharing.SpaceForNewFact(null, mine, shareAboutMe: true));
    }

    /// <summary>With *Share new memories about me* on, remembering saves a new fact about this person in the household's space,
    /// and the rest where it always goes; a character shared together remembers in its own space.</summary>
    [Fact]
    public async Task Remembering_sends_new_facts_about_me_to_the_household_and_a_together_character_uses_its_own_space()
    {
        var root = Path.Combine(AppContext.BaseDirectory, "memory-sharing", Guid.NewGuid().ToString("N"));
        try
        {
            var data = Path.Combine(root, "data");
            var store = new Martlet.Core.Settings.SettingsStore(data);
            var initial = Martlet.Core.Settings.SetupSettings.Begin(null);
            var saved = await store.SaveAsync(initial, null);
            using var memory = new DesktopMemoryService(store);
            var sam = Guid.NewGuid();
            var account = new MemoryAccount(sam, MemorySpaceFolders.AccountFolder(data, sam), data);
            memory.UseAccount(account);
            var revision = (await memory.SaveConfigurationAsync(initial, saved.Revision, enabled: true,
                policy: Martlet.Core.Settings.MemoryStoragePolicy.AppLocalData, customDirectory: null)).Settings.Memory!.ConfigurationRevision;
            var mine = new HashSet<string>(["v-sam"], StringComparer.Ordinal);
            memory.NewFactSpace = voice => MemorySharing.SpaceForNewFact(voice, mine, shareAboutMe: true);

            await memory.RememberAsync(revision, [],
            [
                new(MemoryCaptureKind.Remember, Content: "Sam drinks green tea.", VoiceId: "v-twin"),
                new(MemoryCaptureKind.Remember, Content: "Alex plays the cello.", VoiceId: "v-alex"),
                new(MemoryCaptureKind.Remember, Content: "The cat is called Miso.")
            ], person: id => id == "v-twin" ? "v-sam" : id);
            var spaces = await memory.InspectSpacesAsync(revision);
            Assert.Equal(["Alex plays the cello.", "The cat is called Miso."], spaces[0].Inspection.Facts.Select(f => f.Content).Order());
            var household = spaces.Single(s => s.Space.Id == MemorySpaceId.Household).Inspection.Facts;
            Assert.Equal(("Sam drinks green tea.", "v-twin"), (Assert.Single(household).Content, household[0].VoiceId));

            // A character shared together: its own space is the active one; new facts about others go there.
            var character = MemorySpaceId.Character(Guid.NewGuid());
            memory.UseAccount(account with { Character = character });
            await memory.RememberAsync(revision, [], [new(MemoryCaptureKind.Remember, Content: "Alex likes jazz.", VoiceId: "v-alex")]);
            var after = await memory.InspectSpacesAsync(revision);
            Assert.Equal(character, after[0].Space.Id);
            Assert.Equal("Alex likes jazz.", Assert.Single(after[0].Inspection.Facts).Content);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void A_gift_has_new_IDs_the_same_words_person_and_keeping_and_leaves_out_expired_facts()
    {
        var tea = Fact("Sam drinks green tea.", "v-sam");
        var trip = Fact("Sam visits Kyoto in May.", null, MemoryRetention.ExpiringAt(Start.AddDays(90)));
        var gone = Fact("Sam's old phone number.", "v-sam", MemoryRetention.ExpiringAt(Start.AddDays(1)));
        var now = Start.AddDays(2);

        var gift = MemorySharing.Gift([tea, trip, gone], "alexs-pc", now);
        Assert.Equal(2, gift.Facts.Count);
        Assert.Empty(gift.Forgotten);
        var copies = gift.Facts.Select(f => (Entry: f, Fact: MemoryFactJson.Read(f.Fact!))).ToArray();
        Assert.All(copies, c =>
        {
            Assert.Equal((1L, "alexs-pc", now), (c.Entry.Revision, c.Entry.UpdatedBy, c.Entry.UpdatedAt));
            Assert.Equal(c.Entry.Id, c.Fact.Id);
            Assert.DoesNotContain(c.Fact.Id, new[] { tea.Id, trip.Id });
            Assert.Equal(MemorySourceKind.UserReviewedImport, c.Fact.LastModifiedBy.SourceKind);
        });
        var teaCopy = copies.Single(c => c.Fact.Content == tea.Content).Fact;
        Assert.Equal(("v-sam", MemoryRetentionKind.UntilDeleted), (teaCopy.VoiceId, teaCopy.Retention.Kind));
        var tripCopy = copies.Single(c => c.Fact.Content == trip.Content).Fact;
        Assert.Equal((null, Start.AddDays(90)), (tripCopy.VoiceId, tripCopy.Retention.ExpiresAtUtc));
        Assert.Throws<ArgumentException>(() => MemorySharing.Gift(Enumerable.Range(0, 65).Select(i => Fact($"Fact {i}.", null)), "alexs-pc", now));
    }
}
