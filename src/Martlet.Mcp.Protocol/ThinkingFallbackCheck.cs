using System.Diagnostics;
using System.IO;
using Martlet.Conversation;
using Martlet.Core.Contracts;
using Martlet.Core.Planning;
using Martlet.Core.Settings;
using Martlet.Providers;

namespace Martlet.Mcp;

/// <summary>MCP's <c>thinking_fallback_timing</c>: Companion › Thinking › If Thinking fails, timed (<see cref="ThinkingFallbackTimer"/>).
/// From a data directory: whether the timer applies to the saved Thinking route, the first-token wait it would use now (from
/// model-speed.json) and the rules. Its rehearsal runs the production conversation runtime and Chat Completions adapter against two
/// FIXTURE endpoints on 127.0.0.1 (canned words, NOT AI): a slow model gives way to the fallback after its short wait, a reply asks
/// the fallback first, and a fallback asked first that fails gives way to the model. Plus the timer's skip rule.</summary>
internal static class ThinkingFallbackCheck
{
    private const string Model = "fixture-model", FallbackModel = "fixture-fallback";
    private const string ModelWords = "The Thinking model answered.", FallbackWords = "The fallback answered.";

    internal static async Task<object> RunAsync(string? dataDirectory, CancellationToken cancellation)
    {
        var saved = await SavedAsync(dataDirectory, cancellation);
        var slow = await TurnAsync(modelMs: 3_000, fallbackMs: 100, wait: TimeSpan.FromMilliseconds(800), first: false, cancellation);
        var first = await TurnAsync(modelMs: 100, fallbackMs: 100, wait: TimeSpan.FromMilliseconds(800), first: true, cancellation);
        var firstFails = await TurnAsync(modelMs: 100, fallbackMs: 100, wait: TimeSpan.FromMilliseconds(800), first: true, cancellation,
            fallbackFails: true);
        var timer = Skips();
        var steps = new[]
        {
            new Step("slow-model", slow.Text == FallbackWords && slow.FellBackAfter is not null && slow.FirstWordsMs is < 2_500 &&
                slow.Model.Requests == 1 && !slow.Model.Answered, slow),
            new Step("fallback-first", first.Text == FallbackWords && first.FellBackAfter == ThinkingFallbackTimer.SkippedReason &&
                first.Model.Requests == 0, first),
            new Step("fallback-first-fails", firstFails.Text == ModelWords && firstFails.FellBackAfter is null &&
                firstFails.Fallback.Failed && firstFails.Model.Answered, firstFails),
            new Step("skip-rule", timer.Ok, timer.Detail)
        };
        return new
        {
            ok = steps.All(s => s.Passed),
            rules = new
            {
                startingMs = ThinkingFallbackTimer.Starting.TotalMilliseconds, minimumMs = ThinkingFallbackTimer.Minimum.TotalMilliseconds,
                maximumMs = ThinkingFallbackTimer.Maximum.TotalMilliseconds, ThinkingFallbackTimer.Factor, ThinkingFallbackTimer.MinimumSamples,
                ThinkingFallbackTimer.Window, ThinkingFallbackTimer.FailuresToSkip, skipForMinutes = ThinkingFallbackTimer.SkipFor.TotalMinutes,
                appliesTo = "a cloud Thinking route (not this PC or the home network) with Companion > Thinking > If Thinking fails set"
            },
            dataDirectory = saved,
            provenance = "FIXTURE, NOT AI: canned words from two endpoints on 127.0.0.1",
            rehearsal = steps.Select(s => new { s.Name, s.Passed, s.Detail })
        };
    }

    private sealed record Step(string Name, bool Passed, object Detail);

    /// <summary>FIXTURE route and model <see cref="SeedAsync"/> saves (an NVIDIA Build address, never called) and its fallback.</summary>
    internal const string SeedOrigin = "https://integrate.api.nvidia.com/v1", SeedModel = "nvidia/fixture-nemotron",
        SeedFallbackOrigin = "https://openrouter.ai/api/v1", SeedFallbackModel = "fixture/fallback:free";

    /// <summary>For checking the plan: saves a FIXTURE cloud Thinking route, If Thinking fails and model-speed.json with three
    /// replies (1.0-1.4 s) in a disposable <paramref name="dataDirectory"/>, never Martlet's own data folder. Nothing is contacted.</summary>
    internal static async Task<object> SeedAsync(string? dataDirectory, CancellationToken cancellation)
    {
        if (dataDirectory is not { Length: > 0 } directory || !Path.IsPathFullyQualified(directory) ||
            string.Equals(Path.GetFullPath(directory).TrimEnd('\\'), Path.GetFullPath(SettingsStore.DefaultDataDirectory()).TrimEnd('\\'),
                StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("seed needs an explicit, disposable dataDirectory, never Martlet's own data folder.");
        Directory.CreateDirectory(directory);
        var store = new SettingsStore(directory);
        var loaded = await store.LoadAsync(cancellation);
        if (loaded.Error is not null) throw new InvalidOperationException(loaded.Error.Summary);
        var settings = loaded.Settings ?? AppSettings.CreateUnconfigured() with
        {
            SchemaVersion = AppSettings.CurrentSchemaVersion, Companion = CompanionSettings.Create(), Memory = MemorySettings.Create()
        };
        var open = settings.Setup is not null ? settings : settings with
        {
            Setup = new SetupSettings { SchemaVersion = SetupSettings.CurrentSchemaVersion, Checkpoint = SetupStep.Choice, Routes = [], PendingRemovals = [] }
        };
        var next = ChatCompletionsSetup.SelectRoute(open, SeedOrigin, SeedModel) with
        {
            ThinkingFallback = new() { Origin = SeedFallbackOrigin, ModelId = SeedFallbackModel, ConfigurationRevision = Guid.NewGuid() }
        };
        var save = await store.SaveAsync(next, loaded.Revision, cancellation);
        if (save.Error is not null) throw new InvalidOperationException(save.Error.Summary);
        var route = next.ThinkingRoute()!;
        var list = MeasuredFirstWords.Load(directory);
        var at = DateTimeOffset.UtcNow.AddMinutes(-10);
        foreach (var ms in new[] { 1_000, 1_200, 1_400 }) list = list.With(route.Origin, route.ModelId, ms, at = at.AddMinutes(1));
        if (!list.Save(directory)) throw new IOException("model-speed.json couldn't be saved.");
        return new
        {
            seeded = true, route = new { route.Origin, route.ModelId }, fallback = new { origin = SeedFallbackOrigin, model = SeedFallbackModel },
            samples = list.Find(route.Origin, route.ModelId)!.Samples,
            note = "FIXTURE route, fallback and times, NOT AI; nothing was contacted. Call again without seed to read the plan."
        };
    }

    // The saved Thinking route and If Thinking fails, and the plan a new conversation would start with (model-speed.json's times).
    private static async Task<object?> SavedAsync(string? dataDirectory, CancellationToken cancellation)
    {
        if (dataDirectory is null || !Directory.Exists(dataDirectory)) return null;
        var loaded = await new SettingsStore(dataDirectory).LoadAsync(cancellation);
        var settings = loaded.Settings;
        var thinking = settings.ThinkingRoute();
        var fallback = settings?.ThinkingFallback is { } saved && !saved.Same(thinking) ? saved : null;
        var key = ThinkingFallbackTimer.KeyFor(thinking, fallback is not null);
        var measured = MeasuredFirstWords.Load(dataDirectory).Find(thinking?.Origin, thinking?.ModelId);
        var timer = new ThinkingFallbackTimer();
        if (key is not null && measured is not null) timer.Seed(key, measured.Samples);
        var plan = key is null ? null : timer.Plan(key, DateTimeOffset.UtcNow);
        return new
        {
            thinking = thinking is null ? null : new { thinking.Origin, thinking.ModelId, routeType = thinking.RouteType?.ToString() },
            fallback = fallback is null ? null : new { fallback.Origin, fallback.ModelId },
            applies = key is not null,
            why = key is not null ? null : fallback is null ? "If Thinking fails isn't set" : "Thinking runs on this PC or the home network",
            measuredSamples = measured?.Samples,
            waitMs = plan?.Wait?.TotalMilliseconds,
            plan = plan?.Why
        };
    }

    private static (bool Ok, object Detail) Skips()
    {
        var timer = new ThinkingFallbackTimer();
        const string key = "https://integrate.api.nvidia.com fixture/model";
        var now = DateTimeOffset.UtcNow;
        var starting = timer.Plan(key, now).Wait;
        timer.Seed(key, [900, 1_000, 1_100]);
        var measured = timer.Plan(key, now).Wait;
        var failed = Snapshot("FirstDeltaTimeout");
        timer.Noted(key, failed, now);
        var afterOne = timer.Plan(key, now).FallbackFirst;
        timer.Noted(key, failed, now);
        var afterTwo = timer.Plan(key, now).FallbackFirst;
        var later = timer.Plan(key, now + ThinkingFallbackTimer.SkipFor).FallbackFirst;
        var ok = starting == ThinkingFallbackTimer.Starting && measured == TimeSpan.FromMilliseconds(3_000) && !afterOne && afterTwo && !later;
        return (ok, new
        {
            startingMs = starting?.TotalMilliseconds, measuredMs = measured?.TotalMilliseconds, fallbackFirstAfterOneFailure = afterOne,
            fallbackFirstAfterTwoFailures = afterTwo, fallbackFirstAfterSkip = later
        });
    }

    private static ConversationSnapshot Snapshot(string fellBack) => new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 1, 1,
        ConversationState.Completed, ConversationFailure.None, null, null, 10, true, 0, 0, 0, 0, 0, 0, null, null, null,
        0, 0, 0, false, true, false, 0, null, null, false, FellBackAfter: fellBack, FirstTextAfter: TimeSpan.FromSeconds(4));

    internal sealed record Endpoint(int Requests, bool Answered, bool Failed);

    internal sealed record Turn(string State, string Text, string? FellBackAfter, double? FirstWordsMs, Endpoint Model, Endpoint Fallback,
        IReadOnlyList<string> Why);

    private static async Task<Turn> TurnAsync(int modelMs, int fallbackMs, TimeSpan wait, bool first, CancellationToken cancellation,
        bool fallbackFails = false)
    {
        var model = new Fixture(ModelWords, modelMs, false);
        var fallback = new Fixture(FallbackWords, fallbackMs, fallbackFails);
        try
        {
            await using var runtime = ConversationRuntime.Create(new SpokenReplyCheck.NoCredentials());
            var request = new ConversationRequest(new BoundedTextInput("Say hi.", "Fixture check."),
                new TextModelSelection(ChatCompletionsSetup.Alias, Model), new TextGenerationLimits(), new ConversationLimits(),
                chat: new ChatCompletionsTarget(model.BaseUrl, Keyless: true),
                fallback: new TextFallback(new ChatCompletionsTarget(fallback.BaseUrl, Keyless: true),
                    new TextModelSelection(ChatCompletionsSetup.FallbackAlias, FallbackModel)))
            {
                FallbackAfter = wait, FallbackFirst = first
            };
            var turn = runtime.Start(request, new Permissions(model.BaseUrl, fallback.BaseUrl), cancellation);
            var terminal = await turn.Completion.WaitAsync(TimeSpan.FromSeconds(30), cancellation);
            await turn.OwnershipRelease.WaitAsync(TimeSpan.FromSeconds(10), cancellation);
            return new(terminal.State.ToString(), turn.Content.Text, terminal.FellBackAfter, terminal.FirstTextAfter?.TotalMilliseconds,
                model.Report(), fallback.Report(), [.. turn.SentRequests.Select(s => s.Why)]);
        }
        finally
        {
            await model.DisposeAsync();
            await fallback.DisposeAsync();
        }
    }

    // Each request's key binding is its own endpoint's address, as the desktop binds the Thinking and fallback keys.
    private sealed class Permissions(string modelBase, string fallbackBase) : IConversationAuthorizationSource
    {
        public ValueTask<AuthorizedTextOperation?> AuthorizeTextAsync(TextAuthorizationAction action, CancellationToken cancellationToken)
        {
            var cap = DateTimeOffset.UtcNow.AddSeconds(30);
            var until = action.Context.Deadline < cap ? action.Context.Deadline : cap;
            var origin = action.Model.ModelAlias == ChatCompletionsSetup.FallbackAlias ? fallbackBase : modelBase;
            return ValueTask.FromResult<AuthorizedTextOperation?>(new(new TextDisclosureAuthorization(
                new(ChatCompletionsSetup.BaseUri(origin), ProviderRole.Llm, action.Model.UpstreamModelId), action.Model, action.Context.Ids,
                action.Context.Epoch, action.Limits, until, true, true), new(action.Budget, until)));
        }

        public ValueTask<AuthorizedSpeechOperation?> AuthorizeSpeechAsync(SpeechAuthorizationAction action, CancellationToken cancellationToken) =>
            ValueTask.FromResult<AuthorizedSpeechOperation?>(null);
    }

    // FIXTURE, NOT AI: a Chat Completions endpoint on 127.0.0.1 that answers one sentence (or an error) after a set wait.
    private sealed class Fixture : IAsyncDisposable
    {
        private readonly string words;
        private readonly int wordsMs;
        private readonly bool fails;
        private readonly LoopbackHttpServer server;
        private int requests;
        private bool answered, failed;

        internal Fixture(string words, int wordsMs, bool fails)
        {
            (this.words, this.wordsMs, this.fails) = (words, wordsMs, fails);
            server = new LoopbackHttpServer(AnswerAsync);
        }

        internal string BaseUrl => server.V1;

        internal Endpoint Report()
        {
            lock (this) return new(requests, answered, failed);
        }

        private async Task AnswerAsync(LoopbackExchange exchange)
        {
            lock (this) requests++;
            var closed = exchange.ClosedAsync();
            if (!fails) await exchange.StartAsync();
            if (await Task.WhenAny(closed, Task.Delay(wordsMs, exchange.Cancellation)) == closed) return;
            try
            {
                if (fails)
                {
                    await exchange.RespondAsync(503, "{}");
                    lock (this) failed = true;
                    return;
                }
                await exchange.WriteAsync(FixtureChat.Content(words));
                await exchange.WriteAsync(FixtureChat.Finish());
                lock (this) answered = true;
            }
            catch (IOException) { }
        }

        public ValueTask DisposeAsync() => server.DisposeAsync();
    }
}
