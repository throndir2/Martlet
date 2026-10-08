using System.IO;
using Martlet.Avatar.Hosting;
using Martlet.Providers;

namespace Martlet.Desktop;

/// <summary>The eyes of the character this PC shows (Companion › Eyes › Where the eyes are): measured once per model with
/// the Thinking model's vision in a close-up of the face (<see cref="CharacterEyes"/>), saved per model in character-eyes.json
/// with the pictures sent, and given to the renderer as its eye hint, so that drawings over the eyes fit the iris on models
/// whose own data doesn't say where the eyes are.</summary>
internal sealed class CharacterEyeService(string? dataDirectory)
{
    /// <summary>A file whose text (JSON as a vision model answers about the close-up) stands in for the vision model: every
    /// request of a measurement is answered with it (FIXTURE - NOT AI), so MCP verification runs the real snapshot, close-up,
    /// checks, conversion, saving and eye hint without a vision request.</summary>
    internal const string FixtureVariable = "MARTLET_EYES_FIXTURE";

    private readonly HashSet<string> automatic = new(StringComparer.Ordinal);
    private CharacterEyeMeasurement? current;
    private string? modelId;
    private string? progress;
    private bool busy;

    /// <summary>Raised (on any thread) when the measurement or how measuring goes changes.</summary>
    internal event Action? Changed;

    internal string? ModelId => Volatile.Read(ref modelId);
    /// <summary>The loaded model's measurement, or null before one was saved.</summary>
    internal CharacterEyeMeasurement? Current => Volatile.Read(ref current);
    /// <summary>How measuring went (or is going), or null before it was asked.</summary>
    internal string? Progress => Volatile.Read(ref progress);
    internal bool Busy => Volatile.Read(ref busy);

    /// <summary>Whether a stand-in answers instead of the vision model (FIXTURE - NOT AI).</summary>
    internal static bool Fixture => Environment.GetEnvironmentVariable(FixtureVariable) is { Length: > 0 };

    /// <summary>The close-up of the loaded model's measurement with the answer's boxes drawn and numbered, or null.</summary>
    internal string? BoxesPicture => dataDirectory is not null && ModelId is { } id && Current is not null &&
        Path.Combine(CharacterEyes.PictureFolder(dataDirectory, id), CharacterEyes.BoxesFile) is var path && File.Exists(path) ? path : null;

    /// <summary>The eye hint for the model <paramref name="id"/> when it is the loaded one (no file is read), or null.</summary>
    internal RendererEyes? HintFor(string? id) => id is not null && id == ModelId ? Current?.Hint : null;

    /// <summary>Loads the measurement of the model with <paramref name="id"/> unless it is already loaded.</summary>
    internal void Follow(string? id)
    {
        if (id == ModelId) return;
        Volatile.Write(ref current, id is null || dataDirectory is null ? null : CharacterEyes.Load(dataDirectory, id));
        Volatile.Write(ref modelId, id);
        Volatile.Write(ref progress, null);
        Changed?.Invoke();
    }

    /// <summary>Whether the loaded model should be measured on its own: nothing saved for it and not tried since Martlet
    /// started. Marks it tried.</summary>
    internal bool ClaimAutomatic()
    {
        lock (automatic) return ModelId is { } id && Current is null && !Busy && automatic.Add(id);
    }

    /// <summary>Takes a picture of <paramref name="profile"/>'s character (drawn off screen in its rest pose, with the face
    /// anchor: <see cref="AvatarController.ZoneSnapshotAsync"/>), asks the Thinking model (through <paramref name="ask"/>) where
    /// its eyes are in a close-up of the face, checks and converts the answer, and saves the eye hint with the pictures sent.
    /// Returns what happened.</summary>
    internal async Task<string> MeasureAsync(AvatarController avatar, AvatarProfile profile,
        Func<string, string, string, BoundedImage?, CancellationToken, Task<(string? Answer, string? Failure)>> ask, bool automatically,
        CancellationToken token)
    {
        if (ModelId is not { } id) return Report("Martlet is still reading the character. Try again in a moment.");
        if (dataDirectory is null) return Report("Martlet's data folder isn't available, so the measurement can't be saved.");
        Volatile.Write(ref busy, true);
        Report(automatically ? "The eyes are only estimated, so Martlet measures them: taking a picture of the character..." : "Taking a picture of the character...");
        var fixture = Environment.GetEnvironmentVariable(FixtureVariable) is { Length: > 0 } file ? file : null;
        var tag = fixture is null ? "" : "FIXTURE - NOT AI: ";
        try
        {
            if (await avatar.ZoneSnapshotAsync(profile, token) is not { } shot) return Stop("Martlet couldn't take a picture of the character. Try again.");
            ZonePixels snapshot;
            try { snapshot = await Task.Run(() => TouchZoneImages.Decode(shot.Png), token); }
            catch (Exception error) when (error is NotSupportedException or FileFormatException or ArgumentException or InvalidOperationException or IOException)
            {
                return Stop("The character's picture couldn't be used. Try again.");
            }
            var crop = new TouchZoneBox(shot.Picture.CropLeft, shot.Picture.CropTop, shot.Picture.CropWidth, shot.Picture.CropHeight);
            if (EyeFace.InSnapshot(shot.Probe?.Face, crop, snapshot.Width, snapshot.Height) is not { } face)
            {
                ErrorLog.Info($"Measuring the eyes: the picture's probe has {(shot.Probe?.Face is { } seen ? $"a face that can't be used ({(seen.IsValid ? "outside the picture" : "not valid")})" : "no face")} " +
                    $"({shot.Probe?.Drawables?.Length ?? 0} drawables, {shot.Probe?.Bones?.Length ?? 0} bones).");
                return Stop("Martlet couldn't find the character's face in its picture, so it can't measure the eyes.");
            }
            if (CharacterEyes.CloseUp(snapshot, face) is not { } closeUp)
                return Stop("The character's face is too small (or outside) its picture, so Martlet can't measure the eyes.");
            var answerText = fixture is null ? null : File.Exists(fixture) ? await File.ReadAllTextAsync(fixture, token) : null;
            var folder = CharacterEyes.ClearPictures(dataDirectory, id);
            var pictures = new List<EyeSentPicture>();
            ErrorLog.Info($"Measuring the eyes: a {snapshot.Width}x{snapshot.Height} picture of the character in its rest pose, drawn off screen, " +
                FormattableString.Invariant($"with its face {face.Width:0} pixels wide ({shot.Probe?.Face?.Tracking ?? "found"}); a close-up of {closeUp.Area.Width} pixels square around it."));

            async Task<(string? Answer, string? Failure)> AskAsync(EyeAsk eyeAsk, CancellationToken cancel)
            {
                if (eyeAsk.Picture is not { } picture) return (null, "no picture");
                var image = TouchZoneImages.Encode(picture);
                var name = $"{pictures.Count + 1:00}-{eyeAsk.Step}.{(image.MediaType == ImageMediaType.Png ? "png" : "jpg")}";
                await File.WriteAllBytesAsync(Path.Combine(folder, name), image.Content.ToArray(), cancel);
                pictures.Add(new(name, eyeAsk.Step, image.Width, image.Height, image.ByteCount, image.MimeType));
                ErrorLog.Info($"Measuring the eyes ({eyeAsk.Step}): {(fixture is null ? "sending" : "FIXTURE - NOT AI, not sending")} a {image.Width}x{image.Height} " +
                    $"{image.MimeType} picture ({image.ByteCount / 1024} KB) of the face with a grid{(eyeAsk.Marks.Count > 0 ? $" and {eyeAsk.Marks.Count} numbered boxes" : "")}.");
                if (fixture is not null) return (answerText, answerText is null ? "the fixture file is missing" : null);
                return await ask("Measuring the eyes", eyeAsk.Instructions, eyeAsk.Text, image, cancel);
            }

            var result = await CharacterEyes.RunAsync(closeUp, AskAsync, text => Report(tag + text), token);
            foreach (var step in result.Steps) ErrorLog.Info($"Measuring the eyes: {step}.");
            if (result.Boxes is { } boxes)
            {
                // The close-up with the last answer's boxes, for the owner to see what was measured.
                try { await File.WriteAllBytesAsync(Path.Combine(folder, CharacterEyes.BoxesFile), TouchZoneImages.Encode(closeUp.Picture(CharacterEyes.Marks(boxes))!).Content.ToArray(), token); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException)
                { ErrorLog.Warn($"Couldn't keep the picture of the eyes' boxes: {error.Message}"); }
            }
            if (result.Hint is not { } hint)
            {
                ErrorLog.Info($"Measuring the eyes didn't save a measurement: {result.Failure}");
                return Report(tag + (result.Failure ?? "The eyes couldn't be measured.") + (fixture is null ? " Try again, or choose a model that can see (Companion › Vision)." : ""));
            }
            var measurement = new CharacterEyeMeasurement
            {
                ModelId = id, Left = hint.Left!, Right = hint.Right!, By = fixture is null ? CharacterEyeMeasurement.ByVision : CharacterEyeMeasurement.ByFixture,
                MeasuredAt = DateTimeOffset.Now, Requests = result.Requests, Boxes = result.Boxes, Pictures = [.. pictures], Steps = result.Steps
            };
            try { await CharacterEyes.SaveAsync(dataDirectory, measurement, token); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or Martlet.Core.Contracts.ContractException)
            {
                return Stop(tag + "The eyes were measured but couldn't be saved: " + error.Message);
            }
            if (ModelId == id) Volatile.Write(ref current, measurement);
            ErrorLog.Info((fixture is null ? "The Thinking model" : "FIXTURE - NOT AI: a stand-in") + FormattableString.Invariant(
                $" measured the eyes in {result.Requests} request{(result.Requests == 1 ? "" : "s")}: iris radius {hint.Left!.Iris.R:0.###} and {hint.Right!.Iris.R:0.###} face widths, ") +
                FormattableString.Invariant($"the irises {hint.Right.Iris.X - hint.Left.Iris.X:0.###} face widths apart."));
            return Report(CharacterEyes.Measured(measurement, DateTimeOffset.Now) + $" in {result.Requests} request{(result.Requests == 1 ? "" : "s")}.");
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { return Stop("Measuring the eyes was stopped."); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return Stop($"Martlet couldn't keep the pictures for measuring the eyes: {error.Message}");
        }
        finally
        {
            Volatile.Write(ref busy, false);
            Changed?.Invoke();
        }
    }

    /// <summary>Forgets the loaded model's measurement and its pictures; returns what happened.</summary>
    internal async Task<string> ForgetAsync(CancellationToken token)
    {
        if (ModelId is not { } id || dataDirectory is null) return Report("No character is loaded.");
        try
        {
            var forgot = await CharacterEyes.ForgetAsync(dataDirectory, id, token);
            if (ModelId == id) Volatile.Write(ref current, null);
            // A forgotten measurement isn't taken again on its own until Martlet starts again; Measure the eyes takes it.
            lock (automatic) automatic.Add(id);
            return Report(forgot ? "Forgot the measurement. The eyes use the model's own data or an estimate." : "No measurement was saved for this model.");
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or Martlet.Core.Contracts.ContractException)
        {
            return Report("The measurement couldn't be forgotten: " + error.Message);
        }
    }

    /// <summary>Sets how measuring goes (<see cref="Progress"/>) and returns it.</summary>
    internal string Report(string text)
    {
        Volatile.Write(ref progress, text);
        Changed?.Invoke();
        return text;
    }

    // A measurement that ends without a result: the desktop log says why too.
    private string Stop(string text)
    {
        ErrorLog.Info($"Measuring the eyes stopped: {text}");
        return Report(text);
    }
}
