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
/// switches, never during a reply. One per process (App.AccountSecurity).
/// </summary>
internal sealed class AccountSecurityService(AccountSession session, string dataDirectory)
{
    // Keys of accounts whose files are encrypted here: the account is open, or some of its files still wait to be encrypted.
    private readonly Dictionary<Guid, byte[]> fileKeys = [];
    // How many times each account was opened here; a seal asked for before the last opening doesn't run.
    private readonly Dictionary<Guid, int> openings = [];
    private readonly Dictionary<Guid, object> folderGates = [];
    private readonly HashSet<Guid> justProved = [];

    /// <summary>The waits before a seal tries again the files another program had open.</summary>
    internal IReadOnlyList<TimeSpan> SealRetries { get; init; } = [TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(2)];
    /// <summary>The waits before an unlock tries again the files that didn't decrypt.</summary>
    internal IReadOnlyList<TimeSpan> UnsealRetries { get; init; } = [TimeSpan.FromMilliseconds(300), TimeSpan.FromSeconds(1)];
    /// <summary>Asks the person whether to open an account whose files didn't all decrypt (the file list). Null: a dialog.</summary>
    internal Func<Window?, string, IReadOnlyList<string>, bool>? OpenWithout { get; init; }


    internal AccountSession Session => session;
    internal string DataDirectory => dataDirectory;
    internal AccountLockStore Locks { get; } = new(dataDirectory);
    internal AccountLoginKey ThisWindowsLogin => AccountLoginKey.ForWindows(session.DeviceId, session.WindowsSid);
    internal NetworkRoster? Roster => NetworkIdentity.Load(dataDirectory).Roster;

    /// <summary>A copy of the account's file key (the caller may zero it), or null.</summary>
    internal byte[]? FileKey(Guid id)
    {
        lock (fileKeys) return fileKeys.TryGetValue(id, out var key) ? (byte[])key.Clone() : null;
    }

    internal void SetFileKey(Guid id, byte[]? key)
    {
        lock (fileKeys)
        {
            if (fileKeys.Remove(id, out var old)) CryptographicOperations.ZeroMemory(old);
            if (key is not null) fileKeys[id] = (byte[])key.Clone();
        }
    }

    // One seal or unseal of an account's folder at a time.
    private object FolderGate(Guid id)
    {
        lock (folderGates)
        {
            if (!folderGates.TryGetValue(id, out var gate)) folderGates[id] = gate = new object();
            return gate;
        }
    }

    private int Openings(Guid id)
    {
        lock (fileKeys) return openings.GetValueOrDefault(id);
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

    // Decrypts the account's files when they are encrypted here. A seal of the account asked for before this doesn't run after it.
    internal bool Opened(Guid id, Window? owner)
    {
        var folder = AccountSession.FolderFor(dataDirectory, id);
        IReadOnlyList<string> skipped;
        byte[]? key;
        lock (FolderGate(id))
        {
            lock (fileKeys) openings[id] = openings.GetValueOrDefault(id) + 1;
            if (AccountVault.Count(folder).Encrypted == 0) return true;
            key = FileKey(id);
            if (key is not null)
            {
                try
                {
                    for (var attempt = 0; ; attempt++)
                    {
                        var result = AccountVault.Unseal(folder, key);
                        ErrorLog.Info($"Accounts: decrypted {result.Done} file(s) of account {AccountSession.Short(id)}" +
                            (result.Skipped.Count > 0 ? $"; {result.Skipped.Count} couldn't be decrypted." : "."));
                        skipped = result.Skipped;
                        if (skipped.Count == 0) return true;
                        if (attempt >= UnsealRetries.Count) break;
                        Thread.Sleep(UnsealRetries[attempt]);
                    }
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                {
                    ErrorLog.Warn($"Accounts: the files of account {AccountSession.Short(id)} couldn't be decrypted", error);
                    skipped = ["(the account's folder)"];
                }
                finally { CryptographicOperations.ZeroMemory(key); }
            }
            else skipped = [];
        }
        if (key is null)
        {
            ErrorLog.Warn($"Accounts: account {AccountSession.Short(id)}'s files here are encrypted, but no key opened them.");
            ShowProblem(owner, $"{NameOf(id)}'s files on this PC are encrypted: unlock with your PIN to open them.");
            return false;
        }
        // Some files stay encrypted: the account would open without them (its stores would start from their defaults).
        if ((OpenWithout ?? AskOpenWithout)(owner, NameOf(id), skipped)) return true;
        ErrorLog.Info($"Accounts: account {AccountSession.Short(id)} stays locked; {skipped.Count} file(s) didn't decrypt.");
        Seal(id);
        return false;
    }

    private static bool AskOpenWithout(Window? owner, string name, IReadOnlyList<string> skipped)
    {
        var list = string.Join(", ", skipped.Take(5)) + (skipped.Count > 5 ? $" and {skipped.Count - 5} more" : "");
        var dialog = new AccountChoiceDialog("AccountFilesStayEncryptedDialog", "Accounts", $"Some of {name}'s files stay encrypted",
            $"Martlet couldn't decrypt {skipped.Count} of {name}'s files on this PC ({list}), so if you open the account now, Martlet starts without them.",
            [("AccountFilesOpenAnyway", "Open without them"), ("AccountFilesStayLocked", "Don't open now")]) { Owner = owner };
        return dialog.ShowDialog() == true && dialog.Chosen == "AccountFilesOpenAnyway";
    }

    /// <summary>After <paramref name="id"/> stopped being active here: encrypts its files off the window's thread
    /// (<see cref="Seal(Guid)"/>), unless the account opens again first. Files another program has open are tried again after
    /// <see cref="SealRetries"/>; the key is kept until every file is encrypted, and Martlet tries again when it closes. The
    /// person sees the files that stayed plain.</summary>
    internal Task<AccountSealResult?> SealInBackground(Guid id)
    {
        var opening = Openings(id);
        return Task.Run(async () =>
        {
            for (var attempt = 0; ; attempt++)
            {
                var result = Seal(id, opening);
                if (result is not { Skipped.Count: > 0 }) return result;
                if (attempt >= SealRetries.Count)
                {
                    ShowSealProblem(id, result.Skipped);
                    return result;
                }
                await Task.Delay(SealRetries[attempt]).ConfigureAwait(false);
            }
        });
    }

    private void ShowSealProblem(Guid id, IReadOnlyList<string> skipped)
    {
        var list = string.Join(", ", skipped.Take(5)) + (skipped.Count > 5 ? $" and {skipped.Count - 5} more" : "");
        ErrorLog.Warn($"Accounts: {skipped.Count} file(s) of account {AccountSession.Short(id)} stay plain after every try; Martlet tries again when it closes.");
        if (Application.Current?.Dispatcher is not { } dispatcher) return;
        var name = NameOf(id);
        dispatcher.BeginInvoke(() => ShowProblem(Application.Current?.MainWindow,
            $"{skipped.Count} of {name}'s files on this PC are not encrypted yet because another program has them open ({list}); Martlet tries again when it closes."));
    }

    /// <summary>Encrypts <paramref name="id"/>'s files here when that is on and its key is known (it just stopped being active
    /// here), then forgets the key. When files stay plain (another program has them open), the key is kept so a later seal
    /// (<see cref="SealInBackground"/>, or when Martlet closes) encrypts them.</summary>
    internal AccountSealResult? Seal(Guid id) => Seal(id, opening: null);

    private AccountSealResult? Seal(Guid id, int? opening)
    {
        lock (FolderGate(id))
        {
            byte[]? key;
            lock (fileKeys)
            {
                // The account opened again since this seal was asked for: it is in use, so its files stay plain.
                if (opening is { } asked && openings.GetValueOrDefault(id) != asked) return null;
                fileKeys.Remove(id, out key);
            }
            if (key is null) return null;
            var keep = false;
            try
            {
                var locks = Locks.Load(id);
                // A key is known only while encryption is on; a damaged unlock file doesn't stop the seal (fail closed).
                if (!locks.Encrypt && !locks.Damaged) return null;
                var result = AccountVault.Seal(AccountSession.FolderFor(dataDirectory, id), key);
                ErrorLog.Info($"Accounts: encrypted {result.Done} file(s) of account {AccountSession.Short(id)}" +
                    (result.Skipped.Count > 0 ? $"; {result.Skipped.Count} in use stayed plain: {string.Join(", ", result.Skipped)}." : "."));
                keep = result.Skipped.Count > 0;
                return result;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                ErrorLog.Warn($"Accounts: the files of account {AccountSession.Short(id)} couldn't be encrypted; Martlet tries again", error);
                keep = true;
                return new(0, ["(the account's folder)"]);
            }
            finally
            {
                if (keep)
                    lock (fileKeys)
                    {
                        if (!fileKeys.TryAdd(id, key)) CryptographicOperations.ZeroMemory(key);
                    }
                else CryptographicOperations.ZeroMemory(key);
            }
        }
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
            throw new InvalidOperationException("Your other computers would refuse this change because this PC hasn't proved that account: sign in again first.");
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
            return $"This Windows login no longer signs in as {entry.Name}; this PC asks for your PIN or password from now on.";
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
            // The sign-in proved the person: a damaged unlock file here starts again with nothing set.
            try { Locks.Repair(id); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                ErrorLog.Warn($"Accounts: the damaged unlock file of {AccountSession.Short(id)} couldn't be replaced", error);
            }
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
                try { Locks.Change(id, l => l with { Remember = window.Remember }); }
                catch (Exception error) when (error is InvalidOperationException or IOException or UnauthorizedAccessException)
                {
                    ErrorLog.Warn($"Accounts: Remember me of {AccountSession.Short(id)} wasn't saved here", error);
                }
            }
            if (window.Password is { } password)
            {
                var fileKey = FileKey(id);
                try { Locks.RememberPassword(id, password, fileKey); }
                catch (Exception error) when (error is InvalidOperationException or IOException or UnauthorizedAccessException)
                {
                    ErrorLog.Warn($"Accounts: the password of {AccountSession.Short(id)} wasn't kept here for offline unlock", error);
                }
                finally { if (fileKey is not null) CryptographicOperations.ZeroMemory(fileKey); }
            }
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
        if (Roster is not { } roster) return;
        // A simulated Windows login (MCP checks on a disposable folder) has no e-mail: never the real one of this PC.
        if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(AccountSession.SimulatedLoginVariable))) return;
        if (WindowsLogin.Current.EmailHint is not { } email) return;
        var account = session.Directory.Live.FirstOrDefault(a => a.HasEmail(roster.NetworkId, email) && a.Logins.Any(l => l.Kind == AccountLoginKinds.Martlet));
        if (account is null) return;
        ErrorLog.Info($"Accounts: this Windows login's e-mail hint matches account {AccountSession.Short(account.Id)}; asking Continue as.");
        var ask = new AccountChoiceDialog("ContinueAsDialog", "Continue as", $"Continue as {account.Name}?",
            $"This Windows login's e-mail matches {account.Name}'s account in your household: sign in with {account.Name}'s Martlet password to continue as them.",
            [("ContinueAsYes", $"Continue as {account.Name}"), ("ContinueAsNo", "No, I'm someone else")]);
        if (ask.ShowDialog() != true || ask.Chosen != "ContinueAsYes") return;
        var prove = Prove(null, account.Id, $"Sign in as {account.Name}",
            $"Sign in with {account.Name}'s Martlet password so this Windows login signs in as {account.Name} from now on.", offerRemember: false);
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

    /// <summary>*Merge another account into this one*, before its data steps: binds this PC to the merged account with its Prove
    /// attestation and gives the directory to the hosts at once, so they let this PC read that account's memories and settings.</summary>
    internal async Task PrepareMergeAsync(Martlet.Avatar.Audio2Face.Remote.HostAccountProof other, CancellationToken token)
    {
        var now = DateTimeOffset.UtcNow;
        var directory = ChangeDirectory((d, key, at) => d.Find(other.AccountId) is { Removed: false } account
            ? d.Put(key, AccountLinks.SignedIn(account, session.DeviceId, other.Attestation, now), at)
            : throw new InvalidOperationException("The other account isn't in your household's directory."));
        var given = 0;
        foreach (var choice in Hosts())
        {
            try
            {
                using var connection = ClusterSync.Connect(choice.Host);
                if ((await connection.MergeAccountsAsync(directory, token)).Rejected == 0) given++;
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (Exception error) when (ClusterSync.IsHostFailure(error) || error is OperationCanceledException)
            {
                ErrorLog.Warn($"Accounts: {choice.Host.HostId} didn't take this PC's sign-in for the merge", error);
            }
        }
        if (given == 0) throw new InvalidOperationException("No host took this PC's sign-in as the other account, so its data can't be read for the merge.");
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
