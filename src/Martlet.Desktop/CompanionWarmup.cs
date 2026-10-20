using System.Globalization;
using Martlet.Providers.Ollama;

namespace Martlet.Desktop;

/// <summary>A part that loads before Martlet answers at full speed on this PC: the conversation's settings, Thinking's model in
/// Ollama on this PC, Parakeet for listening, or the character.</summary>
internal enum WarmupPart { Conversation, Thinking, Listening, Character }

/// <summary>A part that is still loading: what it is (a model's name), how long it has taken so far, and how long the same model
/// took the last time it loaded (null when Martlet doesn't know).</summary>
internal sealed record WarmupItem(WarmupPart Part, string? Name, TimeSpan Elapsed, TimeSpan? LastTime = null);

/// <summary>What Martlet holds on this PC now, for Home's Martlet on this PC card: the models Ollama on this PC has loaded (with
/// their memory), Parakeet in memory for listening, the voice-recognition engine, the character and the conversation (and
/// whether it listens or watches).</summary>
internal sealed record PcHoldings(IReadOnlyList<OllamaLoadedModel> Models, string? Listening, bool VoiceRecognition, bool Character,
    bool Conversation, bool Hearing, bool Watching)
{
    internal static PcHoldings Nothing { get; } = new([], null, false, false, false, false, false);
    internal bool Any => Models.Count > 0 || Listening is not null || VoiceRecognition || Character || Conversation;
}

/// <summary>What Home's Free up this PC did: the conversation ended, the character hidden, the models taken out of memory (their
/// names), the graphics and system memory Ollama said those models held, and what didn't work.</summary>
internal sealed record FreeUpResult(bool EndedConversation, bool HidCharacter, IReadOnlyList<string> Unloaded, long GraphicsBytes,
    long MemoryBytes, IReadOnlyList<string> Problems)
{
    internal bool AnyDone => EndedConversation || HidCharacter || Unloaded.Count > 0;
}

/// <summary>The words Home and the talk window use for warming up (the parts that still load before Martlet answers at full
/// speed) and for Home's Martlet on this PC card (what Martlet holds on this PC, and Free up this PC).</summary>
internal static class CompanionWarmup
{
    /// <summary>A part that loads in less time than this doesn't show, so a model that is already loaded never flashes a
    /// warming-up line.</summary>
    internal static TimeSpan ShowAfter => TimeSpan.FromSeconds(1);

    /// <summary>How long "Warmed up in 18 s." stays on Home after warming up ends.</summary>
    internal static TimeSpan DoneFor => TimeSpan.FromSeconds(8);

    /// <summary>The parts worth showing now: those that have loaded for <see cref="ShowAfter"/> or more.</summary>
    internal static IReadOnlyList<WarmupItem> Showing(IEnumerable<WarmupItem> items) => [.. items.Where(i => i.Elapsed >= ShowAfter)];

    /// <summary>One line for what still loads: "Warming up: loading gemma4:e4b on this PC (12 s of about 20 s) and Parakeet TDT
    /// 0.6B v3 for listening (3 s so far)." Without <paramref name="lead"/> (under the talk window's "Warming up" title) it
    /// starts at "Loading ...".</summary>
    internal static string Line(IReadOnlyList<WarmupItem> items, bool lead = true)
    {
        if (items.Count == 0) return "";
        var text = Join([.. items.Select(Phrase)]) + ".";
        return lead ? "Warming up: " + text : char.ToUpperInvariant(text[0]) + text[1..];
    }

    /// <summary>What warming up means for what you do now, after <see cref="Line"/>.</summary>
    internal static string Advice(IReadOnlyList<WarmupItem> items, bool talkWindow) =>
        items.Any(i => i.Part == WarmupPart.Thinking)
            ? talkWindow ? "You can talk or type now; the first reply starts once the model has loaded."
                : "Martlet answers once the model has loaded."
            : items.Any(i => i.Part == WarmupPart.Listening)
                ? "Martlet hears you once it has loaded." : "Martlet is almost ready.";

    /// <summary>How far warming up is, from 0 to 0.95, when the Thinking model's last load time is known; null otherwise (the
    /// progress bar then only shows that something happens).</summary>
    internal static double? Progress(IReadOnlyList<WarmupItem> items) =>
        items.FirstOrDefault(i => i.Part == WarmupPart.Thinking && i.LastTime is { TotalSeconds: > 0 }) is { } thinking
            ? Math.Clamp(thinking.Elapsed / thinking.LastTime!.Value, 0, 0.95) : null;

    /// <summary>The line after warming up ends: "Warmed up in 18 s, and Martlet is ready to talk."</summary>
    internal static string Done(TimeSpan took) => $"Warmed up in {Seconds(took)} s, and Martlet is ready to talk.";

    private static string Phrase(WarmupItem item) => item.Part switch
    {
        WarmupPart.Conversation => "getting the conversation ready",
        WarmupPart.Thinking => $"loading {item.Name ?? "the Thinking model"} on this PC{Timing(item)}",
        WarmupPart.Listening => $"loading {item.Name ?? "Parakeet"} for listening{Timing(item)}",
        _ => "starting the character"
    };

    private static string Timing(WarmupItem item) =>
        item.LastTime is { TotalSeconds: >= 1 } last && last > item.Elapsed ? $" ({Seconds(item.Elapsed)} s of about {Seconds(last)} s)"
        : $" ({Seconds(item.Elapsed)} s so far)";

    // ---------- Martlet on this PC ----------

    /// <summary>The card's line about what Martlet holds on this PC now.</summary>
    internal static string HoldingsLine(PcHoldings held)
    {
        if (!held.Any) return "Martlet holds no models on this PC now, so the character and the conversation are off.";
        var parts = new List<string>();
        parts.AddRange(held.Models.Select(m => $"{m.Name} ({ModelMemory(m)})"));
        if (held.Listening is { } listening) parts.Add($"{listening} for listening");
        if (held.VoiceRecognition) parts.Add("voice recognition");
        if (held.Character) parts.Add("the character");
        if (held.Conversation)
            parts.Add("the conversation" + (held.Hearing && held.Watching ? " (listening and watching)" : held.Hearing ? " (listening)"
                : held.Watching ? " (watching)" : ""));
        return "Martlet uses this PC for " + Join(parts) + ".";
    }

    /// <summary>What Free up this PC did, for the card while Martlet rests and for the status line under Home.</summary>
    internal static string FreedLine(FreeUpResult result)
    {
        var done = new List<string>();
        if (result.EndedConversation) done.Add("ended the conversation");
        if (result.HidCharacter) done.Add("hid the character");
        if (result.Unloaded.Count > 0)
            done.Add("unloaded " + Join([.. result.Unloaded]) +
                (result.GraphicsBytes + result.MemoryBytes > 0 ? $" ({Memory(result.GraphicsBytes, result.MemoryBytes)})" : ""));
        var text = done.Count == 0 ? "Martlet held nothing to free on this PC." : "Martlet " + Join(done) + ".";
        foreach (var problem in result.Problems) text += " " + problem;
        return text;
    }

    /// <summary>The card's line while Martlet rests after Free up this PC.</summary>
    internal static string RestingLine(FreeUpResult result) =>
        FreedLine(result) + " Start talking or Start listening warms it up again.";

    /// <summary>A loaded model's graphics memory and the part of it Ollama keeps in system memory instead.</summary>
    internal static (long Graphics, long Memory) Split(OllamaLoadedModel model) =>
        model.OnGraphicsCard ? (model.Size, 0) : (Math.Max(0, model.SizeVram), model.Size - Math.Max(0, model.SizeVram));

    private static string ModelMemory(OllamaLoadedModel model)
    {
        var (graphics, memory) = Split(model);
        return Memory(graphics, memory);
    }

    private static string Memory(long graphics, long memory) => (graphics > 0, memory > 0) switch
    {
        (true, true) => $"{Gb(graphics)} GB of graphics memory and {Gb(memory)} GB of memory",
        (true, false) => $"{Gb(graphics)} GB of graphics memory",
        _ => $"{Gb(memory)} GB of memory"
    };

    /// <summary>Gigabytes (10^9 bytes, as Ollama reports them) with one decimal.</summary>
    private static string Gb(long bytes) => (bytes / 1e9).ToString("0.0", CultureInfo.InvariantCulture);

    private static string Seconds(TimeSpan time) => Math.Max(0, (int)time.TotalSeconds).ToString(CultureInfo.InvariantCulture);

    private static string Join(IReadOnlyList<string> parts) => parts.Count switch
    {
        0 => "",
        1 => parts[0],
        _ => string.Join(", ", parts.Take(parts.Count - 1)) + " and " + parts[^1]
    };
}
