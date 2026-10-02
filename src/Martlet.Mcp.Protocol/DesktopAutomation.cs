using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Automation;

namespace Martlet.Mcp;

internal sealed class DesktopAutomation(bool allowEffects)
{
    private static readonly HashSet<string> SafeClicks = new(StringComparer.Ordinal)
    {
        "OpenTroubleshooting", "OpenSetup", "OpenAudioSetup", "OpenLiveConversation",
        "OpenConfigurationRecovery", "RefreshDiagnostics",
        "SetupClose", "AudioClose", "CloseLive", "SupportClose",
        "RecoveryClose", "SupportFreeze", "SupportClear",
        "NavHome", "NavDevices", "NavCompanion", "NavSettings", "TourSkip", "DiagnosticsSection",
        "OpenPeople"
    };
    // Read-only status text. Text blocks and buttons have no value, so their accessible name (a text block's text) is returned.
    private static readonly HashSet<string> SafeValues = new(StringComparer.Ordinal)
    {
        "FoundationStatus", "PipelineStatus", "LocalAudioStatus",
        "LiveStatus", "LiveMic", "AudioResult", "SetupActivity", "RecoveryResult", "SupportResult",
        "PeopleStatus", "PeopleSyncStatus", "PeopleVoiceCount", "ListenParakeetStatus", "SetupCharacterView",
        "LipSyncNow", "LipSyncNowProblem"
    };

    /// <summary>Choosing a Companion page in its side list only shows that page.</summary>
    private static bool IsSafeClick(string id) => SafeClicks.Contains(id) || id.StartsWith("CompanionTab-", StringComparison.Ordinal);
    private int? processId;

    internal object Connect(int pid)
    {
        if (pid <= 0) throw new ArgumentException("A positive Martlet desktop process ID is required.");
        using var process = Process.GetProcessById(pid);
        if (!string.Equals(process.ProcessName, "Martlet.Desktop", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("The process is not Martlet.Desktop.");
        var windows = Windows(pid);
        if (!windows.Any(window => window.Current.AutomationId == "MartletMainWindow"))
            throw new InvalidOperationException("The Martlet desktop window is not visible in this interactive session.");
        processId = pid;
        return new { processId = pid, windows = windows.Select(window => window.Current.Name).ToArray() };
    }

    internal object Snapshot()
    {
        var windows = ConnectedWindows();
        return new
        {
            processId,
            windows = windows.Select(window => window.Current.Name).ToArray(),
            controls = Controls(windows).Select(control =>
            {
                var (window, element) = control;
                var id = element.Current.AutomationId;
                // Status text blocks expose their text as the accessible name; a status button's name carries its state.
                var value = !SafeValues.Contains(id) ? null
                    : element.TryGetCurrentPattern(ValuePattern.Pattern, out var pattern) ? ((ValuePattern)pattern).Current.Value
                    : element.Current.ControlType == ControlType.Text || element.Current.ControlType == ControlType.Button ? element.Current.Name : null;
                return new
                {
                    window = window.Current.Name,
                    id,
                    kind = element.Current.ControlType.ProgrammaticName,
                    enabled = element.Current.IsEnabled,
                    value,
                    checkedState = element.TryGetCurrentPattern(TogglePattern.Pattern, out var toggle)
                        ? ((TogglePattern)toggle).Current.ToggleState.ToString() : null
                };
            }).Take(200).ToArray()
        };
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
        if (!windows.Any(window => window.Current.AutomationId == "MartletMainWindow"))
            throw new InvalidOperationException("The Martlet window is no longer visible.");
        return windows;
    }

    private static AutomationElement[] Windows(int pid)
    {
        // UIA's desktop-root traversal can omit live windows while unrelated WPF
        // windows are closing. Discover HWNDs first, then query only this process.
        var handles = new List<nint>();
        if (!EnumWindows((handle, _) =>
            {
                GetWindowThreadProcessId(handle, out var owner);
                if (owner == pid && IsWindowVisible(handle)) handles.Add(handle);
                return true;
            }, 0))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        return handles.Select(handle => WindowForProcess(pid, handle)).ToArray();
    }

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
