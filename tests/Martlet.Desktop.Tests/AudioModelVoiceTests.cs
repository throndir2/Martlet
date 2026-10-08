using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Martlet.Audio;
using Martlet.Conversation;
using Martlet.Core.Settings;
using Martlet.Providers;
using Martlet.Providers.Tests;

namespace Martlet.Desktop.Tests;

// The audio model (docs/SENSE_MODELS.md, Recordings: the audio model). NOT AI: SenseRunner answers the audio model's jobs, and the
// fixture's Chat Completions handler stands in for a Thinking model that hears.
public sealed class AudioModelVoiceTests
{
    private const string ThinkingUrl = "http://127.0.0.1:1234/v1";

    private static readonly DeepThinkingSettings LocalEars = new()
    {
        Place = DeepThinkingPlace.Endpoint, Origin = GenerationSupport.LocalOllamaChatBaseUrl, ModelId = "gemma3n:e4b"
    };

    private static readonly DeepThinkingSettings CloudEars = new()
    {
        Place = DeepThinkingPlace.Endpoint, Origin = "https://audio.example.com/v1", ModelId = "qwen2-audio-7b-instruct"
    };

    private static SenseModels WithEars(DeepThinkingSettings ears) => new() { Audio = new() { Source = SenseSource.Own, Own = ears } };

    // Thinking as a model that hears (an OpenAI-compatible server on this PC): by default it would get your recording.
    private static async Task ThinkingThatHears(LiveFixture fixture)
    {
        var loaded = await fixture.Store.LoadAsync();
        var old = loaded.Settings!.Setup!.Routes.Single(item => item.Role == SetupRole.Llm);
        var changed = SetupSettings.QueueReplacedCredential(ChatCompletionsSetup.SelectRoute(loaded.Settings!, ThinkingUrl, "voxtral-mini-latest"), old);
        var route = changed.Setup!.Routes.Single(item => item.Role == SetupRole.Llm);
        await fixture.Save(SetupSettings.ReplaceRoute(changed, route with { Consent = route.Selection() }));
        Assert.Equal(HearingSupport.Supported, fixture.Controller.Configuration!.Hearing());
    }

    private static HttpResponseMessage ChatReply(string words) => TextRecordingHandler.Sse(
        "data: {\"id\":\"chat-fixture\",\"object\":\"chat.completion.chunk\",\"model\":\"server-model\",\"choices\":[{\"index\":0,\"delta\":" +
        "{\"role\":\"assistant\",\"content\":\"" + words + "\"},\"finish_reason\":\"stop\"}]}\n\ndata: [DONE]\n\n");

    private static HttpResponseMessage Words(string text) => ProviderFixtures.Json(JsonSerializer.Serialize(new { text }));

    private sealed record Request(bool Recording, string Instructions, string[] Users);

    // A Chat Completions request: whether it carried a recording, its instructions and its user messages' text.
    private static Request Read(string body)
    {
        using var document = JsonDocument.Parse(body);
        var messages = document.RootElement.GetProperty("messages").EnumerateArray().ToArray();
        static string Text(JsonElement content) => content.ValueKind == JsonValueKind.String ? content.GetString()!
            : string.Join("\n", content.EnumerateArray().Where(part => part.GetProperty("type").GetString() == "text")
                .Select(part => part.GetProperty("text").GetString()));
        string Role(JsonElement message) => message.GetProperty("role").GetString()!;
        return new(body.Contains("input_audio", StringComparison.Ordinal),
            string.Join("\n", messages.Where(m => Role(m) is "system" or "developer").Select(m => Text(m.GetProperty("content")))),
            [.. messages.Where(m => Role(m) == "user").Select(m => Text(m.GetProperty("content")))]);
    }

    [Fact]
    public Task An_audio_model_of_its_own_hears_you_in_Thinkings_place_and_its_ready_words_go_with_the_reply() => DispatcherTest(async () =>
    {
        await using var fixture = await LiveFixture.Create();
        await ThinkingThatHears(fixture);
        fixture.Controller.SenseModels = WithEars(LocalEars);
        fixture.Controller.SenseSharing = _ => false;
        Assert.Equal(SensePath.Described, fixture.Controller.SenseRoute(SenseKind.Audio).Path);
        Assert.False(fixture.Controller.ThinkingTakesVoice(fixture.Controller.Configuration));
        var jobs = new ConcurrentQueue<SenseJob>();
        fixture.Controller.SenseRunner = (_, _, job, _) =>
        {
            jobs.Enqueue(job);
            return Task.FromResult(SenseAnswer.Done("Laughs; sounds excited."));
        };
        var described = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Controller.VoiceDescribed += _ => described.TrySetResult();
        // Speech-to-text answers after the audio model, so its words are ready when the reply's request is built.
        fixture.Stt.Respond = async (_, token) =>
        {
            await described.Task.WaitAsync(token);
            return Words("Hello there.");
        };
        var bodies = new ConcurrentQueue<string>();
        fixture.Chat.Respond = (_, _) =>
        {
            bodies.Enqueue(Encoding.UTF8.GetString(fixture.Chat.Body));
            return Task.FromResult(ChatReply("Heard you."));
        };
        EnqueueUtterance(fixture.Capture, quietBefore: 5, speech: 25, quietAfter: 15);
        var window = fixture.Open(new TalkPreferences(SpeakReplies: false, HearVoice: true));
        try
        {
            await Loaded(window);
            Click(window, "MicChip");
            await fixture.Advance(() => window.Current is { OwnershipReleased: true } &&
                window.Messages.Any(m => m.Role == ChatRole.Martlet && m.Text == "Heard you."));
            await Heartbeat();
            // The audio model got the recording with its fixed instructions.
            var job = Assert.Single(jobs);
            Assert.Equal(VoiceNotes.Purpose, job.Purpose);
            Assert.NotNull(job.Audio);
            Assert.Contains("describe only what the words miss", job.Instructions, StringComparison.Ordinal);
            // Thinking got the transcript and the audio model's words in a note, never the recording (no straight path either).
            var sent = Read(Assert.Single(bodies));
            Assert.False(sent.Recording);
            Assert.StartsWith("Hello there.", sent.Users[^1], StringComparison.Ordinal);
            Assert.Contains("How the user sounded saying this message, as the audio model heard it: Laughs; sounds excited.", sent.Users[^1],
                StringComparison.Ordinal);
            Assert.Contains("A separate audio model also listens", sent.Instructions, StringComparison.Ordinal);
            var reply = window.Current!;
            Assert.Equal(1, reply.VoiceNotesTaken);
            Assert.False(reply.VoiceSent);
            Assert.Contains("how you sounded", reply.Inputs, StringComparison.Ordinal);
            var said = Assert.Single(window.Messages, m => m.IsUser);
            Assert.Equal("Hello there.", said.Text);
            Assert.StartsWith("The audio model described how you sounded", said.Note, StringComparison.Ordinal);
            Assert.EndsWith("and the reply took it.", said.Note, StringComparison.Ordinal);

            // The next request keeps only a short line after the message, never the note.
            var next = fixture.Start("And now?");
            await fixture.Finish(next);
            var after = Read(bodies.Last());
            Assert.Contains(after.Users, user => user.StartsWith("Hello there.", StringComparison.Ordinal) &&
                user.EndsWith("(voice: Laughs; sounds excited)", StringComparison.Ordinal));
            Assert.DoesNotContain(after.Users, user => user.Contains("How the user sounded", StringComparison.Ordinal));
        }
        finally { window.Close(); }
    });

    [Fact]
    public Task The_reply_never_waits_for_the_audio_model_and_its_late_words_go_with_the_next_request() => DispatcherTest(async () =>
    {
        await using var fixture = await LiveFixture.Create();
        await ThinkingThatHears(fixture);
        fixture.Controller.SenseModels = WithEars(LocalEars);
        fixture.Controller.SenseSharing = _ => false;
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var asked = 0;
        fixture.Controller.SenseRunner = async (_, _, _, token) =>
        {
            Interlocked.Increment(ref asked);
            await release.Task.WaitAsync(token);
            return SenseAnswer.Done("Sighs; sounds tired.");
        };
        var described = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Controller.VoiceDescribed += _ => described.TrySetResult();
        fixture.Stt.Respond = (_, _) => Task.FromResult(Words("Hello there."));
        var bodies = new ConcurrentQueue<string>();
        fixture.Chat.Respond = (_, _) =>
        {
            bodies.Enqueue(Encoding.UTF8.GetString(fixture.Chat.Body));
            return Task.FromResult(ChatReply("Heard you."));
        };
        EnqueueUtterance(fixture.Capture, quietBefore: 5, speech: 25, quietAfter: 15);
        var window = fixture.Open(new TalkPreferences(SpeakReplies: false, HearVoice: true));
        try
        {
            await Loaded(window);
            Click(window, "MicChip");
            await fixture.Advance(() => window.Current is { OwnershipReleased: true } &&
                window.Messages.Any(m => m.Role == ChatRole.Martlet && m.Text == "Heard you."));
            await Heartbeat();
            // The audio model is still describing: the reply went without its words.
            Assert.Equal(1, Volatile.Read(ref asked));
            Assert.False(described.Task.IsCompleted);
            var sent = Read(Assert.Single(bodies));
            Assert.False(sent.Recording);
            Assert.DoesNotContain("How the user sounded", sent.Users[^1], StringComparison.Ordinal);
            Assert.Equal(0, window.Current!.VoiceNotesTaken);
            Assert.Equal("The audio model describes how you sounded; Martlet gets it with your next message.",
                Assert.Single(window.Messages, m => m.IsUser).Note);

            // Once they come, the words wait on the context board for the next request, which takes them once.
            release.SetResult();
            await described.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var note = Assert.Single(fixture.Controller.Board.Snapshot(fixture.Clock.GetLocalNow()).Notes,
                n => n.Source == VoiceNotes.BoardSource);
            Assert.True(note.Consume);
            Assert.Equal("(voice, earlier: Sighs; sounds tired)", note.Kept);
            var next = fixture.Start("And now?");
            await fixture.Finish(next);
            Assert.Contains("How the user sounded in what they said before this message, as the audio model heard it: Sighs; sounds tired.",
                Read(bodies.Last()).Users[^1], StringComparison.Ordinal);
            Assert.DoesNotContain(fixture.Controller.Board.Snapshot(fixture.Clock.GetLocalNow()).Notes, n => n.Source == VoiceNotes.BoardSource);
        }
        finally { window.Close(); }
    });

    [Theory]
    [InlineData(true, null, true)]
    [InlineData(false, null, false)]
    [InlineData(false, true, true)]
    [InlineData(true, false, false)]
    public Task Your_recording_goes_to_the_audio_model_only_under_the_hearing_rule(bool local, bool? choice, bool hears) => DispatcherTest(async () =>
    {
        await using var fixture = await LiveFixture.Create();
        fixture.Controller.SenseModels = WithEars(local ? LocalEars : CloudEars);
        fixture.Controller.SenseSharing = _ => false;
        Assert.Equal(local, fixture.Controller.RecordingStaysOnThisPc());
        var asked = 0;
        fixture.Controller.SenseRunner = (_, _, _, _) =>
        {
            Interlocked.Increment(ref asked);
            return Task.FromResult(SenseAnswer.Done("Calm."));
        };
        fixture.Stt.Respond = (_, _) => Task.FromResult(Words("Hello there."));
        fixture.Answer("Hi!");
        EnqueueUtterance(fixture.Capture, quietBefore: 5, speech: 25, quietAfter: 15);
        var window = fixture.Open(new TalkPreferences(SpeakReplies: false, HearVoice: choice));
        try
        {
            await Loaded(window);
            Click(window, "MicChip");
            await fixture.Advance(() => window.Current is { OwnershipReleased: true } &&
                window.Messages.Any(m => m.Role == ChatRole.Martlet && m.Text == "Hi!") && (!hears || Volatile.Read(ref asked) == 1));
            await Heartbeat();
            // Never chosen, only an audio model in Ollama on this PC hears you; your own choice always wins.
            Assert.Equal(hears ? 1 : 0, Volatile.Read(ref asked));
            Assert.Equal(hears, VoiceNotes.MayHear(choice, fixture.Controller.RecordingStaysOnThisPc()));
        }
        finally { window.Close(); }
    });

    [Fact]
    public Task On_a_model_that_shares_the_conversations_computer_your_voice_is_described_after_the_reply() => DispatcherTest(async () =>
    {
        await using var fixture = await LiveFixture.Create();
        await ThinkingThatHears(fixture);
        fixture.Controller.SenseModels = WithEars(LocalEars);
        fixture.Controller.SenseSharing = _ => true;
        var replied = 0;
        var afterReply = false;
        fixture.Controller.SenseRunner = (_, _, _, _) =>
        {
            afterReply = Volatile.Read(ref replied) == 1;
            return Task.FromResult(SenseAnswer.Done("Whispers."));
        };
        var described = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Controller.VoiceDescribed += _ => described.TrySetResult();
        fixture.Stt.Respond = (_, _) => Task.FromResult(Words("Hello there."));
        var bodies = new ConcurrentQueue<string>();
        fixture.Chat.Respond = (_, _) =>
        {
            bodies.Enqueue(Encoding.UTF8.GetString(fixture.Chat.Body));
            Interlocked.Exchange(ref replied, 1);
            return Task.FromResult(ChatReply("Heard you."));
        };
        EnqueueUtterance(fixture.Capture, quietBefore: 5, speech: 25, quietAfter: 15);
        var window = fixture.Open(new TalkPreferences(SpeakReplies: false, HearVoice: true));
        try
        {
            await Loaded(window);
            Click(window, "MicChip");
            await fixture.Advance(() => described.Task.IsCompleted && window.Current is { OwnershipReleased: true });
            await Heartbeat();
            // The job waited for the reply's request, so it never ran beside it; its words go to the next request.
            Assert.True(afterReply);
            Assert.DoesNotContain("How the user sounded", Read(Assert.Single(bodies)).Users[^1], StringComparison.Ordinal);
            Assert.Equal("(voice, earlier: Whispers)", Assert.Single(fixture.Controller.Board.Snapshot(fixture.Clock.GetLocalNow()).Notes,
                n => n.Source == VoiceNotes.BoardSource).Kept);
        }
        finally { window.Close(); }
    });

    [Fact]
    public async Task With_the_defaults_the_text_model_takes_your_voice_and_no_audio_model_prompt_is_sent()
    {
        await using var fixture = await LiveFixture.Create();
        await ThinkingThatHears(fixture);
        var configured = fixture.Controller.Configuration!;
        Assert.Equal(SensePath.Thinking, fixture.Controller.SenseRoute(SenseKind.Audio).Path);
        Assert.True(fixture.Controller.ThinkingTakesVoice(configured));
        Assert.False(fixture.Controller.DescribesVoice(configured));
        Assert.Equal(configured.RecordingStaysOnThisPc(), fixture.Controller.RecordingStaysOnThisPc());
        Assert.Null(fixture.Controller.SoundJudge());
        var bodies = new ConcurrentQueue<string>();
        fixture.Chat.Respond = (_, _) =>
        {
            bodies.Enqueue(Encoding.UTF8.GetString(fixture.Chat.Body));
            return Task.FromResult(ChatReply("Hi!"));
        };
        var operation = fixture.Start("Hello.");
        await fixture.Finish(operation);
        var sent = Read(Assert.Single(bodies));
        Assert.DoesNotContain("audio model", sent.Instructions, StringComparison.Ordinal);
        Assert.DoesNotContain("How the user sounded", sent.Users[^1], StringComparison.Ordinal);
        Assert.Null(operation.VoiceNotes);
        Assert.DoesNotContain("how you sounded", operation.Inputs, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_sound_digest_judges_on_the_audio_model_first_and_leaves_shared_hardware_to_the_conversation()
    {
        await using var fixture = await LiveFixture.Create();
        Assert.Null(SenseSoundJudge.For(fixture.Controller));
        fixture.Controller.SenseModels = WithEars(LocalEars);
        var judge = Assert.IsType<SenseSoundJudge>(fixture.Controller.SoundJudge());
        Assert.Equal(SoundJudgeKind.AudioModel, judge.Kind);
        Assert.Equal("Ollama on this PC (gemma3n:e4b)", judge.Name);
        Assert.Equal("Ollama on this PC (gemma3n:e4b), the audio model, hears a short clip.", PcSoundDigest.JudgeText(judge.Name, judge.Kind));
        SenseJob? seen = null;
        fixture.Controller.SenseRunner = (kind, _, job, _) =>
        {
            Assert.Equal(SenseKind.Audio, kind);
            seen = job;
            return Task.FromResult(SenseAnswer.Done("Upbeat pop with singing"));
        };
        var clip = new float[3 * PcSoundBuffer.SampleRate];
        for (var i = 0; i < clip.Length; i++) clip[i] = (float)(0.2 * Math.Sin(i * 0.1));
        Assert.Equal("Upbeat pop with singing", await judge.DescribeAsync(clip, CancellationToken.None));
        Assert.Equal(SenseSoundJudge.Purpose, seen!.Purpose);
        Assert.Equal(SoundDigest.Prompt, seen.Text);
        Assert.Equal(TimeSpan.FromSeconds(3), seen.Audio!.Duration);
        // On the conversation's computer it waits while the live turn needs it; elsewhere it never does.
        fixture.Controller.SenseSharing = _ => true;
        Assert.False(fixture.Controller.SoundJudgeHeld());
        fixture.Controller.LiveFloor.Words("a test");
        Assert.True(fixture.Controller.SoundJudgeHeld());
        fixture.Controller.SenseSharing = _ => false;
        Assert.False(fixture.Controller.SoundJudgeHeld());
    }

    [Fact]
    public void Listening_words_name_the_audio_model_when_it_hears_you_instead_of_Thinking()
    {
        var thinking = new SetupRoute
        {
            RouteType = SetupRouteType.ChatCompletions, Role = SetupRole.Llm, ProviderAlias = ChatCompletionsSetup.Alias,
            Origin = "https://api.example.com/v1", ModelId = "qwen3-8b", ConfigurationRevision = Guid.NewGuid(), Enabled = true
        };
        var local = SenseRouting.For(SenseKind.Audio, WithEars(LocalEars), thinking, null);
        var cloud = SenseRouting.For(SenseKind.Audio, WithEars(CloudEars), thinking, null);
        Assert.Equal("On: your voice stays on this PC (the audio model runs here), so the audio model hears it unless you turn this off.",
            LiveConversationConfiguration.HearVoiceChoice(null, thinking, local));
        Assert.Equal("Off until you tick it: your recording would leave this PC for audio.example.com (qwen2-audio-7b-instruct).",
            LiveConversationConfiguration.HearVoiceChoice(null, thinking, cloud));
        Assert.Equal("On: you turned it on.", LiveConversationConfiguration.HearVoiceChoice(true, thinking, cloud));
        Assert.StartsWith("Your recording goes to Ollama on this PC (gemma3n:e4b), the audio model, which describes how you sound",
            LiveConversationConfiguration.HearingAdvice(thinking, null, local), StringComparison.Ordinal);
        var disclosure = LiveConversationConfiguration.HearingDisclosure(thinking, cloud);
        Assert.Contains("goes to audio.example.com (qwen2-audio-7b-instruct), the audio model", disclosure, StringComparison.Ordinal);
        Assert.Contains("never the recording", disclosure, StringComparison.Ordinal);
        // With the defaults (the text model takes recordings) the words are as before.
        var text = SenseRouting.For(SenseKind.Audio, new SenseModels(), thinking, null);
        Assert.Null(text.Model);
        Assert.Equal(LiveConversationConfiguration.HearVoiceChoice(null, thinking), LiveConversationConfiguration.HearVoiceChoice(null, thinking, text));
        Assert.Equal(LiveConversationConfiguration.HearingAdvice(thinking), LiveConversationConfiguration.HearingAdvice(thinking, null, text));
        Assert.Equal(LiveConversationConfiguration.HearingDisclosure(thinking), LiveConversationConfiguration.HearingDisclosure(thinking, text));
    }

    [Fact]
    public void The_audio_models_answer_becomes_a_summary_line_and_details_and_none_adds_nothing()
    {
        Assert.Null(VoiceNotes.Clean(null));
        Assert.Null(VoiceNotes.Clean(" none. "));
        Assert.Null(VoiceNotes.Clean("\"None\""));
        Assert.Equal(("Sighs and sounds tired.", (string?)null), VoiceNotes.Clean("Sighs and sounds tired.")!.Value);
        Assert.Equal(("Laughs.", "A TV plays in the background."), VoiceNotes.Clean("Summary: Laughs.\n- Details: A TV plays in the background.")!.Value);
        // The short context: what was said last, never what this PC played or what Martlet saw.
        var context = VoiceNotes.Context(
        [
            new(TextHistoryRole.User, "What's up?\n[PC audio] From a video: hello viewers\n[Screen] With this message you saw a game."),
            new(TextHistoryRole.Assistant, "Not much!")
        ], "Martlet");
        Assert.Equal("The conversation just before (for context only):\nUser: What's up?\nMartlet: Not much!\n\nThe recording is what the user said next.",
            context);
        Assert.Equal("The recording is the first thing the user says in this conversation.", VoiceNotes.Context([], "Martlet"));
    }

    private static void EnqueueUtterance(Martlet.Audio.Tests.ControlledCapture capture, int quietBefore, int speech, int quietAfter)
    {
        var sample = 0;
        void Packets(int count, double amplitude)
        {
            for (var p = 0; p < count; p++)
            {
                var packet = new byte[3200];
                for (var i = 0; i < 1600; i++, sample++)
                {
                    var t = sample / 16000.0;
                    var value = amplitude * Math.Sin(2 * Math.PI * 220 * t) * (0.7 + 0.3 * Math.Sin(2 * Math.PI * 4 * t));
                    System.Buffers.Binary.BinaryPrimitives.WriteInt16LittleEndian(packet.AsSpan(i * 2), (short)(value * 32767));
                }
                capture.Packets.Enqueue(packet);
            }
        }
        Packets(quietBefore, 0.001);
        Packets(speech, 0.25);
        Packets(quietAfter, 0.001);
    }

    private static void Click(Window window, string name) =>
        Assert.IsType<Button>(window.FindName(name)).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

    private static Task Loaded(LiveConversationWindow window) => LiveConversationTests.Until(() => window.IsReady);

    private static Task Heartbeat() =>
        Dispatcher.CurrentDispatcher.InvokeAsync(() => { }, DispatcherPriority.Background).Task.WaitAsync(TimeSpan.FromSeconds(2));

    private static async Task DispatcherTest(Func<Task> action)
    {
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            dispatcher.UnhandledException += (_, args) =>
            {
                args.Handled = true;
                finished.TrySetException(args.Exception);
                dispatcher.BeginInvokeShutdown(DispatcherPriority.Send);
            };
            dispatcher.BeginInvoke(async () =>
            {
                try
                {
                    await action();
                    finished.TrySetResult();
                }
                catch (Exception error) { finished.TrySetException(error); }
                finally { dispatcher.BeginInvokeShutdown(DispatcherPriority.Send); }
            });
            Dispatcher.Run();
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        await finished.Task.WaitAsync(TimeSpan.FromSeconds(25));
        Assert.True(thread.Join(TimeSpan.FromSeconds(5)));
    }
}
