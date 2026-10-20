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

    internal const int MaximumTaps = 20;

    internal const int MaximumLookSamples = 60;

    internal const int MaximumPoseSamples = 60;

    internal const int MaximumZoneSamples = 60;

    internal const int MaximumMouthSamples = 60;

    internal const int MaximumVoiceLevels = 400;

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

    private static bool Running(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch (ArgumentException) { return false; }
    }

    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetCursorPos(out NativePoint point);

    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool AllowSetForegroundWindow(int processId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern nint GetProp(nint window, string name);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowTextLength(nint window);

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
