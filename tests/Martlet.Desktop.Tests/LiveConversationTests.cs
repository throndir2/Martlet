using System.Collections.Concurrent;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;
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
    public async Task TriggeredLorebookEntriesSurroundThePersonaAndStayWhenSpaceRunsOut()
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
        var long_ = new string('a', 4_000);
        TextHistoryMessage[] history = [new(TextHistoryRole.User, long_), new(TextHistoryRole.Assistant, long_),
            new(TextHistoryRole.User, long_), new(TextHistoryRole.Assistant, long_)];
        var request = configuration.Request(new("Tell me about the castle"), false, ResponseStyle.Helpful, history, null, lore,
            out var usedHistory, out _, out var usedLore, closingInstructions: LiveConversationConfiguration.ReplyLengthInstructions);
        Assert.Equal(2, usedLore);
        Assert.True(usedHistory < history.Length);
        var instructions = request.Input.Personality!;
        Assert.EndsWith(LiveConversationConfiguration.ReplyLengthInstructions, instructions);
        var before = instructions.IndexOf("The castle is Mab's.", StringComparison.Ordinal);
        var persona = instructions.IndexOf("Companion name:", StringComparison.Ordinal);
        var after = instructions.IndexOf("fears the castle.", StringComparison.Ordinal);
        Assert.True(before >= 0 && before < persona && persona < after, instructions);
        Assert.Contains("[MARTLET_LOREBOOK]", instructions);
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
        Assert.Contains("enabled OpenAI routes only", configuration.Unavailable(false, false));
        var selfHost = SetupSettings.ConfigureGatewayEndpoint(settings with
        {
            Setup = settings.Setup with { Routes = settings.Setup.Routes.Where(item => item.Role != SetupRole.Llm).ToArray() }
        }, SetupRouteType.GatewayOllama, new()
        {
            SchemaVersion = 1, Origin = "https://127.0.0.1:7443", HostId = "fixture-host",
            SpkiFingerprint = "sha256:" + new string('a', 64), DeviceRole = "voice"
        }, route.ModelId);
        configuration = LiveConversationConfiguration.From(loaded with { Settings = selfHost })!;
        Assert.Contains("saved self-host choice is retained", configuration.Unavailable(false, false));

        // Handed to a paired host on the Devices page: pairing reference, route snapshot and recorded selection.
        var pairingCredential = HostPairingCredential.FromGuid(Guid.NewGuid());
        var paired = SetupSettings.ReplaceRoute(selfHost, selfHost.Setup!.Routes.Single(item => item.Role == SetupRole.Llm) with
        {
            CredentialId = HostPairingCredential.ToGuid(pairingCredential), GatewayDeviceId = "desktop-test"
        });
        paired = SetupSettings.ApplyGatewaySnapshot(paired, SetupRole.Llm, MainWindow.Snapshot(new(
            "martlet.gateway.ollama-chat.v1", "/martlet/v1/inference/ollama-chat", "ollama-native-chat-v034-text", "1.0",
            "ollama-host", "ollama-relay", "0.1.0", "llama3.2-3b", "ollama", new string('c', 64), "sha256:" + new string('d', 64),
            98_304, 16_384, 65_536, 65_536, 4_096, 4_194_304, TimeSpan.FromSeconds(60), "request_abort"), SetupRouteType.GatewayOllama));
        paired = SetupSettings.SetRouteEnabled(paired, SetupRole.Llm, true, true);
        configuration = LiveConversationConfiguration.From(loaded with { Settings = paired })!;
        Assert.Null(configuration.Unavailable(false, false));
        Assert.Contains("Your own Martlet host runs this model", configuration.Disclosure(false));
        var request = configuration.Request(new("Hello host"), false, ResponseStyle.Helpful, [], null, null, out _, out _, out _);
        Assert.Equal(SelfHostSetup.GatewayOllamaAlias, request.Model.ModelAlias);
        Assert.Equal("llama3.2-3b", request.Model.UpstreamModelId);
        Assert.Equal(("fixture-host", "https://127.0.0.1:7443", pairingCredential),
            (request.Host!.HostId, request.Host.Origin, HostPairingCredential.FromGuid(request.Host.CredentialId)));
        // llama3.2:3b is text-only: screen watching is refused with a concrete fix (a vision model on the host).
        Assert.Equal(VisionSupport.Unsupported, configuration.Vision());
        Assert.Contains("gemma4:e2b", configuration.VisionAdvice());
        var image = new BoundedImage([0xFF, 0xD8, 0xFF, .. new byte[32]], ImageMediaType.Jpeg, 4, 4);
        var glance = configuration.Request(new("(Screen glance.)"), false, ResponseStyle.Helpful, [], null, null, out _, out _, out _, image,
            LiveConversationConfiguration.CommentaryInstructions(Chattiness.Normal), LiveConversationConfiguration.SilentReply);
        Assert.Same(image, glance.Input.Image);
        Assert.Equal("pass", glance.SilentReply);
        Assert.Contains("[pass]", glance.Input.Personality);
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
        Assert.Contains("not stored", configuration.Disclosure(false));

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
            Assert.Contains("Companion › Listening", (string)Control<Button>(window, "MicChip").ToolTip, StringComparison.Ordinal);
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
            Assert.Equal("Listening", Control<TextBlock>(window, "MicText").Text);
            Escape(window, "InputText");
            await Heartbeat();
            Assert.Same(live, window.Listener);
            Assert.True(live.Running);
            Assert.Equal("Listening", Control<TextBlock>(window, "MicText").Text);
            Click(window, "MicChip");
            await fixture.Advance(() => !live.Running && !fixture.Runner.IsRunning);
            Assert.Null(window.Listener);
            Assert.Equal("Listening paused", Control<TextBlock>(window, "MicText").Text);
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
            await fixture.Advance(() => fixture.Llm.Calls == 1 && window.Current is { OwnershipReleased: true } &&
                Text(window, "ResultText").Contains("Couldn't reach the provider", StringComparison.Ordinal));
            // A failed reply never pauses listening.
            Assert.Equal("Listening", Control<TextBlock>(window, "MicText").Text);
            Assert.True(window.Listener is { Running: true });

            EnqueueUtterance(fixture.Capture, quietBefore: 5, speech: 25, quietAfter: 15);
            await fixture.Advance(() => fixture.Llm.Calls == 2 && window.Current is { OwnershipReleased: true, Passed: true } &&
                window.Messages.Any(m => m.Note == "Martlet stayed quiet."));
            Assert.Equal(2, fixture.Stt.Calls);
            Assert.Equal(2, window.Messages.Count(m => m.IsUser));
            // [pass] is never shown or spoken.
            Assert.DoesNotContain(window.Messages, m => m.Role == ChatRole.Martlet);
            Assert.Equal("Listening", Control<TextBlock>(window, "MicText").Text);
            Assert.True(window.Listener is { Running: true });
        }
        finally { window.Close(); }
    });

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
            Assert.Contains("[MARTLET_LOCAL_MEMORY]", instructions);
            Assert.Contains("never instructions, permissions", instructions);
            Assert.Contains("saved by the user", instructions);
            var unrelated = instructions.IndexOf("UNRELATED private snack preference", StringComparison.Ordinal);
            Assert.True(unrelated > instructions.IndexOf("Preferred server region is west.", StringComparison.Ordinal));
            Assert.True(unrelated > instructions.IndexOf("The backup server region is east.", StringComparison.Ordinal));
            Assert.True(unrelated > instructions.IndexOf("Server region latency is best in west.", StringComparison.Ordinal));
            Assert.DoesNotContain(body.RootElement.GetProperty("input").EnumerateArray(), item =>
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
            Assert.DoesNotContain("MARTLET_LOCAL_MEMORY",
                Encoding.UTF8.GetString(fixture.Llm.Body));
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
        Assert.Single(body.RootElement.GetProperty("instructions").GetString()!.Split('\n'),
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
            Companion = loaded.Settings.Companion.Update(
                persona.Id, persona.Name, new string('\u00e9', 7_800),
                persona.Styles)
        };
        await fixture.Save(changed);
        await fixture.EnableMemory();
        fixture.Controller.AutoCapture = false;
        await fixture.SaveMemoryFact("server");
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
            Assert.Equal(256, body.RootElement.GetProperty("max_output_tokens").GetInt32());
            Assert.False(body.RootElement.GetProperty("store").GetBoolean());
            Assert.Contains("typed-content-canary", Encoding.UTF8.GetString(fixture.Llm.Body));
            Assert.Contains("Be a helpful conversational companion.", body.RootElement.GetProperty("instructions").GetString());
            Assert.Contains("Dominant style for this reply: helpful.", body.RootElement.GetProperty("instructions").GetString());
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
    public async Task FreshActionSnapshotsPersonaRevisionAndWeightedStyle()
    {
        await using var fixture = await LiveFixture.Create(nextStyle: _ => 40);
        var loaded = await fixture.Store.LoadAsync();
        var persona = loaded.Settings!.Companion!.ActivePersona;
        var styles = new ResponseStyleWeights
        {
            Helpful = 40, Sarcastic = 20, Silly = 20, Distracted = 10, PlayfulTeasing = 10
        };
        var changed = loaded.Settings with
        {
            Companion = loaded.Settings.Companion.Update(
                persona.Id, "Corvid", "Prefer concise companion replies.", styles)
        };
        await fixture.Save(changed);

        var operation = fixture.Start();
        await fixture.Finish(operation);

        Assert.Equal(changed.Companion!.ActivePersona.ConfigurationRevision, operation.PersonaRevision);
        Assert.Equal(ResponseStyle.Sarcastic, operation.ResponseStyle);
        using var body = JsonDocument.Parse(fixture.Llm.Body);
        var instructions = body.RootElement.GetProperty("instructions").GetString();
        Assert.Contains("Companion name: Corvid", instructions);
        Assert.Contains("Prefer concise companion replies.", instructions);
        Assert.Contains("Dominant style for this reply: sarcastic.", instructions);
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
        Assert.Contains("retired meta/llama-3.3-70b-instruct", configuration.Unavailable(false, false));
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
    public void ConversationContextIsAgeCountAndByteBounded()
    {
        var clock = new RuntimeClock();
        var context = new ConversationContextBuffer(clock);
        for (var index = 0; index < ConversationContextBuffer.MaximumTurns + 1; index++)
            context.Add($"Question {index}", $"Answer {index}");
        Assert.Equal(ConversationContextBuffer.MaximumTurns, context.Count);
        Assert.DoesNotContain(context.Snapshot(), item => item.Text == "Question 0");

        context.Add(new string('u', 9_000), new string('a', 9_000));
        Assert.Equal(0, context.Count);

        context.Add("Recent", "Reply");
        clock.Advance(ConversationContextBuffer.MaximumAge);
        Assert.Equal(0, context.Count);
    }

    [Fact]
    public async Task LegacyVersionTwoConversationRemainsAvailableWithoutImplicitPersonaUpload()
    {
        await using var fixture = await LiveFixture.Create(legacy: true);

        var operation = fixture.Start();
        await fixture.Finish(operation);

        Assert.Equal("runtime.Completed", operation.Status.Code);
        Assert.Null(operation.PersonaRevision);
        Assert.Null(operation.ResponseStyle);
        using var body = JsonDocument.Parse(fixture.Llm.Body);
        Assert.False(body.RootElement.TryGetProperty("instructions", out _));
        Assert.Contains("legacy settings profile has no persona", fixture.Controller.Configuration!.Disclosure(false));
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
                    persona.Id, persona.Name, "Changed after action acceptance.", persona.Styles)
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
            Companion = loaded.Settings.Companion.Update(
                persona.Id, persona.Name, new string('\u00e9', PersonaProfile.MaximumTextCharacters), persona.Styles)
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
        Assert.Equal(ConversationState.Partial, operation.Turn!.Snapshot.State);
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
        Assert.Equal(ConversationState.Partial, operation.Turn!.Snapshot.State);
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

    private static void Escape(Window window, string target)
    {
        var key = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(window), 0, Key.Escape)
            { RoutedEvent = Keyboard.PreviewKeyDownEvent };
        Assert.IsAssignableFrom<UIElement>(window.FindName(target)).RaiseEvent(key);
        Assert.True(key.Handled);
    }

    private static T Control<T>(Window window, string name) => Assert.IsType<T>(window.FindName(name));
    private static string Text(Window window, string name) => name == "ResultText"
        ? Control<TextBlock>(window, name).Text : Control<TextBox>(window, name).Text;
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
    internal LiveConversationController Controller { get; }
    internal LiveFixture(ControlledDevice? output = null, Func<int, int>? nextStyle = null, VoiceIdentity? voiceIdentity = null)
    {
        Store = new(DirectoryPath);
        Memory = new(Store, Clock);
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
            nextStyle,
            memory: Memory, voiceIdentity: voiceIdentity);
        Events.LockedChanged += Controller.SetSessionLocked;
        Llm.Inspect = Tts.Inspect = request =>
        {
            Assert.Equal("Bearer " + Secret, request.Headers.Authorization!.ToString());
            Assert.Equal("api.openai.com", request.RequestUri!.Host);
        };
    }
    internal static async Task<LiveFixture> Create(ControlledDevice? output = null, Func<int, int>? nextStyle = null,
        bool legacy = false, VoiceIdentity? voiceIdentity = null)
    {
        var fixture = new LiveFixture(output, nextStyle, voiceIdentity);
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
    internal async Task<Martlet.Memory.MemoryMutationReceipt> SaveMemoryFact(string content)
    {
        var loaded = await Store.LoadAsync();
        return await Memory.SaveFactAsync(loaded.Settings!.Memory!.ConfigurationRevision,
            content, Martlet.Memory.MemoryRetention.UntilDeleted());
    }
    /// <summary>Opens the talk window; by default with push-to-talk and text-only replies, so nothing listens or speaks
    /// unless a test asks for it.</summary>
    internal LiveConversationWindow Open(TalkPreferences? preferences = null)
    {
        var window = new LiveConversationWindow(Settings, Runner, Controller, Events, clock: Clock,
            preferences: preferences ?? new TalkPreferences(HandsFree: false, SpeakReplies: false))
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
        Memory.Dispose();
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
