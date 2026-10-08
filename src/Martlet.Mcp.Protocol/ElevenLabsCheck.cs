using System.IO;
using System.Net;
using System.Net.Sockets;
using Martlet.Audio;
using Martlet.Conversation;
using Martlet.Core.Contracts;
using Martlet.Core.Settings;
using Martlet.Providers;

namespace Martlet.Mcp;

/// <summary>elevenlabs_check: ElevenLabs as the Voice, rehearsed end to end against <see cref="ElevenLabsFixture"/>, a local
/// stand-in on 127.0.0.1 that follows ElevenLabs' documented protocol (FIXTURE, NOT ElevenLabs, NOT AI; its voice is a quiet
/// tone). It clones a voice (Instant Voice Cloning, a synthetic WAV), then speaks a reply with the production conversation
/// runtime (a fixture Chat Completions endpoint streams the reply a word at a time, the ElevenLabs Text to Dialogue client
/// speaks each piece, a fixture speaker plays nothing). It shows that the Thinking prompt lists ElevenLabs' own tags, that each
/// piece reaches ElevenLabs with its tags as ElevenLabs spells them, that the chat and captions show none, and what the
/// protocol carried (model, pcm_24000, the key in the xi-api-key header, one registered voice, close_socket). model-refused
/// rehearses ElevenLabs refusing the model on the WebSocket (its API reference names only eleven_v3 models); bad-key a wrong
/// key. With dataDirectory it also reports the saved ElevenLabs choice (never the key, the voice ID or the voice's name).
/// Nothing is sent to ElevenLabs: live use is NOT RUN (there is no ElevenLabs account or key).</summary>
internal static class ElevenLabsCheck
{
    internal static readonly string[] Scenarios = ["reply", "model-refused", "bad-key"];
    internal const string Live = "NOT RUN: there is no ElevenLabs account or key. Everything here ran against a local fixture that follows " +
        "ElevenLabs' documented protocol (FIXTURE, NOT ElevenLabs, NOT AI).";
    private const string Model = "fixture-model";
    private const string DefaultReply =
        "[laughs] Oh, you made it back! [whispers] I have a little secret to tell you. [happy] It is going to be a good day.";

    internal static async Task<object> RunAsync(string? scenario, string? model, string? reply, string? dataDirectory, CancellationToken cancellation)
    {
        scenario ??= "reply";
        if (!Scenarios.Contains(scenario)) throw new ArgumentException($"'scenario' must be one of {string.Join(", ", Scenarios)}.");
        model ??= ElevenLabsSetup.DefaultModelId;
        if (!ElevenLabsSetup.SupportsModel(model)) throw new ArgumentException($"'model' must be one of {string.Join(", ", ElevenLabsSetup.ModelIds)}.");
        reply ??= DefaultReply;
        if (string.IsNullOrWhiteSpace(reply) || reply.Length > 1024 || reply.Any(char.IsControl))
            throw new ArgumentException("'reply' must be 1-1024 characters of one-line text.");
        var saved = dataDirectory is null ? null : Saved(dataDirectory);

        await using var fixture = ElevenLabsFixture.Start(new() { RejectedModels = scenario == "model-refused" ? [model] : [] });
        var key = scenario == "bad-key" ? "wrong-fixture-key" : fixture.Options.ExpectedKey;
        var credentials = new FixtureKey(key);

        // 1. Clone a voice from a synthetic recording, as Companion › Voice does on the owner's click.
        object clone;
        var voiceId = fixture.Options.VoiceId;
        var cloneOk = false;
        using (var cloner = new ElevenLabsVoiceCloner(fixture.Origin))
        {
            try
            {
                var made = await cloner.CloneAsync(key, "Martlet - Fixture voice", Wave(), cancellation);
                voiceId = made.VoiceId;
                cloneOk = scenario != "bad-key";
                clone = new { cloned = true, voiceIdValid = ElevenLabsSetup.IsVoiceId(made.VoiceId), made.RequiresVerification };
            }
            catch (ElevenLabsException error)
            {
                cloneOk = scenario == "bad-key" && error.Code == ProviderFailureCode.Authentication;
                clone = new { cloned = false, code = error.Code.ToString(), detail = error.Detail };
            }
        }
        var form = fixture.Clones.LastOrDefault();
        cloneOk &= form is { Name: "Martlet - Fixture voice", FileName: "voice.wav", ContentType: "audio/wav", Wave: true, RemoveBackgroundNoise: "false" };

        // 2. One segment straight through the client, for the failure ElevenLabs gives and the segment's timings.
        var target = new ElevenLabsVoiceTarget(voiceId, model);
        var client = new ElevenLabsDialogueClient(credentials, fixture.Origin);
        var limits = new SpeechSynthesisLimits
        {
            MaxAudioBytes = 480_000, MaxAudioDuration = TimeSpan.FromSeconds(10), FirstAudioTimeout = TimeSpan.FromSeconds(10),
            MaxRequestTime = TimeSpan.FromSeconds(20)
        };
        ElevenLabsException? segmentError = null;
        long segmentBytes = 0;
        try
        {
            await foreach (var pcm in client.StreamAsync(target, new BoundedSpeechInput("[sighs] Hello there, it's me."), limits,
                               DateTimeOffset.UtcNow.AddSeconds(20), cancellation))
                segmentBytes += pcm.Length;
        }
        catch (ElevenLabsException error) { segmentError = error; }
        var segment = client.LastSegment;

        // 3. A whole spoken reply through the production conversation runtime.
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var baseUrl = $"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}/v1";
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        var chunks = System.Text.RegularExpressions.Regex.Matches(reply, @"\s*\S+").Select(m => m.Value).ToArray();
        var serving = SpokenReplyCheck.ServeAsync(listener, chunks, TimeSpan.FromMilliseconds(25), TimeSpan.Zero, stop.Token, [], false);
        var sessionsBefore = fixture.Sessions.Count;
        try
        {
            var speakers = new SpokenReplyCheck.Speakers();
            var speech = new SpeechOutput(ElevenLabsSpeechSynthesisStream.Selection(target), new OutputSelection(OutputPolicy.DefaultAtStart), limits);
            var request = new ConversationRequest(new BoundedTextInput("Say hi.", "Fixture check."),
                new TextModelSelection(ChatCompletionsSetup.Alias, Model), new TextGenerationLimits(),
                new ConversationLimits { MaxSpeechSegments = 8, MaxSpeechTextBytes = 12_288, MaxReservedSpeechSamples = 1_920_000 },
                speech, new ChatCompletionsTarget(baseUrl, Keyless: true), speechBreaks: SpeechBreaks.Default) { ElevenLabsVoice = target };
            var captions = new SpokenTextFeed();
            var shown = new List<string>();
            var reading = Task.Run(async () =>
            {
                try { await foreach (var line in captions.Lines.ReadAllAsync(stop.Token)) lock (shown) shown.Add(line.Text); }
                catch (OperationCanceledException) { }
            });
            await using var runtime = ConversationRuntime.Create(credentials, speakers, spokenText: captions, elevenLabs: client);
            var timeline = new ReplyTimeline(TimeProvider.System, ReplyTimeline.YouSent);
            var startedAt = TimeProvider.System.GetTimestamp();
            timeline.Mark("building the request", startedAt);
            var turn = runtime.Start(request, new Permissions(ChatCompletionsSetup.BaseUri(baseUrl), target), cancellation);
            var terminal = await turn.Completion.WaitAsync(TimeSpan.FromSeconds(60), cancellation);
            await turn.OwnershipRelease.WaitAsync(TimeSpan.FromSeconds(10), cancellation);
            var latencyLine = ReplyLatency.Describe(timeline, startedAt, TimeProvider.System, terminal, $"Thinking {Model}, voice {model}");
            var text = turn.Content.Text;
            var expectedText = VoiceTags.Strip(reply);
            // Captions of unsaid sentences keep coming after a failed voice, one per reading time.
            var waited = System.Diagnostics.Stopwatch.StartNew();
            while (waited.Elapsed < TimeSpan.FromSeconds(scenario == "reply" ? 2 : 25))
            {
                lock (shown) if (Words(string.Join(" ", shown)) == Words(expectedText)) break;
                await Task.Delay(100, cancellation);
            }
            string[] lines;
            lock (shown) lines = [.. shown];

            var sessions = fixture.Sessions.Skip(sessionsBefore).ToArray();
            var sent = sessions.SelectMany(s => s.Texts).ToArray();
            var tags = SpeechEngines.ElevenLabs.Tags;
            var written = tags.Where(tag => reply.Contains(tag.Text, StringComparison.OrdinalIgnoreCase)).Select(tag => tag.Text).ToArray();
            var prompt = VoiceTags.Instructions(SpeechEngines.ElevenLabs, null) ?? "";
            bool Shows(string shownText) => VoiceTags.Known.Any(tag => shownText.Contains(tag.Text, StringComparison.OrdinalIgnoreCase));
            var tagsToElevenLabs = written.All(tag => sent.Any(piece => piece.Contains(tag, StringComparison.Ordinal)));
            var chatClean = text == expectedText && !Shows(text);
            var captionsClean = lines.Length > 0 && !lines.Any(Shows);
            var promptListsTags = tags.All(tag => prompt.Contains(tag.Text, StringComparison.Ordinal));
            var protocol = sessions.Length > 0 && sessions.All(s => s.ModelId == model && s.OutputFormat == ElevenLabsSpeechCatalog.OutputFormat &&
                (scenario == "bad-key" || s.HeaderKeyMatched) && !s.BodyKey &&
                (s.Error is not null || s.Voices.SequenceEqual([voiceId]) && s.CloseSocket));
            var voiceOk = scenario switch
            {
                "reply" => !terminal.SpeechFailed && speakers.Samples > 0 && segmentError is null && segmentBytes > 0 && tagsToElevenLabs,
                "model-refused" => terminal.SpeechFailed && terminal.ProviderFailure == ProviderFailureCode.ModelUnsupported &&
                    segmentError is { Code: ProviderFailureCode.ModelUnsupported } refused &&
                    (model != ElevenLabsSetup.V4Turbo || refused.Detail?.Contains(ElevenLabsSetup.ModelName(ElevenLabsSetup.V3Conversational), StringComparison.Ordinal) == true),
                _ => terminal.SpeechFailed && terminal.ProviderFailure == ProviderFailureCode.Authentication &&
                    segmentError is { Code: ProviderFailureCode.Authentication }
            };
            var ok = cloneOk && voiceOk && protocol && chatClean && captionsClean && promptListsTags &&
                terminal.State == ConversationState.Completed && terminal.TextComplete;
            stop.Cancel();
            await reading;
            return new
            {
                ok,
                scenario,
                model,
                live = Live,
                fixture = new
                {
                    origin = fixture.Origin.ToString(),
                    note = "FIXTURE, NOT ElevenLabs, NOT AI: a quiet tone as long as the words; its first audio waits " +
                        $"{fixture.Options.FirstAudioDelay.TotalMilliseconds:0} ms to stand in for model latency (not a measurement)."
                },
                clone = new
                {
                    ok = cloneOk,
                    result = clone,
                    sent = form is null ? null : new
                    {
                        keyMatched = form.KeyMatched, name = form.Name, fileName = form.FileName, contentType = form.ContentType,
                        fileBytes = form.FileBytes, wave = form.Wave, removeBackgroundNoise = form.RemoveBackgroundNoise, description = form.Description
                    }
                },
                thinkingPrompt = new { listsElevenLabsTags = promptListsTags, tags = tags.Select(tag => new { tag.Text, kind = tag.Kind.ToString(), tag.Cue }) },
                segment = new
                {
                    ok = scenario == "reply" ? segmentError is null : segmentError is not null,
                    audioBytes = segmentBytes,
                    failure = segmentError?.Code.ToString(),
                    detail = segmentError?.Detail,
                    // Fixture timings: connecting to 127.0.0.1 and the fixture's stand-in delay, not ElevenLabs' latency.
                    connectMs = segment?.ConnectMs is { } connect ? Math.Round(connect, 1) : (double?)null,
                    firstAudioMs = segment?.FirstAudioMs is { } first ? Math.Round(first, 1) : (double?)null,
                    totalMs = segment?.TotalMs is { } total ? Math.Round(total, 1) : (double?)null
                },
                protocol = new
                {
                    ok = protocol,
                    connections = sessions.Length,
                    sessions = sessions.Select(s => new
                    {
                        s.ModelId, s.OutputFormat, keyInHeader = s.HeaderKeyMatched, keyInBody = s.BodyKey, voices = s.Voices.Count,
                        registeredClonedVoice = s.Voices.SequenceEqual([voiceId]), texts = s.Texts, s.CloseSocket, s.AudioBytes, s.Error
                    })
                },
                tags = new { written, reachedElevenLabs = tagsToElevenLabs, chatClean, captionsClean },
                reply = new
                {
                    state = terminal.State.ToString(),
                    textComplete = terminal.TextComplete,
                    speechFailed = terminal.SpeechFailed,
                    provider = terminal.ProviderFailure?.ToString(),
                    samplesPlayed = speakers.Samples,
                    text,
                    captions = lines,
                    latency = latencyLine,
                    firstAudioMs = terminal.FirstAudioAfter?.TotalMilliseconds
                },
                saved
            };
        }
        finally
        {
            stop.Cancel();
            listener.Stop();
            try { await serving; } catch (Exception error) when (error is OperationCanceledException or SocketException or ObjectDisposedException or IOException) { }
        }
    }

    /// <summary>The saved ElevenLabs choice in a data directory: whether Voice uses ElevenLabs, its model, whether it is on and
    /// confirmed, whether a key and a cloned voice are saved and whether ElevenLabs asked to verify the voice, and how many
    /// ElevenLabs keys are kept from before. Never the key, the voice ID or the voice's name.</summary>
    internal static object Saved(string dataDirectory)
    {
        var path = Path.Combine(dataDirectory, "settings.json");
        if (!File.Exists(path)) return new { state = "no settings" };
        try
        {
            var settings = SettingsJson.Read(File.ReadAllBytes(path));
            var route = settings.Setup?.Routes.FirstOrDefault(r => r.Role == SetupRole.Tts);
            return new
            {
                state = "loaded",
                voiceRoute = route?.RouteType?.ToString(),
                usesElevenLabs = route?.RouteType == SetupRouteType.ElevenLabs,
                model = route?.RouteType == SetupRouteType.ElevenLabs ? route.ModelId : null,
                enabled = route?.RouteType == SetupRouteType.ElevenLabs ? route.Enabled : null,
                confirmed = route?.RouteType == SetupRouteType.ElevenLabs ? route.Consent is not null && route.Consent == route.Selection() : (bool?)null,
                keySaved = route?.RouteType == SetupRouteType.ElevenLabs ? route.CredentialId is not null : (bool?)null,
                clonedVoice = route?.ClonedVoice is not null,
                requiresVerification = route?.ClonedVoice?.RequiresVerification,
                keysFromBefore = SetupSettings.SetAsideElevenLabsCredentials(settings).Count
            };
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ContractException or System.Text.Json.JsonException)
        {
            return new { state = "unreadable" };
        }
    }

    private static string Words(string text) => string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    // FIXTURE: one second of a quiet 220 Hz tone as a 24 kHz mono 16-bit WAV, standing in for a saved voice's recording.
    private static byte[] Wave()
    {
        const int samples = 24_000;
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write("RIFF"u8);
        writer.Write(36 + samples * 2);
        writer.Write("WAVEfmt "u8);
        writer.Write(16);
        writer.Write((short)1);
        writer.Write((short)1);
        writer.Write(24_000);
        writer.Write(48_000);
        writer.Write((short)2);
        writer.Write((short)16);
        writer.Write("data"u8);
        writer.Write(samples * 2);
        for (var i = 0; i < samples; i++) writer.Write((short)(Math.Sin(2 * Math.PI * 220 * i / 24_000.0) * 3000));
        writer.Flush();
        return stream.ToArray();
    }

    // The fixture's key for ElevenLabs' binding only, as the desktop's authorization resolves the saved key.
    private sealed class FixtureKey(string key) : IProviderCredentialSource
    {
        public ValueTask<BoundProviderCredential?> ResolveAsync(ProviderCredentialBinding binding, CancellationToken cancellationToken) =>
            ValueTask.FromResult<BoundProviderCredential?>(binding.Origin == ElevenLabsSpeechCatalog.Origin ? new BoundProviderCredential(binding, key) : null);
    }

    // Allows exactly what was asked, bound to the fixture endpoint and ElevenLabs' origin, as the desktop's own authorization does.
    private sealed class Permissions(Uri baseUri, ElevenLabsVoiceTarget voice) : IConversationAuthorizationSource
    {
        public ValueTask<AuthorizedTextOperation?> AuthorizeTextAsync(TextAuthorizationAction action, CancellationToken cancellationToken)
        {
            var until = Until(action.Context.Deadline);
            return ValueTask.FromResult<AuthorizedTextOperation?>(new(new TextDisclosureAuthorization(
                new(baseUri, ProviderRole.Llm, action.Model.UpstreamModelId), action.Model, action.Context.Ids, action.Context.Epoch,
                action.Limits, until, true, true), new(action.Budget, until)));
        }

        public ValueTask<AuthorizedSpeechOperation?> AuthorizeSpeechAsync(SpeechAuthorizationAction action, CancellationToken cancellationToken)
        {
            var until = Until(action.Context.Deadline);
            return ValueTask.FromResult<AuthorizedSpeechOperation?>(new(new SpeechDisclosureAuthorization(
                ElevenLabsSpeechSynthesisStream.Binding(voice), action.Selection, action.Input, action.Context.Ids, action.Context.Epoch,
                action.Limits, until, true, true, true), new(action.Budget, until)));
        }

        private static DateTimeOffset Until(DateTimeOffset deadline)
        {
            var cap = DateTimeOffset.UtcNow.AddSeconds(30);
            return deadline < cap ? deadline : cap;
        }
    }
}
