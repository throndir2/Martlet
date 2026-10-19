using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Automation;
using System.Windows.Automation.Text;

namespace Martlet.Mcp;

internal sealed partial class DesktopAutomation(bool allowEffects)
{
    /// <summary>Whether ui_* tools may press buttons that change things (--allow-ui-effects).</summary>
    internal bool AllowsEffects => allowEffects;

    /// <summary>The Martlet desktops running in this Windows session, by process ID.</summary>
    internal static int[] RunningDesktops()
    {
        var session = Process.GetCurrentProcess().SessionId;
        var found = new List<int>();
        foreach (var process in Process.GetProcessesByName("Martlet.Desktop"))
            using (process)
            {
                try { if (process.SessionId == session && !process.HasExited) found.Add(process.Id); }
                catch (Exception error) when (error is InvalidOperationException or Win32Exception) { }
            }
        return [.. found.Order()];
    }

    private int? processId;

    internal object Connect(int? requested)
    {
        var pid = requested ?? RunningDesktops() switch
        {
            [] => throw new InvalidOperationException("Martlet isn't running in this Windows session. Start Martlet, then call ui_connect again."),
            [var only] => only,
            var several => throw new ArgumentException($"More than one Martlet runs here ({string.Join(", ", several)}); pass pid.")
        };
        if (pid <= 0) throw new ArgumentException("A positive Martlet desktop process ID is required.");
        using var process = Process.GetProcessById(pid);
        if (!string.Equals(process.ProcessName, "Martlet.Desktop", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(process.ProcessName, "Martlet.Companion", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("The process is not Martlet.Desktop or Martlet.Companion.");
        var windows = Windows(pid);
        // A Martlet in the notification area (closed to it, or started with Windows) has no visible window but its icon's window;
        // one that couldn't start shows only its problem dialog.
        if (!MainWindowVisible(windows) && !ProblemShown(windows) && !AccountStartShown(windows) && TrayWindow(pid) == 0)
            throw new InvalidOperationException("The Martlet desktop window is not visible in this interactive session.");
        processId = pid;
        return new { processId = pid, windows = windows.Select(window => window.Current.Name).ToArray(), inTray = !MainWindowVisible(windows),
            resources = Resources(pid) };
    }

    internal object Snapshot(bool layout = false, string? idPrefix = null)
    {
        var windows = ConnectedWindows();
        return new
        {
            processId,
            // Front first (the order Windows stacks them), so it shows which window covers which.
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
                    ["clickThrough"] = (GetWindowLongPtrW(handle, ExtendedStyleIndex) & TransparentStyle) != 0,
                    // The Martlet window it belongs to and always stays in front of (a dialog over its window), or null for a
                    // window of its own that another Martlet window can cover, such as the talk window.
                    ["ownedBy"] = GetWindow(handle, OwnerWindow) is var owner && owner != 0
                        ? windows.FirstOrDefault(other => other.Current.NativeWindowHandle == unchecked((int)owner))?.Current.Name
                        : null
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
                var value = !AutomationPolicy.IsSafeValue(id) ? null : AutomationPolicy.Redact(
                    element.TryGetCurrentPattern(ValuePattern.Pattern, out var pattern) ? ((ValuePattern)pattern).Current.Value
                    // A slider reads as its number ("80" for Voice volume at 80%).
                    : element.TryGetCurrentPattern(RangeValuePattern.Pattern, out var range)
                        ? ((RangeValuePattern)range).Current.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)
                    : element.Current.ControlType == ControlType.Text || element.Current.ControlType == ControlType.Button ||
                        element.Current.ControlType == ControlType.ListItem || element.Current.ControlType == ControlType.MenuItem ||
                        element.Current.ControlType == ControlType.RadioButton || element.Current.ControlType == ControlType.CheckBox ||
                        element.Current.ControlType == ControlType.Hyperlink
                        ? element.Current.Name
                    // A combo box without a value pattern reads as its selected option.
                    : element.TryGetCurrentPattern(SelectionPattern.Pattern, out var choice)
                        ? ((SelectionPattern)choice).Current.GetSelection().FirstOrDefault()?.Current.Name : null);
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
                        element.Current.ControlType == ControlType.ComboBox || id.StartsWith("Help-", StringComparison.Ordinal)))
                    entry["help"] = help;
                // A box you type in: its example text (InputHint, dimmed while the box is empty), which it exposes as its help
                // text, or its own help. Never what is typed in it.
                else if (element.Current.ControlType == ControlType.Edit && element.Current.HelpText is { Length: > 0 } example)
                    entry["help"] = example;
                // A section you open and close (an expander such as "Fold-CheckInMore-emotes"): whether it is open now.
                if (element.Current.ControlType != ControlType.ComboBox &&
                    element.TryGetCurrentPattern(ExpandCollapsePattern.Pattern, out var section))
                    entry["expanded"] = ((ExpandCollapsePattern)section).Current.ExpandCollapseState != ExpandCollapseState.Collapsed;
                // A control ui_move can move (the character overlay's MoveAvatar), and whether it can move now: false while the
                // character's position is locked.
                if (element.TryGetCurrentPattern(TransformPattern.Pattern, out var transform))
                    entry["movable"] = ((TransformPattern)transform).Current.CanMove;
                // The control that has the keyboard focus (where ui_type without id types): only that one says so.
                if (Focused(element)) entry["focused"] = true;
                if (layout)
                {
                    entry["bounds"] = Box(element.Current.BoundingRectangle);
                    entry["textBounds"] = FirstLineBounds(element);
                }
                return entry;
            }).Take(200).ToArray(),
            resources = Resources(processId)
        };
    }

    /// <summary>How many Window Manager and GDI objects the desktop holds of what Windows allows it, and its windows by class
    /// (the same reading the desktop log's "Window resources" lines use); null when they can't be read.</summary>
    internal static object? Resources(int? pid)
    {
        if (pid is not { } id || Martlet.Mcp.Shared.GuiResources.Read(id) is not { } count) return null;
        var census = Martlet.Mcp.Shared.GuiResources.Census(id);
        return new
        {
            windowManagerObjects = count.User, windowManagerPeak = count.UserPeak, windowManagerLimit = count.UserQuota,
            gdiObjects = count.Gdi, gdiPeak = count.GdiPeak, gdiLimit = count.GdiQuota,
            windows = census.Windows, windowThreads = census.Threads,
            windowClasses = census.Classes.Take(10).Select(c => new { @class = c.Class, count = c.Count }).ToArray()
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
    [DllImport("user32.dll")] private static extern nint GetWindow(nint window, uint command);
    private const uint OwnerWindow = 4;
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
        if (!allowEffects && !AutomationPolicy.IsSafeClick(id))
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

    /// <summary>The keys ui_type presses besides characters, with their virtual-key codes (all but Backspace and Escape are
    /// extended keys). Up and Down move through a list, such as the Devices list.</summary>
    internal static readonly IReadOnlyDictionary<string, (int Code, bool Extended)> TypeKeys = new Dictionary<string, (int, bool)>(StringComparer.Ordinal)
    {
        ["backspace"] = (0x08, false), ["delete"] = (0x2E, true), ["left"] = (0x25, true), ["right"] = (0x27, true),
        ["up"] = (0x26, true), ["down"] = (0x28, true), ["home"] = (0x24, true), ["end"] = (0x23, true), ["escape"] = (0x1B, false)
    };

    internal const int MaximumTyped = 100, MaximumTypeStepMs = 1000;

    /// <summary>Types the way a person does, one key at a time: with <paramref name="id"/> that text box takes the keyboard focus
    /// first (as a click into it does); without it, the keys go to whatever has the focus in the window, so a test can type, let
    /// the page draw again and type on without clicking. Each character of <paramref name="text"/>, then each of
    /// <paramref name="keys"/>, is a key message posted to that Martlet window only (never to another app or the screen), every
    /// <paramref name="stepMs"/>. Unlike ui_set_text the box isn't filled at once, so a box the page replaced under the typing
    /// shows: the result names the control that has the keyboard focus afterwards.</summary>
    internal async Task<object> TypeAsync(string? id, string? text, string[]? keys, string? window, int? stepMs)
    {
        if (!allowEffects) throw new InvalidOperationException("Typing requires --allow-ui-effects.");
        text ??= "";
        keys ??= [];
        if (text.Length == 0 && keys.Length == 0) throw new ArgumentException("Give text, keys or both.");
        if (text.Length > MaximumTyped || keys.Length > MaximumTyped)
            throw new ArgumentException($"Type at most {MaximumTyped} characters and {MaximumTyped} keys at a time.");
        if (keys.FirstOrDefault(key => !TypeKeys.ContainsKey(key)) is { } unknown)
            throw new ArgumentException($"Unknown key '{unknown}'. Keys: {string.Join(", ", TypeKeys.Keys)}.");
        var step = TimeSpan.FromMilliseconds(Math.Clamp(stepMs ?? 50, 0, MaximumTypeStepMs));
        AutomationElement target;
        if (id is not null)
        {
            var element = Find(id, window);
            if (!element.Current.IsEnabled || element.Current.ControlType != ControlType.Edit && element.Current.ControlType != ControlType.ComboBox)
                throw new InvalidOperationException($"Control '{id}' is not an enabled text field.");
            element.SetFocus();
            target = Controls(ConnectedWindows()).First(control => control.Element.Equals(element)).Window;
        }
        else
        {
            var windows = ConnectedWindows();
            target = (window is null ? windows.FirstOrDefault(w => w.Current.AutomationId == "MartletMainWindow")
                : windows.FirstOrDefault(w => string.Equals(w.Current.Name, window, StringComparison.Ordinal)))
                ?? throw new ArgumentException(window is null ? "Martlet's main window is not open." : $"Window '{window}' is not open.");
        }
        var handle = (nint)target.Current.NativeWindowHandle;
        RequireOwnedVisibleWindow(processId!.Value, handle);
        foreach (var character in text)
        {
            PostMessage(handle, CharMessage, character, 1);
            await Task.Delay(step);
        }
        foreach (var key in keys)
        {
            var (code, extended) = TypeKeys[key];
            var flags = 1 | (extended ? 1 << 24 : 0);
            PostMessage(handle, KeyDownMessage, code, flags);
            PostMessage(handle, KeyUpMessage, code, unchecked(flags | (int)0xC0000000));
            await Task.Delay(step);
        }
        // The window takes the last key before the focus is read.
        await Task.Delay(TimeSpan.FromMilliseconds(150));
        var focused = Controls([target]).FirstOrDefault(control => Focused(control.Element)).Element;
        return new { typed = text.Length, keys = keys.Length, focused = focused?.Current.AutomationId };
    }

    private const int KeyDownMessage = 0x100, KeyUpMessage = 0x101, CharMessage = 0x102;

    // Whether a control has the keyboard focus. The window frame's own parts (TitleBar, SystemMenuBar) say so too while the window
    // has it, so they don't count.
    private static bool Focused(AutomationElement element) =>
        element.Current.HasKeyboardFocus && element.Current.FrameworkId != "Win32";

    /// <summary>Checkboxes that only change what a window shows (nothing is saved or sent), so ui_toggle turns them without
    /// --allow-ui-effects. The Memory window's Show replaced facts lists the facts kept as history too.</summary>
    private static readonly HashSet<string> SafeToggles = new(StringComparer.Ordinal) { "MemoryShowHistory" };

    internal object Toggle(string id)
    {
        if (!allowEffects && !SafeToggles.Contains(id)) throw new InvalidOperationException("Checkbox changes require --allow-ui-effects.");
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
        if (outputPath is not null) McpServer.RequireLocalNewFile(outputPath, "outputPath");
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
            System.IO.File.Copy(file, outputPath, overwrite: false);
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
            // The VRMA clips seen playing and how much of the body they had (0 to 1).
            animations = found.Select(pose => At(pose, ["animation", "name"])).Where(value => value is { ValueKind: System.Text.Json.JsonValueKind.String })
                .Select(value => value!.Value.GetString()).Distinct().ToArray(),
            animationWeight = Range(Numbers(found, "animation", "weight")),
            moved = bones.ToDictionary(name => name, name => new { x = Spread(Numbers(found, "bones", name, "x")), y = Spread(Numbers(found, "bones", name, "y")) })
        };
    }

    internal const int MaximumMouthSamples = 60;
    internal const int MaximumVoiceLevels = 400;

    /// <summary>Gives the showing VRM character a VRMA clip from the file at <paramref name="path"/> (or, without one, plays the clip
    /// it keeps as <paramref name="name"/>) through MoveAvatar's UI Automation value ("animation:{...}"), and waits for the
    /// renderer's answer. With <paramref name="samples"/> it then reads the pose (character_pose) to show the clip playing. It
    /// changes the character, so it needs --allow-ui-effects.</summary>
    internal async Task<object> AnimateCharacterAsync(string? path, string? name, bool play, bool inPlace, int? samples, int? gapMs)
    {
        if (!allowEffects) throw new InvalidOperationException("Playing a clip on the character requires --allow-ui-effects.");
        if (path is null && name is null) throw new ArgumentException("Give path (a .vrma file) or name (a clip the character keeps).");
        if (path is not null && !System.IO.Path.IsPathFullyQualified(path)) throw new ArgumentException("path is the full path of a .vrma file.");
        if (path is not null && !System.IO.File.Exists(path)) throw new ArgumentException("The .vrma file doesn't exist.");
        if (name is not null && !Martlet.Avatar.Hosting.VrmAnimationClip.IsValidName(name))
            throw new ArgumentException($"name is 1 to {Martlet.Avatar.Hosting.VrmAnimationClip.MaximumNameLength} characters without control characters.");
        if (samples is < 1 or > MaximumPoseSamples) throw new ArgumentException($"samples is 1 to {MaximumPoseSamples}.");
        if (gapMs is < 0 or > 5000) throw new ArgumentException("gapMs is 0 to 5000.");
        var element = Find("MoveAvatar");
        if (!element.TryGetCurrentPattern(ValuePattern.Pattern, out var pattern))
            throw new InvalidOperationException("The character overlay can't be reached through UI Automation.");
        var value = (ValuePattern)pattern;
        if (value.Current.IsReadOnly) throw new InvalidOperationException("The character can't play a clip until it has loaded.");
        static System.Text.Json.JsonElement? Played(string text) => string.IsNullOrEmpty(text) ? null :
            System.Text.Json.JsonDocument.Parse(text).RootElement is { ValueKind: System.Text.Json.JsonValueKind.Object } root &&
            root.TryGetProperty("animation", out var animation) ? animation.Clone() : null;
        var before = Played(value.Current.Value)?.GetRawText();
        value.SetValue("animation:" + System.Text.Json.JsonSerializer.Serialize(new { path, name, play, inPlace }));
        var waited = Stopwatch.StartNew();
        System.Text.Json.JsonElement? after;
        while ((after = Played(value.Current.Value))?.GetRawText() == before && waited.Elapsed < TimeSpan.FromSeconds(10)) await Task.Delay(20);
        var played = after is { } read && read.GetRawText() != before ? read : (System.Text.Json.JsonElement?)null;
        var pose = samples is { } count && played is not null ? await PoseCharacterAsync(count, gapMs) : null;
        return new { played, pose, note = played is null ? "The renderer didn't answer within 10 seconds." : null };
    }

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
        if (!MainWindowVisible(windows) && !ProblemShown(windows) && !AccountStartShown(windows) && TrayWindow(pid) == 0)
            throw new InvalidOperationException("The Martlet window is no longer visible.");
        return windows;
    }

    private static bool MainWindowVisible(AutomationElement[] windows) =>
        windows.Any(window => window.Current.AutomationId == "MartletMainWindow");

    /// <summary>Martlet's problem dialog ("Martlet couldn't start" or an unexpected error) is open.</summary>
    private static bool ProblemShown(AutomationElement[] windows) =>
        windows.Any(window => window.Current.AutomationId == "ProblemDialog");

    /// <summary>An account window that shows at start, before the main window (docs/ACCOUNTS.md: Unlock, Continue as ...?, Choose
    /// an account, the Prove sign-in).</summary>
    private static bool AccountStartShown(AutomationElement[] windows) =>
        windows.Any(window => window.Current.AutomationId is "UnlockWindow" or "ContinueAsDialog" or "AccountChooseDialog" or "ProveWindow");

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
