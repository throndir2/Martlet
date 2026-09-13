using Martlet.Core.Contracts;

namespace Martlet.Providers;

public enum ProviderFailureCode
{
    ConsentMissing, ConsentMismatch, ConsentExpired, ConsentConsumed, OriginRejected,
    ModelUnsupported, AudioLimit, CredentialUnavailable, CredentialBindingMismatch,
    Authentication, PermissionDenied, QuotaExceeded, RateLimited, ModelNotFound, FormatRejected,
    RequestRejected, RedirectRejected, Network, Server, ResponseTooLarge, ResponseTruncated,
    ResponseSchema, DeadlineExceeded
}

public sealed record ProviderFailure
{
    public ProviderFailureCode Code { get; }
    public MartletError Error { get; }
    public TimeSpan? RetryAfter { get; }

    internal ProviderFailure(ProviderFailureCode code, TimeSpan? retryAfter = null)
    {
        Code = code;
        RetryAfter = retryAfter;
        var (coreCode, summary, action) = code switch
        {
            ProviderFailureCode.ConsentMissing or ProviderFailureCode.ConsentMismatch or
            ProviderFailureCode.ConsentExpired or ProviderFailureCode.ConsentConsumed =>
                (ErrorCode.NotConfigured, "Explicit authorization for this audio upload and potential charge is required.", "provider.review-consent"),
            ProviderFailureCode.OriginRejected or ProviderFailureCode.CredentialBindingMismatch =>
                (ErrorCode.NotConfigured, "The provider destination or credential binding is not approved.", "provider.review-origin"),
            ProviderFailureCode.ModelUnsupported =>
                (ErrorCode.ProviderCapability, "The configured model is unsupported by this transcription adapter.", "provider.review-model"),
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
                (ErrorCode.StreamTruncated, "The provider response ended before its declared length.", "provider.review-response"),
            ProviderFailureCode.ResponseSchema =>
                (ErrorCode.InvalidContract, "The provider response did not match the supported schema.", "provider.review-response"),
            ProviderFailureCode.DeadlineExceeded =>
                (ErrorCode.DeadlineExceeded, "The transcription deadline expired. Upstream charges may still apply.", "provider.review-request"),
            _ => (ErrorCode.ProviderFailed, "The transcription request failed. No retry or fallback was made.", "provider.review-request")
        };
        Error = new MartletError
        {
            Code = coreCode, Stage = Stage.Transcription, Retryable = false, Summary = summary, ActionId = action
        };
        Error.Validate();
    }
}
