using System.IO;
using Martlet.Core.Lorebooks;
using Martlet.Core.Sync;

namespace Martlet.Desktop;

/// <summary>Each account's settings at start (docs/ACCOUNTS.md, "Account settings"). This runs as an account change step before
/// the window, the theme and the character load, on a thread the starting thread waits for, so it only works with files: on a
/// data folder from before accounts, the settings files belong to the first account that signs in here. That account's own copy
/// starts from the account entries of this folder's shared settings (<see cref="AccountWorkingCopy.Seed"/>), its lorebooks move
/// to its folder (the old file stays as it was), and the working-copy marker names it. When the marker names another account
/// (a switch cut short), the main window finishes the switch before the conversation starts.</summary>
internal static class AccountSettingsStart
{
    internal static void Register(AccountSession accounts) =>
        accounts.AddChangeStep((change, _) =>
        {
            if (change.From is null) Prepare(change.HouseholdFolder, change.To, DateTimeOffset.UtcNow);
            return Task.CompletedTask;
        });

    /// <summary>Gives the files of a data folder from before accounts to <paramref name="account"/>. Does nothing once an account
    /// has them. Returns whether it did.</summary>
    internal static bool Prepare(string dataDirectory, Guid account, DateTimeOffset now)
    {
        if (AccountWorkingCopy.Load(dataDirectory) is not null) return false;
        var folder = AccountWorkingCopy.Folder(dataDirectory, account);
        Directory.CreateDirectory(folder);
        var seeded = AccountWorkingCopy.Seed(dataDirectory, folder);
        var lore = Path.Combine(dataDirectory, LorebookStore.FileName);
        var ownLore = Path.Combine(folder, LorebookStore.FileName);
        var moved = File.Exists(lore) && !File.Exists(ownLore);
        if (moved) File.Copy(lore, ownLore);
        AccountWorkingCopy.Save(dataDirectory, account, now);
        ErrorLog.Info($"Accounts: this PC's settings are now account {AccountSession.Short(account)}'s" +
            (seeded ? ", with what this PC shared before" : "") + (moved ? "; its lorebooks moved to its folder." : "."));
        return true;
    }
}
