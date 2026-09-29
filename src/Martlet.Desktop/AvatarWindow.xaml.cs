using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
using Martlet.Avatar.Hosting;
using Martlet.Avatars;
using Martlet.Core.Contracts;
using Martlet.Core.Settings;
using Microsoft.Win32;

namespace Martlet.Desktop;

public partial class AvatarWindow : ThemedWindow
{
    private readonly AvatarController controller;
    private readonly AvatarProfileStore profiles;
    private readonly ISetupService settings;
    private readonly SetupOperationRunner operations;
    private readonly CancellationTokenSource lifetime = new();
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromMilliseconds(100) };
    private Guid profileId;
    private string? revision;
    private bool busy;
    private bool renderingDraft = true;

    internal AvatarWindow(AvatarController controller, AvatarProfileStore profiles,
        ISetupService settings, SetupOperationRunner operations)
    {
        InitializeComponent();
        this.controller = controller;
        this.profiles = profiles;
        this.settings = settings;
        this.operations = operations;
        RendererChoice.ItemsSource = Enum.GetValues<AvatarRenderer>();
        RendererChoice.SelectedItem = AvatarRenderer.Live2D;
        CharacterChoice.SelectedIndex = 0;
        LipSyncChoice.SelectedIndex = 0;
        CustomModelPanel.IsEnabled = false;
        SourceChoice.ItemsSource = AvatarChannels.BlendshapeNames.Order(StringComparer.Ordinal).ToArray();
        SourceChoice.SelectedItem = "jawOpen";
        ShowRemoteHost();
        ShowConfiguration(AvatarConfiguration.Disabled);
        timer.Tick += (_, _) => StatusText.Text = controller.Status;
        timer.Start();
        renderingDraft = false;
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e) => await ActionAsync(ReloadAsync);
    private async void Reload_Click(object sender, RoutedEventArgs e) => await ActionAsync(ReloadAsync);
    private void Window_Closed(object? sender, EventArgs e)
    {
        timer.Stop();
        if (busy && !controller.IsActive) controller.Revoke();
        lifetime.Cancel();
    }
    private void ShowConfiguration(AvatarConfiguration configuration) =>
        ConfigurationText.Text = Encoding.UTF8.GetString(AvatarJson.WriteConfiguration(configuration));
    private AvatarConfiguration ReadConfiguration() =>
        AvatarJson.ReadConfiguration(Encoding.UTF8.GetBytes(ConfigurationText.Text));
    private void Configuration_Changed(object sender, System.Windows.Controls.TextChangedEventArgs e) => DraftChanged();
    private void Selection_Changed(object sender, System.Windows.Controls.SelectionChangedEventArgs e) => DraftChanged();
    private void CharacterChoice_Changed(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (CustomModelPanel is null) return;
        var builtIn = CharacterChoice.SelectedIndex == 0;
        CustomModelPanel.IsEnabled = !builtIn;
        if (builtIn) RendererChoice.SelectedItem = AvatarRenderer.Live2D;
        DraftChanged();
    }
    private bool BuiltInSelected => CharacterChoice.SelectedIndex == 0;
    private void DraftChanged()
    {
        if (renderingDraft) return;
        controller?.Revoke();
        if (AnalysisPermission is not null) AnalysisPermission.IsChecked = false;
    }

    private async Task ReloadAsync()
    {
        var loaded = await settings.LoadAsync(lifetime.Token);
        if (loaded.Settings is null)
        {
            profileId = Guid.Empty;
            ResultText.Text = "Show character works now. Complete Setup once to save character choices.";
            return;
        }
        profileId = loaded.Settings.Profile.Id;
        if (controller.IsActive && controller.InspectedProfile?.ProfileId != profileId) controller.Revoke();
        var saved = await profiles.LoadAsync(profileId, lifetime.Token);
        if (controller.IsActive && controller.InspectedProfile is { } activeProfile &&
            (saved.Profile is null || !ContractJson.Write(activeProfile, AvatarProfile.MaximumBytes)
                .SequenceEqual(ContractJson.Write(saved.Profile, AvatarProfile.MaximumBytes))))
            controller.Revoke();
        revision = saved.Revision;
        renderingDraft = true;
        try
        {
            if (saved.Profile is { } selected)
            {
                RendererChoice.SelectedItem = selected.Renderer;
                var builtIn = BundledLive2D.IsBuiltIn(selected.ModelPath);
                CharacterChoice.SelectedIndex = builtIn ? 0 : 1;
                CustomModelPanel.IsEnabled = !builtIn;
                ModelPathText.Text = builtIn ? "" : selected.ModelPath;
                LipSyncChoice.SelectedIndex = selected.LipSync switch
                {
                    AvatarLipSync.Loudness => 1, AvatarLipSync.Audio2Face => 2, _ => 0
                };
                AutoShowChoice.IsChecked = selected.AutoShow;
                SdkPathText.Text = selected.SdkDirectory ?? "";
                EndpointText.Text = selected.Endpoint;
                remoteHost = selected.RemoteHost;
                ShowRemoteHost();
                ShowConfiguration(selected.Settings);
            }
        }
        finally { renderingDraft = false; }
        InspectPermission.IsChecked = AnalysisPermission.IsChecked = false;
        ResultText.Text = BundledLive2D.Available
            ? "Choices loaded. Press Show character to open the character on your desktop."
            : "Choices loaded. This build does not include the bundled Live2D runtime; choose a VRM model or an SDK override.";
    }

    private AvatarProfile Selected() => new()
    {
        Version = 1, ProfileId = profileId,
        Renderer = BuiltInSelected ? AvatarRenderer.Live2D : (AvatarRenderer)RendererChoice.SelectedItem,
        ModelPath = BuiltInSelected ? BundledLive2D.Prefix + BundledLive2D.DefaultCharacter : ModelPathText.Text,
        SdkDirectory = string.IsNullOrWhiteSpace(SdkPathText.Text) ? null : SdkPathText.Text,
        Endpoint = EndpointText.Text, Configuration = AvatarProfile.ConfigurationElement(ReadConfiguration()),
        ResourceRevision = controller.InspectedProfile?.ResourceRevision,
        AutoShow = AutoShowChoice.IsChecked == true,
        LipSync = LipSyncChoice.SelectedIndex switch
        {
            1 => AvatarLipSync.Loudness, 2 => AvatarLipSync.Audio2Face, _ => AvatarLipSync.Auto
        },
        RemoteHost = remoteHost
    };

    private AvatarRemoteHost? remoteHost;

    private void ShowRemoteHost() => HostStatusText.Text = remoteHost is { } host
        ? $"Lip-sync is handed to Martlet host {host.HostId} at {host.Origin} (Audio2Face), with this PC's own service and voice loudness as fallbacks. Switch it under Devices > Who does what."
        : "No Martlet host handles lip-sync. Pair one, then hand it lip-sync under Devices > Who does what.";

    private async void Hosts_Click(object sender, RoutedEventArgs e)
    {
        new HostsWindow(profiles, settings) { Owner = this }.ShowDialog();
        // The hosts window saves the pairing into the same avatar document; pick up its new revision.
        await ActionAsync(ReloadAsync);
    }

    private void BrowseModel_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Filter = "Avatar models|*.vrm;*.model3.json", CheckFileExists = true };
        if (dialog.ShowDialog(this) != true) return;
        ModelPathText.Text = dialog.FileName;
        RendererChoice.SelectedItem = dialog.FileName.EndsWith(".vrm", StringComparison.OrdinalIgnoreCase)
            ? AvatarRenderer.Vrm : AvatarRenderer.Live2D;
    }

    private async void Show_Click(object sender, RoutedEventArgs e) => await ActionAsync(async () =>
    {
        if (operations.IsRunning) throw new InvalidOperationException("Finish the current voice/setup action before changing the character.");
        var selected = Selected() with { ResourceRevision = null };
        if (profileId == Guid.Empty) selected = selected with { ProfileId = Guid.NewGuid() };
        else revision = await profiles.SaveAsync(selected, revision, lifetime.Token);
        ResultText.Text = "Opening the character...";
        await controller.ShowAsync(selected, lifetime.Token);
        ResultText.Text = controller.Status;
        if (controller.Capabilities is { } capabilities)
        {
            TargetChoice.ItemsSource = capabilities.Parameters;
            TargetChoice.SelectedIndex = 0;
            CapabilityText.Text = string.Join(Environment.NewLine, capabilities.Parameters.Select(p =>
                $"{p.Id}: {p.Minimum} .. {p.Maximum}; neutral {p.Neutral}; {string.Join(", ", p.Aspects)}"));
        }
    });

    private async void Inspect_Click(object sender, RoutedEventArgs e) => await ActionAsync(async () =>
    {
        if (operations.IsRunning) throw new InvalidOperationException("Finish the current voice/setup action before changing avatar resources.");
        if (InspectPermission.IsChecked != true) throw new InvalidOperationException("Explicit local rendering permission is required.");
        await controller.InspectAsync(Selected(), lifetime.Token);
        TargetChoice.ItemsSource = controller.Capabilities!.Parameters;
        TargetChoice.SelectedIndex = 0;
        CapabilityText.Text = string.Join(Environment.NewLine, controller.Capabilities.Parameters.Select(p =>
            $"{p.Id}: {p.Minimum} .. {p.Maximum}; neutral {p.Neutral}; {string.Join(", ", p.Aspects)}"));
        InspectPermission.IsChecked = false;
        ResultText.Text = "Actual model controls inspected. No A2F inference performed.";
    });

    private void AddMapping_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (TargetChoice.SelectedItem is not RendererParameter target || SourceChoice.SelectedItem is not string source)
                throw new InvalidOperationException("Inspect a model and select an actual target first.");
            var channel = new ChannelReference { Blendshape = source };
            var aspect = AvatarChannels.Aspect(channel);
            if (!target.Aspects.Contains(aspect.ToString(), StringComparer.Ordinal))
                throw new InvalidOperationException("This channel and authored target belong to different aspects.");
            var prior = ReadConfiguration();
            var mappings = prior.MappingProfiles.SelectMany(p => p.Mappings)
                .Where(m => m.TargetParameterId != target.Id).Append(new ChannelMapping
                {
                    Source = channel, TargetParameterId = target.Id,
                    OutputMinimum = target.Minimum, OutputMaximum = target.Maximum
                }).ToArray();
            var aspects = mappings.Select(m => AvatarChannels.Aspect(m.Source)).Distinct().ToArray();
            ShowConfiguration(prior with
            {
                Enabled = false,
                RequestedAspects = Enum.GetValues<AvatarAspect>(),
                OmittedAspects = Enum.GetValues<AvatarAspect>().Except(aspects).ToArray(),
                MappingProfiles = [new() { Id = "user-mapping", SourceId = AvatarController.SourceId,
                    ModelId = controller.Capabilities!.ModelId, Mappings = mappings }],
                Assignments = aspects.Select(a => new AspectAssignment { Aspect = a, SourceId = AvatarController.SourceId,
                    MappingId = "user-mapping", AcceptReduced = ReducedChoice.IsChecked == true }).ToArray()
            });
            ResultText.Text = "Mapping staged; all other aspects explicitly omitted. Review JSON and save before activation.";
        }
        catch (Exception error) when (error is ContractException or InvalidOperationException) { ResultText.Text = error.Message; }
    }

    private void Validate_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var configuration = ReadConfiguration();
            if (controller.Capabilities is not { } caps) throw new InvalidOperationException("Inspect model controls first.");
            var source = new SourceCapabilities { SourceId = AvatarController.SourceId, Backend = AvatarBackend.Audio2Face,
                ChannelsKnown = true, Readiness = RuntimeReadiness.NotChecked,
                Channels = AvatarChannels.BlendshapeNames.Select(name => new ChannelReference { Blendshape = name }).ToArray() };
            var lines = new List<string>();
            foreach (var assignment in configuration.Assignments)
            {
                var model = new ModelCapabilities { ModelId = caps.ModelId, Renderer = Selected().Renderer,
                    MetadataKnown = true, Readiness = RuntimeReadiness.Available,
                    Parameters = caps.Parameters.Where(p => p.Aspects.Contains(assignment.Aspect.ToString(), StringComparer.Ordinal))
                        .Select(p => new ModelParameter { Id = p.Id, Aspect = assignment.Aspect,
                            Minimum = p.Minimum, Maximum = p.Maximum, Neutral = p.Neutral }).ToArray() };
                var result = CompatibilityEngine.Assess(source, model, assignment.Aspect,
                    configuration.MappingProfiles.SingleOrDefault(p => p.Id == assignment.MappingId));
                lines.Add($"{assignment.Aspect}: {result.Compatibility}; {string.Join(" ", result.Issues.Select(i => i.Summary))}");
            }
            ResultText.Text = string.Join(Environment.NewLine, lines) +
                "\nPreview uses allowed A2F schema, not verified channels. The actual backend subset must satisfy every mapping at runtime.";
        }
        catch (Exception error) when (error is ContractException or InvalidOperationException or ArgumentException)
        { ResultText.Text = error.Message; }
    }

    private async void Save_Click(object sender, RoutedEventArgs e) => await ActionAsync(async () =>
    {
        if (controller.IsActive) throw new InvalidOperationException("STOP avatar before saving revised configuration.");
        revision = await profiles.SaveAsync(Selected(), revision, lifetime.Token);
        ResultText.Text = "Avatar document atomically saved. No activation permission persisted.";
    });

    private async void Export_Click(object sender, RoutedEventArgs e) => await ActionAsync(async () =>
    {
        var dialog = new SaveFileDialog { Filter = "Avatar JSON|*.json", FileName = "avatar-export.json" };
        if (dialog.ShowDialog(this) != true) return;
        var bytes = await LocalAvatarFiles.ReadBoundedAsync(profiles.FilePath, AvatarProfile.MaximumBytes, lifetime.Token);
        await using var destination = new FileStream(dialog.FileName, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        await destination.WriteAsync(bytes, lifetime.Token);
        ResultText.Text = "Avatar document exported locally; no assets, credentials, audio or activation permission included.";
    });

    private async void Activate_Click(object sender, RoutedEventArgs e) => await ActionAsync(async () =>
    {
        var currentSettings = await settings.LoadAsync(lifetime.Token);
        if (currentSettings.Settings?.Profile.Id != profileId)
            throw new InvalidOperationException("Application profile changed. Reload and inspect before activation.");
        var selected = Selected();
        var saved = await profiles.LoadAsync(profileId, lifetime.Token);
        if (saved.Profile is null || saved.Revision != revision ||
            !ContractJson.Write(saved.Profile, AvatarProfile.MaximumBytes).SequenceEqual(ContractJson.Write(selected, AvatarProfile.MaximumBytes)))
            throw new InvalidOperationException("Save these exact reviewed choices before activation.");
        lifetime.Token.ThrowIfCancellationRequested();
        await controller.ActivateAsync(selected, AnalysisPermission.IsChecked == true, CancellationToken.None);
        AnalysisPermission.IsChecked = false;
    });

    private async void Restore_Click(object sender, RoutedEventArgs e) => await ActionAsync(async () =>
    {
        if (operations.IsRunning) throw new InvalidOperationException("Finish current voice/setup work before restoring avatar choices.");
        var dialog = new OpenFileDialog { Filter = "Avatar JSON|*.json", CheckFileExists = true };
        if (dialog.ShowDialog(this) != true) return;
        var bytes = await LocalAvatarFiles.ReadBoundedAsync(dialog.FileName, AvatarProfile.MaximumBytes, lifetime.Token);
        var candidate = ContractJson.Read<AvatarProfile>(bytes, AvatarProfile.MaximumBytes);
        if (candidate.ProfileId != profileId) throw new InvalidOperationException("This avatar document belongs to a different application profile.");
        var prior = File.Exists(profiles.FilePath)
            ? await LocalAvatarFiles.ReadBoundedAsync(profiles.FilePath, AvatarProfile.MaximumBytes, lifetime.Token) : null;
        if (!ConfirmationDialog.Confirm(this,
            "Restore these avatar-only choices? Existing bytes are retained as a local backup. Activation stays OFF and resources need fresh inspection.",
            "Explicit avatar recovery")) return;
        await controller.StopAsync();
        await profiles.RestoreAsync(candidate, prior is null ? null : Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(prior)), lifetime.Token);
        await ReloadAsync();
    });

    private async void Stop_Click(object sender, RoutedEventArgs e)
    {
        controller.Revoke();
        await ActionAsync(async () => { await controller.StopAsync(); AnalysisPermission.IsChecked = false; }, allowBusy: true);
    }

    private void Window_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key != System.Windows.Input.Key.Escape) return;
        e.Handled = true;
        Stop_Click(sender, e);
    }

    private async Task ActionAsync(Func<Task> action, bool allowBusy = false)
    {
        if (busy && !allowBusy) { ResultText.Text = "Another avatar operation is still finishing."; return; }
        busy = true;
        try { await action(); }
        catch (OperationCanceledException) { ResultText.Text = "Avatar action canceled; no completed cleanup or rollback assumed."; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ContractException or
            InvalidOperationException or ArgumentException or JsonException or TimeoutException or System.ComponentModel.Win32Exception)
        { ResultText.Text = error.Message; }
        finally { busy = false; }
    }
}
