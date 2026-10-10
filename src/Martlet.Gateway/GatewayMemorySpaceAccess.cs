using Martlet.Core.Accounts;
using Martlet.Core.Network;
using Martlet.Core.Sync;

namespace Martlet.Gateway;

/// <summary>Which paired device may use which memory space on this host (docs/ACCOUNTS.md, "Memory spaces"), decided by the
/// household's account directory that this host keeps (<c>accounts.json</c>). Friends and API keys never get here: the routes
/// refuse them first, and these rules refuse them again.</summary>
internal static class GatewayMemorySpaceRules
{
    /// <summary><c>household</c>: every member device. <c>account-&lt;id&gt;</c>: only a device where that account is signed in
    /// (a device binding in the directory). <c>character-&lt;id&gt;</c>: a device where someone of the household is signed in; the
    /// directory doesn't record yet whose a character is and how it is shared (sharing, workstream W10, narrows this).</summary>
    internal static bool MayUse(AccountDirectory directory, GatewayPrincipal principal, string space, GatewayMemorySpaceUse use)
    {
        ArgumentNullException.ThrowIfNull(directory);
        if (!GatewayMemorySpaces.EveryMember(principal, space, use)) return false;
        if (space == MemorySpaceId.Household) return true;
        if (space.StartsWith(MemorySpaceId.AccountPrefix, StringComparison.Ordinal))
            return directory.Live.FirstOrDefault(a => a.SpaceId == space)?.Device(principal.DeviceId) is not null;
        return space.StartsWith(MemorySpaceId.CharacterPrefix, StringComparison.Ordinal) && directory.SignedInOn(principal.DeviceId).Count > 0;
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
    private bool MayUseMemorySpace(GatewayPrincipal principal, string space, GatewayMemorySpaceUse use) =>
        GatewayMemorySpaceRules.MayUse(Accounts.Current, principal, space, use);

    private void AdmitOldMemories(GatewayPrincipal principal) =>
        GatewayRules.Require(GatewayMemorySpaceRules.MayUseOldDocument(Accounts.Current, Network.Roster, principal), "memories.space_denied");
}
