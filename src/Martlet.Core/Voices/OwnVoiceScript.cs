namespace Martlet.Core.Voices;

/// <summary>The lines the owner reads out loud in Companion › Voice › Voices › Record your own voice. Martlet wrote them.
/// Each line is one recording of about 4 to 7 seconds at a normal pace, inside the 3 to 10 seconds GPT-SoVITS takes, and
/// together, with the short pauses between them, they stay inside the 30 seconds a voice may last. Between them they hold
/// every consonant of spoken English (the "th" of thick and of the, "sh", "ch", "j", "ng", "zh" as in measure, "w", "y" and
/// "h" included) and its vowels and glides (beach, ship, sailed, measure, past, Martlet, morning, cocoa, would, June, cup,
/// learn, my, how, toy). They are said in different ways (calm, describing, asking, excited), so the copied voice also hears
/// how its owner's voice rises and falls.</summary>
public static class OwnVoiceScript
{
    public static IReadOnlyList<string> Lines { get; } =
    [
        "Hello, I'm recording my own voice so Martlet can learn how I usually sound.",
        "On a chilly June morning, the ship sailed past the beach through thick fog.",
        "Would you enjoy a quiet cup of cocoa while we measure the treasure?",
        "Wow, that was amazing! Zoe's puppy jumped up and chewed the purple toy."
    ];
}
