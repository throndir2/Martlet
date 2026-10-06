using System.Text;
using Martlet.Companion.Platform;
using Martlet.Platform.Linux.DBus;

namespace Martlet.Platform.Linux.Desktop;

/// <summary>Starts Martlet at login through the XDG autostart folder (~/.config/autostart, or $XDG_CONFIG_HOME/autostart),
/// which GNOME, KDE, Xfce, Cinnamon, MATE and LXQt all honor. Off until the user turns it on; turning it off deletes the
/// file. An AppImage starts through $APPIMAGE so the entry survives the AppImage's temporary mount; a framework-dependent
/// run (dotnet Martlet.Companion.dll) starts the same way.</summary>
internal sealed class XdgAutostart(string directory, params string[] command) : IAutostart
{
    public const string FileName = "martlet.desktop";

    public static XdgAutostart ForCurrentUser()
    {
        var config = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        if (string.IsNullOrEmpty(config) || !Path.IsPathFullyQualified(config))
            config = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config");
        var appImage = Environment.GetEnvironmentVariable("APPIMAGE");
        var process = Environment.ProcessPath ?? "martlet";
        string[] command = !string.IsNullOrEmpty(appImage) && Path.IsPathFullyQualified(appImage) ? [appImage]
            : Path.GetFileNameWithoutExtension(process) == "dotnet" && Environment.GetCommandLineArgs() is [var assembly, ..] &&
              assembly.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) ? [process, Path.GetFullPath(assembly)]
            : [process];
        return new XdgAutostart(Path.Combine(config, "autostart"), command);
    }

    public string FilePath => Path.Combine(directory, FileName);

    public FeatureStatus Status => FeatureStatus.Yes($"XDG autostart ({FilePath})");

    public bool IsEnabled
    {
        get
        {
            try
            {
                if (!File.Exists(FilePath)) return false;
                var text = File.ReadAllText(FilePath);
                return !text.Contains("Hidden=true", StringComparison.Ordinal) &&
                    !text.Contains("X-GNOME-Autostart-enabled=false", StringComparison.Ordinal);
            }
            catch (IOException) { return false; }
            catch (UnauthorizedAccessException) { return false; }
        }
    }

    public FeatureStatus SetEnabled(bool enabled)
    {
        try
        {
            if (!enabled)
            {
                if (File.Exists(FilePath)) File.Delete(FilePath);
                return FeatureStatus.Yes("Martlet no longer starts when you log in.");
            }
            Directory.CreateDirectory(directory);
            var temporary = FilePath + ".tmp";
            File.WriteAllText(temporary, Entry(command), new UTF8Encoding(false));
            File.Move(temporary, FilePath, overwrite: true);
            return FeatureStatus.Yes("Martlet starts when you log in.");
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return FeatureStatus.No($"Could not change {FilePath}: {error.Message}");
        }
    }

    internal static string Entry(params string[] command) =>
        string.Join('\n',
            "[Desktop Entry]",
            "Type=Application",
            $"Name={LinuxDesktopIds.DisplayName}",
            "Comment=Martlet companion",
            $"Exec={string.Join(' ', command.Select(QuoteExec))}",
            $"Icon={LinuxDesktopIds.AppId}",
            "Terminal=false",
            "X-GNOME-Autostart-enabled=true",
            "X-KDE-autostart-after=panel",
            "");

    /// <summary>Quotes one Exec argument as the Desktop Entry spec requires: inside double quotes, escape " ` $ \ with a
    /// backslash, and double every backslash again because the whole value is a string with its own \ escapes. A bare %
    /// becomes %% (field codes).</summary>
    internal static string QuoteExec(string argument)
    {
        var plain = argument.Length > 0 && argument.All(c => char.IsAsciiLetterOrDigit(c) || c is '/' or '.' or '_' or '-' or '+' or ',' or ':');
        if (plain) return argument;
        var builder = new StringBuilder("\"");
        foreach (var c in argument)
        {
            switch (c)
            {
                case '"' or '`' or '$': builder.Append("\\\\").Append(c); break;
                case '\\': builder.Append("\\\\\\\\"); break;
                case '%': builder.Append("%%"); break;
                default: builder.Append(c); break;
            }
        }
        return builder.Append('"').ToString();
    }
}
