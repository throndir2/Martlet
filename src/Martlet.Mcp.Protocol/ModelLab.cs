using System.Globalization;
using System.IO;
using System.Text.Json;
using Martlet.Avatar.Hosting;
using Martlet.Mcp.Shared;
using Martlet.Providers;

namespace Martlet.Mcp;

/// <summary>model_lab: a live OpenAI-compatible fixture endpoint on 127.0.0.1 for the desktop on a disposable data directory, so
/// Companion › Vision › Image model, Companion › Listening › Audio model and Thinking can choose and test models without a real
/// provider (NOT AI). <c>/v1/models</c> lists <c>lab/sees</c> (text and pictures), <c>lab/hears</c> (text and recordings),
/// <c>lab/omni</c> (all three) and <c>lab/text</c> (text only) with their input modalities, as OpenRouter lists them. Chat
/// completions refuse a picture or a recording that a lab model doesn't take (error 400, as a real server does), read Test
/// vision's word by comparing the picture with this PC's own drawings of the test words, read Test hearing's word with Windows
/// speech recognition limited to the test words (when this PC has an English recognizer), and answer anything else with fixed
/// text, streamed when asked. Its status counts the requests by model and the kinds of parts they carried, never their content.
/// It ends with this server.</summary>
internal static class ModelLab
{
    private sealed record LabModel(string Id, bool Sees, bool Hears);

    private static readonly LabModel[] Models =
        [new("lab/sees", true, false), new("lab/hears", false, true), new("lab/omni", true, true), new("lab/text", false, false)];

    private static readonly object Gate = new();
    private static readonly List<object> Seen = [];
    private static ModelAbilityCheck.Fixture? server;
    private static IReadOnlyDictionary<string, ZonePixels>? drawings;

    internal static async Task<object> RunAsync(string action, int? port, CancellationToken cancellation)
    {
        switch (action)
        {
            case "start":
                if (port is not (null or (>= 1024 and <= 65535))) throw new ArgumentException("port is 1024-65535 (or left out for a free one).");
                drawings ??= await WpfThread.RunAsync(() => ModelVisionTest.Words.ToDictionary(word => word,
                    word => TouchZoneImages.Decode(VisionTestPicture.Render(word).Content.ToArray()), StringComparer.Ordinal));
                lock (Gate)
                    if (server is null)
                    {
                        server = new ModelAbilityCheck.Fixture(Answer, port ?? 0);
                        Seen.Clear();
                    }
                return Status();
            case "status":
                return Status();
            case "stop":
                ModelAbilityCheck.Fixture? stopping;
                lock (Gate) (stopping, server) = (server, null);
                if (stopping is not null) await stopping.DisposeAsync();
                return Status();
            default:
                throw new ArgumentException("action is start, status or stop.");
        }
    }

    private static object Status()
    {
        lock (Gate)
            return new
            {
                running = server is not null,
                baseUrl = server is null ? null : server.Origin + "/v1",
                models = Models.Select(m => new { id = m.Id, sees = m.Sees, hears = m.Hears }).ToArray(),
                requests = Seen.Count,
                recent = Seen.TakeLast(20).ToArray(),
                note = "FIXTURE endpoint on 127.0.0.1, NOT AI: it reads Test vision's word by comparing pictures, Test hearing's with Windows " +
                    "speech recognition limited to the test words, and answers everything else with fixed text."
            };
    }

    private static (int, string) Answer(ModelAbilityCheck.Request request)
    {
        if (request.Path == "/v1/models")
        {
            Note(request.Path, null, [], "listed the models");
            return (200, JsonSerializer.Serialize(new
            {
                @object = "list",
                data = Models.Select(m => new
                {
                    id = m.Id, @object = "model", context_length = 32768,
                    architecture = new { input_modalities = Modalities(m) }
                })
            }));
        }
        if (request.Path != "/v1/chat/completions") return (404, "{\"error\":\"not found\"}");
        string? id, picture = null, recording = null;
        bool stream;
        var parts = new List<string>();
        try
        {
            using var document = JsonDocument.Parse(request.Body);
            var root = document.RootElement;
            id = root.TryGetProperty("model", out var model) ? model.GetString() : null;
            stream = root.TryGetProperty("stream", out var streamed) && streamed.ValueKind == JsonValueKind.True;
            foreach (var message in root.GetProperty("messages").EnumerateArray())
                if (message.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array)
                    foreach (var part in content.EnumerateArray())
                    {
                        var type = part.TryGetProperty("type", out var kind) ? kind.GetString() ?? "" : "";
                        parts.Add(type);
                        if (type == "image_url" && part.TryGetProperty("image_url", out var image) && image.TryGetProperty("url", out var url))
                            picture ??= url.GetString();
                        if (type == "input_audio" && part.TryGetProperty("input_audio", out var audio) && audio.TryGetProperty("data", out var data))
                            recording ??= data.GetString();
                    }
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or KeyNotFoundException)
        {
            return (400, "{\"error\":{\"message\":\"The request isn't a chat completion.\"}}");
        }
        var lab = Models.FirstOrDefault(m => m.Id == id);
        string[] kinds = [.. parts.Distinct(StringComparer.Ordinal)];
        if (lab is null)
        {
            Note(request.Path, id, kinds, "doesn't have that model");
            return (404, "{\"error\":{\"message\":\"model not found\"}}");
        }
        if (picture is not null && !lab.Sees)
        {
            Note(request.Path, id, kinds, "refused the picture");
            return (400, "{\"error\":{\"message\":\"This model does not support image input.\",\"type\":\"invalid_request_error\"}}");
        }
        if (recording is not null && !lab.Hears)
        {
            Note(request.Path, id, kinds, "refused the recording");
            return (400, "{\"error\":{\"message\":\"This model does not support audio input.\",\"type\":\"invalid_request_error\"}}");
        }
        var read = picture is not null ? Read(picture) : recording is not null ? Hear(recording) : null;
        var text = read ?? (picture is not null ? "A lab description of the picture: a test picture with a word on it (NOT AI)."
            : recording is not null ? "A lab description of the recording: someone speaks calmly (NOT AI)."
            : "A lab reply (NOT AI).");
        Note(request.Path, id, kinds, read is not null ? "read the test word"
            : picture is not null ? "described the picture" : recording is not null ? "described the recording" : "answered");
        return (200, stream ? Streamed(text) : JsonSerializer.Serialize(new
        {
            id = "lab", @object = "chat.completion", model = id,
            choices = new[] { new { index = 0, message = new { role = "assistant", content = text }, finish_reason = "stop" } },
            usage = new { prompt_tokens = 16, completion_tokens = 8, total_tokens = 24 }
        }));
    }

    private static string[] Modalities(LabModel model)
    {
        List<string> inputs = ["text"];
        if (model.Sees) inputs.Add("image");
        if (model.Hears) inputs.Add("audio");
        return [.. inputs];
    }

    private static string Streamed(string text) =>
        "data: " + JsonSerializer.Serialize(new
        {
            id = "lab", @object = "chat.completion.chunk",
            choices = new[] { new { index = 0, delta = new { role = "assistant", content = text } } }
        }) + "\n\n" +
        "data: " + JsonSerializer.Serialize(new
        {
            id = "lab", @object = "chat.completion.chunk", choices = new[] { new { index = 0, delta = new { }, finish_reason = "stop" } },
            usage = new { prompt_tokens = 16, completion_tokens = 8, total_tokens = 24 }
        }) + "\n\ndata: [DONE]\n\n";

    // Which test word a PNG picture shows: the closest of this PC's own drawings of the words, when it is close.
    private static string? Read(string url)
    {
        const string prefix = "data:image/png;base64,";
        if (drawings is null || !url.StartsWith(prefix, StringComparison.Ordinal)) return null;
        ZonePixels shown;
        try { shown = TouchZoneImages.Decode(Convert.FromBase64String(url[prefix.Length..])); }
        catch (Exception error) when (error is FormatException or NotSupportedException or ArgumentException or IOException or InvalidOperationException)
        {
            return null;
        }
        string? best = null;
        var least = double.MaxValue;
        foreach (var (word, drawing) in drawings)
        {
            if (drawing.Width != shown.Width || drawing.Height != shown.Height) continue;
            long difference = 0;
            for (var i = 0; i < drawing.Bgra.Length; i += 4) difference += Math.Abs(drawing.Bgra[i] - shown.Bgra[i]);
            var mean = difference / (double)(drawing.Width * drawing.Height);
            if (mean < least) (least, best) = (mean, word);
        }
        return least < 4 ? best : null;
    }

    // Which test word a WAV recording says, with Windows speech recognition limited to the test words; null when this PC has no
    // English recognizer or it isn't sure.
    private static string? Hear(string base64)
    {
        try
        {
            using var engine = new System.Speech.Recognition.SpeechRecognitionEngine(new CultureInfo("en-US"));
            engine.LoadGrammar(new System.Speech.Recognition.Grammar(new System.Speech.Recognition.GrammarBuilder(
                new System.Speech.Recognition.Choices([.. ModelHearingTest.Words]))));
            using var wav = new MemoryStream(Convert.FromBase64String(base64));
            engine.SetInputToWaveStream(wav);
            return engine.Recognize(TimeSpan.FromSeconds(10)) is { Confidence: > 0.3f } result ? result.Text : null;
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or FormatException or IOException or
            PlatformNotSupportedException or System.Runtime.InteropServices.COMException)
        {
            return null;
        }
    }

    private static void Note(string path, string? model, string[] parts, string answer)
    {
        lock (Gate)
        {
            Seen.Add(new { at = DateTimeOffset.UtcNow, path, model, parts, answer });
            if (Seen.Count > 200) Seen.RemoveAt(0);
        }
    }
}
