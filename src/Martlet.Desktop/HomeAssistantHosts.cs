using Martlet.Core.Installation;

namespace Martlet.Desktop;

/// <summary>Home Assistant on the owner's Martlet hosts: the <c>home-assistant</c> host role (installed with the same
/// martlet-host flow as every role, but on the host's own network and without a gateway route) and what each host reported
/// it found (machine.json features). Desktops reach a host's Home Assistant directly at <c>http://&lt;host&gt;:8123</c>.</summary>
internal static class HomeAssistantHosts
{
    internal const string Role = "home-assistant";
    internal const int Port = 8123;

    /// <summary>Feature tokens martlet-host reports, with what they mean to the owner.</summary>
    private static readonly (string Feature, string Words)[] Found =
    [
        ("mqtt-broker", "an MQTT broker"), ("zigbee2mqtt", "Zigbee2MQTT"), ("zwave-js", "Z-Wave JS"), ("frigate", "Frigate"),
        ("go2rtc", "go2rtc"), ("esphome", "ESPHome"), ("node-red", "Node-RED"), ("matter-server", "a Matter server"),
        ("music-assistant", "Music Assistant"), ("zigbee-radio", "a Zigbee radio"), ("zwave-radio", "a Z-Wave radio"),
        ("serial-radio", "a USB radio"), ("bluetooth", "Bluetooth")
    ];

    internal static bool Has(HostHardware? hardware, string feature) => hardware?.Features?.Contains(feature) == true;

    /// <summary>The host runs Martlet's Home Assistant role.</summary>
    internal static bool Runs(HostHardware? hardware) => Has(hardware, Role);

    internal static Uri Address(PairedHost host) => new($"http://{host.Address}:{Port}/");

    /// <summary>Why Home Assistant can't be installed on this host from here, or null when it can (an older host that reports
    /// no features is allowed: its engine checks again and explains).</summary>
    internal static string? CannotInstall(PairedHost host, HostHardware? hardware)
    {
        if (Runs(hardware)) return "It already runs Home Assistant.";
        if (Has(hardware, "ha-existing"))
            return $"Something already uses port {Port} there, probably another Home Assistant. Connect to it with Find or its address.";
        if (host.Method is HostSetupMethod.ThisPcDocker or HostSetupMethod.Agent ||
            hardware?.Platform is "windows" or "macos" or "ios" or "android")
            return "Home Assistant needs a Linux computer with Docker Engine; Docker Desktop on Windows or macOS can't put it on your home network.";
        if (hardware?.Features is not null && !Has(hardware, "host-network"))
            return "Home Assistant needs Docker Engine on Linux there, not Docker Desktop.";
        if (!host.CanLaunch) return "Set how Martlet reaches it (SSH) on its Devices card first.";
        return null;
    }

    /// <summary>What else the host found that Home Assistant can use, in words, or null.</summary>
    internal static string? Extras(HostHardware? hardware)
    {
        var words = Found.Where(f => Has(hardware, f.Feature)).Select(f => f.Words).ToArray();
        return words.Length == 0 ? null : "Also on this computer: " + string.Join(", ", words) + ".";
    }
}
