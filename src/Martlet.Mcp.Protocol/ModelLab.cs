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
/// <c>lab/omni</c> (text, pictures, recordings and video), <c>lab/text</c> (text only), <c>lab/notools</c> (text only, no tool
/// calls) and <c>lab/gone</c> (retired) with their input modalities and supported parameters, as OpenRouter lists them. Chat
/// completions refuse a picture or a recording that a lab model doesn't take (error 400, as a real server does), lab/notools refuses
/// tools (error 400), lab/gone answers HTTP 410 Gone, the tool models call Test tools' made-up tool, read Test
/// vision's word by comparing the picture with this PC's own drawings of the test words, read Test hearing's word with Windows
/// speech recognition limited to the test words (when this PC has an English recognizer), and answer anything else with fixed
/// text, streamed when asked. Its status counts the requests by model and the kinds of parts they carried, never their content.
/// It ends with this server.</summary>
internal static class ModelLab
{
    // Tools: true lists "tools" in supported_parameters and calls Test tools' tool; false lists no "tools" and refuses a request
    // that carries tools (error 400); null lists no supported_parameters and answers in words. Gone answers every chat
    // completion with HTTP 410, as NVIDIA Build does for a retired model.
    private sealed record LabModel(string Id, bool Sees, bool Hears, bool Video = false, bool? Tools = null, bool Gone = false);

    private static readonly LabModel[] Models =
        [new("lab/sees", true, false, Tools: true), new("lab/hears", false, true, Tools: true), new("lab/omni", true, true, Video: true, Tools: true),
         new("lab/text", false, false), new("lab/notools", false, false, Tools: false), new("lab/gone", true, false, Tools: true, Gone: true)];

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
                models = Models.Select(m => new { id = m.Id, sees = m.Sees, hears = m.Hears, video = m.Video, tools = m.Tools, gone = m.Gone }).ToArray(),
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
                    architecture = new { input_modalities = Modalities(m) },
                    supported_parameters = m.Tools switch { true => new[] { "tools", "temperature" }, false => ["temperature"], _ => null }
                })
            }, new JsonSerializerOptions { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull }));
        }
        if (request.Path != "/v1/chat/completions") return (404, "{\"error\":\"not found\"}");
        string? id, picture = null, recording = null, testWord = null;
        bool stream, tools = false;
        var parts = new List<string>();
        try
        {
            using var document = JsonDocument.Parse(request.Body);
            var root = document.RootElement;
            id = root.TryGetProperty("model", out var model) ? model.GetString() : null;
            stream = root.TryGetProperty("stream", out var streamed) && streamed.ValueKind == JsonValueKind.True;
            if (root.TryGetProperty("tools", out var offered) && offered.ValueKind == JsonValueKind.Array && offered.GetArrayLength() > 0)
            {
                tools = true;
                parts.Add("tools");
                if (offered.EnumerateArray().Any(t => t.TryGetProperty("function", out var f) && f.TryGetProperty("name", out var n) &&
                        n.GetString() == ModelToolTest.ToolName))
                {
                    var asked = string.Concat(root.GetProperty("messages").EnumerateArray()
                        .Select(m => m.TryGetProperty("content", out var c) && c.ValueKind == JsonValueKind.String ? c.GetString() : ""));
                    testWord = ModelVisionTest.Words.FirstOrDefault(w => asked.Contains($"\"{w}\"", StringComparison.Ordinal)) ?? "word";
                }
            }
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
        if (lab.Gone)
        {
            Note(request.Path, id, kinds, "answered 410 Gone");
            return (410, "{\"error\":{\"message\":\"This model reached its end of life and is no longer available.\"}}");
        }
        if (tools && lab.Tools == false)
        {
            Note(request.Path, id, kinds, "refused the tools");
            return (400, "{\"error\":{\"message\":\"This model does not support tools.\",\"type\":\"invalid_request_error\"}}");
        }
        if (testWord is not null && lab.Tools == true && !stream)
        {
            Note(request.Path, id, kinds, "called the test tool");
            return (200, JsonSerializer.Serialize(new
            {
                id = "lab", @object = "chat.completion", model = id,
                choices = new[]
                {
                    new
                    {
                        index = 0, finish_reason = "tool_calls",
                        message = new
                        {
                            role = "assistant", content = (string?)null,
                            tool_calls = new[]
                            {
                                new { id = "call_lab", type = "function",
                                    function = new { name = ModelToolTest.ToolName, arguments = JsonSerializer.Serialize(new { word = testWord }) } }
                            }
                        }
                    }
                },
                usage = new { prompt_tokens = 16, completion_tokens = 8, total_tokens = 24 }
            }));
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
        if (model.Video) inputs.Add("video");
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
