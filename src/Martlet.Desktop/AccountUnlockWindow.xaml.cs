using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;

namespace Martlet.Desktop;

internal enum AccountUnlockOutcome { Canceled, Unlocked, ChooseAnother, SignInAsSomeoneElse }

/// <summary>Unlock (docs/ACCOUNTS.md, Logins): switches to an account already signed in on this PC with its PIN, Windows Hello or
/// its Martlet password (the verifier this PC keeps), offline. Shown before the account becomes active, never during a reply.
/// <see cref="FileKey"/> is the key of the account's encrypted files when it has them here.</summary>
public partial class AccountUnlockWindow : ThemedWindow
{
    private readonly Guid accountId;
    private readonly string accountName;
    private readonly AccountLockStore store;
    private AccountLock current;

    internal AccountUnlockOutcome Outcome { get; private set; } = AccountUnlockOutcome.Canceled;
    internal byte[]? FileKey { get; private set; }

    internal AccountUnlockWindow(Guid accountId, string accountName, AccountLockStore store, bool othersAllowed = true)
    {
        InitializeComponent();
        this.accountId = accountId;
        this.accountName = accountName;
        this.store = store;
        current = store.Load(accountId);
        AccountText.Text = $"Unlock {accountName}";
        PinPanel.Visibility = current.HasPin ? Visibility.Visible : Visibility.Collapsed;
        PasswordPanel.Visibility = current.HasPassword ? Visibility.Visible : Visibility.Collapsed;
        PasswordLabel.Text = current.HasPin ? "Or your Martlet password" : "Martlet password";
        HelloButton.Visibility = current.Hello && !current.Encrypt ? Visibility.Visible : Visibility.Collapsed;
        UnlockButton.Visibility = current.HasPin || current.HasPassword ? Visibility.Visible : Visibility.Collapsed;
        OtherButton.Visibility = SignInButton.Visibility = othersAllowed ? Visibility.Visible : Visibility.Collapsed;
        HintText.Text = current.Damaged
            ? $"Martlet can't read how {accountName} unlocks on this PC: sign in with the Martlet password through one of your hosts."
            : current.Methods.Count == 0
            ? $"Nothing unlocks {accountName} on this PC yet: sign in with the Martlet password through one of your hosts."
            : current.Encrypt
                ? $"{accountName}'s files on this PC are encrypted until you unlock."
                : $"Unlock to switch to {accountName}, who is signed in on this PC.";
        if (current.Methods.Count == 0) SignInButton.Content = $"_Sign in as {accountName}";
        Loaded += (_, _) =>
        {
            if (PinPanel.Visibility == Visibility.Visible) PinText.Focus();
            else if (PasswordPanel.Visibility == Visibility.Visible) PasswordText.Focus();
            if (current.RetryAfter is { } wait && wait > DateTimeOffset.UtcNow) StatusText.Text = WaitText(wait);
        };
    }

    private void Secret_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) Unlock_Click(sender, e);
    }

    private async void Unlock_Click(object sender, RoutedEventArgs e)
    {
        var usePin = PinText.Password.Length > 0 || PasswordText.Password.Length == 0 && current.HasPin;
        var method = usePin ? AccountUnlockMethod.Pin : AccountUnlockMethod.Password;
        var secret = usePin ? PinText.Password : PasswordText.Password;
        if (secret.Length == 0)
        {
            StatusText.Text = usePin ? "Type your PIN." : "Type your Martlet password.";
            return;
        }
        UnlockButton.IsEnabled = false;
        StatusText.Text = "Checking...";
        try
        {
            var result = await Task.Run(() => store.Unlock(accountId, method, secret));
            PinText.Clear();
            PasswordText.Clear();
            Show(result, usePin ? "PIN" : "password");
        }
        catch (Exception error) when (IsLockFileError(error))
        {
            ShowLockFileError(error);
        }
        finally { UnlockButton.IsEnabled = true; }
    }

    private async void Hello_Click(object sender, RoutedEventArgs e)
    {
        HelloButton.IsEnabled = false;
        StatusText.Text = "Waiting for Windows Hello...";
        try
        {
            var verified = await WindowsHello.VerifyAsync(new WindowInteropHelper(this).Handle, $"Unlock {accountName} in Martlet");
            if (verified != WindowsHelloResult.Verified)
            {
                StatusText.Text = WindowsHello.Describe(verified);
                return;
            }
            Show(await Task.Run(() => store.Unlock(accountId, AccountUnlockMethod.WindowsHello, null)), "Windows Hello");
        }
        catch (Exception error) when (IsLockFileError(error))
        {
            ShowLockFileError(error);
        }
        finally { HelloButton.IsEnabled = true; }
    }

    private static bool IsLockFileError(Exception error) =>
        error is System.IO.IOException or UnauthorizedAccessException or System.Security.Cryptography.CryptographicException;

    // The details (paths) go to the log only.
    private void ShowLockFileError(Exception error)
    {
        ErrorLog.Warn($"Account {accountId:N}: the unlock file on this PC could not be read or saved", error);
        StatusText.Text = $"Martlet couldn't read or save how {accountName} unlocks on this PC, maybe because another program has the file open: try again in a moment.";
    }

    private void Show(AccountUnlockResult result, string what)
    {
        current = store.Load(accountId);
        switch (result.Status)
        {
            case AccountUnlockStatus.Unlocked:
                FileKey = result.FileKey;
                Outcome = AccountUnlockOutcome.Unlocked;
                ErrorLog.Info($"Account {accountId:N} unlocked on this PC with {what}.");
                DialogResult = true;
                return;
            case AccountUnlockStatus.Wrong:
                StatusText.Text = $"That {what} is wrong.";
                break;
            case AccountUnlockStatus.Wait:
                StatusText.Text = WaitText(result.RetryAfter ?? DateTimeOffset.UtcNow);
                break;
            case AccountUnlockStatus.NeedsPin:
                StatusText.Text = "That password is right, but your encrypted files on this PC open only with your PIN.";
                break;
            default:
                StatusText.Text = $"No {what} is set up for {accountName} on this PC.";
                break;
        }
        ErrorLog.Info($"Account {accountId:N} not unlocked with {what}: {result.Status}.");
    }

    private static string WaitText(DateTimeOffset until) =>
        $"Too many wrong tries: try again after {until.ToLocalTime():t}.";

    private void Other_Click(object sender, RoutedEventArgs e)
    {
        Outcome = AccountUnlockOutcome.ChooseAnother;
        DialogResult = false;
    }

    private void SignIn_Click(object sender, RoutedEventArgs e)
    {
        Outcome = AccountUnlockOutcome.SignInAsSomeoneElse;
        DialogResult = false;
    }
}
