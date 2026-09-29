using System.IO;
using System.Text.Json;
using System.Windows;
using Martlet.Avatar.Audio2Face.Remote;
using Martlet.Avatar.Hosting;
using Martlet.Core.Contracts;
using Martlet.Core.Settings;
using Martlet.Credentials.Windows;

namespace Martlet.Desktop;

/// <summary>Sets up Martlet hosts (this PC via Docker Desktop, another PC over SSH with Docker or native Ubuntu,
/// or by hand on the host) and pairs this desktop with one using the host's one-use pairing code.</summary>
public partial class HostsWindow : ThemedWindow
{
    private readonly AvatarProfileStore profiles;
    private readonly ISetupService settings;
    private readonly CancellationTokenSource lifetime = new();
    private readonly string version = typeof(App).Assembly.GetName().Version is { } v ? v.ToString(3) : "0.0.0";
    private AvatarRemoteHost? paired;
    private bool busy;

    internal HostsWindow(AvatarProfileStore profiles, ISetupService settings)
    {
        InitializeComponent();
        this.profiles = profiles;
        this.settings = settings;
        DeviceIdText.Text = HostSetupCommands.SuggestedDeviceId();
        MethodChoice.SelectedIndex = 0;
    }

    private HostSetupMethod Method => (HostSetupMethod)Math.Max(0, MethodChoice.SelectedIndex);

    private HostSetupTarget Target() => new(Method, SshTargetText.Text.Trim(), AddressText.Text.Trim(),
        Method == HostSetupMethod.ThisPcDocker ? HostSetupCommands.SuggestedHostId(Environment.MachineName) : null, version);

    private async void Window_Loaded(object sender, RoutedEventArgs e) => await ActionAsync(async () =>
    {
        paired = (await LoadProfileAsync()).Profile.RemoteHost;
        if (paired is not null) DeviceIdText.Text = paired.DeviceId;
        ShowPaired();
    });

    private void Window_Closed(object? sender, EventArgs e) => lifetime.Cancel();

    private void Method_Changed(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (SshPanel is null) return;
        var ssh = Method is HostSetupMethod.SshDocker or HostSetupMethod.SshNative;
        SshPanel.Visibility = ssh ? Visibility.Visible : Visibility.Collapsed;
        InstallDockerButton.Visibility = Method == HostSetupMethod.ThisPcDocker ? Visibility.Visible : Visibility.Collapsed;
        if (Method == HostSetupMethod.ThisPcDocker) AddressText.Text = HostSetupCommands.ThisPcAddress() ?? "";
        else if (AddressText.Text == HostSetupCommands.ThisPcAddress()) AddressText.Text = "";
        ShowCommand();
    }

    private async void SshTarget_LostFocus(object sender, RoutedEventArgs e)
    {
        if (AddressText.Text.Length > 0 || SshTargetText.Text.Trim().Length == 0) return;
        try { AddressText.Text = await HostSetupCommands.ResolveAsync(SshTargetText.Text.Trim(), lifetime.Token) ?? ""; }
        catch (OperationCanceledException) { }
    }

    private void Target_Changed(object sender, System.Windows.Controls.TextChangedEventArgs e) => ShowCommand();

    private void ShowCommand(HostAction action = HostAction.Setup)
    {
        if (CommandText is null) return;
        try { CommandText.Text = HostSetupCommands.Preview(Target(), action); }
        catch (InvalidOperationException error) { CommandText.Text = error.Message; }
    }

    private void Run(HostAction action)
    {
        ShowCommand(action);
        try
        {
            if (Method == HostSetupMethod.OnHost)
            {
                StatusText.Text = "Run the command shown on the host itself (for example in a terminal there).";
                return;
            }
            HostSetupCommands.Launch(Target(), action);
            StatusText.Text = action switch
            {
                HostAction.Setup => "Setup opened in a console window. Answer its questions there; afterwards pair this PC.",
                HostAction.Pair => "The host console opened. Follow its instructions, then paste the pairing code here and press Pair with host.",
                _ => "Opened in a console window."
            };
        }
        catch (Exception error) when (error is InvalidOperationException or IOException or UnauthorizedAccessException or
            System.ComponentModel.Win32Exception)
        {
            StatusText.Text = error.Message;
        }
    }

    private void Setup_Click(object sender, RoutedEventArgs e) => Run(HostAction.Setup);
    private void AddAudio2Face_Click(object sender, RoutedEventArgs e) => Run(HostAction.AddAudio2Face);
    private void Status_Click(object sender, RoutedEventArgs e) => Run(HostAction.Status);
    private void RemoveAudio2Face_Click(object sender, RoutedEventArgs e) => Run(HostAction.RemoveAudio2Face);
    private void PairConsole_Click(object sender, RoutedEventArgs e) => Run(HostAction.Pair);

    private void InstallDocker_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            HostSetupCommands.InstallDockerDesktop();
            StatusText.Text = "Docker Desktop installation opened in a console window. Start Docker Desktop once it finishes (GPU roles also need WSL 2 and a current NVIDIA driver).";
        }
        catch (System.ComponentModel.Win32Exception error) { StatusText.Text = error.Message; }
    }

    private void CopyDeviceId_Click(object sender, RoutedEventArgs e)
    {
        try { Clipboard.SetText(DeviceIdText.Text.Trim()); StatusText.Text = "Device ID copied."; }
        catch (System.Runtime.InteropServices.ExternalException) { StatusText.Text = "Could not copy; select the device ID and copy it."; }
    }

    private async Task<(AvatarProfile Profile, string? Revision)> LoadProfileAsync()
    {
        var loaded = await settings.LoadAsync(lifetime.Token);
        if (loaded.Settings is null) throw new InvalidOperationException("Complete Setup once so a host pairing can be saved.");
        var id = loaded.Settings.Profile.Id;
        var saved = await profiles.LoadAsync(id, lifetime.Token);
        return (saved.Profile ?? AvatarProfile.BuiltIn(id), saved.Revision);
    }

    private void ShowPaired() => StatusText.Text = paired is { } host
        ? $"Paired with Martlet host {host.HostId} at {host.Origin} as {host.DeviceId}. Automatic lip-sync uses its Audio2Face role when this PC has none."
        : "No Martlet host paired.";

    private async void Pair_Click(object sender, RoutedEventArgs e) => await ActionAsync(async () =>
    {
        var code = HostPairingCode.Parse(PairingCodeBox.Password);
        var device = DeviceIdText.Text.Trim();
        StatusText.Text = $"Pairing with {code.HostId} at {code.Origin}...";
        var (profile, revision) = await LoadProfileAsync();
        var (pairing, secret) = await code.PairAsync(device, lifetime.Token);
        PairingCodeBox.Clear();
        var store = new WindowsCredentialStore();
        using (var lease = new SecretLease(secret))
        {
            var stored = store.WriteAvatarHostSecret(pairing.HostId, pairing.CredentialId, lease);
            if (stored != CredentialError.None) throw new InvalidOperationException(CredentialMessages.Describe(stored));
        }
        var previous = profile.RemoteHost;
        paired = new AvatarRemoteHost
        {
            Origin = pairing.Origin, HostId = pairing.HostId, SpkiFingerprint = pairing.SpkiFingerprint,
            DeviceId = pairing.DeviceId, CredentialId = pairing.CredentialId
        };
        await profiles.SaveAsync(profile with { RemoteHost = paired }, revision, lifetime.Token);
        if (previous is not null && previous.CredentialId != paired.CredentialId)
            store.DeleteAvatarHostSecret(previous.HostId, previous.CredentialId);
        ShowPaired();
        StatusText.Text += " Paired. In the host console press a key, then type stop and confirm. Show the character again to use it.";
    });

    private async void Check_Click(object sender, RoutedEventArgs e) => await ActionAsync(async () =>
    {
        if (paired is not { } host) { ShowPaired(); return; }
        using var read = new WindowsCredentialStore().ReadAvatarHostSecret(host.HostId, host.CredentialId);
        if (read.Error != CredentialError.None || read.Secret is null)
            throw new InvalidOperationException("This PC's pairing secret is missing; pair again.");
        Audio2FaceHostConnection? connection = null;
        read.Secret.Use(secret => connection = new Audio2FaceHostConnection(GatewayAvatarHostLink.Pairing(host), secret));
        using (connection)
        {
            StatusText.Text = $"Checking {host.HostId} at {host.Origin}...";
            var route = await connection!.ReadRouteAsync(lifetime.Token);
            StatusText.Text = route is null
                ? $"Host {host.HostId} is reachable and this PC is paired, but it has no Audio2Face role yet (Add Audio2Face role)."
                : $"Host {host.HostId} is reachable and offers Audio2Face (model {route.ModelId}).";
        }
    });

    private async void Forget_Click(object sender, RoutedEventArgs e) => await ActionAsync(async () =>
    {
        if (paired is not { } host) { ShowPaired(); return; }
        var (profile, revision) = await LoadProfileAsync();
        await profiles.SaveAsync(profile with { RemoteHost = null }, revision, lifetime.Token);
        new WindowsCredentialStore().DeleteAvatarHostSecret(host.HostId, host.CredentialId);
        paired = null;
        ShowPaired();
        StatusText.Text += $" Forgotten here; also revoke {host.DeviceId} on the host (Pair this PC console: revoke).";
    });

    private async Task ActionAsync(Func<Task> action)
    {
        if (busy) { StatusText.Text = "Another host action is still finishing."; return; }
        busy = true;
        try { await action(); }
        catch (OperationCanceledException) { }
        catch (Audio2FaceHostException error) { StatusText.Text = error.Message; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ContractException or
            InvalidOperationException or ArgumentException or JsonException or TimeoutException)
        { StatusText.Text = error.Message; }
        finally { busy = false; }
    }
}
