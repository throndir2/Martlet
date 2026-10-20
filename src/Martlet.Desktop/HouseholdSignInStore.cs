using System.IO;
using System.Text.Json;
using Martlet.Avatar.Audio2Face.Remote;
using Martlet.Credentials.Windows;
using Document = Martlet.Avatar.Audio2Face.Remote.HouseholdSignInFile.Document;
using Summary = Martlet.Avatar.Audio2Face.Remote.HouseholdSignInFile.Summary;

namespace Martlet.Desktop;

/// <summary>
/// What this PC keeps of the household sign-in providers (<see cref="HouseholdSignInFile"/>, household-signin.json): the providers
/// it set up and their client secrets in Windows Credential Manager, and what it last read of every host's providers.
/// </summary>
internal static class HouseholdSignInStore
{
    internal const string FileName = HouseholdSignInFile.FileName;

    internal static Document Load(string directory)
    {
        try { return HouseholdSignInFile.Read(directory); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        {
            ErrorLog.Warn("Household sign-in: couldn't read " + FileName + ": " + error.Message);
            return new();
        }
    }

    internal static void Save(string directory, Document document) => HouseholdSignInFile.Save(directory, document);

    /// <summary>Records a provider this PC saved for the household, keeping a typed client secret in Credential Manager.</summary>
    internal static void Keep(string directory, HouseholdProvider provider, string? secret)
    {
        var document = Load(directory);
        var before = document.Kept.FirstOrDefault(k => k.Id == provider.Id);
        var kept = before?.Secret == true && before.Kind == provider.Kind;
        if (secret is { Length: > 0 })
        {
            var stored = new WindowsCredentialStore().WriteSignInProviderSecret(provider.Id, secret);
            kept = stored == Martlet.Core.Settings.CredentialError.None;
            if (!kept) ErrorLog.Warn($"Household sign-in: couldn't keep {provider.Id}'s client secret on this PC ({stored}); hosts still have it.");
        }
        document.Kept.RemoveAll(k => k.Id == provider.Id);
        document.Kept.Add(KeptHouseholdProvider.From(provider, kept, DateTimeOffset.UtcNow));
        Save(directory, document);
    }

    /// <summary>Forgets a provider removed from the household, and its kept client secret.</summary>
    internal static void Forget(string directory, string id)
    {
        var document = Load(directory);
        if (document.Kept.RemoveAll(k => k.Id == id) > 0) Save(directory, document);
        _ = new WindowsCredentialStore().DeleteSignInProviderSecret(id);
    }

    /// <summary>The client secret this PC keeps for a household provider, or null.</summary>
    internal static string? Secret(string id) =>
        new WindowsCredentialStore().ReadSignInProviderSecret(id, out var value) == Martlet.Core.Settings.CredentialError.None ? value : null;

    /// <summary>Keeps what this PC last read of the hosts' providers, for MCP (never a secret).</summary>
    internal static void Remember(string directory, IReadOnlyList<HouseholdProviderState> states, IReadOnlyDictionary<string, string> problems,
        DateTimeOffset at)
    {
        var document = Load(directory);
        Save(directory, document with
        {
            CheckedAt = at,
            Providers = states.Select(s => new Summary(s.Provider.Id, s.Provider.Kind, s.Provider.Name, [.. s.On], [.. s.Missing], [.. s.Different],
                [.. s.WithoutSecret], document.Kept.Any(k => k.Id == s.Provider.Id && k.Secret))).ToList(),
            Problems = new Dictionary<string, string>(problems, StringComparer.Ordinal)
        });
    }

    /// <summary>What this PC can add to the hosts that miss a household provider (or lack the secret it needs): the household's
    /// configuration (what most hosts have), the hosts, and whether this PC's kept client secret goes with it (only when it kept
    /// one for that same client). A provider that needs a secret this PC doesn't keep for that client is left out (the PC that set
    /// it up adds it); one removed from every host is never added back.</summary>
    internal static IReadOnlyList<(HouseholdProvider Provider, bool WithSecret, IReadOnlyList<string> Hosts)> Missing(Document document,
        IReadOnlyDictionary<string, HostSignInSettings> read)
    {
        var result = new List<(HouseholdProvider, bool, IReadOnlyList<string>)>();
        foreach (var state in HouseholdSignIn.Providers(read))
        {
            var hosts = state.Missing.Concat(state.WithoutSecret).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
            if (hosts.Length == 0) continue;
            var secret = document.Kept.Any(k => k.Id == state.Provider.Id && k.Secret && k.Kind == state.Provider.Kind && k.ClientId == state.Provider.ClientId);
            if (state.Provider.NeedsSecret(state.HasSecret) && !secret) continue;
            result.Add((state.Provider, secret, hosts));
        }
        return result;
    }
}
