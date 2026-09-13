using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Martlet.Core.Contracts;

namespace Martlet.Providers.Tests;

public sealed class ResponseTests
{
    [Theory]
    [InlineData("""{"text":"Synthetic transcription","languages":[{"code":"fr"}]}""", TranscriptionOutcome.Completed, "fr")]
    [InlineData("""{"text":"Synthetic transcription","languages":[]}""", TranscriptionOutcome.Completed, null)]
    [InlineData("""{"text":"Synthetic transcription"}""", TranscriptionOutcome.Completed, null)]
    [InlineData("""{"text":""}""", TranscriptionOutcome.NoSpeech, null)]
    [InlineData("""{"text":" \r\n\t "}""", TranscriptionOutcome.NoSpeech, null)]
    public async Task Success_no_speech_and_unknown_language_are_distinct(string json, TranscriptionOutcome outcome, string? language)
    {
        var result = await Respond(json);
        Assert.Equal(outcome, result.Outcome);
        Assert.Null(result.Failure);
        Assert.Null(result.Confidence);
        Assert.Null(result.Usage.EstimatedCost);
        Assert.Equal(UsageKind.Unknown, result.Usage.Kind);
        if (language is not null)
            Assert.Equal(language, Assert.Single(result.Languages));
        else
            Assert.Empty(result.Languages);
        if (outcome == TranscriptionOutcome.NoSpeech)
            Assert.Null(result.Text);
        else
            Assert.Equal("Synthetic transcription", result.Text);
    }

    [Theory]
    [InlineData("""{"text":"fixture","usage":{"type":"tokens","input_tokens":5,"output_tokens":3,"total_tokens":8}}""", UsageKind.Tokens)]
    [InlineData("""{"text":"fixture","usage":{"type":"duration","seconds":0.5}}""", UsageKind.Duration)]
    [InlineData("""{"text":"fixture","usage":{"type":"new-billing","amount":999}}""", UsageKind.Unknown)]
    [InlineData("""{"text":"fixture","usage":{}}""", UsageKind.Unknown)]
    [InlineData("""{"text":"fixture","usage":null}""", UsageKind.Unknown)]
    public async Task Known_usage_does_not_manufacture_a_price_and_unknown_usage_is_not_zero(string json, UsageKind expected)
    {
        var result = await Respond(json);
        Assert.Equal(TranscriptionOutcome.Completed, result.Outcome);
        Assert.Equal(expected, result.Usage.Kind);
        Assert.Null(result.Usage.EstimatedCost);
        if (expected == UsageKind.Tokens)
            Assert.Equal(new(UsageKind.Tokens, 5, 3, 8), result.Usage);
        else if (expected == UsageKind.Duration)
            Assert.Equal(0.5, result.Usage.Seconds);
        else
        {
            Assert.Null(result.Usage.TotalTokens);
            Assert.Null(result.Usage.Seconds);
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("null")]
    [InlineData("""{"text":null}""")]
    [InlineData("""{"text":123}""")]
    [InlineData("""{"text":"one","text":"two"}""")]
    [InlineData("""{"text":"one"} trailing""")]
    [InlineData("""{"text":"\u0000"}""")]
    [InlineData("""{"text":"\uD800"}""")]
    [InlineData("""{"text":"ok","ignored":"\uD800"}""")]
    [InlineData("""{"text":"ok","error":{"message":"do not treat as success"}}""")]
    [InlineData("""{"text":"ok","languages":"en"}""")]
    [InlineData("""{"text":"ok","languages":[{"code":"\r\n"}]}""")]
    [InlineData("""{"text":"ok","languages":[{"code":"en"},{"code":"en"}]}""")]
    [InlineData("""{"text":"ok","languages":[{"name":"english"}]}""")]
    [InlineData("""{"text":"ok","usage":123}""")]
    [InlineData("""{"text":"ok","usage":{"type":"tokens","input_tokens":-1,"output_tokens":3,"total_tokens":2}}""")]
    [InlineData("""{"text":"ok","usage":{"type":"tokens","input_tokens":1,"output_tokens":3,"total_tokens":5}}""")]
    [InlineData("""{"text":"ok","usage":{"type":"tokens","input_tokens":1.1,"output_tokens":3,"total_tokens":4.1}}""")]
    [InlineData("""{"text":"ok","usage":{"type":"duration","seconds":-1}}""")]
    [InlineData("""{"text":"ok","usage":{"type":"duration","seconds":1e999}}""")]
    public async Task Unsupported_malformed_or_ambiguous_schema_is_failed_not_no_speech(string json)
    {
        var result = await Respond(json);
        Assert.Equal(TranscriptionOutcome.Failed, result.Outcome);
        Assert.Equal(ProviderFailureCode.ResponseSchema, result.Failure!.Code);
        Assert.Null(result.Text);
    }

    [Theory]
    [InlineData("utf8")]
    [InlineData("depth")]
    [InlineData("text-limit")]
    [InlineData("sse")]
    [InlineData("content-encoding")]
    public async Task Wire_and_configured_response_contracts_are_enforced(string fault)
    {
        var handler = new RecordingHandler
        {
            Respond = (_, _) =>
            {
                var response = ProviderFixtures.Json(fault switch
                {
                    "depth" => """{"text":"ok","ignored":""" + new string('[', 20) + "0" + new string(']', 20) + "}",
                    "text-limit" => """{"text":"four"}""",
                    _ => """{"text":"ok"}"""
                });
                if (fault == "utf8")
                    response.Content = new ByteArrayContent([123, 34, 116, 101, 120, 116, 34, 58, 34, 0xFF, 34, 125]);
                response.Content.Headers.ContentType = new(fault == "sse" ? "text/event-stream" : "application/json");
                if (fault == "content-encoding")
                    response.Content.Headers.ContentEncoding.Add("gzip");
                return Task.FromResult(response);
            }
        };
        var result = await TranscriptionTransportTests.Run(handler, new() { MaxTextCharacters = 3 });
        Assert.Equal(ProviderFailureCode.ResponseSchema, result.Failure!.Code);
        Assert.Equal(1, handler.Calls);
    }

    [Theory]
    [InlineData("declared-large", ProviderFailureCode.ResponseTooLarge, 0)]
    [InlineData("chunked-large", ProviderFailureCode.ResponseTooLarge, 65)]
    [InlineData("truncated", ProviderFailureCode.ResponseTruncated, 13)]
    [InlineData("extra-bytes", ProviderFailureCode.ResponseTruncated, 13)]
    public async Task Response_read_stops_at_limit_plus_one_and_checks_declared_length(string fault, ProviderFailureCode expected, int bytesRead)
    {
        var body = new NonSeekableBody(Encoding.UTF8.GetBytes(fault.Contains("large", StringComparison.Ordinal) ?
            new string('x', 1000) : """{"text":"ok"}"""));
        var handler = new RecordingHandler
        {
            Respond = (_, _) =>
            {
                var response = ProviderFixtures.Json();
                response.Content = new StreamContent(body);
                response.Content.Headers.ContentType = new("application/json");
                response.Content.Headers.ContentLength = fault switch
                {
                    "declared-large" => 1000,
                    "truncated" => 64,
                    "extra-bytes" => 1,
                    _ => null
                };
                return Task.FromResult(response);
            }
        };
        var result = await TranscriptionTransportTests.Run(handler, new() { MaxResponseBytes = 64 });
        Assert.Equal(expected, result.Failure!.Code);
        Assert.Equal(bytesRead, body.BytesRead);
        Assert.True(body.Disposed);
        Assert.Equal(1, handler.Calls);
    }

    [Theory]
    [InlineData(401, "invalid_api_key", ProviderFailureCode.Authentication)]
    [InlineData(403, "unknown", ProviderFailureCode.PermissionDenied)]
    [InlineData(429, "insufficient_quota", ProviderFailureCode.QuotaExceeded)]
    [InlineData(429, "credit_balance_exhausted", ProviderFailureCode.QuotaExceeded)]
    [InlineData(429, "organization_spend_limit_exceeded", ProviderFailureCode.QuotaExceeded)]
    [InlineData(429, "project_spend_limit_exceeded", ProviderFailureCode.QuotaExceeded)]
    [InlineData(429, "organization_usage_limit_exceeded", ProviderFailureCode.QuotaExceeded)]
    [InlineData(429, "rate_limit_exceeded", ProviderFailureCode.RateLimited)]
    [InlineData(429, "slow_down", ProviderFailureCode.RateLimited)]
    [InlineData(404, "model_not_found", ProviderFailureCode.ModelNotFound)]
    [InlineData(404, "unknown", ProviderFailureCode.RequestRejected)]
    [InlineData(400, "model_not_found", ProviderFailureCode.ModelNotFound)]
    [InlineData(400, "invalid_audio_format", ProviderFailureCode.FormatRejected)]
    [InlineData(400, "unsupported_format", ProviderFailureCode.FormatRejected)]
    [InlineData(400, "invalid_file_format", ProviderFailureCode.FormatRejected)]
    [InlineData(415, "unknown", ProviderFailureCode.FormatRejected)]
    [InlineData(400, "unknown", ProviderFailureCode.RequestRejected)]
    [InlineData(408, "unknown", ProviderFailureCode.RequestRejected)]
    [InlineData(500, "unknown", ProviderFailureCode.Server)]
    [InlineData(503, "server_is_overloaded", ProviderFailureCode.Server)]
    [InlineData(204, "unknown", ProviderFailureCode.RequestRejected)]
    public async Task Error_status_codes_are_normalized_without_echoes_retry_or_fallback(int status, string code, ProviderFailureCode expected)
    {
        string json = JsonSerializer.Serialize(new
        {
            error = new { code, message = ProviderFixtures.ContentCanary, param = ProviderFixtures.Secret, type = ProviderFixtures.ContentCanary },
            text = ProviderFixtures.ContentCanary
        });
        var handler = new RecordingHandler { Respond = (_, _) => Task.FromResult(ProviderFixtures.Json(json, status)) };
        var result = await TranscriptionTransportTests.Run(handler);
        Assert.Equal(TranscriptionOutcome.Failed, result.Outcome);
        Assert.Equal(expected, result.Failure!.Code);
        result.Failure.Error.Validate();
        Assert.Equal(Stage.Transcription, result.Failure.Error.Stage);
        Assert.False(result.Failure.Error.Retryable);
        Assert.Null(result.Text);
        Assert.Equal(UsageKind.Unknown, result.Usage.Kind);
        Assert.Equal(1, handler.Calls);
        string metadata = JsonSerializer.Serialize(result) + result + result.Failure;
        Assert.DoesNotContain(ProviderFixtures.Secret, metadata);
        Assert.DoesNotContain(ProviderFixtures.ContentCanary, metadata);
    }

    [Theory]
    [InlineData("invalid", 401, ProviderFailureCode.Authentication)]
    [InlineData("oversize", 401, ProviderFailureCode.Authentication)]
    [InlineData("truncated", 429, ProviderFailureCode.RateLimited)]
    public async Task Known_http_error_status_survives_unusable_error_body(string fault, int status, ProviderFailureCode code)
    {
        var handler = new RecordingHandler
        {
            Respond = (_, _) =>
            {
                var response = ProviderFixtures.Json(fault == "oversize" ? new string('x', 100) : "invalid", status);
                if (fault == "truncated")
                    response.Content.Headers.ContentLength = 32;
                return Task.FromResult(response);
            }
        };
        var result = await TranscriptionTransportTests.Run(handler, new() { MaxResponseBytes = 64 });
        Assert.Equal(code, result.Failure!.Code);
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(10000, 300)]
    [InlineData(-30, 0)]
    public async Task Retry_after_is_bounded_advice_only(int seconds, int expected)
    {
        var handler = new RecordingHandler
        {
            Respond = (_, _) =>
            {
                var response = ProviderFixtures.Json("{}", 429);
                response.Headers.RetryAfter = new RetryConditionHeaderValue(ProviderFixtures.Now.AddSeconds(seconds));
                return Task.FromResult(response);
            }
        };
        var result = await TranscriptionTransportTests.Run(handler);
        Assert.Equal(TimeSpan.FromSeconds(expected), result.Failure!.RetryAfter);
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task Successful_content_is_only_in_explicit_result_not_diagnostic_serialization_or_ToString()
    {
        var result = await Respond(JsonSerializer.Serialize(new
        {
            text = ProviderFixtures.ContentCanary, ignored = ProviderFixtures.Secret,
            logprobs = new[] { new { token = ProviderFixtures.ContentCanary, logprob = 0.0 } }
        }));
        Assert.Equal(ProviderFixtures.ContentCanary, result.Text);
        Assert.Null(result.Confidence);
        string metadata = JsonSerializer.Serialize(result) + result;
        Assert.DoesNotContain(ProviderFixtures.ContentCanary, metadata);
        Assert.DoesNotContain(ProviderFixtures.Secret, metadata);
    }

    private static Task<TranscriptionResult> Respond(string json) =>
        TranscriptionTransportTests.Run(new RecordingHandler { Respond = (_, _) => Task.FromResult(ProviderFixtures.Json(json)) });
}
