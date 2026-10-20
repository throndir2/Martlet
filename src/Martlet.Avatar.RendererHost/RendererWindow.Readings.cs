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

/// <summary>What the page reads on request: the face, the picture, the pose, animations, the touch zones view and the mouth.</summary>
internal sealed partial class RendererWindow
{
    /// <summary>Asks the page where Martlet draws over the face now (Martlet's MCP character_face, through UI Automation); its
    /// answer arrives unprompted (see <see cref="FaceAnswered"/>). Reading it changes nothing on the character.</summary>
    private void ReadFace()
    {
        if (browser.CoreWebView2 is null || failure.Failed || closed) return;
        var id = ++faceId;
        facePending = id;
        try
        {
            browser.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(new { kind = "face", data = new { id } }, RendererProtocol.Json));
        }
        catch (Exception error) when (error is InvalidOperationException or System.Runtime.InteropServices.COMException) { }
    }

    /// <summary>The page's reading of the face, remembered for UI Automation; a stale or malformed one is ignored and never
    /// fails the renderer.</summary>
    private void FaceAnswered(JsonElement answer)
    {
        if (facePending is not { } pending || answer.ValueKind != JsonValueKind.Object || !answer.TryGetProperty("id", out var id) ||
            id.ValueKind != JsonValueKind.Number || !id.TryGetInt32(out var number) || number != pending) return;
        facePending = null;
        viewport.LastFace = CharacterFaceReading.From(answer, number);
    }

    private int pictureId;

    /// <summary>Takes a picture of the character as it shows now for Martlet's MCP character_picture, through UI Automation:
    /// WebView2's capture of the page, so Martlet's drawings over the face are in it, cropped to the character. The PNG goes
    /// to this renderer's file in the temp folder (replaced each time) and its reading to the viewport's LastPicture. A picture
    /// that can't be taken is only reported; it changes nothing on the character and never fails the renderer.</summary>
    private async void TakePicture()
    {
        if (browser.CoreWebView2 is null || failure.Failed || closed) return;
        var id = ++pictureId;
        try
        {
            var picture = await SnapshotAsync(new RendererSnapshot(Portrait: false, Edge: RendererSnapshot.MaximumEdge));
            var folder = Path.Combine(Path.GetTempPath(), "Martlet.CharacterPictures");
            Directory.CreateDirectory(folder);
            var file = Path.Combine(folder, $"overlay-{Environment.ProcessId}.png");
            await File.WriteAllBytesAsync(file, Convert.FromBase64String(picture.Png));
            viewport.LastPicture = JsonSerializer.Serialize(new
            {
                n = id, path = file, width = picture.Width, height = picture.Height, left = Math.Round(picture.CropLeft, 4),
                top = Math.Round(picture.CropTop, 4), cropWidth = Math.Round(picture.CropWidth, 4), cropHeight = Math.Round(picture.CropHeight, 4)
            }, RendererProtocol.Json);
        }
        catch (Exception error) when (error is IOException or NotSupportedException or ArgumentException or InvalidDataException or
            FileFormatException or InvalidOperationException or UnauthorizedAccessException or FormatException or
            System.Runtime.InteropServices.COMException or TimeoutException or OperationCanceledException)
        {
            ErrorLog.Warn($"Couldn't take a picture of the character for Martlet's MCP: {error.Message}");
            viewport.LastPicture = JsonSerializer.Serialize(new { n = id, error = "The picture couldn't be taken." }, RendererProtocol.Json);
        }
    }

    private int poseId;
    private int? posePending;

    /// <summary>Asks the page what the character's idle body does now: a VRM's breath, arms, fingers and sway (Martlet's MCP
    /// character_pose, through UI Automation). Its answer arrives unprompted (see <see cref="PoseAnswered"/>). Reading it changes
    /// nothing on the character.</summary>
    private void ReadPose()
    {
        if (browser.CoreWebView2 is null || failure.Failed || closed) return;
        var id = ++poseId;
        posePending = id;
        try
        {
            browser.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(new { kind = "pose", data = new { id } }, RendererProtocol.Json));
        }
        catch (Exception error) when (error is InvalidOperationException or System.Runtime.InteropServices.COMException) { }
    }

    /// <summary>The page's reading of the idle body, remembered for UI Automation; a stale or malformed one is ignored and never
    /// fails the renderer.</summary>
    private void PoseAnswered(JsonElement answer)
    {
        if (posePending is not { } pending || answer.ValueKind != JsonValueKind.Object || !answer.TryGetProperty("id", out var id) ||
            id.ValueKind != JsonValueKind.Number || !id.TryGetInt32(out var number) || number != pending) return;
        posePending = null;
        viewport.LastPose = CharacterPoseReading.From(answer, number);
    }

    private int mouthId;
    private int? mouthPending;

    private int zonesId;
    private int? zonesPending;

    private int animationId;
    private (int Id, string File)? animationPending;

    /// <summary>Gives the page a VRMA clip from Martlet (<see cref="RendererAnimation"/>) to keep and play, and says what it did.
    /// A name, base64 or clip that isn't valid is only "not started" with the reason; it never stops the character.</summary>
    private async Task<RendererAnimationResult> AnimationAsync(RendererAnimation request)
    {
        var name = request.Name ?? "";
        RendererAnimationResult Refused(string reason) => new(false, false, name, 0, [], reason);
        if (!VrmAnimationClip.IsValidName(name))
            return Refused($"A clip name is 1 to {VrmAnimationClip.MaximumNameLength} characters without control characters.");
        if (request.Clip is { } clip)
        {
            if (clip.Length > (VrmAnimationClip.MaximumBytes + 2) / 3 * 4) return Refused("The clip is too large.");
            try { VrmAnimationClip.Inspect(Convert.FromBase64String(clip)); }
            catch (FormatException) { return Refused("The clip isn't valid base64."); }
            catch (InvalidDataException error) { return Refused(error.Message); }
        }
        var answer = await BrowserAsync("animation", new { name, clip = request.Clip, play = request.Play, inPlace = request.InPlace });
        var result = CharacterAnimationReading.From(answer, name);
        ErrorLog.Info($"VRMA clip '{name}': {(result.Started ? "started" : result.Kept ? "kept" : "not kept")}" +
            (result.Reason is { } reason ? $" ({reason})" : "") + ".");
        return result;
    }

    /// <summary>Gives the page a VRMA clip from a file on this PC (Martlet's MCP character_animation, through UI Automation), or
    /// plays the clip it keeps by <paramref name="name"/> without a file. The answer arrives unprompted (see
    /// <see cref="AnimationAnswered"/>); a file that can't be read or isn't a usable clip is only "not started".</summary>
    private void PlayAnimationFile(string? name, string? path, bool play, bool inPlace)
    {
        var id = ++animationId;
        var file = path is null ? null : Path.GetFileName(path);
        name ??= VrmAnimationClip.NameOf(path!);
        void Refuse(string reason) => viewport.LastAnimation = CharacterAnimationReading.Json(new(false, false, name, 0, [], reason), id, file);
        if (browser.CoreWebView2 is null || failure.Failed || closed) { Refuse("The character isn't showing."); return; }
        if (!VrmAnimationClip.IsValidName(name)) { Refuse("The clip's name isn't valid."); return; }
        string? clip = null;
        if (path is not null)
        {
            try
            {
                var info = new FileInfo(path);
                if (!info.Exists) { Refuse("The file doesn't exist."); return; }
                if (info.Length > VrmAnimationClip.MaximumBytes) { Refuse("The file is too large for a VRMA clip."); return; }
                var bytes = File.ReadAllBytes(path);
                VrmAnimationClip.Inspect(bytes);
                clip = Convert.ToBase64String(bytes);
            }
            catch (InvalidDataException error) { Refuse(error.Message); return; }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            { Refuse("The file can't be read."); return; }
        }
        animationPending = (id, file ?? "");
        try
        {
            browser.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(new { kind = "animation", data = new { id, name, clip, play, inPlace } },
                RendererProtocol.Json));
        }
        catch (Exception error) when (error is InvalidOperationException or System.Runtime.InteropServices.COMException)
        { animationPending = null; Refuse("The character's page couldn't take the clip."); }
    }

    /// <summary>The page's answer about a clip from a file, remembered for UI Automation; a stale or malformed one is ignored and
    /// never fails the renderer.</summary>
    private void AnimationAnswered(JsonElement answer)
    {
        if (animationPending is not { } pending || answer.ValueKind != JsonValueKind.Object || !answer.TryGetProperty("id", out var id) ||
            id.ValueKind != JsonValueKind.Number || !id.TryGetInt32(out var number) || number != pending.Id) return;
        animationPending = null;
        var result = CharacterAnimationReading.From(answer, "");
        viewport.LastAnimation = CharacterAnimationReading.Json(result, number, pending.File.Length > 0 ? pending.File : null);
        ErrorLog.Info($"VRMA clip '{result.Name}' from Martlet's MCP: {(result.Started ? "started" : result.Kept ? "kept" : "not kept")}" +
            (result.Reason is { } reason ? $" ({reason})" : "") + ".");
    }

    /// <summary>Asks the page where each area of the character's touch zones is now, as Martlet last gave them ("zoneview";
    /// Martlet's MCP character_zones, through UI Automation). Its answer arrives unprompted (see <see cref="ZonesAnswered"/>).
    /// Reading it changes nothing on the character.</summary>
    private void ReadZones()
    {
        if (browser.CoreWebView2 is null || failure.Failed || closed) return;
        var id = ++zonesId;
        zonesPending = id;
        try
        {
            browser.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(new { kind = "zonesRead", data = new { id } }, RendererProtocol.Json));
        }
        catch (Exception error) when (error is InvalidOperationException or System.Runtime.InteropServices.COMException) { }
    }

    /// <summary>The page's reading of the touch zones, remembered for UI Automation; a stale or malformed one is ignored and never
    /// fails the renderer.</summary>
    private void ZonesAnswered(JsonElement answer)
    {
        if (zonesPending is not { } pending || answer.ValueKind != JsonValueKind.Object || !answer.TryGetProperty("id", out var id) ||
            id.ValueKind != JsonValueKind.Number || !id.TryGetInt32(out var number) || number != pending) return;
        zonesPending = null;
        // Only what the page measured, bounded: never more than the areas Martlet gave it.
        var text = answer.GetRawText();
        viewport.LastZones = text.Length <= 256 * 1024 ? text : JsonSerializer.Serialize(new { n = number, error = "The reading was too large." }, RendererProtocol.Json);
    }

    /// <summary>Gives the page the character's touch zones (<see cref="RendererZoneView"/>; one that isn't valid clears them) and
    /// whether to draw them over the character. The page takes them beside the touch and face readings, so zones it can't use
    /// never fail the character.</summary>
    private async Task<object> ZoneViewAsync(RendererMessage message)
    {
        var view = message.Data.ValueKind == JsonValueKind.Object ? RendererProtocol.Data<RendererZoneView>(message) : new RendererZoneView(false, []);
        if (!view.IsValid)
        {
            ErrorLog.Warn("Martlet sent touch zones that aren't valid; the character shows none.");
            view = new(false, []);
        }
        await BrowserAsync("zoneview", view);
        return new { areas = view.Areas.Length, draw = view.Draw };
    }

    /// <summary>Asks the page who moves the character's mouth now: the voice or its emotes (Martlet's MCP character_mouth,
    /// through UI Automation). Its answer arrives unprompted (see <see cref="MouthAnswered"/>). Reading it changes nothing on
    /// the character.</summary>
    private void ReadMouth()
    {
        if (browser.CoreWebView2 is null || failure.Failed || closed) return;
        var id = ++mouthId;
        mouthPending = id;
        try
        {
            browser.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(new { kind = "mouthState", data = new { id } }, RendererProtocol.Json));
        }
        catch (Exception error) when (error is InvalidOperationException or System.Runtime.InteropServices.COMException) { }
    }

    /// <summary>The page's reading of the mouth, remembered for UI Automation; a stale or malformed one is ignored and never
    /// fails the renderer.</summary>
    private void MouthAnswered(JsonElement answer)
    {
        if (mouthPending is not { } pending || answer.ValueKind != JsonValueKind.Object || !answer.TryGetProperty("id", out var id) ||
            id.ValueKind != JsonValueKind.Number || !id.TryGetInt32(out var number) || number != pending) return;
        mouthPending = null;
        viewport.LastMouth = CharacterMouthReading.From(answer, number);
    }

    /// <summary>Moves the character's mouth with loudness levels (0 to 1) the way Martlet's voice does, one every
    /// <paramref name="stepMs"/> milliseconds, without a sound (Martlet's MCP character_mouth). The page plays them on its own
    /// frame clock and never answers, so the request pipe's replies stay in step.</summary>
    private void PlayVoice(IReadOnlyList<double> levels, int stepMs)
    {
        if (browser.CoreWebView2 is null || failure.Failed || closed) return;
        try
        {
            browser.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(new { kind = "voiceLevels", data = new { levels, stepMs } },
                RendererProtocol.Json));
        }
        catch (Exception error) when (error is InvalidOperationException or System.Runtime.InteropServices.COMException) { }
    }
}
