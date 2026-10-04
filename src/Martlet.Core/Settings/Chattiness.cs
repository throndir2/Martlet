namespace Martlet.Core.Settings;

/// <summary>How chatty Martlet is about what goes on in the background: what it sees (vision's screen and camera glances) and
/// what this PC plays. Quiet speaks up only when something is clearly remarkable, Normal when it is worth saying, Chatty
/// reacts more often.</summary>
public enum Chattiness { Quiet, Normal, Chatty }

/// <summary>Companion › Vision › How often it comments (also Listening › Watch along): one of the levels, or Martlet decides,
/// which lets the Thinking model pick the level itself and switch it as things happen (<see cref="ChattinessTags"/>). Saved
/// as its number in talk-preferences.json's ScreenChattiness.</summary>
public enum ChattinessChoice { Quiet, Normal, Chatty, MartletDecides }

/// <summary>The control tags a reply switches the level with while Martlet decides how chatty it is, such as
/// <c>[chattiness:quiet]</c>. They are never shown or spoken (<see cref="VoiceTagKind.Control"/>); the reply's last one
/// wins.</summary>
public static class ChattinessTags
{
    /// <summary>The level's lower-case name, as the prompts say it: quiet, normal or chatty.</summary>
    public static string Name(Chattiness level) => level switch
    {
        Chattiness.Quiet => "quiet",
        Chattiness.Chatty => "chatty",
        _ => "normal"
    };

    /// <summary>The tag the Thinking model is told to write for <paramref name="level"/>.</summary>
    public static string Tag(Chattiness level) => $"[chattiness:{Name(level)}]";

    /// <summary>Every spelling Martlet recognizes (with or without a space after the colon; matching ignores case).</summary>
    public static IReadOnlyList<string> All { get; } =
        [.. Enum.GetValues<Chattiness>().SelectMany(level => new[] { Tag(level), $"[chattiness: {Name(level)}]" })];

    /// <summary>The level <paramref name="tag"/> switches to, or null when it isn't a chattiness tag.</summary>
    public static Chattiness? Of(string? tag)
    {
        if (tag is null) return null;
        foreach (var level in Enum.GetValues<Chattiness>())
            if (string.Equals(tag, Tag(level), StringComparison.OrdinalIgnoreCase) ||
                string.Equals(tag, $"[chattiness: {Name(level)}]", StringComparison.OrdinalIgnoreCase))
                return level;
        return null;
    }

    /// <summary>The level the last chattiness tag among <paramref name="tags"/> switches to, or null when there is none.</summary>
    public static Chattiness? Last(IEnumerable<string> tags)
    {
        Chattiness? last = null;
        foreach (var tag in tags)
            if (Of(tag) is { } level) last = level;
        return last;
    }

    /// <summary>The saved choice (talk-preferences.json's ScreenChattiness), Normal when it is out of range.</summary>
    public static ChattinessChoice Choice(int saved) =>
        Enum.IsDefined((ChattinessChoice)saved) ? (ChattinessChoice)saved : ChattinessChoice.Normal;

    /// <summary>The level a choice sets: its own, or <paramref name="decided"/> while Martlet decides.</summary>
    public static Chattiness Level(ChattinessChoice choice, Chattiness decided) =>
        choice == ChattinessChoice.MartletDecides ? decided : (Chattiness)choice;

    /// <summary>How the choice reads in Martlet: Quiet, Normal, Chatty or Martlet decides.</summary>
    public static string Label(ChattinessChoice choice) => choice == ChattinessChoice.MartletDecides ? "Martlet decides" : choice.ToString();

    /// <summary>What replies and looks are told while Martlet decides (Companion › Prompts › Chattiness: Martlet decides): the
    /// levels, when to switch and the tags that switch them; <paramref name="silent"/> is the word for staying quiet. Null when
    /// the owner emptied it.</summary>
    public static string? Instructions(PromptSettings? prompts, string silent) =>
        PromptSettings.Fill(prompts, PromptCatalog.ChattinessDecides, ("silent", silent),
            ("quiet", Tag(Chattiness.Quiet)), ("normal", Tag(Chattiness.Normal)), ("chatty", Tag(Chattiness.Chatty)));

    /// <summary>The note that says the level while Martlet decides (Companion › Prompts › Chattiness right now), or null when the
    /// owner emptied it.</summary>
    public static string? Note(PromptSettings? prompts, Chattiness level) =>
        PromptSettings.Fill(prompts, PromptCatalog.ChattinessNow, ("level", Name(level)));
}
