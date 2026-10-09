using Martlet.Core.Settings;

namespace Martlet.Core.Installation;

/// <summary>Whether a voice engine shares a Windows computer's graphics card with other work, and the warning Martlet shows
/// for it. On Windows (Docker Desktop's WSL 2 machine or natively) the NVIDIA driver shares the card between everything that
/// uses it and, when its memory runs short, quietly moves part of it into main memory instead of failing, where a Linux host
/// reports the shortage. Measured on an RTX 4070 under Docker Desktop (docs/CHATTERBOX_VOICE.md#sharing-the-graphics-card):
/// overfilling the card failed nothing, the other programs' memory was moved out, and the voice's next reply started 0.6 s
/// later than usual; in use, a first reply once waited 51 s.</summary>
public static class SharedGpu
{
    /// <summary>The host roles other than voice engines, by kind and name; each uses the host's graphics card when it has an
    /// NVIDIA one, which a voice engine needs (Thinking, Deep thinking and Listening run on the processor without one; Reading
    /// only with PP-OCRv5, see <see cref="ProcessorOnly"/>).</summary>
    public static readonly IReadOnlyList<(string Kind, string Name)> OtherGpuRoles =
    [
        ("audio2face", "Lip-sync"), ("ollama", "Thinking"), ("deep-thinking", "Thinking pool"), ("stt", "Listening"),
        ("singing", "Singing"), ("pictures", "Pictures"), ("ocr", "Reading")
    ];

    /// <summary>Whether host role <paramref name="kind"/> running <paramref name="model"/> never uses the graphics card, so it
    /// never shares it with a voice engine: Reading with RapidOCR (its PP-OCRv4 models run on the processor). Reading with
    /// PP-OCRv5 ("ppocrv5-mobile" or "ppocrv5-server") can run on an NVIDIA card.</summary>
    public static bool ProcessorOnly(string kind, string? model) =>
        kind == "ocr" && model?.StartsWith("ppocrv5-", StringComparison.Ordinal) != true;

    /// <summary>Whether a host runs its roles on Windows: this PC's host service (Docker Desktop), or a host whose report says
    /// Windows or a WSL 2 kernel ("5.15.167.4-microsoft-standard-WSL2").</summary>
    public static bool OnWindows(bool thisPc, HostHardware? report) =>
        thisPc || string.Equals(report?.Platform, "windows", StringComparison.OrdinalIgnoreCase) ||
        report?.Kernel?.Contains("microsoft", StringComparison.OrdinalIgnoreCase) == true;

    /// <summary>The name of the voice engine among <paramref name="roleKinds"/> (a host runs one at a time), or null.</summary>
    public static string? VoiceName(IEnumerable<string>? roleKinds) =>
        roleKinds?.Select(SpeechEngines.ForRoleKind).FirstOrDefault(engine => engine is not null)?.Name;

    /// <summary>What else uses the card beside the voice engine, in words: the other GPU roles among
    /// <paramref name="roleKinds"/> (other voice engines have their own warning; a role that runs only on the processor with
    /// the model <paramref name="models"/> names for it is left out) and Thinking in Ollama when the host is this PC and
    /// Thinking runs there.</summary>
    public static IReadOnlyList<string> Neighbours(IEnumerable<string>? roleKinds, bool thinkingInOllamaHere,
        IReadOnlyDictionary<string, string>? models = null)
    {
        var kinds = new HashSet<string>(roleKinds ?? [], StringComparer.Ordinal);
        var names = OtherGpuRoles
            .Where(role => kinds.Contains(role.Kind) && !ProcessorOnly(role.Kind, models?.GetValueOrDefault(role.Kind)))
            .Select(role => role.Name).ToList();
        if (thinkingInOllamaHere) names.Add("Thinking in Ollama");
        return names;
    }

    /// <summary>The warning for a voice engine (<paramref name="voice"/>, its name; null before one is set up there) that shares
    /// a Windows host's graphics card with <paramref name="others"/>, or null when the host isn't on Windows or nothing else
    /// uses its card.</summary>
    public static string? Warning(string where, bool onWindows, string? voice, IReadOnlyList<string> others)
    {
        if (!onWindows || others.Count == 0) return null;
        const string risk = "When the card's memory runs short, Windows quietly moves part of it into main memory instead of " +
            "failing, and the voice can then start seconds late or pause mid-sentence.";
        var list = others.Count == 1 ? others[0] : string.Join(", ", others.Take(others.Count - 1)) + " and " + others[^1];
        return voice is null
            ? $"{where} runs on Windows and already uses its graphics card for {list}. A voice engine there would share it. {risk}"
            : $"{where} runs on Windows, so {voice} shares its graphics card with {list}. {risk} For a steady voice, give it " +
              $"a card of its own: move {(others.Count == 1 ? "that job" : "those jobs")} or the voice to another computer.";
    }
}
