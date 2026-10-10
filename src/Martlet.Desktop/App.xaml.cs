using System.IO;
using System.Windows;
using System.ComponentModel;
using System.Windows.Threading;
using Martlet.Core.Settings;

namespace Martlet.Desktop;

public partial class App : Application
{
    private ResourceDictionary? palette;
    internal AppearanceTheme SelectedTheme { get; private set; }
    /// <summary>The character's or the owner's colors applied while a character or custom theme is chosen (null with Martlet's
    /// own palettes, or until the character's colors are known).</summary>
    internal IReadOnlyDictionary<string, string>? ThemeColors { get; private set; }
    /// <summary>Whether the palette in use is dark (a custom palette's window background decides).</summary>
    internal bool IsDarkTheme => SelectedTheme.IsDark(ThemeColors);
    internal string? AppearanceNotice { get; private set; }
    /// <summary>The --data-directory Martlet was started with, so an update restarts it with the same one.</summary>
    internal string? DataDirectoryArgument { get; private set; }
    /// <summary>The previous run did not exit cleanly (crash, kill or power loss); Home says so with the logs folder.</summary>
    internal bool CrashedLastTime { get; private set; }
    /// <summary>This process owns its data folder (<see cref="SingleInstance"/>).</summary>
    private SingleInstance? instance;
    /// <summary>The account signed in on this device (null when the data folder couldn't be opened).</summary>
    internal AccountSession? Accounts { get; private set; }

    private static AccountSession? OpenAccounts(string dataDirectory)
    {
        try { return AccountSession.OpenForThisLogin(dataDirectory); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or Martlet.Core.Contracts.ContractException or
            InvalidOperationException)
        {
            ErrorLog.Error("Accounts: couldn't open this PC's account session", error);
            return null;
        }
    }

    internal void ApplyTheme(AppearanceTheme theme, IReadOnlyDictionary<string, string>? colors = null)
    {
        SelectedTheme = theme;
        ThemeColors = theme.HasColors() ? colors : null;
        var next = Appearance.Palette(theme, SystemParameters.HighContrast, ThemeColors);
        if (palette is not null) Resources.MergedDictionaries.Remove(palette);
        Resources.MergedDictionaries.Add(next);
        palette = next;
    }

    private void SystemAppearanceChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SystemParameters.HighContrast))
            Dispatcher.InvokeAsync(() => ApplyTheme(SelectedTheme, ThemeColors));
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        AccessKeys.RequireAlt();
        SettingsStore? store = null;
        string? error = null;
        // Flags after the data folder: an automatic update restarts Martlet minimized and without taking focus from whatever you
        // are doing (--after-update); Start with Windows, and an update while Martlet was in the notification area, start it there
        // without its window (--tray).
        var args = e.Args;
        bool afterUpdate = false, toTray = false;
        while (args is [.., Martlet.Core.Installation.AppUpdateHelper.AfterUpdateArgument or WindowsStartup.TrayArgument])
        {
            if (args[^1] == WindowsStartup.TrayArgument) toTray = true;
            else afterUpdate = true;
            args = args[..^1];
        }
        try
        {
            var directory = args switch
            {
                [] => SettingsStore.DefaultDataDirectory(),
                ["--data-directory", var path] => path,
                _ => throw new ArgumentException("Invalid launch arguments.")
            };
            store = new SettingsStore(directory);
            SimulatedOllamaCrashLoop.DataDirectory = store.DataDirectory;
            if (args.Length == 2) DataDirectoryArgument = directory;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException or InvalidOperationException)
        {
            error = "Martlet can't open its data folder. Launch normally, or choose an accessible folder.";
        }
        var crashedLastTime = ErrorLog.Initialize(ErrorLog.DefaultDirectory(store?.DataDirectory), "desktop");
        CrashedLastTime = crashedLastTime && !afterUpdate;
        ErrorLog.AttachDispatcher(this, "Martlet", showReport: (title, heading, report) =>
            ProblemDialog.Show(MainWindow, title, heading, report, () => ErrorLog.OpenFolder()));
        // Failed provider requests record their HTTP status and the provider's own short explanation locally.
        Martlet.Providers.ProviderDiagnostics.SetSink(line => ErrorLog.Warn(line));
        // Device notices the microphone code chose not to act on, written off the capture's thread.
        Martlet.Audio.AudioDiagnostics.SetSink(ErrorLog.InfoLater);
        // Each Thinking request step by step, and what it waits for (docs/VOICE_LATENCY.md, Thinking trace), written off the
        // reply's path.
        Martlet.Conversation.ThinkingTrace.Listen(ErrorLog.InfoLater);
        if (error is not null) ErrorLog.Warn(error);
        if (store is not null)
        {
            var acquired = true;
            try
            {
                instance = SingleInstance.TryAcquire(store.DataDirectory);
                acquired = instance is not null;
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or WaitHandleCannotBeOpenedException or IOException)
            {
                ErrorLog.Warn("Couldn't check for another Martlet with this data folder", ex);
            }
            if (!acquired)
            {
                ErrorLog.Info("Martlet already runs with this data folder; showing it instead of starting again.");
                if (!toTray) SingleInstance.ShowRunning(store.DataDirectory);
                Shutdown(0);
                return;
            }
        }
        try
        {
            if (store is not null)
            {
                // This Windows user's device ID, before anything pairs, syncs or labels logs with it.
                var device = NetworkIdentity.UseDataDirectory(store.DataDirectory);
                ErrorLog.Info($"Device ID: {device.Id} ({device.Source.ToString().ToLowerInvariant()}).");
                // The kind only, never the e-mail or name; a work account's details can need the domain, so off this thread.
                _ = Task.Run(() => ErrorLog.Info($"Signed in to Windows with a {WindowsLogin.Current}."));
                // Who uses Martlet now (docs/ACCOUNTS.md): signs in for the first time when needed, then runs the account change
                // steps (added here, before StartAsync) before the theme, character and conversation load.
                Accounts = OpenAccounts(store.DataDirectory);
                // Each account's settings (docs/ACCOUNTS.md): the first account on a data folder from before accounts gets its files.
                if (Accounts is { } owner) AccountSettingsStart.Register(owner);
                if (Accounts is { } accounts) Task.Run(() => accounts.StartAsync(CancellationToken.None)).GetAwaiter().GetResult();
                (SelectedTheme, AppearanceNotice) = Appearance.LoadForStartup(store.DataDirectory);
                ThemeColors = Appearance.LoadColors(store.DataDirectory, SelectedTheme);
                HostShells.Current = new SshHostShell(store.DataDirectory);
                HostSetupResume.Initialize(store.DataDirectory, DataDirectoryArgument);
            }
            ApplyTheme(SelectedTheme, ThemeColors);
            SystemParameters.StaticPropertyChanged += SystemAppearanceChanged;
            var main = new MainWindow(store, error);
            MainWindow = main;
            SessionEnding += (_, _) => main.PrepareForSessionEnd();
            if (toTray) main.EnableTray();
            if (toTray && main.CanStartInTray) main.StartInTray();
            else
            {
                if (afterUpdate)
                {
                    main.ShowActivated = false;
                    main.WindowState = WindowState.Minimized;
                }
                main.Show();
                if (!toTray) main.EnableTray();
            }
            instance?.Listen(() => Dispatcher.BeginInvoke(main.ShowFromTray));
        }
        catch (Exception ex)
        {
            ErrorLog.Error("Martlet failed to start", ex);
            var log = ErrorLog.CurrentFile ?? "(log unavailable)";
            // The dialog may become the main window; closing it must not shut Martlet down before Shutdown(1) below.
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            if (palette is null)
            {
                try { ApplyTheme(SelectedTheme, ThemeColors); }
                catch (Exception theme) when (!ErrorLog.IsFatal(theme)) { ErrorLog.Warn("Couldn't apply the theme for the startup error", theme); }
            }
            if (!ProblemDialog.Show(null, "Martlet couldn't start", "Martlet couldn't start",
                    $"{ex.Message}\n\nDetails were saved to:\n{log}\n\n{ex}", () => ErrorLog.OpenFolder()))
                MessageBox.Show($"Martlet couldn't start.\n\n{ex.Message}\n\nDetails were saved to:\n{log}\n\n(Ctrl+C copies this message.)",
                    "Martlet couldn't start", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
            return;
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        SystemParameters.StaticPropertyChanged -= SystemAppearanceChanged;
        instance?.Dispose();
        ErrorLog.MarkCleanExit();
        base.OnExit(e);
    }
}
