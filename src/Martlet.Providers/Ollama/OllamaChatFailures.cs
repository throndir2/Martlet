using System.Net;
using Martlet.Core.Contracts;

namespace Martlet.Providers.Ollama;

internal static class OllamaChatFailures
{
    internal static OllamaChatStep Fail(ProviderFailureCode code, TimeSpan? retry = null) => new(
        Outcome: code switch
        {
            ProviderFailureCode.DeadlineExceeded or ProviderFailureCode.FirstDeltaTimeout or
                ProviderFailureCode.IdleTimeout => TextGenerationOutcome.DeadlineExceeded,
            ProviderFailureCode.OutputTokenLimit => TextGenerationOutcome.OutputTokenLimit,
            ProviderFailureCode.Incomplete => TextGenerationOutcome.Incomplete,
            _ => TextGenerationOutcome.Failed
        }, Failure: code, RetryAfter: retry);

    internal static ProviderFailureCode Status(HttpStatusCode status) => (int)status switch
    {
        >= 300 and <= 399 => ProviderFailureCode.RedirectRejected,
        401 => ProviderFailureCode.Authentication,
        403 => ProviderFailureCode.PermissionDenied,
        429 => ProviderFailureCode.RateLimited,
        >= 500 and <= 599 => ProviderFailureCode.Server,
        _ => ProviderFailureCode.RequestRejected
    };

    internal static MartletError Error(ProviderFailureCode code)
    {
        var (core, message, action) = code switch
        {
            ProviderFailureCode.ConsentMissing or ProviderFailureCode.ConsentMismatch or
                ProviderFailureCode.ConsentExpired or ProviderFailureCode.ConsentConsumed =>
                (ErrorCode.NotConfigured, "Fresh exact-action authorization from the trusted Ollama caller is required.", "provider.review-consent"),
            ProviderFailureCode.InputLimit =>
                (ErrorCode.PayloadTooLarge, "The input or reported prompt count exceeds the authorized bound.", "provider.reduce-text"),
            ProviderFailureCode.ResponseTooLarge =>
                (ErrorCode.PayloadTooLarge, "The Ollama stream exceeded its byte, record or text bound.", "provider.review-response"),
            ProviderFailureCode.ResponseTruncated =>
                (ErrorCode.StreamTruncated, "The Ollama body ended without its required record, terminal or HTTP boundary.", "provider.review-response"),
            ProviderFailureCode.ResponseSchema =>
                (ErrorCode.InvalidContract, "The Ollama response does not match the supported native chat schema.", "provider.review-response"),
            ProviderFailureCode.UnsupportedOutput =>
                (ErrorCode.ProviderCapability, "The Ollama response contains unsupported semantic output. Text may be partial.", "provider.review-response"),
            ProviderFailureCode.OutputTokenLimit =>
                (ErrorCode.ProviderFailed, "Generation reached or exceeded its requested output bound. Text is incomplete.", "provider.review-request"),
            ProviderFailureCode.Incomplete =>
                (ErrorCode.ProviderFailed, "Ollama did not report a supported successful stop. Text is incomplete.", "provider.review-response"),
            ProviderFailureCode.DeadlineExceeded or ProviderFailureCode.FirstDeltaTimeout or ProviderFailureCode.IdleTimeout =>
                (ErrorCode.DeadlineExceeded, "The original Ollama request or progress deadline expired. Backend work may continue.", "provider.review-request"),
            ProviderFailureCode.RedirectRejected =>
                (ErrorCode.ProviderFailed, "The Ollama endpoint returned a redirect. It was not followed.", "provider.review-origin"),
            ProviderFailureCode.Authentication or ProviderFailureCode.PermissionDenied =>
                (ErrorCode.ProviderFailed, "The selected endpoint denied this request. This adapter supplies no credentials.", "provider.review-origin"),
            ProviderFailureCode.RateLimited =>
                (ErrorCode.ProviderFailed, "The selected endpoint rate-limited the request. No retry was made.", "provider.review-rate"),
            _ => (ErrorCode.ProviderFailed, "The selected Ollama request failed. Review the runtime and model; no fallback or model-management call was made.", "provider.review-request")
        };
        return Create(core, message, action);
    }

    internal static MartletError CleanupError() => Create(ErrorCode.ProviderFailed,
        "Ollama cleanup failed. Retain the quarantined owner; do not start replacement work.", "provider.review-request");

    private static MartletError Create(ErrorCode code, string summary, string action)
    {
        var error = new MartletError { Code = code, Stage = Stage.Generation, Summary = summary,
            ActionId = action, Retryable = false };
        error.Validate();
        return error;
    }
}
