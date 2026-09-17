namespace Martlet.VoiceActivity;

public enum VoiceActivityFailureCode
{
    NativePrivacyUnqualified, InvalidInput, InvalidBinding, AuthorizationRequired,
    AuthorizationConsumed, Canceled, DeadlineExceeded, Busy, Disposed, InputUnavailable,
    PayloadTooLarge, StreamTruncated, InvalidScore, ModelUnavailable, InvalidModelSize,
    InvalidModelHash, ModelSchemaMismatch, UnsupportedPlatform, NativeRuntimeUnavailable,
    InferenceFailed, CancellationFailed, CleanupFailed, InvalidState, CallbackFailed
}

public sealed record VoiceActivityFailure
{
    public VoiceActivityFailureCode Code { get; }
    public string Summary => Code switch
    {
        VoiceActivityFailureCode.NativePrivacyUnqualified => "Production native voice activity analysis is unavailable until its privacy boundary is qualified.",
        VoiceActivityFailureCode.InvalidInput => "Voice activity input or options are invalid.",
        VoiceActivityFailureCode.InvalidBinding => "The source, session, epoch, profile or configuration does not match this analysis.",
        VoiceActivityFailureCode.AuthorizationRequired => "A matching explicit local-analysis authorization is required.",
        VoiceActivityFailureCode.AuthorizationConsumed => "This local-analysis authorization has already been consumed.",
        VoiceActivityFailureCode.Canceled => "Local analysis was canceled. It will not restart itself.",
        VoiceActivityFailureCode.DeadlineExceeded => "The original local-analysis permission or processing budget expired.",
        VoiceActivityFailureCode.Busy => "The previous analysis still owns its resources. No analysis was queued.",
        VoiceActivityFailureCode.Disposed => "This analysis owner has been disposed.",
        VoiceActivityFailureCode.InputUnavailable => "The caller's captured utterance was disposed before it could be copied.",
        VoiceActivityFailureCode.PayloadTooLarge => "The analysis exceeded its bounded sample, frame or segment capacity.",
        VoiceActivityFailureCode.StreamTruncated => "The supplied PCM or score sequence is incomplete or discontinuous.",
        VoiceActivityFailureCode.InvalidScore => "The classifier supplied a nonfinite, out-of-range or malformed result.",
        VoiceActivityFailureCode.ModelUnavailable => "The explicitly selected local model could not be read.",
        VoiceActivityFailureCode.InvalidModelSize => "The model length does not match the pinned artifact.",
        VoiceActivityFailureCode.InvalidModelHash => "The model digest does not match the pinned artifact.",
        VoiceActivityFailureCode.ModelSchemaMismatch => "The model's tensor contract does not match the pinned adapter.",
        VoiceActivityFailureCode.UnsupportedPlatform => "This native qualification composition supports Windows x64 only.",
        VoiceActivityFailureCode.NativeRuntimeUnavailable => "The pinned local CPU runtime is missing or cannot be initialized.",
        VoiceActivityFailureCode.InferenceFailed => "The classifier failed. This is not a no-activity result.",
        VoiceActivityFailureCode.CancellationFailed => "Owned cancellation failed. The analysis owner is quarantined.",
        VoiceActivityFailureCode.CleanupFailed => "Resource cleanup could not be confirmed. Keep this owner quarantined.",
        VoiceActivityFailureCode.InvalidState => "This completed, failed or disposed operation cannot accept that action.",
        VoiceActivityFailureCode.CallbackFailed => "The supplied managed window consumer failed.",
        _ => throw new ArgumentOutOfRangeException()
    };
    public string ActionId => Code switch
    {
        VoiceActivityFailureCode.NativePrivacyUnqualified => "vad.native_unqualified",
        VoiceActivityFailureCode.Busy or VoiceActivityFailureCode.CleanupFailed or VoiceActivityFailureCode.CancellationFailed => "vad.await_release",
        VoiceActivityFailureCode.ModelUnavailable or VoiceActivityFailureCode.InvalidModelSize or
            VoiceActivityFailureCode.InvalidModelHash or VoiceActivityFailureCode.ModelSchemaMismatch => "vad.check_model",
        _ => "vad.review_analysis"
    };

    public VoiceActivityFailure(VoiceActivityFailureCode code)
    {
        if (!Enum.IsDefined(code)) throw new ArgumentOutOfRangeException(nameof(code));
        Code = code;
    }
}

public sealed class VoiceActivityException : Exception
{
    public VoiceActivityFailure Failure { get; }
    public VoiceActivityException(VoiceActivityFailureCode code) : base(new VoiceActivityFailure(code).Summary) =>
        Failure = new(code);
}

internal static class VadCheck
{
    internal static void Require(bool condition, VoiceActivityFailureCode code = VoiceActivityFailureCode.InvalidInput)
    {
        if (!condition) throw new VoiceActivityException(code);
    }
}
