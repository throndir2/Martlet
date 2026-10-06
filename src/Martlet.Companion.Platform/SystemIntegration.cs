namespace Martlet.Companion.Platform;

/// <summary>Keys and secrets (API keys, host pairing tokens). Linux: Secret Service (libsecret / D-Bus
/// org.freedesktop.secrets). macOS: Keychain (generic password, service "Martlet"). Names are short ASCII ids such as
/// "openai-api-key". Secrets never go to settings files or logs.</summary>
public interface ICredentialStore
{
    FeatureStatus Status { get; }

    /// <summary>False when secrets last only until the app quits (the default before the OS store lands).</summary>
    bool IsPersistent { get; }

    Task<string?> GetAsync(string name, CancellationToken cancellationToken);
    Task SetAsync(string name, string secret, CancellationToken cancellationToken);
    Task DeleteAsync(string name, CancellationToken cancellationToken);
}

/// <summary>Start Martlet when the user logs in. Linux: ~/.config/autostart/martlet.desktop. macOS: a login item
/// (SMAppService) or LaunchAgent. Off unless the user turns it on.</summary>
public interface IAutostart
{
    FeatureStatus Status { get; }
    bool IsEnabled { get; }
    FeatureStatus SetEnabled(bool enabled);
}

public enum TrayState { Idle, Listening, Thinking, Speaking, Problem }

public enum TrayCommand { ShowWindow, ToggleCharacter, Quit }

/// <summary>Tray icon (Linux StatusNotifierItem) or menu bar extra (macOS). Optional: when a platform provides none,
/// the app uses Avalonia's built-in TrayIcon, which already covers both. Implement it only for what Avalonia lacks.</summary>
public interface ITrayStatus : IDisposable
{
    FeatureStatus Status { get; }
    void Show(TrayState state, string tooltip);
    event EventHandler<TrayCommand>? Command;
}
