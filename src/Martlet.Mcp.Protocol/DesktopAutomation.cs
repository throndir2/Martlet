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
        "OpenTroubleshooting", "OpenAudioSetup", "OpenLiveConversation",
        "OpenConfigurationRecovery",
        // Troubleshooting › Refresh status: the same read-only status probes Martlet runs at start.
        "SupportRefresh",
        "AudioClose", "CloseLive", "SupportClose",
        "RecoveryClose", "SupportFreeze", "SupportClear",
        "NavHome", "NavDevices", "NavCompanion", "NavConversations", "NavCreations", "NavDiagnostics", "NavSettings", "TourSkip", "TourBegin", "TourBack",
        // The welcome wizard: Look again only asks the local network which Martlet desktops answer (as Add a computer's Find
        // again does), Enter an address opens Add a computer, Next on the hardware step and the two preference cards only move
        // on and show the suggestion. Choosing a network saves the device role, Join asks the other computer, Use these
        // suggestions and Skip the key set up and install, Save key stores a key and Open build.nvidia.com opens the browser, so
        // those need --allow-ui-effects.
        "WizardScanAgain", "WizardJoinManual", "WizardSpecsNext", "WizardPreferLocal", "WizardPreferOnline",
        "OpenPeople", "OpenPrompts", "DeviceFactsSection", "DeviceReachSection", "DeviceRolesSection", "HealthRecheck", "LogsRefresh",
        // Devices' Map and List only switch how the devices show.
        "DevicesViewMap", "DevicesViewList",
        // Home's Configuring indicators (companion and host PC) only open the Devices map.
        "HomeConfiguring", "HostConfiguring",
        // The MCP directory's Close and its optional-settings section only close or expand; opening it, searching and Load more
        // send a request to the directory, and Install writes mcp.json and starts a server, so those need --allow-ui-effects.
        "McpDirectoryClose", "McpDirectoryOptional",
        // The talk window's Stop (Esc) only stops work (a reply, a recording, vision, a song); it starts nothing and never pauses
        // listening. Refresh context only forgets the exchanges kept in mind for the next reply; it sends nothing and stops
        // nothing. Stop singing only ends the song playing (musically). Nothing in the talk window plays a song. Its background
        // tasks chip (LiveTasks) and the task list's close button (LiveTasksClose) only open and close the list.
        "LiveStop", "LiveRefreshContext", "LiveSongStop", "LiveTasks", "LiveTasksClose",
        // Companion › Touch › Touch zones' Stop only stops finding zones; it sends nothing (the zones found until then were
        // already saved). Zoom in, Zoom out and Reset zoom only change how large the zone map shows the picture (TouchZonesZoom
        // says how far); they save nothing.
        "TouchZonesStop", "TouchZonesZoomIn", "TouchZonesZoomOut", "TouchZonesZoomReset",
        // Companion › Emotes and motions › Combos: Add a combo only adds an empty row. Nothing saves until the row has
        // a tag and parts, and typing them needs --allow-ui-effects.
        "CharacterCombosAdd",
        // Companion › Replies' Open Deep thinking only opens that page.
        "RepliesOpenDeepThinking",
        // Companion › Check-ins: Open Thinking pool only opens that page. Each check-in's On box, Every and Its answer choices,
        // the fact, condition and trigger boxes (CheckInFact-<id>-<fact>, CheckInWhen-<id>-<condition>,
        // CheckInTrigger-<id>-<trigger>), the name and task boxes, a
        // built-in check-in's prompt box (CheckInPrompt-<id>) and its Use built-in settings (CheckInReset-<id>), Copy as your
        // own (CheckInCopy-<id>), Add a check-in and Remove save check-ins.json or the prompts, and Check now
        // (CheckInRun-<id>) sends the check to a Thinking pool member, which may be a paid provider, so they need
        // --allow-ui-effects.
        "CheckInsOpenPool",
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
        // The setup advisor (Home's Plan a setup from scratch): opening it, moving between its steps, picking a goal and
        // closing it only change what it shows (the answers stay in memory); its plan's Install on this PC buttons do the work.
        "OpenSetupAdvisor", "AdvisorBack", "AdvisorNext", "AdvisorClose", "GoalBalanced", "GoalSmartest", "GoalFastest", "GoalPrivate",
        // Home's Recommended setup: in a Martlet network it opens the review of the recommended setup (worked out on this PC from
        // what it already knows and, when Use models your apps already run is on, the models the model apps on this PC serve,
        // asked at 127.0.0.1 only; nothing changes), and on a PC alone it opens Set it all up for me's question.
        // The review's Close only closes it. Reconfigure (RecommendedSetupApply) changes every computer, Not now
        // (RecommendedSetupCancel), Use models your apps already run (RecommendedSetupUseServedModels, and ConfirmationOption in
        // Set it all up for me's question) and Prefer models your hosts already have (RecommendedSetupPreferHostModels) save
        // recommended-setup.json, and Set it up installs, so they need --allow-ui-effects.
        "HomeRecommendedSetup", "RecommendedSetupClose",
        // The free API key prompt (FreeKeyPrompt): Add your key (the review's RecommendedSetupFreeKeyAdd, Companion › Thinking's
        // FreeKeyAdd-Thinking, Home's HealthOpen-recommended-setup-free-key) only opens Companion › Thinking at A cloud provider
        // with NVIDIA Build chosen; the review's button closes the review first. Get a free key (RecommendedSetupFreeKeyGet,
        // FreeKeyGet-Thinking, SetupCloudGetKey-Thinking, HealthFix-recommended-setup-free-key-get) opens the browser, so it is
        // not here and needs --allow-ui-effects.
        "RecommendedSetupFreeKeyAdd", "FreeKeyAdd-Thinking",
        // The notification-area menu (ui_tray "menu"): Open Martlet only shows the window, Talk to Martlet opens the talk window
        // like OpenLiveConversation, Pause Martlet only stops work, Stop listening and Stop watching only stop listening or
        // watching, and End the conversation closes the talk window like CloseLive. Start listening, Start watching, Resume
        // Martlet, the character, the startup and closing choices and Exit need --allow-ui-effects.
        "TrayOpen", "TrayTalk", "TrayPause", "TrayStopListening", "TrayStopWatching", "TrayEndTalk",
        // The character overlay (drawn by Martlet's own renderer process, whose windows ui_snapshot includes): MoveAvatar only opens
        // or closes the character's right-click menu; its Talk to Martlet, Open Martlet and Character settings only show a window
        // or page, like TrayTalk and TrayOpen, and Eyes (CharacterEyes) only opens its submenu. Eyes' choices (CharacterEyes-<choice>,
        // their checkedState says which applies) save talk-preferences.json, and the zoom, position, Let clicks pass through
        // (CharacterClickThrough), Keep on top and Hide character items need --allow-ui-effects.
        "MoveAvatar", "CharacterTalk", "CharacterOpenMartlet", "CharacterSettings", "CharacterEyes",
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
        // Companion › Memory's Open conversations goes to the Conversations page (NavConversations), and Search and Show all
        // only filter what it lists (from memory; nothing is written). Edit message only opens the editor with the selected
        // message and its Cancel closes it (nothing is saved until Save edit). Its two choices save conversation-history.json,
        // typing a search or an edit is ui_set_text, choosing an app, a conversation or a message is ui_select, and Save edit,
        // Delete message, Delete this conversation, Delete everything and Stop waiting changes write (deletes ask first; with
        // HistoryAlsoThere on they also queue changes for Telegram and Discord), so they need --allow-ui-effects.
        "OpenHistory", "HistorySearchRun", "HistoryShowAll", "HistoryEditMessage", "HistoryEditCancel",
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
        // Thinking requests (NavThinkingRequests): All, Waiting or running and With problems only filter the list, Timing by
        // type only expands, and the type box only opens (choosing a type is ui_select and only filters). Clear finished
        // (ThinkingRequestsClear) forgets requests and Copy (ThinkingRequestsCopy) writes the clipboard, so they need
        // --allow-ui-effects.
        "NavThinkingRequests", "ThinkingRequestsFilter-all", "ThinkingRequestsFilter-active", "ThinkingRequestsFilter-problems",
        "ThinkingRequestsTimingExpander", "ThinkingRequestsKind",
        // Companion › Discord › Friends and calls: What Discord allows only expands. Approve, Decline, Call, Remove, Add, the
        // may-call boxes and Update picture now change things or contact Discord, so they need --allow-ui-effects.
        "DiscordFriendsAbout",
        // Sign-in from outside: Add a computer's Join with an invite and a paired host's Sign-in from outside only open their
        // windows (the settings window reads the host's sign-in settings, never a secret), and Close closes them. Connect
        // contacts the host named in a pasted invite, Sign in pairs, and the settings window's Make an authenticator secret,
        // Save, recovery codes, Remove, Allow (SignInAllow, SignInRefusedAllow, SignInRefusedAllowFriend), Make a friend or
        // Make one of my computers (SignInAccess-<key>), Remove (SignInRemove-<key>) and Make invite change or reveal things,
        // so they need --allow-ui-effects.
        "HostsJoinWithInvite", "SignInJoinClose", "HostSignInSettings", "SignInSettingsClose",
        // Devices › Friends' Check now only reads each of your hosts' sign-in settings (never a secret) and keeps the non-secret
        // summary in friends.json; Share and Stop sharing (FriendShare-<host>-<key>, FriendStop-<host>-<key>) change who may use
        // a host, so they need --allow-ui-effects. Hosts shared with this PC's Check now (SharedHostsCheck, and each row's
        // SharedHostCheck-<host>) reads what those hosts offer this PC and, like Check all hosts, follows a model their owner
        // changed for a job of this PC there; it, Use for... (SharedHostUse-<host>-<job>) and Forget (SharedHostForget-<host>)
        // need --allow-ui-effects. The card reads them by itself when the Devices page shows.
        "FriendsCheck",
        // Companion › Discord › Martlet in your Discord calls › Check this PC only reads: it lists the playback devices' names,
        // looks for Discord's process and sets up a process loopback and closes it unstarted (nothing is recorded or played).
        // The mode's checkboxes, choices and Open camera view change things, so they need --allow-ui-effects.
        // Companion › Thinking › This PC › A model app you already use: Look again only asks this PC's loopback ports which model
        // apps answer (GET of their model lists), and Find models only asks the address typed there (on this PC only) for its
        // models. Neither saves, sends a prompt or starts anything.
        "LocalServersScan", "LocalServerFind",
        // Companion › Thinking's Image model and Audio model links (beside ThinkingSenses) only open Vision or Hearing, and
        // Listening's link to Hearing only opens it.
        "ThinkingOpenImageModel", "ThinkingOpenAudioModel", "ListeningOpenHearing",
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
        // An option picker's choice ("Picker-VoiceEngine-chatterbox", "Picker-Pictures-Off") only shows that option's details
        // below the list, its Compare ("PickerCompare-VoiceEngine") only shows or hides the table, and its Show N more
        // ("PickerMore-VoiceEngine") only shows or hides the rows past the first four; the details' own button commits (and needs
        // --allow-ui-effects). MainWindow.OptionPicker.cs.
        "Picker-", "PickerCompare-", "PickerMore-",
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
        // Companion › Pictures' and Reading's computer pills ("PicturesHost-this-pc", "ReadingHost-this-pc"), in their role's
        // details, only show where it stands on that computer; their own buttons commit. Reading's model pills
        // ("ReadingModel-ppocrv5-mobile", "ReadingModel-ppocrv5-server", "ReadingModel-rapidocr-ppocrv4") only show that model's
        // details and the button that sets it up or switches to it (Set up and Switch need --allow-ui-effects).
        "PicturesHost-", "ReadingHost-", "ReadingModel-",
        // People's "What Martlet remembers about them" ("PeopleMemories-3") only opens Memory showing that voice's facts.
        "PeopleMemories-",
        // Creations: choosing a creation in the list ("Creation-3f2a9c1b7d04", its short id) only shows its text and details.
        // There is no Play, Show or Activate; its Rename and Delete change it on every computer, so they need --allow-ui-effects.
        "Creation-",
        // Companion › Touch › Touch zones: a zone's More ("TouchZoneMore-3", Less while open) only shows or hides its box and
        // Delete; it saves nothing.
        "TouchZoneMore-",
        // Background tasks: a task's Show or Show output ("TaskShow-3") only shows its run window again, or a finished task's
        // kept output.
        "TaskShow-",
        // Thinking requests: a request's row ("ThinkingRequest-tr-3") only selects it and shows it in full (ThinkingRequestDetail).
        "ThinkingRequest-",
        // Settings › Appearance › Custom: choosing a part ("CustomThemeRole-Accent") only shows its color in the editor.
        "CustomThemeRole-"];
    // Read-only status text. Text blocks and buttons have no value, so their accessible name (a text block's text) is returned.
    private static readonly HashSet<string> SafeValues = new(StringComparer.Ordinal)
    {
        // Martlet.Companion: its status line, headless status JSON (no secrets), guardrail refusals, engines not offered here and
        // their reasons, local-model warnings, the key-storage note and the chosen engines.
        "StatusLine", "CharacterState", "CompanionStatus", "Refusals", "NotOffered", "ThinkingWarnings", "KeyNote", "ThinkingEngine", "ListeningEngine",
        "SpeakingEngine",
        // Troubleshooting: the status report (each check's state and remedy) and the last conversation activity. No secrets.
        "SupportReport",
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
        // and the settings window's status, owner account state (name and recovery codes left), allowed identities (each one of
        // your computers or a friend's), providers and computers that signed in (device IDs, provider, subject and whether a
        // friend's; never a password, secret or recovery code).
        "SignInJoinStatus", "SignInHost", "SignInSettingsStatus", "SignInOwnerState", "SignInAllowedList", "SignInProvidersList",
        "SignInEnrolledList", "SignInRefusedList", "SignInRemovedList", "SignInOutsideWarning",
        // Devices › Friends: how many hosts are shared with how many friends and who asked ("You share 1 host with 1 friend. ...
        // Checked 14:02."), and Hosts shared with this PC: how many hosts friends share with this PC. Counts and fixed wording.
        "FriendsStatus", "SharedHostsStatus",
        // Companion › Discord's voice line: where Martlet is in Discord voice, counts of speakers heard, utterances transcribed
        // and replies spoken (never what was said), whether DAVE is on, whether libdave loaded, and the last problem.
        "DiscordVoiceStatus",
        "LiveStatus", "LiveMic", "LiveVision", "LiveVisionStatus", "LiveContext", "AudioResult", "RecoveryResult", "SupportResult",
        // Home's Start talking reads "Show conversation" while a conversation runs (the talk window open, or hidden while Martlet
        // listens or watches); Home's Start listening / Stop listening button and its listening indicator ("Listening. Just start
        // talking.", "Hearing you…", "Not listening" or why Martlet can't listen), and its Start watching / Stop watching button
        // and watching indicator ("Watching your active window.", "Taking a look…", "Not watching" or why Martlet can't see).
        "OpenLiveConversation", "HomeListen", "HomeListeningStatus", "HomeWatch", "HomeWatchingStatus",
        // Home's Configuring indicator (companion Home, and HostConfiguring on a host PC's Home): a run applying the recommended
        // setup ("Configuring your computers: 1 of 3 finished. gpu-box: Installing Chatterbox Turbo (2 of 4).", or how it
        // ended), a host role this PC changes or this PC following a plan change. Machine IDs, role names and counts only.
        "HomeConfiguring", "HomeConfiguringStatus", "HostConfiguring", "HostConfiguringStatus",
        "PeopleNow", "PeopleNowProblem", "PeopleSyncStatus", "PeopleVoiceCount", "SetupCharacterView", "SetupCharacterSpeechDisplay",
        // The Now line (what the page uses now, in one line) and its problem (what stops it) of Companion › Speech bubbles,
        // Emotes and motions, Eyes, Touch, Tools, Smart home, Discord and Messaging ("SmartHomeNow" reads "Smart home: connected
        // to Home at http://homeassistant.local:8123; Martlet may control lights, ..."). Counts, names, addresses and fixed
        // wording; never a token or what was said.
        "SpeechBubblesNow", "SpeechBubblesNowProblem", "EmotesNow", "EmotesNowProblem", "EyesNow", "EyesNowProblem", "TouchNow",
        "TouchNowProblem", "ToolsNow", "ToolsNowProblem", "SmartHomeNow", "SmartHomeNowProblem", "DiscordNow", "DiscordNowProblem",
        "MessagingNow", "MessagingNowProblem",
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
        // Whether clicks pass through the character (Companion › Character's note), and the click-through buttons' labels, which
        // carry the state: Home's ToggleCharacterClickThrough and Companion's SetupCharacterClickThrough ("Turn on click-through" /
        // "Turn off click-through") and the overlay menu's CharacterClickThrough ("Let clicks pass through" / "Stop letting clicks
        // pass through"). Clicking any of them (or the icon menu's TrayCharacterClickThrough) saves character-click-through.json,
        // so it needs --allow-ui-effects.
        "SetupCharacterClickThroughNote", "ToggleCharacterClickThrough", "SetupCharacterClickThrough", "CharacterClickThrough",
        // The overlay menu's CharacterMuteVoice, whose label carries whether Martlet's voice is muted ("Mute voice" / "Unmute
        // voice"). Clicking it saves talk-preferences.json (Speak Martlet's replies aloud), so it needs --allow-ui-effects.
        "CharacterMuteVoice",
        // Companion › Voice › Voice volume: the slider's number (0 to 100) and its label ("80%"). ui_set_range on VoiceVolume
        // saves talk-preferences.json, so it needs --allow-ui-effects.
        "VoiceVolume", "VoiceVolumeLevel",
        // Companion › Voice › Quick sounds while Martlet thinks: the check box (checkedState; off by default), the chosen delay
        // ("After 0.7 s (recommended)") and where it stands (off, being made, ready with how many clips in which voice, waiting
        // for a click with a paid cloud voice, or why they couldn't be made). Changing the box or the delay saves
        // talk-preferences.json, and VoiceQuickSoundsMake makes the clips with the voice, so they need --allow-ui-effects.
        "VoiceQuickSounds", "VoiceQuickSoundsDelay", "VoiceQuickSoundsStatus",
        // Companion › Voice › Chatterbox Original style: its four sliders' numbers (exaggeration 0.25-2, CFG weight 0-1) and what
        // is saved ("Saved on this PC. General: exaggeration 0.5, CFG weight 0.5. Expressive: ..."); each value's label is
        // ChatterboxStyleValue-<name>. ui_set_range on a slider and ChatterboxStyleReset save chatterbox-style.json, so they
        // need --allow-ui-effects.
        "ChatterboxStyle-GeneralExaggeration", "ChatterboxStyle-GeneralCfgWeight", "ChatterboxStyle-ExpressiveExaggeration",
        "ChatterboxStyle-ExpressiveCfgWeight", "ChatterboxStyleState",
        // What the showing character's model drives (controls, textures and any downscaling, blink and mouth parameters,
        // motions, physics; parameter IDs only, never paths), on Companion › Character and in the character window, which
        // also shows why a chosen model couldn't load; and the character window's status line.
        "SetupCharacterModel", "AvatarModelInfo",
        // The character overlay's drag surface reads as its last tap's hit test (zone, hit areas, drawables, bone; model-authored
        // names only, never paths); character_touch taps it.
        "MoveAvatar",
        // Companion › Lip-sync › This PC: its ways are an option picker (Picker-LipSync-Audio2Face, -Loudness, -Own); the shown
        // way's details read what this PC's graphics card means for Audio2Face (LipSyncDockerAbout) and where your own service
        // stands (LipSyncOwnState).
        "LipSyncNow", "LipSyncNowProblem", "LipSyncOwnState", "LipSyncDockerAbout",
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
        // last action did (never a fact or a name). MemoryStatus (the Memory window's bottom line): whether memory is on, saving
        // or why it can't be (fixed text, never a fact or a folder).
        "SettingsSyncStatus", "SettingsSyncWaiting", "MemorySyncStatus", "MemoryFactStatus", "MemoryStatus",
        // Companion › Memory › Conversation history: whether Martlet keeps a record and may search it, and what the record holds
        // (conversations, exchanges, since when, per app); the Conversations page's status line (counts, or what a search found) and
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
        "VisionStatus", "VisionDisclosure", "TalkHearVoiceStatus", "SetupCloudHint-Thinking", "SetupLocalRecommendation", "F5VoicesStatus",
        // Companion › Thinking's free API key tip (fixed text, or why the browser couldn't open) and its buttons' labels.
        "FreeKeyTip-Thinking", "FreeKeyAdd-Thinking", "FreeKeyGet-Thinking", "SetupCloudGetKey-Thinking",
        // Companion › Vision's Now line: whether vision is on (the default) and what Martlet looks at (your whole screen by
        // default, your active window, or a camera's name or host without its path or password) and how often it comments.
        // The VisionSource-<kind> choices are radio buttons (ui_snapshot's selected). VisionToggle is Turn vision off in the Off
        // choice's details (Picker-Vision-Off) while vision is on, and Turn vision on in the saved image model's details while it
        // is off; clicking it saves talk-preferences.json, so it needs --allow-ui-effects.
        "VisionNow", "VisionToggle",
        // Companion › Hearing's Now line (HearingNow: off and why, or what hears your voice), and HearingToggle: Turn hearing off in
        // the Off choice's details (Picker-Hearing-Off) while a model hears you, Turn hearing on in the saved audio model's details
        // after you turned it off; clicking it saves talk-preferences.json, so it needs --allow-ui-effects.
        "HearingNow", "HearingToggle",
        // Companion › Hearing › Let Thinking hear my voice: which applies (you turned it on or off, or never chosen: on while the
        // recording stays on this PC, off until you tick it when it would leave). Fixed wording; no model names beyond the
        // Thinking destination the page already shows.
        "TalkHearVoiceChoice",
        // Companion › Vision › Glances at your screen (its VisionGaze-Mouse, Keep its usual gaze, and VisionGaze-Martlet choices save
        // talk-preferences.json, so they need --allow-ui-effects): what the character's eyes follow and why; and the talk window's
        // line on it while Martlet decides (what it looks at now and the last time it looked away; never what is on screen).
        "VisionGazeStatus", "LiveGaze",
        // Companion › Vision › Screen summary over time (VisionScreenSummary is a check box; changing it saves
        // talk-preferences.json, so it needs --allow-ui-effects) and whether it runs or why not; the talk window's line on it
        // while Martlet watches: pictures kept, the last summary's age and time taken. Its help text is the last summary
        // (one or two lines on what changed on the screen, never a window title on its own).
        "VisionScreenSummary", "VisionScreenSummaryStatus", "LiveScreenSummary",
        // Companion › Hearing › Hear how you say it: what Test hearing does (and whether it stays on this PC) or what the last test
        // found (the model's one-word answer, never anything said). Clicking TalkHearVoiceTest sends the Thinking model a test
        // recording (a provider request), so it needs --allow-ui-effects and a model on this PC.
        "TalkHearVoiceTestStatus",
        // Companion › Vision's and Companion › Hearing's Now card (MainWindow.SenseModels.cs): the image or audio model in words
        // (ImageModelNow: "Use the same model as the text model (Thinking: gemma4:e2b)." or "A model of its own: Ollama on this PC
        // (qwen2.5vl:7b), chosen on 10/8/2026."), where pictures or recordings go now and why (ImageModelRoute: the routing's
        // words), what the model that takes them is known to do and where that came from (ImageModelKnown), what is sent to a
        // model of its own and where (ImageModelSent), and what Test vision or Test hearing does or last found (ImageModelTestStatus;
        // the model's one-word answer). Under Ollama on this PC: the model picked (ImageModelLocalModel), what Ollama has and what
        // each model takes (ImageModelLocalStatus), what the picked one is known to do (ImageModelLocalKnown) and whether it fits
        // beside Thinking's on the graphics card (ImageModelLocalFit). Under A cloud provider or server: the provider picked
        // (ImageModelProvider) and what the key box will do (ImageModelKeyStatus; never the key, base URL or model ID typed). Under
        // One of your computers: ImageModelHosts when none is paired, and each computer's line in SafeValuePrefixes. The same with
        // AudioModel. The Picker-Vision-<choice> and Picker-Hearing-<choice> options only show a panel; ImageModelUseThinking, ImageModelUseOther,
        // ImageModelUseLocal, ImageModelSaveCloud (with ImageModelConsent) and ImageModelUseHost-<host> save sense-models.json,
        // ImageModelPullModel downloads a model, ImageModelCheckOllama and ImageModelCheckHosts ask Ollama or the paired computers,
        // and ImageModelTest sends a test request, so they need --allow-ui-effects. Companion › Thinking's line on where pictures
        // and recordings go (ThinkingSenses).
        "ImageModelNow", "ImageModelRoute", "ImageModelKnown", "ImageModelSent", "ImageModelTestStatus", "ImageModelLocalModel",
        "ImageModelLocalStatus", "ImageModelLocalKnown", "ImageModelLocalFit", "ImageModelProvider", "ImageModelKeyStatus", "ImageModelHosts",
        "AudioModelNow", "AudioModelRoute", "AudioModelKnown", "AudioModelSent", "AudioModelTestStatus", "AudioModelLocalModel",
        "AudioModelLocalStatus", "AudioModelLocalKnown", "AudioModelLocalFit", "AudioModelProvider", "AudioModelKeyStatus",
        "ThinkingSenses",
        // Companion › Hearing › When Thinking can hear you (shown while Thinking hears your voice): which way your voice goes
        // (straight, or transcribed first) and what that means. Fixed text. TalkVoicePathStraight and TalkVoicePathTranscribeFirst
        // are radio buttons (ui_snapshot's selected); choosing one saves talk-preferences.json, so it needs --allow-ui-effects.
        "TalkVoicePathStatus",
        // Companion › Thinking › This PC › Ollama: its models are an option picker (Picker-OllamaModel-gemma4:e2b,
        // Picker-OllamaModel-Other); under Another Ollama model, the model typed (SetupLocalModel) and what any Ollama model means
        // (SetupLocalOwnModels). Typing in SetupLocalModel saves nothing, but needs --allow-ui-effects.
        "SetupLocalModel", "SetupLocalOwnModels",
        // Companion › Thinking › This PC › Model app: the apps are an option picker (Picker-LocalApp-Ollama,
        // Picker-LocalApp-lm-studio-1234, Picker-LocalApp-Address). For another app: what looking on this PC's loopback ports
        // found (each app's name, base URL and model count, or that one asks for a key), the model in the box and what the app
        // lists (LocalServerModels), what the key box will do (never the key), how to start the picked app's server and where
        // messages go (LocalServerHint), and the last Test model result. Choosing LocalServerModel with ui_select only
        // fills the fields and saves nothing, but needs --allow-ui-effects; LocalServerTest sends the model a short loopback
        // request and LocalServerUse switches Thinking, so they need it too. Martlet.Companion's Find model apps result
        // (LocalModelsFound; its FindLocalModels button fills the unsaved Thinking fields, so it needs --allow-ui-effects).
        "LocalServersStatus", "LocalServerModel", "LocalServerModels", "LocalServerKeyStatus", "LocalServerHint",
        "LocalServerTestResult", "LocalModelsFound",
        // The setup advisor: which step it shows and its plan's summary (the goal's one-line explanation).
        "AdvisorStep", "AdvisorSummary",
        // Companion › Voice › Voices: whether the voice list is shared with the paired Martlet computers, with how many and when,
        // and why Add a voice couldn't add a recording (never the typed name, transcript or file path); Add a voice's line on
        // its recordings (how many, how long joined, or which one Martlet can't use; never paths or words), under each
        // recording's file, what Martlet found in it (its kind, length and whether Martlet converts it) or why it can't be used
        // ("F5AddVoiceRecording", then "F5AddVoiceRecording-2" and so on in SafeValuePrefixes), and its intro, which names the
        // speech-to-text that fills in the words (or how to get one). Each recording's F5AddVoiceHeard line reads through the
        // prefix below.
        // Companion › Singing (under Optional extras): whether and where Martlet sings (SingingNow), and in the Singing role's
        // details (Picker-Singing-Role) where it stands on the shown computer (not set up, setting up, ready with the voice
        // matches set up there, failed with the reason, or why that computer can't sing), whether it needs a graphics card of
        // its own (SingingGpu, fixed text) and the buttons' labels (Set up, Sing on <computer>, Turn singing off in the Off
        // choice's details); the saved quality; with VevoSing chosen where it isn't set up (Picker-SingingVoiceMatch-VevoSing),
        // that it isn't (or is being added) and the Add VevoSing there button's label. Set up, Sing on, Turn singing off, Use
        // SoulX-Singer or VevoSing and Add VevoSing there need --allow-ui-effects. Songs are only performed in conversation
        // (singing_check exercises them headlessly).
        "SingingNow", "SingingState", "SingingSetUp", "SingingUse", "SingingTurnOff", "SingingGpu", "SingingQuality",
        "SingingUseSoulX", "SingingUseVevoSing", "SingingVoiceMatchState", "SingingSetUpVevo",
        // Every Companion page: its group in the side list ("HOW IT WORKS", "OPTIONAL EXTRAS", ...) and its fixed intro, which
        // starts with "Optional." on a page Martlet works without.
        "CompanionGroupTitle", "CompanionIntro",
        // Companion › Pictures: where Martlet draws now (PicturesNow), what Check or Draw a test picture found (PicturesTestState:
        // ready, why not, or the test picture's size, place and seconds), the Pictures role on the shown computer (where it
        // stands, the Set up button), the ComfyUI address and what Connect found (version, checkpoints, whether
        // Z-Image Turbo is there), the chosen workflow, a cloud provider's model ID and whether a key is saved or Thinking's is
        // used (never the key), and the buttons' labels. Pictures themselves are never returned.
        "PicturesNow", "PicturesTestState", "PicturesHostState", "PicturesSetUp", "PicturesUseHost",
        "PicturesComfyAddress", "PicturesComfyState", "PicturesComfyConnect", "PicturesWorkflow", "PicturesLoadWorkflow", "PicturesUseComfy",
        "PicturesModel", "PicturesKeyStatus", "PicturesUseCloud", "PicturesTurnOff", "PicturesCheck", "PicturesTest",
        // Companion › Reading: where Martlet reads the text on the screen (ReadingNow), the newest read while watching and the
        // Read my screen now result (ReadingLast, ReadingTestState: how many lines, which engine, milliseconds, the full-size
        // screenshot's width x height and when, or why it couldn't), whether Windows can read text here, the Reading role on the
        // shown computer (where it stands and with which model), the chosen model's note (where it runs, how accurate and its
        // download) and the buttons' labels. The text read from a real screen (ReadingTestText) is never returned.
        "ReadingNow", "ReadingLast", "ReadingTestState", "ReadingWindowsState", "ReadingHostState", "ReadingModelNote",
        "ReadingSetUp", "ReadingSwitch", "ReadingUseHost", "ReadingUseThisPc", "ReadingTurnOff", "ReadingTest",
        "F5VoicesShared", "F5AddVoiceProblem", "F5AddVoiceRecordings", "F5AddVoiceRecording", "F5AddVoiceAbout",
        // Companion › Character › Your characters: how many characters of the owner's own and what this PC shows (never a
        // name), whether they are shared with the paired Martlet computers (with how many and when), and why Add a character
        // couldn't add a model (never the typed name or file path).
        "CharacterModelsStatus", "CharacterModelsShared", "CharacterModelAddProblem",
        // Companion › Emotes and motions: how many the shown model has and who named them, the Thinking model's
        // naming progress, the tags offered to replies and what follows the voice's cues, the last one played (model-authored
        // names only) and whether edits saved. Each row's name and kind (CharacterActionName-<n>, a model-authored name), and
        // its Try button's label (CharacterActionTry-<n>: "Try", or "Turn off" while that lingering emote is on). The grey hint in
        // an empty When to use box (CharacterActionHint-<n>: Martlet's own hint that replies get, such as "nod, for yes or
        // agreement"; hidden once the owner writes one). The lingering
        // emotes on now and for how long (CharacterActionsHeld: "On now: Glasses (12 min)." or "No lingering emotes are on.").
        // Each row's "Stays on" check box (CharacterActionMode-<n>) reports its mode as checkedState; changing it saves, and Try,
        // Turn off, Clear emotes (CharacterActionsClear) and the overlay menu's Clear emotes (CharacterClearEmotes) change what
        // the character shows, so they need --allow-ui-effects.
        "CharacterActionsStatus", "CharacterActionsNaming", "CharacterActionsOffered", "CharacterActionsLast", "CharacterActionsSaveState",
        "CharacterActionsHeld",
        // Emotes and motions › Combos: how many combos the model has and the combo tags replies get ("2 combos. Replies can use 1:
        // {flustered}." or "No combos yet."). Each combo's title (CharacterComboName-<n>: "{flustered}  ·  combo", or "New combo"),
        // what it sets off (CharacterComboState-<n>: "Turns on "hearts" until {/flustered}; plays "blush" and "nod" once.", or why
        // its parts can't be read), the grey hint in its empty When to use box (CharacterComboHint-<n>: "a combination of {blush},
        // {hearts} and {nod}") and its Try button's label (CharacterComboTry-<n>: "Try", or "Turn off" while one of its lingering
        // parts is on); all four read through SafeValuePrefixes. Typing a combo's tag, parts or When to use, its on box and Remove
        // save, and Try and Turn off change what the character shows, so they need --allow-ui-effects.
        "CharacterCombosStatus",
        // Companion › Touch › Touch zones: how many zones the shown model has, how many are in use and who found them, whether
        // the Thinking model can see (and where pictures go), how Detect zones went (each step while it runs), what the last
        // detection sent (how many pictures, how large, what they showed), which zone the last touch landed in and what it
        // played, and whether edits saved. Each zone's line (TouchZoneState-<n>: its ID, parts it follows, "added by you" for a zone
        // the owner added, which Detect again looks for too, or "special to this character" for one Detect zones found as special to
        // it, and how the persona's temperament feels about it), and each entry of its reaction list, in play order
        // (TouchZoneReactionItem-<n>-<k>: "Blush  ·  emote", "Laugh  ·  sound" for a voice sound the voice makes, "laugh  ·  sound,
        // not with this voice", "F05  ·  not on this model"; TouchZoneReactionNone-<n> reads "nothing" for an empty list).
        // TouchZonesVoiceSounds says which voice makes the voice sounds and which, or why none plays, what is being made, the last
        // problem and what the last sound did (played or not, and why). ▶ on a sound (TouchZoneReactionHear-<n>-<k>) plays it,
        // so it needs --allow-ui-effects.
        // TouchZonesDetectNote says why Detect zones is off (no model that can see pictures), and TouchZonesAddNote which zones
        // Detect zones looks for, and that it also looks for anything special to the character (fixed text). TouchZonesZoom says how
        // far the zone map is zoomed in ("Zoom 2x").
        // Detect zones sends the character's pictures to Thinking, Try plays on the character, Open the pictures opens Explorer,
        // Show the picture Thinking saw is a check box and the rest save, so those need --allow-ui-effects.
        "TouchZonesStatus", "TouchZonesVision", "TouchZonesDetection", "TouchZonesLast", "TouchZonesSaveState", "TouchZonesSent",
        "TouchZonesDetectNote", "TouchZonesAddNote", "TouchZonesZoom", "TouchZonesVoiceSounds",
        // Touch zones › Start over: the level chosen to reset (TouchZonesResetLevel: Zone reactions, Zones, Touch temperament, The
        // character's own changes or Everything), what that level clears (fixed text), what the last reset did or why it couldn't,
        // and the confirmation's question (what the owner loses: zone names, counts, the persona's and custom temperaments' names,
        // a date, and what each of the character's own changes does, never why). Choosing a
        // level needs --allow-ui-effects; Reset (TouchZonesReset) opens the question and its ConfirmationYes resets, so they need
        // --allow-ui-effects too.
        "TouchZonesResetLevel", "TouchZonesResetNote", "TouchZonesResetState", "TouchZonesResetQuestion",
        // Companion › Touch › Changes the character made: how many changes of its own the active persona has in effect and what
        // the last Undo did; ReactionChange-<n> (each change in effect: what it does, until when, when and by which check-in it was
        // made) and ReactionChangeEnded-<n> (the newest that ended, and who ended them) read through SafeValuePrefixes. The
        // character's reasons (ReactionChangeWhy-<n>, ReactionChangeEndedWhy-<n>) come from the conversation and aren't values.
        // Undo (ReactionChangeUndo-<n>) and Undo all (ReactionChangesUndoAll) end changes, so they need --allow-ui-effects.
        "ReactionChangesStatus", "ReactionChangesState",
        // Companion › Eyes › Where the eyes are: where the shown model's eyes come from (the model's own meshes or eye bones,
        // the vision measurement and when it was taken, or an estimate), how measuring went (each step while it runs, or why it
        // failed) and, only when no model can see pictures, why Measure the eyes is off. Fixed text, times and counts only.
        // Measure the eyes (CharacterEyesMeasure) sends a close-up of the character's face to Thinking and Forget the measurement
        // (CharacterEyesForget) deletes it, so they need --allow-ui-effects; CharacterEyesPicture (the close-up with its boxes)
        // isn't a value.
        "CharacterEyesStatus", "CharacterEyesProgress", "CharacterEyesNote",
        // Companion › Touch › Touch temperament: who decided the active persona's temperament (built-in, the Thinking model,
        // FIXTURE - NOT AI or the owner) or which custom temperament or built-in reactions it uses instead; its help text is the
        // whole temperament in words: "head loves, torso hates, ..., intimate loves", its eyes and the parts whose touch turns them to
        // your mouse), how deciding went and whether edits saved or what a Uses, Create, Rename or Delete did (each only while it has
        // something to say). Each line's attitude (TouchTemperamentAttitude-<category or zone ID>, below) is an attitude word, its
        // TouchTemperamentReaction-/TouchTemperamentReaction2- the reactions, its TouchTemperamentLinger- and
        // TouchTemperamentLook-<category or zone ID> the seconds the first reaction stays on and the eyes then look at your mouse,
        // TouchTemperamentParts-<category ID> the parts the category covers ("Parts: mouth, left ear, ..."), TouchTemperamentAfter the
        // touches in a row before it escalates and TouchTemperamentGaze where the eyes usually go. TouchTemperamentUse is the
        // temperament the persona uses (Decided from its personality, Built-in reactions or a custom temperament's name),
        // TouchTemperamentName and TouchTemperamentNewName the custom temperament's name and the name typed for a new one,
        // TouchTemperamentCustomUsers the personas that use the custom temperament, and TouchTemperamentAddKind the part chosen to
        // give its own line. Decide (later Re-decide) from personality sends the personality to Thinking, and the rest save, so
        // they need --allow-ui-effects.
        "TouchTemperamentStatus", "TouchTemperamentDecision", "TouchTemperamentSaveState", "TouchTemperamentGaze", "TouchTemperamentAfter",
        "TouchTemperamentUse", "TouchTemperamentUseState", "TouchTemperamentName", "TouchTemperamentNewName", "TouchTemperamentCustomUsers",
        "TouchTemperamentAddKind",
        // Touch zones' Include intimate zones check box (its label names every intimate part; checkedState says whether it is on)
        // and the zone chosen to add (TouchZonesAddKind).
        "TouchZonesIntimate", "TouchZonesAddKind",
        // Companion › Eyes › Where the character looks: what the eyes do now and why (your choice, the personality's or
        // the character's own in a reply; a touch's look at your mouse; whether it may change where it looks). Its
        // CharacterGaze-<choice> radio buttons (selected) and CharacterGazeFree check box (checkedState) save
        // talk-preferences.json, so they need --allow-ui-effects.
        "CharacterGazeNow",
        // What Martlet noticed (zones with Martlet notices on) that waits for a reply and when a touch reply would start (and
        // whether a touch stopped Martlet talking), and which reply took the last touches and what the Thinking model was told
        // (zone names and the touch line, no words). Touch zones' TouchInterrupt-<choice> radio buttons (any, intimate, never;
        // selected) save talk-preferences.json, so they need --allow-ui-effects.
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
        // decided it and how long the judge and the pause took (never what was said). LiveUnprompted: how many things Martlet
        // meant to say on its own were dropped (too old, the conversation moved on, talked over) and the newest drop's ID, kind
        // and why (never its text).
        "TalkBargeInBehavior", "TalkBargeInBehaviorAbout", "LiveBargeIn", "LiveUnprompted",
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
        // under Another of your computers, that the shown computer isn't reachable (SpeakingHostStatus). The engines are an option
        // picker (Picker-VoiceEngine-<key>); the shown engine's abilities and chips read through the VoiceEngine prefix below.
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
        // Companion › Replies › Short first sentence: the chosen option (On, the default, or Off; choosing one with ui_select
        // saves it, so it needs --allow-ui-effects).
        "RepliesShortFirstSentence",
        // Companion › Replies › Adult content: the chosen option (Off, the default, or On (18+); choosing one with ui_select
        // saves it, so it needs --allow-ui-effects).
        "RepliesAdultContent",
        // Companion › Deep thinking: where a think goes and whether it can run there alongside the conversation (and why); Thinking
        // longer's state (on by default; Where it thinks › Off turns it off) or what keeps it from working, and the chosen
        // effort, time limit, hourly limit and when it shares results (choosing one with ui_select saves them, so they need
        // --allow-ui-effects); what Ollama on this PC has downloaded, whether the model typed for it fits beside Thinking's on the
        // graphics card, and what an endpoint's key field will do (never a key or
        // base URL typed). Each paired computer's line reads through DeepThinkingHost- below. In the talk window, the
        // background tasks chip (LiveTasks: "Background tasks: 1 running · 1 ready") and the task list's line under its title
        // (that tasks keep going while you talk and when finished work comes up; never what a task is about: LiveJob-<id> holds
        // that, and each task's status reads through LiveJobState- below). The song panel's line (the song's id, state,
        // position, line number and section, lead-in, vamps, ducking, or
        // where and why it stopped; never its title or words: LiveSongLine holds those).
        "DeepThinkingNow", "DeepThinkingParallel", "ThinkLongerStatus", "ThinkLongerEffort",
        "ThinkLongerDelivery", "DeepThinkingHosts", "DeepThinkingLocalStatus", "DeepThinkingLocalFit", "DeepThinkingLocalShare",
        "DeepThinkingKeyStatus", "LiveTasks", "LiveJobs", "LiveSong",
        // Companion › Thinking pool › Add a machine: the rule that computers with a Thinking model join by themselves, and which
        // computers you keep out (computer names only).
        "DeepThinkingAutoJoin",
        // Companion › Thinking pool › Machines: the member count and usable slots, the conversation's own row (it is never in the
        // pool), the guidance ("1 slot: long thinking can delay screen and sound summaries; add a second slot for the full
        // experience."), the likely-slowdown warnings (a member beside the conversation's Thinking model or the voice), the live
        // floor's line (which members start no new pool work while you talk with Martlet because they share the conversation's
        // computer; computer names only) and the conversation row's box that lets thinking longer and research run on the
        // conversation model when no machine in the pool takes long jobs (ticking it saves thinking-pool.json, so it needs
        // --allow-ui-effects). Each member's line reads through ThinkingPoolMember- below. Thinking longer's own on/off box
        // (ThinkLongerOn) saves the reply settings, so it needs --allow-ui-effects.
        "ThinkingPoolSummary", "ThinkingPoolConversation", "ThinkingPoolGuidance", "ThinkingPoolWarnings", "ThinkingPoolLiveFloor",
        "ThinkingPoolUseConversationModel", "ThinkLongerOn",
        // Companion › Thinking pool › Backup for slow replies (Backup Thinking, off by default): its box, how long a reply waits
        // for its first words before a member ticked for it is asked too (automatic or a fixed time) and its line (which members
        // may answer, and the wait now; member names only). The box and the wait save thinking-pool.json, so they need
        // --allow-ui-effects. Each member's Backup for slow replies box reads through ThinkingPoolAnswers- below.
        "ThinkingPoolBackup", "ThinkingPoolBackupDelay", "ThinkingPoolBackupStatus",
        // Companion › Thinking pool › Busy pool: whether higher-priority requests may stop lower ones, after how many stops a
        // stopped request becomes more important, how many times a failed request is tried again, and its line (the choices
        // and, since Martlet started, how many requests were stopped, made more important and tried again; counts only). The
        // box and both choices save thinking-pool.json, so they need --allow-ui-effects.
        "ThinkingPoolPreempt", "ThinkingPoolRaiseAfterStops", "ThinkingPoolRetries", "ThinkingPoolPriorityStatus",
        // Companion › Deep thinking › Web research (off by default): whether Martlet may search the web when asked and why it
        // can't yet, and its fixed disclosure of what leaves this PC. The WebResearchOn check box saves the reply settings, so it
        // needs --allow-ui-effects.
        "WebResearchStatus", "WebResearchDisclosure",
        // Companion › Check-ins: how many check-ins are on and the Thinking pool member that takes them first, or why they can't
        // run ("CheckInsNow"), and the last check-in that ran, when, on which member and what came of it in a few words
        // ("CheckInsLast"; never what was said or answered). Each check-in's line reads through CheckInStatus- below.
        "CheckInsNow", "CheckInsLast",
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
        // Companion › Listening's line on what hears you when Listening's own choice (a paired host or OpenAI) can't: the
        // Parakeet model on this PC's processor, or the one to download (host IDs and model names only). Its
        // SetupListenStandInDownload button downloads that model, so it needs --allow-ui-effects.
        "SetupJobStandIn-Listening",
        "SetupCloudKeyStatus-Thinking", "SetupCloudKeyStatus-Voice", "SetupCloudKeyStatus-Listening",
        // Companion › Voice › A cloud provider › ElevenLabs (the owner's cloned voice with tones): whether it is in use, its model,
        // on and confirmed, whether a key is saved and whether ElevenLabs asked to verify the voice (never the voice's name, its
        // voice ID or the key); what the key field will do; and the chosen model ("Eleven v4 Turbo (real time, recommended)").
        // Its abilities and where it runs read through the VoiceEngine prefix (VoiceEngineAbilities-elevenlabs,
        // VoiceEngineRunsOn-elevenlabs). The voice choice (ElevenLabsVoice) holds the owner's voice names, so it is not readable;
        // ElevenLabsSave uploads a recording and saves the route, so it needs --allow-ui-effects (and spends money on ElevenLabs).
        "ElevenLabsStatus", "ElevenLabsKeyStatus", "ElevenLabsModel",
        "StageTitle", "StageText", "HealthTitle", "HealthSummary", "HealthAllClear",
        // Home's Recommended setup button (its label).
        "HomeRecommendedSetup",
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
        // Settings › Your other computers: the minutes a computer may be away before Martlet looks for a better setup (choosing
        // another with ui_select saves node-presence.txt, so it needs --allow-ui-effects) and its fixed explanation. The
        // presence notices themselves are Home items: "HealthIssue-presence-missing-gpu-box" reads "Warning: Working with less:
        // gpu-box isn't answering. ..." and "HealthIssue-presence-back-gpu-box" "Good to know: gpu-box is back. ...".
        "PresenceAwayMinutes", "PresenceAwayStatus",
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
        // status line (Martlet is running, listening, paused or watching), and the menu's items, which read as their fixed labels
        // (Talk to Martlet or Show the talk window, Start listening or Stop listening, Pause Martlet or Resume Martlet...).
        // StayAwakeStatus (shown only when this PC runs its own
        // host service): whether Martlet keeps this PC awake because it is a Martlet host PC, or because that host service serves other computers (host ID and
        // computer names) or lets it sleep, or why Windows refused. Fixed text, names and host IDs only.
        "BackgroundStatus", "TrayStatus", "StayAwakeStatus",
        "TrayOpen", "TrayTalk", "TrayStartListening", "TrayStopListening", "TrayStartWatching", "TrayStopWatching", "TrayPause",
        "TrayResume", "TrayEndTalk", "TrayCharacter", "TrayCharacterClickThrough", "TrayCharacterProfiles", "TrayCloseToTray",
        "TrayStartWithWindows", "TrayExit",
        // Settings › Appearance: the palette (Pink light, Rose dark, Character light, Character dark or Custom; menus and every window
        // follow it) and its status line, and the character's colors (how many and where the accent comes from, or why they
        // couldn't be read; never its name). AppearanceColor-<n> and AppearancePreview-<id> read through the prefixes below.
        "AppearanceTheme", "AppearanceStatus", "AppearanceCharacterStatus",
        // Settings › Appearance › Custom (shown while the Custom palette is chosen): the palette Start from names, the selected
        // part's name, help, color code (#RRGGBB) and the hint shown when a typed code isn't a color, its hue (degrees),
        // saturation and lightness (%), whether every color is easy to read or what may be hard to read (CustomThemeCheck), the
        // Make it easy to read button (shown only then) and the question Use its colors asks before it replaces colors the owner
        // chose. Each part reads through CustomThemeRole- below. Changing a color (the code, a slider, a CustomThemePick-<n>,
        // CustomThemeStartFrom or CustomThemeFix) saves appearance-custom.json, so it needs --allow-ui-effects.
        "CustomThemeBase", "CustomThemeRoleName", "CustomThemeRoleHelp", "CustomThemeHex", "CustomThemeHexHint", "CustomThemeHue",
        "CustomThemeSaturation", "CustomThemeLightness", "CustomThemeCheck", "CustomThemeFix", "CustomThemeStartFromQuestion",
        // What this PC is for: the navigation rail's "Companion PC" or "Host PC", and Settings' line describing that role; and
        // under it, Your other computers: what the list offers (or why it is empty or can't switch them). Each computer's row reads
        // through OtherRole- below; its OtherRoleSwitch- button asks that computer to switch, so it needs --allow-ui-effects.
        // SwitchToCompanion is the host dashboard's button at the top of Home (its fixed label, "Switch to companion PC");
        // clicking it saves device-role.txt, so it needs --allow-ui-effects.
        "DeviceRoleSummary", "DeviceRoleText", "SwitchToCompanion", "OtherRolesStatus",
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
        // Companion › Thinking, Voice and Listening › Keys from before: the question before a key set aside is removed for good
        // (which key, by provider or paired computer; never the key). ConfirmationYes removes it, so it needs --allow-ui-effects.
        "OldKeyRemoveQuestion",
        // Companion › Deep thinking: the question before a Deep thinking model joins a Thinking model on the same graphics card
        // (on a paired computer's Add Deep thinking or This PC's Use Ollama on this PC): the computer, Thinking's model tag and
        // the one-graphics-card-for-each-Thinking-model advice. ConfirmationYes adds or uses it, so it needs --allow-ui-effects.
        "DeepThinkingShareQuestion",
        // Set it all up for me: its one confirmation (the plan, the downloads, lip-sync from the welcome wizard and the voice
        // engine's terms), and its Use models your apps already run choice (ConfirmationOption: whether it is ticked; changing it
        // saves recommended-setup.json and asks again). The welcome wizard's Use these suggestions, the Home fixes and
        // ConfirmationYes install and download, so they need --allow-ui-effects.
        "DefaultSetupQuestion", "ConfirmationOption",
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
        "TasksSummary", "TasksEmpty", "NavTasksCount", "HostRunHideHint", "CancelTaskQuestion",
        // Thinking requests: how many run and wait and the averages (ThinkingRequestsSummary), the pool's slots now
        // (ThinkingRequestsPool), the empty state, the navigation rail's count of waiting and running requests, Timing by type
        // and the selected request in full (type, task, companion, member, tries and timings; never its text or answer).
        // ThinkingRequestTopic is what the conversation asked for (private): it is never returned.
        "ThinkingRequestsSummary", "ThinkingRequestsPool", "ThinkingRequestsEmpty", "NavThinkingRequestsCount", "ThinkingRequestDetail",
        "ThinkingRequestsTiming"
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
    /// Companion › Voice › Voice engine's shown engine (its row is the option picker's "Picker-VoiceEngine-chatterbox"):
    /// "VoiceEngineAbilities-chatterbox" reads "Voice cloning: yes. Laughs &amp; sighs: yes. Emotions: whispering only.",
    /// "VoiceEngineFeatures-chatterbox" "Docker, 5 s+ samples, English", and its button "VoiceEngineUse-chatterbox" "Set up and
    /// use Chatterbox Turbo"; "VoiceEngineNone" says when no engine can run there; clicking a button needs --allow-ui-effects) and its computer pills
    /// ("SpeakingHost-gpu-pc" reads "gpu-pc · speaking");
    /// each character's detail line in Companion › Character › Your characters
    /// ("CharacterModelState-builtin" reads "Live2D. Part of Martlet on every computer. Shown on this PC.",
    /// "CharacterModelState-0123456789abcdef" reads "VRM, 12.4 MB. Added on desktop-a 10/2/2026. Copying to this PC..."; never
    /// the character's name);
    /// each home or host-dashboard step's detail line ("StepDetail-docker" says whether Docker Desktop runs, or why it can't start);
    /// the paired computers a job can be handed to ("HostChoice-listening-gpu-pc" reads "gpu-pc: Runs speech recognition (small).")
    /// and why none are listed or which can't run it ("HostChoices-listening", "HostChoicesUnable-listening"); Home's items
    /// ("HealthIssue-ollama" reads "Problem: Ollama isn't running on this PC. ..." or, from Ollama's own logs, "Problem: Ollama keeps
    /// stopping on this PC. ... Ollama says: ..."; "HealthIssue-ollama-fixed" reads "Good to know: Martlet fixed Ollama on this PC. ...")
    /// and Health tiles ("HealthCheck-microphone"
    /// reads "Microphone: OK. Windows default"); Diagnostics' shown lines, newest first ("LogEntry-0" reads
    /// "21:04:11.532 WARN This PC · App: Host gpu-box stopped answering: ...") and its computer filters ("LogSource-desktop-diva"
    /// reads "From: This PC (desktop-diva, diva-host)"); the Martlet desktops found on the network in
    /// Add a computer ("NearbyItem-0" reads "GAMING-PC (192.168.1.31): gaming-pc-host · Martlet 0.17.0"); the Martlet
    /// network's computers ("NetworkMember-host-gpu-pc" reads "gpu-pc. Host, paired with this PC; added on desktop-a.") and
    /// requests to join ("NetworkJoin-desktop-b" reads "DESKTOP-B asks to join. desktop-b, through gpu-pc. Check number ...")
    /// and computers that use one of this PC's hosts but aren't in the network ("NetworkPaired-desktop-c" reads "DESKTOP-C
    /// (desktop-c). Uses gpu-pc; active now. ..."); Settings' Your other computers ("OtherRole-desktop-imouto" reads "IMOUTO
    /// (desktop-imouto). Companion PC that also runs a host service (imouto-host). Active now on diva-host."); each device role's detail line in the selected device's details
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
    private static readonly string[] SafeValuePrefixes = [
        // An option picker (MainWindow.OptionPicker.cs): each option's short facts ("PickerFacts-VoiceEngine-chatterbox": "NVIDIA GPU ·
        // 4.2 GB VRAM · 0.45 s to first audio"), the shown option's details ("PickerDetail-", "PickerFact-", "PickerState-") and the
        // compare table's cells ("PickerCell-VoiceEngine-chatterbox-vram").
        "PickerFacts-", "PickerDetail-", "PickerFact-", "PickerState-", "PickerCell-", "PickerSummary-",
        "PeopleClips-", "DeviceComponent-", "DeviceComponentDetail-", "F5VoiceRow-", "F5VoiceDetail-", "F5AddVoiceRecording-", "F5AddVoiceHeard", "CharacterModelState-", "CharacterActionName-", "CharacterActionTry-", "CharacterActionHint-", "CharacterComboName-", "CharacterComboState-", "CharacterComboHint-", "CharacterComboTry-", "TouchZoneState-", "ReactionChange-", "ReactionChangeEnded-", "TouchZoneReactionItem-", "TouchZoneReactionNone-", "TouchTemperamentAttitude-", "TouchTemperamentReaction-", "TouchTemperamentReaction2-", "TouchTemperamentLinger-", "TouchTemperamentLook-", "TouchTemperamentParts-", "TouchZoneNotices-", "VoiceEngine", "ChatterboxStyleValue-", "SpeakingHost-", "SingingHost-",
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
        // Home's fixes for this PC's own host service ("HealthFix-host-service-repair-0" reads "Start Docker Desktop: This PC's
        // host service isn't working", or "Starting Docker Desktop...: ..." and is disabled while a run starts it). Fixed text
        // only; clicking one starts or sets up software, so it needs --allow-ui-effects.
        "HealthFix-host-service-",
        "HealthIssue-", "HealthCheck-", "LogEntry-", "LogSource-", "NearbyItem-", "NetworkMember-", "NetworkJoin-", "NetworkPaired-", "OtherRole-", "ApiKeyRow-", "SmartHomeFound-", "SmartHomeHost-",
        // Friends (MainWindow.Friends.cs): a friend's computer in Your Martlet network ("NetworkFriend-friend-pc" reads "FRIEND-PC
        // (friend-pc). A friend's computer. It signed in to gpu-box as a friend and uses only that host's engines; ..."), each
        // person on Devices › Friends ("Friend-authentik-lab-friend-7" reads "ana@example.net (authentik). Shares gpu-box: their
        // engines only. Their computers: ...") and a host it couldn't read ("FriendsHost-gpu-box"), each host a friend shares
        // with this PC ("SharedHost-gpu-box" reads "gpu-box. Shared by a friend; you signed in as ... Offers this PC: Thinking.
        // This PC uses it for thinking."), and in Sign-in from outside each allowed identity ("SignInAllowed-authentik-lab-user-42"
        // reads "me@example.net (authentik: lab-user-42): a friend. ..."). Names, providers, subjects, device and host IDs.
        "NetworkFriend-", "Friend-", "FriendsHost-", "SharedHost-", "SignInAllowed-",
        "SmartHomeDevice-", "SmartHomeUpdate-", "DiscordRule-", "HostInput-choice.", "HostInputTerms-", "PromptState-", "Copy-", "Node-", "DeviceFilter-",
        // The selected device's resource bars ("DeviceResource-vram" reads "Graphics memory: 14 of 32 GB planned (44%), 15 GB
        // free for Martlet."; a range such as "11-14 of 32 GB planned (34-44%)" when jobs grow while they work, with ", tight: ..."
        // when only the usual amounts fit; keys vram, ram, cpu, disk; its help text is the bar's hover breakdown, a line per job,
        // then free, kept for the system and in use now), each job's share ("DeviceShare-deep-thinking-gemma4-12b" reads
        // "Deep thinking (Gemma 4 12B): 25% graphics memory, 3% memory, 6% processor.") and what else fits there
        // ("DeviceAlsoFits-0" reads "Room for another Deep thinking model (Gemma 4 12B) here.").
        "DeviceResource-", "DeviceShare-", "DeviceAlsoFits-",
        // The setup advisor's plan: each role's pick and status ("AdvisorChoice-3" reads "Speech-to-text: Parakeet speech
        // recognition (Available)"; the plan has no personal data).
        "AdvisorChoice-",
        // The talk window's task list: each background task's status ("LiveJobState-think-1" reads "Checking it fits beside
        // Thinking." or "Done after 1:02. Martlet brought it up."; never what the task is about or what it found).
        "LiveJobState-",
        // Companion › Check-ins: each check-in's line ("CheckInStatus-emotes" reads "Waits: next in 3 min. Last at 10:31 PM on
        // diva (qwen3:8b): turned off {blush}. 2 runs since Martlet started, 1 acted on."), and for every check-in, built-in or
        // the owner's own, its On box and Every choice ("CheckInOn-emotes", "CheckInEvery-emotes"), its Its answer choice
        // ("CheckInOutcome-emotes"), its fact and condition boxes ("CheckInFact-c1-Said", "CheckInWhen-emotes-EmoteShown"), what
        // starts it at once ("CheckInTrigger-c1-TouchesEnded"), the
        // model it needs ("CheckInNeeds-c1-Vision"), its screenshot box ("CheckInScreenshot-c1"), its recording and length
        // choices ("CheckInRecording-c1", "CheckInSeconds-c1"), its tool set boxes ("CheckInTools-c1-next-reply", whose help says
        // what the set does and its tools), and for a built-in one its prompt's state
        // ("CheckInPromptState-emotes" reads "Edited. About 180 tokens."). Each fact, condition and trigger box, the answer and the
        // length choice carry what they mean as "help". The hours and cap choices ("CheckInFrom-welcome" reads "8 AM",
        // "CheckInUntil-welcome" "10 PM", "CheckInMostPerHour-c1" "Once an hour") carry theirs too. Changing any of them saves
        // check-ins.json, so it needs --allow-ui-effects; the name, task, prompt and script boxes (the owner's own words) aren't read here.
        "CheckInStatus-", "CheckInOn-", "CheckInEvery-", "CheckInOutcome-", "CheckInFact-", "CheckInWhen-",
        "CheckInNeeds-", "CheckInScreenshot-", "CheckInRecording-", "CheckInSeconds-", "CheckInPromptState-", "CheckInTools-",
        "CheckInTrigger-",
        "CheckInFrom-", "CheckInUntil-", "CheckInMostPerHour-",
        // Companion › Thinking pool › Machines: each paired computer's line ("DeepThinkingHost-diva" reads "diva: Ollama runs
        // gemma4:27b. It joins the pool by itself at its next check.") and, for one without the Thinking pool role, its Add
        // button's name ("DeepThinkingAddRole-diva" reads "Add the Thinking pool role on diva"; clicking it installs the role, so it
        // needs --allow-ui-effects); for one with it, its Change model button's name ("DeepThinkingChangeModel-diva" reads "Change
        // the Thinking pool model and slots on diva (now gemma4:e4b)"; clicking it reads the role's settings there and opens its
        // dialog, so it needs --allow-ui-effects). Thinking's, Listening's and Lip-sync's computers have the same button for the
        // role they run ("SetupChangeHost-thinking-diva" reads "Change model: conversation model on diva (now gemma4-e4b)").
        // Each paired computer's In the pool box ("DeepThinkingPool-diva" reads "diva in the Thinking pool" and whether it is
        // ticked: a computer with a Thinking model joins by itself; unticking takes it out and keeps it out, ticking adds it
        // again; both save thinking-pool.json, so they need --allow-ui-effects). Its line (DeepThinkingHost-diva) says why a
        // computer that isn't a member is out. Each machine in the pool has its line ("ThinkingPoolMember-0" reads "diva's
        // Thinking pool (qwen3-8b): Reads text; writes text. 2 slots, set on diva."; a paired computer's role sets its slots
        // there, so it has Change model instead of a slot choice), its badges ("ThinkingPoolBadges-0" reads "...: Waits while
        // you talk · Costs money · Offline", only those that apply), its slot choice (ThinkingPoolSlots-0, not for a paired
        // computer's role) and its Remove button (ThinkingPoolRemove-0, not for a paired computer). Its Quick jobs, Long jobs and
        // Backup for slow replies boxes ("ThinkingPoolQuick-0", "ThinkingPoolLong-0", "ThinkingPoolAnswers-0" read "diva's Thinking
        // pool (qwen3-8b): Quick jobs" and whether each is ticked). The slot choice, Remove and every box save thinking-pool.json,
        // so they need --allow-ui-effects.
        // Each paired computer's shared-card warning, when the Thinking pool role there shares one graphics card with its Thinking
        // model ("DeepThinkingShare-diva" reads "diva: diva already runs a Thinking model (gemma4:e4b) on its only graphics card. ...").
        "DeepThinkingHost-", "DeepThinkingShare-", "DeepThinkingAddRole-", "DeepThinkingChangeModel-", "DeepThinkingPool-", "SetupChangeHost-",
        // Companion › Vision › Image model › One of your computers: each paired computer's line ("ImageModelHost-diva" reads "Its
        // Thinking pool role runs qwen2.5vl:7b: it sees pictures.") and its Use for pictures button's name ("ImageModelUseHost-diva"
        // reads "Use diva for pictures"; clicking it checks diva and saves sense-models.json, so it needs --allow-ui-effects).
        "ImageModelHost-", "ImageModelUseHost-",
        "ThinkingPoolMember-", "ThinkingPoolBadges-", "ThinkingPoolSlots-", "ThinkingPoolAnswers-", "ThinkingPoolQuick-", "ThinkingPoolLong-",
        // Settings › Appearance: each of the character's main colors ("AppearanceColor-0" reads "#2B3440 31% dark grayish blue") and
        // each character palette's colors by role ("AppearancePreview-rules-dark" reads "Character dark: Canvas #1B1F26, ...").
        "AppearanceColor-", "AppearancePreview-",
        // Settings › Appearance › Custom: each part of the custom palette with its color ("CustomThemeRole-Accent" reads "Accent:
        // #A52D64") and the character's colors it can take ("CustomThemePick-0" reads "Use #2B3440 dark grayish blue").
        "CustomThemeRole-", "CustomThemePick-",
        // Companion › Listening › This PC: the speech recognizers are an option picker (Picker-Listening-parakeet-tdt-110m-en,
        // Picker-Listening-whisper-gpu...); while a Parakeet model downloads, its details read the progress
        // ("ListenParakeetModelState-parakeet-tdt-110m-en": "Downloading: 40% of 477 MB..."). Its SetupListenParakeet-<model>
        // button downloads (after a confirmation) and switches Listening, so it needs --allow-ui-effects.
        "ListenParakeetModelState-",
        // Creations: each creation's line in the list ("CreationState-3f2a9c1b7d04" reads "Song · 1:02 · 6.6 MB · made 10/3/2026
        // 9:41 PM on DESK-PC · on this PC, on 2 of 2 hosts"; never its title).
        "CreationState-",
        // Background tasks: each task's title ("TaskTitle-3" reads "Set up gpu-pc", a run window's title), its line
        // ("TaskState-3" reads "Running for 2 min. Waiting for Docker Desktop to start..." or "Done at 3:41 PM after 5 min.
        // gpu-pc is ready.") and its buttons ("TaskShow-3" reads "Show: Set up gpu-pc" or "Show output: ...", "TaskCancel-3"
        // "Cancel: Set up gpu-pc").
        "TaskTitle-", "TaskState-", "TaskShow-", "TaskCancel-",
        // Companion › Profiles: each profile's state ("CharacterProfileState-3f2a9c1b" reads "In use.", "Ready." or why a part
        // can't switch here, such as "Its look is still copying to this PC. Using it switches the rest."; never a name) and what
        // it keeps on this PC ("CharacterProfileHere-3f2a9c1b" reads "On this PC: its own spot and size (420 × 560, locked) ·
        // Eyes: Follow your mouse · While it talks: any touch stops it."; sizes and fixed labels only).
        "CharacterProfileState-", "CharacterProfileHere-",
        // Devices › Sharing work: each job's line ("WorkSharingJob-speaking" reads "Speaking. When the computer doing it is busy
        // ..."), each computer in its order ("WorkSharingPlace-speaking-diva-host" reads "1. diva-host. this PC's own; does it for
        // this PC now."), each computer's keep line ("WorkSharingHost-diva-host" reads "diva-host. Kept for desk-1.") and its
        // choice ("WorkSharingKeep-diva-host"), the Share and own-computer-first boxes ("WorkSharingShare-speaking",
        // "WorkSharingOwnFirst-speaking"), each Use box ("WorkSharingUse-speaking-diva-host") and Up/Down buttons
        // ("WorkSharingUp-speaking-diva-host"). Changing any of them saves work-sharing.json and shares it with your other
        // computers, so it needs --allow-ui-effects. Host IDs, device IDs and fixed text only.
        "WorkSharing",
        // Companion › Thinking, Voice and Listening › Keys from before: each key Martlet set aside when the job stopped using it
        // ("SetupOldKey-Thinking-0" reads "Your OpenRouter key" or "The pairing key for diva-host"; never the key) and its
        // Remove button's name ("SetupOldKeyRemove-Thinking-0" reads "Remove your OpenRouter key"). Remove deletes the key from
        // Windows Credential Manager after OldKeyRemoveQuestion, so clicking it needs --allow-ui-effects.
        "SetupOldKey",
        // Home's Recommended setup review: its title and summary, each change ("RecommendedSetupChange-0" reads "Improvement:
        // Install Chatterbox Turbo on gpu-box's RTX 4090. ..."), each computer's name and kind, today's and the recommended roles
        // and load ("RecommendedSetupComputer-0", "RecommendedSetupToday-0", "RecommendedSetupTarget-0", "RecommendedSetupLoad-0",
        // "RecommendedSetupBar-0-vram"), who does each job ("RecommendedSetupJob-0"), the notes, downloads, what needs someone at a
        // computer, what Reconfigure needs first ("RecommendedSetupPreflight-0", "RecommendedSetupSecret-0": the label only, never
        // what is typed) and the status line (the preflight state, or why Reconfigure couldn't start; its progress and outcome
        // show in its run window and Background tasks). Computer names, host IDs, model names and fixed text. The banner at the
        // top ("RecommendedSetupBanner", "RecommendedSetupBannerTitle", "RecommendedSetupBannerText": Martlet can't reply, or the
        // free API key tip), the line on computers that haven't answered ("RecommendedSetupOffline") and the free key buttons'
        // labels ("RecommendedSetupFreeKeyAdd", "RecommendedSetupFreeKeyGet"), Use models your apps already run (whether
        // "RecommendedSetupUseServedModels" is ticked) and the models found ("RecommendedSetupServedModels": model names and apps),
        // and Prefer models your hosts already have (whether "RecommendedSetupPreferHostModels" is ticked) and the models your
        // hosts keep ("RecommendedSetupHostModels": model names and computer names) read through the same prefix.
        "RecommendedSetup"];
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
                    ["foreground"] = GetForegroundWindow() == handle,
                    // The mouse passes through the window to the one under it (WS_EX_TRANSPARENT): the character overlay while
                    // click-through is on.
                    ["clickThrough"] = (GetWindowLongPtrW(handle, ExtendedStyleIndex) & TransparentStyle) != 0
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
                        element.Current.ControlType == ControlType.RadioButton || element.Current.ControlType == ControlType.CheckBox
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
                // tokens and the last reply's cache use); a readable check box or choice says what it means the same way
                // (Companion › Check-ins' fact boxes).
                if (value is not null && element.Current.HelpText is { Length: > 0 } help &&
                    (element.Current.ControlType == ControlType.Text || element.Current.ControlType == ControlType.CheckBox ||
                        element.Current.ControlType == ControlType.ComboBox))
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

    private const int WindowStyleIndex = -16, ExtendedStyleIndex = -20;
    private const nint ThickFrame = 0x40000, MinimizeBox = 0x20000, MaximizeBox = 0x10000, TransparentStyle = 0x20;
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

    internal async Task<object> ClickAsync(string id, string? window = null, bool focus = false)
    {
        if (!allowEffects && !IsSafeClick(id))
            throw new InvalidOperationException("This control requires an operator to start MCP with --allow-ui-effects.");
        var element = Find(id, window);
        if (!element.Current.IsEnabled) throw new InvalidOperationException($"Control '{id}' is disabled.");
        // As a mouse click does, the control takes the keyboard focus first (its window comes to the front).
        if (focus)
        {
            try { element.SetFocus(); }
            catch (Exception error) when (error is InvalidOperationException or COMException or ElementNotAvailableException)
            {
                throw new InvalidOperationException($"Control '{id}' can't take the keyboard focus: {error.Message}");
            }
        }
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
        // A text box, or a combo box you can type in (Companion › Vision › Image model's ImageModelLocalModel).
        if (!element.Current.IsEnabled || element.Current.ControlType != ControlType.Edit && element.Current.ControlType != ControlType.ComboBox ||
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
    /// reads the last tap. Returns the last tap as the overlay reports it and, when Companion › Touch › Touch zones shows,
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

    // What Martlet noticed, when Companion › Touch › Touch zones shows (null otherwise).
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

    internal const int MaximumFaceSamples = 60;

    /// <summary>Takes a picture of the showing character as it shows now through MoveAvatar's UI Automation value ("picture"):
    /// the renderer's own capture of the overlay's page, so Martlet's drawings over the face (the blush glow, overlay emotes
    /// such as heart eyes) are in it, cropped to the character. It changes nothing on the character, so it needs no
    /// --allow-ui-effects. The renderer writes the PNG to its file in the temp folder; with <paramref name="outputPath"/> (a full
    /// path to a .png file) it is copied there. Returns the picture's reading: the file, its size and the crop of the overlay's
    /// page it shows (fractions, like character_face's positions).</summary>
    internal async Task<object> PictureCharacterAsync(string? outputPath)
    {
        if (outputPath is not null && (!System.IO.Path.IsPathFullyQualified(outputPath) ||
            !outputPath.EndsWith(".png", StringComparison.OrdinalIgnoreCase)))
            throw new ArgumentException("outputPath is a full path to a .png file.");
        var element = Find("MoveAvatar");
        if (!element.TryGetCurrentPattern(ValuePattern.Pattern, out var pattern))
            throw new InvalidOperationException("The character overlay can't be read through UI Automation.");
        var value = (ValuePattern)pattern;
        if (value.Current.IsReadOnly) throw new InvalidOperationException("The character's picture can't be taken until it has loaded.");
        static System.Text.Json.JsonElement? Picture(string text) => string.IsNullOrEmpty(text) ? null :
            System.Text.Json.JsonDocument.Parse(text).RootElement is { ValueKind: System.Text.Json.JsonValueKind.Object } root &&
            root.TryGetProperty("picture", out var picture) ? picture.Clone() : null;
        var before = Picture(value.Current.Value)?.GetRawText();
        value.SetValue("picture");
        var waited = Stopwatch.StartNew();
        System.Text.Json.JsonElement? after;
        while ((after = Picture(value.Current.Value))?.GetRawText() == before && waited.Elapsed < TimeSpan.FromSeconds(10)) await Task.Delay(50);
        if (after is not { } read || read.GetRawText() == before)
            return new { taken = false, note = "The renderer didn't take the picture within 10 seconds." };
        var taken = !read.TryGetProperty("error", out _);
        string? saved = null;
        if (taken && outputPath is not null && read.TryGetProperty("path", out var path) && path.GetString() is { } file && System.IO.File.Exists(file))
        {
            System.IO.File.Copy(file, outputPath, overwrite: true);
            saved = outputPath;
        }
        return new { taken, picture = read, saved };
    }

    /// <summary>Reads where Martlet draws over the showing character's face (the blush levels and overlay emotes)
    /// <paramref name="samples"/> times, <paramref name="gapMs"/> apart, through MoveAvatar's UI Automation value ("face"). It
    /// changes nothing, so it needs no --allow-ui-effects. Returns each reading (fractions of the overlay's drawing, +y down)
    /// and a summary: how the face is followed, how far it moved, turned and tilted, what of the character is under each
    /// cheek, and where the eyes came from, how each iris moved and how far each eye's opening closed.</summary>
    internal async Task<object> FaceCharacterAsync(int? samples, int? gapMs)
    {
        var count = samples ?? 1;
        if (count is < 1 or > MaximumFaceSamples) throw new ArgumentException($"samples is 1 to {MaximumFaceSamples}.");
        if (gapMs is < 0 or > 5000) throw new ArgumentException("gapMs is 0 to 5000.");
        var element = Find("MoveAvatar");
        if (!element.TryGetCurrentPattern(ValuePattern.Pattern, out var pattern))
            throw new InvalidOperationException("The character overlay can't be read through UI Automation.");
        var value = (ValuePattern)pattern;
        if (value.Current.IsReadOnly) throw new InvalidOperationException("The character's face can't be read until it has loaded.");
        static System.Text.Json.JsonElement? Face(string text) => string.IsNullOrEmpty(text) ? null :
            System.Text.Json.JsonDocument.Parse(text).RootElement is { ValueKind: System.Text.Json.JsonValueKind.Object } root &&
            root.TryGetProperty("face", out var face) ? face.Clone() : null;
        var faces = new List<System.Text.Json.JsonElement>();
        for (var i = 0; i < count; i++)
        {
            if (i > 0) await Task.Delay(gapMs ?? 250);
            var before = Face(value.Current.Value)?.GetRawText();
            value.SetValue("face");
            var waited = Stopwatch.StartNew();
            System.Text.Json.JsonElement? after;
            while ((after = Face(value.Current.Value))?.GetRawText() == before && waited.Elapsed < TimeSpan.FromSeconds(3)) await Task.Delay(20);
            if (after is { } read && read.GetRawText() != before) faces.Add(read);
        }
        return new { samples = count, read = faces.Count, faces, summary = FaceSummary(faces),
            note = faces.Count == 0 ? "The renderer didn't answer within 3 seconds." : null };
    }

    // How the readings went together: the tracking used, how far the face moved, scaled and tilted (fractions of the drawing,
    // degrees), per cheek the share of readings it was over the character, what it was mostly over (the topmost drawable,
    // mesh or bone) and for what share, how much of it showed at least and how wide it was against the face, where the eyes
    // came from and per eye how its iris moved and sat in its opening and how far the opening closed.
    internal static object FaceSummary(IReadOnlyList<System.Text.Json.JsonElement> faces)
    {
        static bool Is(System.Text.Json.JsonElement owner, string key, System.Text.Json.JsonValueKind kind) =>
            owner.ValueKind == System.Text.Json.JsonValueKind.Object && owner.TryGetProperty(key, out var value) && value.ValueKind == kind;
        static double[] Numbers(IEnumerable<System.Text.Json.JsonElement> items, string key) =>
            [.. items.Where(item => Is(item, key, System.Text.Json.JsonValueKind.Number)).Select(item => item.GetProperty(key).GetDouble())];
        static double Spread(double[] values) => values.Length == 0 ? 0 : Math.Round(values.Max() - values.Min(), 4);
        var found = faces.Where(face => Is(face, "found", System.Text.Json.JsonValueKind.True)).ToArray();
        object Cheek(string key)
        {
            var cheeks = found.Where(face => Is(face, key, System.Text.Json.JsonValueKind.Object)).Select(face => face.GetProperty(key)).ToArray();
            static string? Under(System.Text.Json.JsonElement cheek) => !Is(cheek, "hit", System.Text.Json.JsonValueKind.True) ? null
                : Is(cheek, "drawables", System.Text.Json.JsonValueKind.Array) && cheek.GetProperty("drawables").GetArrayLength() > 0
                    ? cheek.GetProperty("drawables")[0].GetString()
                : Is(cheek, "mesh", System.Text.Json.JsonValueKind.String) ? cheek.GetProperty("mesh").GetString()
                : Is(cheek, "bone", System.Text.Json.JsonValueKind.String) ? cheek.GetProperty("bone").GetString() : "character";
            var under = cheeks.Select(Under).ToArray();
            var most = under.OfType<string>().GroupBy(name => name).OrderByDescending(group => group.Count()).FirstOrDefault();
            double Share(int part) => cheeks.Length == 0 ? 0 : Math.Round((double)part / cheeks.Length, 2);
            var visible = Numbers(cheeks, "visible");
            var across = Numbers(cheeks, "across");
            return new { onCharacter = Share(under.Count(name => name is not null)), mostlyOver = most?.Key, mostlyOverShare = Share(most?.Count() ?? 0),
                visibleLeast = visible.Length == 0 ? (double?)null : visible.Min(),
                across = across.Length == 0 ? null : new { least = across.Min(), most = across.Max() } };
        }
        // Per eye: the share of readings with an iris, how far its middle moved, the share of readings with an opening whose
        // iris was inside it, the least and most the opening's box was high (a blink closes it) and the share it was closed.
        object Eye(string iris, string shape)
        {
            var irises = found.Where(face => Is(face, iris, System.Text.Json.JsonValueKind.Object)).Select(face => face.GetProperty(iris)).ToArray();
            var shapes = found.Where(face => Is(face, shape, System.Text.Json.JsonValueKind.Object)).Select(face => face.GetProperty(shape)).ToArray();
            static int Points(System.Text.Json.JsonElement opening) =>
                Is(opening, "points", System.Text.Json.JsonValueKind.Number) && opening.GetProperty("points").TryGetInt32(out var count) ? count : 0;
            static double Part(int part, int of) => of == 0 ? 0 : Math.Round((double)part / of, 2);
            var open = shapes.Where(opening => Points(opening) >= 3 && (Is(opening, "irisInside", System.Text.Json.JsonValueKind.True) ||
                Is(opening, "irisInside", System.Text.Json.JsonValueKind.False))).ToArray();
            var heights = shapes.Where(opening => Is(opening, "top", System.Text.Json.JsonValueKind.Number) && Is(opening, "bottom", System.Text.Json.JsonValueKind.Number))
                .Select(opening => Math.Round(opening.GetProperty("bottom").GetDouble() - opening.GetProperty("top").GetDouble(), 4)).ToArray();
            return new
            {
                iris = Part(irises.Length, found.Length),
                irisMoved = new { x = Spread(Numbers(irises, "x")), y = Spread(Numbers(irises, "y")) },
                irisInside = open.Length == 0 ? (double?)null : Part(open.Count(opening => Is(opening, "irisInside", System.Text.Json.JsonValueKind.True)), open.Length),
                opening = heights.Length == 0 ? null : new { least = heights.Min(), most = heights.Max() },
                closed = Part(shapes.Count(opening => Points(opening) == 0), shapes.Length)
            };
        }
        return new
        {
            found = found.Length,
            tracking = found.Where(face => Is(face, "tracking", System.Text.Json.JsonValueKind.String))
                .Select(face => face.GetProperty("tracking").GetString()).Distinct().ToArray(),
            moved = new { x = Spread(Numbers(found, "x")), y = Spread(Numbers(found, "y")), width = Spread(Numbers(found, "width")),
                tilt = Spread(Numbers(found, "tilt")) },
            cheekLeft = Cheek("cheekLeft"), cheekRight = Cheek("cheekRight"),
            eyesFrom = found.Where(face => Is(face, "eyesFrom", System.Text.Json.JsonValueKind.String))
                .Select(face => face.GetProperty("eyesFrom").GetString()).Distinct().ToArray(),
            eyeLeft = Eye("irisLeft", "eyeLeftShape"), eyeRight = Eye("irisRight", "eyeRightShape")
        };
    }

    internal const int MaximumLookSamples = 60;

    /// <summary>Reads where the showing character looks now <paramref name="samples"/> times, <paramref name="gapMs"/> apart,
    /// through MoveAvatar's UI Automation value ("look"). It changes nothing, so it needs no --allow-ui-effects. Returns each
    /// reading (what the eyes are on, the direction and the point, the usual gaze, and the window you're using and what the eyes
    /// watch in it) and a summary: the targets and what the eyes watched in the window, and the range of the direction.</summary>
    internal async Task<object> LookCharacterAsync(int? samples, int? gapMs)
    {
        var count = samples ?? 1;
        if (count is < 1 or > MaximumLookSamples) throw new ArgumentException($"samples is 1 to {MaximumLookSamples}.");
        if (gapMs is < 0 or > 5000) throw new ArgumentException("gapMs is 0 to 5000.");
        var element = Find("MoveAvatar");
        if (!element.TryGetCurrentPattern(ValuePattern.Pattern, out var pattern))
            throw new InvalidOperationException("The character overlay can't be read through UI Automation.");
        var value = (ValuePattern)pattern;
        if (value.Current.IsReadOnly) throw new InvalidOperationException("Where the character looks can't be read until it has loaded.");
        static System.Text.Json.JsonElement? Look(string text) => string.IsNullOrEmpty(text) ? null :
            System.Text.Json.JsonDocument.Parse(text).RootElement is { ValueKind: System.Text.Json.JsonValueKind.Object } root &&
            root.TryGetProperty("look", out var look) ? look.Clone() : null;
        var looks = new List<System.Text.Json.JsonElement>();
        for (var i = 0; i < count; i++)
        {
            if (i > 0) await Task.Delay(gapMs ?? 250);
            var before = Look(value.Current.Value)?.GetRawText();
            value.SetValue("look");
            var waited = Stopwatch.StartNew();
            System.Text.Json.JsonElement? after;
            while ((after = Look(value.Current.Value))?.GetRawText() == before && waited.Elapsed < TimeSpan.FromSeconds(3)) await Task.Delay(20);
            if (after is { } read && read.GetRawText() != before) looks.Add(read);
        }
        return new { samples = count, read = looks.Count, looks, summary = LookSummary(looks),
            note = looks.Count == 0 ? "The renderer didn't answer within 3 seconds." : null };
    }

    // The targets and what the eyes watched in the window across the readings, and the least and most of the direction.
    internal static object LookSummary(IReadOnlyList<System.Text.Json.JsonElement> looks)
    {
        string[] Words(string name) => [.. looks.Select(look => look.TryGetProperty(name, out var word) &&
            word.ValueKind == System.Text.Json.JsonValueKind.String ? word.GetString() : null).OfType<string>().Distinct()];
        object? Range(string name)
        {
            double[] values = [.. looks.Where(look => look.TryGetProperty(name, out var number) && number.ValueKind == System.Text.Json.JsonValueKind.Number)
                .Select(look => look.GetProperty(name).GetDouble())];
            return values.Length == 0 ? null : new { least = values.Min(), most = values.Max() };
        }
        return new { targets = Words("target"), watching = Words("watching"), x = Range("x"), y = Range("y") };
    }

    internal const int MaximumPoseSamples = 60;

    internal const int MaximumZoneSamples = 60;

    /// <summary>Reads where each area of the showing character's touch zones is now (as Martlet last gave them; Touch zones › Show
    /// the zones on the character draws them) <paramref name="samples"/> times, <paramref name="gapMs"/> apart, through
    /// MoveAvatar's UI Automation value ("zones"). It changes nothing, so it needs no --allow-ui-effects. Returns each reading
    /// (fractions of the overlay's drawing, +y down) and a summary per area: what placed it and how far its middle moved.</summary>
    internal async Task<object> ZonesCharacterAsync(int? samples, int? gapMs)
    {
        var count = samples ?? 1;
        if (count is < 1 or > MaximumZoneSamples) throw new ArgumentException($"samples is 1 to {MaximumZoneSamples}.");
        if (gapMs is < 0 or > 5000) throw new ArgumentException("gapMs is 0 to 5000.");
        var element = Find("MoveAvatar");
        if (!element.TryGetCurrentPattern(ValuePattern.Pattern, out var pattern))
            throw new InvalidOperationException("The character overlay can't be read through UI Automation.");
        var value = (ValuePattern)pattern;
        if (value.Current.IsReadOnly) throw new InvalidOperationException("The character's touch zones can't be read until it has loaded.");
        static System.Text.Json.JsonElement? Zones(string text) => string.IsNullOrEmpty(text) ? null :
            System.Text.Json.JsonDocument.Parse(text).RootElement is { ValueKind: System.Text.Json.JsonValueKind.Object } root &&
            root.TryGetProperty("zones", out var zones) ? zones.Clone() : null;
        var readings = new List<System.Text.Json.JsonElement>();
        for (var i = 0; i < count; i++)
        {
            if (i > 0) await Task.Delay(gapMs ?? 250);
            var before = Zones(value.Current.Value)?.GetRawText();
            value.SetValue("zones");
            var waited = Stopwatch.StartNew();
            System.Text.Json.JsonElement? after;
            while ((after = Zones(value.Current.Value))?.GetRawText() == before && waited.Elapsed < TimeSpan.FromSeconds(3)) await Task.Delay(20);
            if (after is { } read && read.GetRawText() != before) readings.Add(read);
        }
        return new { samples = count, read = readings.Count, last = readings.LastOrDefault(), summary = ZonesSummary(readings),
            note = readings.Count == 0 ? "The renderer didn't answer within 3 seconds." : null };
    }

    // How the readings went together, per area (its zone and place among the zone's areas): what placed it, its last box and how
    // far its middle moved across the readings (fractions of the drawing).
    internal static object[] ZonesSummary(IReadOnlyList<System.Text.Json.JsonElement> readings)
    {
        var areas = new Dictionary<(string Zone, int Area), List<(string From, double Left, double Top, double Right, double Bottom)>>();
        foreach (var reading in readings)
        {
            if (reading.ValueKind != System.Text.Json.JsonValueKind.Object || !reading.TryGetProperty("areas", out var list) ||
                list.ValueKind != System.Text.Json.JsonValueKind.Array) continue;
            foreach (var area in list.EnumerateArray())
            {
                if (area.ValueKind != System.Text.Json.JsonValueKind.Object || !area.TryGetProperty("zone", out var zone) ||
                    !area.TryGetProperty("area", out var index) || !index.TryGetInt32(out var at)) continue;
                double Number(string name) => area.TryGetProperty(name, out var number) && number.ValueKind == System.Text.Json.JsonValueKind.Number
                    ? number.GetDouble() : double.NaN;
                var key = (zone.GetString() ?? "", at);
                if (!areas.TryGetValue(key, out var seen)) areas[key] = seen = [];
                seen.Add((area.TryGetProperty("from", out var from) ? from.GetString() ?? "" : "", Number("left"), Number("top"), Number("right"), Number("bottom")));
            }
        }
        static double Spread(IEnumerable<double> values)
        {
            var finite = values.Where(double.IsFinite).ToArray();
            return finite.Length == 0 ? 0 : Math.Round(finite.Max() - finite.Min(), 4);
        }
        return [.. areas.OrderBy(a => a.Key.Zone, StringComparer.Ordinal).ThenBy(a => a.Key.Area).Select(a => (object)new
        {
            zone = a.Key.Zone, area = a.Key.Area, from = a.Value.Select(v => v.From).Distinct().ToArray(),
            box = new[] { a.Value[^1].Left, a.Value[^1].Top, a.Value[^1].Right, a.Value[^1].Bottom },
            moved = new { x = Spread(a.Value.Select(v => (v.Left + v.Right) / 2)), y = Spread(a.Value.Select(v => (v.Top + v.Bottom) / 2)) }
        })];
    }

    /// <summary>Reads what the showing character's idle body does (a VRM's breath, arm hang, finger curl and sway)
    /// <paramref name="samples"/> times, <paramref name="gapMs"/> apart, through MoveAvatar's UI Automation value ("pose"). It
    /// changes nothing, so it needs no --allow-ui-effects. Returns each reading and a summary: how full the breath got, the
    /// breaths a minute, how the arms hang and the fingers curl, the sway and how far the head, shoulders and hands moved.</summary>
    internal async Task<object> PoseCharacterAsync(int? samples, int? gapMs)
    {
        var count = samples ?? 1;
        if (count is < 1 or > MaximumPoseSamples) throw new ArgumentException($"samples is 1 to {MaximumPoseSamples}.");
        if (gapMs is < 0 or > 5000) throw new ArgumentException("gapMs is 0 to 5000.");
        var element = Find("MoveAvatar");
        if (!element.TryGetCurrentPattern(ValuePattern.Pattern, out var pattern))
            throw new InvalidOperationException("The character overlay can't be read through UI Automation.");
        var value = (ValuePattern)pattern;
        if (value.Current.IsReadOnly) throw new InvalidOperationException("The character's pose can't be read until it has loaded.");
        static System.Text.Json.JsonElement? Pose(string text) => string.IsNullOrEmpty(text) ? null :
            System.Text.Json.JsonDocument.Parse(text).RootElement is { ValueKind: System.Text.Json.JsonValueKind.Object } root &&
            root.TryGetProperty("pose", out var pose) ? pose.Clone() : null;
        var poses = new List<System.Text.Json.JsonElement>();
        for (var i = 0; i < count; i++)
        {
            if (i > 0) await Task.Delay(gapMs ?? 250);
            var before = Pose(value.Current.Value)?.GetRawText();
            value.SetValue("pose");
            var waited = Stopwatch.StartNew();
            System.Text.Json.JsonElement? after;
            while ((after = Pose(value.Current.Value))?.GetRawText() == before && waited.Elapsed < TimeSpan.FromSeconds(3)) await Task.Delay(20);
            if (after is { } read && read.GetRawText() != before) poses.Add(read);
        }
        return new { samples = count, read = poses.Count, poses, summary = PoseSummary(poses),
            note = poses.Count == 0 ? "The renderer didn't answer within 3 seconds." : null };
    }

    // How the readings went together: whether the body idled, the least and most of the breath (inhale, 0 to 1), the breaths a
    // minute, each arm's angle from straight down and elbow bend, each hand's finger curl and the sway (degrees), and how far
    // each placed bone moved across the readings (fractions of the drawing).
    internal static object PoseSummary(IReadOnlyList<System.Text.Json.JsonElement> poses)
    {
        static System.Text.Json.JsonElement? At(System.Text.Json.JsonElement owner, string[] path)
        {
            foreach (var key in path)
            {
                if (owner.ValueKind != System.Text.Json.JsonValueKind.Object || !owner.TryGetProperty(key, out var next)) return null;
                owner = next;
            }
            return owner;
        }
        static double[] Numbers(IEnumerable<System.Text.Json.JsonElement> items, params string[] path) =>
            [.. items.Select(item => At(item, path)).Where(value => value is { ValueKind: System.Text.Json.JsonValueKind.Number })
                .Select(value => value!.Value.GetDouble())];
        static object? Range(double[] values) => values.Length == 0 ? null : new { least = values.Min(), most = values.Max() };
        static double Spread(double[] values) => values.Length == 0 ? 0 : Math.Round(values.Max() - values.Min(), 4);
        var found = poses.Where(pose => At(pose, ["found"]) is { ValueKind: System.Text.Json.JsonValueKind.True }).ToArray();
        object Arm(string side) => new { fromDown = Range(Numbers(found, "arms", side, "fromDown")), elbow = Range(Numbers(found, "arms", side, "elbow")) };
        var bones = found.Select(pose => At(pose, ["bones"])).Where(value => value is { ValueKind: System.Text.Json.JsonValueKind.Object })
            .SelectMany(value => value!.Value.EnumerateObject().Select(property => property.Name)).Distinct().ToArray();
        return new
        {
            found = found.Length,
            idle = found.Length > 0 && found.All(pose => At(pose, ["idle"]) is { ValueKind: System.Text.Json.JsonValueKind.True }),
            inhale = Range(Numbers(found, "breathing", "inhale")),
            perMinute = Range(Numbers(found, "breathing", "perMinute")),
            arms = new { left = Arm("left"), right = Arm("right") },
            curl = new { left = Range(Numbers(found, "curl", "left")), right = Range(Numbers(found, "curl", "right")) },
            sway = Range(Numbers(found, "sway")),
            moved = bones.ToDictionary(name => name, name => new { x = Spread(Numbers(found, "bones", name, "x")), y = Spread(Numbers(found, "bones", name, "y")) })
        };
    }

    internal const int MaximumMouthSamples = 60;
    internal const int MaximumVoiceLevels = 400;

    /// <summary>Reads who moves the showing character's mouth, the voice or its emotes, <paramref name="samples"/> times,
    /// <paramref name="gapMs"/> apart, through MoveAvatar's UI Automation value ("mouth"). With <paramref name="levels"/> (0 to
    /// 1, one every <paramref name="stepMs"/>; needs --allow-ui-effects) the mouth first moves as Martlet's loudness lip-sync moves
    /// it, without a sound ("voice:ms;level;..."), and the readings start at once. Returns each reading and a summary.</summary>
    internal async Task<object> MouthCharacterAsync(double[]? levels, int? stepMs, int? samples, int? gapMs)
    {
        var count = samples ?? 1;
        if (count is < 1 or > MaximumMouthSamples) throw new ArgumentException($"samples is 1 to {MaximumMouthSamples}.");
        if (gapMs is < 0 or > 5000) throw new ArgumentException("gapMs is 0 to 5000.");
        var step = stepMs ?? 50;
        if (levels is not null)
        {
            if (!allowEffects) throw new InvalidOperationException("Moving the character's mouth requires --allow-ui-effects.");
            if (levels.Length is < 1 or > MaximumVoiceLevels) throw new ArgumentException($"Give 1 to {MaximumVoiceLevels} levels.");
            if (levels.Any(level => level is not (>= 0 and <= 1))) throw new ArgumentException("Each level is a number from 0 to 1.");
            if (step is < 10 or > 1000) throw new ArgumentException("stepMs is 10 to 1000.");
        }
        var element = Find("MoveAvatar");
        if (!element.TryGetCurrentPattern(ValuePattern.Pattern, out var pattern))
            throw new InvalidOperationException("The character overlay can't be read through UI Automation.");
        var value = (ValuePattern)pattern;
        if (value.Current.IsReadOnly) throw new InvalidOperationException("The character's mouth can't be read until it has loaded.");
        static System.Text.Json.JsonElement? Mouth(string text) => string.IsNullOrEmpty(text) ? null :
            System.Text.Json.JsonDocument.Parse(text).RootElement is { ValueKind: System.Text.Json.JsonValueKind.Object } root &&
            root.TryGetProperty("mouth", out var mouth) ? mouth.Clone() : null;
        if (levels is not null)
            value.SetValue("voice:" + string.Join(";", new[] { step.ToString(System.Globalization.CultureInfo.InvariantCulture) }
                .Concat(levels.Select(level => level.ToString("R", System.Globalization.CultureInfo.InvariantCulture)))));
        var mouths = new List<System.Text.Json.JsonElement>();
        for (var i = 0; i < count; i++)
        {
            if (i > 0) await Task.Delay(gapMs ?? 250);
            var before = Mouth(value.Current.Value)?.GetRawText();
            value.SetValue("mouth");
            var waited = Stopwatch.StartNew();
            System.Text.Json.JsonElement? after;
            while ((after = Mouth(value.Current.Value))?.GetRawText() == before && waited.Elapsed < TimeSpan.FromSeconds(3)) await Task.Delay(20);
            if (after is { } read && read.GetRawText() != before) mouths.Add(read);
        }
        return new { played = levels?.Length ?? 0, stepMs = levels is null ? (int?)null : step, samples = count, read = mouths.Count, mouths,
            summary = MouthSummary(mouths), note = mouths.Count == 0 ? "The renderer didn't answer within 3 seconds." : null };
    }

    // How the readings went together: how many found the mouth, the share in which the voice had it (speaking), the least and
    // most of how much the voice had it, its loudness, the emotes' opening, the mouth's opening and a VRM's block, and the last
    // reading (where the mouth ended up).
    internal static object MouthSummary(IReadOnlyList<System.Text.Json.JsonElement> mouths)
    {
        static bool Is(System.Text.Json.JsonElement owner, string key, System.Text.Json.JsonValueKind kind) =>
            owner.ValueKind == System.Text.Json.JsonValueKind.Object && owner.TryGetProperty(key, out var value) && value.ValueKind == kind;
        static object? Range(IEnumerable<System.Text.Json.JsonElement> items, string key)
        {
            double[] values = [.. items.Where(item => Is(item, key, System.Text.Json.JsonValueKind.Number)).Select(item => item.GetProperty(key).GetDouble())];
            return values.Length == 0 ? null : new { least = values.Min(), most = values.Max() };
        }
        var found = mouths.Where(mouth => Is(mouth, "found", System.Text.Json.JsonValueKind.True)).ToArray();
        return new
        {
            found = found.Length,
            speaking = found.Length == 0 ? 0 : Math.Round((double)found.Count(mouth => Is(mouth, "speaking", System.Text.Json.JsonValueKind.True)) / found.Length, 2),
            voice = Range(found, "voice"), level = Range(found, "level"), emote = Range(found, "emote"), open = Range(found, "open"),
            blocked = Range(found, "blocked"),
            last = found.Length == 0 ? (System.Text.Json.JsonElement?)null : found[^1]
        };
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

    /// <summary>Reads or scrolls a control that scrolls (Companion › Touch's zone map, TouchZonesMap, or a page) through UI
    /// Automation's Scroll pattern: <paramref name="horizontal"/> and <paramref name="vertical"/> (0 to 100, each optional) are how
    /// far along to scroll it each way. Returns how far along it is each way (-1 when it can't scroll that way) and which part of
    /// its content shows, in percent of the content. Scrolling changes only what shows, so it needs no --allow-ui-effects.</summary>
    internal object Scroll(string id, double? horizontal, double? vertical)
    {
        if (horizontal is { } x && !(x is >= 0 and <= 100)) throw new ArgumentException("horizontal takes 0 through 100.");
        if (vertical is { } y && !(y is >= 0 and <= 100)) throw new ArgumentException("vertical takes 0 through 100.");
        var element = Find(id);
        if (!element.TryGetCurrentPattern(ScrollPattern.Pattern, out var pattern))
            throw new InvalidOperationException($"Control '{id}' doesn't scroll.");
        var scroll = (ScrollPattern)pattern;
        if (horizontal is not null && !scroll.Current.HorizontallyScrollable)
            throw new InvalidOperationException($"Control '{id}' can't scroll sideways now: all of its content shows across.");
        if (vertical is not null && !scroll.Current.VerticallyScrollable)
            throw new InvalidOperationException($"Control '{id}' can't scroll up or down now: all of its content shows down.");
        if (horizontal is not null || vertical is not null)
        {
            scroll.SetScrollPercent(horizontal ?? ScrollPattern.NoScroll, vertical ?? ScrollPattern.NoScroll);
            Thread.Sleep(200);
        }
        var now = scroll.Current;
        return new
        {
            control = id,
            horizontal = Math.Round(now.HorizontalScrollPercent, 1),
            vertical = Math.Round(now.VerticalScrollPercent, 1),
            shows = new
            {
                left = Shown(now.HorizontalScrollPercent, now.HorizontalViewSize).Start,
                right = Shown(now.HorizontalScrollPercent, now.HorizontalViewSize).End,
                top = Shown(now.VerticalScrollPercent, now.VerticalViewSize).Start,
                bottom = Shown(now.VerticalScrollPercent, now.VerticalViewSize).End
            },
            bounds = Box(element.Current.BoundingRectangle)
        };

        // The part of the content that shows one way: all of it when it can't scroll that way.
        static (double Start, double End) Shown(double percent, double view)
        {
            if (percent < 0 || !(view is > 0 and < 100)) return (0, 100);
            var start = percent / 100 * (100 - view);
            return (Math.Round(start, 1), Math.Round(start + view, 1));
        }
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
