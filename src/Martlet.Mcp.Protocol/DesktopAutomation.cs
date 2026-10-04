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
        "OpenTroubleshooting", "OpenSetup", "OpenAudioSetup", "OpenLiveConversation",
        "OpenConfigurationRecovery", "RefreshDiagnostics",
        "SetupClose", "AudioClose", "CloseLive", "SupportClose",
        "RecoveryClose", "SupportFreeze", "SupportClear",
        "NavHome", "NavDevices", "NavCompanion", "NavDiagnostics", "NavSettings", "TourSkip", "TourBegin", "TourBack", "DiagnosticsSection",
        "OpenPeople", "OpenPrompts", "DeviceFactsSection", "DeviceReachSection", "DeviceRolesSection", "HealthRecheck", "LogsRefresh",
        // The MCP directory's Close and its optional-settings section only close or expand; opening it, searching and Load more
        // send a request to the directory, and Install writes mcp.json and starts a server, so those need --allow-ui-effects.
        "McpDirectoryClose", "McpDirectoryOptional",
        // The talk window's Stop (Esc) only stops work (a reply, a recording, vision); it starts nothing and never pauses listening.
        // Refresh context only forgets the exchanges kept in mind for the next reply; it sends nothing and stops nothing.
        "LiveStop", "LiveRefreshContext",
        // Companion › Replies' Open Deep thinking only opens that page.
        "RepliesOpenDeepThinking",
        // A tool call's Deny in the talk window only declines the waiting call (an MCP tool or a terminal command); it runs
        // nothing. Allow once and Always allow run it, so they need --allow-ui-effects.
        "LiveToolDeny",
        // Add a computer: opening the wizard, moving between its steps and choosing how a host is reached only change what it
        // shows; its Set up, Pair and role buttons do the work.
        "AddComputer", "OpenHosts", "HostsStepWhere", "HostsStepInstall", "HostsStepPair", "HostsStepRoles", "HostsBack", "HostsNext",
        "HostsClose", "HostsEnterCode", "HostMethodThisPc", "HostMethodSshDocker", "HostMethodSshNative", "HostMethodOnHost",
        "HostCommandSection", "PairCommandSection", "DeviceIdSection",
        // The notification-area menu (ui_tray "menu"): Open Martlet only shows the window, Talk to Martlet opens the talk window
        // like OpenLiveConversation, Pause Martlet only stops work and End the conversation closes the talk window like CloseLive.
        // Start listening, Resume Martlet, the character, the startup and closing choices and Exit need --allow-ui-effects.
        "TrayOpen", "TrayTalk", "TrayPause", "TrayEndTalk",
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
        // Apps and API keys: Cancel closes the create dialog without making a key, and Done closes the dialog that showed a new
        // key once. Create API key, Create key, Copy (the clipboard) and Revoke change things, so they need --allow-ui-effects.
        "ApiKeyCreateCancel", "ApiKeyCreatedDone",
        // Personality, Character, Lorebooks and Memory open their windows (the character itself doesn't show), and Done/Close
        // closes them. Those windows save each change on their own as it is made (edits need --allow-ui-effects), so closing
        // never writes anything that wasn't already changed. The Character window's sections only expand.
        "OpenCompanion", "OpenAvatar", "OpenLorebooks", "OpenMemory", "CompanionClose", "AvatarClose", "LorebookClose", "MemoryClose",
        "AvatarAdvanced", "RemoteHostSection",
        // Companion › Memory's Open conversation history opens the record's window, Close closes it, and Search and Show all
        // only filter what it lists (from memory; nothing is written). Its two choices save conversation-history.json, typing a
        // search is ui_set_text, and Delete asks first; those need --allow-ui-effects.
        "OpenHistory", "HistoryClose", "HistorySearchRun", "HistoryShowAll",
        // The problem dialog's Close only closes it; its Open logs folder (Explorer) and every Copy button (the clipboard) need
        // --allow-ui-effects.
        "ProblemClose",
        // Add a character's Cancel only closes the dialog; Add a character, Use and Remove change things.
        "CharacterModelAddCancel",
        // Add a voice: Add another recording only adds an empty recording row to the dialog (F5AddVoiceDrop-n removes row n);
        // nothing is read or saved until Add voice. Opening the dialog (F5AddVoice), typing, Fill in the words (F5AddVoiceFill
        // runs speech-to-text, which may send the recording to the Listening host) and Add voice need --allow-ui-effects.
        "F5AddVoiceMore"
    };
    /// <summary>Choosing a Companion page in its side list only shows that page; Devices map nodes ("Node-this-pc",
    /// "Node-host:gpu-1") and the problem card's Show buttons only select a device and show its details; a job's
    /// "Where it runs" options ("Place-Voice-Computer") only show that place's choices, which their own buttons commit, and
    /// Voice engine's computer pills ("SpeakingHost-gpu-pc") only show that computer's engines. Home's
    /// Health tiles ("HealthCheck-thinking") and its passive fixes ("HealthOpen-voice-setup-open-voice", "HealthOpen-crash-dismiss")
    /// only open the page where something changes, or hide the item. Diagnostics' filters ("LogLevel-errors", "LogSource-all",
    /// "LogPart-gateway") only filter the shown lines, and selecting a line ("LogEntry-0") only shows it in full. An MCP directory
    /// result ("McpDirectoryResult-io.github.upstash/context7") only shows that server's details.</summary>
    private static readonly string[] SafeClickPrefixes = ["CompanionTab-", "Node-", "CoverageShow-", "Place-", "SpeakingHost-", "HealthCheck-", "HealthOpen-",
        "LogLevel-", "LogSource-", "LogPart-", "LogEntry-", "McpDirectoryResult-", "F5AddVoiceDrop-",
        // A background job's Cancel in the talk window ("LiveJobCancel-think-1") only stops that job: it sends, saves and starts
        // nothing (the next thing you say tells Martlet you stopped it).
        "LiveJobCancel-",
        // Companion › Deep thinking's "Where it thinks" options ("DeepPlace-Computer", "DeepPlace-Off") only show that place's
        // card; its own Use and Turn off buttons commit (and need --allow-ui-effects).
        "DeepPlace-",
        // People's "What Martlet remembers about them" ("PeopleMemories-3") only opens Memory showing that voice's facts.
        "PeopleMemories-"];
    // Read-only status text. Text blocks and buttons have no value, so their accessible name (a text block's text) is returned.
    private static readonly HashSet<string> SafeValues = new(StringComparer.Ordinal)
    {
        "FoundationStatus", "PipelineStatus", "LocalAudioStatus",
        "LiveStatus", "LiveMic", "LiveVision", "LiveVisionStatus", "LiveContext", "AudioResult", "SetupActivity", "RecoveryResult", "SupportResult",
        // Home's Start talking reads "Show conversation" while a conversation runs (the talk window open, or hidden while Martlet
        // listens); Home's Start listening / Stop listening button and its listening indicator ("Listening. Just start talking.",
        // "Hearing you…", "Not listening" or why Martlet can't listen).
        "OpenLiveConversation", "HomeListen", "HomeListeningStatus",
        "PeopleStatus", "PeopleSyncStatus", "PeopleVoiceCount", "ListenParakeetStatus", "SetupCharacterView", "SetupCharacterSpeechDisplay",
        // Where the character's speech bubble goes: following the character or in one place, and its pixel offsets.
        "SetupCharacterBubblePlacement", "SetupCharacterBubbleOffsetX", "SetupCharacterBubbleOffsetY",
        "SetupCharacterNow", "SetupCharacterNowProblem",
        // Whether the character's position is locked and where (Companion › Character, in device-independent pixels), and the
        // lock buttons' labels, which carry the state: Home's ToggleCharacterLock ("Lock character position" / "Unlock
        // character position"), Companion's SetupCharacterLock ("Lock position" / "Unlock position") and the overlay menu's
        // CharacterLockPosition ("Lock position" / "Position locked: unlock in Martlet"). Clicking any of them saves
        // character-placement.json, so it needs --allow-ui-effects.
        "SetupCharacterPlacement", "ToggleCharacterLock", "SetupCharacterLock", "CharacterLockPosition",
        // The overlay menu's CharacterMuteVoice, whose label carries whether Martlet's voice is muted ("Mute voice" / "Unmute
        // voice"). Clicking it saves talk-preferences.json (Speak Martlet's replies aloud), so it needs --allow-ui-effects.
        "CharacterMuteVoice",
        // What the showing character's model drives (controls, textures and any downscaling, blink and mouth parameters,
        // motions, physics; parameter IDs only, never paths), on Companion › Character and in the character window, which
        // also shows why a chosen model couldn't load; and the character window's status line.
        "SetupCharacterModel", "AvatarModelInfo",
        "LipSyncNow", "LipSyncNowProblem", "LipSyncOwnTitle", "LipSyncOwnState", "LipSyncDockerTitle", "LipSyncDockerAbout", "LipSyncLoudnessTitle",
        // The selected device, its status and, when that status is a button ("Update available"), what clicking it does
        // ("Update available: Update to Martlet 0.40.0"). Clicking SelectedDeviceHealthAction updates the host, so it needs
        // --allow-ui-effects.
        "SelectedDevice", "SelectedDeviceHealth", "SelectedDeviceHealthAction", "ClusterStatus",
        // Settings for all devices: whether Martlet's settings are the same on the paired hosts (how many, when last checked, what
        // was last taken from another computer) and the settings this PC can't follow yet with why (never values or keys). Its
        // SettingsSyncClaim button makes every computer use this PC's settings, so it needs --allow-ui-effects. MemorySyncStatus:
        // how many facts Martlet remembers, on how many hosts they are the same, when checked and how many were taken from or
        // forgotten on other computers (never a fact). MemoryFactStatus (the Memory window): how many facts it remembers, how
        // many belong to people Martlet knows by voice or to forgotten voices, how many the Show choice lists, and what the
        // last action did (never a fact or a name).
        "SettingsSyncStatus", "SettingsSyncWaiting", "MemorySyncStatus", "MemoryFactStatus",
        // Companion › Memory › Conversation history: whether Martlet keeps a record and may search it, and what the record holds
        // (conversations, exchanges, since when); the history window's status line (counts, or what a search found). Never what
        // was said: the window's list and text (HistoryConversations, HistoryExchanges) are not readable values.
        "HistoryStatus", "HistoryWindowStatus",
        // The selected paired host's Martlet release as this PC knows it (from its checks and the release it announces on each
        // network sync: "0.22.0, up to date", "Needs update from 0.21.0 to 0.22.0") and what this PC last did to update it.
        "SelectedDeviceRelease", "SelectedDeviceUpdate",
        "VisionStatus", "VisionDisclosure", "TalkHearVoiceStatus", "SetupCloudHint-Thinking", "SetupLocalRecommendation", "SetupProviderHint", "F5VoicesStatus",
        // Companion › Vision › Where the character looks (its VisionGaze-Mouse and VisionGaze-Martlet choices save
        // talk-preferences.json, so they need --allow-ui-effects): what the character's eyes follow and why; and the talk window's
        // line on it while Martlet decides (what it looks at now and the last time it looked away; never what is on screen).
        "VisionGazeStatus", "LiveGaze",
        // Companion › Listening › Hear how you say it: what Test hearing does (and whether it stays on this PC) or what the last test
        // found (the model's one-word answer, never anything said). Clicking TalkHearVoiceTest sends the Thinking model a test
        // recording (a provider request), so it needs --allow-ui-effects and a model on this PC.
        "TalkHearVoiceTestStatus",
        // Companion › Voice › Voices: whether the voice list is shared with the paired Martlet computers, with how many and when,
        // and why Add a voice couldn't add a recording (never the typed name, transcript or file path); Add a voice's line on
        // its recordings (how many, how long joined, or which one Martlet can't use; never paths or words), under each
        // recording's file, what Martlet found in it (its kind, length and whether Martlet converts it) or why it can't be used
        // ("F5AddVoiceRecording", then "F5AddVoiceRecording-2" and so on in SafeValuePrefixes), and its intro, which names the
        // speech-to-text that fills in the words (or how to get one). Each recording's F5AddVoiceHeard line reads through the
        // prefix below.
        "F5VoicesShared", "F5AddVoiceProblem", "F5AddVoiceRecordings", "F5AddVoiceRecording", "F5AddVoiceAbout",
        // Companion › Character › Your characters: how many characters of the owner's own and what this PC shows (never a
        // name), whether they are shared with the paired Martlet computers (with how many and when), and why Add a character
        // couldn't add a model (never the typed name or file path).
        "CharacterModelsStatus", "CharacterModelsShared", "CharacterModelAddProblem",
        // Companion › Character › Emotes and motions: how many the shown model has and who named them, the Thinking model's
        // naming progress, the tags offered to replies and what follows the voice's cues, the last one played (model-authored
        // names only) and whether edits saved. Each row's name and kind (CharacterActionName-<n>, a model-authored name).
        "CharacterActionsStatus", "CharacterActionsNaming", "CharacterActionsOffered", "CharacterActionsLast", "CharacterActionsSaveState",
        // Companion › Listening › Speakers and echo: whether echo reduction is on and how the last listen went (or why it couldn't
        // run). The TalkReduceEcho check box saves the choice, so it needs --allow-ui-effects.
        "TalkReduceEchoStatus",
        // Companion › Listening › How you talk: what talking over Martlet takes (real words; never a hum, a cough, laughter, a
        // quick "yeah" or what this PC plays). Fixed text. Word check: the chosen option (Relaxed, Normal or Sensitive; choosing
        // one with ui_select saves talk-preferences.json, so it needs --allow-ui-effects) and its fixed explanation.
        "TalkBargeInAbout", "TalkWordCheck", "TalkWordCheckAbout",
        // Companion › Listening › Watch along: whether Martlet also hears what this PC plays and whether its own voice is left
        // out (TalkHearPc saves the choice, so it needs --allow-ui-effects); and the talk window's line on it (hearing the PC
        // now, or why it can't). Never what was heard.
        "TalkHearPcStatus", "LivePcAudio",
        // Companion › Voice › Voice engine: the voice engines the speaking computer still runs besides the one that speaks
        // (SpeakingEngineOthers; its SpeakingEngineRelease button stops them, so it needs --allow-ui-effects) and, under Another
        // of your computers, that the shown computer isn't reachable (SpeakingHostStatus). Each engine row reads through the
        // VoiceEngine prefix below.
        "SpeakingEngineOthers", "SpeakingHostStatus",
        "SetupOllamaStatus", "SetupLocalModelTest", "HostRunStatus", "RepliesNow", "AppUpdateStatus", "AppCurrentVersion",
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
        // background work line (each job's id, state and time, and when it is brought up; never what a job is about: LiveJob-<id>
        // holds that).
        "DeepThinkingNow", "DeepThinkingParallel", "ThinkLongerStatus", "ThinkLongerEffort", "ThinkLongerTime", "ThinkLongerPerHour",
        "ThinkLongerDelivery", "DeepThinkingHosts", "DeepThinkingLocalStatus", "DeepThinkingLocalFit", "DeepThinkingSameStatus",
        "DeepThinkingKeyStatus", "LiveJobs",
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
        "SetupCloudKeyStatus-Thinking", "SetupCloudKeyStatus-Voice", "SetupCloudKeyStatus-Listening",
        "StageTitle", "StageText", "HealthTitle", "HealthSummary", "HealthAllClear",
        "LogSummary", "LogHostStatus", "LogHostChoice", "LogDetail",
        "HostStatus", "PairedHost", "PairCodeTitle", "PairCodeHelp", "HostRunPairAddress", "NetworkStatus",
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
        // Settings › Startup and closing (what closing does and whether Windows starts Martlet), and the notification-area menu's
        // status line (Martlet is running, listening, paused or watching).
        "BackgroundStatus", "TrayStatus",
        // Settings › Appearance: the palette (Pink light, Rose dark, Character light or dark, or Character light or dark by
        // Thinking; menus and every window follow it) and its status line; the character's colors (how many and where the accent
        // comes from, or why they couldn't be read; never its name) and the Thinking model's palettes (made when and from what,
        // its reason and the colors it chose, or how asking went). AppearanceColor-<n> and AppearancePreview-<id> read through
        // the prefixes below. AppearanceThinkingMake sends the character's colors, name and picture to the Thinking model, so it
        // needs --allow-ui-effects. Who the character is (AppearanceCharacterAbout, typed by the owner) and what Thinking is told
        // (AppearanceIdentity) carry its name, so their text is never returned.
        "AppearanceTheme", "AppearanceStatus", "AppearanceCharacterStatus", "AppearanceThinkingStatus", "AppearanceThinkingMake",
        // What this PC is for: the navigation rail's "Companion PC" or "Host PC", and Settings' line describing that role.
        "DeviceRoleSummary", "DeviceRoleText",
        // The host dashboard's status under its icon ("Host is running", "Needs Windows restart", "Waiting for Docker Desktop", ...), its
        // steps' heading ("This host is ready" or "Get this host running") and the line under it (how many steps are left and
        // the next one, or "All set", and when Martlet last checked).
        "HostServiceStatus", "HostStepsHeading", "HostStepsSummary",
        // The confirmation and host-input dialogs' Copy buttons read "Copy", then "Copied" (or "Couldn't copy") for a few
        // seconds after a click; never what they copied. The problem dialog's heading (its report, ProblemText, can hold paths).
        "ConfirmationCopy", "HostInputCopy", "ProblemHeading",
        // Exiting: the closing panel's step ("Stopping your tool servers...") and, once closing is slow, what Exit now
        // interrupts; the questions an exit asks first (what Martlet is still busy with: work kinds, run window titles,
        // a host ID or an update version, never paths, keys or conversation text) and before Exit now (the step).
        // ClosingExitNow and the dialogs' ConfirmationYes exit Martlet, so they need --allow-ui-effects.
        "ClosingStatus", "ClosingSlow", "ExitBusyQuestion", "ExitNowQuestion",
        // Companion › Thinking › This PC's Use Ollama on this PC, for a model Ollama doesn't have yet: the download question
        // (model tag, its size when Martlet knows it and what Thinking keeps using until it's ready). ConfirmationYes downloads
        // it, so it needs --allow-ui-effects.
        "LocalModelDownloadQuestion"
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
    /// never the key or its verifier); a host role's choices in its Add dialog ("HostInput-choice.A2F_ENGINE" reads "local";
    /// never its secret fields), the terms that follow a variant choice ("HostInputTerms-A2F_ENGINE") and each Companion › Prompts
    /// prompt's state ("PromptState-reply_length" reads "Edited. About 82 tokens." or, while it saves, "Edited. About 82 tokens.
    /// Saving..."; never the prompt text);
    /// and the Copy button on every read-only text box ("Copy-HostRunOutput" reads "Copy", or "Copied" for a few seconds after a
    /// click; never the text it copies).</summary>
    private static readonly string[] SafeValuePrefixes = ["DeviceComponent-", "DeviceComponentDetail-", "F5VoiceRow-", "F5VoiceDetail-", "F5AddVoiceRecording-", "F5AddVoiceHeard", "CharacterModelState-", "CharacterActionName-", "VoiceEngine", "SpeakingHost-",
        "StepDetail-", "StepState-", "Step-",
        "HostChoice",
        "HealthIssue-", "HealthCheck-", "LogEntry-", "LogSource-", "NearbyItem-", "NetworkMember-", "NetworkJoin-", "NetworkPaired-", "ApiKeyRow-", "SmartHomeFound-", "SmartHomeHost-",
        "SmartHomeDevice-", "SmartHomeUpdate-", "HostInput-choice.", "HostInputTerms-", "PromptState-", "Copy-", "Node-",
        // Companion › Deep thinking: each paired computer's line ("DeepThinkingHost-diva" reads "diva: Ollama runs gemma4:27b.").
        "DeepThinkingHost-",
        // Settings › Appearance: each of the character's main colors ("AppearanceColor-0" reads "#2B3440 31% dark grayish blue") and
        // each character palette's colors by role ("AppearancePreview-rules-dark" reads "Character dark: Canvas #1B1F26, ...",
        // or "...: not made yet" for a Thinking palette not made).
        "AppearanceColor-", "AppearancePreview-",
        // Companion › Listening › Parakeet in Martlet: each model's title with its tags ("ListenParakeetModel-parakeet-tdt-110m-en"
        // reads "Fastest in English  ·  recommended") and its line ("ListenParakeetModelState-parakeet-tdt-110m-en" reads
        // "Parakeet TDT 110M (English). Replies start sooner: ... Downloads once: 477 MB."). Its SetupListenParakeet-<model>
        // button downloads (after a confirmation) and switches Listening, so it needs --allow-ui-effects.
        "ListenParakeetModel"];
    private int? processId;

    private static bool IsSafeClick(string id) =>
        SafeClicks.Contains(id) || SafeClickPrefixes.Any(prefix => id.StartsWith(prefix, StringComparison.Ordinal));

    private static bool IsSafeValue(string id) =>
        SafeValues.Contains(id) || SafeValuePrefixes.Any(prefix => id.StartsWith(prefix, StringComparison.Ordinal));

    internal object Connect(int pid)
    {
        if (pid <= 0) throw new ArgumentException("A positive Martlet desktop process ID is required.");
        using var process = Process.GetProcessById(pid);
        if (!string.Equals(process.ProcessName, "Martlet.Desktop", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("The process is not Martlet.Desktop.");
        var windows = Windows(pid);
        // A Martlet in the notification area (closed to it, or started with Windows) has no visible window but its icon's window;
        // one that couldn't start shows only its problem dialog.
        if (!MainWindowVisible(windows) && !ProblemShown(windows) && TrayWindow(pid) == 0)
            throw new InvalidOperationException("The Martlet desktop window is not visible in this interactive session.");
        processId = pid;
        return new { processId = pid, windows = windows.Select(window => window.Current.Name).ToArray(), inTray = !MainWindowVisible(windows) };
    }

    internal object Snapshot(bool layout = false)
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
            controls = Controls(windows).Select(control =>
            {
                var (window, element) = control;
                var id = element.Current.AutomationId;
                // Status text blocks expose their text as the accessible name; a status button's name carries its state, a
                // list item's (a Diagnostics log line) its text and a filter pill's (Diagnostics' "From: This PC (...)") its choice.
                var value = !IsSafeValue(id) ? null
                    : element.TryGetCurrentPattern(ValuePattern.Pattern, out var pattern) ? ((ValuePattern)pattern).Current.Value
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

    internal async Task<object> ClickAsync(string id)
    {
        if (!allowEffects && !IsSafeClick(id))
            throw new InvalidOperationException("This control requires an operator to start MCP with --allow-ui-effects.");
        var element = Find(id);
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

    internal const int MaximumMove = 10_000;

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

    private AutomationElement Find(string id)
    {
        if (string.IsNullOrWhiteSpace(id)) throw new ArgumentException("A control automation ID is required.");
        var matches = Controls(ConnectedWindows()).Select(control => control.Element)
            .Where(element => element.Current.AutomationId == id)
            .Take(2).ToArray();
        return matches.Length switch
        {
            1 => matches[0],
            0 => throw new ArgumentException($"Control '{id}' was not found. Refresh the UI snapshot."),
            _ => throw new ArgumentException($"Control '{id}' is ambiguous across open windows.")
        };
    }

    private AutomationElement[] ConnectedWindows()
    {
        if (processId is not int pid) throw new InvalidOperationException("Connect to a running Martlet desktop first.");
        using var process = Process.GetProcessById(pid);
        if (!string.Equals(process.ProcessName, "Martlet.Desktop", StringComparison.OrdinalIgnoreCase))
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
