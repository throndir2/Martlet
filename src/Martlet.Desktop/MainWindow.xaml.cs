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
    private readonly DesktopServices services;
    private readonly SetupOperationRunner setupOperations;
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
    private readonly AvatarController avatar;
    private readonly SpeechCaptions captions;
    private readonly WindowsAudioSessionEvents audioSessionEvents;
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
        : this(startupError, DesktopServices.Create(store, support)) { }

    /// <summary>The window over <paramref name="services"/> (<see cref="DesktopServices.Create"/>): it wires them to each other and
    /// to its pages, and builds the live conversation, which needs the window's own callbacks.</summary>
    internal MainWindow(string? startupError, DesktopServices services)
    {
        InitializeComponent();
        this.services = services;
        var store = services.Store;
        this.store = store;
        // Who uses Martlet now (MainWindow.Accounts.cs), before anything loads that belongs to an account.
        accounts = services.Accounts;
        ThemeChoice.SelectedIndex = (int)((Application.Current as App)?.SelectedTheme ?? AppearanceTheme.Light);
        AppearanceStatus.Text = (Application.Current as App)?.AppearanceNotice
            ?? "Choose a palette; your other computers use the same choice.";
        InitializeUpdates();
        support = services.Support;
        setupOperations = services.SetupOperations;
        audioSetup = services.AudioSetup;
        audioSessionEvents = services.AudioSessionEvents;
        audioSessionEvents.LockedChanged += audioSetup.SetSessionLocked;
        var vault = services.Vault;
        setupService = services.SetupService;
        if (store is not null) store.Saved += settingsChanges.Written;
        companionService = services.CompanionService;
        memory = services.Memory;
        conversationHistory = services.ConversationHistory;
        if (conversationHistory is not null) ConversationsPage.Attach(conversationHistory);
        // Lorebooks are each account's own, in its folder (docs/ACCOUNTS.md).
        lorebooks = services.Lorebooks;
        smartHome = services.SmartHome;
        discord = services.Discord;
        // A Discord message naming the companion (a persona's name) is meant for Martlet.
        discord.CompanionNames = () => Martlet.Core.Speakers.CompanionNames.From(homeSettings?.Companion?.Personas.Select(p => p.Name)).Names;
        discordCalls = services.DiscordCalls;
        mcpTools = services.McpTools;
        mcpTools.Changed += ToolsChanged;
        smartHome.Attach(mcpTools);
        messaging = services.Messaging;
        messaging.Answer = AnswerMessageAsync;
        messaging.History = conversationHistory;
        messaging.Changed += () => Dispatcher.BeginInvoke(MessagingChanged);
        // Discord text exchanges join the record while it is kept; deletions and edits there go to Telegram and Discord too.
        discord.History = conversationHistory;
        discord.Recording = () => conversationHistory?.Active(homeSettings?.Memory) == true;
        conversationHistory?.Platforms.Start();
        voiceIdentity = services.VoiceIdentity;
        localVoices = services.LocalVoices;
        parakeet = services.Parakeet;
        // Discord calls transcribe on this PC or a paired computer and speak with a host's voice engine; never the cloud.
        discord.Speech = new DiscordSpeech(() => homeSettings?.Setup?.Routes, parakeet, store?.DataDirectory, () => conversation?.Replying == true);
        recovery = services.Recovery;
        avatar = services.Avatar;
        captions = services.Captions;
        captions.Changed += () => speechBubblesPage?.Show();
        avatar.Placement = CharacterPlacementStore.Load(store?.DataDirectory);
        avatar.ClickThrough = CharacterClickThroughStore.Load(store?.DataDirectory);
        avatar.VoiceMuted = !Talk.SpeakReplies;
        avatar.Requested += action => Dispatcher.InvokeAsync(() => CharacterRequested(action));
        avatar.Gaze.Decides = Talk.DecideGaze;
        characterActions = services.CharacterActions;
        characterTouchZones = services.CharacterTouchZones;
        characterEyes = services.CharacterEyes;
        characterTemperaments = services.CharacterTemperaments;
        characterReactionChanges = services.CharacterReactionChanges;
        // The character's usual gaze: your choice, else what the active persona's temperament decided.
        avatar.Gaze.Personality = () => characterTemperaments.For(homeSettings?.Companion?.ActivePersonaId)?.Gaze;
        avatar.Gaze.Configure(Talk.GazeUsual, Talk.GazeFree);
        characterThemes = services.CharacterThemes;
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
            // Body motions Martlet makes for VRM characters (body_motion) are kept as creations too, out of the creation tools.
            Martlet.Core.Creations.CreationRegistry.Shared.Register(Martlet.Conversation.MotionCreations.Kind);
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
            conversation.UserTurnStarted += words => Dispatcher.BeginInvoke(() => CheckInUserTurn(words));
            conversation.Spoke += answered => Dispatcher.BeginInvoke(() => CheckInSpoke(answered));
            // A reply found a model retired on its route: Home proposes the replacement at once.
            conversation.AbilityRecorded += ability => Dispatcher.BeginInvoke(() => { if (ability.Retired is not null) FollowRetiredAsync().Forget(); });
            discord.UseReplies(setupService, vault, conversation, memory, lorebooks);
        }
        WireCharacterActions();
        InitializeCharacterMotions();
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
            RenderWarmup();
            RenderPcCard();
            if (!started) return;
            PollPcModels();
            FollowCharacterActions();
            FollowCharacterTheme();
            TickCharacterEyes();
            FollowGreeting();
            RenderGreeting();
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
        InitializeConversationSync();
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
        InitializeVRoidHub();
        InitializeCreations();
        InitializeAppGuides();
        InitializeHomeShare();
        InitializeNodeAgent();
        InitializeLogs();
        InitializeBackground();
        InitializeTasks();
        InitializeThinkingRequests();
    }
}
