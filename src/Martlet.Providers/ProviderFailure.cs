using Martlet.Core.Contracts;

namespace Martlet.Providers;

public enum ProviderFailureCode
{
    ConsentMissing, ConsentMismatch, ConsentExpired, ConsentConsumed, OriginRejected,
    ModelUnsupported, AudioLimit, CredentialUnavailable, CredentialBindingMismatch,
    Authentication, PermissionDenied, QuotaExceeded, RateLimited, ModelNotFound, FormatRejected,
    RequestRejected, RedirectRejected, Network, Server, ResponseTooLarge, ResponseTruncated,
    ResponseSchema, DeadlineExceeded, InputLimit, UnsupportedOutput, OutputTokenLimit,
    Incomplete, ContentFiltered, FirstDeltaTimeout, IdleTimeout
}

public sealed record ProviderFailure
{
    public ProviderFailureCode Code { get; }
    public MartletError Error { get; }
    public TimeSpan? RetryAfter { get; }

    internal ProviderFailure(ProviderFailureCode code, TimeSpan? retryAfter = null, Stage stage = Stage.Transcription)
    {
        Code = code;
        RetryAfter = retryAfter;
        var (coreCode, summary, action) = code switch
        {
            ProviderFailureCode.ConsentMissing or ProviderFailureCode.ConsentMismatch or
            ProviderFailureCode.ConsentExpired or ProviderFailureCode.ConsentConsumed =>
                (ErrorCode.NotConfigured, stage == Stage.Generation
                    ? "Explicit authorization for this text disclosure and potential charge is required."
                    : "Explicit authorization for this audio upload and potential charge is required.", "provider.review-consent"),
            ProviderFailureCode.OriginRejected or ProviderFailureCode.CredentialBindingMismatch =>
                (ErrorCode.NotConfigured, "The provider destination or credential binding is not approved.", "provider.review-origin"),
            ProviderFailureCode.ModelUnsupported =>
                (ErrorCode.ProviderCapability, stage == Stage.Generation
                    ? "The configured model is unsupported by this text adapter."
                    : "The configured model is unsupported by this transcription adapter.", "provider.review-model"),
            ProviderFailureCode.AudioLimit =>
                (ErrorCode.PayloadTooLarge, "The audio exceeds the authorized upload limits.", "provider.reduce-audio"),
            ProviderFailureCode.CredentialUnavailable or ProviderFailureCode.Authentication or ProviderFailureCode.PermissionDenied =>
                (ErrorCode.ProviderFailed, "The provider credential is unavailable or access was denied.", "provider.review-credential"),
            ProviderFailureCode.QuotaExceeded =>
                (ErrorCode.ProviderFailed, "The provider reported an account quota or billing limit.", "provider.review-billing"),
            ProviderFailureCode.RateLimited =>
                (ErrorCode.ProviderFailed, "The provider rate-limited the request. No retry was made.", "provider.review-rate"),
            ProviderFailureCode.ModelNotFound =>
                (ErrorCode.ProviderCapability, "The requested model is unavailable to this request.", "provider.review-model"),
            ProviderFailureCode.FormatRejected =>
                (ErrorCode.AudioFormatUnsupported, "The provider rejected the audio format.", "provider.review-audio"),
            ProviderFailureCode.RedirectRejected =>
                (ErrorCode.ProviderFailed, "The provider returned a redirect. It was not followed.", "provider.review-origin"),
            ProviderFailureCode.ResponseTooLarge =>
                (ErrorCode.PayloadTooLarge, "The provider response exceeded the configured limit.", "provider.review-response"),
            ProviderFailureCode.ResponseTruncated =>
                (ErrorCode.StreamTruncated, stage == Stage.Generation
                    ? "The provider response ended before its required boundary."
                    : "The provider response ended before its declared length.", "provider.review-response"),
            ProviderFailureCode.ResponseSchema =>
                (ErrorCode.InvalidContract, "The provider response did not match the supported schema.", "provider.review-response"),
            ProviderFailureCode.DeadlineExceeded =>
                (ErrorCode.DeadlineExceeded, stage == Stage.Generation
                    ? "The generation deadline expired. Upstream charges may still apply."
                    : "The transcription deadline expired. Upstream charges may still apply.", "provider.review-request"),
            ProviderFailureCode.InputLimit =>
                (ErrorCode.PayloadTooLarge, "The text input exceeds the authorized context limits.", "provider.reduce-text"),
            ProviderFailureCode.UnsupportedOutput =>
                (ErrorCode.ProviderCapability, "The response contains an unsupported output or event type.", "provider.review-response"),
            ProviderFailureCode.OutputTokenLimit =>
                (ErrorCode.ProviderFailed, "Generation stopped at the output token limit. Text is incomplete.", "provider.review-request"),
            ProviderFailureCode.ContentFiltered =>
                (ErrorCode.ProviderFailed, "Generation was incomplete because of content filtering.", "provider.review-request"),
            ProviderFailureCode.Incomplete =>
                (ErrorCode.ProviderFailed, "The provider reported incomplete generation.", "provider.review-request"),
            ProviderFailureCode.FirstDeltaTimeout or ProviderFailureCode.IdleTimeout =>
                (ErrorCode.DeadlineExceeded, "The text stream progress deadline expired. Upstream charges may still apply.", "provider.review-request"),
            _ => (ErrorCode.ProviderFailed, stage == Stage.Generation
                ? "The generation request failed. No retry or fallback was made."
                : "The transcription request failed. No retry or fallback was made.", "provider.review-request")
        };
        Error = new MartletError
        {
            Code = coreCode, Stage = stage, Retryable = false, Summary = summary, ActionId = action
        };
        Error.Validate();
    }
}
