using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using Martlet.Avatar.Audio2Face.Remote;
using Martlet.Core.Contracts;
using Martlet.Core.Network;

namespace Martlet.Desktop;

/// <summary>On a computer away from home: paste the owner's invite, pin the host it names, sign in (the owner account here;
/// browser sign-ins where the host offers them) and keep the resulting pairing like any other. The network sync then asks
/// to join, and a member PC at home lets this PC in by the host's sign-in attestation.</summary>
public partial class SignInJoinWindow : ThemedWindow
{
    private readonly string deviceId;
    private readonly Func<Audio2FaceHostPairing, string, Task> keep;
    private readonly CancellationTokenSource lifetime = new();
    private NetworkInvite? invite;
    private string? origin;
    private HostSignInProvider? provider;

    internal SignInJoinWindow(string deviceId, Func<Audio2FaceHostPairing, string, Task> keep, string? invite = null)
    {
        InitializeComponent();
        this.deviceId = deviceId;
        this.keep = keep;
        InviteText.Text = invite ?? "";
        Closed += (_, _) => lifetime.Cancel();
    }

    private async void Connect_Click(object sender, RoutedEventArgs e)
    {
        ConnectButton.IsEnabled = false;
        SignInPanel.Visibility = Visibility.Collapsed;
        try
        {
            invite = NetworkInvite.Parse(InviteText.Text);
            StatusText.Text = $"Reaching {invite.HostId}...";
            var (answered, providers) = await HostSignInClient.ReadProvidersAsync(invite, lifetime.Token);
            origin = answered;
            HostText.Text = $"{invite.Label ?? invite.HostId} ({invite.HostId}) at {new Uri(answered).Authority}, key checked.";
            ProviderList.Children.Clear();
            foreach (var item in providers)
            {
                var choice = new RadioButton
                {
                    Content = item.Name, GroupName = "SignInProvider", Margin = new Thickness(0, 2, 0, 2), Tag = item,
                    IsEnabled = !item.InBrowser
                };
                System.Windows.Automation.AutomationProperties.SetAutomationId(choice, "SignInProvider-" + item.Id);
                choice.Checked += (_, _) => Choose(item);
                ProviderList.Children.Add(choice);
            }
            if (providers.Count == 0)
            {
                StatusText.Text = $"{invite.HostId} has no sign-in set up. At home, open Devices › {invite.HostId} › Sign-in from outside and set up the owner account.";
                return;
            }
            SignInPanel.Visibility = Visibility.Visible;
            ((RadioButton)ProviderList.Children[0]).IsChecked = true;
            StatusText.Text = "Choose how to sign in.";
        }
        catch (OperationCanceledException) { }
        catch (Exception error) when (error is ContractException or Audio2FaceHostException)
        {
            StatusText.Text = error.Message;
        }
        finally { ConnectButton.IsEnabled = true; }
    }

    private void Choose(HostSignInProvider chosen)
    {
        provider = chosen;
        OwnerPanel.Visibility = chosen.Kind == "owner" ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void SignIn_Click(object sender, RoutedEventArgs e)
    {
        if (invite is null || origin is null || provider is null) return;
        SignInButton.IsEnabled = false;
        try
        {
            StatusText.Text = "Signing in...";
            (Audio2FaceHostPairing Pairing, string Secret, HostSignInIdentity Identity) result;
            if (provider.Kind == "owner")
                result = await HostSignInClient.SignInAsOwnerAsync(invite, origin, UserText.Text.Trim(), PasswordText.Password, CodeText.Text.Trim(),
                    deviceId, Environment.MachineName, lifetime.Token);
            else
            {
                StatusText.Text = $"{provider.Name} sign-in needs a newer Martlet on this PC.";
                return;
            }
            PasswordText.Clear();
            CodeText.Clear();
            await keep(result.Pairing, result.Secret);
            StatusText.Text = $"Signed in as {result.Identity}. This PC is paired with {result.Pairing.HostId} and joins your Martlet network by " +
                "itself as soon as one of your computers at home syncs (no check number needed).";
            ErrorLog.Info($"Sign-in: paired with {result.Pairing.HostId} as {result.Identity.Provider}.");
        }
        catch (OperationCanceledException) { }
        catch (Exception error) when (error is Audio2FaceHostException or InvalidOperationException or IOException or ContractException or JsonException)
        {
            StatusText.Text = error.Message;
        }
        finally { SignInButton.IsEnabled = true; }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
