namespace Martlet.Launcher;

internal static class MinimalProcessEnvironment
{
    internal static IReadOnlyDictionary<string, string> Create(
        string runtimeDirectory,
        IReadOnlyDictionary<string, string> readiness)
    {
        runtimeDirectory = RequiredDirectory(runtimeDirectory);
        var windows = RequiredDirectory(
            Environment.GetFolderPath(Environment.SpecialFolder.Windows));
        var result = new Dictionary<string, string>(
            StringComparer.OrdinalIgnoreCase)
        {
            ["SystemRoot"] = windows,
            ["WINDIR"] = windows,
            ["USERPROFILE"] = runtimeDirectory,
            ["LOCALAPPDATA"] = runtimeDirectory,
            ["APPDATA"] = runtimeDirectory,
            ["TEMP"] = runtimeDirectory,
            ["TMP"] = runtimeDirectory,
            ["DOTNET_EnableDiagnostics"] = "0",
            ["COMPlus_EnableDiagnostics"] = "0",
            ["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1"
        };
        foreach (var pair in readiness)
        {
            if (!result.TryAdd(pair.Key, pair.Value))
                throw new LauncherException(LauncherFailure.ProcessStartFailed);
        }
        return result;
    }

    private static string RequiredDirectory(string path)
    {
        if (string.IsNullOrWhiteSpace(path) ||
            !Path.IsPathFullyQualified(path) ||
            !Directory.Exists(path) ||
            path.IndexOf('\0') >= 0)
            throw new LauncherException(LauncherFailure.ProcessStartFailed);
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
    }
}
