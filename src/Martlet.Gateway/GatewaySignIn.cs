using System.Text.Json;
using System.Text.Json.Serialization;
using Martlet.Core.Access;
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
        var revoked = stale.Sum(e => credentials.RevokeDevice(e.DeviceId, CancellationToken.None));
        foreach (var enrollment in stale)
            log(LogLevels.Info, $"Revoked {enrollment.DeviceId}: it paired by signing in as {enrollment.Label ?? enrollment.Subject} ({enrollment.Provider}), which is no longer allowed.");
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
    /// <c>signin.expired</c>, <c>signin.invalid</c>, <c>signin.not_allowed</c> or <c>signin.provider</c>; the attempt is used
    /// up either way.</summary>
    internal async ValueTask<(IssuedDeviceCredential Credential, GatewaySignInIdentity Identity)> CompleteAsync(string attemptId,
        string deviceId, string displayName, JsonElement proof, CancellationToken cancellationToken)
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
        GatewaySignInIdentity who;
        if (attempt!.Provider == OwnerProvider) who = VerifyOwner(current, proof);
        else
        {
            var config = current.Providers.FirstOrDefault(p => p.Id == attempt.Provider);
            var provider = config is null ? null : Providers(config);
            GatewayRules.Require(provider is not null, "signin.unavailable");
            who = await provider!.VerifyAsync(attempt, proof, cancellationToken).ConfigureAwait(false);
            GatewayRules.Require(who.Provider == attempt.Provider && who.Subject is { Length: > 0 and <= 256 }, "signin.invalid");
        }
        lock (gate)
        {
            current = document = LoadLocked();
            if (who.Provider == OwnerProvider) GatewayRules.Require(current.Owner?.User == who.Subject, "signin.invalid");
            else if (!current.Allowed.Any(a => a.Provider == who.Provider && a.Subject == who.Subject))
            {
                log(LogLevels.Warn, $"Sign-in by {Display(who)} for {deviceId} refused: that identity is not on this host's allow list.");
                refused.RemoveAll(r => r.Identity.Provider == who.Provider && r.Identity.Subject == who.Subject);
                refused.Insert(0, (deviceId, who, clock.GetUtcNow()));
                if (refused.Count > MaximumRefused) refused.RemoveAt(refused.Count - 1);
                throw new GatewayProtocolException("signin.not_allowed");
            }
            credentials.RevokeDevice(deviceId, cancellationToken);
            var issued = credentials.Issue(deviceId, Martlet.Core.Network.NetworkRoster.CleanName(displayName, deviceId), [GatewayRole.Voice],
                cancellationToken);
            var next = current.Clone();
            next.Enrolled.RemoveAll(e => e.DeviceId == deviceId);
            next.Enrolled.Add(new() { DeviceId = deviceId, Provider = who.Provider, Subject = who.Subject, Label = who.Label, EnrolledAt = clock.GetUtcNow() });
            if (next.Enrolled.Count > MaximumEnrolled) next.Enrolled.RemoveRange(0, next.Enrolled.Count - MaximumEnrolled);
            try { SaveLocked(next); }
            catch (Exception error) when (error is not GatewayProtocolException)
            {
                log(LogLevels.Warn, "Could not save signin.json; the computer that signed in is paired, but a member desktop will ask for an Allow to let it into the network.");
            }
            log(LogLevels.Info, $"{deviceId} paired by signing in as {Display(who)}.");
            return (issued, who);
        }
    }

    /// <summary>Checks the owner account: account name (any case), password and a current authenticator code that wasn't used
    /// yet, or an unused recovery code (used up here).</summary>
    private GatewaySignInIdentity VerifyOwner(GatewaySignInDocument current, JsonElement proof)
    {
        var owner = current.Owner ?? throw new GatewayProtocolException("signin.unavailable");
        string? Text(string name) => proof.ValueKind == JsonValueKind.Object && proof.TryGetProperty(name, out var value) &&
            value.ValueKind == JsonValueKind.String ? value.GetString() : null;
        var user = Text("user");
        var password = Text("password");
        var code = Text("code");
        var passwordOk = GatewayAccounts.VerifyPassword(password, owner.Password);
        var userOk = string.Equals(user?.Trim(), owner.User, StringComparison.OrdinalIgnoreCase);
        if (!passwordOk || !userOk) throw new GatewayProtocolException("signin.invalid");
        lock (gate)
        {
            var latest = LoadLocked();
            var account = latest.Owner;
            GatewayRules.Require(account is not null && account.User == owner.User, "signin.invalid");
            var next = latest.Clone();
            var step = Totp.Verify(account!.TotpSecret, code, clock.GetUtcNow(), account.LastTotpStep);
            if (step is { } used) next.Owner!.LastTotpStep = used;
            else if (GatewayAccounts.RecoveryVerifier(code) is { } verifier &&
                next.Owner!.RecoveryCodes.FindIndex(r => crypto.FixedTimeEquals(System.Text.Encoding.ASCII.GetBytes(r),
                    System.Text.Encoding.ASCII.GetBytes(verifier))) is >= 0 and var index)
            {
                next.Owner.RecoveryCodes.RemoveAt(index);
                log(LogLevels.Warn, $"The owner account signed in with a recovery code; {next.Owner.RecoveryCodes.Count} left.");
            }
            else throw new GatewayProtocolException("signin.invalid");
            SaveLocked(next);
        }
        return new(OwnerProvider, owner.User, owner.User);
    }

    /// <summary>The identity that enrolled <paramref name="deviceId"/> by signing in, while that device is still paired here
    /// and the identity is still allowed; null otherwise. A member desktop lets such a device into the network without a
    /// check number.</summary>
    internal (GatewaySignInIdentity Identity, DateTimeOffset At)? Attestation(string deviceId)
    {
        lock (gate)
        {
            if (storage is null) return null;
            var current = document;
            var enrolled = current.Enrolled.LastOrDefault(e => e.DeviceId == deviceId);
            if (enrolled is null || !credentials.PairedDevices().Any(d => d.DeviceId == deviceId)) return null;
            var allowed = enrolled.Provider == OwnerProvider ? current.Owner?.User == enrolled.Subject
                : current.Allowed.Any(a => a.Provider == enrolled.Provider && a.Subject == enrolled.Subject);
            return allowed ? (new(enrolled.Provider, enrolled.Subject, enrolled.Label), enrolled.EnrolledAt) : null;
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
                var user = change.User?.Trim();
                GatewayRules.Require(user is { Length: > 0 and <= 64 } && user.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-' or '@'),
                    "request.invalid");
                GatewayAccounts.RequirePassword(change.Password);
                GatewayAccounts.RequireTotpSecret(change.TotpSecret);
                // The owner proves the authenticator app took the secret before it becomes mandatory.
                var step = Totp.Verify(change.TotpSecret!, change.Code, now, -1);
                GatewayRules.Require(step is not null, "signin.invalid");
                var (codes, verifiers) = GatewayAccounts.NewRecoveryCodes();
                next.Owner = new()
                {
                    User = user!, Password = GatewayAccounts.HashPassword(change.Password!), TotpSecret = change.TotpSecret!.ToUpperInvariant(),
                    LastTotpStep = step!.Value, RecoveryCodes = verifiers, CreatedAt = now
                };
                return codes;
            }
            case "recovery-codes":
            {
                GatewayRules.Require(next.Owner is not null, "signin.unavailable");
                var (codes, verifiers) = GatewayAccounts.NewRecoveryCodes();
                next.Owner!.RecoveryCodes = verifiers;
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
                    change.Subject is { Length: > 0 and <= 256 } && change.Subject.All(c => !char.IsControl(c)) &&
                    (change.Label is null || change.Label.Length <= 128 && change.Label.All(c => !char.IsControl(c))), "request.invalid");
                next.Allowed.RemoveAll(a => a.Provider == change.Provider && a.Subject == change.Subject);
                GatewayRules.Require(next.Allowed.Count < MaximumAllowed, "request.invalid");
                next.Allowed.Add(new() { Provider = change.Provider!, Subject = change.Subject!, Label = change.Label, AddedAt = now });
                return null;
            }
            case "disallow":
            {
                next.Allowed.RemoveAll(a => a.Provider == change.Provider && a.Subject == change.Subject);
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
    [JsonPropertyName("provider_config")]
    public GatewaySignInProviderConfig? ProviderConfig { get; init; }
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
        GatewayRules.Require(Id is { Length: > 0 and <= 32 } && Id != GatewaySignInService.OwnerProvider &&
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

internal sealed class GatewayOwnerAccount
{
    public required string User { get; set; }
    public required GatewayPasswordVerifier Password { get; set; }
    public required string TotpSecret { get; set; }
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
    public GatewayOwnerAccount? Owner { get; set; }
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
        parsed.Allowed ??= [];
        parsed.Enrolled ??= [];
        parsed.Removed ??= [];
        foreach (var provider in parsed.Providers) provider.Validate();
        return parsed;
    }

    internal GatewaySignInDocument Clone() => Parse(Write());

    /// <summary>Whether <paramref name="provider"/>/<paramref name="subject"/> may sign in: the owner account's own name, or an
    /// identity on the allow list.</summary>
    internal bool Allows(string provider, string subject) => provider == GatewaySignInService.OwnerProvider
        ? Owner?.User == subject
        : Allowed.Any(a => a.Provider == provider && a.Subject == subject);

    /// <summary>Drops (and returns) the enrollments of identities that may no longer sign in; their computers lose access here
    /// and are recorded in <see cref="Removed"/> so member desktops remove them from the network too.</summary>
    internal IReadOnlyList<GatewaySignInEnrollment> Sweep(DateTimeOffset now)
    {
        var stale = Enrolled.Where(e => !Allows(e.Provider, e.Subject)).ToArray();
        Enrolled.RemoveAll(e => !Allows(e.Provider, e.Subject));
        foreach (var enrollment in stale)
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
    internal IReadOnlyList<GatewaySignInEnrollment> WouldSweep() => Enrolled.Where(e => !Allows(e.Provider, e.Subject)).ToArray();
}
