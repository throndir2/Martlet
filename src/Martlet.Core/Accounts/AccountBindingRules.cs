using Martlet.Core.Network;

namespace Martlet.Core.Accounts;

/// <summary>
/// The binding rule (docs/ACCOUNTS.md, "Device bindings"): a device acts for an account only when it proved that account, so no
/// PC takes over someone else's account. Hosts and desktops both apply it, through <see cref="AccountDirectory.Refusal"/>. A
/// device binding of a live account is valid when one of these is true:
/// <list type="number">
/// <item>The copy this computer already accepted has exactly that binding (it was checked then; a host removed since doesn't
/// undo it).</item>
/// <item>The device created the account (<see cref="Account.CreatedBy"/>, which no later write may change).</item>
/// <item>It carries an <see cref="AccountAttestation"/> that <see cref="AccountAttestation.Check"/> accepts against the roster
/// at the binding's <see cref="AccountDevice.SignedInAt"/>, for that device and for this account.</item>
/// <item>Migration: the account is the household's owner account (<see cref="OwnerAccount.IdFor"/>) and the device, an active
/// member desktop of the roster, binds itself (it wrote the entry) with its own Windows login. Every desktop of a household
/// that existed before accounts was the owner's.</item>
/// <item>Merged: "Merge another account into this one" moved it from an account that the directory shows merged into this
/// one (the device created that account, or has an attestation for it), and the device that wrote that merge is bound to this
/// account by one of the proofs above.</item>
/// </list>
/// </summary>
public static class AccountBindingRules
{
    public const string Refused = "account.binding";

    /// <summary>Null when every device binding of <paramref name="incoming"/> is valid, else <see cref="Refused"/>.
    /// <paramref name="mergedIntoThis"/>: the removed accounts that were merged into <paramref name="incoming"/>.</summary>
    public static string? Refusal(Account? accepted, Account incoming, NetworkRoster roster, IReadOnlyCollection<Account>? mergedIntoThis = null)
    {
        ArgumentNullException.ThrowIfNull(incoming);
        ArgumentNullException.ThrowIfNull(roster);
        if (incoming.Removed) return null;
        foreach (var device in incoming.Devices)
            if (Proof(accepted, incoming, device, roster, mergedIntoThis) is null) return Refused;
        return null;
    }

    /// <summary>Why <paramref name="device"/> may act for <paramref name="account"/>: "accepted", "creator", "attestation",
    /// "migration" or "merged"; null when it may not. A merged account counts only when the device that merged it (the writer of
    /// its removal) has a binding of its own to <paramref name="account"/> with a direct proof, so nobody carries a device into
    /// someone else's account by merging an account of their own into it.</summary>
    public static string? Proof(Account? accepted, Account account, AccountDevice device, NetworkRoster roster,
        IReadOnlyCollection<Account>? mergedIntoThis = null)
    {
        ArgumentNullException.ThrowIfNull(account);
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(roster);
        if (Direct(accepted, account, device, roster) is { } direct) return direct;
        var merged = (mergedIntoThis ?? []).Where(m => m.Removed && m.MergedInto == account.Id &&
            account.Device(m.UpdatedBy) is { } merger && Direct(accepted, account, merger, roster) is not null).ToArray();
        if (merged.Length == 0) return null;
        return merged.Any(m => m.CreatedBy == device.DeviceId) || Attested(account, device, roster, merged.Select(m => m.Id)) ? "merged" : null;
    }

    private static string? Direct(Account? accepted, Account account, AccountDevice device, NetworkRoster roster)
    {
        if (accepted is not null && accepted.Id == account.Id && accepted.Device(device.DeviceId) is { } kept && kept == device) return "accepted";
        if (device.DeviceId == account.CreatedBy) return "creator";
        if (Attested(account, device, roster, [])) return "attestation";
        if (account.Id == OwnerAccount.IdFor(roster.NetworkId) && account.UpdatedBy == device.DeviceId &&
            device.Login is { Kind: AccountLoginKinds.Windows } login && login.Provider == device.DeviceId &&
            roster.Desktop(device.DeviceId) is { Removed: false })
            return "migration";
        return null;
    }

    /// <summary>The removed accounts that <paramref name="directories"/> show merged into <paramref name="id"/>.</summary>
    public static IReadOnlyList<Account> MergedInto(Guid id, params AccountDirectory[] directories) =>
        directories.SelectMany(d => d.Accounts).Where(a => a.Removed && a.MergedInto == id).DistinctBy(a => a.Id).ToArray();

    private static bool Attested(Account account, AccountDevice device, NetworkRoster roster, IEnumerable<Guid> merged)
    {
        if (device.Attestation is not { Length: > 0 } text) return false;
        AccountAttestation attestation;
        try { attestation = AccountAttestation.FromText(text); }
        catch (FormatException) { return false; }
        return attestation.DeviceId == device.DeviceId &&
            (attestation.AccountId == account.Id || merged.Contains(attestation.AccountId)) &&
            attestation.Check(roster, device.SignedInAt) == AccountAttestationCheck.Valid;
    }
}
