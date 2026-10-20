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

/// <summary>The command session with Martlet: the pipe handshake and command loop, the bridge to the page (one command at a time) and failure.</summary>
internal sealed partial class RendererWindow
{
    private async Task RunAsync()
    {
        try
        {
            using var handshake = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
            handshake.CancelAfter(TimeSpan.FromSeconds(15));
            var message = await RendererProtocol.ReadAsync(input, handshake.Token);
            // A model Martlet shows from memory (a VRoid Hub character) comes first, in pieces: it is never on disk.
            if (message.Kind == "model")
            {
                activation = message.Activation;
                byte[]? model = null;
                string? modelPath = null;
                for (var index = 0; ; index++)
                {
                    var piece = RendererProtocol.Data<RendererModelPiece>(message);
                    if (modelPath is not null && piece.Path != modelPath) throw new InvalidDataException("Model pieces name two models.");
                    modelPath = piece.Path;
                    model = piece.AddTo(model, index);
                    await ReplyAsync("ok", new { piece = index });
                    if (index == piece.Count - 1) break;
                    handshake.CancelAfter(TimeSpan.FromSeconds(30));
                    message = await RendererProtocol.ReadAsync(input, handshake.Token);
                    if (message.Kind != "model" || message.Activation != activation) throw new InvalidDataException("Model pieces stopped part way.");
                }
                RemoteCharacterModels.Put(modelPath!, model!);
                handshake.CancelAfter(TimeSpan.FromSeconds(15));
                message = await RendererProtocol.ReadAsync(input, handshake.Token);
                if (message.Activation != activation) throw new InvalidDataException("Private renderer initialization required.");
            }
            if (message.Kind != "load") throw new InvalidDataException("Private renderer initialization required.");
            activation = message.Activation;
            var load = RendererProtocol.Data<RendererLoad>(message);
            ApplyOverlayTheme(load.DarkTheme, load.ThemeColors);
            if (load.Placement is { } saved) RestorePlacement(saved);
            UseVoice(load.VoiceMuted);
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
            // Without a Direct3D 9 device the composition WebView2 can't draw, and its half-made image would end this process
            // from the finalizer thread (#1083): stop here with the reason instead.
            if (RendererGraphics.Direct3D9Problem(new System.Windows.Interop.WindowInteropHelper(this).EnsureHandle()) is { } graphics)
            {
                graphicsProblem = graphics;
                throw new InvalidOperationException($"The character can't be drawn: {graphics}.");
            }
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
                        if (rendererError.ValueKind == JsonValueKind.String && rendererError.GetString() == "avatar.model_rejected" &&
                            document.RootElement.TryGetProperty("detail", out var detail) && detail.ValueKind == JsonValueKind.String)
                        {
                            modelRejection = new string((detail.GetString() ?? "").Take(300)
                                .Select(c => char.IsControl(c) ? ' ' : c).ToArray()).Trim();
                            ErrorLog.Warn($"The character model couldn't be shown: {modelRejection}");
                        }
                        FailRenderer();
                        return;
                    }
                    if (document.RootElement.TryGetProperty("bounds", out var bounds))
                    {
                        // Unsolicited and never a command reply.
                        if (bounds.ValueKind != JsonValueKind.Object || !bounds.TryGetProperty("top", out var top) ||
                            top.ValueKind != JsonValueKind.Number || !double.IsFinite(top.GetDouble()))
                            throw new InvalidDataException("Browser bounds are invalid.");
                        ApplyContentTop(top.GetDouble());
                        return;
                    }
                    if (document.RootElement.TryGetProperty("touch", out var touch))
                    {
                        // Unsolicited answer to a tap; never a command reply.
                        Touched(touch);
                        return;
                    }
                    if (document.RootElement.TryGetProperty("touches", out var touches))
                    {
                        // Unsolicited answer to a batch of hit tests (a stroke's path, a zoom's focus); never a command reply.
                        TouchesAnswered(touches);
                        return;
                    }
                    if (document.RootElement.TryGetProperty("faceReading", out var faceReading))
                    {
                        // Unsolicited answer to a reading of the face (MCP's character_face); never a command reply.
                        FaceAnswered(faceReading);
                        return;
                    }
                    if (document.RootElement.TryGetProperty("poseReading", out var poseReading))
                    {
                        // Unsolicited answer to a reading of the idle body (MCP's character_pose); never a command reply.
                        PoseAnswered(poseReading);
                        return;
                    }
                    if (document.RootElement.TryGetProperty("mouthReading", out var mouthReading))
                    {
                        // Unsolicited answer to a reading of the mouth (MCP's character_mouth); never a command reply.
                        MouthAnswered(mouthReading);
                        return;
                    }
                    if (document.RootElement.TryGetProperty("zonesReading", out var zonesReading))
                    {
                        // Unsolicited answer to a reading of the touch zones (MCP's character_zones); never a command reply.
                        ZonesAnswered(zonesReading);
                        return;
                    }
                    if (document.RootElement.TryGetProperty("animationPlayed", out var animationPlayed))
                    {
                        // Unsolicited answer to a clip from a file (MCP's character_animation); never a command reply.
                        AnimationAnswered(animationPlayed);
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
                assets = assets.Assets.Select(a => a.Name).ToArray(),
                extras = load.Profile.Renderer == Martlet.Avatars.AvatarRenderer.Live2D ? LocalAvatarFiles.Extras(assets.Assets, assets.ModelFile) : null,
                still });
            await ReplyAsync("capabilities", loaded);
            SendView();
            if (!still) StartLookTracking();
            while (!lifetime.IsCancellationRequested)
            {
                message = await RendererProtocol.ReadAsync(input, lifetime.Token);
                if (message.Activation != activation || message.Kind is not ("configure" or "reset" or "apply" or "stop" or "theme" or "mouth" or "motion" or "action" or "home" or "zoom" or "say" or "lock" or "click-through" or "voice" or "gaze" or "where" or "place" or "camera" or "snapshot" or "zones" or "eyes" or "zoneview" or "animation"))
                    throw new InvalidDataException("Renderer command is invalid.");
                if (message.Kind == "animation")
                {
                    await ReplyAsync("animation", await AnimationAsync(RendererProtocol.Data<RendererAnimation>(message)));
                    continue;
                }
                if (message.Kind == "eyes")
                {
                    await ReplyAsync("eyes", await EyesAsync(message));
                    continue;
                }
                if (message.Kind == "zoneview")
                {
                    await ReplyAsync("ok", await ZoneViewAsync(message));
                    continue;
                }
                if (message.Kind == "camera")
                {
                    UseCamera(RendererProtocol.Data<RendererCamera>(message));
                    await ReplyAsync("ok", new { camera = camera is not null });
                    continue;
                }
                if (message.Kind == "snapshot")
                {
                    RendererPicture picture;
                    // A picture that can't be taken never stops the character; the reply is then empty.
                    try { picture = await SnapshotAsync(RendererProtocol.Data<RendererSnapshot>(message)); }
                    catch (Exception error) when (error is IOException or NotSupportedException or ArgumentException or InvalidDataException or
                        FileFormatException or InvalidOperationException or System.Runtime.InteropServices.COMException)
                    {
                        ErrorLog.Warn($"Couldn't take a picture of the character: {error.Message}");
                        picture = new("", 0, 0);
                    }
                    await ReplyAsync("picture", picture);
                    continue;
                }
                if (message.Kind == "gaze")
                {
                    await ReplyAsync("look", Gaze(RendererProtocol.Data<RendererGaze>(message)));
                    continue;
                }
                // Martlet's Reset position: back to the default spot even when locked (it stays locked there).
                if (message.Kind == "home")
                {
                    ResetToDefault(evenLocked: true);
                    await ReplyAsync("placement", Placement());
                    continue;
                }
                if (message.Kind == "where")
                {
                    await ReplyAsync("placement", Placement());
                    continue;
                }
                // A character profile's own place on this PC (Martlet's profile switch).
                if (message.Kind == "place")
                {
                    var placement = RendererProtocol.Data<RendererPlacement>(message);
                    if (!placement.IsValid) throw new InvalidDataException("The character's place is invalid.");
                    MovePlacement(placement);
                    await ReplyAsync("placement", camera is null ? Placement() : placement);
                    continue;
                }
                if (message.Kind == "lock")
                {
                    LockPlacement(RendererProtocol.Data<RendererLock>(message).Locked);
                    await ReplyAsync("placement", Placement());
                    continue;
                }
                if (message.Kind == "click-through")
                {
                    UseClickThrough(RendererProtocol.Data<RendererClickThrough>(message).On);
                    await ReplyAsync("ok", new { clickThrough });
                    continue;
                }
                if (message.Kind == "voice")
                {
                    UseVoice(RendererProtocol.Data<RendererVoice>(message).Muted);
                    await ReplyAsync("ok", new { });
                    continue;
                }
                if (message.Kind == "zoom")
                {
                    switch (RendererProtocol.Data<RendererZoom>(message).Action)
                    {
                        case "in": Zoom(ZoomStep * ZoomStep, null); break;
                        case "out": Zoom(1 / (ZoomStep * ZoomStep), null); break;
                        case "reset": ResetZoom(); break;
                        case "left": Nudge(-10, 0); break;
                        case "right": Nudge(10, 0); break;
                        case "up": Nudge(0, -10); break;
                        case "down": Nudge(0, 10); break;
                        case "status": break;
                        default: throw new InvalidDataException("Zoom action is invalid.");
                    }
                    await ReplyAsync("view", ViewState());
                    continue;
                }
                if (message.Kind == "theme")
                {
                    var theme = RendererProtocol.Data<RendererTheme>(message);
                    ApplyOverlayTheme(theme.Dark, theme.Colors);
                    await ReplyAsync("ok", new { });
                    continue;
                }
                if (message.Kind == "say")
                {
                    await ReplyAsync("bubble", ShowSpeech(RendererProtocol.Data<RendererSay>(message)));
                    continue;
                }
                var result = await BrowserAsync(message.Kind, message.Data);
                await ReplyAsync("ok", result);
            }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested && !failure.Failed) { }
        catch (Exception error) when (error is IOException or ArgumentException or InvalidOperationException or
            System.Runtime.InteropServices.COMException or TimeoutException or JsonException or InvalidDataException or
            Martlet.Core.Contracts.ContractException or OperationCanceledException)
        {
            // Without this line the log only says the renderer exited cleanly, and Martlet sees a character that just went away.
            closeReason ??= $"it stopped after an error ({error.GetType().Name}: {error.Message})";
            ErrorLog.Warn("The character renderer stopped after an error; it tells Martlet and closes.", error);
            try
            {
                // After any reply still going out, never into it.
                if (activation != Guid.Empty)
                    await replyWriter.WriteAsync(RendererProtocol.Message("error", activation, modelRejection is { Length: > 0 } rejected
                        ? new { code = "avatar.model_rejected", message = $"This model can't be shown: {rejected}" }
                        : graphicsProblem is { } graphics
                        ? new { code = "avatar.renderer_unavailable", message = $"This PC can't draw the character: {graphics}." }
                        : new { code = "avatar.renderer_unavailable", message = "The character renderer failed; check what it needs on this PC and try again." }))
                        .WaitAsync(TimeSpan.FromSeconds(1));
            }
            catch (Exception failure) when (failure is IOException or ObjectDisposedException or TimeoutException) { }
        }
        finally { Close(); }
    }

    private async Task<JsonElement> BrowserAsync<T>(string kind, T data)
    {
        failure.ThrowIfFailed();
        // The page answers one command at a time with no request ID, so a command from outside the command loop (a picture for
        // MCP) waits its turn instead of taking another command's reply.
        await browserGate.WaitAsync(lifetime.Token);
        try
        {
            failure.ThrowIfFailed();
            response = new(TaskCreationOptions.RunContinuationsAsynchronously);
            browser.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(new { kind, data }, RendererProtocol.Json));
            var result = await response.Task.WaitAsync(TimeSpan.FromSeconds(kind switch { "load" => 30, "picture" => 15, "animation" => 10, _ => 2 }), lifetime.Token);
            failure.ThrowIfFailed();
            if (result.TryGetProperty("error", out _)) throw new InvalidDataException("Browser rejected the selected resource or controls.");
            return result;
        }
        finally
        {
            response = null;
            browserGate.Release();
        }
    }

    private void FailRenderer()
    {
        failure.Fail();
        response?.TrySetException(new InvalidDataException("Renderer failed; fresh inspection required."));
        lifetime.Cancel();
    }

    private Task ReplyAsync<T>(string kind, T data) =>
        replyWriter.WriteAsync(RendererProtocol.Message(kind, activation, data)).WaitAsync(lifetime.Token);
}
