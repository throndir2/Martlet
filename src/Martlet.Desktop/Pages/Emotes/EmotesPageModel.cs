using System.Globalization;
using Martlet.Avatar.Hosting;
using Martlet.Conversation;
using Martlet.Core.Settings;

namespace Martlet.Desktop;

/// <summary>Companion › Emotes and motions' words, without WPF: how many emotes and motions the model has and who named them, what
/// replies are offered, the lingering emotes on now, each row's kind, the combos' line and the body motions' line.
/// <see cref="EmotesPage"/> draws them; the Now line's words are <see cref="MainWindow.EmotesNow"/>.</summary>
internal static class EmotesPageModel
{
    internal const string NoCue = "(none)";

    /// <summary>The Now line's counts: the model's emotes, motions and Martlet gestures, how many of each are on, and the combos
    /// that are on.</summary>
    internal static string Now(CharacterActionCatalog catalog)
    {
        int Count(CharacterActionKind kind, bool on) => catalog.Entries.Count(e => e.Source.Kind == kind && (!on || e.Action.Enabled));
        return MainWindow.EmotesNow(Count(CharacterActionKind.Expression, true), Count(CharacterActionKind.Expression, false),
            Count(CharacterActionKind.Motion, true), Count(CharacterActionKind.Motion, false), catalog.Combos.Count(c => c.Enabled),
            Count(CharacterActionKind.Gesture, true), Count(CharacterActionKind.Gesture, false));
    }

    /// <summary>The card's status line (CharacterActionsStatus): why the model's emotes couldn't be read, that they are being read,
    /// or how many there are and who named them.</summary>
    internal static string Status(string? problem, CharacterActionCatalog? catalog) =>
        problem is not null ? "Martlet couldn't read this model's emotes and motions: " + problem
        : catalog is null ? "Reading the character's emotes and motions..."
        : Counts(catalog.Inventory) + (catalog.Settings.DetectedBy == CharacterActionSettings.ByThinking && catalog.Settings.DetectedAt is { } at
            ? $"; named by the Thinking model on {at.ToLocalTime().ToString("g", CultureInfo.CurrentCulture)}."
            : "; named from the model's own files.");

    internal static string Counts(CharacterActionInventory inventory)
    {
        var emotes = inventory.Sources.Count(s => s.Kind == CharacterActionKind.Expression);
        var motions = inventory.Sources.Count(s => s.Kind == CharacterActionKind.Motion);
        return $"{emotes} emote{(emotes == 1 ? "" : "s")}, {motions} motion{(motions == 1 ? "" : "s")} and Martlet's nod and shake.";
    }

    /// <summary>What replies are offered (with the voice chosen now, <paramref name="engine"/>) and what follows the voice's cues, in
    /// words.</summary>
    internal static string Offered(CharacterActionCatalog catalog, SpeechEngine? engine)
    {
        var tags = catalog.Offered(engine).Select(e => "{" + e.Action.Tag + "}").ToArray();
        var linked = catalog.Entries.Where(e => e.Action is { Enabled: true, Cue: not null }).Select(e =>
            $"{(engine?.Tags.FirstOrDefault(t => t.Cue == e.Action.Cue)?.Text ?? e.Action.Cue)} plays {e.Source.Name}").ToArray();
        return (tags.Length == 0 ? "Replies aren't offered any tags" : $"Replies can use {tags.Length}: {string.Join(" ", tags)}") +
            (linked.Length == 0 ? "." : engine?.SupportsTags == true
                ? $", following {engine.Name}'s voice: {string.Join("; ", linked)}."
                : $", linked to voice cues (with a voice that makes sounds and tones): {string.Join("; ", linked)}.");
    }

    /// <summary>The lingering emotes the character shows now, in words.</summary>
    internal static string HeldText(IReadOnlyList<HeldEmote> held, DateTimeOffset now) =>
        held.Count == 0 ? "No lingering emotes are on." : "On now: " + string.Join(", ", held.Select(h =>
            $"{h.Source.Name} ({CharacterActions.Age(now - h.Since)})")) + ". Clear emotes on the character's menu turns them off.";

    /// <summary>A row's kind, after its name.</summary>
    internal static string Kind(CharacterActionSource source) => source.Kind switch
    {
        CharacterActionKind.Expression => "emote",
        CharacterActionKind.Motion when CharacterActionInventory.IsKeptMotion(source.Id) => "kept motion",
        CharacterActionKind.Motion => "motion",
        _ => "Martlet gesture"
    };

    /// <summary>How many combos the model has and which tags replies get (CharacterCombosStatus).</summary>
    internal static string CombosStatus(CharacterActionCatalog catalog)
    {
        var combos = catalog.Combos;
        if (combos.Count == 0) return "No combos yet.";
        var offered = catalog.OfferedCombos().Select(c => "{" + c.Tag + "}").ToArray();
        return $"{combos.Count} combo{(combos.Count == 1 ? "" : "s")}, " + (offered.Length == 0
            ? "but replies aren't offered any: a combo needs to be on, with a part that is on."
            : $"and replies can use {offered.Length}: {string.Join(" ", offered)}.");
    }

    /// <summary>The body motions' line (CharacterMotionStatus): what motions need while they aren't set up, else how many are
    /// kept.</summary>
    internal static string MotionsStatus(CharacterMotions motions)
    {
        if (!motions.IsSetUp)
            return "Body motions: a computer with the motion role (an NVIDIA GPU with about 24 GB) in the Motion list (Companion › " +
                "Motion) lets Martlet make new body motions for VRM characters and keep them here.";
        var kept = motions.Kept().Count;
        return (motions.ChosenVrm ? "Body motions: Martlet makes new ones in conversation when asked and keeps them; "
                : "Body motions: replies make new ones while the chosen character is a VRM (Companion › Character); ") +
            $"{kept} kept motion{(kept == 1 ? " is" : "s are")} in the list below." + (motions.Starters is { } starters ? " " + starters : "");
    }
}
