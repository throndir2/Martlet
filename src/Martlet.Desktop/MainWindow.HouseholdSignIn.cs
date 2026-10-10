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

    /// <summary>After this PC joined the network by signing in to a household account through a host (Join with an invite;
    /// joined-account.json), signs that account in here with the host's attestation, which checks against the roster now that
    /// this PC has one, and switches to it between replies. A statement that can't be used any more (it lasts ten minutes) is
    /// forgotten, and the person signs in from the account button instead.</summary>
    private async Task SignInJoinedAccountAsync()
    {
        if (accounts is null || store is null || closing || networkState.Roster is not { } roster) return;
        if (JoinedAccount.Load(store.DataDirectory) is not { } joined) return;
        Guid account;
        try
        {
            var attestation = Martlet.Core.Accounts.AccountAttestation.FromText(joined.Attestation);
            account = attestation.AccountId;
            if (!accounts.State.SignedIn.Contains(account)) accounts.SignIn(attestation, roster, DateTimeOffset.UtcNow);
            JoinedAccount.Forget(store.DataDirectory);
            ErrorLog.Info($"Accounts: this PC joined the network as account {AccountSession.Short(account)}'s computer; signed it in here.");
        }
        catch (Exception error) when (error is ArgumentException or FormatException or IOException or UnauthorizedAccessException or
            Martlet.Core.Contracts.ContractException)
        {
            JoinedAccount.Forget(store.DataDirectory);
            ErrorLog.Warn("Accounts: couldn't sign in the account this PC joined as: " + error.Message);
            ActionText.Text = "This PC joined your Martlet network, but the sign-in to your account can't be used any more. Sign in to your " +
                "account from the account button.";
            return;
        }
        RenderAccount();
        // The household's directory names the account: read it first, so the switch greets the person by name.
        await SyncAccountsAsync();
        if (closing) return;
        if (AccountSwitchBlocked() is { } why) ActionText.Text = $"Your account is signed in on this PC. {why}";
        else await SwitchAccountAsync(account);
    }

    /// <summary>The name the household's account directory gives <paramref name="account"/> ("Sam"), or "a household account"
    /// while this PC doesn't know it yet.</summary>
    private static string HouseholdAccountName(string directory, Guid account)
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
