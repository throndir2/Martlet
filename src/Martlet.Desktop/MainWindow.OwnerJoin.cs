using System.IO;
using Martlet.Core.Accounts;
using Martlet.Core.Network;

namespace Martlet.Desktop;

/// <summary>Joining with the owner's approval (docs/ACCOUNTS.md, "Joining with the owner's approval"): a fresh install makes its own
/// owner account before it joins. Once the directory sync finds the household's owner account, this PC signs in to it with its
/// Windows login, switches to it between replies and folds its own account into it with the merge steps, so the owner's settings
/// and characters are here. Windows logins with the same name are one person (<see cref="SamePerson"/>): an account this PC
/// made with the same name as another account of the household folds into that one the same way. Never on a reply's path.</summary>
public partial class MainWindow
{
    private bool ownerFoldBusy;
    private DateTimeOffset ownerFoldRetryAt;

    /// <summary>The plan for this PC now, or null (<see cref="OwnerJoin.Plan"/>, then <see cref="SamePerson.Plan"/>: Windows logins
    /// with the same name are one person).</summary>
    private OwnerJoinPlan? OwnerJoinPlanFor(AccountDirectory directory, NetworkRoster roster) =>
        accounts is null || store is null ? null
        : OwnerJoin.Plan(directory, accounts.State, roster, accounts.DeviceId, JoinedAccount.Load(store.DataDirectory) is not null)
            ?? SamePerson.Plan(directory, accounts.State, roster, accounts.DeviceId);

    private async Task FoldIntoHouseholdOwnerAsync()
    {
        if (accounts is null || security is null || store is null || closing || ownerFoldBusy) return;
        if (networkState.Roster is not { } roster || OwnerJoinPlanFor(accounts.Directory, roster) is not { } plan) return;
        if (!accounts.State.SignedIn.Contains(plan.Owner) || accounts.Directory.Find(plan.Owner)?.Device(accounts.DeviceId) is null) return;
        if (DateTimeOffset.UtcNow < ownerFoldRetryAt || AccountSwitchBlocked() is not null) return;
        ownerFoldBusy = true;
        var owner = accounts.Directory.Find(plan.Owner)!;
        try
        {
            if (accounts.AccountId != plan.Owner)
            {
                await SwitchAccountAsync(plan.Owner);
                if (closing || accounts.AccountId != plan.Owner) return;
            }
            // An account that never reached the directory has nothing on the hosts: only its files here move.
            IReadOnlyList<IAccountMergeStep> steps = plan.OwnWritten ? new AccountPage(this).MergeSteps : [new AccountFolderMergeStep(store.DataDirectory)];
            foreach (var step in steps) await step.MergeAsync(plan.Owner, plan.Own, lifetime.Token);
            if (plan.OwnWritten)
                security.ChangeDirectory((directory, key, now) => AccountLinks.Merge(directory, key, plan.Owner, plan.Own, now, foldOwner: true));
            else
                accounts.SignOut(plan.Own);
            security.Locks.Delete(plan.Own);
            QueueAccountsSync();
            RenderAccount();
            ErrorLog.Info($"Accounts: folded this PC's own account {AccountSession.Short(plan.Own)} into {(plan.SameName ? "the account of the same name" : "the household's owner account")} " +
                $"{AccountSession.Short(plan.Owner)} ({steps.Count} data step(s)).");
            ActionText.Text = plan.SameName
                ? $"This PC now uses {owner.Name}'s account from your other computers (the same name, so the same person): your settings and characters are together."
                : $"This PC now uses {owner.Name}'s account, the household owner's, so your settings and characters are here.";
        }
        catch (OperationCanceledException) { }
        catch (Exception error) when (error is InvalidOperationException or IOException or UnauthorizedAccessException or
            Martlet.Core.Contracts.ContractException || ClusterSync.IsHostFailure(error))
        {
            ownerFoldRetryAt = DateTimeOffset.UtcNow.AddMinutes(5);
            ErrorLog.Warn($"Accounts: couldn't fold this PC's own account {AccountSession.Short(plan.Own)} into " +
                $"{(plan.SameName ? "the account of the same name" : "the household's owner account")} yet; trying again in 5 minutes", error);
        }
        finally { ownerFoldBusy = false; }
    }
}
