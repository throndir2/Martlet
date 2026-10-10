using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;
using Martlet.Avatar.Audio2Face.Remote;
using Martlet.Core.Accounts;
using Martlet.Core.Contracts;

namespace Martlet.Desktop;

/// <summary>Accounts on this PC (docs/ACCOUNTS.md): the account button at the bottom of the navigation rail shows who uses
/// Martlet now and opens the account picker (the accounts signed in on this Windows login, and Add a person). Switching ends the
/// conversation and happens only between replies. The household's account directory is kept the same on every host: every 30
/// seconds while this PC is in a Martlet network (whether or not "Keep Martlet the same on all my computers" is on) it reads
/// each host's copy when it changed, writes this PC's new accounts and Windows login, and gives hosts with an older copy the
/// merged one. It never runs during a reply.</summary>
public partial class MainWindow
{
    private readonly AccountSession? accounts;
    private readonly DispatcherTimer accountsTimer = new() { Interval = TimeSpan.FromSeconds(30) };
    private bool accountsSyncBusy, accountsSyncQueued;
    /// <summary>Each host's copy as last read or merged, with its digest, so a copy is read again only when it changed.</summary>
    private readonly Dictionary<string, (string Digest, AccountDirectory Copy)> accountCopies = new(StringComparer.Ordinal);
    private string accountsSyncStatus = "Accounts: not synced yet.";

    /// <summary>The session App opened at start, or (in tests, without App) one opened here.</summary>
    private static AccountSession? OpenAccounts(Martlet.Core.Settings.SettingsStore? store)
    {
        if (store is null) return null;
        if (Application.Current is App { Accounts: { } opened } && opened.HouseholdFolder == store.DataDirectory) return opened;
        try { return AccountSession.OpenForThisLogin(store.DataDirectory); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ContractException or InvalidOperationException)
        {
            ErrorLog.Error("Accounts: couldn't open this PC's account session", error);
            return null;
        }
    }

    private void InitializeAccounts()
    {
        accountsTimer.Tick += (_, _) => SyncAccountsAsync().Forget();
        if (accounts is not null) accounts.Changed += () => Dispatcher.BeginInvoke(RenderAccount);
        RenderAccount();
        ShowAccountsSyncStatus();
    }

    private void StartAccounts()
    {
        if (accounts is null || closing) return;
        accountsTimer.Start();
        SyncAccountsAsync().Forget();
    }

    /// <summary>Syncs soon (debounced), so a person added here reaches the other computers quickly.</summary>
    private void QueueAccountsSync()
    {
        if (accountsSyncQueued || accounts is null || closing) return;
        accountsSyncQueued = true;
        SyncSoonAsync().Forget();

        async Task SyncSoonAsync()
        {
            try { await Task.Delay(TimeSpan.FromSeconds(2), lifetime.Token); }
            catch (OperationCanceledException) { return; }
            finally { accountsSyncQueued = false; }
            await SyncAccountsAsync();
        }
    }

    // ---------- the account button and picker ----------

    private void RenderAccount()
    {
        if (closing || AccountButton is null) return;
        if (accounts is null)
        {
            AccountButton.Visibility = Visibility.Collapsed;
            return;
        }
        var current = accounts.Current;
        var people = accounts.SignedIn.Count;
        AccountInitials.Text = current.Initials;
        AccountName.Text = current.Name;
        AccountRole.Text = current.RoleText + (people > 1 ? $" · {people} people on this PC" : "");
        AutomationProperties.SetName(AccountName, current.Name);
        AutomationProperties.SetName(AccountButton, $"Account: {current.Name}, {current.RoleText}" + (people > 1 ? $", {people} people on this PC" : ""));
    }

    /// <summary>Why an account switch must wait now, or null when it can happen.</summary>
    private string? AccountSwitchBlocked() =>
        accounts is null ? "Accounts aren't available on this PC."
        : conversation?.Replying == true ? "Martlet is replying. Switch accounts when it has finished."
        : accounts.Switching ? "Martlet is switching accounts."
        : model?.IsRunning == true || setupOperations.IsRunning || changes.Busy || settingsBusy || memorySyncBusy
            ? "Martlet is saving a change. Switch accounts in a moment." : null;

    private void AccountButton_Click(object sender, RoutedEventArgs e)
    {
        if (accounts is null || closing) return;
        var menu = new ContextMenu { PlacementTarget = AccountButton, Placement = PlacementMode.Top };
        AutomationProperties.SetAutomationId(menu, "AccountMenu");
        AutomationProperties.SetName(menu, "Accounts");
        var blocked = AccountSwitchBlocked();
        foreach (var account in accounts.SignedIn)
        {
            var current = account.Id == accounts.AccountId;
            var label = account.Name + (current ? " (in use)" : "");
            // A TextBlock header: a name with "_" isn't an access key.
            var item = new MenuItem
            {
                Header = new TextBlock { Text = label }, IsChecked = current, IsEnabled = !current && blocked is null,
                ToolTip = account.RoleText + (account.Pending ? ". It reaches your other computers once this PC is in your Martlet network." : "")
            };
            AutomationProperties.SetAutomationId(item, "AccountSwitch-" + account.Key);
            AutomationProperties.SetName(item, label);
            var id = account.Id;
            item.Click += (_, _) =>
            {
                menu.IsOpen = false;
                Dispatcher.InvokeAsync(() => SwitchAccountAsync(id).Forget(), DispatcherPriority.Background);
            };
            menu.Items.Add(item);
        }
        if (blocked is not null && accounts.SignedIn.Count > 1)
        {
            var note = new MenuItem { Header = new TextBlock { Text = blocked }, IsEnabled = false };
            AutomationProperties.SetAutomationId(note, "AccountMenuNote");
            AutomationProperties.SetName(note, blocked);
            menu.Items.Add(note);
        }
        menu.Items.Add(new Separator());
        var add = new MenuItem { Header = "_Add a person…" };
        AutomationProperties.SetAutomationId(add, "AccountAddPerson");
        add.Click += (_, _) =>
        {
            menu.IsOpen = false;
            // After the menu has closed: the dialog must not open inside the menu's click.
            Dispatcher.InvokeAsync(AddPerson, DispatcherPriority.Background);
        };
        menu.Items.Add(add);
        menu.IsOpen = true;
    }

    /// <summary>Switches to an account signed in on this PC, between replies only: the conversation ends (listening and watching
    /// stop), the change steps run, then Home shows the account.</summary>
    private async Task SwitchAccountAsync(Guid id)
    {
        if (accounts is null || closing || id == accounts.AccountId) return;
        if (AccountSwitchBlocked() is { } why)
        {
            ActionText.Text = why;
            return;
        }
        var to = accounts.SignedIn.FirstOrDefault(a => a.Id == id);
        if (to is null) return;
        var talking = openConversation is not null;
        openConversation?.End();
        AccountButton.IsEnabled = false;
        try { await accounts.SwitchToAsync(id, lifetime.Token); }
        catch (OperationCanceledException) { return; }
        catch (Exception error) when (!ErrorLog.IsFatal(error))
        {
            ErrorLog.Warn($"Accounts: couldn't switch to account {AccountSession.Short(id)}", error);
            if (!closing) ActionText.Text = $"Couldn't switch to {to.Name}: {error.Message}";
            return;
        }
        finally
        {
            if (!closing)
            {
                AccountButton.IsEnabled = true;
                RenderAccount();
            }
        }
        if (closing) return;
        ActionText.Text = $"Hi, {to.Name}. " + (talking
            ? "The conversation ended for the switch. Start talking or listening when you're ready."
            : "Martlet is yours now.");
        RenderListening();
        UpdateTray();
        await RefreshAsync();
    }

    private void AddPerson()
    {
        if (accounts is null || closing) return;
        var dialog = new AddPersonDialog(name =>
        {
            try { return (accounts.AddPerson(name, DateTimeOffset.UtcNow), null); }
            catch (ArgumentException error) { return (null, error.Message); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or ContractException)
            {
                ErrorLog.Warn("Accounts: couldn't add a person", error);
                return (null, "Martlet couldn't save the new account: " + error.Message);
            }
        }) { Owner = this };
        if (dialog.ShowDialog() != true || dialog.Added is not { } added) return;
        RenderAccount();
        QueueAccountsSync();
        if (AccountSwitchBlocked() is { } why)
        {
            ActionText.Text = $"Added {accounts.SignedIn.First(a => a.Id == added).Name}. {why}";
            return;
        }
        SwitchAccountAsync(added).Forget();
    }

    // ---------- the account directory sync ----------

    private async Task SyncAccountsAsync()
    {
        if (accounts is null || store is null || accountsSyncBusy || closing || accounts.Switching) return;
        // Never in a conversation's way: a reply or the owner talking hold the sync for the next check.
        if (conversation?.Replying == true || openConversation?.HearingYou == true) return;
        var roster = networkState.Roster;
        if (roster is null)
        {
            var here = accounts.SignedIn.Count;
            accountsSyncStatus = $"Accounts: {here} {(here == 1 ? "person" : "people")} on this PC. They reach your other computers once this PC is in a Martlet network.";
            ShowAccountsSyncStatus();
            return;
        }
        var hosts = NetworkMap.Hosts(Inputs());
        if (hosts.Count == 0)
        {
            accountsSyncStatus = "Accounts: pair a Martlet host to share them with your other computers.";
            ShowAccountsSyncStatus();
            return;
        }
        accountsSyncBusy = true;
        try
        {
            var reads = await Task.WhenAll(hosts.Select(ReadAccountsCopyAsync));
            if (closing || accounts.Switching) return;
            var merged = accounts.Directory;
            var refused = 0;
            foreach (var copy in reads.Select(r => r.Copy).OfType<AccountDirectory>())
            {
                var accepted = AccountDirectory.Accept(merged, copy, roster);
                merged = accepted.Directory;
                refused += accepted.Rejected;
            }
            IReadOnlyList<Guid> written = [];
            // This PC's new accounts and Windows login go in only after the household's copy was read, so a second owner is
            // never written.
            if (reads.Any(r => r.Ok))
            {
                using var key = NetworkIdentity.LoadOrCreate(store.DataDirectory, NetworkIdentity.DeviceId(homeHosts));
                (merged, written) = AccountSession.Reconcile(merged, accounts.State, roster, key, accounts.DeviceId, DateTimeOffset.UtcNow);
            }
            accounts.Follow(merged, written);
            if (written.Count > 0) ErrorLog.Info($"Accounts: wrote {written.Count} account{(written.Count == 1 ? "" : "s")} of this PC to the household's directory.");
            if (refused > 0) ErrorLog.Warn($"Accounts: refused {refused} account change{(refused == 1 ? "" : "s")} not signed by a computer of your network.");
            var digest = merged.Digest();
            foreach (var read in reads.Where(r => r.Ok))
            {
                if (closing) return;
                if (accountCopies.GetValueOrDefault(read.HostId).Digest == digest) continue;
                try
                {
                    var result = await ClusterSync.WithConnectionAsync(read.Host.Pairing, connection => connection.MergeAccountsAsync(merged, lifetime.Token));
                    accountCopies[read.HostId] = (result.Directory.Digest(), result.Directory);
                    if (result.Rejected > 0) ErrorLog.Warn($"Accounts: {read.HostId} refused {result.Rejected} account change{(result.Rejected == 1 ? "" : "s")} from this PC.");
                }
                catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { throw; }
                catch (Exception error) when (error is OperationCanceledException or ArgumentException || ClusterSync.IsHostFailure(error))
                {
                    ErrorLog.Warn($"Couldn't give {read.HostId} the household's accounts; trying again on the next check.", error);
                }
            }
            var people = merged.Live.Count();
            var pending = accounts.State.Pending.Count;
            var same = reads.Count(r => r.Ok && accountCopies.GetValueOrDefault(r.HostId).Digest == digest);
            var old = reads.Count(r => r.Old);
            var down = reads.Count(r => !r.Ok && !r.Old);
            accountsSyncStatus = $"Accounts: {people} {(people == 1 ? "person" : "people")} in your household, the same on {same} of {hosts.Count} " +
                $"host{(hosts.Count == 1 ? "" : "s")}; checked {DateTime.Now:t}." +
                (pending > 0 ? $" {pending} on this PC {(pending == 1 ? "waits" : "wait")} to reach your hosts." : "") +
                (down > 0 ? $" {down} not responding." : "") +
                (old > 0 ? $" Update {(old == 1 ? "one host" : old + " hosts")} to share accounts." : "");
        }
        catch (OperationCanceledException) { }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ContractException or InvalidOperationException)
        {
            accountsSyncStatus = "Accounts: couldn't sync with your other computers: " + error.Message;
            ErrorLog.Warn("Account directory sync failed.", error);
        }
        finally
        {
            accountsSyncBusy = false;
            if (!closing) ShowAccountsSyncStatus();
        }
    }

    /// <summary>A host's copy of the account directory: read again only when its digest changed. Hosts older than accounts answer
    /// request.invalid or route.not_found.</summary>
    private async Task<(string HostId, PairedHost Host, AccountDirectory? Copy, bool Ok, bool Old)> ReadAccountsCopyAsync(PairedHost host)
    {
        try
        {
            var copy = await ClusterSync.WithConnectionAsync(host.Pairing, async connection =>
            {
                var digest = await connection.ReadAccountsDigestAsync(lifetime.Token);
                if (accountCopies.TryGetValue(host.HostId, out var known) && known.Digest == digest) return known.Copy;
                var read = await connection.ReadAccountsAsync(lifetime.Token);
                accountCopies[host.HostId] = (read.Digest(), read);
                return read;
            });
            return (host.HostId, host, copy, true, false);
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { throw; }
        catch (Audio2FaceHostException error) when (error.Code is "request.invalid" or "request.not_found" or "route.not_found")
        {
            return (host.HostId, host, null, false, true);
        }
        catch (Exception error) when (error is OperationCanceledException or ArgumentException || ClusterSync.IsHostFailure(error))
        {
            return (host.HostId, host, null, false, false);
        }
    }

    private void ShowAccountsSyncStatus()
    {
        if (AccountsSyncStatusText is not null) AccountsSyncStatusText.Text = accountsSyncStatus;
    }
}
