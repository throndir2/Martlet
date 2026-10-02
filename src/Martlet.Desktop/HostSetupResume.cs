using System.IO;
using Martlet.Core.Installation;
using Microsoft.Win32;

namespace Martlet.Desktop;

/// <summary>Continues a setup after Windows restarts for virtualization: a note in the data directory
/// (<see cref="ContinueSetup"/>) says what to continue, and a per-user RunOnce entry starts Martlet once at the next
/// sign-in (Windows deletes the entry before running it). Martlet reads and deletes the note at its next start
/// (<see cref="Take"/>), and forgets both as soon as Docker Desktop answers (<see cref="Clear"/>).</summary>
internal static class HostSetupResume
{
    internal const string RunOnceKey = @"Software\Microsoft\Windows\CurrentVersion\RunOnce";
    internal const string RunOnceValue = "MartletContinueSetup";
    private static string? dataDirectory;
    private static string? dataDirectoryArgument;

    /// <summary>The data directory Martlet runs with, and the --data-directory it was started with (null for the default).</summary>
    internal static void Initialize(string directory, string? argument)
    {
        dataDirectory = directory;
        dataDirectoryArgument = argument;
    }

    /// <summary>Remembers to continue <paramref name="task"/> (<paramref name="kind"/>) after the restart and asks Windows to
    /// start Martlet at the next sign-in. Returns the sentence that tells the owner what happens then.</summary>
    internal static string Register(ContinueSetupKind kind, string task)
    {
        if (dataDirectory is null) return "After you sign in again, open Martlet and press the same button again.";
        try { new ContinueSetup(kind, task, DateTimeOffset.Now).Save(dataDirectory); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException)
        {
            ErrorLog.Warn("Could not save what to continue after the restart", error);
            return "After you sign in again, open Martlet and press the same button again.";
        }
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunOnceKey);
            key.SetValue(RunOnceValue, Command(Environment.ProcessPath ?? throw new InvalidOperationException("Martlet's path is unknown."),
                dataDirectoryArgument));
            ErrorLog.Info($"Continues after the next sign-in: {kind} ({task})");
            return $"After you sign in again, Martlet opens by itself and continues: {task}.";
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Security.SecurityException or
            InvalidOperationException)
        {
            ErrorLog.Warn("Could not ask Windows to start Martlet at the next sign-in", error);
            return $"After you sign in again, open Martlet; it continues: {task}.";
        }
    }

    /// <summary>What to continue now (read once and forgotten), or null.</summary>
    internal static ContinueSetup? Take()
    {
        if (dataDirectory is null) return null;
        var note = ContinueSetup.Read(dataDirectory, DateTimeOffset.Now);
        Clear();
        return note;
    }

    /// <summary>Forgets a pending continuation (Docker Desktop answers, so there is nothing left to continue).</summary>
    internal static void Clear()
    {
        if (dataDirectory is null) return;
        var pending = File.Exists(ContinueSetup.PathIn(dataDirectory));
        ContinueSetup.Delete(dataDirectory);
        if (!pending) return;
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunOnceKey, writable: true);
            key?.DeleteValue(RunOnceValue, throwOnMissingValue: false);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Security.SecurityException) { }
    }

    /// <summary>The RunOnce command line: Martlet's executable, with the same --data-directory when it was started with one
    /// (trailing backslashes doubled so the closing quote stays a quote).</summary>
    internal static string Command(string executable, string? directoryArgument) =>
        Quote(executable) + (directoryArgument is null ? "" : " --data-directory " + Quote(directoryArgument));

    private static string Quote(string value)
    {
        var trailing = value.Length - value.TrimEnd('\\').Length;
        return "\"" + value + new string('\\', trailing) + "\"";
    }
}
