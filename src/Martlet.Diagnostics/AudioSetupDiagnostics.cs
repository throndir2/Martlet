using Martlet.Core.Contracts;
using Martlet.Core.Settings;

namespace Martlet.Diagnostics;

public static class AudioSetupDiagnostics
{
    public static string Remedy(ErrorCode? code) => code switch
    {
        ErrorCode.AudioAccessDenied => "Allow microphone access in Windows Settings, then try again.",
        ErrorCode.AudioDeviceBusy => "Close any app using the device, then try again.",
        ErrorCode.AudioFormatUnsupported => "Choose another device or change its Windows audio format.",
        ErrorCode.AudioDeviceUnavailable or ErrorCode.AudioDeviceLost or ErrorCode.AudioDeviceChanged =>
            "Reconnect or enable the device, then choose it again.",
        ErrorCode.DeadlineExceeded => "The test took too long. Check the device, then run it again.",
        ErrorCode.AudioCaptureFailed or ErrorCode.AudioPlaybackFailed =>
            "The audio test failed. Close apps using the device, then try again.",
        _ => "Check the selected microphone or speakers, then run the test again."
    };

    public static string Describe(AudioSetupStatus? saved, string? localStages = null) =>
        ShortStatus(saved ?? AudioSetupStatus.From(null)) + Environment.NewLine +
        (localStages ?? "This session: no device test yet.");

    private static string ShortStatus(AudioSetupStatus saved) =>
        $"{Choice("Microphone", saved.Input, saved.InputSelected, saved.InputUsesDefault)}{Environment.NewLine}" +
        $"{Choice("Speakers", saved.Output, saved.OutputSelected, saved.OutputUsesDefault)}";

    private static string Choice(string label, AudioCheckpointStatus? checkpoint, bool selected, bool usesDefault)
    {
        var device = usesDefault ? "Windows default" : "chosen device";
        if (!selected) return $"{label}: not set up.";
        if (checkpoint is null) return $"{label}: {device}, not tested.";
        var result = checkpoint.Outcome switch
        {
            LocalAudioOutcome.SamplesReceived => "tested",
            LocalAudioOutcome.Heard => "heard",
            _ => "tone played"
        };
        return $"{label}: {device}, {result}.";
    }
}
