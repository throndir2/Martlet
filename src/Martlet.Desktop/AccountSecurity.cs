using Martlet.Core.Accounts;
using Martlet.Core.Network;

namespace Martlet.Desktop;

/// <summary>What the Account page needs from the desktop's account session (docs/ACCOUNTS.md, "Desktop account session").</summary>
internal interface IAccountPageHost
{
    string DataDirectory { get; }
    string DeviceId { get; }
    WindowsLogin Windows { get; }
    /// <summary>The active account as this PC's account directory has it now.</summary>
    Account? Active { get; }
    AccountDirectory Directory { get; }
    NetworkRoster? Roster { get; }
    /// <summary>The hosts of this PC's own network it is paired with, to check sign-ins and keep password logins.</summary>
    IReadOnlyList<ProveHost> Hosts { get; }
    AccountLockStore Locks { get; }
    /// <summary>The active account's file key while it is unlocked and its files are encrypted here; null otherwise.</summary>
    byte[]? FileKey { get; set; }
    /// <summary>Changes this PC's account directory (signed by this PC's network key), saves it and syncs it to the hosts.</summary>
    Task<AccountDirectory> ChangeDirectoryAsync(Func<AccountDirectory, INetworkSigner, DateTimeOffset, AccountDirectory> change, CancellationToken token);
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
