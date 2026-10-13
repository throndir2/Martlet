// Also built into Martlet's MCP server (with TalkPreferences.cs), in its own namespace.
#if MARTLET_MCP
namespace Martlet.Mcp.Shared;
#else
namespace Martlet.Desktop;
#endif

/// <summary>What Watch looks at: the user's screen, a camera Windows knows (webcam, capture card, virtual camera, a
/// phone connected as a webcam) or a phone / network camera address.</summary>
internal enum WatchKind { ActiveWindow, ActiveScreen, Camera, Url }

/// <summary>A phone or network camera address as Martlet saves it.</summary>
internal static class CameraAddress
{
    /// <summary>The address with any user name and password removed, safe to save in preferences.</summary>
    internal static string WithoutCredentials(string address) =>
        Uri.TryCreate(address.Trim(), UriKind.Absolute, out var uri) && uri.UserInfo.Length > 0
            ? new UriBuilder(uri) { UserName = "", Password = "" }.Uri.ToString() : address.Trim();
}
