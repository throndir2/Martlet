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
/// Machine Platform, Windows Subsystem for Linux, the Windows hypervisor and WSL (<see cref="WindowsVirtualization"/>),
/// turns on what is off with one administrator prompt, and when Windows must restart asks the owner, restarts it and
/// continues the setup after the next sign-in. Virtualization turned off in the firmware can't be changed from Windows:
/// Martlet offers to restart straight into the firmware settings instead.</summary>
internal static class WindowsVirtualizationSetup
{
    private const string FirmwareHow =
        "turn on the option usually called Intel Virtualization Technology (VT-x), or SVM Mode on AMD processors, then save and exit";

    /// <summary>Returns when Windows is ready (or does not say otherwise): true when Martlet just changed Windows without a
    /// restart (Docker Desktop, if it is running, needs a restart of its own). Throws <see cref="PausedForRestartException"/>
    /// when Windows must restart and <see cref="InvalidOperationException"/> when the owner declined or a step failed.
    /// <paramref name="resume"/> is what continues after the restart.</summary>
    internal static async Task<bool> EnsureReadyAsync(HostRunWindow run, ContinueSetupKind resume)
    {
        run.Status("Checking that Windows can run Docker Desktop (virtualization and WSL 2)...");
        var state = await WindowsVirtualization.ProbeAsync(run.Token);
        run.Output.Report("Windows: " + state.Describe() + ".");
        if (state.FirmwareOff) await FirmwareAsync(run, resume);
        if (!state.NeedsChanges) return false;
        var problems = string.Join(", ", state.Problems());
        run.Output.Report($"Docker Desktop can't start yet: {problems}. Martlet turns on what it needs.");
        run.Status("Turning on Virtual Machine Platform, Windows Subsystem for Linux and WSL for Docker Desktop. Windows asks for " +
            "administrator approval; installing WSL can take a few minutes...");
        switch (await FixAsync(run.Output, run.Token))
        {
            case null:
                throw new InvalidOperationException($"Windows was not changed, so Docker Desktop can't start ({problems}). " +
                    "Press the same button again and approve Windows' administrator prompt.");
            case WindowsVirtualization.RestartExitCode:
                await RestartAsync(run, resume);
                break;
            case 0:
                break;
            case 2:
                throw new InvalidOperationException("WSL could not be installed from Microsoft. Check this PC's internet connection, " +
                    "then press the same button again. The output shows why.");
            case var exit:
                throw new InvalidOperationException($"Windows could not turn on what Docker Desktop needs (exit {exit}). The output shows why.");
        }
        var after = await WindowsVirtualization.ProbeAsync(run.Token);
        run.Output.Report("Windows: " + after.Describe() + ".");
        if (after.FirmwareOff) await FirmwareAsync(run, resume);
        if (after.NeedsChanges)
            throw new InvalidOperationException($"Docker Desktop still can't start: {string.Join(", ", after.Problems())}. " +
                (after.HypervisorOff && !after.FeaturesOff
                    ? "Windows' virtualization features are on, but its hypervisor isn't running. Restart Windows; if that doesn't help, " +
                      "check that virtualization is on in the firmware (UEFI/BIOS) and that no other virtualization software blocks it."
                    : "The output shows why."));
        run.Output.Report("Windows is ready for Docker Desktop.");
        return true;
    }

    private static async Task FirmwareAsync(HostRunWindow run, ContinueSetupKind resume)
    {
        if (!ConfirmationDialog.Confirm(run,
                "Docker Desktop needs hardware virtualization, which is turned off in this PC's firmware (UEFI/BIOS). Windows can't turn " +
                "it on by itself.\n\nRestart into the firmware settings now? There, " + FirmwareHow + ". Save your work in other apps " +
                "first; Windows asks for administrator approval. After you sign in again, Martlet continues the setup.",
                "Turn on virtualization"))
            throw new InvalidOperationException("Docker Desktop can't start: virtualization is turned off in this PC's firmware (UEFI/BIOS). " +
                "Restart, open the firmware settings (usually Del, F2 or F10 while the PC starts) and " + FirmwareHow +
                "; then press the same button again.");
        var continues = HostSetupResume.Register(resume, run.Heading);
        run.Status("Windows asks for administrator approval to restart into the firmware settings...");
        if (await RestartWindowsAsync(firmware: true, run.Token) is { } failure)
            throw new PausedForRestartException($"Windows could not restart into the firmware settings ({failure}). Restart it yourself, " +
                "open the firmware settings (usually Del, F2 or F10 while the PC starts) and " + FirmwareHow + ". " + continues);
        throw new PausedForRestartException("Windows restarts into the firmware settings in a few seconds: " + FirmwareHow + ". " + continues);
    }

    private static async Task RestartAsync(HostRunWindow run, ContinueSetupKind resume)
    {
        var continues = HostSetupResume.Register(resume, run.Heading);
        if (!ConfirmationDialog.Confirm(run,
                "Windows needs to restart to finish turning on virtualization for Docker Desktop. Save your work in other apps first.\n\n" +
                continues + "\n\nRestart Windows now?", "Restart Windows"))
            throw new PausedForRestartException("Restart Windows to finish turning on virtualization for Docker Desktop. " + continues);
        run.Status("Restarting Windows...");
        if (await RestartWindowsAsync(firmware: false, run.Token) is { } failure)
            throw new PausedForRestartException($"Windows did not restart ({failure}). Restart it yourself to finish turning on " +
                "virtualization. " + continues);
        throw new PausedForRestartException("Windows restarts in a few seconds. " + continues);
    }

    /// <summary>Restarts Windows in five seconds with shutdown.exe (any signed-in user may); into the firmware settings it
    /// needs administrator approval. Returns null when Windows accepted, otherwise why not.</summary>
    internal static async Task<string?> RestartWindowsAsync(bool firmware, CancellationToken token)
    {
        var shutdown = Path.Combine(Environment.SystemDirectory, "shutdown.exe");
        var arguments = (firmware ? "/r /fw /t 5" : "/r /t 5") +
            " /c \"Martlet is restarting Windows to finish turning on virtualization for Docker Desktop.\"";
        try
        {
            using var process = Process.Start(new ProcessStartInfo(shutdown, arguments)
            {
                UseShellExecute = firmware, Verb = firmware ? "runas" : "", CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden
            });
            if (process is null) return "shutdown.exe did not start";
            await process.WaitForExitAsync(token);
            return process.ExitCode == 0 ? null : $"shutdown.exe exit {process.ExitCode}";
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
            output.Report("Administrator approval was declined; Windows was not changed.");
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
