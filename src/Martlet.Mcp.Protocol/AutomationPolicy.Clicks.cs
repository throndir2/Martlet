namespace Martlet.Mcp;

/// <summary>The click lists of <see cref="AutomationPolicy"/> (AutomationPolicy.Rules.cs has the rules).</summary>
internal static partial class AutomationPolicy
{
    /// <summary>Controls ui_click may press without --allow-ui-effects: they only navigate, open, close, expand, refresh or read.</summary>
    internal static readonly Entry[] SafeClicks =
    [
        new("TalkTab", "Martlet.Companion (dev run)", "Only shows the Talk tab (passive navigation)."),
        new("SettingsTab", "Martlet.Companion (dev run)", "Only shows the Settings tab (passive navigation)."),
        new("ComputerTab", "Martlet.Companion (dev run)", "Only shows the This computer tab (passive navigation)."),
        new("OpenTroubleshooting", "Settings › Tools", "Only opens the Troubleshooting window."),
        new("OpenAudioSetup", "Companion › Voice and Listening", "Test or change speakers or microphone: only opens the microphone and speakers window."),
        new("OpenLiveConversation", "Home", "Only opens the talk window."),
        new("OpenConfigurationRecovery", "Settings › Tools", "Only opens the Backup and restore window."),
        new("SupportRefresh", "Troubleshooting", "Refresh status: only runs the same read-only status probes that Martlet runs at start."),
        new("AudioClose", "Microphone and speakers window", "Done: only closes the microphone and speakers window."),
        new("CloseLive", "Talk window", "Only closes the talk window, which ends the conversation."),
        new("SupportClose", "Troubleshooting", "Only closes the Troubleshooting window."),
        new("RecoveryClose", "Backup and restore", "Only closes the Backup and restore window."),
        new("SupportFreeze", "Troubleshooting", "Preview support files: only makes a read-only preview of the support files for the chosen time range; saves and sends nothing."),
        new("SupportClear", "Troubleshooting", "Clear preview: only clears the support files preview."),
        new("NavHome", "Navigation rail", "Only opens the Home page."),
        new("NavDevices", "Navigation rail", "Only opens the Devices page."),
        new("NavShare", "Navigation rail", "Only opens the Share page (your network, friends, your language models, API keys and hosts shared with this PC)."),
        new("NavCompanion", "Navigation rail", "Only opens the Companion page."),
        new("NavConversations", "Navigation rail", "Only opens the Conversations page."),
        new("NavCreations", "Navigation rail", "Only opens the Creations page."),
        new("NavDiagnostics", "Navigation rail", "Only opens the Diagnostics page."),
        new("NavSettings", "Navigation rail", "Only opens the Settings page."),
        new("TourSkip", "Welcome wizard", "Skip for now: closes the welcome wizard and keeps the device role (companion PC when no role is chosen yet)."),
        new("TourBegin", "Welcome wizard", "Let's begin: only moves from the greeting to the first step."),
        new("TourBack", "Welcome wizard", "Back: only moves back one step."),
        new("AccountButton", "Account picker", "Only opens the account picker (AccountMenu)."),
        new("AccountAddPerson", "Account picker", "Add a person: only opens the Add a person dialog (AddPersonDialog)."),
        new("AddPersonCancel", "Account picker", "Cancel: only closes the Add a person dialog."),
        // Needs --allow-ui-effects: AccountSwitch-<32 hex> and AddPersonAdd (they save session.json in the accounts folder and end the conversation).
        new("WizardScanAgain", "Welcome wizard", "Look again: only asks the local network which Martlet desktops answer, like Add a computer's Find again."),
        new("WizardJoinManual", "Welcome wizard", "Enter an address: only opens Add a computer."),
        new("WizardSpecsNext", "Welcome wizard", "Next on the hardware step: only moves on."),
        new("WizardGamesYes", "Welcome wizard", "An answer to the games question: only changes the selection in the wizard; nothing saves until Show my setup."),
        new("WizardGamesNo", "Welcome wizard", "An answer to the games question: only changes the selection in the wizard; nothing saves until Show my setup."),
        new("WizardOnlineNever", "Welcome wizard", "An answer to the online question: only changes the selection in the wizard; nothing saves until Show my setup."),
        new("WizardOnlineBackup", "Welcome wizard", "An answer to the online question: only changes the selection in the wizard; nothing saves until Show my setup."),
        new("WizardOnlineYes", "Welcome wizard", "An answer to the online question: only changes the selection in the wizard; nothing saves until Show my setup."),
        new("WizardQualityQuick", "Welcome wizard", "An answer to the quality question: only changes the selection in the wizard; nothing saves until Show my setup."),
        new("WizardQualityBalanced", "Welcome wizard", "An answer to the quality question: only changes the selection in the wizard; nothing saves until Show my setup."),
        new("WizardQualitySmarter", "Welcome wizard", "An answer to the quality question: only changes the selection in the wizard; nothing saves until Show my setup."),
        // Needs --allow-ui-effects: Show my setup (WizardQuestionsNext, saves recommendation-preferences.json), choosing a network (saves the device role) and Join (asks the other computer).
        // Needs --allow-ui-effects: Use these suggestions and Skip the key (set up and install), Save key (stores a key) and Open build.nvidia.com (opens the browser).
        new("OpenPeople", "Companion › Listening", "Open People: only opens Companion › People."),
        new("OpenPrompts", "Companion › Personality", "Edit prompts: only opens Companion › Prompts."),
        new("DeviceFactsSection", "Devices", "Hardware and details (or Details) of the selected device: only expands or collapses the facts."),
        new("DeviceReachSection", "Devices", "Connection method of the selected host: only expands or collapses the section."),
        new("DeviceRolesSection", "Devices", "Host roles of the selected device: only expands or collapses the section."),
        new("HealthRecheck", "Home", "Check again in What needs attention: only reads this PC again and refreshes the list."),
        new("LogsRefresh", "Diagnostics", "Refresh: only reads the logs again and shows them."),
        new("DevicesViewMap", "Devices", "Map: only shows the devices as a map."),
        new("DevicesViewList", "Devices", "List: only shows the devices as a list."),
        new("HomeConfiguring", "Home", "The Configuring indicator of the companion PC: only opens the Devices map."),
        new("HostConfiguring", "Home", "The Configuring indicator of the host PC: only opens the Devices map."),
        new("MotionOpenEmotes", "Companion › Motion", "'Kept motions and Make a motion': only opens Companion › Emotes and motions."),
        new("McpDirectoryClose", "MCP directory", "Close: only closes the MCP directory."),
        new("McpDirectoryOptional", "MCP directory", "Only expands or collapses the optional-settings section."),
        // Needs --allow-ui-effects: opening the MCP directory, searching and Load more (send a request to the directory), and Install (writes mcp.json and starts a server).
        new("LiveStop", "Talk window", "Stop (Esc): only stops work (a reply, a recording, vision, a song); starts nothing and never pauses listening."),
        new("LiveRefreshContext", "Talk window", "Refresh context: only forgets the exchanges kept in mind for the next reply; sends nothing and stops nothing."),
        new("LiveSongStop", "Talk window", "Stop singing: only ends the song playing (musically); nothing in the talk window plays a song."),
        new("LiveTasks", "Talk window", "The background tasks chip: only opens the task list."),
        new("LiveTasksClose", "Talk window", "Only closes the task list."),
        // Needs --allow-ui-effects: LiveReadMore (Read more reads more of an app's wiki on the web and adds it to its guide).
        new("MessageContextClose", "What was sent", "Close: only closes the What was sent window."),
        // Needs --allow-ui-effects: MessageContextPlay (it plays the recording) and MessageContextCopyAll (it writes the clipboard).
        new("TouchZonesStop", "Companion › Touch › Touch zones", "Stop: only stops finding zones; sends nothing (the zones found until then are already saved)."),
        new("TouchZonesZoomIn", "Companion › Touch › Touch zones", "Zoom in: only makes the picture on the zone map larger (TouchZonesZoom says how far); saves nothing."),
        new("TouchZonesZoomOut", "Companion › Touch › Touch zones", "Zoom out: only makes the picture on the zone map smaller (TouchZonesZoom says how far); saves nothing."),
        new("TouchZonesZoomReset", "Companion › Touch › Touch zones", "Reset zoom: only resets the size of the picture on the zone map; saves nothing."),
        new("CharacterCombosAdd", "Companion › Emotes and motions › Combos", "Add a combo: only adds an empty row; nothing saves until the row has a tag and parts."),
        // Needs --allow-ui-effects: typing a combo's tag and parts.
        new("RepliesOpenDeepThinking", "Companion › Replies", "Open Deep thinking: only opens that page."),
        new("CheckInsOpenPool", "Companion › Check-ins", "Open Thinking pool: only opens Companion › Thinking pool."),
        // Needs --allow-ui-effects: each check-in's On box, Every, answer choices, fact, condition, trigger, name, task and prompt boxes, CheckInReset-<id>, CheckInCopy-<id>, Add a check-in and Remove (save check-ins.json or the prompts).
        // Needs --allow-ui-effects: CheckInRun-<id> (Check now sends the check to a Thinking pool member, which may be a paid provider).
        new("Pool-vision-AlsoOpen", "Companion › Vision", "Open Thinking pool under 'Also gets your pictures': only opens that page."),
        new("Pool-hearing-AlsoOpen", "Companion › Hearing", "Open Thinking pool under 'Also gets your recordings': only opens that page."),
        // Needs --allow-ui-effects: PicturesCheck (Check asks each place in the list, a ComfyUI server or a cloud provider, whether it can draw now).
        // Needs --allow-ui-effects: Draw a test picture, Set up, Save, Load workflow file, Save key and Add.
        new("LiveToolDeny", "Talk window", "A tool call's Deny: only declines the waiting call (an MCP tool or a terminal command); runs nothing."),
        // Needs --allow-ui-effects: Allow once and Always allow (they run the tool call).
        new("AddComputer", "Devices", "Add a computer: only opens the Add a computer wizard."),
        new("OpenHosts", "Settings › Tools", "Hosts (also in the Character window's remote host section): only opens the Add a computer wizard."),
        new("SelectedDeviceAdd", "Devices map", "The + of the Add a computer details: only opens the Add a computer wizard."),
        new("NodeAction-AddComputer", "Devices map", "The first choice card of the Add a computer details: only opens the Add a computer wizard."),
        new("HomeConnectComputers", "Home", "Connect to your other computers (on a new PC): only opens the Add a computer wizard."),
        new("HostsStepConnect", "Add a computer", "Only moves to the Connect step; changes nothing."),
        new("HostsStepRoles", "Add a computer", "Only moves to the Roles step; changes nothing."),
        new("HostsBack", "Add a computer", "Back: only moves back one step."),
        new("HostsNext", "Add a computer", "Next: only moves to the next step."),
        new("HostsClose", "Add a computer", "Only closes the Add a computer wizard."),
        new("HostsEnterCode", "Add a computer", "Only shows the address-and-code fields."),
        new("HostAddressSection", "Add a computer", "Its private network address: only shows or hides the SSH address."),
        new("DeviceIdSection", "Add a computer", "This PC's device ID: only expands or collapses the section."),
        // Needs --allow-ui-effects: the wizard's Connect, Set up and role buttons (they do the work).
        new("OpenSetupAdvisor", "Home", "Plan a setup from scratch: only opens the setup advisor."),
        new("AdvisorBack", "Setup advisor", "Back: only moves back one step; the answers stay in memory."),
        new("AdvisorNext", "Setup advisor", "Next: only moves to the next step; the answers stay in memory."),
        new("AdvisorClose", "Setup advisor", "Only closes the setup advisor; the answers stay in memory."),
        new("GoalBalanced", "Setup advisor", "Picks the Balanced goal: only changes what the advisor shows; the answer stays in memory."),
        new("GoalSmartest", "Setup advisor", "Picks the Smartest goal: only changes what the advisor shows; the answer stays in memory."),
        new("GoalFastest", "Setup advisor", "Picks the Fastest goal: only changes what the advisor shows; the answer stays in memory."),
        new("GoalPrivate", "Setup advisor", "Picks the Private goal: only changes what the advisor shows; the answer stays in memory."),
        // Needs --allow-ui-effects: the plan's Install on this PC buttons (they do the work).
        new("HomeRecommendedSetup", "Home", "Recommended setup: in a Martlet network, only opens the review (worked out on this PC; served models asked at 127.0.0.1 only; nothing changes). On a PC alone, opens Set it all up for me's question."),
        new("RecommendedSetupClose", "Recommended setup review", "Close: only closes the review."),
        // Needs --allow-ui-effects: Reconfigure (RecommendedSetupApply, changes every computer) and Set it up (installs).
        // Needs --allow-ui-effects: Not now (RecommendedSetupCancel), Your preferences (RecommendedSetupQuality, -Online, -Games-<n>, -Hearing, -HostShare, -UseServedModels, -PreferHostModels, ConfirmationOption)
        // and Settings' Recommendation controls: they save recommendation-preferences.json.
        new("RecommendedSetupUseSuggestion-thinking", "Recommended setup review", "Use the suggestion for the locked Thinking job: only closes the review and opens it again with that suggestion planned; saves nothing."),
        new("RecommendedSetupUseSuggestion-listening", "Recommended setup review", "Use the suggestion for the locked Listening job: only closes the review and opens it again with that suggestion planned; saves nothing."),
        new("RecommendedSetupUseSuggestion-lip-sync", "Recommended setup review", "Use the suggestion for the locked lip-sync job: only closes the review and opens it again with that suggestion planned; saves nothing."),
        // Needs --allow-ui-effects: the locks RecommendedSetupLock-<job> (save recommendation-preferences.json) and Think with it
        // (RecommendedSetupUseServed-<n>, switches Thinking to a model an app on this PC serves).
        new("RecommendedSetupFreeKeyAdd", "Recommended setup review", "Add your key in the free API key prompt: closes the review, then only opens Companion › Thinking at A cloud provider with NVIDIA Build chosen."),
        new("FreeKeyAdd-Thinking", "Companion › Thinking", "Add your key in the free API key prompt: only opens Companion › Thinking at A cloud provider with NVIDIA Build chosen."),
        // Needs --allow-ui-effects: Get a free key (RecommendedSetupFreeKeyGet, FreeKeyGet-Thinking, SetupCloudGetKey-Thinking, HealthFix-recommended-setup-free-key-get) opens the browser.
        new("SituationSimulateGame", "Companion › Thinking (fixture)", "FIXTURE (MARTLET_SIMULATE_SITUATION): only starts or ends a simulated game; changes nothing saved and looks at no real game or host."),
        new("SituationSimulateAway", "Companion › Thinking (fixture)", "FIXTURE (MARTLET_SIMULATE_SITUATION): only makes the Thinking host simulated away or back; changes nothing saved and looks at no real game or host."),
        new("TrayOpen", "Tray menu", "Open Martlet: only shows the window."),
        new("TrayTalk", "Tray menu", "Talk to Martlet: only opens the talk window, like OpenLiveConversation."),
        // Needs --allow-ui-effects: TrayPause, TrayStopListening and TrayStopWatching (they pause Martlet until you turn it on again).
        new("TrayEndTalk", "Tray menu", "End the conversation: only closes the talk window, like CloseLive."),
        // Needs --allow-ui-effects: Start listening, Start watching, Resume Martlet, the character, the startup and closing choices and Exit.
        new("MoveAvatar", "Character overlay", "Only opens or closes the character's right-click menu."),
        new("CharacterTalk", "Character overlay", "Talk to Martlet: only opens the talk window, like TrayTalk."),
        new("CharacterOpenMartlet", "Character overlay", "Open Martlet: only shows the window, like TrayOpen."),
        new("CharacterSettings", "Character overlay", "Character settings: only shows that page."),
        new("CharacterEyes", "Character overlay", "Eyes: only opens its submenu."),
        // Needs --allow-ui-effects: Eyes' choices CharacterEyes-<choice> (save talk-preferences.json), the zoom, position, Let clicks pass through (CharacterClickThrough), Keep on top and Hide character.
        new("NearbyFind", "Martlet on your network", "Find again: only asks the local network Martlet's own discovery question (port 9444) and lists who answers."),
        new("NearbyCancel", "Martlet on your network", "Stop asking: only withdraws this PC's own request."),
        // Needs --allow-ui-effects: Connect, Allow and Deny (they do the work).
        new("CheckHostService", "Host dashboard", "Check again: only reads this PC's host service and, while the engine is unavailable, Windows' WSL status, virtualization services and pending restart. Starts, sets up and pairs nothing."),
        new("SmartHomeFind", "Companion › Smart home", "Find on my network: only asks the local network one multicast DNS question for Home Assistant's service type and lists who answers."),
        new("SmartHomeSetupCancel", "Companion › Smart home", "Not now: only hides the setup form."),
        // Needs --allow-ui-effects: Sign in, Set up, Connect, Disconnect, Add, Install and Restart.
        new("DiscordSetupSteps", "Companion › Discord", "Step by step: only expands the setup steps."),
        // Needs --allow-ui-effects: the portal and invite buttons (open the browser), Save, Forget, Reconnect, the on/off box and every choice.
        new("MessagingPairCancel", "Companion › Messaging", "Cancel: only withdraws the pairing code shown; sends and saves nothing."),
        // Needs --allow-ui-effects: Connect, Pair a chat, Open BotFather, Open in Telegram, Remove, Disconnect and the two check boxes.
        new("ApiKeyCreateCancel", "Share › Apps and API keys", "Cancel: only closes the create dialog without making a key."),
        new("ApiKeyCreatedDone", "Share › Apps and API keys", "Done: only closes the dialog that showed a new key once."),
        // Needs --allow-ui-effects: Create API key, Create key, Copy (the clipboard) and Revoke.
        new("OpenCompanion", "Companion", "Only opens the Personality window. The window saves each change on its own (edits need --allow-ui-effects)."),
        new("OpenAvatar", "Companion", "Only opens the Character window (the character itself does not show). The window saves each change on its own (edits need --allow-ui-effects)."),
        new("OpenLorebooks", "Companion", "Only opens the Lorebooks window. The window saves each change on its own (edits need --allow-ui-effects)."),
        new("OpenMemory", "Companion", "Only opens the Memory window. The window saves each change on its own (edits need --allow-ui-effects)."),
        new("CompanionClose", "Personality window", "Done or Close: only closes the window; it writes nothing that was not already changed."),
        new("AvatarClose", "Character window", "Done or Close: only closes the window; it writes nothing that was not already changed."),
        new("LorebookClose", "Lorebooks window", "Done or Close: only closes the window; it writes nothing that was not already changed."),
        new("MemoryClose", "Memory window", "Done or Close: only closes the window; it writes nothing that was not already changed."),
        new("AvatarAdvanced", "Character window", "Advanced lip-sync setup: only expands or collapses the section."),
        new("RemoteHostSection", "Character window", "Only expands or collapses the remote host section."),
        new("MemoryNewFact", "Memory window", "New: only clears the fact editor; saves nothing."),
        new("MemoryStorageSection", "Memory window", "Where memory is stored: only expands or collapses the section."),
        new("MemoryExportSection", "Memory window", "Export to a file: only expands or collapses the section."),
        // Needs --allow-ui-effects: Add, Save changes and every Delete button (they change facts). MemorySearch is ui_set_text; Show replaced facts is in SafeToggles.
        new("OpenHistory", "Companion › Memory", "Open conversations: only opens the Conversations page, like NavConversations."),
        new("HistorySearchRun", "Conversations", "Search: only filters what the page lists (from memory); writes nothing."),
        new("HistoryShowAll", "Conversations", "Show all: only removes the filter from what the page lists (from memory); writes nothing."),
        new("HistoryEditMessage", "Conversations", "Edit message: only opens the editor with the selected message; nothing saves until Save edit."),
        new("HistoryEditCancel", "Conversations", "Cancel: only closes the message editor; saves nothing."),
        // Needs --allow-ui-effects: the two choices (save conversation-history.json), Save edit, Delete message, Delete this conversation, Delete everything and Stop waiting changes.
        // With HistoryAlsoThere on, those changes also queue for Telegram and Discord. Typing is ui_set_text; choosing an app, conversation or message is ui_select.
        new("MeaningSearchCheck", "Companion › Memory › Finding by meaning", "Check again: only asks Ollama on this PC (loopback) whether the embedding model is there and what it has loaded; downloads, loads and saves nothing."),
        // Needs --allow-ui-effects: its check box and model box (save meaning-search.json) and Download (downloads a model).
        new("ProblemClose", "Problem dialog", "Close: only closes the problem dialog."),
        // Needs --allow-ui-effects: Open logs folder (Explorer) and every Copy button (the clipboard).
        new("OutsideAccessSetUpSignIn", "Outside access dialog", "'Set up sign-in first': only opens the sign-in window for that host; changes nothing by itself."),
        new("FriendsMakeShareLink", "Share › Friends", "Make a share link: only opens the form; nothing is made until Make link (HostInputOk)."),
        new("AcceptShareLink", "Share › Hosts shared with this PC", "Accept a share link: only opens the paste form; nothing signs in until Continue and the confirmation."),
        new("ShareLinkClose", "Share link window", "Close: only closes the share link window."),
        // Needs --allow-ui-effects: ShareLinkCopy-<key> (shows the link), ShareLinkCopy (the clipboard) and ShareLinkStop-<key> (removes the link from every host).
        new("CharacterModelAddCancel", "Add a character", "Cancel: only closes the dialog."),
        // Needs --allow-ui-effects: Add a character, Use and Remove.
        new("CharacterFinderOpen", "Companion › Character", "Find a character...: only opens the Find a character window."),
        new("CharacterFinderClose", "Find a character", "Close: only closes the Find a character window."),
        new("CharacterFinderAddVoice", "Find a character", "Add a voice clip you downloaded: only opens Companion › Voice."),
        // Needs --allow-ui-effects: Search (sends the name to VRoid Hub and GitHub; the Thinking model writes a personality), Write again, Search the web (DuckDuckGo) and Link VRoid Hub (browser).
        // Needs --allow-ui-effects: Open its page, Open, the site searches (browser), Add a model you downloaded, Use this look, Use without a new look, AccountVRoidHubLink and AccountVRoidHubUnlink.
        new("HomeManageCharacters", "Home", "Manage profiles: only opens Companion › Profiles."),
        new("OpenProfiles", "Companion › Character and Personality", "Open profiles: only opens Companion › Profiles."),
        new("ProfilesOpenCharacter", "Companion › Profiles", "A link: only opens Companion › Character."),
        new("ProfilesOpenVoice", "Companion › Profiles", "A link: only opens Companion › Voice."),
        new("ProfilesOpenPersonality", "Companion › Profiles", "A link: only opens Companion › Personality."),
        new("CharacterProfileNew", "Companion › Profiles", "New profile...: only opens the form; nothing saves until Save."),
        new("CharacterProfileCancel", "Companion › Profiles", "Cancel: only closes the form; saves nothing."),
        // Needs --allow-ui-effects: Save, Use (here, Home's HomeCharacterProfile and the icon menu's TrayCharacterProfile-<key>) and Remove.
        new("F5AddVoiceMore", "Add a voice", "Add another recording: only adds an empty recording row (F5AddVoiceDrop-n removes row n); reads and saves nothing until Add voice."),
        // Needs --allow-ui-effects: opening the dialog (F5AddVoice), typing, Fill in the words (F5AddVoiceFill, may send the recording to the Listening host) and Add voice.
        new("F5RecordVoice", "Companion › Voice › Record your own voice", "Only opens the Record your own voice dialog; records nothing."),
        new("OwnVoiceCancel", "Companion › Voice › Record your own voice", "Cancel: only closes the dialog."),
        // Needs --allow-ui-effects: ticking its box, Record (OwnVoiceRecord-<n>, the microphone), Play and Play all (audio) and Add voice.
        new("NavTasks", "Navigation rail", "Only opens the Background tasks page."),
        new("HostRunHide", "Run window", "Hide: only hides the run window while its task keeps running (closes it once the task has finished); stops, sends and saves nothing."),
        new("TasksClear", "Background tasks", "Clear finished: only drops finished tasks' kept output from the list; stops, sends and saves nothing."),
        // Needs --allow-ui-effects: Cancel task (HostRunCancel) and a task's Cancel... (TaskCancel-<id>): they ask first, then stop the task.
        new("NavThinkingRequests", "Navigation rail", "Only opens the Thinking requests page."),
        new("ThinkingRequestsFilter-all", "Thinking requests", "All: only filters the list."),
        new("ThinkingRequestsFilter-active", "Thinking requests", "Waiting or running: only filters the list."),
        new("ThinkingRequestsFilter-problems", "Thinking requests", "With problems: only filters the list."),
        new("ThinkingRequestsTimingExpander", "Thinking requests", "Timing by type: only expands or collapses the section."),
        new("ThinkingRequestsKind", "Thinking requests", "The type box: only opens; choosing a type is ui_select and only filters."),
        new("ThinkingRequestDetailClose", "Thinking requests", "Close: only hides the selected request's details."),
        // Needs --allow-ui-effects: Clear finished (ThinkingRequestsClear, forgets requests) and Copy (ThinkingRequestsCopy, writes the clipboard).
        new("DiscordFriendsAbout", "Companion › Discord › Friends and calls", "What Discord allows: only expands the section."),
        // Needs --allow-ui-effects: Approve, Decline, Call, Remove, Add, the may-call boxes and Update picture now.
        new("HostsJoinWithInvite", "Add a computer", "Join with an invite: only opens its window."),
        new("SignInJoinClose", "Sign-in from outside › Join window", "Close: only closes the window."),
        // Needs --allow-ui-effects: HostSignInSettings (opening the settings window reads the host's sign-in settings over the network and writes household-signin.json).
        new("SignInSettingsClose", "Sign-in from outside › Settings window", "Close: only closes the window."),
        // Needs --allow-ui-effects: Connect (contacts the invite's host), Sign in (pairs), and the settings window's authenticator secret, Save, recovery codes, Remove and Make invite.
        // Needs --allow-ui-effects: SignInAllow, SignInRefusedAllow, SignInRefusedAllowFriend, SignInAccess-<key>, SignInRemove-<key>, SignInProviderSave, SignInProviderRemove and SignInProvidersPush.
        new("AccountOpen", "Account picker", "Account: only opens the Account page."),
        new("AccountClose", "Account page", "Close: only closes the Account page."),
        new("UnlockOther", "Unlock window", "Choose another account: only closes the Unlock window for the account picker."),
        new("ProveClose", "Prove window", "Close: only closes the Prove window."),
        // Needs --allow-ui-effects: everything else on the Account page, Unlock and Prove windows (password, Windows login link, PIN, Windows Hello, Encrypt, Remember, Lock now, Sign out, Merge, Unlock, Sign in).
        // Needs --allow-ui-effects: FriendsCheck and OpenAiCheck (Check now sends signed requests to each host and writes friends.json or openai-models.json).
        new("OpenAiShowProxy", "Share › Share your language models", "Show setup: only opens a window with the steps and the Tailscale Funnel, Nginx Proxy Manager, Nginx, Caddy and Cloudflare Tunnel text to copy; changes nothing."),
        new("OpenAiProxyClose", "Share › Show setup", "Close: only closes the setup window."),
        // Needs --allow-ui-effects: OpenAiCheckAddress (Check address contacts the saved address).
        new("OpenAiGiveAccess", "Share › Share your language models", "Give someone access: only opens the form; no key is made until Make key (OpenAiAccessConfirm)."),
        new("OpenAiAccessCancel", "Share › Give someone access", "Cancel: only closes the form without making a key."),
        new("OpenAiInviteDone", "Share › Ready to send", "Done: only closes the window that showed a new key once."),
        // Needs --allow-ui-effects: OpenAiFrontDoorOpen and OpenAiFrontDoorClose (run martlet-host exposure --front-door on a host and restart its gateway),
        // OpenAiSaveAddress (saves the shared address), OpenAiAccessConfirm (makes a key), OpenAiInviteCopy (puts the key on the clipboard),
        // OpenAiKeyPriority-<id> (changes a key's priority on every host) and OpenAiKeyRevoke-<id> (revokes a key).
        // Needs --allow-ui-effects: OpenAiProxyManagerSetup (signs in to Nginx Proxy Manager and, after a yes, changes or adds a site there).
        // Needs --allow-ui-effects: FriendShare-<host>-<key> and FriendStop-<host>-<key> (they change who may use a host).
        // Needs --allow-ui-effects: SharedHostsCheck, SharedHostCheck-<host>, SharedHostUse-<host>-<job> and SharedHostForget-<host> (Hosts shared with this PC).
        new("LocalServersScan", "Companion › Thinking › This PC", "A model app you already use › Look again: only asks this PC's loopback ports which model apps answer (GET of their model lists); saves, prompts and starts nothing."),
        new("LocalServerFind", "Companion › Thinking › This PC", "Find models: only asks the address typed there (on this PC only) for its models; saves, prompts and starts nothing."),
        new("ThinkingOpenImageModel", "Companion › Thinking", "The Image model link beside ThinkingSenses: only opens Companion › Vision."),
        new("ThinkingOpenAudioModel", "Companion › Thinking", "The Audio model link beside ThinkingSenses: only opens Companion › Hearing."),
        new("ListeningOpenHearing", "Companion › Listening", "The link to Hearing: only opens Companion › Hearing."),
        new("DiscordCallCheck", "Companion › Discord › Martlet in your Discord calls", "Check this PC: reads only. Lists the playback devices' names, looks for Discord's process, and sets up a process loopback and closes it unstarted (records and plays nothing)."),
        // Needs --allow-ui-effects: the call mode's checkboxes, choices and Open camera view.
    ];

    /// <summary>Families of generated IDs (every ID that starts with the entry) that ui_click may press without --allow-ui-effects.</summary>
    internal static readonly Entry[] SafeClickPrefixes =
    [
        new("CompanionTab-", "Companion", "A page in the Companion side list, such as CompanionTab-<page>; only shows that page."),
        new("Node-", "Devices map and list", "A device's card on the map or its row in the list, such as Node-this-pc or Node-host:Nahida-host (Nahida, Nahida-host: each computer by the name it gives itself, with its host ID); only selects that device and shows its details."),
        new("CoverageShow-", "Devices map", "A Show button on the problem card, CoverageShow-<job>; only selects the device that does that job and shows its details."),
        new("Place-", "Where it runs", "A place option in a job's Where it runs list, such as Place-Voice-Computer; only shows that place's choices, which their own buttons commit."),
        new("SpeakingHost-", "Companion › Voice › Voice engine", "A computer pill, such as SpeakingHost-gpu-pc; only shows the engines of that computer."),
        new("HealthCheck-", "Home", "A Health tile, such as HealthCheck-thinking; only opens the page where something changes."),
        new("HealthOpen-", "Home", "A passive fix on a Health item, such as HealthOpen-voice-setup-open-voice or HealthOpen-crash-dismiss; only opens the page where something changes, or hides the item."),
        new("LogLevel-", "Diagnostics", "A level filter, such as LogLevel-errors; only filters the shown log lines."),
        new("LogSource-", "Diagnostics", "A source filter, such as LogSource-all; only filters the shown log lines."),
        new("LogPart-", "Diagnostics", "A part filter, such as LogPart-gateway; only filters the shown log lines."),
        new("LogEntry-", "Diagnostics", "A log line, such as LogEntry-0; only selects that line and shows it in full."),
        new("McpDirectoryResult-", "MCP directory", "A directory result, such as McpDirectoryResult-io.github.upstash/context7; only shows the details of that server."),
        new("F5AddVoiceDrop-", "Add a voice", "The remove button of recording row n, F5AddVoiceDrop-n; only removes that row from the dialog. Nothing is read or saved until Add voice."),
        // MainWindow.OptionPicker.cs
        new("Picker-", "Any option picker", "A choice in an option picker, such as Picker-VoiceEngine-chatterbox or Picker-Pictures-Off; only shows the details of that option below the list."),
        new("PickerCompare-", "Any option picker", "The Compare button of an option picker, such as PickerCompare-VoiceEngine; only shows or hides the comparison table."),
        new("PickerMore-", "Any option picker", "The Show N more button of an option picker, such as PickerMore-VoiceEngine; only shows or hides the rows after the first four."),
        // Needs --allow-ui-effects: the button in an option's details (it commits that option).
        new("DeviceFilter-", "Devices", "A list filter, such as DeviceFilter-attention; only filters the device cards shown."),
        // Needs --allow-ui-effects: LiveJobCancel-<job> (it ends a background job, and Martlet hears that you stopped it next time you talk).
        new("CharacterProfileEdit-", "Companion › Profiles", "The Edit button of a profile, such as CharacterProfileEdit-3f2a9c1b; only opens the form. The form's Save writes."),
        new("LiveJobResultToggle-", "Talk window", "The Show result button of a finished task in the task list, such as LiveJobResultToggle-think-1; only shows or hides what the task found."),
        new("LiveMessageContext-", "Talk window", "What was sent, on one of Martlet's messages, such as LiveMessageContext-2 (the second message that kept it): only opens the What was sent window (MessageContextWindow) for that message. MCP never reads its text."),
        new("MessageContextSection-", "What was sent", "A section, such as MessageContextSection-instructions or MessageContextSection-history; only shows or hides its text."),
        // Never returned: LiveJobResult-<id> (what a task found is not a readable value).
        new("ReadingModel-", "Companion › Reading", "A model pill in a list member's settings, such as ReadingModel-this-pc-ppocrv5-mobile; only shows that model's details and its Set up or Switch button."),
        // Needs --allow-ui-effects: a Reading model's Set up and Switch (they set up the model or switch to it on that computer).
        new("PeopleMemories-", "Companion › People", "The 'What Martlet remembers about them' link, such as PeopleMemories-3; only opens Memory with the facts of that voice."),
        new("Creation-", "Creations", "A creation in the list, such as Creation-3f2a9c1b7d04 (its short id); only shows its text and details. It has no Play, Show or Activate."),
        // Needs --allow-ui-effects: a creation's Rename and Delete (they change it on every computer).
        new("TouchZoneMore-", "Companion › Touch", "The More (Less while open) button of a touch zone, such as TouchZoneMore-3; only shows or hides its box and Delete. It saves nothing."),
        new("TaskShow-", "Background tasks", "The Show or Show output button of a task, such as TaskShow-3; only shows its run window again, or the kept output of a finished task."),
        new("ThinkingRequest-", "Thinking requests", "A request row, such as ThinkingRequest-tr-3; only selects that request and shows it in full (ThinkingRequestDetail)."),
        new("CustomThemeRole-", "Settings › Appearance", "A part of the Custom theme, such as CustomThemeRole-Accent; only shows its color in the editor."),
        // HelpTip.cs and Fold.cs
        new("Help-", "Any page", "A '?' button, such as Help-CheckInsAbout; only shows its explanation card. It saves nothing."),
        // Needs --allow-ui-effects: Help-<id>-Wiki, the card's "Read more in the wiki" link (it opens the browser; IsSafeClick refuses it).
        new("Fold-", "Any page", "A section that you open and close, such as Fold-CheckInMore-emotes; only shows or hides its settings. It saves nothing."),
        // MainWindow.Pools.cs: the pool lists on Companion › Voice, Listening, Thinking and each pooled area's page.
        new("Pool-speaking-Settings-", "Companion › Voice", "The Settings (Hide settings while open) button of a list member, such as Pool-speaking-Settings-0; only shows or hides its settings under it."),
        new("Pool-listening-Settings-", "Companion › Listening", "The Settings (Hide settings while open) button of a list member, such as Pool-listening-Settings-<n>; only shows or hides its settings under it."),
        new("Pool-thinking-Settings-", "Companion › Thinking", "The Settings (Hide settings while open) button of a list member, such as Pool-thinking-Settings-<n>; only shows or hides its settings under it."),
        new("Pool-reading-Settings-", "Companion › Reading", "The Settings (Hide settings while open) button of a list member, such as Pool-reading-Settings-<n>; only shows or hides its settings under it."),
        new("Pool-speaking-Show-", "Companion › Voice", "The 'Show its setup below' button of a place, such as Pool-speaking-Show-host:diva-host; only shows that place's own card under the list, whose own button commits."),
        new("Pool-listening-Show-", "Companion › Listening", "The 'Show its setup below' button of a place, such as Pool-listening-Show-<place>; only shows that place's own card under the list, whose own button commits."),
        new("Pool-speaking-AddCloud", "Companion › Voice", "The 'A cloud provider' button, Pool-speaking-AddCloud; only shows the cloud provider's own card under the list, whose own button commits."),
        new("Pool-listening-AddCloud", "Companion › Listening", "The 'A cloud provider' button, Pool-listening-AddCloud; only shows the cloud provider's own card under the list, whose own button commits."),
        new("Pool-lip-sync-Settings-", "Companion › Lip-sync", "The Settings button of a list member, such as Pool-lip-sync-Settings-0; only shows or hides This PC's address box (Pool-lip-sync-Endpoint) or a computer's Install, Change model and Check it buttons."),
        new("WorkSharingOpen-", "Devices › Sharing work", "An Open button, such as WorkSharingOpen-speaking; only opens the page of that area."),
        // Needs --allow-ui-effects: a pool list's On, Up, Down, Remove, Add and kept-for choices (they save pools.json and share it).
        // Needs --allow-ui-effects: Pool-lip-sync-SaveEndpoint (it saves the address in Pool-lip-sync-Endpoint).
        new("Pool-singing-Settings-", "Companion › Singing", "The Settings button of a member of the music list, such as Pool-singing-Settings-0; only shows or hides its Singing state, Set up Singing here and its own quality."),
        new("Pool-voice-match-Settings-", "Companion › Singing", "The Settings button of a member of the voice match list, such as Pool-voice-match-Settings-0; only shows or hides its Singing state, Set up Singing here, Add VevoSing here and its own model."),
        new("Pool-singing-AddCloud", "Companion › Singing", "The 'A cloud provider' button of the music list, Pool-singing-AddCloud; only shows the Singing cloud form (ElevenLabs Music, Mureka or fal.ai), whose own Add button commits."),
        new("Pool-voice-match-AddCloud", "Companion › Singing", "The 'A cloud provider' button of the voice match list, Pool-voice-match-AddCloud; only shows the Singing cloud form (ElevenLabs Voice Changer), whose own Add button commits."),
        // Needs --allow-ui-effects: Singing's Set up Singing here, every Add <model> here (Pool-singing-AddXl/AddHeartMuLa/AddDiffRhythm/AddYuE-<key>, Pool-voice-match-AddVevo/AddVevo2/AddSeedVc-<key>), quality (Pool-singing-Quality-<key>), music model (Pool-singing-MusicModel-<key>) and model (Pool-voice-match-Model-<key>) (they save or install).
        // Needs --allow-ui-effects: the Singing cloud form's provider choice and Add button (SingingCloudProvider-<area>, SingingCloudAdd-<area>), a cloud member's Save key (Pool-<area>-CloudSaveKey-<key>) and Mureka's own voice box (Pool-singing-OwnVoice-<key>) (they save keys or pools.json).
        new("Pool-pictures-Settings-", "Companion › Picture making", "The Settings button of a place, such as Pool-pictures-Settings-0; only shows or hides its settings."),
        // Needs --allow-ui-effects: PicturesComfyConnect-<n> (Connect contacts that ComfyUI server).
        new("Pool-motion-Settings-", "Companion › Motion", "The Settings button of a list member, such as Pool-motion-Settings-0; only shows or hides where Motion stands there and its Set up Motion here button."),
        // Needs --allow-ui-effects: Set up Motion here (it installs the role).
        new("Pool-vision-Settings-", "Companion › Vision", "The Settings button of a list member, such as Pool-vision-Settings-0; only shows or hides its model and settings."),
        new("Pool-hearing-Settings-", "Companion › Hearing", "The Settings button of a list member, such as Pool-hearing-Settings-<n>; only shows or hides its model and settings."),
        new("Pool-vision-AddCloud", "Companion › Vision", "The 'A cloud provider or server' button, Pool-vision-AddCloud; only shows the cloud form, whose 'Add to the list' commits."),
        new("Pool-hearing-AddCloud", "Companion › Hearing", "The 'A cloud provider or server' button, Pool-hearing-AddCloud; only shows the cloud form, whose 'Add to the list' commits."),
        // Needs --allow-ui-effects: the cloud form's 'Add to the list' (it adds the provider to the list).
    ];

    private static readonly HashSet<string> SafeClickIds = new(SafeClicks.Select(entry => entry.Id), StringComparer.Ordinal);
}
