using System.ComponentModel;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Martlet.Avatar.Hosting;
using Martlet.Avatars;
using Martlet.Core.Contracts;
using Martlet.Core.Settings;
using Microsoft.Win32;

namespace Martlet.Desktop;

/// <summary>The character: which model, how its mouth moves, whether it shows at startup, and advanced Audio2Face mapping.
/// There is no Save button: each choice saves on its own (a pause after typing a path, at once for a pick) into the newest
/// avatar document, keeping the lip-sync host chosen elsewhere (the Lip-sync page, the Devices map, or another computer through
/// who-does-what sync). A showing character switches to a newly chosen model or lip-sync mode right away.</summary>
public partial class AvatarWindow : ThemedWindow
{
    private readonly AvatarController controller;
    private readonly AvatarProfileStore profiles;
    private readonly ISetupService settings;
    private readonly SetupOperationRunner operations;
    private readonly SpeechCaptions? captions;
    private readonly CancellationTokenSource lifetime = new();
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromMilliseconds(100) };
    private readonly AutoSave autoSave;
    private Guid profileId;
    private string? revision;
    private bool busy;
    private bool loaded;
    private bool renderingDraft = true;
    private bool closeConfirmed;
    private bool finishing;
    /// <summary>Why the current choices can't be saved (an invalid path or mapping), or why the last save failed.</summary>
    private string? problem;
    private string? saveError;
    private bool saving;

    internal AvatarWindow(AvatarController controller, AvatarProfileStore profiles,
        ISetupService settings, SetupOperationRunner operations, SpeechCaptions? captions = null)
    {
        autoSave = new AutoSave(SaveChoicesAsync);
        autoSave.Settled += () => { if (!lifetime.IsCancellationRequested) RenderSaveState(); };
        InitializeComponent();
        this.controller = controller;
        this.profiles = profiles;
        this.settings = settings;
        this.operations = operations;
        this.captions = captions;
        ShowSpeechDisplay();
        if (captions is not null) captions.Changed += ShowSpeechDisplay;
        SpeechBubbleChoice.IsEnabled = SubtitleChoice.IsEnabled = captions is not null;
        RendererChoice.ItemsSource = Enum.GetValues<AvatarRenderer>();
        RendererChoice.SelectedItem = AvatarRenderer.Live2D;
        CharacterChoice.SelectedIndex = 0;
        LipSyncChoice.SelectedIndex = 0;
        CustomModelPanel.IsEnabled = false;
        SourceChoice.ItemsSource = AvatarChannels.BlendshapeNames.Order(StringComparer.Ordinal).ToArray();
        SourceChoice.SelectedItem = "jawOpen";
        ShowRemoteHost();
        ShowConfiguration(AvatarConfiguration.Disabled);
        timer.Tick += (_, _) => RenderShowing();
        timer.Start();
        RenderShowing();
        RenderSaveState();
        renderingDraft = false;
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e) => await ActionAsync(ReloadAsync);

    private void RenderShowing()
    {
        StatusText.Text = controller.Status;
        var showing = controller.IsShowing;
        ShowButton.Visibility = showing ? Visibility.Collapsed : Visibility.Visible;
        HideButton.Visibility = showing ? Visibility.Visible : Visibility.Collapsed;
    }

    private void RenderSaveState()
    {
        var unsaved = problem ?? saveError;
        SaveStateText.Text = !loaded ? "Loading your choices..."
            : problem is not null ? "Not saved yet: " + problem
            : saveError is not null ? "Not saved: " + saveError + " Martlet tries again with your next change."
            : saving || autoSave.Pending ? "Saving..."
            : profileId == Guid.Empty ? "Finish Setup once, and your character choices save on their own."
            : "All changes saved.";
        SaveStateText.SetResourceReference(TextBlock.ForegroundProperty, unsaved is null ? "MutedBrush" : "WarningBrush");
    }

    private bool showingSpeechDisplay;
    private void ShowSpeechDisplay()
    {
        showingSpeechDisplay = true;
        try
        {
            SpeechBubbleChoice.IsChecked = captions?.Preferences.SpeechBubbles == true;
            SubtitleChoice.IsChecked = captions?.Preferences.Subtitles == true;
        }
        finally { showingSpeechDisplay = false; }
    }
    // Checked/Unchecked rather than Click, so UI Automation and screen-reader toggles save too.
    private void SpeechDisplay_Changed(object sender, RoutedEventArgs e)
    {
        if (captions is null || showingSpeechDisplay) return;
        var saved = captions.Update(captions.Preferences with
        {
            SpeechBubbles = SpeechBubbleChoice.IsChecked == true, Subtitles = SubtitleChoice.IsChecked == true
        });
        SpeechDisplayStatus.Text = saved
            ? "Saved. Changes apply the next time Martlet speaks."
            : "Changes applied for now, but couldn't be saved. Check your data folder.";
    }
    private void Window_Closed(object? sender, EventArgs e)
    {
        if (captions is not null) captions.Changed -= ShowSpeechDisplay;
        timer.Stop();
        autoSave.Cancel();
        if (busy && !controller.IsActive) controller.Revoke();
        lifetime.Cancel();
    }

    /// <summary>Closing saves a choice still waiting for its pause first. Only a choice that can't be saved asks first.</summary>
    private async void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (closeConfirmed || !loaded || !autoSave.Pending && problem is null && saveError is null) return;
        e.Cancel = true;
        if (finishing) return;
        finishing = true;
        try
        {
            for (var tries = 0; tries < 50 && autoSave.Pending && problem is null && saveError is null; tries++)
            {
                await autoSave.SaveNowAsync();
                if (autoSave.Pending) await Task.Delay(100);
            }
            var unsaved = problem ?? saveError ?? (autoSave.Pending ? "Another character action is still finishing." : null);
            if (unsaved is not null && !ConfirmationDialog.Confirm(this,
                    $"Your latest character choice isn't saved: {unsaved}\n\nClose anyway and lose it?", "Unsaved change"))
                return;
            closeConfirmed = true;
            await Dispatcher.InvokeAsync(Close);
        }
        finally { finishing = false; }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void ShowConfiguration(AvatarConfiguration configuration) =>
        ConfigurationText.Text = Encoding.UTF8.GetString(AvatarJson.WriteConfiguration(configuration));
    private AvatarConfiguration ReadConfiguration() =>
        AvatarJson.ReadConfiguration(Encoding.UTF8.GetBytes(ConfigurationText.Text));

    /// <summary>A typed field (model path, SDK folder, endpoint, mapping JSON) saves after a short pause.</summary>
    private void Configuration_Changed(object sender, TextChangedEventArgs e)
    {
        if (!DraftChanged()) return;
        if (ReferenceEquals(sender, ModelPathText) && !BuiltInSelected)
        {
            // Keep the advanced renderer choice in step with the typed model's file type.
            renderingDraft = true;
            try { RendererChoice.SelectedItem = RendererFor(ModelPathText.Text.Trim()); }
            finally { renderingDraft = false; }
        }
        autoSave.Changed();
        RenderSaveState();
    }

    /// <summary>A pick (lip-sync mode, renderer) saves at once.</summary>
    private void Selection_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (DraftChanged()) SaveNow();
    }

    private void CharacterChoice_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (CustomModelPanel is null) return;
        var builtIn = CharacterChoice.SelectedIndex == 0;
        CustomModelPanel.IsEnabled = !builtIn;
        if (builtIn) RendererChoice.SelectedItem = AvatarRenderer.Live2D;
        if (DraftChanged()) SaveNow();
    }

    /// <summary>Show at startup only takes effect at the next start, so it saves without touching lip-sync.</summary>
    private void AutoShow_Changed(object sender, RoutedEventArgs e)
    {
        if (!renderingDraft && loaded) SaveNow();
    }

    private bool BuiltInSelected => CharacterChoice.SelectedIndex == 0;

    /// <summary>A changed choice ends an activated Audio2Face session (it was reviewed for the old choices). Returns whether
    /// the change came from the user rather than from loading.</summary>
    private bool DraftChanged()
    {
        if (renderingDraft || !loaded) return false;
        problem = null;
        controller?.Revoke();
        if (AnalysisPermission is not null) AnalysisPermission.IsChecked = false;
        return true;
    }

    private void SaveNow()
    {
        autoSave.SaveNowAsync().Forget();
        RenderSaveState();
    }

    private async Task ReloadAsync()
    {
        var loadedSettings = await settings.LoadAsync(lifetime.Token);
        loaded = true;
        if (loadedSettings.Settings is null)
        {
            profileId = Guid.Empty;
            ResultText.Text = "You can show the character now. Finish Setup once, and your choices save on their own.";
            RenderSaveState();
            return;
        }
        profileId = loadedSettings.Settings.Profile.Id;
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
        problem = saveError = null;
        InspectPermission.IsChecked = AnalysisPermission.IsChecked = false;
        ResultText.Text = BundledLive2D.Available ? "" : "Choose your own model or select a Live2D SDK folder.";
        RenderSaveState();
    }

    private AvatarProfile Selected() => new()
    {
        Version = 1, ProfileId = profileId,
        Renderer = BuiltInSelected ? AvatarRenderer.Live2D : RendererFor(ModelPathText.Text.Trim()),
        ModelPath = BuiltInSelected ? BundledLive2D.Prefix + BundledLive2D.DefaultCharacter : ModelPathText.Text.Trim(),
        SdkDirectory = string.IsNullOrWhiteSpace(SdkPathText.Text) ? null : SdkPathText.Text.Trim(),
        Endpoint = EndpointText.Text.Trim(), Configuration = AvatarProfile.ConfigurationElement(ReadConfiguration()),
        ResourceRevision = controller.InspectedProfile?.ResourceRevision,
        AutoShow = AutoShowChoice.IsChecked == true,
        LipSync = LipSyncChoice.SelectedIndex switch
        {
            1 => AvatarLipSync.Loudness, 2 => AvatarLipSync.Audio2Face, _ => AvatarLipSync.Auto
        },
        RemoteHost = remoteHost
    };

    /// <summary>The auto-save. Writes the choices into the newest avatar document (its lip-sync host is chosen elsewhere and
    /// kept), then switches a showing character to a new model, renderer, SDK folder, endpoint or lip-sync mode. Returns false
    /// to be tried again shortly while another character action runs or another save landed in between.</summary>
    private async Task<bool> SaveChoicesAsync()
    {
        if (!loaded || lifetime.IsCancellationRequested) return true;
        if (busy) return false;
        AvatarProfile choice;
        try
        {
            choice = Selected();
            if (!BuiltInSelected && !IsModelFile(choice.ModelPath))
                throw new ArgumentException("Choose your model file: an existing .vrm or .model3.json file.");
            if (profileId != Guid.Empty) choice.Validate();
        }
        catch (Exception error) when (error is ContractException or JsonException or ArgumentException or InvalidOperationException or
            IOException or UnauthorizedAccessException or NotSupportedException)
        {
            problem = error.Message;
            RenderSaveState();
            return true;
        }
        if (profileId == Guid.Empty)
        {
            RenderSaveState();
            return true;
        }
        busy = true;
        saving = true;
        RenderSaveState();
        AvatarProfile? before;
        AvatarProfile next;
        try
        {
            var current = await profiles.LoadAsync(profileId, lifetime.Token);
            next = choice with { RemoteHost = current.Profile?.RemoteHost };
            before = current.Profile;
            if (before is null || !Bytes(before).SequenceEqual(Bytes(next)))
                revision = await profiles.SaveAsync(next, current.Revision, lifetime.Token);
            else revision = current.Revision;
            remoteHost = next.RemoteHost;
            ShowRemoteHost();
            problem = saveError = null;
        }
        catch (ContractException error) when (error.Message.Contains("changed", StringComparison.OrdinalIgnoreCase))
        {
            // Another save (such as a lip-sync host chosen on another page) landed between reading and writing.
            busy = saving = false;
            return false;
        }
        catch (OperationCanceledException)
        {
            busy = saving = false;
            return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ContractException or
            InvalidOperationException or ArgumentException or JsonException)
        {
            saveError = error is IOException or UnauthorizedAccessException
                ? "Martlet couldn't write the character settings file. Check access to your data folder." : error.Message;
            busy = saving = false;
            if (!lifetime.IsCancellationRequested) RenderSaveState();
            return true;
        }
        saving = false;
        try
        {
            if (controller.IsShowing && Shown(before) != Shown(next) && !operations.IsRunning)
            {
                ResultText.Text = "Switching the character...";
                RenderSaveState();
                await controller.ShowAsync(next with { ResourceRevision = null }, lifetime.Token);
                ResultText.Text = controller.Status;
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ContractException or
            InvalidOperationException or ArgumentException or JsonException or TimeoutException or Win32Exception)
        {
            ResultText.Text = "Saved, but the character couldn't switch: " + error.Message;
        }
        finally
        {
            busy = false;
            if (!lifetime.IsCancellationRequested)
            {
                RenderShowing();
                RenderSaveState();
            }
        }
        return true;

        static byte[] Bytes(AvatarProfile profile) => ContractJson.Write(profile, AvatarProfile.MaximumBytes);
        static (AvatarRenderer, string, string?, string, AvatarLipSync)? Shown(AvatarProfile? profile) => profile is null ? null
            : (profile.Renderer, profile.ModelPath, profile.SdkDirectory, profile.Endpoint, profile.LipSync);
    }

    private static bool IsModelFile(string path) =>
        (path.EndsWith(".vrm", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".model3.json", StringComparison.OrdinalIgnoreCase)) &&
        Path.IsPathFullyQualified(path) && File.Exists(path);

    /// <summary>An own model's renderer follows its file type (.vrm is VRM, .model3.json is Live2D), so a typed path needs no
    /// renderer choice; any other file keeps the advanced renderer choice.</summary>
    private AvatarRenderer RendererFor(string path) =>
        path.EndsWith(".vrm", StringComparison.OrdinalIgnoreCase) ? AvatarRenderer.Vrm
        : path.EndsWith(".model3.json", StringComparison.OrdinalIgnoreCase) ? AvatarRenderer.Live2D
        : RendererChoice.SelectedItem as AvatarRenderer? ?? AvatarRenderer.Live2D;

    private AvatarRemoteHost? remoteHost;

    private void ShowRemoteHost() => HostStatusText.Text = remoteHost is { } host
        ? $"Lip-sync can use host {host.HostId}. Change this on the Lip-sync page or the Devices map."
        : "No host is selected for lip-sync. Set one up on the Lip-sync page or the Devices map.";

    private async void Hosts_Click(object sender, RoutedEventArgs e)
    {
        await autoSave.SaveNowAsync();
        new HostsWindow(profiles, settings) { Owner = this }.ShowDialog();
        // The hosts window saves the pairing into the same avatar document; pick up its new revision.
        await ActionAsync(ReloadAsync);
    }

    private void BrowseModel_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Filter = "Avatar models|*.vrm;*.model3.json", CheckFileExists = true };
        if (dialog.ShowDialog(this) != true) return;
        var wasDrafting = renderingDraft;
        renderingDraft = true;
        try
        {
            ModelPathText.Text = dialog.FileName;
            RendererChoice.SelectedItem = dialog.FileName.EndsWith(".vrm", StringComparison.OrdinalIgnoreCase)
                ? AvatarRenderer.Vrm : AvatarRenderer.Live2D;
        }
        finally { renderingDraft = wasDrafting; }
        if (DraftChanged()) SaveNow();
    }

    private async void Show_Click(object sender, RoutedEventArgs e)
    {
        await autoSave.SaveNowAsync();
        await ActionAsync(async () =>
        {
            if (operations.IsRunning) throw new InvalidOperationException("Wait for the current setup or voice action to finish before changing the character.");
            var selected = Selected() with { ResourceRevision = null };
            if (profileId == Guid.Empty) selected = selected with { ProfileId = Guid.NewGuid() };
            ResultText.Text = "Opening the character...";
            await controller.ShowAsync(selected, lifetime.Token);
            ResultText.Text = controller.Status;
            RenderShowing();
            if (controller.Capabilities is { } capabilities)
            {
                TargetChoice.ItemsSource = capabilities.Parameters;
                TargetChoice.SelectedIndex = 0;
                CapabilityText.Text = string.Join(Environment.NewLine, capabilities.Parameters.Select(p =>
                    $"{p.Id}: {p.Minimum} .. {p.Maximum}; neutral {p.Neutral}; {string.Join(", ", p.Aspects)}"));
            }
        });
    }

    private async void Inspect_Click(object sender, RoutedEventArgs e) => await ActionAsync(async () =>
    {
        if (operations.IsRunning) throw new InvalidOperationException("Wait for the current setup or voice action to finish before changing the character.");
        if (InspectPermission.IsChecked != true) throw new InvalidOperationException("Allow local model inspection first.");
        await controller.InspectAsync(Selected(), lifetime.Token);
        TargetChoice.ItemsSource = controller.Capabilities!.Parameters;
        TargetChoice.SelectedIndex = 0;
        CapabilityText.Text = string.Join(Environment.NewLine, controller.Capabilities.Parameters.Select(p =>
            $"{p.Id}: {p.Minimum} .. {p.Maximum}; neutral {p.Neutral}; {string.Join(", ", p.Aspects)}"));
        InspectPermission.IsChecked = false;
        ResultText.Text = "Model controls inspected.";
    });

    private void AddMapping_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (TargetChoice.SelectedItem is not RendererParameter target || SourceChoice.SelectedItem is not string source)
                throw new InvalidOperationException("Inspect a model and choose a model control first.");
            var channel = new ChannelReference { Blendshape = source };
            var aspect = AvatarChannels.Aspect(channel);
            if (!target.Aspects.Contains(aspect.ToString(), StringComparer.Ordinal))
                throw new InvalidOperationException("This source channel doesn't match the selected model control.");
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
            ResultText.Text = "Mapping added. Review it, then activate lip-sync.";
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
                "\nValidation checks the saved mapping; final lip-sync depends on what Audio2Face returns.";
        }
        catch (Exception error) when (error is ContractException or InvalidOperationException or ArgumentException)
        { ResultText.Text = error.Message; }
    }

    private async void Export_Click(object sender, RoutedEventArgs e)
    {
        await autoSave.SaveNowAsync();
        await ActionAsync(async () =>
        {
            var dialog = new SaveFileDialog { Filter = "Avatar JSON|*.json", FileName = "avatar-export.json" };
            if (dialog.ShowDialog(this) != true) return;
            var bytes = await LocalAvatarFiles.ReadBoundedAsync(profiles.FilePath, AvatarProfile.MaximumBytes, lifetime.Token);
            await using var destination = new FileStream(dialog.FileName, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            await destination.WriteAsync(bytes, lifetime.Token);
            ResultText.Text = "Avatar settings exported. Model files and activation permission aren't included.";
        });
    }

    private async void Activate_Click(object sender, RoutedEventArgs e)
    {
        // Activation is for the reviewed choices as saved; save whatever is still waiting first.
        await autoSave.SaveNowAsync();
        await ActionAsync(async () =>
        {
            var currentSettings = await settings.LoadAsync(lifetime.Token);
            if (currentSettings.Settings?.Profile.Id != profileId)
                throw new InvalidOperationException("Your profile changed. Close this window, open it again and inspect again.");
            if ((problem ?? saveError) is { } unsaved)
                throw new InvalidOperationException("These choices aren't saved yet: " + unsaved);
            var selected = Selected();
            var saved = await profiles.LoadAsync(profileId, lifetime.Token);
            if (saved.Profile is null || saved.Revision != revision ||
                !ContractJson.Write(saved.Profile, AvatarProfile.MaximumBytes).SequenceEqual(ContractJson.Write(selected, AvatarProfile.MaximumBytes)))
                throw new InvalidOperationException("These choices are still saving. Try again in a moment.");
            lifetime.Token.ThrowIfCancellationRequested();
            await controller.ActivateAsync(selected, AnalysisPermission.IsChecked == true, CancellationToken.None);
            AnalysisPermission.IsChecked = false;
        });
    }

    private async void Restore_Click(object sender, RoutedEventArgs e) => await ActionAsync(async () =>
    {
        if (operations.IsRunning) throw new InvalidOperationException("Wait for the current setup or voice action to finish before restoring avatar settings.");
        var dialog = new OpenFileDialog { Filter = "Avatar JSON|*.json", CheckFileExists = true };
        if (dialog.ShowDialog(this) != true) return;
        var bytes = await LocalAvatarFiles.ReadBoundedAsync(dialog.FileName, AvatarProfile.MaximumBytes, lifetime.Token);
        var candidate = ContractJson.Read<AvatarProfile>(bytes, AvatarProfile.MaximumBytes);
        if (candidate.ProfileId != profileId) throw new InvalidOperationException("These avatar settings belong to a different profile.");
        var prior = File.Exists(profiles.FilePath)
            ? await LocalAvatarFiles.ReadBoundedAsync(profiles.FilePath, AvatarProfile.MaximumBytes, lifetime.Token) : null;
        if (!ConfirmationDialog.Confirm(this,
            "Restore these avatar settings? Martlet keeps a local backup. Inspect and activate lip-sync again afterward.",
            "Restore avatar settings")) return;
        autoSave.Cancel();
        await controller.StopAsync();
        await profiles.RestoreAsync(candidate, prior is null ? null : Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(prior)), lifetime.Token);
        await ReloadAsync();
    });

    private async void Stop_Click(object sender, RoutedEventArgs e)
    {
        controller.Revoke();
        await ActionAsync(async () => { await controller.StopAsync(); AnalysisPermission.IsChecked = false; }, allowBusy: true);
        RenderShowing();
    }

    private void Window_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key != System.Windows.Input.Key.Escape) return;
        e.Handled = true;
        Stop_Click(sender, e);
    }

    private async Task ActionAsync(Func<Task> action, bool allowBusy = false)
    {
        if (busy && !allowBusy) { ResultText.Text = "Another character action is still finishing."; return; }
        busy = true;
        try { await action(); }
        catch (OperationCanceledException) { ResultText.Text = "Character action canceled."; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ContractException or
            InvalidOperationException or ArgumentException or JsonException or TimeoutException or Win32Exception)
        { ResultText.Text = error.Message; }
        finally
        {
            busy = false;
            if (!lifetime.IsCancellationRequested) RenderSaveState();
        }
    }
}
