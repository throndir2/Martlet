using System.IO;
using Martlet.Avatar.Audio2Face.Remote;

namespace Martlet.Desktop;

/// <summary>
/// Household sign-in providers (docs/NETWORK.md): an admin sets a provider up once in a host's Sign-in from outside, and this PC
/// saves it on every host of the network. Here, in the background, this PC adds the household's providers to hosts of the network
/// that miss them: after a network sync brings a host, and whenever Devices › Friends has just read every host's sign-in
/// settings. A provider that needs a client secret is added only by the PC that kept it. Never on a reply's path, so it adds no
/// conversation latency. It also keeps the non-secret summary (household-signin.json) MCP network_status shows.
/// </summary>
public partial class MainWindow
{
    private bool householdSignInBusy;

    /// <summary>Reads every household host's sign-in settings, then adds what is missing.</summary>
    private void QueueHouseholdSignIn() => SyncHouseholdSignInAsync(null, null).Forget();

    /// <summary>Adds the household's providers to the hosts in <paramref name="read"/> that miss them (reading every host first
    /// when null) and keeps the summary.</summary>
    private async Task SyncHouseholdSignInAsync(IReadOnlyDictionary<string, HostSignInSettings>? read, IReadOnlyDictionary<string, string>? problems)
    {
        if (householdSignInBusy || closing || store is null) return;
        var directory = store.DataDirectory;
        var hosts = FriendsHosts().ToDictionary(h => h.HostId, h => h.Pairing, StringComparer.Ordinal);
        if (hosts.Count == 0) return;
        householdSignInBusy = true;
        try
        {
            if (read is null)
            {
                var answered = await HouseholdSignIn.ReadAsync(hosts.Keys, id => ClusterSync.Connect(hosts[id]), lifetime.Token);
                (read, problems) = (answered.Read, answered.Problems);
            }
            var current = new Dictionary<string, HostSignInSettings>(read.Where(r => hosts.ContainsKey(r.Key)), StringComparer.Ordinal);
            foreach (var (provider, withSecret, targets) in HouseholdSignInStore.Missing(HouseholdSignInStore.Load(directory), current))
            {
                var secret = withSecret ? HouseholdSignInStore.Secret(provider.Id) : null;
                if (withSecret && secret is null) continue;
                var results = await HouseholdSignIn.SendAsync(targets.Where(hosts.ContainsKey), id => ClusterSync.Connect(hosts[id]),
                    _ => provider.Change(secret), lifetime.Token);
                foreach (var result in results.Where(r => r.Settings is not null)) current[result.HostId] = result.Settings!;
                ErrorLog.Info("Household sign-in: " + HouseholdSignIn.Describe(results, $"added {provider.Name} ({provider.Id})"));
            }
            HouseholdSignInStore.Remember(directory, HouseholdSignIn.Providers(current), problems ?? new Dictionary<string, string>(), DateTimeOffset.UtcNow);
        }
        catch (OperationCanceledException) { }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            ErrorLog.Warn("Household sign-in: couldn't keep the summary: " + error.Message);
        }
        finally { householdSignInBusy = false; }
    }

    /// <summary>The name the household's account directory gives <paramref name="account"/> ("Sam"), or "a household account"
    /// while this PC doesn't know it yet.</summary>
    private static string AccountName(string directory, Guid account)
    {
        try
        {
            var path = Path.Combine(directory, Martlet.Core.Accounts.AccountDirectory.FileName);
            if (File.Exists(path) && Martlet.Core.Accounts.AccountDirectory.Parse(File.ReadAllBytes(path)).Find(account) is { Removed: false } known)
                return known.Name;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or Martlet.Core.Contracts.ContractException) { }
        return "a household account";
    }
}
