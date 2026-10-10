using System.Security.Cryptography;
using Martlet.Core.Contracts;
using Martlet.Core.Sync;
using Microsoft.AspNetCore.Http;

namespace Martlet.Gateway;

/// <summary>Where a gateway keeps each account's settings between restarts (account-settings-&lt;32 hex&gt;.json beside host.json
/// on Linux hosts): one <see cref="SharedSettings"/> document per account ID, without any secret. They hold each person's
/// personalities and prompts, so they must be private to the gateway owner. <see cref="Load"/> returns null when the account
/// has no copy; every call may throw on storage failure.</summary>
public interface IGatewayAccountSettingsStorage
{
    /// <summary>The account IDs (32 lowercase hex digits) saved here.</summary>
    IReadOnlyCollection<string> List();
    byte[]? Load(string account);
    void Save(string account, byte[] bytes);
}

/// <summary>This host's copy of each account's settings (docs/ACCOUNTS.md, "Account settings"): the personalities and character
/// profiles, replies, prompts, memory on or off, lorebooks, the character shown, how you talk, speech display, the theme, Voice
/// ID, touch temperaments and reminders of one person, apart from the household's settings (<see cref="GatewaySettingsStore"/>)
/// and from every other account's. Paired desktops where the account is signed in read it and merge their changes into it;
/// the host never looks inside. An account's copy exists once a merge put something in it; a host keeps at most
/// <see cref="MaximumAccounts"/>.</summary>
internal sealed class GatewayAccountSettings
{
    internal const int MaximumAccounts = 64;
    private static readonly string EmptyDigest = SharedSettings.Empty.Digest();
    private readonly object gate = new();
    private readonly Dictionary<string, GatewaySettingsStore> accounts = new(StringComparer.Ordinal);
    private IGatewayAccountSettingsStorage? storage;

    /// <summary>Whether <paramref name="value"/> is an account ID as paths carry it: 32 lowercase hex digits.</summary>
    internal static bool IsAccount(string? value) =>
        value is { Length: 32 } && value.AsSpan().IndexOfAnyExcept("0123456789abcdef") < 0;

    /// <summary>The accounts this host keeps settings for, in order.</summary>
    internal IReadOnlyList<string> Accounts
    {
        get { lock (gate) return [.. accounts.Keys.Order(StringComparer.Ordinal)]; }
    }

    internal void Attach(IGatewayAccountSettingsStorage value)
    {
        ArgumentNullException.ThrowIfNull(value);
        string[] saved;
        try { saved = [.. value.List().Where(IsAccount).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)]; }
        // Accounts that cannot be listed come back from the next desktops that sync them.
        catch (Exception) { saved = []; }
        lock (gate)
        {
            storage = value;
            foreach (var (account, store) in accounts) store.Attach(new AccountStorage(value, account));
            foreach (var account in saved)
            {
                if (accounts.ContainsKey(account)) continue;
                if (accounts.Count >= MaximumAccounts) break;
                var store = new GatewaySettingsStore();
                store.Attach(new AccountStorage(value, account));
                accounts[account] = store;
            }
        }
    }

    internal SharedSettings Read(string account)
    {
        lock (gate) return accounts.TryGetValue(account, out var store) ? store.Current : SharedSettings.Empty;
    }

    internal string Digest(string account)
    {
        lock (gate) return accounts.TryGetValue(account, out var store) ? store.Digest : EmptyDigest;
    }

    /// <summary>Merges <paramref name="incoming"/> into the account's copy and returns the merged copy. A merge that brings
    /// nothing makes no copy; a new account past <see cref="MaximumAccounts"/> is <c>settings.accounts_full</c>.</summary>
    internal SharedSettings Merge(string account, SharedSettings incoming)
    {
        GatewaySettingsStore? store;
        lock (gate)
        {
            if (!accounts.TryGetValue(account, out store))
            {
                if (incoming.Settings.Count == 0) return SharedSettings.Empty;
                GatewayRules.Require(accounts.Count < MaximumAccounts, "settings.accounts_full");
                store = new GatewaySettingsStore();
                if (storage is not null) store.Attach(new AccountStorage(storage, account));
                accounts[account] = store;
            }
        }
        return store.Merge(incoming);
    }

    private sealed class AccountStorage(IGatewayAccountSettingsStorage storage, string account) : IGatewaySettingsStorage
    {
        public byte[]? Load() => storage.Load(account);
        public void Save(byte[] bytes) => storage.Save(account, bytes);
    }
}

internal sealed partial class GatewayHttpApplication
{
    internal const string AccountSettingsPath = SettingsPath + "/accounts/";
    private const string AccountSettingsDigestSuffix = "/digest";

    internal GatewayAccountSettings AccountSettings { get; } = new();

    private static bool IsAccountSettingsTarget(string rawTarget) => rawTarget.StartsWith(AccountSettingsPath, StringComparison.Ordinal);

    /// <summary>GET /settings/accounts/{account} returns this host's copy of one account's settings, POST merges a desktop's copy
    /// into it and returns the merged result, and GET /settings/accounts/{account}/digest returns only the copy's digest.
    /// Paired member devices only, over their signed, pinned connection (never friends or API keys), and only a device that may
    /// use the account's memory space (<c>account-&lt;32 hex&gt;</c>, <see cref="GatewayMemorySpaces.Access"/>), which is where
    /// the account directory says on which devices the account is signed in. A POST needs both read and write access, and an
    /// account's settings never carry an API key: a copy with a secret is refused.</summary>
    private async ValueTask InvokeAccountSettingsAsync(HttpContext context, string rawTarget)
    {
        var rest = rawTarget[AccountSettingsPath.Length..];
        var digestOnly = rest.EndsWith(AccountSettingsDigestSuffix, StringComparison.Ordinal);
        var account = digestOnly ? rest[..^AccountSettingsDigestSuffix.Length] : rest;
        GatewayRules.Require(GatewayAccountSettings.IsAccount(account), "request.invalid");
        var space = MemorySpaceId.AccountPrefix + account;
        if (digestOnly)
        {
            GatewayRules.Require(context.Request.Method == HttpMethods.Get, "request.invalid");
            EnsureEmptyRequest(context.Request);
            AdmitAccountSettings(authenticator.Authenticate(context.Request), space, GatewayMemorySpaceUse.Read);
            await WriteJsonAsync(context, StatusCodes.Status200OK, new SettingsDigestDocument
            {
                ProtocolVersion = GatewayProtocolVersion.Current,
                HostId = identity.HostId,
                Account = account,
                Digest = AccountSettings.Digest(account)
            }).ConfigureAwait(false);
            return;
        }
        SharedSettings result;
        if (context.Request.Method == HttpMethods.Get)
        {
            EnsureEmptyRequest(context.Request);
            AdmitAccountSettings(authenticator.Authenticate(context.Request), space, GatewayMemorySpaceUse.Read);
            result = AccountSettings.Read(account);
        }
        else if (context.Request.Method == HttpMethods.Post)
        {
            var bytes = await ReadInferenceBodyAsync(context.Request, SharedSettings.MaximumBytes, context.RequestAborted).ConfigureAwait(false);
            try
            {
                var principal = authenticator.Authenticate(context.Request, crypto.Sha256(bytes));
                AdmitAccountSettings(principal, space, GatewayMemorySpaceUse.Read);
                AdmitAccountSettings(principal, space, GatewayMemorySpaceUse.Write);
                SharedSettings incoming;
                try { incoming = SharedSettings.Parse(bytes); }
                catch (ContractException) { throw new GatewayProtocolException("request.invalid"); }
                GatewayRules.Require(incoming.Secrets.Count == 0 && incoming.Settings.All(s => s.SecretSha256 is null), "request.invalid");
                result = AccountSettings.Merge(account, incoming);
            }
            finally { CryptographicOperations.ZeroMemory(bytes); }
        }
        else throw new GatewayProtocolException("request.invalid");
        await WriteSettingsAsync(context, result, account).ConfigureAwait(false);
    }

    private void AdmitAccountSettings(GatewayPrincipal principal, string space, GatewayMemorySpaceUse use) =>
        GatewayRules.Require(MemorySpaces.Access(principal, space, use), "settings.account_denied");
}
