using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Martlet.Avatar.Hosting;
using Martlet.Core.Settings;
using Martlet.Core.Creations;
using Martlet.Discord.Calls;
using Martlet.Presentation;

namespace Martlet.Desktop;

/// <summary>Companion › Discord › Martlet in your Discord calls (companion mode on the owner's own Discord account, under
/// Martlet's own bot). Every control has an automation ID; the status lines are MCP SafeValues.</summary>
public partial class MainWindow
{
    private readonly DiscordCallService discordCalls;
    private bool callOutputsListed;

    private string? callPictureState;
    private string callPicturePrompt = "";
    private bool callPictureDrawing;

    private void ChooseCallPicture()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Choose the camera background", Filter = "Pictures (*.png;*.jpg;*.jpeg;*.webp)|*.png;*.jpg;*.jpeg;*.webp", CheckFileExists = true
        };
        if (dialog.ShowDialog(this) != true) return;
        byte[] bytes;
        try
        {
            if (new System.IO.FileInfo(dialog.FileName).Length > Martlet.Core.Pictures.PictureImages.MaximumBytes)
            {
                UsedCallPicture("That picture is too large (24 MB at most).", null);
                return;
            }
            bytes = System.IO.File.ReadAllBytes(dialog.FileName);
        }
        catch (Exception error) when (error is System.IO.IOException or UnauthorizedAccessException)
        {
            UsedCallPicture("Couldn't read that picture: " + error.Message, null);
            return;
        }
        UsedCallPicture(discordCalls.UsePicture(bytes, DiscordCameraPictureSource.File), "a picture from a file");
    }

    private async Task UseCreationPictureAsync(Martlet.Core.Creations.Creation creation)
    {
        if (store is null || closing) return;
        try
        {
            var bytes = await CreationStore.Assets(CreationsFolder!, creation).ReadAsync(Martlet.Conversation.PictureCreations.Image, lifetime.Token);
            UsedCallPicture(bytes is null ? "That picture hasn't reached this PC yet." : discordCalls.UsePicture(bytes, DiscordCameraPictureSource.Creation),
                "a picture from Creations");
        }
        catch (OperationCanceledException) { }
        catch (Exception error) when (CreationStore.IsFailure(error)) { UsedCallPicture("Couldn't read that picture: " + error.Message, null); }
    }

    /// <summary>Draws a 16:9 picture from <paramref name="instruction"/> where Companion › Picture making draws, keeps it in Creations and
    /// puts the camera on it. A cloud provider asks first, since a picture costs money.</summary>
    private async Task DrawCallPictureAsync(string instruction)
    {
        if (store is null || closing || callPictureDrawing) return;
        var text = string.Join(' ', instruction.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (text.Length == 0)
        {
            callPictureState = "Say what to draw first, for example \"a cosy library at night, warm lamps\".";
            RenderTab();
            return;
        }
        var dataDirectory = store.DataDirectory;
        if (PictureClient.FirstPaid(dataDirectory) is { } paid && !PictureClient.Fixture &&
            !ConfirmationDialog.Confirm(this, $"Draw the camera background on {paid}, which may cost money?", "Draw a camera background", "Draw", "Cancel"))
            return;
        var thinking = homeSettings.ThinkingRoute();
        var maker = PictureClient.For(dataDirectory, homeSettings?.Profile.Id ?? Guid.Empty, thinking);
        if (maker is null) return;
        callPictureDrawing = true;
        callPictureState = $"Drawing it on {maker.Where}…";
        RenderTab();
        string? problem = null;
        try
        {
            var availability = await maker.GetAvailabilityAsync(lifetime.Token);
            if (!availability.Available) problem = availability.Reason ?? "Picture making isn't available right now.";
            else
            {
                var request = new Martlet.Core.Pictures.PictureRequest { Prompt = text, Shape = Martlet.Core.Pictures.PictureShape.Wide };
                var result = await maker.GenerateAsync(request, null, lifetime.Token);
                PictureClient.FreeLater(maker);
                ErrorLog.Info($"Pictures: camera background on {result.Where}: {result.Width}x{result.Height} {result.MediaType}, " +
                    $"{result.Image.Length / 1024} KiB, {result.Took.TotalSeconds:0.0} s{(result.Fixture ? ", FIXTURE - NOT AI" : "")}.");
                var author = new Martlet.Core.Creations.CreationAuthor { Device = HostSetupCommands.SuggestedDeviceId(), Computer = Martlet.Core.Network.ThisComputer.Name };
                var about = Martlet.Conversation.PictureTools.Label(text);
                try
                {
                    await CreationStore.AddAsync(CreationsFolder ?? dataDirectory, Martlet.Conversation.PictureCreations.Draft(result, "Camera background: " + about, about, request, author),
                        Martlet.Core.Creations.CreationRegistry.Shared, DateTimeOffset.UtcNow, lifetime.Token);
                }
                catch (Exception error) when (CreationStore.IsFailure(error)) { ErrorLog.Info($"Pictures: couldn't keep the camera background ({error.Message})."); }
                problem = discordCalls.UsePicture(result.Image, DiscordCameraPictureSource.Drawn);
            }
        }
        catch (OperationCanceledException) { return; }
        catch (Martlet.Core.Pictures.PictureException error)
        {
            problem = error.Message;
            ErrorLog.Info($"Pictures: camera background on {maker.Where} failed ({error.Code}: {error.Message}).");
        }
        finally
        {
            callPictureDrawing = false;
            (maker as IDisposable)?.Dispose();
        }
        UsedCallPicture(problem, "a picture Martlet drew");
    }

    private void UsedCallPicture(string? problem, string? what)
    {
        callPictureState = problem ?? $"The camera shows {what}" + (discordCalls.CameraOpen ? "." : " when you open the camera view.");
        if (problem is null)
        {
            ErrorLog.Info($"Discord call: the camera background is now {what}.");
            RefreshCallCamera();
        }
        else ActionText.Text = problem;
        if (!closing && openTab == CompanionTab.Discord) RenderTab();
    }

    private void RefreshCallCamera()
    {
        if (discordCalls.CameraOpen) RefreshCallCameraAsync().Forget();
    }

    private async Task RefreshCallCameraAsync()
    {
        try { await avatar.SetCameraAsync(discordCalls.CameraView, lifetime.Token); }
        catch (Exception error) when (error is OperationCanceledException || RendererFailures.Is(error, lifetime.Token))
        {
            if (!closing && RendererFailures.Is(error, lifetime.Token)) RendererFailures.Log("The camera view couldn't be refreshed", error);
        }
    }

    private string? callDoctor;

    private void SaveCall(Func<DiscordCallPreferences, DiscordCallPreferences> update)
    {
        if (!discordCalls.Save(update)) ActionText.Text = "Couldn't save this choice.";
        RenderTab();
    }

    private async Task RefreshCallOutputsAsync()
    {
        await discordCalls.RefreshOutputsAsync(CancellationToken.None);
        if (!closing && openTab == CompanionTab.Discord && !tabEdited) RenderTab();
    }

    private async Task CheckCallAsync()
    {
        await discordCalls.RefreshOutputsAsync(CancellationToken.None);
        var saved = discordCalls.Preferences;
        var outputs = discordCalls.Outputs;
        var result = await Task.Run(() => DiscordCallDoctor.Check(saved, outputs));
        callDoctor = result.Describe();
        ErrorLog.Info("Discord call check: " + callDoctor);
        if (!closing && openTab == CompanionTab.Discord) RenderTab();
    }

    private async Task ToggleCallCameraAsync()
    {
        var open = !discordCalls.CameraOpen;
        try
        {
            if (open && !avatar.IsShowing) await ShowSavedCharacterAsync(onlyIfAutoShow: false);
            var showing = await avatar.SetCameraAsync(open ? discordCalls.CameraView : null, CancellationToken.None);
            discordCalls.CameraOpen = open;
            ActionText.Text = !open ? "The camera view is closed."
                : showing ? "The camera view is open: capture the \"Martlet camera\" window in OBS."
                : "The camera view opens as soon as the character shows (Companion › Character).";
        }
        catch (Exception error) when (error is Martlet.Core.Contracts.ContractException or OperationCanceledException ||
            RendererFailures.Is(error, CancellationToken.None))
        {
            ActionText.Text = "Couldn't change the camera view: " + error.Message;
        }
        if (!closing && openTab == CompanionTab.Discord) RenderTab();
    }

    /// <summary>Frames the character in the open camera view from the Discord card: "in", "out", "left", "right", "up", "down"
    /// or "reset", then saves the framing.</summary>
    private async Task FrameCallCameraAsync(string action)
    {
        try
        {
            var view = await avatar.ZoomAsync(action, CancellationToken.None);
            if (view is not { Camera: true }) throw new InvalidOperationException("The camera view isn't open.");
            SaveCallFraming(view);
            ActionText.Text = $"Camera framing: {discordCalls.Preferences.CameraFraming}.";
        }
        catch (Exception error) when (error is InvalidOperationException or System.IO.IOException or TimeoutException or
            OperationCanceledException or ObjectDisposedException or System.IO.InvalidDataException or System.Text.Json.JsonException)
        {
            ActionText.Text = "Couldn't frame the character: " + error.Message;
        }
        if (!closing && openTab == CompanionTab.Discord && !tabEdited) RenderTab();
    }

    /// <summary>The character was moved or zoomed in the camera window and has settled: saves the framing, so the camera view
    /// opens framed the same way next time.</summary>
    private async Task RememberCallFramingAsync()
    {
        try
        {
            if (await avatar.ZoomAsync("status", lifetime.Token) is not { Camera: true } view || closing) return;
            SaveCallFraming(view);
            if (openTab == CompanionTab.Discord && !tabEdited) RenderTab();
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception error) when (RendererFailures.Is(error, lifetime.Token))
        {
            if (!closing) RendererFailures.Log("The camera view's framing couldn't be read to save it", error);
        }
    }

    private void SaveCallFraming(RendererView view)
    {
        double zoom = view.Zoom, x = view.X ?? 0, y = view.Y ?? 0;
        avatar.RememberCameraFraming(zoom, x, y);
        if (discordCalls.Save(prefs => prefs with { CameraZoom = zoom, CameraX = x, CameraY = y }))
            ErrorLog.Info($"Camera framing saved: {discordCalls.Preferences.CameraFraming}.");
        else ErrorLog.Warn("The camera view's framing couldn't be saved.");
    }

    // ---------- set_camera_background: Martlet changes its own webcam background ----------

    /// <summary>set_camera_background (on the dispatcher): a plain color, or one of Martlet's pictures (a creation's key or ID;
    /// <paramref name="drawn"/> when Martlet just drew it for this) saved as the camera picture, shown at once while the camera
    /// view is open. The words are for the model.</summary>
    private async Task<string> SetCallBackgroundAsync(DiscordCameraBackground? color, string? picture, bool drawn, CancellationToken token)
    {
        string? problem;
        string what, shown;
        if (color is { } chosen)
        {
            problem = discordCalls.Save(prefs => prefs with { CameraBackground = chosen, CameraPicture = null }) ? null : "The background couldn't be saved.";
            what = shown = "plain " + chosen.ToString().ToLowerInvariant();
        }
        else
        {
            if (CreationsFolder is not { } kept || Martlet.Conversation.PictureCreations.Find(kept, picture) is not { Removed: false } creation)
                return "There's no picture with that id. Use list_creations to find one, or draw a new one.";
            byte[]? bytes = null;
            try { bytes = await CreationStore.Assets(kept, creation).ReadAsync(Martlet.Conversation.PictureCreations.Image, token); }
            catch (Exception error) when (CreationStore.IsFailure(error)) { ErrorLog.Info($"Discord call: couldn't read a picture ({error.Message})."); }
            if (bytes is null) return "That picture hasn't reached this computer yet. Tell the user you'll try again in a moment.";
            problem = discordCalls.UsePicture(bytes, drawn ? DiscordCameraPictureSource.Drawn : DiscordCameraPictureSource.Creation);
            what = $"the picture \"{creation.Title ?? creation.Key}\"";
            // The card and the log never show a title.
            shown = drawn ? "a picture Martlet drew" : "a picture from Creations";
        }
        UsedCallPicture(problem, shown);
        if (problem is not null) return $"The background didn't change: {problem} Tell the user briefly.";
        return discordCalls.CameraOpen ? $"Your webcam background is now {what}; everyone in the call sees it."
            : $"Your webcam background is now {what}. The camera view is closed right now, so it shows once the owner opens it.";
    }

    /// <summary>set_camera_background's way to the main window.</summary>
    private sealed class CallCameraBridge(MainWindow window) : ICallCamera
    {
        public bool Offered => window.discordCalls.Preferences.On;

        public Task<string> SetBackgroundAsync(DiscordCameraBackground? color, string? picture, bool drawn, CancellationToken token) =>
            window.Dispatcher.InvokeAsync(() => window.SetCallBackgroundAsync(color, picture, drawn, token)).Task.Unwrap();
    }
}
