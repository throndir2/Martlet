using System.Net.Http.Headers;
using System.Text.Json;
using Martlet.Core.Contracts;

namespace Martlet.Providers.Tests;

public sealed class ReviewRegressionTests
{
    [Theory]
    [InlineData("consent", ProviderFailureCode.ConsentExpired)]
    [InlineData("consent-clock-rollback", ProviderFailureCode.ConsentExpired)]
    [InlineData("consent-clock-forward", ProviderFailureCode.ConsentExpired)]
    [InlineData("request-deadline", ProviderFailureCode.DeadlineExceeded)]
    [InlineData("request-duration", ProviderFailureCode.DeadlineExceeded)]
    [InlineData("request-duration-clock-rollback", ProviderFailureCode.DeadlineExceeded)]
    public async Task Deferred_timer_after_credential_lookup_cannot_authorize_late_upload(string cutoff, ProviderFailureCode expected)
    {
        var clock = new FixtureClock { DeferTimerCallbacks = true };
        BoundProviderCredential? credential = null;
        var credentials = new FixtureCredentials
        {
            Resolve = async (binding, token) =>
            {
                await Task.Yield();
                clock.Advance(TimeSpan.FromSeconds(cutoff == "consent-clock-forward" ? 1 : 5));
                if (cutoff.EndsWith("clock-rollback", StringComparison.Ordinal))
                    clock.ShiftUtc(TimeSpan.FromHours(-1));
                if (cutoff == "consent-clock-forward")
                    clock.ShiftUtc(TimeSpan.FromHours(1));
                Assert.False(token.IsCancellationRequested);
                return credential = new(binding, ProviderFixtures.Secret);
            }
        };
        var handler = new RecordingHandler();
        using var adapter = OpenAiTranscriptionAdapter.CreateForFixture(handler, credentials, clock);
        var context = ProviderFixtures.Context();
        if (cutoff == "request-deadline")
            context = context with { Deadline = ProviderFixtures.Now.AddSeconds(5) };
        var limits = new TranscriptionLimits
        {
            MaxRequestTime = TimeSpan.FromSeconds(cutoff.StartsWith("request-duration", StringComparison.Ordinal) ? 5 : 60)
        };
        var result = await adapter.TranscribeAsync(context, "gpt-transcribe",
            BoundedWaveAudio.FromWave(ProviderFixtures.Wave()), limits,
            ProviderFixtures.Authorize(context, limits, expiry: cutoff.StartsWith("consent", StringComparison.Ordinal) ?
                ProviderFixtures.Now.AddSeconds(5) : null));

        Assert.NotNull(credential);
        Assert.Throws<CredentialUnavailableException>(() => credential.CreateAuthorization());
        Assert.Equal(0, handler.Calls);
        Assert.Empty(handler.Body);
        Assert.Equal(expected, result.Failure!.Code);
        Assert.Equal(expected == ProviderFailureCode.ConsentExpired ? TranscriptionOutcome.Failed :
            TranscriptionOutcome.DeadlineExceeded, result.Outcome);
        Assert.Null(result.Text);
    }

    [Theory]
    [InlineData(401, "http-io", ProviderFailureCode.Authentication)]
    [InlineData(403, "http-io", ProviderFailureCode.PermissionDenied)]
    [InlineData(429, "http-io", ProviderFailureCode.RateLimited)]
    [InlineData(200, "http-io", ProviderFailureCode.ResponseTruncated)]
    [InlineData(401, "http-request", ProviderFailureCode.Authentication)]
    [InlineData(403, "http-request", ProviderFailureCode.PermissionDenied)]
    [InlineData(429, "http-request", ProviderFailureCode.RateLimited)]
    [InlineData(200, "http-request", ProviderFailureCode.ResponseTruncated)]
    [InlineData(401, "io", ProviderFailureCode.Authentication)]
    [InlineData(403, "io", ProviderFailureCode.PermissionDenied)]
    [InlineData(429, "io", ProviderFailureCode.RateLimited)]
    [InlineData(200, "io", ProviderFailureCode.Network)]
    public async Task Throwing_response_body_preserves_known_http_failure_and_advice(int status, string transportError, ProviderFailureCode expected)
    {
        var body = new NonSeekableBody([], () => throw (transportError switch
        {
            "http-io" => new HttpIOException(HttpRequestError.ResponseEnded, ProviderFixtures.ContentCanary + ProviderFixtures.Secret),
            "http-request" => new HttpRequestException(HttpRequestError.ResponseEnded, ProviderFixtures.ContentCanary + ProviderFixtures.Secret),
            _ => new IOException(ProviderFixtures.ContentCanary + ProviderFixtures.Secret)
        }));
        var handler = new RecordingHandler
        {
            Respond = (_, _) =>
            {
                var response = ProviderFixtures.Json(status: status);
                response.Content = new StreamContent(body);
                response.Content.Headers.ContentType = new("application/json");
                response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(17));
                return Task.FromResult(response);
            }
        };
        var result = await TranscriptionTransportTests.Run(handler);
        Assert.Equal(expected, result.Failure!.Code);
        Assert.Equal(status == 200 ? null : TimeSpan.FromSeconds(17), result.Failure.RetryAfter);
        Assert.Equal(TranscriptionOutcome.Failed, result.Outcome);
        Assert.Equal(1, handler.Calls);
        Assert.True(body.Disposed);
        Assert.Null(result.Text);
        Assert.False(result.Failure.Error.Retryable);
        string metadata = JsonSerializer.Serialize(result) + result.Failure;
        Assert.DoesNotContain(ProviderFixtures.ContentCanary, metadata);
        Assert.DoesNotContain(ProviderFixtures.Secret, metadata);
    }

    [Theory]
    [InlineData("consent", ProviderFailureCode.ConsentExpired)]
    [InlineData("request", ProviderFailureCode.DeadlineExceeded)]
    public async Task Delayed_transport_cannot_begin_serialization_after_cutoff(string cutoff, ProviderFailureCode expected)
    {
        var clock = new FixtureClock { DeferTimerCallbacks = true };
        var handler = new RecordingHandler { BeforeSerialization = () => clock.Advance(TimeSpan.FromSeconds(5)) };
        using var adapter = OpenAiTranscriptionAdapter.CreateForFixture(handler, new FixtureCredentials(), clock);
        var context = ProviderFixtures.Context();
        var limits = new TranscriptionLimits { MaxRequestTime = TimeSpan.FromSeconds(cutoff == "request" ? 5 : 60) };
        var result = await adapter.TranscribeAsync(context, "gpt-transcribe",
            BoundedWaveAudio.FromWave(ProviderFixtures.Wave()), limits,
            ProviderFixtures.Authorize(context, limits, expiry: cutoff == "consent" ? ProviderFixtures.Now.AddSeconds(5) : null));
        Assert.Equal(expected, result.Failure!.Code);
        Assert.Equal(1, handler.Calls);
        Assert.Empty(handler.Body);
    }

    [Theory]
    [InlineData("canceled")]
    [InlineData("deadline")]
    [InlineData("deferred-deadline")]
    public async Task Cancellation_and_deadline_precede_known_http_status_when_body_throws(string stop)
    {
        using var cancellation = new CancellationTokenSource();
        var clock = new FixtureClock { DeferTimerCallbacks = stop == "deferred-deadline" };
        var body = new NonSeekableBody([], () =>
        {
            if (stop == "canceled")
                cancellation.Cancel();
            else
                clock.Advance(TimeSpan.FromSeconds(30));
            throw new HttpIOException(HttpRequestError.ResponseEnded, ProviderFixtures.ContentCanary);
        });
        var handler = new RecordingHandler
        {
            Respond = (_, _) =>
            {
                var response = ProviderFixtures.Json(status: 401);
                response.Content = new StreamContent(body);
                return Task.FromResult(response);
            }
        };
        using var adapter = OpenAiTranscriptionAdapter.CreateForFixture(handler, new FixtureCredentials(), clock);
        var context = ProviderFixtures.Context();
        var limits = new TranscriptionLimits();
        var result = await adapter.TranscribeAsync(context, "gpt-transcribe",
            BoundedWaveAudio.FromWave(ProviderFixtures.Wave()), limits,
            ProviderFixtures.Authorize(context, limits), cancellation.Token);
        Assert.Equal(stop == "canceled" ? TranscriptionOutcome.Canceled : TranscriptionOutcome.DeadlineExceeded, result.Outcome);
        Assert.Equal(1, handler.Calls);
        Assert.True(body.Disposed);
    }
}
