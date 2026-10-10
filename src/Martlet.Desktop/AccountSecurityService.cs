using System.IO;
using System.Security.Cryptography;
using System.Windows;
using Martlet.Core.Accounts;
using Martlet.Core.Network;

namespace Martlet.Desktop;

/// <summary>
/// Account security on this PC for the account session (docs/ACCOUNTS.md, Account security): who needs an Unlock before
/// becoming active and showing it, decrypting an account's files at unlock and encrypting them when it stops being active,
/// *Sign in as someone else* and *Continue as ...?* (Prove sign-ins), checked changes to this PC's account directory, and what
/// happens when Martlet closes (*Remember me on this PC* off signs out; encrypted accounts are encrypted). Runs at start and at
/// switches, never during a reply. One per process (<see cref="Current"/>, set by App).
/// </summary>
internal sealed class AccountSecurityService(AccountSession session, string dataDirectory)
{
    private readonly Dictionary<Guid, byte[]> fileKeys = [];
    private readonly HashSet<Guid> justProved = [];

    internal static AccountSecurityService? Current { get; set; }

    internal AccountSession Session => session;
    internal string DataDirectory => dataDirectory;
    internal AccountLockStore Locks { get; } = new(dataDirectory);
    internal AccountLoginKey ThisWindowsLogin => AccountLoginKey.ForWindows(session.DeviceId, session.WindowsSid);
    internal NetworkRoster? Roster => NetworkIdentity.Load(dataDirectory).Roster;

    internal byte[]? FileKey(Guid id)
    {
        lock (fileKeys) return fileKeys.GetValueOrDefault(id);
    }

    internal void SetFileKey(Guid id, byte[]? key)
    {
        lock (fileKeys)
        {
            if (key is null) fileKeys.Remove(id);
            else fileKeys[id] = key;
        }
    }

    /// <summary>Whether this Windows login unlocks <paramref name="id"/> here: it wasn't signed in with a Prove sign-in (or was
    /// linked to this Windows login since), and the directory lists this Windows login among its logins (or doesn't have the
    /// account yet: a new account of this Windows login).</summary>
    internal bool UnlocksWithWindows(Guid id)
    {
        if (session.State.ProofFor(id) is { } proof && proof.Login != ThisWindowsLogin) return false;
        return session.Directory.Find(id) is not { } entry || entry.Login(ThisWindowsLogin) is not null;
    }

    internal bool NeedsUnlock(Guid id) => Locks.Load(id).NeedsUnlock(UnlocksWithWindows(id));

    /// <summary>The hosts of this PC's own network it is paired with (never a friend's), to check a Prove sign-in.</summary>
    internal IReadOnlyList<ProveHost> Hosts()
    {
        var roster = Roster;
        IReadOnlyList<PairedHost> paired;
        try { paired = HostRegistry.Load(dataDirectory); }
        catch (Exception error) when (error is InvalidDataException or IOException or UnauthorizedAccessException) { paired = []; }
        return AccountProveWindow.Hosts(paired.Where(h => !h.Shared && (roster is null || roster.Host(h.HostId) is { Removed: false }))
            .Select(h => h.Pairing), roster);
    }

    private string NameOf(Guid id) => session.SignedIn.FirstOrDefault(a => a.Id == id)?.Name ?? session.Directory.Find(id)?.Name ?? "this account";

    /// <summary>Unlocks <paramref name="id"/> before it becomes active, when it needs it (shows the Unlock window), and decrypts
    /// its files here. False: it stays locked; <paramref name="outcome"/> says what the person chose instead.</summary>
    internal bool Unlock(Guid id, Window? owner, out AccountUnlockOutcome outcome)
    {
        outcome = AccountUnlockOutcome.Unlocked;
        if (justProved.Remove(id) || !NeedsUnlock(id)) return Opened(id, owner);
        var window = new AccountUnlockWindow(id, NameOf(id), Locks) { Owner = owner };
        window.ShowDialog();
        outcome = window.Outcome;
        if (outcome != AccountUnlockOutcome.Unlocked) return false;
        if (window.FileKey is { } key) SetFileKey(id, key);
        return Opened(id, owner);
    }

    // Decrypts the account's files when they are encrypted here.
    private bool Opened(Guid id, Window? owner)
    {
        var folder = AccountSession.FolderFor(dataDirectory, id);
        if (AccountVault.Count(folder).Encrypted == 0) return true;
        if (FileKey(id) is not { } key)
        {
            ErrorLog.Warn($"Accounts: account {AccountSession.Short(id)}'s files here are encrypted, but no key opened them.");
            ShowProblem(owner, $"{NameOf(id)}'s files on this PC are encrypted. Unlock with your PIN to open them.");
            return false;
        }
        var result = AccountVault.Unseal(folder, key);
        ErrorLog.Info($"Accounts: decrypted {result.Done} file(s) of account {AccountSession.Short(id)}" +
            (result.Skipped.Count > 0 ? $"; {result.Skipped.Count} couldn't be decrypted." : "."));
        return true;
    }

    /// <summary>Encrypts <paramref name="id"/>'s files here when that is on and its key is known (it just stopped being active
    /// here), then forgets the key.</summary>
    internal AccountSealResult? Seal(Guid id)
    {
        byte[]? key;
        lock (fileKeys) fileKeys.Remove(id, out key);
        if (key is null) return null;
        if (!Locks.Load(id).Encrypt)
        {
            CryptographicOperations.ZeroMemory(key);
            return null;
        }
        try
        {
            var result = AccountVault.Seal(AccountSession.FolderFor(dataDirectory, id), key);
            ErrorLog.Info($"Accounts: encrypted {result.Done} file(s) of account {AccountSession.Short(id)}" +
                (result.Skipped.Count > 0 ? $"; {result.Skipped.Count} in use stayed plain." : "."));
            return result;
        }
        finally { CryptographicOperations.ZeroMemory(key); }
    }

    /// <summary>Changes this PC's account directory, signed by this PC's network key, after checking that the household's other
    /// computers would take it (the binding rule); saves it. The directory sync then gives it to the hosts.</summary>
    internal AccountDirectory ChangeDirectory(Func<AccountDirectory, INetworkSigner, DateTimeOffset, AccountDirectory> change)
    {
        var roster = Roster ?? throw new InvalidOperationException("This PC isn't in a Martlet network yet, so its accounts can't change here.");
        using var key = NetworkIdentity.LoadOrCreate(dataDirectory, session.DeviceId);
        var current = session.Directory;
        var next = change(current, key, DateTimeOffset.UtcNow);
        var accepted = AccountDirectory.Accept(current, next, roster);
        if (accepted.Rejected > 0)
            throw new InvalidOperationException("Your other computers would refuse this change: this PC hasn't proved that account. Sign in again first.");
        session.Follow(accepted.Directory, []);
        return accepted.Directory;
    }

    /// <summary>A Prove sign-in through one of this PC's hosts (the Prove window); null when nobody signed in. The caller forgets
    /// the password (<see cref="AccountProveWindow.Forget"/>) once it kept a verifier.</summary>
    private AccountProveWindow? Prove(Window? owner, Guid? expected, string? heading = null, string? hint = null, bool offerRemember = true)
    {
        var window = new AccountProveWindow(Hosts(), dataDirectory, heading ?? (expected is { } who ? $"Sign in as {NameOf(who)}" : null), hint, expected,
            offerRemember: offerRemember) { Owner = owner };
        return window.ShowDialog() == true && window.Proof is not null ? window : null;
    }

    /// <summary>*Link this Windows login* to the account in use: the directory lists it and this PC's binding uses it (keeping
    /// its proof), so this Windows login unlocks the account here from now on.</summary>
    internal void LinkWindowsLogin()
    {
        var id = session.AccountId;
        var now = DateTimeOffset.UtcNow;
        ChangeDirectory((directory, key, at) => directory.Find(id) is { Removed: false } account
            ? directory.Put(key, AccountLinks.WithWindowsLogin(account, session.DeviceId, session.WindowsSid, null, now), at)
            : throw new InvalidOperationException("This account isn't in your household's directory yet."));
        session.BindWith(id, ThisWindowsLogin);
    }

    /// <summary>*Unlink* this Windows login from the account in use: after a Prove sign-in with its Martlet password the account
    /// stays signed in here with that password (asked for at every Unlock). Returns what happened.</summary>
    internal string UnlinkWindowsLogin(Window owner)
    {
        var id = session.AccountId;
        var entry = session.Directory.Find(id) ?? throw new InvalidOperationException("This account isn't in your household's directory yet.");
        if (!entry.Logins.Any(l => l.Kind == AccountLoginKinds.Martlet))
            throw new InvalidOperationException("Add a Martlet password first, so this account can still sign in here without this Windows login.");
        var roster = Roster ?? throw new InvalidOperationException("This PC isn't in a Martlet network yet.");
        var window = Prove(owner, id, $"Sign in as {entry.Name}", $"Sign in with {entry.Name}'s Martlet password to stay signed in on this PC without this Windows login.",
            offerRemember: false);
        if (window?.Proof is not { } proof) return "Nothing changed.";
        try
        {
            var now = DateTimeOffset.UtcNow;
            ChangeDirectory((directory, key, at) => directory.Find(id) is { Removed: false } account
                ? directory.Put(key, AccountLinks.SignedIn(AccountLinks.WithoutWindowsLogin(account, session.DeviceId, session.WindowsSid),
                    session.DeviceId, proof.Attestation, now), at)
                : directory);
            session.SignIn(proof.Attestation, roster, now);
            if (window.Password is { } password) Locks.RememberPassword(id, password, FileKey(id));
            return $"This Windows login no longer signs in as {entry.Name}. This PC asks for your PIN or password from now on.";
        }
        finally { window.Forget(); }
    }

    /// <summary>*Sign in as someone else* (or, with <paramref name="expected"/>, as that account): a Prove sign-in through one of
    /// this PC's hosts, then the account is signed in here with the host's attestation, its password unlocks it here and
    /// *Remember me* is kept. Returns the account, or null when nobody signed in. The next Unlock of it is skipped.</summary>
    internal Guid? SignInAsSomeoneElse(Window? owner, Guid? expected = null)
    {
        if (Prove(owner, expected) is not { Proof: { } proof } window) return null;
        try
        {
            var roster = Roster ?? throw new InvalidOperationException("This PC isn't in a Martlet network yet.");
            var id = proof.AccountId;
            var now = DateTimeOffset.UtcNow;
            // An account this Windows login already unlocks keeps signing in with it; the sign-in only proved the person.
            if (!(session.State.SignedIn.Contains(id) && UnlocksWithWindows(id)))
            {
                session.SignIn(proof.Attestation, roster, now);
                try
                {
                    ChangeDirectory((directory, key, at) => directory.Find(id) is { Removed: false } account
                        ? directory.Put(key, AccountLinks.SignedIn(account, session.DeviceId, proof.Attestation, now), at)
                        : directory);
                }
                catch (InvalidOperationException error) { ErrorLog.Warn($"Accounts: the binding of {AccountSession.Short(id)} waits for the directory sync", error); }
                Locks.Change(id, l => l with { Remember = window.Remember });
            }
            if (window.Password is { } password) Locks.RememberPassword(id, password, FileKey(id));
            justProved.Add(id);
            return id;
        }
        catch (ArgumentException error)
        {
            ShowProblem(owner, error.Message);
            return null;
        }
        finally { window.Forget(); }
    }

    /// <summary>At start, before the account loads: *Continue as ...?* at first start, then the Unlock of the account in use. False
    /// when nobody unlocked (Martlet closes).</summary>
    internal bool AtStart()
    {
        try { ContinueAsAtStart(); }
        catch (Exception error) when (error is InvalidOperationException or ArgumentException or IOException or UnauthorizedAccessException)
        {
            ErrorLog.Warn("Accounts: Continue as didn't finish; using the new account", error);
        }
        while (true)
        {
            var id = session.AccountId;
            if (Unlock(id, null, out var outcome)) return true;
            Guid? next = outcome switch
            {
                AccountUnlockOutcome.ChooseAnother => Choose(null, id),
                AccountUnlockOutcome.SignInAsSomeoneElse => SignInAsSomeoneElse(null, Locks.Load(id).Methods.Count == 0 ? id : null),
                _ => null
            };
            if (outcome == AccountUnlockOutcome.Canceled) return false;
            if (next is { } chosen && chosen != id) session.UseAtStart(chosen);
        }
    }

    /// <summary>*Choose another account*: the other accounts signed in here and *Sign in as someone else*.</summary>
    internal Guid? Choose(Window? owner, Guid except)
    {
        var others = session.SignedIn.Where(a => a.Id != except).ToArray();
        var dialog = new AccountChoiceDialog("AccountChooseDialog", "Choose an account", "Who uses Martlet now?",
            others.Length == 0 ? "Nobody else is signed in on this PC." : "Choose your account.",
            [.. others.Select(a => ("UnlockChoose-" + a.Key, a.Name)), ("UnlockChooseSignIn", "Sign in as someone else")]) { Owner = owner };
        if (dialog.ShowDialog() != true) return null;
        if (dialog.Chosen == "UnlockChooseSignIn") return SignInAsSomeoneElse(owner);
        return others.FirstOrDefault(a => "UnlockChoose-" + a.Key == dialog.Chosen)?.Id;
    }

    /// <summary>First start: when the new account made for this Windows login is still unwritten and the Windows login's e-mail
    /// hint matches an account with a Martlet password, asks *Continue as ...?* and needs a Prove sign-in as that account.</summary>
    private void ContinueAsAtStart()
    {
        var state = session.State;
        if (state.SignedIn.Count != 1 || state.PendingFor(state.Current) is null || session.Directory.Find(state.Current) is not null) return;
        if (Roster is not { } roster || WindowsLogin.Current.EmailHint is not { } email) return;
        var account = session.Directory.Live.FirstOrDefault(a => a.HasEmail(roster.NetworkId, email) && a.Logins.Any(l => l.Kind == AccountLoginKinds.Martlet));
        if (account is null) return;
        ErrorLog.Info($"Accounts: this Windows login's e-mail hint matches account {AccountSession.Short(account.Id)}; asking Continue as.");
        var ask = new AccountChoiceDialog("ContinueAsDialog", "Continue as", $"Continue as {account.Name}?",
            $"This Windows login's e-mail matches {account.Name}'s account in your household. Sign in with {account.Name}'s Martlet password to " +
            "continue as them on this PC; the e-mail alone never signs anyone in.",
            [("ContinueAsYes", $"Continue as {account.Name}"), ("ContinueAsNo", "No, I'm someone else")]);
        if (ask.ShowDialog() != true || ask.Chosen != "ContinueAsYes") return;
        var prove = Prove(null, account.Id, $"Sign in as {account.Name}",
            $"Sign in with {account.Name}'s Martlet password. This Windows login then signs in as {account.Name}.", offerRemember: false);
        if (prove?.Proof is not { } proof) return;
        try
        {
            var now = DateTimeOffset.UtcNow;
            session.SignIn(proof.Attestation, roster, now, bindWith: ThisWindowsLogin);
            session.UseAtStart(account.Id, replaceNew: true);
            try
            {
                ChangeDirectory((directory, key, at) => directory.Find(account.Id) is { Removed: false } entry
                    ? directory.Put(key, AccountLinks.WithWindowsLogin(entry, session.DeviceId, session.WindowsSid, null, now, proof.Attestation), at)
                    : directory);
            }
            catch (InvalidOperationException error) { ErrorLog.Warn("Accounts: the link of this Windows login waits for the directory sync", error); }
            if (prove.Password is { } password) Locks.RememberPassword(account.Id, password, null);
            justProved.Add(account.Id);
        }
        finally { prove.Forget(); }
    }

    /// <summary>*Sign out of this PC* for <paramref name="id"/>, which isn't in use: this device's binding leaves the directory
    /// (when this PC can change it), the account leaves this device's session and its unlock file goes. Its folder stays.</summary>
    internal void SignOut(Guid id)
    {
        Seal(id);
        try { ChangeDirectory((directory, key, at) => directory.Find(id) is { Removed: false } entry && entry.Device(session.DeviceId) is not null
            ? directory.Put(key, entry.WithoutDevice(session.DeviceId), at) : directory); }
        catch (InvalidOperationException error) { ErrorLog.Warn($"Accounts: couldn't remove this PC's binding of {AccountSession.Short(id)}", error); }
        session.SignOut(id);
        Locks.Delete(id);
    }

    /// <summary>When Martlet closes: accounts signed in with a Prove sign-in and *Remember me* off sign out of this PC, and each
    /// account whose files are encrypted here is encrypted again.</summary>
    internal void OnExit()
    {
        foreach (var id in session.State.SignedIn.ToArray())
        {
            if (session.State.ProofFor(id) is null || Locks.Load(id).Remember) continue;
            try
            {
                if (id == session.AccountId)
                {
                    var other = session.State.SignedIn.FirstOrDefault(a => a != id && Locks.Load(a).Remember);
                    if (other == Guid.Empty) continue;
                    session.UseOnExit(other);
                }
                SignOut(id);
            }
            catch (Exception error) when (error is InvalidOperationException or IOException or UnauthorizedAccessException or ArgumentException)
            {
                ErrorLog.Warn($"Accounts: couldn't sign account {AccountSession.Short(id)} out of this PC on exit", error);
            }
        }
        Guid[] open;
        lock (fileKeys) open = fileKeys.Keys.ToArray();
        foreach (var id in open) Seal(id);
    }

    private static void ShowProblem(Window? owner, string text)
    {
        var dialog = new AccountChoiceDialog("AccountProblemDialog", "Accounts", "Accounts", text, [("AccountProblemOk", "OK")]) { Owner = owner };
        dialog.ShowDialog();
    }
}
