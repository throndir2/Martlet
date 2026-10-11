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
        // Who uses Martlet now (MainWindow.Accounts.cs), before anything loads that belongs to an account.
        accounts = OpenAccounts(store);
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
        if (conversationHistory is not null) ConversationsPage.Attach(conversationHistory);
        // Lorebooks are each account's own, in its folder (docs/ACCOUNTS.md).
        lorebooks = store is null ? null : new LorebookStore(accounts?.AccountFolder ?? store.DataDirectory);
        smartHome = new(store?.DataDirectory, vault);
        discord = new(store?.DataDirectory, vault);
        // A Discord message naming the companion (a persona's name) is meant for Martlet.
        discord.CompanionNames = () => Martlet.Core.Speakers.CompanionNames.From(homeSettings?.Companion?.Personas.Select(p => p.Name)).Names;
        discordCalls = new(store?.DataDirectory);
        mcpTools = new(store?.DataDirectory);
        mcpTools.Changed += ToolsChanged;
        smartHome.Attach(mcpTools);
        messaging = new(store?.DataDirectory, vault) { Answer = AnswerMessageAsync, History = conversationHistory };
        messaging.Changed += () => Dispatcher.BeginInvoke(MessagingChanged);
        // Discord text exchanges join the record while it is kept; deletions and edits there go to Telegram and Discord too.
        discord.History = conversationHistory;
        discord.Recording = () => conversationHistory?.Active(homeSettings?.Memory) == true;
        conversationHistory?.Platforms.Start();
        voiceIdentity = new(store?.DataDirectory);
        voiceIdentity.Load();
        localVoices = new(store?.DataDirectory);
        parakeet = store is null ? null : new(LocalVoices.SpeechRoot(store.DataDirectory));
        // Discord calls transcribe on this PC or a paired computer and speak with a host's voice engine; never the cloud.
        discord.Speech = new DiscordSpeech(() => homeSettings?.Setup?.Routes, parakeet, store?.DataDirectory, () => conversation?.Replying == true);
        recovery = store is null ? null : new(store, setupOperations, () => !support.HasResources);
        captions = new(avatar, store?.DataDirectory);
        captions.Changed += () => ShowSpeechDisplay();
        avatar.Placement = CharacterPlacementStore.Load(store?.DataDirectory);
        avatar.ClickThrough = CharacterClickThroughStore.Load(store?.DataDirectory);
        avatar.VoiceMuted = !Talk.SpeakReplies;
        avatar.Requested += action => Dispatcher.InvokeAsync(() => CharacterRequested(action));
        avatar.Gaze.Decides = Talk.DecideGaze;
        characterActions = new(store?.DataDirectory);
        characterTouchZones = new(store?.DataDirectory);
        characterEyes = new(store?.DataDirectory);
        characterTemperaments = new(store?.DataDirectory);
        characterReactionChanges = new(store?.DataDirectory);
        // The character's usual gaze: your choice, else what the active persona's temperament decided.
        avatar.Gaze.Personality = () => characterTemperaments.For(homeSettings?.Companion?.ActivePersonaId)?.Gaze;
        avatar.Gaze.Configure(Talk.GazeUsual, Talk.GazeFree);
        characterThemes = new(store?.DataDirectory);
        if (setupService is not null)
        {
            // MARTLET_SIMULATE_MICROPHONE / MARTLET_SIMULATE_SPEAKERS: fixture devices for MCP verification (never real audio).
            var simulated = SimulatedAudio.Microphone();
            // The microphone keeps its last seconds in memory only while a check-in that is on asks for them.
            ICaptureDeviceFactory microphones = new Martlet.Audio.KeptCaptureDeviceFactory(
                simulated is null ? new WasapiCaptureDeviceFactory() : simulated, checkInMicrophone);
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
                pcAudio: simulated is not null ? null : new Martlet.Audio.PcAudioCaptureFactory(discordCalls.Sources(new WasapiPcAudioSourceFactory()),
                    sound: checkInPcSound),
                pcActivity: simulated is not null ? null : new PcActivityMonitor(() => new WindowsPcActivitySource()),
                characterCues: avatar.Cues, characterActions: CharacterActionPromptFor, history: conversationHistory, singing: singing,
                board: contextBoard, turnJudge: SmartTurnJudge.Bundled(), listeningStandIn: ListeningStandIn);
            conversation.TurnDecided += () => Dispatcher.BeginInvoke(ShowTurnJudge);
            conversation.EarlyDecided += () => Dispatcher.BeginInvoke(ShowEarlyReplies);
            audioSessionEvents.LockedChanged += conversation.SetSessionLocked;
            conversation.VoiceVolume = Talk.VoiceVolume;
            conversation.QuickSounds = Talk.QuickSoundOptions;
            conversation.QuickSoundsChanged += () => Dispatcher.BeginInvoke(ShowQuickSounds);
            conversation.ChattinessDecided += (_, _) => Dispatcher.BeginInvoke(FollowChattiness);
            conversation.ExchangeEnded += () => Dispatcher.BeginInvoke(CheckInExchangeEnded);
            discord.UseReplies(setupService, vault, conversation, memory, lorebooks);
        }
        WireCharacterActions();
        WireCharacterTouchZones();
        WireVoiceSounds();
        WireCharacterReactionChanges();
        WireCharacterPhysical();
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
            TickCharacterEyes();
        };
        characterTimer.Start();
        var processor = Martlet.Core.Platforms.MachineArchitecture.Current;
        ThisPcArchitectureText.Text = "This PC: " + processor.Describe() + (processor.WindowsOnArmNote is { } arm ? " " + arm : "");
        if (store is not null)
        {
            model = new(new FoundationStatusService(store).Executor);
            model.PropertyChanged += (_, _) => Render();
            ageTimer.Tick += (_, _) => model.UpdateAge();
            ageTimer.Start();
        }
        else
        {
            ConversationButton.IsEnabled = AutomaticUpdateCheck.IsEnabled = CheckForUpdatesButton.IsEnabled = PrimaryStageButton.IsEnabled =
                AutomaticHostUpdate.IsEnabled = UpdateHostsButton.IsEnabled = false;
        }
        InitializeShell();
        InitializeCluster();
        InitializeNodePresence();
        InitializeSettingsSync();
        InitializeHouseholdSharing();
        InitializeReminders();
        InitializeCheckIns();
        InitializeRecommendedSetup();
        InitializeConfiguring();
        InitializeMemorySync();
        InitializeAccounts();
        InitializeAccountSecurity();
        InitializeNetwork();
        InitializeApiKeys();
        InitializeFriends();
        InitializeNearby();
        InitializeVoiceSync();
        InitializeVoiceLinks();
        InitializeSpeakingVoices();
        InitializeCharacterModels();
        InitializeCreations();
        InitializeHomeShare();
        InitializeNodeAgent();
        InitializeLogs();
        InitializeBackground();
        InitializeTasks();
        InitializeThinkingRequests();
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        StartAmbientMotion();
        StartSimulatedTask();
        StartSimulatedRequests();
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
        // A switch between accounts cut short at the last start is finished before the character and the conversation load.
        await FinishAccountSettingsAsync();
        ReadMachineAsync().Forget();
        await RefreshAsync();
        if (!closing) ContinueSetupAsync().Forget();
        // A host PC starts Docker Desktop and its host roles by itself, warm for the first request.
        if (!closing && Role == DeviceRole.Host && deviceRole is not null) StartHostRolesByItself();
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
        StartNodePresence();
        StartSettingsSync();
        StartHouseholdSharing();
        StartReminders();
        StartCheckIns();
        StartMemorySync();
        StartNetwork();
        StartAccounts();
        StartApiKeys();
        StartFriends();
        StartVoiceSync();
        StartSpeakingVoices();
        StartCharacterModels();
        StartCreations();
        StartModelCatalog();
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
    private async Task RefreshAsync()
    {
        if (model is null)
        {
            ActionText.Text = startupError ?? "";
            return;
        }
        if (!closing)
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
        support.ObserveReport(model.Report);
        ConversationButton.IsEnabled = PrimaryStageButton.IsEnabled = !model.IsRunning;
    }

    private async void AudioSetup_Click(object sender, RoutedEventArgs e)
    {
        if (store is null || closing || model?.IsRunning == true) return;
        new AudioSetupWindow(setupService!, setupOperations, audioSetup, sessionEvents: audioSessionEvents)
            { Owner = this, Troubleshooting = OpenTroubleshooting }.ShowDialog();
        // Closing can leave its last save or device listing finishing; a refresh now would be skipped and show stale status.
        for (var i = 0; i < 50 && setupOperations.IsRunning && !closing; i++) await Task.Delay(100);
        await RefreshAsync();
    }

    private async void Companion_Click(object sender, RoutedEventArgs e) => await OpenCompanionWindowAsync(importCard: false);

    private async Task OpenCompanionWindowAsync(bool importCard)
    {
        if (companionService is null || closing || model?.IsRunning == true) return;
        var personalities = homeSettings?.Companion?.Personas.ToDictionary(p => p.Id, p => p.Text);
        new CompanionWindow(companionService, setupOperations, importCardOnOpen: importCard, lorebooks: lorebooks) { Owner = this }.ShowDialog();
        homeLore = null;
        // The window's last save can still be finishing; the refresh would be skipped and miss a changed personality.
        for (var i = 0; i < 50 && setupOperations.IsRunning && !closing; i++) await Task.Delay(100);
        await RefreshAsync();
        PersonalitiesSaved(personalities);
    }

    private async Task OpenLorebooksAsync(bool import = false)
    {
        if (lorebooks is null || closing || model?.IsRunning == true) return;
        new LorebookWindow(lorebooks, homeSettings?.Companion, importOnOpen: import) { Owner = this }.ShowDialog();
        homeLore = null;
        await RefreshAsync();
        if (!closing && openTab == CompanionTab.Lorebook) RenderTab();
    }

    private async void Memory_Click(object sender, RoutedEventArgs e) => await OpenMemoryAsync();

    /// <summary>Opens Memory, showing the facts of <paramref name="person"/> (a voice ID, from People) when given.</summary>
    private async Task OpenMemoryAsync(string? person = null)
    {
        if (memory is null || closing || model?.IsRunning == true) return;
        memoryWindowOpen = true;
        try { new MemoryWindow(memory, setupOperations, voices: () => localVoices.Roster, person: person, yours: localVoices.IsYours) { Owner = this, Sharing = MemorySharingOptionsNow() }.ShowDialog(); }
        finally { memoryWindowOpen = false; }
        QueueMemorySync();
        await RefreshAsync();
    }

    /// <summary>Companion › Memory › Open conversations: goes to the Conversations page, to read, search, edit and delete the record.</summary>
    private void History_Click()
    {
        if (conversationHistory is null || closing) return;
        Navigate(NavConversations);
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
        ConversationButton.Content = open ? "Show conversation" : "Start talking";
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
                if (await StopAvatarSafelyAsync())
                {
                    ActionText.Text = "Character hidden.";
                    NoticeCharacterChange(global::Martlet.Conversation.PhysicalKind.Hidden);
                }
            }
            else
            {
                await ShowSavedCharacterAsync(onlyIfAutoShow: false);
                if (avatar.IsShowing) NoticeCharacterChange(global::Martlet.Conversation.PhysicalKind.Shown);
            }
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
        CharacterButton.Content = avatar.IsShowing ? "Hide character" : "Show character";
        // Reset stays reachable while the character is hidden (or lost off screen) once it has a saved place.
        ResetCharacterButton.Visibility = avatar.IsShowing || avatar.Placement is not null ? Visibility.Visible : Visibility.Collapsed;
        ResetCharacterZoomButton.Visibility = avatar.IsShowing ? Visibility.Visible : Visibility.Collapsed;
        var locked = avatar.PlacementLocked;
        ResetCharacterButton.IsEnabled = true;
        ResetCharacterButton.ToolTip = avatar.IsShowing
            ? "Move the character back to the lower-right of your main screen" + (locked ? " (it stays locked there)" : "")
            : "Forget where the character was, so it shows at the lower-right of your main screen";
    }

    /// <summary>Carries out a choice from the character's own right-click menu (or Esc on it): hide the character, open
    /// Martlet, talk, open Companion › Character, lock or unlock its position, mute or unmute Martlet's voice, or choose where
    /// its eyes go (Eyes). The overlay handles its zoom, position and keep-on-top itself.</summary>
    private void CharacterRequested(string action)
    {
        if (closing) return;
        if (action == "placed")
        {
            RememberCharacterPlacementAsync().Forget();
            return;
        }
        if (action == "framed")
        {
            RememberCallFramingAsync().Forget();
            return;
        }
        ErrorLog.Info($"The character's menu chose '{action}'.");
        if (action.StartsWith(Martlet.Avatar.Hosting.RendererRequest.LookPrefix, StringComparison.Ordinal))
        {
            ChooseCharacterGaze(action);
            return;
        }
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
            case Martlet.Avatar.Hosting.RendererRequest.ClickThroughOn:
                if (!avatar.ClickThrough) SetCharacterClickThroughAsync(true).Forget();
                break;
            case Martlet.Avatar.Hosting.RendererRequest.ClickThroughOff:
                if (avatar.ClickThrough) SetCharacterClickThroughAsync(false).Forget();
                break;
            case "clear": ClearCharacterEmotesAsync().Forget(); break;
        }
    }
    private async void ResetCharacter_Click(object sender, RoutedEventArgs e) => await ResetCharacterPositionAsync();
    private async void ResetCharacterZoom_Click(object sender, RoutedEventArgs e) => await ResetCharacterZoomAsync();
    private Task ResetCharacterZoomAsync() => ZoomCharacterAsync("reset");

    private bool changingCharacterLock, changingCharacterClickThrough;

    /// <summary>Lets clicks pass through the character (from Companion › Character, the notification-area menu or the
    /// character's own menu) or makes it catch them again (only from Martlet: the mouse can't reach the character's menu then),
    /// and saves that on this PC so the character shows the same way next time.</summary>
    private async Task SetCharacterClickThroughAsync(bool on)
    {
        if (closing || changingCharacterClickThrough) return;
        changingCharacterClickThrough = true;
        UpdateCharacterButton();
        try
        {
            await avatar.SetClickThroughAsync(on, lifetime.Token);
            var saved = CharacterClickThroughStore.Save(store?.DataDirectory, on);
            if (closing) return;
            ErrorLog.Info(on ? "Click-through turned on: clicks pass through the character." : "Click-through turned off: the character catches clicks.");
            ActionText.Text = (on
                ? "Clicks now pass through the character to the windows under it. Turn this off in Companion › Character or from Martlet's icon in the notification area."
                : "Click-through is off. You can drag, zoom and right-click the character again.") +
                (saved ? "" : on ? " It couldn't be saved on this PC, so it turns off when Martlet restarts."
                    : " It couldn't be saved on this PC, so it may turn on again when Martlet restarts.");
        }
        catch (Exception error) when (error is System.IO.IOException or InvalidOperationException or TimeoutException or
            OperationCanceledException or ObjectDisposedException or System.IO.InvalidDataException or System.Text.Json.JsonException)
        {
            if (!closing) ActionText.Text = $"Couldn't turn click-through {(on ? "on" : "off")}: {error.Message}";
        }
        finally
        {
            changingCharacterClickThrough = false;
            UpdateCharacterButton();
            if (!closing) RenderHome();
        }
    }

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
            RememberProfileHere();
            UpdateCharacterButton();
            if (characterPlacementNote is { } note) note.Text = CharacterPlacementText();
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception error) when (RendererFailures.Is(error, lifetime.Token))
        {
            if (!closing) RendererFailures.Log("The character's new position couldn't be read to save it", error);
        }
    }

    /// <summary>A monitor's device name as people read it: DISPLAY2 rather than \\.\DISPLAY2.</summary>
    internal static string CharacterScreen(string device) => device.StartsWith(@"\\.\", StringComparison.Ordinal) ? device[4..] : device;

    /// <summary>Locks the showing character where it is or unlocks it (from Companion › Character or the character's own
    /// menu), and saves that on this PC so a locked character shows in the same place.</summary>
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
            RememberProfileHere();
            NoticeCharacterChange(locked ? global::Martlet.Conversation.PhysicalKind.Locked : global::Martlet.Conversation.PhysicalKind.Unlocked);
            ErrorLog.Info(locked && place is { } at
                ? $"Character position locked at {at.Left:0}, {at.Top:0} ({at.Width:0} × {at.Height:0})" +
                    (at.Screen is { } screen ? $" on {CharacterScreen(screen)}." : ".")
                : "Character position unlocked.");
            ActionText.Text = (locked
                ? "Character position locked. Unlock it in Companion › Character or on the character's right-click menu to move it."
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
        } + (view.Locked == true ? " Position locked." : "") + (view.ClickThrough == true ? " Clicks pass through." : "");
    private async Task ResetCharacterPositionAsync()
    {
        try
        {
            var showing = avatar.IsShowing;
            var place = await avatar.ResetPositionAsync(lifetime.Token);
            var saved = CharacterPlacementStore.Save(store?.DataDirectory, place);
            if (closing) return;
            RememberProfileHere();
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
            var (saved, profile) = await SavedCharacterAsync();
            if (onlyIfAutoShow && saved?.AutoShow != true) return;
            await avatar.ShowAsync(profile with { ResourceRevision = null }, lifetime.Token);
            characterCleanupProblem = null;
            ActionText.Text = avatar.Status;
        }
        catch (Exception error) when (error is UnauthorizedAccessException or System.ComponentModel.Win32Exception or
            Martlet.Core.Contracts.ContractException or OperationCanceledException || RendererFailures.Is(error, lifetime.Token))
        {
            if (closing) return;
            ErrorLog.Warn("The character couldn't be shown.", error);
            ActionText.Text = $"Couldn't show the character: {error.Message}";
        }
        finally { UpdateCharacterButton(); }
    }

    /// <summary>The saved character profile (null when none is saved) and the character Martlet shows for it: that profile, or
    /// the bundled default when there is none.</summary>
    private async Task<(AvatarProfile? Saved, AvatarProfile Shown)> SavedCharacterAsync()
    {
        var loaded = await setupService!.LoadAsync(lifetime.Token);
        AvatarProfile? saved = null;
        if (loaded.Settings is { } settings)
            saved = (await new AvatarProfileStore(store!.DataDirectory).LoadAsync(settings.Profile.Id, lifetime.Token)).Profile;
        // Without a completed Setup profile the bundled character still shows; its choices are simply not saved.
        return (saved, saved ?? AvatarProfile.BuiltIn(loaded.Settings?.Profile.Id ?? Guid.NewGuid()));
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
        catch (Exception error) when (error is System.ComponentModel.Win32Exception or UnauthorizedAccessException ||
            RendererFailures.Is(error, CancellationToken.None))
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
        if (recovery is null || closing) return;
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

    private void Exit_Click(object sender, RoutedEventArgs e) => ExitMartlet();
}
