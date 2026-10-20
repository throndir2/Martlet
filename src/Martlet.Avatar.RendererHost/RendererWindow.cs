using System.ComponentModel;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using Martlet.Avatar.Hosting;
using Martlet.Avatar.RendererHost.Logging;
using Martlet.Presentation;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace Martlet.Avatar.RendererHost;

internal sealed partial class RendererWindow : Window
{
    private readonly Stream input, output;
    // Menu choices Martlet itself carries out (hide, open, talk, settings, lock, mute and unmute, click-through, where the eyes
    // go); null when started without it (tests).
    private readonly Stream? requests;
    // Every message to Martlet goes whole and in order: a write is never cut part way (see RendererPipeWriter).
    private readonly RendererPipeWriter replyWriter;
    private readonly RendererPipeWriter? requestWriter;
    // Why the overlay closes by itself, for avatar-renderer.log (Martlet ends the renderer without closing it).
    private string? closeReason;
    private readonly CancellationTokenSource lifetime = new();
    private readonly Dictionary<string, AvatarAsset> resources = new(StringComparer.Ordinal);
    // Composition avoids the child-HWND airspace/opacity of the ordinary WPF WebView2.
    private readonly WebView2CompositionControl browser = new()
    {
        DefaultBackgroundColor = System.Drawing.Color.Transparent,
        IsHitTestVisible = false,
        Focusable = false
    };
    private readonly CharacterViewport viewport = new() { Background = Brushes.Transparent, Cursor = Cursors.SizeAll };
    private readonly Popup speechBubble = new()
    {
        AllowsTransparency = true, Placement = PlacementMode.Absolute, Focusable = false, IsHitTestVisible = false,
        PopupAnimation = PopupAnimation.Fade
    };
    private readonly TextBlock speechText = SpeechText();
    // Sizes the bubble. The shown text can't: while the bubble is hidden its closed popup suspends layout, so measuring the
    // shown text then returns its old size (an empty bubble) and the next speech would overflow a tiny bubble.
    private readonly TextBlock speechMeasure = SpeechText();
    private readonly System.Windows.Shapes.Path speechShape = new() { StrokeThickness = 2, StrokeLineJoin = PenLineJoin.Round };
    // The bubble's button (Read more), under the text while Martlet offers it; it sizes from speechButtonMeasure like the text.
    private readonly Button speechButton = SpeechButton();
    private readonly TextBlock speechButtonMeasure = new() { FontSize = 14, FontFamily = new FontFamily("Segoe UI"), FontWeight = FontWeights.SemiBold };
    private double speechTextHeight;
    // The bubble's soft halo takes the palette's Glow, like the halos behind Martlet's mascot; Windows' high contrast drops it.
    private readonly System.Windows.Media.Effects.DropShadowEffect speechHalo = new()
    {
        BlurRadius = 18, ShadowDepth = 2, Direction = 270, Opacity = 0.9
    };
    private readonly Canvas speechCanvas = new();
    private readonly ScaleTransform speechPop = new(1, 1);
    private RendererSay speechPlacement = new(null);
    private RendererBubble speechShown = new("hidden", 0, 0, 0, 0);
    private ResourceDictionary? palette;
    private bool darkTheme;
    private IReadOnlyDictionary<string, string>? themeColors;
    private bool highContrast;
    private bool closed;
    private TaskCompletionSource<JsonElement>? response;
    private readonly SemaphoreSlim browserGate = new(1, 1);
    private Guid activation;
    // A still renderer draws Martlet's touch zones picture: never on screen, never animated (the model's rest pose).
    private readonly bool still;
    private readonly RendererFailureLatch failure = new();
    // Why the browser couldn't load the selected model (a bounded Live2D/VRM reason), reported back to Martlet.
    private volatile string? modelRejection;
    // Why this PC can't draw the character at all (no Direct3D 9 device for the composition WebView2), reported back to Martlet.
    private string? graphicsProblem;
    private string? userData;

    internal RendererWindow(Stream input, Stream output, Stream? requests = null, bool still = false)
    {
        this.input = input;
        this.output = output;
        this.requests = requests;
        replyWriter = new RendererPipeWriter(output, capacity: 4);
        if (requests is not null) requestWriter = new RendererPipeWriter(requests);
        this.still = still;
        Resources.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new Uri("pack://application:,,,/Martlet.Avatar.RendererHost;component/Themes/Controls.xaml")
        });
        ApplyOverlayTheme(false);
        Loaded += (_, _) =>
        {
            if (highContrast != SystemParameters.HighContrast) ApplyOverlayTheme(darkTheme);
            SystemParameters.StaticPropertyChanged += SystemAppearanceChanged;
        };
        Dispatcher.ShutdownStarted += (_, _) => SystemParameters.StaticPropertyChanged -= SystemAppearanceChanged;
        Title = "Martlet character overlay";
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        Topmost = true;
        ShowActivated = false;
        WindowStartupLocation = WindowStartupLocation.Manual;
        PlaceOnDesktop();
        if (still)
        {
            // Never seen and never touched: fully transparent (clicks pass through), off every screen and out of the taskbar.
            Title = "Martlet character picture";
            Topmost = false;
            ShowInTaskbar = false;
            Opacity = 0;
            IsHitTestVisible = false;
            Left = SystemParameters.VirtualScreenLeft - Width - 100;
            Top = SystemParameters.VirtualScreenTop - Height - 100;
        }

        // The main Martlet window also shows, hides and resets the character; the overlay shows only the character and its menu.
        AutomationProperties.SetAutomationId(viewport, "MoveAvatar");
        viewport.MoveTo = MoveOverlayTo;
        ShowPlacementLock();
        viewport.Children.Add(browser);
        var loading = new TextBlock
        {
            Text = "Loading character...",
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Top, Padding = new Thickness(8)
        };
        loading.SetResourceReference(TextBlock.BackgroundProperty, "SurfaceBrush");
        loading.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");
        viewport.Children.Add(loading);
        viewport.Children.Add(CreateSpeechBubble());
        viewport.MouseLeftButtonDown += DragCharacter;
        viewport.MouseDown += (_, e) => { if (e.ChangedButton == MouseButton.Middle) StartPan(e); };
        viewport.MouseMove += (_, e) => Pan(e.GetPosition(viewport));
        viewport.MouseUp += (_, _) => EndPan();
        // A left press that comes up where it went down, soon enough, is a tap on the character (dragging, locked or panning).
        viewport.MouseMove += (_, e) => TrackPress(e.GetPosition(viewport));
        viewport.MouseLeftButtonUp += (_, e) => EndPress(e.GetPosition(viewport));
        viewport.TouchAt = TouchAt;
        // A press and drag on a locked character can't move it: it strokes the character instead.
        viewport.MouseMove += (_, e) => TrackStroke(e.GetPosition(viewport), Environment.TickCount64);
        viewport.MouseLeftButtonUp += (_, e) => EndStroke(e.GetPosition(viewport), Environment.TickCount64);
        viewport.StrokeAlong = StrokeAlong;
        viewport.ReadFace = ReadFace;
        viewport.TakePicture = TakePicture;
        viewport.ReadPose = ReadPose;
        viewport.ReadMouth = ReadMouth;
        viewport.PlayVoice = PlayVoice;
        viewport.ReadLook = ReadLook;
        viewport.ReadZones = ReadZones;
        viewport.PlayAnimation = PlayAnimationFile;
        viewport.LostMouseCapture += (_, _) =>
        {
            if (stroke is { } lost && !strokeReleasing) EndStroke(lost.Last, Environment.TickCount64);
        };
        viewport.LostMouseCapture += (_, _) =>
        {
            if (panFrom is null) return;
            panFrom = null;
            FramingChanged();
        };
        // Mouse wheel grows the overlay up to screen height, then keeps zooming into the character toward the cursor.
        viewport.MouseWheel += (_, e) =>
        {
            e.Handled = true;
            Zoom(e.Delta > 0 ? ZoomStep : 1 / ZoomStep, e.GetPosition(viewport));
        };
        viewport.ContextMenu = CreateCharacterMenu();
        PreviewKeyDown += (_, e) =>
        {
            var step = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? 1 : 10;
            // A locked place ignores the arrow keys and Home until it is unlocked. In the camera view they frame the character.
            switch (e.Key)
            {
                case Key.Escape: Request("hide"); break;
                case Key.Left when camera is not null: Nudge(-step, 0); break;
                case Key.Right when camera is not null: Nudge(step, 0); break;
                case Key.Up when camera is not null: Nudge(0, -step); break;
                case Key.Down when camera is not null: Nudge(0, step); break;
                case Key.Home when camera is not null: ResetZoom(); break;
                case Key.Left when !placementLocked: Physically("moved", () => Left -= step); PlacementChanged(); break;
                case Key.Right when !placementLocked: Physically("moved", () => Left += step); PlacementChanged(); break;
                case Key.Up when !placementLocked: Physically("moved", () => Top -= step); PlacementChanged(); break;
                case Key.Down when !placementLocked: Physically("moved", () => Top += step); PlacementChanged(); break;
                case Key.Home when !placementLocked: ResetToDefault(); break;
                case Key.OemPlus or Key.Add: Zoom(ZoomStep * ZoomStep, null); break;
                case Key.OemMinus or Key.Subtract: Zoom(1 / (ZoomStep * ZoomStep), null); break;
                case Key.D0 or Key.NumPad0: ResetZoom(); break;
                default: return;
            }
            e.Handled = true;
        };
        Content = viewport;
        Loaded += async (_, _) => await RunAsync();
        Closed += (_, _) =>
        {
            closed = true;
            ErrorLog.Info($"The character overlay closed: {closeReason ?? "its window was closed (not by Martlet)"}.");
            SystemParameters.StaticPropertyChanged -= SystemAppearanceChanged;
            lifetime.Cancel();
            input.Dispose();
            output.Dispose();
            requests?.Dispose();
            browser.Dispose();
        };
    }

    internal void ApplyOverlayTheme(bool dark, IReadOnlyDictionary<string, string>? colors = null)
    {
        darkTheme = dark;
        themeColors = colors;
        highContrast = SystemParameters.HighContrast;
        if (palette is not null) Resources.MergedDictionaries.Remove(palette);
        palette = AppearancePalette.Create(dark, highContrast, colors);
        Resources.MergedDictionaries.Add(palette);
        speechHalo.Color = ((SolidColorBrush)palette["GlowBrush"]).Color;
        speechShape.Effect = highContrast ? null : speechHalo;
    }

    private void SystemAppearanceChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SystemParameters.HighContrast) &&
            !closed && !Dispatcher.HasShutdownStarted && !Dispatcher.HasShutdownFinished)
            Dispatcher.InvokeAsync(() => { if (!closed) ApplyOverlayTheme(darkTheme, themeColors); });
    }

    // The character is fitted into a portrait frame centered in the overlay. Each side of the frame has this much of its
    // width again as transparent room the model can move into (swinging tails, hair, arms), so motion isn't cut off at the
    // frame's edges. Clicks pass through the transparent room; only the character itself is a drag surface.
    private const double SideRoom = 0.5, FrameFraction = 1 / (1 + 2 * SideRoom), DefaultFrameWidth = 420;

    /// <summary>Width of the character's frame; the overlay is this plus the side room on each side.</summary>
    private double FrameWidth => Width * FrameFraction;

    /// <summary>The character's frame within the overlay, in screen coordinates (device-independent pixels).</summary>
    internal Rect Frame => new(Left + FrameOffset(Width), Top, FrameWidth, Height);

    // Martlet's own actions first (they go to Martlet over the request pipe), then the overlay's view, then Hide.
    private ContextMenu CreateCharacterMenu()
    {
        MenuItem Item(string header, string id, string? gesture, Action action)
        {
            var item = new MenuItem { Header = header, InputGestureText = gesture ?? "" };
            AutomationProperties.SetAutomationId(item, id);
            item.Click += (_, _) =>
            {
                CloseMenu(item);
                action();
            };
            return item;
        }
        // A menu opened through UI Automation stays open on its own (see CharacterViewport), so a choice closes it here (from a
        // submenu too).
        static void CloseMenu(MenuItem item)
        {
            DependencyObject? at = item;
            while (at is MenuItem { Parent: var parent }) at = parent;
            if (at is ContextMenu { IsOpen: true } owner) owner.IsOpen = false;
        }
        var talk = Item("_Talk to Martlet", "CharacterTalk", null, () => Request("talk"));
        // Muting goes through Martlet, which saves it (Speak Martlet's replies aloud) and silences a reply it is speaking.
        var mute = muteItem = Item("Mute _voice", "CharacterMuteVoice", null, () => Request(voiceMuted ? "unmute" : "mute"));
        var open = Item("Open _Martlet", "CharacterOpenMartlet", null, () => Request("open"));
        var settings = Item("Character _settings", "CharacterSettings", null, () => Request("settings"));
        // Lingering emotes (glasses, a blush...) stay until a reply turns them off; this turns them all off at once.
        var clearEmotes = Item("_Clear emotes", "CharacterClearEmotes", null, () => Request("clear"));
        // Where the eyes usually go, and whether the character may change that in its replies. Both go through Martlet, which
        // saves them and sends them back (gaze), so the checks show what applies.
        var eyes = new MenuItem { Header = "_Eyes" };
        AutomationProperties.SetAutomationId(eyes, "CharacterEyes");
        var eyeLabels = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [RendererGaze.Personality] = "As the _personality decides", [CharacterGaze.Word(GazeMode.Mouse)] = "Follow your _mouse",
            [CharacterGaze.Word(GazeMode.Near)] = "Follow your mouse when it's _near", [CharacterGaze.Word(GazeMode.Ahead)] = "Look straight _ahead",
            [CharacterGaze.Word(GazeMode.Window)] = "Watch the _window you're using"
        };
        var eyeChoices = RendererGaze.Choices.Select(choice =>
        {
            var item = Item(eyeLabels[choice], "CharacterEyes-" + choice, null, () => Request(RendererRequest.LookPrefix + choice));
            item.IsCheckable = true;
            eyes.Items.Add(item);
            return (Choice: choice, Item: item);
        }).ToArray();
        var eyesFree = Item("_Let the character change it", "CharacterEyes-free", null,
            () => Request(gazeFree ? RendererRequest.FreeOff : RendererRequest.FreeOn));
        eyesFree.IsCheckable = true;
        eyes.Items.Add(new Separator());
        eyes.Items.Add(eyesFree);
        // The checks show what Martlet last said applies (gaze), also when its answer comes while the menu is open.
        void ShowEyes()
        {
            foreach (var (choice, item) in eyeChoices) item.IsChecked = choice == gazeChoice;
            eyesFree.IsChecked = gazeFree;
        }
        eyes.SubmenuOpened += (_, _) => ShowEyes();
        var zoomIn = Item("Zoom _in", "CharacterZoomIn", "+", () => Zoom(ZoomStep * ZoomStep, null));
        var zoomOut = Item("Zoom _out", "CharacterZoomOut", "-", () => Zoom(1 / (ZoomStep * ZoomStep), null));
        var reset = Item("_Reset zoom", "CharacterResetZoom", "0", ResetZoom);
        var home = Item("Reset _position and size", "CharacterResetPosition", "Home", () => ResetToDefault());
        // Locking and unlocking go through Martlet, which saves the place.
        var placeLock = Item("_Lock position", "CharacterLockPosition", null, () => Request(placementLocked ? "unlock" : "lock"));
        // Click-through goes through Martlet, which saves it. Once on, the mouse can't reach this menu: Martlet turns it off.
        var passThrough = Item("Let clicks p_ass through", "CharacterClickThrough", null,
            () => Request(clickThrough ? RendererRequest.ClickThroughOff : RendererRequest.ClickThroughOn));
        var onTop = new MenuItem { Header = "_Keep on top", IsCheckable = true, IsChecked = Topmost };
        AutomationProperties.SetAutomationId(onTop, "CharacterOnTop");
        onTop.Checked += (_, _) => Topmost = true;
        onTop.Unchecked += (_, _) => Topmost = false;
        onTop.Click += (_, _) => CloseMenu(onTop);
        var hide = Item("_Hide character", "CharacterHide", "Esc", () => Request("hide"));
        var menu = new ContextMenu
        {
            Items = { talk, mute, open, settings, clearEmotes, eyes, new Separator(), zoomIn, zoomOut, reset, home, placeLock, passThrough, onTop, new Separator(), hide }
        };
        AutomationProperties.SetAutomationId(menu, "CharacterMenu");
        AutomationProperties.SetName(menu, "Character");
        menu.Closed += (_, _) => menu.StaysOpen = false;
        menu.Opened += (_, _) =>
        {
            // Until Martlet has loaded the character there is no one to ask; Hide still closes the overlay then.
            talk.IsEnabled = mute.IsEnabled = open.IsEnabled = settings.IsEnabled = clearEmotes.IsEnabled = placeLock.IsEnabled = eyes.IsEnabled = CanRequest;
            ShowVoice();
            ShowEyes();
            zoomIn.IsEnabled = CanZoomIn;
            zoomOut.IsEnabled = CanZoomOut;
            reset.IsEnabled = CanResetZoom;
            reset.Header = camera is not null ? "_Reset framing" : "_Reset zoom";
            home.IsEnabled = !placementLocked && camera is null;
            placeLock.Header = placementLocked ? "_Unlock position" : "_Lock position";
            placeLock.IsChecked = placementLocked;
            AutomationProperties.SetName(placeLock, placementLocked ? "Unlock position" : "Lock position");
            passThrough.IsEnabled = CanRequest && camera is null;
            passThrough.IsChecked = clickThrough;
            AutomationProperties.SetName(passThrough, clickThrough ? "Stop letting clicks pass through" : "Let clicks pass through");
            onTop.IsChecked = Topmost;
        };
        return menu;
    }

    private bool CanRequest => requests is { CanWrite: true } && activation != Guid.Empty && !closed && !failure.Failed;

    /// <summary>Asks Martlet to carry out a menu choice. Hiding goes through Martlet so it stops the character cleanly (and
    /// its buttons and tray say so); if Martlet can't be asked, the overlay just closes.</summary>
    private async void Request(string action)
    {
        if (!CanRequest)
        {
            if (action == "hide") CloseBecause("Hide character was chosen and Martlet couldn't be asked to hide it");
            return;
        }
        if (!await SendRequestAsync("request", new RendererRequest(action), $"The character's '{action}' choice") &&
            action == "hide" && !closed)
            CloseBecause("Hide character was chosen and Martlet didn't take the request in time");
    }

    /// <summary>Sends one unprompted message to Martlet on the request pipe, whole and in order. Waits at most 2 seconds for it
    /// to go out (after that it still goes out whole, later); returns whether it went out in time.</summary>
    private async Task<bool> SendRequestAsync<T>(string kind, T data, string what)
    {
        try
        {
            await requestWriter!.WriteAsync(RendererProtocol.Message(kind, activation, data))
                .WaitAsync(TimeSpan.FromSeconds(2), lifetime.Token);
            return true;
        }
        catch (Exception error) when (error is IOException or ObjectDisposedException or OperationCanceledException or
            TimeoutException or InvalidDataException)
        {
            ErrorLog.Warn($"{what} couldn't reach Martlet.", error);
            return false;
        }
    }

    private void CloseBecause(string reason)
    {
        closeReason ??= reason;
        Close();
    }

    private void DragCharacter(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState != MouseButtonState.Pressed) return;
        press = (e.GetPosition(viewport), Environment.TickCount64);
        // In the camera view a drag moves the character within it; Shift+drag moves the window itself.
        if (camera is not null)
        {
            if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
            {
                e.Handled = true;
                DragWindow();
            }
            else StartPan(e);
            return;
        }
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control) && viewZoom > 1)
        {
            StartPan(e);
            return;
        }
        e.Handled = true;
        if (!placementLocked) DragWindow();
        else BeginStroke(e.GetPosition(viewport), Environment.TickCount64);
    }
}
