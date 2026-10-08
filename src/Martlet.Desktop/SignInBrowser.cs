using System.Diagnostics;
using System.IO;
using Martlet.Credentials.Windows;

namespace Martlet.Desktop;

/// <summary>Opens a browser sign-in page (OpenID Connect, Google, Discord, Steam) in the system browser. In a Martlet MCP lab
/// run only (Invoke-MartletMcp.ps1 -LabCredentials sets both <see cref="LabCredentialNative.Variable"/> and
/// <see cref="Variable"/>, which names an existing folder), the page goes to the sign-in lab's simulated browser instead: the
/// address is written to <see cref="FileName"/> in that folder, and the lab answers it through this PC's real loopback
/// redirect. Never set in normal use.</summary>
internal static class SignInBrowser
{
    internal const string Variable = "MARTLET_LAB_BROWSER";
    internal const string FileName = "signin-browser.url";

    internal static void Open(string url)
    {
        if (LabFolder() is { } folder)
        {
            var temporary = Path.Combine(folder, $"signin-browser.{Guid.NewGuid():N}.tmp");
            File.WriteAllText(temporary, url);
            File.Move(temporary, Path.Combine(folder, FileName), overwrite: true);
            ErrorLog.Info($"Sign-in: the sign-in page went to the lab's simulated browser ({Variable}).");
            return;
        }
        Process.Start(new ProcessStartInfo(url) { UseShellExecute = true })?.Dispose();
    }

    private static string? LabFolder() =>
        LabCredentialNative.FromEnvironment() is not null && Environment.GetEnvironmentVariable(Variable) is { Length: > 0 } folder &&
        Path.IsPathFullyQualified(folder) && Directory.Exists(folder) ? folder : null;
}
