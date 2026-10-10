namespace Martlet.Audio;

/// <summary>Lines about audio devices for the local log, for example device notices Martlet chose not to act on. Never a
/// device name or ID, and nothing heard.</summary>
public static class AudioDiagnostics
{
    private static Action<string>? sink;

    /// <summary>Routes diagnostic lines to <paramref name="value"/> (null turns them off). It must not block: lines come from
    /// the capture's own thread. A throwing sink is ignored.</summary>
    public static void SetSink(Action<string>? value) => Volatile.Write(ref sink, value);

    internal static void Note(string line)
    {
        if (Volatile.Read(ref sink) is not { } write) return;
        try { write(line); }
        catch (Exception) { }
    }
}
