using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Martlet.Avatar.Hosting;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace Martlet.Avatar.RendererHost;

internal sealed class RendererWindow : Window
{
    private readonly Stream input, output;
    private readonly CancellationTokenSource lifetime = new();
    private readonly Dictionary<string, AvatarAsset> resources = new(StringComparer.Ordinal);
    // Composition avoids the child-HWND airspace/opacity of the ordinary WPF WebView2.
    private readonly WebView2CompositionControl browser = new()
    {
        DefaultBackgroundColor = System.Drawing.Color.Transparent,
        IsHitTestVisible = false,
        Focusable = false
    };
    private readonly Grid viewport = new() { Background = Brushes.Transparent, Cursor = Cursors.SizeAll };
    private TaskCompletionSource<JsonElement>? response;
    private Guid activation;
    private readonly RendererFailureLatch failure = new();
    private string? userData;

    internal RendererWindow(Stream input, Stream output)
    {
        this.input = input;
        this.output = output;
        Title = "Martlet character overlay";
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        Topmost = true;
        ShowActivated = false;
        WindowStartupLocation = WindowStartupLocation.Manual;
        PlaceOnDesktop();

        var move = new Button
        {
            Content = "Move character", Cursor = Cursors.SizeAll, Padding = new Thickness(10, 5, 10, 5),
            ToolTip = "Drag the character or this handle. Arrow keys move; Shift makes fine adjustments. Home returns to the primary screen."
        };
        AutomationProperties.SetAutomationId(move, "MoveAvatar");
        AutomationProperties.SetName(move, "Move character");
        AutomationProperties.SetHelpText(move, (string)move.ToolTip);
        move.PreviewMouseLeftButtonDown += DragCharacter;
        move.PreviewKeyDown += (_, e) =>
        {
            var step = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? 1 : 10;
            switch (e.Key)
            {
                case Key.Left: Left -= step; break;
                case Key.Right: Left += step; break;
                case Key.Up: Top -= step; break;
                case Key.Down: Top += step; break;
                case Key.Home: PlaceOnDesktop(); break;
                default: return;
            }
            e.Handled = true;
        };
        var close = new Button
        {
            Content = "Close", Padding = new Thickness(10, 5, 10, 5),
            ToolTip = "Close the avatar only. Voice continues."
        };
        AutomationProperties.SetAutomationId(close, "CloseAvatar");
        AutomationProperties.SetName(close, "Close character overlay");
        close.Click += (_, _) => Close();
        var controls = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
        controls.Children.Add(move);
        controls.Children.Add(close);
        DockPanel.SetDock(controls, Dock.Top);
        var layout = new DockPanel();
        layout.Children.Add(controls);
        layout.Children.Add(viewport);
        viewport.Children.Add(browser);
        viewport.Children.Add(new TextBlock
        {
            Text = "Loading character...", Background = SystemColors.WindowBrush, Foreground = SystemColors.WindowTextBrush,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Top, Padding = new Thickness(8)
        });
        viewport.MouseLeftButtonDown += DragCharacter;
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) { e.Handled = true; Close(); } };
        Content = layout;
        Loaded += async (_, _) => await RunAsync();
        Closed += (_, _) => { lifetime.Cancel(); input.Dispose(); output.Dispose(); browser.Dispose(); };
    }

    private void PlaceOnDesktop()
    {
        var area = SystemParameters.WorkArea;
        Width = Math.Min(420, area.Width);
        Height = Math.Min(560, area.Height);
        Left = Math.Max(area.Left, area.Right - Width - 24);
        Top = Math.Max(area.Top, area.Bottom - Height - 24);
    }

    private void DragCharacter(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState != MouseButtonState.Pressed) return;
        e.Handled = true;
        if (sender is Button handle) handle.Focus();
        DragMove();
    }

    private async Task RunAsync()
    {
        try
        {
            using var handshake = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
            handshake.CancelAfter(TimeSpan.FromSeconds(15));
            var message = await RendererProtocol.ReadAsync(input, handshake.Token);
            if (message.Kind != "load") throw new InvalidDataException("Private renderer initialization required.");
            activation = message.Activation;
            var load = RendererProtocol.Data<RendererLoad>(message);
            var assets = await LocalAvatarFiles.SnapshotAsync(load.Profile, handshake.Token);
            if (assets.Revision != load.ResourceRevision) throw new InvalidDataException("Selected resources changed.");
            foreach (var asset in assets.Assets) resources.Add(RendererResourcePolicy.CanonicalName("asset/" + asset.Name), asset);
            var web = Path.Combine(AppContext.BaseDirectory, "web");
            foreach (var name in new[] { "index.html", "app.js" })
                resources.Add(name, new(name, await LocalAvatarFiles.ReadBoundedAsync(Path.Combine(web, name),
                    16 * 1024 * 1024, handshake.Token), name.EndsWith(".html", StringComparison.Ordinal) ? "text/html" : "text/javascript"));
            _ = CoreWebView2Environment.GetAvailableBrowserVersionString();
            userData = Path.Combine(Path.GetTempPath(), "Martlet.Avatar", activation.ToString("N"));
            var environment = await CoreWebView2Environment.CreateAsync(userDataFolder: userData,
                options: new CoreWebView2EnvironmentOptions { AllowSingleSignOnUsingOSPrimaryAccount = false });
            await browser.EnsureCoreWebView2Async(environment);
            var core = browser.CoreWebView2;
            core.Settings.AreHostObjectsAllowed = false;
            core.Settings.AreDevToolsEnabled = false;
            core.Settings.AreDefaultContextMenusEnabled = false;
            core.Settings.AreDefaultScriptDialogsEnabled = false;
            core.Settings.AreBrowserAcceleratorKeysEnabled = false;
            core.PermissionRequested += (_, args) => args.State = CoreWebView2PermissionState.Deny;
            core.NewWindowRequested += (_, args) => args.Handled = true;
            core.DownloadStarting += (_, args) => args.Cancel = true;
            core.NavigationStarting += (_, args) => args.Cancel = args.Uri != RendererResourcePolicy.Document;
            core.FrameNavigationStarting += (_, args) => args.Cancel = true;
            core.ProcessFailed += (_, _) => FailRenderer();
            core.WebMessageReceived += (_, args) =>
            {
                if (args.Source != RendererResourcePolicy.Document) return;
                try
                {
                    if (args.WebMessageAsJson.Length > RendererProtocol.MaximumMessageBytes)
                        throw new InvalidDataException("Browser reply exceeds its limit.");
                    using var document = JsonDocument.Parse(args.WebMessageAsJson);
                    if (document.RootElement.TryGetProperty("error", out var rendererError))
                    {
                        FailRenderer();
                        return;
                    }
                    failure.ThrowIfFailed();
                    response?.TrySetResult(document.RootElement.Clone());
                }
                catch (Exception error) when (error is JsonException or InvalidDataException)
                { FailRenderer(); }
            };
            core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All);
            core.WebResourceRequested += (_, args) =>
            {
                var uri = args.Request.Uri;
                if (RendererResourcePolicy.ResourceName(uri, args.Request.Method) is { } name &&
                    resources.TryGetValue(name, out var asset))
                    args.Response = environment.CreateWebResourceResponse(new MemoryStream(asset.Bytes, writable: false), 200, "OK",
                        $"Content-Type: {asset.ContentType}\r\nCache-Control: no-store\r\n" +
                        $"Content-Security-Policy: {RendererResourcePolicy.ContentSecurityPolicy(load.Profile.Renderer == Martlet.Avatars.AvatarRenderer.Live2D)}\r\n");
                else args.Response = environment.CreateWebResourceResponse(Stream.Null, 403, "Blocked", "Content-Type: text/plain");
            };
            viewport.Children.RemoveAt(1);
            response = new(TaskCreationOptions.RunContinuationsAsynchronously);
            core.Navigate(RendererResourcePolicy.Document);
            await response.Task.WaitAsync(TimeSpan.FromSeconds(15), lifetime.Token);
            var loaded = await BrowserAsync("load", new { renderer = load.Profile.Renderer.ToString(),
                modelFile = assets.ModelFile, resourceRevision = assets.Revision,
                assets = assets.Assets.Select(a => a.Name).ToArray() });
            await ReplyAsync("capabilities", loaded);
            while (!lifetime.IsCancellationRequested)
            {
                message = await RendererProtocol.ReadAsync(input, lifetime.Token);
                if (message.Activation != activation || message.Kind is not ("configure" or "reset" or "apply" or "stop"))
                    throw new InvalidDataException("Renderer command is invalid.");
                var result = await BrowserAsync(message.Kind, message.Data);
                await ReplyAsync("ok", result);
            }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested && !failure.Failed) { }
        catch (Exception error) when (error is IOException or ArgumentException or InvalidOperationException or
            System.Runtime.InteropServices.COMException or TimeoutException or JsonException or
            Martlet.Core.Contracts.ContractException or OperationCanceledException)
        {
            try
            {
                if (activation != Guid.Empty)
                    await RendererProtocol.WriteAsync(output, RendererProtocol.Message("error", activation,
                        new { code = "avatar.renderer_unavailable", message = "Renderer/runtime/resource operation failed. Inspect local prerequisites and retry explicitly." }),
                        CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(1));
            }
            catch (Exception failure) when (failure is IOException or ObjectDisposedException or TimeoutException) { }
        }
        finally { Close(); }
    }

    private async Task<JsonElement> BrowserAsync<T>(string kind, T data)
    {
        failure.ThrowIfFailed();
        response = new(TaskCreationOptions.RunContinuationsAsynchronously);
        browser.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(new { kind, data }, RendererProtocol.Json));
        var result = await response.Task.WaitAsync(TimeSpan.FromSeconds(kind == "load" ? 30 : 2), lifetime.Token);
        failure.ThrowIfFailed();
        if (result.TryGetProperty("error", out _)) throw new InvalidDataException("Browser rejected the selected resource or controls.");
        return result;
    }

    private void FailRenderer()
    {
        failure.Fail();
        response?.TrySetException(new InvalidDataException("Renderer failed; fresh inspection required."));
        lifetime.Cancel();
    }

    private Task ReplyAsync<T>(string kind, T data) =>
        RendererProtocol.WriteAsync(output, RendererProtocol.Message(kind, activation, data), lifetime.Token);
}
