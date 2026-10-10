using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Text.Json.Serialization;
using Martlet.Core.Accounts;
using Martlet.Core.Logs;
using Microsoft.AspNetCore.Http;

namespace Martlet.Gateway;

/// <summary>
/// Sign-in enrollment routes (<see cref="GatewaySignInService"/>). Anyone may call the first three, before pairing, over TLS
/// the computer pinned from the owner's invite; every call goes through the request guard (the dispatcher's per-address
/// budget and lockout, then here the per-account lockout and the audit of each sign-in's outcome):
/// <list type="bullet">
/// <item><c>GET /martlet/v1/signin</c>: the ways to sign in here (nothing secret).</item>
/// <item><c>POST /martlet/v1/signin/begin</c>: starts an attempt (state, nonce and, for a provider, the browser URL built
/// from the computer's PKCE challenge and loopback redirect).</item>
/// <item><c>POST /martlet/v1/signin/complete</c>: the proof (owner account: user, password, authenticator or recovery code;
/// a provider: what the browser brought back) for a device ID and name; answers 201 with a device credential like pairing,
/// and an account attestation when the identity is linked to an account and this host is in a network.</item>
/// </list>
/// <c>POST /martlet/v1/signin/prove</c> is a Prove sign-in (docs/ACCOUNTS.md) by a paired computer that is already in: a
/// signed request with the attempt and its proof (a Martlet password login, "martlet" or "owner", or a provider); it answers
/// 200 with the host's signed <see cref="AccountAttestation"/> for that computer and issues no credential.
/// <c>GET</c>/<c>POST /martlet/v1/signin/settings</c> read and change the owner account, household account logins, providers
/// and allow list; they need a signed request from a member desktop of this host's network (any paired desktop while the host
/// is in no network) and never return a secret.
/// </summary>
internal sealed partial class GatewayHttpApplication
{
    internal const string SignInPath = "/martlet/v1/signin";
    internal const string SignInBeginPath = "/martlet/v1/signin/begin";
    internal const string SignInCompletePath = "/martlet/v1/signin/complete";
    internal const string SignInProvePath = "/martlet/v1/signin/prove";
    internal const string SignInSettingsPath = "/martlet/v1/signin/settings";
    internal const string SignInRouteClass = "signin";
    private const int MaximumSignInRequestBytes = 16_384;

    private GatewaySignInService? signIn;

    internal TimeProvider Clock => clock;

    /// <summary>The host's current TLS certificate with its private key, which signs account attestations (its key is the one
    /// the roster pins). Set when the listener starts; null before.</summary>
    internal Func<X509Certificate2>? SigningCertificate { get; set; }

    internal GatewaySignInService SignIn => signIn ?? throw new InvalidOperationException("Sign-in is not initialized.");

    internal void InitializeSignIn(GatewayCredentialStore credentials)
    {
        var providers = new GatewaySignInProviders(clock);
        signIn = new(credentials, clock, crypto, (level, message) => Logs.Own(level, message))
        {
            Providers = providers.Create,
            IsMember = deviceId => Network.Roster?.Desktop(deviceId) is { Removed: false },
            DefaultOwnerAccount = () => Network.State == "bound" && Network.Roster is { } roster ? OwnerAccount.IdFor(roster.NetworkId) : null
        };
        authenticator.FriendAllowed = principal => SignIn.FriendAllowed(principal.CredentialId);
    }

    private static bool IsSignInTarget(string rawTarget) =>
        rawTarget is SignInPath or SignInBeginPath or SignInCompletePath or SignInProvePath or SignInSettingsPath;

    private async ValueTask InvokeSignInAsync(HttpContext context, string rawTarget)
    {
        if (rawTarget == SignInSettingsPath)
        {
            await InvokeSignInSettingsAsync(context).ConfigureAwait(false);
            return;
        }
        if (rawTarget == SignInProvePath)
        {
            await InvokeSignInProveAsync(context).ConfigureAwait(false);
            return;
        }
        if (rawTarget == SignInPath)
        {
            GatewayRules.Require(context.Request.Method == HttpMethods.Get, "request.invalid");
            EnsureEmptyRequest(context.Request);
            var providers = SignIn.Available().Select(p => new SignInProviderDocument { Id = p.Id, Kind = p.Kind, Name = p.Name, RedirectPort = p.RedirectPort }).ToArray();
            await WriteJsonAsync(context, StatusCodes.Status200OK, new SignInProvidersDocument
            {
                ProtocolVersion = GatewayProtocolVersion.Current, HostId = identity.HostId, Providers = providers,
                MartletSignIn = SignIn.MartletAvailable()
            }).ConfigureAwait(false);
            return;
        }
        GatewayRules.Require(context.Request.Method == HttpMethods.Post, "request.invalid");
        if (rawTarget == SignInBeginPath)
        {
            var begin = await ReadSignInAsync<SignInBeginBody>(context.Request, context.RequestAborted).ConfigureAwait(false);
            begin.ProtocolVersion.Validate();
            GatewayRules.Require(begin.Provider is { Length: > 0 and <= 32 }, "request.invalid");
            var (attempt, url) = await SignIn.BeginAsync(begin.Provider, begin.CodeChallenge, begin.RedirectUri, context.RequestAborted)
                .ConfigureAwait(false);
            await WriteJsonAsync(context, StatusCodes.Status200OK, new SignInAttemptDocument
            {
                ProtocolVersion = GatewayProtocolVersion.Current, HostId = identity.HostId, AttemptId = attempt.Id,
                Provider = attempt.Provider, State = attempt.State, Nonce = attempt.Nonce, ExpiresAt = attempt.ExpiresAt, AuthorizeUrl = url
            }).ConfigureAwait(false);
            return;
        }

        var complete = await ReadSignInAsync<SignInCompleteBody>(context.Request, context.RequestAborted).ConfigureAwait(false);
        complete.ProtocolVersion.Validate();
        GatewayRules.Identifier(complete.DeviceId);
        GatewayRules.Token(complete.DisplayName, 64);
        GatewayRules.Require(Base64Url.TryDecode(complete.AttemptId, 16, out _), "request.invalid");
        // The claimed account name (owner account) counts toward lockout per identity as well as per address.
        var claimed = Claimed(complete.Proof);
        var request = Admit(context, claimed);
        try
        {
            var (credential, who) = await SignIn.CompleteAsync(complete.AttemptId, complete.DeviceId, complete.DisplayName, complete.Proof,
                context.RequestAborted).ConfigureAwait(false);
            Guard.Record(request with { Subject = Subject(who) }, GatewayGuardOutcome.Success, "signin.ok", Subject(who));
            // The computer that just signed in also gets the account it proved, attested, when this host can say so.
            var account = credential.Access == GatewayAccess.Friend ? null : SignIn.AccountFor(who);
            await WriteJsonAsync(context, StatusCodes.Status201Created, new SignInResponseDocument
            {
                ProtocolVersion = GatewayProtocolVersion.Current, HostId = identity.HostId, CredentialId = credential.CredentialId,
                CredentialSecret = credential.Secret.Reveal(), DeviceId = credential.DeviceId, Roles = credential.Roles,
                Lifetime = credential.Lifetime,
                SignedIn = new() { Provider = who.Provider, Subject = who.Subject, Label = who.Label },
                Access = credential.Access == GatewayAccess.Friend ? GatewaySignInDocument.FriendAccess : "member",
                AccountId = account?.AccountId,
                Attestation = account is { } proved ? TryAttest(proved.AccountId, credential.DeviceId, proved.Login, AccountAttestation.DefaultLifetime) : null
            }).ConfigureAwait(false);
        }
        catch (GatewayProtocolException error) when (error.Failure.Code.StartsWith("signin.", StringComparison.Ordinal))
        {
            Guard.Record(request, GatewayGuardOutcome.Failure, error.Failure.Code, claimed);
            throw;
        }
    }

    private static string? Claimed(JsonElement proof) =>
        proof.ValueKind == JsonValueKind.Object && proof.TryGetProperty("user", out var user) && user.ValueKind == JsonValueKind.String
            ? "owner:" + user.GetString()?.Trim().ToLowerInvariant() : null;

    /// <summary>A Prove sign-in by a paired computer that is already in (not a friend's): verifies the attempt's proof and
    /// answers the host's signed statement that the account proved itself on that computer.</summary>
    private async ValueTask InvokeSignInProveAsync(HttpContext context)
    {
        GatewayRules.Require(context.Request.Method == HttpMethods.Post, "request.invalid");
        var body = await ReadInferenceBodyAsync(context.Request, MaximumSignInRequestBytes, context.RequestAborted).ConfigureAwait(false);
        var caller = authenticator.Authenticate(context.Request, crypto.Sha256(body));
        var prove = ParseNetworkBody<SignInProveBody>(body);
        prove.ProtocolVersion.Validate();
        GatewayRules.Require(Base64Url.TryDecode(prove.AttemptId, 16, out _), "request.invalid");
        var lifetime = prove.LifetimeSeconds is { } seconds ? TimeSpan.FromSeconds(seconds) : AccountAttestation.DefaultLifetime;
        GatewayRules.Require(lifetime >= AccountAttestation.MinimumLifetime && lifetime <= AccountAttestation.MaximumLifetime, "request.invalid");
        // Before the proof is checked (and the attempt used up): this host must be able to sign at all.
        _ = AttestationKey();
        var claimed = Claimed(prove.Proof);
        var request = Admit(context, claimed);
        // Linking a provider login (W13): only for an account this computer may act for (bound to it here, or an owner's or admin's
        // computer), or any member computer while the household has no accounts yet.
        if (prove.LinkAccountId is { } link)
            GatewayRules.Require(link != Guid.Empty && (!Accounts.Current.Live.Any() ||
                Accounts.Current.SignedInOn(caller.DeviceId).Any(a => a.Id == link || AccountRoles.ManagesHousehold(a.Role))), "signin.denied");
        try
        {
            var (who, accountId, login) = await SignIn.ProveAsync(prove.AttemptId, caller.DeviceId, prove.Proof, context.RequestAborted, prove.LinkAccountId)
                .ConfigureAwait(false);
            var attestation = TryAttest(accountId, caller.DeviceId, login, lifetime) ?? throw new GatewayProtocolException("signin.unavailable");
            Guard.Record(request with { Subject = Subject(who) }, GatewayGuardOutcome.Success, "signin.proved", Subject(who));
            await WriteJsonAsync(context, StatusCodes.Status200OK, new SignInProofDocument
            {
                ProtocolVersion = GatewayProtocolVersion.Current, HostId = identity.HostId, DeviceId = caller.DeviceId,
                SignedIn = new() { Provider = who.Provider, Subject = who.Subject, Label = who.Label }, AccountId = accountId,
                Attestation = attestation
            }).ConfigureAwait(false);
        }
        catch (GatewayProtocolException error) when (error.Failure.Code.StartsWith("signin.", StringComparison.Ordinal))
        {
            Guard.Record(request, GatewayGuardOutcome.Failure, error.Failure.Code, claimed);
            throw;
        }
    }

    /// <summary>The network this host vouches in and the certificate it signs with: <c>signin.no_network</c> while the host is
    /// in no network (nobody could check the statement), <c>signin.unavailable</c> before its listener started.</summary>
    private (string NetworkId, X509Certificate2 Certificate) AttestationKey()
    {
        var roster = Network.State == "bound" ? Network.Roster : null;
        GatewayRules.Require(roster?.Host(identity.HostId) is { Removed: false }, "signin.no_network");
        X509Certificate2? certificate;
        try { certificate = SigningCertificate?.Invoke(); }
        catch (Exception error) when (error is not OperationCanceledException) { certificate = null; }
        GatewayRules.Require(certificate is not null, "signin.unavailable");
        return (roster!.NetworkId, certificate!);
    }

    /// <summary>The signed attestation as JSON, or null when this host can't make one now (no network, no key yet).</summary>
    private JsonElement? TryAttest(Guid accountId, string deviceId, AccountLoginKey login, TimeSpan lifetime)
    {
        try
        {
            var (networkId, certificate) = AttestationKey();
            var attestation = AccountAttestation.Issue(networkId, identity.HostId, accountId, deviceId, login, clock.GetUtcNow(), lifetime, certificate);
            if (AccountAttestation.KeyFingerprint(attestation.HostKey) != identity.SpkiFingerprint) return null;
            using var document = JsonDocument.Parse(attestation.Write());
            return document.RootElement.Clone();
        }
        catch (Exception error) when (error is GatewayProtocolException or ArgumentException or System.Security.Cryptography.CryptographicException)
        {
            return null;
        }
    }

    private GatewayGuardRequest Admit(HttpContext context, string? subject)
    {
        var request = GatewayGuard.For(context, SignInRouteClass, subject);
        // The dispatcher already spent this address's budget and checked its lockout; this adds the claimed account's.
        if (subject is not null && !Guard.TryAdmit(request, out var retryAfter)) throw GatewayGuard.Throttled(context, retryAfter);
        return request;
    }

    private static string Subject(GatewaySignInIdentity who) => who.Provider + ":" + who.Subject;

    private async ValueTask InvokeSignInSettingsAsync(HttpContext context)
    {
        GatewayPrincipal caller;
        IReadOnlyList<string>? codes = null;
        if (context.Request.Method == HttpMethods.Get)
        {
            EnsureEmptyRequest(context.Request);
            caller = authenticator.Authenticate(context.Request);
            RequireSignInOwner(caller);
        }
        else
        {
            GatewayRules.Require(context.Request.Method == HttpMethods.Post, "request.invalid");
            var body = await ReadInferenceBodyAsync(context.Request, MaximumSignInRequestBytes, context.RequestAborted).ConfigureAwait(false);
            caller = authenticator.Authenticate(context.Request, crypto.Sha256(body));
            RequireSignInOwner(caller);
            var change = ParseNetworkBody<GatewaySignInChange>(body);
            GatewayRules.Require(GatewaySignInRoles.Allowed(Accounts.Current, caller.DeviceId, change, SignIn.Snapshot()), "signin.denied");
            if (change.Action == "link-login") RequireLinkAttestation(change, caller);
            codes = SignIn.Change(change, caller.Caller, context.RequestAborted);
        }
        var current = SignIn.Snapshot();
        await WriteJsonAsync(context, StatusCodes.Status200OK, new SignInSettingsDocument
        {
            ProtocolVersion = GatewayProtocolVersion.Current, HostId = identity.HostId, Attached = SignIn.Attached,
            BlockedReason = SignIn.Attached ? GatewaySignInSettings.BlockedReason(current) : "signin.not_set_up",
            OwnerAccountId = current.OwnerAccountId ?? SignIn.DefaultOwnerAccount?.Invoke(),
            Owner = current.Owner is { } owner ? new() { User = owner.User, RecoveryCodesLeft = owner.RecoveryCodes.Count, CreatedAt = owner.CreatedAt } : null,
            Accounts = current.Accounts.Select(a => new SignInAccountDocument
            {
                AccountId = a.AccountId, User = a.User, HasAuthenticator = a.TotpSecret is not null, RecoveryCodesLeft = a.RecoveryCodes.Count,
                CreatedAt = a.CreatedAt
            }).ToArray(),
            Providers = current.Providers.Select(p => new SignInProviderSettingsDocument
            {
                Id = p.Id, Kind = p.Kind, Name = p.Name, Issuer = p.Issuer, ClientId = p.ClientId, Scopes = p.Scopes,
                RedirectPort = p.RedirectPort, HasClientSecret = p.ClientSecret is not null
            }).ToArray(),
            Allowed = current.Allowed.Select(a => new SignInAllowedDocument
            {
                Provider = a.Provider, Subject = a.Subject, Label = a.Label, AddedAt = a.AddedAt, Access = a.Access ?? "member",
                AccountId = a.AccountId
            }).ToArray(),
            Enrolled = current.Enrolled.Select(e => new SignInEnrolledDocument
            {
                DeviceId = e.DeviceId, Provider = e.Provider, Subject = e.Subject, Label = e.Label, EnrolledAt = e.EnrolledAt,
                Access = e.Access ?? "member"
            }).ToArray(),
            RemovedFromNetwork = current.Removed.Select(r => new SignInEnrolledDocument
            {
                DeviceId = r.DeviceId, Provider = r.Provider, Subject = r.Subject, Label = r.Label, EnrolledAt = r.At
            }).ToArray(),
            Refused = SignIn.Refused().Select(r => new SignInEnrolledDocument
            {
                DeviceId = r.DeviceId, Provider = r.Identity.Provider, Subject = r.Identity.Subject, Label = r.Identity.Label, EnrolledAt = r.At
            }).ToArray(),
            RecoveryCodes = codes
        }).ConfigureAwait(false);
    }

    /// <summary>A <c>link-login</c> change (W13) carries another host's attestation that a provider identity proved an account on
    /// the calling computer: it must check against this host's roster now and name the caller, and when this host allows the
    /// identity already it must prove that same account here (<c>signin.login_taken</c> otherwise).</summary>
    private void RequireLinkAttestation(GatewaySignInChange change, GatewayPrincipal caller)
    {
        var attested = GatewaySignInSettings.ParseAttestation(change.Attestation);
        var roster = Network.State == "bound" ? Network.Roster : null;
        GatewayRules.Require(roster is not null, "signin.no_network");
        GatewayRules.Require(attested.DeviceId == caller.DeviceId && attested.Check(roster!, clock.GetUtcNow()) == AccountAttestationCheck.Valid,
            "signin.invalid");
        var login = attested.Login;
        if (SignIn.Snapshot().Allowed.Any(a => a.Provider == login.Provider && a.Subject == login.Subject))
            GatewayRules.Require(SignIn.AccountFor(new GatewaySignInIdentity(login.Provider, login.Subject, null))?.AccountId == attested.AccountId,
                "signin.login_taken");
    }

    /// <summary>Only the owner's computers change sign-in: an active member desktop of the network this host is in, or any
    /// paired desktop while it is in none (the owner approved that pairing here).</summary>
    private void RequireSignInOwner(GatewayPrincipal caller)
    {
        GatewayRules.Require(caller.Key is null, "signin.denied");
        var roster = Network.Roster;
        if (Network.State == "bound")
            GatewayRules.Require(roster?.Desktop(caller.DeviceId) is { Removed: false }, "signin.denied");
    }

    private static async ValueTask<T> ReadSignInAsync<T>(HttpRequest request, CancellationToken cancellationToken) where T : class =>
        await ReadPairingAsync<T>(request, cancellationToken).ConfigureAwait(false);

    [JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
    private sealed record SignInBeginBody
    {
        public required GatewayProtocolVersion ProtocolVersion { get; init; }
        public required string Provider { get; init; }
        public string? CodeChallenge { get; init; }
        public string? RedirectUri { get; init; }
    }

    [JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
    private sealed record SignInCompleteBody
    {
        public required GatewayProtocolVersion ProtocolVersion { get; init; }
        public required string AttemptId { get; init; }
        public required string DeviceId { get; init; }
        public required string DisplayName { get; init; }
        public required JsonElement Proof { get; init; }
    }

    [JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
    private sealed record SignInProveBody
    {
        public required GatewayProtocolVersion ProtocolVersion { get; init; }
        public required string AttemptId { get; init; }
        public required JsonElement Proof { get; init; }
        /// <summary>How long the attestation lasts, 60 seconds to 30 days; ten minutes when absent.</summary>
        public int? LifetimeSeconds { get; init; }
        /// <summary>W13: link a provider identity this host doesn't allow yet to this account as one of its logins (the caller must
        /// be able to act for it); the answer then attests that account with the new login.</summary>
        public Guid? LinkAccountId { get; init; }
    }

    private sealed record SignInProofDocument
    {
        public required GatewayProtocolVersion ProtocolVersion { get; init; }
        public required string HostId { get; init; }
        public required string DeviceId { get; init; }
        public required SignedInDocument SignedIn { get; init; }
        public required Guid AccountId { get; init; }
        /// <summary>The <see cref="AccountAttestation"/> JSON, signed with this host's TLS key.</summary>
        public required JsonElement Attestation { get; init; }
    }

    private sealed record SignInProvidersDocument
    {
        public required GatewayProtocolVersion ProtocolVersion { get; init; }
        public required string HostId { get; init; }
        public required SignInProviderDocument[] Providers { get; init; }
        /// <summary>Whether household accounts sign in here with a Martlet password (provider "martlet", not in the list).</summary>
        public bool MartletSignIn { get; init; }
    }

    private sealed record SignInProviderDocument
    {
        public required string Id { get; init; }
        public required string Kind { get; init; }
        public required string Name { get; init; }
        public int? RedirectPort { get; init; }
    }

    private sealed record SignInAttemptDocument
    {
        public required GatewayProtocolVersion ProtocolVersion { get; init; }
        public required string HostId { get; init; }
        public required string AttemptId { get; init; }
        public required string Provider { get; init; }
        public required string State { get; init; }
        public required string Nonce { get; init; }
        public required DateTimeOffset ExpiresAt { get; init; }
        public string? AuthorizeUrl { get; init; }
    }

    private sealed record SignInResponseDocument
    {
        public required GatewayProtocolVersion ProtocolVersion { get; init; }
        public required string HostId { get; init; }
        public required string CredentialId { get; init; }
        public required string CredentialSecret { get; init; }
        public required string DeviceId { get; init; }
        public required IReadOnlyList<GatewayRole> Roles { get; init; }
        public required GatewayCredentialLifetime Lifetime { get; init; }
        public required SignedInDocument SignedIn { get; init; }
        /// <summary>"member": one of the owner's computers (it joins the network next). "friend": this host's engines only.</summary>
        public required string Access { get; init; }
        /// <summary>The household account the sign-in proves (null for a friend, or an identity linked to no account yet).</summary>
        public Guid? AccountId { get; init; }
        /// <summary>The host's <see cref="AccountAttestation"/> for this computer and that account, when this host is in a
        /// network.</summary>
        public JsonElement? Attestation { get; init; }
    }

    private sealed record SignedInDocument
    {
        public required string Provider { get; init; }
        public required string Subject { get; init; }
        public string? Label { get; init; }
    }

    private sealed record SignInSettingsDocument
    {
        public required GatewayProtocolVersion ProtocolVersion { get; init; }
        public required string HostId { get; init; }
        public required bool Attached { get; init; }
        /// <summary>Whether someone can sign in here now; while not, the host isn't reachable from outside home.</summary>
        public bool Usable => BlockedReason is null;
        public string? BlockedReason { get; init; }
        /// <summary>The household owner's account ID, when a member desktop set it.</summary>
        public Guid? OwnerAccountId { get; init; }
        public SignInOwnerDocument? Owner { get; init; }
        /// <summary>Other household accounts' Martlet password logins (never a verifier or secret).</summary>
        public required SignInAccountDocument[] Accounts { get; init; }
        public required SignInProviderSettingsDocument[] Providers { get; init; }
        public required SignInAllowedDocument[] Allowed { get; init; }
        public required SignInEnrolledDocument[] Enrolled { get; init; }
        /// <summary>Identities that signed in at a provider but aren't allowed (newest first), so the owner can allow them.</summary>
        public required SignInEnrolledDocument[] Refused { get; init; }
        /// <summary>Computers whose sign-in is no longer allowed and that your member desktops still have to remove from the
        /// network (they do on their next sync); <c>enrolled_at</c> is when the sign-in was removed.</summary>
        public required SignInEnrolledDocument[] RemovedFromNetwork { get; init; }
        /// <summary>Only in the answer to the change that made them; shown once to the owner, kept here only as verifiers.</summary>
        public IReadOnlyList<string>? RecoveryCodes { get; init; }
    }

    private sealed record SignInOwnerDocument
    {
        public required string User { get; init; }
        public required int RecoveryCodesLeft { get; init; }
        public DateTimeOffset CreatedAt { get; init; }
    }

    private sealed record SignInAccountDocument
    {
        public required Guid AccountId { get; init; }
        public required string User { get; init; }
        public required bool HasAuthenticator { get; init; }
        public required int RecoveryCodesLeft { get; init; }
        public DateTimeOffset CreatedAt { get; init; }
    }

    private sealed record SignInProviderSettingsDocument
    {
        public required string Id { get; init; }
        public required string Kind { get; init; }
        public required string Name { get; init; }
        public string? Issuer { get; init; }
        public string? ClientId { get; init; }
        public string? Scopes { get; init; }
        public int? RedirectPort { get; init; }
        public required bool HasClientSecret { get; init; }
    }

    private sealed record SignInAllowedDocument
    {
        public required string Provider { get; init; }
        public required string Subject { get; init; }
        public string? Label { get; init; }
        public DateTimeOffset AddedAt { get; init; }
        /// <summary>"member" (the owner's computers, which join the network) or "friend" (this host's engines only).</summary>
        public string? Access { get; init; }
        /// <summary>For a member: the account it signs in as; absent means the owner's.</summary>
        public Guid? AccountId { get; init; }
    }

    private sealed record SignInEnrolledDocument
    {
        public required string DeviceId { get; init; }
        public required string Provider { get; init; }
        public required string Subject { get; init; }
        public string? Label { get; init; }
        public DateTimeOffset EnrolledAt { get; init; }
        /// <summary>For computers that signed in: "member" or "friend", the access their sign-in gave.</summary>
        public string? Access { get; init; }
    }
}
