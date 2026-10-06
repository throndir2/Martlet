using System.Globalization;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Automation;
using Avalonia.Media;
using Avalonia.Threading;
using Martlet.Companion.Platform;

namespace Martlet.Companion;

/// <summary>The character on the desktop: a borderless, transparent, always-on-top window hosting the VRM/Live2D
/// renderer in Avalonia's NativeWebView (WKWebView on macOS, WebKitGTK on Linux). The platform overlay adds
/// click-through, non-activating and all-Spaces behaviors where it has them. The mouth follows the voice's loudness.</summary>
public sealed class CharacterWindow : Window
{
    private readonly NativeWebView view;
    private readonly CharacterServer server;
    private readonly ICharacterOverlay overlay;
    private readonly Func<float> level;
    private readonly DispatcherTimer lipSync;
    private CharacterModel? model;
    private bool ready, loaded;
    private float sent = -1;

    public string State { get; private set; } = "starting";
    public event EventHandler<string>? StateChanged;

    public CharacterWindow(CharacterServer server, ICharacterOverlay overlay, Func<float> level)
    {
        this.server = server;
        this.overlay = overlay;
        this.level = level;
        Title = "Martlet character";
        Width = 360;
        Height = 520;
        Topmost = true;
        ShowInTaskbar = false;
        CanResize = true;
        WindowDecorations = WindowDecorations.None;
        // Windows dev runs: WebView2 in a layered (transparent) window may never finish loading, so it stays opaque there.
        if (!OperatingSystem.IsWindows())
        {
            Background = Brushes.Transparent;
            TransparencyLevelHint = [WindowTransparencyLevel.Transparent];
        }
        AutomationProperties.SetAutomationId(this, "CharacterWindow");
        view = new NativeWebView { Source = server.Address };
        AutomationProperties.SetAutomationId(view, "CharacterView");
        view.WebMessageReceived += OnMessage;
        view.AdapterCreated += (_, _) => { if (!ready) SetState("web view started"); };
        view.NavigationStarted += (_, _) => { if (!ready) SetState("opening the character page"); };
        view.NavigationCompleted += async (_, _) =>
        {
            if (ready) return;
            var probe = await view.InvokeScript("typeof invokeCSharpAction + ' ' + typeof window.__martletSend + ' ' + document.readyState");
            if (!ready) SetState("page loaded, waiting for the renderer (" + probe + ")");
        };
        Content = view;
        lipSync = new DispatcherTimer(TimeSpan.FromMilliseconds(33), DispatcherPriority.Background, (_, _) => PushMouth());
        var watch = DispatcherTimer.Run(() =>
        {
            if (!ready && server.Served > 0)
                _ = view.InvokeScript("typeof invokeCSharpAction + ' ' + typeof window.__martletSend + ' ' + typeof window.chrome?.webview?.postMessage + ' ' + document.readyState")
                    .ContinueWith(t => Dispatcher.UIThread.Post(() => { if (!ready) SetState($"waiting for the renderer ({server.Served} files served, last {server.LastServed}; page: {(t.IsCompletedSuccessfully ? t.Result : t.Exception?.GetBaseException().Message)})"); }));
            return !ready;
        }, TimeSpan.FromSeconds(2));
        Opened += (_, _) =>
        {
            if (Screens.ScreenFromWindow(this)?.WorkingArea ?? Screens.Primary?.WorkingArea is { } area)
                Position = new PixelPoint(area.Right - (int)(Width * RenderScaling) - 24, area.Bottom - (int)(Height * RenderScaling) - 24);
            if (TryGetPlatformHandle() is { } handle)
            {
                var status = overlay.Attach(new NativeWindow(handle.Handle, handle.HandleDescriptor ?? ""), OverlayBehavior.Character);
                if (status.Available) overlay.SetInteractiveRegions(new NativeWindow(handle.Handle, handle.HandleDescriptor ?? ""),
                    [new Martlet.Companion.Platform.PixelRect(0, 0, (int)(Bounds.Width * RenderScaling), (int)(Bounds.Height * RenderScaling))]);
            }
            lipSync.Start();
        };
        Closed += (_, _) => lipSync.Stop();
        PointerPressed += (_, e) => { if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) BeginMoveDrag(e); };
    }

    public void ShowModel(CharacterModel? character)
    {
        model = character;
        server.Model = character;
        loaded = false;
        if (character is null) SetState("no character: choose a VRM or Live2D model in Settings");
        else if (ready) Load();
    }

    private void Load()
    {
        if (model is null) return;
        SetState("loading " + model.ModelFile);
        _ = view.InvokeScript(CharacterServer.Script(model.LoadMessage()));
    }

    private void OnMessage(object? sender, WebMessageReceivedEventArgs e)
    {
        try
        {
            using var document = JsonDocument.Parse(e.Body ?? "{}");
            var root = document.RootElement;
            if (root.TryGetProperty("ready", out _))
            {
                ready = true;
                Load();
            }
            else if (root.TryGetProperty("modelId", out _))
            {
                loaded = true;
                SetState("showing " + model?.ModelFile);
            }
            else if (root.TryGetProperty("error", out var error))
            {
                loaded = false;
                SetState("failed: " + error.GetString() + (root.TryGetProperty("detail", out var detail) ? " " + detail.GetString() : ""));
            }
        }
        catch (JsonException) { }
    }

    private void PushMouth()
    {
        if (!loaded) return;
        var value = MathF.Round(level() * 50) / 50;
        if (value == sent || value == 0 && sent == 0) return;
        sent = value;
        _ = view.InvokeScript(CharacterServer.Script(new { kind = "mouth", data = new { level = value } }));
    }

    private void SetState(string state)
    {
        State = state;
        StateChanged?.Invoke(this, state);
    }

    internal static string Format(float value) => value.ToString("0.00", CultureInfo.InvariantCulture);
}
