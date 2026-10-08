using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using Martlet.Conversation;
using Martlet.Conversation.Tests;
using Martlet.Core.Settings;
using Martlet.Providers;
using Martlet.Providers.Tests;

namespace Martlet.Desktop.Tests;

// The image model (docs/SENSE_MODELS.md, Pictures: the image model). NOT AI: the fixture's Chat Completions handler stands in for
// an image model of its own, and the fixture's Responses handler for Thinking.
public sealed class ImageModelPipelineTests
{
    private const string EyesUrl = "https://10.77.0.20:8444/v1";
    private static readonly BoundedImage Image = new([0xFF, 0xD8, 0xFF, .. new byte[32]], ImageMediaType.Jpeg, 4, 4);
    private static readonly WatchSource Whole = new(WatchKind.ActiveScreen);
    private static readonly DeepThinkingSettings Eyes = new() { Place = DeepThinkingPlace.Endpoint, Origin = EyesUrl, ModelId = "qwen2.5vl:7b" };
    private static SenseModels WithEyes => new() { Image = new() { Source = SenseSource.Own, Own = Eyes } };
    private const string Described = "A boss fight in ELDEN RING\nThe boss's health bar is at 20%.";
    private const string Note = "What the user sees now (the user's whole screen), as Martlet's image model describes it:\n" + Described;

    private static HttpResponseMessage ChatReply(string words) => TextRecordingHandler.Sse(
        "data: {\"id\":\"chat-fixture\",\"object\":\"chat.completion.chunk\",\"model\":\"fixture\",\"choices\":[{\"index\":0,\"delta\":" +
        "{\"role\":\"assistant\",\"content\":" + JsonSerializer.Serialize(words) + "},\"finish_reason\":\"stop\"}]}\n\ndata: [DONE]\n\n");

    private static HttpResponseMessage Refusal() => new(HttpStatusCode.BadRequest)
    {
        Content = new StringContent("{\"error\":{\"message\":\"image input is not supported by this model\"}}", Encoding.UTF8, "application/json")
    };

    private static SeenScreen Game(LiveFixture fixture, long? picture = null) =>
        new(Image, "ELDEN RING", Whole, "ELDEN RING", FullScreen: true, Picture: picture ?? PictureDescriptions.NewVersion(),
            TakenAt: fixture.Clock.GetUtcNow());

    private static LiveConversationOperation Reply(LiveFixture fixture, string text, SeenScreen? seen) =>
        fixture.Controller.Start(text, voice: false, microphone: false, approved: true, seen: seen);

    private static LiveConversationOperation Look(LiveFixture fixture, long picture) =>
        fixture.Controller.StartCommentary(Image, "ELDEN RING", ChattinessChoice.Normal, voice: false, screenApproved: true, source: Whole,
            app: "ELDEN RING", fullScreen: true, picture: picture, takenAt: fixture.Clock.GetUtcNow());

    // A captured Chat Completions request (the image model's): its model, system text, last user text and pictures.
    private static (string? Model, string System, string User, int Pictures) Chat(byte[] body)
    {
        using var json = JsonDocument.Parse(body);
        string system = "", user = "";
        var pictures = 0;
        foreach (var message in json.RootElement.GetProperty("messages").EnumerateArray())
        {
            var content = message.GetProperty("content");
            var text = content.ValueKind == JsonValueKind.String ? content.GetString()! : string.Join("\n", content.EnumerateArray()
                .Where(part => part.GetProperty("type").GetString() == "text").Select(part => part.GetProperty("text").GetString()));
            if (content.ValueKind == JsonValueKind.Array)
                pictures += content.EnumerateArray().Count(part => part.GetProperty("type").GetString() == "image_url");
            if (message.GetProperty("role").GetString() == "system") system += text;
            else if (message.GetProperty("role").GetString() == "user") user = text;
        }
        return (json.RootElement.GetProperty("model").GetString(), system, user, pictures);
    }

    // Thinking's Responses request: its instructions, each user message's text and how many pictures it carried.
    private static (string Instructions, string[] Users, int Pictures) Thinking(byte[] body)
    {
        using var json = JsonDocument.Parse(body);
        var pictures = 0;
        var users = new List<string>();
        foreach (var item in json.RootElement.GetProperty("input").EnumerateArray().Where(item => item.GetProperty("role").GetString() == "user"))
        {
            var content = item.GetProperty("content");
            if (content.ValueKind == JsonValueKind.String)
            {
                users.Add(content.GetString()!);
                continue;
            }
            pictures += content.EnumerateArray().Count(part => part.GetProperty("type").GetString() == "input_image");
            users.Add(string.Join("\n", content.EnumerateArray().Where(part => part.GetProperty("type").GetString() == "input_text")
                .Select(part => part.GetProperty("text").GetString())));
        }
        return (json.RootElement.GetProperty("instructions").GetString()!, [.. users], pictures);
    }

    [Fact]
    public async Task A_described_reply_takes_the_ready_description_as_a_note_and_sends_no_picture()
    {
        await using var fixture = await LiveFixture.Create();
        fixture.Controller.SenseModels = WithEyes;
        fixture.Chat.Respond = (_, _) => Task.FromResult(ChatReply(Described));
        var seen = Game(fixture);

        // You start to talk: the image model describes the newest picture ahead of time, with what was said lately.
        fixture.Controller.DescribeAhead(seen, PictureTrigger.Talking);
        await fixture.Advance(() => fixture.Controller.Pictures.For(seen.Shot()) is not null);
        var eyes = Chat(fixture.Chat.Body);
        Assert.Equal("qwen2.5vl:7b", eyes.Model);
        Assert.Equal(1, eyes.Pictures);
        Assert.Equal(PromptCatalog.DefaultDescribePictureInstructions, eyes.System);
        Assert.StartsWith("The picture shows the user's whole screen: every monitor", eyes.User);
        Assert.Contains("Active app: ELDEN RING (full screen). Active window: \"ELDEN RING\".", eyes.User);
        Assert.Contains("Nothing was said yet.", eyes.User);

        // The reply takes it at once: the words go as a note, Thinking gets no picture, and its instructions say what the note is.
        fixture.Answer("Almost there!");
        var reply = Reply(fixture, "What do you think?", seen);
        await fixture.Finish(reply);
        Assert.True(reply.ScreenDescribed);
        Assert.False(reply.ScreenSent);
        Assert.Equal("your words and the image model's description of the picture", reply.Inputs);
        var sent = Thinking(fixture.Llm.Body);
        Assert.Equal(0, sent.Pictures);
        Assert.Contains(PictureDescriptions.Instructions(null)!, sent.Instructions);
        Assert.DoesNotContain(SeenTags.Instructions(null, StayQuiet.Marker)!, sent.Instructions);
        Assert.DoesNotContain("When the user's message comes with a picture", sent.Instructions);
        Assert.StartsWith("What do you think?", sent.Users[^1]);
        Assert.Contains(Note, sent.Users[^1]);
        Assert.Contains("Active app in the picture: ELDEN RING (full screen). Active window: \"ELDEN RING\".", sent.Users[^1]);
        Assert.Equal(1, fixture.Chat.Calls);

        // The conversation keeps a [Screen] line made from its first line, never the note; the next description gets what was said.
        fixture.Answer("Sure.");
        await fixture.Finish(Reply(fixture, "Thanks.", null));
        var next = Thinking(fixture.Llm.Body);
        Assert.EndsWith("\n[Screen] With this message you saw the user's whole screen (active window \"ELDEN RING\" in ELDEN RING, full screen): " +
            "A boss fight in ELDEN RING.", next.Users[^2]);
        Assert.DoesNotContain("What the user sees now", next.Users[^2]);
        Assert.Equal(sent.Instructions, next.Instructions);
        // The pace for descriptions while you talk is one every 4 s.
        fixture.Clock.Advance(TimeSpan.FromSeconds(5));
        var later = Game(fixture);
        fixture.Controller.DescribeAhead(later with { Title = "Discord", App = "Discord", FullScreen = false }, PictureTrigger.Talking);
        await fixture.Advance(() => fixture.Chat.Calls == 2);
        Assert.Contains("What was said lately, oldest first:\nUser: What do you think?", Chat(fixture.Chat.Body).User);
        Assert.Contains("Martlet: Almost there!", Chat(fixture.Chat.Body).User);
    }

    [Fact]
    public async Task A_reply_never_waits_for_a_description_that_is_still_being_made()
    {
        await using var fixture = await LiveFixture.Create();
        fixture.Controller.SenseModels = WithEyes;
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Chat.Respond = async (_, token) =>
        {
            await release.Task.WaitAsync(token);
            return ChatReply(Described);
        };
        var seen = Game(fixture);
        fixture.Controller.DescribeAhead(seen, PictureTrigger.Talking);
        await fixture.Advance(() => fixture.Chat.Calls == 1);

        fixture.Answer("Hi!");
        var reply = Reply(fixture, "Hello?", seen);
        await fixture.Finish(reply);
        Assert.Equal("runtime.Completed", reply.Status.Code);
        Assert.False(reply.ScreenDescribed);
        Assert.False(reply.ScreenSent);
        var sent = Thinking(fixture.Llm.Body);
        Assert.Equal(0, sent.Pictures);
        Assert.DoesNotContain("What the user sees now", sent.Users[^1]);
        // Its instructions are the same as a reply that took one, so the prompt cache keeps them.
        Assert.Contains(PictureDescriptions.Instructions(null)!, sent.Instructions);

        // The description that comes later is there for the next reply.
        release.SetResult();
        await fixture.Advance(() => fixture.Controller.Pictures.For(seen.Shot()) is not null);
        fixture.Answer("Oh, nice.");
        var next = Reply(fixture, "And now?", seen with { TakenAt = fixture.Clock.GetUtcNow() });
        await fixture.Finish(next);
        Assert.True(next.ScreenDescribed);
        Assert.Contains(Note, Thinking(fixture.Llm.Body).Users[^1]);
    }

    [Fact]
    public async Task With_the_default_path_the_reply_request_is_the_same_byte_for_byte()
    {
        await using var fixture = await LiveFixture.Create();
        var seen = Game(fixture);
        async Task<string> Ask(SenseModels senses, SeenScreen picture)
        {
            fixture.Controller.SenseModels = senses;
            fixture.Answer("Nice. [seen: a boss fight]");
            await fixture.Finish(Reply(fixture, "What do you think?", picture));
            var body = Encoding.UTF8.GetString(fixture.Llm.Body);
            fixture.Controller.ForgetContext();
            return body;
        }

        var plain = await Ask(new SenseModels(), seen);
        // "The same model as the audio model", which is the text model, and a picture without a version both send the same request.
        Assert.Equal(plain, await Ask(new() { Image = new() { Source = SenseSource.OtherSense } }, seen));
        Assert.Equal(plain, await Ask(new SenseModels(), seen with { Picture = 0, TakenAt = null }));
        var sent = Thinking(Encoding.UTF8.GetBytes(plain));
        Assert.Equal(1, sent.Pictures);
        Assert.Contains(SeenTags.Instructions(null, StayQuiet.Marker)!, sent.Instructions);
        Assert.Contains("When the user's message comes with a picture", sent.Instructions);
        Assert.DoesNotContain(PictureDescriptions.Instructions(null)!, sent.Instructions);
        Assert.DoesNotContain("image model", plain);
        Assert.Equal(0, fixture.Chat.Calls);
    }

    [Fact]
    public async Task A_look_with_an_image_model_has_two_stages_and_uses_a_description_of_the_same_picture_again()
    {
        await using var fixture = await LiveFixture.Create();
        fixture.Controller.SenseModels = WithEyes;
        fixture.Chat.Respond = (_, _) => Task.FromResult(ChatReply(Described));
        var picture = PictureDescriptions.NewVersion();

        // Stage one: the image model describes the screenshot. Stage two: Thinking gets the glance message with the words.
        fixture.Answer("Almost there!");
        var look = Look(fixture, picture);
        await fixture.Finish(look);
        Assert.Equal("runtime.Completed", look.Status.Code);
        Assert.Equal((1, 1), (fixture.Chat.Calls, fixture.Llm.Calls));
        Assert.Equal(1, Chat(fixture.Chat.Body).Pictures);
        var sent = Thinking(fixture.Llm.Body);
        Assert.Equal(0, sent.Pictures);
        Assert.StartsWith("(Screen glance. Active app: ELDEN RING (full screen). Active window: \"ELDEN RING\".", sent.Users[^1]);
        Assert.Contains("\n\n" + Note, sent.Users[^1]);
        Assert.Contains(PictureDescriptions.Instructions(null)!, sent.Instructions);
        Assert.DoesNotContain(SeenTags.Instructions(null, StayQuiet.Marker)!, sent.Instructions);
        Assert.Equal("the image model's description of the picture", look.Inputs);
        Assert.Same(fixture.Controller.Pictures.For(new SeenScreen(Image, "ELDEN RING", Whole, "ELDEN RING", true, picture).Shot(), exact: true),
            look.Described);

        // A second look at the same picture uses the description again: no second request to the image model.
        fixture.Answer("[pass]");
        var again = Look(fixture, picture);
        await fixture.Finish(again);
        Assert.Equal((1, 2), (fixture.Chat.Calls, fixture.Llm.Calls));
        var users = Thinking(fixture.Llm.Body).Users;
        Assert.Equal("[Screen] You looked at the user's whole screen (active window \"ELDEN RING\" in ELDEN RING, full screen): " +
            "A boss fight in ELDEN RING.", users[0]);
    }

    [Fact]
    public async Task A_refused_picture_ends_the_look_without_Thinking_and_later_replies_get_no_picture()
    {
        await using var fixture = await LiveFixture.Create();
        fixture.Controller.SenseModels = WithEyes;
        fixture.Chat.Respond = (_, _) => Task.FromResult(Refusal());

        var look = Look(fixture, PictureDescriptions.NewVersion());
        await fixture.Finish(look);
        Assert.Equal("commentary.image_refused", look.Status.Code);
        Assert.Equal(0, fixture.Llm.Calls);
        var calls = fixture.Chat.Calls;
        // Martlet remembers that the image model can't see: no model takes pictures now, and Thinking never gets them instead.
        Assert.False(fixture.Controller.CanSee);
        Assert.Contains("doesn't see pictures", fixture.Controller.ImageAdvice());
        var refused = Assert.Throws<LiveActionException>(() => Look(fixture, PictureDescriptions.NewVersion()));
        Assert.Equal("commentary.vision_unsupported", refused.Code);

        fixture.Answer("Sure.");
        var reply = Reply(fixture, "What do you think?", Game(fixture));
        await fixture.Finish(reply);
        Assert.Null(reply.Seen);
        var sent = Thinking(fixture.Llm.Body);
        Assert.Equal(0, sent.Pictures);
        Assert.DoesNotContain(PictureDescriptions.Instructions(null)!, sent.Instructions);
        Assert.DoesNotContain(SeenTags.Instructions(null, StayQuiet.Marker)!, sent.Instructions);
        Assert.Equal(calls, fixture.Chat.Calls);
    }

    [Fact]
    public async Task A_description_on_the_conversations_hardware_stops_when_a_reply_starts()
    {
        await using var fixture = await LiveFixture.Create();
        fixture.Controller.SenseModels = WithEyes;
        fixture.Controller.SenseSharing = kind => kind == SenseKind.Image;
        var stopped = 0;
        fixture.Chat.Respond = async (_, token) =>
        {
            try { await Task.Delay(Timeout.Infinite, token); }
            catch (OperationCanceledException) { Interlocked.Increment(ref stopped); throw; }
            return ChatReply(Described);
        };
        var seen = Game(fixture);
        fixture.Controller.DescribeAhead(seen, PictureTrigger.Talking);
        await fixture.Advance(() => fixture.Chat.Calls == 1);

        // Thinking answers only once the description was stopped, so the reply holds the hardware meanwhile.
        var answer = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Llm.Respond = async (_, token) =>
        {
            await answer.Task.WaitAsync(token);
            return TextRecordingHandler.Sse(Harness.Trace("Hi!"));
        };
        var reply = Reply(fixture, "Hello?", seen);
        await fixture.Advance(() => Volatile.Read(ref stopped) == 1);
        answer.SetResult();
        await fixture.Finish(reply);
        Assert.False(reply.ScreenDescribed);
        Assert.Equal(0, Thinking(fixture.Llm.Body).Pictures);
        await fixture.Advance(() => fixture.Controller.Pictures.Now().Running == 0);
        Assert.Null(fixture.Controller.Pictures.For(seen.Shot()));
        Assert.Equal(SenseJobOutcome.Preempted, fixture.Controller.Senses.Status()[0].LastOutcome);
    }

    [Fact]
    public async Task The_screen_summary_uses_the_image_model_first()
    {
        await using var fixture = await LiveFixture.Create();
        var thinker = fixture.Controller.ScreenDigestThinker;
        // The fixture has no Thinking pool member that sees: without an image model there are no summaries, as before.
        Assert.False(thinker.CanSee);

        fixture.Controller.SenseModels = WithEyes;
        fixture.Chat.Respond = (_, _) => Task.FromResult(ChatReply("They switched from VS Code to a boss fight."));
        Assert.True(thinker.CanSee);
        Assert.True(thinker.MayStartNow);
        Assert.True(thinker.SeesBesideConversation);
        var now = fixture.Clock.GetUtcNow();
        var digest = thinker.DigestAsync(new ScreenDigestJob("FIXTURE summary message.", Image, 2, now.AddSeconds(-10), now, "changes"),
            CancellationToken.None);
        await fixture.Advance(() => digest.IsCompleted);
        Assert.Equal("They switched from VS Code to a boss fight.", await digest);
        var eyes = Chat(fixture.Chat.Body);
        Assert.Equal((PoolScreenDigestThinker.Instructions, "FIXTURE summary message.", 1), (eyes.System, eyes.User, eyes.Pictures));
        Assert.Equal("screen summary", fixture.Controller.Senses.Status()[0].LastPurpose);

        // On the conversation's own computer and graphics card, the summary right after you start to speak isn't made there.
        fixture.Controller.SenseSharing = _ => true;
        Assert.False(thinker.SeesBesideConversation);
    }

    [Fact]
    public async Task Vision_advice_and_disclosure_name_the_image_model_and_keep_the_default_text()
    {
        await using var fixture = await LiveFixture.Create();
        var configured = fixture.Controller.Configuration!;
        var thinking = configured.Routes.Single(route => route.Role == SetupRole.Llm);
        var abilities = configured.Abilities;
        var standard = SenseRouting.For(SenseKind.Image, new SenseModels(), thinking, abilities);
        Assert.Equal(LiveConversationConfiguration.VisionAdvice(thinking, abilities), LiveConversationConfiguration.VisionAdvice(thinking, abilities, standard));
        Assert.Equal(LiveConversationConfiguration.ScreenDisclosure(thinking, ChattinessChoice.Normal, Whole),
            LiveConversationConfiguration.ScreenDisclosure(thinking, ChattinessChoice.Normal, Whole, standard));
        Assert.Equal(configured.VisionAdvice(), fixture.Controller.ImageAdvice());

        var eyes = SenseRouting.For(SenseKind.Image, WithEyes, thinking, abilities);
        var advice = LiveConversationConfiguration.VisionAdvice(thinking, abilities, eyes);
        Assert.StartsWith("The image model, 10.77.0.20 (qwen2.5vl:7b), sees the pictures: pictures go to it, and only its words go to ", advice);
        var disclosure = LiveConversationConfiguration.ScreenDisclosure(thinking, ChattinessChoice.Normal, Whole, eyes);
        Assert.Contains("Pictures go to your image model, 10.77.0.20 (qwen2.5vl:7b), which describes them in words", disclosure);
        Assert.Contains("the window title, the name of the program in front and the last few lines of your conversation", disclosure);
        Assert.Contains("(or to the fallback for Thinking, if Thinking fails)", disclosure);

        fixture.Controller.SenseModels = WithEyes;
        Assert.True(fixture.Controller.CanSee);
        Assert.True(fixture.Controller.ImageDescribed);
        Assert.Equal(advice, fixture.Controller.ImageAdvice());
    }

    [Fact]
    public async Task A_description_counts_for_the_same_picture_or_a_fresh_screenshot_of_the_same_window()
    {
        var clock = new RuntimeClock();
        var pictures = new PictureDescriptions(clock);
        var shot = new PictureShot(PictureDescriptions.NewVersion(), clock.GetUtcNow(), "ActiveScreen:", false, "ELDEN RING", "ELDEN RING", true,
            "the user's whole screen", "the user's whole screen");
        var made = await pictures.Describe(shot, Image, null, [], PictureTrigger.Talking,
            (_, _) => Task.FromResult(new SenseJobResult(SenseJobOutcome.Succeeded, Described, "fixture", null, TimeSpan.FromSeconds(1))));
        Assert.Equal(("A boss fight in ELDEN RING", "The boss's health bar is at 20%."), (made.Description!.Summary, made.Description.Details));
        var now = () => clock.GetUtcNow();
        clock.Advance(TimeSpan.FromSeconds(5));
        Assert.NotNull(pictures.For(shot with { TakenAt = now() }));
        // Another screenshot of the same window, its description's screenshot 5 s old: it counts, but not for a look.
        var newer = shot with { Version = PictureDescriptions.NewVersion(), TakenAt = now() };
        Assert.NotNull(pictures.For(newer));
        Assert.Null(pictures.For(newer, exact: true));
        Assert.Null(pictures.For(newer with { Title = "#general - Discord", App = "Discord", FullScreen = false }));
        Assert.Null(pictures.For(newer with { Source = "ActiveWindow:" }));
        clock.Advance(TimeSpan.FromSeconds(6));
        // The described screenshot is 11 s old now: only the same picture still counts, up to a minute.
        Assert.Null(pictures.For(newer));
        Assert.NotNull(pictures.For(shot));
        clock.Advance(TimeSpan.FromSeconds(50));
        Assert.Null(pictures.For(shot));
        Assert.Null(pictures.For(shot with { Version = 0 }));
    }

    [Fact]
    public async Task Descriptions_are_asked_for_at_a_bounded_pace_and_not_again_right_after_a_failure()
    {
        var clock = new RuntimeClock();
        var pictures = new PictureDescriptions(clock);
        PictureShot Shot(string title) => new(PictureDescriptions.NewVersion(), clock.GetUtcNow(), "ActiveScreen:", false, title, "App", false,
            "the user's whole screen", "the user's whole screen");
        Task<SenseJobResult> Works(SenseJob job, CancellationToken token) =>
            Task.FromResult(new SenseJobResult(SenseJobOutcome.Succeeded, "Words", "fixture", null, TimeSpan.Zero));
        Task<SenseJobResult> Fails(SenseJob job, CancellationToken token) =>
            Task.FromResult(new SenseJobResult(SenseJobOutcome.Failed, null, "fixture", "it failed", TimeSpan.Zero));

        var first = Shot("One");
        Assert.True(pictures.Wants(first, PictureTrigger.Talking));
        await pictures.Describe(first, Image, null, [], PictureTrigger.Talking, Works);
        // Described: not again. Another picture while you talk: at most one every 4 s; a changed one only while you talk with Martlet.
        Assert.False(pictures.Wants(first with { TakenAt = clock.GetUtcNow() }, PictureTrigger.Talking));
        var second = Shot("Two");
        Assert.False(pictures.Wants(second, PictureTrigger.Talking));
        Assert.True(pictures.Wants(second, PictureTrigger.Look));
        clock.Advance(TimeSpan.FromSeconds(4));
        Assert.True(pictures.Wants(second, PictureTrigger.Talking));
        Assert.False(pictures.Wants(second, PictureTrigger.Changed, active: true));
        clock.Advance(TimeSpan.FromSeconds(11));
        Assert.False(pictures.Wants(second, PictureTrigger.Changed, active: false));
        Assert.True(pictures.Wants(second, PictureTrigger.Changed, active: true));

        // A failure: nothing new for 5 s, and not the same picture for 30 s.
        var failed = await pictures.Describe(second, Image, null, [], PictureTrigger.Look, Fails);
        Assert.Null(failed.Description);
        var third = Shot("Three");
        Assert.False(pictures.Wants(third, PictureTrigger.Look));
        clock.Advance(TimeSpan.FromSeconds(6));
        Assert.True(pictures.Wants(third, PictureTrigger.Look));
        Assert.False(pictures.Wants(second, PictureTrigger.Look));
        clock.Advance(TimeSpan.FromSeconds(25));
        Assert.True(pictures.Wants(second, PictureTrigger.Look));

        // Forget lets every description go.
        pictures.Forget();
        Assert.Null(pictures.For(first));
    }

    [Theory]
    [InlineData("**Summary:** A boss fight in ELDEN RING.\n- The boss's health bar is at 20%.\n", "A boss fight in ELDEN RING", "The boss's health bar is at 20%.")]
    [InlineData("1. A chat in Discord [seen: x]\n2. Sam wrote last.", "A chat in Discord", "Sam wrote last.")]
    [InlineData("They fight a boss in ELDEN RING and its health is low, about a fifth of the bar is left. " +
        "The user's own health is full and the music swells.", "They fight a boss in ELDEN RING and its health is low, about a fifth of the bar is left",
        "The user's own health is full and the music swells.")]
    public void The_answer_gives_a_summary_line_and_details(string answer, string summary, string details) =>
        Assert.Equal((summary, details), PictureDescriptions.Parse(answer));

    [Fact]
    public void An_empty_answer_is_no_description()
    {
        Assert.Null(PictureDescriptions.Parse(null));
        Assert.Null(PictureDescriptions.Parse("  \n "));
        Assert.Null(PictureDescriptions.Parse("[seen: nothing]"));
        Assert.True(PictureDescriptions.Parse(new string('x', 3000))!.Value.Summary.Length <= SeenTags.MaximumDescription + 1);
    }

    [Fact]
    public async Task The_mcp_check_rehearses_the_image_model_against_fixture_endpoints()
    {
        var directory = Path.Combine(Path.GetTempPath(), "Martlet.ImageModelCheck." + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var result = await Martlet.Mcp.ImageModelCheck.RunAsync(directory, "http://127.0.0.1:11434/v1", "qwen2.5vl:7b", 3000, CancellationToken.None);
            using var json = JsonDocument.Parse(JsonSerializer.Serialize(result));
            Assert.Equal("Described", json.RootElement.GetProperty("route").GetProperty("path").GetString());
            Assert.True(json.RootElement.GetProperty("rehearsal").GetProperty("ok").GetBoolean(), json.RootElement.GetRawText());
        }
        finally { Directory.Delete(directory, true); }
    }
}
