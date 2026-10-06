using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security;
using Martlet.Companion.Platform;
using Martlet.Platform.MacOS.Native;

namespace Martlet.Platform.MacOS;

public enum MacAutostartState
{
    Off,
    On,
    /// <summary>Registered, but the user must allow it in System Settings > General > Login Items.</summary>
    NeedsApproval,
    Unavailable
}

/// <summary>Start Martlet when you log in. Inside Martlet.app on macOS 13+ it registers the app itself as a login item
/// (SMAppService.mainAppService), which shows under System Settings > General > Login Items. A copy run outside an app
/// bundle (development builds) uses a per-user LaunchAgent plist in ~/Library/LaunchAgents instead.</summary>
public sealed class MacAutostart(string? executablePath = null, string? launchAgentsFolder = null) : IAutostart
{
    public const string Label = "io.github.throndir2.martlet.companion";

    private readonly string executable = executablePath ?? Environment.ProcessPath ?? "";
    private readonly string agents = launchAgentsFolder ??
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "LaunchAgents");

    public string LaunchAgentPath => Path.Combine(agents, Label + ".plist");

    public FeatureStatus Status => Describe(State);

    public bool IsEnabled => State is MacAutostartState.On or MacAutostartState.NeedsApproval;

    public FeatureStatus SetEnabled(bool enabled) => Describe(enabled ? Enable() : Disable());

    internal FeatureStatus Describe(MacAutostartState state) => state switch
    {
        MacAutostartState.NeedsApproval => FeatureStatus.Yes(
            "macOS is holding Martlet's login item back: allow Martlet in System Settings > General > Login Items."),
        MacAutostartState.Unavailable => FeatureStatus.No(
            "macOS couldn't register Martlet as a login item. Move Martlet.app to the Applications folder and try again."),
        _ => FeatureStatus.Yes(UsesLoginItem() ? "Martlet is a login item (System Settings > General > Login Items)."
            : $"Martlet starts at login through {LaunchAgentPath}.")
    };

    /// <summary>Whether Martlet runs from inside an .app bundle (…/Martlet.app/Contents/MacOS/Martlet).</summary>
    public bool InAppBundle => AppBundle(executable) is not null;

    public MacAutostartState State
    {
        get
        {
            if (UsesLoginItem()) return OperatingSystem.IsMacOSVersionAtLeast(13) ? LoginItem.Status() : MacAutostartState.Unavailable;
            return File.Exists(LaunchAgentPath) ? MacAutostartState.On : MacAutostartState.Off;
        }
    }

    public MacAutostartState Enable()
    {
        if (UsesLoginItem()) return OperatingSystem.IsMacOSVersionAtLeast(13) ? LoginItem.Register() : MacAutostartState.Unavailable;
        if (executable.Length == 0) return MacAutostartState.Unavailable;
        Directory.CreateDirectory(agents);
        File.WriteAllText(LaunchAgentPath, LaunchAgentPlist(executable));
        return MacAutostartState.On;
    }

    public MacAutostartState Disable()
    {
        if (UsesLoginItem()) return OperatingSystem.IsMacOSVersionAtLeast(13) ? LoginItem.Unregister() : MacAutostartState.Unavailable;
        File.Delete(LaunchAgentPath);
        return MacAutostartState.Off;
    }

    /// <summary>Opens System Settings at Login Items, where the user approves a login item macOS held back.</summary>
    public static void OpenLoginItemsSettings()
    {
        if (OperatingSystem.IsMacOSVersionAtLeast(13)) LoginItem.OpenSettings();
    }

    private bool UsesLoginItem() => InAppBundle && (!OperatingSystem.IsMacOS() || OperatingSystem.IsMacOSVersionAtLeast(13));

    internal static string? AppBundle(string executable)
    {
        var marker = executable.IndexOf(".app/Contents/MacOS/", StringComparison.Ordinal);
        return marker < 0 ? null : executable[..(marker + 4)];
    }

    /// <summary>A LaunchAgent that starts <paramref name="executable"/> at login, once, without restarting it after quit.</summary>
    internal static string LaunchAgentPlist(string executable) => $"""
        <?xml version="1.0" encoding="UTF-8"?>
        <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
        <plist version="1.0">
        <dict>
          <key>Label</key>
          <string>{Label}</string>
          <key>ProgramArguments</key>
          <array>
            <string>{SecurityElement.Escape(executable)}</string>
            <string>--autostart</string>
          </array>
          <key>RunAtLoad</key>
          <true/>
          <key>KeepAlive</key>
          <false/>
          <key>ProcessType</key>
          <string>Interactive</string>
          <key>LimitLoadToSessionType</key>
          <string>Aqua</string>
        </dict>
        </plist>

        """;

    internal static MacAutostartState MapStatus(long status) => status switch
    {
        0 => MacAutostartState.Off,           // SMAppServiceStatusNotRegistered
        1 => MacAutostartState.On,            // SMAppServiceStatusEnabled
        2 => MacAutostartState.NeedsApproval, // SMAppServiceStatusRequiresApproval
        _ => MacAutostartState.Unavailable    // SMAppServiceStatusNotFound
    };

    [SupportedOSPlatform("macos13.0")]
    private static class LoginItem
    {
        private static readonly nint Framework =
            NativeLibrary.Load("/System/Library/Frameworks/ServiceManagement.framework/ServiceManagement");

        private static nint Service() => Framework == 0 ? 0 : ObjC.Send(ObjC.Class("SMAppService"), "mainAppService");

        public static MacAutostartState Status() => MapStatus(ObjC.SendLong(Service(), "status"));

        public static MacAutostartState Register()
        {
            _ = Framework;
            ObjC.SendBool(Service(), "registerAndReturnError:", 0);
            return Status();
        }

        public static MacAutostartState Unregister()
        {
            _ = Framework;
            ObjC.SendBool(Service(), "unregisterAndReturnError:", 0);
            return Status();
        }

        public static void OpenSettings()
        {
            _ = Framework;
            ObjC.SendVoid(ObjC.Class("SMAppService"), "openSystemSettingsLoginItems");
        }
    }
}
