using System.Text;
using Martlet.Core.Contracts;

namespace Martlet.Providers.Tests;

public sealed class AppCancellationTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(20);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Either_original_source_cancels_admission_without_consuming_authorization(bool operationSource)
    {
        using var caller = new CancellationTokenSource();
        using var operation = new CancellationTokenSource();
        (operationSource ? operation : caller).Cancel();
        var credentials = new FixtureCredentials();
        var handler = new RecordingHandler();
        using var adapter = OpenAiTranscriptionAdapter.CreateForFixture(handler, credentials, new FixtureClock());
        var context = ProviderFixtures.Context() with { Deadline = ProviderFixtures.Now };
        var limits = new TranscriptionLimits();
        var authorization = ProviderFixtures.Authorize(context, limits);

        var result = await adapter.TranscribeAsync(context, "gpt-transcribe",
            BoundedWaveAudio.FromWave(ProviderFixtures.Wave()), limits, authorization,
            cancellationToken: caller.Token, operationCancellationToken: operation.Token);

        AssertCanceled(result, context);
        Assert.Equal(0, credentials.Calls);
        Assert.Equal(0, handler.Calls);
        Assert.Empty(handler.Body);
        Assert.True(authorization.TryConsume());
    }

    public static IEnumerable<object[]> BlockedCallbackCases()
    {
        foreach (bool operationSource in new[] { false, true })
        {
            foreach (bool cancelOther in new[] { false, true })
                foreach (string boundary in new[] { "credentials", "serialization", "headers", "body" })
                    yield return new object[] { operationSource, cancelOther, boundary, "success" };
            foreach (var (boundary, failure) in new[]
            {
                ("credentials", "credential"), ("headers", "network"), ("headers", "io"),
                ("headers", "http-truncated"), ("headers", "io-truncated"), ("headers", "canceled"),
                ("headers", "deadline"), ("headers", "cutoff"), ("body", "network"), ("body", "io")
            })
                yield return new object[] { operationSource, false, boundary, failure };
        }
    }

    [Theory]
    [MemberData(nameof(BlockedCallbackCases))]
    public async Task Original_sources_win_while_a_newer_callback_blocks_propagation(
        bool operationSource, bool cancelOther, string boundary, string failure)
    {
        using var caller = new CancellationTokenSource();
        using var operationCancellation = new CancellationTokenSource();
        var selected = operationSource ? operationCancellation : caller;
        var other = operationSource ? caller : operationCancellation;
        using var callbackRelease = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var callbackEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var clock = new FixtureClock { DeferTimerCallbacks = true };
        CancellationToken requestToken = default;
        BoundProviderCredential? credential = null;
        async Task PauseAsync()
        {
            entered.TrySetResult();
            await resume.Task.WaitAsync(TestTimeout);
            if (failure == "deadline")
                clock.Advance(TimeSpan.FromSeconds(30));
            else if (failure != "success")
                throw BoundaryFailure(failure);
        }
        var credentials = new FixtureCredentials
        {
            Resolve = async (binding, token) =>
            {
                requestToken = token;
                if (boundary == "credentials")
                    await PauseAsync();
                return credential = new(binding, ProviderFixtures.Secret);
            }
        };
        byte[] bytes = Encoding.UTF8.GetBytes("""{"text":"Rejected late fixture transcript."}""");
        using var body = new FragmentedTextBody(bytes, bytes.Length) { IgnoreCancellation = true };
        if (boundary == "body")
            body.BeforeRead = _ => PauseAsync();
        var handler = new RecordingHandler
        {
            Respond = async (_, _) =>
            {
                if (boundary == "headers")
                    await PauseAsync();
                var response = ProviderFixtures.Json();
                if (boundary == "body")
                {
                    response.Content.Dispose();
                    response.Content = new StreamContent(body);
                    response.Content.Headers.ContentType = new("application/json");
                }
                return response;
            }
        };
        if (boundary == "serialization")
            handler.BeforeSerialization = () => PauseAsync().GetAwaiter().GetResult();
        using var adapter = OpenAiTranscriptionAdapter.CreateForFixture(handler, credentials, clock);
        var context = ProviderFixtures.Context();
        var limits = new TranscriptionLimits();
        var authorization = ProviderFixtures.Authorize(context, limits);
        var audio = BoundedWaveAudio.FromWave(ProviderFixtures.Wave());
        var transcription = Task.Run(() => adapter.TranscribeAsync(context, "gpt-transcribe", audio,
            limits, authorization, caller.Token, operationCancellation.Token));
        CancellationTokenRegistration registration = default;
        Task cancellation = Task.CompletedTask;
        try
        {
            await entered.Task.WaitAsync(TestTimeout);
            // This newer LIFO callback runs before the request window's source registration.
            registration = selected.Token.Register(() =>
            {
                callbackEntered.TrySetResult();
                callbackRelease.Wait();
            });
            cancellation = selected.CancelAsync();
            await callbackEntered.Task.WaitAsync(TestTimeout);
            Assert.True(selected.IsCancellationRequested);
            Assert.False(other.IsCancellationRequested);
            Assert.True(requestToken.CanBeCanceled);
            Assert.False(requestToken.IsCancellationRequested);
            if (cancelOther)
            {
                await other.CancelAsync().WaitAsync(TestTimeout);
                Assert.True(requestToken.IsCancellationRequested);
            }
            resume.TrySetResult();
            var result = await transcription.WaitAsync(TestTimeout);

            AssertCanceled(result, context);
            Assert.Equal(1, credentials.Calls);
            Assert.Equal(boundary == "credentials" ? 0 : 1, handler.Calls);
            if (boundary is "credentials" or "serialization")
                Assert.Empty(handler.Body);
            if (boundary == "body")
            {
                Assert.True(body.Disposed);
                if (failure == "success")
                    Assert.Equal(bytes.Length, body.BytesRead);
            }
            if (failure != "credential")
            {
                Assert.NotNull(credential);
                Assert.Throws<CredentialUnavailableException>(() => credential.CreateAuthorization());
            }
            Assert.False(authorization.TryConsume());
            Assert.False(handler.Disposed);
            Assert.False(cancellation.IsCompleted);
        }
        finally
        {
            callbackRelease.Set();
            resume.TrySetResult();
            await cancellation.WaitAsync(TestTimeout);
            registration.Dispose();
            await transcription.WaitAsync(TestTimeout);
        }
    }

    [Theory]
    [InlineData(false, "credentials")]
    [InlineData(true, "credentials")]
    [InlineData(false, "headers")]
    [InlineData(true, "headers")]
    [InlineData(false, "body")]
    [InlineData(true, "body")]
    public async Task Either_source_cooperatively_aborts_in_flight_awaits(bool operationSource, string boundary)
    {
        using var caller = new CancellationTokenSource();
        using var operation = new CancellationTokenSource();
        var selected = operationSource ? operation : caller;
        CancellationToken requestToken = default;
        var credentials = new FixtureCredentials
        {
            Resolve = async (binding, token) =>
            {
                requestToken = token;
                if (boundary == "credentials")
                {
                    selected.Cancel();
                    await Task.Delay(Timeout.InfiniteTimeSpan, token);
                }
                return new(binding, ProviderFixtures.Secret);
            }
        };
        using var body = new NonSeekableBody([], selected.Cancel);
        var handler = new RecordingHandler
        {
            Respond = async (_, token) =>
            {
                if (boundary == "headers")
                {
                    selected.Cancel();
                    await Task.Delay(Timeout.InfiniteTimeSpan, token);
                }
                var response = ProviderFixtures.Json();
                if (boundary == "body")
                {
                    response.Content.Dispose();
                    response.Content = new StreamContent(body);
                    response.Content.Headers.ContentType = new("application/json");
                }
                return response;
            }
        };
        using var adapter = OpenAiTranscriptionAdapter.CreateForFixture(handler, credentials, new FixtureClock());
        var context = ProviderFixtures.Context();
        var limits = new TranscriptionLimits();
        var result = await adapter.TranscribeAsync(context, "gpt-transcribe",
            BoundedWaveAudio.FromWave(ProviderFixtures.Wave()), limits, ProviderFixtures.Authorize(context, limits),
            caller.Token, operation.Token).WaitAsync(TestTimeout);

        AssertCanceled(result, context);
        Assert.True(requestToken.IsCancellationRequested);
        Assert.Equal(boundary == "credentials" ? 0 : 1, handler.Calls);
        if (boundary == "credentials")
            Assert.Empty(handler.Body);
        if (boundary == "body")
            Assert.True(body.Disposed);
    }

    [Fact]
    public async Task Legacy_optional_token_and_explicit_none_preserve_success()
    {
        var handler = new RecordingHandler();
        using var adapter = OpenAiTranscriptionAdapter.CreateForFixture(handler, new FixtureCredentials(), new FixtureClock());
        var context = ProviderFixtures.Context();
        var limits = new TranscriptionLimits();
        var audio = BoundedWaveAudio.FromWave(ProviderFixtures.Wave());
        var omitted = await adapter.TranscribeAsync(context, "gpt-transcribe", audio, limits,
            ProviderFixtures.Authorize(context, limits));
        var legacyNone = await adapter.TranscribeAsync(context, "gpt-transcribe", audio, limits,
            ProviderFixtures.Authorize(context, limits), CancellationToken.None);
        var bothNone = await adapter.TranscribeAsync(context, "gpt-transcribe", audio, limits,
            ProviderFixtures.Authorize(context, limits), CancellationToken.None, CancellationToken.None);

        foreach (var result in new[] { omitted, legacyNone, bothNone })
        {
            Assert.Equal(TranscriptionOutcome.Completed, result.Outcome);
            Assert.Equal("Synthetic fixture transcript.", result.Text);
            Assert.Equal(context, result.Context);
            Assert.Null(result.Failure);
        }
        Assert.Equal(3, handler.Calls);
    }

    [Fact]
    public void Only_legacy_overload_has_an_optional_cancellation_token()
    {
        var overloads = typeof(OpenAiTranscriptionAdapter).GetMethods()
            .Where(method => method.Name == nameof(OpenAiTranscriptionAdapter.TranscribeAsync)).ToArray();
        Assert.Equal(2, overloads.Length);
        var legacy = Assert.Single(overloads, method => method.GetParameters().Length == 6).GetParameters();
        Assert.Equal("cancellationToken", legacy[^1].Name);
        Assert.True(legacy[^1].IsOptional);
        var dual = Assert.Single(overloads, method => method.GetParameters().Length == 7).GetParameters();
        Assert.Equal("cancellationToken", dual[^2].Name);
        Assert.Equal("operationCancellationToken", dual[^1].Name);
        Assert.False(dual[^2].IsOptional);
        Assert.False(dual[^1].IsOptional);
    }

    private static Exception BoundaryFailure(string failure) => failure switch
    {
        "credential" => new CredentialUnavailableException(),
        "network" => new HttpRequestException(ProviderFixtures.ContentCanary),
        "io" => new IOException(ProviderFixtures.ContentCanary),
        "http-truncated" => new HttpRequestException(HttpRequestError.ResponseEnded, ProviderFixtures.ContentCanary),
        "io-truncated" => new HttpIOException(HttpRequestError.ResponseEnded, ProviderFixtures.ContentCanary),
        "canceled" => new OperationCanceledException(),
        "cutoff" => new RequestCutoffException(ProviderFailureCode.ConsentExpired),
        _ => throw new ArgumentOutOfRangeException(nameof(failure))
    };

    private static void AssertCanceled(TranscriptionResult result, ProviderRequestContext context)
    {
        Assert.Equal(TranscriptionOutcome.Canceled, result.Outcome);
        Assert.Null(result.Failure);
        Assert.Null(result.Text);
        Assert.Equal(context, result.Context);
        Assert.Equal(EvidenceProvenance.Fixture, result.Provenance);
        Assert.Equal(ProviderEventKind.Canceled, result.ToTerminalEvent().Kind);
    }
}
