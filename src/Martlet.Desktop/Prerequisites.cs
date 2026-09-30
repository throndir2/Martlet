using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using Martlet.Core.Installation;
using Microsoft.Win32;

namespace Martlet.Desktop;

/// <summary>A Windows prerequisite the bundled tool installs; <paramref name="Id"/> is its -Install name.</summary>
internal sealed record Prerequisite(string Id, string Title, string Detail);

/// <summary>Finds missing Windows prerequisites from the registry and files only, and hands the ones the user picked
/// to the installed prerequisites tool in a visible console. Nothing is installed until the user asks.</summary>
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
        "Hosts Audio2Face and other GPU roles on this PC (free for personal use). Asks for administrator approval; a restart may follow.");

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

    /// <summary>Opens the prerequisites tool: installs <paramref name="items"/> when given, otherwise its checklist.
    /// Returns a status line for the home screen.</summary>
    internal static string Launch(IReadOnlyCollection<Prerequisite> items)
    {
        var script = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "prerequisites", "Install-Prerequisites.ps1"));
        if (!File.Exists(script))
            return $"The prerequisites tool is installed with Martlet but was not found at {script}. From a source checkout, run packaging\\windows\\Install-Prerequisites.ps1.";
        var arguments = $"-NoProfile -ExecutionPolicy Bypass -File \"{script}\"";
        if (items.Count > 0) arguments += " -PauseWhenDone -Install " + string.Join(",", items.Select(i => i.Id));
        try
        {
            var powershell = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");
            Process.Start(new ProcessStartInfo(powershell, arguments) { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(script)! })?.Dispose();
        }
        catch (Exception error) when (error is Win32Exception or IOException) { return error.Message; }
        return items.Count == 0
            ? "Prerequisites opened in a console window. Nothing is installed until you choose an item there."
            : $"Installing {string.Join(", ", items.Select(i => i.Title))} in a console window. Each comes from its publisher; follow the window until it finishes.";
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
