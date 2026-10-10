using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Interop;
using Martlet.Avatar.Audio2Face.Remote;
using Martlet.Core.Access;
using Martlet.Core.Accounts;

namespace Martlet.Desktop;

/// <summary>
/// The Account page (docs/ACCOUNTS.md, Flows): the active account's logins; a Martlet password and an optional authenticator,
/// kept on every host of this PC's network (W4's sign-in settings; the owner's account always has an authenticator); linking
/// and unlinking this Windows login; how the account unlocks on this PC (*Ask for my password on this PC*, a PIN, Windows
/// Hello, *Encrypt my files on this PC while locked*, *Remember me on this PC*, **Lock now**, **Sign out of this PC**); and
/// *Merge another account into this one*. Nothing secret is shown back or reaches MCP.
/// </summary>
public partial class AccountWindow : ThemedWindow
{
    private readonly IAccountPageHost host;
    private readonly CancellationTokenSource lifetime = new();
    private string? secret;
    private bool refreshing;

    internal AccountWindow(IAccountPageHost host)
    {
        InitializeComponent();
        this.host = host;
        Closed += (_, _) => lifetime.Cancel();
        Loaded += async (_, _) =>
        {
            Refresh();
            await ShowPasswordStateAsync();
            await ShowHelloAsync();
        };
    }

    private Account? Active => host.Active;
    private AccountLoginKey ThisWindowsLogin => AccountLoginKey.ForWindows(host.DeviceId, host.WindowsSid);

    private bool IsOwnersAccount(Account account, HostSignInSettings? settings = null) =>
        account.Id == (settings?.OwnerAccountId ?? (host.Roster is { } roster ? OwnerAccount.IdFor(roster.NetworkId) : Guid.Empty));

    private void Refresh()
    {
        refreshing = true;
        try { RefreshNow(); }
        finally { refreshing = false; }
    }

    private void RefreshNow()
    {
        var id = host.ActiveId;
        var account = Active;
        var locks = host.Locks.Load(id);
        var windows = !host.SignedInWithProve && (account is null || account.Login(ThisWindowsLogin) is not null);
        NameText.Text = $"{host.ActiveName} ({account?.Role ?? "not in your household's directory yet"})";
        LoginsText.Text = account is null
            ? "Logins: this Windows login. The account reaches your household's directory once this PC is in your Martlet network."
            : "Logins: " + Describe(account);
        PasswordSection.IsEnabled = WindowsSection.IsEnabled = MergeSection.IsEnabled = account is not null;
        LockStateText.Text = locks.NeedsUnlock(windows)
            ? "Martlet asks you to unlock this account on this PC."
            : "This Windows login unlocks this account by itself on this PC.";
        MethodsText.Text = locks.Methods.Count == 0
            ? "Unlocks on this PC with: nothing yet."
            : "Unlocks on this PC with: " + string.Join(", ", locks.Methods.Select(m => m switch { "pin" => "PIN", "hello" => "Windows Hello", _ => "password" })) + ".";
        WindowsStateText.Text = windows
            ? $"This Windows login ({host.WindowsKind}) signs in as {host.ActiveName}."
            : $"This Windows login ({host.WindowsKind}) doesn't sign in as {host.ActiveName} by itself.";
        LinkWindowsButton.IsEnabled = !windows;
        UnlinkWindowsButton.IsEnabled = windows;
        AskCheck.IsChecked = locks.AskOnThisPc;
        AskCheck.IsEnabled = windows;
        HelloCheck.IsChecked = locks.Hello;
        HelloCheck.IsEnabled = !locks.Encrypt;
        EncryptCheck.IsChecked = locks.Encrypt;
        EncryptText.Text = locks.Encrypt
            ? "Your files on this PC are encrypted whenever you switch to another account or close Martlet. Your PIN" + (locks.Methods.Contains("password") ? " or password" : "") + " opens them."
            : "Needs a PIN on this PC. Type it in the PIN box, then turn this on. Windows Hello can't open encrypted files.";
        RememberCheck.IsChecked = locks.Remember;
        RememberCheck.Visibility = host.SignedInWithProve ? Visibility.Visible : Visibility.Collapsed;
        PinRemoveButton.IsEnabled = locks.HasPin;
        if (account is not null && IsOwnersAccount(account))
        {
            AuthenticatorCheck.IsChecked = true;
            AuthenticatorCheck.IsEnabled = false;
            AuthenticatorRemoveButton.IsEnabled = false;
        }
        MergeChoice.ItemsSource = host.Directory.Live.Where(a => a.Id != id).OrderBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase)
            .Select(a => new MergeItem(a)).ToArray();
    }

    private string Describe(Account account)
    {
        var parts = new List<string>();
        var windows = account.Logins.Where(l => l.Kind == AccountLoginKinds.Windows).ToArray();
        if (windows.Length > 0)
            parts.Add(windows.Any(l => l.Key == ThisWindowsLogin)
                ? $"this Windows login{(windows.Length > 1 ? $" and {windows.Length - 1} more" : "")}"
                : $"{windows.Length} Windows login{(windows.Length == 1 ? "" : "s")} on other PCs");
        if (account.Logins.Any(l => l.Kind == AccountLoginKinds.Martlet)) parts.Add("a Martlet password");
        foreach (var provider in account.Logins.Where(l => l.Kind is not (AccountLoginKinds.Windows or AccountLoginKinds.Martlet)).GroupBy(l => l.Kind))
            parts.Add($"{provider.Count()} {provider.Key} login{(provider.Count() == 1 ? "" : "s")}");
        return parts.Count == 0 ? "none" : string.Join(", ", parts) + ".";
    }

    private async Task ShowPasswordStateAsync()
    {
        if (Active is not { } account) return;
        if (host.Hosts.Count == 0)
        {
            PasswordStateText.Text = "Your Martlet password is kept on your hosts. Pair a host of your network first.";
            PasswordSaveButton.IsEnabled = AuthenticatorRemoveButton.IsEnabled = false;
            return;
        }
        var set = 0;
        string? user = null;
        bool? authenticator = null;
        var reached = 0;
        foreach (var choice in host.Hosts)
        {
            try
            {
                using var connection = ClusterSync.Connect(choice.Host);
                var settings = await connection.ReadSignInSettingsAsync(lifetime.Token);
                reached++;
                if (IsOwnersAccount(account, settings))
                {
                    if (settings.OwnerUser is { } owner) { set++; user ??= owner; authenticator = true; }
                }
                else if (settings.Accounts.FirstOrDefault(a => a.AccountId == account.Id) is { } login)
                {
                    set++;
                    user ??= login.User;
                    authenticator = (authenticator ?? true) && login.HasAuthenticator;
                }
            }
            catch (OperationCanceledException) { return; }
            catch (Exception error) when (ClusterSync.IsHostFailure(error)) { }
        }
        if (user is not null && UserText.Text.Length == 0) UserText.Text = user;
        PasswordStateText.Text = set == 0
            ? $"No Martlet password yet ({reached} of {host.Hosts.Count} host{(host.Hosts.Count == 1 ? "" : "s")} checked). Add one to sign in on other PCs and to unlock here."
            : $"Password set on {set} of {host.Hosts.Count} host{(host.Hosts.Count == 1 ? "" : "s")}" +
              (authenticator == true ? ", with an authenticator." : ", without an authenticator.") +
              (reached < host.Hosts.Count ? $" {host.Hosts.Count - reached} didn't answer." : "");
        if (authenticator != true && !IsOwnersAccount(account)) AuthenticatorRemoveButton.IsEnabled = false;
    }

    private async Task ShowHelloAsync()
    {
        var availability = await WindowsHello.AvailabilityAsync(lifetime.Token);
        HelloText.Text = WindowsHello.Describe(availability) +
            " Windows Hello can't tell apart people who share this Windows login, so use it only if nobody else set up Windows Hello here.";
    }

    private void Authenticator_Changed(object sender, RoutedEventArgs e)
    {
        var on = AuthenticatorCheck.IsChecked == true;
        AuthenticatorPanel.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        if (on && secret is null && Active is { } account)
        {
            secret = Totp.NewSecret();
            var label = UserText.Text.Trim() is { Length: > 0 } user ? user : account.Name;
            SecretText.Text = $"Key: {secret}\n{Totp.Uri("Martlet", label, secret)}";
        }
    }

    private async void PasswordSave_Click(object sender, RoutedEventArgs e)
    {
        if (Active is not { } account) return;
        var user = UserText.Text.Trim().ToLowerInvariant();
        var password = PasswordText.Password;
        if (!AccountLoginKey.IsUserName(user)) { Status("A user name is up to 64 letters, digits, dots, dashes, underscores or @."); return; }
        if (password.Length < AccountLockStore.MinimumPasswordLength) { Status("Use a password of at least 12 characters."); return; }
        if (password != RepeatText.Password) { Status("The two passwords are not the same."); return; }
        var withAuthenticator = AuthenticatorCheck.IsChecked == true;
        if (withAuthenticator && CodeText.Text.Trim().Length == 0) { Status("Type the code your authenticator app shows."); return; }
        var owner = IsOwnersAccount(account);
        var change = owner
            ? new JsonObject { ["action"] = "owner", ["account_id"] = account.Id.ToString(), ["user"] = user, ["password"] = password,
                ["totp_secret"] = secret, ["code"] = CodeText.Text.Trim() }
            : new JsonObject { ["action"] = "account", ["account_id"] = account.Id.ToString(), ["user"] = user, ["password"] = password };
        if (!owner && withAuthenticator)
        {
            change["totp_secret"] = secret;
            change["code"] = CodeText.Text.Trim();
        }
        PasswordSaveButton.IsEnabled = false;
        var saved = new List<string>();
        var failed = new List<string>();
        var codes = new List<string>();
        try
        {
            foreach (var choice in host.Hosts)
            {
                Status($"Saving on {choice.Label}...");
                try
                {
                    using var connection = ClusterSync.Connect(choice.Host);
                    var settings = await connection.ChangeSignInSettingsAsync((JsonObject)change.DeepClone(), lifetime.Token);
                    saved.Add(choice.Label);
                    if (settings.RecoveryCodes is { Count: > 0 } made) codes.Add($"{choice.Label}:\n  " + string.Join("  ", made));
                }
                catch (OperationCanceledException) { return; }
                catch (Exception error) when (ClusterSync.IsHostFailure(error)) { failed.Add($"{choice.Label} ({error.Message})"); }
            }
            if (saved.Count > 0)
            {
                host.Locks.RememberPassword(account.Id, password, host.FileKey);
                var key = AccountLoginKey.ForPassword(user);
                try
                {
                    await host.ChangeDirectoryAsync((directory, signer, now) => directory.Find(account.Id) is { Removed: false } current
                        ? directory.Put(signer, (current with { Logins = current.Logins.Where(l => l.Kind != AccountLoginKinds.Martlet || l.Key == key).ToArray() })
                            .WithLogin(AccountLogin.For(key, user, now)), now)
                        : directory, lifetime.Token);
                }
                catch (InvalidOperationException error) { failed.Add("your household's directory (" + error.Message + ")"); }
                PasswordText.Clear();
                RepeatText.Clear();
                CodeText.Clear();
                ErrorLog.Info($"Account {account.Key}: Martlet password saved on {saved.Count} host(s){(withAuthenticator ? " with an authenticator" : "")}.");
            }
            RecoveryText.Text = codes.Count == 0 ? "" : "Recovery codes, shown once. Each works once if you lose your phone; keep them somewhere safe.\n" +
                string.Join("\n", codes);
            RecoveryText.Visibility = codes.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
            Status((saved.Count > 0 ? $"Password saved on {string.Join(", ", saved)}; this PC unlocks you with it now." : "The password was not saved.") +
                (failed.Count > 0 ? " Not saved on: " + string.Join("; ", failed) + "." : ""));
            Refresh();
            await ShowPasswordStateAsync();
        }
        finally { PasswordSaveButton.IsEnabled = true; }
    }

    private async void AuthenticatorRemove_Click(object sender, RoutedEventArgs e)
    {
        if (Active is not { } account || IsOwnersAccount(account)) return;
        var removed = 0;
        foreach (var choice in host.Hosts)
        {
            try
            {
                using var connection = ClusterSync.Connect(choice.Host);
                await connection.ChangeSignInSettingsAsync(new JsonObject { ["action"] = "remove-account-authenticator", ["account_id"] = account.Id.ToString() },
                    lifetime.Token);
                removed++;
            }
            catch (OperationCanceledException) { return; }
            catch (Exception error) when (ClusterSync.IsHostFailure(error)) { }
        }
        Status($"Authenticator removed on {removed} of {host.Hosts.Count} host(s).");
        await ShowPasswordStateAsync();
    }

    private async void LinkWindows_Click(object sender, RoutedEventArgs e)
    {
        if (Active is not { } account) return;
        try
        {
            await host.LinkWindowsLoginAsync(lifetime.Token);
            Status($"This Windows login now signs in as {account.Name}.");
            Refresh();
        }
        catch (InvalidOperationException error) { Status(error.Message); }
    }

    private async void UnlinkWindows_Click(object sender, RoutedEventArgs e)
    {
        if (Active is null) return;
        try
        {
            Status(await host.UnlinkWindowsLoginAsync(this, lifetime.Token));
            Refresh();
        }
        catch (InvalidOperationException error) { Status(error.Message); }
    }

    private void Ask_Click(object sender, RoutedEventArgs e)
    {
        if (refreshing) return;
        var accountId = host.ActiveId;
        try
        {
            host.Locks.Change(accountId, l => l with { AskOnThisPc = AskCheck.IsChecked == true });
            Status(AskCheck.IsChecked == true ? "This PC asks to unlock you from now on." : "This Windows login unlocks you by itself again.");
        }
        catch (InvalidOperationException error) { Status(error.Message); }
        Refresh();
    }

    private void PinSet_Click(object sender, RoutedEventArgs e)
    {
        var accountId = host.ActiveId;
        if (!AccountLockStore.IsPin(PinText.Password)) { Status("A PIN is 4 to 12 digits."); return; }
        try
        {
            host.Locks.SetPin(accountId, PinText.Password, host.FileKey);
            PinText.Clear();
            Status("PIN set for this PC.");
        }
        catch (Exception error) when (error is InvalidOperationException or ArgumentException) { Status(error.Message); }
        Refresh();
    }

    private void PinRemove_Click(object sender, RoutedEventArgs e)
    {
        var accountId = host.ActiveId;
        try
        {
            host.Locks.RemovePin(accountId);
            Status("PIN removed from this PC.");
        }
        catch (InvalidOperationException error) { Status(error.Message); }
        Refresh();
    }

    private async void Hello_Click(object sender, RoutedEventArgs e)
    {
        if (refreshing) return;
        var accountId = host.ActiveId;
        try
        {
            if (HelloCheck.IsChecked == true)
            {
                var availability = await WindowsHello.AvailabilityAsync(lifetime.Token);
                if (availability != WindowsHelloAvailability.Available)
                {
                    Status(WindowsHello.Describe(availability));
                    Refresh();
                    return;
                }
                var verified = await WindowsHello.VerifyAsync(new WindowInteropHelper(this).Handle, $"Use Windows Hello to unlock {host.ActiveName} in Martlet");
                if (verified != WindowsHelloResult.Verified)
                {
                    Status(WindowsHello.Describe(verified));
                    Refresh();
                    return;
                }
            }
            host.Locks.SetHello(accountId, HelloCheck.IsChecked == true);
            Status(HelloCheck.IsChecked == true ? "Windows Hello unlocks you on this PC." : "Windows Hello no longer unlocks you here.");
        }
        catch (InvalidOperationException error) { Status(error.Message); }
        Refresh();
    }

    private void Encrypt_Click(object sender, RoutedEventArgs e)
    {
        if (refreshing) return;
        var accountId = host.ActiveId;
        try
        {
            if (EncryptCheck.IsChecked == true)
            {
                if (!host.Locks.Load(accountId).HasPin) throw new InvalidOperationException("Set a PIN first: it opens your encrypted files.");
                var (_, key) = host.Locks.TurnOnEncryption(accountId, PinText.Password,
                    PasswordText.Password.Length > 0 ? PasswordText.Password : null);
                host.FileKey = key;
                PinText.Clear();
                PasswordText.Clear();
                Status("Your files on this PC are encrypted whenever this account stops being active here: when you switch to another account or close Martlet.");
            }
            else
            {
                host.Locks.TurnOffEncryption(accountId);
                host.FileKey = null;
                Status("Your files on this PC are no longer encrypted when you lock.");
            }
        }
        catch (InvalidOperationException error) { Status(error.Message); }
        Refresh();
    }

    private void Remember_Click(object sender, RoutedEventArgs e)
    {
        if (refreshing) return;
        var accountId = host.ActiveId;
        host.Locks.Change(accountId, l => l with { Remember = RememberCheck.IsChecked == true });
        Status(RememberCheck.IsChecked == true ? "This PC remembers you." : "You are signed out of this PC when Martlet closes.");
        Refresh();
    }

    private async void LockNow_Click(object sender, RoutedEventArgs e)
    {
        var accountId = host.ActiveId;
        if (host.Locks.Load(accountId).Methods.Count == 0)
        {
            Status("Set a PIN, Windows Hello or a password first, so you can unlock again.");
            return;
        }
        Close();
        await host.LockNowAsync();
    }

    private async void SignOut_Click(object sender, RoutedEventArgs e)
    {
        if (Active is not { } account) return;
        if (!account.Logins.Any(l => l.Kind != AccountLoginKinds.Windows))
        {
            Status("Add a Martlet password first, so you can sign in again later.");
            return;
        }
        Close();
        await host.SignOutAsync(account.Id);
    }

    private async void Merge_Click(object sender, RoutedEventArgs e)
    {
        if (Active is not { } account || MergeChoice.SelectedItem is not MergeItem { Account: var other }) { Status("Choose the account to merge into this one."); return; }
        if (other.Role == AccountRoles.Owner) { Status($"{other.Name} is the household owner's account. Merge this account into it instead (sign in as {other.Name})."); return; }
        // Prove both: the other account always, this one too when it has a Martlet password.
        var proveOther = new AccountProveWindow(host.Hosts, host.DataDirectory, $"Sign in as {other.Name}",
            $"To merge {other.Name} into {account.Name}, sign in as {other.Name}.", other.Id, offerRemember: false) { Owner = this };
        if (proveOther.ShowDialog() != true) return;
        proveOther.Forget();
        if (account.Logins.Any(l => l.Kind == AccountLoginKinds.Martlet))
        {
            var proveThis = new AccountProveWindow(host.Hosts, host.DataDirectory, $"Sign in as {account.Name}",
                $"Now sign in as {account.Name}, the account you keep.", account.Id, offerRemember: false) { Owner = this };
            if (proveThis.ShowDialog() != true) return;
            proveThis.Forget();
        }
        MergeButton.IsEnabled = false;
        try
        {
            foreach (var step in host.MergeSteps)
            {
                Status($"Moving {other.Name}'s {step.Name}...");
                await step.MergeAsync(account.Id, other.Id, lifetime.Token);
            }
            await host.ChangeDirectoryAsync((directory, signer, now) => AccountLinks.Merge(directory, signer, account.Id, other.Id, now), lifetime.Token);
            host.Locks.Delete(other.Id);
            ErrorLog.Info($"Account {other.Key} merged into {account.Key} on this PC ({host.MergeSteps.Count} data step(s)).");
            Status($"{other.Name} is merged into {account.Name}.");
            Refresh();
        }
        catch (OperationCanceledException) { }
        catch (Exception error) when (error is InvalidOperationException || ClusterSync.IsHostFailure(error))
        {
            Status($"The merge stopped: {error.Message} Nothing was removed; try again.");
        }
        finally { MergeButton.IsEnabled = true; }
    }

    private void Status(string text) => StatusText.Text = text;

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    /// <summary>An account in the merge list, read by its name (screen readers and MCP's ui_select).</summary>
    private sealed record MergeItem(Account Account)
    {
        public override string ToString() => Account.Name;
    }
}
