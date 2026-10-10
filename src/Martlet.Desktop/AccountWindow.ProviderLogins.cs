using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Martlet.Avatar.Audio2Face.Remote;
using Martlet.Avatar.Hosting;
using Martlet.Core.Accounts;
using Martlet.Core.Contracts;

namespace Martlet.Desktop;

/// <summary>
/// The Account page's *Sign-ins from your household's providers* (docs/ACCOUNTS.md, Provider logins; W13): the provider logins
/// of the active account as the hosts' allow lists hold them, **Link** for each household provider and **Unlink** for each login.
/// Linking needs a Prove sign-in of the account first (its password or a sign-in already linked) unless this Windows login is the
/// account's only way in so far; then the person signs in with the provider in the browser, every host allows that identity as
/// the account's login and the account directory lists it. Reads and changes run only while this page is open, never in a reply.
/// </summary>
public partial class AccountWindow
{
    private IReadOnlyDictionary<string, HostSignInSettings> providerRead = new Dictionary<string, HostSignInSettings>();
    private bool providerBusy;

    private Dictionary<string, AvatarRemoteHost> ProviderHosts() =>
        host.Hosts.GroupBy(h => h.Host.HostId, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First().Host, StringComparer.Ordinal);

    private async Task ShowProviderLoginsAsync(string? done = null)
    {
        var hosts = ProviderHosts();
        if (hosts.Count == 0)
        {
            ProviderLoginsText.Text = "Your sign-ins are kept on your hosts. Pair a host of your network first.";
            ProviderLoginRows.Children.Clear();
            ProviderLinkButtons.Children.Clear();
            return;
        }
        try
        {
            var (read, problems) = await HouseholdSignIn.ReadAsync(hosts.Keys, id => ClusterSync.Connect(hosts[id]), lifetime.Token);
            providerRead = read;
            RenderProviderLogins(done, problems);
        }
        catch (OperationCanceledException) { }
    }

    private void RenderProviderLogins(string? done, IReadOnlyDictionary<string, string> problems)
    {
        var id = host.ActiveId;
        var logins = HouseholdSignIn.Logins(providerRead, id);
        var providers = HouseholdSignIn.Providers(providerRead).Where(p => p.Provider.Kind is "oidc" or "discord" or "steam").ToArray();
        ProviderLoginRows.Children.Clear();
        foreach (var login in logins)
        {
            var row = new DockPanel { Margin = new Thickness(0, 2, 0, 2) };
            var unlink = new Button { Content = "Unlink", Margin = new Thickness(8, 0, 0, 0) };
            AutomationProperties.SetAutomationId(unlink, "AccountUnlinkLogin-" + FriendsOverview.Key(login.Provider, login.Subject));
            AutomationProperties.SetName(unlink, "Unlink " + login);
            unlink.Click += async (_, _) => await UnlinkLoginAsync(login);
            DockPanel.SetDock(unlink, Dock.Right);
            row.Children.Add(unlink);
            row.Children.Add(new TextBlock
            {
                Text = $"{ProviderName(login.Provider)}: {login.Label ?? login.Subject} (on {string.Join(", ", login.Hosts)})", TextWrapping = TextWrapping.Wrap,
                VerticalAlignment = VerticalAlignment.Center
            });
            ProviderLoginRows.Children.Add(row);
        }
        ProviderLinkButtons.Children.Clear();
        foreach (var state in providers)
        {
            var link = new Button { Content = $"Link {state.Provider.Name}", Margin = new Thickness(0, 0, 8, 4) };
            AutomationProperties.SetAutomationId(link, "AccountLinkProvider-" + state.Provider.Id);
            link.Click += async (_, _) => await LinkProviderAsync(state);
            ProviderLinkButtons.Children.Add(link);
        }
        var text = logins.Count == 0
            ? "No sign-in from a provider is linked to this account yet."
            : "Linked: " + string.Join(", ", logins.Select(l => $"{ProviderName(l.Provider)} as {l.Label ?? l.Subject}")) + ".";
        if (providers.Length == 0)
            text += " Your household has no sign-in provider yet: an admin sets one up under Devices › a host › Sign-in from outside.";
        if (problems.Count > 0) text += " Couldn't read " + string.Join(", ", problems.Keys) + ".";
        ProviderLoginsText.Text = done is null ? text : done + " " + text;
    }

    private string ProviderName(string id) =>
        HouseholdSignIn.Providers(providerRead).FirstOrDefault(p => p.Provider.Id == id)?.Provider.Name ?? id;

    private string ProviderKind(string id) =>
        HouseholdSignIn.Providers(providerRead).FirstOrDefault(p => p.Provider.Id == id)?.Provider.Kind ?? "oidc";

    /// <summary>**Link** a household provider to the active account: prove the account (unless this Windows login is its only way
    /// in so far), sign in with the provider in the browser through a host that has it, allow it on every host as this account's
    /// login, and list it in the account directory.</summary>
    private async Task LinkProviderAsync(HouseholdProviderState state)
    {
        if (providerBusy || Active is not { } account) return;
        var id = account.Id;
        if (host.Roster is not { } roster)
        {
            ProviderLoginsText.Text = "This PC isn't in a Martlet network yet.";
            return;
        }
        var hosts = ProviderHosts();
        providerBusy = true;
        try
        {
            AccountAttestation? proved = null;
            var proves = host.SignedInWithProve || HouseholdSignIn.HasPassword(providerRead, id) || HouseholdSignIn.Logins(providerRead, id).Count > 0;
            if (proves)
            {
                var prove = new AccountProveWindow(host.Hosts, host.DataDirectory, $"Sign in as {host.ActiveName}",
                    $"Prove it's you first, with your Martlet password or a sign-in already linked. Then sign in with {state.Provider.Name} to link it.",
                    id, offerRemember: false) { Owner = this };
                if (prove.ShowDialog() != true || prove.Proof is not { } proof)
                {
                    ProviderLoginsText.Text = "Nothing changed.";
                    return;
                }
                prove.Forget();
                proved = proof.Attestation;
            }
            else if (account.Login(ThisWindowsLogin) is null)
            {
                ProviderLoginsText.Text = $"Sign in as {host.ActiveName} with this Windows login or a password first.";
                return;
            }
            var at = state.On.Concat(hosts.Keys).FirstOrDefault(hosts.ContainsKey) ?? hosts.Keys.First();
            ProviderLoginsText.Text = $"Finish signing in with {state.Provider.Name} in your browser, then come back here.";
            HouseholdLinkResult result;
            using (var connection = ClusterSync.Connect(hosts[at]))
                result = await HouseholdSignIn.LinkInBrowserAsync(connection, id, proved, roster, state.Provider.Id, SignInBrowser.Open, TimeSpan.FromMinutes(5),
                    hosts.Keys, hostId => ClusterSync.Connect(hosts[hostId]), state.Provider.RedirectPort, lifetime.Token);
            Activate();
            // The account directory lists the login too: public facts (kind, provider, subject and a label), never a secret.
            var key = AccountLoginKey.ForProvider(state.Provider.Kind, state.Provider.Id, result.Identity.Subject);
            try
            {
                await host.ChangeDirectoryAsync((directory, signer, now) => directory.Find(id) is { Removed: false } entry && entry.Login(key) is null
                    ? directory.Put(signer, entry.WithLogin(AccountLogin.For(key, Account.CleanText(result.Identity.Label, 128), now)), now)
                    : directory, lifetime.Token);
            }
            catch (Exception error) when (error is InvalidOperationException or ContractException or ArgumentException)
            {
                ErrorLog.Warn("Accounts: the account directory doesn't list the linked sign-in yet", error);
            }
            ErrorLog.Info($"Accounts: linked a {state.Provider.Kind} sign-in ({state.Provider.Id}) to account {AccountSession.Short(id)} on " +
                $"{result.Results.Count(r => r.Saved)} host(s).");
            await ShowProviderLoginsAsync(HouseholdSignIn.Describe(result.Results, $"Linked {result.Identity}"));
        }
        catch (OperationCanceledException) { }
        catch (Exception error) when (ClusterSync.IsHostFailure(error))
        {
            ProviderLoginsText.Text = error.Message;
        }
        finally { providerBusy = false; }
    }

    /// <summary>**Unlink** a login: every host removes it (as for any removed sign-in, computers that joined with it leave the
    /// network) and the account directory no longer lists it. The account keeps at least one login.</summary>
    private async Task UnlinkLoginAsync(HouseholdLogin login)
    {
        if (providerBusy || Active is not { } account) return;
        var id = account.Id;
        var key = AccountLoginKey.ForProvider(ProviderKind(login.Provider), login.Provider, login.Subject);
        if (HouseholdSignIn.UnlinkRefusal(providerRead, id, login, keepsAnotherLogin: account.Logins.Any(l => l.Key != key)) is { } why)
        {
            ProviderLoginsText.Text = why;
            return;
        }
        if (!ConfirmationDialog.Confirm(this, $"Unlink {login} from {host.ActiveName}? It no longer signs in to this account. Computers that " +
                "joined your network with it leave it on your computers' next sync, as for any removed sign-in.", "Unlink"))
            return;
        var hosts = ProviderHosts();
        providerBusy = true;
        try
        {
            var results = await HouseholdSignIn.UnlinkAsync(login, hosts.Keys, hostId => ClusterSync.Connect(hosts[hostId]), lifetime.Token);
            try
            {
                await host.ChangeDirectoryAsync((directory, signer, now) => directory.Find(id) is { Removed: false } entry && entry.Login(key) is not null
                    ? directory.Put(signer, entry.WithoutLogin(key), now)
                    : directory, lifetime.Token);
            }
            catch (Exception error) when (error is InvalidOperationException or ContractException or ArgumentException)
            {
                ErrorLog.Warn("Accounts: the account directory still lists the unlinked sign-in", error);
            }
            ErrorLog.Info($"Accounts: unlinked a sign-in ({login.Provider}) from account {AccountSession.Short(id)} on {results.Count(r => r.Saved)} host(s).");
            await ShowProviderLoginsAsync(HouseholdSignIn.Describe(results, $"Unlinked {login}"));
        }
        catch (OperationCanceledException) { }
        finally { providerBusy = false; }
    }
}
