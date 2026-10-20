namespace Martlet.Core.Network;

/// <summary>Let my other computers find this PC (nearby.txt: "off" turns it off; anything else, or no file, means on). It stays on
/// this computer: it is how this computer is reached (docs/CLUSTER.md, "What stays with each computer").</summary>
public static class NearbyChoice
{
    public const string FileName = "nearby.txt";

    /// <summary>False when the owner turned it off here, true when the file says anything else, null without a readable file (on).</summary>
    public static bool? Read(string directory)
    {
        try { return File.ReadAllText(Path.Combine(directory, FileName)).Trim() != "off"; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return null; }
    }

    public static bool Enabled(string directory) => Read(directory) ?? true;

    public static void Save(string directory, bool on) => File.WriteAllText(Path.Combine(directory, FileName), on ? "on" : "off");

    /// <summary>"off", "on", or "on (default)" without a file.</summary>
    public static string Describe(string directory) => Read(directory) switch { false => "off", true => "on", null => "on (default)" };
}
