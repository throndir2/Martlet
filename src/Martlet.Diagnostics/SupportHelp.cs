namespace Martlet.Diagnostics;

// Shared authored help, not a collector or a filesystem readiness probe.
public static class SupportHelp
{
    public const string DirectoryName = "support-v1";
    public const string Summary =
        "Troubleshooting metadata recording is OFF at each launch. Open Desktop Troubleshooting at any setup stage. " +
        "Opening it does not start a journal, resolve keys, enumerate/capture/play audio, contact a provider or write settings. " +
        "Record troubleshooting metadata is an explicit local action, NOT speech recording or telemetry consent. " +
        "Stop or closing Troubleshooting stops collection; outstanding IO and cleanup must actually finish.";
    public const string Retention =
        "The selected settings data directory's support-v1 child is the only journal scope. It must be trusted, user-owned, local, " +
        "and free of network/reparse paths; an unsupported location is an error, never silently replaced. " +
        "Engine defaults: at most 50 MiB, 32 segments, 8 KiB per record and 7 days. Whole-segment retention runs on explicit Start/Append, " +
        "not an idle timer; size/slot pressure can remove newer evidence. Selection: at most 2,048 records / 2 MiB; frozen preview: 4 MiB.";
    public const string Export =
        "Preview every frozen file, its size, source and digest, then choose a separate local ZIP destination. " +
        "The default-No confirmation binds those exact bytes and destination. Existing files are never overwritten. " +
        "Exported locally means a local archive only. No support contact or upload channel is configured. CLI export is not implemented. " +
        "Do not post an unreviewed bundle publicly. No audio, text conversations, screenshots, keys or raw settings are collected; " +
        "times, counts, versions and pseudonymous correlations remain potentially identifying. Exports are outside journal retention.";
    public static string Text => Summary + "\n\n" + Retention + "\n\n" + Export;
    public static string Location(string dataDirectory) => Path.Combine(dataDirectory, DirectoryName);
}
