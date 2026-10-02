using Martlet.Core.Contracts;
using Martlet.Core.Settings;

namespace Martlet.Diagnostics;

public static class AudioSetupDiagnostics
{
    public static string Remedy(ErrorCode? code) => code switch
    {
        ErrorCode.AudioAccessDenied => "Review Windows Settings > Privacy & security > Microphone, including desktop-app access. Martlet changes no permissions.",
        ErrorCode.AudioDeviceBusy => "Close the competing audio client or release exclusive use, then explicitly retry. Do not change drivers or services.",
        ErrorCode.AudioFormatUnsupported => "Select another endpoint or manually review its supported shared PCM format. Bluetooth profile changes can invalidate a test.",
        ErrorCode.AudioDeviceUnavailable or ErrorCode.AudioDeviceLost or ErrorCode.AudioDeviceChanged =>
            "Reconnect or enable the intended endpoint, then reopen the device list in Microphone and speakers and select it explicitly. No replacement, room-speaker fallback or replay occurs.",
        ErrorCode.DeadlineExceeded => "This action expired. Wait for actual ownership release, review the selection and authorize a fresh bounded test.",
        ErrorCode.AudioCaptureFailed or ErrorCode.AudioPlaybackFailed =>
            "Native work or cleanup failed. Stop and wait for actual release. If ownership remains quarantined, close Martlet; do not start a replacement client.",
        _ => "No automatic retry. For input, review privacy and the intended microphone. For output, check headset connection, hardware mute and Windows/app volume manually, then explicitly test again."
    };

    public static string Describe(AudioSetupStatus? saved, string? localStages = null) =>
        (saved ?? AudioSetupStatus.From(null)).Describe() + Environment.NewLine +
        (localStages ?? "Current session: no local device test has run.") + Environment.NewLine +
        "Enumeration is not capture permission. PCM amplitude is not speech/VAD. Device drain is not audibility. " +
        "No provider, model, transcript, upload or completed conversation is involved.";
}
