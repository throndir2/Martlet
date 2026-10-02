using System.Globalization;
using System.IO;
using System.Windows;
using Martlet.Core.Installation;
using Microsoft.Win32;

namespace Martlet.Desktop;

/// <summary>A Windows prerequisite the bundled tool installs; <paramref name="Id"/> is its -Install name.</summary>
internal sealed record Prerequisite(string Id, string Title, string Detail);

/// <summary>Finds missing Windows prerequisites from the registry and files only, and installs the ones the user picked
/// with the installed prerequisites tool, hidden, in a Martlet run window. Nothing is installed until the user asks.</summary>
internal static class Prerequisites
{
    private const string WebView2Client = @"Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}";
    private const string MicrophoneConsent = @"Software\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\microphone";

    internal static Prerequisite WebView2 { get; } = new("WebView2", "Microsoft Edge WebView2 Runtime",
        "Needed to show the desktop character. Downloaded from Microsoft.");
    internal static Prerequisite Microphone { get; } = new("Microphone", "Let desktop apps use the microphone",
        "Needed for push-to-talk. Opens Windows Settings, where you turn it on.");
    internal static Prerequisite WindowsSpeech { get; } = new("WindowsSpeech", "Windows offline speech for your language",
        "Speech recognition and voices from Windows Update. Asks for administrator approval.");
    internal static Prerequisite Ollama { get; } = new("Ollama", "Ollama",
        "Runs a conversation model on this PC (ollama.com via winget, MIT license). Offers a model sized to your GPU afterwards.");
    internal static Prerequisite DockerDesktop { get; } = new("DockerDesktop", "WSL 2 and Docker Desktop",
        "Hosts Audio2Face and other GPU roles on this PC (free for personal use). Also turns on Windows' virtualization features; asks for administrator approval, and after a restart Martlet continues by itself.");

    internal static IReadOnlyList<Prerequisite> All { get; } = [WebView2, Microphone, WindowsSpeech, Ollama, DockerDesktop];

    internal static Prerequisite For(AdvisorInstall install) => install switch
    {
        AdvisorInstall.WindowsSpeech => WindowsSpeech,
        AdvisorInstall.Ollama => Ollama,
        _ => DockerDesktop
    };

    internal static IReadOnlyList<Prerequisite> Missing() => All.Where(IsMissing).ToArray();

    internal static bool IsMissing(Prerequisite item)
    {
        try
        {
            return item.Id switch
            {
                "WebView2" => !HasWebView2(),
                "Microphone" => MicrophoneBlocked(),
                "WindowsSpeech" => !HasKey(Registry.LocalMachine,
                    $@"SOFTWARE\Microsoft\Speech\Recognizers\Tokens\MS-{CultureInfo.CurrentUICulture.LCID}-80-DESK"),
                "Ollama" => !File.Exists(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Ollama", "ollama.exe")) &&
                    !File.Exists(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Ollama", "ollama.exe")),
                _ => !MachineInfo.DockerDesktopInstalled()
            };
        }
        catch (Exception error) when (error is System.Security.SecurityException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>Installs <paramref name="items"/> with the bundled prerequisites tool in a run window: PowerShell runs hidden
    /// (-NoPrompt), so no console appears; installers and Windows' administrator prompt show their own windows. Docker
    /// Desktop is installed by Martlet itself in the same window, which also turns on what it needs from Windows and, when
    /// Windows must restart, continues after the next sign-in.
    /// <paramref name="ollamaModel"/> also downloads that model once Ollama is installed. Returns a status line.</summary>
    internal static async Task<string> InstallAsync(Window owner, IReadOnlyCollection<Prerequisite> items, string? ollamaModel = null)
    {
        if (items.Count == 0) return "Nothing to install.";
        var script = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "prerequisites", "Install-Prerequisites.ps1"));
        var scripted = items.Where(i => !ReferenceEquals(i, DockerDesktop)).ToArray();
        var docker = scripted.Length != items.Count;
        if (scripted.Length > 0 && !File.Exists(script))
            return $"The prerequisites tool is installed with Martlet but was not found at {script}. From a source checkout, run packaging\\windows\\Install-Prerequisites.ps1.";
        var titles = string.Join(", ", items.Select(i => i.Title));
        var summary = await HostRunWindow.RunAsync(owner, items.Count == 1 ? $"Install {items.First().Title}" : "Install prerequisites", async run =>
        {
            run.Status($"Installing {titles}. Each comes from its publisher; Windows may ask for administrator approval...");
            if (scripted.Length > 0)
            {
                var powershell = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");
                var args = new List<string> { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", script, "-NoPrompt",
                    "-Install", string.Join(",", scripted.Select(i => i.Id)) };
                if (!string.IsNullOrWhiteSpace(ollamaModel)) args.AddRange(["-OllamaModel", ollamaModel.Trim()]);
                var exit = await LocalProcess.RunAsync(powershell, args, run.Output, run.Token, workingDirectory: Path.GetDirectoryName(script));
                if (exit != 0) throw new InvalidOperationException($"The prerequisites tool stopped (exit {exit}). The output shows why.");
            }
            if (docker)
            {
                if (!MachineInfo.DockerDesktopInstalled()) await HostLocal.InstallDockerDesktopAsync(run.Status, run.Output, run.Token);
                await WindowsVirtualizationSetup.EnsureReadyAsync(run, ContinueSetupKind.Docker);
            }
            var still = items.Where(i => i.Id != Microphone.Id && IsMissing(i)).ToArray();
            return still.Length == 0 ? $"Done: {titles}."
                : $"Finished, but {string.Join(", ", still.Select(i => i.Title))} still isn't installed. The output shows why.";
        });
        return summary ?? $"{titles} were not installed. The run window shows why.";
    }

    /// <summary>The prerequisites checklist in Martlet: what is installed and what is missing, with the missing items to
    /// install ticked by default. Installs the ticked items in a run window; returns a status line, or null when canceled.</summary>
    internal static async Task<string?> ChooseAndInstallAsync(Window owner)
    {
        var missing = Missing();
        var installed = All.Where(i => !missing.Contains(i)).ToArray();
        var dialog = new HostInputDialog("Prerequisites", "Windows prerequisites for Martlet",
            (installed.Length == 0 ? "" : $"Already on this PC: {string.Join(", ", installed.Select(i => i.Title))}.\n\n") +
            (missing.Count == 0 ? "Everything Martlet can install is already here."
                : "Tick what to install. Each item comes from its publisher and keeps its own license terms; Martlet shows the progress."),
            missing.Count == 0 ? "_Close" : "_Install selected");
        foreach (var item in missing)
            dialog.AddCheck(item.Id, $"{item.Title}: {item.Detail}", ReferenceEquals(item, WebView2) || ReferenceEquals(item, Microphone));
        if (dialog.Ask(owner) is not { } values || missing.Count == 0) return null;
        var chosen = missing.Where(i => values.GetValueOrDefault(i.Id) == "yes").ToArray();
        return chosen.Length == 0 ? null : await InstallAsync(owner, chosen);
    }

    private static bool HasWebView2()
    {
        foreach (var (hive, view, path) in new[]
        {
            (RegistryHive.LocalMachine, RegistryView.Registry32, @"SOFTWARE\" + WebView2Client),
            (RegistryHive.LocalMachine, RegistryView.Registry64, @"SOFTWARE\" + WebView2Client),
            (RegistryHive.CurrentUser, RegistryView.Default, @"Software\" + WebView2Client)
        })
        {
            using var root = RegistryKey.OpenBaseKey(hive, view);
            using var key = root.OpenSubKey(path);
            if (key?.GetValue("pv") is string version && version.Length > 0 && version != "0.0.0.0") return true;
        }
        return false;
    }

    private static bool MicrophoneBlocked() =>
        Denied(Registry.LocalMachine, MicrophoneConsent) || Denied(Registry.CurrentUser, MicrophoneConsent) ||
        Denied(Registry.CurrentUser, MicrophoneConsent + @"\NonPackaged");

    private static bool Denied(RegistryKey root, string path)
    {
        using var key = root.OpenSubKey(path);
        return string.Equals(key?.GetValue("Value") as string, "Deny", StringComparison.OrdinalIgnoreCase);
    }

    private static bool HasKey(RegistryKey root, string path)
    {
        using var key = root.OpenSubKey(path);
        return key is not null;
    }
}
