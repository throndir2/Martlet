using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Windows;
using Martlet.Avatar.Audio2Face.Remote;
using Martlet.Avatar.Hosting;
using Martlet.Core.Accounts;
using Martlet.Core.Contracts;
using Martlet.Core.Network;

namespace Martlet.Desktop;

/// <summary>A host this PC is paired with, as the Prove window lists it.</summary>
internal sealed record ProveHost(AvatarRemoteHost Host, string Label);

/// <summary>
/// A Prove sign-in (docs/ACCOUNTS.md, Logins) through one of this PC's own hosts: *Sign in as someone else*, *Continue as ...?*,
/// linking this Windows login and merging accounts. The host checks the Martlet password (and the authenticator code when the
/// login has one) and answers its signed attestation that the account proved itself on this PC (<c>/signin/prove</c>); this
/// window checks that attestation against this PC's roster before anyone uses it. The password is never saved: the caller keeps
/// only a verifier of it (<see cref="AccountLockStore.RememberPassword"/>). Provider sign-ins (Google, Authentik and others,
/// W13) add a method to <see cref="MethodList"/> and a branch to <see cref="ProveAsync"/>.
/// </summary>
public partial class AccountProveWindow : ThemedWindow
{
    private readonly string dataDirectory;
    private readonly Guid? expected;
    private readonly CancellationTokenSource lifetime = new();

    internal HostAccountProof? Proof { get; private set; }
    /// <summary>The password that proved the account, for the caller to keep a verifier of; cleared by <see cref="Forget"/>.</summary>
    internal string? Password { get; private set; }
    internal bool Remember => RememberCheck.IsChecked == true;
    internal string? ProvedBy { get; private set; }

    internal AccountProveWindow(IReadOnlyList<ProveHost> hosts, string dataDirectory, string? heading = null, string? hint = null,
        Guid? expectedAccount = null, string? user = null, bool offerRemember = true)
    {
        InitializeComponent();
        this.dataDirectory = dataDirectory;
        expected = expectedAccount;
        if (heading is not null) HeadingText.Text = heading;
        if (hint is not null) HintText.Text = hint;
        UserText.Text = user ?? "";
        RememberCheck.Visibility = RememberHint.Visibility = offerRemember ? Visibility.Visible : Visibility.Collapsed;
        HostChoice.ItemsSource = hosts;
        HostChoice.SelectedIndex = hosts.Count > 0 ? 0 : -1;
        if (hosts.Count == 0)
        {
            SignInButton.IsEnabled = false;
            StatusText.Text = "This PC has no host of your Martlet network to check a sign-in. Pair one under Devices first.";
        }
        Loaded += (_, _) => (UserText.Text.Length == 0 ? (UIElement)UserText : PasswordText).Focus();
        Closed += (_, _) => lifetime.Cancel();
    }

    /// <summary>The hosts of this PC's own Martlet network that it is paired with, by roster name.</summary>
    internal static IReadOnlyList<ProveHost> Hosts(IEnumerable<AvatarRemoteHost> paired, NetworkRoster? roster) =>
        paired.Select(h => new ProveHost(h, roster?.Host(h.HostId)?.Name is { Length: > 0 } name ? $"{name} ({h.HostId})" : h.HostId)).ToArray();

    internal void Forget() => Password = null;

    private async void SignIn_Click(object sender, RoutedEventArgs e)
    {
        if (UserText.Text.Trim().Length == 0 || PasswordText.Password.Length == 0)
        {
            StatusText.Text = "Type your user name and password.";
            return;
        }
        SignInButton.IsEnabled = false;
        var selected = HostChoice.SelectedItem as ProveHost;
        var hosts = (HostChoice.ItemsSource as IReadOnlyList<ProveHost> ?? [])
            .OrderBy(h => ReferenceEquals(h, selected) ? 0 : 1).ToArray();
        var reasons = new List<string>();
        try
        {
            foreach (var choice in hosts)
            {
                StatusText.Text = $"Checking with {choice.Label}...";
                try
                {
                    var proof = await ProveAsync(choice.Host, lifetime.Token);
                    if (Problem(proof) is { } problem)
                    {
                        StatusText.Text = problem;
                        return;
                    }
                    Proof = proof;
                    Password = PasswordText.Password;
                    ProvedBy = choice.Host.HostId;
                    PasswordText.Clear();
                    CodeText.Clear();
                    ErrorLog.Info($"Account sign-in: account {proof.AccountId:N} proved on this PC by {choice.Host.HostId} ({proof.Attestation.Login.Kind}).");
                    DialogResult = true;
                    return;
                }
                catch (Audio2FaceHostException error) when (error.Code.StartsWith("signin.", StringComparison.Ordinal) || error.Code.StartsWith("access.", StringComparison.Ordinal))
                {
                    // The host answered: the sign-in itself is wrong, so other hosts would say the same.
                    StatusText.Text = error.Message;
                    ErrorLog.Info($"Account sign-in refused by {choice.Host.HostId}: {error.Code}.");
                    return;
                }
                catch (Exception error) when (error is Audio2FaceHostException or HttpRequestException or IOException or TimeoutException or
                    InvalidOperationException or JsonException or ContractException ||
                    error is OperationCanceledException && !lifetime.IsCancellationRequested)
                {
                    reasons.Add($"{choice.Label}: {error.Message}");
                }
            }
            StatusText.Text = "No host could check the sign-in. " + string.Join(" ", reasons);
        }
        catch (OperationCanceledException) { }
        finally { SignInButton.IsEnabled = true; }
    }

    private async Task<HostAccountProof> ProveAsync(AvatarRemoteHost host, CancellationToken cancellationToken)
    {
        using var connection = ClusterSync.Connect(host);
        return await connection.ProveWithPasswordAsync(UserText.Text.Trim(), PasswordText.Password, CodeText.Text.Trim() is { Length: > 0 } code ? code : null,
            cancellationToken: cancellationToken);
    }

    // The attestation must hold against this PC's own roster, for this PC, and for the account asked for.
    private string? Problem(HostAccountProof proof)
    {
        var roster = NetworkIdentity.Load(dataDirectory).Roster;
        if (roster is null) return "This PC is in no Martlet network, so it can't check the host's answer.";
        var check = proof.Attestation.Check(roster, DateTimeOffset.UtcNow);
        if (check != AccountAttestationCheck.Valid) return $"The host's answer didn't check out ({check}). Try another host.";
        if (expected is { } account && proof.AccountId != account) return "That login belongs to another account.";
        return null;
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
