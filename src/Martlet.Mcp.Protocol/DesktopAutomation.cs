using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Automation;
using System.Windows.Automation.Text;

namespace Martlet.Mcp;

internal sealed class DesktopAutomation(bool allowEffects)
{
    private static readonly HashSet<string> SafeClicks = new(StringComparer.Ordinal)
    {
        // Martlet for Linux and macOS (Martlet.Companion) on a Windows dev run: its three tabs (passive navigation).
        "TalkTab", "SettingsTab", "ComputerTab",
        "OpenTroubleshooting", "OpenSetup", "OpenAudioSetup", "OpenLiveConversation",
        "OpenConfigurationRecovery", "RefreshDiagnostics",
        "SetupClose", "AudioClose", "CloseLive", "SupportClose",
        "RecoveryClose", "SupportFreeze", "SupportClear",
        "NavHome", "NavDevices", "NavCompanion", "NavCreations", "NavDiagnostics", "NavSettings", "TourSkip", "TourBegin", "TourBack", "DiagnosticsSection",
        // The welcome wizard: Look again only asks the local network which Martlet desktops answer (as Add a computer's Find
        // again does), Enter an address opens Add a computer, Next on the hardware step and the two preference cards only move
        // on and show the suggestion. Choosing a network saves the device role, Join asks the other computer, Use these
        // suggestions and Skip the key set up and install, Save key stores a key and Open build.nvidia.com opens the browser, so
        // those need --allow-ui-effects.
        "WizardScanAgain", "WizardJoinManual", "WizardSpecsNext", "WizardPreferLocal", "WizardPreferOnline",
        "OpenPeople", "OpenPrompts", "DeviceFactsSection", "DeviceReachSection", "DeviceRolesSection", "HealthRecheck", "LogsRefresh",
        // Devices' Map and List only switch how the devices show.
        "DevicesViewMap", "DevicesViewList",
        // The MCP directory's Close and its optional-settings section only close or expand; opening it, searching and Load more
        // send a request to the directory, and Install writes mcp.json and starts a server, so those need --allow-ui-effects.
        "McpDirectoryClose", "McpDirectoryOptional",
        // The talk window's Stop (Esc) only stops work (a reply, a recording, vision, a song); it starts nothing and never pauses
        // listening. Refresh context only forgets the exchanges kept in mind for the next reply; it sends nothing and stops
        // nothing. Stop singing only ends the song playing (musically). Nothing in the talk window plays a song. Its background
        // tasks chip (LiveTasks) and the task list's close button (LiveTasksClose) only open and close the list.
        "LiveStop", "LiveRefreshContext", "LiveSongStop", "LiveTasks", "LiveTasksClose",
        // Companion › Replies' Open Deep thinking only opens that page.
        "RepliesOpenDeepThinking",
        // Companion › Pictures' Check only asks the saved place whether it can draw now (a cloud provider: only whether a key is
        // there); Connect only reads the typed ComfyUI's status and models. Neither saves or draws. Draw a test picture, Set up
        // and the Draw with/Turn off buttons need --allow-ui-effects.
        "PicturesCheck", "PicturesComfyConnect",
        // A tool call's Deny in the talk window only declines the waiting call (an MCP tool or a terminal command); it runs
        // nothing. Allow once and Always allow run it, so they need --allow-ui-effects.
        "LiveToolDeny",
        // Add a computer: opening the wizard, moving between its two steps (Connect, Roles) and showing the address-and-code
        // fields or the SSH address only change what it shows; its Connect, Set up and role buttons do the work. The Devices map's Add a computer details open the same wizard
        // from their + (SelectedDeviceAdd) and their first choice card, and so does Home's Connect to your other computers on a
        // new PC (HomeConnectComputers).
        "AddComputer", "OpenHosts", "SelectedDeviceAdd", "NodeAction-AddComputer", "HomeConnectComputers", "HostsStepConnect",
        "HostsStepRoles", "HostsBack", "HostsNext", "HostsClose", "HostsEnterCode", "HostAddressSection", "DeviceIdSection",
        // The setup advisor (Home's Get a setup recommendation): opening it, moving between its steps, picking a goal and
        // closing it only change what it shows (the answers stay in memory); its plan's Install on this PC buttons do the work.
        "OpenSetupAdvisor", "AdvisorBack", "AdvisorNext", "AdvisorClose", "GoalBalanced", "GoalSmartest", "GoalFastest", "GoalPrivate",
        // The notification-area menu (ui_tray "menu"): Open Martlet only shows the window, Talk to Martlet opens the talk window
        // like OpenLiveConversation, Pause Martlet only stops work, Stop listening and Stop watching only stop listening or
        // watching, and End the conversation closes the talk window like CloseLive. Start listening, Start watching, Resume
        // Martlet, the character, the startup and closing choices and Exit need --allow-ui-effects.
        "TrayOpen", "TrayTalk", "TrayPause", "TrayStopListening", "TrayStopWatching", "TrayEndTalk",
        // The character overlay (drawn by Martlet's own renderer process, whose windows ui_snapshot includes): MoveAvatar only opens
        // or closes the character's right-click menu; its Talk to Martlet, Open Martlet and Character settings only show a window
        // or page, like TrayTalk and TrayOpen. Its zoom, position, Keep on top and Hide character items need --allow-ui-effects.
        "MoveAvatar", "CharacterTalk", "CharacterOpenMartlet", "CharacterSettings",
        // Martlet on your network: Find again only sends Martlet's own discovery query (port 9444) on the local network and
        // lists who answers; Stop asking only withdraws this PC's own request. Connect, Allow and Deny do the work.
        "NearbyFind", "NearbyCancel",
        // The host dashboard's Check again reads this PC's host service and, while the engine is unavailable, Windows' WSL
        // status, virtualization services and pending restart. It starts, sets up and pairs nothing.
        "CheckHostService",
        // Smart home: Find on my network only sends one multicast DNS question for Home Assistant's service type and lists who
        // answers; Not now only hides the setup form. Sign in, Set up, Connect, Disconnect, Add, Install and Restart do the work.
        "SmartHomeFind", "SmartHomeSetupCancel",
        // Companion › Discord: Step by step only expands the setup steps. The portal and invite buttons open the browser, and
        // Save, Forget, Reconnect, the on/off box and every choice change things, so they need --allow-ui-effects.
        "DiscordSetupSteps",
        // Messaging: Cancel only withdraws the pairing code shown (nothing is sent or saved). Connect, Pair a chat, Open BotFather,
        // Open in Telegram, Remove, Disconnect and the two check boxes do the work.
        "MessagingPairCancel",
        // Apps and API keys: Cancel closes the create dialog without making a key, and Done closes the dialog that showed a new
        // key once. Create API key, Create key, Copy (the clipboard) and Revoke change things, so they need --allow-ui-effects.
        "ApiKeyCreateCancel", "ApiKeyCreatedDone",
        // Personality, Character, Lorebooks and Memory open their windows (the character itself doesn't show), and Done/Close
        // closes them. Those windows save each change on their own as it is made (edits need --allow-ui-effects), so closing
        // never writes anything that wasn't already changed. The Character window's sections only expand.
        "OpenCompanion", "OpenAvatar", "OpenLorebooks", "OpenMemory", "CompanionClose", "AvatarClose", "LorebookClose", "MemoryClose",
        "AvatarAdvanced", "RemoteHostSection",
        // Memory: New only clears the fact editor (nothing is saved), and its Where memory is stored and Export to a file sections
        // only expand. Typing a search (MemorySearch) is ui_set_text; Add, Save changes and every Delete button change facts (Delete
        // asks first), so they need --allow-ui-effects.
        "MemoryNewFact", "MemoryStorageSection", "MemoryExportSection",
        // Companion › Memory's Open conversation history opens the record's window, Close closes it, and Search and Show all
        // only filter what it lists (from memory; nothing is written). Edit message only opens the editor with the selected
        // message and its Cancel closes it (nothing is saved until Save edit). Its two choices save conversation-history.json,
        // typing a search or an edit is ui_set_text, choosing an app, a conversation or a message is ui_select, and Save edit,
        // Delete message, Delete this conversation, Delete everything and Stop waiting changes write (deletes ask first; with
        // HistoryAlsoThere on they also queue changes for Telegram and Discord), so they need --allow-ui-effects.
        "OpenHistory", "HistoryClose", "HistorySearchRun", "HistoryShowAll", "HistoryEditMessage", "HistoryEditCancel",
        // The problem dialog's Close only closes it; its Open logs folder (Explorer) and every Copy button (the clipboard) need
        // --allow-ui-effects.
        "ProblemClose",
        // Outside access's "Set up sign-in first" only opens the sign-in window for that host; it changes nothing by itself.
        "OutsideAccessSetUpSignIn",
        // Add a character's Cancel only closes the dialog; Add a character, Use and Remove change things.
        "CharacterModelAddCancel",
        // Character profiles: Home's Manage profiles, the Character and Personality pages' Open profiles and the Profiles page's
        // links only open a Companion page; New profile... and a profile's Edit (CharacterProfileEdit-<key>) only open the form,
        // filled in, and Cancel closes it. Nothing is saved until Save; Use (here, Home's HomeCharacterProfile and the icon
        // menu's TrayCharacterProfile-<key>) and Remove change things, so they need --allow-ui-effects.
        "HomeManageCharacters", "OpenProfiles", "ProfilesOpenCharacter", "ProfilesOpenVoice", "ProfilesOpenPersonality",
        "CharacterProfileNew", "CharacterProfileCancel",
        // Add a voice: Add another recording only adds an empty recording row to the dialog (F5AddVoiceDrop-n removes row n);
        // nothing is read or saved until Add voice. Opening the dialog (F5AddVoice), typing, Fill in the words (F5AddVoiceFill
        // runs speech-to-text, which may send the recording to the Listening host) and Add voice need --allow-ui-effects.
        "F5AddVoiceMore",
        // Background tasks (NavTasks): a run window's Hide only hides it while its task keeps running (and closes it once the
        // task has finished), and Clear finished only drops finished tasks' kept output from the list; neither stops, sends or
        // saves anything. Cancel task (HostRunCancel) and a task's Cancel... (TaskCancel-<id>) ask first and then stop the task,
        // so they need --allow-ui-effects.
        "NavTasks", "HostRunHide", "TasksClear",
        // Companion › Discord › Friends and calls: What Discord allows only expands. Approve, Decline, Call, Remove, Add, the
        // may-call boxes and Update picture now change things or contact Discord, so they need --allow-ui-effects.
        "DiscordFriendsAbout",
        // Sign-in from outside: Add a computer's Join with an invite and a paired host's Sign-in from outside only open their
        // windows (the settings window reads the host's sign-in settings, never a secret), and Close closes them. Connect
        // contacts the host named in a pasted invite, Sign in pairs, and the settings window's Make an authenticator secret,
        // Save, recovery codes, Remove, Allow and Make invite change or reveal things, so they need --allow-ui-effects.
        "HostsJoinWithInvite", "SignInJoinClose", "HostSignInSettings", "SignInSettingsClose",
        // Companion › Discord › Martlet in your Discord calls › Check this PC only reads: it lists the playback devices' names,
        // looks for Discord's process and sets up a process loopback and closes it unstarted (nothing is recorded or played).
        // The mode's checkboxes, choices and Open camera view change things, so they need --allow-ui-effects.
        "DiscordCallCheck"
    };
    /// <summary>Choosing a Companion page in its side list only shows that page; Devices map nodes ("Node-this-pc",
    /// "Node-host:gpu-1") and the problem card's Show buttons only select a device and show its details; a job's
    /// "Where it runs" options ("Place-Voice-Computer") only show that place's choices, which their own buttons commit, and
    /// Voice engine's computer pills ("SpeakingHost-gpu-pc") and Singing's ("SingingHost-this-pc") only show that computer's engines. Home's
    /// Health tiles ("HealthCheck-thinking") and its passive fixes ("HealthOpen-voice-setup-open-voice", "HealthOpen-crash-dismiss")
    /// only open the page where something changes, or hide the item. Diagnostics' filters ("LogLevel-errors", "LogSource-all",
    /// "LogPart-gateway") only filter the shown lines, and selecting a line ("LogEntry-0") only shows it in full. An MCP directory
    /// result ("McpDirectoryResult-io.github.upstash/context7") only shows that server's details.</summary>
    private static readonly string[] SafeClickPrefixes = ["CompanionTab-", "Node-", "CoverageShow-", "Place-", "SpeakingHost-", "SingingHost-", "HealthCheck-", "HealthOpen-",
        "LogLevel-", "LogSource-", "LogPart-", "LogEntry-", "McpDirectoryResult-", "F5AddVoiceDrop-",
        // Devices' list filters ("DeviceFilter-attention") only filter the cards shown.
        "DeviceFilter-",
        // A background job's Cancel in the talk window ("LiveJobCancel-think-1") only stops that job: it sends, saves and starts
        // nothing (the next thing you say tells Martlet you stopped it).
        "LiveJobCancel-",
        // Companion › Profiles: a profile's Edit ("CharacterProfileEdit-3f2a9c1b") only opens the form; Save writes.
        "CharacterProfileEdit-",
        // A finished task's Show result in the talk window's task list ("LiveJobResultToggle-think-1") only shows or hides
        // what it found (LiveJobResult-<id>, which isn't a readable value).
        "LiveJobResultToggle-",
        // Companion › Deep thinking's "Where it thinks" options ("DeepPlace-Computer", "DeepPlace-Off") only show that place's
        // card; its own Use and Turn off buttons commit (and need --allow-ui-effects).
        "DeepPlace-",
        // Companion › Pictures' "Where it draws" options ("PicturesPlace-Host", "PicturesPlace-ComfyUi") and its computer pills
        // ("PicturesHost-this-pc") only show that place's card; its own buttons commit.
        "PicturesPlace-", "PicturesHost-",
        // Companion › Reading's "Where it reads" options ("ReadingPlace-ThisPc", "ReadingPlace-Host") and its computer pills
        // ("ReadingHost-this-pc") only show that place's card; its own buttons commit.
        "ReadingPlace-", "ReadingHost-",
        // People's "What Martlet remembers about them" ("PeopleMemories-3") only opens Memory showing that voice's facts.
        "PeopleMemories-",
        // Creations: choosing a creation in the list ("Creation-3f2a9c1b7d04", its short id) only shows its text and details.
        // There is no Play, Show or Activate; its Rename and Delete change it on every computer, so they need --allow-ui-effects.
        "Creation-",
        // Background tasks: a task's Show or Show output ("TaskShow-3") only shows its run window again, or a finished task's
        // kept output.
        "TaskShow-"];
    // Read-only status text. Text blocks and buttons have no value, so their accessible name (a text block's text) is returned.
    private static readonly HashSet<string> SafeValues = new(StringComparer.Ordinal)
    {
        // Martlet.Companion: its status line, headless status JSON (no secrets), guardrail refusals, engines not offered here and
        // their reasons, local-model warnings, the key-storage note and the chosen engines.
        "StatusLine", "CharacterState", "CompanionStatus", "Refusals", "NotOffered", "ThinkingWarnings", "KeyNote", "ThinkingEngine", "ListeningEngine",
        "SpeakingEngine",
        "FoundationStatus", "PipelineStatus", "LocalAudioStatus",
        // Settings › Tools: this PC's processor type and whether Martlet runs under x64 emulation (Windows on Arm), with what
        // that means. Fixed wording.
        "ThisPcArchitecture",
        // Companion › Discord › Friends and calls: how many friends and waiting requests, the call now and the last call's
        // outcome; the bot's Discord status ("Discord status: Online, "Hanging out"."); when its picture last changed and why
        // it didn't; and what the last action on the card did. Counts, names and fixed wording; never a token.
        "DiscordFriendsStatus", "DiscordPresenceStatus", "DiscordAvatarStatus", "DiscordFriendsResult",
        // Companion › Discord's text-chat line: counts of messages seen, considered, answered, passed, dropped and failed, the
        // last reply's place kind (DM or server) and the last problem; never message text, names or IDs.
        "DiscordTextStatus",
        // Sign-in from outside: the join window's status line and the host it checked ("home-host at name:port, key checked"),
        // and the settings window's status, owner account state (name and recovery codes left), allowed identities, providers
        // and computers that signed in (device IDs, provider and subject; never a password, secret or recovery code).
        "SignInJoinStatus", "SignInHost", "SignInSettingsStatus", "SignInOwnerState", "SignInAllowedList", "SignInProvidersList",
        "SignInEnrolledList", "SignInRefusedList", "SignInRemovedList", "SignInOutsideWarning",
        // Companion › Discord's voice line: where Martlet is in Discord voice, counts of speakers heard, utterances transcribed
        // and replies spoken (never what was said), whether DAVE is on, whether libdave loaded, and the last problem.
        "DiscordVoiceStatus",
        "LiveStatus", "LiveMic", "LiveVision", "LiveVisionStatus", "LiveContext", "AudioResult", "SetupActivity", "RecoveryResult", "SupportResult",
        // Home's Start talking reads "Show conversation" while a conversation runs (the talk window open, or hidden while Martlet
        // listens or watches); Home's Start listening / Stop listening button and its listening indicator ("Listening. Just start
        // talking.", "Hearing you…", "Not listening" or why Martlet can't listen), and its Start watching / Stop watching button
        // and watching indicator ("Watching your active window.", "Taking a look…", "Not watching" or why Martlet can't see).
        "OpenLiveConversation", "HomeListen", "HomeListeningStatus", "HomeWatch", "HomeWatchingStatus",
        "PeopleStatus", "PeopleSyncStatus", "PeopleVoiceCount", "ListenParakeetStatus", "SetupCharacterView", "SetupCharacterSpeechDisplay",
        // Where the character's speech bubble goes: following the character or in one place, and its pixel offsets.
        "SetupCharacterBubblePlacement", "SetupCharacterBubbleOffsetX", "SetupCharacterBubbleOffsetY",
        "SetupCharacterNow", "SetupCharacterNowProblem",
        // Companion › Profiles: how many profiles there are and whether one is in use ("2 profiles. One of them is in use."),
        // and the profile form's problem ("Give the profile a name."). Never a profile's name.
        "CharacterProfilesStatus", "CharacterProfileEditorProblem",
        // Whether the character's position is locked and where (Companion › Character, in device-independent pixels), and the
        // lock buttons' labels, which carry the state: Home's ToggleCharacterLock ("Lock character position" / "Unlock
        // character position"), Companion's SetupCharacterLock ("Lock position" / "Unlock position") and the overlay menu's
        // CharacterLockPosition ("Lock position" / "Unlock position"). Clicking any of them saves
        // character-placement.json, so it needs --allow-ui-effects.
        "SetupCharacterPlacement", "ToggleCharacterLock", "SetupCharacterLock", "CharacterLockPosition",
        // The overlay menu's CharacterMuteVoice, whose label carries whether Martlet's voice is muted ("Mute voice" / "Unmute
        // voice"). Clicking it saves talk-preferences.json (Speak Martlet's replies aloud), so it needs --allow-ui-effects.
        "CharacterMuteVoice",
        // Companion › Voice › Voice volume: the slider's number (0 to 100) and its label ("80%"). ui_set_range on VoiceVolume
        // saves talk-preferences.json, so it needs --allow-ui-effects.
        "VoiceVolume", "VoiceVolumeLevel",
        // What the showing character's model drives (controls, textures and any downscaling, blink and mouth parameters,
        // motions, physics; parameter IDs only, never paths), on Companion › Character and in the character window, which
        // also shows why a chosen model couldn't load; and the character window's status line.
        "SetupCharacterModel", "AvatarModelInfo",
        // The character overlay's drag surface reads as its last tap's hit test (zone, hit areas, drawables, bone; model-authored
        // names only, never paths); character_touch taps it.
        "MoveAvatar",
        "LipSyncNow", "LipSyncNowProblem", "LipSyncOwnTitle", "LipSyncOwnState", "LipSyncDockerTitle", "LipSyncDockerAbout", "LipSyncLoudnessTitle",
        // The selected device, its status and, when that status is a button ("Update available"), what clicking it does
        // ("Update available: Update to Martlet 0.40.0"). Clicking SelectedDeviceHealthAction updates the host, so it needs
        // --allow-ui-effects.
        "SelectedDevice", "SelectedDeviceHealth", "SelectedDeviceHealthAction", "ClusterStatus",
        // Devices: how many devices and how many need attention ("53 devices, 2 need attention. Select one to see details.") and,
        // in the list, how many it shows ("Showing 12 of 53 devices." or "No device matches \"gpu\".").
        "DevicesSummary", "DeviceListStatus",
        // Devices' resource view (MainWindow.DeviceCapacity.cs): the selected device's hardware line ("NVIDIA GeForce RTX 5090
        // (32 GB) · 64 GB memory · 32 processor threads"), what it has left ("Left free: 15 GB graphics memory, ...") and the
        // network card's lines: what runs where ("On your computers: Thinking (gpu-box). Online: ... Not set up: ..."), the
        // totals and what else fits ("Your computers could also run 2 more Deep thinking models (Gemma 4 12B)."). Hardware
        // and Martlet's own estimates only.
        "DeviceSpecs", "DeviceHeadroom", "CapacityCoverage", "CapacityTotals", "CapacityFits",
        // Settings for all devices: whether Martlet's settings are the same on the paired hosts (how many, when last checked, what
        // was last taken from another computer) and the settings this PC can't follow yet with why (never values or keys). Its
        // SettingsSyncClaim button makes every computer use this PC's settings, so it needs --allow-ui-effects. MemorySyncStatus:
        // how many facts Martlet remembers, on how many hosts they are the same, when checked and how many were taken from or
        // forgotten on other computers (never a fact). MemoryFactStatus (the Memory window): how many facts it remembers, how
        // many belong to people Martlet knows by voice or to forgotten voices, how many the Show choice lists, and what the
        // last action did (never a fact or a name).
        "SettingsSyncStatus", "SettingsSyncWaiting", "MemorySyncStatus", "MemoryFactStatus",
        // Companion › Memory › Conversation history: whether Martlet keeps a record and may search it, and what the record holds
        // (conversations, exchanges, since when, per app); the history window's status line (counts, or what a search found) and
        // its line on changes waiting for Telegram and Discord (counts, apps and the last problem). Never what was said: the
        // window's lists (HistoryConversations, HistoryMessages, whose items are named "Conversation 2 (Telegram)" and
        // "Message 3: Martlet · Discord") and the editor are not readable values.
        "HistoryStatus", "HistoryWindowStatus", "HistoryPlatformStatus",
        // The selected paired host's Martlet release as this PC knows it (from its checks and the release it announces on each
        // network sync: "0.22.0, up to date", "Needs update from 0.21.0 to 0.22.0") and what this PC last did to update it; and,
        // for a Windows computer whose voice engine shares its graphics card with other roles, the warning that it can fall
        // behind (SelectedDeviceSharedGpu, fixed wording with role and engine names).
        "SelectedDeviceRelease", "SelectedDeviceUpdate", "SelectedDeviceSharedGpu",
        // A managed host's outside access ("2 outside addresses; pairing codes from outside home refused; every connection
        // treated as outside home."): counts and choices only, never the addresses.
        "SelectedDeviceOutside",
        // The Outside access dialog's note when the host needs sign-in first ("Outside access paused: sign-in is off. ... (signin.not_set_up)"):
        // the reason code is the outsideAccessBlockedReason. Its "Set up sign-in first" button (OutsideAccessSetUpSignIn) only opens
        // the sign-in window, which changes nothing until its own buttons are used.
        "OutsideAccessBlockedReason",
        "VisionStatus", "VisionDisclosure", "TalkHearVoiceStatus", "SetupCloudHint-Thinking", "SetupLocalRecommendation", "SetupProviderHint", "F5VoicesStatus",
        // Companion › Vision's Now line: whether vision is on (the default) and what Martlet looks at (your whole screen by
        // default, your active window, or a camera's name or host without its path or password) and how often it comments.
        // The VisionSource-<kind> choices are radio buttons (ui_snapshot's selected) and VisionToggle's label says what it does
        // (clicking it saves talk-preferences.json, so it needs --allow-ui-effects).
        "VisionNow", "VisionToggle",
        // Companion › Listening › Let Thinking hear my voice: which applies (you turned it on or off, or never chosen: on while the
        // recording stays on this PC, off until you tick it when it would leave). Fixed wording; no model names beyond the
        // Thinking destination the page already shows.
        "TalkHearVoiceChoice",
        // Companion › Vision › Where the character looks (its VisionGaze-Mouse and VisionGaze-Martlet choices save
        // talk-preferences.json, so they need --allow-ui-effects): what the character's eyes follow and why; and the talk window's
        // line on it while Martlet decides (what it looks at now and the last time it looked away; never what is on screen).
        "VisionGazeStatus", "LiveGaze",
        // Companion › Vision › Screen summary over time (VisionScreenSummary is a check box; changing it saves
        // talk-preferences.json, so it needs --allow-ui-effects) and whether it runs or why not; the talk window's line on it
        // while Martlet watches: pictures kept, the last summary's age and time taken. Its help text is the last summary
        // (one or two lines on what changed on the screen, never a window title on its own).
        "VisionScreenSummary", "VisionScreenSummaryStatus", "LiveScreenSummary",
        // Companion › Listening › Hear how you say it: what Test hearing does (and whether it stays on this PC) or what the last test
        // found (the model's one-word answer, never anything said). Clicking TalkHearVoiceTest sends the Thinking model a test
        // recording (a provider request), so it needs --allow-ui-effects and a model on this PC.
        "TalkHearVoiceTestStatus",
        // Companion › Listening › When Thinking can hear you (shown while Thinking hears your voice): which way your voice goes
        // (straight, or transcribed first) and what that means. Fixed text. TalkVoicePathStraight and TalkVoicePathTranscribeFirst
        // are radio buttons (ui_snapshot's selected); choosing one saves talk-preferences.json, so it needs --allow-ui-effects.
        "TalkVoicePathStatus",
        // Companion › Thinking › This PC: the suggested local model picked from SetupLocalModelPicks (its size, the card it fits,
        // whether it hears your voice or gets the transcript, and whether it's the fastest or the smartest that fits; choosing
        // one with ui_select only fills SetupLocalModel, the model name, so it needs --allow-ui-effects but saves nothing).
        "SetupLocalModelPicks", "SetupLocalModel",
        // The setup advisor: which step it shows and its plan's summary (the goal's one-line explanation).
        "AdvisorStep", "AdvisorSummary",
        // Companion › Voice › Voices: whether the voice list is shared with the paired Martlet computers, with how many and when,
        // and why Add a voice couldn't add a recording (never the typed name, transcript or file path); Add a voice's line on
        // its recordings (how many, how long joined, or which one Martlet can't use; never paths or words), under each
        // recording's file, what Martlet found in it (its kind, length and whether Martlet converts it) or why it can't be used
        // ("F5AddVoiceRecording", then "F5AddVoiceRecording-2" and so on in SafeValuePrefixes), and its intro, which names the
        // speech-to-text that fills in the words (or how to get one). Each recording's F5AddVoiceHeard line reads through the
        // prefix below.
        // Companion › Voice › Singing: the role on the shown computer (title with its badge, chips, where it stands: not set up,
        // setting up, ready with the voice matches set up there, failed with the reason, or why that computer can't sing), the
        // Set up button's label, whether it needs a graphics card of its own (SingingGpu, fixed text) and the saved quality and
        // voice match; with VevoSing chosen where it isn't set up, that it isn't (or is being added) and the Add VevoSing there
        // button's label. Set up and Add VevoSing there need --allow-ui-effects. Songs are only performed in conversation
        // (singing_check exercises them headlessly).
        "SingingEngine", "SingingFeatures", "SingingState", "SingingSetUp", "SingingGpu", "SingingQuality", "SingingVoiceMatch",
        "SingingVoiceMatchState", "SingingSetUpVevo",
        // Companion › Pictures: where Martlet draws now (PicturesNow), what Check or Draw a test picture found (PicturesTestState:
        // ready, why not, or the test picture's size, place and seconds), the Pictures role on the shown computer (title, chips,
        // where it stands, the Set up button), the ComfyUI address and what Connect found (version, checkpoints, whether
        // Z-Image Turbo is there), the chosen workflow, a cloud provider's model ID and whether a key is saved or Thinking's is
        // used (never the key), and the buttons' labels. Pictures themselves are never returned.
        "PicturesNow", "PicturesTestState", "PicturesEngine", "PicturesFeatures", "PicturesHostState", "PicturesSetUp", "PicturesUseHost",
        "PicturesComfyAddress", "PicturesComfyState", "PicturesComfyConnect", "PicturesWorkflow", "PicturesLoadWorkflow", "PicturesUseComfy",
        "PicturesModel", "PicturesKeyStatus", "PicturesUseCloud", "PicturesTurnOff", "PicturesCheck", "PicturesTest",
        // Companion › Reading: where Martlet reads the text on the screen (ReadingNow), the newest read while watching and the
        // Read my screen now result (ReadingLast, ReadingTestState: how many lines, which engine, milliseconds and when, or why
        // it couldn't), whether Windows can read text here, the Reading role on the shown computer (title, chips, where it
        // stands) and the buttons' labels. The text read from a real screen (ReadingTestText) is never returned.
        "ReadingNow", "ReadingLast", "ReadingTestState", "ReadingWindowsState", "ReadingEngine", "ReadingFeatures", "ReadingHostState",
        "ReadingSetUp", "ReadingUseHost", "ReadingUseThisPc", "ReadingTurnOff", "ReadingTest",
        "F5VoicesShared", "F5AddVoiceProblem", "F5AddVoiceRecordings", "F5AddVoiceRecording", "F5AddVoiceAbout",
        // Companion › Character › Your characters: how many characters of the owner's own and what this PC shows (never a
        // name), whether they are shared with the paired Martlet computers (with how many and when), and why Add a character
        // couldn't add a model (never the typed name or file path).
        "CharacterModelsStatus", "CharacterModelsShared", "CharacterModelAddProblem",
        // Companion › Character › Emotes and motions: how many the shown model has and who named them, the Thinking model's
        // naming progress, the tags offered to replies and what follows the voice's cues, the last one played (model-authored
        // names only) and whether edits saved. Each row's name and kind (CharacterActionName-<n>, a model-authored name), and
        // its Try button's label (CharacterActionTry-<n>: "Try", or "Turn off" while that lingering emote is on). The lingering
        // emotes on now and for how long (CharacterActionsHeld: "On now: Glasses (12 min)." or "No lingering emotes are on.").
        // Each row's "Stays on" check box (CharacterActionMode-<n>) reports its mode as checkedState; changing it saves, and Try,
        // Turn off, Clear emotes (CharacterActionsClear) and the overlay menu's Clear emotes (CharacterClearEmotes) change what
        // the character shows, so they need --allow-ui-effects.
        "CharacterActionsStatus", "CharacterActionsNaming", "CharacterActionsOffered", "CharacterActionsLast", "CharacterActionsSaveState",
        "CharacterActionsHeld",
        // Companion › Character › Touch zones: how many zones the shown model has, how many are in use and who found them, whether
        // the Thinking model can see (and where pictures go), how Detect zones went, which zone the last touch landed in and what
        // it played, and whether edits saved. Each zone's line (TouchZoneState-<n>: its ID, parts it follows and default reaction).
        // Detect zones sends the character's picture to Thinking, Try plays on the character and the rest save, so those need
        // --allow-ui-effects.
        "TouchZonesStatus", "TouchZonesVision", "TouchZonesDetection", "TouchZonesLast", "TouchZonesSaveState",
        // Companion › Character › Touch temperament: who decided the active persona's temperament (built-in, the Thinking model,
        // FIXTURE - NOT AI or the owner), its attitude per group and part ("head loves, torso hates, ..."), how deciding went and
        // whether edits saved. Each line's attitude (TouchTemperamentAttitude-<group or zone ID>, below) is an attitude word.
        // Re-decide from personality sends the personality to Thinking, and the rest save, so they need --allow-ui-effects.
        "TouchTemperamentStatus", "TouchTemperamentSummary", "TouchTemperamentDecision", "TouchTemperamentSaveState",
        // What Martlet noticed (zones with Martlet notices on) that waits for a reply and when a touch reply would start, and
        // which reply took the last touches and what the Thinking model was told (zone names and the touch line, no words).
        "TouchZonesNoticed", "TouchZonesNoticedLast",
        // Touch zones' line on the last stroke across the locked character (zones crossed, pace, passes, seconds, samples on the
        // character) or the last move, zoom, pan, lock, hide or show, as Martlet's touch ledger heard it. Fixed wording and zone
        // names only.
        "CharacterPhysicalLast",
        // Companion › Listening › Speakers and echo: whether echo reduction is on and how the last listen went (or why it couldn't
        // run). The TalkReduceEcho check box saves the choice, so it needs --allow-ui-effects.
        "TalkReduceEchoStatus",
        // Companion › Listening › How you talk: what talking over Martlet takes (real words; never a hum, a cough, laughter, a
        // quick "yeah" or what this PC plays). Fixed text. Word check: the chosen option (Relaxed, Normal or Sensitive; choosing
        // one with ui_select saves talk-preferences.json, so it needs --allow-ui-effects) and its fixed explanation.
        "TalkBargeInAbout", "TalkWordCheck", "TalkWordCheckAbout",
        // Companion › Listening › How you talk › Judge when I finish talking: on or off, which end-of-turn judge runs and whether
        // it can (or why not), how long it took to load, and counts of the newest decisions with the judge's median time and the
        // last one's outcome and silence (never words or audio). The TalkJudgeTurns check box saves the choice, so it needs
        // --allow-ui-effects.
        "TalkJudgeTurnsStatus",
        // Companion › Listening › How you talk › Start replies early: on or off, whether replies can start early with this setup
        // (Parakeet on this PC, a Thinking model on the user's own computers or Also for cloud models, whether the voice is
        // prepared too) and what became of the newest ones (taken as the reply, let go because you went on talking, let go for
        // another reason; the last one's outcome and wait), never words or audio; and its fixed explanation. The TalkEarlyReplies,
        // TalkEarlyRepliesCloud and TalkEarlyVoice check boxes save the choice, so they need --allow-ui-effects.
        "TalkEarlyRepliesStatus", "TalkEarlyRepliesAbout",
        // Companion › Listening › When you talk over Martlet: the chosen option (Pause and decide or Stop at once; choosing one
        // with ui_select saves talk-preferences.json, so it needs --allow-ui-effects) and its fixed explanation. In the talk
        // window, LiveBargeIn: the last time you talked over Martlet, whether it paused, stopped or played on, the verdict, what
        // decided it and how long the judge and the pause took (never what was said).
        "TalkBargeInBehavior", "TalkBargeInBehaviorAbout", "LiveBargeIn",
        // Companion › Listening › Watch along: whether Martlet also hears what this PC plays and whether its own voice is left
        // out (TalkHearPc saves the choice, so it needs --allow-ui-effects); and the talk window's line on it (hearing the PC
        // now, or why it can't). Never what was heard. Describe PC sounds (TalkDescribePcSounds saves the choice, so it needs
        // --allow-ui-effects) and its status: on or off, the active judge (a Thinking pool model or the CPU sound tagger), and the
        // last line with its age, judge and milliseconds (a short description of the PC's non-speech sound, never a transcript).
        "TalkHearPcStatus", "LivePcAudio", "TalkDescribePcSoundsStatus",
        // Companion › Discord › Martlet in your Discord calls: the mode's line (on or off, whether Martlet hears the Discord
        // app alone or everything but itself, who-is-talking source and the output its voice goes to), the who-is-talking line
        // (its source and how many people were named, never who), the output line (the device's name), the camera view's line
        // (open or closed, its background), the camera framing line (the character's size and how far it is moved from the
        // middle; its Bigger, Smaller, Left, Right, Up, Down and Reset framing buttons save discord-calls.json, so they need
        // --allow-ui-effects), Check this PC's result, and the What to hear, Voice output and camera background
        // choices (choosing one with ui_select saves discord-calls.json, so it needs --allow-ui-effects), and the camera picture's
        // line (where the saved picture came from, drawing, or why it couldn't be used; never a title or what was asked for).
        // Never the owner's Discord name or anything heard or seen.
        "DiscordCallStatus", "DiscordCallAttribution", "DiscordCallOutputStatus", "DiscordCallCameraStatus", "DiscordCallCameraFraming",
        "DiscordCallDoctor",
        "DiscordCallCapture", "DiscordCallOutput", "DiscordCallCameraBackground", "DiscordCallCameraPictureStatus",
        // What the talk window's newest reply, report or look took together (One moment: your words, lines this PC played, the
        // picture and what wanted your attention, finished background work), counts only, never what was said, seen or found.
        "LiveTurnInputs",
        // Companion › Vision › How often it comments and the same choice under Listening › Watch along: the chosen option
        // (Quiet, Normal, Chatty or Martlet decides; choosing one with ui_select saves talk-preferences.json, so it needs
        // --allow-ui-effects) and what it means (with Martlet decides, the level Martlet picked while a conversation runs); and
        // the talk window's line while Martlet decides and vision is on or it hears this PC (the level it picked and since when).
        "VisionChattiness", "VisionChattinessStatus", "TalkPcChattiness", "TalkPcChattinessStatus", "LiveChattiness",
        // Companion › Voice › Voice engine: the voice engines the speaking computer still runs besides the one that speaks
        // (SpeakingEngineOthers; its SpeakingEngineRelease button stops them, so it needs --allow-ui-effects), that the shown
        // Windows computer's graphics card also does other jobs, so its voice can fall behind (SpeakingEngineSharedGpu) and,
        // under Another of your computers, that the shown computer isn't reachable (SpeakingHostStatus). Each engine row reads
        // through the VoiceEngine prefix below.
        "SpeakingEngineOthers", "SpeakingEngineSharedGpu", "SpeakingHostStatus",
        "SetupOllamaStatus", "SetupLocalModelTest", "HostRunStatus", "RepliesNow", "AppUpdateStatus", "AppCurrentVersion",
        // Settings › App updates: this PC's own host service following the app's version (shown only when this PC runs one):
        // current, being updated in the background, busy (and when Martlet tries again), stopped, not running, or why the
        // update stopped. Versions and fixed text only.
        "OwnHostUpdateStatus",
        // Companion › Replies › Context size: the size replies use, where it comes from (the setting, Martlet's default, the
        // model's limit, the host's default or Ollama's context length) and what Martlet knows of the model's own limit. Its
        // Check model limit button (RepliesCheckContext) asks the Thinking model's server, so it needs --allow-ui-effects.
        "RepliesContextStatus",
        // Companion › Replies › Thinking steps: the chosen option (Default, Off or On; choosing one with ui_select saves it, so
        // it needs --allow-ui-effects) and how the Thinking route takes it.
        "RepliesThinking", "RepliesThinkingStatus",
        // Companion › Deep thinking: where a think goes and whether it can run there alongside the conversation (and why); Thinking
        // longer's state (on by default; Where it thinks › Off turns it off) or what keeps it from working, and the chosen
        // effort, time limit, hourly limit and when it shares results (choosing one with ui_select saves them, so they need
        // --allow-ui-effects); what Ollama on this PC has downloaded, whether the model typed for it fits beside Thinking's on the
        // graphics card, why Same as Thinking can or can't think here, and what an endpoint's key field will do (never a key or
        // base URL typed). Each paired computer's line reads through DeepThinkingHost- below. In the talk window, the
        // background tasks chip (LiveTasks: "Background tasks: 1 running · 1 ready") and the task list's line under its title
        // (that tasks keep going while you talk and when finished work comes up; never what a task is about: LiveJob-<id> holds
        // that, and each task's status reads through LiveJobState- below). The song panel's line (the song's id, state,
        // position, line number and section, lead-in, vamps, ducking, or
        // where and why it stopped; never its title or words: LiveSongLine holds those).
        "DeepThinkingNow", "DeepThinkingParallel", "ThinkLongerStatus", "ThinkLongerEffort",
        "ThinkLongerDelivery", "DeepThinkingHosts", "DeepThinkingLocalStatus", "DeepThinkingLocalFit", "DeepThinkingLocalShare", "DeepThinkingSameStatus",
        "DeepThinkingKeyStatus", "DeepThinkingPoolStatus", "LiveTasks", "LiveJobs", "LiveSong",
        // Companion › Thinking pool › Pool members: the member count and usable slots, the guidance ("1 slot: long thinking can
        // delay screen and sound summaries; add a second slot for the full experience."), the likely-slowdown warnings (a member
        // beside the conversation's Thinking model or the voice), and the Use the conversation model when the pool is empty box
        // (ticking it saves thinking-pool.json, so it needs --allow-ui-effects). Each member's line reads through
        // ThinkingPoolMember- below.
        "ThinkingPoolSummary", "ThinkingPoolGuidance", "ThinkingPoolWarnings", "ThinkingPoolUseConversationModel",
        // Companion › Deep thinking › Web research (off by default): whether Martlet may search the web when asked and why it
        // can't yet, and its fixed disclosure of what leaves this PC. The WebResearchOn check box saves the reply settings, so it
        // needs --allow-ui-effects.
        "WebResearchStatus", "WebResearchDisclosure",
        // Companion › Prompts: how many internal prompts are edited or emptied, and the estimated tokens of all prompts together
        // as typed (counts only, never the prompt text).
        "PromptsNow", "PromptsTokens",
        // Editors that save on their own (no Save button): whether every change is saved ("All changes saved.", "Saving...",
        // "Not saved yet: <why>"), in Personality (the Companion window), the Character window and Lorebooks; and the Character
        // window's character status ("Character is showing...", "Character hidden. Voice continues.").
        "CompanionSaveState", "AvatarSaveState", "LorebookSaveState", "AvatarStatus",
        // Personality › Where the voice pauses: how short an ending is said with the words before it ("Up to 2 words"). Its
        // check boxes report their state as checkedState; changing any of them saves, so it needs --allow-ui-effects.
        "CompanionShortEnding",
        // Companion › Thinking › If Thinking fails: the saved fallback in words (provider, model, whose key; never the key) and
        // what its key field will do.
        "FallbackNow", "FallbackKeyStatus",
        // Companion › Thinking, Voice and Listening: the job's Now line (where it runs and the model, as "Ollama on this PC:
        // gemma4:12b") and, under A cloud provider, what the key field will do: keep the saved key, use again a key set aside
        // when the job left that provider, or ask for one (never the key). Its Use button (SetupCloudSave-<page>) and
        // SetupUseLocalThinking save the route, so they need --allow-ui-effects.
        "SetupJobNow-Thinking", "SetupJobNow-Voice", "SetupJobNow-Listening",
        // The job's line about your Martlet network: the host your computers use for it and why this PC hasn't switched yet,
        // or that your other computers use this PC for it (host IDs and reasons only).
        "SetupJobNetwork-Thinking", "SetupJobNetwork-Voice", "SetupJobNetwork-Listening",
        "SetupCloudKeyStatus-Thinking", "SetupCloudKeyStatus-Voice", "SetupCloudKeyStatus-Listening",
        "StageTitle", "StageText", "HealthTitle", "HealthSummary", "HealthAllClear",
        "LogSummary", "LogShareStatus", "LogDetail",
        "HostStatus", "PairedHost", "PairCodeHelp", "DockerState", "RolesSummaryText", "HostRunPairAddress", "NetworkStatus",
        // A run window's pairing panel: the note on how long the code works (fixed text) and its Copy code button's label
        // ("Copy code", then "Copied" or "Couldn't copy"); never the code (HostRunPairCode). Clicking HostRunPairCopy puts the
        // code on the clipboard, so it needs --allow-ui-effects.
        "HostRunPairNote", "HostRunPairCopy",
        "NearbyStatus", "NearbyNumber", "NearbyShareStatus", "JoinRequestTitle", "JoinRequestText", "JoinRequestNumber", "JoinRequestExpiry",
        // The MCP directory's status line and the selected server's public directory facts (never what was typed into its fields).
        "McpDirectoryStatus", "McpDirectoryNoSelection", "McpDirectoryDetailTitle", "McpDirectoryDetailName", "McpDirectorySummary",
        "McpDirectoryNeeds", "McpDirectoryInstalled", "McpDirectoryCantInstall",
        // Settings › Your other computers (whether Martlet here runs commands your other computers send, and what it last did)
        // and a paired host's How Martlet reaches it (the saved route in words, and what each route means).
        "NodeAgentStatus", "HostReachNow", "HostReachHint",
        // Companion › Smart home: the connection in words (address, name, version, whether the other computers use it; never the
        // token), the typed address, Find's result line, the setup form's target and outcome (never the password fields), the
        // one connection for all computers, the flexible-requests state, the devices check and the Home Assistant summary
        // (version, installation, integrations, last backup) or why it couldn't be read.
        "SmartHomeStatus", "SmartHomeAddress", "SmartHomeFindStatus", "SmartHomeSetupTarget", "SmartHomeSetupStatus",
        "SmartHomeShareState", "SmartHomeShareStatus", "SmartHomeToolsStatus", "SmartHomeDevicesStatus", "SmartHomeMqtt",
        "SmartHomeManageStatus", "SmartHomeManageProblem",
        // Companion › Discord: the next setup step, whether a bot token is saved (with its application ID; never the token, and
        // DiscordToken is never read), what saving the token last did, the connection (state line, on/off, bot name, servers,
        // problem such as Message Content Intent being off), the invite links (built from the application ID), the chat modes
        // and channel rules ("DiscordRule-<channel>" through the prefix below), the rule picker's state, the chosen chat modes
        // in the combo boxes and the people summary (counts only), the owner's account ID and the home server choice.
        "DiscordSetupNext", "DiscordConfigured", "DiscordTokenStatus", "DiscordState", "DiscordEnabledStatus", "DiscordBotName",
        "DiscordServers", "DiscordProblem", "DiscordInviteStatus", "DiscordServerLink", "DiscordHomeLink", "DiscordUserLink",
        "DiscordChatModes", "DiscordServerChat", "DiscordDirectChat", "DiscordVoiceChat", "DiscordRuleChannelsStatus",
        "DiscordPeopleCount", "DiscordOwnerStatus", "DiscordOwnerId", "DiscordHomeServer",
        // Companion › Messaging: whether Martlet answers the Telegram bot on this PC now or why not (bot username, chat count,
        // when it last answered; never the token), the connect outcome, how many chats are paired and the pairing note (when
        // the code expires; never the code itself, MessagingPairCode, or chat names).
        "MessagingStatus", "MessagingNote", "MessagingChats", "MessagingPairStatus",
        // The same for WhatsApp (number, chat count; never the access token, app secret or code), plus where Meta delivers
        // messages: the own public address or the Cloudflare quick tunnel and whether cloudflared is on this PC.
        "MessagingWhatsAppStatus", "MessagingWhatsAppNote", "MessagingWhatsAppChats", "MessagingWhatsAppPairStatus", "MessagingWhatsAppTunnel",
        // Companion › Tools › Terminal: whether Martlet may run commands on this PC and how (shell, asks first, time limit) or
        // what keeps it from working, the chosen shell and time limit (choosing either with ui_select saves it, as do the
        // ToolsTerminalOn and ToolsTerminalAskFirst check boxes and the folder buttons, so they need --allow-ui-effects; the
        // start folder's path is never returned), and the fixed question turning Ask before every command off asks. In the talk
        // window, a waiting tool call's heading ("Run this command?") and question (which shell or server, and the seconds
        // left); never the command or arguments (LiveToolApprovalArguments).
        "ToolsTerminalStatus", "ToolsTerminalShell", "ToolsTerminalTimeLimit", "ToolsTerminalNoAskQuestion",
        "LiveToolApprovalTitle", "LiveToolApprovalText",
        // Devices › Apps and API keys: how many keys and how many hosts have them; the created dialog's title, host addresses
        // with their public key pins, and the example request (it names $MARTLET_API_KEY, never the key). The key itself
        // (ApiKeyValue) is never returned.
        "ApiKeysStatus", "ApiKeyCreatedTitle", "ApiKeyHosts", "ApiKeyExample",
        // Devices › Sharing work: how many of this PC's requests another computer took since Martlet started, per job, the last
        // one's computer, how many were busy and how long it waited. Job names and host IDs only.
        "WorkSharingStatus",
        // Settings › Startup and closing (what closing does and whether Windows starts Martlet), and the notification-area menu's
        // status line (Martlet is running, listening, paused or watching). StayAwakeStatus (shown only when this PC runs its own
        // host service): whether Martlet keeps this PC awake because it is a Martlet host PC, or because that host service serves other computers (host ID and
        // computer names) or lets it sleep, or why Windows refused. Fixed text, names and host IDs only.
        "BackgroundStatus", "TrayStatus", "StayAwakeStatus",
        // Settings › Appearance: the palette (Pink light, Rose dark, Character light or Character dark; menus and every window
        // follow it) and its status line, and the character's colors (how many and where the accent comes from, or why they
        // couldn't be read; never its name). AppearanceColor-<n> and AppearancePreview-<id> read through the prefixes below.
        "AppearanceTheme", "AppearanceStatus", "AppearanceCharacterStatus",
        // What this PC is for: the navigation rail's "Companion PC" or "Host PC", and Settings' line describing that role.
        "DeviceRoleSummary", "DeviceRoleText",
        // The host dashboard's status under its icon ("Host is running", "Needs Windows restart", "Waiting for Docker Desktop", ...), its
        // steps' heading ("This host is ready" or "Get this host running"), the line under it (how many steps are left and
        // the next one, or "All set", and when Martlet last checked) and the setup runs working now, side by side, each with
        // its status line ("Start Docker Desktop: Waiting for Docker Desktop to start..."; run titles and status lines only,
        // never output or pairing codes).
        "HostServiceStatus", "HostStepsHeading", "HostStepsSummary", "HostRunsNow",
        // What Martlet did by itself when this PC became (or started as) a host PC: started Docker Desktop and the host roles and
        // loaded their models, or why it didn't (Docker Desktop not installed, Windows not ready, no roles yet). Fixed text and
        // role names only.
        "HostAutoStart",
        // The confirmation and host-input dialogs' Copy buttons read "Copy", then "Copied" (or "Couldn't copy") for a few
        // seconds after a click; never what they copied. The problem dialog's heading (its report, ProblemText, can hold paths).
        // A host role's dialog heading says whether it adds the role or changes one the host runs ("Change deep-thinking on
        // diva"; role and host names only).
        "ConfirmationCopy", "HostInputCopy", "ProblemHeading", "HostInputHeading",
        // Exiting: the closing panel's step ("Stopping your tool servers...") and, once closing is slow, what Exit now
        // interrupts; the questions an exit asks first (what Martlet is still busy with: work kinds, run window titles,
        // a host ID or an update version, never paths, keys or conversation text) and before Exit now (the step).
        // ClosingExitNow and the dialogs' ConfirmationYes exit Martlet, so they need --allow-ui-effects.
        "ClosingStatus", "ClosingSlow", "ExitBusyQuestion", "ExitNowQuestion",
        // Companion › Thinking › This PC's Use Ollama on this PC, for a model Ollama doesn't have yet: the download question
        // (model tag, its size when Martlet knows it and what Thinking keeps using until it's ready). ConfirmationYes downloads
        // it, so it needs --allow-ui-effects.
        "LocalModelDownloadQuestion",
        // Companion › Deep thinking: the question before a Deep thinking model joins a Thinking model on the same graphics card
        // (on a paired computer's Add Deep thinking or This PC's Use Ollama on this PC): the computer, Thinking's model tag and
        // the one-graphics-card-for-each-Thinking-model advice. ConfirmationYes adds or uses it, so it needs --allow-ui-effects.
        "DeepThinkingShareQuestion",
        // Set it all up for me: its one confirmation (the plan, the downloads, lip-sync from the welcome wizard and the voice
        // engine's terms). The welcome wizard's Use these suggestions, the Home fixes and ConfirmationYes install and download, so
        // they need --allow-ui-effects.
        "DefaultSetupQuestion",
        // The welcome wizard: what Look for Martlet found, this PC's hardware (graphics card and memory, memory, processor
        // threads), the suggestion's summary (the preference chosen and whether Thinking goes online), its totals as shares of
        // this PC, and the NVIDIA key step's intro, numbered steps and outcome. Never the key.
        "WizardScanStatus", "WizardSpecs", "WizardPlanSummary", "WizardPlanTotals", "WizardKeyIntro", "WizardKeySteps", "WizardKeyStatus",
        // Creations: the fixed note ("Ask Martlet to sing or show any of these.") and empty state ("Things Martlet makes, like
        // songs, appear here."), how many creations and how large, whether they are shared with the paired computers (with how
        // many and when), and the selected creation's kind line (kind, length, size, when and on which computer it was made),
        // where it is (this PC and which hosts hold it) and what to ask Martlet ("Ask Martlet to sing it."). Never a title, text,
        // voice or personality: those are the owner's own (CreationTitle and the rename box are never returned).
        "CreationsNote", "CreationsEmpty", "CreationsSummary", "CreationsStatus", "CreationKind", "CreationSync", "CreationAsk",
        // Background tasks: how many run now and how many finished (TasksSummary), its empty state, the navigation rail's count
        // of running tasks (NavTasksCount, shown only while some run), a run window's line on Hide (HostRunHideHint) and the
        // question Cancel task asks first (the task's title, which is a run window's title).
        "TasksSummary", "TasksEmpty", "NavTasksCount", "HostRunHideHint", "CancelTaskQuestion"
    };
    /// <summary>Job titles in the selected device's details ("DeviceComponent-job-Llm" reads "Thinking (conversation model)");
    /// whether each home or host-dashboard step is ticked ("StepState-service" reads "Host service: done") and its buttons'
    /// labels ("Step-roles-0" reads "Add Thinking: Add roles");
    /// Smart home's found Home Assistants ("SmartHomeFound-0" reads "Home: http://192.168.1.20:8123 (Home Assistant 2026.9.4)"),
    /// each paired host's Home Assistant line ("SmartHomeHost-gpu-pc" reads "gpu-pc: can run Home Assistant."), the devices
    /// Home Assistant discovered ("SmartHomeDevice-0" reads "Philips Hue: Hue Bridge") and its waiting updates
    /// ("SmartHomeUpdate-0" reads "Update: Home Assistant Core 2026.9.3 → 2026.9.4");
    /// Companion › Voice's starter voices ("F5VoiceRow-arctic-slt" reads "SLT (US female)", with "· in use" when it is;
    /// never the names of the owner's own recordings) and every voice's detail line ("F5VoiceDetail-3f2a..." reads
    /// "3 recordings, 14.5 seconds joined, added 10/2/2026. XTTS-v2 learns from each recording."; never names or words) and
    /// Add a voice's line on filling in each recording's words ("F5AddVoiceHeard" and "F5AddVoiceHeard-2" read "Filled in by
    /// Parakeet on this PC: 12 words. Check them and fix anything it misheard."; never the words);
    /// Companion › Voice › Voice engine's rows, one per engine ("VoiceEngine-chatterbox" reads "Chatterbox Turbo · recommended",
    /// "VoiceEngineFeatures-chatterbox" "NVIDIA GPU, 6 GB+, Docker, Voice cloning, ...", "VoiceEngineState-chatterbox"
    /// "Ready on this PC." or why it can't run there, and its button "VoiceEngineUse-chatterbox" "Set up and use Chatterbox
    /// Turbo"; key "windows" for a Windows voice; clicking a button needs --allow-ui-effects) and its computer pills
    /// ("SpeakingHost-gpu-pc" reads "gpu-pc · speaking");
    /// each character's detail line in Companion › Character › Your characters
    /// ("CharacterModelState-builtin" reads "Live2D. Part of Martlet on every computer. Shown on this PC.",
    /// "CharacterModelState-0123456789abcdef" reads "VRM, 12.4 MB. Added on desktop-a 10/2/2026. Copying to this PC..."; never
    /// the character's name);
    /// each home or host-dashboard step's detail line ("StepDetail-docker" says whether Docker Desktop runs, or why it can't start);
    /// the paired computers a job can be handed to ("HostChoice-listening-gpu-pc" reads "gpu-pc: Runs speech recognition (small).")
    /// and why none are listed or which can't run it ("HostChoices-listening", "HostChoicesUnable-listening"); Home's items
    /// ("HealthIssue-ollama" reads "Problem: Ollama isn't running on this PC. ...") and Health tiles ("HealthCheck-microphone"
    /// reads "Microphone: OK. Windows default"); Diagnostics' shown lines, newest first ("LogEntry-0" reads
    /// "21:04:11.532 WARN This PC · App: Host gpu-box stopped answering: ...") and its computer filters ("LogSource-desktop-diva"
    /// reads "From: This PC (desktop-diva, diva-host)"); the Martlet desktops found on the network in
    /// Add a computer ("NearbyItem-0" reads "GAMING-PC (192.168.1.31): gaming-pc-host · Martlet 0.17.0"); the Martlet
    /// network's computers ("NetworkMember-host-gpu-pc" reads "gpu-pc. Host, paired with this PC; added on desktop-a.") and
    /// requests to join ("NetworkJoin-desktop-b" reads "DESKTOP-B asks to join. desktop-b, through gpu-pc. Check number ...")
    /// and computers that use one of this PC's hosts but aren't in the network ("NetworkPaired-desktop-c" reads "DESKTOP-C
    /// (desktop-c). Uses gpu-pc; active now. ..."); each device role's detail line in the selected device's details
    /// ("DeviceComponentDetail-users" reads "IMOUTO (desktop-imouto), active now; This PC, active now.",
    /// "DeviceComponentDetail-host-service" reads "Paired as diva-host. Used by IMOUTO (desktop-imouto), active now.",
    /// "DeviceComponentDetail-member" reads "In your Martlet network. Active now on diva-host.");
    /// each device on the Devices map ("Node-pc:desktop-imouto" reads "IMOUTO, desktop-imouto. Active now. Runs: Martlet app,
    /// Listening", "Node-cloud:openrouter.ai" reads "OpenRouter, openrouter.ai. Ready. Runs: Thinking");
    /// API keys ("ApiKeyRow-AbC..." reads "Home Assistant. See status and logs. Made on desktop-a 10/2/2026. ... ID AbCdEf.",
    /// never the key or its verifier); a host role's choices in its Add dialog ("HostInput-choice.A2F_ENGINE" reads "local",
    /// a variant's own "HostInput-choice.STT_MODEL@STT_ENGINE=parakeet" reads "Parakeet TDT 110M (English)";
    /// never its secret fields), the terms that follow a variant choice ("HostInputTerms-A2F_ENGINE") and each Companion › Prompts
    /// prompt's state ("PromptState-reply_length" reads "Edited. About 82 tokens." or, while it saves, "Edited. About 82 tokens.
    /// Saving..."; never the prompt text);
    /// and the Copy button on every read-only text box ("Copy-HostRunOutput" reads "Copy", or "Copied" for a few seconds after a
    /// click; never the text it copies).</summary>
    // People's "Hear them (3):" per voice ("PeopleClips-2") counts the clips kept of a voice not named yet; playing one
    // ("PeopleClip-2-0") plays audio, so it needs --allow-ui-effects.
    private static readonly string[] SafeValuePrefixes = ["PeopleClips-", "DeviceComponent-", "DeviceComponentDetail-", "F5VoiceRow-", "F5VoiceDetail-", "F5AddVoiceRecording-", "F5AddVoiceHeard", "CharacterModelState-", "CharacterActionName-", "CharacterActionTry-", "TouchZoneState-", "TouchTemperamentAttitude-", "TouchZoneNotices-", "VoiceEngine", "SpeakingHost-", "SingingHost-",
        "StepDetail-", "StepState-", "Step-",
        // Prepare this computer's GPU power lines: each slider's watts ("PreparePower-0"), the chosen limit
        // ("PreparePowerValue-0" reads "300 W") and the GPU's limits ("PreparePowerDetail-0" reads "Now 370 W, default
        // 370 W, allowed 100-450 W."). Numbers only.
        "PreparePower-", "PreparePowerValue-", "PreparePowerDetail-",
        // The welcome wizard: each Martlet found ("WizardFound-0": name, address, version and hosts), each hardware line
        // ("WizardSpecRow-Vram") and each suggested part ("WizardPlanItem-Thinking": what, where, its % of graphics memory,
        // memory and processor, and why) and, after joining a network, what changes ("WizardJoinSuggestion-0").
        "WizardFound-", "WizardSpecRow-", "WizardPlanItem-", "WizardJoinSuggestion-",
        // Companion › Discord › Friends and calls: each friend's line ("DiscordFriend-123" reads "Ana (123) — Martlet also knows
        // Ana by voice"). Never a token.
        "DiscordFriend-",
        "HostChoice",
        "HealthIssue-", "HealthCheck-", "LogEntry-", "LogSource-", "NearbyItem-", "NetworkMember-", "NetworkJoin-", "NetworkPaired-", "ApiKeyRow-", "SmartHomeFound-", "SmartHomeHost-",
        "SmartHomeDevice-", "SmartHomeUpdate-", "DiscordRule-", "HostInput-choice.", "HostInputTerms-", "PromptState-", "Copy-", "Node-", "DeviceFilter-",
        // The selected device's resource bars ("DeviceResource-vram" reads "Graphics memory: 14 of 32 GB planned (44%), 15 GB
        // free for Martlet."; a range such as "11-14 of 32 GB planned (34-44%)" when jobs grow while they work, with ", tight: ..."
        // when only the usual amounts fit; keys vram, ram, cpu, disk), each job's share ("DeviceShare-deep-thinking-gemma4-12b" reads
        // "Deep thinking (Gemma 4 12B): 25% graphics memory, 3% memory, 6% processor.") and what else fits there
        // ("DeviceAlsoFits-0" reads "Room for another Deep thinking model (Gemma 4 12B) here.").
        "DeviceResource-", "DeviceShare-", "DeviceAlsoFits-",
        // The setup advisor's plan: each role's pick and status ("AdvisorChoice-3" reads "Speech-to-text: Parakeet speech
        // recognition (Available)"; the plan has no personal data).
        "AdvisorChoice-",
        // The talk window's task list: each background task's status ("LiveJobState-think-1" reads "Checking it fits beside
        // Thinking." or "Done after 1:02. Martlet brought it up."; never what the task is about or what it found).
        "LiveJobState-",
        // Companion › Deep thinking: each paired computer's line ("DeepThinkingHost-diva" reads "diva: Ollama runs gemma4:27b.")
        // and, for one without the Deep thinking role, its Add button's name ("DeepThinkingAddRole-diva" reads "Add Deep thinking
        // on diva"; clicking it installs the role, so it needs --allow-ui-effects); for one with it, its Change model button's
        // name ("DeepThinkingChangeModel-diva" reads "Change the Deep thinking model on diva (now gemma4:e4b)"; clicking it
        // reads the role's settings there and opens its dialog, so it needs --allow-ui-effects). Thinking's, Listening's and
        // Lip-sync's computers have the same button for the role they run ("SetupChangeHost-thinking-diva" reads "Change model:
        // conversation model on diva (now gemma4-e4b)").
        // Each paired computer's Join the Thinking pool box ("DeepThinkingPool-diva" reads "Join the Thinking pool on diva" and
        // whether it is ticked; ticking it saves thinking-pool.json, so it needs --allow-ui-effects). Each pool member's line
        // ("ThinkingPoolMember-0" reads "diva's Thinking pool (qwen3-8b): 2 slots; text only."), its slot choice
        // (ThinkingPoolSlots-0) and its Remove button (ThinkingPoolRemove-0); both save thinking-pool.json, so they need
        // --allow-ui-effects.
        // Each paired computer's shared-card warning, when Deep thinking there shares one graphics card with its Thinking model
        // ("DeepThinkingShare-diva" reads "diva: diva already runs a Thinking model (gemma4:e4b) on its only graphics card. ...").
        "DeepThinkingHost-", "DeepThinkingShare-", "DeepThinkingAddRole-", "DeepThinkingChangeModel-", "DeepThinkingPool-", "SetupChangeHost-",
        "ThinkingPoolMember-", "ThinkingPoolSlots-",
        // Settings › Appearance: each of the character's main colors ("AppearanceColor-0" reads "#2B3440 31% dark grayish blue") and
        // each character palette's colors by role ("AppearancePreview-rules-dark" reads "Character dark: Canvas #1B1F26, ...").
        "AppearanceColor-", "AppearancePreview-",
        // Companion › Listening › Parakeet in Martlet: each model's title with its tags ("ListenParakeetModel-parakeet-tdt-110m-en"
        // reads "Fastest in English  ·  recommended") and its line ("ListenParakeetModelState-parakeet-tdt-110m-en" reads
        // "Parakeet TDT 110M (English). Replies start sooner: ... Downloads once: 477 MB."). Its SetupListenParakeet-<model>
        // button downloads (after a confirmation) and switches Listening, so it needs --allow-ui-effects.
        "ListenParakeetModel",
        // Creations: each creation's line in the list ("CreationState-3f2a9c1b7d04" reads "Song · 1:02 · 6.6 MB · made 10/3/2026
        // 9:41 PM on DESK-PC · on this PC, on 2 of 2 hosts"; never its title).
        "CreationState-",
        // Background tasks: each task's title ("TaskTitle-3" reads "Set up gpu-pc", a run window's title), its line
        // ("TaskState-3" reads "Running for 2 min. Waiting for Docker Desktop to start..." or "Done at 3:41 PM after 5 min.
        // gpu-pc is ready.") and its buttons ("TaskShow-3" reads "Show: Set up gpu-pc" or "Show output: ...", "TaskCancel-3"
        // "Cancel: Set up gpu-pc").
        "TaskTitle-", "TaskState-", "TaskShow-", "TaskCancel-",
        // Companion › Profiles: each profile's state ("CharacterProfileState-3f2a9c1b" reads "In use.", "Ready." or why a part
        // can't switch here, such as "Its look is still copying to this PC. Using it switches the rest."; never a name).
        "CharacterProfileState-",
        // Devices › Sharing work: each job's line ("WorkSharingJob-speaking" reads "Speaking. When the computer doing it is busy
        // ..."), each computer in its order ("WorkSharingPlace-speaking-diva-host" reads "1. diva-host. this PC's own; does it for
        // this PC now."), each computer's keep line ("WorkSharingHost-diva-host" reads "diva-host. Kept for desk-1.") and its
        // choice ("WorkSharingKeep-diva-host"), the Share and own-computer-first boxes ("WorkSharingShare-speaking",
        // "WorkSharingOwnFirst-speaking"), each Use box ("WorkSharingUse-speaking-diva-host") and Up/Down buttons
        // ("WorkSharingUp-speaking-diva-host"). Changing any of them saves work-sharing.json and shares it with your other
        // computers, so it needs --allow-ui-effects. Host IDs, device IDs and fixed text only.
        "WorkSharing"];
    private int? processId;

    private static bool IsSafeClick(string id) =>
        SafeClicks.Contains(id) || SafeClickPrefixes.Any(prefix => id.StartsWith(prefix, StringComparison.Ordinal));

    private static bool IsSafeValue(string id) =>
        SafeValues.Contains(id) || SafeValuePrefixes.Any(prefix => id.StartsWith(prefix, StringComparison.Ordinal));

    internal object Connect(int pid)
    {
        if (pid <= 0) throw new ArgumentException("A positive Martlet desktop process ID is required.");
        using var process = Process.GetProcessById(pid);
        if (!string.Equals(process.ProcessName, "Martlet.Desktop", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(process.ProcessName, "Martlet.Companion", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("The process is not Martlet.Desktop or Martlet.Companion.");
        var windows = Windows(pid);
        // A Martlet in the notification area (closed to it, or started with Windows) has no visible window but its icon's window;
        // one that couldn't start shows only its problem dialog.
        if (!MainWindowVisible(windows) && !ProblemShown(windows) && TrayWindow(pid) == 0)
            throw new InvalidOperationException("The Martlet desktop window is not visible in this interactive session.");
        processId = pid;
        return new { processId = pid, windows = windows.Select(window => window.Current.Name).ToArray(), inTray = !MainWindowVisible(windows) };
    }

    internal object Snapshot(bool layout = false, string? idPrefix = null)
    {
        var windows = ConnectedWindows();
        return new
        {
            processId,
            windows = windows.Select(window => window.Current.Name).ToArray(),
            // A window that is disabled can't take input, as when a modal dialog blocks it; the talk window never blocks Martlet.
            // Its frame shows whether it can be resized, minimized and maximized; with layout, where it is and the work area
            // (screen minus taskbar) of its monitor, both in screen pixels, so a window can be checked to open wholly on screen.
            // Whether it is minimized and has the focus show, for example, that Martlet restarted by an unattended update came
            // back minimized without taking the focus.
            windowStates = windows.Select(window =>
            {
                var handle = (nint)window.Current.NativeWindowHandle;
                var style = GetWindowLongPtrW(handle, WindowStyleIndex);
                var state = new Dictionary<string, object?>
                {
                    ["name"] = window.Current.Name,
                    ["id"] = window.Current.AutomationId,
                    ["enabled"] = IsWindowEnabled(handle),
                    ["resizable"] = (style & ThickFrame) != 0,
                    ["minimizable"] = (style & MinimizeBox) != 0,
                    ["maximizable"] = (style & MaximizeBox) != 0,
                    ["minimized"] = IsIconic(handle),
                    ["foreground"] = GetForegroundWindow() == handle
                };
                if (layout) (state["bounds"], state["workArea"]) = Placement(handle);
                return state;
            }).ToArray(),
            controls = Controls(windows).Where(control => idPrefix is null ||
                control.Item2.Current.AutomationId.StartsWith(idPrefix, StringComparison.Ordinal)).Select(control =>
            {
                var (window, element) = control;
                var id = element.Current.AutomationId;
                // Status text blocks expose their text as the accessible name; a status button's name carries its state, a
                // list item's (a Diagnostics log line) its text and a filter pill's (Diagnostics' "From: This PC (...)") its choice.
                var value = !IsSafeValue(id) ? null
                    : element.TryGetCurrentPattern(ValuePattern.Pattern, out var pattern) ? ((ValuePattern)pattern).Current.Value
                    // A slider reads as its number ("80" for Voice volume at 80%).
                    : element.TryGetCurrentPattern(RangeValuePattern.Pattern, out var range)
                        ? ((RangeValuePattern)range).Current.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)
                    : element.Current.ControlType == ControlType.Text || element.Current.ControlType == ControlType.Button ||
                        element.Current.ControlType == ControlType.ListItem || element.Current.ControlType == ControlType.MenuItem ||
                        element.Current.ControlType == ControlType.RadioButton
                        ? element.Current.Name
                    // A combo box without a value pattern reads as its selected option.
                    : element.TryGetCurrentPattern(SelectionPattern.Pattern, out var choice)
                        ? ((SelectionPattern)choice).Current.GetSelection().FirstOrDefault()?.Current.Name : null;
                var entry = new Dictionary<string, object?>
                {
                    ["window"] = window.Current.Name,
                    ["id"] = id,
                    ["kind"] = element.Current.ControlType.ProgrammaticName,
                    ["enabled"] = element.Current.IsEnabled,
                    ["value"] = value,
                    ["checkedState"] = element.TryGetCurrentPattern(TogglePattern.Pattern, out var toggle)
                        ? ((TogglePattern)toggle).Current.ToggleState.ToString() : null,
                    // Radio buttons, list items and navigation entries are selected rather than checked.
                    ["selected"] = element.TryGetCurrentPattern(SelectionItemPattern.Pattern, out var item)
                        ? ((SelectionItemPattern)item).Current.IsSelected : null
                };
                // A status line's details sit in its tooltip, its accessible help text (the talk window's LiveContext: the
                // tokens and the last reply's cache use).
                if (value is not null && element.Current.ControlType == ControlType.Text && element.Current.HelpText is { Length: > 0 } help)
                    entry["help"] = help;
                // A control ui_move can move (the character overlay's MoveAvatar), and whether it can move now: false while the
                // character's position is locked.
                if (element.TryGetCurrentPattern(TransformPattern.Pattern, out var transform))
                    entry["movable"] = ((TransformPattern)transform).Current.CanMove;
                if (layout)
                {
                    entry["bounds"] = Box(element.Current.BoundingRectangle);
                    entry["textBounds"] = FirstLineBounds(element);
                }
                return entry;
            }).Take(200).ToArray()
        };
    }

    /// <summary>Screen pixels as [x, y, width, height], or null when the element has no on-screen area.</summary>
    private static int[]? Box(System.Windows.Rect rect) =>
        rect.IsEmpty || rect.Width <= 0 || rect.Height <= 0 ? null
            : [(int)Math.Round(rect.X), (int)Math.Round(rect.Y), (int)Math.Round(rect.Width), (int)Math.Round(rect.Height)];

    private const int WindowStyleIndex = -16;
    private const nint ThickFrame = 0x40000, MinimizeBox = 0x20000, MaximizeBox = 0x10000;
    private static readonly nint PerMonitorAwareV2 = -4;

    /// <summary>A top-level window's frame and its monitor's work area as [x, y, width, height] in physical screen pixels, like
    /// UI Automation's bounds at any display scale.</summary>
    private static (int[]? Bounds, int[]? WorkArea) Placement(nint window)
    {
        var previous = SetThreadDpiAwarenessContext(PerMonitorAwareV2);
        try
        {
            var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
            return (GetWindowRect(window, out var frame) ? Area(frame) : null,
                GetMonitorInfo(MonitorFromWindow(window, 2), ref info) ? Area(info.Work) : null);
        }
        finally { if (previous != 0) SetThreadDpiAwarenessContext(previous); }

        static int[] Area(NativeRect rect) => [rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top];
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public int Size;
        public NativeRect Monitor, Work;
        public uint Flags;
    }

    [DllImport("user32.dll")] private static extern nint GetWindowLongPtrW(nint window, int index);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetWindowRect(nint window, out NativeRect rect);
    [DllImport("user32.dll")] private static extern nint MonitorFromWindow(nint window, uint flags);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetMonitorInfo(nint monitor, ref MonitorInfo info);
    [DllImport("user32.dll")] private static extern nint SetThreadDpiAwarenessContext(nint context);

    /// <summary>Where a text control's first line of text is drawn (for checking alignment, e.g. a hint against typed text).
    /// Geometry only: the text itself is never read.</summary>
    private static int[]? FirstLineBounds(AutomationElement element)
    {
        try
        {
            if (!element.TryGetCurrentPattern(TextPattern.Pattern, out var pattern)) return null;
            var line = ((TextPattern)pattern).DocumentRange.Clone();
            line.MoveEndpointByRange(TextPatternRangeEndpoint.End, line, TextPatternRangeEndpoint.Start);
            line.ExpandToEnclosingUnit(TextUnit.Line);
            var rects = line.GetBoundingRectangles();
            return rects.Length == 0 ? null : Box(rects[0]);
        }
        catch (Exception ex) when (ex is InvalidOperationException or COMException or ElementNotAvailableException)
        {
            return null;
        }
    }

    internal async Task<object> ClickAsync(string id, string? window = null)
    {
        if (!allowEffects && !IsSafeClick(id))
            throw new InvalidOperationException("This control requires an operator to start MCP with --allow-ui-effects.");
        var element = Find(id, window);
        if (!element.Current.IsEnabled) throw new InvalidOperationException($"Control '{id}' is disabled.");
        // Navigation items select a page and sections expand or collapse; neither starts work.
        if (element.TryGetCurrentPattern(SelectionItemPattern.Pattern, out var selection))
        {
            ((SelectionItemPattern)selection).Select();
            return new { clicked = id, completed = true };
        }
        if (element.TryGetCurrentPattern(ExpandCollapsePattern.Pattern, out var section) &&
            !element.TryGetCurrentPattern(InvokePattern.Pattern, out _))
        {
            var expander = (ExpandCollapsePattern)section;
            if (expander.Current.ExpandCollapseState == ExpandCollapseState.Collapsed) expander.Expand();
            else expander.Collapse();
            return new { clicked = id, completed = true, expanded = expander.Current.ExpandCollapseState != ExpandCollapseState.Collapsed };
        }
        if (!element.TryGetCurrentPattern(InvokePattern.Pattern, out var pattern))
            throw new InvalidOperationException($"Control '{id}' does not support UI Automation Invoke.");
        var invocation = Task.Run(() => ((InvokePattern)pattern).Invoke());
        try
        {
            await invocation.WaitAsync(TimeSpan.FromSeconds(1));
            return new { clicked = id, completed = true };
        }
        catch (TimeoutException)
        {
            _ = invocation.ContinueWith(task => Console.Error.WriteLine(task.Exception),
                TaskContinuationOptions.OnlyOnFaulted);
            return new { clicked = id, completed = false, note = "UI action is still open; inspect the window before continuing." };
        }
    }

    internal object Select(string id, string item)
    {
        if (!allowEffects)
            throw new InvalidOperationException("This selection requires --allow-ui-effects.");
        var element = Find(id);
        if (!element.Current.IsEnabled) throw new InvalidOperationException($"Control '{id}' is disabled.");
        if (element.TryGetCurrentPattern(ExpandCollapsePattern.Pattern, out var expand))
            ((ExpandCollapsePattern)expand).Expand();
        var matches = element.FindAll(TreeScope.Descendants,
            new PropertyCondition(AutomationElement.NameProperty, item));
        var option = matches.Cast<AutomationElement>().FirstOrDefault(candidate =>
            candidate.TryGetCurrentPattern(SelectionItemPattern.Pattern, out _))
            ?? throw new ArgumentException($"Item '{item}' is not available in '{id}'.");
        ((SelectionItemPattern)option.GetCurrentPattern(SelectionItemPattern.Pattern)).Select();
        if (element.TryGetCurrentPattern(ExpandCollapsePattern.Pattern, out expand))
            ((ExpandCollapsePattern)expand).Collapse();
        return new { selected = item, control = id };
    }

    internal object SetText(string id, string text)
    {
        if (!allowEffects) throw new InvalidOperationException("Text entry requires --allow-ui-effects.");
        if (text.Length > 4096) throw new ArgumentException("Text exceeds 4096 characters.");
        var element = Find(id);
        if (!element.Current.IsEnabled || element.Current.ControlType != ControlType.Edit ||
            !element.TryGetCurrentPattern(ValuePattern.Pattern, out var pattern) ||
            ((ValuePattern)pattern).Current.IsReadOnly)
            throw new InvalidOperationException($"Control '{id}' is not an enabled editable text field.");
        ((ValuePattern)pattern).SetValue(text);
        return new { updated = id };
    }

    internal object Toggle(string id)
    {
        if (!allowEffects) throw new InvalidOperationException("Checkbox changes require --allow-ui-effects.");
        var element = Find(id);
        if (!element.Current.IsEnabled || !element.TryGetCurrentPattern(TogglePattern.Pattern, out var pattern))
            throw new InvalidOperationException($"Control '{id}' is not an enabled checkbox.");
        ((TogglePattern)pattern).Toggle();
        return new { toggled = id, state = ((TogglePattern)pattern).Current.ToggleState.ToString() };
    }

    internal object SetRange(string id, double value)
    {
        if (!allowEffects) throw new InvalidOperationException("Slider changes require --allow-ui-effects.");
        if (!double.IsFinite(value)) throw new ArgumentException("The value must be a number.");
        var element = Find(id);
        if (!element.Current.IsEnabled || !element.TryGetCurrentPattern(RangeValuePattern.Pattern, out var pattern) ||
            ((RangeValuePattern)pattern).Current.IsReadOnly)
            throw new InvalidOperationException($"Control '{id}' is not an enabled slider.");
        var range = (RangeValuePattern)pattern;
        if (value < range.Current.Minimum || value > range.Current.Maximum)
            throw new ArgumentException($"'{id}' takes {range.Current.Minimum} through {range.Current.Maximum}.");
        range.SetValue(value);
        return new { updated = id, value = range.Current.Value, minimum = range.Current.Minimum, maximum = range.Current.Maximum };
    }

    internal const int MaximumMove = 10_000;

    /// <summary>Taps the showing character at <paramref name="x"/>, <paramref name="y"/> (fractions 0..1 of the overlay's
    /// drawing, +y down; needs --allow-ui-effects) through MoveAvatar's UI Automation value, like a click there held for
    /// <paramref name="holdMs"/> (600 or more: a hold), <paramref name="repeat"/> times <paramref name="gapMs"/> apart, or each
    /// of <paramref name="taps"/> (x, y, holdMs) in turn, waiting for the renderer's hit test after each; without a point it only
    /// reads the last tap. Returns the last tap as the overlay reports it and, when Companion › Character › Touch zones shows,
    /// what Martlet noticed (TouchZonesNoticed, TouchZonesNoticedLast) after <paramref name="settleMs"/>.</summary>
    internal async Task<object> TouchCharacterAsync(double? x, double? y, int? holdMs = null, int? repeat = null, int? gapMs = null,
        IReadOnlyList<(double X, double Y, int HoldMs)>? taps = null, int? settleMs = null)
    {
        if ((x is null) != (y is null)) throw new ArgumentException("Give both x and y, or neither to read the last tap.");
        if (taps is not null && x is not null) throw new ArgumentException("Give x and y, or taps, not both.");
        var element = Find("MoveAvatar");
        if (!element.TryGetCurrentPattern(ValuePattern.Pattern, out var pattern))
            throw new InvalidOperationException("The character overlay can't be tapped through UI Automation.");
        var value = (ValuePattern)pattern;
        static object? Read(string text) => string.IsNullOrEmpty(text) ? null : System.Text.Json.JsonDocument.Parse(text).RootElement.Clone();
        if (x is null && taps is null) return new { touched = false, last = Read(value.Current.Value), noticed = Noticed() };
        if (!allowEffects) throw new InvalidOperationException("Tapping the character requires --allow-ui-effects.");
        var times = repeat ?? 1;
        if (times is < 1 or > MaximumTaps) throw new ArgumentException($"repeat is 1 to {MaximumTaps}.");
        if (holdMs is < 0 or > 10_000) throw new ArgumentException("holdMs is 0 to 10000.");
        if (gapMs is < 0 or > 10_000) throw new ArgumentException("gapMs is 0 to 10000.");
        if (settleMs is < 0 or > 15_000) throw new ArgumentException("settleMs is 0 to 15000.");
        var sequence = taps ?? Enumerable.Repeat((x!.Value, y!.Value, holdMs ?? 0), times).ToArray();
        if (sequence.Count is < 1 or > MaximumTaps) throw new ArgumentException($"taps holds 1 to {MaximumTaps} taps.");
        if (sequence.Any(t => t.X is not (>= 0 and <= 1) || t.Y is not (>= 0 and <= 1) || t.HoldMs is < 0 or > 10_000))
            throw new ArgumentException("Each tap's x and y are fractions from 0 to 1 and holdMs is 0 to 10000.");
        if (value.Current.IsReadOnly) throw new InvalidOperationException("The character can't be tapped until it has loaded.");
        var answered = 0;
        string after = value.Current.Value;
        for (var i = 0; i < sequence.Count; i++)
        {
            if (i > 0) await Task.Delay(gapMs ?? 150);
            var (tx, ty, held) = sequence[i];
            var before = value.Current.Value;
            value.SetValue(held > 0 ? FormattableString.Invariant($"{tx},{ty},{held}") : FormattableString.Invariant($"{tx},{ty}"));
            var waited = System.Diagnostics.Stopwatch.StartNew();
            while ((after = value.Current.Value) == before && waited.Elapsed < TimeSpan.FromSeconds(3)) await Task.Delay(50);
            if (after != before) answered++;
        }
        if (settleMs is > 0) await Task.Delay(settleMs.Value);
        return answered == 0
            ? new { touched = false, taps = sequence.Count, answered, last = Read(after), noticed = Noticed(), note = (string?)"The renderer didn't answer the tap within 3 seconds." }
            : new { touched = true, taps = sequence.Count, answered, last = Read(after), noticed = Noticed(), note = (string?)null };
    }

    internal const int MaximumTaps = 20;

    // What Martlet noticed, when Companion › Character › Touch zones shows (null otherwise).
    private object? Noticed()
    {
        string? Text(string id)
        {
            try { return Find(id).Current.Name; }
            catch (ArgumentException) { return null; }
        }
        var waiting = Text("TouchZonesNoticed");
        return waiting is null ? null : new { waiting, last = Text("TouchZonesNoticedLast"), zone = Text("TouchZonesLast") };
    }

    /// <summary>Strokes the showing, locked character along <paramref name="points"/> (fractions 0..1 of the overlay's drawing; needs
    /// --allow-ui-effects) through MoveAvatar's UI Automation value ("stroke:ms;x,y;..."), then waits for the overlay's stroke
    /// record; without points it only reads the last one. Returns the overlay's reading (last tap, last stroke, last change).</summary>
    internal async Task<object> StrokeCharacterAsync((double X, double Y)[]? points, int stepMs)
    {
        var element = Find("MoveAvatar");
        if (!element.TryGetCurrentPattern(ValuePattern.Pattern, out var pattern))
            throw new InvalidOperationException("The character overlay can't be stroked through UI Automation.");
        var value = (ValuePattern)pattern;
        static object? Read(string text) => string.IsNullOrEmpty(text) ? null : System.Text.Json.JsonDocument.Parse(text).RootElement.Clone();
        static string? Stroke(string text) => string.IsNullOrEmpty(text) ? null :
            System.Text.Json.JsonDocument.Parse(text).RootElement is { ValueKind: System.Text.Json.JsonValueKind.Object } root &&
            root.TryGetProperty("stroke", out var stroke) ? stroke.GetRawText() : null;
        if (points is null) return new { stroked = false, last = Read(value.Current.Value) };
        if (!allowEffects) throw new InvalidOperationException("Stroking the character requires --allow-ui-effects.");
        if (points.Length is < 2 or > 200) throw new ArgumentException("Give 2 to 200 points.");
        if (points.Any(p => p.X is not (>= 0 and <= 1) || p.Y is not (>= 0 and <= 1))) throw new ArgumentException("x and y are fractions from 0 to 1.");
        if (stepMs is < 10 or > 2000) throw new ArgumentException("stepMs is 10 to 2000.");
        if (value.Current.IsReadOnly) throw new InvalidOperationException("The character can't be stroked until it has loaded.");
        var before = Stroke(value.Current.Value);
        value.SetValue("stroke:" + string.Join(";", new[] { stepMs.ToString(System.Globalization.CultureInfo.InvariantCulture) }
            .Concat(points.Select(p => FormattableString.Invariant($"{p.X},{p.Y}")))));
        var waited = System.Diagnostics.Stopwatch.StartNew();
        var limit = TimeSpan.FromMilliseconds(points.Length * stepMs + 3000);
        string after;
        while (Stroke(after = value.Current.Value) == before && waited.Elapsed < limit) await Task.Delay(50);
        return Stroke(after) == before
            ? new { stroked = false, last = Read(after), note = "The overlay didn't finish the stroke in time." }
            : new { stroked = true, last = Read(after) };
    }

    /// <summary>Moves a movable control (the character overlay's MoveAvatar, like dragging the character) by dx, dy screen
    /// pixels through UI Automation's Transform pattern, then reports where it was and is. Refused while it can't move, as
    /// when the character's position is locked in Martlet.</summary>
    internal object Move(string id, int dx, int dy)
    {
        if (!allowEffects) throw new InvalidOperationException("Moving a control requires --allow-ui-effects.");
        if (Math.Abs(dx) > MaximumMove || Math.Abs(dy) > MaximumMove)
            throw new ArgumentException($"Move at most {MaximumMove} pixels each way.");
        var element = Find(id);
        if (!element.Current.IsEnabled || !element.TryGetCurrentPattern(TransformPattern.Pattern, out var pattern))
            throw new InvalidOperationException($"Control '{id}' can't be moved.");
        var transform = (TransformPattern)pattern;
        if (!transform.Current.CanMove)
            throw new InvalidOperationException($"Control '{id}' can't move now (the character's position is locked in Martlet).");
        var before = element.Current.BoundingRectangle;
        transform.Move(before.X + dx, before.Y + dy);
        Thread.Sleep(200);
        return new { moved = id, from = Box(before), to = Box(element.Current.BoundingRectangle) };
    }

    /// <summary>The control with automation ID <paramref name="id"/>, in the window titled <paramref name="window"/> when given
    /// (several run windows can be open side by side, each with the same controls).</summary>
    private AutomationElement Find(string id, string? window = null)
    {
        if (string.IsNullOrWhiteSpace(id)) throw new ArgumentException("A control automation ID is required.");
        var windows = ConnectedWindows();
        if (window is not null)
        {
            windows = windows.Where(candidate => string.Equals(candidate.Current.Name, window, StringComparison.Ordinal)).ToArray();
            if (windows.Length == 0) throw new ArgumentException($"Window '{window}' is not open. Refresh the UI snapshot.");
        }
        var matches = Controls(windows).Select(control => control.Element)
            .Where(element => element.Current.AutomationId == id)
            .Take(2).ToArray();
        return matches.Length switch
        {
            1 => matches[0],
            0 => throw new ArgumentException($"Control '{id}' was not found. Refresh the UI snapshot."),
            _ => throw new ArgumentException($"Control '{id}' is ambiguous across open windows; name the window.")
        };
    }

    private AutomationElement[] ConnectedWindows()
    {
        if (processId is not int pid) throw new InvalidOperationException("Connect to a running Martlet desktop first.");
        using var process = Process.GetProcessById(pid);
        if (!string.Equals(process.ProcessName, "Martlet.Desktop", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(process.ProcessName, "Martlet.Companion", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The attached Martlet process exited.");
        var windows = Windows(pid);
        if (!MainWindowVisible(windows) && !ProblemShown(windows) && TrayWindow(pid) == 0)
            throw new InvalidOperationException("The Martlet window is no longer visible.");
        return windows;
    }

    private static bool MainWindowVisible(AutomationElement[] windows) =>
        windows.Any(window => window.Current.AutomationId == "MartletMainWindow");

    /// <summary>Martlet's problem dialog ("Martlet couldn't start" or an unexpected error) is open.</summary>
    private static bool ProblemShown(AutomationElement[] windows) =>
        windows.Any(window => window.Current.AutomationId == "ProblemDialog");

    // ---------- the notification-area icon (Martlet.Desktop's TrayIcon) ----------

    private const string TrayWindowTitle = "Martlet notification area";
    private const string TrayAddedProperty = "MartletTrayIconAdded";
    private const int TrayCallbackMessage = 0x8000 + 0x4D;
    private const int TrayIconId = 1;
    private const int NinSelect = 0x400, WmContextMenu = 0x7B;
    internal static readonly string[] TrayActions = ["status", "open", "menu", "close"];

    /// <summary>Martlet's notification-area icon: "status" reads whether the icon is shown and the main window visible; "open"
    /// and "menu" send the icon what Explorer sends for a left click (show Martlet) and a right click (its menu, at the mouse
    /// pointer or at x, y), granting it the foreground as Explorer does when this process may, then ui_snapshot lists the menu's
    /// Tray* items; every action reports menuOpen and, while it is open, menuBounds. "close" presses the main window's close
    /// button, which hides Martlet in the notification area by default and exits it when Keep running when closed is off, so it
    /// needs --allow-ui-effects.</summary>
    internal object Tray(string action, int? x = null, int? y = null)
    {
        if (!TrayActions.Contains(action)) throw new ArgumentException($"Unknown ui_tray action '{action}'.");
        if (action == "close" && !allowEffects)
            throw new InvalidOperationException("Closing Martlet's window can exit it, so it requires --allow-ui-effects.");
        if ((x is null) != (y is null)) throw new ArgumentException("Give both x and y, or neither.");
        if (x is not null && action is not ("open" or "menu")) throw new ArgumentException("x and y only apply to \"open\" and \"menu\".");
        if (x is < short.MinValue or > short.MaxValue || y is < short.MinValue or > short.MaxValue)
            throw new ArgumentOutOfRangeException(x is < short.MinValue or > short.MaxValue ? nameof(x) : nameof(y), "Screen pixels fit in 16 bits.");
        if (action == "status" && processId is int exited && !Running(exited))
            return new { processId = exited, running = false, trayIcon = false, mainWindowVisible = false, inTray = false, menuOpen = false,
                menuBounds = (int[]?)null };
        var windows = ConnectedWindows();
        var pid = processId!.Value;
        var icon = TrayWindow(pid);
        switch (action)
        {
            case "open" or "menu":
                if (icon == 0) throw new InvalidOperationException("Martlet has no notification-area icon.");
                // Explorer passes where the click was in physical screen pixels, whatever the app's DPI awareness.
                var pointer = x is int clickX && y is int clickY ? new NativePoint { X = clickX, Y = clickY } : PhysicalPointer();
                var at = (nint)((pointer.Y & 0xFFFF) << 16 | (pointer.X & 0xFFFF));
                // Explorer lets the clicked icon's app take the foreground; this only works while this process may take it itself.
                AllowSetForegroundWindow(pid);
                if (!PostMessage(icon, TrayCallbackMessage, at, TrayIconId << 16 | (action == "open" ? NinSelect : WmContextMenu)))
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                break;
            case "close":
                var main = windows.FirstOrDefault(window => window.Current.AutomationId == "MartletMainWindow")
                    ?? throw new InvalidOperationException("Martlet's window is already hidden.");
                ((WindowPattern)main.GetCurrentPattern(WindowPattern.Pattern)).Close();
                break;
        }
        if (action != "status") Thread.Sleep(500);
        if (!Running(pid))
            return new { processId = pid, running = false, trayIcon = false, mainWindowVisible = false, inTray = false, menuOpen = false,
                menuBounds = (int[]?)null };
        var now = Windows(pid);
        var visible = MainWindowVisible(now);
        icon = TrayWindow(pid);
        var menu = TrayMenuWindow(now);
        return new { processId = pid, running = true, trayIcon = icon != 0 && GetProp(icon, TrayAddedProperty) != 0, mainWindowVisible = visible,
            inTray = icon != 0 && !visible, menuOpen = menu is not null,
            menuBounds = menu is null ? null : Placement((nint)menu.Current.NativeWindowHandle).Bounds };
    }

    /// <summary>The window holding the icon's open menu: a Martlet popup window (not the main window) with the TrayMenu.</summary>
    private static AutomationElement? TrayMenuWindow(AutomationElement[] windows) =>
        windows.Where(window => window.Current.AutomationId != "MartletMainWindow").FirstOrDefault(window =>
            window.Current.AutomationId == "TrayMenu" ||
            window.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.AutomationIdProperty, "TrayMenu")) is not null);

    /// <summary>The mouse pointer in physical screen pixels, as Explorer reports it to the icon.</summary>
    private static NativePoint PhysicalPointer()
    {
        var previous = SetThreadDpiAwarenessContext(PerMonitorAwareV2);
        try
        {
            GetCursorPos(out var pointer);
            return pointer;
        }
        finally { if (previous != 0) SetThreadDpiAwarenessContext(previous); }
    }

    private static bool Running(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch (ArgumentException) { return false; }
    }

    /// <summary>The attached process's (hidden) notification-area window, or 0.</summary>
    private static nint TrayWindow(int pid)
    {
        nint found = 0;
        EnumWindows((handle, _) =>
        {
            GetWindowThreadProcessId(handle, out var owner);
            if (owner != pid || GetWindowTextLength(handle) != TrayWindowTitle.Length) return true;
            var title = new System.Text.StringBuilder(TrayWindowTitle.Length + 1);
            GetWindowText(handle, title, title.Capacity);
            if (title.ToString() != TrayWindowTitle) return true;
            found = handle;
            return false;
        }, 0);
        return found;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint { public int X, Y; }

    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetCursorPos(out NativePoint point);
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostMessage(nint window, int message, nint wParam, nint lParam);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool AllowSetForegroundWindow(int processId);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern nint GetProp(nint window, string name);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowTextLength(nint window);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(nint window, System.Text.StringBuilder text, int count);

    private static AutomationElement[] Windows(int pid)
    {
        // UIA's desktop-root traversal can omit live windows while unrelated WPF
        // windows are closing. Discover HWNDs first, then query only this process
        // and its own character renderer (the overlay, its menu and speech bubble).
        var owners = RendererProcesses(pid).Append(pid).ToHashSet();
        var handles = new List<(nint Handle, int Owner)>();
        if (!EnumWindows((handle, _) =>
            {
                GetWindowThreadProcessId(handle, out var owner);
                if (owners.Contains((int)owner) && IsWindowVisible(handle)) handles.Add((handle, (int)owner));
                return true;
            }, 0))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        return handles.Select(window => WindowForProcess(window.Owner, window.Handle)).ToArray();
    }

    private const string RendererExecutable = "Martlet.Avatar.RendererHost.exe";

    /// <summary>The attached desktop's character renderers: its Martlet.Avatar.RendererHost child processes. Another
    /// Martlet's renderer (your own profile's) is never included.</summary>
    private static int[] RendererProcesses(int pid)
    {
        var children = new List<int>();
        var snapshot = CreateToolhelp32Snapshot(0x2, 0);
        if (snapshot == -1) return [];
        try
        {
            var entry = new ProcessEntry { Size = Marshal.SizeOf<ProcessEntry>() };
            for (var more = Process32FirstW(snapshot, ref entry); more; more = Process32NextW(snapshot, ref entry))
                if (entry.ParentProcessId == pid && string.Equals(entry.ExeFile, RendererExecutable, StringComparison.OrdinalIgnoreCase))
                    children.Add((int)entry.ProcessId);
        }
        finally { CloseHandle(snapshot); }
        if (children.Count == 0) return [];
        // A parent's process ID can be reused; its real children started after it.
        static DateTime? Started(int id)
        {
            try
            {
                using var process = Process.GetProcessById(id);
                return process.StartTime;
            }
            catch (Exception error) when (error is ArgumentException or InvalidOperationException or Win32Exception) { return null; }
        }
        return Started(pid) is { } parent ? children.Where(id => Started(id) >= parent).ToArray() : [];
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ProcessEntry
    {
        public int Size, Usage;
        public uint ProcessId;
        public nint DefaultHeapId;
        public int ModuleId, Threads;
        public uint ParentProcessId;
        public int PriorityBase, Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string ExeFile;
    }

    [DllImport("kernel32.dll", SetLastError = true)] private static extern nint CreateToolhelp32Snapshot(uint flags, uint processId);
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Process32FirstW(nint snapshot, ref ProcessEntry entry);
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Process32NextW(nint snapshot, ref ProcessEntry entry);
    [DllImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CloseHandle(nint handle);

    internal static AutomationElement WindowForProcess(int pid, nint handle)
    {
        RequireOwnedVisibleWindow(pid, handle);
        var window = AutomationElement.FromHandle(handle);
        RequireOwnedVisibleWindow(pid, handle);
        if (window.Current.ProcessId != pid || window.Current.NativeWindowHandle != unchecked((int)handle))
            throw new InvalidOperationException("The Martlet window changed during discovery.");
        return window;
    }

    private static void RequireOwnedVisibleWindow(int pid, nint handle)
    {
        _ = GetWindowThreadProcessId(handle, out var owner);
        if (owner != pid || !IsWindowVisible(handle))
            throw new InvalidOperationException("The Martlet window closed or changed during discovery.");
    }

    private delegate bool EnumWindowsCallback(nint window, nint parameter);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(EnumWindowsCallback callback, nint parameter);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint window, out uint processId);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(nint window);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowEnabled(nint window);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsIconic(nint window);

    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();

    private static IEnumerable<AutomationElement> Elements(AutomationElement window) =>
        window.FindAll(TreeScope.Descendants,
            new PropertyCondition(AutomationElement.IsControlElementProperty, true))
            .Cast<AutomationElement>().Where(element => !string.IsNullOrEmpty(element.Current.AutomationId));

    private static IEnumerable<(AutomationElement Window, AutomationElement Element)> Controls(AutomationElement[] windows) =>
        // WPF can also expose an owned native dialog beneath its owner's UIA tree.
        // Deduplicate element identity, not IDs: two different controls remain ambiguous.
        windows.SelectMany(window => Elements(window).Select(element => (Window: window, Element: element)))
            .DistinctBy(control => control.Element);
}
