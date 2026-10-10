using Martlet.Core.Accounts;

namespace Martlet.Gateway;

/// <summary>
/// Who may change a host's sign-in settings (docs/ACCOUNTS.md, Roles): once the household has accounts in this host's account
/// directory, a member desktop bound to an owner or admin account may change everything; a desktop bound only to member
/// accounts may change only those accounts' own logins: their Martlet password logins (<c>account</c>,
/// <c>remove-account-authenticator</c>, <c>recovery-codes</c> and <c>remove-account</c> with that <c>account_id</c>), and (W13)
/// their provider logins: <c>link-login</c> with an attestation for that account (the gateway checks it) and <c>disallow</c> of an
/// identity linked to it. A household with no accounts yet (before its desktops migrate) keeps the earlier rule: any active
/// member desktop. The host sees devices, not people, so a desktop bound to an admin account counts as an admin's whichever of
/// its accounts is active; the desktop shows these changes only to the account that may make them.
/// </summary>
internal static class GatewaySignInRoles
{
    private static readonly HashSet<string> OwnAccountActions = ["account", "remove-account-authenticator", "recovery-codes", "remove-account"];

    internal static bool Allowed(AccountDirectory directory, string deviceId, GatewaySignInChange change, GatewaySignInDocument? signIn = null)
    {
        ArgumentNullException.ThrowIfNull(directory);
        ArgumentNullException.ThrowIfNull(change);
        if (!directory.Live.Any()) return true;
        var bound = directory.SignedInOn(deviceId);
        if (bound.Any(a => AccountRoles.ManagesHousehold(a.Role))) return true;
        if (change.Action == "link-login")
            return GatewaySignInSettings.ParseAttestation(change.Attestation) is var attested && bound.Any(a => a.Id == attested.AccountId);
        if (change.Action == "disallow")
            return signIn?.Allowed.LastOrDefault(a => a.Provider == change.Provider && a.Subject == change.Subject) is { Access: null, AccountId: { } linked } &&
                bound.Any(a => a.Id == linked);
        return OwnAccountActions.Contains(change.Action) && change.AccountId is { } id && bound.Any(a => a.Id == id);
    }
}
