using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Threading;

namespace Martlet.Desktop;

/// <summary>Martlet in the notification area: closing the window keeps Martlet running there (on by default; Exit Martlet, or
/// Exit in the icon's menu, closes it completely), the icon's menu opens Martlet, starts or shows the talk window, pauses and
/// resumes Martlet, ends the conversation, shows or hides the character, switches character profiles and holds the startup and closing choices, and
/// Settings › Startup and closing has the same choices plus Start with Windows.</summary>
public partial class MainWindow
{
    private TrayIcon? tray;
    private BackgroundPreferences background = new();
    private readonly DispatcherTimer trayTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private ContextMenu? trayMenu;
    /// <summary>Exit was chosen (Exit Martlet, the icon's Exit, an update or Windows signing out), so closing really exits.</summary>
    private bool exiting;
    /// <summary>The window is hidden in the notification area (an update started now restarts Martlet there too).</summary>
    private bool inTray;
    private bool changingBackgroundChoice;

    private nint WindowHandle => new WindowInteropHelper(this).Handle;

    private void InitializeBackground()
    {
        background = BackgroundPreferences.Load(store?.DataDirectory);
        RenderBackground();
    }

    /// <summary>Adds Martlet's notification-area icon. Only the app does this; windows built elsewhere close as before.</summary>
    internal void EnableTray()
    {
        try { tray = new TrayIcon(); }
        catch (Exception error) when (error is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            ErrorLog.Warn("Martlet's notification-area icon couldn't be created; closing the window exits Martlet.", error);
            return;
        }
        tray.Opened += ShowFromTray;
        tray.MenuRequested += ShowTrayMenu;
        trayTimer.Tick += (_, _) => UpdateTray();
        trayTimer.Start();
        UpdateTray();
        RenderBackground();
    }

    /// <summary>Whether Martlet may start without its window: the icon is in the notification area, the data folder opened and no
    /// setup is waiting to continue after a Windows restart.</summary>
    internal bool CanStartInTray => tray is { Added: true } && store is not null && !HostSetupResume.Pending;

    /// <summary>Starts Martlet in the notification area: everything runs, only the window stays hidden until you open it.</summary>
    internal void StartInTray()
    {
        inTray = true;
        new WindowInteropHelper(this).EnsureHandle();
        StartRunningAsync().Forget();
        UpdateTray();
    }

    /// <summary>Shows Martlet's window (from the icon, its menu or a second start). A Martlet dialog that is open keeps the focus,
    /// as it does with the window open.</summary>
    internal void ShowFromTray()
    {
        // While Martlet closes, its window says what it is finishing (and offers Exit now when that takes long).
        if (closing) { RevealClosing(); return; }
        var wasHidden = !IsVisible;
        inTray = false;
        if (wasHidden) Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        var dialog = IsWindowEnabled(WindowHandle) ? null : Application.Current.Windows.OfType<Window>()
            .LastOrDefault(window => !ReferenceEquals(window, this) && window.IsVisible && IsWindowEnabled(new WindowInteropHelper(window).Handle));
        if (dialog is not null)
        {
            if (dialog.WindowState == WindowState.Minimized) dialog.WindowState = WindowState.Normal;
            dialog.Activate();
        }
        else Activate();
        if (wasHidden && DevicesPage.IsVisible) RenderMap();
        UpdateTray();
    }

    private void HideToTray()
    {
        inTray = true;
        Hide();
        openConversation?.UseOwnTaskbarButton();
        if (!background.HintShown)
        {
            tray!.ShowNotice("Martlet is still running",
                "Martlet keeps running in the notification area. Right-click its icon to talk, pause or exit, or change this in Settings.");
            background = background with { HintShown = true };
            background.Save(store?.DataDirectory);
        }
        UpdateTray();
    }

    /// <summary>Exits Martlet completely, whatever the close button does.</summary>
    internal void ExitMartlet()
    {
        if (closing) return;
        exiting = true;
        Close();
    }

    /// <summary>Windows is signing out or shutting down: Martlet exits rather than hiding, without asking about work it interrupts.</summary>
    internal void PrepareForSessionEnd() => exiting = sessionEnding = true;

    /// <summary>Exit couldn't finish yet: the close button goes back to hiding, and a hidden window shows why.</summary>
    private void RefuseExit()
    {
        exiting = false;
        if (!IsVisible) ShowFromTray();
    }

    private string TrayStatusText() => closing ? $"Martlet is closing: {closingStep}"
        : openConversation is { } talk
            ? talk.Paused ? "Martlet is paused"
                : talk.IsListening && talk.IsWatching ? "Martlet is listening and watching"
                : talk.IsListening ? "Martlet is listening" : talk.IsWatching ? "Martlet is watching"
                : talk.IsVisible ? "Martlet: talk window open" : "Martlet is running"
        : "Martlet is running";

    private void UpdateTray() => tray?.SetToolTip(TrayStatusText());

    // ---------- the icon's menu ----------

    private void ShowTrayMenu(Point at)
    {
        if (tray is null) return;
        if (closing) { RevealClosing(); return; }
        if (trayMenu is { IsOpen: true } open) open.IsOpen = false;
        var talk = openConversation;
        // Another Martlet window waits for an answer (Companion, Setup...): then only opening Martlet and exiting make sense.
        var blocked = !IsWindowEnabled(WindowHandle);
        // A Martlet host doesn't talk, listen or show the character, so its menu only offers what's still running.
        var companion = Role == DeviceRole.Companion;
        var canTalk = companion && !blocked && conversation is not null;
        // Its colors come from the palette's ContextMenu and MenuItem styles (Themes\Controls.xaml).
        var menu = new ContextMenu { Placement = PlacementMode.AbsolutePoint, HorizontalOffset = at.X, VerticalOffset = at.Y };
        AutomationProperties.SetAutomationId(menu, "TrayMenu");
        AutomationProperties.SetName(menu, "Martlet");

        var status = TrayItem("TrayStatus", TrayStatusText(), null);
        status.IsHitTestVisible = status.Focusable = false;
        status.SetResourceReference(ForegroundProperty, "MutedBrush");
        menu.Items.Add(status);
        menu.Items.Add(new Separator());
        menu.Items.Add(TrayItem("TrayOpen", "_Open Martlet", ShowFromTray, bold: true));
        if (companion || talk is not null)
            menu.Items.Add(TrayItem("TrayTalk", talk is null ? "_Talk to Martlet" : "Show the _talk window", TrayTalk,
                enabled: talk is not null || canTalk));
        // Always listening starts only from Start listening, here or on Home; it runs without the talk window.
        if (talk is { HandsFree: true, ListeningStarted: true })
            menu.Items.Add(TrayItem("TrayStopListening", "Stop _listening", () => { talk.ToggleListening(); UpdateTray(); }));
        else if (companion && (talk?.HandsFree ?? Talk.HandsFree))
            menu.Items.Add(TrayItem("TrayStartListening", "Start _listening", TrayStartListening, enabled: talk is not null || canTalk));
        // Watching starts only from Start watching, here, on Home or in the talk window; it runs without the talk window too.
        if (talk is { WatchingStarted: true })
            menu.Items.Add(TrayItem("TrayStopWatching", "Stop w_atching", () => { talk.StopWatchingNow(); UpdateTray(); }));
        else if (companion && (talk?.VisionOn ?? Talk.Watch))
            menu.Items.Add(TrayItem("TrayStartWatching", "Start w_atching", TrayStartWatching, enabled: talk is not null || canTalk));
        if (talk is not null)
        {
            menu.Items.Add(talk.Paused
                ? TrayItem("TrayResume", "_Resume Martlet", () => { talk.Resume(); UpdateTray(); })
                : TrayItem("TrayPause", "_Pause Martlet", () => { talk.Pause(); UpdateTray(); }));
            menu.Items.Add(TrayItem("TrayEndTalk", "_End the conversation", talk.End));
        }
        if (companion || avatar.IsShowing)
            menu.Items.Add(TrayItem("TrayCharacter", avatar.IsShowing ? "Hide the _character" : "Show the _character",
                () => Character_Click(this, new RoutedEventArgs()), enabled: !blocked && setupService is not null));
        // Click-through can't be turned off on the character itself, so it is here too (also while the character is hidden).
        if (companion && (avatar.IsShowing || avatar.ClickThrough))
            menu.Items.Add(TrayCheck("TrayCharacterClickThrough", "Let clicks pass thro_ugh the character", avatar.ClickThrough,
                on => SetCharacterClickThroughAsync(on).Forget()));
        if (companion && TrayCharacterProfiles(menu, enabled: !blocked && setupService is not null) is { } profiles)
            menu.Items.Add(profiles);
        menu.Items.Add(new Separator());
        menu.Items.Add(TrayCheck("TrayCloseToTray", "_Keep running when closed", background.CloseToTray, SetCloseToTray));
        menu.Items.Add(TrayCheck("TrayStartWithWindows", "Start with _Windows", ReadStartup().State == StartupState.On,
            SetStartWithWindows));
        menu.Items.Add(new Separator());
        menu.Items.Add(TrayItem("TrayExit", "E_xit Martlet", ExitFromTray));
        trayMenu = menu;
        // The menu belongs to the icon's own window, which always exists (Martlet's window has none until it is first shown, and a
        // menu anchored there never opens) and takes the foreground first, as Windows expects: the menu then has the keyboard and
        // the mouse, and a click elsewhere closes it. Activating the menu itself would move the keyboard off it and close it at once.
        menu.PlacementTarget = tray.MenuAnchor;
        tray.TakeForeground();
        menu.IsOpen = true;
    }

    private MenuItem TrayItem(string id, string header, Action? action, bool enabled = true, bool bold = false)
    {
        var item = new MenuItem { Header = header, IsEnabled = enabled };
        if (bold) item.FontWeight = FontWeights.SemiBold;
        AutomationProperties.SetAutomationId(item, id);
        item.Click += (_, _) =>
        {
            // WPF closes a menu it could put in menu mode; one opened without the foreground (no mouse capture) closes here.
            if (item.Parent is ContextMenu { IsOpen: true } owner) owner.IsOpen = false;
            // After the menu has closed: the talk window opens as a dialog and must not run inside the menu's click.
            if (action is not null) Dispatcher.InvokeAsync(action, DispatcherPriority.Background);
        };
        return item;
    }

    private MenuItem TrayCheck(string id, string header, bool isChecked, Action<bool> changed)
    {
        var item = TrayItem(id, header, null);
        item.IsCheckable = true;
        item.IsChecked = isChecked;
        item.Checked += (_, _) => changed(true);
        item.Unchecked += (_, _) => changed(false);
        return item;
    }

    private void TrayTalk()
    {
        if (closing) return;
        if (openConversation is { } talk)
        {
            if (!talk.IsVisible) talk.Show();
            if (talk.WindowState == WindowState.Minimized) talk.WindowState = WindowState.Normal;
            talk.Activate();
            UpdateTray();
            return;
        }
        if (!IsWindowEnabled(WindowHandle))
        {
            ShowFromTray();
            return;
        }
        Conversation_Click(this, new RoutedEventArgs());
    }

    /// <summary>Start listening from the menu: Martlet listens without opening the talk window.</summary>
    private void TrayStartListening()
    {
        if (closing) return;
        if (openConversation is null && !IsWindowEnabled(WindowHandle))
        {
            ShowFromTray();
            return;
        }
        StartListening();
        UpdateTray();
    }

    /// <summary>Start watching from the menu: Martlet watches without opening the talk window.</summary>
    private void TrayStartWatching()
    {
        if (closing) return;
        if (openConversation is null && !IsWindowEnabled(WindowHandle))
        {
            ShowFromTray();
            return;
        }
        StartWatching();
        UpdateTray();
    }

    private void ExitFromTray()
    {
        if (closing) return;
        if (!IsWindowEnabled(WindowHandle))
        {
            ShowFromTray();
            var open = Application.Current.Windows.OfType<Window>().LastOrDefault(window => !ReferenceEquals(window, this) &&
                window.IsVisible && IsWindowEnabled(new WindowInteropHelper(window).Handle));
            ActionText.Text = open is { Title.Length: > 0 }
                ? $"Close the \"{open.Title}\" window first, then exit."
                : "Close the open Martlet window first, then exit.";
            return;
        }
        ExitMartlet();
    }

    // ---------- Settings › Startup and closing ----------

    private void CloseToTray_Changed(object sender, RoutedEventArgs e)
    {
        if (!changingBackgroundChoice) SetCloseToTray(CloseToTrayCheck.IsChecked == true);
    }

    private void StartWithWindows_Changed(object sender, RoutedEventArgs e)
    {
        if (!changingBackgroundChoice) SetStartWithWindows(StartWithWindowsCheck.IsChecked == true);
    }

    private void StartInTray_Changed(object sender, RoutedEventArgs e)
    {
        if (changingBackgroundChoice) return;
        var on = StartInTrayCheck.IsChecked == true;
        background = background with { StartInTray = on };
        var problem = background.Save(store?.DataDirectory) ? null : "Couldn't save this choice. It applies until Martlet closes.";
        if (ReadStartup().State == StartupState.On) problem = ChangeStartup(true) ?? problem;
        RenderBackground(problem);
    }

    private void SetCloseToTray(bool on)
    {
        if (background.CloseToTray != on)
        {
            background = background with { CloseToTray = on };
            ErrorLog.Info(on ? "Closing the window keeps Martlet in the notification area." : "Closing the window exits Martlet.");
            if (!background.Save(store?.DataDirectory))
            {
                RenderBackground("Couldn't save this choice. It applies until Martlet closes.");
                return;
            }
        }
        RenderBackground();
    }

    private void SetStartWithWindows(bool on) => RenderBackground(ChangeStartup(on));

    /// <summary>Turns Start with Windows on (with the current Start in the notification area choice) or off; returns the problem.</summary>
    private string? ChangeStartup(bool on)
    {
        try
        {
            if (on) WindowsStartup.Enable(StartupCommand(background.StartInTray));
            else WindowsStartup.Disable();
            ErrorLog.Info(on ? $"Martlet starts when you sign in to Windows{(background.StartInTray ? ", in the notification area" : "")}."
                : "Martlet no longer starts with Windows.");
            return null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Security.SecurityException or
            InvalidOperationException)
        {
            ErrorLog.Warn("Couldn't change Windows startup", error);
            return $"Couldn't change Windows startup: {error.Message}";
        }
    }

    private static string StartupCommand(bool inTray) =>
        WindowsStartup.Command(Environment.ProcessPath ?? throw new InvalidOperationException("Martlet's path is unknown."),
            (Application.Current as App)?.DataDirectoryArgument, inTray);

    private static (StartupState State, string? Command) ReadStartup()
    {
        try { return WindowsStartup.Read(); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return (StartupState.Off, null);
        }
    }

    private void RenderBackground(string? problem = null)
    {
        var (state, command) = ReadStartup();
        // A Martlet host never shows the character or listens as it starts; the saved choice stays for a companion PC.
        var host = Role == DeviceRole.Host;
        changingBackgroundChoice = true;
        CloseToTrayCheck.IsChecked = background.CloseToTray;
        StartWithWindowsCheck.IsChecked = state == StartupState.On;
        StartInTrayCheck.IsChecked = background.StartInTray;
        StartInTrayCheck.IsEnabled = state == StartupState.On;
        StartCompanionCheck.IsChecked = background.StartCompanion;
        StartCompanionCheck.IsEnabled = !host;
        changingBackgroundChoice = false;
        var closeText = tray is { Added: false }
            ? "Martlet's notification-area icon isn't available, so closing the window exits Martlet."
            : background.CloseToTray
                ? host
                    ? "Closing the window keeps Martlet running in the notification area by the clock. Right-click its icon to open or exit Martlet."
                    : "Closing the window keeps Martlet running in the notification area by the clock. Right-click its icon to talk, pause or exit."
                : "Closing the window exits Martlet.";
        string? expected;
        try { expected = StartupCommand(background.StartInTray); }
        catch (InvalidOperationException) { expected = null; }
        var startText = state switch
        {
            StartupState.On when !string.Equals(command, expected, StringComparison.OrdinalIgnoreCase) =>
                " Windows starts a Martlet from another folder or with other settings at sign-in; turn Start with Windows off and on to start this one.",
            StartupState.On => background.StartInTray
                ? " Martlet starts in the notification area when you sign in to Windows."
                : " Martlet opens when you sign in to Windows.",
            StartupState.TurnedOffInWindows =>
                " Martlet's startup is turned off in Windows (Settings › Apps › Startup); tick Start with Windows to turn it back on.",
            _ => ""
        };
        BackgroundStatusText.Text = problem ?? closeText + startText + (host
            ? " This PC is a Martlet host, so the character and listening don't start with Martlet here." + (background.StartCompanion
                ? " Your choice to start them is kept for when it's your companion PC again."
                : "")
            : background.StartCompanion
                ? " When Martlet starts, it shows the character and starts listening."
                : "");
    }

    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsWindowEnabled(nint window);
}
