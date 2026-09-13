using Martlet.Core.Contracts;

namespace Martlet.Providers.Tests;

public sealed class CancellationTests
{
    [Theory]
    [InlineData("before", 0, 0)]
    [InlineData("credentials", 1, 0)]
    [InlineData("send", 1, 1)]
    [InlineData("late-success", 1, 1)]
    [InlineData("body", 1, 1)]
    public async Task Caller_cancellation_propagates_and_late_success_is_never_published(string phase, int credentialCalls, int sends)
    {
        using var cancellation = new CancellationTokenSource();
        var credentials = new FixtureCredentials
        {
            Resolve = (binding, token) =>
            {
                if (phase == "credentials")
                    cancellation.Cancel();
                return ValueTask.FromResult<BoundProviderCredential?>(new(binding, ProviderFixtures.Secret));
            }
        };
        var handler = new RecordingHandler
        {
            Respond = async (_, token) =>
            {
                if (phase is "send" or "late-success")
                    cancellation.Cancel();
                if (phase == "send")
                    await Task.Delay(Timeout.InfiniteTimeSpan, token);
                var response = ProviderFixtures.Json();
                if (phase == "body")
                {
                    response.Content = new StreamContent(new NonSeekableBody([1, 2, 3], cancellation.Cancel));
                    response.Content.Headers.ContentType = new("application/json");
                }
                return response;
            }
        };
        using var adapter = OpenAiTranscriptionAdapter.CreateForFixture(handler, credentials, new FixtureClock());
        var context = ProviderFixtures.Context();
        var limits = new TranscriptionLimits();
        if (phase == "before")
            cancellation.Cancel();
        var result = await adapter.TranscribeAsync(context, "gpt-transcribe", BoundedWaveAudio.FromWave(ProviderFixtures.Wave()),
            limits, ProviderFixtures.Authorize(context, limits), cancellation.Token);
        Assert.Equal(TranscriptionOutcome.Canceled, result.Outcome);
        Assert.Null(result.Failure);
        Assert.Null(result.Text);
        Assert.Equal(context, result.Context);
        Assert.Equal(ProviderEventKind.Canceled, result.ToTerminalEvent().Kind);
        Assert.Equal(credentialCalls, credentials.Calls);
        Assert.Equal(sends, handler.Calls);
    }

    [Theory]
    [InlineData("before", 0, 0)]
    [InlineData("credentials", 1, 0)]
    [InlineData("send", 1, 1)]
    [InlineData("body", 1, 1)]
    [InlineData("request-limit", 1, 1)]
    [InlineData("consent-expiry", 1, 1)]
    public async Task Deadline_bounds_every_phase_using_injected_time_not_sleeps(string phase, int credentialCalls, int sends)
    {
        var clock = new FixtureClock();
        var credentials = new FixtureCredentials
        {
            Resolve = (binding, _) =>
            {
                if (phase == "credentials")
                    clock.Advance(TimeSpan.FromSeconds(30));
                return ValueTask.FromResult<BoundProviderCredential?>(new(binding, ProviderFixtures.Secret));
            }
        };
        var handler = new RecordingHandler
        {
            Respond = async (_, token) =>
            {
                if (phase != "body")
                {
                    clock.Advance(TimeSpan.FromSeconds(phase is "request-limit" or "consent-expiry" ? 5 : 30));
                    await Task.Delay(Timeout.InfiniteTimeSpan, token);
                }
                var response = ProviderFixtures.Json();
                if (phase == "body")
                {
                    response.Content = new StreamContent(new NonSeekableBody([1], () => clock.Advance(TimeSpan.FromSeconds(30))));
                    response.Content.Headers.ContentType = new("application/json");
                }
                return response;
            }
        };
        using var adapter = OpenAiTranscriptionAdapter.CreateForFixture(handler, credentials, clock);
        var context = ProviderFixtures.Context();
        var limits = new TranscriptionLimits { MaxRequestTime = TimeSpan.FromSeconds(phase == "request-limit" ? 5 : 60) };
        if (phase == "before")
            clock.Advance(TimeSpan.FromSeconds(30));
        var result = await adapter.TranscribeAsync(context, "gpt-transcribe", BoundedWaveAudio.FromWave(ProviderFixtures.Wave()),
            limits, ProviderFixtures.Authorize(context, limits, expiry: phase == "consent-expiry" ? ProviderFixtures.Now.AddSeconds(5) : null));
        Assert.Equal(TranscriptionOutcome.DeadlineExceeded, result.Outcome);
        Assert.Equal(ProviderFailureCode.DeadlineExceeded, result.Failure!.Code);
        Assert.Equal(ErrorCode.DeadlineExceeded, result.Failure.Error.Code);
        Assert.Equal(ProviderEventKind.Failed, result.ToTerminalEvent().Kind);
        Assert.Equal(context, result.Context);
        Assert.Null(result.Text);
        Assert.Equal(credentialCalls, credentials.Calls);
        Assert.Equal(sends, handler.Calls);
    }

    [Fact]
    public async Task Caller_cancellation_takes_precedence_over_elapsed_deadline()
    {
        using var cancellation = new CancellationTokenSource();
        var clock = new FixtureClock();
        var handler = new RecordingHandler
        {
            Respond = (_, _) =>
            {
                cancellation.Cancel();
                clock.Advance(TimeSpan.FromMinutes(1));
                return Task.FromResult(ProviderFixtures.Json());
            }
        };
        using var adapter = OpenAiTranscriptionAdapter.CreateForFixture(handler, new FixtureCredentials(), clock);
        var context = ProviderFixtures.Context();
        var limits = new TranscriptionLimits();
        var result = await adapter.TranscribeAsync(context, "gpt-transcribe", BoundedWaveAudio.FromWave(ProviderFixtures.Wave()),
            limits, ProviderFixtures.Authorize(context, limits), cancellation.Token);
        Assert.Equal(TranscriptionOutcome.Canceled, result.Outcome);
    }

    [Theory]
    [InlineData("request")]
    [InlineData("io")]
    [InlineData("http-truncated")]
    [InlineData("io-truncated")]
    public async Task Transport_failures_are_not_cancel_or_no_speech_and_do_not_retry(string fault)
    {
        var handler = new RecordingHandler
        {
            Respond = (_, _) => throw (fault switch
            {
                "io" => new IOException(ProviderFixtures.ContentCanary),
                "http-truncated" => new HttpRequestException(HttpRequestError.ResponseEnded, ProviderFixtures.ContentCanary),
                "io-truncated" => new HttpIOException(HttpRequestError.ResponseEnded, ProviderFixtures.ContentCanary),
                _ => new HttpRequestException(ProviderFixtures.ContentCanary)
            })
        };
        var result = await TranscriptionTransportTests.Run(handler);
        Assert.Equal(TranscriptionOutcome.Failed, result.Outcome);
        Assert.Equal(fault.EndsWith("truncated", StringComparison.Ordinal) ? ProviderFailureCode.ResponseTruncated :
            ProviderFailureCode.Network, result.Failure!.Code);
        Assert.Equal(1, handler.Calls);
        Assert.DoesNotContain(ProviderFixtures.ContentCanary, result.Failure.ToString());
    }
}
