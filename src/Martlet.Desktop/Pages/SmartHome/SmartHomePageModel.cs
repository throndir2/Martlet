using Martlet.Core.Installation;
using Martlet.Home;
using Martlet.HostClient;
using Martlet.Mcp.Client;

namespace Martlet.Desktop;

/// <summary>Companion › Smart home's decisions, without WPF. The page makes one each time it draws: it reads the saved connection,
/// your Martlet hosts and their last checks once, and says what Now, the connection, the hosts, sharing, flexible requests and
/// Manage Home Assistant show. <see cref="SmartHomePage"/> draws it, and <see cref="ISmartHomePageServices"/> is all it reads in
/// the app.</summary>
internal sealed class SmartHomePageModel
{
    private readonly ISmartHomePageServices services;

    internal SmartHomePageModel(ISmartHomePageServices services)
    {
        this.services = services;
        Saved = services.Preferences;
        Connected = services.Connected;
        Hosts = services.Hosts();
        Hardware = services.Hardware();
    }

    internal HomePreferences Saved { get; }
    internal bool Connected { get; }
    /// <summary>Your paired Martlet hosts, and what their last checks found.</summary>
    internal IReadOnlyList<PairedHost> Hosts { get; }
    internal IReadOnlyList<HostHardware> Hardware { get; }

    /// <summary>Now's line (SmartHomeNow).</summary>
    internal string NowText => Now(Saved, Connected);

    /// <summary>Whether Martlet uses Home Assistant when you ask (connected and on).</summary>
    internal bool InUse => Connected && Saved.Control;

    /// <summary>Which Home Assistant, in words: its name and address.</summary>
    internal string Where => (Saved.LocationName.Length > 0 ? Saved.LocationName : "Home Assistant") + (Saved.Address.Length > 0 ? $" at {Saved.Address}" : "");

    /// <summary>Whether the page should read Home Assistant's version, devices, updates and backups again (connected to a new address).</summary>
    internal bool NeedsManagement => Connected && services.ManageLoadedFor != Saved.Address && !services.ManageBusy;

    /// <summary>Companion › Smart home: which Home Assistant Martlet is connected to and what it may do with it.</summary>
    internal static string Now(HomePreferences saved, bool connected)
    {
        if (!connected) return "Off: Home Assistant isn't connected.";
        var where = (saved.LocationName.Length > 0 ? saved.LocationName : "Home Assistant") + (saved.Address.Length > 0 ? $" at {saved.Address}" : "");
        if (!saved.Control) return $"Off: connected to {where}, but Martlet doesn't control your smart home.";
        return $"Smart home: connected to {where}; Martlet may control lights, switches, climate, media, scenes and blinds" +
            (saved.AllowSensitive ? ", and locks, doors and alarms after a Yes each time" : "") +
            (saved.ModelTools ? ". Flexible requests are on." : ".");
    }

    /// <summary>The connection card's status line (SmartHomeStatus).</summary>
    internal string ConnectionText => Connected
        ? $"Connected to {Saved.LocationName} at {Saved.Address}" + (Saved.Version.Length > 0 ? $" (Home Assistant {Saved.Version})" : "") +
            (Saved.FollowShare ? Saved.SharedBy.Length > 0 ? $"; your other computers use it too (through {Saved.SharedBy})" : "; your other computers use it too" : "") + "."
        : "Not connected.";

    /// <summary>What Find on my network found, in words, for one Home Assistant.</summary>
    internal static string FoundText(FoundHomeAssistant found) =>
        $"{found.Name}: {HomeAssistantEndpoint.Display(found.Address)}" + (found.Version is { } v ? $" (Home Assistant {v})" : "");

    /// <summary>The report of the last check of <paramref name="host"/>, or null.</summary>
    internal HostHardware? Report(PairedHost host) => Hardware.FirstOrDefault(h => h.HostId == host.HostId);

    /// <summary>What a Martlet host's line says about Home Assistant, and which action it offers: Use (set up or sign in to the one it
    /// runs), Install, or none.</summary>
    internal (string Text, HostOffer Offer, string Display) HostLine(PairedHost host)
    {
        var report = Report(host);
        var display = HomeAssistantEndpoint.Display(HomeAssistantHosts.Address(host));
        var usesIt = Connected && string.Equals(Saved.Address, display, StringComparison.OrdinalIgnoreCase);
        if (HomeAssistantHosts.Runs(report))
            return ($"{host.HostId}: runs Home Assistant at {display}" + (usesIt ? ", and this PC uses it." : "."), usesIt ? HostOffer.None : HostOffer.Use, display);
        if (HomeAssistantHosts.CannotInstall(host, report) is { } why) return ($"{host.HostId}: {why}", HostOffer.None, display);
        return ($"{host.HostId}: can run Home Assistant.", HostOffer.Install, display);
    }

    /// <summary>The sharing card's first line (SmartHomeShareState).</summary>
    internal string ShareText
    {
        get
        {
            if (!services.ClusterEnabled) return "Keep Martlet the same on all my computers (Settings) is off, so only this PC uses this connection.";
            if (Connected && Saved.FollowShare)
                return Saved.SharedBy.Length > 0
                    ? $"Every computer uses this Home Assistant (this PC took it from {Saved.SharedBy})."
                    : "Every computer uses this Home Assistant.";
            if (Connected) return "Martlet gives this connection to your other computers on the next check.";
            return services.SharedAddress is { } offered
                ? $"Your other computers use Home Assistant at {offered}, and this PC connects to it by itself within a minute."
                : "None of your computers is connected to Home Assistant.";
        }
    }

    /// <summary>Whether Home Assistant's MCP server is running among the conversation's tools, in words (SmartHomeToolsStatus).</summary>
    internal string ToolsText
    {
        get
        {
            if (!services.ModelToolsEnabled) return Saved.Control ? "Off: Martlet uses standard commands." : "Off.";
            var (status, conflict) = services.HomeAssistantTools();
            if (conflict) return "Using your existing Home Assistant tools setup.";
            return status?.State switch
            {
                McpServerState.Ready => $"Ready: {status.Tools.Count} tools available.",
                McpServerState.Starting => "Connecting to Home Assistant's tools...",
                McpServerState.Failed => $"Couldn't connect to Home Assistant's tools ({status.Error}), so Martlet uses standard commands.",
                _ => "On: it connects when you open a talk window."
            };
        }
    }

    /// <summary>The devices card's status line (SmartHomeDevicesStatus).</summary>
    internal string DevicesText => services.DevicesStatus ?? (services.ManageBusy ? "Checking..." : "Not checked yet.");

    /// <summary>The Martlet host with an MQTT broker that runs this Home Assistant, when Home Assistant has no MQTT integration yet.</summary>
    internal PairedHost? MqttHost
    {
        get
        {
            var host = Hosts.FirstOrDefault(h => string.Equals(HomeAssistantEndpoint.Display(HomeAssistantHosts.Address(h)), Saved.Address,
                StringComparison.OrdinalIgnoreCase) && HomeAssistantHosts.Has(Report(h), "mqtt-broker"));
            return host is not null && services.System is { } system && !system.Integrations.Any(i => i.Contains("MQTT", StringComparison.OrdinalIgnoreCase))
                ? host : null;
        }
    }

    /// <summary>The Martlet host that runs this Home Assistant, or null.</summary>
    internal PairedHost? OnHost => Hosts.FirstOrDefault(h => string.Equals(HomeAssistantEndpoint.Display(HomeAssistantHosts.Address(h)), Saved.Address,
        StringComparison.OrdinalIgnoreCase));

    /// <summary>Manage Home Assistant's summary (SmartHomeManageStatus): version, installation, integrations and the last backup.</summary>
    internal string ManageText
    {
        get
        {
            var system = services.System;
            var onHost = OnHost;
            return system is null
                ? services.ManageStatus ?? (services.ManageBusy ? "Reading Home Assistant..." : "Not read yet.")
                : $"{system.LocationName}: Home Assistant {system.Version} ({system.Installation}" + (onHost is null ? "" : $" on {onHost.HostId}") + "); " +
                    (system.Integrations.Count == 0 ? "no integrations yet" : $"{system.Integrations.Count} integrations: {string.Join(", ", system.Integrations.Take(8))}" +
                        (system.Integrations.Count > 8 ? ", ..." : "")) +
                    (services.Backups is { } backups ? backups.Latest is { } latest ? $"; last backup {latest.ToLocalTime():g} ({backups.Count} kept)" : "; no backups yet" : "") + ".";
        }
    }

    /// <summary>How Home Assistant itself is updated, when it isn't through Home Assistant's own updates, or null.</summary>
    internal string? UpdatedWithText => services.System is { Supervised: false }
        ? OnHost is { } onHost
            ? $"Home Assistant itself is updated with Martlet: Update host on {onHost.HostId}'s Devices card brings the version this Martlet release uses."
            : "This Home Assistant runs as a container you manage; update it where it runs."
        : null;

    /// <summary>One waiting update, in words (SmartHomeUpdate-n).</summary>
    internal static string UpdateText(HomeUpdate update) =>
        $"Update: {update.Title} {update.Installed ?? "?"} → {update.Latest ?? "newer"}" + (update.Installing ? " (installing)" : "");

    /// <summary>What adding a device or MQTT did, in words.</summary>
    internal static string FlowText(string name, HomeFlowStep step) => step.Kind switch
    {
        HomeStepKind.Done => $"Added {name} to Home Assistant.",
        HomeStepKind.Aborted => step.Reason is "already_configured" or "already_in_progress"
            ? $"{name} is already set up in Home Assistant." : $"Home Assistant couldn't add {name} ({step.Reason ?? "stopped"}).",
        HomeStepKind.Progress => $"Home Assistant is still adding {name}, so check again in a moment.",
        _ when step.Errors.Count > 0 => $"Home Assistant couldn't add {name} ({string.Join(", ", step.Errors)}): finish it on its Integrations page, which is opening.",
        _ => $"{name} needs a few more details (such as a code, key or choice): finish it on Home Assistant's Integrations page, which is opening."
    };
}

/// <summary>What a Martlet host's line on Companion › Smart home offers.</summary>
internal enum HostOffer { None, Use, Install }
