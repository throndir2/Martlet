using System.Text.RegularExpressions;

namespace Martlet.Mcp;

internal static partial class AutomationPolicy
{
    /// <summary>A safe value's text as ui_snapshot returns it: every e-mail address is [email] and every file path (C:\..., a
    /// \\server\share path, /home/... or /Users/...) is [path], so a log line or a friend's row never gives them away.</summary>
    internal static string? Redact(string? text)
    {
        if (string.IsNullOrEmpty(text)) return text;
        text = Email().Replace(text, "[email]");
        return FilePath().Replace(text, "[path]");
    }

    [GeneratedRegex(@"[A-Za-z0-9._%+-]+@[A-Za-z0-9-]+(?:\.[A-Za-z0-9-]+)*\.[A-Za-z]{2,}")]
    private static partial Regex Email();

    [GeneratedRegex(@"(?<![A-Za-z0-9])(?:[A-Za-z]:[\\/]|\\\\[^\\\s]+\\|/(?:home|Users)/)[^\s""'<>|]*")]
    private static partial Regex FilePath();
}
