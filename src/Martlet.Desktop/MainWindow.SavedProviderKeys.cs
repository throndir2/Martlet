using Martlet.Core.Settings;
using Martlet.Credentials.Windows;

namespace Martlet.Desktop;

/// <summary>Keys the owner saved for a hosted provider anywhere in Martlet (<see cref="SavedProviderKeys"/>): every page that asks
/// for a provider's key says when one is saved already and uses a copy of it, so a key added on one computer (it travels to all
/// of them) is never asked for again.</summary>
public partial class MainWindow
{
    /// <summary>Where a key for the provider at <paramref name="origin"/> is saved ("the Thinking pool"), or null.</summary>
    internal string? SavedKeyWhere(string? origin) =>
        SavedProviderKeys.For(homeSettings, store?.DataDirectory, origin).FirstOrDefault()?.Where;

    /// <summary>A copy of a saved key for the provider at <paramref name="origin"/>, read on a worker; null when none can be
    /// read. The caller disposes it.</summary>
    internal async Task<SecretLease?> ReadSavedKeyAsync(string? origin)
    {
        var keys = SavedProviderKeys.For(homeSettings, store?.DataDirectory, origin);
        return keys.Count == 0 ? null : await Task.Run(() => SavedProviderKeys.Read(new WindowsCredentialStore(), keys));
    }

    string? IJobPageServices.SavedKeyWhere(string? origin) => SavedKeyWhere(origin);
    string? IThinkingCardsServices.SavedKeyWhere(string? origin) => SavedKeyWhere(origin);
    string? IPicturesPageServices.SavedKeyWhere(string? origin) => SavedKeyWhere(origin);
}
