using Martlet.Core.Accounts;
using Martlet.Core.Network;

namespace Martlet.Desktop;

/// <summary>
/// The account directory changes of account security (docs/ACCOUNTS.md, Flows): this PC signing in as an account it proved,
/// linking and unlinking this Windows login, and merging another account into one. Each keeps the binding rule
/// (<see cref="AccountBindingRules"/>): a binding of this PC carries a host attestation, or this PC made the account.
/// </summary>
internal static class AccountLinks
{
    /// <summary>The directory login an attestation names (the same key, kept as the directory writes it).</summary>
    internal static AccountLoginKey LoginOf(AccountLoginKey login) => login.Kind == AccountLoginKinds.Martlet
        ? AccountLoginKey.ForPassword(login.Subject)
        : AccountLoginKey.ForProvider(login.Kind, login.Provider, login.Subject);

    /// <summary><paramref name="account"/> with this PC (<paramref name="deviceId"/>) bound to it by the Prove sign-in that
    /// <paramref name="attestation"/> records, and the login it proved with listed.</summary>
    internal static Account SignedIn(Account account, string deviceId, AccountAttestation attestation, DateTimeOffset now)
    {
        if (attestation.AccountId != account.Id || attestation.DeviceId != deviceId)
            throw new InvalidOperationException("That sign-in proved another account or another PC.");
        var login = LoginOf(attestation.Login);
        var listed = account.Login(login) is null ? account.WithLogin(AccountLogin.For(login, attestation.Login.Subject, now)) : account;
        return listed.WithDevice(AccountDevice.For(deviceId, login, now, attestation.ToText()));
    }

    /// <summary>*Link this Windows login*: this PC's Windows login (device ID and SID) becomes a login of
    /// <paramref name="account"/> and this PC signs in with it. The proof of this PC's binding stays: a fresh attestation, the
    /// one this PC already has for the account, or none when this PC made the account.</summary>
    internal static Account WithWindowsLogin(Account account, string deviceId, string sid, string? label, DateTimeOffset now,
        AccountAttestation? fresh = null)
    {
        var key = AccountLoginKey.ForWindows(deviceId, sid);
        var existing = account.Device(deviceId);
        var binding = fresh is not null
            ? fresh.AccountId == account.Id && fresh.DeviceId == deviceId ? AccountDevice.For(deviceId, key, now, fresh.ToText())
                : throw new InvalidOperationException("That sign-in proved another account or another PC.")
            : existing is not null ? existing with { Login = key }
            : account.CreatedBy == deviceId ? AccountDevice.For(deviceId, key, now)
            : throw new InvalidOperationException($"Sign in as {account.Name} on this PC first.");
        return account.WithLogin(AccountLogin.For(key, Account.CleanText(label, 128), now)).WithDevice(binding);
    }

    /// <summary>*Unlink this Windows login*: it no longer signs in as <paramref name="account"/>, and the account signs out of
    /// this PC where it signed in with it. Refused when it is the account's only login, so the account keeps a way in.</summary>
    internal static Account WithoutWindowsLogin(Account account, string deviceId, string sid)
    {
        var key = AccountLoginKey.ForWindows(deviceId, sid);
        if (account.Login(key) is null) return account;
        if (account.Logins.Count <= 1)
            throw new InvalidOperationException("This Windows login is this account's only login. Add a password first, so the account keeps a way in.");
        return account.WithoutLogin(key);
    }

    /// <summary>*Merge another account into this one*: <paramref name="into"/> gets the logins, device bindings, voices and e-mail
    /// hints of <paramref name="from"/>, which is removed with <c>merged_into</c>, both signed by <paramref name="by"/>. The
    /// caller proved both accounts on this PC first, and this PC must be bound to <paramref name="into"/> by a proof of its own
    /// (the binding rule refuses the moved bindings otherwise). The household owner's account can't be merged into another.</summary>
    internal static AccountDirectory Merge(AccountDirectory directory, INetworkSigner by, Guid into, Guid from, DateTimeOffset now)
    {
        var target = directory.Find(into) is { Removed: false } t ? t : throw new InvalidOperationException("The account to keep is not in the directory.");
        var merged = directory.Find(from) is { Removed: false } m ? m : throw new InvalidOperationException("The account to merge is not in the directory.");
        if (into == from) throw new InvalidOperationException("An account can't be merged into itself.");
        if (merged.Role == AccountRoles.Owner)
            throw new InvalidOperationException($"{merged.Name} is the household owner's account. Merge the other account into it instead.");
        if (target.Device(by.DeviceId) is null) throw new InvalidOperationException($"Sign in as {target.Name} on this PC first.");
        var result = target;
        foreach (var login in merged.Logins.Where(l => target.Login(l.Key) is null)) result = result.WithLogin(login);
        foreach (var device in merged.Devices.Where(d => target.Device(d.DeviceId) is null)) result = result.WithDevice(device);
        foreach (var voice in merged.Voices) result = result.WithVoice(voice);
        result = result with
        {
            EmailHints = result.EmailHints.Concat(merged.EmailHints).Distinct(StringComparer.Ordinal).Take(Account.MaximumEmailHints).ToArray(),
            Role = Higher(target.Role, merged.Role)
        };
        if (result.Logins.Count > Account.MaximumLogins || result.Devices.Count > Account.MaximumDevices || result.Voices.Count > Account.MaximumVoices)
            throw new InvalidOperationException("Together the two accounts have too many logins, computers or voices. Remove some first.");
        return directory.Remove(by, from, now, mergedInto: into).Put(by, result, now);
    }

    private static string Higher(string a, string b) =>
        a == AccountRoles.Owner || b == AccountRoles.Owner ? AccountRoles.Owner
        : a == AccountRoles.Admin || b == AccountRoles.Admin ? AccountRoles.Admin : AccountRoles.Member;
}
