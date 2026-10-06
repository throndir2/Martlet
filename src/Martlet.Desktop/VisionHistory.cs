// Also built into Martlet's MCP server (vision_history_check), in its own namespace.
#if MARTLET_MCP
namespace Martlet.Mcp.Shared;
#else
namespace Martlet.Desktop;
#endif

/// <summary>How what Martlet sees is kept in the conversation (docs/SCREEN_COMMENTARY.md): every screen glance and camera
/// look is an exchange whose user line starts with <see cref="ScreenMarker"/> or <see cref="CameraMarker"/> and says where
/// Martlet looked and what it saw (the reply's [seen: ...] words, <c>SeenTags</c>), answered by the remark or [pass]; a
/// message that came with a picture gets such a line after its words. Pictures are never kept. Like lines heard from what the
/// PC plays, these lines are never the user's words: memory, learning names, the record of conversations and the smart home
/// never read them.</summary>
internal static class VisionHistory
{
    /// <summary>Starts a line about a look at the user's screen.</summary>
    internal const string ScreenMarker = "[Screen]";

    /// <summary>Starts a line about a look through a camera.</summary>
    internal const string CameraMarker = "[Camera]";

    internal static string Marker(bool camera) => camera ? CameraMarker : ScreenMarker;

    /// <summary>The user's line of a look Martlet took on its own: <paramref name="where"/> it looked (the source and the
    /// window's title), <paramref name="why"/> when something drew its attention, and what it <paramref name="seen"/>.</summary>
    internal static string Look(bool camera, string where, string? why, string? seen) =>
        $"{Marker(camera)} You looked at {where}{(string.IsNullOrWhiteSpace(why) ? "" : $" ({why})")}{Seen(seen)}";

    /// <summary>The line after the words of a message that came with a picture of <paramref name="where"/>.</summary>
    internal static string WithMessage(bool camera, string where, string? seen) =>
        $"{Marker(camera)} With this message you saw {where}{Seen(seen)}";

    private static string Seen(string? seen) => string.IsNullOrWhiteSpace(seen) ? "." : $": {seen}.";

    /// <summary>Whether <paramref name="line"/> is one of these lines.</summary>
    internal static bool IsLine(string line)
    {
        var trimmed = line.TrimStart();
        return trimmed.StartsWith(ScreenMarker, StringComparison.Ordinal) || trimmed.StartsWith(CameraMarker, StringComparison.Ordinal);
    }

    /// <summary>Whether <paramref name="text"/> has one of these lines.</summary>
    internal static bool Has(string text) => text.Split('\n').Any(IsLine);

    /// <summary><paramref name="text"/> with <paramref name="line"/> after it, on a line of its own.</summary>
    internal static string After(string text, string line) => text.Length == 0 ? line : text + "\n" + line;

    /// <summary><paramref name="text"/> without these lines (null when nothing else is left): what memory and learning names
    /// may read.</summary>
    internal static string? Without(string? text)
    {
        if (text is null || !Has(text)) return text;
        var own = string.Join("\n", text.Split('\n').Where(line => !IsLine(line)));
        return string.IsNullOrWhiteSpace(own) ? null : own.Trim();
    }
}
