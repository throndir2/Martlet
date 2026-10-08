using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Martlet.Avatar.Audio2Face.Remote;
using Martlet.Avatar.Hosting;
using Martlet.Core.Access;
using Martlet.Core.Contracts;
using Martlet.Core.Network;

namespace Martlet.Desktop;

/// <summary>At home, on a member PC: a paired host's sign-in settings (the owner account with its mandatory authenticator
/// and recovery codes, the other allowed identities, each one of your computers or a friend's, the computers that signed in)
/// and the invite to hand a computer that is away or a friend. Every change goes to the host over this PC's signed, pinned
/// connection; nothing secret is read back.</summary>
public partial class SignInSettingsWindow : ThemedWindow
{
    private readonly AvatarRemoteHost host;
    private readonly string dataDirectory;
    private readonly CancellationTokenSource lifetime = new();
    private string? pendingSecret;
    private HostSignInSettings? shown;

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
        shown = settings;
        var outside = OutsideAddresses();
        OutsideWarningText.Text = settings.Usable ? ""
            : outside.Count > 0
                ? $"Outside access to {host.HostId} is paused: nobody can sign in to it ({(settings.BlockedReason == "signin.no_allowed_identity" ? "no provider has an allowed identity" : "no owner account or provider")}). " +
                  "Set up the owner account or allow an identity to resume it; its outside addresses are kept."
                : "Nobody can sign in to this host yet. Set up the owner account (or a provider and an allowed identity) before you make it reachable from outside home.";
        OutsideWarningText.Visibility = OutsideWarningText.Text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        RefusedText.Text = settings.Refused.Count == 0 ? "None."
            : string.Join(Environment.NewLine, settings.Refused.Select(r =>
                $"{r.Label ?? r.Subject} ({r.Provider}: {r.Subject}) from {r.DeviceId} {r.EnrolledAt.ToLocalTime():g}"));
        OwnerStateText.Text = settings.OwnerUser is { } user
            ? $"Set up: {user}, with an authenticator; {settings.RecoveryCodesLeft} recovery code(s) left. Setting it again replaces it."
            : "Not set up. Set a name and password, then add the authenticator secret to your app.";
        if (settings.OwnerUser is { } name) OwnerUserText.Text = name;
        AllowedText.Text = settings.Allowed.Count == 0 ? "None."
            : string.Join(Environment.NewLine, settings.Allowed.Select(a => $"{a.Label ?? a.Subject} ({a.Provider}: {a.Subject}), " +
                (a.Friend ? "a friend: this host's engines only" : "one of your computers")));
        AllowedPanel.Children.Clear();
        foreach (var allowed in settings.Allowed) AllowedPanel.Children.Add(AllowedRow(allowed));
        ProvidersText.Text = settings.Providers.Count == 0 ? "No sign-in providers are set up on this host yet."
            : "Providers: " + string.Join(", ", settings.Providers.Select(p => $"{p.Name} ({p.Id}, {p.Kind})"));
        EnrolledText.Text = settings.Enrolled.Count == 0 ? "None yet."
            : string.Join(Environment.NewLine, settings.Enrolled.Select(e =>
                $"{e.DeviceId}: signed in as {e.Label ?? e.Subject} ({e.Provider}), " +
                (e.Friend ? "a friend's computer (this host's engines only)" : "one of your computers") + $", {e.EnrolledAt.ToLocalTime():g}"));
        RemovedText.Text = settings.RemovedFromNetwork.Count == 0 ? "None."
            : string.Join(Environment.NewLine, settings.RemovedFromNetwork.Select(e =>
                $"{e.DeviceId}: its sign-in as {e.Label ?? e.Subject} ({e.Provider}) was removed {e.EnrolledAt.ToLocalTime():g}; your computers remove it from the network on their next sync"));
        if (settings.RecoveryCodes is { Count: > 0 } codes)
        {
            RecoveryText.Text = "Recovery codes (each works once instead of an authenticator code; keep them somewhere safe, Martlet won't show them again):" +
                Environment.NewLine + string.Join("   ", codes);
            RecoveryText.Visibility = Visibility.Visible;
        }
        StatusText.Text = done ?? $"Read {host.HostId}'s sign-in settings.";
    }

    /// <summary>One allowed identity: who it is, what its computers get, and buttons to switch that and to remove it.</summary>
    private FrameworkElement AllowedRow(HostSignInAllowed allowed)
    {
        var key = FriendsOverview.Key(allowed.Provider, allowed.Subject);
        var row = new DockPanel { Margin = new Thickness(0, 4, 0, 4) };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        var change = new Button { Content = allowed.Friend ? "Make one of my computers" : "Make a friend", Margin = new Thickness(8, 0, 0, 0) };
        AutomationProperties.SetAutomationId(change, "SignInAccess-" + key);
        AutomationProperties.SetName(change, (allowed.Friend ? "Make one of my computers: " : "Make a friend: ") + (allowed.Label ?? allowed.Subject));
        change.Click += async (_, _) => await SwitchAccessAsync(allowed);
        var remove = new Button { Content = "Remove", Margin = new Thickness(8, 0, 0, 0) };
        AutomationProperties.SetAutomationId(remove, "SignInRemove-" + key);
        AutomationProperties.SetName(remove, "Remove the sign-in of " + (allowed.Label ?? allowed.Subject));
        remove.Click += async (_, _) => await RemoveAsync(allowed);
        buttons.Children.Add(change);
        buttons.Children.Add(remove);
        DockPanel.SetDock(buttons, Dock.Right);
        row.Children.Add(buttons);
        var text = new TextBlock
        {
            Text = $"{allowed.Label ?? allowed.Subject} ({allowed.Provider}: {allowed.Subject}): " +
                (allowed.Friend ? "a friend. Its computers use only this host's engines and never join your network."
                    : "one of your computers. It joins your Martlet network."),
            TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center
        };
        AutomationProperties.SetAutomationId(text, "SignInAllowed-" + key);
        row.Children.Add(text);
        return row;
    }

    /// <summary>Switches an identity between one of your computers and a friend: the host allows it again with the other access
    /// and revokes the computers it signed in with the old one, which sign in again.</summary>
    private async Task SwitchAccessAsync(HostSignInAllowed allowed)
    {
        var name = allowed.Label ?? allowed.Subject;
        var question = allowed.Friend
            ? $"Make {name} one of your own computers on {host.HostId}? Its computers that signed in as a friend lose access now. When they " +
              "sign in again, they join your Martlet network and can use all your hosts, settings and memories."
            : $"Make {name} a friend on {host.HostId}? Its computers that signed in as yours lose access to this host now, and your computers " +
              "remove them from your Martlet network on their next sync, so they lose every host. When they sign in again, they may use only " +
              "this host's engines.";
        if (!ConfirmationDialog.Confirm(this, question, allowed.Friend ? "Make one of my computers" : "Make a friend")) return;
        await ChangeAsync(new JsonObject
        {
            ["action"] = "allow", ["provider"] = allowed.Provider, ["subject"] = allowed.Subject, ["label"] = allowed.Label,
            ["access"] = allowed.Friend ? HostSignInAccess.Member : HostSignInAccess.Friend
        }, allowed.Friend ? $"{name} is one of your computers now; its computers sign in again to join your network."
            : $"{name} is a friend now: its computers sign in again and then use only {host.HostId}'s engines.");
    }

    private async Task RemoveAsync(HostSignInAllowed allowed)
    {
        var name = allowed.Label ?? allowed.Subject;
        if (!ConfirmationDialog.Confirm(this, allowed.Friend
                ? $"Stop sharing {host.HostId} with {name}? This host revokes their computers at once."
                : $"Remove {name}'s sign-in? This host revokes the computers it signed in, and your computers remove them from your Martlet " +
                  "network on their next sync, so they lose every host.", "Remove sign-in"))
            return;
        await ChangeAsync(new JsonObject { ["action"] = "disallow", ["provider"] = allowed.Provider, ["subject"] = allowed.Subject },
            allowed.Friend ? $"Stopped sharing {host.HostId} with {name}; their computers lost access."
                : $"Removed {name}. This host revoked the computers it signed in, and your computers remove them from your Martlet network on their next sync.");
    }

    private async Task ChangeAsync(JsonObject change, string done)
    {
        // A host reachable from outside home is reachable only while someone can sign in to it: ask before a change that
        // leaves no way to sign in.
        if (shown is { Usable: true } current && !current.UsableAfter(change) && OutsideAddresses().Count > 0 &&
            MessageBox.Show(this, $"{host.HostId} is reachable from outside home ({string.Join(", ", OutsideAddresses())}). After this change nobody " +
                "can sign in to it, so its outside access pauses (its outside addresses are kept) until you set up the owner account or allow " +
                "an identity again. Continue?", "Martlet", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes)
        {
            StatusText.Text = "Nothing changed.";
            return;
        }
        await RunAsync(async connection => Show(await connection.ChangeSignInSettingsAsync(change, lifetime.Token), done), "Saving");
    }

    /// <summary>The outside addresses this host has in the network roster or with its pairing here.</summary>
    private IReadOnlyList<string> OutsideAddresses()
    {
        try
        {
            if (NetworkIdentity.Load(dataDirectory).Roster?.Host(host.HostId) is { Removed: false, Addresses: { Count: > 0 } listed }) return listed;
            return HostRegistry.Load(dataDirectory).FirstOrDefault(h => h.HostId == host.HostId)?.OutsideAddresses ?? [];
        }
        catch (Exception error) when (error is IOException or ContractException or UnauthorizedAccessException or InvalidDataException) { return []; }
    }

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

    private async void Allow_Click(object sender, RoutedEventArgs e)
    {
        var friend = (AllowAccessBox.SelectedItem as ComboBoxItem)?.Tag as string == HostSignInAccess.Friend;
        var name = AllowLabelText.Text.Trim() is { Length: > 0 } typed ? typed : AllowSubjectText.Text.Trim();
        await ChangeAsync(new JsonObject
        {
            ["action"] = "allow", ["provider"] = AllowProviderText.Text.Trim(), ["subject"] = AllowSubjectText.Text.Trim(),
            ["label"] = AllowLabelText.Text.Trim() is { Length: > 0 } label ? label : null,
            ["access"] = friend ? HostSignInAccess.Friend : HostSignInAccess.Member
        }, friend ? $"Allowed {name} as a friend: once they sign in with the invite, their computer may use {host.HostId}'s engines."
            : $"Allowed {name} as one of your computers.");
    }

    private async void Disallow_Click(object sender, RoutedEventArgs e) => await ChangeAsync(new JsonObject
    {
        ["action"] = "disallow", ["provider"] = AllowProviderText.Text.Trim(), ["subject"] = AllowSubjectText.Text.Trim()
    }, "Removed. This host revoked the computers it signed in; your computers remove your own among them from your Martlet network on their next sync.");

    private void ProviderKind_Changed(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (ProviderIdText is null) return;
        switch ((ProviderKindBox.SelectedItem as System.Windows.Controls.ComboBoxItem)?.Tag)
        {
            case "google":
                (ProviderIdText.Text, ProviderNameText.Text, ProviderIssuerText.Text, ProviderScopesText.Text, ProviderPortText.Text) =
                    ("google", "Google", "https://accounts.google.com", "openid email profile", "");
                break;
            case "discord":
                (ProviderIdText.Text, ProviderNameText.Text, ProviderIssuerText.Text, ProviderScopesText.Text, ProviderPortText.Text) =
                    ("discord", "Discord", "", "identify", "53682");
                break;
            case "steam":
                (ProviderIdText.Text, ProviderNameText.Text, ProviderIssuerText.Text, ProviderScopesText.Text, ProviderPortText.Text) =
                    ("steam", "Steam", "", "", "");
                ProviderClientIdText.Text = "";
                break;
        }
    }

    private async void SaveProvider_Click(object sender, RoutedEventArgs e)
    {
        var kind = ((ProviderKindBox.SelectedItem as System.Windows.Controls.ComboBoxItem)?.Tag as string) switch
        {
            "discord" => "discord", "steam" => "steam", _ => "oidc"
        };
        int? port = int.TryParse(ProviderPortText.Text.Trim(), out var number) ? number : null;
        var config = new JsonObject
        {
            ["id"] = ProviderIdText.Text.Trim().ToLowerInvariant(), ["kind"] = kind, ["name"] = ProviderNameText.Text.Trim(),
            ["issuer"] = kind == "oidc" ? ProviderIssuerText.Text.Trim().TrimEnd('/') : null,
            ["client_id"] = ProviderClientIdText.Text.Trim() is { Length: > 0 } client ? client : null,
            ["scopes"] = ProviderScopesText.Text.Trim() is { Length: > 0 } scopes ? scopes : null,
            ["redirect_port"] = port
        };
        if (ProviderSecretText.Password.Length > 0) config["client_secret"] = ProviderSecretText.Password;
        await ChangeAsync(new JsonObject { ["action"] = "provider", ["provider_config"] = config },
            $"Saved {ProviderNameText.Text.Trim()}. Sign in with it once from the computer to allow, then allow it here.");
        ProviderSecretText.Clear();
    }

    private async void RemoveProvider_Click(object sender, RoutedEventArgs e) => await ChangeAsync(new JsonObject
    {
        ["action"] = "remove-provider", ["id"] = ProviderIdText.Text.Trim().ToLowerInvariant()
    }, "Provider removed; computers that signed in with it lost access.");

    private async void AllowRefused_Click(object sender, RoutedEventArgs e) => await AllowNewestAsync(friend: false);

    private async void AllowRefusedFriend_Click(object sender, RoutedEventArgs e) => await AllowNewestAsync(friend: true);

    /// <summary>Allows the identity that signed in most recently without being allowed, as one of your computers or as a friend.</summary>
    private async Task AllowNewestAsync(bool friend)
    {
        if (shown?.Refused.FirstOrDefault() is not { } newest) { StatusText.Text = "Nobody is waiting to be allowed."; return; }
        await ChangeAsync(new JsonObject
        {
            ["action"] = "allow", ["provider"] = newest.Provider, ["subject"] = newest.Subject, ["label"] = newest.Label,
            ["access"] = friend ? HostSignInAccess.Friend : HostSignInAccess.Member
        }, friend
            ? $"Allowed {newest.Label ?? newest.Subject} as a friend. Once they sign in again from {newest.DeviceId}, their computer may use {host.HostId}'s engines."
            : $"Allowed {newest.Label ?? newest.Subject}. Sign in again from {newest.DeviceId}.");
    }

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
