using System.Text.Json;
using System.Text.Json.Serialization;
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
/// a provider: what the browser brought back) for a device ID and name; answers 201 with a device credential like pairing.</item>
/// </list>
/// <c>GET</c>/<c>POST /martlet/v1/signin/settings</c> read and change the owner account, providers and allow list; they need
/// a signed request from a member desktop of this host's network (any paired desktop while the host is in no network) and
/// never return a secret.
/// </summary>
internal sealed partial class GatewayHttpApplication
{
    internal const string SignInPath = "/martlet/v1/signin";
    internal const string SignInBeginPath = "/martlet/v1/signin/begin";
    internal const string SignInCompletePath = "/martlet/v1/signin/complete";
    internal const string SignInSettingsPath = "/martlet/v1/signin/settings";
    internal const string SignInRouteClass = "signin";
    private const int MaximumSignInRequestBytes = 16_384;

    private GatewaySignInService? signIn;

    internal TimeProvider Clock => clock;

    internal GatewaySignInService SignIn => signIn ?? throw new InvalidOperationException("Sign-in is not initialized.");

    internal void InitializeSignIn(GatewayCredentialStore credentials)
    {
        var providers = new GatewaySignInProviders(clock);
        signIn = new(credentials, clock, crypto, (level, message) => Logs.Own(level, message)) { Providers = providers.Create };
    }

    private static bool IsSignInTarget(string rawTarget) =>
        rawTarget is SignInPath or SignInBeginPath or SignInCompletePath or SignInSettingsPath;

    private async ValueTask InvokeSignInAsync(HttpContext context, string rawTarget)
    {
        if (rawTarget == SignInSettingsPath)
        {
            await InvokeSignInSettingsAsync(context).ConfigureAwait(false);
            return;
        }
        if (rawTarget == SignInPath)
        {
            GatewayRules.Require(context.Request.Method == HttpMethods.Get, "request.invalid");
            EnsureEmptyRequest(context.Request);
            await WriteJsonAsync(context, StatusCodes.Status200OK, new SignInProvidersDocument
            {
                ProtocolVersion = GatewayProtocolVersion.Current, HostId = identity.HostId,
                Providers = SignIn.Available().Select(p => new SignInProviderDocument { Id = p.Id, Kind = p.Kind, Name = p.Name, RedirectPort = p.RedirectPort }).ToArray()
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
        var claimed = complete.Proof.ValueKind == JsonValueKind.Object && complete.Proof.TryGetProperty("user", out var user) &&
            user.ValueKind == JsonValueKind.String ? "owner:" + user.GetString()?.Trim().ToLowerInvariant() : null;
        var request = Admit(context, claimed);
        try
        {
            var (credential, who) = await SignIn.CompleteAsync(complete.AttemptId, complete.DeviceId, complete.DisplayName, complete.Proof,
                context.RequestAborted).ConfigureAwait(false);
            Guard.Record(request with { Subject = Subject(who) }, GatewayGuardOutcome.Success, "signin.ok", Subject(who));
            await WriteJsonAsync(context, StatusCodes.Status201Created, new SignInResponseDocument
            {
                ProtocolVersion = GatewayProtocolVersion.Current, HostId = identity.HostId, CredentialId = credential.CredentialId,
                CredentialSecret = credential.Secret.Reveal(), DeviceId = credential.DeviceId, Roles = credential.Roles,
                Lifetime = credential.Lifetime,
                SignedIn = new() { Provider = who.Provider, Subject = who.Subject, Label = who.Label }
            }).ConfigureAwait(false);
        }
        catch (GatewayProtocolException error) when (error.Failure.Code.StartsWith("signin.", StringComparison.Ordinal))
        {
            Guard.Record(request, GatewayGuardOutcome.Failure, error.Failure.Code, claimed);
            throw;
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
            codes = SignIn.Change(change, caller.Caller, context.RequestAborted);
        }
        var current = SignIn.Snapshot();
        await WriteJsonAsync(context, StatusCodes.Status200OK, new SignInSettingsDocument
        {
            ProtocolVersion = GatewayProtocolVersion.Current, HostId = identity.HostId, Attached = SignIn.Attached,
            BlockedReason = SignIn.Attached ? GatewaySignInSettings.BlockedReason(current) : "signin.not_set_up",
            Owner = current.Owner is { } owner ? new() { User = owner.User, RecoveryCodesLeft = owner.RecoveryCodes.Count, CreatedAt = owner.CreatedAt } : null,
            Providers = current.Providers.Select(p => new SignInProviderSettingsDocument
            {
                Id = p.Id, Kind = p.Kind, Name = p.Name, Issuer = p.Issuer, ClientId = p.ClientId, Scopes = p.Scopes,
                RedirectPort = p.RedirectPort, HasClientSecret = p.ClientSecret is not null
            }).ToArray(),
            Allowed = current.Allowed.Select(a => new SignInAllowedDocument { Provider = a.Provider, Subject = a.Subject, Label = a.Label, AddedAt = a.AddedAt }).ToArray(),
            Enrolled = current.Enrolled.Select(e => new SignInEnrolledDocument
            {
                DeviceId = e.DeviceId, Provider = e.Provider, Subject = e.Subject, Label = e.Label, EnrolledAt = e.EnrolledAt
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

    private sealed record SignInProvidersDocument
    {
        public required GatewayProtocolVersion ProtocolVersion { get; init; }
        public required string HostId { get; init; }
        public required SignInProviderDocument[] Providers { get; init; }
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
        public SignInOwnerDocument? Owner { get; init; }
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
    }

    private sealed record SignInEnrolledDocument
    {
        public required string DeviceId { get; init; }
        public required string Provider { get; init; }
        public required string Subject { get; init; }
        public string? Label { get; init; }
        public DateTimeOffset EnrolledAt { get; init; }
    }
}
