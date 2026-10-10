using Martlet.Core.Sync;
using Martlet.Memory;

namespace Martlet.Desktop;

/// <summary>Sharing memories with the household (docs/ACCOUNTS.md, "Sharing"): where a new fact from remembering goes (for *Share
/// new memories about me*), and the copies of facts given to another account's space (which this device may not read) through the
/// hosts' give route.</summary>
internal static class MemorySharing
{
    /// <summary>Where remembering saves a new fact: the household space when *Share new memories about me* is on and the fact is
    /// about this person (<paramref name="voiceId"/> is one of <paramref name="myVoices"/>, after following merges with
    /// <paramref name="canonical"/>); else <paramref name="active"/>, the space the character in use remembers in.</summary>
    internal static string SpaceForNewFact(string? voiceId, IReadOnlySet<string> myVoices, bool shareAboutMe, string active,
        Func<string?, string?>? canonical = null) =>
        shareAboutMe && voiceId is not null && myVoices.Contains((canonical ?? (id => id))(voiceId) ?? voiceId) ? MemorySpaceId.Household : active;

    /// <summary>Copies of <paramref name="facts"/> to give to another account's space: new IDs, revision 1, the same words, whose
    /// fact it is and how long it is kept, marked as a reviewed import made now by this device. Expired facts are left out. At
    /// most 64 facts per gift.</summary>
    internal static SharedMemories Gift(IEnumerable<MemoryFact> facts, string deviceId, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(facts);
        var entries = new List<SharedMemory>();
        foreach (var fact in facts)
        {
            if (fact.Retention.HasExpired(now)) continue;
            var provenance = MemoryProvenance.UserReviewedImport(Guid.NewGuid(), now);
            var copy = fact with
            {
                Id = Guid.NewGuid(), Revision = 1, CreatedAtUtc = now, UpdatedAtUtc = now, CreatedFrom = provenance, LastModifiedBy = provenance
            };
            entries.Add(new SharedMemory
            {
                Id = copy.Id, Revision = 1, UpdatedAt = now, UpdatedBy = deviceId, Fact = SharedMemories.Canonical(MemoryFactJson.Write(copy))
            });
        }
        if (entries.Count > 64) throw new ArgumentException("Share at most 64 facts at once.", nameof(facts));
        return new SharedMemories { SchemaVersion = SharedMemories.SchemaVersion1, Facts = entries };
    }
}
