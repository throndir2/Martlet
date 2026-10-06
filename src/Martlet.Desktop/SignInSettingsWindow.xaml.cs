using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using Martlet.Avatar.Audio2Face.Remote;
using Martlet.Avatar.Hosting;
using Martlet.Core.Access;
using Martlet.Core.Contracts;
using Martlet.Core.Network;

namespace Martlet.Desktop;

/// <summary>At home, on a member PC: a paired host's sign-in settings (the owner account with its mandatory authenticator
/// and recovery codes, the other allowed identities, the computers that signed in) and the invite to hand a computer that is
/// away. Every change goes to the host over this PC's signed, pinned connection; nothing secret is read back.</summary>
public partial class SignInSettingsWindow : ThemedWindow
{
    private readonly AvatarRemoteHost host;
    private readonly string dataDirectory;
    private readonly CancellationTokenSource lifetime = new();
    private string? pendingSecret;

    internal SignInSettingsWindow(AvatarRemoteHost host, string dataDirectory)
    {
        InitializeComponent();
        this.host = host;
        this.dataDirectory = dataDirectory;
        HeadingText.Text = $"Sign-in from outside: {host.HostId}";
        // The invite carries the outside addresses the network already has for this host (Your Martlet network › Outside addresses).
        try
        {
            if (NetworkIdentity.Load(dataDirectory).Roster?.Host(host.HostId) is { Removed: false, Addresses: { Count: > 0 } outside })
                InviteAddressText.Text = string.Join(" ", outside);
        }
        catch (Exception error) when (error is IOException or ContractException or UnauthorizedAccessException) { }
        Closed += (_, _) => lifetime.Cancel();
        Loaded += async (_, _) => await RunAsync(async connection => Show(await connection.ReadSignInSettingsAsync(lifetime.Token)), "Reading");
    }

    private async Task RunAsync(Func<Audio2FaceHostConnection, Task> action, string doing)
    {
        StatusText.Text = doing + "...";
        try
        {
            using var connection = ClusterSync.Connect(host);
            await action(connection);
        }
        catch (OperationCanceledException) { }
        catch (Exception error) when (error is Audio2FaceHostException or InvalidOperationException or IOException or ContractException or JsonException)
        {
            StatusText.Text = error.Message;
        }
    }

    private void Show(HostSignInSettings settings, string? done = null)
    {
        OwnerStateText.Text = settings.OwnerUser is { } user
            ? $"Set up: {user}, with an authenticator; {settings.RecoveryCodesLeft} recovery code(s) left. Setting it again replaces it."
            : "Not set up. Set a name and password, then add the authenticator secret to your app.";
        if (settings.OwnerUser is { } name) OwnerUserText.Text = name;
        AllowedText.Text = settings.Allowed.Count == 0 ? "None."
            : string.Join(Environment.NewLine, settings.Allowed.Select(a => $"{a.Label ?? a.Subject} ({a.Provider}: {a.Subject})"));
        ProvidersText.Text = settings.Providers.Count == 0 ? "No sign-in providers are set up on this host yet."
            : "Providers: " + string.Join(", ", settings.Providers.Select(p => $"{p.Name} ({p.Id}, {p.Kind})"));
        EnrolledText.Text = settings.Enrolled.Count == 0 ? "None yet."
            : string.Join(Environment.NewLine, settings.Enrolled.Select(e =>
                $"{e.DeviceId}: signed in as {e.Label ?? e.Subject} ({e.Provider}) {e.EnrolledAt.ToLocalTime():g}"));
        if (settings.RecoveryCodes is { Count: > 0 } codes)
        {
            RecoveryText.Text = "Recovery codes (each works once instead of an authenticator code; keep them somewhere safe, Martlet won't show them again):" +
                Environment.NewLine + string.Join("   ", codes);
            RecoveryText.Visibility = Visibility.Visible;
        }
        StatusText.Text = done ?? $"Read {host.HostId}'s sign-in settings.";
    }

    private Task ChangeAsync(JsonObject change, string done) =>
        RunAsync(async connection => Show(await connection.ChangeSignInSettingsAsync(change, lifetime.Token), done), "Saving");

    private void NewSecret_Click(object sender, RoutedEventArgs e)
    {
        pendingSecret = Totp.NewSecret();
        SecretText.Text = pendingSecret;
        SecretLinkText.Text = Totp.Uri("Martlet " + host.HostId, OwnerUserText.Text.Trim(), pendingSecret);
        SecretPanel.Visibility = Visibility.Visible;
        StatusText.Text = "Add the secret to your authenticator app, then type the code it shows.";
    }

    private async void SaveOwner_Click(object sender, RoutedEventArgs e)
    {
        if (pendingSecret is null) return;
        await ChangeAsync(new JsonObject
        {
            ["action"] = "owner", ["user"] = OwnerUserText.Text.Trim(), ["password"] = OwnerPasswordText.Password,
            ["totp_secret"] = pendingSecret, ["code"] = OwnerCodeText.Text.Trim()
        }, "Owner account saved. Write down the recovery codes below.");
        if (RecoveryText.Visibility == Visibility.Visible)
        {
            OwnerPasswordText.Clear();
            OwnerCodeText.Clear();
            SecretPanel.Visibility = Visibility.Collapsed;
            pendingSecret = null;
        }
    }

    private async void NewCodes_Click(object sender, RoutedEventArgs e) =>
        await ChangeAsync(new JsonObject { ["action"] = "recovery-codes" }, "New recovery codes made; the old ones no longer work.");

    private async void RemoveOwner_Click(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show(this, "Remove the owner account? Computers that signed in with it lose access to this host.", "Martlet",
                MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        await ChangeAsync(new JsonObject { ["action"] = "remove-owner" }, "Owner account removed.");
    }

    private async void Allow_Click(object sender, RoutedEventArgs e) => await ChangeAsync(new JsonObject
    {
        ["action"] = "allow", ["provider"] = AllowProviderText.Text.Trim(), ["subject"] = AllowSubjectText.Text.Trim(),
        ["label"] = AllowLabelText.Text.Trim() is { Length: > 0 } label ? label : null
    }, "Allowed.");

    private async void Disallow_Click(object sender, RoutedEventArgs e) => await ChangeAsync(new JsonObject
    {
        ["action"] = "disallow", ["provider"] = AllowProviderText.Text.Trim(), ["subject"] = AllowSubjectText.Text.Trim()
    }, "Removed; computers it signed in lost access.");

    private void MakeInvite_Click(object sender, RoutedEventArgs e)
    {
        var addresses = new List<string>();
        foreach (var part in InviteAddressText.Text.Split([' ', ',', ';'], StringSplitOptions.RemoveEmptyEntries))
        {
            if (NetworkInvite.NormalizeAddress(part) is not { } address)
            {
                StatusText.Text = $"\"{part}\" isn't an address. Use a name or address with its port, like home.example.net:9443.";
                return;
            }
            addresses.Add(address);
        }
        string? network = null;
        try { network = NetworkIdentity.Load(dataDirectory).Roster is { } roster && roster.Host(host.HostId) is { Removed: false } ? roster.NetworkId : null; }
        catch (Exception error) when (error is IOException or ContractException or UnauthorizedAccessException) { }
        var invite = new NetworkInvite
        {
            HostId = host.HostId, SpkiFingerprint = host.SpkiFingerprint, Origin = host.Origin, Addresses = addresses.Take(NetworkInvite.MaximumAddresses).ToArray(),
            NetworkId = network, Label = "Martlet at home"
        };
        InviteText.Text = invite.Write();
        InviteText.Visibility = Visibility.Visible;
        CopyInviteButton.Visibility = Visibility.Visible;
        StatusText.Text = addresses.Count == 0
            ? "Invite made with the home address only: it works only where that address is reachable. Add an outside address for a computer away from home."
            : "Invite made. On the computer away from home: Devices › Add a computer › Join with an invite.";
    }

    private void CopyInvite_Click(object sender, RoutedEventArgs e)
    {
        try { Clipboard.SetText(InviteText.Text); StatusText.Text = "Invite copied."; }
        catch (System.Runtime.InteropServices.COMException) { StatusText.Text = "Couldn't copy; select the invite and copy it yourself."; }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
