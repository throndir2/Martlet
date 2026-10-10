using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Threading;
using Martlet.Core.Accounts;
using Martlet.Core.Network;

namespace Martlet.Desktop;

/// <summary>Account security in the main window (docs/ACCOUNTS.md, Account security): the account menu's **Account…**, **Sign
/// in as someone else…** and **Lock**; the Unlock before a switch to an account that needs it; encrypting the files of an account
/// that stopped being active here; signing an account out of this PC; and following merged accounts. Everything runs at a switch
/// or from the account menu, never during a reply.</summary>
public partial class MainWindow
{
    private AccountSecurityService? security;
    private Guid? securedAccount;

    private void InitializeAccountSecurity()
    {
        if (accounts is null || store is null) return;
        security = AccountSecurityService.Current is { } opened && opened.Session == accounts ? opened : new AccountSecurityService(accounts, store.DataDirectory);
        AccountSecurityService.Current ??= security;
        securedAccount = accounts.AccountId;
        accounts.AccountChanged += () =>
        {
            var from = securedAccount;
            securedAccount = accounts.AccountId;
            // The previous account's files were released by the change steps; encrypt them off the window's thread.
            if (from is { } previous && previous != accounts.AccountId && security is { } service) Task.Run(() => service.Seal(previous));
        };
        accounts.Changed += () => Dispatcher.BeginInvoke(FollowMerges, DispatcherPriority.Background);
    }

    /// <summary>The account menu's account security items, after its separator.</summary>
    private void AddAccountSecurityItems(ContextMenu menu)
    {
        if (security is null || accounts is null) return;
        Add("_Account…", "AccountOpen", true, OpenAccountPage);
        Add("Sign in as _someone else…", "AccountSignInOther", AccountSwitchBlocked() is null, () => SignInAsSomeoneElseAsync().Forget());
        Add("_Lock", "AccountLock", security.Locks.Load(accounts.AccountId).Methods.Count > 0, () => LockNowAsync().Forget());

        void Add(string header, string id, bool enabled, Action action)
        {
            var item = new MenuItem { Header = header, IsEnabled = enabled };
            AutomationProperties.SetAutomationId(item, id);
            item.Click += (_, _) =>
            {
                menu.IsOpen = false;
                Dispatcher.InvokeAsync(action, DispatcherPriority.Background);
            };
            menu.Items.Add(item);
        }
    }

    /// <summary>The Unlock of <paramref name="id"/> before a switch to it; false: no switch now (what the person chose instead
    /// runs next).</summary>
    private bool UnlockBeforeSwitch(Guid id)
    {
        if (security is null) return true;
        if (security.Unlock(id, this, out var outcome)) return true;
        if (outcome == AccountUnlockOutcome.SignInAsSomeoneElse)
        {
            var expected = security.Locks.Load(id).Methods.Count == 0 ? id : (Guid?)null;
            Dispatcher.InvokeAsync(() => SignInAsSomeoneElseAsync(expected).Forget(), DispatcherPriority.Background);
        }
        else if (outcome == AccountUnlockOutcome.ChooseAnother) Dispatcher.InvokeAsync(() => ChooseAccountAsync(id).Forget(), DispatcherPriority.Background);
        return false;
    }

    private async Task SignInAsSomeoneElseAsync(Guid? expected = null)
    {
        if (security is null || accounts is null || closing) return;
        if (security.SignInAsSomeoneElse(this, expected) is not { } id) return;
        QueueAccountsSync();
        RenderAccount();
        if (id != accounts.AccountId) await SwitchAccountAsync(id);
    }

    private async Task ChooseAccountAsync(Guid except)
    {
        if (security is null || accounts is null || closing) return;
        if (security.Choose(this, except) is { } chosen && chosen != accounts.AccountId) await SwitchAccountAsync(chosen);
    }

    /// <summary>**Lock**: the conversation ends and the Unlock window stays over Martlet until the account is unlocked or another
    /// one is in use.</summary>
    private async Task LockNowAsync()
    {
        if (security is null || accounts is null || closing) return;
        var id = accounts.AccountId;
        openConversation?.End();
        ErrorLog.Info($"Accounts: account {AccountSession.Short(id)} locked on this PC.");
        while (!closing && accounts.AccountId == id)
        {
            var window = new AccountUnlockWindow(id, accounts.Current.Name, security.Locks) { Owner = this };
            window.ShowDialog();
            switch (window.Outcome)
            {
                case AccountUnlockOutcome.Unlocked:
                    if (window.FileKey is { } key) security.SetFileKey(id, key);
                    ActionText.Text = $"Welcome back, {accounts.Current.Name}.";
                    return;
                case AccountUnlockOutcome.ChooseAnother:
                    if (security.Choose(this, id) is { } other && other != id) await SwitchAccountAsync(other);
                    break;
                case AccountUnlockOutcome.SignInAsSomeoneElse:
                    if (security.SignInAsSomeoneElse(this) is { } signed && signed != id)
                    {
                        QueueAccountsSync();
                        await SwitchAccountAsync(signed);
                    }
                    break;
            }
        }
    }

    private void OpenAccountPage()
    {
        if (security is null || accounts is null || closing) return;
        new AccountWindow(new AccountPage(this)) { Owner = this }.ShowDialog();
        RenderAccount();
    }

    /// <summary>*Sign out of this PC*: switches to another account first when <paramref name="id"/> is in use.</summary>
    private async Task SignOutAccountAsync(Guid id)
    {
        if (security is null || accounts is null || closing) return;
        if (id == accounts.AccountId)
        {
            var other = accounts.SignedIn.FirstOrDefault(a => a.Id != id && security.UnlocksWithWindows(a.Id)) ?? accounts.SignedIn.FirstOrDefault(a => a.Id != id);
            if (other is null)
            {
                ActionText.Text = "You are the only account on this PC. Add a person or sign in as someone else first.";
                return;
            }
            await SwitchAccountAsync(other.Id);
            if (accounts.AccountId == id) return;
        }
        security.SignOut(id);
        QueueAccountsSync();
        RenderAccount();
        ActionText.Text = "Signed out of this PC.";
    }

    /// <summary>Accounts signed in here that were merged into another or removed leave this PC; when the account in use was
    /// merged into one signed in here, Martlet switches to it.</summary>
    private void FollowMerges()
    {
        if (security is null || accounts is null || closing || accounts.Switching) return;
        foreach (var gone in accounts.SignedIn.Where(a => accounts.Directory.Find(a.Id) is { Removed: true }).ToArray())
        {
            if (gone.Id != accounts.AccountId)
            {
                security.SignOut(gone.Id);
                continue;
            }
            var into = accounts.Directory.Find(gone.Id)!.MergedInto;
            if (into is { } kept && accounts.SignedIn.Any(a => a.Id == kept) && AccountSwitchBlocked() is null)
                SwitchAccountAsync(kept).Forget();
            else if (ActionText is not null)
                ActionText.Text = into is null ? $"{gone.Name}'s account was removed. Choose another account." : $"{gone.Name}'s account was merged into another one. Sign in as it.";
        }
    }

    /// <summary>The Account page's view of the session (<see cref="IAccountPageHost"/>).</summary>
    private sealed class AccountPage(MainWindow main) : IAccountPageHost
    {
        private AccountSecurityService Security => main.security!;
        private AccountSession Session => main.accounts!;

        public string DataDirectory => Security.DataDirectory;
        public string DeviceId => Session.DeviceId;
        public string WindowsSid => Session.WindowsSid;
        public string WindowsKind => string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(AccountSession.SimulatedLoginVariable))
            ? WindowsLogin.Current.Kind : "simulated";
        public Account? Active => Session.Directory.Find(Session.AccountId) is { Removed: false } account ? account : null;
        public bool SignedInWithProve => !Security.UnlocksWithWindows(Session.AccountId);
        public AccountDirectory Directory => Session.Directory;
        public NetworkRoster? Roster => Security.Roster;
        public IReadOnlyList<ProveHost> Hosts => Security.Hosts();
        public AccountLockStore Locks => Security.Locks;

        public byte[]? FileKey
        {
            get => Security.FileKey(Session.AccountId);
            set => Security.SetFileKey(Session.AccountId, value);
        }

        public Task<AccountDirectory> ChangeDirectoryAsync(Func<AccountDirectory, INetworkSigner, DateTimeOffset, AccountDirectory> change, CancellationToken token)
        {
            var changed = Security.ChangeDirectory(change);
            main.QueueAccountsSync();
            return Task.FromResult(changed);
        }

        public Task LinkWindowsLoginAsync(CancellationToken token)
        {
            Security.LinkWindowsLogin();
            main.QueueAccountsSync();
            return Task.CompletedTask;
        }

        public Task<string> UnlinkWindowsLoginAsync(Window owner, CancellationToken token)
        {
            var done = Security.UnlinkWindowsLogin(owner);
            main.QueueAccountsSync();
            return Task.FromResult(done);
        }

        public Task LockNowAsync() => main.LockNowAsync();
        public Task SignOutAsync(Guid accountId) => main.SignOutAccountAsync(accountId);
        public IReadOnlyList<IAccountMergeStep> MergeSteps => [new AccountFolderMergeStep(DataDirectory)];
    }
}
