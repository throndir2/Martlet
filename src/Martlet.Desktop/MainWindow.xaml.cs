using System.Windows;
using System.ComponentModel;
using System.IO;
using System.Diagnostics;
using System.Net.Http;
using System.Windows.Controls;
using System.Windows.Threading;
using Microsoft.Win32;
using Martlet.Core.Settings;
using Martlet.Diagnostics;
using Martlet.Audio;
using Martlet.Audio.Windows;
using Martlet.Sessions;
using Martlet.Credentials.Windows;
using Martlet.Avatar.Hosting;
using Martlet.Core.Voices;

namespace Martlet.Desktop;

public partial class MainWindow : ThemedWindow
{
    private readonly SettingsStore? store;
    private readonly SupportController support;
    private TroubleshootingWindow? troubleshooting;
    private readonly SetupOperationRunner setupOperations = new();
    private readonly ISetupService? setupService;
    private readonly ICompanionSettingsService? companionService;
    private readonly DesktopMemoryService? memory;
    private readonly ConfigurationRecoveryController? recovery;
    private readonly AudioSetupService audioSetup;
    private readonly LiveConversationController? conversation;
    private readonly AvatarController avatar = new();
    private readonly WindowsAudioSessionEvents audioSessionEvents = new();
    private readonly string? startupError;
    private readonly DiagnosticStatusModel? model;
    private readonly DispatcherTimer ageTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly CancellationTokenSource lifetime = new();
    private readonly FixtureSession fixture = new(new PcmPlaybackSink(new WasapiDeviceFactory()),
        pacing: TimeSpan.FromMilliseconds(200));
    private readonly DispatcherTimer fixtureTimer = new() { Interval = TimeSpan.FromMilliseconds(100) };
    private FixtureSessionSnapshot? lastFixture;
    private bool runningFixture;
    private bool saving;
    private bool closing;
    private bool mayClose;
    private SetupOperation? fixtureOperation;
    private SetupOperation? voiceOperation;
    private readonly TaskCompletionSource fixtureQuarantine = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private CancellationTokenSource? updateCheckCancellation;
    private CancellationTokenSource? updateDownloadCancellation;
    private TaskCompletionSource? updateDrain;
    private string? interruptedUpdateCleanup;
    private GitHubUpdate? availableUpdate;
    private bool updateChecksEnabled;
    private bool changingUpdateChoice;
    private bool updateBusy;

    public MainWindow(SettingsStore? store, string? startupError) : this(store, startupError, new(store?.DataDirectory)) { }

    internal MainWindow(SettingsStore? store, string? startupError, SupportController support)
    {
        InitializeComponent();
        this.store = store;
        ThemeChoice.SelectedIndex = Application.Current is App { SelectedTheme: PinkTheme.Dark } ? 1 : 0;
        AppearanceStatus.Text = (Application.Current as App)?.AppearanceNotice
            ?? "Pink light / rose dark. Your choice is saved locally; Windows high contrast takes priority.";
        UpdateStatusText.Text = "Automatic checks OFF. No update network request has run.";
        if (store is not null)
        {
            try { updateChecksEnabled = UpdateCheckPreferences.Load(store.DataDirectory); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                UpdateStatusText.Text = "Could not read update-checks.txt. Automatic checks remain OFF. Check access or save a new preference.";
            }
            AutomaticUpdateCheck.IsChecked = updateChecksEnabled;
        }
        this.support = support;
        audioSetup = new(setupOperations, new WindowsAudioDeviceCatalog(), new WasapiCaptureDeviceFactory(), new WasapiDeviceFactory());
        audioSessionEvents.LockedChanged += audioSetup.SetSessionLocked;
        var vault = new WindowsCredentialStore();
        setupService = store is null ? null : new SetupService(store, vault);
        companionService = store is null ? null : new CompanionSettingsService(store);
        memory = store is null ? null : new DesktopMemoryService(store);
        recovery = store is null ? null : new(store, setupOperations, () => !support.HasResources);
        if (setupService is not null)
        {
            conversation = new(setupOperations, setupService, vault, new WasapiCaptureDeviceFactory(), new WasapiDeviceFactory(),
                memory: memory, generatedSpeech: avatar.Observer, revokeAvatar: avatar.Revoke);
            audioSessionEvents.LockedChanged += conversation.SetSessionLocked;
        }
        audioSessionEvents.LockedChanged += AvatarSessionLocked;
        this.startupError = startupError;
        ScenarioChoice.ItemsSource = FixtureSession.Scenarios;
        ScenarioChoice.SelectedIndex = 0;
        FixtureText.Text = "FIXTURE - NOT AI. Choose a scenario; no fixture or audio has run.";
        fixtureTimer.Tick += (_, _) => ObserveFixture();
        fixtureTimer.Tick += (_, _) => UpdateCharacterButton();
        fixtureTimer.Start();
        DataPathText.Text = "Local settings only. Reports never include the data directory, credentials or settings contents.";
        StatusText.Text = "Foundation: loading local status. No audio or network services are active.";
        AudioStatusText.Text = AudioSetupDiagnostics.Describe(null);
        if (store is not null)
        {
            model = new(new FoundationStatusService(store).Executor);
            model.PropertyChanged += (_, _) => Render();
            ageTimer.Tick += (_, _) => model.UpdateAge();
            ageTimer.Start();
        }
        else
        {
            PipelineText.Text = "Mic / VAD / STT / Policy / LLM / TTS / Playback: unavailable; not run. Correct the launch data directory first.";
            DemoButton.IsEnabled = ToneButton.IsEnabled = ScenarioChoice.IsEnabled =
                SetupButton.IsEnabled = AudioSetupButton.IsEnabled = CompanionButton.IsEnabled =
                MemoryButton.IsEnabled = ConversationButton.IsEnabled = VoiceLibraryButton.IsEnabled =
                AutomaticUpdateCheck.IsEnabled = CheckForUpdatesButton.IsEnabled = false;
        }
    }

    private async void Theme_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded || Application.Current is not App app) return;
        var theme = ThemeChoice.SelectedIndex == 1 ? PinkTheme.Dark : PinkTheme.Light;
        app.ApplyTheme(theme);
        if (store is null)
        {
            AppearanceStatus.Text = "Theme applied for this session only. Correct the launch data directory to save your preference.";
            return;
        }
        try
        {
            Appearance.Save(store.DataDirectory, theme);
            AppearanceStatus.Text = $"{(theme == PinkTheme.Dark ? "Rose dark" : "Pink light")} saved locally. Windows high contrast takes priority.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppearanceStatus.Text = "Theme applied for this session, but appearance.txt could not be saved. Check access to your data directory and choose again. Profile settings were not changed.";
        }
        try { await avatar.UpdateThemeAsync(lifetime.Token); }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or OperationCanceledException or TimeoutException)
        {
            if (!closing)
                AppearanceStatus.Text += " The avatar overlay could not update its palette. Stop it and inspect again to use this theme.";
        }
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        var checkAtLaunch = updateChecksEnabled;
        await RefreshAsync();
        await ShowSavedCharacterAsync(onlyIfAutoShow: true);
        if (checkAtLaunch && updateChecksEnabled && !closing)
            await CheckForUpdatesAsync();
    }
    private async void Refresh_Click(object sender, RoutedEventArgs e) => await RefreshAsync();

    private void UpdateCheckSetting_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded || changingUpdateChoice || store is null) return;
        var enabled = AutomaticUpdateCheck.IsChecked == true;
        try
        {
            UpdateCheckPreferences.Save(store.DataDirectory, enabled);
            updateChecksEnabled = enabled;
            if (!enabled) updateCheckCancellation?.Cancel();
            UpdateStatusText.Text = enabled
                ? "Automatic release checks enabled for future launches. No download or install is authorized."
                : "Automatic checks OFF. No update network request will run on launch.";
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            changingUpdateChoice = true;
            AutomaticUpdateCheck.IsChecked = updateChecksEnabled;
            changingUpdateChoice = false;
            UpdateStatusText.Text = "Could not save update-checks.txt. The previous check preference remains in effect; check data-directory access.";
        }
    }

    private async void CheckForUpdates_Click(object sender, RoutedEventArgs e) => await CheckForUpdatesAsync();

    private async Task CheckForUpdatesAsync()
    {
        if (closing || updateBusy) return;
        updateBusy = true;
        updateDrain = new(TaskCreationOptions.RunContinuationsAsynchronously);
        availableUpdate = null;
        CheckForUpdatesButton.IsEnabled = false;
        DownloadUpdateButton.Visibility = Visibility.Collapsed;
        ReviewUpdateButton.Visibility = Visibility.Collapsed;
        UpdateStatusText.Text = "Checking public GitHub Releases; no download or installation has started.";
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        updateCheckCancellation = cancellation;
        try
        {
            using var http = GitHubReleaseClient.CreateHttpClient();
            var result = await new GitHubReleaseClient(http).CheckAsync(typeof(App).Assembly.GetName().Version!, cancellation.Token);
            if (closing || cancellation.IsCancellationRequested) return;
            availableUpdate = result;
            UpdateStatusText.Text = result is null
                ? "No newer Martlet release is available."
                : $"Version {result.Version} is available ({result.Bytes / (1024d * 1024d):F1} MiB). Review {result.ReleasePage} before deciding to download. The installer is not code-signed; in-app installation is not yet supported.";
            DownloadUpdateButton.Visibility = result is null ? Visibility.Collapsed : Visibility.Visible;
            ReviewUpdateButton.Visibility = result is null ? Visibility.Collapsed : Visibility.Visible;
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (OperationCanceledException)
        {
            if (!closing) UpdateStatusText.Text = "GitHub Release check timed out. Try again when the network is available.";
        }
        catch (Exception error) when (error is HttpRequestException or InvalidDataException or IOException)
        {
            if (!closing) UpdateStatusText.Text = $"GitHub Release check failed: {UpdateError(error)}";
        }
        finally
        {
            updateCheckCancellation = null;
            updateBusy = false;
            if (!closing) CheckForUpdatesButton.IsEnabled = true;
            updateDrain.TrySetResult();
            updateDrain = null;
        }
    }

    private async void DownloadUpdate_Click(object sender, RoutedEventArgs e)
    {
        if (closing || updateBusy || availableUpdate is not { } update) return;
        if (!ConfirmationDialog.Confirm(this,
            $"Download {update.AssetName} ({update.Bytes / (1024d * 1024d):F1} MiB) from the public Martlet GitHub Release?\n\n{update.ReleasePage}\n\nThe SHA-256 digest will be checked, but a digest from the same source is not a publisher signature. The installer will NOT be launched or installed by Martlet.",
            "Confirm app update download"))
            return;
        var picker = new SaveFileDialog
        {
            Title = "Choose where to save the Martlet update",
            FileName = update.AssetName,
            DefaultExt = ".exe",
            AddExtension = false,
            Filter = "Windows installer (*.exe)|*.exe",
            OverwritePrompt = false
        };
        if (picker.ShowDialog(this) != true) return;
        updateBusy = true;
        updateDrain = new(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        updateDownloadCancellation = cancellation;
        DownloadUpdateButton.IsEnabled = CheckForUpdatesButton.IsEnabled = false;
        UpdateStatusText.Text = "Downloading the explicitly approved installer; checking size and SHA-256 before retaining it.";
        try
        {
            using var http = GitHubReleaseClient.CreateHttpClient();
            await new GitHubReleaseClient(http).DownloadAsync(update, picker.FileName, cancellation.Token);
            if (!closing)
                UpdateStatusText.Text = $"Downloaded to {picker.FileName}. SHA-256 matches GitHub metadata, not an independent publisher signature. Martlet has not installed the update; check the release notes and Windows publisher warnings. In-app installation and rollback remain unavailable.";
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (Exception error) when (error is HttpRequestException or InvalidDataException or IOException or
            UnauthorizedAccessException or ArgumentException)
        {
            if (error is UpdateCleanupException) interruptedUpdateCleanup = error.Message;
            if (!closing) UpdateStatusText.Text = $"Update download failed: {UpdateError(error)}";
        }
        finally
        {
            updateDownloadCancellation = null;
            updateBusy = false;
            if (!closing) CheckForUpdatesButton.IsEnabled = DownloadUpdateButton.IsEnabled = true;
            updateDrain.TrySetResult();
            updateDrain = null;
        }
    }

    private void ReviewUpdate_Click(object sender, RoutedEventArgs e)
    {
        if (availableUpdate is not { } update || closing) return;
        try { Process.Start(new ProcessStartInfo(update.ReleasePage.AbsoluteUri) { UseShellExecute = true }); }
        catch (Exception error) when (error is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            UpdateStatusText.Text = $"Could not open the release page. Copy this URL into your browser instead: {update.ReleasePage}";
        }
    }

    private static string UpdateError(Exception error) => error switch
    {
        InvalidDataException or ArgumentException => error.Message,
        HttpRequestException => "GitHub could not be reached or refused the request. No installer was retained.",
        UpdateCleanupException => error.Message,
        UnauthorizedAccessException or IOException => "Cannot read or save the update at the selected location. Check access and free space; check for an incomplete .part file before retrying.",
        _ => "The update could not be checked or downloaded."
    };

    private async Task RefreshAsync()
    {
        if (model is null)
        {
            StatusText.Text = startupError;
            RefreshButton.IsEnabled = false;
            return;
        }
        if (!saving && !closing)
        {
            await model.RefreshAsync();
            support.ObserveReport(model.Report, record: true);
        }
    }

    private void Render()
    {
        if (closing || model is null)
            return;
        StatusText.Text = model.Text;
        support.ObserveReport(model.Report);
        if (model.FixtureReport is { } observedFixture) support.ObserveReport(observedFixture, fixture: true);
        PipelineText.Text = string.Join(Environment.NewLine, model.Pipeline.Select(node => node.Description));
        ActivityText.Text = runningFixture ? "Offline fixture active. Stop fixture is available. No real provider or microphone is active."
            : setupOperations.IsRunning ? "An app-shared setup/audio/conversation worker owns resources. Check its action window; new effects wait for actual cleanup."
            : model.Activity;
        CreateButton.IsEnabled = !saving && !runningFixture && !setupOperations.IsRunning && model.CanCreateProfile;
        SetupButton.IsEnabled = !saving && !runningFixture && !model.IsRunning;
        AudioSetupButton.IsEnabled = SetupButton.IsEnabled;
        CompanionButton.IsEnabled = SetupButton.IsEnabled;
        MemoryButton.IsEnabled = SetupButton.IsEnabled;
        ConversationButton.IsEnabled = SetupButton.IsEnabled;
        VoiceLibraryButton.IsEnabled = SetupButton.IsEnabled;
        RefreshButton.IsEnabled = !saving && !runningFixture && model.CanRefresh;
        StopButton.IsEnabled = !saving && model.IsRunning;
        DemoButton.IsEnabled = ToneButton.IsEnabled = !saving && !runningFixture && !model.IsRunning && !setupOperations.IsRunning;
        ScenarioChoice.IsEnabled = !runningFixture;
        FixtureStopButton.IsEnabled = runningFixture;
        FixtureText.Text = model.FixtureText;
    }

    private void ObserveFixture()
    {
        if (closing || fixture.Snapshot is not { } current || current == lastFixture)
            return;
        lastFixture = current;
        if (model is not null)
            model.ObserveFixture(current);
        else
            FixtureText.Text = ReportFormatter.Human(FixtureDiagnostics.Report(current));
    }

    private async void Demo_Click(object sender, RoutedEventArgs e) =>
        await RunFixtureAsync((string)ScenarioChoice.SelectedItem, tone: false);

    private async void Tone_Click(object sender, RoutedEventArgs e)
    {
        if (ConfirmationDialog.Confirm(this,
            "Play a 200 ms synthetic tone, NOT speech, after a completed offline fixture? Check the current Windows output, volume and audience first. The default is fixed at start, with no fallback. This permission applies only to this action and is not saved.",
            "Explicit output permission"))
            await RunFixtureAsync("complete", tone: true);
    }

    private async Task RunFixtureAsync(string scenario, bool tone)
    {
        if (closing || saving || runningFixture || model?.IsRunning == true || setupOperations.IsRunning)
            return;
        var reported = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var session = fixture;
        var quarantine = fixtureQuarantine.Task;
        fixtureOperation = setupOperations.TryStart(async token =>
        {
            try
            {
                var result = await session.RunAsync(scenario, tone ? new(OutputPolicy.DefaultAtStart) : null, token).ConfigureAwait(false);
                reported.TrySetResult();
                // Fixture's public report cannot establish a late release after a frozen failed terminal.
                // Keep the shared slot quarantined instead of allowing a second native audio owner.
                if (result.Playback is { DeviceReleased: false })
                    await quarantine.ConfigureAwait(false);
                return new SetupWorkResult(SetupWorkOutcome.Completed);
            }
            finally { reported.TrySetResult(); }
        });
        if (fixtureOperation is null) return;
        runningFixture = true;
        Render();
        try
        {
            await Task.WhenAny(reported.Task, fixtureOperation.Completion);
            if (fixture.Snapshot?.Playback is not { DeviceReleased: false })
                await fixtureOperation.Completion;
            ObserveFixture();
            if (model?.FixtureReport is { } report) support.ObserveReport(report, fixture: true, record: true);
        }
        finally
        {
            runningFixture = false;
            if (!closing)
                Render();
        }
    }

    private void FixtureStop_Click(object sender, RoutedEventArgs e)
    {
        var owned = fixtureOperation;
        owned?.RequestCancellation();
        ObserveFixture();
    }

    private async void Create_Click(object sender, RoutedEventArgs e)
    {
        if (store is null || model is null)
        {
            ActionText.Text = startupError;
            return;
        }
        if (saving || closing || setupOperations.IsRunning || !model.CanCreateProfile)
            return;
        saving = true;
        Render();
        try
        {
            var backend = store;
            var initial = AppSettings.CreateUnconfigured();
            var worker = setupOperations.TryStart(async token => new(SetupWorkOutcome.Completed,
                Saved: new(await backend.SaveAsync(initial, expectedRevision: null, token).ConfigureAwait(false), initial)));
            if (worker is null) return;
            if (await Task.WhenAny(worker.Completion, Task.Delay(TimeSpan.FromSeconds(5), lifetime.Token)) != worker.Completion)
            {
                worker.RequestCancellation();
                if (!closing) ActionText.Text = "Save observation ended. Actual work may still own the app slot; refresh after release. No rollback is claimed.";
                return;
            }
            var completed = await worker.Completion;
            var result = completed.Saved?.Save;
            if (!closing)
                ActionText.Text = result is null ? "Profile save failed or was canceled. Refresh local status."
                    : result.Saved
                    ? "Unconfigured profile saved. Nothing was connected or enabled."
                    : $"{result.Error!.Summary} Next action ({result.Error.ActionId}): {DiagnosticCatalog.Remedy(result.Error.ActionId).Guidance}";
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        finally { saving = false; }
        if (!closing)
            await RefreshAsync();
    }

    private async void Setup_Click(object sender, RoutedEventArgs e)
    {
        if (store is null || closing || saving || runningFixture || model?.IsRunning == true) return;
        var characterWasShowing = avatar.IsShowing;
        if (!await StopAvatarSafelyAsync()) return;
        new SetupWindow(setupService!, setupOperations) { Owner = this, Troubleshooting = OpenTroubleshooting, ConfigurationRecovery = OpenRecovery }.ShowDialog();
        await RefreshAsync();
        if (characterWasShowing) await ShowSavedCharacterAsync(onlyIfAutoShow: false);
    }

    private async void AudioSetup_Click(object sender, RoutedEventArgs e)
    {
        if (store is null || closing || saving || runningFixture || model?.IsRunning == true) return;
        new AudioSetupWindow(setupService!, setupOperations, audioSetup,
            observe: text => { if (!closing) AudioStatusText.Text = text; }, sessionEvents: audioSessionEvents)
            { Owner = this, Troubleshooting = OpenTroubleshooting }.ShowDialog();
        await RefreshAsync();
    }

    private async void Companion_Click(object sender, RoutedEventArgs e)
    {
        if (companionService is null || closing || saving || runningFixture || model?.IsRunning == true) return;
        new CompanionWindow(companionService, setupOperations) { Owner = this }.ShowDialog();
        await RefreshAsync();
    }

    private async void Memory_Click(object sender, RoutedEventArgs e)
    {
        if (memory is null || closing || saving || runningFixture || model?.IsRunning == true) return;
        new MemoryWindow(memory, setupOperations) { Owner = this }.ShowDialog();
        await RefreshAsync();
    }

    private async void Conversation_Click(object sender, RoutedEventArgs e)
    {
        if (conversation is null || closing || saving || runningFixture || model?.IsRunning == true) return;
        new LiveConversationWindow(setupService!, setupOperations, conversation, audioSessionEvents, audioSetup)
            { Owner = this, Troubleshooting = OpenTroubleshooting, Support = support, ConfigurationRecovery = OpenRecovery,
                Avatar = OpenAvatar }.ShowDialog();
        await RefreshAsync();
    }

    private void VoiceLibrary_Click(object sender, RoutedEventArgs e)
    {
        if (store is null || closing || saving || runningFixture || model?.IsRunning == true) return;
        new VoiceLibraryWindow(new VoiceLibrary(System.IO.Path.Combine(store.DataDirectory, "voice-library")), setupOperations)
            { Owner = this, OperationStarted = ObserveVoiceOperation }.ShowDialog();
    }

    internal void ObserveVoiceOperation(SetupOperation operation) => voiceOperation = operation;

    private void Troubleshooting_Click(object sender, RoutedEventArgs e) => OpenTroubleshooting(this);
    private void Avatar_Click(object sender, RoutedEventArgs e) => OpenAvatar(this);
    private void Hosts_Click(object sender, RoutedEventArgs e)
    {
        if (store is null || setupService is null || closing) return;
        new HostsWindow(new AvatarProfileStore(store.DataDirectory), setupService) { Owner = this }.ShowDialog();
    }
    private bool togglingCharacter;
    private async void Character_Click(object sender, RoutedEventArgs e)
    {
        if (togglingCharacter) return;
        togglingCharacter = true;
        try
        {
            if (avatar.IsShowing)
            {
                if (await StopAvatarSafelyAsync()) ActionText.Text = "Character hidden. Voice is unaffected.";
            }
            else await ShowSavedCharacterAsync(onlyIfAutoShow: false);
        }
        finally
        {
            togglingCharacter = false;
            UpdateCharacterButton();
        }
    }
    private void UpdateCharacterButton() =>
        CharacterButton.Content = avatar.IsShowing ? "Hide _character" : "Show _character";
    /// <summary>Shows the saved character, or the bundled default when none is configured.</summary>
    private async Task ShowSavedCharacterAsync(bool onlyIfAutoShow)
    {
        if (store is null || setupService is null || closing || avatar.IsShowing) return;
        try
        {
            var loaded = await setupService.LoadAsync(lifetime.Token);
            AvatarProfile? saved = null;
            if (loaded.Settings is { } settings)
                saved = (await new AvatarProfileStore(store.DataDirectory).LoadAsync(settings.Profile.Id, lifetime.Token)).Profile;
            if (onlyIfAutoShow && saved?.AutoShow != true) return;
            // Without a completed Setup profile the bundled character still shows; its choices are simply not saved.
            var profile = saved ?? AvatarProfile.BuiltIn(loaded.Settings?.Profile.Id ?? Guid.NewGuid());
            await avatar.ShowAsync(profile with { ResourceRevision = null }, lifetime.Token);
            ActionText.Text = avatar.Status;
        }
        catch (Exception error) when (error is System.IO.IOException or InvalidOperationException or TimeoutException or
            UnauthorizedAccessException or System.ComponentModel.Win32Exception or Martlet.Core.Contracts.ContractException or
            OperationCanceledException)
        {
            if (!closing) ActionText.Text = $"Character could not start: {error.Message} Voice is unaffected.";
        }
        finally { UpdateCharacterButton(); }
    }
    private void OpenAvatar(Window owner)
    {
        if (store is null || setupService is null || closing) return;
        new AvatarWindow(avatar, new AvatarProfileStore(store.DataDirectory), setupService, setupOperations)
            { Owner = owner }.ShowDialog();
        UpdateCharacterButton();
    }
    private void AvatarSessionLocked(bool locked)
    {
        // Revocation ends privileged Audio2Face analysis; the character itself and local lip-sync stay available.
        if (locked) avatar.Revoke();
    }
    private async Task<bool> StopAvatarSafelyAsync()
    {
        try { await avatar.StopAsync(); return true; }
        catch (Exception error) when (error is System.IO.IOException or InvalidOperationException or TimeoutException or
            System.ComponentModel.Win32Exception or UnauthorizedAccessException)
        {
            ActionText.Text = "Avatar cleanup is incomplete. Voice is unaffected; retry STOP avatar before changing its resources.";
            return false;
        }
    }
    private async void Recovery_Click(object sender, RoutedEventArgs e)
    {
        OpenRecovery(this);
        await RefreshAsync();
    }
    private void OpenRecovery(Window owner)
    {
        if (recovery is null || closing || saving) return;
        avatar.Revoke();
        new ConfigurationRecoveryWindow(recovery) { Owner = owner }.ShowDialog();
    }
    private void OpenTroubleshooting(Window owner)
    {
        if (troubleshooting is { } existing && existing.Owner == owner) { existing.Activate(); return; }
        var previous = troubleshooting;
        var next = new TroubleshootingWindow(support, RefreshAsync) { Owner = owner };
        next.Closed += (_, _) => { if (ReferenceEquals(troubleshooting, next)) troubleshooting = null; };
        // A pre-existing HWND is disabled by ShowDialog. Create a presentation in the active
        // modal context, not a replacement resource owner or a globally re-enabled window.
        next.Show();
        troubleshooting = next;
        previous?.CloseForPresentationTransfer();
    }

    private void Stop_Click(object sender, RoutedEventArgs e) => model?.Stop();

    private async void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (mayClose)
            return;
        e.Cancel = true;
        if (closing)
            return;
        if (voiceOperation is { Completion.IsCompleted: false } pendingVoice)
        {
            pendingVoice.RequestCancellation();
            ActionText.Text = "Exit is waiting for Voice Library IO and owned staging cleanup. Keep Martlet open, then Exit again after the local operation finishes.";
            return;
        }
        if (recovery?.HasResources == true)
        {
            recovery.StopObserving();
            ActionText.Text = "Exit is waiting for configuration recovery IO/callbacks or owned staging cleanup. Keep Martlet open; inspect Backup / restore after release, retry cleanup if needed, then Exit again.";
            return;
        }
        if (support.HasResources)
        {
            support.CancelAndClose();
            ActionText.Text = "Exit is waiting for owned support IO/cancellation/cleanup. Keep Martlet open; use Troubleshooting to retry cleanup, then Exit again. No rollback or completed cleanup is assumed.";
            return;
        }
        if (updateDrain is { Task.IsCompleted: false } pendingUpdate)
        {
            IsEnabled = false;
            updateCheckCancellation?.Cancel();
            updateDownloadCancellation?.Cancel();
            try { await pendingUpdate.Task.WaitAsync(TimeSpan.FromSeconds(5)); }
            catch (TimeoutException)
            {
                IsEnabled = true;
                ActionText.Text = "Exit is waiting for the update request and exact partial-file cleanup. Keep Martlet open and retry Exit after the operation releases.";
                return;
            }
            if (interruptedUpdateCleanup is { } error)
            {
                IsEnabled = true;
                ActionText.Text = $"Update cleanup could not finish. Inspect the selected download directory before exiting: {error}";
                return;
            }
        }
        closing = true;
        ageTimer.Stop();
        fixtureTimer.Stop();
        audioSessionEvents.LockedChanged -= audioSetup.SetSessionLocked;
        audioSessionEvents.LockedChanged -= AvatarSessionLocked;
        if (conversation is not null) audioSessionEvents.LockedChanged -= conversation.SetSessionLocked;
        audioSessionEvents.Dispose();
        lifetime.Cancel();
        fixtureOperation?.RequestCancellation();
        setupOperations.RequestCancellation();
        IsEnabled = false;
        if (model is not null)
            await model.CloseAsync();
        await Task.Run(async () => await fixture.DisposeAsync());
        if (conversation is not null) await Task.Run(async () => await conversation.DisposeAsync());
        if (!await StopAvatarSafelyAsync())
        {
            closing = false;
            IsEnabled = true;
            return;
        }
        await avatar.DisposeAsync();
        memory?.Dispose();
        // WPF OnMainWindowClose exits the process, including any non-cooperative in-process callback.
        // Even absent or synchronous cleanup must leave WPF's original Closing event before closing again.
        await Dispatcher.InvokeAsync(() =>
        {
            mayClose = true;
            Close();
        });
    }

    private void Exit_Click(object sender, RoutedEventArgs e) => Close();
}
