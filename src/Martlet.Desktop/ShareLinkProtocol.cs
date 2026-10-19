using System.IO;
using Microsoft.Win32;
using Martlet.Core.Network;

namespace Martlet.Desktop;

/// <summary>
/// Makes share links open Martlet (docs/NETWORK.md, "Sharing with a link"). Martlet registers the martlet: link type for this
/// Windows user (HKCU\Software\Classes\martlet, no administrator rights) each time it starts normally, pointing at itself, so
/// clicking martlet://share/... starts Martlet with the link. A start with a link while Martlet already runs hands the link
/// over in the data folder (one pending-share-link-*.link file each) and asks the running Martlet to show itself; that one takes them.
/// </summary>
internal static class ShareLinkProtocol
{
    private const string ClassesKey = @"Software\Classes\" + ShareLink.Scheme;
    internal const string HandOverFile = "pending-share-link.txt";
    private const string HandOverPrefix = "pending-share-link-";
    private const string HandOverExtension = ".link";

    /// <summary>The registry command for <paramref name="executable"/>: the link is the only argument.</summary>
    internal static string Command(string executable) => $"\"{executable}\" \"%1\"";

    /// <summary>Registers (or repoints) the martlet: link type for this Windows user at <paramref name="executable"/>. Returns
    /// whether it changed anything.</summary>
    internal static bool Register(string executable)
    {
        var command = Command(executable);
        using (var existing = Registry.CurrentUser.OpenSubKey(ClassesKey + @"\shell\open\command"))
            if (existing?.GetValue(null) as string == command) return false;
        using var root = Registry.CurrentUser.CreateSubKey(ClassesKey);
        root.SetValue(null, "URL:Martlet share link");
        root.SetValue("URL Protocol", "");
        using (var icon = root.CreateSubKey("DefaultIcon")) icon.SetValue(null, $"\"{executable}\",0");
        using (var open = root.CreateSubKey(@"shell\open\command")) open.SetValue(null, command);
        return true;
    }

    /// <summary>Where the martlet: link type points now, or null when it isn't registered (for MCP and the log).</summary>
    internal static string? Registered()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(ClassesKey + @"\shell\open\command");
            return key?.GetValue(null) as string;
        }
        catch (Exception error) when (error is System.Security.SecurityException or UnauthorizedAccessException or IOException) { return null; }
    }

    /// <summary>Takes a share link from the start arguments (the last one, when it is a martlet: link); returns the rest.</summary>
    internal static (string[] Others, string? Link) Split(string[] args) =>
        args is [.., var last] && last.StartsWith(ShareLink.Scheme + ":", StringComparison.OrdinalIgnoreCase) && last.Length <= 16_384
            ? (args[..^1], last)
            : (args, null);

    /// <summary>Leaves <paramref name="link"/> for the Martlet already running with <paramref name="dataDirectory"/>. Each link
    /// gets its own file, written whole under a temporary name and then renamed, so two quick starts don't lose or cut one.</summary>
    internal static void HandOver(string dataDirectory, string link)
    {
        var name = $"{HandOverPrefix}{DateTime.UtcNow.Ticks:D20}-{Guid.NewGuid():N}";
        var temporary = Path.Combine(dataDirectory, name + ".tmp");
        File.WriteAllText(temporary, link);
        File.Move(temporary, Path.Combine(dataDirectory, name + HandOverExtension));
    }

    /// <summary>The links other starts left, oldest first; each is removed once taken.</summary>
    internal static IReadOnlyList<string> TakeAllHandedOver(string dataDirectory)
    {
        var links = new List<string>();
        string[] files;
        try
        {
            files = Directory.GetFiles(dataDirectory, HandOverPrefix + "*" + HandOverExtension);
            // The file name of an older Martlet still counts.
            var legacy = Path.Combine(dataDirectory, HandOverFile);
            if (File.Exists(legacy)) files = [legacy, .. files];
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return links; }
        foreach (var path in files.OrderBy(f => Path.GetFileName(f) == HandOverFile ? "" : Path.GetFileName(f), StringComparer.Ordinal))
        {
            try
            {
                var text = File.ReadAllText(path);
                File.Delete(path);
                if (text.Length is > 0 and <= 16_384) links.Add(text.Trim());
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }
        return links;
    }
}
