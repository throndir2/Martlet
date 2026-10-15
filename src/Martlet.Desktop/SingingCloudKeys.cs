using System.IO;
using Martlet.Core.Settings;
using Martlet.Core.Singing;
using Martlet.Core.Sync;
using Martlet.Credentials.Windows;

namespace Martlet.Desktop;

/// <summary>
/// The API keys of Singing's cloud providers on this PC (<see cref="SingingCloud"/>): one key per provider, in Windows Credential
/// Manager under the provider's own scope, with its credential ID in singing-keys.json (<see cref="SingingKeys"/>). An ElevenLabs
/// member without a key of its own uses Speaking's ElevenLabs key (the same scope). The keys travel to the owner's other
/// computers with the shared setting <see cref="SingingKeys.SharedKey"/> (<see cref="Section"/>).
/// </summary>
internal static class SingingCloudKeys
{
    /// <summary>Whether this PC has a key for <paramref name="provider"/>, without reading the vault: its own, or (ElevenLabs)
    /// Speaking's.</summary>
    internal static bool Has(string directory, string provider) =>
        SingingKeys.Load(directory).For(provider) is not null || provider == SingingCloud.ElevenLabs && SpeakingKeyId(directory) is not null;

    /// <summary>Whether <paramref name="provider"/>'s key here is Speaking's ElevenLabs key (the member has none of its own).</summary>
    internal static bool UsesSpeakingKey(string directory, string provider) =>
        provider == SingingCloud.ElevenLabs && SingingKeys.Load(directory).For(provider) is null && SpeakingKeyId(directory) is not null;

    /// <summary>The key for <paramref name="provider"/> on this PC, read from Windows Credential Manager; null when there is none.</summary>
    internal static async Task<string?> ReadAsync(string directory, string provider, CancellationToken token)
    {
        var settings = (await new SettingsStore(directory).LoadAsync(token).ConfigureAwait(false)).Settings;
        if (settings is null) return null;
        var id = SingingKeys.Load(directory).For(provider) ?? (provider == SingingCloud.ElevenLabs ? SpeakingKeyId(settings) : null);
        if (id is not { } credential) return null;
        return Read(SingingCloud.Binding(provider, settings.Profile.Id, credential));
    }

    /// <summary>Saves <paramref name="key"/> as <paramref name="provider"/>'s key for Singing and removes the key it had before
    /// (never Speaking's).</summary>
    internal static async Task SaveAsync(string directory, string provider, SecretLease key, CancellationToken token)
    {
        var settings = (await new SettingsStore(directory).LoadAsync(token).ConfigureAwait(false)).Settings ??
            throw new InvalidOperationException("Set up Martlet first, then add the key.");
        var vault = new WindowsCredentialStore();
        var id = Guid.NewGuid();
        var binding = SingingCloud.Binding(provider, settings.Profile.Id, id);
        var error = await Task.Run(() => vault.Write(binding, key), token).ConfigureAwait(false);
        if (error != CredentialError.None) throw new InvalidOperationException(CredentialMessages.Describe(error));
        var keys = SingingKeys.Load(directory);
        var old = keys.For(provider);
        if (!keys.With(provider, id).Save(directory))
        {
            vault.Delete(binding);
            throw new InvalidOperationException("Couldn't save the key's reference on this PC.");
        }
        if (old is { } previous && previous != SpeakingKeyId(settings))
            vault.Delete(SingingCloud.Binding(provider, settings.Profile.Id, previous));
    }

    /// <summary>The shared setting that carries the keys to the owner's other computers: the providers in its value, their keys in
    /// its secret. A computer that already has the same key keeps it; a new one is saved in its vault; a provider no longer shared
    /// is removed with its key (never Speaking's).</summary>
    internal sealed class Section(string directory) : ISharedSection
    {
        public string Key => SingingKeys.SharedKey;
        public string Title => "Singing cloud keys";
        public string? Default => SingingKeys.Share([]);

        public async Task<SharedLocal?> ReadAsync(CancellationToken token)
        {
            var path = Path.Combine(directory, SingingKeys.FileName);
            var saved = SingingKeys.Load(directory);
            var changed = File.Exists(path) ? new DateTimeOffset(File.GetLastWriteTimeUtc(path), TimeSpan.Zero) : (DateTimeOffset?)null;
            if (saved.Keys.Count == 0) return new(SingingKeys.Share([]), null, true, changed);
            var settings = (await new SettingsStore(directory).LoadAsync(token).ConfigureAwait(false)).Settings;
            if (settings is null) return null;
            List<string> providers = [], keys = [];
            foreach (var provider in SingingCloud.Providers)
                if (saved.For(provider) is { } id && Read(SingingCloud.Binding(provider, settings.Profile.Id, id)) is { } key)
                {
                    providers.Add(provider);
                    keys.Add(key);
                }
            return new(SingingKeys.Share(providers), SingingKeys.Bundle(keys), providers.Count == 0, changed);
        }

        public async Task<SharedApply> ApplyAsync(SharedSetting setting, string? secret, CancellationToken token)
        {
            if (SingingKeys.Parse(setting.Value) is not { } providers)
                return SharedApply.Waiting("They were saved by a newer Martlet. Update this PC to use them.");
            var keys = SingingKeys.Unbundle(secret);
            if (keys.Count < providers.Count) return SharedApply.Waiting("Their API keys haven't reached this PC yet.");
            var saved = SingingKeys.Load(directory);
            if (providers.Count == 0 && saved.Keys.Count == 0) return SharedApply.Done;
            var settings = (await new SettingsStore(directory).LoadAsync(token).ConfigureAwait(false)).Settings;
            if (settings is null) return SharedApply.Waiting("Set up Martlet on this PC first.");
            var vault = new WindowsCredentialStore();
            var speaking = SpeakingKeyId(settings);
            var next = saved;
            List<Core.Settings.CredentialBinding> written = [], gone = [];
            for (var i = 0; i < providers.Count; i++)
            {
                var provider = providers[i];
                if (saved.For(provider) is { } id && Read(SingingCloud.Binding(provider, settings.Profile.Id, id)) == keys[i]) continue;
                var fresh = Guid.NewGuid();
                var binding = SingingCloud.Binding(provider, settings.Profile.Id, fresh);
                using (var lease = new SecretLease(keys[i]))
                    if (vault.Write(binding, lease) is var error && error != CredentialError.None)
                    {
                        foreach (var made in written) vault.Delete(made);
                        return SharedApply.Waiting(CredentialMessages.Describe(error));
                    }
                written.Add(binding);
                if (saved.For(provider) is { } old && old != speaking) gone.Add(SingingCloud.Binding(provider, settings.Profile.Id, old));
                next = next.With(provider, fresh);
            }
            foreach (var (provider, id) in saved.Keys.Where(k => !providers.Contains(k.Key)).ToArray())
            {
                if (id != speaking) gone.Add(SingingCloud.Binding(provider, settings.Profile.Id, id));
                next = next.With(provider, null);
            }
            if (!next.Save(directory))
            {
                foreach (var made in written) vault.Delete(made);
                return SharedApply.Waiting("This PC's Singing keys couldn't be saved. Check access to Martlet's data folder.");
            }
            foreach (var old in gone) vault.Delete(old);
            return SharedApply.Done;
        }
    }

    private static string? Read(Core.Settings.CredentialBinding binding)
    {
        using var read = new WindowsCredentialStore().Read(binding);
        if (read.Error != CredentialError.None || read.Secret is null) return null;
        string? value = null;
        read.Secret.Use(secret => value = new string(secret));
        return value;
    }

    private sealed record SpeakingKey(Guid? Id);

    // Read without the async store (cached by the file's write time), so Has can be asked on any thread for each song.
    private static Guid? SpeakingKeyId(string directory)
    {
        var path = Path.Combine(directory, "settings.json");
        return WorkSharingRoster.Shared.Cached(path, () =>
        {
            try { return new SpeakingKey(File.Exists(path) ? SpeakingKeyId(SettingsJson.Read(File.ReadAllBytes(path))) : null); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or Core.Contracts.ContractException)
            {
                return new SpeakingKey(null);
            }
        }).Id;
    }

    // Speaking's ElevenLabs key, when Speaking uses ElevenLabs with a key (the same credential scope as Singing's ElevenLabs key).
    private static Guid? SpeakingKeyId(AppSettings? settings) =>
        settings?.Setup?.Routes.FirstOrDefault(r => r.Role == SetupRole.Tts && r.RouteType == SetupRouteType.ElevenLabs)?.CredentialId;
}
