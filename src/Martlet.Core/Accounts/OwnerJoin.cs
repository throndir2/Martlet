using Martlet.Core.Network;

namespace Martlet.Core.Accounts;

/// <summary>What a desktop does after the owner let it join (docs/ACCOUNTS.md, "Joining with the owner's approval"): it signs in
/// to <see cref="Owner"/>, the household's owner account, and folds <see cref="Own"/>, the owner account it made itself before it
/// joined, into it. <see cref="OwnWritten"/>: <see cref="Own"/> is in the directory (it is merged and removed); else it is only
/// pending on this PC (it is signed out). <see cref="SameName"/>: <see cref="Owner"/> is the account of the same name that
/// <see cref="Own"/> folds into (<see cref="SamePerson"/>), not necessarily the household's owner.</summary>
public sealed record OwnerJoinPlan(Guid Owner, Guid Own, bool OwnWritten, bool SameName = false);

public static class OwnerJoin
{
    /// <summary>The household's owner account in <paramref name="directory"/>: a live <c>owner</c> account that
    /// <see cref="AccountBindingRules.IsHouseholdOwner"/> accepts, <see cref="OwnerAccount.IdFor"/> first. Null when there is none.</summary>
    public static Account? HouseholdOwner(AccountDirectory directory, NetworkRoster roster)
    {
        ArgumentNullException.ThrowIfNull(directory);
        ArgumentNullException.ThrowIfNull(roster);
        var migrated = OwnerAccount.IdFor(roster.NetworkId);
        return directory.Live.Where(a => a.Role == AccountRoles.Owner && AccountBindingRules.IsHouseholdOwner(a, roster))
            .OrderBy(a => a.Id == migrated ? 0 : 1).ThenBy(a => a.Key, StringComparer.Ordinal).FirstOrDefault();
    }

    /// <summary>The plan for desktop <paramref name="deviceId"/>, or null when there is nothing to do: it signed nobody in with a
    /// Prove sign-in and didn't join by signing in to an account (<paramref name="joinedBySignIn"/>), the household has an owner
    /// account, and an <c>owner</c> account signed in here is one this device made (in the directory, or pending) that is not the
    /// household's. The founder's own owner account is the household's, so the founder never folds it.</summary>
    public static OwnerJoinPlan? Plan(AccountDirectory directory, AccountSessionState state, NetworkRoster roster, string deviceId, bool joinedBySignIn)
    {
        ArgumentNullException.ThrowIfNull(directory);
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(roster);
        if (joinedBySignIn || state.Proofs.Count > 0 || roster.Desktop(deviceId) is not { Removed: false }) return null;
        if (HouseholdOwner(directory, roster) is not { } owner) return null;
        foreach (var id in state.SignedIn.Where(id => id != owner.Id))
        {
            var entry = directory.Find(id);
            if (entry is { Removed: false, Role: AccountRoles.Owner } && entry.CreatedBy == deviceId && !AccountBindingRules.IsHouseholdOwner(entry, roster))
                return new(owner.Id, id, true);
            if (entry is null && state.PendingFor(id) is { Role: AccountRoles.Owner }) return new(owner.Id, id, false);
        }
        return null;
    }
}
