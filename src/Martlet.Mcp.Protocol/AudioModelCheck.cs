using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Martlet.Conversation;
using Martlet.Core.Settings;
using Martlet.Mcp.Shared;
using Martlet.Providers;

namespace Martlet.Mcp;

/// <summary>audio_model_check: Companion › Listening › Audio model (docs/SENSE_MODELS.md, Recordings: the audio model), rehearsed
/// headless with the production pieces against fixture Chat Completions endpoints on 127.0.0.1 (canned answers, NOT AI): where
/// recordings go for the audio model's choices (<see cref="SenseRouting"/>), the hearing consent (Let ... hear my voice), and the
/// desktop's own voice notes (VoiceNote.cs, linked) through the production lanes (<see cref="SenseLanes"/>), the Chat Completions
/// adapter and the context board. The audio model gets the recording with its fixed instructions and a short context; Thinking
/// gets the transcript and the audio model's words as a note sent with that request only, never the recording; the conversation
/// keeps a short line; words that come late go to the context board for the next request, and the reply never waits for them;
/// "none" adds nothing; a model that refuses the recording ends Refused. The recording is a synthesized speech-like clip, never a
/// microphone. Nothing leaves loopback and no credentials are read. With a dataDirectory it also says where recordings go now.</summary>
internal static class AudioModelCheck
{
    private const string Ollama = GenerationSupport.LocalOllamaChatBaseUrl;
    private const string Persona = "You are Martlet, a friendly desktop companion. Keep every reply to one short sentence.";
    private const string Transcript = "I don't know, maybe something quiet.";
    // What the fixture audio model answers (NOT AI): a summary line, then details.
    private const string Described = "Sighs and sounds tired.\nSpeaks slowly; a TV plays in the background.";

    private sealed record Step(string Name, bool Passed, string Detail);

    internal static async Task<object> RunAsync(string? dataDirectory, CancellationToken cancellation)
    {
        var watch = Stopwatch.StartNew();
        List<Step> steps = [];
        void Check(string name, bool passed, string detail) => steps.Add(new(name, passed, detail));

        // ---- Where recordings go (fixture model names, nothing sent) ----
        var omni = Chat("gemma4:e2b");
        var textOnly = Chat("qwen3:8b");
        var ears = Endpoint("gemma3n:e4b");
        string PathOf(SenseModels senses, SetupRoute thinking, ModelAbilities? abilities = null) =>
            SenseRouting.For(SenseKind.Audio, senses, thinking, abilities) is var route && route.Model is null
                ? route.Path.ToString() : $"{route.Path} (own model)";
        Check("route-defaults-omni", PathOf(new(), omni) == "Thinking",
            "the default with a Thinking model that hears: recordings go in Thinking's own request, as before");
        Check("route-defaults-text-only", PathOf(new(), textOnly) == "None", "the default with a text-only Thinking model: transcript only");
        Check("route-own-model", PathOf(new() { Audio = Own(ears) }, textOnly) == "Described (own model)",
            "an audio model of its own that hears describes your voice for a text-only Thinking model");
        Check("route-own-model-beside-omni", PathOf(new() { Audio = Own(ears) }, omni) == "Described (own model)",
            "with an audio model of its own, a Thinking model that hears gets no recording either");
        Check("route-own-is-thinking", PathOf(new() { Audio = Own(Endpoint("gemma4:e2b")) }, omni) == "Thinking",
            "a model of its own that is exactly Thinking's endpoint and model is the text model");
        Check("route-same-as-image-that-cant-hear",
            PathOf(new() { Image = Own(Endpoint("qwen2.5vl:7b")), Audio = new() { Source = SenseSource.OtherSense } }, textOnly) == "None (own model)",
            "the same model as an image model that can't hear: transcript only");
        var deaf = new ModelAbilities().With(new()
        {
            Origin = Ollama, ModelId = "gemma3n:e4b", Hears = false, Source = "a refused recording", CheckedAt = DateTimeOffset.UtcNow
        });
        Check("route-refused-before", PathOf(new() { Audio = Own(ears) }, textOnly, deaf) == "None (own model)",
            "a model that refused a recording before gets none");

        // ---- Consent: the same rule as Let Thinking hear my voice, for where the recording goes ----
        var places = new (string Where, DeepThinkingSettings Model)[]
        {
            ("Ollama on this PC", ears), ("a model Ollama runs in its cloud", Endpoint("gpt-oss:120b-cloud")),
            ("another server on this PC", Endpoint("voxtral-mini-latest", "http://127.0.0.1:1234/v1")),
            ("a cloud provider", Endpoint("gemini-2.5-flash", "https://generativelanguage.googleapis.com/v1beta/openai"))
        };
        var consent = places.Select(place =>
        {
            var stays = VoiceNotes.StaysOnThisPc(place.Model);
            return new
            {
                where = place.Where, staysOnThisPc = stays, neverChosen = VoiceNotes.MayHear(null, stays),
                ticked = VoiceNotes.MayHear(true, stays), unticked = VoiceNotes.MayHear(false, stays)
            };
        }).ToArray();
        Check("consent", consent[0].neverChosen && consent.Skip(1).All(c => !c.neverChosen) && consent.All(c => c.ticked && !c.unticked),
            "never chosen, only Ollama on this PC hears you; ticked, every model may; unticked, none");

        // ---- The pipeline against fixture endpoints ----
        await using var audio = new FixtureEndpoint();
        await using var thinking = new FixtureEndpoint();
        var model = new DeepThinkingSettings { Place = DeepThinkingPlace.Endpoint, Origin = audio.BaseUrl, ModelId = "fixture-audio-model" };
        var described = new SenseRoute(SenseKind.Audio, SensePath.Described, model, "fixture: an audio model of its own");
        var lanes = new SenseLanes(kind => kind == SenseKind.Audio ? described : new(kind, SensePath.None, null, "fixture"), RunJobAsync);
        var clip = HearingCheck.Clip();
        List<TextHistoryMessage> history =
        [
            new(TextHistoryRole.User, "Can you help me plan the weekend?"), new(TextHistoryRole.Assistant, "Sure! What do you have in mind?")
        ];
        var instructions = PromptSettings.Fill(null, PromptCatalog.VoiceDescription)!;
        var told = Persona + "\n\n" + PromptSettings.Fill(null, PromptCatalog.HeardVoiceDescribed)!;
        SenseJob Job() => VoiceNotes.Job(instructions, VoiceNotes.Context(history, "Martlet"), clip, shared: false);
        VoiceNote Note() => new(TimeProvider.System, TimeProvider.System.GetTimestamp(), false, Guid.Empty, model.Describe());

        // 1. In time: the words are ready when the reply's request is built, so they go with it.
        audio.Respond = (_, _) => Task.FromResult((200, Sse(Described)));
        var inTime = Note();
        var result = await lanes.RunAsync(SenseKind.Audio, Job(), cancellation);
        inTime.Finish(result.Outcome, result.Succeeded ? VoiceNotes.Clean(result.Text) : null, result.Problem, result.Took);
        var ready = inTime.TryPeek(out _, out _);
        var note = VoiceNotes.Note(null, [inTime], late: false);
        var reply = new BoundedTextInput(Transcript, told, history, context: note);
        await AskAsync(thinking, reply, cancellation);
        var taken = inTime.TryTake();
        var kept = VoiceNotes.Kept([inTime], late: false);
        // The request after it carries the message as the conversation keeps it: the words and the short line, not the note.
        List<TextHistoryMessage> after = [.. history, new(TextHistoryRole.User, reply.KeptUserText + "\n" + kept), new(TextHistoryRole.Assistant, "Fixture reply (not AI).")];
        await AskAsync(thinking, new BoundedTextInput("Thanks.", told, after), cancellation);
        var heard = Read(audio.Requests[0]);
        var first = Read(thinking.Requests[0]);
        var second = Read(thinking.Requests[1]);
        Check("in-time-audio-model-request", heard is { Audio: true, Model: "fixture-audio-model" } &&
            heard.System.Contains("describe only what the words miss", StringComparison.Ordinal) &&
            heard.LastUser.Contains("The recording is what the user said next.", StringComparison.Ordinal) &&
            heard.LastUser.Contains("Sure! What do you have in mind?", StringComparison.Ordinal),
            "the audio model got the recording, its fixed instructions and the last lines said");
        Check("in-time-reply-takes-the-words", result.Outcome == SenseJobOutcome.Succeeded && ready && taken &&
            first is { Audio: false } && first.LastUser.StartsWith(Transcript, StringComparison.Ordinal) &&
            first.LastUser.Contains("How the user sounded saying this message, as the audio model heard it: Sighs and sounds tired. " +
                "Speaks slowly; a TV plays in the background.", StringComparison.Ordinal) &&
            first.System.Contains("A separate audio model also listens", StringComparison.Ordinal),
            "Thinking got the transcript and the audio model's words as a note, never the recording");
        Check("in-time-kept-line", kept == "(voice: Sighs and sounds tired)" && second.Users.Length >= 2 &&
            second.Users[^2] == Transcript + "\n(voice: Sighs and sounds tired)" && !second.LastUser.Contains("How the user sounded", StringComparison.Ordinal),
            "the conversation keeps only the short line after the message; the note went with one request");

        // 2. Late: the words come after the reply's request was sent; the reply never waited, and the next request takes them.
        var answer = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        audio.Respond = async (_, token) =>
        {
            await answer.Task.WaitAsync(token);
            return (200, Sse(Described));
        };
        var late = Note();
        var describing = Task.Run(async () =>
        {
            var words = await lanes.RunAsync(SenseKind.Audio, Job(), cancellation);
            return late.Finish(words.Outcome, words.Succeeded ? VoiceNotes.Clean(words.Text) : null, words.Problem, words.Took);
        }, cancellation);
        var withoutWords = !late.TryPeek(out _, out _);
        var replyWatch = Stopwatch.StartNew();
        await AskAsync(thinking, new BoundedTextInput("Maybe a walk.", told, history), cancellation);
        var replyMs = replyWatch.ElapsedMilliseconds;
        var pendingWhenSent = !late.IsReady;
        var markedNow = late.MarkLate();
        answer.SetResult();
        var post = await describing.WaitAsync(TimeSpan.FromSeconds(30), cancellation);
        var board = new ContextBoard();
        if (post && VoiceNotes.Note(null, [late], late: true) is { } lateNote)
            board.Post(VoiceNotes.BoardSource, lateNote, DateTimeOffset.Now, VoiceNotes.LateAge, consume: true, kept: VoiceNotes.Kept([late], late: true));
        var snapshot = board.Snapshot(DateTimeOffset.Now);
        await AskAsync(thinking, new BoundedTextInput("What do you think?", told, history, context: snapshot.Text), cancellation);
        board.MarkSent(snapshot);
        var sentWithout = Read(thinking.Requests[2]);
        var next = Read(thinking.Requests[3]);
        Check("late-reply-never-waits", withoutWords && pendingWhenSent && !markedNow && sentWithout is { Audio: false } &&
            !sentWithout.LastUser.Contains("How the user sounded", StringComparison.Ordinal),
            $"the reply's request went without the words in {replyMs} ms while the audio model was still answering");
        Check("late-words-go-to-the-board", post && snapshot.Notes.Count == 1 && snapshot.KeptText == "(voice, earlier: Sighs and sounds tired)" &&
            next.LastUser.Contains("How the user sounded in what they said before this message", StringComparison.Ordinal) &&
            board.Snapshot(DateTimeOffset.Now).Notes.Count == 0,
            "the late words went to the context board as one consume-once note, and the next request took them");

        // 3. Nothing stands out: "none" adds nothing.
        audio.Respond = (_, _) => Task.FromResult((200, Sse("none")));
        var nothing = await lanes.RunAsync(SenseKind.Audio, Job(), cancellation);
        Check("nothing-stands-out", nothing.Succeeded && VoiceNotes.Clean(nothing.Text) is null, "the audio model answered none: no note");

        // 4. A model that refuses the recording ends Refused (the desktop then remembers it can't hear).
        audio.Respond = (_, _) => Task.FromResult((400, "{\"error\":{\"message\":\"audio input is not supported by this model\"}}"));
        var refused = await lanes.RunAsync(SenseKind.Audio, Job(), cancellation);
        Check("refused", refused.Outcome == SenseJobOutcome.Refused, $"{refused.Outcome}: {refused.Problem}");

        return new
        {
            ok = steps.All(step => step.Passed),
            saved = dataDirectory is null ? null : await SavedAsync(dataDirectory, cancellation),
            consent,
            steps = steps.Select(step => new { step.Name, step.Passed, step.Detail }),
            requests = new { audioModel = audio.Requests.Length, thinking = thinking.Requests.Length },
            fixture = "FIXTURE endpoints on 127.0.0.1 with canned answers (NOT AI); a synthesized speech-like clip, never a microphone.",
            ms = watch.ElapsedMilliseconds
        };
    }

    // Where recordings go with a data directory's choices (sense-models.json, settings.json, model-abilities.json; never a key).
    private static async Task<object> SavedAsync(string dataDirectory, CancellationToken cancellation)
    {
        var loaded = await new SettingsStore(dataDirectory).LoadAsync(cancellation);
        var thinking = loaded.Settings?.Setup?.Routes.FirstOrDefault(r => r.Role == SetupRole.Llm);
        var (senses, file) = SenseModels.Read(dataDirectory);
        var route = SenseRouting.For(SenseKind.Audio, senses, thinking, ModelAbilities.Load(dataDirectory));
        return new
        {
            file, source = senses.Audio.Source.ToString(), path = route.Path.ToString(), model = route.Model?.Describe(), unknown = route.Unknown,
            why = route.Why, staysOnThisPc = route.Model is { } own && VoiceNotes.StaysOnThisPc(own),
            thinkingGetsRecordings = route.Model is null && route.Path == SensePath.Thinking
        };
    }

    // The desktop's runner in short: one request with the job's instructions, text and recording on the production adapter. A
    // request the model rejects before answering, with a recording, is a refused recording (as ConversationTurn decides it).
    private static async Task<SenseAnswer> RunJobAsync(SenseKind kind, DeepThinkingSettings model, SenseJob job, CancellationToken token)
    {
        using var adapter = ChatCompletionsTextGenerationAdapter.Create(model.Origin!);
        var asked = await HearingCheck.AskAsync(adapter, model.Origin!, model.ModelId!,
            new BoundedTextInput(job.Text, job.Instructions, audio: job.Audio), job.Audio is not null, token);
        if (asked.Reply.Length > 0) return SenseAnswer.Done(asked.Reply);
        return job.Audio is not null && asked.Failure == nameof(ProviderFailureCode.RequestRejected)
            ? SenseAnswer.Rejected("it refused the recording") : SenseAnswer.Failed(asked.Failure ?? asked.Outcome ?? "no answer");
    }

    private static async Task AskAsync(FixtureEndpoint thinking, BoundedTextInput input, CancellationToken cancellation)
    {
        using var adapter = ChatCompletionsTextGenerationAdapter.Create(thinking.BaseUrl);
        await HearingCheck.AskAsync(adapter, thinking.BaseUrl, "fixture-text-model", input, false, cancellation);
    }

    private static SetupRoute Chat(string model, string origin = Ollama) => new()
    {
        RouteType = SetupRouteType.ChatCompletions, Role = SetupRole.Llm, ProviderAlias = ChatCompletionsSetup.Alias, Origin = origin,
        ModelId = model, ConfigurationRevision = Guid.NewGuid(), Enabled = true
    };

    private static DeepThinkingSettings Endpoint(string model, string origin = Ollama) =>
        new() { Place = DeepThinkingPlace.Endpoint, Origin = origin, ModelId = model };

    private static SenseModel Own(DeepThinkingSettings model) => new() { Source = SenseSource.Own, Own = model };

    // One canned Chat Completions answer as a stream.
    private static string Sse(string text)
    {
        const string chunk = "{\"id\":\"fixture\",\"object\":\"chat.completion.chunk\",\"model\":\"fixture\",\"choices\":[{\"index\":0,";
        return "data: " + chunk + "\"delta\":{\"role\":\"assistant\",\"content\":" + JsonSerializer.Serialize(text) + "},\"finish_reason\":null}]}\n\n" +
            "data: " + chunk + "\"delta\":{},\"finish_reason\":\"stop\"}]}\n\ndata: [DONE]\n\n";
    }

    private sealed record Sent(string? Model, bool Audio, string System, string[] Users)
    {
        internal string LastUser => Users.Length == 0 ? "" : Users[^1];
    }

    // A captured request: its model, whether any message carried a recording, the instructions and the user messages' text.
    private static Sent Read(byte[] body)
    {
        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;
        var messages = root.GetProperty("messages").EnumerateArray().ToArray();
        static string Text(JsonElement content) => content.ValueKind == JsonValueKind.String ? content.GetString() ?? ""
            : string.Join("\n", content.EnumerateArray().Where(part => part.GetProperty("type").GetString() == "text")
                .Select(part => part.GetProperty("text").GetString()));
        static bool Recording(JsonElement message) => message.TryGetProperty("content", out var content) &&
            content.ValueKind == JsonValueKind.Array && content.EnumerateArray().Any(part => part.GetProperty("type").GetString() == "input_audio");
        string Role(JsonElement message) => message.GetProperty("role").GetString() ?? "";
        return new(root.TryGetProperty("model", out var model) ? model.GetString() : null, messages.Any(Recording),
            string.Join("\n", messages.Where(m => Role(m) is "system" or "developer").Select(m => Text(m.GetProperty("content")))),
            [.. messages.Where(m => Role(m) == "user").Select(m => Text(m.GetProperty("content")))]);
    }

    // A minimal HTTP/1.1 endpoint on 127.0.0.1: records each request body and answers with Respond (a stream for 200).
    private sealed class FixtureEndpoint : IAsyncDisposable
    {
        private readonly TcpListener listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource stop = new();
        private readonly List<byte[]> requests = [];
        private readonly Task serving;

        internal FixtureEndpoint()
        {
            listener.Start();
            BaseUrl = $"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}/v1";
            serving = ServeAsync();
        }

        internal string BaseUrl { get; }
        internal Func<byte[], CancellationToken, Task<(int Status, string Payload)>> Respond { get; set; } =
            (_, _) => Task.FromResult((200, Sse("Fixture reply (not AI).")));
        internal byte[][] Requests { get { lock (requests) return [.. requests]; } }

        private async Task ServeAsync()
        {
            try
            {
                while (!stop.IsCancellationRequested)
                {
                    using var client = await listener.AcceptTcpClientAsync(stop.Token);
                    await using var stream = client.GetStream();
                    var body = await HearingCheck.ReadRequestAsync(stream, stop.Token);
                    lock (requests) requests.Add(body);
                    var (status, payload) = await Respond(body, stop.Token);
                    var bytes = Encoding.UTF8.GetBytes(payload);
                    var head = Encoding.ASCII.GetBytes($"HTTP/1.1 {status} {(status == 200 ? "OK" : "Bad Request")}\r\n" +
                        $"Content-Type: {(status == 200 ? "text/event-stream" : "application/json")}\r\nContent-Length: {bytes.Length}\r\n" +
                        "Connection: close\r\n\r\n");
                    await stream.WriteAsync(head, stop.Token);
                    await stream.WriteAsync(bytes, stop.Token);
                    await stream.FlushAsync(stop.Token);
                }
            }
            catch (Exception error) when (error is OperationCanceledException or SocketException or ObjectDisposedException or IOException) { }
        }

        public async ValueTask DisposeAsync()
        {
            stop.Cancel();
            listener.Stop();
            await serving.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            stop.Dispose();
        }
    }
}
