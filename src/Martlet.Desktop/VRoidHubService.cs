using System.IO;
using System.Net.Http;
using System.Text.Json;
using Martlet.Avatar.Hosting;
using Martlet.Core.Settings;
using Martlet.Credentials.Windows;

namespace Martlet.Desktop;

/// <summary>The owner's VRoid Hub link (docs/AVATARS.md#find-a-character-online): one linked VRoid Hub account per Martlet
/// account on this PC, its tokens in Windows Credential Manager (Martlet/v3/vroid-hub/&lt;account&gt;), never in settings or
/// on another computer. It searches VRoid Hub and Open Source Avatars, and downloads a chosen VRoid Hub model into memory when
/// Martlet shows it (<see cref="RemoteCharacterModels.Loader"/>).</summary>
internal sealed class VRoidHubService
{
    private readonly WindowsCredentialStore vault;
    private readonly Func<string> account;
    private readonly string? dataDirectory;
    private readonly SemaphoreSlim refresh = new(1, 1);

    internal VRoidHubService(string? dataDirectory, Func<string> account, WindowsCredentialStore? vault = null)
    {
        this.dataDirectory = dataDirectory;
        this.account = account;
        this.vault = vault ?? new WindowsCredentialStore();
        Search = new CharacterModelSearch(CharacterModelSearch.CreateHttpClient());
    }

    internal CharacterModelSearch Search { get; }
    internal VRoidHub Hub => Search.VRoidHub;
    /// <summary>The linked account changed (linked, unlinked or signed out by VRoid Hub).</summary>
    internal event Action? Changed;

    /// <summary>This Martlet account's key for Credential Manager: its ID in hex, or "pc" before accounts.</summary>
    internal static string AccountKey(Guid? accountId) => accountId is { } id && id != Guid.Empty ? id.ToString("N") : "pc";

    internal static bool CanLink => VRoidHubApp.Configured;

    /// <summary>The linked account's tokens, or null when none is linked here.</summary>
    internal VRoidHubTokens? Tokens() =>
        vault.ReadVRoidHubTokens(account(), out var text) == CredentialError.None ? VRoidHubTokens.Read(text) : null;

    /// <summary>Who is linked, in words, for the Account page and MCP (no tokens).</summary>
    internal string Status() => !CanLink ? "This Martlet build can't link VRoid Hub (it has no VRoid Hub app keys)."
        : Tokens() is { } tokens ? $"Linked as {tokens.UserName} on VRoid Hub." : "Not linked.";

    /// <summary>Links VRoid Hub in the browser and saves the tokens for this Martlet account.</summary>
    internal async Task<VRoidHubTokens> LinkAsync(CancellationToken token)
    {
        var tokens = await Hub.SignInAsync(uri => SignInBrowser.Open(uri.AbsoluteUri), token);
        Save(tokens);
        ErrorLog.Info("VRoid Hub: an account was linked on this PC.");
        Changed?.Invoke();
        return tokens;
    }

    /// <summary>Unlinks: tells VRoid Hub, deletes the tokens and forgets every VRoid Hub model held in memory.</summary>
    internal async Task UnlinkAsync(CancellationToken token)
    {
        if (Tokens() is { } tokens) await Hub.RevokeAsync(tokens, token);
        vault.DeleteVRoidHubTokens(account());
        RemoteCharacterModels.Clear();
        ErrorLog.Info("VRoid Hub: the account was unlinked on this PC.");
        Changed?.Invoke();
    }

    /// <summary>The linked account's tokens, refreshed when they ran out; null when none is linked. A refused refresh unlinks.</summary>
    internal async Task<VRoidHubTokens?> FreshTokensAsync(CancellationToken token)
    {
        if (Tokens() is not { } tokens) return null;
        if (!tokens.Expired(DateTimeOffset.UtcNow)) return tokens;
        await refresh.WaitAsync(token);
        try
        {
            if (Tokens() is not { } current) return null;
            if (!current.Expired(DateTimeOffset.UtcNow)) return current;
            try
            {
                var fresh = await Hub.RefreshAsync(current, token);
                Save(fresh);
                return fresh;
            }
            catch (VRoidHubException error) when (error.SignedOut)
            {
                vault.DeleteVRoidHubTokens(account());
                ErrorLog.Warn("VRoid Hub signed Martlet out; link the account again.");
                Changed?.Invoke();
                return null;
            }
        }
        finally { refresh.Release(); }
    }

    private void Save(VRoidHubTokens tokens)
    {
        var error = vault.WriteVRoidHubTokens(account(), tokens.Write());
        if (error != CredentialError.None) throw new InvalidOperationException($"Windows Credential Manager didn't keep the VRoid Hub link ({error}).");
    }

    /// <summary>Searches the sources for a character's models (the linked account's own first).</summary>
    internal async Task<CharacterModelSearchResult> SearchAsync(string query, int count, CancellationToken token) =>
        await Search.SearchAsync(query, count, await FreshTokensAsync(token), null, token);

    /// <summary>For <see cref="RemoteCharacterModels.Loader"/>: downloads a <c>vroidhub:</c> model into memory with the linked
    /// account. Throws <see cref="Martlet.Core.Contracts.ContractException"/> with a readable reason when it can't.</summary>
    internal async Task<byte[]> LoadModelAsync(string path, CancellationToken token)
    {
        var id = RemoteCharacterModels.VRoidHubId(path) ?? throw new Martlet.Core.Contracts.ContractException(
            Martlet.Core.Contracts.ErrorCode.InvalidContract, "This isn't a VRoid Hub character.");
        var tokens = await FreshTokensAsync(token) ?? throw new Martlet.Core.Contracts.ContractException(Martlet.Core.Contracts.ErrorCode.InvalidContract,
            CanLink ? "Link your VRoid Hub account (Account › Linked services) to show this VRoid Hub character."
                : "This Martlet build can't link VRoid Hub, so it can't show VRoid Hub characters.");
        try
        {
            var bytes = await Hub.DownloadAsync(id, tokens, token);
            ErrorLog.Info($"VRoid Hub: a model was downloaded into memory ({bytes.Length / (1024d * 1024):0.0} MB); it is not saved.");
            return bytes;
        }
        catch (Exception error) when (error is VRoidHubException or HttpRequestException)
        {
            throw new Martlet.Core.Contracts.ContractException(Martlet.Core.Contracts.ErrorCode.InvalidContract, error.Message);
        }
    }

    // ---------- the looks this PC used ----------

    internal IReadOnlyList<VRoidHubLook> Looks() => VRoidHubLooks.Load(dataDirectory);

    internal VRoidHubLook? Look(string? path) => VRoidHubLooks.Find(dataDirectory, path);

    internal void Remember(VRoidHubLook look)
    {
        try { VRoidHubLooks.Remember(dataDirectory, look); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            ErrorLog.Warn("Martlet couldn't keep the VRoid Hub look's credit on this PC: " + error.Message);
        }
    }

    internal void Forget(string id)
    {
        try { VRoidHubLooks.Forget(dataDirectory, id); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { ErrorLog.Warn("Martlet couldn't forget the VRoid Hub look: " + error.Message); }
        RemoteCharacterModels.Forget(RemoteCharacterModels.VRoidHubPath(id));
    }

    /// <summary>A look's words: kept here, else read from VRoid Hub (public) and kept.</summary>
    internal async Task<VRoidHubLook?> DescribeAsync(string path, CancellationToken token)
    {
        if (Look(path) is { } kept) return kept;
        if (RemoteCharacterModels.VRoidHubId(path) is not { } id) return null;
        try
        {
            if (await Hub.ModelAsync(id, Tokens()?.UserId, token) is not { } candidate) return null;
            var look = VRoidHubLook.From(candidate, DateTimeOffset.UtcNow);
            Remember(look);
            return look;
        }
        catch (Exception error) when (error is VRoidHubException or HttpRequestException or Martlet.Core.Contracts.ContractException) { return null; }
    }
}