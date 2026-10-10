using Martlet.Core.Accounts;
using Martlet.Core.Network;
using Martlet.Core.Sharing;
using Martlet.Core.Sync;

namespace Martlet.Gateway;

/// <summary>Which paired device may use which memory space on this host (docs/ACCOUNTS.md, "Memory spaces"), decided by the
/// household's account directory that this host keeps (<c>accounts.json</c>). Friends and API keys never get here: the routes
/// refuse them first, and these rules refuse them again.</summary>
internal static class GatewayMemorySpaceRules
{
    /// <summary><c>household</c>: every member device. <c>account-&lt;id&gt;</c>: only a device where that account is signed in
    /// (a device binding in the directory). <c>character-&lt;id&gt;</c>, by the household's sharing entries
    /// (<c>sharing.&lt;account&gt;</c>, <paramref name="sharing"/>): a character shared together, a device where someone of the
    /// household is signed in; a character that remembers on its own but isn't shared together (now private), only a device where
    /// its owner is signed in; any other, a device where someone of the household is signed in. Giving facts
    /// (<see cref="GatewayMemorySpaceUse.Give"/>: sharing a fact with someone, never reading their space) is for every member
    /// device.</summary>
    internal static bool MayUse(AccountDirectory directory, GatewayPrincipal principal, string space, GatewayMemorySpaceUse use,
        IReadOnlyDictionary<Guid, HouseholdSharing>? sharing = null)
    {
        ArgumentNullException.ThrowIfNull(directory);
        if (!GatewayMemorySpaces.EveryMember(principal, space, use)) return false;
        if (use == GatewayMemorySpaceUse.Give || space == MemorySpaceId.Household) return true;
        if (space.StartsWith(MemorySpaceId.AccountPrefix, StringComparison.Ordinal))
            return directory.Live.FirstOrDefault(a => a.SpaceId == space)?.Device(principal.DeviceId) is not null;
        if (!space.StartsWith(MemorySpaceId.CharacterPrefix, StringComparison.Ordinal)) return false;
        var character = Guid.ParseExact(space[MemorySpaceId.CharacterPrefix.Length..], "N");
        var entries = sharing?.Values ?? [];
        if (!entries.Any(s => s.ModeOf(character) == CharacterShareMode.Together) &&
            entries.FirstOrDefault(s => s.OwnSpaces.Contains(character)) is { } owner)
            return directory.Find(owner.AccountId) is { Removed: false } account && account.Device(principal.DeviceId) is not null;
        return directory.SignedInOn(principal.DeviceId).Count > 0;
    }

    /// <summary>The old single memory document (<c>/martlet/v1/memories</c>) holds the owner's memories from before accounts.
    /// A member device where the owner is signed in may use it, and so may a device where no account is signed in yet (a desktop
    /// on an older Martlet); a device where only other people are signed in may not.</summary>
    internal static bool MayUseOldDocument(AccountDirectory directory, NetworkRoster? roster, GatewayPrincipal principal)
    {
        ArgumentNullException.ThrowIfNull(directory);
        if (principal is not { Access: GatewayAccess.Full, Key: null }) return false;
        var signedIn = directory.SignedInOn(principal.DeviceId);
        if (signedIn.Count == 0) return true;
        Guid? owner = roster is null ? null : OwnerAccount.IdFor(roster.NetworkId);
        return signedIn.Any(a => a.Id == owner || a.Role == AccountRoles.Owner);
    }
}

internal sealed partial class GatewayHttpApplication
{
    private sealed record SharingCache(SharedSettings Settings, IReadOnlyDictionary<Guid, HouseholdSharing> Entries);
    private SharingCache? householdSharing;

    private bool MayUseMemorySpace(GatewayPrincipal principal, string space, GatewayMemorySpaceUse use) =>
        GatewayMemorySpaceRules.MayUse(Accounts.Current, principal, space, use, HouseholdSharingNow());

    /// <summary>The household's sharing entries in this host's copy of the household settings, read again only when it changed.</summary>
    private IReadOnlyDictionary<Guid, HouseholdSharing> HouseholdSharingNow()
    {
        var settings = Settings.Current;
        if (householdSharing is { } cached && ReferenceEquals(cached.Settings, settings)) return cached.Entries;
        var entries = HouseholdSharing.All(settings.Settings.Where(s => HouseholdSharing.IsKey(s.Key)).Select(s => (s.Key, s.Value)));
        householdSharing = new(settings, entries);
        return entries;
    }

    private void AdmitOldMemories(GatewayPrincipal principal) =>
        GatewayRules.Require(GatewayMemorySpaceRules.MayUseOldDocument(Accounts.Current, Network.Roster, principal), "memories.space_denied");
}
