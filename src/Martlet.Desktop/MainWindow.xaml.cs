using System.Windows;
using System.ComponentModel;
using System.IO;
using System.Diagnostics;
using System.Net.Http;
using System.Windows.Controls;
using System.Windows.Threading;
using Microsoft.Win32;
using Martlet.Core.Settings;
using Martlet.Core.Lorebooks;
using Martlet.Core.Installation;
using Martlet.Diagnostics;
using Martlet.Audio;
using Martlet.Audio.Windows;
using Martlet.Credentials.Windows;
using Martlet.Avatar.Hosting;

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
    private readonly LorebookStore? lorebooks;
    private readonly SmartHome smartHome;
    private readonly McpToolService mcpTools;
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
    private readonly DispatcherTimer characterTimer = new() { Interval = TimeSpan.FromMilliseconds(100) };
    private bool saving;
    private bool closing;
    private bool mayClose;
    private CancellationTokenSource? updateCheckCancellation;
    private CancellationTokenSource? updateDownloadCancellation;
    private TaskCompletionSource? updateDrain;
    private string? interruptedUpdateCleanup;
    private GitHubUpdate? availableUpdate;
    private bool updateChecksEnabled = true;
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
        lorebooks = store is null ? null : new LorebookStore(store.DataDirectory);
        smartHome = new(store?.DataDirectory, vault);
        mcpTools = new(store?.DataDirectory);
        mcpTools.Changed += ToolsChanged;
        smartHome.Attach(mcpTools);
        voiceIdentity = new(store?.DataDirectory);
        voiceIdentity.Load();
        recovery = store is null ? null : new(store, setupOperations, () => !support.HasResources);
        captions = new(avatar, store?.DataDirectory);
        if (setupService is not null)
        {
            conversation = new(setupOperations, setupService, vault, new WasapiCaptureDeviceFactory(), new WasapiDeviceFactory(),
                memory: memory, generatedSpeech: avatar.Observer, revokeAvatar: avatar.Revoke, voiceIdentity: voiceIdentity,
                dataDirectory: store!.DataDirectory, spokenText: captions.Feed, smartHome: smartHome, lorebooks: lorebooks,
                tools: mcpTools);
            audioSessionEvents.LockedChanged += conversation.SetSessionLocked;
        }
        audioSessionEvents.LockedChanged += AvatarSessionLocked;
        this.startupError = startupError;
        characterTimer.Tick += (_, _) => UpdateCharacterButton();
        characterTimer.Start();
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
            ConversationButton.IsEnabled = AutomaticUpdateCheck.IsEnabled = CheckForUpdatesButton.IsEnabled = PrimaryStageButton.IsEnabled =
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
        ReadMachineAsync().Forget();
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
        PipelineText.Text = string.Join(Environment.NewLine, model.Pipeline.Select(node => node.Description));
        ActivityText.Text = setupOperations.IsRunning ? "An app-shared setup/audio/conversation worker owns resources. Check its action window; new effects wait for actual cleanup."
            : model.Activity;
        CreateButton.IsEnabled = !saving && !setupOperations.IsRunning && model.CanCreateProfile;
        ConversationButton.IsEnabled = PrimaryStageButton.IsEnabled = !saving && !model.IsRunning;
        RefreshButton.IsEnabled = !saving && model.CanRefresh;
        StopButton.IsEnabled = !saving && model.IsRunning;
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

    private Martlet.Core.Settings.SetupRole? nextSetupJob;

    private async void Setup_Click(object sender, RoutedEventArgs e)
    {
        if (store is null || closing || saving || model?.IsRunning == true) return;
        var characterWasShowing = avatar.IsShowing;
        if (!await StopAvatarSafelyAsync()) return;
        new SetupWindow(setupService!, setupOperations) { Owner = this, Troubleshooting = OpenTroubleshooting, ConfigurationRecovery = OpenRecovery, InitialRole = nextSetupJob }.ShowDialog();
        nextSetupJob = null;
        await RefreshAsync();
        if (characterWasShowing) await ShowSavedCharacterAsync(onlyIfAutoShow: false);
    }

    private async void AudioSetup_Click(object sender, RoutedEventArgs e)
    {
        if (store is null || closing || saving || model?.IsRunning == true) return;
        new AudioSetupWindow(setupService!, setupOperations, audioSetup,
            observe: text => { if (!closing) AudioStatusText.Text = text; }, sessionEvents: audioSessionEvents)
            { Owner = this, Troubleshooting = OpenTroubleshooting }.ShowDialog();
        // Closing can leave its last save or device listing finishing; a refresh now would be skipped and show stale status.
        for (var i = 0; i < 50 && setupOperations.IsRunning && !closing; i++) await Task.Delay(100);
        await RefreshAsync();
    }

    private async void Companion_Click(object sender, RoutedEventArgs e) => await OpenCompanionWindowAsync(importCard: false);

    private async Task OpenCompanionWindowAsync(bool importCard)
    {
        if (companionService is null || closing || saving || model?.IsRunning == true) return;
        new CompanionWindow(companionService, setupOperations, importCardOnOpen: importCard, lorebooks: lorebooks) { Owner = this }.ShowDialog();
        homeLore = null;
        await RefreshAsync();
    }

    private async Task OpenLorebooksAsync(bool import = false)
    {
        if (lorebooks is null || closing || saving || model?.IsRunning == true) return;
        new LorebookWindow(lorebooks, homeSettings?.Companion, importOnOpen: import) { Owner = this }.ShowDialog();
        homeLore = null;
        await RefreshAsync();
        if (!closing && openTab == CompanionTab.Lorebook) RenderTab();
    }

    private async void Memory_Click(object sender, RoutedEventArgs e)
    {
        if (memory is null || closing || saving || model?.IsRunning == true) return;
        new MemoryWindow(memory, setupOperations) { Owner = this }.ShowDialog();
        await RefreshAsync();
    }

    private async void Conversation_Click(object sender, RoutedEventArgs e)
    {
        if (conversation is null || closing || saving || model?.IsRunning == true) return;
        openConversation = new LiveConversationWindow(setupService!, setupOperations, conversation, audioSessionEvents, voiceIdentity: voiceIdentity,
            preferences: Talk, videoAddress: visionAddress)
            { Owner = this, Support = support };
        try { openConversation.ShowDialog(); }
        finally { openConversation = null; }
        await RefreshAsync();
    }

    private void VoiceLibrary_Click(object sender, RoutedEventArgs e) => OpenCompanion(CompanionTab.Voice);

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
                if (missing.Length == 0) ActionText.Text = "Everything this plan needs on this PC is already installed.";
                else InstallPrerequisitesAsync(missing).Forget();
                break;
            case AdvisorNextStep.Setup: OpenCompanion(CompanionTab.Thinking); break;
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
    /// <summary>Shows the prerequisites checklist in Martlet; it changes nothing until the user ticks and installs items.</summary>
    private void Prerequisites_Click(object sender, RoutedEventArgs e) => ChoosePrerequisitesAsync().Forget();

    private async Task ChoosePrerequisitesAsync()
    {
        if (closing) return;
        if (await Prerequisites.ChooseAndInstallAsync(this) is { } status && !closing) ActionText.Text = status;
        if (!closing) await ReadMachineAsync();
    }

    /// <summary>Installs prerequisites in a run window (no console) and reports the outcome on the home screen.</summary>
    private async Task InstallPrerequisitesAsync(IReadOnlyCollection<Prerequisite> items, string? ollamaModel = null)
    {
        if (closing) return;
        ActionText.Text = $"Installing {string.Join(", ", items.Select(i => i.Title))}; the run window shows the progress.";
        var status = await Prerequisites.InstallAsync(this, items, ollamaModel);
        if (closing) return;
        ActionText.Text = status;
        await ReadMachineAsync();
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
        ResetCharacterZoomButton.Visibility = ResetCharacterButton.Visibility;
    }
    private async void ResetCharacter_Click(object sender, RoutedEventArgs e) => await ResetCharacterPositionAsync();
    private async void ResetCharacterZoom_Click(object sender, RoutedEventArgs e) => await ResetCharacterZoomAsync();
    private async Task ResetCharacterZoomAsync()
    {
        try
        {
            await avatar.ZoomAsync("reset", lifetime.Token);
            ActionText.Text = "Character returned to its default size and zoom.";
        }
        catch (Exception error) when (error is System.IO.IOException or InvalidOperationException or TimeoutException or
            OperationCanceledException or ObjectDisposedException)
        {
            if (!closing) ActionText.Text = $"Character zoom could not be reset: {error.Message}";
        }
        finally { UpdateCharacterButton(); }
    }
    private async Task ResetCharacterPositionAsync()
    {
        try
        {
            await avatar.ResetPositionAsync(lifetime.Token);
            ActionText.Text = "Character moved back to the lower-right of your main screen at its default size.";
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
        RefreshHomeAsync().Forget();
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
        characterTimer.Stop();
        updateTimer.Stop();
        clusterTimer.Stop();
        audioSessionEvents.LockedChanged -= audioSetup.SetSessionLocked;
        audioSessionEvents.LockedChanged -= AvatarSessionLocked;
        if (conversation is not null) audioSessionEvents.LockedChanged -= conversation.SetSessionLocked;
        audioSessionEvents.Dispose();
        lifetime.Cancel();
        setupOperations.RequestCancellation();
        IsEnabled = false;
        if (model is not null)
            await model.CloseAsync();
        if (conversation is not null) await Task.Run(async () => await conversation.DisposeAsync());
        // Ends every MCP server Martlet started (they also end with Martlet's process through its job object).
        await Task.Run(async () => await mcpTools.DisposeAsync());
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
