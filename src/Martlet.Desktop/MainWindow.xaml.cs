using System.Windows;
using System.Windows.Automation;
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
    private readonly DesktopConversationHistory? conversationHistory;
    private readonly LorebookStore? lorebooks;
    private readonly SmartHome smartHome;
    private readonly MessagingService messaging;
    private readonly DiscordService discord;
    private readonly McpToolService mcpTools;
    private readonly VoiceIdentity voiceIdentity;
    private readonly LocalVoices localVoices;
    private readonly ParakeetListener? parakeet;
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
        ThemeChoice.SelectedIndex = (int)((Application.Current as App)?.SelectedTheme ?? AppearanceTheme.Light);
        AppearanceStatus.Text = (Application.Current as App)?.AppearanceNotice
            ?? "Choose a palette. Your choice is saved on this PC.";
        InitializeUpdates();
        this.support = support;
        audioSetup = new(setupOperations, new WindowsAudioDeviceCatalog(), new WasapiCaptureDeviceFactory(), new WasapiDeviceFactory());
        audioSessionEvents.LockedChanged += audioSetup.SetSessionLocked;
        var vault = new WindowsCredentialStore();
        setupService = store is null ? null : new SetupService(store, vault);
        companionService = store is null ? null : new CompanionSettingsService(store);
        memory = store is null ? null : new DesktopMemoryService(store);
        conversationHistory = store is null ? null : new DesktopConversationHistory(store.DataDirectory);
        lorebooks = store is null ? null : new LorebookStore(store.DataDirectory);
        smartHome = new(store?.DataDirectory, vault);
        discord = new(store?.DataDirectory, vault);
        // A Discord message naming the companion (a persona's name) is meant for Martlet.
        discord.CompanionNames = () => Martlet.Core.Speakers.CompanionNames.From(homeSettings?.Companion?.Personas.Select(p => p.Name)).Names;
        discordCalls = new(store?.DataDirectory);
        mcpTools = new(store?.DataDirectory);
        mcpTools.Changed += ToolsChanged;
        smartHome.Attach(mcpTools);
        messaging = new(store?.DataDirectory, vault) { Answer = AnswerMessageAsync };
        messaging.Changed += () => Dispatcher.BeginInvoke(MessagingChanged);
        voiceIdentity = new(store?.DataDirectory);
        voiceIdentity.Load();
        localVoices = new(store?.DataDirectory);
        parakeet = store is null ? null : new(LocalVoices.SpeechRoot(store.DataDirectory));
        // Discord calls transcribe on this PC or a paired computer and speak with a host or Windows voice; never the cloud.
        discord.Speech = new DiscordSpeech(() => homeSettings?.Setup?.Routes, parakeet, store?.DataDirectory, () => conversation?.Replying == true);
        recovery = store is null ? null : new(store, setupOperations, () => !support.HasResources);
        captions = new(avatar, store?.DataDirectory);
        captions.Changed += () => ShowSpeechDisplay();
        avatar.Placement = CharacterPlacementStore.Load(store?.DataDirectory);
        avatar.VoiceMuted = !Talk.SpeakReplies;
        avatar.Requested += action => Dispatcher.InvokeAsync(() => CharacterRequested(action));
        avatar.Gaze.Decides = Talk.DecideGaze;
        characterActions = new(store?.DataDirectory);
        characterThemes = new(store?.DataDirectory);
        if (setupService is not null)
        {
            // MARTLET_SIMULATE_MICROPHONE / MARTLET_SIMULATE_SPEAKERS: fixture devices for MCP verification (never real audio).
            var simulated = SimulatedAudio.Microphone();
            ICaptureDeviceFactory microphones = simulated is null ? new WasapiCaptureDeviceFactory() : simulated;
            IPlaybackDeviceFactory speakers = SimulatedAudio.Speakers() is { } silent ? silent : new WasapiDeviceFactory();
            // Martlet sings through the same output as its voice; the character's mouth follows the vocals. (The FIXTURE song
            // maker's songs play into a silent output, so automated checks never sound.) Songs are kept as creations.
            Martlet.Core.Creations.CreationRegistry.Shared.Register(Martlet.Conversation.SongCreations.Kind);
            // Web research reports are kept as creations too; showing one opens it as a web page in the default browser.
            Martlet.Core.Creations.CreationRegistry.Shared.Register(Martlet.Conversation.ResearchReports.Kind);
            Martlet.Core.Creations.CreationRegistry.Shared.Handle(Martlet.Conversation.ResearchReports.KindName,
                Martlet.Conversation.ResearchReports.Handler(store!.DataDirectory, OpenReportPage));
            // Pictures Martlet draws (draw_picture) are kept as creations too, and shown in the talk window.
            Martlet.Core.Creations.CreationRegistry.Shared.Register(Martlet.Conversation.PictureCreations.Kind);
            var singing = new ConversationSinging(store!.DataDirectory, new DesktopSongSource(store.DataDirectory),
                DesktopSongSource.Fixture ? new SilentSongOutput() : speakers is SimulatedSpeakers ? speakers : new WasapiDeviceFactory(),
                captions.Feed, avatar, (vocals, rate, token) => avatar.AnalyzeSongAsync(vocals, rate, OwnLipSyncEndpoint(), token));
            conversation = new(setupOperations, setupService, vault, microphones, discordCalls.Output(speakers),
                memory: memory, generatedSpeech: avatar.Observer, revokeAvatar: avatar.Revoke, voiceIdentity: voiceIdentity,
                dataDirectory: store!.DataDirectory, spokenText: captions.Feed, smartHome: smartHome, lorebooks: lorebooks,
                tools: mcpTools, voices: localVoices, localListener: parakeet,
                echoReducer: simulated is not null ? null
                    : new(microphones, new WasapiLoopbackReferenceFactory(), Martlet.EchoCancellation.WebRtcEchoCanceller.Create),
                pcAudio: simulated is not null ? null : new Martlet.Audio.PcAudioCaptureFactory(discordCalls.Sources(new WasapiPcAudioSourceFactory())),
                characterCues: avatar.Cues, characterActions: CharacterActionPromptFor, history: conversationHistory, singing: singing);
            audioSessionEvents.LockedChanged += conversation.SetSessionLocked;
            conversation.VoiceVolume = Talk.VoiceVolume;
            conversation.ChattinessDecided += (_, _) => Dispatcher.BeginInvoke(FollowChattiness);
            discord.UseReplies(setupService, vault, conversation, memory, lorebooks);
        }
        WireCharacterActions();
        WireCharacterThemes();
        audioSessionEvents.LockedChanged += AvatarSessionLocked;
        this.startupError = startupError;
        characterTimer.Tick += (_, _) =>
        {
            UpdateCharacterButton();
            RenderListening();
            if (!started) return;
            FollowCharacterActions();
            FollowCharacterTheme();
        };
        characterTimer.Start();
        DataPathText.Text = "Settings are stored on this PC.";
        StatusText.Text = "Loading local status...";
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
            PipelineText.Text = "Status is unavailable until Martlet can use its data folder.";
            ConversationButton.IsEnabled = AutomaticUpdateCheck.IsEnabled = CheckForUpdatesButton.IsEnabled = PrimaryStageButton.IsEnabled =
                AutomaticHostUpdate.IsEnabled = UpdateHostsButton.IsEnabled = false;
        }
        InitializeShell();
        InitializeCluster();
        InitializeSettingsSync();
        InitializeReminders();
        InitializeMemorySync();
        InitializeNetwork();
        InitializeApiKeys();
        InitializeNearby();
        InitializeVoiceSync();
        InitializeSpeakingVoices();
        InitializeCharacterModels();
        InitializeCreations();
        InitializeHomeShare();
        InitializeNodeAgent();
        InitializeLogs();
        InitializeBackground();
        InitializeTasks();
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        StartAmbientMotion();
        StartSimulatedTask();
        await StartRunningAsync();
    }

    private bool started;

    /// <summary>Everything Martlet does once it runs: status, the saved character, sync, the node agent, logs and updates. It
    /// runs when the window first shows, or straight away when Martlet starts in the notification area.</summary>
    private async Task StartRunningAsync()
    {
        if (started) return;
        started = true;
        // A host PC stays awake from the start, before its first network sync or host check (after a Wake-on-LAN wake, Windows
        // sleeps again within minutes otherwise).
        UpdateStayAwake();
        ReadMachineAsync().Forget();
        await RefreshAsync();
        if (!closing) ContinueSetupAsync().Forget();
        // A host lends its power to your companion PC: the character, listening and Parakeet stay off here, while their
        // saved choices are kept for when this PC is your companion PC again.
        if (Role == DeviceRole.Companion)
        {
            await ShowSavedCharacterAsync(onlyIfAutoShow: true);
            if (background.StartCompanion && !closing) await StartCompanionAsync();
            // Messaging apps (Companion › Messaging): the paired chats reach Martlet whenever it runs on this companion PC.
            if (!closing) messaging.Start();
        }
        else ErrorLog.Info("Martlet started as a Martlet host: the character and listening stay off on this PC" +
            (background.StartCompanion ? " (When Martlet starts, show the character and start listening is kept for when it's your companion PC)." : "."));
        // An update Martlet just restarted into brings back the character, listening and watching that were on when it closed for it.
        if (!closing) await ResumeAfterUpdateAsync();
        StartCluster();
        StartSettingsSync();
        StartReminders();
        StartMemorySync();
        StartNetwork();
        StartApiKeys();
        StartVoiceSync();
        StartSpeakingVoices();
        StartCharacterModels();
        StartCreations();
        StartHomeShare();
        InitializeDiscordCompanion();
        discord.StartIfEnabledAsync(lifetime.Token).Forget();
        StartNodeAgent();
        StartLogSharing();
        // Parakeet takes a few seconds to load; do it now rather than on the first thing said.
        if (Role == DeviceRole.Companion &&
            homeSettings?.Setup?.Routes.FirstOrDefault(r => r.Role == SetupRole.Stt) is { RouteType: SetupRouteType.LocalParakeet } listening)
            parakeet?.WarmAsync(listening.ModelId).Forget();
        // Martlet is usable now; this PC's own host service follows its version in the background (after an update, right away).
        FollowOwnHostAsync().Forget();
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
        ActivityText.Text = setupOperations.IsRunning ? "A setup task is still finishing. Try again in a moment."
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
                if (!closing) ActionText.Text = "Saving is still finishing. Refresh in a moment.";
                return;
            }
            var completed = await worker.Completion;
            var result = completed.Saved?.Save;
            if (!closing)
                ActionText.Text = result is null ? "Couldn't create the profile. Refresh and try again."
                    : result.Saved
                    ? "Profile created."
                    : $"{result.Error!.Summary} {DiagnosticCatalog.Remedy(result.Error.ActionId).Guidance}";
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

    private async void Memory_Click(object sender, RoutedEventArgs e) => await OpenMemoryAsync();

    /// <summary>Opens Memory, showing the facts of <paramref name="person"/> (a voice ID, from People) when given.</summary>
    private async Task OpenMemoryAsync(string? person = null)
    {
        if (memory is null || closing || saving || model?.IsRunning == true) return;
        memoryWindowOpen = true;
        try { new MemoryWindow(memory, setupOperations, voices: () => localVoices.Roster, person: person) { Owner = this }.ShowDialog(); }
        finally { memoryWindowOpen = false; }
        QueueMemorySync();
        await RefreshAsync();
    }

    /// <summary>Companion › Memory › Open conversation history: the record of conversations on this PC, to read, search and delete.</summary>
    private void History_Click()
    {
        if (conversationHistory is null || closing) return;
        new ConversationHistoryWindow(conversationHistory) { Owner = this }.ShowDialog();
        if (!closing && openTab == CompanionTab.Memory) RenderTab();
    }

    /// <summary>Opens the talk window beside Martlet (modeless, so Home, Companion and the rest stay usable while you talk), or
    /// brings it back to the front when it is already open or runs hidden while Martlet listens.</summary>
    private void Conversation_Click(object sender, RoutedEventArgs e)
    {
        if (ConversationSession() is not { } window) return;
        if (!window.IsVisible) window.Show();
        if (window.WindowState == WindowState.Minimized) window.WindowState = WindowState.Normal;
        window.Activate();
        UpdateTray();
    }

    /// <summary>Home's Start talking reads Show conversation while a conversation runs (shown or hidden).</summary>
    private void RenderConversationButton()
    {
        var open = openConversation is not null;
        ConversationButton.Content = open ? "Show _conversation" : "Start _talking";
        AutomationProperties.SetName(ConversationButton, open ? "Show conversation" : "Start talking");
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
                if (missing.Length == 0) ActionText.Text = "This PC already has everything needed for that plan.";
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
        if (info.BestGpu is not { } gpu) return (AdvisorGpu.None, "No dedicated graphics card found");
        var answer = SetupAdvisor.Classify(gpu.Name, gpu.MemoryGb);
        return (answer, answer == AdvisorGpu.None ? $"{gpu.Name} (not dedicated)" : gpu.Describe());
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
    private void Hosts_Click(object sender, RoutedEventArgs e) => OpenHosts(0);
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
        ActionText.Text = $"Installing {string.Join(", ", items.Select(i => i.Title))}. The progress window shows details.";
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
                if (await StopAvatarSafelyAsync()) ActionText.Text = "Character hidden.";
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
        // Reset stays reachable while the character is hidden (or lost off screen) once it has a saved place.
        ResetCharacterButton.Visibility = avatar.IsShowing || avatar.Placement is not null ? Visibility.Visible : Visibility.Collapsed;
        ResetCharacterZoomButton.Visibility = avatar.IsShowing ? Visibility.Visible : Visibility.Collapsed;
        var locked = avatar.PlacementLocked;
        ResetCharacterButton.IsEnabled = true;
        ResetCharacterButton.ToolTip = avatar.IsShowing
            ? "Move the character back to the lower-right of your main screen" + (locked ? " (it stays locked there)" : "")
            : "Forget where the character was, so it shows at the lower-right of your main screen";
        // Unlocking also works while the character is hidden: it then shows at its default spot.
        LockCharacterButton.Visibility = avatar.IsShowing || locked ? Visibility.Visible : Visibility.Collapsed;
        LockCharacterButton.IsEnabled = !changingCharacterLock;
        LockCharacterButton.Content = locked ? "Un_lock character position" : "Lock character p_osition";
        AutomationProperties.SetName(LockCharacterButton, locked ? "Unlock character position" : "Lock character position");
        LockCharacterButton.ToolTip = locked ? "Let the character be dragged, moved and resized again"
            : "Keep the character where it is until you unlock it here or on its right-click menu";
    }

    /// <summary>Carries out a choice from the character's own right-click menu (or Esc on it): hide the character, open
    /// Martlet, talk, open Companion › Character, lock or unlock its position, or mute or unmute Martlet's voice. The overlay
    /// handles its zoom, position and keep-on-top itself.</summary>
    private void CharacterRequested(string action)
    {
        if (closing) return;
        if (action == "placed")
        {
            RememberCharacterPlacementAsync().Forget();
            return;
        }
        ErrorLog.Info($"The character's menu chose '{action}'.");
        switch (action)
        {
            case "hide":
                if (avatar.IsShowing) Character_Click(this, new RoutedEventArgs());
                break;
            case "open": ShowFromTray(); break;
            case "talk": TrayTalk(); break;
            case "settings":
                ShowFromTray();
                // Another Martlet window waiting for an answer keeps the focus, as from the notification area.
                if (IsWindowEnabled(WindowHandle)) OpenCompanion(CompanionTab.Character);
                break;
            case "lock":
                if (!avatar.PlacementLocked) SetCharacterLockAsync(true).Forget();
                break;
            case "unlock":
                if (avatar.PlacementLocked) SetCharacterLockAsync(false).Forget();
                break;
            case "mute": SetVoiceMuted(true); break;
            case "unmute": SetVoiceMuted(false); break;
        }
    }
    private async void ResetCharacter_Click(object sender, RoutedEventArgs e) => await ResetCharacterPositionAsync();
    private async void ResetCharacterZoom_Click(object sender, RoutedEventArgs e) => await ResetCharacterZoomAsync();
    private Task ResetCharacterZoomAsync() => ZoomCharacterAsync("reset");
    private void LockCharacter_Click(object sender, RoutedEventArgs e) => SetCharacterLockAsync(!avatar.PlacementLocked).Forget();

    private bool changingCharacterLock;

    /// <summary>The character was moved or resized and has settled: saves where it is now (and on which monitor) on this PC,
    /// so it shows there again after Hide/Show, a restart, a shutdown or an update.</summary>
    private async Task RememberCharacterPlacementAsync()
    {
        try
        {
            if (await avatar.ReadPlacementAsync(lifetime.Token) is not { } place || closing) return;
            if (CharacterPlacementStore.Save(store?.DataDirectory, place))
                ErrorLog.Info($"Character position saved at {place.Left:0}, {place.Top:0} ({place.Width:0} × {place.Height:0})" +
                    (place.Screen is { } screen ? $" on {CharacterScreen(screen)}." : "."));
            UpdateCharacterButton();
            if (characterPlacementNote is { } note) note.Text = CharacterPlacementText();
        }
        catch (Exception error) when (error is System.IO.IOException or InvalidOperationException or TimeoutException or
            OperationCanceledException or ObjectDisposedException or System.IO.InvalidDataException or System.Text.Json.JsonException)
        {
            if (!closing) ErrorLog.Warn("The character's new position couldn't be read to save it.", error);
        }
    }

    /// <summary>A monitor's device name as people read it: DISPLAY2 rather than \\.\DISPLAY2.</summary>
    internal static string CharacterScreen(string device) => device.StartsWith(@"\\.\", StringComparison.Ordinal) ? device[4..] : device;

    /// <summary>Locks the showing character where it is (from here, Companion › Character or the character's own menu) or
    /// unlocks it (only from this window), and saves that on this PC so a locked character shows in the same place.</summary>
    private async Task SetCharacterLockAsync(bool locked)
    {
        if (closing || changingCharacterLock) return;
        changingCharacterLock = true;
        UpdateCharacterButton();
        try
        {
            var place = await avatar.LockPlacementAsync(locked, lifetime.Token);
            var saved = CharacterPlacementStore.Save(store?.DataDirectory, place);
            if (closing) return;
            ErrorLog.Info(locked && place is { } at
                ? $"Character position locked at {at.Left:0}, {at.Top:0} ({at.Width:0} × {at.Height:0})" +
                    (at.Screen is { } screen ? $" on {CharacterScreen(screen)}." : ".")
                : "Character position unlocked.");
            ActionText.Text = (locked
                ? "Character position locked. Unlock it here, in Companion › Character or on the character's right-click menu to move it."
                : "Character position unlocked. Drag the character to move it.") +
                (saved ? "" : locked ? " It couldn't be saved on this PC, so it unlocks when the character hides."
                    : " It couldn't be saved on this PC, so the character may show locked next time.");
        }
        catch (Exception error) when (error is System.IO.IOException or InvalidOperationException or TimeoutException or
            OperationCanceledException or ObjectDisposedException or System.IO.InvalidDataException or System.Text.Json.JsonException)
        {
            if (!closing) ActionText.Text = $"Couldn't {(locked ? "lock" : "unlock")} the character's position: {error.Message}";
        }
        finally
        {
            changingCharacterLock = false;
            UpdateCharacterButton();
            if (!closing) RenderHome();
        }
    }

    /// <summary>Companion › Character's line on whether the character's position is locked, where it is remembered and on which
    /// monitor.</summary>
    private string CharacterPlacementText()
    {
        var saved = avatar.Placement;
        var where = saved is { } spot ? $"{spot.Left:0}, {spot.Top:0}" + (spot.Screen is { } device ? $" on {CharacterScreen(device)}" : "") : "";
        return (saved, avatar.IsShowing) switch
        {
            ({ Locked: true } at, true) => $"Position locked at {where} ({at.Width:0} × {at.Height:0}). The character can't be dragged, moved or resized until you unlock it; zoom still works.",
            ({ Locked: true }, false) => $"Position locked at {where}. The character shows there when it opens, until you unlock it.",
            ({ }, true) => $"Position unlocked, remembered at {where} on this PC, even after a restart or update. Drag the character where you want it, then lock it here or from its right-click menu.",
            ({ }, false) => $"Position unlocked. The character shows where you left it, at {where}. Reset position if it's lost: it then shows at the lower-right.",
            (null, true) => "Position unlocked. Drag the character where you want it; Martlet remembers where on this PC, even after a restart or update. Lock it here or from its right-click menu.",
            _ => "Position unlocked. The character shows at the lower-right; show it to place and lock it."
        };
    }

    /// <summary>The Character page's line on where the character is remembered, refreshed when it is moved.</summary>
    private TextBlock? characterPlacementNote;

    /// <summary>The Character page's line describing the overlay's current size, zoom and head framing.</summary>
    private TextBlock? characterViewText;

    /// <summary>Zooms the character "in", "out", "reset"s it, or only reads its "status", then shows the resulting view.</summary>
    private async Task ZoomCharacterAsync(string action)
    {
        try
        {
            var view = await avatar.ZoomAsync(action, lifetime.Token);
            if (closing) return;
            if (characterViewText is { } line) line.Text = view is null ? "" : CharacterViewText(view);
            if (action == "reset") ActionText.Text = "Character view reset.";
            else if (action != "status" && view is not null) ActionText.Text = $"Character zoomed {action}. {CharacterViewText(view)}";
        }
        catch (Exception error) when (error is System.IO.IOException or InvalidOperationException or TimeoutException or
            OperationCanceledException or ObjectDisposedException or System.IO.InvalidDataException or System.Text.Json.JsonException)
        {
            if (!closing && action != "status")
                ActionText.Text = action == "reset" ? $"Couldn't reset character zoom: {error.Message}" : $"Couldn't change character zoom: {error.Message}";
        }
        finally { UpdateCharacterButton(); }
    }

    internal static string CharacterViewText(RendererView view) =>
        $"Character view: {view.Width:0} × {view.Height:0}" +
        (view.DrawWidth is double drawn && drawn > view.Width + 0.5 ? $" ({drawn:0} wide with room to move)" : "") + view.ScreenTop switch
        {
            null => "",
            double below when below >= 0 => ", on screen",
            _ => ", partly above the screen"
        } + $", zoom {view.Zoom:0.##}x. " + view.HeadTop switch
        {
            null => "head position not available.",
            double below when below >= 0 => "head is in view.",
            _ => "head may be cropped."
        } + (view.Locked == true ? " Position locked." : "");
    private async Task ResetCharacterPositionAsync()
    {
        try
        {
            var showing = avatar.IsShowing;
            var place = await avatar.ResetPositionAsync(lifetime.Token);
            var saved = CharacterPlacementStore.Save(store?.DataDirectory, place);
            if (closing) return;
            ErrorLog.Info(place is { } at
                ? $"Character position reset to {at.Left:0}, {at.Top:0}" + (at.Screen is { } screen ? $" on {CharacterScreen(screen)}." : ".")
                : "Character position reset; it shows at its default spot next time.");
            ActionText.Text = (showing
                ? "Character moved back to the lower-right of your main screen." + (place is { Locked: true } ? " It stays locked there." : "")
                : "Character position reset. It shows at the lower-right of your main screen next time, unlocked.") +
                (saved ? "" : " It couldn't be saved on this PC.");
        }
        catch (Exception error) when (error is System.IO.IOException or InvalidOperationException or TimeoutException or
            OperationCanceledException or ObjectDisposedException or System.IO.InvalidDataException or System.Text.Json.JsonException)
        {
            if (!closing) ActionText.Text = $"Couldn't reset character position: {error.Message}";
        }
        finally
        {
            UpdateCharacterButton();
            if (!closing) RenderHome();
        }
    }
    /// <summary>Shows the saved character, or the bundled default when none is configured.</summary>
    private async Task ShowSavedCharacterAsync(bool onlyIfAutoShow)
    {
        if (store is null || setupService is null || closing || avatar.IsShowing) return;
        if (Role == DeviceRole.Host)
        {
            if (!onlyIfAutoShow) ActionText.Text = HostHasNoCompanionText;
            UpdateCharacterButton();
            return;
        }
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
            characterCleanupProblem = null;
            ActionText.Text = avatar.Status;
        }
        catch (Exception error) when (error is System.IO.IOException or InvalidOperationException or TimeoutException or
            UnauthorizedAccessException or System.ComponentModel.Win32Exception or Martlet.Core.Contracts.ContractException or
            OperationCanceledException)
        {
            if (!closing) ActionText.Text = $"Couldn't show the character: {error.Message}";
        }
        finally { UpdateCharacterButton(); }
    }
    private void OpenAvatar(Window owner)
    {
        if (store is null || setupService is null || closing) return;
        if (Role == DeviceRole.Host)
        {
            ActionText.Text = HostHasNoCompanionText;
            return;
        }
        avatarWindowOpen = true;
        try
        {
            new AvatarWindow(avatar, new AvatarProfileStore(store.DataDirectory), setupService, setupOperations, captions, ShareCharacterAsync)
                { Owner = owner }.ShowDialog();
        }
        finally { avatarWindowOpen = false; }
        UpdateCharacterButton();
        RefreshHomeAsync().Forget();
    }
    private void AvatarSessionLocked(bool locked)
    {
        // Revocation ends privileged Audio2Face analysis; the character itself and local lip-sync stay available.
        if (locked) avatar.Revoke();
    }
    /// <summary>Why the character's last stop did not finish, or null. Shown on Companion › Character until a stop succeeds.</summary>
    private string? characterCleanupProblem;
    private async Task<bool> StopAvatarSafelyAsync()
    {
        try
        {
            await avatar.StopAsync();
            characterCleanupProblem = null;
            return true;
        }
        catch (Exception error) when (error is System.IO.IOException or InvalidOperationException or TimeoutException or
            System.ComponentModel.Win32Exception or UnauthorizedAccessException)
        {
            ErrorLog.Warn("The character could not be stopped cleanly.", error);
            characterCleanupProblem = "The character didn't close cleanly. Press Hide or Show character to try again.";
            if (!closing) ActionText.Text = characterCleanupProblem;
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

    private void Exit_Click(object sender, RoutedEventArgs e) => ExitMartlet();
}
