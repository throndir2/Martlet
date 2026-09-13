using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Martlet.Core.Contracts;

namespace Martlet.Providers.Tests;

public sealed class SpeechProtocolTests
{
    public static IEnumerable<object[]> FragmentCases =>
        from fragment in new[] { 1, 2, 3, 7, 479, 480, 481, 959, 960, 961, 4096 }
        from samples in new[] { 1, 479, 480, 481, 2003 }
        select new object[] { fragment, samples };

    [Theory]
    [MemberData(nameof(FragmentCases))]
    public async Task Arbitrary_network_fragments_become_exact_contiguous_core_frames(int fragment, int samples)
    {
        var bytes = SpeechFixtures.Audio(samples);
        var body = new FragmentedTextBody(bytes, fragment);
        var handler = SpeechFixtures.Handler();
        handler.Respond = (_, _) => Task.FromResult(SpeechFixtures.Pcm(body));
        var result = await SpeechFixtures.Run(handler);
        Assert.Equal(SpeechSynthesisOutcome.Completed, result.Result.Outcome);
        Assert.Equal(SpeechProviderTerminal.HttpBodyCompleted, result.Result.ProviderTerminal);
        Assert.Equal(samples, result.Result.FinalSampleCount);
        Assert.Equal(samples, result.Result.ToTerminalEvent().FinalSampleCount);
        Assert.Equal(200, result.Result.HttpStatusCode);
        Assert.False(result.Result.IsPartial);
        Assert.Equal(bytes, result.Frames.SelectMany(f => f.Data.ToArray()).ToArray());
        Assert.Equal((samples + 479) / 480, result.Frames.Count);
        Assert.All(result.Frames.SkipLast(1), f => Assert.Equal(960, f.Data.Length));
        Assert.Equal(samples % 480 == 0 ? 480 : samples % 480, result.Frames[^1].SamplesPerChannel);
        Assert.Equal(bytes.Length, body.BytesRead);
        Assert.True(body.Disposed);
        Assert.Equal(1, handler.Calls);
    }

    [Theory]
    [InlineData("application/octet-stream")]
    [InlineData("audio/pcm")]
    [InlineData("Audio/PCM")]
    public async Task Declared_raw_media_is_parsed_case_insensitively_and_exact_length_completes(string media)
    {
        var bytes = SpeechFixtures.Audio(480);
        var body = new FragmentedTextBody(bytes, 1);
        var handler = SpeechFixtures.Handler();
        handler.Respond = (_, _) => Task.FromResult(SpeechFixtures.Pcm(body, length: bytes.Length, media: media));
        var result = await SpeechFixtures.Run(handler);
        Assert.Equal(SpeechSynthesisOutcome.Completed, result.Result.Outcome);
        Assert.Equal(bytes, Assert.Single(result.Frames).Data.ToArray());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("application/json")]
    [InlineData("application/problem+json")]
    [InlineData("audio/wav")]
    [InlineData("audio/mpeg")]
    [InlineData("audio/aac")]
    [InlineData("audio/opus")]
    [InlineData("audio/flac")]
    [InlineData("audio/L16")]
    [InlineData("text/event-stream")]
    [InlineData("audio/pcm; rate=16000")]
    [InlineData("audio/pcm; channels=2")]
    [InlineData("application/octet-stream; charset=utf-8")]
    [InlineData("audio/pcm; malformed")]
    [InlineData("not a media type")]
    [InlineData("application/octet-stream, audio/pcm")]
    public async Task Unsupported_ambiguous_media_never_becomes_pcm(string? media)
    {
        var body = new FragmentedTextBody(SpeechFixtures.Audio(), 1);
        var handler = SpeechFixtures.Handler();
        handler.Respond = (_, _) => Task.FromResult(SpeechFixtures.Pcm(body, media: media));
        var result = await SpeechFixtures.Run(handler);
        Assert.Equal(ProviderFailureCode.SpeechMediaUnsupported, result.Result.Failure!.Code);
        Assert.Equal(0, body.BytesRead);
        Assert.Empty(result.Frames);
        Assert.True(body.Disposed);
    }

    [Theory]
    [InlineData("gzip")]
    [InlineData("br")]
    [InlineData("deflate")]
    [InlineData("identity")]
    [InlineData("")]
    public async Task Any_content_encoding_is_rejected_without_decoding(string encoding)
    {
        var body = new FragmentedTextBody(SpeechFixtures.Audio());
        var handler = SpeechFixtures.Handler();
        handler.Respond = (_, _) =>
        {
            var response = SpeechFixtures.Pcm(body);
            response.Content.Headers.TryAddWithoutValidation("Content-Encoding", encoding);
            return Task.FromResult(response);
        };
        var result = await SpeechFixtures.Run(handler);
        Assert.Equal(ProviderFailureCode.SpeechMediaUnsupported, result.Result.Failure!.Code);
        Assert.Empty(result.Frames);
        Assert.Equal(0, body.BytesRead);
    }

    public static TheoryData<byte[]> NonPcmPrefixes => new()
    {
        ProviderFixtures.Wave(),
        "RIFX0000WAVEfmt "u8.ToArray(), "RF640000WAVEfmt "u8.ToArray(),
        "OggSfixture"u8.ToArray(), "fLaCfixture"u8.ToArray(), "ID3fixture"u8.ToArray(),
        new byte[] { 0x1f, 0x8b, 0, 0 }, new byte[] { 0xff, 0xfb, 0, 0 }, new byte[] { 0xff, 0xf1, 0, 0 },
        "PK\u0003\u0004fixture"u8.ToArray(),
        """{"error":{"code":"insufficient_quota","message":"private-response"}}"""u8.ToArray(),
        " \t\r\n{\"error\":null} "u8.ToArray(), "\uFEFF {\"error\":null}"u8.ToArray(),
        "[\"private\"]"u8.ToArray(), "<html>private</html>"u8.ToArray()
    };

    [Theory]
    [MemberData(nameof(NonPcmPrefixes))]
    public async Task Recognizable_wrong_body_with_binary_200_is_rejected_before_audio(byte[] prefix)
    {
        byte[] bytes = prefix.Concat(new byte[960]).ToArray();
        if (bytes.Length % 2 != 0) bytes = bytes.Append((byte)0).ToArray();
        var handler = SpeechFixtures.Handler(bytes, fragment: 1);
        var result = await SpeechFixtures.Run(handler);
        Assert.Equal(ProviderFailureCode.SpeechMediaUnsupported, result.Result.Failure!.Code);
        Assert.Equal(200, result.Result.HttpStatusCode);
        Assert.Empty(result.Frames);
        Assert.DoesNotContain("private-response", JsonSerializer.Serialize(result.Result));
    }

    [Theory]
    [InlineData(0, null, ProviderFailureCode.EmptyAudio, 0)]
    [InlineData(0, 0L, ProviderFailureCode.EmptyAudio, 0)]
    [InlineData(0, 2L, ProviderFailureCode.ResponseTruncated, 0)]
    [InlineData(1, null, ProviderFailureCode.ResponseTruncated, 0)]
    [InlineData(961, null, ProviderFailureCode.ResponseTruncated, 480)]
    [InlineData(1921, 1921L, ProviderFailureCode.ResponseTruncated, 960)]
    [InlineData(960, 962L, ProviderFailureCode.ResponseTruncated, 480)]
    [InlineData(962, 960L, ProviderFailureCode.ResponseTruncated, 480)]
    [InlineData(2, 0L, ProviderFailureCode.ResponseTruncated, 0)]
    [InlineData(958, 960L, ProviderFailureCode.ResponseTruncated, 0)]
    public async Task Empty_odd_or_length_mismatched_body_never_claims_completion(
        int byteCount, long? declared, ProviderFailureCode code, int partialSamples)
    {
        var bytes = SpeechFixtures.Audio((byteCount + 1) / 2).Take(byteCount).ToArray();
        var body = new FragmentedTextBody(bytes, 7);
        var handler = SpeechFixtures.Handler();
        handler.Respond = (_, _) => Task.FromResult(SpeechFixtures.Pcm(body, length: declared));
        var result = await SpeechFixtures.Run(handler);
        Assert.Equal(code, result.Result.Failure!.Code);
        Assert.Equal(partialSamples, result.Result.DeliveredSampleCount);
        Assert.Equal(partialSamples > 0, result.Result.IsPartial);
        Assert.Null(result.Result.FinalSampleCount);
        Assert.Null(result.Result.ProviderTerminal);
        Assert.True(body.Disposed);
        Assert.Equal(1, handler.Calls);
    }

    [Theory]
    [InlineData("bytes", false)]
    [InlineData("duration", false)]
    [InlineData("bytes", true)]
    [InlineData("duration", true)]
    public async Task Byte_and_sample_duration_limits_read_at_most_one_extra_byte(string limit, bool declared)
    {
        var body = new FragmentedTextBody(SpeechFixtures.Audio(1001), 4096);
        var handler = SpeechFixtures.Handler();
        handler.Respond = (_, _) => Task.FromResult(SpeechFixtures.Pcm(body, length: declared ? 2002 : null));
        var limits = limit == "bytes" ? new SpeechSynthesisLimits { MaxAudioBytes = 1920 }
            : new SpeechSynthesisLimits { MaxAudioDuration = TimeSpan.FromMilliseconds(40) };
        var result = await SpeechFixtures.Run(handler, limits);
        Assert.Equal(ProviderFailureCode.OutputAudioLimit, result.Result.Failure!.Code);
        Assert.Equal(declared ? 0 : 1921, body.BytesRead);
        Assert.Equal(declared ? 0 : 960, result.Result.DeliveredSampleCount);
        Assert.Null(result.Result.FinalSampleCount);
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task Duration_limit_uses_integer_per_channel_samples_not_rounded_seconds_or_bytes()
    {
        var limits = new SpeechSynthesisLimits { MaxAudioDuration = TimeSpan.FromTicks(417) };
        Assert.Equal(1, limits.MaxSamples);
        var exact = await SpeechFixtures.Run(SpeechFixtures.Handler(SpeechFixtures.Audio(1)), limits);
        Assert.Equal(SpeechSynthesisOutcome.Completed, exact.Result.Outcome);
        Assert.Equal(1, exact.Result.FinalSampleCount);
        var overflow = await SpeechFixtures.Run(SpeechFixtures.Handler(SpeechFixtures.Audio(2)), limits);
        Assert.Equal(ProviderFailureCode.OutputAudioLimit, overflow.Result.Failure!.Code);
    }

    [Fact]
    public async Task Clean_aligned_eof_without_length_is_honestly_the_only_raw_terminal()
    {
        // There is no semantic upstream marker/known expected duration to detect this shortened, aligned body.
        var result = await SpeechFixtures.Run(SpeechFixtures.Handler(SpeechFixtures.Audio(17)));
        Assert.Equal(SpeechSynthesisOutcome.Completed, result.Result.Outcome);
        Assert.Equal(17, result.Result.FinalSampleCount);
        Assert.Equal(SpeechProviderTerminal.HttpBodyCompleted, result.Result.ProviderTerminal);
    }

    [Theory]
    [InlineData("negative")]
    [InlineData("nonnumeric")]
    [InlineData("duplicate")]
    [InlineData("overflow")]
    public async Task Malformed_declared_lengths_are_not_ignored(string cause)
    {
        var body = new FragmentedTextBody(SpeechFixtures.Audio());
        var handler = SpeechFixtures.Handler();
        handler.Respond = (_, _) =>
        {
            var response = SpeechFixtures.Pcm(body);
            var values = cause switch
            {
                "negative" => new[] { "-1" }, "nonnumeric" => ["secret"],
                "duplicate" => ["2", "4"], _ => ["999999999999999999999999"]
            };
            response.Content.Headers.TryAddWithoutValidation("Content-Length", values);
            return Task.FromResult(response);
        };
        var result = await SpeechFixtures.Run(handler);
        Assert.Equal(ProviderFailureCode.ResponseSchema, result.Result.Failure!.Code);
        Assert.Empty(result.Frames);
    }

    [Theory]
    [InlineData(401, "io", ProviderFailureCode.Authentication)]
    [InlineData(403, "io", ProviderFailureCode.PermissionDenied)]
    [InlineData(429, "io", ProviderFailureCode.RateLimited)]
    [InlineData(401, "http-io", ProviderFailureCode.Authentication)]
    [InlineData(403, "http-io", ProviderFailureCode.PermissionDenied)]
    [InlineData(429, "http-io", ProviderFailureCode.RateLimited)]
    [InlineData(401, "http", ProviderFailureCode.Authentication)]
    [InlineData(429, "http", ProviderFailureCode.RateLimited)]
    [InlineData(200, "io", ProviderFailureCode.Network)]
    [InlineData(200, "http-io", ProviderFailureCode.ResponseTruncated)]
    [InlineData(200, "http", ProviderFailureCode.ResponseTruncated)]
    public async Task Optional_error_transport_preserves_status_and_advice_but_200_requires_body(
        int status, string cause, ProviderFailureCode code)
    {
        var body = new FragmentedTextBody([])
        {
            OnRead = () => throw cause switch
            {
                "http-io" => new HttpIOException(HttpRequestError.ResponseEnded, ProviderFixtures.Secret),
                "http" => new HttpRequestException(HttpRequestError.ResponseEnded, ProviderFixtures.Secret),
                _ => new IOException(ProviderFixtures.ContentCanary)
            }
        };
        var handler = SpeechFixtures.Handler();
        handler.Respond = (_, _) =>
        {
            var response = SpeechFixtures.Pcm(body, status);
            response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(400));
            return Task.FromResult(response);
        };
        var result = await SpeechFixtures.Run(handler);
        Assert.Equal(code, result.Result.Failure!.Code);
        Assert.Equal(status, result.Result.HttpStatusCode);
        Assert.Equal(status == 200 ? null : TimeSpan.FromSeconds(300), result.Result.Failure.RetryAfter);
        Assert.Equal(status is 401 or 403 ? "provider.review-credential" :
            status == 429 ? "provider.review-rate" : status == 200 && cause != "io" ? "provider.review-response" : "provider.review-request",
            result.Result.Failure.Error.ActionId);
        Assert.True(body.Disposed);
        Assert.Equal(1, handler.Calls);
        Assert.DoesNotContain(ProviderFixtures.Secret, JsonSerializer.Serialize(result.Result));
        Assert.DoesNotContain(ProviderFixtures.ContentCanary, JsonSerializer.Serialize(result.Result));
    }

    [Theory]
    [InlineData(401, "{}", ProviderFailureCode.Authentication)]
    [InlineData(403, "{}", ProviderFailureCode.PermissionDenied)]
    [InlineData(429, """{"error":{"code":"insufficient_quota","message":"private-echo"}}""", ProviderFailureCode.QuotaExceeded)]
    [InlineData(429, """{"error":{"code":"unknown"}}""", ProviderFailureCode.RateLimited)]
    [InlineData(429, """{"error":{"code":"insufficient_quota","code":"unknown"}}""", ProviderFailureCode.RateLimited)]
    [InlineData(404, """{"error":{"code":"model_not_found"}}""", ProviderFailureCode.ModelNotFound)]
    [InlineData(400, """{"error":{"code":"unsupported_format"}}""", ProviderFailureCode.FormatRejected)]
    [InlineData(400, "{}", ProviderFailureCode.RequestRejected)]
    [InlineData(415, "{}", ProviderFailureCode.FormatRejected)]
    [InlineData(500, "{}", ProviderFailureCode.Server)]
    [InlineData(302, "{}", ProviderFailureCode.RedirectRejected)]
    [InlineData(307, "{}", ProviderFailureCode.RedirectRejected)]
    [InlineData(308, "{}", ProviderFailureCode.RedirectRejected)]
    public async Task Status_classification_is_reused_with_synthesis_stage_and_no_retry(
        int status, string payload, ProviderFailureCode code)
    {
        var handler = SpeechFixtures.Handler();
        handler.Respond = (_, _) =>
        {
            var response = ProviderFixtures.Json(payload, status);
            response.Headers.Location = new("https://example.com/never-follow");
            return Task.FromResult(response);
        };
        var result = await SpeechFixtures.Run(handler);
        Assert.Equal(code, result.Result.Failure!.Code);
        Assert.Equal(status, result.Result.HttpStatusCode);
        Assert.Equal(1, handler.Calls);
        Assert.Empty(result.Frames);
        Assert.DoesNotContain("private-echo", JsonSerializer.Serialize(result.Result));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Oversized_optional_error_body_is_bounded_and_does_not_erase_429(bool declared)
    {
        var body = new FragmentedTextBody(Encoding.UTF8.GetBytes(new string('x', 1000)), 1000);
        var handler = SpeechFixtures.Handler();
        handler.Respond = (_, _) => Task.FromResult(SpeechFixtures.Pcm(body, 429, declared ? 1000 : null));
        var result = await SpeechFixtures.Run(handler, new() { MaxErrorBytes = 128 });
        Assert.Equal(ProviderFailureCode.RateLimited, result.Result.Failure!.Code);
        Assert.Equal(declared ? 0 : 129, body.BytesRead);
    }

    [Fact]
    public async Task Http_io_premature_end_after_frames_is_partial_not_complete_or_replayed()
    {
        var body = new FragmentedTextBody(SpeechFixtures.Audio(960), 960);
        body.OnRead = () =>
        {
            if (body.BytesRead == 1920) throw new HttpIOException(HttpRequestError.ResponseEnded, "fixture");
        };
        var handler = SpeechFixtures.Handler();
        handler.Respond = (_, _) => Task.FromResult(SpeechFixtures.Pcm(body));
        var result = await SpeechFixtures.Run(handler);
        Assert.Equal(960, result.Result.DeliveredSampleCount);
        Assert.True(result.Result.IsPartial);
        Assert.Equal(ProviderFailureCode.ResponseTruncated, result.Result.Failure!.Code);
        Assert.Null(result.Result.ProviderTerminal);
        Assert.Equal(1, handler.Calls);
        Assert.True(body.Disposed);
    }

    [Theory]
    [InlineData(-20, 0)]
    [InlineData(17, 17)]
    [InlineData(700, 300)]
    public async Task Retry_date_advice_is_bounded_without_retrying(int offset, int expected)
    {
        var handler = SpeechFixtures.Handler();
        handler.Respond = (_, _) =>
        {
            var response = ProviderFixtures.Json("{}", 429);
            response.Headers.RetryAfter = new RetryConditionHeaderValue(ProviderFixtures.Now.AddSeconds(offset));
            return Task.FromResult(response);
        };
        var result = await SpeechFixtures.Run(handler);
        Assert.Equal(TimeSpan.FromSeconds(expected), result.Result.Failure!.RetryAfter);
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task Exact_hard_sample_limit_completes_without_an_event_count_or_byte_sample_confusion()
    {
        var bytes = SpeechFixtures.Audio(2_160_000);
        var body = new FragmentedTextBody(bytes, 4096);
        var handler = SpeechFixtures.Handler();
        handler.Respond = (_, _) => Task.FromResult(SpeechFixtures.Pcm(body, length: bytes.Length));
        using var adapter = OpenAiSpeechSynthesisAdapter.CreateForFixture(handler, new FixtureCredentials(), new FixtureClock());
        var context = ProviderFixtures.Context();
        var input = new BoundedSpeechInput("Fixture.");
        var limits = new SpeechSynthesisLimits();
        var stream = adapter.Stream(context, SpeechFixtures.Selection, input, limits, SpeechFixtures.Authorize(context, input, limits));
        long samples = 0;
        int count = 0;
        await foreach (var frame in stream)
        {
            Assert.Equal(count++, frame.Sequence);
            Assert.Equal(samples, frame.SampleOffset);
            samples += frame.SamplesPerChannel;
            Assert.Equal(960, frame.Data.Length);
        }
        Assert.Equal(4500, count);
        Assert.Equal(2_160_000, samples);
        Assert.Equal(samples, stream.Result!.FinalSampleCount);
        Assert.Equal(4_320_000, body.BytesRead);
        stream.Result.ToTerminalEvent().Validate();
        Assert.True(body.Disposed);
    }

    [Fact]
    public async Task Successful_terminal_disposes_before_return_and_cannot_restart_reads()
    {
        var body = new FragmentedTextBody(SpeechFixtures.Audio(480), 960);
        int reads = 0;
        body.OnRead = () => reads++;
        var handler = SpeechFixtures.Handler();
        handler.Respond = (_, _) => Task.FromResult(SpeechFixtures.Pcm(body));
        using var adapter = OpenAiSpeechSynthesisAdapter.CreateForFixture(handler, new FixtureCredentials(), new FixtureClock());
        var context = ProviderFixtures.Context();
        var input = new BoundedSpeechInput("Fixture.");
        var limits = new SpeechSynthesisLimits();
        var stream = adapter.Stream(context, SpeechFixtures.Selection, input, limits, SpeechFixtures.Authorize(context, input, limits));
        await using var enumerator = stream.GetAsyncEnumerator();
        Assert.True(await enumerator.MoveNextAsync());
        Assert.Null(stream.Result);
        Assert.Equal(1, reads);
        Assert.False(await enumerator.MoveNextAsync());
        Assert.True(body.Disposed);
        Assert.Equal(2, reads);
        Assert.Equal(SpeechSynthesisOutcome.Completed, stream.Result!.Outcome);
        Assert.False(await enumerator.MoveNextAsync());
        Assert.Equal(2, reads);
    }
}
