using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Martlet.Conversation;
using Martlet.Core.Contracts;
using Martlet.Core.Settings;
using Martlet.Mcp.Shared;
using Martlet.Providers;

namespace Martlet.Mcp;

/// <summary>image_model_check: the image model (docs/SENSE_MODELS.md, Pictures: the image model). From a data directory: where
/// pictures go now (sense-models.json, the Thinking route and model-abilities.json through the production SenseRouting; or an
/// image model of its own given as arguments), the image prompts, the desktop's image-model-status.json and the newest
/// "Picture path:" and "Image model:" log lines (times only). Then a rehearsal with the production code (SenseLanes,
/// PictureDescriptions, the prompts and the Chat Completions adapter) against two fixture endpoints on 127.0.0.1 (canned words,
/// NOT AI) and synthetic screenshots (no screen capture): a description made ahead of time, a described reply (no picture to
/// Thinking, the note and the fixed instruction, the kept [Screen] line), a reply that doesn't wait for a description still being
/// made, and a look in two stages (a description of the same picture used again, then one made for a new picture). Reads no
/// credentials and contacts nothing else.</summary>
internal static class ImageModelCheck
{
    internal const string GameWords = "FIXTURE, NOT AI: a boss fight in ELDEN RING, nearly won\nThe boss's health bar is at 20%.\n" +
        "The user's health bar is full.";
    internal const string ChatWords = "FIXTURE, NOT AI: a chat in Discord\nA new message from Sam is at the bottom of #general.";
    private const string EyesModel = "fixture-vl", ThinkingModel = "fixture-thinking";

    internal static async Task<object> RunAsync(string dataDirectory, string? imageOrigin, string? imageModel, int? delayMs,
        CancellationToken cancellation)
    {
        if (imageOrigin is null != imageModel is null) throw new ArgumentException("Give both imageOrigin and imageModel, or neither.");
        if (delayMs is < 200 or > 10_000) throw new ArgumentException("delayMs is 200-10000.");
        var loaded = await new SettingsStore(dataDirectory).LoadAsync(cancellation);
        var thinking = loaded.Settings?.Setup?.Routes.FirstOrDefault(r => r.Role == SetupRole.Llm);
        var abilities = ModelAbilities.Load(dataDirectory);
        var (senses, sensesState) = SenseSetup.Read(dataDirectory);
        if (imageOrigin is not null)
        {
            try
            {
                senses = senses.With(SenseKind.Image, new SenseModel
                {
                    Source = SenseSource.Own,
                    Own = new() { Place = DeepThinkingPlace.Endpoint, Origin = imageOrigin, ModelId = imageModel }
                });
                senses.Validate();
            }
            catch (ContractException error) { throw new ArgumentException(error.Message); }
        }
        var route = SenseRouting.For(SenseKind.Image, senses, thinking, abilities);
        var prompts = loaded.Settings?.Prompts;
        return new
        {
            route = new
            {
                path = route.Path.ToString(), model = route.Model?.Describe(), unknown = route.Unknown, why = route.Why,
                from = imageOrigin is null ? "sense-models.json" : "arguments"
            },
            settings = loaded.State switch { SettingsLoadState.Loaded => "loaded", SettingsLoadState.FirstRun => "none", _ => "unreadable" },
            senseModels = imageOrigin is null ? sensesState : "arguments",
            thinking = thinking is null ? null : new { routeType = thinking.RouteType?.ToString(), thinking.ModelId },
            prompts = new
            {
                describePicture = PromptState(prompts, PromptCatalog.DescribePicture),
                picturesAsWords = PromptState(prompts, PromptCatalog.SeenDescribed),
                whatTheImageModelSaw = PromptState(prompts, PromptCatalog.SeenDescribedNote)
            },
            rehearsal = await RehearseAsync(prompts, delayMs ?? 1500, cancellation),
            live = Live(dataDirectory),
            lastTurn = LastTurn(dataDirectory)
        };
    }

    private static string PromptState(PromptSettings? prompts, string id) =>
        prompts?.Overrides.TryGetValue(id, out var text) != true ? "default" : string.IsNullOrWhiteSpace(text) ? "empty" : "edited";

    private static async Task<object> RehearseAsync(PromptSettings? prompts, int delayMs, CancellationToken cancellation)
    {
        // The image model answers its second request (the picture of the chat) only after delayMs, so a reply asks meanwhile.
        await using var eyes = new Fixture(index => index == 1 ? delayMs : 0, index => index == 1 ? ChatWords : GameWords);
        await using var thinking = new Fixture(_ => 0, _ => "Fixture reply (not AI).");
        using var eyesAdapter = ChatCompletionsTextGenerationAdapter.Create(eyes.BaseUrl);
        using var thinkingAdapter = ChatCompletionsTextGenerationAdapter.Create(thinking.BaseUrl);
        var place = new DeepThinkingSettings { Place = DeepThinkingPlace.Endpoint, Origin = eyes.BaseUrl, ModelId = EyesModel };
        var described = new SenseRoute(SenseKind.Image, SensePath.Described, place, "FIXTURE: the image model on 127.0.0.1 describes pictures.");
        var lanes = new SenseLanes(kind => kind == SenseKind.Image ? described : new(kind, SensePath.None, null, "not rehearsed"),
            async (_, model, job, token) =>
            {
                var asked = await AskAsync(eyesAdapter, eyes.BaseUrl, model.ModelId!, new BoundedTextInput(job.Text, job.Instructions, image: job.Image),
                    image: true, token);
                return asked.Reply.Length > 0 ? SenseAnswer.Done(asked.Reply) : SenseAnswer.Failed(asked.Failure ?? asked.Outcome ?? "no words");
            });
        Task<SenseJobResult> Run(SenseJob job, CancellationToken token) => lanes.RunAsync(SenseKind.Image, job, token);
        var pictures = new PictureDescriptions(TimeProvider.System);
        var silent = ("silent", StayQuiet.Marker);
        TextHistoryMessage[] lately =
        [
            new(TextHistoryRole.User, "Ugh, this boss again."),
            new(TextHistoryRole.Assistant, "You've got this! [seen: a boss fight]")
        ];
        var game = Screenshot(game: true);
        var chat = Screenshot(game: false);
        var now = DateTimeOffset.UtcNow;
        // The whole screen as the talk window keys it: the source, the window's title, the program in front and full screen.
        static PictureShot Shot(DateTimeOffset at, string title, string app, bool full) => new(PictureDescriptions.NewVersion(), at,
            "ActiveScreen:", false, title, app, full, "the user's whole screen: every monitor, with the taskbar and any pop-up notifications",
            "the user's whole screen");
        static string Where(PictureShot shot) => VisionHistory.Screen(true, shot.Title, ActiveApp.Label(shot.App, shot.FullScreen));

        // 1. You start to talk: the image model describes the newest picture ahead of time.
        var gameShot = Shot(now, "ELDEN RING", "ELDEN RING", true);
        var wanted = pictures.Wants(gameShot, PictureTrigger.Talking);
        var watch = Stopwatch.StartNew();
        var ahead = await pictures.Describe(gameShot, game, prompts, lately, PictureTrigger.Talking, Run).WaitAsync(cancellation);
        var aheadMs = watch.ElapsedMilliseconds;
        var eyesAhead = Read(eyes.Bodies[0]);
        var describePrompt = PromptSettings.Fill(prompts, PromptCatalog.DescribePicture) ?? PromptCatalog.DefaultDescribePictureInstructions;

        // 2. The reply a moment later, on the same picture: it takes the description at once, as a note; no picture goes.
        var instructions = Join(PromptSettings.Fill(prompts, PromptCatalog.Moment, silent), PictureDescriptions.Instructions(prompts));
        var taken = pictures.For(gameShot with { TakenAt = now.AddSeconds(2) });
        var note = taken is null ? null : PictureDescriptions.Note(prompts, taken);
        var replyAnswer = await AskAsync(thinkingAdapter, thinking.BaseUrl, ThinkingModel, new BoundedTextInput("What do you think of this?",
            instructions, context: note is null ? null : "[MARTLET_NOTES]\n" + note + "\n[/MARTLET_NOTES]"), image: false, cancellation);
        var replySent = Read(thinking.Bodies[^1]);
        var seenTag = SeenTags.Instructions(prompts, StayQuiet.Marker);
        var keptLine = taken is null ? null : VisionHistory.WithMessage(false, Where(gameShot), taken.Summary);

        // 3. The picture changed (a chat now) and the image model is slow: the reply doesn't wait and goes without a description.
        var chatShot = Shot(now.AddSeconds(5), "#general - Discord", "Discord", false);
        var making = pictures.Describe(chatShot, chat, prompts, lately, PictureTrigger.Talking, Run);
        watch.Restart();
        var notReady = pictures.For(chatShot) is null;
        var plainAnswer = await AskAsync(thinkingAdapter, thinking.BaseUrl, ThinkingModel,
            new BoundedTextInput("Who messaged me?", instructions), image: false, cancellation);
        var waitedMs = watch.ElapsedMilliseconds;
        var stillDescribing = !making.IsCompleted;
        var plainSent = Read(thinking.Bodies[^1]);
        var made = await making.WaitAsync(cancellation);
        var readyForNext = pictures.For(chatShot with { TakenAt = now.AddSeconds(6) }) is not null;

        // 4. A look at the same picture: stage one uses the description already made; stage two goes to Thinking without the picture.
        var requestsBefore = eyes.Bodies.Length;
        var reused = await pictures.ForLookAsync(chatShot, chat, prompts, lately, Run, cancellation);
        var reuseRequests = eyes.Bodies.Length - requestsBefore;
        var lookPrompt = PromptSettings.Fill(prompts, PromptCatalog.GlanceScreen, ("app", ActiveApp.Describe(chatShot.App, chatShot.FullScreen)),
            ("title", chatShot.Title), ("remarks", ""), silent)!;
        var lookInstructions = string.Join("\n", new[]
        {
            PromptSettings.Fill(prompts, PromptCatalog.CommentaryScreen, silent), PictureDescriptions.Instructions(prompts),
            PromptSettings.Fill(prompts, PromptCatalog.ChattinessNormal, silent)
        }.Where(part => part is not null));
        var lookNote = reused.Description is { } chatWords ? PictureDescriptions.Note(prompts, chatWords) ?? chatWords.Text : null;
        watch.Restart();
        var lookAnswer = lookNote is null ? null : await AskAsync(thinkingAdapter, thinking.BaseUrl, ThinkingModel,
            new BoundedTextInput(PictureDescriptions.GlanceMessage(lookPrompt, lookNote, null), Join(PromptSettings.Fill(prompts, PromptCatalog.Moment, silent),
                lookInstructions)), image: false, cancellation);
        var lookMs = watch.ElapsedMilliseconds;
        var lookSent = Read(thinking.Bodies[^1]);

        // 5. A look at a new picture: stage one asks the image model first.
        var newShot = Shot(now.AddSeconds(9), "ELDEN RING", "ELDEN RING", true);
        watch.Restart();
        var fresh = await pictures.ForLookAsync(newShot, game, prompts, lately, Run, cancellation);
        var stageOneMs = watch.ElapsedMilliseconds;
        var lane = lanes.Status().First(status => status.Kind == SenseKind.Image);

        var ok = wanted && ahead.Description is not null && eyesAhead.Images == 1 && eyesAhead.System == describePrompt &&
            eyesAhead.User.Contains("Active app: ELDEN RING (full screen).", StringComparison.Ordinal) &&
            eyesAhead.User.Contains("What was said lately", StringComparison.Ordinal) &&
            taken is not null && replyAnswer.Outcome == "Completed" && replySent.Images == 0 && note is not null &&
            replySent.User.Contains(note, StringComparison.Ordinal) && replySent.System.Contains(PictureDescriptions.Instructions(prompts) ?? "\u0000", StringComparison.Ordinal) &&
            (seenTag is null || !replySent.System.Contains(seenTag, StringComparison.Ordinal)) &&
            notReady && stillDescribing && waitedMs < delayMs && plainSent.Images == 0 && !plainSent.User.Contains("What the user sees now", StringComparison.Ordinal) &&
            made.Description is not null && readyForNext &&
            reused is { Reused: true, Description: not null } && reuseRequests == 0 &&
            lookAnswer?.Outcome == "Completed" && lookSent.Images == 0 && lookSent.User.Contains(lookNote!, StringComparison.Ordinal) &&
            (seenTag is null || !lookSent.System.Contains(seenTag, StringComparison.Ordinal)) &&
            fresh is { Reused: false, Description: not null } && eyes.Bodies.Length == 3 && lane.Runs == 3;
        return new
        {
            ok,
            provenance = "FIXTURE, NOT AI: canned words from two Chat Completions endpoints on 127.0.0.1 (the image model and Thinking) " +
                "and synthetic screenshots; no screen capture, nothing sent anywhere else.",
            ahead = new
            {
                trigger = "you started to talk", wanted, outcome = ahead.Result.Outcome.ToString(), ms = aheadMs,
                summary = ahead.Description?.Summary, details = ahead.Description?.Details,
                imageModelRequest = new
                {
                    pictures = eyesAhead.Images, instructionsAreThePrompt = eyesAhead.System == describePrompt,
                    namesTheApp = eyesAhead.User.Contains("Active app: ELDEN RING (full screen).", StringComparison.Ordinal),
                    carriesWhatWasSaid = eyesAhead.User.Contains("What was said lately", StringComparison.Ordinal),
                    messageCharacters = eyesAhead.User.Length
                }
            },
            describedReply = new
            {
                tookDescription = taken is not null, outcome = replyAnswer.Outcome, picturesToThinking = replySent.Images,
                noteSent = note is not null && replySent.User.Contains(note, StringComparison.Ordinal),
                fixedInstruction = replySent.System.Contains(PictureDescriptions.Instructions(prompts) ?? "\u0000", StringComparison.Ordinal),
                seenTagOffered = seenTag is not null && replySent.System.Contains(seenTag, StringComparison.Ordinal),
                keptLine
            },
            neverWaits = new
            {
                imageModelDelayMs = delayMs, descriptionReadyWhenAsked = !notReady, replySentWithoutWaitingMs = waitedMs,
                imageModelStillDescribing = stillDescribing, outcome = plainAnswer.Outcome, picturesToThinking = plainSent.Images,
                noteSent = plainSent.User.Contains("What the user sees now", StringComparison.Ordinal),
                descriptionOutcome = made.Result.Outcome.ToString(), readyForTheNextReply = readyForNext
            },
            look = new
            {
                samePicture = new
                {
                    reused = reused.Reused, imageModelRequests = reuseRequests, stageTwoOutcome = lookAnswer?.Outcome,
                    stageTwoMs = lookMs, picturesToThinking = lookSent.Images,
                    descriptionInMessage = lookNote is not null && lookSent.User.Contains(lookNote, StringComparison.Ordinal),
                    seenTagOffered = seenTag is not null && lookSent.System.Contains(seenTag, StringComparison.Ordinal),
                    keptLine = reused.Description is { } words ? VisionHistory.Look(false, Where(chatShot), null, words.Summary) : null
                },
                newPicture = new { reused = fresh.Reused, outcome = fresh.Result.Outcome.ToString(), stageOneMs, summary = fresh.Description?.Summary }
            },
            lane = new
            {
                runs = lane.Runs, lastPurpose = lane.LastPurpose, lastOutcome = lane.LastOutcome?.ToString(), lane.LastMilliseconds,
                imageModelRequests = eyes.Bodies.Length, thinkingRequests = thinking.Bodies.Length
            },
            timing = new
            {
                freshSeconds = pictures.Timing.Fresh.TotalSeconds, maximumAgeSeconds = pictures.Timing.MaximumAge.TotalSeconds,
                talkEverySeconds = pictures.Timing.TalkEvery.TotalSeconds, changeEverySeconds = pictures.Timing.ChangeEvery.TotalSeconds,
                activeForSeconds = pictures.Timing.ActiveFor.TotalSeconds, timeoutSeconds = pictures.Timing.Timeout.TotalSeconds
            }
        };
    }

    private static string? Join(params string?[] parts) =>
        parts.Where(part => !string.IsNullOrWhiteSpace(part)).ToArray() is { Length: > 0 } present ? string.Join("\n\n", present) : null;

    // A synthetic screenshot (NOT a capture): a red arena with a short green health bar, or a dark chat with light lines.
    private static BoundedImage Screenshot(bool game)
    {
        const int width = 320, height = 180;
        var pixels = new byte[width * height * 4];
        for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
            {
                var i = (y * width + x) * 4;
                var (b, g, r) = game
                    ? y < 12 && x < width / 5 ? ((byte)0, (byte)200, (byte)0) : ((byte)30, (byte)30, (byte)150)
                    : y % 18 < 3 && x % 200 < 150 ? ((byte)220, (byte)220, (byte)220) : ((byte)54, (byte)57, (byte)63);
                pixels[i] = b; pixels[i + 1] = g; pixels[i + 2] = r; pixels[i + 3] = 255;
            }
        return ScreenDigestSheet.Encode(pixels, width, height);
    }

    private sealed record Asked(string? Outcome, string? Failure, string Reply);

    private static async Task<Asked> AskAsync(ChatCompletionsTextGenerationAdapter adapter, string baseUrl, string model, BoundedTextInput input,
        bool image, CancellationToken cancellation)
    {
        var ids = new CorrelationIds { SessionId = Guid.NewGuid(), TurnId = Guid.NewGuid(), RequestId = Guid.NewGuid() };
        var deadline = DateTimeOffset.UtcNow.AddSeconds(30);
        var limits = new TextGenerationLimits();
        var selection = new TextModelSelection(ChatCompletionsSetup.Alias, model);
        var authorization = new TextDisclosureAuthorization(new(ChatCompletionsSetup.BaseUri(baseUrl), ProviderRole.Llm, model),
            selection, ids, 1, limits, deadline, true, true, allowImageDisclosure: image);
        var stream = adapter.Stream(new() { Ids = ids, Epoch = 1, Deadline = deadline }, selection, input, limits, authorization, cancellation);
        var reply = new StringBuilder();
        await foreach (var item in stream.WithCancellation(cancellation))
            if (item.Kind == ProviderEventKind.TextDelta) reply.Append(item.Text);
        return new(stream.Result?.Outcome.ToString(), stream.Result?.Failure?.Code.ToString(), reply.ToString());
    }

    private sealed record Sent(int Images, string System, string User);

    // A captured Chat Completions request: how many pictures it carried, its system text and its last user message's text.
    private static Sent Read(byte[] body)
    {
        using var document = JsonDocument.Parse(body);
        var images = 0;
        string system = "", user = "";
        foreach (var message in document.RootElement.GetProperty("messages").EnumerateArray())
        {
            var content = message.GetProperty("content");
            string text;
            if (content.ValueKind == JsonValueKind.String) text = content.GetString() ?? "";
            else
            {
                images += content.EnumerateArray().Count(part => part.GetProperty("type").GetString() == "image_url");
                text = string.Join("\n", content.EnumerateArray().Where(part => part.GetProperty("type").GetString() == "text")
                    .Select(part => part.GetProperty("text").GetString()));
            }
            switch (message.GetProperty("role").GetString())
            {
                case "system": system += text; break;
                case "user": user = text; break;
            }
        }
        return new(images, system, user);
    }

    // The desktop's image-model-status.json (counts and times; never a description), or null without one.
    private static JsonElement? Live(string dataDirectory)
    {
        var path = Path.Combine(dataDirectory, "image-model-status.json");
        if (!File.Exists(path)) return null;
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            return document.RootElement.Clone();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException) { return null; }
    }

    /// <summary>The newest "Picture path:" and "Image model:" lines of the desktop log: whether the last reply with a picture took
    /// the image model's description, and how long the last description took (never the words).</summary>
    internal static object? LastTurn(string dataDirectory)
    {
        var directory = Martlet.Diagnostics.LocalLogs.Directory(dataDirectory);
        if (!Directory.Exists(directory)) return null;
        var lines = Martlet.Diagnostics.LocalLogs.Read(directory, Martlet.Diagnostics.LocalLogs.ThisDeviceId(dataDirectory))
            .Where(r => r.Component == "desktop").OrderBy(r => r.At).ToArray();
        var path = lines.LastOrDefault(r => r.Message.StartsWith("Picture path: ", StringComparison.Ordinal));
        var made = lines.LastOrDefault(r => r.Message.StartsWith("Image model: ", StringComparison.Ordinal));
        return path is null && made is null ? null : new
        {
            picturePath = path is null ? null : new { path.At, path.Message },
            imageModel = made is null ? null : new { made.At, made.Message }
        };
    }

    // FIXTURE, NOT AI: a Chat Completions endpoint on 127.0.0.1 that records each request and streams canned words, after a wait
    // chosen by the request's number.
    private sealed class Fixture : IAsyncDisposable
    {
        private readonly TcpListener listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource stop = new();
        private readonly List<byte[]> bodies = [];
        private readonly Func<int, int> delay;
        private readonly Func<int, string> words;
        private readonly Task serving;

        internal Fixture(Func<int, int> delay, Func<int, string> words)
        {
            this.delay = delay;
            this.words = words;
            listener.Start();
            serving = ServeAsync();
        }

        internal string BaseUrl => $"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}/v1";

        internal byte[][] Bodies
        {
            get { lock (bodies) return [.. bodies]; }
        }

        private async Task ServeAsync()
        {
            var handling = new List<Task>();
            try
            {
                while (!stop.IsCancellationRequested) handling.Add(HandleAsync(await listener.AcceptTcpClientAsync(stop.Token)));
            }
            catch (Exception error) when (error is OperationCanceledException or SocketException or ObjectDisposedException) { }
            await Task.WhenAll(handling);
        }

        private async Task HandleAsync(TcpClient client)
        {
            using (client)
            {
                try
                {
                    await using var stream = client.GetStream();
                    var body = await HearingCheck.ReadRequestAsync(stream, stop.Token);
                    int index;
                    lock (bodies)
                    {
                        index = bodies.Count;
                        bodies.Add(body);
                    }
                    var wait = delay(index);
                    if (wait > 0) await Task.Delay(wait, stop.Token);
                    const string chunk = "{\"id\":\"fixture\",\"object\":\"chat.completion.chunk\",\"model\":\"fixture\",\"choices\":[{\"index\":0,";
                    var events = "data: " + chunk + "\"delta\":{\"role\":\"assistant\",\"content\":" + JsonSerializer.Serialize(words(index)) +
                        "},\"finish_reason\":null}]}\n\ndata: " + chunk + "\"delta\":{},\"finish_reason\":\"stop\"}]}\n\ndata: [DONE]\n\n";
                    var payload = Encoding.UTF8.GetBytes(events);
                    var head = Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Type: text/event-stream\r\n" +
                        $"Content-Length: {payload.Length}\r\nConnection: close\r\n\r\n");
                    await stream.WriteAsync(head, stop.Token);
                    await stream.WriteAsync(payload, stop.Token);
                    await stream.FlushAsync(stop.Token);
                }
                catch (Exception error) when (error is OperationCanceledException or IOException or SocketException or ObjectDisposedException) { }
            }
        }

        public async ValueTask DisposeAsync()
        {
            stop.Cancel();
            listener.Stop();
            await serving;
            stop.Dispose();
        }
    }
}
