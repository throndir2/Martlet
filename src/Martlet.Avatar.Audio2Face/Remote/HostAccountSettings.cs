using System.Security.Cryptography;
using Martlet.Core.Sync;

namespace Martlet.Avatar.Audio2Face.Remote;

/// <summary>The host's copy of one account's settings (docs/ACCOUNTS.md, "Account settings"): that person's personalities,
/// replies, prompts, theme and the rest (<see cref="SettingScopes"/>), never an API key. A host gives it only to a device where
/// the account is signed in (<c>settings.account_denied</c> otherwise); hosts older than it answer <c>route.not_found</c> or
/// <c>request.invalid</c>.</summary>
public sealed partial class Audio2FaceHostConnection
{
    private const string AccountSettingsPath = SettingsPath + "/accounts/";

    /// <summary>The digest of the host's copy of <paramref name="account"/>'s settings, to read the copy only when it changed.</summary>
    public Task<string> ReadAccountSettingsDigestAsync(Guid account, CancellationToken cancellationToken = default) =>
        SettingsDigestAsync(AccountSettingsPath + AccountPath(account) + "/digest", cancellationToken);

    /// <summary>Reads the host's copy of <paramref name="account"/>'s settings.</summary>
    public async Task<SharedSettings> ReadAccountSettingsAsync(Guid account, CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, pairing.Origin + AccountSettingsPath + AccountPath(account));
        Sign(request, []);
        return await SettingsAsync(request, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Merges <paramref name="settings"/> into the host's copy of <paramref name="account"/>'s settings and returns the
    /// merged copy, which may include newer changes another computer made. An account's settings never carry a secret.</summary>
    public async Task<SharedSettings> MergeAccountSettingsAsync(Guid account, SharedSettings settings, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (settings.Secrets.Count > 0 || settings.Settings.Any(s => s.SecretSha256 is not null))
            throw new ArgumentException("An account's settings never carry an API key.", nameof(settings));
        var path = AccountSettingsPath + AccountPath(account);
        var body = settings.Write();
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, pairing.Origin + path)
            {
                Content = Audio2FaceHostClient.JsonContent(body)
            };
            Sign(request, body);
            return await SettingsAsync(request, cancellationToken).ConfigureAwait(false);
        }
        finally { CryptographicOperations.ZeroMemory(body); }
    }

    private static string AccountPath(Guid account) =>
        account == Guid.Empty ? throw new ArgumentException("An account ID is required.", nameof(account)) : account.ToString("N");
}
