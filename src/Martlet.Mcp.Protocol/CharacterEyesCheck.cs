using System.Globalization;
using System.IO;
using System.Text.Json;
using Martlet.Avatar.Hosting;
using Martlet.Avatars;
using Martlet.Mcp.Shared;
using Martlet.Providers;

namespace Martlet.Mcp;

/// <summary>character_eyes: Companion › Eyes › Where the face is as Martlet.Avatar.Hosting's CharacterEyes measures the eyes,
/// cheeks and mouth,
/// with NO vision request: the request the vision model gets, what the production parser, checks and conversion make of a
/// simulated vision answer about the close-up (and of a second answer when the first fails, as the measurement asks once
/// more), and the eye hint the renderer gets. With snapshotPath and face, the production close-up is composed from a real
/// snapshot (and, with previewDirectory, kept as files to look at), with a FIXTURE - NOT AI stand-in answering. With save (an
/// explicit, disposable data directory only) the measurement is saved as the desktop saves one; forget removes it. Contacts
/// nothing; never returns the model's path.</summary>
internal static class CharacterEyesCheck
{
    internal static async Task<object> RunAsync(string dataDirectory, bool explicitDirectory, string? modelPath, string? modelId, string? answer,
        string? second, string? snapshotPath, string? face, string? previewDirectory, bool save, bool forget, string? eyesFrom,
        CancellationToken cancellation)
    {
        var (id, renderer, problem) = await ModelAsync(dataDirectory, modelPath, modelId, cancellation);
        EyeCloseUp closeUp;
        if (snapshotPath is not null)
        {
            var snapshot = TouchZoneImages.Decode(await File.ReadAllBytesAsync(snapshotPath, cancellation));
            var given = Face(face ?? throw new ArgumentException("snapshotPath needs face: \"x,y,width[,rollDegrees]\", fractions of the snapshot."),
                snapshot.Width, snapshot.Height);
            closeUp = CharacterEyes.CloseUp(snapshot, given) ?? throw new ArgumentException("The face is too small or outside the snapshot.");
        }
        else closeUp = EyeCloseUp.Standard();
        if (previewDirectory is not null) Directory.CreateDirectory(previewDirectory);

        object? measurement = null;
        EyeMeasurementResult? result = null;
        var pictures = new List<(EyeSentPicture Sent, byte[] Bytes)>();
        if (answer is not null)
        {
            var asked = new List<object>();
            var answers = new Queue<string?>([answer, second]);
            result = await CharacterEyes.RunAsync(closeUp, async (ask, token) =>
            {
                string? file = null, picture = null;
                if (ask.Picture is { } pixels)
                {
                    var image = TouchZoneImages.Encode(pixels);
                    file = $"{pictures.Count + 1:00}-{ask.Step}.{(image.MediaType == ImageMediaType.Png ? "png" : "jpg")}";
                    var bytes = image.Content.ToArray();
                    pictures.Add((new(file, ask.Step, image.Width, image.Height, image.ByteCount, image.MimeType), bytes));
                    picture = $"{image.Width}x{image.Height} {image.MimeType}, {image.ByteCount / 1024} KB";
                    if (previewDirectory is not null) await File.WriteAllBytesAsync(Path.Combine(previewDirectory, file), bytes, token);
                }
                var reply = answers.Count > 0 ? answers.Dequeue() : null;
                asked.Add(new { ask.Step, kind = ask.Kind.ToString(), picture, file, marks = ask.Marks.Count, text = ask.Text, answer = reply });
                return (reply, reply is null ? "no simulated answer for this request (give second)" : null);
            }, null, cancellation);
            measurement = new
            {
                fixture = "FIXTURE - NOT AI: simulated answers stood in for the vision model; no vision request was made",
                requestCount = result.Requests, result.Steps, asked,
                boxes = result.Boxes is { } boxes ? new
                {
                    leftIris = Box(boxes.LeftIris), leftEye = Box(boxes.LeftEye), rightIris = Box(boxes.RightIris), rightEye = Box(boxes.RightEye),
                    leftCheek = boxes.LeftCheek is { } leftCheek ? Box(leftCheek) : null, rightCheek = boxes.RightCheek is { } rightCheek ? Box(rightCheek) : null,
                    mouth = boxes.Mouth is { } mouth ? Box(mouth) : null
                } : null,
                result.Problems, hint = Hint(result.Hint), result.Failure
            };
        }

        string? wrote = null;
        if (forget)
        {
            if (!explicitDirectory) throw new ArgumentException("forget needs an explicit (disposable) dataDirectory.");
            wrote = await CharacterEyes.ForgetAsync(dataDirectory, Need(id, problem), cancellation) ? "Forgot the model's measurement and its pictures." : "No measurement was saved for the model.";
        }
        if (save)
        {
            if (!explicitDirectory) throw new ArgumentException("save needs an explicit (disposable) dataDirectory.");
            if (result?.Hint is not { } hint) throw new ArgumentException("save needs an answer that passes Martlet's checks.");
            var model = Need(id, problem);
            var folder = CharacterEyes.ClearPictures(dataDirectory, model);
            foreach (var (sent, bytes) in pictures) await File.WriteAllBytesAsync(Path.Combine(folder, sent.File), bytes, cancellation);
            if (closeUp.Picture(CharacterEyes.Marks(result.Boxes!)) is { } marked)
                await File.WriteAllBytesAsync(Path.Combine(folder, CharacterEyes.BoxesFile), TouchZoneImages.Encode(marked).Content.ToArray(), cancellation);
            await CharacterEyes.SaveAsync(dataDirectory, CharacterEyeMeasurement.From(model, hint) with
            {
                By = CharacterEyeMeasurement.ByFixture, MeasuredAt = DateTimeOffset.Now,
                Requests = result.Requests, Boxes = result.Boxes, Pictures = [.. pictures.Select(p => p.Sent)], Steps = result.Steps
            }, cancellation);
            wrote = $"Saved the measurement for the model in {CharacterEyes.FileName}" + (pictures.Count > 0 ? " with the pictures sent and the picture of its boxes." : ".");
        }

        var saved = id is null ? null : CharacterEyes.Load(dataDirectory, id);
        var (width, height) = closeUp.Size;
        return new
        {
            model = new { renderer, modelId = id, problem },
            request = new
            {
                instructions = CharacterEyes.Instructions, text = CharacterEyes.Text,
                check = CharacterEyes.CheckText(["The left iris (box 1) lies mostly outside the left eye's opening (box 2)."]),
                again = CharacterEyes.AgainText("it gave no box for the right iris"),
                edge = CharacterEyes.Edge, faceWidths = CharacterEyes.FaceWidths, maximumZoom = CharacterEyes.MaximumZoom
            },
            closeUp = new
            {
                width, height, fromSnapshot = closeUp.Snapshot is not null,
                area = new { closeUp.Area.X, closeUp.Area.Y, closeUp.Area.Width, closeUp.Area.Height },
                face = new { x = Math.Round(closeUp.Face.X, 2), y = Math.Round(closeUp.Face.Y, 2), width = Math.Round(closeUp.Face.Width, 2),
                    rollDegrees = Math.Round(closeUp.Face.Angle * 180 / Math.PI, 2) }
            },
            measurement,
            status = eyesFrom is null && saved is null ? null : CharacterEyes.Status(eyesFrom, saved, eyesFrom is not null, DateTimeOffset.Now),
            wrote,
            saved = saved is null ? null : new
            {
                saved.By, saved.MeasuredAt, saved.Requests, hint = Hint(saved.Hint), pictures = saved.Pictures.Select(p => p.File).ToArray(), saved.Steps,
                boxesPicture = File.Exists(Path.Combine(CharacterEyes.PictureFolder(dataDirectory, saved.ModelId), CharacterEyes.BoxesFile)),
                line = CharacterEyes.Measured(saved, DateTimeOffset.Now)
            },
            checks = new
            {
                eyeWidth = new[] { CharacterEyes.MinimumEyeWidth, CharacterEyes.MaximumEyeWidth }, eyeHeight = new[] { CharacterEyes.MinimumEyeHeight, CharacterEyes.MaximumEyeHeight },
                iris = new[] { CharacterEyes.MinimumIris, CharacterEyes.MaximumIris }, mostlyInside = CharacterEyes.MostlyInside, open = CharacterEyes.MinimumOpen,
                apart = CharacterEyes.MinimumApart, tilt = CharacterEyes.MaximumTilt,
                cheekWidth = new[] { CharacterEyes.MinimumCheek, CharacterEyes.MaximumCheek }, cheekDrop = CharacterEyes.MaximumCheekDrop,
                mouthWidth = new[] { CharacterEyes.MinimumMouth, CharacterEyes.MaximumMouth }
            }
        };
    }

    private static string Need(string? id, string? problem) =>
        id ?? throw new ArgumentException(problem ?? "Give modelPath (a .model3.json or .vrm), modelId, or a dataDirectory whose avatar.json shows a model.");

    // The model to save for or read: modelId, else the one modelPath (or the data directory's avatar.json) names.
    private static async Task<(string? Id, string Renderer, string? Problem)> ModelAsync(string dataDirectory, string? modelPath, string? modelId,
        CancellationToken cancellation)
    {
        var renderer = AvatarRenderer.Live2D;
        if (modelPath is null)
        {
            try
            {
                using var avatar = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(dataDirectory, "avatar.json")));
                modelPath = avatar.RootElement.TryGetProperty("model_path", out var value) ? value.GetString() : null;
                if (avatar.RootElement.TryGetProperty("renderer", out var kind) && kind.ToString().Contains("vrm", StringComparison.OrdinalIgnoreCase))
                    renderer = AvatarRenderer.Vrm;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException) { }
        }
        if (modelId is not null || modelPath is null) return (modelId, renderer.ToString(), null);
        if (modelPath.EndsWith(".vrm", StringComparison.OrdinalIgnoreCase)) renderer = AvatarRenderer.Vrm;
        try { return ((await CharacterActionInventory.ReadAsync(renderer, modelPath, cancellation)).ModelId, renderer.ToString(), null); }
        catch (Exception error) when (error is Martlet.Core.Contracts.ContractException or IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return (null, renderer.ToString(), "The model can't be read: " + error.Message);
        }
    }

    // "x,y,width[,rollDegrees]": the face's middle and width as fractions of the snapshot (the width of its width), its roll in
    // degrees (clockwise).
    private static EyeFace Face(string text, int width, int height)
    {
        var values = text.Split([',', ' '], StringSplitOptions.RemoveEmptyEntries).Select(t => double.Parse(t, CultureInfo.InvariantCulture)).ToArray();
        if (values.Length is not (3 or 4)) throw new ArgumentException("face is \"x,y,width[,rollDegrees]\", fractions of the snapshot.");
        return new(values[0] * width, values[1] * height, values[2] * width, values.Length == 4 ? values[3] * Math.PI / 180 : 0);
    }

    // The eye hint as the renderer page gets it (the contract's camelCase names).
    private static JsonElement? Hint(RendererEyes? hint) => hint is null ? null : JsonSerializer.SerializeToElement(hint, RendererProtocol.Json);

    private static double[] Box(TouchZoneBox box) => [Math.Round(box.X, 4), Math.Round(box.Y, 4), Math.Round(box.Width, 4), Math.Round(box.Height, 4)];
}
