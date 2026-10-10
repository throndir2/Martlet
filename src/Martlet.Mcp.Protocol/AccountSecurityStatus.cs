using System.IO;
using Martlet.Core.Accounts;
using Martlet.Core.Network;
using Martlet.Mcp.Shared;

namespace Martlet.Mcp;

/// <summary>
/// MCP <c>account_security_status</c> (docs/ACCOUNTS.md, docs/MCP.md): account security as a desktop data directory keeps it,
/// read-only and without the desktop. Per account in the account directory (accounts.json): role, removed or merged, the kinds
/// of its logins and each device binding with the proof that lets it act for the account (the binding rule) or why not; and per
/// account with an unlock file here (account-locks): the ways it unlocks on this PC, *Ask for my password on this PC*,
/// *Remember me*, encryption and how many of its files are encrypted, wrong tries and a wait. Never a PIN, password,
/// verifier, attestation, e-mail, user name or SID.
/// </summary>
internal static class AccountSecurityStatus
{
    internal static object Read(string dataDirectory)
    {
        var device = DeviceIds.Peek(dataDirectory)?.Id;
        var roster = Roster(dataDirectory);
        var (directory, directoryState) = Directory(dataDirectory);
        var locks = new AccountLockStore(dataDirectory).All();
        var accounts = directory?.Accounts.Select(account =>
        {
            var merged = AccountBindingRules.MergedInto(account.Id, directory);
            return new
            {
                id = account.Key,
                name = account.Name,
                role = account.Role,
                removed = account.Removed,
                mergedInto = account.MergedInto?.ToString("N"),
                createdBy = account.CreatedBy,
                logins = account.Logins.GroupBy(l => l.Kind).Select(g => new { kind = g.Key, count = g.Count() }).ToArray(),
                windowsLoginOfThisPc = device is not null && account.Logins.Any(l => l.Kind == AccountLoginKinds.Windows && l.Provider == device),
                devices = account.Devices.Select(d => new
                {
                    deviceId = d.DeviceId,
                    thisPc = d.DeviceId == device,
                    login = d.Login.Kind,
                    attestation = d.Attestation is not null,
                    proof = roster is null ? "no_network" : AccountBindingRules.Proof(null, account, d, roster, merged) ?? "none"
                }).ToArray(),
                emailHints = account.EmailHints.Count
            };
        }).ToArray() ?? [];
        return new
        {
            device,
            network = roster is not null,
            directory = directoryState,
            accounts,
            signedInHere = device is null || directory is null ? [] : directory.SignedInOn(device).Select(a => a.Key).ToArray(),
            locks = locks.Select(l =>
            {
                var (encrypted, plain) = AccountVault.Count(Path.Combine(dataDirectory, "accounts", l.AccountId.ToString("N")));
                return new
                {
                    account = l.AccountId.ToString("N"),
                    methods = l.Methods,
                    askOnThisPc = l.AskOnThisPc,
                    remember = l.Remember,
                    encrypt = l.Encrypt,
                    encryptedFiles = encrypted,
                    plainFiles = plain,
                    failures = l.Failures,
                    waitUntil = l.RetryAfter is { } wait && wait > DateTimeOffset.UtcNow ? wait : (DateTimeOffset?)null,
                    protectedPart = l.Protected is not null
                };
            }).ToArray()
        };
    }

    private static NetworkRoster? Roster(string dataDirectory)
    {
        try
        {
            var path = Path.Combine(dataDirectory, Martlet.Avatar.Audio2Face.Remote.NetworkLocalState.FileName);
            return File.Exists(path) ? Martlet.Avatar.Audio2Face.Remote.NetworkLocalState.Parse(File.ReadAllBytes(path)).Roster : null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or Martlet.Core.Contracts.ContractException) { return null; }
    }

    private static (AccountDirectory? Directory, string State) Directory(string dataDirectory)
    {
        var path = Path.Combine(dataDirectory, AccountDirectory.FileName);
        if (!File.Exists(path)) return (null, "none");
        try { return (AccountDirectory.Parse(File.ReadAllBytes(path)), "read"); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or Martlet.Core.Contracts.ContractException) { return (null, "unreadable"); }
    }
}
