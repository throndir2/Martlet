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
        // The host dashboard's Check again only reads this PC's own host service (Docker, the gateway's role records and network
        // roster, its published port); it starts, sets up and pairs nothing.
        "CheckHostService",
        // Smart home: Find on my network only sends one multicast DNS question for Home Assistant's service type and lists who
        // answers; Not now only hides the setup form. Sign in, Set up, Connect, Share, Add, Install and Restart do the work.
        "SmartHomeFind", "SmartHomeSetupCancel",
        // Apps and API keys: Cancel closes the create dialog without making a key, and Done closes the dialog that showed a new
        // key once. Create API key, Create key, Copy (the clipboard) and Revoke change things, so they need --allow-ui-effects.
        "ApiKeyCreateCancel", "ApiKeyCreatedDone",
        // Personality, Character, Lorebooks and Memory open their windows (the character itself doesn't show), and Done/Close
        // closes them. Those windows save each change on their own as it is made (edits need --allow-ui-effects), so closing
        // never writes anything that wasn't already changed. The Character window's sections only expand.
        "OpenCompanion", "OpenAvatar", "OpenLorebooks", "OpenMemory", "CompanionClose", "AvatarClose", "LorebookClose", "MemoryClose",
        "AvatarAdvanced", "RemoteHostSection"
    };
    /// <summary>Choosing a Companion page in its side list only shows that page; Devices map nodes ("Node-this-pc",
    /// "Node-host:gpu-1") and the problem card's Show buttons only select a device and show its details; a job's
    /// "Where it runs" options ("Place-Voice-Computer") only show that place's choices, which their own buttons commit. Home's
    /// Health tiles ("HealthCheck-thinking") and its passive fixes ("HealthOpen-voice-setup-open-voice", "HealthOpen-crash-dismiss")
    /// only open the page where something changes, or hide the item. Diagnostics' filters ("LogLevel-errors", "LogSource-all",
    /// "LogPart-gateway") only filter the shown lines, and selecting a line ("LogEntry-0") only shows it in full. An MCP directory
    /// result ("McpDirectoryResult-io.github.upstash/context7") only shows that server's details.</summary>
    private static readonly string[] SafeClickPrefixes = ["CompanionTab-", "Node-", "CoverageShow-", "Place-", "HealthCheck-", "HealthOpen-",
        "LogLevel-", "LogSource-", "LogPart-", "LogEntry-", "McpDirectoryResult-"];
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
        "LipSyncNow", "LipSyncNowProblem", "LipSyncOwnTitle", "LipSyncOwnState", "LipSyncDockerTitle", "LipSyncDockerAbout", "LipSyncLoudnessTitle",
        "SelectedDevice", "SelectedDeviceHealth", "ClusterStatus",
        "VisionStatus", "TalkHearVoiceStatus", "SetupCloudHint-Thinking", "SetupLocalRecommendation", "SetupProviderHint", "SetupF5About", "F5VoicesStatus",
        // Companion › Voice › Voices: whether the voice list is shared with the paired Martlet computers, with how many and when,
        // and why Add a voice couldn't add a recording (never the typed name, transcript or file path).
        "F5VoicesShared", "F5AddVoiceProblem",
        // Companion › Listening › Speakers and echo: whether echo reduction is on and how the last listen went (or why it couldn't
        // run). The TalkReduceEcho check box saves the choice, so it needs --allow-ui-effects.
        "TalkReduceEchoStatus",
        // Companion › Voice › Voice engine: the chosen self-hosted engine (F5-TTS, XTTS-v2, GPT-SoVITS or Dia) and where it speaks with its
        // model licence, and the engines the speaking computer still runs besides it (SpeakingEngineOthers). Choosing another engine
        // (ui_select SpeakingEngine) may install a host role and stops the one it replaces, and SpeakingEngineRelease stops the others,
        // so both need --allow-ui-effects.
        "SpeakingEngine", "SpeakingEngineStatus", "SpeakingEngineTags", "SpeakingEngineOthers",
        "SetupOllamaStatus", "SetupLocalModelTest", "HostRunStatus", "RepliesNow", "AppUpdateStatus", "AppCurrentVersion",
        // Companion › Prompts: how many internal prompts are edited or emptied (counts only, never the prompt text).
        "PromptsNow",
        // Editors that save on their own (no Save button): whether every change is saved ("All changes saved.", "Saving...",
        // "Not saved yet: <why>"), in Personality (the Companion window), the Character window and Lorebooks; and the Character
        // window's character status ("Character is showing...", "Character hidden. Voice continues.").
        "CompanionSaveState", "AvatarSaveState", "LorebookSaveState", "AvatarStatus",
        // Companion › Thinking › If Thinking fails: the saved fallback in words (provider, model, whose key; never the key) and
        // what its key field will do.
        "FallbackNow", "FallbackKeyStatus",
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
        // Companion › Smart home: the connection in words (address, name, version, whether it is shared; never the token),
        // the typed address, Find's result line, the setup form's target and outcome (never the password fields), sharing,
        // the flexible-requests state, the devices check and the Home Assistant summary (version, installation, integrations,
        // last backup) or why it couldn't be read.
        "SmartHomeStatus", "SmartHomeAddress", "SmartHomeFindStatus", "SmartHomeSetupTarget", "SmartHomeSetupStatus",
        "SmartHomeShareState", "SmartHomeShareStatus", "SmartHomeToolsStatus", "SmartHomeDevicesStatus", "SmartHomeMqtt",
        "SmartHomeManageStatus", "SmartHomeManageProblem",
        // Devices › Apps and API keys: how many keys and how many hosts have them; the created dialog's title, host addresses
        // with their public key pins, and the example request (it names $MARTLET_API_KEY, never the key). The key itself
        // (ApiKeyValue) is never returned.
        "ApiKeysStatus", "ApiKeyCreatedTitle", "ApiKeyHosts", "ApiKeyExample",
        // Settings › Startup and closing (what closing does and whether Windows starts Martlet), and the notification-area menu's
        // status line (Martlet is running, listening, paused or watching).
        "BackgroundStatus", "TrayStatus",
        // What this PC is for: the navigation rail's "Companion PC" or "Host PC", and Settings' line describing that role.
        "DeviceRoleSummary", "DeviceRoleText",
        // The host dashboard's status under its icon ("Host is running", "Waiting for Docker Desktop", "Not set up yet", ...), its
        // steps' heading ("This host is ready" or "Get this host running") and the line under it (how many steps are left and
        // the next one, or "All set", and when Martlet last checked).
        "HostServiceStatus", "HostStepsHeading", "HostStepsSummary"
    };
    /// <summary>Job titles in the selected device's details ("DeviceComponent-job-Llm" reads "Thinking (conversation model)");
    /// whether each home or host-dashboard step is ticked ("StepState-service" reads "Host service: done") and its buttons'
    /// labels ("Step-roles-0" reads "Add Thinking: Add roles");
    /// Smart home's found Home Assistants ("SmartHomeFound-0" reads "Home: http://192.168.1.20:8123 (Home Assistant 2026.9.4)"),
    /// each paired host's Home Assistant line ("SmartHomeHost-gpu-pc" reads "gpu-pc: can run Home Assistant."), the devices
    /// Home Assistant discovered ("SmartHomeDevice-0" reads "Philips Hue: Hue Bridge") and its waiting updates
    /// ("SmartHomeUpdate-0" reads "Update: Home Assistant Core 2026.9.3 → 2026.9.4");
    /// Companion › Voice's starter voices ("F5VoiceRow-arctic-slt" reads "SLT (US female)", with "· in use" when it is;
    /// never the names of the owner's own recordings);
    /// each home or host-dashboard step's detail line ("StepDetail-docker" says whether Docker Desktop runs, or why it can't start);
    /// the paired computers a job can be handed to ("HostChoice-speaking-gpu-pc" reads "gpu-pc: Runs F5 (f5tts-v1-base).")
    /// and why none are listed or which can't run it ("HostChoices-speaking", "HostChoicesUnable-speaking"); Home's items
    /// ("HealthIssue-ollama" reads "Problem: Ollama isn't running on this PC. ...") and Health tiles ("HealthCheck-microphone"
    /// reads "Microphone: OK. Windows default"); Diagnostics' shown lines, newest first ("LogEntry-0" reads
    /// "21:04:11.532 WARN This PC · App: Host gpu-box stopped answering: ..."); the Martlet desktops found on the network in
    /// Add a computer ("NearbyItem-0" reads "GAMING-PC (192.168.1.31): gaming-pc-host · Martlet 0.17.0"); the Martlet
    /// network's computers ("NetworkMember-host-gpu-pc" reads "gpu-pc. Host, paired with this PC; added on desktop-a.") and
    /// requests to join ("NetworkJoin-desktop-b" reads "DESKTOP-B asks to join. desktop-b, through gpu-pc. Check number ...")
    /// and computers that use one of this PC's hosts but aren't in the network ("NetworkPaired-desktop-c" reads "DESKTOP-C
    /// (desktop-c). Uses gpu-pc; active now. ..."); each device role's detail line in the selected device's details
    /// ("DeviceComponentDetail-users" reads "IMOUTO (desktop-imouto), active now; This PC, active now.",
    /// "DeviceComponentDetail-host-service" reads "Paired as diva-host. Used by IMOUTO (desktop-imouto), active now.");
    /// API keys ("ApiKeyRow-AbC..." reads "Home Assistant. See status and logs. Made on desktop-a 10/2/2026. ... ID AbCdEf.",
    /// never the key or its verifier); a host role's choices in its Add dialog ("HostInput-choice.A2F_ENGINE" reads "local";
    /// never its secret fields), the terms that follow a variant choice ("HostInputTerms-A2F_ENGINE") and each Companion › Prompts
    /// prompt's state ("PromptState-reply_length" reads "Edited." or, while it saves, "Edited. Saving..."; never the prompt text).</summary>
    private static readonly string[] SafeValuePrefixes = ["DeviceComponent-", "DeviceComponentDetail-", "F5VoiceRow-", "StepDetail-", "StepState-", "Step-",
        "HostChoice",
        "HealthIssue-", "HealthCheck-", "LogEntry-", "NearbyItem-", "NetworkMember-", "NetworkJoin-", "NetworkPaired-", "ApiKeyRow-", "SmartHomeFound-", "SmartHomeHost-",
        "SmartHomeDevice-", "SmartHomeUpdate-", "HostInput-choice.", "HostInputTerms-", "PromptState-"];
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
        // A Martlet in the notification area (closed to it, or started with Windows) has no visible window but its icon's window.
        if (!windows.Any(window => window.Current.AutomationId == "MartletMainWindow") && TrayWindow(pid) == 0)
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
            windowStates = windows.Select(window => new
            {
                name = window.Current.Name,
                enabled = IsWindowEnabled(window.Current.NativeWindowHandle)
            }).ToArray(),
            controls = Controls(windows).Select(control =>
            {
                var (window, element) = control;
                var id = element.Current.AutomationId;
                // Status text blocks expose their text as the accessible name; a status button's name carries its state, and a
                // list item's (a Diagnostics log line) its text.
                var value = !IsSafeValue(id) ? null
                    : element.TryGetCurrentPattern(ValuePattern.Pattern, out var pattern) ? ((ValuePattern)pattern).Current.Value
                    : element.Current.ControlType == ControlType.Text || element.Current.ControlType == ControlType.Button ||
                        element.Current.ControlType == ControlType.ListItem || element.Current.ControlType == ControlType.MenuItem
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
        if (!MainWindowVisible(windows) && TrayWindow(pid) == 0)
            throw new InvalidOperationException("The Martlet window is no longer visible.");
        return windows;
    }

    private static bool MainWindowVisible(AutomationElement[] windows) =>
        windows.Any(window => window.Current.AutomationId == "MartletMainWindow");

    // ---------- the notification-area icon (Martlet.Desktop's TrayIcon) ----------

    private const string TrayWindowTitle = "Martlet notification area";
    private const string TrayAddedProperty = "MartletTrayIconAdded";
    private const int TrayCallbackMessage = 0x8000 + 0x4D;
    private const int TrayIconId = 1;
    private const int NinSelect = 0x400, WmContextMenu = 0x7B;
    internal static readonly string[] TrayActions = ["status", "open", "menu", "close"];

    /// <summary>Martlet's notification-area icon: "status" reads whether the icon is shown and the main window visible; "open"
    /// and "menu" send the icon exactly what Explorer sends for a left click (show Martlet) and a right click (its menu, at the
    /// mouse pointer), then ui_snapshot lists the menu's Tray* items; "close" presses the main window's close button, which
    /// hides Martlet in the notification area by default and exits it when Keep running when closed is off, so it needs
    /// --allow-ui-effects.</summary>
    internal object Tray(string action)
    {
        if (!TrayActions.Contains(action)) throw new ArgumentException($"Unknown ui_tray action '{action}'.");
        if (action == "close" && !allowEffects)
            throw new InvalidOperationException("Closing Martlet's window can exit it, so it requires --allow-ui-effects.");
        if (action == "status" && processId is int exited && !Running(exited))
            return new { processId = exited, running = false, trayIcon = false, mainWindowVisible = false, inTray = false };
        var windows = ConnectedWindows();
        var pid = processId!.Value;
        var icon = TrayWindow(pid);
        switch (action)
        {
            case "open" or "menu":
                if (icon == 0) throw new InvalidOperationException("Martlet has no notification-area icon.");
                GetCursorPos(out var pointer);
                var at = (nint)((pointer.Y & 0xFFFF) << 16 | (pointer.X & 0xFFFF));
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
        if (!Running(pid)) return new { processId = pid, running = false, trayIcon = false, mainWindowVisible = false, inTray = false };
        var visible = MainWindowVisible(Windows(pid));
        icon = TrayWindow(pid);
        return new { processId = pid, running = true, trayIcon = icon != 0 && GetProp(icon, TrayAddedProperty) != 0, mainWindowVisible = visible,
            inTray = icon != 0 && !visible };
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
