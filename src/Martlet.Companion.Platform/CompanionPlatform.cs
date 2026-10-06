namespace Martlet.Companion.Platform;

/// <summary>The platform services the companion uses. Each per-OS integration returns one from its factory
/// (<c>LinuxPlatform.Create()</c>, <c>MacPlatform.Create()</c>), replacing only the services it has built; the rest
/// keep these defaults, so the app runs (with those features reported unavailable) before an integration lands.</summary>
public sealed record CompanionPlatform
{
    public IPlatformProbe Probe { get; init; } = new DefaultPlatformProbe();
    public IPushToTalkHotkey Hotkey { get; init; } = new NoHotkey();
    public ICharacterOverlay Overlay { get; init; } = new NoOverlay();
    public IScreenCapture ScreenCapture { get; init; } = new NoScreenCapture();
    public ICredentialStore Credentials { get; init; } = new SessionCredentialStore();
    public IAutostart Autostart { get; init; } = new NoAutostart();
    /// <summary>Null: the app uses Avalonia's TrayIcon (StatusNotifierItem on Linux, NSStatusItem on macOS).</summary>
    public ITrayStatus? Tray { get; init; }
    /// <summary>When <see cref="Tray"/> is null: whether the system can show Avalonia's tray icon (Linux needs a
    /// StatusNotifierItem host: KDE, or GNOME with the AppIndicator extension). Null means assume yes.</summary>
    public FeatureStatus? TrayHost { get; init; }

    public static CompanionPlatform Defaults() => new();
}

public sealed class NoHotkey : IPushToTalkHotkey
{
    public FeatureStatus Status { get; } = FeatureStatus.NotBuilt("A global push-to-talk key");
    public Task<FeatureStatus> RegisterAsync(HotkeyGesture gesture, CancellationToken cancellationToken) => Task.FromResult(Status);
    public void Unregister() { }
    public event EventHandler? Pressed { add { } remove { } }
    public event EventHandler? Released { add { } remove { } }
    public void Dispose() { }
}

public sealed class NoOverlay : ICharacterOverlay
{
    public FeatureStatus Status { get; } =
        FeatureStatus.No("Only always-on-top and transparency (Avalonia's own) work here; click-through, non-activating and all-Spaces aren't built for this system yet.");
    public FeatureStatus Attach(NativeWindow window, OverlayBehavior behavior) => Status;
    public void SetInteractiveRegions(NativeWindow window, IReadOnlyList<PixelRect> regions) { }
    public void Detach(NativeWindow window) { }
}

public sealed class NoScreenCapture : IScreenCapture
{
    public FeatureStatus Status { get; } = FeatureStatus.NotBuilt("Watching your screen");
    public Task<FeatureStatus> RequestConsentAsync(CancellationToken cancellationToken) => Task.FromResult(Status);
    public Task<ScreenShot?> CaptureAsync(ScreenCaptureRequest request, CancellationToken cancellationToken) => Task.FromResult<ScreenShot?>(null);
    public void Release() { }
}

public sealed class NoAutostart : IAutostart
{
    public FeatureStatus Status { get; } = FeatureStatus.NotBuilt("Starting Martlet when you log in");
    public bool IsEnabled => false;
    public FeatureStatus SetEnabled(bool enabled) => Status;
}

/// <summary>Keeps secrets in memory until the app quits. The safe default until the system keychain integration
/// lands: nothing is written to disk.</summary>
public sealed class SessionCredentialStore : ICredentialStore
{
    private readonly Dictionary<string, string> secrets = new(StringComparer.Ordinal);
    private readonly Lock gate = new();

    public FeatureStatus Status { get; } =
        FeatureStatus.Yes("Keys are kept only until Martlet quits; saving them in the system keychain isn't built for this system yet.");
    public bool IsPersistent => false;

    public Task<string?> GetAsync(string name, CancellationToken cancellationToken)
    {
        lock (gate) return Task.FromResult(secrets.TryGetValue(name, out var value) ? value : null);
    }

    public Task SetAsync(string name, string secret, CancellationToken cancellationToken)
    {
        lock (gate) secrets[name] = secret;
        return Task.CompletedTask;
    }

    public Task DeleteAsync(string name, CancellationToken cancellationToken)
    {
        lock (gate) secrets.Remove(name);
        return Task.CompletedTask;
    }
}
