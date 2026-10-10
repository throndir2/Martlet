using System.Text.Json;
using System.Text.Json.Serialization;
using Martlet.Core.Access;
using Martlet.Core.Accounts;
using Martlet.Core.Logs;

namespace Martlet.Gateway;

/// <summary>Where a host keeps its sign-in settings (signin.json beside host.json on Linux hosts, 0600, service owner): the
/// owner account's verifiers and authenticator secret, provider client secrets and the allowed identities. <see cref="Load"/>
/// returns null when sign-in was never set up; <see cref="Save"/> may throw on storage failure.</summary>
public interface IGatewaySignInStorage
{
    byte[]? Load();
    void Save(byte[] bytes);
}

/// <summary>Who signed in: the provider's ID on this host ("owner" for the owner account), the provider's stable subject
/// (the account name, an OpenID Connect <c>sub</c>, a Discord user ID, a SteamID64) and a label for people (an email
/// address or user name; shown, never trusted).</summary>
public sealed record GatewaySignInIdentity(string Provider, string Subject, string? Label)
{
    public override string ToString() => Label is { Length: > 0 } ? $"{Label} ({Provider})" : $"{Provider}:{Subject}";
}

/// <summary>One sign-in in progress: what <c>/signin/begin</c> handed out and <c>/signin/complete</c> must match. Works once
/// and for <see cref="GatewaySignInService.AttemptLifetime"/>.</summary>
internal sealed record GatewaySignInAttempt(string Id, string Provider, string State, string Nonce, string? CodeChallenge,
    string? RedirectUri, DateTimeOffset ExpiresAt);

/// <summary>A way to sign in other than the owner account (OpenID Connect, Discord, Steam). It turns an attempt into the URL
/// the computer opens in its browser, and the proof that computer brings back into a verified identity; it throws
/// <see cref="GatewayProtocolException"/> (<c>signin.invalid</c>, <c>signin.provider</c>) otherwise. Client secrets stay on the
/// host.</summary>
internal interface IGatewaySignInProvider
{
    ValueTask<string?> AuthorizeAsync(GatewaySignInAttempt attempt, CancellationToken cancellationToken);
    ValueTask<GatewaySignInIdentity> VerifyAsync(GatewaySignInAttempt attempt, JsonElement proof, CancellationToken cancellationToken);
}

/// <summary>
/// Sign-in enrollment: lets a computer away from home pair with this host by signing in instead of typing a pairing code
/// shown here. Signing in gates enrollment only. A successful sign-in by an allowed identity gets exactly what pairing gets
/// (one <c>voice</c> device credential, pinned TLS and signed requests from then on, revoked like any pairing) and the host
/// remembers which identity enrolled the device, so a member desktop can let it into the network by that attestation
/// (<see cref="Attestation"/>). The owner account (password plus a mandatory authenticator code or recovery code) is always
/// allowed; any other identity must be on the owner's allow list. Removing an identity from the list revokes the computers it
/// enrolled.
/// </summary>
internal sealed class GatewaySignInService(GatewayCredentialStore credentials, TimeProvider clock,
    IGatewayCrypto crypto, Action<string, string> log)
{
    internal const string OwnerProvider = "owner";
    /// <summary>Any Martlet password login on this host (the owner's or another household account's), by user name.</summary>
    internal const string MartletProvider = AccountLoginKinds.MartletProvider;
    internal static readonly TimeSpan AttemptLifetime = TimeSpan.FromMinutes(10);
    internal const int MaximumAttempts = 32;
    internal const int MaximumEnrolled = 64;

    private readonly object gate = new();
    private readonly Dictionary<string, GatewaySignInAttempt> attempts = new(StringComparer.Ordinal);
    private readonly List<(string DeviceId, GatewaySignInIdentity Identity, DateTimeOffset At)> refused = [];
    internal const int MaximumRefused = 8;
    private GatewaySignInDocument document = new();
    private IGatewaySignInStorage? storage;

    /// <summary>Builds the provider for a configured entry; external providers plug in here.</summary>
    internal Func<GatewaySignInProviderConfig, IGatewaySignInProvider?> Providers { get; set; } = _ => null;

    internal bool Attached { get { lock (gate) return storage is not null; } }

    internal void Attach(IGatewaySignInStorage value)
    {
        ArgumentNullException.ThrowIfNull(value);
        lock (gate)
        {
            storage = value;
            document = LoadLocked();
        }
    }

    /// <summary>The settings as saved now (signin.json is re-read each time, so martlet-host edits apply without a restart).
    /// Computers enrolled by an identity that is no longer allowed lose their credentials here.</summary>
    private GatewaySignInDocument LoadLocked()
    {
        if (storage is null) return document;
        GatewaySignInDocument loaded;
        try { loaded = storage.Load() is { } bytes ? GatewaySignInDocument.Parse(bytes) : new(); }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            log(LogLevels.Warn, "signin.json is unreadable or invalid, so nobody can sign in to this host until it is set up again.");
            return new();
        }
        var stale = loaded.Sweep(clock.GetUtcNow());
        if (stale.Count > 0)
        {
            try { storage.Save(loaded.Write()); }
            catch (Exception error) when (error is not OperationCanceledException) { }
            RevokeLocked(stale);
        }
        return loaded;
    }

    private int RevokeLocked(IReadOnlyList<GatewaySignInEnrollment> stale)
    {
        // A friend's sign-in revokes exactly the credential it issued; the owner's computers lose every pairing of the device,
        // as before (a member desktop may have paired again by its network key since).
        var revoked = stale.Sum(e => e is { Access: not null, CredentialId: { } id }
            ? credentials.RevokeCredential(id) ? 1 : 0
            : credentials.RevokeDevice(e.DeviceId, CancellationToken.None));
        foreach (var enrollment in stale)
            log(LogLevels.Info, $"Revoked {enrollment.DeviceId}: it paired by signing in as {enrollment.Label ?? enrollment.Subject} ({enrollment.Provider})" +
                (enrollment.Access is null ? ", which is no longer allowed." : " as a friend, which is no longer allowed."));
        return revoked;
    }

    private void SaveLocked(GatewaySignInDocument next)
    {
        GatewayRules.Require(storage is not null, "signin.unavailable");
        storage!.Save(next.Write());
        document = next;
    }

    /// <summary>The ways to sign in here, for anyone (nothing secret).</summary>
    internal IReadOnlyList<(string Id, string Kind, string Name, int? RedirectPort)> Available()
    {
        lock (gate)
        {
            if (storage is null) return [];
            var current = document = LoadLocked();
            var list = new List<(string, string, string, int?)>();
            if (current.Owner is not null) list.Add((OwnerProvider, OwnerProvider, "Owner account", null));
            list.AddRange(current.Providers.Where(p => Providers(p) is not null).Select(p => (p.Id, p.Kind, p.Name, p.RedirectPort)));
            return list;
        }
    }

    /// <summary>Whether the "martlet" provider (any household account's Martlet password) takes sign-ins here. It is not in
    /// <see cref="Available"/>, which desktops older than accounts take as a list of browser providers.</summary>
    internal bool MartletAvailable()
    {
        lock (gate) return storage is not null && document.PasswordLogins().Any();
    }

    internal async ValueTask<(GatewaySignInAttempt Attempt, string? AuthorizeUrl)> BeginAsync(string provider, string? codeChallenge,
        string? redirectUri, CancellationToken cancellationToken)
    {
        GatewaySignInAttempt attempt;
        IGatewaySignInProvider? external = null;
        lock (gate)
        {
            GatewayRules.Require(storage is not null, "signin.unavailable");
            var current = document = LoadLocked();
            if (provider == OwnerProvider) GatewayRules.Require(current.Owner is not null, "signin.unavailable");
            else if (provider == MartletProvider) GatewayRules.Require(current.PasswordLogins().Any(), "signin.unavailable");
            else
            {
                var config = current.Providers.FirstOrDefault(p => p.Id == provider);
                external = config is null ? null : Providers(config);
                GatewayRules.Require(external is not null, "signin.unavailable");
                GatewayRules.Require(Base64Url.TryDecode(codeChallenge, 32, out _) && IsLoopbackRedirect(redirectUri) &&
                    (config!.RedirectPort is not { } port || new Uri(redirectUri!).Port == port), "request.invalid");
            }
            var now = clock.GetUtcNow();
            foreach (var stale in attempts.Values.Where(a => a.ExpiresAt <= now).ToArray()) attempts.Remove(stale.Id);
            if (attempts.Count >= MaximumAttempts) attempts.Remove(attempts.Values.OrderBy(a => a.ExpiresAt).First().Id);
            attempt = new(Random(), provider, Random(), Random(), codeChallenge, redirectUri, now + AttemptLifetime);
            attempts[attempt.Id] = attempt;
        }
        var url = external is null ? null : await external.AuthorizeAsync(attempt, cancellationToken).ConfigureAwait(false);
        return (attempt, url);
    }

    /// <summary>Finishes a sign-in: verifies the proof, checks the allow list and issues this device a credential. Throws
    /// <c>signin.expired</c>, <c>signin.invalid</c>, <c>signin.not_allowed</c>, <c>signin.needs_authenticator</c> (a Martlet
    /// password login without an authenticator never adds a computer) or <c>signin.provider</c>; the attempt is used up either
    /// way.</summary>
    internal async ValueTask<(IssuedDeviceCredential Credential, GatewaySignInIdentity Identity)> CompleteAsync(string attemptId,
        string deviceId, string displayName, JsonElement proof, CancellationToken cancellationToken)
    {
        var who = await VerifyAttemptAsync(attemptId, proof, enroll: true, cancellationToken).ConfigureAwait(false);
        lock (gate)
        {
            var current = document = LoadLocked();
            RequireAllowedLocked(current, who, deviceId);
            var access = current.AccessOf(who.Provider, who.Subject);
            RequireDeviceFreeLocked(current, deviceId, who);
            List<GatewaySignInEnrollment> evicted = access is null ? [] : MakeRoomForFriendLocked(current, deviceId, who);
            credentials.RevokeDevice(deviceId, cancellationToken);
            var issued = credentials.Issue(deviceId, Martlet.Core.Network.NetworkRoster.CleanName(displayName, deviceId), [GatewayRole.Voice],
                cancellationToken, access == GatewaySignInDocument.FriendAccess ? GatewayAccess.Friend : GatewayAccess.Full);
            var next = current.Clone();
            next.Enrolled.RemoveAll(e => e.DeviceId == deviceId || evicted.Any(x => x.DeviceId == e.DeviceId));
            next.Enrolled.Add(new()
            {
                DeviceId = deviceId, Provider = who.Provider, Subject = who.Subject, Label = who.Label, EnrolledAt = clock.GetUtcNow(),
                Access = access, CredentialId = issued.CredentialId
            });
            var dropped = Trim(next, deviceId);
            try { SaveLocked(next); }
            catch (Exception error) when (error is not GatewayProtocolException)
            {
                if (access is not null)
                {
                    // Unrecorded, the friend's credential would never pass the friend check: take it back and say so.
                    credentials.RevokeCredential(issued.CredentialId);
                    throw new GatewayProtocolException("signin.unavailable");
                }
                log(LogLevels.Warn, "Could not save signin.json; the computer that signed in is paired, but a member desktop will ask for an Allow to let it into the network.");
            }
            foreach (var replaced in evicted.Concat(dropped.Where(d => d.Access is not null)))
            {
                if (replaced.CredentialId is { } id) credentials.RevokeCredential(id);
                log(LogLevels.Info, $"Revoked {replaced.DeviceId}, the oldest computer of {replaced.Label ?? replaced.Subject} ({replaced.Provider}) here: a friend " +
                    $"keeps at most {MaximumFriendDevices} computers and {MaximumEnrolled} sign-ins are recorded.");
            }
            log(LogLevels.Info, access is null
                ? $"{deviceId} paired by signing in as {Display(who)}."
                : $"{deviceId} paired by signing in as {Display(who)}, a friend: it may use this host's engines and nothing else.");
            return (issued, who);
        }
    }

    /// <summary>Computers one friend keeps here; signing in on another replaces their oldest.</summary>
    internal const int MaximumFriendDevices = 3;
    /// <summary>Friends' computers together; more are refused (signin.friends_full), so friends never fill the credential table
    /// (<see cref="GatewayCredentialStore.MaximumRegistrations"/>) the owner's computers need.</summary>
    internal const int MaximumFriendCredentials = 32;

    // Frees room for a friend's new computer and returns the enrollments it replaces (that friend's oldest beyond
    // MaximumFriendDevices). Friend credentials sign-in no longer records (signin.json edited or replaced) can never be used
    // again, so their slots are freed first; then all friends together stay under MaximumFriendCredentials.
    private List<GatewaySignInEnrollment> MakeRoomForFriendLocked(GatewaySignInDocument current, string deviceId, GatewaySignInIdentity who)
    {
        var live = credentials.ListRegistrations().Where(r => !r.Revoked && r.Access == GatewayAccess.Friend).ToList();
        foreach (var orphan in live.Where(r => !current.Enrolled.Any(e => e.CredentialId == r.CredentialId)).ToArray())
        {
            credentials.RevokeCredential(orphan.CredentialId);
            live.Remove(orphan);
        }
        var theirs = current.Enrolled.Where(e => e.Access is not null && e.Provider == who.Provider && e.Subject == who.Subject &&
            e.DeviceId != deviceId).OrderBy(e => e.EnrolledAt).ToList();
        var evicted = theirs.Take(Math.Max(0, theirs.Count - (MaximumFriendDevices - 1))).ToList();
        var others = live.Count(r => r.DeviceId != deviceId && !evicted.Any(e => e.CredentialId == r.CredentialId));
        GatewayRules.Require(others < MaximumFriendCredentials, "signin.friends_full");
        return evicted;
    }

    // Keeps at most MaximumEnrolled records: friends' oldest go first (the caller revokes their credentials); the owner's
    // computers' oldest only when theirs alone fill the list, as before. The record just added always stays.
    private static List<GatewaySignInEnrollment> Trim(GatewaySignInDocument next, string deviceId)
    {
        var over = next.Enrolled.Count - MaximumEnrolled;
        if (over <= 0) return [];
        var candidates = next.Enrolled.Where(e => e.DeviceId != deviceId);
        var dropped = candidates.Where(e => e.Access is not null).OrderBy(e => e.EnrolledAt)
            .Concat(candidates.Where(e => e.Access is null).OrderBy(e => e.EnrolledAt)).Take(over).ToList();
        next.Enrolled.RemoveAll(dropped.Contains);
        return dropped;
    }

    /// <summary>Who decides that a device ID belongs to an active member desktop of this host's network (those pair by their
    /// network key and never sign in). Set by the gateway.</summary>
    internal Func<string, bool>? IsMember { get; set; }

    // A sign-in may only replace this device's own earlier sign-in by the same identity: never a member desktop of the network,
    // a pairing made another way (a code, a card, a network key) or another identity's computer. So nobody signs in under
    // another computer's ID, to act as it or to revoke its pairing here.
    private void RequireDeviceFreeLocked(GatewaySignInDocument current, string deviceId, GatewaySignInIdentity who)
    {
        GatewayRules.Require(IsMember?.Invoke(deviceId) != true, "signin.device_taken");
        var enrolled = current.Enrolled.LastOrDefault(e => e.DeviceId == deviceId);
        var same = enrolled is not null && enrolled.Provider == who.Provider && enrolled.Subject == who.Subject;
        foreach (var live in credentials.ListRegistrations().Where(r => r.DeviceId == deviceId && !r.Revoked))
            GatewayRules.Require(same && (enrolled!.CredentialId is null || enrolled.CredentialId == live.CredentialId), "signin.device_taken");
    }

    /// <summary>Uses up attempt <paramref name="attemptId"/> and checks its proof: a Martlet password login ("owner": the owner's
    /// only; "martlet": any household account's, by user name) or a provider's answer. Returns who signed in; the allow list is
    /// the caller's to check. <paramref name="enroll"/>: the sign-in adds a computer, which a password login without an
    /// authenticator may not do.</summary>
    private async ValueTask<GatewaySignInIdentity> VerifyAttemptAsync(string attemptId, JsonElement proof, bool enroll,
        CancellationToken cancellationToken)
    {
        GatewaySignInAttempt? attempt;
        GatewaySignInDocument current;
        lock (gate)
        {
            GatewayRules.Require(storage is not null, "signin.unavailable");
            attempts.Remove(attemptId, out attempt);
            GatewayRules.Require(attempt is not null && attempt.ExpiresAt > clock.GetUtcNow(), "signin.expired");
            current = document = LoadLocked();
        }
        if (attempt!.Provider is OwnerProvider or MartletProvider) return VerifyPassword(current, proof, attempt.Provider, enroll);
        var config = current.Providers.FirstOrDefault(p => p.Id == attempt.Provider);
        var provider = config is null ? null : Providers(config);
        GatewayRules.Require(provider is not null, "signin.unavailable");
        var who = await provider!.VerifyAsync(attempt, proof, cancellationToken).ConfigureAwait(false);
        GatewayRules.Require(who.Provider == attempt.Provider && who.Subject is { Length: > 0 and <= 256 }, "signin.invalid");
        return who;
    }

    // The owner login and household password logins are always allowed while they exist; any other identity must be on the
    // allow list, and one that isn't is remembered under Refused so the owner can allow it.
    private void RequireAllowedLocked(GatewaySignInDocument current, GatewaySignInIdentity who, string deviceId)
    {
        if (who.Provider is OwnerProvider or MartletProvider)
        {
            GatewayRules.Require(current.Allows(who.Provider, who.Subject), "signin.invalid");
            return;
        }
        if (current.Allowed.Any(a => a.Provider == who.Provider && a.Subject == who.Subject)) return;
        log(LogLevels.Warn, $"Sign-in by {Display(who)} for {deviceId} refused: that identity is not on this host's allow list.");
        refused.RemoveAll(r => r.Identity.Provider == who.Provider && r.Identity.Subject == who.Subject);
        refused.Insert(0, (deviceId, who, clock.GetUtcNow()));
        if (refused.Count > MaximumRefused) refused.RemoveAt(refused.Count - 1);
        throw new GatewayProtocolException("signin.not_allowed");
    }

    /// <summary>Checks a Martlet password login: user name (any case), password and, when the login has an authenticator (the
    /// owner's always does), a current authenticator code that wasn't used yet or an unused recovery code (used up here). The
    /// owner's login signs in as the owner (provider "owner") whichever provider was asked; another account's as
    /// "martlet:&lt;lowercase user name&gt;".</summary>
    private GatewaySignInIdentity VerifyPassword(GatewaySignInDocument current, JsonElement proof, string provider, bool enroll)
    {
        string? Text(string name) => proof.ValueKind == JsonValueKind.Object && proof.TryGetProperty(name, out var value) &&
            value.ValueKind == JsonValueKind.String ? value.GetString() : null;
        var user = Text("user")?.Trim();
        var password = Text("password");
        var code = Text("code");
        IGatewayPasswordLogin? login = provider == OwnerProvider ? current.Owner : current.PasswordLogin(user);
        // An unknown user name costs the same verifier work as a known one.
        var verifier = (login ?? current.PasswordLogins().FirstOrDefault())?.Password ?? throw new GatewayProtocolException("signin.unavailable");
        var passwordOk = GatewayAccounts.VerifyPassword(password, verifier);
        var userOk = login is not null && string.Equals(user, login.User, StringComparison.OrdinalIgnoreCase);
        if (!passwordOk || !userOk) throw new GatewayProtocolException("signin.invalid");
        var owner = login is GatewayOwnerAccount;
        lock (gate)
        {
            var next = LoadLocked().Clone();
            var account = owner ? next.Owner : next.PasswordLogin(login!.User);
            // The same login with the same password as checked above (not removed or changed meanwhile).
            GatewayRules.Require(account is not null && account.User == login!.User && account.Password == login.Password &&
                account is GatewayOwnerAccount == owner, "signin.invalid");
            if (account!.TotpSecret is { } secret)
            {
                var step = Totp.Verify(secret, code, clock.GetUtcNow(), account.LastTotpStep);
                if (step is { } used) account.LastTotpStep = used;
                else if (GatewayAccounts.RecoveryVerifier(code) is { } typed &&
                    account.RecoveryCodes.FindIndex(r => crypto.FixedTimeEquals(System.Text.Encoding.ASCII.GetBytes(r),
                        System.Text.Encoding.ASCII.GetBytes(typed))) is >= 0 and var index)
                {
                    account.RecoveryCodes.RemoveAt(index);
                    log(LogLevels.Warn, (owner ? "The owner account" : $"Account login {account.User}") +
                        $" signed in with a recovery code; {account.RecoveryCodes.Count} left.");
                }
                else throw new GatewayProtocolException("signin.invalid");
                SaveLocked(next);
            }
            else GatewayRules.Require(!enroll, "signin.needs_authenticator");
        }
        return owner ? new(OwnerProvider, login!.User, login.User) : new(MartletProvider, GatewaySignInDocument.MartletSubject(login!.User), login.User);
    }

    /// <summary>A Prove sign-in on an already paired computer (<paramref name="deviceId"/>, the signed caller): checks the proof
    /// like <see cref="CompleteAsync"/> (a Martlet password login needs its authenticator only when it has one) and returns the
    /// account it proves and the login, for the host's attestation. Issues no credential. Throws what CompleteAsync throws,
    /// and <c>signin.no_account</c> when the identity is a friend's or is linked to no account here.</summary>
    internal async ValueTask<(GatewaySignInIdentity Identity, Guid AccountId, AccountLoginKey Login)> ProveAsync(string attemptId,
        string deviceId, JsonElement proof, CancellationToken cancellationToken, Guid? linkAccount = null)
    {
        var who = await VerifyAttemptAsync(attemptId, proof, enroll: false, cancellationToken).ConfigureAwait(false);
        lock (gate)
        {
            var current = document = LoadLocked();
            // Linking (W13): a provider identity that this host doesn't allow yet becomes a login of linkAccount, which the caller
            // (checked by the gateway) may act for; one that already proves another account, or is a friend's, can't be.
            if (linkAccount is { } link && who.Provider is not (OwnerProvider or MartletProvider))
            {
                var existing = current.Allowed.LastOrDefault(a => a.Provider == who.Provider && a.Subject == who.Subject);
                if (existing is null)
                {
                    var next = current.Clone();
                    GatewayRules.Require(next.Allowed.Count < GatewaySignInSettings.MaximumAllowed, "request.invalid");
                    next.Allowed.Add(new() { Provider = who.Provider, Subject = who.Subject, Label = who.Label, AddedAt = clock.GetUtcNow(), AccountId = link });
                    SaveLocked(next);
                    current = next;
                    log(LogLevels.Info, $"{deviceId} linked {Display(who)} to account {link:N} as one of its logins.");
                }
                else if (existing.Access is not null || current.AccountOf(who.Provider, who.Subject, DefaultOwnerAccount?.Invoke()) != link)
                {
                    log(LogLevels.Warn, $"{deviceId} tried to link {Display(who)} to account {link:N}, but it is a friend's or another account's login.");
                    throw new GatewayProtocolException("signin.login_taken");
                }
            }
            RequireAllowedLocked(current, who, deviceId);
            if (current.AccountOf(who.Provider, who.Subject, DefaultOwnerAccount?.Invoke()) is not { } account)
            {
                log(LogLevels.Warn, $"{deviceId} signed in as {Display(who)}, which is linked to no account here, so no account was proved.");
                throw new GatewayProtocolException("signin.no_account");
            }
            log(LogLevels.Info, $"{deviceId} proved account {account:N} by signing in as {Display(who)}.");
            return (who, account, current.LoginOf(who));
        }
    }

    /// <summary>The account <paramref name="who"/> signs in as here and its login (null for a friend, or an identity linked to no
    /// account yet).</summary>
    internal (Guid AccountId, AccountLoginKey Login)? AccountFor(GatewaySignInIdentity who)
    {
        lock (gate)
        {
            var current = document;
            return current.AccountOf(who.Provider, who.Subject, DefaultOwnerAccount?.Invoke()) is { } account ? (account, current.LoginOf(who)) : null;
        }
    }

    /// <summary>The owner's account when signin.json names none: derived from the network this host is in (docs/ACCOUNTS.md,
    /// migration), so a host links its existing owner login without waiting for a desktop. Set by the gateway; null result
    /// while the host is in no network.</summary>
    internal Func<Guid?>? DefaultOwnerAccount { get; set; }

    /// <summary>The effective owner account: signin.json's, else <see cref="DefaultOwnerAccount"/>.</summary>
    internal Guid? OwnerAccount()
    {
        lock (gate) return document.OwnerAccountId ?? DefaultOwnerAccount?.Invoke();
    }

    /// <summary>The identity that enrolled <paramref name="deviceId"/> by signing in as one of the owner's computers, while that
    /// device is still paired here and the identity is still allowed so; null otherwise (a friend's computer never is). A member
    /// desktop lets such a device into the network without a check number.</summary>
    internal (GatewaySignInIdentity Identity, DateTimeOffset At)? Attestation(string deviceId)
    {
        lock (gate)
        {
            if (storage is null) return null;
            var current = document;
            var enrolled = current.Enrolled.LastOrDefault(e => e.DeviceId == deviceId);
            if (enrolled is null || enrolled.Access is not null || !credentials.PairedDevices().Any(d => d.DeviceId == deviceId)) return null;
            var allowed = current.Allows(enrolled.Provider, enrolled.Subject) && current.AccessOf(enrolled.Provider, enrolled.Subject) is null;
            return allowed ? (new(enrolled.Provider, enrolled.Subject, enrolled.Label), enrolled.EnrolledAt) : null;
        }
    }

    /// <summary>How often a friend's requests read signin.json again, so a friend removed with martlet-host loses access within
    /// this time even while nobody signs in (a change from a member desktop applies at once).</summary>
    internal static readonly TimeSpan FriendRecheck = TimeSpan.FromSeconds(5);
    private DateTimeOffset friendsCheckedAt = DateTimeOffset.MinValue;

    /// <summary>Whether the friend's credential <paramref name="credentialId"/> is still allowed here: its sign-in is recorded and
    /// the identity is still allowed as a friend. False whenever sign-in isn't attached or readable, so a friend never gets in
    /// without it.</summary>
    internal bool FriendAllowed(string credentialId)
    {
        lock (gate)
        {
            if (storage is null) return false;
            var now = clock.GetUtcNow();
            if (now - friendsCheckedAt >= FriendRecheck || now < friendsCheckedAt)
            {
                document = LoadLocked();
                friendsCheckedAt = now;
            }
            var enrolled = document.Enrolled.LastOrDefault(e => e.CredentialId == credentialId);
            return enrolled is { Access: GatewaySignInDocument.FriendAccess } &&
                document.Allows(enrolled.Provider, enrolled.Subject) &&
                document.AccessOf(enrolled.Provider, enrolled.Subject) == GatewaySignInDocument.FriendAccess;
        }
    }

    /// <summary>Identities that signed in but weren't allowed, newest first (in memory, at most <see cref="MaximumRefused"/>), so
    /// the owner can allow the one that was theirs without looking up a provider's subject.</summary>
    internal IReadOnlyList<(string DeviceId, GatewaySignInIdentity Identity, DateTimeOffset At)> Refused()
    {
        lock (gate) return refused.Where(r => !document.Allows(r.Identity.Provider, r.Identity.Subject)).ToArray();
    }

    /// <summary>Remembers the network key a device that signed in here asks to join with, so a later removal names exactly
    /// that key.</summary>
    internal void RememberJoinKey(string deviceId, string key)
    {
        lock (gate)
        {
            if (storage is null) return;
            var current = LoadLocked();
            var index = current.Enrolled.FindLastIndex(e => e.DeviceId == deviceId);
            if (index < 0 || current.Enrolled[index].Key == key) return;
            var next = current.Clone();
            next.Enrolled[index] = next.Enrolled[index] with { Key = key };
            try { SaveLocked(next); }
            catch (Exception error) when (error is not OperationCanceledException) { }
        }
    }

    /// <summary>Computers to remove from the network because the sign-in they joined with is no longer allowed, as of
    /// <paramref name="roster"/>: records the roster already shows removed (with that key), or never listed, are forgotten.</summary>
    internal IReadOnlyList<GatewaySignInRemoval> Removals(Martlet.Core.Network.NetworkRoster? roster)
    {
        lock (gate)
        {
            if (storage is null) return [];
            var current = document = LoadLocked();
            if (current.Removed.Count == 0) return [];
            bool Pending(GatewaySignInRemoval r) =>
                roster?.Desktop(r.DeviceId) is { Removed: false } desktop && (r.Key is null || desktop.Key == r.Key);
            var pending = current.Removed.Where(Pending).ToArray();
            if (roster is not null && pending.Length != current.Removed.Count)
            {
                var next = current.Clone();
                next.Removed.RemoveAll(r => !Pending(r));
                try { SaveLocked(next); }
                catch (Exception error) when (error is not OperationCanceledException) { }
            }
            return pending;
        }
    }

    /// <summary>Why nobody can sign in to this host right now (<see cref="GatewaySignInSettings.BlockedReason"/>), or null.</summary>
    internal string? BlockedReason()
    {
        lock (gate) if (storage is null) return "signin.not_set_up";
        return GatewaySignInSettings.BlockedReason(Snapshot());
    }

    internal GatewaySignInDocument Snapshot()
    {
        lock (gate) return document = LoadLocked();
    }

    /// <summary>Applies an owner's change from a member desktop and saves it; returns new recovery codes when it made some.</summary>
    internal IReadOnlyList<string>? Change(GatewaySignInChange change, string by, CancellationToken cancellationToken)
    {
        lock (gate)
        {
            GatewayRules.Require(storage is not null, "signin.unavailable");
            var next = LoadLocked().Clone();
            var codes = GatewaySignInSettings.Apply(next, change, clock.GetUtcNow());
            var stale = next.Sweep(clock.GetUtcNow());
            SaveLocked(next);
            var revoked = RevokeLocked(stale);
            log(LogLevels.Info, $"{by} changed this host's sign-in settings ({change.Action})" +
                (revoked > 0 ? $"; revoked {revoked} credential(s) of computers enrolled by sign-ins no longer allowed." : "."));
            return codes;
        }
    }

    internal static bool IsLoopbackRedirect(string? redirectUri) =>
        Uri.TryCreate(redirectUri, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttp &&
        uri.Host is "127.0.0.1" or "[::1]" && !uri.IsDefaultPort && uri.Query.Length == 0 && uri.Fragment.Length == 0 &&
        uri.UserInfo.Length == 0 && redirectUri!.Length <= 128;

    private string Random() => Base64Url.Encode(crypto.RandomBytes(16));

    private static string Display(GatewaySignInIdentity who) =>
        new(who.ToString().Take(96).Select(c => char.IsControl(c) ? '?' : c).ToArray());
}

/// <summary>The owner's changes to a host's sign-in settings, applied the same way from a member desktop
/// (<c>POST /martlet/v1/signin/settings</c>) and from <c>martlet-host owner-signin</c> on the host.</summary>
internal static class GatewaySignInSettings
{
    internal const int MaximumAllowed = 32;
    internal const int MaximumProviders = 8;

    /// <summary>Whether sign-in is usable on a host with these settings: null when it is (an owner account, which always has
    /// an authenticator, or a configured provider with at least one allowed identity), otherwise why not:
    /// <c>signin.not_set_up</c> (no settings, or neither an owner account nor a provider) or
    /// <c>signin.no_allowed_identity</c> (providers, but none with an allowed identity, and no owner account). Pure: reads
    /// nothing, so martlet-host can ask it of signin.json while the service is stopped.</summary>
    internal static string? BlockedReason(GatewaySignInDocument? document)
    {
        if (document is null || document.Owner is null && document.Providers.Count == 0) return "signin.not_set_up";
        if (document.Owner is not null) return null;
        return document.Providers.Any(p => p.Kind is "oidc" or "discord" or "steam" && document.Allowed.Any(a => a.Provider == p.Id))
            ? null : "signin.no_allowed_identity";
    }

    internal static IReadOnlyList<string>? Apply(GatewaySignInDocument next, GatewaySignInChange change, DateTimeOffset now)
    {
        switch (change.Action)
        {
            case "owner":
            {
                var user = RequireUser(change.User);
                GatewayRules.Require(!next.Accounts.Any(a => SameUser(a.User, user)), "signin.user_taken");
                GatewayAccounts.RequirePassword(change.Password);
                GatewayAccounts.RequireTotpSecret(change.TotpSecret);
                // The owner proves the authenticator app took the secret before it becomes mandatory.
                var step = Totp.Verify(change.TotpSecret!, change.Code, now, -1);
                GatewayRules.Require(step is not null, "signin.invalid");
                if (change.AccountId is { } account) SetOwnerAccount(next, account);
                var (codes, verifiers) = GatewayAccounts.NewRecoveryCodes();
                next.Owner = new()
                {
                    User = user, Password = GatewayAccounts.HashPassword(change.Password!), TotpSecret = change.TotpSecret!.ToUpperInvariant(),
                    LastTotpStep = step!.Value, RecoveryCodes = verifiers, CreatedAt = now
                };
                return codes;
            }
            case "owner-account":
            {
                SetOwnerAccount(next, change.AccountId ?? throw new GatewayProtocolException("request.invalid"));
                return null;
            }
            case "account":
            {
                // Another household account's Martlet password login: a password (12+ characters, needed for a new login, kept
                // when an existing login's change leaves it out) and, optionally, an authenticator proven by a current code.
                var id = change.AccountId;
                GatewayRules.Require(id is { } value && value != Guid.Empty && value != next.OwnerAccountId, "request.invalid");
                var user = RequireUser(change.User);
                GatewayRules.Require(!(next.Owner is { } owner && SameUser(owner.User, user)) &&
                    !next.Accounts.Any(a => a.AccountId != id && SameUser(a.User, user)), "signin.user_taken");
                var existing = next.Accounts.FirstOrDefault(a => a.AccountId == id);
                GatewayRules.Require(existing is not null || change.Password is not null && next.Accounts.Count < MaximumAccounts, "request.invalid");
                long? step = null;
                if (change.TotpSecret is not null)
                {
                    GatewayAccounts.RequireTotpSecret(change.TotpSecret);
                    step = Totp.Verify(change.TotpSecret, change.Code, now, -1);
                    GatewayRules.Require(step is not null, "signin.invalid");
                }
                var login = existing ?? new GatewayAccountLogin
                {
                    AccountId = id!.Value, User = user, Password = GatewayAccounts.HashPassword(change.Password!), CreatedAt = now
                };
                login.User = user;
                if (existing is not null && change.Password is not null) login.Password = GatewayAccounts.HashPassword(change.Password);
                IReadOnlyList<string>? codes = null;
                if (step is { } proven)
                {
                    List<string> verifiers;
                    (codes, verifiers) = GatewayAccounts.NewRecoveryCodes();
                    login.TotpSecret = change.TotpSecret!.ToUpperInvariant();
                    login.LastTotpStep = proven;
                    login.RecoveryCodes = verifiers;
                }
                if (existing is null) next.Accounts.Add(login);
                return codes;
            }
            case "remove-account-authenticator":
            {
                var login = next.Accounts.FirstOrDefault(a => a.AccountId == change.AccountId) ?? throw new GatewayProtocolException("signin.unavailable");
                login.TotpSecret = null;
                login.LastTotpStep = 0;
                login.RecoveryCodes = [];
                return null;
            }
            case "remove-account":
            {
                GatewayRules.Require(change.AccountId is not null, "request.invalid");
                next.Accounts.RemoveAll(a => a.AccountId == change.AccountId);
                return null;
            }
            case "recovery-codes":
            {
                // The owner's login, or (with account_id) another account's login that has an authenticator.
                IGatewayPasswordLogin? login = change.AccountId is { } id && id != next.OwnerAccountId
                    ? next.Accounts.FirstOrDefault(a => a.AccountId == id)
                    : next.Owner;
                GatewayRules.Require(login?.TotpSecret is not null, "signin.unavailable");
                var (codes, verifiers) = GatewayAccounts.NewRecoveryCodes();
                login!.RecoveryCodes = verifiers;
                return codes;
            }
            case "remove-owner":
            {
                next.Owner = null;
                return null;
            }
            case "allow":
            {
                GatewayRules.Require(change.Provider is { Length: > 0 and <= 32 } && change.Provider != GatewaySignInService.OwnerProvider &&
                    change.Provider != GatewaySignInService.MartletProvider &&
                    change.Subject is { Length: > 0 and <= 256 } && change.Subject.All(c => !char.IsControl(c)) &&
                    (change.Label is null || change.Label.Length <= 128 && change.Label.All(c => !char.IsControl(c))) &&
                    change.Access is null or "member" or GatewaySignInDocument.FriendAccess, "request.invalid");
                // A household member's identity names the account it signs in as; a friend's never has one.
                GatewayRules.Require(change.AccountId is null || change.AccountId != Guid.Empty && change.Access != GatewaySignInDocument.FriendAccess,
                    "request.invalid");
                var previous = next.Allowed.LastOrDefault(a => a.Provider == change.Provider && a.Subject == change.Subject);
                next.Allowed.RemoveAll(a => a.Provider == change.Provider && a.Subject == change.Subject);
                GatewayRules.Require(next.Allowed.Count < MaximumAllowed, "request.invalid");
                var friend = change.Access == GatewaySignInDocument.FriendAccess;
                next.Allowed.Add(new()
                {
                    Provider = change.Provider!, Subject = change.Subject!, Label = change.Label, AddedAt = now,
                    Access = friend ? GatewaySignInDocument.FriendAccess : null,
                    // Allowing again without an account keeps the account the identity was linked to.
                    AccountId = friend ? null : change.AccountId ?? previous?.AccountId
                });
                return null;
            }
            case "link":
            {
                // Links an allowed identity to the account it signs in as (no account_id: back to the owner's account).
                var index = next.Allowed.FindLastIndex(a => a.Provider == change.Provider && a.Subject == change.Subject);
                GatewayRules.Require(index >= 0, "signin.unavailable");
                GatewayRules.Require(change.AccountId is null || change.AccountId != Guid.Empty && next.Allowed[index].Access is null, "request.invalid");
                next.Allowed[index] = next.Allowed[index] with { AccountId = change.AccountId };
                return null;
            }
            case "disallow":
            {
                next.Allowed.RemoveAll(a => a.Provider == change.Provider && a.Subject == change.Subject);
                return null;
            }
            case "link-login":
            {
                // W13: another host checked this provider identity and attested that it proves the account (the gateway checked
                // the attestation against the roster and the caller before this): allow it here as that account's login.
                var attested = ParseAttestation(change.Attestation);
                var login = attested.Login;
                GatewayRules.Require(login.Kind is "oidc" or "discord" or "steam" &&
                    next.Providers.Any(p => p.Id == login.Provider && p.Kind == login.Kind), "signin.unavailable");
                GatewayRules.Require(change.Label is null || change.Label.Length <= 128 && change.Label.All(c => !char.IsControl(c)), "request.invalid");
                var existing = next.Allowed.LastOrDefault(a => a.Provider == login.Provider && a.Subject == login.Subject);
                if (existing is not null)
                {
                    GatewayRules.Require(existing.Access is null && (existing.AccountId ?? attested.AccountId) == attested.AccountId, "signin.login_taken");
                    // A member identity linked to no account proves the owner's: the gateway checked that the attested account is the
                    // owner's (signin.login_taken otherwise), so it stays as it is.
                    if (existing.AccountId is null) return null;
                }
                next.Allowed.RemoveAll(a => a.Provider == login.Provider && a.Subject == login.Subject);
                GatewayRules.Require(next.Allowed.Count < MaximumAllowed, "request.invalid");
                next.Allowed.Add(new()
                {
                    Provider = login.Provider, Subject = login.Subject, Label = change.Label ?? existing?.Label, AddedAt = now, AccountId = attested.AccountId
                });
                return null;
            }
            case "provider":
            {
                var provider = change.ProviderConfig ?? throw new GatewayProtocolException("request.invalid");
                provider.Validate();
                var existing = next.Providers.FirstOrDefault(p => p.Id == provider.Id);
                // A change that leaves the client secret out keeps the one saved (the desktop never reads it back).
                if (provider.ClientSecret is null && existing?.Kind == provider.Kind) provider = provider with { ClientSecret = existing.ClientSecret };
                next.Providers.RemoveAll(p => p.Id == provider.Id);
                GatewayRules.Require(next.Providers.Count < MaximumProviders, "request.invalid");
                next.Providers.Add(provider);
                return null;
            }
            case "remove-provider":
            {
                next.Providers.RemoveAll(p => p.Id == change.Id);
                next.Allowed.RemoveAll(a => a.Provider == change.Id);
                return null;
            }
            default:
                throw new GatewayProtocolException("request.invalid");
        }
    }

    /// <summary>Martlet password logins of household accounts besides the owner's.</summary>
    internal const int MaximumAccounts = 16;

    private static string RequireUser(string? value)
    {
        var user = value?.Trim();
        GatewayRules.Require(user is { Length: > 0 and <= 64 } && user.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-' or '@'),
            "request.invalid");
        return user!;
    }

    private static bool SameUser(string left, string right) => string.Equals(left, right, StringComparison.OrdinalIgnoreCase);

    /// <summary>The account attestation a <c>link-login</c> change carries; <c>request.invalid</c> when there is none or it is
    /// malformed. Its signature and lifetime are checked by the gateway against the roster, not here.</summary>
    internal static AccountAttestation ParseAttestation(JsonElement? element)
    {
        GatewayRules.Require(element is { ValueKind: JsonValueKind.Object }, "request.invalid");
        try { return AccountAttestation.Parse(element!.Value); }
        catch (FormatException) { throw new GatewayProtocolException("request.invalid"); }
    }

    // The household owner's account (docs/ACCOUNTS.md): the owner login and every member identity without an account of its
    // own sign in as it. Another account's password login can't hold that ID.
    private static void SetOwnerAccount(GatewaySignInDocument next, Guid account)
    {
        GatewayRules.Require(account != Guid.Empty && !next.Accounts.Any(a => a.AccountId == account), "request.invalid");
        next.OwnerAccountId = account;
    }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record GatewaySignInChange
{
    public required string Action { get; init; }
    public string? User { get; init; }
    public string? Password { get; init; }
    public string? TotpSecret { get; init; }
    public string? Code { get; init; }
    public string? Provider { get; init; }
    public string? Subject { get; init; }
    public string? Label { get; init; }
    public string? Id { get; init; }
    /// <summary>For <c>allow</c>: "member" (or absent) lets the identity's computers join the network as the owner's own;
    /// "friend" shares only this host's engines with them.</summary>
    public string? Access { get; init; }
    /// <summary>The household account (docs/ACCOUNTS.md) of <c>owner</c>, <c>owner-account</c>, <c>account</c>,
    /// <c>remove-account</c>, <c>remove-account-authenticator</c>, <c>recovery-codes</c>, <c>allow</c> and <c>link</c>.</summary>
    public Guid? AccountId { get; init; }
    [JsonPropertyName("provider_config")]
    public GatewaySignInProviderConfig? ProviderConfig { get; init; }
    /// <summary>For <c>link-login</c> (W13): another host's <see cref="AccountAttestation"/> that a provider identity proves an
    /// account on the calling computer.</summary>
    public JsonElement? Attestation { get; init; }
}

/// <summary>A configured sign-in provider. <see cref="Kind"/> is "oidc" (any OpenID Connect issuer: Authentik, Authelia,
/// Keycloak, Pocket ID, Google), "discord" or "steam". <see cref="ClientSecret"/> never leaves the host.</summary>
internal sealed record GatewaySignInProviderConfig
{
    public required string Id { get; init; }
    public required string Kind { get; init; }
    public required string Name { get; init; }
    public string? Issuer { get; init; }
    public string? ClientId { get; init; }
    public string? ClientSecret { get; init; }
    public string? Scopes { get; init; }
    /// <summary>The loopback port the computer must listen on, for providers that only take redirect URIs registered
    /// exactly (Discord): http://127.0.0.1:&lt;port&gt;/. Null lets the computer pick a free port (RFC 8252).</summary>
    public int? RedirectPort { get; init; }

    internal void Validate()
    {
        GatewayRules.Require(RedirectPort is null or (>= 1024 and <= 65535), "request.invalid");
        GatewayRules.Require(Id is { Length: > 0 and <= 32 } && Id != GatewaySignInService.OwnerProvider && Id != GatewaySignInService.MartletProvider &&
            Id.All(c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c == '-'), "request.invalid");
        GatewayRules.Require(Kind is "oidc" or "discord" or "steam", "request.invalid");
        GatewayRules.Require(Name is { Length: > 0 and <= 64 } && Name.All(c => !char.IsControl(c)), "request.invalid");
        GatewayRules.Require(Issuer is null || Issuer.Length <= 256 && Uri.TryCreate(Issuer, UriKind.Absolute, out var issuer) &&
            issuer.Scheme == Uri.UriSchemeHttps, "request.invalid");
        GatewayRules.Require(ClientId is null || ClientId.Length is > 0 and <= 256 && ClientId.All(c => c > ' ' && c < 127), "request.invalid");
        GatewayRules.Require(ClientSecret is null || ClientSecret.Length is > 0 and <= 512 && ClientSecret.All(c => c > ' ' && c < 127), "request.invalid");
        GatewayRules.Require(Scopes is null || Scopes.Length <= 256 && Scopes.All(c => c >= ' ' && c < 127), "request.invalid");
        GatewayRules.Require(Kind == "steam" || ClientId is not null, "request.invalid");
        GatewayRules.Require(Kind != "oidc" || Issuer is not null, "request.invalid");
        GatewayRules.Require(Kind != "discord" || ClientSecret is not null, "request.invalid");
    }
}

/// <summary>A Martlet password login on a host: the owner's (<see cref="GatewayOwnerAccount"/>, authenticator mandatory) or
/// another household account's (<see cref="GatewayAccountLogin"/>, authenticator optional).</summary>
internal interface IGatewayPasswordLogin
{
    string User { get; }
    GatewayPasswordVerifier Password { get; }
    string? TotpSecret { get; }
    long LastTotpStep { get; set; }
    List<string> RecoveryCodes { get; set; }
}

internal sealed class GatewayOwnerAccount : IGatewayPasswordLogin
{
    public required string User { get; set; }
    public required GatewayPasswordVerifier Password { get; set; }
    public required string TotpSecret { get; set; }
    public long LastTotpStep { get; set; }
    public List<string> RecoveryCodes { get; set; } = [];
    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>Another household account's Martlet password login (docs/ACCOUNTS.md): the account it proves, a user name unique
/// on the host (any case), the password verifier and an optional authenticator with its recovery codes. Signing in with it
/// proves the account (an attestation); adding a computer with it needs the authenticator.</summary>
internal sealed class GatewayAccountLogin : IGatewayPasswordLogin
{
    public required Guid AccountId { get; set; }
    public required string User { get; set; }
    public required GatewayPasswordVerifier Password { get; set; }
    public string? TotpSecret { get; set; }
    public long LastTotpStep { get; set; }
    public List<string> RecoveryCodes { get; set; } = [];
    public DateTimeOffset CreatedAt { get; set; }
}

internal sealed record GatewayAllowedSignIn
{
    public required string Provider { get; init; }
    public required string Subject { get; init; }
    public string? Label { get; init; }
    public DateTimeOffset AddedAt { get; init; }
    /// <summary>Null: the owner's own computers (they join the network). <see cref="GatewaySignInDocument.FriendAccess"/>: a friend
    /// the owner shares this host with (this host's engines only, never the network).</summary>
    public string? Access { get; init; }
    /// <summary>For a household member's identity (access null): the account it signs in as; null means the owner's account
    /// (<see cref="GatewaySignInDocument.OwnerAccountId"/>), as before accounts. Always null for a friend.</summary>
    public Guid? AccountId { get; init; }
}

internal sealed record GatewaySignInEnrollment
{
    public required string DeviceId { get; init; }
    public required string Provider { get; init; }
    public required string Subject { get; init; }
    public string? Label { get; init; }
    public DateTimeOffset EnrolledAt { get; init; }
    /// <summary>The network key the device asked to join with through this host (null until it asks).</summary>
    public string? Key { get; init; }
    /// <summary>The access the identity had when it signed in (null or "friend"); a change of access sweeps the enrollment.</summary>
    public string? Access { get; init; }
    /// <summary>The credential the sign-in issued (null for enrollments older than this field).</summary>
    public string? CredentialId { get; init; }
}

/// <summary>A computer that joined (or may join) the network through a sign-in that is no longer allowed. Member desktops
/// remove it from the roster (signed by them) on their next sync, so every host revokes it; the host forgets the record once
/// the roster shows it removed.</summary>
internal sealed record GatewaySignInRemoval
{
    public required string DeviceId { get; init; }
    public string? Key { get; init; }
    public required string Provider { get; init; }
    public required string Subject { get; init; }
    public string? Label { get; init; }
    public DateTimeOffset At { get; init; }
}

/// <summary>signin.json: everything a host needs to let computers sign in. Holds secrets (the authenticator secret, provider
/// client secrets) and verifiers, so it stays 0600 for the service owner and is never served whole.</summary>
internal sealed class GatewaySignInDocument
{
    internal const int MaximumBytes = 65_536;
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        MaxDepth = 8,
        WriteIndented = true
    };

    public int SchemaVersion { get; set; } = 1;
    /// <summary>The household owner's account ID (docs/ACCOUNTS.md): the owner login and member identities without an account
    /// of their own prove it. Null until a member desktop (or martlet-host) sets it.</summary>
    public Guid? OwnerAccountId { get; set; }
    public GatewayOwnerAccount? Owner { get; set; }
    /// <summary>Other household accounts' Martlet password logins.</summary>
    public List<GatewayAccountLogin> Accounts { get; set; } = [];
    public List<GatewaySignInProviderConfig> Providers { get; set; } = [];
    public List<GatewayAllowedSignIn> Allowed { get; set; } = [];
    public List<GatewaySignInEnrollment> Enrolled { get; set; } = [];
    public List<GatewaySignInRemoval> Removed { get; set; } = [];
    internal const int MaximumRemoved = 32;

    internal byte[] Write()
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(this, Json);
        GatewayRules.Require(bytes.Length <= MaximumBytes, "request.invalid");
        return bytes;
    }

    internal static GatewaySignInDocument Parse(byte[] bytes)
    {
        if (bytes.Length > MaximumBytes) throw new InvalidDataException("signin.json is too large.");
        var parsed = JsonSerializer.Deserialize<GatewaySignInDocument>(bytes, Json) ?? throw new InvalidDataException("signin.json is empty.");
        if (parsed.SchemaVersion != 1) throw new InvalidDataException("signin.json was written by a newer Martlet.");
        parsed.Providers ??= [];
        parsed.Accounts ??= [];
        parsed.Allowed ??= [];
        parsed.Enrolled ??= [];
        parsed.Removed ??= [];
        foreach (var provider in parsed.Providers) provider.Validate();
        return parsed;
    }

    internal GatewaySignInDocument Clone() => Parse(Write());

    /// <summary>The access value of an identity allowed as a friend.</summary>
    internal const string FriendAccess = "friend";

    /// <summary>Whether <paramref name="provider"/>/<paramref name="subject"/> may sign in: the owner account's own name, a
    /// household password login ("martlet", lowercase user name), or an identity on the allow list.</summary>
    internal bool Allows(string provider, string subject) => provider switch
    {
        GatewaySignInService.OwnerProvider => Owner?.User == subject,
        GatewaySignInService.MartletProvider => Accounts.Any(a => MartletSubject(a.User) == subject),
        _ => Allowed.Any(a => a.Provider == provider && a.Subject == subject)
    };

    /// <summary>What an allowed identity gets: null for the owner's computers (the owner account always), "friend" for a friend.</summary>
    internal string? AccessOf(string provider, string subject) => provider is GatewaySignInService.OwnerProvider or GatewaySignInService.MartletProvider
        ? null
        : Allowed.LastOrDefault(a => a.Provider == provider && a.Subject == subject)?.Access;

    /// <summary>The account an allowed identity signs in as: the owner login's and member identities' without their own is
    /// <see cref="OwnerAccountId"/> (or <paramref name="defaultOwner"/>, the owner account derived from the host's network, when
    /// none is set); a household password login's is its account; a friend has none.</summary>
    internal Guid? AccountOf(string provider, string subject, Guid? defaultOwner = null) => provider switch
    {
        GatewaySignInService.OwnerProvider => Owner?.User == subject ? OwnerAccountId ?? defaultOwner : null,
        GatewaySignInService.MartletProvider => Accounts.FirstOrDefault(a => MartletSubject(a.User) == subject)?.AccountId,
        _ => Allowed.LastOrDefault(a => a.Provider == provider && a.Subject == subject) is { Access: null } member
            ? member.AccountId ?? OwnerAccountId ?? defaultOwner
            : null
    };

    /// <summary>The login of docs/ACCOUNTS.md an identity signed in with: "martlet" for a password login (the owner's too), else
    /// the provider's kind and ID.</summary>
    internal AccountLoginKey LoginOf(GatewaySignInIdentity who) => who.Provider is GatewaySignInService.OwnerProvider or GatewaySignInService.MartletProvider
        ? AccountLoginKey.ForPassword(who.Label ?? who.Subject)
        : AccountLoginKey.ForProvider(Providers.FirstOrDefault(p => p.Id == who.Provider)?.Kind ?? AccountLoginKinds.Oidc, who.Provider, who.Subject);

    /// <summary>The password logins: the owner's first, then the other accounts'.</summary>
    internal IEnumerable<IGatewayPasswordLogin> PasswordLogins() => Owner is null ? Accounts : Accounts.Prepend<IGatewayPasswordLogin>(Owner);

    internal IGatewayPasswordLogin? PasswordLogin(string? user) =>
        user is null ? null : PasswordLogins().FirstOrDefault(l => string.Equals(l.User, user.Trim(), StringComparison.OrdinalIgnoreCase));

    /// <summary>A password login's subject in the Login contract: its user name in lowercase.</summary>
    internal static string MartletSubject(string user) => user.Trim().ToLowerInvariant();

    // An enrollment stays while its identity may still sign in with the access it signed in with.
    private bool Current(GatewaySignInEnrollment e) => Allows(e.Provider, e.Subject) && AccessOf(e.Provider, e.Subject) == e.Access;

    /// <summary>Drops (and returns) the enrollments of identities that may no longer sign in, or no longer with the access they
    /// signed in with; their computers lose access here. The owner's computers among them are recorded in <see cref="Removed"/>
    /// so member desktops remove them from the network too (a friend's computer never joined it).</summary>
    internal IReadOnlyList<GatewaySignInEnrollment> Sweep(DateTimeOffset now)
    {
        var stale = Enrolled.Where(e => !Current(e)).ToArray();
        Enrolled.RemoveAll(e => !Current(e));
        foreach (var enrollment in stale.Where(e => e.Access is null))
        {
            Removed.RemoveAll(r => r.DeviceId == enrollment.DeviceId);
            Removed.Add(new()
            {
                DeviceId = enrollment.DeviceId, Key = enrollment.Key, Provider = enrollment.Provider, Subject = enrollment.Subject,
                Label = enrollment.Label, At = now
            });
        }
        if (Removed.Count > MaximumRemoved) Removed.RemoveRange(0, Removed.Count - MaximumRemoved);
        return stale;
    }

    /// <summary>The enrollments a change would sweep, without changing anything (for martlet-host status and previews).</summary>
    internal IReadOnlyList<GatewaySignInEnrollment> WouldSweep() => Enrolled.Where(e => !Current(e)).ToArray();
}
