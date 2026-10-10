using Martlet.Core.Accounts;
using Martlet.Core.Network;

namespace Martlet.Desktop;

/// <summary>What the Account page needs from the desktop's account session (docs/ACCOUNTS.md, "Desktop account session").</summary>
internal interface IAccountPageHost
{
    string DataDirectory { get; }
    string DeviceId { get; }
    /// <summary>The SID of the Windows login the session is bound to (the subject of this PC's <c>windows</c> login).</summary>
    string WindowsSid { get; }
    /// <summary>The Windows login's kind (microsoft, work or local), to show.</summary>
    string WindowsKind { get; }
    /// <summary>The active account as this PC's account directory has it now; null while it waits to reach the directory.</summary>
    Account? Active { get; }
    /// <summary>The active account's ID and name (also while it waits to reach the directory).</summary>
    Guid ActiveId { get; }
    string ActiveName { get; }
    /// <summary>Whether the active account signed in here with a Prove sign-in (not this Windows login).</summary>
    bool SignedInWithProve { get; }
    AccountDirectory Directory { get; }
    NetworkRoster? Roster { get; }
    /// <summary>The hosts of this PC's own network it is paired with, to check sign-ins and keep password logins.</summary>
    IReadOnlyList<ProveHost> Hosts { get; }
    AccountLockStore Locks { get; }
    /// <summary>The active account's file key while it is unlocked and its files are encrypted here; null otherwise.</summary>
    byte[]? FileKey { get; set; }
    /// <summary>Changes this PC's account directory (signed by this PC's network key), saves it and syncs it to the hosts.</summary>
    Task<AccountDirectory> ChangeDirectoryAsync(Func<AccountDirectory, INetworkSigner, DateTimeOffset, AccountDirectory> change, CancellationToken token);
    /// <summary>*Link this Windows login* to the active account.</summary>
    Task LinkWindowsLoginAsync(CancellationToken token);
    /// <summary>*Unlink*: the active account keeps signing in here with its Martlet password (after a Prove sign-in); returns what
    /// happened, to show.</summary>
    Task<string> UnlinkWindowsLoginAsync(System.Windows.Window owner, CancellationToken token);
    /// <summary>Locks the active account (encrypting its files when that is on) and shows Unlock.</summary>
    Task LockNowAsync();
    /// <summary>Signs <paramref name="accountId"/> out of this PC: removes this PC's binding and its unlock file.</summary>
    Task SignOutAsync(Guid accountId);
    /// <summary>The steps that move another account's data into one (<see cref="IAccountMergeStep"/>), in order.</summary>
    IReadOnlyList<IAccountMergeStep> MergeSteps { get; }
}

/// <summary>
/// One kind of an account's data that *Merge another account into this one* moves (docs/ACCOUNTS.md, "Merging accounts"):
/// characters and account settings, memories, conversation history, creations, reminders, voice links. Each kind's owner
/// moves <c>from</c>'s data into <c>into</c> through its own store and sync, so every host ends with one account. A step
/// that finds nothing to move does nothing; a step may run again after a failure.
/// </summary>
internal interface IAccountMergeStep
{
    string Name { get; }
    Task MergeAsync(Guid into, Guid from, CancellationToken token);
}

/// <summary>The merge step for the files of the merged account's folder on this PC (<c>&lt;data&gt;\accounts\&lt;32 hex&gt;\</c>):
/// each file the kept account's folder lacks is copied there; a file both have stays the kept account's. Encrypted files
/// (<c>.mlock</c>) can't be merged until the merged account is unlocked here once.</summary>
internal sealed class AccountFolderMergeStep(string dataDirectory) : IAccountMergeStep
{
    public string Name => "files on this PC";

    public Task MergeAsync(Guid into, Guid from, CancellationToken token) => Task.Run(() =>
    {
        var source = AccountFolder(dataDirectory, from);
        var copied = Copy(source, AccountFolder(dataDirectory, into), token);
        if (copied > 0) ErrorLog.Info($"Accounts: copied {copied} file(s) of account {from:N} into account {into:N} for the merge.");
    }, token);

    internal static string AccountFolder(string dataDirectory, Guid id) => System.IO.Path.Combine(dataDirectory, "accounts", id.ToString("N"));

    internal static int Copy(string source, string target, CancellationToken token = default)
    {
        if (!System.IO.Directory.Exists(source)) return 0;
        var files = System.IO.Directory.EnumerateFiles(source, "*", System.IO.SearchOption.AllDirectories).ToArray();
        if (files.Any(f => f.EndsWith(AccountVault.SealedExtension, StringComparison.Ordinal)))
            throw new InvalidOperationException("The other account's files on this PC are encrypted. Switch to it once to unlock them, then merge.");
        var copied = 0;
        foreach (var file in files)
        {
            token.ThrowIfCancellationRequested();
            var destination = System.IO.Path.Combine(target, System.IO.Path.GetRelativePath(source, file));
            if (System.IO.File.Exists(destination)) continue;
            System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(destination)!);
            System.IO.File.Copy(file, destination);
            copied++;
        }
        return copied;
    }
}
