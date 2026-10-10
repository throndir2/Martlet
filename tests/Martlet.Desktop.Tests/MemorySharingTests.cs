using Martlet.Core.Speakers;
using Martlet.Core.Sync;
using Martlet.Desktop;
using Martlet.Memory;
using Xunit;

namespace Martlet.Desktop.Tests;

/// <summary>Sharing memories with the household (docs/ACCOUNTS.md, "Sharing"): whose voices are this person, where a new fact
/// about them goes, and the copies given to another account's space.</summary>
public sealed class MemorySharingTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);

    private static float[] Print(int seed)
    {
        var random = new Random(seed);
        return VoicePrints.Normalize(Enumerable.Range(0, VoicePrints.Dimension).Select(_ => (float)(random.NextDouble() - 0.5)).ToArray());
    }

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
    public void My_voices_are_the_voices_linked_to_my_account_or_without_accounts_the_voice_marked_this_is_me()
    {
        var roster = VoiceRoster.Empty;
        (roster, var sam) = roster.Add(Print(1), 3, "desk", Start);
        (roster, var alex) = roster.Add(Print(2), 3, "desk", Start);
        (roster, var twin) = roster.Add(Print(3), 3, "desk", Start);
        var samsAccount = Guid.NewGuid();
        var alexsAccount = Guid.NewGuid();
        roster = roster.SetOwner(sam!.Id, true, "desk", Start).SetAccount(sam.Id, samsAccount, true, "desk", Start)
            .SetAccount(alex!.Id, alexsAccount, false, "desk", Start).Join(twin!.Id, sam.Id, "desk", Start);

        Assert.Equal([sam.Id], MemorySharing.MyVoices(samsAccount, roster));
        Assert.Equal([alex.Id], MemorySharing.MyVoices(alexsAccount, roster));
        // Someone with no voice linked yet is never taken for the owner.
        Assert.Empty(MemorySharing.MyVoices(Guid.NewGuid(), roster));
        Assert.Equal([sam.Id], MemorySharing.MyVoices(null, roster));
        Assert.Empty(MemorySharing.MyVoices(null, null));
    }

    [Fact]
    public void A_new_fact_about_me_goes_to_the_household_only_when_I_share_new_memories_about_me()
    {
        var mine = new HashSet<string>(["v-sam"], StringComparer.Ordinal);
        var active = MemorySpaceId.Account(Guid.NewGuid());
        Assert.Equal(MemorySpaceId.Household, MemorySharing.SpaceForNewFact("v-sam", mine, shareAboutMe: true, active));
        Assert.Equal(active, MemorySharing.SpaceForNewFact("v-sam", mine, shareAboutMe: false, active));
        Assert.Equal(active, MemorySharing.SpaceForNewFact("v-alex", mine, shareAboutMe: true, active));
        Assert.Equal(active, MemorySharing.SpaceForNewFact(null, mine, shareAboutMe: true, active));
        // A merged voice counts as the person it joined.
        Assert.Equal(MemorySpaceId.Household, MemorySharing.SpaceForNewFact("v-twin", mine, true, active, id => id == "v-twin" ? "v-sam" : id));
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
