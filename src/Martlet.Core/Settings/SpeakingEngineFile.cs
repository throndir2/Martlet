namespace Martlet.Core.Settings;

/// <summary>Which self-hosted voice engine (<see cref="SpeechEngines"/>) does the Speaking job on this PC, in speaking-engine.txt
/// (the engine's key). It stays on this PC because it follows the engine the speaking host runs now (docs/CLUSTER.md, "What stays
/// with each computer"): a speaking route already saved on a host's engine wins over the file.</summary>
public static class SpeakingEngineFile
{
    public const string FileName = "speaking-engine.txt";

    /// <summary>The saved engine's key, or null when there is none or it can't be read.</summary>
    public static string? ReadKey(string directory)
    {
        try { return File.ReadAllText(Path.Combine(directory, FileName)).Trim(); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>The saved engine, or null when there is none or it names an unknown engine.</summary>
    public static SpeechEngine? Read(string directory) => SpeechEngines.ForKey(ReadKey(directory));

    public static void Save(string directory, SpeechEngine engine)
    {
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, FileName), engine.Key);
    }
}
