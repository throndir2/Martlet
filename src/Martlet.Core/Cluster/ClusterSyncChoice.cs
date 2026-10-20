namespace Martlet.Core.Cluster;

/// <summary>Whether this computer keeps Martlet the same on all the owner's computers (cluster-sync.txt: "off" turns sync off;
/// anything else, or no file, means on). It stays on this computer: it says whether this computer takes part (docs/CLUSTER.md,
/// "What stays with each computer").</summary>
public static class ClusterSyncChoice
{
    public const string FileName = "cluster-sync.txt";

    /// <summary>False when the owner turned sync off here, true when the file says anything else, null when there is no file or
    /// it can't be read (on).</summary>
    public static bool? Read(string directory)
    {
        try { return File.ReadAllText(Path.Combine(directory, FileName)).Trim() != "off"; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>Whether sync is on: on by default, off only after the owner turned it off on this computer.</summary>
    public static bool Enabled(string directory) => Read(directory) ?? true;

    public static void Save(string directory, bool enabled)
    {
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, FileName), enabled ? "on" : "off");
    }

    /// <summary>The choice in words, as MCP shows it: "off", "on", or "on (default)" without a file.</summary>
    public static string Describe(string directory) => Read(directory) switch { false => "off", true => "on", null => "on (default)" };
}
