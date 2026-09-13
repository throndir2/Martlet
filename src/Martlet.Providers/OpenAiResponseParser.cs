using System.Net;
using System.Text.Json;
using Martlet.Core.Contracts;

namespace Martlet.Providers;

internal static class OpenAiResponseParser
{
    public static TranscriptionResult Parse(ReadOnlyMemory<byte> bytes, ProviderRequestContext context,
        EvidenceProvenance provenance, TranscriptionLimits limits)
    {
        var response = ContractJson.Read<TranscriptionEnvelope>(bytes, limits.MaxResponseBytes);
        ContractRules.Text(response.Text, limits.MaxTextCharacters);
        return new(context, provenance,
            string.IsNullOrWhiteSpace(response.Text) ? TranscriptionOutcome.NoSpeech : TranscriptionOutcome.Completed,
            string.IsNullOrWhiteSpace(response.Text) ? null : response.Text,
            ParseLanguages(response.Languages), ParseUsage(response.Usage));
    }

    public static ProviderFailureCode Classify(HttpStatusCode status, ReadOnlyMemory<byte> bytes)
    {
        string? code = null;
        try
        {
            var envelope = ContractJson.Read<ErrorEnvelope>(bytes);
            if (envelope.Error is { ValueKind: JsonValueKind.Object } error)
            {
                // Only exact, recognized codes influence classification. Never retain message/param/type.
                if (error.TryGetProperty("code", out var value) && value.ValueKind == JsonValueKind.String)
                    code = value.GetString();
            }
        }
        catch (ContractException)
        {
            // HTTP status is still meaningful when the optional error body is missing or malformed.
        }

        return status switch
        {
            HttpStatusCode.Unauthorized => ProviderFailureCode.Authentication,
            HttpStatusCode.Forbidden => ProviderFailureCode.PermissionDenied,
            HttpStatusCode.TooManyRequests when code is "insufficient_quota" or "credit_balance_exhausted" or
                "organization_spend_limit_exceeded" or "project_spend_limit_exceeded" or "organization_usage_limit_exceeded" =>
                ProviderFailureCode.QuotaExceeded,
            HttpStatusCode.TooManyRequests => ProviderFailureCode.RateLimited,
            HttpStatusCode.NotFound when code == "model_not_found" => ProviderFailureCode.ModelNotFound,
            HttpStatusCode.BadRequest when code == "model_not_found" => ProviderFailureCode.ModelNotFound,
            HttpStatusCode.UnsupportedMediaType => ProviderFailureCode.FormatRejected,
            HttpStatusCode.BadRequest when code is "invalid_audio_format" or "unsupported_format" or "invalid_file_format" =>
                ProviderFailureCode.FormatRejected,
            _ when (int)status >= 500 => ProviderFailureCode.Server,
            _ => ProviderFailureCode.RequestRejected
        };
    }

    private static IReadOnlyList<string> ParseLanguages(JsonElement? languages)
    {
        if (languages is null || languages.Value.ValueKind == JsonValueKind.Null)
            return Array.Empty<string>();
        var value = languages.Value;
        Require(value.ValueKind == JsonValueKind.Array && value.GetArrayLength() <= 16);
        var result = new List<string>();
        foreach (var language in value.EnumerateArray())
        {
            Require(language.ValueKind == JsonValueKind.Object &&
                language.TryGetProperty("code", out var code) && code.ValueKind == JsonValueKind.String);
            string text = language.GetProperty("code").GetString()!;
            Require(text.Length is >= 2 and <= 35 && char.IsAsciiLetter(text[0]) &&
                text.All(c => char.IsAsciiLetterOrDigit(c) || c == '-') &&
                !result.Contains(text, StringComparer.Ordinal));
            result.Add(text);
        }
        return result.AsReadOnly();
    }

    private static TranscriptionUsage ParseUsage(JsonElement? usage)
    {
        if (usage is null || usage.Value.ValueKind == JsonValueKind.Null)
            return TranscriptionUsage.Unknown;
        var value = usage.Value;
        Require(value.ValueKind == JsonValueKind.Object);
        if (!value.TryGetProperty("type", out var type) || type.ValueKind != JsonValueKind.String)
            return TranscriptionUsage.Unknown;
        switch (type.GetString())
        {
            case "tokens":
                long input = TokenCount(value, "input_tokens");
                long output = TokenCount(value, "output_tokens");
                long total = TokenCount(value, "total_tokens");
                Require(total == input + output);
                return new(UsageKind.Tokens, input, output, total);
            case "duration":
                Require(value.TryGetProperty("seconds", out var seconds) &&
                    seconds.ValueKind == JsonValueKind.Number && seconds.TryGetDouble(out double count) &&
                    double.IsFinite(count) && count is >= 0 and <= 3600);
                return new(UsageKind.Duration, Seconds: value.GetProperty("seconds").GetDouble());
            default:
                return TranscriptionUsage.Unknown;
        }
    }

    private static long TokenCount(JsonElement value, string name)
    {
        Require(value.TryGetProperty(name, out var count) && count.ValueKind == JsonValueKind.Number &&
            count.TryGetInt64(out long number) && number is >= 0 and <= 1_000_000_000);
        return value.GetProperty(name).GetInt64();
    }

    private static void Require(bool condition) => ContractRules.Require(condition, "Unsupported transcription response schema.");

    internal sealed class TranscriptionEnvelope : IContract
    {
        public required string Text { get; init; }
        public JsonElement? Languages { get; init; }
        public JsonElement? Usage { get; init; }
        public JsonElement? Error { get; init; }
        public void Validate()
        {
            ContractRules.Text(Text, ContractRules.MaxTextCharacters);
            Require(Error is null);
        }
    }

    internal sealed class ErrorEnvelope : IContract
    {
        public JsonElement? Error { get; init; }
        public void Validate() { }
    }
}
