using Martlet.Core.Accounts;
using Martlet.Core.Contracts;
using Martlet.Core.Logs;
using Martlet.Core.Network;
using Microsoft.AspNetCore.Http;

namespace Martlet.Gateway;

/// <summary>Where a gateway keeps its copy of the household's account directory between restarts (accounts.json beside
/// host.json on Linux hosts). <see cref="Load"/> returns null when there is none; either call may throw on storage failure.</summary>
public interface IGatewayAccountStorage
{
    byte[]? Load();
    void Save(byte[] bytes);
}

/// <summary>This host's copy of the household's account directory (docs/ACCOUNTS.md). Paired member desktops read it and merge
/// their changes into it; the host takes an entry only when an active member desktop of its network roster signed it. A copy
/// that cannot be saved is still served from memory, and desktops push it again.</summary>
internal sealed class GatewayAccountDirectoryStore
{
    private readonly object gate = new();
    private AccountDirectory directory = AccountDirectory.Empty;
    private string digest = AccountDirectory.Empty.Digest();
    private IGatewayAccountStorage? storage;

    internal AccountDirectory Current { get { lock (gate) return directory; } }

    internal string Digest { get { lock (gate) return digest; } }

    /// <summary>Loads the saved copy. It is this host's own file, so its entries are taken as they are.</summary>
    internal void Attach(IGatewayAccountStorage value)
    {
        ArgumentNullException.ThrowIfNull(value);
        AccountDirectory? saved = null;
        try { if (value.Load() is { } bytes) saved = AccountDirectory.Parse(bytes); }
        // An unreadable or malformed copy is replaced by the next desktop that syncs.
        catch (Exception) { }
        lock (gate)
        {
            storage = value;
            if (saved is not null) Replace(AccountDirectory.Merge(directory, saved), save: false);
        }
    }

    /// <summary>Takes the entries of <paramref name="incoming"/> that an active member desktop of <paramref name="roster"/>
    /// signed and returns the merged copy and how many entries were refused.</summary>
    internal AccountDirectoryMerge Merge(AccountDirectory incoming, NetworkRoster? roster)
    {
        lock (gate)
        {
            var result = AccountDirectory.Accept(directory, incoming, roster);
            Replace(result.Directory, save: true);
            return result with { Directory = directory };
        }
    }

    private void Replace(AccountDirectory next, bool save)
    {
        var nextDigest = next.Digest();
        if (nextDigest == digest) return;
        directory = next;
        digest = nextDigest;
        if (!save || storage is null) return;
        try { storage.Save(next.Write()); }
        catch (Exception) { }
    }
}

internal sealed partial class GatewayHttpApplication
{
    internal const string AccountsPath = "/martlet/v1/accounts";
    internal const string AccountsDigestPath = AccountsPath + "/digest";
    private const int MaximumAccountsResponseBytes = AccountDirectory.MaximumBytes + 4_096;

    internal GatewayAccountDirectoryStore Accounts { get; } = new();

    private static bool IsAccountsTarget(string rawTarget) => rawTarget is AccountsPath or AccountsDigestPath;

    /// <summary>GET /accounts returns this host's copy of the account directory and POST merges a member desktop's copy into it
    /// (only entries signed by an active member desktop of this host's network count) and returns the merged copy and how many
    /// entries were refused; GET /accounts/digest returns only the copy's digest. Paired devices only, over their signed,
    /// pinned connection; friends and API keys are refused.</summary>
    private async ValueTask InvokeAccountsAsync(HttpContext context, string rawTarget)
    {
        if (rawTarget == AccountsDigestPath)
        {
            GatewayRules.Require(context.Request.Method == HttpMethods.Get, "request.invalid");
            EnsureEmptyRequest(context.Request);
            _ = authenticator.Authenticate(context.Request);
            await WriteJsonAsync(context, StatusCodes.Status200OK, new AccountsDigestDocument
            {
                ProtocolVersion = GatewayProtocolVersion.Current,
                HostId = identity.HostId,
                Digest = Accounts.Digest
            }).ConfigureAwait(false);
            return;
        }
        AccountDirectory result;
        var rejected = 0;
        if (context.Request.Method == HttpMethods.Get)
        {
            EnsureEmptyRequest(context.Request);
            _ = authenticator.Authenticate(context.Request);
            result = Accounts.Current;
        }
        else if (context.Request.Method == HttpMethods.Post)
        {
            var bytes = await ReadInferenceBodyAsync(context.Request, AccountDirectory.MaximumBytes, context.RequestAborted).ConfigureAwait(false);
            var principal = authenticator.Authenticate(context.Request, crypto.Sha256(bytes));
            AccountDirectory incoming;
            try { incoming = AccountDirectory.Parse(bytes); }
            catch (ContractException) { throw new GatewayProtocolException("request.invalid"); }
            var merged = Accounts.Merge(incoming, Network.Roster);
            result = merged.Directory;
            rejected = merged.Rejected;
            if (rejected > 0)
                Logs.Own(LogLevels.Warn, $"Refused {rejected} account entr{(rejected == 1 ? "y" : "ies")} from {principal.DeviceId}: " +
                    "not signed by a member desktop of this host's Martlet network.");
        }
        else throw new GatewayProtocolException("request.invalid");
        await WriteJsonAsync(context, StatusCodes.Status200OK, new AccountsDocument
        {
            ProtocolVersion = GatewayProtocolVersion.Current,
            HostId = identity.HostId,
            Digest = result.Digest(),
            Rejected = rejected,
            Accounts = System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(result.Write())
        }, MaximumAccountsResponseBytes).ConfigureAwait(false);
    }

    private sealed record AccountsDocument
    {
        public required GatewayProtocolVersion ProtocolVersion { get; init; }
        public required string HostId { get; init; }
        public required string Digest { get; init; }
        /// <summary>How many entries of the posted copy this host refused (0 for GET).</summary>
        public required int Rejected { get; init; }
        public required System.Text.Json.JsonElement Accounts { get; init; }
    }

    private sealed record AccountsDigestDocument
    {
        public required GatewayProtocolVersion ProtocolVersion { get; init; }
        public required string HostId { get; init; }
        public required string Digest { get; init; }
    }
}
