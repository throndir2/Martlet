using System.Collections.Concurrent;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Martlet.Audio;
using Martlet.Audio.Tests;
using Martlet.Conversation;
using Martlet.Conversation.Tests;
using Martlet.Core.Contracts;
using Martlet.Core.Settings;
using Martlet.Credentials.Windows;
using Martlet.Desktop;
using Martlet.Memory;
using Martlet.Participation;
using Martlet.Providers;
using Martlet.Providers.Tests;

namespace Martlet.Desktop.Tests;

public sealed class LiveConversationTests
{
    [Fact]
    public async Task TriggeredLorebookEntriesStayInNotesWhenSpaceRunsOut()
    {
        await using var fixture = await LiveFixture.Create();
        var configuration = LiveConversationConfiguration.From(await fixture.Store.LoadAsync())!;
        var library = Martlet.Core.Lorebooks.LorebookLibrary.Create() with
        {
            Books =
            [
                new()
                {
                    Id = Guid.NewGuid(), Name = "World",
                    Entries =
                    [
                        new() { Uid = 0, Keys = ["castle"], Content = "The castle is Mab's.", Position = Martlet.Core.Lorebooks.LorebookPosition.BeforePersona },
                        new() { Uid = 1, Keys = ["castle"], Content = "{{char}} fears the castle." }
                    ]
                }
            ]
        };
        var lore = Martlet.Core.Lorebooks.LorebookScanner.Scan(library,
            new("Tell me about the castle", [], configuration.Persona?.Id, configuration.Persona?.Name), _ => 0);
        // About a million characters of earlier conversation: more than the default context size holds.
        var long_ = new string('a', 16_000);
        var history = Enumerable.Range(0, 60).Select(i => new TextHistoryMessage(i % 2 == 0 ? TextHistoryRole.User : TextHistoryRole.Assistant, long_))
            .ToArray();
        var request = configuration.Request(new("Tell me about the castle"), false, history, null, lore,
            out var usedHistory, out _, out var usedLore, closingInstructions: LiveConversationConfiguration.ReplyLengthInstructions);
        Assert.Equal(2, usedLore);
        Assert.True(usedHistory < history.Length);
        var instructions = request.Input.Personality!;
        Assert.Contains("Companion name:", instructions);
        Assert.DoesNotContain("Dominant style", instructions, StringComparison.Ordinal);
        Assert.EndsWith(LiveConversationConfiguration.ReplyLengthInstructions, instructions);
        Assert.DoesNotContain("[MARTLET_LOREBOOK]", instructions);
        var notes = request.Input.Notes!;
        var before = notes.IndexOf("The castle is Mab's.", StringComparison.Ordinal);
        var after = notes.IndexOf("fears the castle.", StringComparison.Ordinal);
        Assert.True(before >= 0 && before < after, notes);
        Assert.Contains("[MARTLET_LOREBOOK]", notes);
        Assert.DoesNotContain(LiveConversationConfiguration.ReplyLengthInstructions, notes);
        fixture.NoEffects();
    }
    [Fact]
    public async Task SchemaFiveDisabledOrSelfHostRouteCannotReachTheApiAdapter()
    {
        await using var fixture = await LiveFixture.Create();
        var loaded = await fixture.Store.LoadAsync();
        var settings = loaded.Settings!;
        var route = settings.Setup!.Routes.Single(item => item.Role == SetupRole.Llm);
        var disabled = SetupSettings.SetRouteEnabled(settings, SetupRole.Llm, false, true);
        var configuration = LiveConversationConfiguration.From(loaded with { Settings = disabled })!;
        Assert.Contains("Review Thinking in Companion", configuration.Unavailable(false, false));
        var selfHost = SetupSettings.ConfigureGatewayEndpoint(settings with
        {
            Setup = settings.Setup with { Routes = settings.Setup.Routes.Where(item => item.Role != SetupRole.Llm).ToArray() }
        }, SetupRouteType.GatewayOllama, new()
        {
            SchemaVersion = 1, Origin = "https://127.0.0.1:7443", HostId = "fixture-host",
            SpkiFingerprint = "sha256:" + new string('a', 64), DeviceRole = "voice"
        }, route.ModelId);
        configuration = LiveConversationConfiguration.From(loaded with { Settings = selfHost })!;
        Assert.Contains("This setup isn't available", configuration.Unavailable(false, false));

        // Handed to a paired host on the Devices page: pairing reference, route snapshot and recorded selection.
        var pairingCredential = HostPairingCredential.FromGuid(Guid.NewGuid());
        var paired = SetupSettings.ReplaceRoute(selfHost, selfHost.Setup!.Routes.Single(item => item.Role == SetupRole.Llm) with
        {
            CredentialId = HostPairingCredential.ToGuid(pairingCredential), GatewayDeviceId = "desktop-test"
        });
        paired = SetupSettings.ApplyGatewaySnapshot(paired, SetupRole.Llm, MainWindow.Snapshot(new(
            "martlet.gateway.ollama-chat.v1", "/martlet/v1/inference/ollama-chat", "ollama-native-chat-v034-text", "1.0",
            "ollama-host", "ollama-relay", "0.1.0", "llama3.2-3b", "ollama", new string('c', 64), "sha256:" + new string('d', 64),
            98_304, 16_384, 65_536, 65_536, 4_096, 4_194_304, TimeSpan.FromMinutes(15), "request_abort"), SetupRouteType.GatewayOllama));
        paired = SetupSettings.SetRouteEnabled(paired, SetupRole.Llm, true, true);
        configuration = LiveConversationConfiguration.From(loaded with { Settings = paired })!;
        Assert.Null(configuration.Unavailable(false, false));
        // A paired host is on the home network: it gets the local timing, so a loading or slow model isn't cut off at 45 s.
        Assert.True(configuration.NetworkThinking);
        Assert.Equal(TimeSpan.FromMinutes(2), configuration.TextLimits.MaxRequestTime);
        Assert.Equal(LiveConversationConfiguration.ActionLifetime,
            configuration.Request(new("Hi"), false, [], null, null, out _, out _, out _).Limits.TurnTimeout);
        // Host Ollama's num_predict also pays for thinking: the Chat Completions budget, under a small saved context size.
        Assert.Equal(GenerationSettings.ChatReplyTokens, configuration.TextLimits.MaxOutputTokens);
        Assert.Equal(1_024, LiveConversationConfiguration.From(loaded with
        {
            Settings = paired with { Generation = new() { ContextTokens = 2_048 } }
        })!.TextLimits.MaxOutputTokens);
        Assert.Contains("Maximum length is 4096 tokens, including any hidden thinking",
            MainWindow.DescribeGeneration(null, paired.Setup!.Routes.Single(r => r.Role == SetupRole.Llm)));
        Assert.Contains("your Martlet host fixture-host", configuration.Disclosure(false));
        var request = configuration.Request(new("Hello host"), false, [], null, null, out _, out _, out _);
        Assert.Equal(SelfHostSetup.GatewayOllamaAlias, request.Model.ModelAlias);
        Assert.Equal("llama3.2-3b", request.Model.UpstreamModelId);
        Assert.Equal(("fixture-host", "https://127.0.0.1:7443", pairingCredential),
            (request.Host!.HostId, request.Host.Origin, HostPairingCredential.FromGuid(request.Host.CredentialId)));
        // llama3.2:3b is text-only: screen watching is refused with a concrete fix (a vision model on the host).
        Assert.Equal(VisionSupport.Unsupported, configuration.Vision());
        Assert.Contains("vision-capable model for the host", configuration.VisionAdvice());
        var image = new BoundedImage([0xFF, 0xD8, 0xFF, .. new byte[32]], ImageMediaType.Jpeg, 4, 4);
        var glance = configuration.Request(new("(Screen glance.)"), false, [], null, null, out _, out _, out _, image,
            LiveConversationConfiguration.CommentaryInstructions(Chattiness.Normal), LiveConversationConfiguration.SilentReply);
        Assert.Same(image, glance.Input.Image);
        Assert.Equal("pass", glance.SilentReply);
        Assert.Contains("[pass]", glance.Input.Personality);
        Assert.Null(glance.Input.Notes);
        fixture.NoEffects();
    }

    [Fact]
    public async Task ListeningHandedToAPairedHostTranscribesThereAndSaysSo()
    {
        await using var fixture = await LiveFixture.Create();
        var loaded = await fixture.Store.LoadAsync();
        var pairing = Guid.NewGuid();
        var settings = HostHandoff.ToHost(loaded.Settings!, SetupRouteType.GatewayStt, new()
        {
            SchemaVersion = 1, Origin = "https://192.168.1.20:9443", HostId = "gpu-host",
            SpkiFingerprint = "sha256:" + new string('a', 64), DeviceRole = "voice"
        }, pairing, "desktop-test", MainWindow.Snapshot(new(
            Martlet.Avatar.Audio2Face.Remote.Audio2FaceHostConnection.TranscriptionRouteId,
            Martlet.Avatar.Audio2Face.Remote.Audio2FaceHostConnection.TranscriptionPath, "martlet.transcription-relay", "1.0",
            "stt-host", "whisper-relay", "1.0.0", "small", "whisper.cpp-1.9.4", new string('c', 64), "sha256:" + new string('d', 64),
            1_400_000, 960_000, 16_384, 16_384, 16, 262_144, TimeSpan.FromSeconds(60), "request_abort"), SetupRouteType.GatewayStt));
        var configuration = LiveConversationConfiguration.From(loaded with { Settings = settings })!;
        Assert.Null(configuration.Unavailable(false, true));
        Assert.Equal(("gpu-host", pairing), (configuration.SttHostTarget()!.HostId, configuration.SttHostTarget()!.CredentialId));
        Assert.Contains("your Martlet host gpu-host", configuration.Disclosure(false));
        Assert.Contains("doesn't store recordings", configuration.Disclosure(false));

        // The host adapter consumes the same one-use upload authorization, bound to the host's origin and model.
        var host = configuration.SttHostTarget()!;
        var context = new ProviderRequestContext
        {
            Ids = new() { SessionId = Guid.NewGuid(), TurnId = Guid.NewGuid(), RequestId = Guid.NewGuid() }, Epoch = 1,
            Deadline = DateTimeOffset.UtcNow.AddSeconds(30)
        };
        var permission = new AudioUploadAuthorization(HostTranscriptionAdapter.Binding(host, "small"), context.Ids, 1,
            LiveConversationConfiguration.TranscriptionLimits, DateTimeOffset.UtcNow.AddSeconds(30), true, true);
        var listener = new FakeListener("  hello from the host ");
        var adapter = new HostTranscriptionAdapter(listener);
        var audio = BoundedWaveAudio.FromPcm(new() { SampleRate = 16_000, Channels = 1, Encoding = Martlet.Core.Audio.PcmEncoding.Signed16LittleEndian },
            new byte[32_000]);
        var result = await adapter.TranscribeAsync(context, host, "small", audio, LiveConversationConfiguration.TranscriptionLimits,
            permission, CancellationToken.None);
        Assert.Equal((TranscriptionOutcome.Completed, "hello from the host"), (result.Outcome, result.Text));
        Assert.Equal(32_000, listener.Bytes);
        var replay = await adapter.TranscribeAsync(context, host, "small", audio, LiveConversationConfiguration.TranscriptionLimits,
            permission, CancellationToken.None);
        Assert.Equal(ProviderFailureCode.ConsentConsumed, replay.Failure!.Code);
        fixture.NoEffects();
    }

    private sealed class FakeListener(string text) : IHostTranscriptionClient
    {
        internal int Bytes { get; private set; }

        public Task<string> TranscribeAsync(HostTextTarget target, string modelId, ReadOnlyMemory<byte> pcm16kMono,
            CorrelationIds ids, long epoch, DateTimeOffset deadline, CancellationToken cancellationToken)
        {
            Bytes = pcm16kMono.Length;
            return Task.FromResult(text);
        }
    }

    [Fact]
    public Task OpeningWindowWithoutListeningRouteShowsWhyAndHasNoEffects() => DispatcherTest(async () =>
    {
        await using var fixture = await LiveFixture.Create();
        var settings = (await fixture.Store.LoadAsync()).Settings!;
        var stt = settings.Setup!.Routes.Single(r => r.Role == SetupRole.Stt);
        await fixture.Save(SetupSettings.ReplaceRoute(settings, stt with { Consent = null }));
        // Always listening is the default, but it can't start until listening is set up; the mic button says why.
        var window = fixture.Open(new TalkPreferences());
        try
        {
            await Loaded(window);
            Assert.False(Control<Button>(window, "SendButton").IsEnabled);
            Assert.Equal(Visibility.Collapsed, Control<Button>(window, "PttButton").Visibility);
            Assert.Equal(Visibility.Visible, Control<Button>(window, "MicChip").Visibility);
            Assert.Equal("Can't listen", Control<TextBlock>(window, "MicText").Text);
            Assert.Contains("Review Listening", (string)Control<Button>(window, "MicChip").ToolTip, StringComparison.Ordinal);
            Assert.Empty(window.Messages);
            Click(window, "SendButton"); // An empty message box sends nothing, even through a routed click.
            Click(window, "MicChip");
            await Heartbeat();
            fixture.NoEffects();
            Assert.False(Directory.Exists(Path.Combine(
                fixture.DirectoryPath, MemorySettings.AppLocalDirectoryName)));
        }
        finally { window.Close(); }
        var reopened = fixture.Open(new TalkPreferences());
        try { await Loaded(reopened); fixture.NoEffects(); }
        finally { reopened.Close(); }
    });

    [Fact]
    public Task AlwaysListeningStartsWithUntestedDefaultMicrophoneAndShowsTheSpokenExchange() => DispatcherTest(async () =>
    {
        await using var fixture = await LiveFixture.Create();
        // No microphone test: the chosen microphone is used as is.
        fixture.Answer("Heard you.");
        EnqueueUtterance(fixture.Capture, quietBefore: 5, speech: 25, quietAfter: 15);
        var window = fixture.Open(new TalkPreferences(SpeakReplies: false));
        try
        {
            await Loaded(window);
            // Nothing listens until Start listening is pressed.
            Assert.Null(window.Listener);
            Assert.Equal("Start listening", Control<TextBlock>(window, "MicText").Text);
            Assert.Equal(0, fixture.Capture.Opens);
            Click(window, "MicChip");
            await fixture.Advance(() => window.Messages.Any(m => m.Role == ChatRole.Martlet && m.Text.Contains("Heard you.", StringComparison.Ordinal)));
            var said = Assert.Single(window.Messages, m => m.IsUser);
            Assert.Contains("Synthetic fixture transcript.", said.Text);
            Assert.StartsWith("You (spoken)", said.Caption);
            Assert.Equal(1, fixture.Stt.Calls);
            Assert.Equal(1, fixture.Llm.Calls);
            Assert.Equal(0, fixture.Tts.Calls);
            // What always listening hears goes to the model, which may answer [pass] to stay quiet.
            Assert.Contains("[pass]", Encoding.UTF8.GetString(fixture.Llm.Body));
            // Listening ran beside the reply and goes on; Esc quiets Martlet but never pauses it: only the mic button does.
            var live = Assert.IsType<LiveListener>(window.Listener);
            Assert.True(live.Running);
            Assert.Equal("Stop listening", Control<TextBlock>(window, "MicText").Text);
            Escape(window, "InputText");
            await Heartbeat();
            Assert.Same(live, window.Listener);
            Assert.True(live.Running);
            Assert.Equal("Stop listening", Control<TextBlock>(window, "MicText").Text);
            Click(window, "MicChip");
            await fixture.Advance(() => !live.Running && !fixture.Runner.IsRunning);
            Assert.Null(window.Listener);
            Assert.Equal("Start listening", Control<TextBlock>(window, "MicText").Text);
            await Heartbeat();
            Assert.False(fixture.Runner.IsRunning);
            Assert.Equal(1, fixture.Stt.Calls);
        }
        finally { window.Close(); }
    });

    [Fact]
    public Task ListeningCarriesOnAfterAFailedReplyAndTheModelMayStayQuiet() => DispatcherTest(async () =>
    {
        await using var fixture = await LiveFixture.Create();
        var calls = 0;
        fixture.Llm.Respond = (_, _) => Task.FromResult(Interlocked.Increment(ref calls) == 1
            ? ProviderFixtures.Json("{}", 500) : TextRecordingHandler.Sse(Harness.Trace("[pass]")));
        EnqueueUtterance(fixture.Capture, quietBefore: 5, speech: 25, quietAfter: 15);
        var window = fixture.Open(new TalkPreferences(SpeakReplies: false));
        try
        {
            await Loaded(window);
            Click(window, "MicChip");
            await fixture.Advance(() => fixture.Llm.Calls == 1 && window.Current is { OwnershipReleased: true } &&
                Text(window, "ResultText").Contains("Couldn't reach the provider", StringComparison.Ordinal));
            // A failed reply never pauses listening.
            Assert.Equal("Stop listening", Control<TextBlock>(window, "MicText").Text);
            Assert.True(window.Listener is { Running: true });

            EnqueueUtterance(fixture.Capture, quietBefore: 5, speech: 25, quietAfter: 15);
            await fixture.Advance(() => fixture.Llm.Calls == 2 && window.Current is { OwnershipReleased: true, Passed: true } &&
                window.Messages.Any(m => m.Note == "Martlet stayed quiet."));
            Assert.Equal(2, fixture.Stt.Calls);
            Assert.Equal(2, window.Messages.Count(m => m.IsUser));
            // [pass] is never shown or spoken.
            Assert.DoesNotContain(window.Messages, m => m.Role == ChatRole.Martlet);
            Assert.Equal("Stop listening", Control<TextBlock>(window, "MicText").Text);
            Assert.True(window.Listener is { Running: true });
        }
        finally { window.Close(); }
    });

    [Fact]
    public Task AlwaysListeningStartsTheReplyEarlyAndShowsItOnlyOnceYourTurnEnds() => DispatcherTest(async () =>
    {
        // Parakeet on this PC (FIXTURE) and a judge that finds the pause unfinished: the reply starts at the quick transcript,
        // 260 ms into the pause, and the talk window takes it as the reply when the longer pause ends the turn.
        var quick = 0;
        var words = new FixtureWords(() => Interlocked.Increment(ref quick), "Shall we watch a film tonight");
        await using var fixture = await LiveFixture.Create(localListener: words, turnJudge: new FixtureJudge(TurnVerdict.Incomplete));
        var settings = (await fixture.Store.LoadAsync()).Settings!;
        var oldStt = settings.Setup!.Routes.Single(route => route.Role == SetupRole.Stt);
        settings = SetupSettings.QueueReplacedCredential(LocalSpeechSetup.SelectParakeet(settings, LocalSpeechSetup.Parakeet110mEnglishModelId), oldStt);
        var stt = settings.Setup!.Routes.Single(route => route.Role == SetupRole.Stt);
        settings = SetupSettings.ReplaceRoute(settings, stt with { Consent = stt.Selection() });
        var oldLlm = settings.Setup!.Routes.Single(route => route.Role == SetupRole.Llm);
        settings = SetupSettings.QueueReplacedCredential(ChatCompletionsSetup.SelectRoute(settings, "http://127.0.0.1:1234/v1", "llama3.2:3b"), oldLlm);
        var llm = settings.Setup!.Routes.Single(route => route.Role == SetupRole.Llm);
        await fixture.Save(SetupSettings.ReplaceRoute(settings, llm with { Consent = llm.Selection() }));
        fixture.Chat.Respond = (_, _) => Task.FromResult(TextRecordingHandler.Sse(
            "data: {\"id\":\"chat-fixture\",\"object\":\"chat.completion.chunk\",\"model\":\"server-model\",\"choices\":[{\"index\":0,\"delta\":{\"role\":\"assistant\",\"content\":\"Yes, let's.\"},\"finish_reason\":\"stop\"}]}\n\ndata: [DONE]\n\n"));
        EnqueueUtterance(fixture.Capture, quietBefore: 5, speech: 25, quietAfter: 3);
        var window = fixture.Open(new TalkPreferences(SpeakReplies: false));
        try
        {
            await Loaded(window);
            Click(window, "MicChip");
            await fixture.Advance(() => fixture.Chat.Calls == 1 && fixture.Controller.EarlyReply is { Turn.Snapshot.TextComplete: true });
            await Heartbeat();
            // Started, written, and still nothing in the talk window: your turn hasn't ended.
            Assert.DoesNotContain(window.Messages, m => m.Role == ChatRole.Martlet);
            Assert.DoesNotContain(window.Messages, m => m.IsUser);
            Assert.Equal(0, fixture.Controller.ContextTurns);
            EnqueueUtterance(fixture.Capture, quietBefore: 14, speech: 0, quietAfter: 0);
            await fixture.Advance(() => window.Messages.Any(m => m.Role == ChatRole.Martlet && m.Text == "Yes, let's.") &&
                window.Current is { OwnershipReleased: true });
            Assert.Equal("Shall we watch a film tonight", Assert.Single(window.Messages, m => m.IsUser).Text);
            // One request: the one started early became the reply.
            Assert.Equal(1, fixture.Chat.Calls);
            Assert.Equal(1, quick);
            Assert.Equal(EarlyReplyRecord.Promoted, Assert.Single(fixture.Controller.EarlyReplies).Outcome);
            Assert.Equal(1, fixture.Controller.ContextTurns);
            Assert.True(window.Listener is { Running: true });
        }
        finally { window.Close(); }
    });

    private sealed class FixtureWords(Func<int> counted, string text) : ILocalTranscriber
    {
        public Task<LocalTranscript> TranscribeAsync(string modelId, ReadOnlyMemory<byte> pcm16kMono, CancellationToken cancellationToken)
        {
            counted();
            return Task.FromResult(new LocalTranscript(text));
        }
    }

    private sealed class FixtureJudge(TurnVerdict verdict) : IEndOfTurnJudge
    {
        public string Name => "fixture judge";
        public bool Available => true;
        public Task<EndOfTurnJudgement> JudgeAsync(EndOfTurnRequest request, CancellationToken cancellationToken) =>
            Task.FromResult(new EndOfTurnJudgement(verdict));
    }

    [Fact]
    public Task TalkingOnBeforeMartletAnswersRestartsTheReplyWithEverythingSaid() => DispatcherTest(async () =>
    {
        await using var fixture = await LiveFixture.Create();
        var transcripts = new ConcurrentQueue<string>(["Getting there.", "What do you think?"]);
        fixture.Stt.Respond = (_, _) => Task.FromResult(ProviderFixtures.Json(
            JsonSerializer.Serialize(new { text = transcripts.TryDequeue(out var next) ? next : "Again." })));
        var bodies = new ConcurrentQueue<string>();
        fixture.Llm.Respond = async (_, token) =>
        {
            bodies.Enqueue(Encoding.UTF8.GetString(fixture.Llm.Body));
            if (bodies.Count == 1)
            {
                // Still thinking when you carry on talking: this reply is dropped and asked again with both utterances.
                EnqueueUtterance(fixture.Capture, quietBefore: 3, speech: 25, quietAfter: 15);
                await Task.Delay(Timeout.Infinite, token);
            }
            return TextRecordingHandler.Sse(Harness.Trace("Got all of it."));
        };
        EnqueueUtterance(fixture.Capture, quietBefore: 5, speech: 25, quietAfter: 15);
        var window = fixture.Open(new TalkPreferences(SpeakReplies: false));
        try
        {
            await Loaded(window);
            Click(window, "MicChip");
            await fixture.Advance(() => window.Messages.Any(m => m.Role == ChatRole.Martlet && m.Text == "Got all of it."));
            Assert.Equal(2, fixture.Stt.Calls);
            Assert.Equal(2, fixture.Llm.Calls);
            Assert.Contains("Getting there. What do you think?", bodies.Last(), StringComparison.Ordinal);
            Assert.Equal(["Getting there.", "What do you think?"], window.Messages.Where(m => m.IsUser).Select(m => m.Text));
            // The dropped reply left nothing behind; the one answer covers both.
            Assert.Single(window.Messages, m => m.Role == ChatRole.Martlet);
            Assert.True(window.Listener is { Running: true });
        }
        finally { window.Close(); }
    });

    // Straight to Thinking (a Thinking model that hears, the recording alone): the bubble shows the words once speech-to-text
    // beside the reply has them; when it couldn't transcribe it (no words, or it failed) the bubble goes and nothing replaces it.
    [Theory]
    [InlineData("Hello there.", 200)]
    [InlineData("", 200)]
    [InlineData("", 500)]
    public Task StraightToThinkingBubbleShowsTheWordsOrNothingWhenNotTranscribed(string transcript, int sttStatus) => DispatcherTest(async () =>
    {
        await using var fixture = await LiveFixture.Create();
        var loaded = await fixture.Store.LoadAsync();
        var old = loaded.Settings!.Setup!.Routes.Single(item => item.Role == SetupRole.Llm);
        var changed = SetupSettings.QueueReplacedCredential(
            ChatCompletionsSetup.SelectRoute(loaded.Settings!, "http://127.0.0.1:1234/v1", "voxtral-mini-latest"), old);
        var route = changed.Setup!.Routes.Single(item => item.Role == SetupRole.Llm);
        await fixture.Save(SetupSettings.ReplaceRoute(changed, route with { Consent = route.Selection() }));
        Assert.Equal(HearingSupport.Supported, fixture.Controller.Configuration!.Hearing());
        fixture.Stt.Respond = (_, _) => Task.FromResult(sttStatus == 200
            ? ProviderFixtures.Json(JsonSerializer.Serialize(new { text = transcript })) : ProviderFixtures.Json("{}", sttStatus));
        var bodies = new ConcurrentQueue<string>();
        fixture.Chat.Respond = (_, _) =>
        {
            bodies.Enqueue(Encoding.UTF8.GetString(fixture.Chat.Body));
            return Task.FromResult(TextRecordingHandler.Sse(
                "data: {\"id\":\"chat-fixture\",\"object\":\"chat.completion.chunk\",\"model\":\"server-model\",\"choices\":[{\"index\":0,\"delta\":{\"role\":\"assistant\",\"content\":\"Heard you.\"},\"finish_reason\":\"stop\"}]}\n\ndata: [DONE]\n\n"));
        };
        EnqueueUtterance(fixture.Capture, quietBefore: 5, speech: 25, quietAfter: 15);
        var window = fixture.Open(new TalkPreferences(SpeakReplies: false, HearVoice: true));
        try
        {
            await Loaded(window);
            Click(window, "MicChip");
            await fixture.Advance(() => fixture.Stt.Calls == 1 && window.Current is { OwnershipReleased: true } &&
                window.Messages.Any(m => m.Role == ChatRole.Martlet && m.Text == "Heard you.") &&
                !window.Messages.Any(m => m.IsUser && m.Text == LiveConversationWindow.StraightPlaceholder));
            await Heartbeat();
            // It went straight: the request carried the recording alone, never a transcript.
            var sent = Assert.Single(bodies);
            Assert.Contains("input_audio", sent, StringComparison.Ordinal);
            Assert.Contains(SpokenWords.StandIn, sent, StringComparison.Ordinal);
            if (transcript.Length > 0)
                Assert.Equal(transcript, Assert.Single(window.Messages, m => m.IsUser).Text);
            else
                Assert.DoesNotContain(window.Messages, m => m.IsUser);
            Assert.DoesNotContain(window.Messages, m => m.Text.Contains("couldn't transcribe", StringComparison.Ordinal));
            // What MCP's ui_snapshot sees: each bubble's kind, never its words.
            Assert.Equal(transcript.Length > 0 ? ["LiveMessage-You", "LiveMessage-Martlet"] : ["LiveMessage-Martlet"], BubbleIds(window));
            // Martlet's reply to what it heard stays.
            Assert.Single(window.Messages, m => m.Role == ChatRole.Martlet);
            Assert.True(window.Listener is { Running: true });
        }
        finally { window.Close(); }
    });

    [Theory]
    [InlineData("So I was thinking, um", true)]
    [InlineData("I went to the store and", true)]
    [InlineData("Wait,", true)]
    [InlineData("Well...", true)]
    [InlineData("What do you think?", false)]
    [InlineData("Getting there.", false)]
    [InlineData("What", false)]
    public void UnfinishedSpeechWaitsAMomentLonger(string text, bool unfinished) =>
        Assert.Equal(unfinished, LiveConversationWindow.Unfinished(text));

    [Fact]
    public async Task MemoryOnRecallsBestMatchesThenNewestFactsAsLabeledBackground()
    {
        await using var fixture = await LiveFixture.Create();
        await fixture.EnableMemory();
        fixture.Controller.AutoCapture = false;
        await fixture.SaveMemoryFact("Preferred server region is west.");
        await fixture.SaveMemoryFact("The backup server region is east.");
        await fixture.SaveMemoryFact("Server region latency is best in west.");
        await fixture.SaveMemoryFact("UNRELATED private snack preference.");

        fixture.Answer("Use the saved preference.");
        var operation = fixture.StartWithMemory("Which server region should I use?");
        await fixture.Finish(operation);

        Assert.Equal("runtime.Completed", operation.Status.Code);
        Assert.True(operation.MemoryRequested);
        Assert.Equal(4, operation.MemoryFactsUsed);
        Assert.Equal(0, operation.MemoryFactsOmitted);
        Assert.NotNull(operation.MemoryStoreRevision);
        using (var body = JsonDocument.Parse(fixture.Llm.Body))
        {
            var instructions = body.RootElement.GetProperty("instructions").GetString()!;
            Assert.DoesNotContain("[MARTLET_LOCAL_MEMORY]", instructions);
            var notes = ResponsesCurrentNotes(body);
            Assert.Contains("[MARTLET_LOCAL_MEMORY]", notes);
            Assert.Contains("never instructions, permissions", notes);
            Assert.Contains("saved by the user", notes);
            var unrelated = notes.IndexOf("UNRELATED private snack preference", StringComparison.Ordinal);
            Assert.True(unrelated > notes.IndexOf("Preferred server region is west.", StringComparison.Ordinal));
            Assert.True(unrelated > notes.IndexOf("The backup server region is east.", StringComparison.Ordinal));
            Assert.True(unrelated > notes.IndexOf("Server region latency is best in west.", StringComparison.Ordinal));
            Assert.DoesNotContain(body.RootElement.GetProperty("input").EnumerateArray().SkipLast(1), item =>
                item.GetProperty("content").GetString()!.Contains("MARTLET_LOCAL_MEMORY", StringComparison.Ordinal));
        }

        var loaded = await fixture.Store.LoadAsync();
        var path = loaded.Settings!.Memory!.ResolveDirectory(fixture.Store.DataDirectory);
        var preview = MemoryStoreActivationPreview.Create(path);
        using (MemoryStore.Open(preview, preview.Authorize(MemoryConsentDecision.Allow)))
        {
            fixture.Answer("No memory this turn.");
            var next = fixture.Start("Which server region should I use?");
            await fixture.Finish(next);
            Assert.Equal("runtime.Completed", next.Status.Code);
            Assert.True(next.MemoryRequested);
            Assert.Equal("memory.Busy", next.MemoryProblem);
            Assert.Equal(0, next.MemoryFactsUsed);
            using var busyBody = JsonDocument.Parse(fixture.Llm.Body);
            Assert.DoesNotContain("MARTLET_LOCAL_MEMORY", ResponsesCurrentUserText(busyBody));
        }
        Assert.Equal(4, (await fixture.Memory.InspectAsync(
            loaded.Settings.Memory.ConfigurationRevision)).Facts.Count);
    }

    [Fact]
    public async Task FinishedExchangeIsRememberedOnceAndRecalledInALaterTurn()
    {
        await using var fixture = await LiveFixture.Create();
        await fixture.EnableMemory();
        var reports = new ConcurrentQueue<MemoryCaptureReport>();
        fixture.Controller.MemoryCaptured += reports.Enqueue;
        var requests = new ConcurrentQueue<string>();
        fixture.Llm.Respond = (_, _) =>
        {
            var body = Decoded(fixture.Llm.Body);
            requests.Enqueue(body);
            return Task.FromResult(TextRecordingHandler.Sse(Harness.Trace(
                body.Contains("long-term memory", StringComparison.Ordinal)
                    ? "REMEMBER: The user's dog is called Biscuit."
                    : "Biscuit is a lovely name.")));
        };

        var first = fixture.Start("My dog is called Biscuit.");
        await fixture.Finish(first);
        await fixture.FinishRemembering();
        Assert.Equal("runtime.Completed", first.Status.Code);
        Assert.Equal(2, fixture.Llm.Calls);
        var change = Assert.Single(Assert.Single(reports).Changes!);
        Assert.Equal(MemoryCaptureKind.Remember, change.Kind);
        Assert.Equal("The user's dog is called Biscuit.", change.Content);
        var revision = (await fixture.Store.LoadAsync()).Settings!.Memory!.ConfigurationRevision;
        var fact = Assert.Single((await fixture.Memory.InspectAsync(revision)).Facts);
        Assert.Equal("The user's dog is called Biscuit.", fact.Content);
        Assert.Equal(MemorySourceKind.Conversation, fact.CreatedFrom.SourceKind);
        var capture = requests.ElementAt(1);
        Assert.Contains("User: My dog is called Biscuit.", capture);
        Assert.Contains("Martlet: Biscuit is a lovely name.", capture);

        var second = fixture.Start("What is my dog called?");
        await fixture.Finish(second);
        await fixture.FinishRemembering();
        Assert.Equal(4, fixture.Llm.Calls);
        Assert.Equal(1, second.MemoryFactsUsed);
        Assert.Contains("The user's dog is called Biscuit. (from conversation", requests.ElementAt(2));
        Assert.Contains("1. The user's dog is called Biscuit.", requests.ElementAt(3));
        Assert.Single(reports);
        Assert.Single((await fixture.Memory.InspectAsync(revision)).Facts);

        // The request JSON escapes apostrophes; compare the decoded instructions and messages.
        static string Decoded(byte[] body)
        {
            using var json = JsonDocument.Parse(body);
            return json.RootElement.GetProperty("instructions").GetString() + "\n" + string.Join("\n",
                json.RootElement.GetProperty("input").EnumerateArray().Select(item => item.GetProperty("content").GetString()));
        }
    }

    [Fact]
    public async Task MemoryOffNeitherRecallsNorRemembers()
    {
        await using var fixture = await LiveFixture.Create();
        fixture.Answer("Plain answer.");
        var operation = fixture.Start("My dog is called Biscuit.");
        await fixture.Finish(operation);
        await fixture.FinishRemembering();
        Assert.Equal("runtime.Completed", operation.Status.Code);
        Assert.False(operation.MemoryRequested);
        Assert.Equal(1, fixture.Llm.Calls);
        Assert.False(Directory.Exists(Path.Combine(fixture.DirectoryPath, MemorySettings.AppLocalDirectoryName)));
    }

    [Theory]
    [InlineData("REMEMBER: The user's sister is called Ana.\nNOTHING\nUPDATE 9: out of range", 1, 1)]
    [InlineData("**UPDATE 1:** The user now lives in Seattle.\n- FORGET 2", 2, 2)]
    [InlineData("NOTHING", 2, 0)]
    [InlineData("REMEMBER: ok\nREMEMBER: The user likes tea.\nREMEMBER: The user likes tea!", 0, 1)]
    public void CaptureAnswersAreParsedStrictly(string answer, int shown, int expected)
    {
        var operations = MemoryCapture.Parse(answer, shown);
        Assert.Equal(expected, operations.Count);
        Assert.All(operations, operation => Assert.True(operation.Index is null || operation.Index <= shown));
    }

    [Fact]
    public async Task RetrievalDropsLowerRankedFactsToStayInsideExistingRequestBudget()
    {
        await using var fixture = await LiveFixture.Create();
        // The smallest context size: room for the persona and one fact, not two.
        var loaded = await fixture.Store.LoadAsync();
        var persona = loaded.Settings!.Companion!.ActivePersona;
        await fixture.Save(loaded.Settings with
        {
            Generation = new() { ContextTokens = GenerationSettings.MinimumContextTokens },
            Companion = loaded.Settings.Companion.Update(persona.Id, persona.Name, new string('\u00e9', 2_000))
        });
        await fixture.EnableMemory();
        fixture.Controller.AutoCapture = false;
        for (var index = 0; index < 3; index++)
            await fixture.SaveMemoryFact($"server {index} " + new string('\u00e9', 4_000));

        fixture.Answer("Budgeted answer.");
        var operation = fixture.StartWithMemory("server");
        await fixture.Finish(operation);

        Assert.Equal("runtime.Completed", operation.Status.Code);
        Assert.Equal(1, operation.MemoryFactsUsed);
        Assert.Equal(2, operation.MemoryFactsOmitted);
        using var body = JsonDocument.Parse(fixture.Llm.Body);
        Assert.Single(ResponsesCurrentNotes(body).Split('\n'),
            line => line.StartsWith("- server ", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LongTypedAndPttInputsUseABoundedLexicalQuery(bool microphone)
    {
        await using var fixture = await LiveFixture.Create();
        await fixture.EnableMemory();
        fixture.Controller.AutoCapture = false;
        await fixture.SaveMemoryFact("server region is west");
        var input = "server region " +
            string.Join(' ', Enumerable.Range(0, 40).Select(index => $"term{index}")) +
            " " + new string('\u00e9', 3_600);
        fixture.Answer("Bounded query answer.");
        LiveConversationOperation operation;
        if (microphone)
        {
            fixture.Capture.Packets.Enqueue(new byte[3_200]);
            fixture.Stt.Respond = (_, _) => Task.FromResult(
                ProviderFixtures.Json(JsonSerializer.Serialize(new { text = input })));
            operation = fixture.Controller.Start(null, voice: false, microphone: true,
                approved: true, localCaptureApproved: true, uploadApproved: true,
                caller: default);
            await Until(() => operation.Capture?.Snapshot.CanonicalSamples > 0);
            operation.ReleasePress();
        }
        else
        {
            operation = fixture.StartWithMemory(input);
        }

        await fixture.Finish(operation);

        Assert.Equal("runtime.Completed", operation.Status.Code);
        Assert.Equal(1, operation.MemoryFactsUsed);
        Assert.Equal(0, operation.MemoryFactsOmitted);
        Assert.Equal(1, fixture.Llm.Calls);
        Assert.Contains("[MARTLET_LOCAL_MEMORY]",
            Encoding.UTF8.GetString(fixture.Llm.Body));
    }

    [Fact]
    public async Task OmittingEveryFactAlsoOmitsMemoryOnlyInstructions()
    {
        await using var fixture = await LiveFixture.Create();
        var loaded = await fixture.Store.LoadAsync();
        var persona = loaded.Settings!.Companion!.ActivePersona;
        var changed = loaded.Settings with
        {
            Generation = new() { ContextTokens = GenerationSettings.MinimumContextTokens },
            Companion = loaded.Settings.Companion.Update(
                persona.Id, persona.Name, new string('\u00e9', 7_800))
        };
        await fixture.Save(changed);
        await fixture.EnableMemory();
        fixture.Controller.AutoCapture = false;
        await fixture.SaveMemoryFact("server " + new string('\u00e9', 4_000));
        fixture.Answer("Near-budget answer.");

        var operation = fixture.StartWithMemory("server");
        await fixture.Finish(operation);

        Assert.Equal("runtime.Completed", operation.Status.Code);
        Assert.Equal(0, operation.MemoryFactsUsed);
        Assert.Equal(1, operation.MemoryFactsOmitted);
        using var request = JsonDocument.Parse(fixture.Llm.Body);
        Assert.DoesNotContain("MARTLET_LOCAL_MEMORY",
            request.RootElement.GetRawText());
        Assert.Contains(new string('\u00e9', 100),
            request.RootElement.GetProperty("instructions").GetString());
    }

    [Fact]
    public async Task MessagesFromPairedChatsAreAnsweredWhileLockedButNeverAloud()
    {
        await using var fixture = await LiveFixture.Create();
        fixture.Controller.SetSessionLocked(true);
        Assert.Equal("conversation.controls_blocked",
            Assert.Throws<LiveActionException>(() => fixture.Controller.Start("hi", voice: false, microphone: false, approved: true)).Code);
        Assert.Equal("conversation.controls_blocked",
            Assert.Throws<LiveActionException>(() => fixture.Controller.Start("hi", voice: true, microphone: false, approved: true, remote: true)).Code);
        Assert.Equal("conversation.invalid_input",
            Assert.Throws<LiveActionException>(() => fixture.Controller.Start(null, voice: false, microphone: true, approved: true,
                localCaptureApproved: true, uploadApproved: true, remote: true)).Code);
        fixture.Answer("Hello from the PC.");

        var operation = fixture.Controller.Start("hi", voice: false, microphone: false, approved: true, remote: true);
        await fixture.Finish(operation);

        Assert.Equal("runtime.Completed", operation.Status.Code);
        Assert.Equal("Hello from the PC.", operation.Turn!.Content.Text);
        Assert.Equal(1, fixture.Llm.Calls);
    }

    [Theory]
    [InlineData("consent")]
    [InlineData("pause")]
    [InlineData("lock")]
    [InlineData("stop")]
    [InlineData("close")]
    [InlineData("configuration")]
    public async Task LifecycleChangesInvalidateInFlightMemoryBeforeProviderDispatch(string action)
    {
        await using var fixture = await LiveFixture.Create();
        await fixture.EnableMemory();
        await fixture.SaveMemoryFact("server region is west");
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Memory.TestHook = (_, _) =>
        {
            entered.TrySetResult();
            release.Wait();
        };

        var operation = fixture.StartWithMemory("server region");
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        switch (action)
        {
            case "consent":
                fixture.Controller.Stop(operation, "conversation.revoked");
                break;
            case "pause":
                fixture.Controller.SetControls(pause: true, mute: false, sessionLocked: false);
                break;
            case "lock":
                fixture.Controller.SetSessionLocked(true);
                break;
            case "stop":
                fixture.Controller.Stop(operation);
                break;
            case "close":
                fixture.Controller.Revoke("conversation.closed");
                break;
            case "configuration":
                var loaded = await fixture.Store.LoadAsync();
                var saved = await fixture.Memory.SaveConfigurationAsync(
                    loaded.Settings!, loaded.Revision, enabled: false,
                    policy: loaded.Settings!.Memory!.StoragePolicy,
                    customDirectory: loaded.Settings.Memory.CustomDirectory);
                Assert.True(saved.Save.Save.Saved);
                break;
        }
        release.Set();
        await fixture.Finish(operation);

        Assert.Equal(0, fixture.Llm.Calls);
        Assert.True(operation.Status.Finished);
        Assert.DoesNotContain("runtime.Completed", operation.Timeline);
    }

    [Fact]
    public async Task DeleteDuringRecallRereadsCurrentFactsSoTheDeletedFactIsNeverSent()
    {
        await using var fixture = await LiveFixture.Create();
        await fixture.EnableMemory();
        fixture.Controller.AutoCapture = false;
        await fixture.SaveMemoryFact("server region is west");
        var loaded = await fixture.Store.LoadAsync();
        var configurationRevision = loaded.Settings!.Memory!.ConfigurationRevision;
        var fact = Assert.Single((await fixture.Memory.InspectAsync(configurationRevision)).Facts);
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Memory.TestHook = (_, _) =>
        {
            entered.TrySetResult();
            release.Wait();
        };

        fixture.Answer("Answer without the deleted fact.");
        var operation = fixture.StartWithMemory("server region");
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await fixture.Memory.DeleteFactAsync(configurationRevision, fact);
        release.Set();
        await fixture.Finish(operation);

        Assert.Equal("runtime.Completed", operation.Status.Code);
        Assert.Equal(0, operation.MemoryFactsUsed);
        Assert.Equal(1, fixture.Llm.Calls);
        Assert.DoesNotContain("server region is west", Encoding.UTF8.GetString(fixture.Llm.Body));
        Assert.Empty((await fixture.Memory.InspectAsync(configurationRevision)).Facts);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task TypedWindowRunsActualBridgePolicyParsersRuntimeAndSelectedSink(bool voice) => DispatcherTest(async () =>
    {
        await using var fixture = await LiveFixture.Create();
        fixture.Answer("Hello there. ", "Second sentence.");
        var window = fixture.Open(new TalkPreferences(HandsFree: false, SpeakReplies: voice));
        try
        {
            await Loaded(window);
            Control<TextBox>(window, "InputText").Text = "typed-content-canary";
            Assert.True(Control<Button>(window, "SendButton").IsEnabled);
            Click(window, "SendButton");
            Assert.Equal("", Control<TextBox>(window, "InputText").Text);
            await fixture.Finish();
            await Until(() => window.Current is { OwnershipReleased: true });
            await Until(() => window.Messages.Any(m => m.Role == ChatRole.Martlet && m.Text == "Hello there. Second sentence."));
            Assert.Equal("typed-content-canary", Assert.Single(window.Messages, m => m.IsUser).Text);
            Assert.False(Assert.Single(window.Messages, m => m.Role == ChatRole.Martlet).HasNote);
            Assert.Equal(1, fixture.Llm.Calls);
            Assert.Equal(voice ? 2 : 0, fixture.Tts.Calls);
            Assert.Equal(voice ? 2 : 0, fixture.Output.Opens);
            Assert.Equal(0, fixture.Capture.Opens);
            Assert.Equal(0, fixture.Stt.Calls);
            using var body = JsonDocument.Parse(fixture.Llm.Body);
            Assert.Equal(TextFixtures.Model, body.RootElement.GetProperty("model").GetString());
            Assert.Equal(GenerationSettings.DefaultMaxReplyTokens, body.RootElement.GetProperty("max_output_tokens").GetInt32());
            Assert.False(body.RootElement.GetProperty("store").GetBoolean());
            Assert.Contains("typed-content-canary", Encoding.UTF8.GetString(fixture.Llm.Body));
            Assert.Contains("Be a helpful conversational companion.", body.RootElement.GetProperty("instructions").GetString());
            Assert.DoesNotContain("Dominant style", Encoding.UTF8.GetString(fixture.Llm.Body), StringComparison.Ordinal);
            Assert.DoesNotContain("MARTLET_NOTES", ResponsesCurrentUserText(body));
            Assert.Equal(voice ? 3 : 1, fixture.Native.Targets.Count);
            Assert.All(fixture.Native.Leases, lease => Assert.Throws<ObjectDisposedException>(() => lease.Use(_ => { })));
            Assert.All(fixture.Native.Threads, thread => Assert.NotEqual(Environment.CurrentManagedThreadId, thread));
            Assert.Contains("/openai-llm/", fixture.Native.Targets.First());
            if (voice)
            {
                using var speech = JsonDocument.Parse(fixture.Tts.Body);
                Assert.Equal("coral", speech.RootElement.GetProperty("voice").GetString());
                Assert.Equal("gpt-4o-mini-tts-2025-12-15", speech.RootElement.GetProperty("model").GetString());
                Assert.Equal(OutputPolicy.FixedEndpoint, fixture.Output.Selection!.Policy);
                Assert.Equal("private-output-id", fixture.Output.Selection.EndpointId);
                Assert.All(fixture.Native.Targets.Skip(1), target => Assert.Contains("/openai-tts/", target));
                Assert.NotEmpty(fixture.Output.Bytes);
            }
            Assert.Null(fixture.Controller.PolicySnapshot.ActiveIntentId);
            Assert.DoesNotContain(LiveFixture.Secret, Text(window, "ResultText") + string.Concat(window.Messages.Select(m => m.Text + m.Note)));
            Assert.DoesNotContain(LiveFixture.Secret, await File.ReadAllTextAsync(fixture.Store.FilePath));
        }
        finally { window.Close(); }
    });

    [Fact]
    public async Task FreshActionSnapshotsPersonaRevisionWithoutAResponseStyle()
    {
        await using var fixture = await LiveFixture.Create();
        var loaded = await fixture.Store.LoadAsync();
        var persona = loaded.Settings!.Companion!.ActivePersona;
        var changed = loaded.Settings with
        {
            Companion = loaded.Settings.Companion.Update(
                persona.Id, "Corvid", "Prefer concise companion replies.")
        };
        await fixture.Save(changed);

        var operation = fixture.Start();
        await fixture.Finish(operation);

        Assert.Equal(changed.Companion!.ActivePersona.ConfigurationRevision, operation.PersonaRevision);
        using var body = JsonDocument.Parse(fixture.Llm.Body);
        var instructions = body.RootElement.GetProperty("instructions").GetString();
        Assert.Contains("Companion name: Corvid", instructions);
        Assert.Contains("Prefer concise companion replies.", instructions);
        // Only the persona says how Martlet talks: no response style is picked or sent, in the instructions or the notes.
        Assert.DoesNotContain("Dominant style", Encoding.UTF8.GetString(fixture.Llm.Body), StringComparison.Ordinal);
    }

    [Fact]
    public async Task SavedCompatibleLlmModelSwitchRebindsFreshAuthorizationAndRequest()
    {
        await using var fixture = await LiveFixture.Create();
        const string model = "gpt-4.1-2025-04-14";
        const string answer = "Switched model fixture response.";
        fixture.Llm.Respond = (_, _) => Task.FromResult(TextRecordingHandler.Sse(
            Harness.Trace(answer).Replace(TextFixtures.Model, model, StringComparison.Ordinal)));
        var loaded = await fixture.Store.LoadAsync();
        var changed = SetupSettings.SelectRoute(
            loaded.Settings!, SetupRole.Llm, model, null);
        var route = changed.Setup!.Routes.Single(item => item.Role == SetupRole.Llm);
        changed = SetupSettings.ReplaceRoute(changed, route with { Consent = route.Selection() });
        await fixture.Save(changed);

        var operation = fixture.Start();
        await fixture.Finish(operation);

        Assert.Equal("runtime.Completed", operation.Status.Code);
        Assert.Equal(answer, operation.Turn!.Content.Text);
        using var body = JsonDocument.Parse(fixture.Llm.Body);
        Assert.Equal(model, body.RootElement.GetProperty("model").GetString());
        Assert.Contains("Be a helpful conversational companion.",
            body.RootElement.GetProperty("instructions").GetString());
        Assert.Contains("/openai-llm/", Assert.Single(fixture.Native.Targets));
    }

    [Theory]
    [InlineData(ChatCompletionsEndpointCatalog.OpenRouterBaseUrl, "meta-llama/llama-3.3-70b-instruct:free", true)]
    [InlineData(ChatCompletionsEndpointCatalog.NvidiaBuildBaseUrl, "google/diffusiongemma-26b-a4b-it", true)]
    [InlineData("https://api.groq.com/openai/v1", "llama-3.3-70b-versatile", true)]
    [InlineData("http://127.0.0.1:1234/v1", "local-model", false)]
    public async Task ChatCompletionsLlmRouteReachesItsExactEndpointWithScopedKey(string baseUrl, string model, bool keyed)
    {
        await using var fixture = await LiveFixture.Create();
        const string answer = "Chat Completions fixture response.";
        string? authorization = null;
        Uri? target = null;
        fixture.Chat.Inspect = request =>
        {
            authorization = request.Headers.Authorization?.ToString();
            target = request.RequestUri;
        };
        fixture.Chat.Respond = (_, _) => Task.FromResult(TextRecordingHandler.Sse(
            "data: {\"id\":\"chat-fixture\",\"object\":\"chat.completion.chunk\",\"model\":\"server-model\",\"choices\":[{\"index\":0,\"delta\":{\"role\":\"assistant\",\"content\":\"" +
            answer + "\"},\"finish_reason\":\"stop\"}]}\n\ndata: [DONE]\n\n"));
        var loaded = await fixture.Store.LoadAsync();
        var old = loaded.Settings!.Setup!.Routes.Single(item => item.Role == SetupRole.Llm);
        var changed = SetupSettings.QueueReplacedCredential(
            ChatCompletionsSetup.SelectRoute(loaded.Settings!, baseUrl, model), old);
        Assert.Contains(changed.Setup!.PendingRemovals, item => item.CredentialId == old.CredentialId);
        var route = changed.Setup.Routes.Single(item => item.Role == SetupRole.Llm);
        if (keyed) route = route.WithCredential(Guid.NewGuid());
        changed = SetupSettings.ReplaceRoute(changed, route with { Consent = route.Selection() });
        await fixture.Save(changed);
        Assert.Null(fixture.Controller.Configuration!.Unavailable(false, false));
        Assert.Contains(baseUrl, fixture.Controller.Configuration.Disclosure(false));

        var operation = fixture.Start();
        await fixture.Finish(operation);

        Assert.Equal("runtime.Completed", operation.Status.Code);
        Assert.Equal(answer, operation.Turn!.Content.Text);
        Assert.Equal(0, fixture.Llm.Calls);
        Assert.Equal(1, fixture.Chat.Calls);
        Assert.Equal(baseUrl + "/chat/completions", target!.AbsoluteUri);
        Assert.Equal(keyed ? "Bearer " + LiveFixture.Secret : null, authorization);
        if (keyed) Assert.Contains("/chat-completions/", Assert.Single(fixture.Native.Targets));
        else Assert.Empty(fixture.Native.Targets);
        using var body = JsonDocument.Parse(fixture.Chat.Body);
        Assert.Equal(model, body.RootElement.GetProperty("model").GetString());
        Assert.Equal("system", body.RootElement.GetProperty("messages")[0].GetProperty("role").GetString());
    }

    [Fact]
    public async Task NamedChatCompletionsEndpointRequiresAKey()
    {
        await using var fixture = await LiveFixture.Create();
        var loaded = await fixture.Store.LoadAsync();
        var old = loaded.Settings!.Setup!.Routes.Single(item => item.Role == SetupRole.Llm);
        var changed = SetupSettings.QueueReplacedCredential(ChatCompletionsSetup.SelectRoute(
            loaded.Settings!, ChatCompletionsEndpointCatalog.NvidiaBuildBaseUrl, "google/diffusiongemma-26b-a4b-it"), old);
        var route = changed.Setup!.Routes.Single(item => item.Role == SetupRole.Llm);
        changed = SetupSettings.ReplaceRoute(changed, route with { Consent = route.Selection() });
        await fixture.Save(changed);
        Assert.Contains("NVIDIA Build API key", fixture.Controller.Configuration!.Unavailable(false, false));
        fixture.NoEffects();
    }

    [Fact]
    public async Task RetiredNvidiaModelIsRefusedWithTheVisionDefaultAsTheFix()
    {
        await using var fixture = await LiveFixture.Create();
        var loaded = await fixture.Store.LoadAsync();
        var old = loaded.Settings!.Setup!.Routes.Single(item => item.Role == SetupRole.Llm);
        var changed = SetupSettings.QueueReplacedCredential(ChatCompletionsSetup.SelectRoute(
            loaded.Settings!, ChatCompletionsEndpointCatalog.NvidiaBuildBaseUrl, "meta/llama-3.3-70b-instruct"), old);
        var route = changed.Setup!.Routes.Single(item => item.Role == SetupRole.Llm).WithCredential(Guid.NewGuid());
        changed = SetupSettings.ReplaceRoute(changed, route with { Consent = route.Selection() });
        await fixture.Save(changed);
        var configuration = fixture.Controller.Configuration!;
        Assert.Contains("retired this Thinking model", configuration.Unavailable(false, false));
        Assert.Equal(VisionSupport.Unsupported, configuration.Vision());
        Assert.Contains("google/diffusiongemma-26b-a4b-it", configuration.VisionAdvice());
        fixture.NoEffects();
    }

    [Fact]
    public async Task CompletedExplicitTurnsSupplyBoundedHistoryAndPauseClearsIt()
    {
        await using var fixture = await LiveFixture.Create();
        fixture.Answer("First answer.");
        await fixture.Finish(fixture.Start("First question."));
        Assert.Equal(1, fixture.Controller.ContextTurns);

        fixture.Answer("Second answer.");
        var second = fixture.Start("Second question.");
        await fixture.Finish(second);
        Assert.Equal(2, second.ContextMessages);
        Assert.Equal(0, second.ContextMessagesOmitted);
        using (var body = JsonDocument.Parse(fixture.Llm.Body))
        {
            var input = body.RootElement.GetProperty("input");
            Assert.Equal(3, input.GetArrayLength());
            Assert.Equal("First question.", input[0].GetProperty("content").GetString());
            Assert.Equal("First answer.", input[1].GetProperty("content").GetString());
            Assert.Equal("Second question.", input[2].GetProperty("content").GetString());
        }
        Assert.Equal(2, fixture.Controller.ContextTurns);

        // Saving settings continues the conversation rather than starting a fresh one.
        var loaded = await fixture.Store.LoadAsync();
        await fixture.Save(loaded.Settings!);
        Assert.Equal(2, fixture.Controller.ContextTurns);

        fixture.Controller.Stop(second, "conversation.closed");
        Assert.Equal(0, fixture.Controller.ContextTurns);
        fixture.Answer("Before pause.");
        await fixture.Finish(fixture.Start("Another question."));
        Assert.Equal(1, fixture.Controller.ContextTurns);
        fixture.Controller.SetControls(pause: true, mute: false, sessionLocked: false);
        fixture.Controller.SetControls(pause: false, mute: false, sessionLocked: false);
        Assert.Equal(0, fixture.Controller.ContextTurns);
        fixture.Answer("After pause.");
        var afterPause = fixture.Start("Fresh question.");
        await fixture.Finish(afterPause);
        Assert.Equal(0, afterPause.ContextMessages);
        using var freshBody = JsonDocument.Parse(fixture.Llm.Body);
        Assert.Single(freshBody.RootElement.GetProperty("input").EnumerateArray());
    }

    [Fact]
    public void ConversationContextIsCountAndByteBoundedNotTimed()
    {
        var context = new ConversationContextBuffer();
        for (var index = 0; index < ConversationContextBuffer.MaximumTurns + 1; index++)
            context.Add($"Question {index}", $"Answer {index}");
        Assert.Equal(ConversationContextBuffer.MaximumTurns, context.Count);
        Assert.DoesNotContain(context.Snapshot(), item => item.Text == "Question 0");

        context.Clear();
        context.Add(new string('u', 9_000), new string('a', 9_000));
        Assert.Equal(1, context.Count);
        Assert.Equal(18_000, context.Utf8Bytes);

        var huge = new string('h', ConversationContextBuffer.MaximumUtf8Bytes / 2 + 1);
        context.Add(huge, huge);
        Assert.Equal(0, context.Count);
    }

    [Fact]
    public void PassedLooksInARowKeepOnlyTheLastAndVisionLinesAreNeverTheUsersWords()
    {
        var context = new ConversationContextBuffer();
        context.Add("Hi.", "Hello!");
        Assert.False(context.AddLook(VisionHistory.Look(false, "the user's active window \"Game\"", null, "a menu"), "[pass]", passed: true));
        Assert.True(context.AddLook(VisionHistory.Look(false, "the user's active window \"Game\"", null, "a boss fight"), "[pass]", passed: true));
        Assert.Equal(2, context.Count);
        Assert.False(context.AddLook(VisionHistory.Look(true, "the user's camera \"Desk\"", "a notification just popped up", null),
            "Ooh, a cat!", passed: false));
        // A remark ends the quiet stretch: the next pass is kept beside it.
        Assert.False(context.AddLook(VisionHistory.Look(false, "the user's whole screen", null, null), "[pass]", passed: true));
        Assert.Equal(
        [
            "Hi.", "Hello!",
            "[Screen] You looked at the user's active window \"Game\": a boss fight.", "[pass]",
            "[Camera] You looked at the user's camera \"Desk\" (a notification just popped up).", "Ooh, a cat!",
            "[Screen] You looked at the user's whole screen.", "[pass]"
        ], context.Snapshot().Select(message => message.Text));

        Assert.Equal("What's this?", LiveConversationConfiguration.WithoutMarked(
            VisionHistory.After("What's this?", VisionHistory.WithMessage(false, "the user's whole screen", "a chart"))));
        Assert.Null(LiveConversationConfiguration.WithoutMarked("[PC audio] And now the weather.\n[Camera] You looked at the user's camera."));
        Assert.Equal("Mine.", LiveConversationConfiguration.WithoutMarked("[PC audio] Theirs.\nMine.\n[Screen] With this message you saw x."));
    }

    [Fact]
    public async Task EveryLookAndEveryPictureWithAMessageStayInTheConversationAsWhatWasSeen()
    {
        await using var fixture = await LiveFixture.Create();
        var image = new BoundedImage([0xFF, 0xD8, 0xFF, .. new byte[32]], ImageMediaType.Jpeg, 4, 4);
        var screen = new WatchSource(WatchKind.ActiveWindow);
        var seenPrompt = SeenTags.Instructions(null, LiveConversationConfiguration.SilentReply)!;
        static string[] Earlier(JsonDocument body) => [.. body.RootElement.GetProperty("input").EnumerateArray().SkipLast(1)
            .Select(item => item.GetProperty("content").GetString()!)];
        LiveConversationOperation Glance() => fixture.Controller.StartCommentary(image, "Program.cs - Code", ChattinessChoice.Normal,
            voice: false, screenApproved: true, source: screen);

        // A passed look is kept too: where Martlet looked and what it saw (the [seen: ...] words, never shown).
        fixture.Answer("[pass] ", "[seen: a code ", "editor]");
        var first = Glance();
        await fixture.Finish(first);
        Assert.True(first.Passed);
        Assert.Equal("[pass]", first.Turn!.Content.Text.Trim());
        Assert.Equal(["[seen: a code editor]"], first.Turn.Controls);
        using (var body = JsonDocument.Parse(fixture.Llm.Body))
            Assert.Contains(seenPrompt, body.RootElement.GetProperty("instructions").GetString());

        // Passes in a row keep only the last; a remark is kept with what was seen, its tag never shown.
        fixture.Answer("[pass] [seen: a build running]");
        await fixture.Finish(Glance());
        fixture.Answer("Green build, nice! ", "[seen: a green build result]");
        var remark = Glance();
        await fixture.Finish(remark);
        Assert.False(remark.Passed);
        Assert.Equal("Green build, nice!", remark.Turn!.Content.Text.Trim());

        // A message with a picture: its request carries the looks, and it is told to say what it saw.
        fixture.Answer("Looks tidy. [seen: Program.cs with a Main method]");
        var asked = fixture.Controller.Start("What do you think?", voice: false, microphone: false, approved: true,
            seen: new SeenScreen(image, "Program.cs - Code", screen));
        await fixture.Finish(asked);
        Assert.True(asked.ScreenSent);
        Assert.Equal("Looks tidy.", asked.Turn!.Content.Text.Trim());
        using (var body = JsonDocument.Parse(fixture.Llm.Body))
        {
            Assert.Contains(seenPrompt, body.RootElement.GetProperty("instructions").GetString());
            Assert.Equal(
            [
                "[Screen] You looked at the user's active window \"Program.cs - Code\": a build running.", "[pass]",
                "[Screen] You looked at the user's active window \"Program.cs - Code\": a green build result.", "Green build, nice!"
            ], Earlier(body));
        }

        // The next request keeps the message with a line saying what came with it; the picture itself is never kept.
        fixture.Answer("Sure.");
        await fixture.Finish(fixture.Controller.Start("Thanks.", voice: false, microphone: false, approved: true));
        using (var body = JsonDocument.Parse(fixture.Llm.Body))
        {
            var earlier = Earlier(body);
            Assert.Equal(6, earlier.Length);
            Assert.StartsWith("What do you think?", earlier[4]);
            Assert.EndsWith("\n[Screen] With this message you saw the user's active window \"Program.cs - Code\": Program.cs with a Main method.",
                earlier[4]);
            Assert.Equal("Looks tidy.", earlier[5]);
            Assert.DoesNotContain("input_image", body.RootElement.GetRawText());
            // Without a picture the reply isn't told about the seen tag, so the instructions are as before.
            Assert.DoesNotContain(seenPrompt, body.RootElement.GetProperty("instructions").GetString());
        }
    }

    [Fact]
    public async Task LooksAndPicturesNameTheAppInFrontWithoutChangingTheInstructions()
    {
        await using var fixture = await LiveFixture.Create();
        var image = new BoundedImage([0xFF, 0xD8, 0xFF, .. new byte[32]], ImageMediaType.Jpeg, 4, 4);
        var whole = new WatchSource(WatchKind.ActiveScreen);
        static string[] Users(JsonDocument body) => [.. body.RootElement.GetProperty("input").EnumerateArray()
            .Where(item => item.GetProperty("role").GetString() == "user")
            .Select(item => item.GetProperty("content") is { ValueKind: JsonValueKind.String } text ? text.GetString()!
                : item.GetProperty("content").EnumerateArray().Single(part => part.GetProperty("type").GetString() == "input_text")
                    .GetProperty("text").GetString()!)];

        // A look's message names the program in front and says it is full screen; the conversation keeps that with the look.
        fixture.Answer("[pass] [seen: a boss fight]");
        var look = fixture.Controller.StartCommentary(image, "ELDEN RING", ChattinessChoice.Normal, voice: false, screenApproved: true,
            source: whole, app: "ELDEN RING", fullScreen: true);
        await fixture.Finish(look);
        using (var body = JsonDocument.Parse(fixture.Llm.Body))
            Assert.StartsWith("(Screen glance. Active app: ELDEN RING (full screen). Active window: \"ELDEN RING\".",
                ResponsesCurrentUserText(body));

        // Messages with pictures of different windows: the instructions stay the same (so the prompt cache keeps them), and the
        // program in front and the window's title go after the words, in notes the conversation doesn't keep.
        fixture.Answer("Cute! [seen: a cat video]");
        await fixture.Finish(fixture.Controller.Start("What's this?", voice: false, microphone: false, approved: true,
            seen: new SeenScreen(image, "Cat video - YouTube - Google Chrome", whole, "Google Chrome", FullScreen: true)));
        string instructions;
        using (var body = JsonDocument.Parse(fixture.Llm.Body))
        {
            instructions = body.RootElement.GetProperty("instructions").GetString()!;
            Assert.Contains("it shows the user's whole screen: every monitor, with the taskbar and any pop-up notifications right now",
                instructions);
            Assert.DoesNotContain("Google Chrome", instructions);
            Assert.DoesNotContain("YouTube", instructions);
            var users = Users(body);
            Assert.Equal("[Screen] You looked at the user's whole screen (active window \"ELDEN RING\" in ELDEN RING, full screen): " +
                "a boss fight.", users[0]);
            Assert.StartsWith("What's this?", users[^1]);
            Assert.Contains("Active app in the picture: Google Chrome (full screen). Active window: \"Cat video - YouTube - Google Chrome\".",
                users[^1]);
        }

        fixture.Answer("Busy day. [seen: code]");
        await fixture.Finish(fixture.Controller.Start("And now?", voice: false, microphone: false, approved: true,
            seen: new SeenScreen(image, "Program.cs - Code", whole, "Visual Studio Code")));
        using (var body = JsonDocument.Parse(fixture.Llm.Body))
        {
            Assert.Equal(instructions, body.RootElement.GetProperty("instructions").GetString());
            var users = Users(body);
            Assert.Contains("Active app in the picture: Visual Studio Code. Active window: \"Program.cs - Code\".", users[^1]);
            // The earlier message keeps where its picture was from, with the app, and not the note that went with it.
            Assert.StartsWith("What's this?", users[^2]);
            Assert.EndsWith("\n[Screen] With this message you saw the user's whole screen (active window \"Cat video - YouTube - " +
                "Google Chrome\" in Google Chrome, full screen): a cat video.", users[^2]);
            Assert.DoesNotContain("Active app in the picture", users[^2]);
        }

        // The same window again: the conversation's latest [Screen] line already says it, so nothing is added.
        fixture.Answer("Still coding. [seen: code]");
        await fixture.Finish(fixture.Controller.Start("Still here?", voice: false, microphone: false, approved: true,
            seen: new SeenScreen(image, "Program.cs - Code", whole, "Visual Studio Code")));
        using (var body = JsonDocument.Parse(fixture.Llm.Body))
        {
            Assert.Equal(instructions, body.RootElement.GetProperty("instructions").GetString());
            var current = Users(body)[^1];
            Assert.StartsWith("Still here?", current);
            Assert.DoesNotContain("Active app in the picture", current);
        }
    }

    [Fact]
    public void ThePictureOnlyNamesTheAppWhenTheConversationsLatestScreenLineDoesNot()
    {
        var image = new BoundedImage([0xFF, 0xD8, 0xFF, .. new byte[32]], ImageMediaType.Jpeg, 4, 4);
        var whole = new WatchSource(WatchKind.ActiveScreen);
        var game = new SeenScreen(image, "ELDEN RING", whole, "ELDEN RING", FullScreen: true);
        static TextHistoryMessage User(string text) => new(TextHistoryRole.User, text);
        var expected = "Active app in the picture: ELDEN RING (full screen). Active window: \"ELDEN RING\".";
        Assert.Equal(expected, game.Active(null));
        Assert.Equal(expected, game.Active(null, []));
        // The latest look or message saw the same window of the same program, full screen too: nothing to add.
        Assert.Null(game.Active(null, [User(game.HistoryLine("a boss fight")), new(TextHistoryRole.Assistant, "[pass]")]));
        Assert.Null(game.Active(null, [User("Nice?\n" + game.HistoryLine("a boss fight", message: true) + "\n(touch: a pat)")]));
        Assert.Null(game.Active(null, [User(game.HistoryLine(null, why: "a notification popped up"))]));
        // Another window, the same one no longer full screen, or an older line followed by a newer one about something else.
        Assert.Equal(expected, game.Active(null, [User(new SeenScreen(image, "Steam", whole, "Steam").HistoryLine("a store page"))]));
        Assert.Equal(expected, game.Active(null, [User(new SeenScreen(image, "ELDEN RING", whole, "ELDEN RING").HistoryLine("a menu"))]));
        Assert.Equal(expected, game.Active(null,
            [User(game.HistoryLine("a boss fight")), User(new SeenScreen(image, "Discord", whole, "Discord").HistoryLine("a chat"))]));
        // A camera, nothing known, or the prompt emptied: no note.
        Assert.Null(new SeenScreen(image, "Desk cam", new WatchSource(WatchKind.Camera, "camera-1", "Desk cam")).Active(null));
        Assert.Null(new SeenScreen(image, "", whole).Active(null));
        var emptied = new PromptSettings { Overrides = new Dictionary<string, string> { [PromptCatalog.SeenApp] = "" } };
        Assert.Null(game.Active(emptied));
    }

    [Fact]
    public async Task ThinkingOnTheHomeNetworkGetsLocalTimingAndCloudKeepsItsOwn()
    {
        await using var fixture = await LiveFixture.Create();
        var loaded = await fixture.Store.LoadAsync();
        var route = loaded.Settings!.Setup!.Routes.Single(item => item.Role == SetupRole.Llm);
        Assert.False(LiveConversationConfiguration.From(loaded)!.NetworkThinking);
        Assert.Equal(TimeSpan.FromSeconds(45), LiveConversationConfiguration.From(loaded)!.TextLimits.MaxRequestTime);
        foreach (var origin in new[] { "http://192.168.1.20:11434/v1", "http://10.0.0.5:8080/v1", "http://gpu-box.local:11434/v1",
                     "http://127.0.0.1:1234/v1" })
            Assert.True(LiveConversationConfiguration.InNetwork(route with { RouteType = SetupRouteType.ChatCompletions, Origin = origin }), origin);
        Assert.False(LiveConversationConfiguration.InNetwork(route with
        {
            RouteType = SetupRouteType.ChatCompletions, Origin = ChatCompletionsEndpointCatalog.OpenRouterBaseUrl
        }));
        Assert.Contains("not a key or sign-in problem", LiveConversationController.TimeLimit(ConversationFailure.AuthorizationExpired));
        Assert.Equal("", LiveConversationController.TimeLimit(ConversationFailure.ProviderFailed));
        fixture.NoEffects();
    }

    [Fact]
    public void ALongConversationSendsTheNewestExchangesThatFitTheContextSize()
    {
        var history = Enumerable.Range(0, 2_000).Select(i => new TextHistoryMessage(i % 2 == 0 ? TextHistoryRole.User : TextHistoryRole.Assistant,
            $"Message {i}: " + new string('x', 600))).ToArray();
        var prompt = new BoundedTextInput("Now?", "Persona.");
        var budget = ContextBudget.For(SetupRouteType.ChatCompletions, ChatCompletionsEndpointCatalog.OpenRouterBaseUrl, null, null);
        var start = BoundedTextInput.HistoryStart(prompt, history, BoundedTextInput.HardMaxInputUtf8Bytes, budget.InputTokens,
            budget.InputTokens, BoundedTextInput.HardMaxHistoryMessages)!.Value;
        Assert.True(start is > 0 and < 2_000 && start % 2 == 0);
        var sent = new BoundedTextInput("Now?", "Persona.", history.Skip(start));
        Assert.True(sent.InputTokenReservation <= budget.InputTokens);
        Assert.True(new BoundedTextInput("Now?", "Persona.", history.Skip(start - 2)).InputTokenReservation > budget.InputTokens);
        // A paired host takes 16 earlier messages and 16 KiB at most.
        Assert.Equal(2_000 - 16, BoundedTextInput.HistoryStart(prompt, history, BoundedTextInput.HardMaxUtf8Bytes, 6_144, 6_144,
            TextGenerationLimits.DefaultMaxHistoryMessages));
    }

    [Fact]
    public async Task LegacyVersionTwoConversationRemainsAvailableWithoutImplicitPersonaUpload()
    {
        await using var fixture = await LiveFixture.Create(legacy: true);

        var operation = fixture.Start();
        await fixture.Finish(operation);

        Assert.Equal("runtime.Completed", operation.Status.Code);
        Assert.Null(operation.PersonaRevision);
        using var body = JsonDocument.Parse(fixture.Llm.Body);
        var instructions = body.RootElement.GetProperty("instructions").GetString()!;
        Assert.DoesNotContain("Companion name:", instructions);
        Assert.EndsWith(LiveConversationConfiguration.ReplyLengthInstructions, instructions);
        Assert.Contains("No companion persona is included", fixture.Controller.Configuration!.Disclosure(false));
    }

    [Fact]
    public async Task SpokenRepliesCloseTheirInstructionsWithAShortFirstSentenceTheSameWayEveryTurn()
    {
        await using var fixture = await LiveFixture.Create();
        var shortFirst = PromptSettings.Fill(null, PromptCatalog.ShortFirstSentence, ("silent", LiveConversationConfiguration.SilentReply))!;
        string Instructions()
        {
            using var body = JsonDocument.Parse(fixture.Llm.Body);
            return body.RootElement.GetProperty("instructions").GetString()!;
        }

        // Two spoken replies in a row: the short first sentence prompt sits just before reply length, which still closes the
        // instructions, and the instructions are the same both times, so the model's prompt cache keeps them.
        await fixture.Finish(fixture.Start("Hi there.", voice: true));
        var first = Instructions();
        Assert.EndsWith(shortFirst + "\n\n" + LiveConversationConfiguration.ReplyLengthInstructions, first);
        Assert.Equal(1, first.Split(shortFirst).Length - 1);
        await fixture.Finish(fixture.Start("And how are you?", voice: true));
        Assert.Equal(first, Instructions());
        using (var second = JsonDocument.Parse(fixture.Llm.Body))
            Assert.Contains("Hi there.", second.RootElement.GetProperty("input").GetRawText());

        // A reply that isn't spoken never gets it.
        await fixture.Finish(fixture.Start("Typed only."));
        Assert.DoesNotContain(shortFirst, Instructions());
        Assert.EndsWith(LiveConversationConfiguration.ReplyLengthInstructions, Instructions());

        // Companion › Replies › Short first sentence Off: spoken replies close with reply length alone.
        var loaded = await fixture.Store.LoadAsync();
        await fixture.Save(loaded.Settings! with { Generation = new() { ShortFirstSentence = false } });
        await fixture.Finish(fixture.Start("Hi again.", voice: true));
        Assert.DoesNotContain(shortFirst, Instructions());
        Assert.EndsWith(LiveConversationConfiguration.ReplyLengthInstructions, Instructions());
        // MainWindow's statics need WPF's pack: scheme, which a test that shows no window hasn't registered yet.
        _ = System.IO.Packaging.PackUriHelper.UriSchemePack;
        Assert.Contains("Spoken replies don't start with a short first sentence.", MainWindow.DescribeGeneration(new() { ShortFirstSentence = false }));
        Assert.Contains("Spoken replies start with a short first sentence.", MainWindow.DescribeGeneration(null));
    }

    [Fact]
    public async Task PersonaChangeDuringAuthorizationRevokesBeforeProviderDisclosure()
    {
        await using var fixture = await LiveFixture.Create();
        fixture.Settings.BeforeLoad = async _ =>
        {
            fixture.Settings.BeforeLoad = null;
            var loaded = await fixture.Store.LoadAsync();
            var persona = loaded.Settings!.Companion!.ActivePersona;
            var changed = loaded.Settings with
            {
                Companion = loaded.Settings.Companion.Update(
                    persona.Id, persona.Name, "Changed after action acceptance.")
            };
            Assert.True((await fixture.Store.SaveAsync(changed, loaded.Revision)).Saved);
        };

        var operation = fixture.Start();
        await fixture.Finish(operation);

        Assert.Equal("conversation.configuration_changed", operation.Status.Code);
        fixture.NoEffects();
        Assert.Null(operation.PersonaRevision);
    }

    [Fact]
    public async Task OversizedPersonaAndInputFailWithoutTruncationOrProviderCall()
    {
        await using var fixture = await LiveFixture.Create();
        var loaded = await fixture.Store.LoadAsync();
        var persona = loaded.Settings!.Companion!.ActivePersona;
        var changed = loaded.Settings with
        {
            // A persona and message beyond the context size are refused, never cut.
            Generation = new() { ContextTokens = GenerationSettings.MinimumContextTokens },
            Companion = loaded.Settings.Companion.Update(
                persona.Id, persona.Name, new string('\u00e9', PersonaProfile.MaximumTextCharacters))
        };
        await fixture.Save(changed);

        var operation = fixture.Start(new string('u', 4_096));
        await fixture.Finish(operation);

        Assert.Equal("conversation.input_limit", operation.Status.Code);
        fixture.NoEffects();
        Assert.Null(operation.PersonaRevision);
    }

    [Fact]
    public Task KeyboardPttStreamsCanonicalWaveThroughSttPolicyAndVoice() => DispatcherTest(async () =>
    {
        await using var fixture = await LiveFixture.Create();
        byte[] pcm = ProviderFixtures.Wave(1600)[44..];
        fixture.Capture.Packets.Enqueue(pcm);
        fixture.Answer("Spoken answer.");
        var window = fixture.Open(new TalkPreferences(HandsFree: false));
        try
        {
            await Loaded(window);
            Assert.Equal(Visibility.Visible, Control<Button>(window, "PttButton").Visibility);
            SendKey(window, Key.Space, down: true);
            await Until(() => fixture.Capture.Reads > 0);
            Assert.True(Control<Button>(window, "PttButton").IsEnabled);
            SendKey(window, Key.Space, down: false);
            await fixture.Finish();
            await Until(() => window.Messages.Any(m => m.Role == ChatRole.Martlet && m.Text.Contains("Spoken answer.", StringComparison.Ordinal)));
            Assert.Equal(1, fixture.Capture.Opens);
            Assert.Equal(1, fixture.Stt.Calls);
            Assert.Equal(1, fixture.Llm.Calls);
            Assert.Equal(1, fixture.Tts.Calls);
            Assert.Equal(1, fixture.Output.Opens);
            Assert.Equal(InputPolicy.FixedEndpoint, fixture.Capture.Selection!.Policy);
            Assert.Equal("private-input-id", fixture.Capture.Selection.EndpointId);
            Assert.Equal("Bearer " + LiveFixture.Secret, fixture.Stt.Authorization);
            Assert.Equal("https://api.openai.com/v1/audio/transcriptions", fixture.Stt.Uri!.AbsoluteUri);
            var multipart = Encoding.UTF8.GetString(fixture.Stt.Body);
            Assert.Contains("gpt-transcribe", multipart);
            Assert.Contains("utterance.wav", multipart);
            int riff = fixture.Stt.Body.AsSpan().IndexOf("RIFF"u8);
            Assert.True(riff >= 0);
            Assert.Equal(ProviderFixtures.Wave(1600), fixture.Stt.Body[riff..(riff + pcm.Length + 44)]);
            Assert.Equal(3, fixture.Native.Targets.Count);
            Assert.Contains("/openai-stt/", fixture.Native.Targets.ElementAt(0));
            Assert.Contains("/openai-llm/", fixture.Native.Targets.ElementAt(1));
            Assert.Contains("/openai-tts/", fixture.Native.Targets.ElementAt(2));
            Assert.Contains("Synthetic fixture transcript.", Encoding.UTF8.GetString(fixture.Llm.Body));
            Assert.Contains("Synthetic fixture transcript.", Assert.Single(window.Messages, m => m.IsUser).Text);
            Assert.Null(fixture.Controller.PolicySnapshot.ActiveIntentId);
        }
        finally { window.Close(); }
    });

    [Theory]
    [InlineData("no-speech")]
    [InlineData("stt-failed")]
    [InlineData("refused")]
    [InlineData("markup")]
    [InlineData("tts-failed")]
    public async Task SuppressionRefusalAndAudioFailuresDoNotCauseHiddenRequests(string scenario)
    {
        await using var fixture = await LiveFixture.Create();
        bool microphone = scenario.StartsWith("stt", StringComparison.Ordinal) || scenario == "no-speech";
        if (scenario == "no-speech") fixture.Stt.Respond = (_, _) => Task.FromResult(ProviderFixtures.Json("""{"text":""}"""));
        if (scenario == "stt-failed") fixture.Stt.Respond = (_, _) => Task.FromResult(ProviderFixtures.Json("{}", 401));
        if (scenario == "refused") fixture.Llm.Respond = (_, _) => Task.FromResult(TextRecordingHandler.Sse(string.Concat(TextFixtures.Trace("Not available.", refusal: true))));
        if (scenario == "markup") fixture.Answer("`code` https://example.invalid\n");
        if (scenario == "tts-failed") fixture.Tts.Respond = (_, _) => Task.FromResult(ProviderFixtures.Json("{}", 429));
        fixture.Capture.Packets.Enqueue(new byte[3200]);
        var operation = fixture.Start(voice: true, microphone: microphone);
        if (microphone) { await Until(() => operation.Capture?.Snapshot.CanonicalSamples > 0); operation.ReleasePress(); }
        await fixture.Finish(operation);
        Assert.Equal(0, fixture.Output.Opens);
        Assert.Equal(microphone ? 0 : 1, fixture.Llm.Calls);
        Assert.Equal(scenario == "tts-failed" ? 1 : 0, fixture.Tts.Calls);
        Assert.Null(fixture.Controller.PolicySnapshot.ActiveIntentId);
        if (scenario == "refused")
        {
            Assert.Equal(ConversationState.Refused, operation.Turn!.Snapshot.State);
            Assert.Equal("Not available.", operation.Turn.Content.Refusal);
            Assert.Equal("", operation.Turn.Content.Text);
        }
        if (scenario == "tts-failed") Assert.Equal("Hello fixture.", operation.Turn!.Content.Text);
        if (microphone) Assert.Equal(0, operation.Capture!.Snapshot.RetainedPcmBytes);
    }

    [Fact]
    public async Task ParakeetOnThisPcHearsWhatListeningsOwnRouteCouldNot()
    {
        // OpenAI refuses the transcription; Parakeet on this PC (FIXTURE words) hears the same recording and the reply goes on.
        var heard = 0;
        var asked = new List<string>();
        await using var fixture = await LiveFixture.Create(localListener: new FixtureWords(() => Interlocked.Increment(ref heard), "Heard on this PC."),
            listeningStandIn: route => { asked.Add(route.ModelId); return LocalSpeechSetup.Parakeet110mEnglishModelId; });
        fixture.Stt.Respond = (_, _) => Task.FromResult(ProviderFixtures.Json("{}", 401));
        fixture.Capture.Packets.Enqueue(new byte[3200]);
        var operation = fixture.Start(microphone: true);
        await Until(() => operation.Capture?.Snapshot.CanonicalSamples > 0);
        operation.ReleasePress();
        await fixture.Finish(operation);
        Assert.Equal((1, 1, 1), (fixture.Stt.Calls, heard, fixture.Llm.Calls));
        Assert.Equal(["gpt-transcribe"], asked);
        Assert.Equal(LocalSpeechSetup.Parakeet110mEnglishModelId, operation.StandIn);
        Assert.Equal("Heard on this PC.", operation.Transcription!.Text);
        Assert.Contains("Heard on this PC.", Encoding.UTF8.GetString(fixture.Llm.Body));
        Assert.Equal(ConversationState.Completed, operation.Turn!.Snapshot.State);
        // OpenAI itself still failed: Home says so, and that Parakeet heard it instead, until OpenAI answers again.
        var failure = Assert.Single(fixture.Controller.RecentFailures);
        Assert.Equal(SetupRole.Stt, failure.Role);
        Assert.StartsWith("outcome Failed, provider Authentication; Parakeet parakeet-tdt-110m-en on this PC heard it instead in ", failure.Outcome,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task AStandInThatFailsTooLeavesTheRoutesOwnFailureAndNoReply()
    {
        await using var fixture = await LiveFixture.Create(localListener: new BrokenWords(),
            listeningStandIn: _ => LocalSpeechSetup.Parakeet110mEnglishModelId);
        fixture.Stt.Respond = (_, _) => Task.FromResult(ProviderFixtures.Json("{}", 401));
        fixture.Capture.Packets.Enqueue(new byte[3200]);
        var operation = fixture.Start(microphone: true);
        await Until(() => operation.Capture?.Snapshot.CanonicalSamples > 0);
        operation.ReleasePress();
        await fixture.Finish(operation);
        Assert.Equal((1, 0), (fixture.Stt.Calls, fixture.Llm.Calls));
        Assert.Equal(("stt.Failed", ProviderFailureCode.Authentication), (operation.Status.Code, operation.Status.ProviderFailure));
        Assert.Contains("; Parakeet parakeet-tdt-110m-en on this PC couldn't hear it either (outcome Failed, provider Server, ",
            Assert.Single(fixture.Controller.RecentFailures).Outcome, StringComparison.Ordinal);
        Assert.Equal(0, operation.Capture!.Snapshot.RetainedPcmBytes);
        // A stand-in that can't hear either never keeps the next turn from the route.
        fixture.Capture.Packets.Enqueue(new byte[3200]);
        var next = fixture.Start(microphone: true);
        await Until(() => next.Capture?.Snapshot.CanonicalSamples > 0);
        next.ReleasePress();
        await fixture.Finish(next);
        Assert.Equal(2, fixture.Stt.Calls);
    }

    private sealed class BrokenWords : ILocalTranscriber
    {
        public Task<LocalTranscript> TranscribeAsync(string modelId, ReadOnlyMemory<byte> pcm16kMono, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("The fixture model can't load.");
    }

    [Fact]
    public async Task AfterListeningsOwnRouteFailsParakeetHearsAtOnceUntilTheRouteIsAskedAgain()
    {
        var heard = 0;
        await using var fixture = await LiveFixture.Create(localListener: new FixtureWords(() => Interlocked.Increment(ref heard), "Heard on this PC."),
            listeningStandIn: _ => LocalSpeechSetup.Parakeet110mEnglishModelId);
        fixture.Stt.Respond = (_, _) => Task.FromResult(ProviderFixtures.Json("{}", 401));
        async Task<LiveConversationOperation> Say()
        {
            fixture.Capture.Packets.Enqueue(new byte[3200]);
            var operation = fixture.Start(microphone: true);
            await Until(() => operation.Capture?.Snapshot.CanonicalSamples > 0);
            operation.ReleasePress();
            await fixture.Finish(operation);
            Assert.Equal(ConversationState.Completed, operation.Turn!.Snapshot.State);
            return operation;
        }
        await Say();
        Assert.Equal((1, 1), (fixture.Stt.Calls, heard));
        // Moments later the route isn't asked again: a computer that is off would make every turn wait for it to time out.
        var second = await Say();
        Assert.Equal((1, 2), (fixture.Stt.Calls, heard));
        Assert.Equal(LocalSpeechSetup.Parakeet110mEnglishModelId, second.StandIn);
        Assert.Equal("Heard on this PC.", second.Transcription!.Text);
        Assert.Single(fixture.Controller.RecentFailures);
        // After StandInFor the route is asked again; once it answers, Home clears and the next turns go to it.
        fixture.Clock.Advance(LiveConversationController.StandInFor);
        fixture.Stt.Respond = (_, _) => Task.FromResult(ProviderFixtures.Json());
        var third = await Say();
        Assert.Equal((2, 2), (fixture.Stt.Calls, heard));
        Assert.Null(third.StandIn);
        Assert.Empty(fixture.Controller.RecentFailures);
        await Say();
        Assert.Equal((3, 2), (fixture.Stt.Calls, heard));
    }

    [Fact]
    public async Task PolicyRejectsExactPunctuationInputBeforeAnyCredentialOrProvider()
    {
        await using var fixture = await LiveFixture.Create();
        var operation = fixture.Start(text: "...");
        await fixture.Finish(operation);
        Assert.Equal(PolicyReason.NoSpeech, operation.Status.Policy);
        Assert.Null(operation.Turn);
        fixture.NoEffects();
    }

    [Fact]
    public async Task UnsupportedRoleAndMissingConsentOrOutputRejectBeforeEffectsWhileTextOnlyStillWorks()
    {
        await using var fixture = await LiveFixture.Create();
        var settings = (await fixture.Store.LoadAsync()).Settings!;
        var changed = SetupSettings.SelectRoute(settings, SetupRole.Tts, "unsupported-canary", "unsupported-voice");
        var route = changed.Setup!.Routes.Single(r => r.Role == SetupRole.Tts);
        changed = SetupSettings.ReplaceRoute(changed, route with { Consent = route.Selection() });
        await fixture.Save(changed);
        Assert.Throws<LiveActionException>(() => fixture.Start(voice: true));
        Assert.DoesNotContain("unsupported-canary", fixture.Controller.Configuration!.Disclosure(true));
        Assert.Throws<LiveActionException>(() => fixture.Controller.Start("hi", false, true, true, true, false));
        fixture.NoEffects();
        await fixture.Finish(fixture.Start());
        Assert.Equal(1, fixture.Llm.Calls);
        Assert.Equal(0, fixture.Tts.Calls);
        Assert.Equal(0, fixture.Output.Opens);
    }

    [Theory]
    [InlineData("stop")]
    [InlineData("escape")]
    [InlineData("lock")]
    [InlineData("close")]
    public Task SlowVaultNeverBlocksDispatcherOrReleasesAppOwnershipEarly(string transition) => DispatcherTest(async () =>
    {
        await using var fixture = await LiveFixture.Create();
        fixture.Native.Block = true;
        var window = fixture.Open();
        try
        {
            await Loaded(window);
            Control<TextBox>(window, "InputText").Text = "test";
            Click(window, "SendButton");
            await fixture.Native.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await Heartbeat();
            switch (transition)
            {
                case "stop": Click(window, "StopButton"); break;
                case "escape": Escape(window, "InputText"); break;
                case "lock": fixture.Events.Signal(true); break;
                case "close": window.Close(); break;
            }
            await Heartbeat();
            fixture.Clock.Advance(TimeSpan.FromSeconds(3));
            await Heartbeat();
            Assert.True(fixture.Runner.IsRunning);
            Assert.NotNull(fixture.Controller.PolicySnapshot.ActiveIntentId);
            Assert.Null(fixture.Runner.TryStart(_ => Task.FromResult(new SetupWorkResult(SetupWorkOutcome.Completed))));
            Assert.Equal(0, fixture.Llm.Calls);
            fixture.Native.Release.Set();
            await fixture.Finish();
            Assert.Equal(0, fixture.Llm.Calls);
            Assert.Equal(0, fixture.Output.Opens);
            Assert.All(fixture.Native.Leases, lease => Assert.Throws<ObjectDisposedException>(() => lease.Use(_ => { })));
            Assert.Null(fixture.Controller.PolicySnapshot.ActiveIntentId);
        }
        finally { fixture.Native.Release.Set(); window.Close(); }
    });

    [Fact]
    public async Task CredentialRevisionIsCheckedAgainAfterBlockedNativeReturn()
    {
        await using var fixture = await LiveFixture.Create();
        fixture.Native.Block = true;
        var operation = fixture.Start();
        await fixture.Native.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var loaded = await fixture.Store.LoadAsync();
        var changed = SetupSettings.SelectRoute(loaded.Settings!, SetupRole.Llm, "unsupported", null);
        Assert.True((await fixture.Store.SaveAsync(changed, loaded.Revision)).Saved);
        fixture.Native.Release.Set();
        await fixture.Finish(operation);
        Assert.Equal(0, fixture.Llm.Calls);
        Assert.Single(fixture.Native.Targets);
        Assert.All(fixture.Native.Leases, lease => Assert.Throws<ObjectDisposedException>(() => lease.Use(_ => { })));
    }

    [Fact]
    public async Task OriginalCallerFlagBeatsBlockedNewerCancellationCallbackAndLateVaultReturn()
    {
        await using var fixture = await LiveFixture.Create();
        using var caller = new CancellationTokenSource();
        using var unblock = new ManualResetEventSlim();
        var callbackEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Native.Block = true;
        var operation = fixture.Start(caller: caller.Token);
        await fixture.Native.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        using var newest = caller.Token.Register(() => { callbackEntered.SetResult(); unblock.Wait(); });
        var cancellation = caller.CancelAsync();
        try
        {
            await callbackEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            fixture.Native.Release.Set();
            fixture.Clock.Advance(TimeSpan.FromMilliseconds(25));
            await fixture.Finish(operation);
            Assert.False(cancellation.IsCompleted);
            Assert.Equal(0, fixture.Llm.Calls);
            Assert.All(fixture.Native.Leases, lease => Assert.Throws<ObjectDisposedException>(() => lease.Use(_ => { })));
        }
        finally { unblock.Set(); await cancellation; }
    }

    [Fact]
    public async Task OriginalActionClockCannotBeRenewedBySlowLoadAndUtcRollback()
    {
        await using var fixture = await LiveFixture.Create();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Settings.BeforeLoad = async _ => { entered.TrySetResult(); await release.Task; };
        var operation = fixture.Start();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        fixture.Clock.DeferCallbacks = true;
        fixture.Clock.Advance(TimeSpan.FromSeconds(151));
        fixture.Clock.ShiftUtc(TimeSpan.FromHours(-1));
        release.SetResult();
        await fixture.Finish(operation, advance: false);
        Assert.Equal("conversation.expired", operation.Status.Code);
        fixture.NoEffects();
    }

    [Fact]
    public Task WhatThePcPlaysIsMarkedAndNeverRemembered() => DispatcherTest(async () =>
    {
        var pc = new PcSourceFixture();
        await using var fixture = await LiveFixture.Create(pcAudio: pc);
        await fixture.EnableMemory();
        fixture.Answer("Ha, good one.");
        // The video speaks for 2.5 s; then nothing plays at all (a loopback delivers no packets).
        pc.Enqueue(quiet: 3, speech: 25);
        var window = fixture.Open(new TalkPreferences(SpeakReplies: false, HearPc: true));
        try
        {
            await Loaded(window);
            Assert.Equal("Also hears this PC once you start listening.", Control<TextBlock>(window, "PcAudioText").Text);
            Click(window, "MicChip");
            using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20)))
                while (!window.Messages.Any(m => m.Role == ChatRole.Martlet))
                {
                    fixture.Clock.Advance(TimeSpan.FromMilliseconds(50));
                    await Task.Delay(1, timeout.Token);
                }
            var heard = Assert.Single(window.Messages, m => m.IsPcAudio);
            Assert.StartsWith("Playing on this PC", heard.Caption);
            Assert.Equal("Synthetic fixture transcript.", heard.Text);
            Assert.DoesNotContain(window.Messages, m => m.IsUser);
            Assert.NotNull(window.PcListener);
            var body = Encoding.UTF8.GetString(fixture.Llm.Body);
            Assert.Contains("[PC audio] Synthetic fixture transcript.", body, StringComparison.Ordinal);
            Assert.Contains("never the user", body, StringComparison.Ordinal);
            await fixture.FinishRemembering();
            // One transcription and one reply: nothing was remembered from what the PC played.
            Assert.Equal(1, fixture.Stt.Calls);
            Assert.Equal(1, fixture.Llm.Calls);
        }
        finally { window.Close(); }
    });

    [Fact]
    public void TheContextRowStaysShortAndItsTooltipHasTheTokens()
    {
        var budget = new ContextBudget(32_768, 4_096, ContextSource.Saved, null);
        Assert.Equal("Keeps the last exchange in mind.", LiveConversationWindow.ContextLine(1, 300, budget));
        Assert.Equal("Keeps the last 8 exchanges in mind.", LiveConversationWindow.ContextLine(8, 1_593, budget));
        Assert.Equal("Keeps the last 8 exchanges in mind.", LiveConversationWindow.ContextLine(8, 1_593, null));
        Assert.Equal("Keeps the last 90 exchanges in mind; replies send the newest that fit.",
            LiveConversationWindow.ContextLine(90, 40_000, budget));
        Assert.Equal($"About {1_593:N0} tokens of its {32_768:N0}-token context.", LiveConversationWindow.ContextDetail(1_593, budget));
        Assert.Equal($"About {40_000:N0} tokens, more than fit its {32_768:N0}-token context. Last reply: 75% of its {2_000:N0} " +
            "input tokens came from the model's cache.", LiveConversationWindow.ContextDetail(40_000, budget, (2_000, 1_500)));
        Assert.Equal("", LiveConversationWindow.ContextDetail(1_593, null));
    }

    [Fact]
    public void PcLinesAreMarkedInTheOrderTheyWereHeard() =>
        Assert.Equal("[PC audio] And now the weather.\nWhat did he say? Was it rain?\n[PC audio] Rain all week.",
            LiveConversationWindow.PcMessage([("And now the weather.", true), ("What did he say?", false), ("Was it rain?", false),
                ("Rain all week.", true)]));

    [Fact]
    public void PcLinesSayWhereTheyCameFromWhenMartletCanTell()
    {
        IReadOnlyList<PcSource> youtube = [new(PcActivityKind.Video, "Chrome", "YouTube")];
        IReadOnlyList<PcSource> call = [new(PcActivityKind.VoiceChat, "Discord"), new(PcActivityKind.Game, "ELDEN RING")];
        Assert.Equal("[PC audio] From a YouTube video in Chrome: And now the weather.\nWhat did he say?\n" +
            "[PC audio] From a voice chat in Discord or a game (ELDEN RING): Push left!\n[PC audio] Unknown.",
            LiveConversationWindow.PcMessage([("And now the weather.", true, youtube), ("What did he say?", false, null),
                ("Push left!", true, call), ("Unknown.", true, [])]));
    }

    [Fact]
    public Task APcLineSaysWhereItCameFromAndRepliesKnowWhatYouAreDoing() => DispatcherTest(async () =>
    {
        var pc = new PcSourceFixture();
        await using var fixture = await LiveFixture.Create(pcAudio: pc,
            pcActivity: clock => new PcActivityMonitor(() => new AppsFixture(), clock, manual: true));
        fixture.Answer("Ha, good one.");
        var window = fixture.Open(new TalkPreferences(SpeakReplies: false, HearPc: true));
        var activity = fixture.Controller.PcActivity!;
        try
        {
            await Loaded(window);
            Click(window, "MicChip");
            // The talk window follows what plays on this PC while it hears the PC; then a YouTube video in Chrome talks.
            await Follow(fixture, activity, () => activity.On);
            pc.Enqueue(quiet: 3, speech: 25);
            await Follow(fixture, activity, () => window.Messages.Any(m => m.Role == ChatRole.Martlet));
            var heard = Assert.Single(window.Messages, m => m.IsPcAudio);
            Assert.StartsWith("Playing on this PC: a YouTube video in Chrome · ", heard.Caption);
            Assert.Equal("Synthetic fixture transcript.", heard.Text);
            var body = Encoding.UTF8.GetString(fixture.Llm.Body);
            Assert.Contains("[PC audio] From a YouTube video in Chrome: Synthetic fixture transcript.", body, StringComparison.Ordinal);
            Assert.Contains("What the user seems to be doing on this PC now (a guess from which apps play sound and which window fills " +
                "the screen): watching a YouTube video in Chrome.", body, StringComparison.Ordinal);
            Assert.Contains("a voice chat or call (Discord, TeamSpeak, Zoom, Teams)", body, StringComparison.Ordinal);
            // The line under the status says what you seem to be doing (MCP reads it as LivePcAudio's help).
            var pcLine = Control<TextBlock>(window, "PcAudioText");
            await Follow(fixture, activity, () => AutomationProperties.GetHelpText(pcLine).Contains("Now: watching a YouTube video in Chrome."));
            // Stopping listening stops following the PC, and its note leaves the context board.
            Assert.Contains(ContextBoard.Activity, fixture.Controller.Board.Snapshot(fixture.Clock.GetLocalNow()).Sources);
            Click(window, "MicChip");
            await Follow(fixture, activity, () => !activity.On);
            Assert.DoesNotContain(ContextBoard.Activity, fixture.Controller.Board.Snapshot(fixture.Clock.GetLocalNow()).Sources);
        }
        finally { window.Close(); }
    });

    [Fact]
    public Task TheMicrophoneHearingAVideoOnTheSpeakersIsNotYou() => DispatcherTest(async () =>
    {
        const string Weather = "And now the weather for the weekend.";
        var pc = new PcSourceFixture();
        await using var fixture = await LiveFixture.Create(pcAudio: pc,
            pcActivity: clock => new PcActivityMonitor(() => new AppsFixture(), clock, manual: true));
        fixture.Stt.Respond = (_, _) => Task.FromResult(ProviderFixtures.Json($$"""{"text":"{{Weather}}"}"""));
        fixture.Answer("Ha.");
        var window = fixture.Open(new TalkPreferences(SpeakReplies: false, HearPc: true));
        var activity = fixture.Controller.PcActivity!;
        try
        {
            await Loaded(window);
            Click(window, "MicChip");
            await Follow(fixture, activity, () => activity.On);
            // A YouTube video plays on the speakers: the PC and the microphone hear the same words at the same moment. The PC's
            // line is ready first (it waits for the microphone, which still hears the room), then the microphone's.
            pc.Enqueue(quiet: 5, speech: 25);
            EnqueueUtterance(fixture.Capture, quietBefore: 5, speech: 25, quietAfter: 0);
            await Follow(fixture, activity, () => fixture.Stt.Calls >= 1);
            for (var i = 0; i < 10; i++)
            {
                activity.Tick();
                fixture.Clock.Advance(TimeSpan.FromMilliseconds(50));
                await Task.Delay(1);
            }
            EnqueueUtterance(fixture.Capture, quietBefore: 15, speech: 0, quietAfter: 0);
            await Follow(fixture, activity, () => window.Messages.Any(m => m.Role == ChatRole.Martlet));
            for (var i = 0; i < 100; i++)
            {
                activity.Tick();
                fixture.Clock.Advance(TimeSpan.FromMilliseconds(50));
                await Task.Delay(1);
            }
            // Both heard it; Martlet answered once, as what the PC played. The microphone's copy is a faded note, never your words.
            Assert.Equal(2, fixture.Stt.Calls);
            Assert.Equal(1, fixture.Llm.Calls);
            Assert.DoesNotContain(window.Messages, m => m.IsUser);
            Assert.Equal(Weather, Assert.Single(window.Messages, m => m.IsPcAudio).Text);
            Assert.Contains(window.Messages, m => m.IsNote && m.Text.Contains("this PC's speakers: a YouTube video in Chrome", StringComparison.Ordinal));
            var body = Encoding.UTF8.GetString(fixture.Llm.Body);
            Assert.Contains($"[PC audio] From a YouTube video in Chrome: {Weather}", body, StringComparison.Ordinal);
            Assert.Single(System.Text.RegularExpressions.Regex.Matches(body, "the weather for the weekend"));
            var pcLine = Control<TextBlock>(window, "PcAudioText");
            await Follow(fixture, activity, () => AutomationProperties.GetHelpText(pcLine)
                .Contains("The microphone also heard this PC's speakers; Martlet left out 1 line of it.", StringComparison.Ordinal));
        }
        finally { window.Close(); }
    });

    // Moves the fixture clock on (and the PC activity monitor with it) until the condition holds.
    private static async Task Follow(LiveFixture fixture, PcActivityMonitor activity, Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        while (!condition())
        {
            activity.Tick();
            fixture.Clock.Advance(TimeSpan.FromMilliseconds(50));
            await Task.Delay(1, timeout.Token);
        }
    }

    // What this PC plays, as the PC activity monitor reads it: a YouTube video in Chrome, always loud.
    private sealed class AppsFixture : IPcActivitySource
    {
        public IReadOnlyList<PcAppLevel> Levels() => [new("chrome", 0.25f)];
        public IReadOnlyList<PcAppFacts> Facts(IReadOnlyCollection<string> apps) => [new("chrome", Titles: ["Lofi beats - YouTube - Google Chrome"])];
        public void Dispose() { }
    }

    [Fact]
    public Task YourOwnVoicePlayedBackOnThisPcIsAnsweredOnce() => DispatcherTest(async () =>
    {
        var pc = new PcSourceFixture();
        await using var fixture = await LiveFixture.Create(pcAudio: pc);
        fixture.Answer("Heard you.");
        // You talk while this PC plays your voice back (a voice changer's "hear myself"): both listeners hear the same words.
        EnqueueUtterance(fixture.Capture, quietBefore: 5, speech: 25, quietAfter: 15);
        pc.Enqueue(quiet: 5, speech: 25);
        var window = fixture.Open(new TalkPreferences(SpeakReplies: false, HearPc: true));
        try
        {
            await Loaded(window);
            Click(window, "MicChip");
            await fixture.Advance(() => window.Messages.Any(m => m.Role == ChatRole.Martlet));
            // Well past when what the PC played would have gone to Martlet on its own.
            for (var i = 0; i < 700; i++)
            {
                fixture.Clock.Advance(TimeSpan.FromMilliseconds(50));
                await Task.Delay(1);
            }
            Assert.Equal(1, fixture.Llm.Calls);
            Assert.Single(window.Messages, m => m.IsUser);
            Assert.Single(window.Messages, m => m.Role == ChatRole.Martlet);
            Assert.DoesNotContain(window.Messages, m => m.IsPcAudio);
            Assert.DoesNotContain("[PC audio]", Encoding.UTF8.GetString(fixture.Llm.Body), StringComparison.Ordinal);
            // The line stays short; how many lines of your voice it left out is in its tooltip (MCP's help).
            var pcLine = Control<TextBlock>(window, "PcAudioText");
            Assert.DoesNotContain("left out", pcLine.Text);
            Assert.EndsWith("This PC plays your voice back too; Martlet left out 1 line of it.", AutomationProperties.GetHelpText(pcLine));
            Assert.Equal(AutomationProperties.GetHelpText(pcLine), pcLine.ToolTip);
        }
        finally { window.Close(); }
    });

    [Theory]
    // The first two are the reports on a PC whose voice changer played the user's voice back (the companion is called Jane).
    [InlineData("Hello Jane.", "Hello, Jane.", true)]
    [InlineData("Why is that the way?", "Why is that the right?", true)]
    [InlineData("Do you like being called that?", "Do you like being called that?", true)]
    [InlineData("What's the weather like tomorrow?", "what's the weather like, tomorrow", true)]
    [InlineData("that's it", "That’s it!", true)]
    [InlineData("你好吗", "你好吗？", true)]
    [InlineData("Okay, so we could watch the next episode tonight. What do you think?", "we could watch the next episode tonight", true)]
    [InlineData("Did you hear that?", "And now the weather for the weekend.", false)]
    [InlineData("Haha, what is she doing?", "Why is that the right?", false)]
    [InlineData("Why is that the right?", "Oh wow, look at that. Why is that the right?", false)]
    [InlineData("Ha, he said rain all week.", "Expect rain all week across the region, with flooding in the north.", false)]
    [InlineData("Anything.", "", false)]
    public void APcLineThatRepeatsYouIsYourOwnVoice(string spoken, string played, bool repeats) =>
        Assert.Equal(repeats, PcEcho.Repeats(played, [spoken]));

    private sealed class PcSourceFixture : IPcAudioSourceFactory, IPcAudioSource
    {
        private readonly ConcurrentQueue<byte[]> packets = new();
        private int sample;
        public bool WithoutMartlet => true;
        public CaptureSourceFormat Format { get; } = new(16000, 1, 16, DeviceSampleEncoding.IntegerPcm);
        public IPcAudioSource Open(CancellationToken cancellationToken) => this;
        public void Start() { }
        public void Stop() { }
        public void Dispose() { }
        public CapturePacket Read(Span<byte> destination)
        {
            if (!packets.TryDequeue(out var bytes)) return new(0);
            bytes.AsSpan().CopyTo(destination);
            return new(bytes.Length);
        }

        internal void Enqueue(int quiet, int speech)
        {
            void Packets(int count, double amplitude)
            {
                for (var p = 0; p < count; p++)
                {
                    var packet = new byte[3200];
                    for (var i = 0; i < 1600; i++, sample++)
                    {
                        var t = sample / 16000.0;
                        var value = amplitude * Math.Sin(2 * Math.PI * 180 * t) * (0.7 + 0.3 * Math.Sin(2 * Math.PI * 4 * t));
                        System.Buffers.Binary.BinaryPrimitives.WriteInt16LittleEndian(packet.AsSpan(i * 2), (short)(value * 32767));
                    }
                    packets.Enqueue(packet);
                }
            }
            Packets(quiet, 0.001);
            Packets(speech, 0.25);
        }
    }

    [Fact]
    public async Task HandsFreeListeningSendsOnlyTheEndpointedUtterance()
    {
        await using var fixture = await LiveFixture.Create();
        EnqueueUtterance(fixture.Capture, quietBefore: 10, speech: 12, quietAfter: 15);
        var operation = fixture.Controller.Start(null, voice: false, microphone: true, approved: true, true, true,
            listening: new(HandsFree: true, new VoiceActivitySettings(), RequireVoiceId: false));
        await fixture.Finish(operation);
        Assert.Equal(CaptureEndReason.Released, operation.Capture!.Snapshot.EndReason);
        Assert.Equal(1, fixture.Stt.Calls);
        // Pre-roll + 1.2 s speech + tail, not the whole 3.7 s recording.
        Assert.InRange(fixture.Stt.Body.Length, 38_400, 80_000);
        Assert.Equal(1, fixture.Controller.PolicySnapshot.AcceptedThroughIntentId);
        Assert.Equal(ConversationState.Completed, operation.Turn!.Snapshot.State);
    }

    [Fact]
    public async Task HandsFreeSilenceRestartsWithoutUploading()
    {
        await using var fixture = await LiveFixture.Create();
        EnqueueUtterance(fixture.Capture, quietBefore: 20, speech: 0, quietAfter: 0);
        var operation = fixture.Controller.Start(null, voice: false, microphone: true, approved: true, true, true,
            listening: new(HandsFree: true, new VoiceActivitySettings(), RequireVoiceId: false));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        while (!operation.Worker.Completion.IsCompleted)
        {
            fixture.Clock.Advance(TimeSpan.FromMilliseconds(100));
            await Task.Delay(1, timeout.Token);
        }
        await operation.Worker.Completion;
        Assert.Equal("mic.no_speech", operation.Status.Code);
        Assert.Equal(0, fixture.Stt.Calls);
        Assert.Equal(0, fixture.Llm.Calls);
    }

    [Fact]
    public async Task VoiceIdDiscardsAnotherVoiceBeforeUpload()
    {
        var directory = Path.Combine(Path.GetTempPath(), "Martlet.VoiceId." + Guid.NewGuid().ToString("N"));
        try
        {
            var identity = new VoiceIdentity(directory);
            var print = new float[SpeakerEncoder.EmbeddingSize];
            for (var i = 0; i < print.Length; i += 7) print[i] = 1;
            identity.Save(new(SpeakerEncoder.Average([print]), 0.9f, 0.9f, DateTimeOffset.Now, 20));
            await using var fixture = await LiveFixture.Create(voiceIdentity: identity);
            EnqueueUtterance(fixture.Capture, quietBefore: 5, speech: 25, quietAfter: 15);
            var operation = fixture.Controller.Start(null, voice: false, microphone: true, approved: true, true, true,
                listening: new(HandsFree: true, new VoiceActivitySettings(), RequireVoiceId: true));
            await fixture.Finish(operation);
            Assert.Equal("speaker.not_user", operation.Status.Code);
            Assert.Equal(SpeakerVerdict.OtherSpeaker, operation.SpeakerCheck!.Verdict);
            Assert.Equal(0, fixture.Stt.Calls);
            Assert.Equal(0, fixture.Llm.Calls);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [Fact]
    public Task AlwaysListeningIgnoresSoundsThatArentWordsAndShowsThemFaded() => DispatcherTest(async () =>
    {
        await using var fixture = await LiveFixture.Create();
        var transcripts = new ConcurrentQueue<string>(["Mmm.", "Thanks for watching!", "What do you think?"]);
        fixture.Stt.Respond = (_, _) => Task.FromResult(ProviderFixtures.Json(
            JsonSerializer.Serialize(new { text = transcripts.TryDequeue(out var next) ? next : "Again." })));
        fixture.Llm.Respond = (_, _) => Task.FromResult(TextRecordingHandler.Sse(Harness.Trace("I think so.")));
        EnqueueUtterance(fixture.Capture, quietBefore: 5, speech: 25, quietAfter: 15);
        var window = fixture.Open(new TalkPreferences(SpeakReplies: false));
        try
        {
            await Loaded(window);
            Click(window, "MicChip");
            await fixture.Advance(() => fixture.Stt.Calls == 1 && window.Messages.Any(m => m.IsNote && m.Text.Contains("Ignored", StringComparison.Ordinal)));
            EnqueueUtterance(fixture.Capture, quietBefore: 5, speech: 25, quietAfter: 15);
            await fixture.Advance(() => fixture.Stt.Calls == 2 && window.Messages.Count(m => m.IsNote) == 1 &&
                window.Messages.Single(m => m.IsNote).Text.Contains("Thanks for watching", StringComparison.Ordinal));
            // Neither became a turn or reached Thinking; both share one faded note.
            Assert.Equal(0, fixture.Llm.Calls);
            Assert.DoesNotContain(window.Messages, m => m.IsUser);
            Assert.Equal("Ignored \u201CMmm.\u201D (not words).  Ignored \u201CThanks for watching!\u201D (speech-to-text makes this up from noise).",
                window.Messages.Single(m => m.IsNote).Text);
            EnqueueUtterance(fixture.Capture, quietBefore: 5, speech: 25, quietAfter: 15);
            await fixture.Advance(() => window.Messages.Any(m => m.Role == ChatRole.Martlet && m.Text == "I think so."));
            Assert.Equal(["What do you think?"], window.Messages.Where(m => m.IsUser).Select(m => m.Text));
            Assert.Equal(1, fixture.Llm.Calls);
        }
        finally { window.Close(); }
    });

    [Fact]
    public Task AlwaysListeningKeepsListeningWhileMartletSpeaksAndAnswersWhatItHeardAfter() => DispatcherTest(async () =>
    {
        await using var fixture = await LiveFixture.Create(new ControlledDevice { AutoConsume = false }, echo: true);
        var transcripts = new ConcurrentQueue<string>(["What's the weather like?", "Also, remind me about lunch."]);
        fixture.Stt.Respond = (_, _) => Task.FromResult(ProviderFixtures.Json(
            JsonSerializer.Serialize(new { text = transcripts.TryDequeue(out var next) ? next : "Again." })));
        var replies = new ConcurrentQueue<string>(["It is sunny today.", "Lunch is at noon."]);
        fixture.Llm.Respond = (_, _) => Task.FromResult(TextRecordingHandler.Sse(Harness.Trace(replies.TryDequeue(out var next) ? next : "Okay.")));
        EnqueueUtterance(fixture.Capture, quietBefore: 5, speech: 25, quietAfter: 15);
        // Echo reduction on (the default), barge-in off (the default).
        var window = fixture.Open(new TalkPreferences(SpeakReplies: true));
        try
        {
            await Loaded(window);
            Click(window, "MicChip");
            await fixture.Advance(() => fixture.Controller.Speaking == PlaybackMode.Reply);
            var reply = Assert.IsType<LiveConversationOperation>(window.Current);
            // Martlet is speaking and the microphone stays open: the capture listening since the reply began isn't let go.
            var disposals = fixture.Capture.Disposals;
            await fixture.Pass(TimeSpan.FromMilliseconds(500));
            await Heartbeat();
            Assert.Equal(PlaybackMode.Reply, fixture.Controller.Speaking);
            Assert.Equal(disposals, fixture.Capture.Disposals);
            Assert.StartsWith("Listening. Martlet listens for you, even while it speaks",
                System.Windows.Automation.AutomationProperties.GetName(Control<Button>(window, "MicChip")));
            // What you say now is heard and shown...
            EnqueueUtterance(fixture.Capture, quietBefore: 5, speech: 25, quietAfter: 15);
            await fixture.Advance(() => fixture.Stt.Calls == 2 && window.Messages.Count(m => m.IsUser) == 2);
            Assert.Equal(["What's the weather like?", "Also, remind me about lunch."], window.Messages.Where(m => m.IsUser).Select(m => m.Text));
            Assert.Equal(PlaybackMode.Reply, fixture.Controller.Speaking);
            Assert.False(reply.OwnershipReleased);
            // ...without stopping the reply (barge-in is off) or being answered before it finishes.
            Assert.Equal(1, fixture.Llm.Calls);
            fixture.Output.AutoConsume = true;
            await fixture.Advance(() => window.Messages.Any(m => m.Role == ChatRole.Martlet && m.Text == "Lunch is at noon."));
            Assert.Equal("runtime.Completed", reply.Status.Code);
            Assert.Equal(2, fixture.Llm.Calls);
            Assert.Contains("Also, remind me about lunch.", Encoding.UTF8.GetString(fixture.Llm.Body));
        }
        finally { window.Close(); }
    });

    [Fact]
    public Task WithoutEchoReductionAlwaysListeningPausesWhileMartletSpeaks() => DispatcherTest(async () =>
    {
        await using var fixture = await LiveFixture.Create(new ControlledDevice { AutoConsume = false }, echo: true);
        fixture.Stt.Respond = (_, _) => Task.FromResult(ProviderFixtures.Json(JsonSerializer.Serialize(new { text = "What's the weather like?" })));
        fixture.Answer("It is sunny today.");
        EnqueueUtterance(fixture.Capture, quietBefore: 5, speech: 25, quietAfter: 15);
        // Nothing tells Martlet's own voice from yours: it would hear itself, so it doesn't listen while it speaks.
        var window = fixture.Open(new TalkPreferences(SpeakReplies: true, ReduceEcho: false));
        try
        {
            await Loaded(window);
            Click(window, "MicChip");
            await fixture.Advance(() => fixture.Controller.Speaking == PlaybackMode.Reply);
            await fixture.Advance(() => System.Windows.Automation.AutomationProperties.GetName(Control<Button>(window, "MicChip"))
                .Contains("Not listening while Martlet speaks", StringComparison.Ordinal));
            var opens = fixture.Capture.Opens;
            EnqueueUtterance(fixture.Capture, quietBefore: 5, speech: 25, quietAfter: 15);
            await fixture.Pass(TimeSpan.FromMilliseconds(500));
            await Heartbeat();
            // Nothing said while it speaks is recorded.
            Assert.Equal(PlaybackMode.Reply, fixture.Controller.Speaking);
            Assert.Equal(opens, fixture.Capture.Opens);
            Assert.Equal(1, fixture.Stt.Calls);
            Assert.Single(window.Messages, m => m.IsUser);
            fixture.Output.AutoConsume = true;
            // Once Martlet is done it listens again.
            await fixture.Advance(() => fixture.Capture.Opens > opens);
        }
        finally { window.Close(); }
    });

    [Fact]
    public Task WatchingStartsOnlyFromStartWatchingAndStopsFromItsOwnButton() => DispatcherTest(async () =>
    {
        await using var fixture = await LiveFixture.Create();
        var glancer = new CountingGlancer();
        var window = fixture.Open(new TalkPreferences(HandsFree: false, SpeakReplies: false, Watch: true,
            ScreenScope: (int)WatchKind.ActiveWindow), glancer);
        try
        {
            await Loaded(window);
            await Ticks(fixture);
            // Vision on in Companion only offers watching: opening the window doesn't look.
            Assert.True(window.VisionOn);
            Assert.False(window.IsWatching);
            Assert.False(window.WatchingStarted);
            Assert.Equal(0, glancer.Captures);
            Assert.Equal("Start watching", Control<TextBlock>(window, "VisionText").Text);
            Assert.Equal(("Not watching", false), window.WatchingStatus);

            Click(window, "VisionChip");
            Assert.True(window.IsWatching);
            Assert.Equal("Stop watching", Control<TextBlock>(window, "VisionText").Text);
            Assert.StartsWith("Watching. ", System.Windows.Automation.AutomationProperties.GetName(Control<Button>(window, "VisionChip")));
            Assert.Equal(("Watching your active window.", false), window.WatchingStatus);
            await fixture.Advance(() => glancer.Captures > 0);

            Click(window, "VisionChip");
            Assert.False(window.IsWatching);
            Assert.False(window.WatchingStarted);
            Assert.Equal("Start watching", Control<TextBlock>(window, "VisionText").Text);
            // A capture already under way may still finish; nothing new starts after that.
            await Ticks(fixture);
            var seen = glancer.Captures;
            await Ticks(fixture);
            Assert.Equal(seen, glancer.Captures);

            // Home's and the notification-area menu's Start watching and Stop watching; neither touches listening.
            window.WatchWhenReady();
            Assert.True(window.IsWatching);
            Assert.False(window.ListeningStarted);
            window.StopWatchingNow();
            Assert.False(window.IsWatching);
            Assert.False(window.WatchingStarted);

            // Stop (Esc) stops watching too, and it stays stopped.
            window.WatchWhenReady();
            Escape(window, "InputText");
            Assert.False(window.WatchingStarted);
            Assert.False(window.IsWatching);

            // Locking Windows stops looking for a moment; unlocking carries on.
            window.WatchWhenReady();
            fixture.Events.Signal(true);
            await Until(() => !window.IsWatching);
            Assert.True(window.WatchingStarted);
            fixture.Events.Signal(false);
            await Until(() => window.IsWatching);

            // Pause Martlet stops it and Resume brings it back.
            window.Pause();
            Assert.False(window.IsWatching);
            Assert.False(window.WatchingStarted);
            window.Resume();
            Assert.True(window.IsWatching);

            // Turning vision off in Companion stops it; turning it on again only offers Start watching.
            var preferences = new TalkPreferences(HandsFree: false, SpeakReplies: false, Watch: true, ScreenScope: (int)WatchKind.ActiveWindow);
            window.UsePreferences(preferences with { Watch = false }, null);
            Assert.False(window.IsWatching);
            window.UsePreferences(preferences, null);
            await Ticks(fixture);
            Assert.False(window.IsWatching);
            Assert.False(window.WatchingStarted);
        }
        finally { window.End(); }
    });

    [Fact]
    public Task WatchingThatCantStartSaysWhyAndStaysStopped() => DispatcherTest(async () =>
    {
        await using var fixture = await LiveFixture.Create();
        var glancer = new CountingGlancer();
        var camera = new TalkPreferences(HandsFree: false, SpeakReplies: false, Watch: true, ScreenScope: (int)WatchKind.Camera);
        var window = fixture.Open(camera, glancer);
        try
        {
            await Loaded(window);
            Click(window, "VisionChip");
            Assert.False(window.IsWatching);
            Assert.False(window.WatchingStarted);
            Assert.Equal("Can't see", Control<TextBlock>(window, "VisionText").Text);
            Assert.Equal(("Choose a camera in Companion › Vision.", true), window.WatchingStatus);
            // Choosing a camera clears the problem but doesn't start looking by itself.
            window.UsePreferences(camera with { CameraId = "camera-1", CameraName = "Test camera" }, null);
            Assert.Equal("Start watching", Control<TextBlock>(window, "VisionText").Text);
            Assert.Equal(("Not watching", false), window.WatchingStatus);
            Assert.False(window.IsWatching);
            Assert.Equal(0, glancer.Captures);
        }
        finally { window.Close(); }
    });

    [Fact]
    public Task TheVisionLineSaysOnlyWhatMartletWatches() => DispatcherTest(async () =>
    {
        await using var fixture = await LiveFixture.Create();
        fixture.Answer("[pass]");
        var glancer = new FrameGlancer();
        var window = fixture.Open(new TalkPreferences(HandsFree: false, SpeakReplies: false, Watch: true,
            ScreenScope: (int)WatchKind.ActiveWindow), glancer);
        try
        {
            await Loaded(window);
            Click(window, "VisionChip");
            await fixture.Advance(() => glancer.Captures > 0);
            await Ticks(fixture);
            var line = Control<TextBlock>(window, "VisionStatusText");
            // No "First look soon.", "You're talking; not interrupting." or last look in the line; those are its tooltip, which
            // starts with the program in front, never the window's title.
            Assert.True(line.Text is "Watching your active window." or "Watching your active window. Taking a look…", line.Text);
            var help = System.Windows.Automation.AutomationProperties.GetHelpText(line);
            Assert.StartsWith($"Active app: {FrameGlancer.App} (full screen).", help);
            Assert.True(help == $"Active app: {FrameGlancer.App} (full screen)." ||
                help.StartsWith($"Active app: {FrameGlancer.App} (full screen). Last look ", StringComparison.Ordinal), help);
            Assert.DoesNotContain(FrameGlancer.Title, line.Text + help);
        }
        finally { window.End(); }
    });

    /// <summary>Lets the window's timer run while the clock passes a few capture ticks.</summary>
    private static async Task Ticks(LiveFixture fixture)
    {
        for (var i = 0; i < 10; i++)
        {
            fixture.Clock.Advance(ScreenCommentaryPacer.Tick);
            await Task.Delay(30);
        }
    }

    /// <summary>A screen that always shows only Martlet: it counts captures and never offers a picture to look at.</summary>
    private sealed class CountingGlancer : IScreenGlancer
    {
        private int captures;
        internal int Captures => Volatile.Read(ref captures);
        public GlanceResult Capture(ScreenScope scope)
        {
            Interlocked.Increment(ref captures);
            return new(null, GlanceSkip.MartletInFront);
        }
        public TimeSpan UserIdle => TimeSpan.Zero;
        public void Release() { }
    }

    /// <summary>A full-screen window in front that always shows a tiny picture with a private title.</summary>
    private sealed class FrameGlancer : IScreenGlancer
    {
        internal const string Title = "Private window title";
        internal const string App = "Fixture Player";
        private int captures;
        internal int Captures => Volatile.Read(ref captures);
        public GlanceResult Capture(ScreenScope scope)
        {
            Interlocked.Increment(ref captures);
            return new(new ScreenFrame(new byte[2 * 2 * 4], 2, 2, Title, 0.5, app: App, fullScreen: true), GlanceSkip.None);
        }
        public TimeSpan UserIdle => TimeSpan.Zero;
        public void Release() { }
    }

    private static void EnqueueUtterance(ControlledCapture capture, int quietBefore, int speech, int quietAfter)
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

    [Fact]
    public async Task StaleStopAndReleaseCannotCancelTheNextAction()
    {
        await using var fixture = await LiveFixture.Create();
        var old = fixture.Start();
        await fixture.Finish(old);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Llm.Respond = async (_, _) => { entered.TrySetResult(); await release.Task; return TextRecordingHandler.Sse(Harness.Trace("New response.")); };
        var next = fixture.Start();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        fixture.Controller.Stop(old);
        old.ReleasePress();
        Assert.False(next.Authorization.IsCanceled);
        release.SetResult();
        await fixture.Finish(next);
        Assert.NotEqual(old.Id, next.Id);
        Assert.NotEqual(old.Turn!.TurnId, next.Turn!.TurnId);
        Assert.True(next.Turn.Epoch > old.Turn.Epoch);
        Assert.Equal(ConversationState.Completed, next.Turn.Snapshot.State);
    }

    [Theory]
    [InlineData(ErrorCode.AudioAccessDenied)]
    [InlineData(ErrorCode.AudioDeviceLost)]
    [InlineData(ErrorCode.AudioDeviceChanged)]
    public async Task CaptureFailuresNeverUploadAndTypedFallbackRequiresNewAction(ErrorCode code)
    {
        await using var fixture = await LiveFixture.Create();
        fixture.Capture.ReadFailure = new CaptureDeviceException(code);
        var operation = fixture.Start(microphone: true);
        await fixture.Finish(operation);
        Assert.Equal(code, operation.Status.AudioFailure);
        Assert.Equal(0, fixture.Stt.Calls);
        Assert.Equal(0, fixture.Llm.Calls);
        Assert.Equal(0, operation.Capture!.Snapshot.RetainedPcmBytes);
        await fixture.Finish(fixture.Start());
        Assert.Equal(1, fixture.Llm.Calls);
        Assert.Equal(1, fixture.Capture.Opens);
    }

    [Fact]
    public void AlwaysListeningWaitsLongerAfterEachMicrophoneFailureInARow() =>
        Assert.Equal([1, 1, 5, 10, 20, 30, 30, 30],
            new[] { 0, 1, 2, 3, 4, 5, 6, 100 }.Select(failures => LiveConversationController.MicrophoneRetry(failures).TotalSeconds));

    [Fact]
    public Task AlwaysListeningBacksOffAMicrophoneThatKeepsFailingAndSaysHowOftenItTries() => DispatcherTest(async () =>
    {
        await using var fixture = await LiveFixture.Create();
        fixture.Answer("Heard you.");
        fixture.Capture.OpenFailure = new CaptureDeviceException(ErrorCode.AudioDeviceChanged);
        var window = fixture.Open(new TalkPreferences(SpeakReplies: false));
        try
        {
            await Loaded(window);
            Click(window, "MicChip");
            // Soon after the first failure, then longer with each one in a row; the microphone stays closed for each wait.
            foreach (var (opens, wait) in new[] { (1, 1), (2, 5), (3, 10) })
            {
                var status = $"Martlet can't open the microphone. Check that it is connected and enabled. Martlet keeps trying every {wait} s.";
                await fixture.Advance(() => fixture.Capture.Opens == opens && window.Listener?.Retry == TimeSpan.FromSeconds(wait));
                await Task.Delay(50);
                fixture.Clock.Advance(TimeSpan.FromSeconds(wait) - TimeSpan.FromMilliseconds(200));
                // The talk window shows it on its own (real-time) tick; the fixture clock stays where it is meanwhile.
                await Until(() => window.ListeningStatus == (status, true));
                Assert.Equal(opens, fixture.Capture.Opens);
            }
            // It works again: the next utterance is heard and answered, and the failures in a row are over.
            fixture.Capture.OpenFailure = null;
            EnqueueUtterance(fixture.Capture, quietBefore: 5, speech: 25, quietAfter: 15);
            await fixture.Advance(() => window.Messages.Any(m => m.Role == ChatRole.Martlet && m.Text.Contains("Heard you.", StringComparison.Ordinal)));
            Assert.True(fixture.Capture.Opens >= 4);
            Assert.Null(window.Listener!.Retry);
            Assert.False(window.ListeningStatus.Problem);
            Assert.Equal(1, fixture.Stt.Calls);
        }
        finally { window.Close(); }
    });

    [Fact]
    public async Task TranscriptIntentStartsAtActualReceiptWithoutRenewingAnOldPermit()
    {
        await using var fixture = await LiveFixture.Create();
        fixture.Capture.Packets.Enqueue(new byte[3200]);
        fixture.Stt.Respond = (_, _) =>
        {
            fixture.Clock.Advance(TimeSpan.FromSeconds(8));
            return Task.FromResult(ProviderFixtures.Json());
        };
        var operation = fixture.Start(microphone: true);
        await Until(() => operation.Capture?.Snapshot.CanonicalSamples > 0);
        operation.ReleasePress();
        await fixture.Finish(operation);
        Assert.Equal(ConversationState.Completed, operation.Turn!.Snapshot.State);
        Assert.Equal(1, fixture.Controller.PolicySnapshot.AcceptedThroughIntentId);
        Assert.Equal(0, fixture.Controller.PolicySnapshot.RecentUnsolicitedDispatches);
    }

    [Theory]
    [InlineData(1168, CredentialError.Missing)]
    [InlineData(5, CredentialError.AccessDenied)]
    [InlineData(87, CredentialError.InvalidInput)]
    public async Task NativeCredentialFailureHasTypedRemedyAndNeverSends(int nativeError, CredentialError expected)
    {
        await using var fixture = await LiveFixture.Create();
        fixture.Native.Error = nativeError;
        var operation = fixture.Start();
        await fixture.Finish(operation);
        Assert.Equal(expected, operation.Authorization.CredentialFailure);
        Assert.Equal(ProviderFailureCode.CredentialUnavailable, operation.Turn!.Snapshot.ProviderFailure);
        Assert.Equal(0, fixture.Llm.Calls);
        Assert.All(fixture.Native.Leases, lease => Assert.Throws<ObjectDisposedException>(() => lease.Use(_ => { })));
        Assert.DoesNotContain(LiveFixture.Secret, JsonSerializer.Serialize(operation.Status));
        Assert.DoesNotContain(LiveFixture.Secret, operation.Authorization.ToString());
    }

    [Theory]
    [InlineData(ProviderRole.Llm, "https://api.openai.com")]
    [InlineData(ProviderRole.Stt, "https://api.openai.com")]
    [InlineData(ProviderRole.Llm, "https://wrong.invalid")]
    public async Task CredentialBridgeRequiresMatchingActiveAuthorizedRoleBeforeNativeAccess(ProviderRole role, string origin)
    {
        await using var fixture = await LiveFixture.Create();
        var authorization = new ConversationAuthorization(fixture.Controller.Configuration!, false, false, fixture.Clock,
            () => true, fixture.Settings.LoadAsync, new WindowsCredentialStore(fixture.Native), CancellationToken.None);
        var source = new ConversationCredentialSource(() => authorization);
        await Assert.ThrowsAsync<CredentialUnavailableException>(async () =>
            await source.ResolveAsync(new(new Uri(origin), role, TextFixtures.Model), CancellationToken.None));
        fixture.NoEffects();
    }

    [Fact]
    public async Task DelayedPressKeepsOriginalCaptureExpiryAcrossUtcRollback()
    {
        await using var fixture = await LiveFixture.Create();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Settings.BeforeLoad = async _ => { entered.TrySetResult(); await release.Task; };
        fixture.Capture.Packets.Enqueue(new byte[3200]);
        var operation = fixture.Start(microphone: true);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        fixture.Clock.DeferCallbacks = true;
        fixture.Clock.Advance(TimeSpan.FromSeconds(28));
        fixture.Clock.ShiftUtc(TimeSpan.FromHours(-1));
        release.SetResult();
        await Until(() => operation.Capture?.Snapshot.CanonicalSamples > 0);
        fixture.Clock.Advance(TimeSpan.FromSeconds(2));
        operation.ReleasePress();
        await fixture.Finish(operation, advance: false);
        Assert.Equal(CaptureEndReason.AuthorizationExpired, operation.Capture!.Snapshot.EndReason);
        Assert.Equal(0, operation.Capture.Snapshot.RetainedPcmBytes);
        Assert.Equal(0, fixture.Stt.Calls);
        Assert.Equal(0, fixture.Llm.Calls);
    }

    [Fact]
    public async Task CaptureCleanupAfterExpiryNeverRescuesOldPcmForUpload()
    {
        await using var fixture = await LiveFixture.Create();
        using var release = new ManualResetEventSlim();
        fixture.Capture.DisposeBlock = release;
        fixture.Capture.Packets.Enqueue(new byte[3200]);
        var operation = fixture.Start(microphone: true);
        await Until(() => operation.Capture?.Snapshot.CanonicalSamples > 0);
        operation.ReleasePress();
        await Until(() => fixture.Capture.DisposeEntered.IsSet);
        fixture.Clock.DeferCallbacks = true;
        fixture.Clock.Advance(TimeSpan.FromSeconds(31));
        fixture.Clock.ShiftUtc(TimeSpan.FromHours(-1));
        Assert.True(fixture.Runner.IsRunning);
        release.Set();
        await fixture.Finish(operation, advance: false);
        Assert.Equal(0, fixture.Stt.Calls);
        Assert.Equal(0, fixture.Llm.Calls);
        Assert.Equal(0, operation.Capture!.Snapshot.RetainedPcmBytes);
    }

    [Fact]
    public async Task PartialTtsPreservesTextAndPlayedAccountingWithoutRetry()
    {
        await using var fixture = await LiveFixture.Create();
        var waiting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var body = new FragmentedTextBody(SpeechFixtures.Audio(4800).Concat(new byte[] { 1 }).ToArray(), 960);
        body.BeforeRead = async _ =>
        {
            if (body.BytesRead >= 9600) { waiting.TrySetResult(); await release.Task; }
        };
        fixture.Tts.Respond = (_, _) => Task.FromResult(SpeechFixtures.Pcm(body));
        var operation = fixture.Start(voice: true);
        await waiting.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Until(() => fixture.Output.Starts > 0 && fixture.Output.Samples > 0);
        release.SetResult();
        await fixture.Finish(operation);
        // A voice cut off partway leaves the reply complete; only the voice stopped.
        Assert.Equal(ConversationState.Completed, operation.Turn!.Snapshot.State);
        Assert.True(operation.Turn.Snapshot.SpeechFailed);
        Assert.Equal(ProviderFailureCode.ResponseTruncated, operation.Turn.Snapshot.ProviderFailure);
        Assert.True(operation.Turn.Snapshot.MayHavePlayed);
        Assert.Equal("Hello fixture.", operation.Turn.Content.Text);
        Assert.Equal(1, fixture.Llm.Calls);
        Assert.Equal(1, fixture.Tts.Calls);
        Assert.Equal(1, fixture.Output.Opens);
        Assert.True(operation.OwnershipReleased);
    }

    [Fact]
    public async Task OutputLossKeepsTextAndNeverSelectsAnotherDevice()
    {
        await using var fixture = await LiveFixture.Create(new ControlledDevice { PaddingError = ErrorCode.AudioDeviceLost });
        var operation = fixture.Start(voice: true);
        await fixture.Finish(operation);
        Assert.Equal(ConversationState.Completed, operation.Turn!.Snapshot.State);
        Assert.Equal(ConversationFailure.PlaybackFailed, operation.Turn.Snapshot.SpeechFailure);
        Assert.Equal("Hello fixture.", operation.Turn.Content.Text);
        Assert.Equal(1, fixture.Output.Opens);
        Assert.Equal("private-output-id", fixture.Output.Selection!.EndpointId);
        Assert.Equal(ErrorCode.AudioDeviceLost, operation.Turn.Snapshot.Playback!.Error!.Code);
        await fixture.Finish(fixture.Start());
        Assert.Equal(1, fixture.Output.Opens);
    }

    [Fact]
    public async Task FailedNativeCleanupQuarantinesAppWideOwnerAfterVisibleTerminal()
    {
        await using var fixture = await LiveFixture.Create(new ControlledDevice { FailDispose = true });
        var operation = fixture.Start(voice: true);
        await Until(() => operation.Turn?.Completion.IsCompleted == true);
        await Until(() => operation.Status.Quarantined);
        Assert.Equal("Hello fixture.", operation.Turn!.Content.Text);
        Assert.True(fixture.Runner.IsRunning);
        Assert.False(operation.OwnershipReleased);
        Assert.NotNull(fixture.Controller.PolicySnapshot.ActiveIntentId);
        Assert.Throws<LiveActionException>(() => fixture.Start());
        Assert.Null(fixture.Runner.TryStart(_ => Task.FromResult(new SetupWorkResult(SetupWorkOutcome.Completed))));
        Assert.Equal(1, fixture.Output.Opens);
    }

    [Fact]
    public async Task SttDeadlineReportsWhileNoncooperativeVaultStillOwnsSlot()
    {
        await using var fixture = await LiveFixture.Create();
        fixture.Native.Block = true;
        fixture.Capture.Packets.Enqueue(new byte[3200]);
        var operation = fixture.Start(microphone: true);
        await Until(() => operation.Capture?.Snapshot.CanonicalSamples > 0);
        operation.ReleasePress();
        await fixture.Native.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        fixture.Clock.Advance(TimeSpan.FromSeconds(31));
        await Until(() => operation.Status.Code == "stt.deadline_exceeded");
        Assert.True(fixture.Runner.IsRunning);
        Assert.Null(fixture.Controller.PolicySnapshot.ActiveIntentId);
        Assert.Equal(0, fixture.Stt.Calls);
        fixture.Native.Release.Set();
        await fixture.Finish(operation);
        Assert.Equal("stt.deadline_exceeded", operation.Status.Code);
        Assert.Equal(0, fixture.Stt.Calls);
        Assert.Equal(0, fixture.Llm.Calls);
        Assert.Equal(0, operation.Capture!.Snapshot.RetainedPcmBytes);
    }

    [Fact]
    public Task MessagesFromPairedChatsJoinTheConversationAndGetTheReplyEvenWhileLocked() => DispatcherTest(async () =>
    {
        await using var fixture = await LiveFixture.Create();
        var window = fixture.Open();
        try
        {
            await Loaded(window);
            fixture.Answer("Hello from the PC.");
            var reply = window.AskFromMessage("hi there", "Sam (Telegram)", speak: false, CancellationToken.None);
            await fixture.Advance(() => reply.IsCompleted);
            Assert.Equal("Hello from the PC.", await reply);
            Assert.Contains(window.Messages, message => message.IsUser && message.Text == "hi there" && message.Caption.StartsWith("Sam (Telegram)"));
            Assert.Contains(window.Messages, message => message.Role == ChatRole.Martlet && message.Text == "Hello from the PC.");

            fixture.Events.Signal(true);
            await fixture.Advance(() => fixture.Controller.Controls.Locked);
            fixture.Answer("Still here.");
            reply = window.AskFromMessage("are you there?", "Sam (Telegram)", speak: true, CancellationToken.None);
            await fixture.Advance(() => reply.IsCompleted);
            Assert.Equal("Still here.", await reply);
            Assert.Equal(2, fixture.Llm.Calls);
            Assert.Equal(0, window.RemoteWaiting);
        }
        finally { window.Close(); }
    });

    [Fact]
    public Task LostHeldKeyboardFocusCancelsCaptureInsteadOfSending() => DispatcherTest(async () =>
    {
        await using var fixture = await LiveFixture.Create();
        fixture.Capture.Packets.Enqueue(new byte[3200]);
        var window = fixture.Open();
        try
        {
            await Loaded(window);
            SendKey(window, Key.Space, down: true);
            await Until(() => fixture.Capture.Reads > 0);
            var ptt = Control<Button>(window, "PttButton");
            ptt.RaiseEvent(new KeyboardFocusChangedEventArgs(Keyboard.PrimaryDevice, 0, ptt, Control<TextBox>(window, "InputText"))
            { RoutedEvent = Keyboard.LostKeyboardFocusEvent });
            SendKey(window, Key.Space, down: false);
            await fixture.Finish();
            Assert.Equal(0, fixture.Stt.Calls);
            Assert.Equal(0, fixture.Llm.Calls);
            Assert.Equal(1, fixture.Capture.Opens);
            Assert.Empty(window.Messages);
        }
        finally { window.Close(); }
    });

    // A bubble is as wide as its words, never stretched to a longer caption or note under it (the tone and emotes, "Martlet saw
    // your whole screen."). Martlet's bubbles keep to the left, yours to the right, and a long reply still wraps within the row.
    [Fact]
    public Task BubblesFitTheirWords() => DispatcherTest(async () =>
    {
        await using var fixture = await LiveFixture.Create();
        var window = fixture.Open();
        try
        {
            await Loaded(window);
            window.Width = 720;
            window.Messages.Add(new ChatMessage(ChatRole.Martlet, "Oh, you too, my love!", "Martlet · 10:32 PM")
                { Note = "Tone: happy. Emotes: smile, hearts." });
            window.Messages.Add(new ChatMessage(ChatRole.User, "Soon to", "You (spoken) · 10:32 PM")
                { Note = "Martlet saw your whole screen." });
            window.Messages.Add(new ChatMessage(ChatRole.User, "Hi", "You · 10:33 PM"));
            window.Messages.Add(new ChatMessage(ChatRole.Martlet,
                string.Join(' ', Enumerable.Repeat("This reply is long enough to wrap.", 12)), "Martlet · 10:33 PM"));
            var bodies = Bodies(window);
            Assert.Equal(4, bodies.Length);
            foreach (var body in bodies[..3])
            {
                var bubble = Ancestor<Border>(body, "Bubble");
                var row = Ancestor<StackPanel>(body, "Row");
                var blank = body.ActualWidth - body.GetRectFromCharacterIndex(body.Text.Length - 1, trailingEdge: true).Right;
                Assert.True(blank < 4, $"\"{body.Text}\": {blank:0.#} px blank after the words in a {bubble.ActualWidth:0.#} px bubble.");
                var left = bubble.TranslatePoint(new Point(0, 0), row).X;
                if (body.DataContext is ChatMessage { IsUser: true })
                    Assert.Equal(row.ActualWidth, left + bubble.ActualWidth, 1);
                else Assert.Equal(0, left, 1);
            }
            var wrapped = bodies[3];
            Assert.True(wrapped.LineCount > 1);
            Assert.InRange(Ancestor<Border>(wrapped, "Bubble").ActualWidth, 400, 580);
        }
        finally { window.Close(); }
    });

    [Theory]
    [InlineData(550, 450)]
    [InlineData(920, 850)]
    public Task StopRemainsVisibleAndClickableAtEveryScrollPosition(double width, double height) => DispatcherTest(async () =>
    {
        await using var fixture = await LiveFixture.Create();
        fixture.Native.Block = true;
        var window = fixture.Open();
        try
        {
            await Loaded(window);
            window.Width = width;
            window.Height = height;
            for (var i = 0; i < 40; i++) window.Messages.Add(new ChatMessage(ChatRole.Note, $"Earlier note {i}."));
            Control<TextBox>(window, "InputText").Text = "test";
            Click(window, "SendButton");
            await fixture.Native.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var content = Assert.IsAssignableFrom<FrameworkElement>(window.Content);
            var scroll = Control<ScrollViewer>(window, "HistoryScroll");
            var stop = Control<Button>(window, "StopButton");
            Assert.True(stop.IsEnabled);
            window.UpdateLayout();
            Assert.True(scroll.ScrollableHeight > 0);
            Point? fixedPosition = null;
            foreach (double fraction in new[] { 0.0, 0.5, 1.0 })
            {
                scroll.ScrollToVerticalOffset(scroll.ScrollableHeight * fraction);
                window.UpdateLayout();
                var bounds = stop.TransformToAncestor(content).TransformBounds(new Rect(stop.RenderSize));
                Assert.True(new Rect(content.RenderSize).Contains(bounds), $"Stop outside window content: {bounds}");
                var hit = Assert.IsAssignableFrom<DependencyObject>(content.InputHitTest(
                    new Point(bounds.X + bounds.Width / 2, bounds.Y + bounds.Height / 2)));
                while (hit is FrameworkContentElement element)
                    hit = Assert.IsAssignableFrom<DependencyObject>(element.Parent);
                Assert.True(ReferenceEquals(hit, stop) || stop.IsAncestorOf(hit), "Stop is clipped or covered.");
                if (fixedPosition is { } position) Assert.Equal(position, bounds.TopLeft);
                fixedPosition = bounds.TopLeft;
            }
            Assert.Equal("Esc", System.Windows.Automation.AutomationProperties.GetAcceleratorKey(stop));
            Assert.Equal(0, fixture.Llm.Calls);
        }
        finally { fixture.Native.Release.Set(); window.Close(); await fixture.Finish(); }
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task StopOrEscapeWhileIdleStartsNoWorkAndKeepsTheDraft(bool escape) => DispatcherTest(async () =>
    {
        await using var fixture = await LiveFixture.Create();
        await fixture.EnableMemory();
        var window = fixture.Open(new TalkPreferences(HandsFree: false, SpeakReplies: true));
        try
        {
            await Loaded(window);
            Control<TextBox>(window, "InputText").Text = "draft";
            Assert.False(Control<Button>(window, "StopButton").IsEnabled);
            if (escape) Escape(window, "InputText");
            else Click(window, "StopButton");
            await Heartbeat();
            Assert.Equal("draft", Control<TextBox>(window, "InputText").Text);
            Assert.Empty(window.Messages);
            Assert.False(Control<Button>(window, "StopButton").IsEnabled);
            fixture.NoEffects();
        }
        finally { window.Close(); }
    });

    [Fact]
    public Task EscapeDiscardsHeldPttAndLateSpaceReleaseCannotUploadOrRearm() => DispatcherTest(async () =>
    {
        await using var fixture = await LiveFixture.Create();
        fixture.Capture.Packets.Enqueue(new byte[3200]);
        var window = fixture.Open(new TalkPreferences(HandsFree: false, SpeakReplies: true));
        try
        {
            await Loaded(window);
            SendKey(window, Key.Space, down: true);
            await Until(() => fixture.Capture.Reads > 0);
            Escape(window, "PttButton");
            SendKey(window, Key.Space, down: false);
            await fixture.Finish();
            var discarded = Assert.IsType<LiveConversationOperation>(window.Current);
            await Until(() => discarded.OwnershipReleased);
            Assert.Equal("conversation.canceled", discarded.Status.Code);
            Assert.Equal(0, discarded.Capture!.Snapshot.RetainedPcmBytes);
            Assert.Empty(window.Messages);
            await Until(() => Text(window, "ResultText") == "Stopped.");
            Assert.Equal(1, fixture.Capture.Opens);
            Assert.Equal(1, fixture.Capture.Stops);
            Assert.Equal(1, fixture.Capture.Disposals);
            Assert.Empty(fixture.Native.Targets);
            Assert.Equal(0, fixture.Stt.Calls);
            Assert.Equal(0, fixture.Llm.Calls);
            Assert.Equal(0, fixture.Tts.Calls);
            Assert.Equal(0, fixture.Output.Opens);
        }
        finally { window.Close(); }
    });

    [Fact]
    public Task EscapeFromResponseStopsPlaybackAndPreservesTextWithoutReplay() => DispatcherTest(async () =>
    {
        await using var fixture = await LiveFixture.Create(new ControlledDevice { AutoConsume = false });
        fixture.Answer("Retained response.");
        var window = fixture.Open(new TalkPreferences(HandsFree: false, SpeakReplies: true));
        try
        {
            await Loaded(window);
            Control<TextBox>(window, "InputText").Text = "test";
            Click(window, "SendButton");
            await Until(() => fixture.Output.Starts > 0);
            Escape(window, "InputText");
            await fixture.Finish();
            var stopped = Assert.IsType<LiveConversationOperation>(window.Current);
            await Until(() => stopped.OwnershipReleased && window.Messages.Any(m => m.Role == ChatRole.Martlet && m.HasNote));
            Assert.Contains("Retained response.", Assert.Single(window.Messages, m => m.Role == ChatRole.Martlet).Text);
            Assert.Equal("conversation.canceled", stopped.Status.Code);
            Assert.True(fixture.Output.Samples > 0);
            Assert.Equal(1, fixture.Output.Stops);
            Assert.Equal(1, fixture.Output.Disposals);
            Assert.Equal(1, fixture.Llm.Calls);
            Assert.Equal(1, fixture.Tts.Calls);
            Escape(window, "InputText");
            Assert.Equal(1, fixture.Output.Opens);
            Assert.Equal(1, fixture.Tts.Calls);
        }
        finally { window.Close(); }
    });

    [Fact]
    public Task MutingTheVoiceSilencesTheReplyBeingSpokenAndKeepsItsText() => DispatcherTest(async () =>
    {
        await using var fixture = await LiveFixture.Create(new ControlledDevice { AutoConsume = false });
        fixture.Answer("Retained response.");
        var spoken = new TalkPreferences(HandsFree: false, SpeakReplies: true);
        var window = fixture.Open(spoken);
        try
        {
            await Loaded(window);
            Control<TextBox>(window, "InputText").Text = "test";
            Click(window, "SendButton");
            await Until(() => fixture.Output.Starts > 0);
            // Mute voice on the character's menu (or Speak Martlet's replies aloud off) while Martlet speaks.
            window.UsePreferences(spoken with { SpeakReplies = false }, null);
            await fixture.Finish();
            var muted = Assert.IsType<LiveConversationOperation>(window.Current);
            await Until(() => muted.OwnershipReleased && window.Messages.Any(m => m.Role == ChatRole.Martlet && m.HasNote));
            var reply = Assert.Single(window.Messages, m => m.Role == ChatRole.Martlet);
            Assert.Contains("Retained response.", reply.Text);
            Assert.Contains("Muted partway", reply.Note);
            // Not canceled and not a voice failure: the reply completed, only what was said aloud ended.
            Assert.Equal("runtime.Completed", muted.Status.Code);
            Assert.Equal(ConversationState.Completed, muted.Turn!.Snapshot.State);
            Assert.True(muted.Turn.Snapshot.VoiceMuted);
            Assert.False(muted.Turn.Snapshot.SpeechFailed);
            Assert.Equal(1, fixture.Output.Opens);
            Assert.Equal(1, fixture.Output.Stops);
            Assert.Equal(1, fixture.Tts.Calls);
        }
        finally { window.Close(); }
    });

    [Fact]
    public Task ATouchStopsTheSpokenReplyAndTheReactionKnowsWhatMartletWasSaying() => DispatcherTest(async () =>
    {
        await using var fixture = await LiveFixture.Create(new ControlledDevice { AutoConsume = false });
        fixture.Answer("Once upon a time there was a fox.", " It lived in a quiet wood.");
        var window = fixture.Open(new TalkPreferences(HandsFree: false, SpeakReplies: true));
        try
        {
            await Loaded(window);
            Control<TextBox>(window, "InputText").Text = "Tell me a story.";
            Click(window, "SendButton");
            await Until(() => fixture.Output.Starts > 0);
            var story = Assert.IsType<LiveConversationOperation>(window.Current);
            await Until(() => story.Turn?.SaidAloud.Length > 0);
            Assert.Equal("Tell me a story.", story.Asked);

            // A poke on an intimate part while Martlet tells it: it stops at once, like talking over it.
            fixture.Answer("Eek! Hey, I was telling a story!");
            window.Physical(new PhysicalEvent(PhysicalKind.Tap, fixture.Controller.TouchNow, "your groin", "groin", Intimate: true));
            Assert.Contains("Martlet stopped talking for it.", window.TouchStatus);
            await fixture.Advance(() => story.OwnershipReleased);
            Assert.Equal(LiveConversationWindow.TouchCutInCode, story.Status.Code);
            await Until(() => window.Messages.Any(m => m.Role == ChatRole.Martlet && m.Note.Contains("Stopped for your touch.")));

            // Its reaction starts about half a second after the touch, on its own, and knows what it was saying.
            await fixture.Advance(() => fixture.Llm.Calls >= 2);
            using var body = JsonDocument.Parse(fixture.Llm.Body);
            var message = ResponsesCurrentUserText(body);
            Assert.Contains("They poked your groin once. They did it while you were talking, so you stopped mid-sentence. You had said, " +
                "out loud: \"Once upon a time there was a fox.\" You were answering their message: \"Tell me a story.\"", message);
            Assert.True(Assert.IsType<LiveConversationOperation>(window.Current).Touch);
        }
        finally { window.Close(); }
    });

    [Fact]
    public Task ATouchThatIsNotIntimateWaitsWhileMartletTalksWhenOnlyIntimateTouchesStopIt() => DispatcherTest(async () =>
    {
        await using var fixture = await LiveFixture.Create(new ControlledDevice { AutoConsume = false });
        fixture.Answer("Once upon a time there was a fox.");
        var window = fixture.Open(new TalkPreferences(HandsFree: false, SpeakReplies: true, TouchInterrupts: TouchInterrupts.Intimate));
        try
        {
            await Loaded(window);
            Control<TextBox>(window, "InputText").Text = "Tell me a story.";
            Click(window, "SendButton");
            await Until(() => fixture.Output.Starts > 0);
            var story = Assert.IsType<LiveConversationOperation>(window.Current);
            // Only intimate touches stop it: a pat on the head waits for it to finish.
            window.Physical(new PhysicalEvent(PhysicalKind.Pat, fixture.Controller.TouchNow, "the top of your head", "top of head"));
            await Heartbeat();
            Assert.False(story.OwnershipReleased);
            Assert.Equal(1, fixture.Llm.Calls);
            Assert.DoesNotContain("stopped talking", window.TouchStatus);
        }
        finally { window.Close(); }
    });

    [Fact]
    public Task EscapeDuringSettingsLoadRetainsOwnershipUntilWorkerReturns() => DispatcherTest(async () =>
    {
        await using var fixture = await LiveFixture.Create();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Settings.BeforeLoad = async _ => { entered.TrySetResult(); await release.Task; };
        var window = fixture.Open();
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(Control<Button>(window, "StopButton").IsEnabled);
            Escape(window, "InputText");
            await Heartbeat();
            Assert.True(fixture.Runner.IsRunning);
            Assert.Null(fixture.Runner.TryStart(_ => Task.FromResult(new SetupWorkResult(SetupWorkOutcome.Completed))));
            Assert.False(Control<Button>(window, "SendButton").IsEnabled);
            fixture.NoEffects();
            release.TrySetResult();
            await fixture.Finish();
            Assert.False(Control<Button>(window, "SendButton").IsEnabled);
            fixture.NoEffects();
        }
        finally { release.TrySetResult(); window.Close(); }
    });

    [Fact]
    public async Task MartletDecidesHowChattyItIsWithATagAtTheEndOfAReply()
    {
        await using var fixture = await LiveFixture.Create();
        var switched = new ConcurrentQueue<(Chattiness Before, Chattiness Level)>();
        fixture.Controller.ChattinessDecided += (before, level) => switched.Enqueue((before, level));
        Assert.Equal(Chattiness.Normal, fixture.Controller.DecidedChattiness);
        LiveConversationOperation Say(string text, ChattinessChoice? choice) =>
            fixture.Controller.Start(text, voice: false, microphone: false, approved: true, chattiness: choice);
        static string[] Earlier(JsonDocument body) => [.. body.RootElement.GetProperty("input").EnumerateArray().SkipLast(1)
            .Select(item => item.GetProperty("content").GetString()!)];

        // Asked for quiet, the reply switches the level with a tag at its very end: never shown, spoken or kept.
        fixture.Answer("Sure, I'll keep it down. ", "[chattiness:", "quiet]");
        var first = Say("Shh, I'm concentrating.", ChattinessChoice.MartletDecides);
        await fixture.Finish(first);
        Assert.Equal("runtime.Completed", first.Status.Code);
        Assert.Equal("Sure, I'll keep it down.", first.Turn!.Content.Text);
        Assert.Equal(["[chattiness:quiet]"], first.Turn.Controls);
        Assert.Equal(Chattiness.Quiet, fixture.Controller.DecidedChattiness);
        Assert.Equal((Chattiness.Normal, Chattiness.Quiet), Assert.Single(switched));
        string instructions;
        using (var body = JsonDocument.Parse(fixture.Llm.Body))
        {
            instructions = body.RootElement.GetProperty("instructions").GetString()!;
            Assert.Contains(LiveConversationConfiguration.ChattinessDecides(null)!, instructions);
            Assert.Contains("[chattiness:quiet]", instructions);
            Assert.Contains("Your chattiness right now: normal.", ResponsesCurrentNotes(body));
        }

        // The next reply's notes say the new level once; the instructions stay exactly the same, so the prompt cache holds.
        fixture.Answer("Okay.");
        await fixture.Finish(Say("Thanks.", ChattinessChoice.MartletDecides));
        using (var body = JsonDocument.Parse(fixture.Llm.Body))
        {
            Assert.Equal(instructions, body.RootElement.GetProperty("instructions").GetString());
            Assert.Contains("Your chattiness right now: quiet.", ResponsesCurrentNotes(body));
            Assert.Contains("Sure, I'll keep it down.", Earlier(body));
            Assert.DoesNotContain(Earlier(body), text => text.Contains("[chattiness", StringComparison.OrdinalIgnoreCase));
        }
        fixture.Answer("Mm-hmm.");
        await fixture.Finish(Say("Still here?", ChattinessChoice.MartletDecides));
        using (var body = JsonDocument.Parse(fixture.Llm.Body))
        {
            Assert.Equal(instructions, body.RootElement.GetProperty("instructions").GetString());
            Assert.DoesNotContain("Your chattiness right now", ResponsesCurrentUserText(body), StringComparison.Ordinal);
        }
        Assert.Single(switched);

        // With a fixed level (or nothing in the background) replies aren't told about it, and a stray tag switches nothing.
        fixture.Answer("Fine. [chattiness:chatty]");
        await fixture.Finish(Say("Hello.", ChattinessChoice.Normal));
        using (var body = JsonDocument.Parse(fixture.Llm.Body))
            Assert.DoesNotContain("You decide how chatty you are", body.RootElement.GetProperty("instructions").GetString());
        fixture.Answer("Sure. [chattiness:chatty]");
        await fixture.Finish(Say("Hello again.", null));
        Assert.Equal(Chattiness.Quiet, fixture.Controller.DecidedChattiness);
        Assert.Single(switched);
    }

    [Fact]
    public async Task AGlanceWhileMartletDecidesMayStayQuietAndStillSwitchTheLevel()
    {
        await using var fixture = await LiveFixture.Create();
        var image = new BoundedImage([0xFF, 0xD8, 0xFF, .. new byte[32]], ImageMediaType.Jpeg, 4, 4);
        fixture.Answer("[pass] ", "[chattiness:chatty]");
        var glance = fixture.Controller.StartCommentary(image, "Boss fight", ChattinessChoice.MartletDecides, voice: false,
            screenApproved: true);
        await fixture.Finish(glance);
        Assert.True(glance.Passed);
        Assert.Equal("commentary.passed", glance.Status.Code);
        Assert.Equal(Chattiness.Chatty, fixture.Controller.DecidedChattiness);
        using (var body = JsonDocument.Parse(fixture.Llm.Body))
        {
            var instructions = body.RootElement.GetProperty("instructions").GetString()!;
            // The look's prompt, then how to switch: the same at every level, without a fixed level's line.
            Assert.EndsWith(LiveConversationConfiguration.CommentaryInstructions(Chattiness.Quiet, decides: true)!, instructions);
            Assert.Equal(LiveConversationConfiguration.CommentaryInstructions(Chattiness.Quiet, decides: true),
                LiveConversationConfiguration.CommentaryInstructions(Chattiness.Chatty, decides: true));
            Assert.DoesNotContain(PromptCatalog.Default(PromptCatalog.ChattinessNormal).Replace("{silent}", "pass"), instructions);
            Assert.Contains("Your chattiness right now: normal.", ResponsesCurrentNotes(body));
        }

        // A fixed level keeps its own line in the glance instructions and isn't told how to switch.
        fixture.Answer("[pass]");
        var fixedGlance = fixture.Controller.StartCommentary(image, "Boss fight", ChattinessChoice.Quiet, voice: false, screenApproved: true);
        await fixture.Finish(fixedGlance);
        using (var body = JsonDocument.Parse(fixture.Llm.Body))
        {
            var instructions = body.RootElement.GetProperty("instructions").GetString()!;
            Assert.EndsWith(LiveConversationConfiguration.CommentaryInstructions(Chattiness.Quiet)!, instructions);
            Assert.DoesNotContain("You decide how chatty you are", instructions);
        }
        Assert.Equal(Chattiness.Chatty, fixture.Controller.DecidedChattiness);
    }

    [Fact]
    public Task TheTalkWindowSaysWhatMartletDecidedAndNotesTheSwitch() => DispatcherTest(async () =>
    {
        await using var fixture = await LiveFixture.Create();
        // Hearing this PC is on (push-to-talk never hears it, so nothing is captured): Martlet decides applies to replies.
        var window = fixture.Open(new TalkPreferences(HandsFree: false, SpeakReplies: false, HearPc: true,
            ScreenChattiness: (int)ChattinessChoice.MartletDecides));
        try
        {
            await Loaded(window);
            Assert.Equal("Chattiness: normal (Martlet decides).", Control<TextBlock>(window, "ChattinessText").Text);
            Assert.Equal(Visibility.Visible, Control<TextBlock>(window, "ChattinessText").Visibility);
            fixture.Answer("Got it, I'll hush. ", "[chattiness:quiet]");
            Control<TextBox>(window, "InputText").Text = "Please be quiet for a bit.";
            Click(window, "SendButton");
            await fixture.Finish();
            await Until(() => window.Messages.Any(m => m.Role == ChatRole.Martlet && m.Text == "Got it, I'll hush."));
            await Until(() => Control<TextBlock>(window, "ChattinessText").Text == "Chattiness: quiet (Martlet decides).");
            Assert.StartsWith("Martlet picks how chatty it is about what it sees and hears and switched to quiet at ",
                AutomationProperties.GetHelpText(Control<TextBlock>(window, "ChattinessText")));
            Assert.Contains(window.Messages, m => m.Role == ChatRole.Note &&
                m.Text == LiveConversationWindow.ChattinessSwitched(Chattiness.Normal, Chattiness.Quiet));
            Assert.Contains("Your chattiness right now: normal.", Encoding.UTF8.GetString(fixture.Llm.Body));

            // A fixed level says nothing here (Companion shows it).
            window.UsePreferences(new TalkPreferences(HandsFree: false, SpeakReplies: false, HearPc: true,
                ScreenChattiness: (int)ChattinessChoice.Chatty), null);
            Assert.Equal(Visibility.Collapsed, Control<TextBlock>(window, "ChattinessText").Visibility);
        }
        finally { window.Close(); }
    });

    // The talk window calls the character by its persona's name (Companion › Personality): the header, the title, the message
    // box, the empty conversation, each reply's label and the notes in the history, such as a touch reply's "You touched Ivy".
    // Its accessible name stays "Talk with Martlet", so MCP's window list never carries the character's name.
    [Fact]
    public Task TheTalkWindowCallsTheCharacterByItsPersonasName() => DispatcherTest(async () =>
    {
        await using var fixture = await LiveFixture.Create();
        var loaded = await fixture.Store.LoadAsync();
        var persona = loaded.Settings!.Companion!.ActivePersona;
        await fixture.Save(loaded.Settings with { Companion = loaded.Settings.Companion.Update(persona.Id, "Ivy", persona.Text) });
        var window = fixture.Open();
        try
        {
            await Loaded(window);
            Assert.Equal("Ivy", window.CharacterName);
            Assert.Equal("Ivy", Control<TextBlock>(window, "CharacterNameText").Text);
            Assert.Equal("Talk with Ivy", window.Title);
            Assert.Equal("Message Ivy", Control<TextBlock>(window, "Placeholder").Text);
            Assert.Equal("Message Ivy", AutomationProperties.GetName(Control<TextBox>(window, "InputText")));
            Assert.Equal("Say hi to Ivy", Control<TextBlock>(window, "EmptyTitle").Text);
            Assert.Equal("Talk with Martlet", AutomationProperties.GetName(window));

            fixture.Answer("Hello there!");
            Control<TextBox>(window, "InputText").Text = "Hi!";
            Click(window, "SendButton");
            await fixture.Finish();
            await Until(() => window.Messages.Any(m => m.Role == ChatRole.Martlet && m.Text == "Hello there!"));

            // Touches on their own start a short reply of their own, noted with who was touched.
            fixture.Answer("Hey, that tickles!");
            window.Physical(new(PhysicalKind.Tap, fixture.Controller.TouchNow, "your left cheek", "left cheek"));
            window.Physical(new(PhysicalKind.Tap, fixture.Controller.TouchNow, "your left cheek", "left cheek"));
            await fixture.Advance(() => window.Messages.Any(m => m.Role == ChatRole.Martlet && m.Text == "Hey, that tickles!") &&
                window.Current is { OwnershipReleased: true });
            Assert.Contains(window.Messages, m => m.IsNote && m.Text == "You touched Ivy (touch: left cheek poke x2)");
            Assert.Equal(2, window.Messages.Count(m => m.Role == ChatRole.Martlet));
            Assert.All(window.Messages.Where(m => m.Role == ChatRole.Martlet), m => Assert.StartsWith("Ivy · ", m.Caption));
            Assert.DoesNotContain(window.Messages, m => (m.Caption + m.Text + m.Note).Contains("Martlet", StringComparison.Ordinal));
            Assert.Equal("Ivy went quiet about what it sees and hears. It speaks up only for something notable.",
                LiveConversationWindow.ChattinessSwitched(Chattiness.Normal, Chattiness.Quiet, window.CharacterName));
        }
        finally { window.Close(); }
    });

    [Fact]
    public Task BackgroundTasksShowAsAChipThatOpensTheTaskList() => DispatcherTest(async () =>
    {
        await using var fixture = await LiveFixture.Create();
        // Finished work waits for the next message, so nothing here brings it up on its own (no reply is asked for).
        var loaded = await fixture.Store.LoadAsync();
        await fixture.Save(loaded.Settings! with { Generation = new() { ThinkLonger = new() { Delivery = ThinkDelivery.NextMessage } } });
        var window = fixture.Open();
        try
        {
            await Loaded(window);
            var chip = Control<Button>(window, "TasksChip");
            Assert.Equal(Visibility.Collapsed, chip.Visibility);

            var think = new BackgroundJobKind(ThinkLonger.KindName, 2, 10, TimeSpan.FromMinutes(5), Doing: "Thinking about");
            var song = new BackgroundJobKind("song", 1, 10, TimeSpan.FromMinutes(5), Offer: true, Doing: "Making a song");
            var finish = new TaskCompletionSource<BackgroundJobOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);
            var first = fixture.Controller.Jobs.Start(think, "the trip plan", (job, token) =>
            {
                job.Report(BackgroundJobState.Running, "comparing the routes");
                return finish.Task.WaitAsync(token);
            }).Job!;
            var second = fixture.Controller.Jobs.Start(song, "a birthday song", async (_, token) =>
            {
                await Task.Delay(Timeout.Infinite, token);
                return BackgroundJobOutcome.Done("x");
            }).Job!;
            await Until(() => Control<TextBlock>(window, "TasksText").Text == "2 running");
            Assert.Equal(Visibility.Visible, chip.Visibility);
            Assert.Equal("Background tasks: 2 running", AutomationProperties.GetName(chip));
            Assert.Equal(Visibility.Collapsed, Control<Grid>(window, "TasksPanel").Visibility);

            // The chip opens the list: one card per task with what it is about, its status and Cancel.
            Click(window, "TasksChip");
            Assert.Equal(Visibility.Visible, Control<Grid>(window, "TasksPanel").Visibility);
            await Until(() => Find<TextBlock>(window, "LiveJobState-" + first.Id)?.Text == "Comparing the routes.");
            Assert.Equal("the trip plan", Find<TextBlock>(window, "LiveJob-" + first.Id)!.Text);
            Assert.Equal("Martlet keeps working on these while you talk. Stop (Esc) doesn't end them.",
                Control<TextBlock>(window, "JobsText").Text);

            // Cancel stops just that task; the list says so and the chip counts what still runs.
            Find<Button>(window, "LiveJobCancel-" + second.Id)!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Until(() => Find<TextBlock>(window, "LiveJobState-" + second.Id)?.Text == "You stopped it.");
            await Until(() => Control<TextBlock>(window, "TasksText").Text == "1 running");
            Assert.Equal(Visibility.Collapsed, Find<Button>(window, "LiveJobCancel-" + second.Id)!.Visibility);

            // Esc closes the list first, and stops nothing.
            Escape(window, "InputText");
            Assert.Equal(Visibility.Collapsed, Control<Grid>(window, "TasksPanel").Visibility);
            Assert.False(first.Finished);
            Click(window, "TasksChip");

            // A finished task waits to come up; its result shows on request.
            finish.SetResult(BackgroundJobOutcome.Done("Take the coast road."));
            await Until(() => Control<TextBlock>(window, "TasksText").Text == "1 ready");
            Assert.Equal("Done after 0:00. Martlet brings it up when you talk next.", Find<TextBlock>(window, "LiveJobState-" + first.Id)!.Text);
            Assert.Equal("Finished work comes up when you talk next.", Control<TextBlock>(window, "JobsText").Text);
            Assert.Equal(0, fixture.Llm.Calls);
            var toggle = Find<Button>(window, "LiveJobResultToggle-" + first.Id)!;
            Assert.Equal(Visibility.Visible, toggle.Visibility);
            toggle.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Equal(Visibility.Visible, Find<TextBox>(window, "LiveJobResult-" + first.Id)!.Visibility);
            Assert.Equal("Take the coast road.", Find<TextBox>(window, "LiveJobResult-" + first.Id)!.Text);
            Click(window, "TasksCloseButton");
            Assert.Equal(Visibility.Collapsed, Control<Grid>(window, "TasksPanel").Visibility);
        }
        finally { window.Close(); }
    });

    [Fact]
    public Task ADueReminderComesUpOnItsOwnEvenWhenFinishedWorkWaitsForTheNextMessage() => DispatcherTest(async () =>
    {
        await using var fixture = await LiveFixture.Create();
        var loaded = await fixture.Store.LoadAsync();
        await fixture.Save(loaded.Settings! with { Generation = new() { ThinkLonger = new() { Delivery = ThinkDelivery.NextMessage } } });
        fixture.Answer("Hey, it's dishes time!");
        var window = fixture.Open();
        try
        {
            await Loaded(window);
            var job = window.Remind("do the dishes", "do the dishes (they asked for it at 3:12 PM, for 4:12 PM)")!;
            Assert.True(job.Kind.Notice);
            await Until(() => job.Delivery == BackgroundDeliveryState.Delivered);
            Assert.Equal(1, fixture.Llm.Calls);
            var body = Encoding.UTF8.GetString(fixture.Llm.Body);
            Assert.Contains("a reminder they asked you for is due now", body);
            Assert.Contains("do the dishes (they asked for it at 3:12 PM, for 4:12 PM)", body);
            Click(window, "TasksChip");
            await Until(() => Find<TextBlock>(window, "LiveJobState-" + job.Id)?.Text == "Martlet reminded you.");
        }
        finally { window.Close(); }
    });

    [Fact]
    public Task WhatACheckInBringsUpComesUpOnItsOwnInItsOwnWords() => DispatcherTest(async () =>
    {
        await using var fixture = await LiveFixture.Create();
        fixture.Answer("Hey, how about a quick stretch?");
        var window = fixture.Open();
        try
        {
            await Loaded(window);
            var job = window.BringUp("Breaks", "Suggest a short stretch break, it has been hours.")!;
            Assert.True(job.Kind.Notice);
            Assert.Equal(CheckIns.SayKindName, job.Kind.Name);
            await Until(() => job.Delivery == BackgroundDeliveryState.Delivered);
            Assert.Equal(1, fixture.Llm.Calls);
            var body = Encoding.UTF8.GetString(fixture.Llm.Body);
            Assert.Contains("your own check-in came up with something to bring up", body);
            Assert.Contains("Suggest a short stretch break, it has been hours.", body);
            Assert.DoesNotContain("a reminder they asked you for is due now", body);
            Click(window, "TasksChip");
            await Until(() => Find<TextBlock>(window, "LiveJobState-" + job.Id)?.Text == "Martlet brought it up.");
        }
        finally { window.Close(); }
    });

    [Fact]
    public void TheTaskChipCountsRunningReadyAndDoneTasks()
    {
        Assert.Equal("2 running", LiveConversationWindow.TasksChipLine(2, 0, 3));
        Assert.Equal("1 running · 1 ready", LiveConversationWindow.TasksChipLine(1, 1, 1));
        Assert.Equal("1 ready", LiveConversationWindow.TasksChipLine(0, 1, 2));
        Assert.Equal("3 done", LiveConversationWindow.TasksChipLine(0, 0, 3));
    }

    // The element with this automation ID in the window's visual tree, or null.
    private static T? Find<T>(Window window, string automationId) where T : DependencyObject
    {
        window.UpdateLayout();
        T? Walk(DependencyObject node)
        {
            if (node is T match && AutomationProperties.GetAutomationId(match) == automationId) return match;
            for (var i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(node); i++)
                if (Walk(System.Windows.Media.VisualTreeHelper.GetChild(node, i)) is { } found) return found;
            return null;
        }
        return Walk(window);
    }

    private static void Escape(Window window, string target)
    {
        var key = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(window), 0, Key.Escape)
            { RoutedEvent = Keyboard.PreviewKeyDownEvent };
        Assert.IsAssignableFrom<UIElement>(window.FindName(target)).RaiseEvent(key);
        Assert.True(key.Handled);
    }

    private static T Control<T>(Window window, string name) => Assert.IsType<T>(window.FindName(name));
    // The automation IDs of the talk window's bubbles, in order.
    private static string[] BubbleIds(Window window) =>
        [.. Bodies(window).Select(System.Windows.Automation.AutomationProperties.GetAutomationId)];

    // The words of the talk window's bubbles, in order.
    private static TextBox[] Bodies(Window window)
    {
        window.UpdateLayout();
        var found = new List<TextBox>();
        void Walk(DependencyObject node)
        {
            if (node is TextBox { Name: "Body" } body) found.Add(body);
            for (var i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(node); i++)
                Walk(System.Windows.Media.VisualTreeHelper.GetChild(node, i));
        }
        Walk(Control<ItemsControl>(window, "History"));
        return [.. found];
    }

    // The nearest element above node in the visual tree with this name.
    private static T Ancestor<T>(DependencyObject node, string name) where T : FrameworkElement
    {
        var parent = System.Windows.Media.VisualTreeHelper.GetParent(node);
        while (parent is not null && (parent as T)?.Name != name) parent = System.Windows.Media.VisualTreeHelper.GetParent(parent);
        return Assert.IsType<T>(parent);
    }
    private static string Text(Window window, string name) => name == "ResultText"
        ? Control<TextBlock>(window, name).Text : Control<TextBox>(window, name).Text;
    private static string ResponsesCurrentUserText(JsonDocument body)
    {
        var users = body.RootElement.GetProperty("input").EnumerateArray()
            .Where(item => item.GetProperty("role").GetString() == "user")
            .Select(item =>
            {
                var content = item.GetProperty("content");
                if (content.ValueKind == JsonValueKind.String) return content.GetString()!;
                return content.EnumerateArray().Single(part => part.GetProperty("type").GetString() == "input_text")
                    .GetProperty("text").GetString()!;
            })
            .ToArray();
        Assert.NotEmpty(users);
        return users[^1];
    }
    private static string ResponsesCurrentNotes(JsonDocument body)
    {
        var text = ResponsesCurrentUserText(body);
        var at = text.LastIndexOf("[MARTLET_NOTES]", StringComparison.Ordinal);
        Assert.True(at >= 0, text);
        return text[at..];
    }
    private static void Click(Window window, string name) => Control<Button>(window, name).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    private static Task Loaded(LiveConversationWindow window) => Until(() => window.IsReady);
    private static void SendKey(Window window, Key key, bool down) =>
        Control<Button>(window, "PttButton").RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(window), 0, key)
        { RoutedEvent = down ? Keyboard.PreviewKeyDownEvent : Keyboard.PreviewKeyUpEvent });
    internal static async Task Until(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!condition()) await Task.Delay(1, timeout.Token);
    }
    private static Task Heartbeat() => Dispatcher.CurrentDispatcher.InvokeAsync(() => { }, DispatcherPriority.Background).Task.WaitAsync(TimeSpan.FromSeconds(2));
    private static async Task DispatcherTest(Func<Task> action)
    {
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            dispatcher.UnhandledException += (_, args) => { args.Handled = true; finished.TrySetException(args.Exception); dispatcher.BeginInvokeShutdown(DispatcherPriority.Send); };
            dispatcher.BeginInvoke(async () =>
            {
                try { await action(); finished.TrySetResult(); }
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

internal sealed class LiveFixture : IAsyncDisposable
{
    internal const string Secret = "synthetic-vault-canary-NOT-A-KEY";
    internal string DirectoryPath { get; } = Path.Combine(Path.GetTempPath(), "Martlet.Live.Fixture." + Guid.NewGuid().ToString("N"));
    internal SettingsStore Store { get; }
    internal DelayedSettings Settings { get; }
    internal RuntimeClock Clock { get; } = new();
    internal SetupOperationRunner Runner { get; } = new();
    internal NativeFixture Native { get; } = new();
    internal SessionFixture Events { get; } = new();
    internal RecordingHandler Stt { get; } = new();
    internal TextRecordingHandler Llm { get; } = new();
    internal TextRecordingHandler Chat { get; } = new();
    internal TextRecordingHandler Tts { get; } = SpeechFixtures.Handler();
    internal ControlledCapture Capture { get; } = new();
    internal ControlledDevice Output { get; }
    internal DesktopMemoryService Memory { get; }
    internal DesktopConversationHistory? History { get; }
    internal McpToolService? ToolService { get; }
    /// <summary>The voices Martlet knows (voices.json in the fixture's folder), when the test asked for them.</summary>
    internal LocalVoices? Voices { get; }
    internal LiveConversationController Controller { get; }
    internal LiveFixture(ControlledDevice? output = null, VoiceIdentity? voiceIdentity = null,
        IPcAudioSourceFactory? pcAudio = null, bool voices = false, bool history = false, bool tools = false, bool echo = false,
        ILocalTranscriber? localListener = null, IEndOfTurnJudge? turnJudge = null,
        Func<SetupRoute, string?>? listeningStandIn = null, Func<TimeProvider, PcActivityMonitor>? pcActivity = null, bool data = false)
    {
        Store = new(DirectoryPath);
        Memory = new(Store, Clock);
        Voices = voices ? new LocalVoices(DirectoryPath, "desk-test") : null;
        History = history ? new(DirectoryPath, Clock, TimeZoneInfo.Utc) : null;
        ToolService = tools ? new(DirectoryPath, Clock) : null;
        Output = output ?? new();
        var vault = new WindowsCredentialStore(Native);
        Settings = new(new SetupService(Store, vault));
        Controller = new(Runner, Settings, vault, Capture, Output, Clock,
            (credentials, clock) => ConversationRuntime.ForFixture(
                OpenAiTextGenerationAdapter.CreateForFixture(Llm, credentials, clock),
                OpenAiSpeechSynthesisAdapter.CreateForFixture(Tts, credentials, clock), Output, new(), clock,
                chat: target => ChatCompletionsTextGenerationAdapter.CreateForFixture(target.BaseUrl, Chat,
                    target.Keyless ? null : credentials, clock)),
            (credentials, clock) => OpenAiTranscriptionAdapter.CreateForFixture(Stt, credentials, clock),
            memory: Memory, voiceIdentity: voiceIdentity, voices: Voices,
            pcAudio: pcAudio is null ? null : new PcAudioCaptureFactory(pcAudio, Clock), tools: ToolService, history: History,
            // Echo reduction over the fixture microphone, with speakers whose loopback stays quiet and a canceller that keeps
            // the microphone as it is.
            echoReducer: echo ? new EchoReducer(Capture, new QuietSpeakers(), () => new KeptMicrophone(), Clock) : null,
            localListener: localListener, turnJudge: turnJudge, listeningStandIn: listeningStandIn, pcActivity: pcActivity?.Invoke(Clock),
            // With data, the controller keeps what it finds out (model-abilities.json) and its status files in the fixture's folder.
            dataDirectory: data ? DirectoryPath : null);
        Events.LockedChanged += Controller.SetSessionLocked;
        Llm.Inspect = Tts.Inspect = request =>
        {
            Assert.Equal("Bearer " + Secret, request.Headers.Authorization!.ToString());
            Assert.Equal("api.openai.com", request.RequestUri!.Host);
        };
    }
    internal static async Task<LiveFixture> Create(ControlledDevice? output = null,
        bool legacy = false, VoiceIdentity? voiceIdentity = null, IPcAudioSourceFactory? pcAudio = null, bool voices = false,
        bool history = false, bool tools = false, bool echo = false, ILocalTranscriber? localListener = null,
        IEndOfTurnJudge? turnJudge = null, Func<SetupRoute, string?>? listeningStandIn = null,
        Func<TimeProvider, PcActivityMonitor>? pcActivity = null, bool data = false)
    {
        var fixture = new LiveFixture(output, voiceIdentity, pcAudio, voices, history, tools, echo, localListener, turnJudge,
            listeningStandIn, pcActivity, data);
        var settings = SetupSettings.Begin(null);
        settings = settings with { Profile = settings.Profile with { Kind = ProfileKind.Api },
            Audio = AudioSettings.Create() };
        settings = settings with { Audio = settings.Audio with
        {
            Input = settings.Audio.Input.Select("private-input-id", "Selected test microphone"),
            Output = settings.Audio.Output.Select("private-output-id", "Selected test output")
        } };
        foreach (var role in Enum.GetValues<SetupRole>())
        {
            settings = SetupSettings.SelectRoute(settings, role, role switch
            {
                SetupRole.Stt => "gpt-transcribe", SetupRole.Llm => TextFixtures.Model, _ => "gpt-4o-mini-tts-2025-12-15"
            }, role == SetupRole.Tts ? "coral" : null);
            var route = settings.Setup!.Routes.Single(r => r.Role == role).WithCredential(Guid.NewGuid());
            settings = SetupSettings.ReplaceRoute(settings, route with { Consent = route.Selection() });
        }
        // Memory is ON for real profiles; most tests check one exact request, so they start with it OFF.
        settings = settings with { Memory = settings.Memory!.Configure(false, MemoryStoragePolicy.AppLocalData, null) };
        if (legacy)
            settings = settings with
            {
                SchemaVersion = 2, Companion = null, Memory = null,
                Setup = settings.Setup!.DowngradeOpenAiForHistoricalSettings()
            };
        await fixture.Save(settings);
        return fixture;
    }
    internal async Task Save(AppSettings settings)
    {
        var prior = await Store.LoadAsync();
        Assert.True((await Store.SaveAsync(settings, prior.Revision)).Saved);
        Controller.Configure(await Store.LoadAsync());
    }
    internal LiveConversationOperation Start(string text = "Explicit test input.", bool voice = false, bool microphone = false, CancellationToken caller = default) =>
        Controller.Start(microphone ? null : text, voice, microphone, true, microphone, microphone, caller);
    internal LiveConversationOperation StartWithMemory(string text = "Explicit memory test input.") =>
        Controller.Start(text, voice: false, microphone: false, approved: true, caller: default);
    internal async Task EnableMemory()
    {
        var loaded = await Store.LoadAsync();
        var saved = await Memory.SaveConfigurationAsync(
            loaded.Settings!, loaded.Revision, enabled: true,
            policy: MemoryStoragePolicy.AppLocalData, customDirectory: null);
        Assert.True(saved.Save.Save.Saved);
        Controller.Configure(await Store.LoadAsync());
    }
    internal async Task FinishRemembering()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        var idle = Controller.MemoryCaptureIdle;
        while (!idle.IsCompleted)
        {
            Clock.Advance(TimeSpan.FromMilliseconds(5));
            await Task.Delay(1, timeout.Token);
        }
        await idle;
    }
    internal async Task<Martlet.Memory.MemoryMutationReceipt> SaveMemoryFact(string content, string? voiceId = null)
    {
        var loaded = await Store.LoadAsync();
        return await Memory.SaveFactAsync(loaded.Settings!.Memory!.ConfigurationRevision,
            content, Martlet.Memory.MemoryRetention.UntilDeleted(), voiceId);
    }
    /// <summary>Opens the talk window; by default with push-to-talk and text-only replies, so nothing listens or speaks
    /// unless a test asks for it.</summary>
    internal LiveConversationWindow Open(TalkPreferences? preferences = null, IScreenGlancer? glancer = null)
    {
        var window = new LiveConversationWindow(Settings, Runner, Controller, Events, clock: Clock,
            glancer: glancer, preferences: preferences ?? new TalkPreferences(HandsFree: false, SpeakReplies: false))
        { ShowActivated = false, ShowInTaskbar = false };
        window.Show();
        return window;
    }
    internal async Task Advance(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        while (!condition())
        {
            Clock.Advance(TimeSpan.FromMilliseconds(5));
            await Task.Delay(1, timeout.Token);
        }
    }
    /// <summary>Lets this much of the fixture clock pass, 5 ms at a time, with the work it wakes running in between.</summary>
    internal async Task Pass(TimeSpan time)
    {
        for (var passed = TimeSpan.Zero; passed < time; passed += TimeSpan.FromMilliseconds(5))
        {
            Clock.Advance(TimeSpan.FromMilliseconds(5));
            await Task.Delay(1);
        }
    }
    internal void Answer(params string[] text) => Llm.Respond = (_, _) => Task.FromResult(TextRecordingHandler.Sse(Harness.Trace(text)));
    internal async Task Finish(LiveConversationOperation? operation = null, bool advance = true)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        while (operation is null ? Runner.IsRunning : !operation.Worker.Completion.IsCompleted)
        {
            if (advance) Clock.Advance(TimeSpan.FromMilliseconds(5));
            await Task.Delay(1, timeout.Token);
        }
        if (operation is not null) await operation.Worker.Completion;
    }
    internal void NoEffects()
    {
        Assert.Empty(Native.Targets);
        Assert.Equal(0, Stt.Calls);
        Assert.Equal(0, Llm.Calls);
        Assert.Equal(0, Tts.Calls);
        Assert.Equal(0, Capture.Opens);
        Assert.Equal(0, Output.Opens);
    }
    public async ValueTask DisposeAsync()
    {
        Native.Release.Set();
        Output.Release.Set();
        Capture.OpenBlock?.Set();
        Capture.ReadBlock?.Set();
        Capture.DisposeBlock?.Set();
        await Controller.DisposeAsync();
        if (ToolService is not null) await ToolService.DisposeAsync();
        if (History is not null) await History.Idle;
        Memory.Dispose();
        Voices?.Dispose();
        if (System.IO.Directory.Exists(DirectoryPath)) System.IO.Directory.Delete(DirectoryPath, true);
    }
    internal sealed class NativeFixture : ICredentialNative
    {
        public bool IsSupported => true;
        internal bool Block;
        internal int Error;
        internal ManualResetEventSlim Release { get; } = new();
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal ConcurrentQueue<string> Targets { get; } = new();
        internal ConcurrentQueue<int> Threads { get; } = new();
        internal ConcurrentQueue<SecretLease> Leases { get; } = new();
        public int Read(string target, out SecretLease? secret)
        {
            Targets.Enqueue(target);
            Threads.Enqueue(Environment.CurrentManagedThreadId);
            secret = new SecretLease(Secret);
            Leases.Enqueue(secret);
            Entered.TrySetResult();
            if (Block) Release.Wait();
            return Error;
        }
        public int Write(string target, ReadOnlySpan<char> secret) => throw new InvalidOperationException("Unexpected native write.");
        public int Delete(string target) => throw new InvalidOperationException("Unexpected native delete.");
    }
    internal sealed class SessionFixture : IAudioSessionEvents
    {
        public event Action<bool>? LockedChanged;
        internal void Signal(bool value) => LockedChanged?.Invoke(value);
        public void Dispose() { }
    }
    private sealed class QuietSpeakers : IEchoReferenceFactory
    {
        public IEchoReference Open(string? outputEndpointId, CancellationToken cancellationToken) => new Loopback();
        private sealed class Loopback : IEchoReference
        {
            public CaptureSourceFormat Format { get; } = new(16000, 1, 32, DeviceSampleEncoding.IeeeFloat);
            public void Start() { }
            public CapturePacket Read(Span<byte> destination) => new(0);
            public void Stop() { }
            public void Dispose() { }
        }
    }
    private sealed class KeptMicrophone : IEchoCanceller
    {
        public void Process(ReadOnlySpan<float> speaker, Span<float> microphone) { }
        public void Dispose() { }
    }
    internal sealed class DelayedSettings(ISetupService inner) : ISetupService
    {
        internal Func<CancellationToken, Task>? BeforeLoad;
        public async Task<SettingsLoadResult> LoadAsync(CancellationToken token = default)
        {
            if (BeforeLoad is { } before) await before(token);
            return await inner.LoadAsync(token);
        }
        public Task<SetupSaveResult> SaveAsync(AppSettings settings, string? revision, CancellationToken token = default) => inner.SaveAsync(settings, revision, token);
        public Task<SetupSaveResult> ReplaceCredentialAsync(AppSettings settings, string? revision, SetupRole role, SecretLease secret, CancellationToken token = default) => inner.ReplaceCredentialAsync(settings, revision, role, secret, token);
        public Task<SetupSaveResult> DetachCredentialAsync(AppSettings settings, string? revision, SetupRole role, CancellationToken token = default) => inner.DetachCredentialAsync(settings, revision, role, token);
        public Task<SetupSaveResult> RemoveDetachedAsync(AppSettings settings, string? revision, PendingCredentialRemoval removal, CancellationToken token = default) => inner.RemoveDetachedAsync(settings, revision, removal, token);
        public CredentialError CheckCredential(AppSettings settings, SetupRole role) => inner.CheckCredential(settings, role);
    }
}
