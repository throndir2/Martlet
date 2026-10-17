namespace Martlet.Core.Network;

/// <summary>How Martlet names a computer's host service and how it shows that computer. One rule makes every new host ID from
/// the computer's name, whatever the computer runs (Windows with Docker Desktop, Linux or a NAS natively or in Docker, a Mac):
/// <see cref="ForComputer"/>. deploy/host/martlet-host's host_id_for does the same. A host ID never changes once its host has
/// one, because it keys pairings, certificates, the shared plan and host-hardware.json: hosts set up earlier keep theirs
/// (Nahida-host). Devices show the computer's own name (<see cref="ComputerName"/>) and the host ID only as a detail.</summary>
public static class HostIds
{
    public const string Suffix = "-host";
    private const int MaximumNameLength = 58;
    /// <summary>The longest computer name a host reports (a Linux host name has at most 63 characters).</summary>
    public const int MaximumComputerNameLength = 63;
    /// <summary>The name Docker Desktop gives its engine on every computer, so it never names the computer.</summary>
    public const string DockerDesktop = "docker-desktop";

    /// <summary>A new host ID for the computer named <paramref name="computerName"/>: the name in lowercase, spaces as "-", only
    /// ASCII letters, digits, '.', '_' and '-', without a leading '-', '_' or '.', at most 58 characters, then "-host"
    /// ("DIVA": diva-host, "Nahida": nahida-host, "Ada's MacBook Pro": adas-macbook-pro-host). "martlet-host" when nothing is left.</summary>
    public static string ForComputer(string? computerName)
    {
        var clean = new string((computerName ?? "").ToLowerInvariant().Select(c => char.IsWhiteSpace(c) ? '-' : c)
            .Where(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.').ToArray()).TrimStart('-', '_', '.');
        return (clean.Length == 0 ? "martlet" : clean.Length > MaximumNameLength ? clean[..MaximumNameLength] : clean) + Suffix;
    }

    /// <summary>The name a host's computer goes by on the Devices map, in lists and in status texts: the name the host reported
    /// for its computer (<paramref name="reported"/>, Nahida), else the computer name its host ID was most likely made from (the ID
    /// without "-host"), else the host ID itself (a host ID chosen by hand, such as gpu-box).</summary>
    public static string ComputerName(string hostId, string? reported = null)
    {
        if (CleanComputerName(reported) is { } name) return name;
        if (hostId.Length <= Suffix.Length || !hostId.EndsWith(Suffix, StringComparison.OrdinalIgnoreCase)) return hostId;
        var made = hostId[..^Suffix.Length];
        // An older Docker-method host on Docker Desktop took its engine's name (docker-desktop-host), not its computer's.
        return made is "martlet" or DockerDesktop ? hostId : made;
    }

    /// <summary>A computer name as a host may report it: 1 to 63 printable ASCII characters, trimmed; null for anything else and
    /// for Docker Desktop's engine name, which isn't the computer's.</summary>
    public static string? CleanComputerName(string? name)
    {
        var text = name?.Trim();
        return text is { Length: > 0 and <= MaximumComputerNameLength } && text.All(c => c is >= ' ' and <= '~') &&
            !text.Equals(DockerDesktop, StringComparison.OrdinalIgnoreCase) ? text : null;
    }
}
