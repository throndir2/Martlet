namespace Martlet.Diagnostics;

// Shared authored help, not a collector or a filesystem readiness probe.
public static class SupportHelp
{
    public const string DirectoryName = "support-v1";
    public const string Summary =
        "Troubleshooting recording is off until you start it. It records local status only, not audio, conversations, screenshots or keys.";
    public const string Retention =
        "Records stay in the selected Martlet data folder. Martlet keeps up to 50 MB or 7 days.";
    public const string Export =
        "Export creates a local ZIP you choose. Review it before sharing; Martlet does not upload it. Existing files are not overwritten.";
    public static string Text => Summary + "\n\n" + Retention + "\n\n" + Export;
    public static string Location(string dataDirectory) => Path.Combine(dataDirectory, DirectoryName);
}
