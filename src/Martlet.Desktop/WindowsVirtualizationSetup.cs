using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text;
using Martlet.Core.Installation;

namespace Martlet.Desktop;

/// <summary>A run stopped because Windows must restart (or open its firmware settings) before Docker Desktop can start.
/// Martlet continues the setup after the next sign-in (<see cref="HostSetupResume"/>).</summary>
internal sealed class PausedForRestartException(string message) : InvalidOperationException(message);

/// <summary>Gets Windows ready for Docker Desktop's WSL 2 engine inside a run window: checks virtualization, Virtual
/// Machine Platform, Windows Subsystem for Linux, their services and WSL (<see cref="WindowsVirtualization"/>),
/// turns on what is off with one administrator prompt, and when Windows must restart asks the owner, restarts it and
/// continues the setup after the next sign-in. A restart already pending is handled before another install or Docker retry.
/// Virtualization turned off in the firmware can't be changed from Windows: on a fresh PC the features are turned on first,
/// so the one offered restart into the firmware settings finishes both.</summary>
internal static class WindowsVirtualizationSetup
{
    private const string FirmwareHow = "turn on Intel VT-x or AMD SVM, then save and exit";

    /// <summary>Returns when Windows is ready (or does not say otherwise): true when Martlet just changed Windows without a
    /// restart (Docker Desktop, if it is running, needs a restart of its own). Throws <see cref="PausedForRestartException"/>
    /// when Windows must restart and <see cref="InvalidOperationException"/> when the owner declined or a step failed.
    /// <paramref name="resume"/> is what continues after the restart.</summary>
    internal static async Task<bool> EnsureReadyAsync(HostRunWindow run, ContinueSetupKind resume)
    {
        run.Status("Checking Windows virtualization...");
        var state = await WindowsVirtualization.ProbeAsync(run.Token);
        run.Output.Report("Windows: " + state.Describe() + ".");
        if (state.FeaturesBeforeFirmware)
        {
            // A fresh PC: turning the features on needs no virtualization, so the restart into the firmware settings finishes both.
            run.Output.Report($"Windows needs changes before Docker Desktop can start: {string.Join(", ", state.Problems())}. " +
                "Martlet turns on the Windows features first, so one restart finishes both.");
            try { await TurnOnFeaturesAsync(run); }
            catch (InvalidOperationException error)
            {
                run.Output.Report(error.Message + " Martlet tries again once virtualization is on.");
            }
        }
        await CheckRestartAsync(state, run, resume);
        if (!state.NeedsChanges)
        {
            if (state.RuntimeUnavailable)
                throw new InvalidOperationException($"Docker Desktop can't start: {string.Join(", ", state.Problems())}. {state.Recovery}");
            if (!state.Ready) run.Output.Report("Windows readiness could not be confirmed. " + state.Recovery);
            return false;
        }
        run.Output.Report($"Windows needs changes before Docker Desktop can start: {string.Join(", ", state.Problems())}.");
        if (await TurnOnFeaturesAsync(run) == WindowsVirtualization.RestartExitCode) await RestartAsync(run, resume);
        var after = await WindowsVirtualization.ProbeAsync(run.Token);
        run.Output.Report("Windows: " + after.Describe() + ".");
        await CheckRestartAsync(after, run, resume);
        if (after.Blocked)
            throw new InvalidOperationException($"Docker Desktop still can't start: {string.Join(", ", after.Problems())}. {after.Recovery}");
        run.Output.Report(after.Ready ? "Windows is ready for Docker Desktop."
            : "Windows changes finished, but readiness could not be confirmed. " + after.Recovery);
        return true;
    }

    /// <summary>Turns on what Docker Desktop needs from Windows (<see cref="FixAsync"/>, one administrator prompt). Returns 0,
    /// or <see cref="WindowsVirtualization.RestartExitCode"/> when Windows must restart to finish; throws when the owner
    /// declined or a step failed.</summary>
    private static async Task<int> TurnOnFeaturesAsync(HostRunWindow run)
    {
        run.Status("Turning on Windows features for Docker Desktop. Windows asks for administrator approval. This can take a few minutes...");
        return await FixAsync(run.Output, run.Token) switch
        {
            null => throw new InvalidOperationException("Windows was not changed, so Docker Desktop can't start. Run this again and approve the administrator prompt."),
            0 => 0,
            WindowsVirtualization.RestartExitCode => WindowsVirtualization.RestartExitCode,
            2 => throw new InvalidOperationException("Windows couldn't install WSL. Check this PC's internet connection, then try again."),
            var exit => throw new InvalidOperationException($"Windows couldn't turn on the features Docker Desktop needs (exit {exit}). Check the output for details.")
        };
    }

    private static async Task CheckRestartAsync(WindowsVirtualization state, HostRunWindow run, ContinueSetupKind resume)
    {
        if (state.FirmwareOff)
        {
            if (state.VirtualMachine) throw new InvalidOperationException(state.Recovery);
            await FirmwareAsync(run, resume);
        }
        if (state.RestartRequired)
        {
            run.Output.Report(state.Recovery);
            run.Status("Windows needs a restart before Docker Desktop can start.");
            await RestartAsync(run, resume);
        }
    }

    private static async Task FirmwareAsync(HostRunWindow run, ContinueSetupKind resume)
    {
        if (!ConfirmationDialog.Confirm(run,
                "Hardware virtualization is off in this PC's firmware. Docker Desktop needs it, and until it is on Docker Desktop says " +
                "\"Virtualization support not detected\".\n\nRestart into firmware settings now? " +
                "Save your work first. Windows asks for administrator approval. After you sign in again, Martlet continues setup.\n\n" +
                "In firmware settings, " + FirmwareHow + ".",
                "Turn on virtualization"))
            throw new InvalidOperationException("Docker Desktop can't start until hardware virtualization is on. Restart into the firmware settings, " +
                FirmwareHow + ", then run this step again.");
        var continues = HostSetupResume.Register(resume, run.Heading);
        run.Status("Windows is asking to restart into firmware settings...");
        if (await RestartWindowsAsync(firmware: true, run.Token) is { } failure)
            throw new PausedForRestartException($"Windows couldn't restart into firmware settings ({failure}). Restart it yourself, open firmware settings and " +
                FirmwareHow + ". " + continues);
        throw new PausedForRestartException("Windows will restart into firmware settings. There, " + FirmwareHow + ". " + continues);
    }

    private static async Task RestartAsync(HostRunWindow run, ContinueSetupKind resume)
    {
        var continues = HostSetupResume.Register(resume, run.Heading);
        if (!ConfirmationDialog.Confirm(run,
                "Windows needs to restart to finish setup for Docker Desktop. Save your work first.\n\n" +
                continues + "\n\nRestart Windows now?", "Restart Windows"))
            throw new PausedForRestartException("Restart Windows to finish setup for Docker Desktop. Until then Docker Desktop can't start: " +
                "if you open it, it says virtualization isn't detected or Virtual Machine Platform isn't enabled. " + continues);
        run.Status("Restarting Windows...");
        if (await RestartWindowsAsync(firmware: false, run.Token) is { } failure)
            throw new PausedForRestartException($"Windows didn't restart ({failure}). Restart it yourself to finish setup. " + continues);
        throw new PausedForRestartException("Windows will restart in a few seconds. " + continues);
    }

    /// <summary>Restarts Windows in five seconds with shutdown.exe (any signed-in user may); into the firmware settings it
    /// needs administrator approval. Returns null when Windows accepted, otherwise why not.</summary>
    internal static async Task<string?> RestartWindowsAsync(bool firmware, CancellationToken token)
    {
        var shutdown = Path.Combine(Environment.SystemDirectory, "shutdown.exe");
        var arguments = (firmware ? "/r /fw /t 5" : "/r /t 5") +
            " /c \"Martlet needs to restart Windows to finish setting up Docker Desktop.\"";
        try
        {
            using var process = Process.Start(new ProcessStartInfo(shutdown, arguments)
            {
                UseShellExecute = firmware, Verb = firmware ? "runas" : "", CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden
            });
            if (process is null) return "Windows restart did not start";
            await process.WaitForExitAsync(token);
            return process.ExitCode == 0 ? null : $"restart command failed ({process.ExitCode})";
        }
        catch (Win32Exception error) when (error.NativeErrorCode == 1223) { return "administrator approval was declined"; }
        catch (Win32Exception error) { return error.Message; }
    }

    /// <summary>Runs <see cref="WindowsVirtualization.FixScript"/> elevated and hidden (one UAC prompt), showing its log as
    /// it goes. Returns its exit code, or null when the administrator prompt was declined.</summary>
    private static async Task<int?> FixAsync(IProgress<string> output, CancellationToken token)
    {
        var log = Path.Combine(Path.GetTempPath(), $"martlet-windows-features-{Guid.NewGuid():N}.log");
        Process? process;
        try
        {
            process = Process.Start(new ProcessStartInfo(WindowsVirtualization.PowerShellPath,
                "-NoProfile -NonInteractive -ExecutionPolicy Bypass -WindowStyle Hidden -EncodedCommand " +
                WindowsVirtualization.Encode(WindowsVirtualization.FixScript(log)))
            {
                UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden
            });
        }
        catch (Win32Exception error) when (error.NativeErrorCode == 1223)
        {
            output.Report("Administrator approval was declined. Windows was not changed.");
            return null;
        }
        catch (Win32Exception error) { throw new InvalidOperationException("Windows PowerShell could not start: " + error.Message); }
        if (process is null) throw new InvalidOperationException("Windows PowerShell could not start.");
        var shown = 0;
        void Show(bool all)
        {
            string text;
            try
            {
                using var stream = new FileStream(log, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var reader = new StreamReader(stream, Encoding.UTF8);
                text = reader.ReadToEnd();
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return; }
            var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
            var complete = all ? lines.Length : lines.Length - 1;
            for (; shown < complete; shown++)
                if (LocalProcess.Clean(lines[shown]) is { } line) output.Report(line);
        }
        using (process)
        {
            while (!process.HasExited)
            {
                await Task.WhenAny(process.WaitForExitAsync(token), Task.Delay(TimeSpan.FromSeconds(1), token));
                token.ThrowIfCancellationRequested();
                Show(all: false);
            }
            Show(all: true);
            try { File.Delete(log); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
            return process.ExitCode;
        }
    }
}
