using System.Text.Json.Nodes;
using Martlet.Core.Accounts;

namespace Martlet.Avatar.Audio2Face.Remote;

/// <summary>A sign-in provider as the household sets it up: the same configuration on every host of the network. The client
/// secret is not part of it: hosts keep it, and the PC that set the provider up keeps a copy in Windows Credential Manager only to
/// add the provider to a host that misses it.</summary>
public sealed record HouseholdProvider(string Id, string Kind, string Name, string? Issuer, string? ClientId, string? Scopes, int? RedirectPort)
{
    public static HouseholdProvider From(HostSignInProviderSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return new(settings.Id, settings.Kind, settings.Name, settings.Issuer, settings.ClientId, settings.Scopes, settings.RedirectPort);
    }

    /// <summary>Whether a host's provider has this configuration (the secret aside).</summary>
    public bool SameAs(HostSignInProviderSettings settings) =>
        settings.Id == Id && settings.Kind == Kind && settings.Name == Name && Trim(settings.Issuer) == Trim(Issuer) &&
        settings.ClientId == ClientId && (settings.Scopes ?? "") == (Scopes ?? "") && settings.RedirectPort == RedirectPort;

    /// <summary>Whether signing in with this provider needs a client secret on the host (Discord always; OpenID Connect for a
    /// confidential client, which the household's hosts show by having one). Steam never does.</summary>
    public bool NeedsSecret(bool householdHasSecret) => Kind switch
    {
        "steam" => false,
        "discord" => true,
        _ => householdHasSecret
    };

    /// <summary>The host's settings change that saves this provider (<c>{"action":"provider","provider_config":{...}}</c>). Without
    /// <paramref name="clientSecret"/> the host keeps the secret it has for the provider.</summary>
    public JsonObject Change(string? clientSecret)
    {
        var config = new JsonObject
        {
            ["id"] = Id, ["kind"] = Kind, ["name"] = Name, ["issuer"] = Kind == "oidc" ? Trim(Issuer) : null, ["client_id"] = ClientId,
            ["scopes"] = Scopes, ["redirect_port"] = RedirectPort
        };
        if (clientSecret is { Length: > 0 }) config["client_secret"] = clientSecret;
        return new JsonObject { ["action"] = "provider", ["provider_config"] = config };
    }

    public static JsonObject RemoveChange(string id) => new() { ["action"] = "remove-provider", ["id"] = id };

    private static string? Trim(string? issuer) => issuer?.TrimEnd('/');
}

/// <summary>Where one household provider is set up: the hosts that have the household's configuration (<see cref="On"/>), the
/// hosts that don't have it (<see cref="Missing"/>), the hosts with another configuration under the same ID
/// (<see cref="Different"/>) and the hosts among <see cref="On"/> without a client secret while others have one
/// (<see cref="WithoutSecret"/>).</summary>
public sealed record HouseholdProviderState(HouseholdProvider Provider, IReadOnlyList<string> On, IReadOnlyList<string> Missing,
    IReadOnlyList<string> Different, IReadOnlyList<string> WithoutSecret)
{
    /// <summary>Whether a host of the household keeps a client secret for it.</summary>
    public bool HasSecret { get; init; }
    public bool Everywhere => Missing.Count == 0 && Different.Count == 0 && WithoutSecret.Count == 0;
}

/// <summary>What one host answered to a household change: <see cref="Settings"/> (its sign-in settings after the change) or
/// <see cref="Problem"/>.</summary>
public sealed record HouseholdChangeResult(string HostId, HostSignInSettings? Settings, string? Problem)
{
    public bool Saved => Settings is not null;
}

/// <summary>A provider login of a household account (docs/ACCOUNTS.md): an identity at a household provider that the hosts in
/// <see cref="Hosts"/> allow as that account's login. <see cref="Label"/> is for people (an e-mail or user name; never
/// trusted).</summary>
public sealed record HouseholdLogin(string Provider, string Subject, string? Label, IReadOnlyList<string> Hosts)
{
    public override string ToString() => Label is { Length: > 0 } ? $"{Label} ({Provider})" : $"{Subject} ({Provider})";
}

/// <summary>What linking a login did: the identity (with the account), whether a host already had it as that account's login,
/// and what each host answered.</summary>
public sealed record HouseholdLinkResult(HostSignInIdentity Identity, bool AlreadyLinked, IReadOnlyList<HouseholdChangeResult> Results);

/// <summary>
/// Sign-in providers for the whole household (docs/NETWORK.md): an admin sets a provider up once and the PC where it was set up
/// sends it to every host of the network, so a computer or a person can sign in with it through any host. The hosts stay the
/// authority: each keeps its own <c>signin.json</c> and the client secret, and the household view is read from their sign-in
/// settings (never a secret). Allowed friends stay per host: nothing here adds, changes or removes an allowed identity.
/// </summary>
public static class HouseholdSignIn
{
    /// <summary>How long one host may take to answer one change or read.</summary>
    public static readonly TimeSpan HostTimeout = TimeSpan.FromSeconds(15);

    /// <summary>The household's providers as the hosts in <paramref name="read"/> have them, by ID. A provider's household
    /// configuration is the one most hosts have (ties: the host first by ID).</summary>
    public static IReadOnlyList<HouseholdProviderState> Providers(IReadOnlyDictionary<string, HostSignInSettings> read)
    {
        ArgumentNullException.ThrowIfNull(read);
        var hosts = read.Keys.Order(StringComparer.Ordinal).ToArray();
        var states = new List<HouseholdProviderState>();
        foreach (var id in read.Values.SelectMany(s => s.Providers).Select(p => p.Id).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))
        {
            var found = hosts.Select(h => (Host: h, Settings: read[h].Providers.FirstOrDefault(p => p.Id == id))).Where(x => x.Settings is not null).ToArray();
            var household = found.GroupBy(x => HouseholdProvider.From(x.Settings!))
                .OrderByDescending(g => g.Count()).ThenBy(g => g.First().Host, StringComparer.Ordinal).First();
            var provider = household.Key;
            var on = found.Where(x => provider.SameAs(x.Settings!)).ToArray();
            var hasSecret = found.Any(x => x.Settings!.HasClientSecret);
            states.Add(new(provider, on.Select(x => x.Host).ToArray(), hosts.Where(h => found.All(x => x.Host != h)).ToArray(),
                found.Where(x => !provider.SameAs(x.Settings!)).Select(x => x.Host).ToArray(),
                provider.NeedsSecret(hasSecret) ? on.Where(x => !x.Settings!.HasClientSecret).Select(x => x.Host).ToArray() : [])
            {
                HasSecret = hasSecret
            });
        }
        return states;
    }

    /// <summary>The hosts of <paramref name="hosts"/> that should get <paramref name="provider"/>: those that miss it, have another
    /// configuration under its ID, or (when the change brings a secret) have it without one. A host whose settings couldn't be read
    /// (not in <paramref name="read"/>) gets it too; saving the same provider again changes nothing there.</summary>
    public static IReadOnlyList<string> Targets(HouseholdProvider provider, IReadOnlyDictionary<string, HostSignInSettings> read,
        IEnumerable<string> hosts, bool withSecret)
    {
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentNullException.ThrowIfNull(read);
        return hosts.Distinct(StringComparer.Ordinal).Where(host =>
            !read.TryGetValue(host, out var settings) ||
            settings.Providers.FirstOrDefault(p => p.Id == provider.Id) is not { } there ||
            !provider.SameAs(there) ||
            withSecret && !there.HasClientSecret).ToArray();
    }

    /// <summary>Sends a change to each host at once (each within <see cref="HostTimeout"/>); <paramref name="changeFor"/> makes the
    /// change for a host (so a secret goes only where it is needed). Never throws for one host's failure.</summary>
    public static async Task<IReadOnlyList<HouseholdChangeResult>> SendAsync(IEnumerable<string> hosts,
        Func<string, Audio2FaceHostConnection> connect, Func<string, JsonObject> changeFor, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connect);
        ArgumentNullException.ThrowIfNull(changeFor);
        return await Task.WhenAll(hosts.Distinct(StringComparer.Ordinal).Select(async host =>
        {
            try
            {
                using var connection = connect(host);
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(HostTimeout);
                return new HouseholdChangeResult(host, await connection.ChangeSignInSettingsAsync(changeFor(host), timeout.Token).ConfigureAwait(false), null);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return new HouseholdChangeResult(host, null, "didn't answer in time");
            }
            catch (Exception error) when (error is Audio2FaceHostException or InvalidOperationException or IOException or HttpRequestException or
                System.Text.Json.JsonException or ArgumentException)
            {
                return new HouseholdChangeResult(host, null, Problem(error));
            }
        })).ConfigureAwait(false);
    }

    /// <summary>Reads every host's sign-in settings at once (each within <see cref="HostTimeout"/>): what answered, and why the
    /// others didn't.</summary>
    public static async Task<(IReadOnlyDictionary<string, HostSignInSettings> Read, IReadOnlyDictionary<string, string> Problems)> ReadAsync(
        IEnumerable<string> hosts, Func<string, Audio2FaceHostConnection> connect, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connect);
        var results = await Task.WhenAll(hosts.Distinct(StringComparer.Ordinal).Select(async host =>
        {
            try
            {
                using var connection = connect(host);
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(HostTimeout);
                return (Host: host, Settings: await connection.ReadSignInSettingsAsync(timeout.Token).ConfigureAwait(false), Problem: (string?)null);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return (Host: host, Settings: (HostSignInSettings?)null, Problem: "didn't answer in time");
            }
            catch (Exception error) when (error is Audio2FaceHostException or InvalidOperationException or IOException or HttpRequestException or
                System.Text.Json.JsonException or ArgumentException)
            {
                return (Host: host, Settings: null, Problem: Problem(error));
            }
        })).ConfigureAwait(false);
        return (results.Where(r => r.Settings is not null).ToDictionary(r => r.Host, r => r.Settings!, StringComparer.Ordinal),
            results.Where(r => r.Problem is not null).ToDictionary(r => r.Host, r => r.Problem!, StringComparer.Ordinal));
    }

    /// <summary>One line for people: "Saved Google on gpu-box and lab-host-b. linux-box: didn't answer in time; ...".</summary>
    public static string Describe(IReadOnlyList<HouseholdChangeResult> results, string done)
    {
        ArgumentNullException.ThrowIfNull(results);
        var saved = results.Where(r => r.Saved).Select(r => r.HostId).Order(StringComparer.Ordinal).ToArray();
        var failed = results.Where(r => !r.Saved).OrderBy(r => r.HostId, StringComparer.Ordinal).ToArray();
        var text = saved.Length == 0 ? "" : $"{done} on {Join(saved)}.";
        if (failed.Length > 0)
            text += (text.Length > 0 ? " " : "") + "Not changed on " + string.Join("; ", failed.Select(f => $"{f.HostId} ({f.Problem})")) +
                ". Martlet adds it there when that host answers again (or use Add to the other hosts).";
        return text.Length == 0 ? "No host to change." : text;
    }

    /// <summary>Where the household's providers are set up, for people (one line each).</summary>
    public static string Coverage(IReadOnlyList<HouseholdProviderState> states, IReadOnlyDictionary<string, string>? problems = null)
    {
        ArgumentNullException.ThrowIfNull(states);
        var lines = states.Select(s =>
        {
            var line = $"{s.Provider.Name} ({s.Provider.Id}): " + (s.On.Count > 0 ? "on " + Join(s.On) : "on no host with the household's settings");
            if (s.Missing.Count > 0) line += "; missing on " + Join(s.Missing);
            if (s.Different.Count > 0) line += "; other settings on " + Join(s.Different);
            if (s.WithoutSecret.Count > 0) line += "; no client secret on " + Join(s.WithoutSecret);
            return line + ".";
        }).ToList();
        if (lines.Count == 0) lines.Add("No sign-in provider is set up on your hosts yet.");
        if (problems is { Count: > 0 })
            lines.Add("Couldn't read " + string.Join("; ", problems.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => $"{p.Key} ({p.Value})")) + ".");
        return string.Join(Environment.NewLine, lines);
    }

    private static string Join(IReadOnlyList<string> hosts) => hosts.Count switch
    {
        0 => "",
        1 => hosts[0],
        _ => string.Join(", ", hosts.Take(hosts.Count - 1)) + " and " + hosts[^1]
    };

    // ---------- provider logins linked to household accounts (docs/ACCOUNTS.md) ----------

    /// <summary>The provider logins of <paramref name="account"/> as the hosts' allow lists hold them: member identities linked to
    /// it (and, for the owner's account, member identities linked to no account, which prove the owner's), with the hosts that
    /// have each. Friends are never an account's login.</summary>
    public static IReadOnlyList<HouseholdLogin> Logins(IReadOnlyDictionary<string, HostSignInSettings> read, Guid account)
    {
        ArgumentNullException.ThrowIfNull(read);
        return read.OrderBy(r => r.Key, StringComparer.Ordinal)
            .SelectMany(r => r.Value.Allowed.Where(a => !a.Friend && (a.AccountId ?? r.Value.OwnerAccountId) == account).Select(a => (Host: r.Key, Allowed: a)))
            .GroupBy(x => (x.Allowed.Provider, x.Allowed.Subject))
            .Select(g => new HouseholdLogin(g.Key.Provider, g.Key.Subject, g.Select(x => x.Allowed.Label).FirstOrDefault(l => l is not null),
                g.Select(x => x.Host).ToArray()))
            .OrderBy(l => l.Provider, StringComparer.Ordinal).ThenBy(l => l.Label ?? l.Subject, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    /// <summary>Whether any host keeps a Martlet password login for <paramref name="account"/> (a household account's, or the owner
    /// login for the owner's account), which can prove it on any computer.</summary>
    public static bool HasPassword(IReadOnlyDictionary<string, HostSignInSettings> read, Guid account)
    {
        ArgumentNullException.ThrowIfNull(read);
        return read.Values.Any(s => s.Accounts.Any(a => a.AccountId == account) || s.OwnerUser is not null && s.OwnerAccountId == account);
    }

    /// <summary>Why <paramref name="login"/> can't be unlinked from <paramref name="account"/>, or null: an account keeps at least
    /// one login that proves it on another computer (a Martlet password or another provider login).</summary>
    public static string? UnlinkRefusal(IReadOnlyDictionary<string, HostSignInSettings> read, Guid account, HouseholdLogin login) =>
        HasPassword(read, account) || Logins(read, account).Any(l => (l.Provider, l.Subject) != (login.Provider, login.Subject))
            ? null
            : $"{login.Label ?? login.Subject} ({login.Provider}) is the only sign-in that proves this account on another computer. Add a " +
              "password or link another sign-in first.";

    /// <summary>The settings change that makes an identity a login of <paramref name="account"/> on a host (an allowed member
    /// identity with that account; docs/ACCOUNTS.md).</summary>
    public static JsonObject LinkChange(string provider, string subject, string? label, Guid account) => new()
    {
        ["action"] = "allow", ["provider"] = provider, ["subject"] = subject, ["label"] = label, ["account_id"] = account.ToString()
    };

    /// <summary>The settings change that removes a login from a host. As for any removed sign-in, the host revokes the computers it
    /// added and your computers remove them from the network on their next sync (docs/NETWORK.md).</summary>
    public static JsonObject UnlinkChange(string provider, string subject) => new()
    {
        ["action"] = "disallow", ["provider"] = provider, ["subject"] = subject
    };

    /// <summary>
    /// Links a provider login to the account this computer proved (<paramref name="proved"/>, a fresh attestation from a Prove
    /// sign-in on this computer, checked here against <paramref name="roster"/>): the person signs in with
    /// <paramref name="provider"/> in the browser through the host of <paramref name="at"/>, which checks the identity with the
    /// provider (a Prove sign-in, so it issues no credential), and every host in <paramref name="hosts"/> then allows that identity
    /// as a login of the account. An identity the host doesn't allow yet is refused there and listed under the host's refused
    /// sign-ins with this computer's device ID; that entry, read back over this computer's signed connection, names it. An
    /// identity that already proves another account (or is a friend's) is refused with <c>signin.login_taken</c>.
    /// </summary>
    public static async Task<HouseholdLinkResult> LinkInBrowserAsync(Audio2FaceHostConnection at, AccountAttestation proved,
        Martlet.Core.Network.NetworkRoster roster, string provider, Action<string> openBrowser, TimeSpan timeout, IEnumerable<string> hosts,
        Func<string, Audio2FaceHostConnection> connect, int? redirectPort = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(at);
        ArgumentNullException.ThrowIfNull(proved);
        ArgumentNullException.ThrowIfNull(roster);
        var device = at.Pairing.DeviceId;
        var checkedAt = DateTimeOffset.UtcNow;
        if (proved.DeviceId != device || proved.Check(roster, checkedAt) != Martlet.Core.Accounts.AccountAttestationCheck.Valid)
            throw new Audio2FaceHostException("signin.prove_first", "Prove it's you first (your password, or a sign-in already linked), then link again.");
        var started = DateTimeOffset.UtcNow;
        HostSignInIdentity who;
        var already = false;
        try
        {
            var proof = await at.ProveInBrowserAsync(provider, openBrowser, timeout, redirectPort: redirectPort, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            if (proof.AccountId != proved.AccountId)
                throw new Audio2FaceHostException("signin.login_taken", $"{proof.Identity} already signs in to another account of your household. " +
                    "Unlink it there first.");
            who = proof.Identity;
            already = true;
        }
        catch (Audio2FaceHostException error) when (error.Code == "signin.no_account")
        {
            throw new Audio2FaceHostException("signin.login_taken", "That sign-in is a friend's on this host, so it can't be a login of your account.");
        }
        catch (Audio2FaceHostException error) when (error.Code == "signin.not_allowed")
        {
            // The host checked the identity with the provider and listed it as refused for this computer: read it back.
            var settings = await at.ReadSignInSettingsAsync(cancellationToken).ConfigureAwait(false);
            var refused = settings.Refused.Where(r => r.Provider == provider && r.DeviceId == device && r.EnrolledAt >= started.AddMinutes(-1))
                .OrderByDescending(r => r.EnrolledAt).FirstOrDefault()
                ?? throw new Audio2FaceHostException("response.invalid", "The host checked the sign-in but didn't say whose it was. Try again.");
            who = new HostSignInIdentity(refused.Provider, refused.Subject, refused.Label);
        }
        var results = await SendAsync(hosts.Append(at.Pairing.HostId), connect, _ => LinkChange(who.Provider, who.Subject, who.Label, proved.AccountId),
            cancellationToken).ConfigureAwait(false);
        return new(who with { AccountId = proved.AccountId }, already, results);
    }

    /// <summary>Removes a login of an account from every host in <paramref name="hosts"/>.</summary>
    public static Task<IReadOnlyList<HouseholdChangeResult>> UnlinkAsync(HouseholdLogin login, IEnumerable<string> hosts,
        Func<string, Audio2FaceHostConnection> connect, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(login);
        return SendAsync(hosts, connect, _ => UnlinkChange(login.Provider, login.Subject), cancellationToken);
    }

    private static string Problem(Exception error) => error is Audio2FaceHostException { Code: "signin.unsupported" }
        ? "runs an older Martlet without sign-in; update it"
        : error.Message;
}
