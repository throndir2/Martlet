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
/// at the binding's <see cref="AccountDevice.SignedInAt"/>, for that device and for this account, or for an account that the
/// directory shows merged into this one ("Merge another account into this one" moves the merged account's bindings).</item>
/// <item>Migration: the account is the household's owner account (<see cref="OwnerAccount.IdFor"/>) and the device, an active
/// member desktop of the roster, binds itself (it wrote the entry) with its own Windows login. Every desktop of a household
/// that existed before accounts was the owner's.</item>
/// </list>
/// </summary>
public static class AccountBindingRules
{
    public const string Refused = "account.binding";

    /// <summary>Null when every device binding of <paramref name="incoming"/> is valid, else <see cref="Refused"/>.
    /// <paramref name="mergedInto"/> tells which account another account was merged into (null when it wasn't).</summary>
    public static string? Refusal(Account? accepted, Account incoming, NetworkRoster roster, Func<Guid, Guid?>? mergedInto = null)
    {
        ArgumentNullException.ThrowIfNull(incoming);
        ArgumentNullException.ThrowIfNull(roster);
        if (incoming.Removed) return null;
        foreach (var device in incoming.Devices)
            if (Proof(accepted, incoming, device, roster, mergedInto) is null) return Refused;
        return null;
    }

    /// <summary>Why <paramref name="device"/> may act for <paramref name="account"/>: "accepted", "creator", "attestation" or
    /// "migration"; null when it may not.</summary>
    public static string? Proof(Account? accepted, Account account, AccountDevice device, NetworkRoster roster, Func<Guid, Guid?>? mergedInto = null)
    {
        ArgumentNullException.ThrowIfNull(account);
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(roster);
        if (accepted is not null && accepted.Id == account.Id && accepted.Device(device.DeviceId) is { } kept && kept == device) return "accepted";
        if (device.DeviceId == account.CreatedBy) return "creator";
        if (Attested(account, device, roster, mergedInto)) return "attestation";
        if (account.Id == OwnerAccount.IdFor(roster.NetworkId) && account.UpdatedBy == device.DeviceId &&
            device.Login is { Kind: AccountLoginKinds.Windows } login && login.Provider == device.DeviceId &&
            roster.Desktop(device.DeviceId) is { Removed: false })
            return "migration";
        return null;
    }

    private static bool Attested(Account account, AccountDevice device, NetworkRoster roster, Func<Guid, Guid?>? mergedInto)
    {
        if (device.Attestation is not { Length: > 0 } text) return false;
        AccountAttestation attestation;
        try { attestation = AccountAttestation.FromText(text); }
        catch (FormatException) { return false; }
        return attestation.DeviceId == device.DeviceId &&
            (attestation.AccountId == account.Id || mergedInto?.Invoke(attestation.AccountId) == account.Id) &&
            attestation.Check(roster, device.SignedInAt) == AccountAttestationCheck.Valid;
    }
}
