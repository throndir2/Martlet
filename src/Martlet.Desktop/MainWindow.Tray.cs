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
/// resumes Martlet, ends the conversation, shows or hides the character and holds the startup and closing choices, and
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
        if (closing) return;
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

    /// <summary>Windows is signing out or shutting down: Martlet exits rather than hiding.</summary>
    internal void PrepareForSessionEnd() => exiting = true;

    /// <summary>Exit couldn't finish yet: the close button goes back to hiding, and a hidden window shows why.</summary>
    private void RefuseExit()
    {
        exiting = false;
        if (!IsVisible) ShowFromTray();
    }

    private string TrayStatusText() => closing ? "Martlet is closing"
        : openConversation is { } talk
            ? talk.Paused ? "Martlet is paused" : talk.IsListening ? "Martlet is listening" : talk.IsWatching ? "Martlet is watching"
                : "Martlet: talk window open"
        : "Martlet is running";

    private void UpdateTray() => tray?.SetToolTip(TrayStatusText());

    // ---------- the icon's menu ----------

    private void ShowTrayMenu(Point at)
    {
        if (closing || tray is null) return;
        if (trayMenu is { IsOpen: true } open) open.IsOpen = false;
        var talk = openConversation;
        // Another Martlet window waits for an answer (Companion, Setup...): then only opening Martlet and exiting make sense.
        var blocked = !IsWindowEnabled(WindowHandle) && talk is null;
        var menu = new ContextMenu { Placement = PlacementMode.AbsolutePoint, HorizontalOffset = at.X, VerticalOffset = at.Y };
        menu.SetResourceReference(BackgroundProperty, "SurfaceBrush");
        menu.SetResourceReference(BorderBrushProperty, "BorderBrush");
        menu.SetResourceReference(ForegroundProperty, "TextBrush");
        AutomationProperties.SetAutomationId(menu, "TrayMenu");
        AutomationProperties.SetName(menu, "Martlet");

        var status = TrayItem("TrayStatus", TrayStatusText(), null);
        status.IsHitTestVisible = status.Focusable = false;
        status.SetResourceReference(ForegroundProperty, "MutedBrush");
        menu.Items.Add(status);
        menu.Items.Add(new Separator());
        menu.Items.Add(TrayItem("TrayOpen", "_Open Martlet", ShowFromTray, bold: true));
        menu.Items.Add(TrayItem("TrayTalk", talk is null ? "_Talk to Martlet" : "Show the _talk window", TrayTalk,
            enabled: talk is not null || !blocked && conversation is not null));
        if (talk is not null)
        {
            menu.Items.Add(talk.Paused
                ? TrayItem("TrayResume", "_Resume Martlet", () => { talk.Resume(); UpdateTray(); })
                : TrayItem("TrayPause", "_Pause Martlet", () => { talk.Pause(); UpdateTray(); }, enabled: talk.CanPause));
            menu.Items.Add(TrayItem("TrayEndTalk", "_End the conversation", () => { if (talk.IsVisible) talk.Close(); }));
        }
        menu.Items.Add(TrayItem("TrayCharacter", avatar.IsShowing ? "Hide the _character" : "Show the _character",
            () => Character_Click(this, new RoutedEventArgs()), enabled: !blocked && setupService is not null));
        menu.Items.Add(new Separator());
        menu.Items.Add(TrayCheck("TrayCloseToTray", "_Keep running when closed", background.CloseToTray, SetCloseToTray));
        menu.Items.Add(TrayCheck("TrayStartWithWindows", "Start with _Windows", ReadStartup().State == StartupState.On,
            SetStartWithWindows));
        menu.Items.Add(new Separator());
        menu.Items.Add(TrayItem("TrayExit", "E_xit Martlet", ExitFromTray));
        trayMenu = menu;
        // Anchored to a Martlet window (the active one, else the talk window or this one) so the menu takes keyboard focus and mouse
        // capture from it like any context menu; it still opens at the icon.
        menu.PlacementTarget = Application.Current.Windows.OfType<Window>().FirstOrDefault(window => window.IsActive) ?? talk ?? (UIElement)this;
        tray.TakeForeground();
        menu.IsOpen = true;
        // The menu takes the foreground too, or it would stay open after clicking elsewhere.
        if (PresentationSource.FromVisual(menu) is HwndSource popup) SetForegroundWindow(popup.Handle);
    }

    private MenuItem TrayItem(string id, string header, Action? action, bool enabled = true, bool bold = false)
    {
        var item = new MenuItem { Header = header, IsEnabled = enabled };
        if (bold) item.FontWeight = FontWeights.SemiBold;
        item.SetResourceReference(ForegroundProperty, "TextBrush");
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
            if (talk.WindowState == WindowState.Minimized) talk.WindowState = WindowState.Normal;
            talk.Activate();
            return;
        }
        if (!IsWindowEnabled(WindowHandle))
        {
            ShowFromTray();
            return;
        }
        Conversation_Click(this, new RoutedEventArgs());
    }

    private void ExitFromTray()
    {
        if (closing) return;
        if (openConversation is { } talk)
        {
            if (talk.IsVisible) talk.Close();
            // The talk window's dialog finishes closing first.
            Dispatcher.InvokeAsync(ExitFromTray, DispatcherPriority.Background);
            return;
        }
        if (!IsWindowEnabled(WindowHandle))
        {
            ShowFromTray();
            ActionText.Text = "Close the open Martlet window first, then exit.";
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
        changingBackgroundChoice = true;
        CloseToTrayCheck.IsChecked = background.CloseToTray;
        StartWithWindowsCheck.IsChecked = state == StartupState.On;
        StartInTrayCheck.IsChecked = background.StartInTray;
        StartInTrayCheck.IsEnabled = state == StartupState.On;
        changingBackgroundChoice = false;
        var closeText = tray is { Added: false }
            ? "Martlet's notification-area icon isn't available, so closing the window exits Martlet."
            : background.CloseToTray
                ? "Closing the window keeps Martlet running in the notification area by the clock. Right-click its icon to talk, pause or exit."
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
        BackgroundStatusText.Text = problem ?? closeText + startText;
    }

    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsWindowEnabled(nint window);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetForegroundWindow(nint window);
}
