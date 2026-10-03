namespace Martlet.Core.Nodes;

/// <summary>How the martlet-host engine reports that another change on that host is still running. A host makes one change
/// at a time (setup, add, remove, pair, console, network-reset, machine, update; the kernel releases the engine's lock
/// however its holder ends). Attended runs wait for it and show what they wait for; background runs (Martlet's automatic
/// host updates: no terminal, no --yes) and attended ones that waited too long stop without changing anything, with exit
/// code <see cref="ExitCode"/> and one line <c>MARTLET-BUSY &lt;what is running&gt;</c>.</summary>
public static class HostEngineBusy
{
    public const int ExitCode = 75;
    public const string Marker = "MARTLET-BUSY ";
    /// <summary>The longest description kept from the engine's line.</summary>
    public const int MaximumCharacters = 200;

    /// <summary>What the host was busy with, from a run's exit code and output, or null when it did not stop as busy.</summary>
    public static string? Read(int exitCode, IEnumerable<string> lines)
    {
        if (exitCode != ExitCode) return null;
        string? found = null;
        foreach (var line in lines)
        {
            var text = (line ?? "").Trim();
            if (text.StartsWith(Marker, StringComparison.Ordinal)) found = text[Marker.Length..];
        }
        if (found is null) return null;
        var clean = new string(found.Where(c => !char.IsControl(c)).ToArray()).Trim();
        if (clean.Length == 0) return "another change";
        return clean.Length <= MaximumCharacters ? clean : clean[..(MaximumCharacters - 3)] + "...";
    }
}
