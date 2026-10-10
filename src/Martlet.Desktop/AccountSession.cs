using System.IO;
using System.Security.Principal;
using Martlet.Core.Accounts;
using Martlet.Core.Contracts;
using Martlet.Core.Network;

namespace Martlet.Desktop;

/// <summary>An account change: at start (<see cref="From"/> null) or a switch. <see cref="New"/>: <see cref="ToFolder"/> was just
/// made (a new account, or the owner account of an install from before accounts).</summary>
internal sealed record AccountChange(Guid? From, Guid To, string? FromFolder, string ToFolder, string HouseholdFolder, bool New);

/// <summary>An account as the picker shows it: from the household's account directory, or pending on this PC.</summary>
internal sealed record AccountView(Guid Id, string Name, string Role, bool Pending)
{
    /// <summary>The account ID as 32 lowercase hex digits (folders, memory spaces, automation IDs).</summary>
    internal string Key => Id.ToString("N");

    /// <summary>Up to two letters for the avatar: the first letters of the first two words.</summary>
    internal string Initials => string.Concat(Name.Split(' ', StringSplitOptions.RemoveEmptyEntries).Take(2)
        .Select(word => char.ToUpperInvariant(word[0]))) is { Length: > 0 } letters ? letters : "?";

    internal string RoleText => Role switch
    {
        AccountRoles.Owner => "Owner",
        AccountRoles.Admin => "Admin",
        _ => "Member"
    };
}

/// <summary>
/// The account signed in on this device now (docs/ACCOUNTS.md, "Desktop account session"): its ID, its folder
/// (&lt;data&gt;\accounts\&lt;32 hex&gt;), the household folder (the data folder root) and the accounts this Windows login can
/// switch to. The session is kept in accounts\session.json (<see cref="AccountSessionState"/>, device scope); the household's
/// account directory in accounts.json beside network.json, which the directory sync keeps the same on every host.
/// <list type="bullet">
/// <item>First start (<see cref="Open"/>): accounts bound to this Windows login sign in; else an install from before accounts
/// that is in a Martlet network becomes the household's owner account (<see cref="OwnerAccount.IdFor"/>); else a new account
/// named after the Windows display name, with no password. A new account is pending on this PC until the directory sync writes
/// it.</item>
/// <item>Change steps (<see cref="AddChangeStep"/>) run in order at start (<see cref="StartAsync"/>) and on every switch
/// (<see cref="SwitchToAsync"/>). A switch saves the session and raises <see cref="AccountChanged"/> only after every step
/// finished. The main window switches only between replies; nothing here runs on a reply's path.</item>
/// </list>
/// </summary>
internal sealed class AccountSession
{
    /// <summary>MARTLET_SIMULATE_WINDOWS_LOGIN: a display name for MCP checks on a disposable data folder, used with a fixture SID
    /// instead of the real Windows login, so no real name is written there.</summary>
    internal const string SimulatedLoginVariable = "MARTLET_SIMULATE_WINDOWS_LOGIN";
    internal const string SimulatedSid = "S-1-5-21-1000-1000-1000-1001";
    private const string FallbackName = "Me";
    private readonly List<Func<AccountChange, CancellationToken, Task>> steps = [];
    private bool startNew;
    private AccountSessionState state;
    private bool started;

    private AccountSession(string householdFolder, string deviceId, AccountSessionState state, AccountDirectory directory, bool startNew)
    {
        HouseholdFolder = householdFolder;
        DeviceId = deviceId;
        this.state = state;
        Directory = directory;
        this.startNew = startNew;
    }

    /// <summary>The data folder root: household-scope files (network.json, accounts.json, hosts and paid keys).</summary>
    internal string HouseholdFolder { get; }
    /// <summary>This Windows user's device ID: the provider of its <c>windows</c> login.</summary>
    internal string DeviceId { get; }
    /// <summary>The Windows login (SID) the signed-in accounts are bound to.</summary>
    internal string WindowsSid => state.WindowsSid;
    /// <summary>The account signed in now.</summary>
    internal Guid AccountId => state.Current;
    /// <summary>The folder of the account signed in now: &lt;data&gt;\accounts\&lt;32 hex&gt;.</summary>
    internal string AccountFolder => FolderFor(HouseholdFolder, AccountId);
    /// <summary>This PC's copy of the household's account directory.</summary>
    internal AccountDirectory Directory { get; private set; }
    internal AccountSessionState State => state;
    internal AccountView Current => View(AccountId);
    /// <summary>The accounts signed in on this device, most recently used first.</summary>
    internal IReadOnlyList<AccountView> SignedIn => state.SignedIn.Select(View).ToArray();
    /// <summary>The household's owner account: the directory's live owner, else an owner account pending on this PC.</summary>
    internal Guid? OwnerId =>
        Directory.Live.Where(a => a.Role == AccountRoles.Owner).OrderBy(a => a.Key, StringComparer.Ordinal).Select(a => (Guid?)a.Id).FirstOrDefault()
        ?? state.Pending.FirstOrDefault(p => p.Role == AccountRoles.Owner)?.Id;
    /// <summary>A switch runs its change steps now.</summary>
    internal bool Switching { get; private set; }

    /// <summary>Another account is in use: raised on the switching thread (the main window's) after the change steps ran and the
    /// session was saved. Never raised during a reply.</summary>
    internal event Action? AccountChanged;
    /// <summary>A name, a role or the signed-in list changed (a person added, or the directory sync brought a change).</summary>
    internal event Action? Changed;

    internal static string FolderFor(string dataDirectory, Guid accountId) =>
        Path.Combine(dataDirectory, AccountSessionState.Folder, accountId.ToString("N"));

    internal static string DirectoryPath(string dataDirectory) => Path.Combine(dataDirectory, AccountDirectory.FileName);

    /// <summary>Adds a step every account change awaits, in the order added: at start (before the theme, character and
    /// conversation load; the starting thread waits for it, so it must not wait for the window's thread) and on every switch
    /// (awaited without blocking the window's thread). A step that throws stops a switch, which stays on the old account; at
    /// start it is logged and start goes on.</summary>
    internal void AddChangeStep(Func<AccountChange, CancellationToken, Task> step)
    {
        ArgumentNullException.ThrowIfNull(step);
        steps.Add(step);
    }

    /// <summary>The session of the Windows user Martlet runs as (or the MARTLET_SIMULATE_WINDOWS_LOGIN fixture).</summary>
    internal static AccountSession OpenForThisLogin(string dataDirectory)
    {
        var simulated = Environment.GetEnvironmentVariable(SimulatedLoginVariable);
        if (!string.IsNullOrWhiteSpace(simulated))
            return Open(dataDirectory, SimulatedSid, () => simulated, NetworkIdentity.ThisDevice, DateTimeOffset.UtcNow);
        using var identity = WindowsIdentity.GetCurrent();
        var sid = identity.User?.Value ?? throw new InvalidOperationException("Windows didn't tell which user Martlet runs as.");
        return Open(dataDirectory, sid, () => WindowsLogin.Current.DisplayName, NetworkIdentity.ThisDevice, DateTimeOffset.UtcNow);
    }

    /// <summary>Opens this device's session, signing in for the first time when there is none for <paramref name="windowsSid"/>
    /// (<paramref name="displayName"/> is read only then), and makes the account's folder.</summary>
    internal static AccountSession Open(string dataDirectory, string windowsSid, Func<string> displayName, string deviceId, DateTimeOffset now)
    {
        var directory = LoadDirectory(dataDirectory);
        AccountSessionState? saved = null;
        try { saved = AccountSessionState.Load(dataDirectory); }
        catch (Exception error) when (error is ContractException or IOException or UnauthorizedAccessException)
        {
            ErrorLog.Warn($"Accounts: {AccountSessionState.FileName} couldn't be read ({error.Message}); signing in again.");
        }
        var state = saved is { } kept && kept.WindowsSid == windowsSid ? kept : FirstStart(dataDirectory, directory, windowsSid, displayName, deviceId, now);
        if (!ReferenceEquals(state, saved)) state.Save(dataDirectory);
        var folder = FolderFor(dataDirectory, state.Current);
        var made = !System.IO.Directory.Exists(folder);
        System.IO.Directory.CreateDirectory(folder);
        return new(dataDirectory, deviceId, state, directory, made);
    }

    private static AccountSessionState FirstStart(string dataDirectory, AccountDirectory directory, string windowsSid, Func<string> displayName,
        string deviceId, DateTimeOffset now)
    {
        var login = AccountLoginKey.ForWindows(deviceId, windowsSid);
        var bound = directory.SignedInOn(deviceId).Where(a => a.Device(deviceId)!.Login == login).Select(a => a.Id).ToArray();
        if (bound.Length > 0)
        {
            ErrorLog.Info($"Accounts: signed in to {bound.Length} account{(bound.Length == 1 ? "" : "s")} bound to this Windows login.");
            return new() { SchemaVersion = AccountSessionState.SchemaVersion1, WindowsSid = windowsSid, Current = bound[0], SignedIn = bound };
        }
        var name = NameFrom(displayName);
        // An install from before accounts that is in a network: every member desktop becomes the same owner account.
        if (directory.Accounts.Count == 0 && NetworkIdentity.Load(dataDirectory).Roster is { } roster)
        {
            var owner = OwnerAccount.IdFor(roster.NetworkId);
            ErrorLog.Info($"Accounts: this PC is in a Martlet network from before accounts, so it signs in as the household's owner account {Short(owner)}.");
            return AccountSessionState.For(windowsSid, owner, new() { Id = owner, Name = name, Role = AccountRoles.Owner, CreatedAt = now.ToUniversalTime() });
        }
        var id = Guid.NewGuid();
        var role = directory.Live.Any(a => a.Role == AccountRoles.Owner) ? AccountRoles.Member : AccountRoles.Owner;
        ErrorLog.Info($"Accounts: made account {Short(id)} ({role}) for this Windows login, with no password.");
        return AccountSessionState.For(windowsSid, id, new() { Id = id, Name = name, Role = role, CreatedAt = now.ToUniversalTime() });
    }

    private static string NameFrom(Func<string> displayName)
    {
        string? name;
        try { name = Account.CleanText(displayName(), Account.MaximumNameLength); }
        catch (Exception error) when (!ErrorLog.IsFatal(error))
        {
            ErrorLog.Warn("Accounts: couldn't read the Windows display name", error);
            name = null;
        }
        return Account.IsName(name) ? name! : FallbackName;
    }

    /// <summary>Runs the change steps for the account signed in at start, once.</summary>
    internal async Task StartAsync(CancellationToken token)
    {
        if (started) return;
        started = true;
        var change = new AccountChange(null, AccountId, null, AccountFolder, HouseholdFolder, startNew);
        foreach (var step in steps.ToArray())
        {
            try { await step(change, token).ConfigureAwait(false); }
            catch (Exception error) when (!ErrorLog.IsFatal(error) && error is not OperationCanceledException)
            {
                ErrorLog.Warn($"Accounts: a start step for account {Short(AccountId)} failed", error);
            }
        }
    }

    /// <summary>Switches to <paramref name="accountId"/>, signed in on this device: awaits every change step, then saves the
    /// session and raises <see cref="AccountChanged"/>. The caller makes sure no reply runs (the main window ends the
    /// conversation first).</summary>
    internal async Task SwitchToAsync(Guid accountId, CancellationToken token)
    {
        if (accountId == AccountId) return;
        if (!state.SignedIn.Contains(accountId)) throw new InvalidOperationException("That account isn't signed in on this PC.");
        if (Switching) throw new InvalidOperationException("Martlet is already switching accounts.");
        Switching = true;
        try
        {
            var toFolder = FolderFor(HouseholdFolder, accountId);
            var made = !System.IO.Directory.Exists(toFolder);
            System.IO.Directory.CreateDirectory(toFolder);
            var change = new AccountChange(AccountId, accountId, AccountFolder, toFolder, HouseholdFolder, made);
            foreach (var step in steps.ToArray()) await Task.Run(() => step(change, token), token);
            var next = state.Use(accountId);
            next.Save(HouseholdFolder);
            state = next;
            ErrorLog.Info($"Accounts: switched from {Short(change.From!.Value)} to {Short(accountId)}.");
        }
        finally { Switching = false; }
        AccountChanged?.Invoke();
    }

    /// <summary>Adds a person who uses this Windows login: a new member account bound to it, with no password, signed in on this
    /// device and pending until the directory sync writes it. Throws <see cref="ArgumentException"/> with a message to show when the
    /// name can't be used.</summary>
    internal Guid AddPerson(string name, DateTimeOffset now)
    {
        var clean = Account.CleanText(name, Account.MaximumNameLength);
        if (!Account.IsName(clean)) throw new ArgumentException($"Type a name of 1 to {Account.MaximumNameLength} letters.");
        if (SignedIn.FirstOrDefault(a => string.Equals(a.Name, clean, StringComparison.CurrentCultureIgnoreCase)) is { } same)
            throw new ArgumentException($"{same.Name} already uses this PC. Choose another name.");
        if (state.SignedIn.Count >= AccountSessionState.MaximumAccounts) throw new ArgumentException("This PC has as many people as it can keep.");
        var id = Guid.NewGuid();
        var next = state.Add(id, new() { Id = id, Name = clean!, Role = AccountRoles.Member, CreatedAt = now.ToUniversalTime() });
        next.Save(HouseholdFolder);
        state = next;
        ErrorLog.Info($"Accounts: added account {Short(id)} (member) on this Windows login, with no password.");
        Changed?.Invoke();
        return id;
    }

    /// <summary>Signs an account in on this device after a Prove sign-in (W12, W13: a password, a provider or Allow on another
    /// device): <paramref name="attestation"/> is the host's statement (<see cref="AccountAttestation"/>) that the account proved
    /// itself on this device. It must be valid now for <paramref name="roster"/> and name this device. The account joins the
    /// picker (it doesn't unlock with this Windows login) and the directory sync writes its device binding with the attestation.
    /// The caller switches to it with <see cref="SwitchToAsync"/>. Throws <see cref="ArgumentException"/> when the statement
    /// can't be used here.</summary>
    internal void SignIn(AccountAttestation attestation, NetworkRoster roster, DateTimeOffset now, AccountLoginKey? bindWith = null)
    {
        ArgumentNullException.ThrowIfNull(attestation);
        ArgumentNullException.ThrowIfNull(roster);
        if (attestation.Check(roster, now) is var check and not AccountAttestationCheck.Valid)
            throw new ArgumentException($"The host's sign-in statement can't be used ({check}). Sign in again.");
        if (attestation.DeviceId != DeviceId) throw new ArgumentException("The host's sign-in statement is for another device. Sign in again.");
        if (!state.SignedIn.Contains(attestation.AccountId) && state.SignedIn.Count >= AccountSessionState.MaximumAccounts)
            throw new ArgumentException("This PC has as many people as it can keep.");
        var next = state.Add(new AccountProof
        {
            Id = attestation.AccountId, Login = bindWith ?? attestation.Login, Attestation = attestation.ToText(), SignedInAt = now.ToUniversalTime()
        });
        next.Save(HouseholdFolder);
        state = next;
        ErrorLog.Info($"Accounts: account {Short(attestation.AccountId)} signed in on this PC with a {attestation.Login.Kind} login.");
        Changed?.Invoke();
    }

    /// <summary>W12, *Link this Windows login*: this device's binding of <paramref name="accountId"/>, signed in with a Prove
    /// sign-in, uses <paramref name="login"/> from now on (keeping the attestation), so the directory sync writes it that way.</summary>
    internal void BindWith(Guid accountId, AccountLoginKey login)
    {
        if (state.ProofFor(accountId) is not { } proof || proof.Login == login) return;
        var next = state.Add(proof with { Login = login });
        next.Save(HouseholdFolder);
        state = next;
    }

    /// <summary>W12, *Sign out of this PC*: <paramref name="accountId"/> leaves this device's signed-in accounts (switch away from
    /// it first). The caller removes this device's binding from the directory.</summary>
    internal void SignOut(Guid accountId)
    {
        if (!state.SignedIn.Contains(accountId)) return;
        var next = state.Without(accountId);
        next.Save(HouseholdFolder);
        state = next;
        ErrorLog.Info($"Accounts: account {Short(accountId)} signed out of this PC.");
        Changed?.Invoke();
    }

    /// <summary>W12: while Martlet closes, makes <paramref name="accountId"/> the account in use at the next start (no change
    /// steps run: nothing loads it now).</summary>
    internal void UseOnExit(Guid accountId)
    {
        if (!state.SignedIn.Contains(accountId) || accountId == state.Current) return;
        var next = state.Use(accountId);
        next.Save(HouseholdFolder);
        state = next;
    }

    /// <summary>W12: before <see cref="StartAsync"/>, uses <paramref name="accountId"/>, signed in on this device, instead of the
    /// account first chosen. <paramref name="replaceNew"/> (*Continue as ...?*): the new account first start made for this Windows
    /// login is dropped while it was never written to the directory.</summary>
    internal void UseAtStart(Guid accountId, bool replaceNew = false)
    {
        if (started) throw new InvalidOperationException("The session has started already.");
        if (!state.SignedIn.Contains(accountId)) throw new InvalidOperationException("That account isn't signed in on this PC.");
        var made = state.Current;
        var next = state.Use(accountId);
        if (replaceNew && made != accountId && next.PendingFor(made) is not null && Directory.Find(made) is null)
        {
            next = next.Without(made);
            try { System.IO.Directory.Delete(FolderFor(HouseholdFolder, made)); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }
        next.Save(HouseholdFolder);
        state = next;
        startNew = !System.IO.Directory.Exists(AccountFolder);
        System.IO.Directory.CreateDirectory(AccountFolder);
        ErrorLog.Info($"Accounts: continuing as account {Short(accountId)} on this Windows login.");
    }

    /// <summary>Takes the directory the sync merged and wrote: saves accounts.json and the session without the pending accounts
    /// <paramref name="written"/> to it, and raises <see cref="Changed"/> when names, roles or the list changed.</summary>
    internal void Follow(AccountDirectory directory, IEnumerable<Guid> written)
    {
        var before = Signature();
        if (directory.Digest() != Directory.Digest()) SaveDirectory(HouseholdFolder, directory);
        Directory = directory;
        var next = state.Written(written);
        if (!ReferenceEquals(next, state))
        {
            next.Save(HouseholdFolder);
            state = next;
        }
        if (Signature() != before) Changed?.Invoke();
    }

    private string Signature() => string.Join('|', SignedIn.Select(a => $"{a.Key}:{a.Name}:{a.Role}:{a.Pending}:{Directory.Find(a.Id)?.Removed == true}"));

    private AccountView View(Guid id) =>
        Directory.Find(id) is { } entry ? new(id, entry.Name, entry.Role, false)
        : state.PendingFor(id) is { } pending ? new(id, pending.Name, pending.Role, true)
        : new(id, "Someone", AccountRoles.Member, true);

    /// <summary>The first eight hex digits of an account ID, for logs (never names).</summary>
    internal static string Short(Guid id) => id.ToString("N")[..8];

    /// <summary>This PC's copy of the account directory; empty when there is none or it can't be read (the hosts restore it).</summary>
    internal static AccountDirectory LoadDirectory(string dataDirectory)
    {
        try { return AccountDirectory.Parse(File.ReadAllBytes(DirectoryPath(dataDirectory))); }
        catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException) { return AccountDirectory.Empty; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ContractException)
        {
            ErrorLog.Warn($"Accounts: {AccountDirectory.FileName} couldn't be read ({error.Message}); your hosts give it back.");
            return AccountDirectory.Empty;
        }
    }

    internal static void SaveDirectory(string dataDirectory, AccountDirectory directory)
    {
        var bytes = directory.Write();
        System.IO.Directory.CreateDirectory(dataDirectory);
        var temporary = Path.Combine(dataDirectory, $"accounts.{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllBytes(temporary, bytes);
            File.Move(temporary, DirectoryPath(dataDirectory), overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    /// <summary>
    /// Writes this device's accounts into <paramref name="directory"/> after it was merged with the hosts' copies: each signed-in
    /// account the directory lacks is written from its pending name and role, and each one gets this device's binding when it
    /// lacks it (a write elsewhere at the same time can drop it): with this Windows login (added to its logins too), or with the
    /// login and attestation of its Prove sign-in. A household has one owner: a pending owner is written as a member when the
    /// directory has another owner. Entries are signed by <paramref name="signer"/>. Returns the directory and the accounts
    /// written.
    /// </summary>
    internal static (AccountDirectory Directory, IReadOnlyList<Guid> Written) Reconcile(AccountDirectory directory, AccountSessionState state,
        NetworkRoster roster, INetworkSigner signer, string deviceId, DateTimeOffset now)
    {
        var windows = AccountLoginKey.ForWindows(deviceId, state.WindowsSid);
        var written = new List<Guid>();
        foreach (var id in state.SignedIn)
        {
            var proof = state.ProofFor(id);
            var login = proof?.Login ?? windows;
            var entry = directory.Find(id);
            if (entry is { Removed: true }) continue;
            if (entry is null)
            {
                if (state.PendingFor(id) is not { } pending) continue;
                var role = pending.Role == AccountRoles.Owner && directory.Live.Any(a => a.Role == AccountRoles.Owner && a.Id != id)
                    ? AccountRoles.Member : pending.Role;
                entry = (id == OwnerAccount.IdFor(roster.NetworkId) ? OwnerAccount.Create(roster, pending.Name) with { Role = role }
                    : Account.Create(pending.Name, role, id)).WithLogin(AccountLogin.For(windows, null, pending.CreatedAt));
            }
            else if ((proof is not null || entry.Login(windows) is not null) && entry.Device(deviceId) is { } bound &&
                bound.Login == login && bound.Attestation == proof?.Attestation)
            {
                if (state.PendingFor(id) is not null) written.Add(id);
                continue;
            }
            else if (proof is null && entry.Login(windows) is null) entry = entry.WithLogin(AccountLogin.For(windows, null, now));
            if (entry.Device(deviceId) is not { } device || device.Login != login || device.Attestation != proof?.Attestation)
                entry = entry.WithDevice(AccountDevice.For(deviceId, login, proof?.SignedInAt ?? now, proof?.Attestation));
            directory = directory.Put(signer, entry, now);
            written.Add(id);
        }
        return (directory, written);
    }
}
