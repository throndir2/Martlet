using System.IO;
using Martlet.Core.Accounts;
using Martlet.Core.Contracts;
using Martlet.Core.Network;

namespace Martlet.Mcp;

/// <summary>accounts_status: who uses Martlet on a desktop's data folder (docs/ACCOUNTS.md). It reads accounts\session.json (the
/// accounts signed in on that device and the one in use) and accounts.json (that PC's copy of the household's account
/// directory). Display names, roles, login kinds, device IDs and counts only: never a SID, e-mail, e-mail hint, signature or
/// attestation. Read-only; contacts nothing.</summary>
internal static class AccountsStatus
{
    /// <summary>The fixture SID of MARTLET_SIMULATE_WINDOWS_LOGIN (Martlet.Desktop's AccountSession.SimulatedSid).</summary>
    private const string SimulatedSid = "S-1-5-21-1000-1000-1000-1001";

    internal static object Read(string dataDirectory)
    {
        var device = DeviceIds.Peek(dataDirectory)?.Id;
        var (directoryState, directory) = ReadDirectory(dataDirectory);
        AccountSessionState? session = null;
        string sessionState;
        try
        {
            session = AccountSessionState.Load(dataDirectory);
            sessionState = session is null ? "none" : "loaded";
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ContractException) { sessionState = "unreadable"; }

        object View(Guid id) => directory?.Find(id) is { } entry
            ? new { id = entry.Key, name = entry.Name, role = entry.Role, pending = false, removed = entry.Removed, current = id == session?.Current,
                folder = Directory.Exists(Folder(dataDirectory, id)) }
            : session?.PendingFor(id) is { } pending
                ? new { id = id.ToString("N"), name = pending.Name, role = pending.Role, pending = true, removed = false, current = id == session.Current,
                    folder = Directory.Exists(Folder(dataDirectory, id)) }
                : new { id = id.ToString("N"), name = "", role = "", pending = true, removed = false, current = id == session?.Current,
                    folder = Directory.Exists(Folder(dataDirectory, id)) };

        var owner = directory?.Live.Where(a => a.Role == AccountRoles.Owner).OrderBy(a => a.Key, StringComparer.Ordinal).FirstOrDefault()?.Key
            ?? session?.Pending.FirstOrDefault(p => p.Role == AccountRoles.Owner)?.Id.ToString("N");
        return new
        {
            device,
            session = sessionState,
            windowsLogin = session is null ? null : session.WindowsSid == SimulatedSid ? "fixture" : "windows",
            current = session is null ? null : View(session.Current),
            signedIn = session?.SignedIn.Select(View).ToArray(),
            pending = session?.Pending.Count ?? 0,
            owner,
            directory = directoryState,
            accounts = directory?.Live.Count() ?? 0,
            removed = directory?.Accounts.Count(a => a.Removed) ?? 0,
            owners = directory?.Live.Count(a => a.Role == AccountRoles.Owner) ?? 0,
            revision = directory?.Revision,
            entries = directory?.Accounts.Select(a => new
            {
                id = a.Key, name = a.Name, role = a.Role, removed = a.Removed,
                logins = a.Logins.Select(l => l.Kind).ToArray(),
                devices = a.Devices.Count,
                thisDevice = device is not null && a.Device(device) is not null,
                createdBy = a.CreatedBy, updatedBy = a.UpdatedBy, changedAt = a.UpdatedAt
            }).ToArray()
        };
    }

    private static string Folder(string dataDirectory, Guid id) => Path.Combine(dataDirectory, AccountSessionState.Folder, id.ToString("N"));

    private static (string State, AccountDirectory? Directory) ReadDirectory(string dataDirectory)
    {
        var path = Path.Combine(dataDirectory, AccountDirectory.FileName);
        if (!File.Exists(path)) return ("none", null);
        try { return ("loaded", AccountDirectory.Parse(File.ReadAllBytes(path))); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ContractException) { return ("unreadable", null); }
    }
}
