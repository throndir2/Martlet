using System.IO;
using System.Text.Json;
using Microsoft.Win32;

namespace Martlet.Desktop;

/// <summary>Settings › Startup and closing: whether closing the window keeps Martlet running in the notification area (on by
/// default), whether a start with Windows opens no window, whether Martlet shows the character and starts listening as it starts,
/// and whether Martlet already said where it went. Saved in background.json; Start with Windows itself is the per-user Run entry
/// (<see cref="WindowsStartup"/>).</summary>
internal sealed record BackgroundPreferences(bool CloseToTray = true, bool StartInTray = true, bool HintShown = false,
    bool StartCompanion = false)
{
    private const string FileName = "background.json";

    internal static BackgroundPreferences Load(string? directory)
    {
        if (directory is null) return new();
        try
        {
            var path = Path.Combine(directory, FileName);
            return File.Exists(path) ? JsonSerializer.Deserialize<BackgroundPreferences>(File.ReadAllText(path)) ?? new() : new();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException) { return new(); }
    }

    internal bool Save(string? directory)
    {
        if (directory is null) return false;
        try
        {
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, FileName);
            var temporary = Path.Combine(directory, $"background.{Guid.NewGuid():N}.tmp");
            try
            {
                File.WriteAllText(temporary, JsonSerializer.Serialize(this));
                File.Move(temporary, path, overwrite: true);
            }
            finally
            {
                if (File.Exists(temporary)) File.Delete(temporary);
            }
            return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return false; }
    }
}

internal enum StartupState { Off, On, TurnedOffInWindows }

/// <summary>Start Martlet when you sign in to Windows: the per-user Run entry "Martlet" with this executable, the same
/// --data-directory and, to open no window, --tray. Windows' own Startup apps switch (StartupApproved) can turn it off; turning
/// it on in Martlet clears that switch again. The uninstaller removes the entry when it points into the install.</summary>
internal static class WindowsStartup
{
    internal const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    internal const string ApprovedKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";
    internal const string ValueName = "Martlet";
    internal const string TrayArgument = "--tray";

    /// <summary>Whether Windows starts Martlet at sign-in, and the command it runs.</summary>
    internal static (StartupState State, string? Command) Read()
    {
        using var run = Registry.CurrentUser.OpenSubKey(RunKey);
        if (run?.GetValue(ValueName) is not string command || command.Length == 0) return (StartupState.Off, null);
        using var approved = Registry.CurrentUser.OpenSubKey(ApprovedKey);
        // The first byte is even while the entry is allowed (2) and odd once turned off in Settings or Task Manager (3).
        return approved?.GetValue(ValueName) is byte[] { Length: > 0 } flags && (flags[0] & 1) == 1
            ? (StartupState.TurnedOffInWindows, command)
            : (StartupState.On, command);
    }

    internal static void Enable(string command)
    {
        using (var run = Registry.CurrentUser.CreateSubKey(RunKey)) run.SetValue(ValueName, command);
        using var approved = Registry.CurrentUser.OpenSubKey(ApprovedKey, writable: true);
        approved?.DeleteValue(ValueName, throwOnMissingValue: false);
    }

    internal static void Disable()
    {
        using (var run = Registry.CurrentUser.OpenSubKey(RunKey, writable: true)) run?.DeleteValue(ValueName, throwOnMissingValue: false);
        using var approved = Registry.CurrentUser.OpenSubKey(ApprovedKey, writable: true);
        approved?.DeleteValue(ValueName, throwOnMissingValue: false);
    }

    internal static string Command(string executable, string? dataDirectoryArgument, bool inTray) =>
        HostSetupResume.Command(executable, dataDirectoryArgument) + (inTray ? " " + TrayArgument : "");
}
