using System.Net;
using System.Text;
using Martlet.Conversation;
using Martlet.Core.Settings;
using Martlet.Providers;
using Martlet.Providers.Tests;

namespace Martlet.Desktop.Tests;

// NOT AI: the fixture's Chat Completions handler stands in for an image model of its own (and, where a test says so, for the
// conversation's Thinking model on the same computer of the home network).
public sealed class SenseModelsDesktopTests
{
    private const string ThinkingUrl = "https://10.77.0.20:8443/v1", EyesUrl = "https://10.77.0.20:8444/v1";

    private static readonly DeepThinkingSettings Eyes = new()
    {
        Place = DeepThinkingPlace.Endpoint, Origin = EyesUrl, ModelId = "qwen2.5vl:7b"
    };

    private static SenseModels WithEyes => new() { Image = new() { Source = SenseSource.Own, Own = Eyes } };

    private static SenseJob Look() => new()
    {
        Purpose = "reply picture", Key = "picture", Instructions = "Describe what the user sees, in words for another model.",
        Text = "Window: a game.", Image = new([0xFF, 0xD8, 0xFF, .. new byte[32]], ImageMediaType.Jpeg, 4, 4)
    };

    private static HttpResponseMessage ChatReply(string words) => TextRecordingHandler.Sse(
        "data: {\"id\":\"chat-fixture\",\"object\":\"chat.completion.chunk\",\"model\":\"fixture\",\"choices\":[{\"index\":0,\"delta\":" +
        "{\"role\":\"assistant\",\"content\":\"" + words + "\"},\"finish_reason\":\"stop\"}]}\n\ndata: [DONE]\n\n");

    private static string? ModelOf(byte[] body)
    {
        using var json = System.Text.Json.JsonDocument.Parse(body);
        return json.RootElement.GetProperty("model").GetString();
    }

    private static async Task<SenseJobResult> Describe(LiveFixture fixture)
    {
        var job = fixture.Controller.RunSenseAsync(SenseKind.Image, Look(), CancellationToken.None);
        await fixture.Advance(() => job.IsCompleted);
        return await job;
    }

    // Thinking as a text-only model on a computer of the home network, where the image model runs too.
    private static async Task ThinkOnTheImageModelsComputer(LiveFixture fixture)
    {
        var loaded = await fixture.Store.LoadAsync();
        var old = loaded.Settings!.Setup!.Routes.Single(route => route.Role == SetupRole.Llm);
        var changed = SetupSettings.QueueReplacedCredential(ChatCompletionsSetup.SelectRoute(loaded.Settings!, ThinkingUrl, "qwen3:8b"), old);
        var route = changed.Setup!.Routes.Single(item => item.Role == SetupRole.Llm);
        await fixture.Save(SetupSettings.ReplaceRoute(changed, route with { Consent = route.Selection() }));
    }

    [Fact]
    public async Task With_the_defaults_the_text_model_takes_pictures_and_no_sense_job_is_sent()
    {
        await using var fixture = await LiveFixture.Create();
        // The fixture's Thinking model (OpenAI) sees; OpenAI's route takes no recordings.
        Assert.Equal(SensePath.Thinking, fixture.Controller.SenseRoute(SenseKind.Image).Path);
        Assert.Equal(SensePath.None, fixture.Controller.SenseRoute(SenseKind.Audio).Path);
        Assert.False(fixture.Controller.SenseSharesConversation(SenseKind.Image));

        var result = await Describe(fixture);
        Assert.Equal(SenseJobOutcome.NoModel, result.Outcome);
        Assert.Equal(0, fixture.Chat.Calls);
        Assert.Equal(0, fixture.Llm.Calls);
    }

    [Fact]
    public async Task An_image_model_of_its_own_describes_the_picture_on_its_own_endpoint()
    {
        await using var fixture = await LiveFixture.Create();
        fixture.Controller.SenseModels = WithEyes;
        fixture.Chat.Respond = (_, _) => Task.FromResult(ChatReply("A boss fight; the health bar is at 20%."));
        var route = fixture.Controller.SenseRoute(SenseKind.Image);
        Assert.Equal(SensePath.Described, route.Path);
        Assert.Equal(Eyes.Key, route.Model!.Key);

        var result = await Describe(fixture);
        Assert.Equal(SenseJobOutcome.Succeeded, result.Outcome);
        Assert.Equal("A boss fight; the health bar is at 20%.", result.Text);
        Assert.Equal(Eyes.Describe(), result.Model);
        // The image model got the picture with the job's own instructions; Thinking got nothing.
        Assert.Equal(1, fixture.Chat.Calls);
        Assert.Equal(0, fixture.Llm.Calls);
        Assert.Equal("qwen2.5vl:7b", ModelOf(fixture.Chat.Body));
        var body = Encoding.UTF8.GetString(fixture.Chat.Body);
        Assert.Contains("image_url", body);
        Assert.Contains("Describe what the user sees, in words for another model.", body);
        Assert.Equal((1, SenseJobOutcome.Succeeded), (fixture.Controller.Senses.Status()[0].Runs, fixture.Controller.Senses.Status()[0].LastOutcome));
    }

    [Fact]
    public async Task A_model_that_refuses_the_picture_is_remembered_as_not_seeing_and_gets_no_more_pictures()
    {
        await using var fixture = await LiveFixture.Create(data: true);
        fixture.Controller.SenseModels = WithEyes;
        fixture.Chat.Respond = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StringContent("{\"error\":{\"message\":\"image input is not supported by this model\"}}", Encoding.UTF8, "application/json")
        });

        var result = await Describe(fixture);
        Assert.Equal(SenseJobOutcome.Refused, result.Outcome);
        var found = ModelAbilities.Load(fixture.DirectoryPath).Find(EyesUrl, "qwen2.5vl:7b");
        Assert.NotNull(found);
        Assert.False(found.Sees);
        Assert.Equal("a refused picture", found.Source);
        var route = fixture.Controller.SenseRoute(SenseKind.Image);
        Assert.Equal(SensePath.None, route.Path);
        Assert.Contains("doesn't see pictures", route.Why);

        var calls = fixture.Chat.Calls;
        Assert.Equal(SenseJobOutcome.NoModel, (await Describe(fixture)).Outcome);
        Assert.Equal(calls, fixture.Chat.Calls);
        // The status file says where pictures go now, never what was sent.
        await fixture.Advance(() => File.Exists(Path.Combine(fixture.DirectoryPath, LiveConversationController.SenseStatusFile)));
        var status = fixture.Controller.SenseStatusJson();
        Assert.Contains("\"path\": \"None\"", status);
        Assert.DoesNotContain("Window: a game.", status);
    }

    [Fact]
    public async Task A_job_on_the_conversations_computer_waits_until_the_reply_is_made()
    {
        await using var fixture = await LiveFixture.Create();
        await ThinkOnTheImageModelsComputer(fixture);
        fixture.Controller.SenseModels = WithEyes;
        Assert.True(fixture.Controller.SenseSharesConversation(SenseKind.Image));
        var reply = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var described = 0;
        fixture.Chat.Respond = async (_, token) =>
        {
            if (ModelOf(fixture.Chat.Body) == "qwen2.5vl:7b")
            {
                Interlocked.Increment(ref described);
                return ChatReply("A described picture.");
            }
            await reply.Task.WaitAsync(token);
            return ChatReply("Hello!");
        };

        var operation = fixture.Start("Hello there.");
        await fixture.Advance(() => fixture.Chat.Calls == 1 && operation.Turn is not null);
        var job = fixture.Controller.RunSenseAsync(SenseKind.Image, Look(), CancellationToken.None);
        await fixture.Advance(() => fixture.Controller.Senses.Status()[0].Held == 1);
        await fixture.Pass(TimeSpan.FromMilliseconds(200));
        // The reply's Thinking request runs on the same computer and nothing of it came yet: the image model waits.
        Assert.Equal(0, Volatile.Read(ref described));
        Assert.True(fixture.Controller.SenseHeld(SenseKind.Image));

        reply.SetResult();
        await fixture.Advance(() => job.IsCompleted);
        Assert.Equal(SenseJobOutcome.Succeeded, (await job).Outcome);
        Assert.Equal(1, described);
        await fixture.Finish(operation);
        Assert.Equal("Hello!", operation.Turn!.Content.Text);
    }

    [Fact]
    public async Task A_running_job_on_the_conversations_computer_is_stopped_when_a_reply_starts()
    {
        await using var fixture = await LiveFixture.Create();
        await ThinkOnTheImageModelsComputer(fixture);
        fixture.Controller.SenseModels = WithEyes;
        var reply = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stopped = 0;
        fixture.Chat.Respond = async (_, token) =>
        {
            if (ModelOf(fixture.Chat.Body) == "qwen2.5vl:7b")
            {
                try { await Task.Delay(Timeout.Infinite, token); }
                catch (OperationCanceledException) { Interlocked.Increment(ref stopped); throw; }
            }
            await reply.Task.WaitAsync(token);
            return ChatReply("Hello!");
        };

        var job = fixture.Controller.RunSenseAsync(SenseKind.Image, Look(), CancellationToken.None);
        await fixture.Advance(() => fixture.Chat.Calls == 1);
        var operation = fixture.Start("Hello there.");
        await fixture.Advance(() => job.IsCompleted);
        var result = await job;
        Assert.Equal(SenseJobOutcome.Preempted, result.Outcome);
        await fixture.Advance(() => Volatile.Read(ref stopped) == 1);

        reply.SetResult();
        await fixture.Finish(operation);
        Assert.Equal("Hello!", operation.Turn!.Content.Text);
        Assert.False(fixture.Controller.SenseHeld(SenseKind.Image));
    }

    [Fact]
    public async Task Tests_can_answer_jobs_and_decide_sharing_and_a_refusal_shows_at_once_without_a_data_folder()
    {
        await using var fixture = await LiveFixture.Create();
        fixture.Controller.SenseModels = WithEyes;
        var answers = new Queue<SenseAnswer>([SenseAnswer.Done("Words from the test."), SenseAnswer.Rejected("it refused the picture")]);
        fixture.Controller.SenseRunner = (kind, model, job, _) =>
        {
            Assert.Equal((SenseKind.Image, Eyes.Key, "reply picture"), (kind, model.Key, job.Purpose));
            return Task.FromResult(answers.Dequeue());
        };
        fixture.Controller.SenseSharing = kind => kind == SenseKind.Image;
        Assert.True(fixture.Controller.SenseSharesConversation(SenseKind.Image));
        Assert.False(fixture.Controller.SenseHeld(SenseKind.Image));

        Assert.Equal("Words from the test.", (await Describe(fixture)).Text);
        Assert.Equal(SenseJobOutcome.Refused, (await Describe(fixture)).Outcome);
        Assert.Equal(0, fixture.Chat.Calls);
        var route = fixture.Controller.SenseRoute(SenseKind.Image);
        Assert.Equal(SensePath.None, route.Path);
        Assert.Equal(false, fixture.Controller.Configuration!.Abilities.Find(EyesUrl, "qwen2.5vl:7b")!.Sees);
    }

    [Fact]
    public async Task A_helper_job_with_a_picture_goes_to_the_image_model_when_no_pool_member_sees()
    {
        await using var fixture = await LiveFixture.Create();
        fixture.Controller.SenseModels = WithEyes;
        var jobs = new List<SenseJob>();
        fixture.Controller.SenseRunner = (kind, model, job, _) =>
        {
            Assert.Equal((SenseKind.Image, Eyes.Key), (kind, model.Key));
            lock (jobs) jobs.Add(job);
            return Task.FromResult(SenseAnswer.Done("LEFT 0.31 0.42 0.05; RIGHT 0.62 0.42 0.05"));
        };
        var picture = new BoundedImage([0xFF, 0xD8, 0xFF, .. new byte[32]], ImageMediaType.Jpeg, 4, 4);

        var asked = fixture.Controller.AskHelperAsync(HelperJobKind.Eyes, "Measuring the eyes", "Find both eyes.", "The close-up.", picture,
            CancellationToken.None);
        await fixture.Advance(() => asked.IsCompleted);
        Assert.Equal(("LEFT 0.31 0.42 0.05; RIGHT 0.62 0.42 0.05", (string?)null), await asked);
        var job = Assert.Single(jobs);
        Assert.Equal(("eyes", LiveConversationController.HelperPriority, "Find both eyes.", "The close-up."),
            (job.Purpose, job.Priority, job.Instructions, job.Text));
        Assert.Same(picture, job.Image);
        Assert.Equal((SenseJob.MaximumOutputTokens, false, (bool?)null), (job.MaxOutputTokens, job.DropWhenStale, job.Reasoning));
        // Thinking got nothing; a helper job without a picture still goes to it.
        Assert.Equal(0, fixture.Llm.Calls);
        var named = fixture.Controller.AskHelperAsync(HelperJobKind.ActionNaming, "Naming emotes", "Name them.", "wave, nod", null, CancellationToken.None);
        await fixture.Advance(() => named.IsCompleted);
        Assert.NotNull((await named).Answer);
        Assert.Equal(1, fixture.Llm.Calls);
        Assert.Single(jobs);
    }

    [Fact]
    public async Task Without_an_image_model_a_helper_job_with_a_picture_goes_to_thinking_as_before()
    {
        await using var fixture = await LiveFixture.Create();
        var picture = new BoundedImage([0xFF, 0xD8, 0xFF, .. new byte[32]], ImageMediaType.Jpeg, 4, 4);
        var asked = fixture.Controller.AskHelperAsync(HelperJobKind.Eyes, "Measuring the eyes", "Find both eyes.", "The close-up.", picture,
            CancellationToken.None);
        await fixture.Advance(() => asked.IsCompleted);
        Assert.NotNull((await asked).Answer);
        Assert.Equal(1, fixture.Llm.Calls);
        Assert.Equal(0, fixture.Controller.Senses.Status()[0].Runs);
    }
}
