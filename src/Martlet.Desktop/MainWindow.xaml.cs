using System.Windows;
using System.ComponentModel;
using System.IO;
using System.Diagnostics;
using System.Net.Http;
using System.Windows.Controls;
using System.Windows.Threading;
using Microsoft.Win32;
using Martlet.Core.Settings;
using Martlet.Core.Installation;
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
    private readonly VoiceIdentity voiceIdentity;
    private readonly ConfigurationRecoveryController? recovery;
    private readonly AudioSetupService audioSetup;
    private readonly LiveConversationController? conversation;
    private readonly AvatarController avatar = new();
    private readonly SpeechCaptions captions;
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
        InitializeUpdates();
        this.support = support;
        audioSetup = new(setupOperations, new WindowsAudioDeviceCatalog(), new WasapiCaptureDeviceFactory(), new WasapiDeviceFactory());
        audioSessionEvents.LockedChanged += audioSetup.SetSessionLocked;
        var vault = new WindowsCredentialStore();
        setupService = store is null ? null : new SetupService(store, vault);
        companionService = store is null ? null : new CompanionSettingsService(store);
        memory = store is null ? null : new DesktopMemoryService(store);
        voiceIdentity = new(store?.DataDirectory);
        voiceIdentity.Load();
        recovery = store is null ? null : new(store, setupOperations, () => !support.HasResources);
        captions = new(avatar, store?.DataDirectory);
        if (setupService is not null)
        {
            conversation = new(setupOperations, setupService, vault, new WasapiCaptureDeviceFactory(), new WasapiDeviceFactory(),
                memory: memory, generatedSpeech: avatar.Observer, revokeAvatar: avatar.Revoke, voiceIdentity: voiceIdentity,
                dataDirectory: store!.DataDirectory, spokenText: captions.Feed);
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
                AutomaticUpdateCheck.IsEnabled = CheckForUpdatesButton.IsEnabled = PrimaryStageButton.IsEnabled =
                AutomaticHostUpdate.IsEnabled = UpdateHostsButton.IsEnabled = false;
        }
        InitializeShell();
        InitializeCluster();
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
        StartAmbientMotion();
        _ = ReadMachineAsync();
        await RefreshAsync();
        await ShowSavedCharacterAsync(onlyIfAutoShow: true);
        StartCluster();
        if (!closing) await StartUpdatesAsync();
    }
    private async void Refresh_Click(object sender, RoutedEventArgs e) => await RefreshAsync();

    private async Task RefreshAsync()
    {
        if (model is null)
        {
            StatusText.Text = startupError;
            ActionText.Text = startupError ?? "";
            RefreshButton.IsEnabled = false;
            return;
        }
        if (!saving && !closing)
        {
            await model.RefreshAsync();
            support.ObserveReport(model.Report, record: true);
        }
        if (!closing) await RefreshHomeAsync();
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
        ConversationButton.IsEnabled = PrimaryStageButton.IsEnabled = SetupButton.IsEnabled;
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
        openConversation = new LiveConversationWindow(setupService!, setupOperations, conversation, audioSessionEvents, audioSetup, voiceIdentity: voiceIdentity)
            { Owner = this, Troubleshooting = OpenTroubleshooting, Support = support, ConfigurationRecovery = OpenRecovery,
                Avatar = OpenAvatar };
        try { openConversation.ShowDialog(); }
        finally { openConversation = null; }
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
    private AdvisorAnswers? advisorAnswers;
    private void Advisor_Click(object sender, RoutedEventArgs e)
    {
        if (closing) return;
        var advisor = new SetupAdvisorWindow(advisorAnswers, DetectThisPcGpu(), PairedHostsForAdvisor()) { Owner = this };
        advisor.ShowDialog();
        advisorAnswers = advisor.Answers;
        switch (advisor.RequestedStep)
        {
            case AdvisorNextStep.Prerequisites:
                var missing = SetupAdvisor.Recommend(advisor.Answers).ThisPcInstalls.Select(Prerequisites.For).Where(Prerequisites.IsMissing).ToArray();
                ActionText.Text = missing.Length > 0 ? Prerequisites.Launch(missing) : "Everything this plan needs on this PC is already installed.";
                break;
            case AdvisorNextStep.Setup: Setup_Click(sender, e); break;
            case AdvisorNextStep.AudioSetup: AudioSetup_Click(sender, e); break;
            case AdvisorNextStep.Hosts: Hosts_Click(sender, e); break;
            case AdvisorNextStep.VoiceLibrary: VoiceLibrary_Click(sender, e); break;
            case AdvisorNextStep.Character: Avatar_Click(sender, e); break;
        }
    }
    private void Avatar_Click(object sender, RoutedEventArgs e) => OpenAvatar(this);

    /// <summary>This PC's graphics card, read locally from Windows (no probing), as an advisor answer.</summary>
    private (AdvisorGpu Gpu, string Text)? DetectThisPcGpu()
    {
        var info = machine;
        if (ReferenceEquals(info, MachineInfo.Unknown))
        {
            try { info = MachineInfo.Read(); }
            catch (Exception error) when (error is InvalidOperationException or IOException or UnauthorizedAccessException or
                System.ComponentModel.Win32Exception) { return null; }
        }
        if (info.BestGpu is not { } gpu) return (AdvisorGpu.None, "no dedicated graphics card found");
        var answer = SetupAdvisor.Classify(gpu.Name, gpu.MemoryGb);
        return (answer, answer == AdvisorGpu.None ? $"{gpu.Name} (integrated graphics, not a dedicated GPU)" : gpu.Describe());
    }

    /// <summary>Paired hosts (other than this PC) with the hardware they last reported.</summary>
    private IReadOnlyList<AdvisorComputer> PairedHostsForAdvisor()
    {
        if (store is null) return [];
        return new HostHardwareStore(store.DataDirectory).Load()
            .Where(host => !Uri.TryCreate(host.Origin, UriKind.Absolute, out var origin) || origin.Host != machine.LanAddress)
            .Select(host => new AdvisorComputer(host.AdvisorGpu, host.HostId, HostsWindow.DescribeHardware(host)))
            .ToArray();
    }
    private void Hosts_Click(object sender, RoutedEventArgs e) => OpenHosts(null, 0);
    /// <summary>Opens the installed prerequisites tool; it changes nothing until the user picks an item.</summary>
    private void Prerequisites_Click(object sender, RoutedEventArgs e) => ActionText.Text = Prerequisites.Launch([]);
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
            if (!closing)
            {
                RenderHome();
                if (DevicesPage.IsVisible) RenderMap();
            }
        }
    }
    private void UpdateCharacterButton()
    {
        CharacterButton.Content = avatar.IsShowing ? "Hide _character" : "Show _character";
        ResetCharacterButton.Visibility = avatar.IsShowing ? Visibility.Visible : Visibility.Collapsed;
    }
    private async void ResetCharacter_Click(object sender, RoutedEventArgs e) => await ResetCharacterPositionAsync();
    private async Task ResetCharacterPositionAsync()
    {
        try
        {
            await avatar.ResetPositionAsync(lifetime.Token);
            ActionText.Text = "Character moved back to the lower-right of your main screen.";
        }
        catch (Exception error) when (error is System.IO.IOException or InvalidOperationException or TimeoutException or
            OperationCanceledException or ObjectDisposedException)
        {
            if (!closing) ActionText.Text = $"Character position could not be reset: {error.Message}";
        }
        finally { UpdateCharacterButton(); }
    }
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
        new AvatarWindow(avatar, new AvatarProfileStore(store.DataDirectory), setupService, setupOperations, captions)
            { Owner = owner }.ShowDialog();
        UpdateCharacterButton();
        _ = RefreshHomeAsync();
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
                ActionText.Text = $"Update cleanup could not finish. Inspect Martlet's updates folder before exiting: {error}";
                return;
            }
        }
        closing = true;
        ReleaseShell();
        ageTimer.Stop();
        fixtureTimer.Stop();
        updateTimer.Stop();
        clusterTimer.Stop();
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
        captions.Dispose();
        if (!await StopAvatarSafelyAsync())
        {
            closing = false;
            IsEnabled = true;
            return;
        }
        await avatar.DisposeAsync();
        memory?.Dispose();
        LaunchPendingInstall();
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
