namespace Martlet.Desktop;

/// <summary>What Home's Get started card needs to know: whether each of the three steps of the main flow is done, and what each
/// step says.</summary>
/// <param name="ServersReady">Thinking, Listening and Voice are set up and nothing stops Martlet replying.</param>
/// <param name="Servers">What does each job now, such as "Thinking: Ollama on this PC · Listening: not set up".</param>
/// <param name="CharacterChosen">At least one character profile exists (a personality with its look and voice).</param>
/// <param name="Character">Who Martlet is now, such as "Martlet is Martlet now."</param>
/// <param name="Talked">A conversation was had: one is open or saved.</param>
/// <param name="CanFindCharacter">Thinking can reply, so Find a character can write a personality.</param>
internal sealed record GetStartedState(bool ServersReady, string Servers, bool CharacterChosen, string Character, bool Talked, bool CanFindCharacter);

/// <summary>What Get started's buttons do.</summary>
internal sealed record GetStartedActions(Action RecommendedSetup, Action AddComputer, Action Profiles, Action FindCharacter, Action Look,
    Action Voice, Action Talk, Action Listen);

/// <summary>Home's Get started card (docs/UI_DESIGN.md, "Get started"): the main flow in three numbered steps. 1. Set up where
/// Martlet runs, 2. choose who Martlet is (personality, look and voice), 3. talk. Pure, so tests read it without a window.</summary>
internal static class GetStarted
{
    internal const string Servers = "GetStartedServers", Character = "GetStartedCharacter", Talk = "GetStartedTalk";

    /// <summary>Whether all three steps are done, so the card hides.</summary>
    internal static bool Done(GetStartedState state) => state.ServersReady && state.CharacterChosen && state.Talked;

    /// <summary>The card's line under its heading.</summary>
    internal static string Summary(GetStartedState state)
    {
        var done = new[] { state.ServersReady, state.CharacterChosen, state.Talked }.Count(d => d);
        return done switch
        {
            0 => "Three steps and Martlet is ready to talk with you.",
            3 => "All done.",
            _ => $"{done} of 3 done."
        };
    }

    internal static IReadOnlyList<HomeStep> Steps(GetStartedState state, GetStartedActions actions) =>
    [
        new(Servers, "Set up where Martlet runs",
            state.ServersReady ? state.Servers : "Choose the computers and services that think, listen and speak. " + state.Servers,
            state.ServersReady, false,
            [new("Recommended setup", actions.RecommendedSetup, Primary: true, Id: "RecommendedSetup"), new("Add a computer", actions.AddComputer, Id: "AddComputer")]),
        new(Character, "Choose who Martlet is",
            state.CharacterChosen ? state.Character : ("Pick or make a character: its personality, its look and its voice. " + state.Character).TrimEnd(),
            state.CharacterChosen, false,
            [
                new("Choose a character", actions.Profiles, Primary: true, Id: "Profiles"),
                .. state.CanFindCharacter ? [new StepCommand("Find a character", actions.FindCharacter, Id: "Find")] : Array.Empty<StepCommand>(),
                new("Look", actions.Look, Id: "Look"),
                new("Voice", actions.Voice, Id: "Voice")
            ]),
        new(Talk, "Talk with Martlet",
            state.Talked ? "You have talked with Martlet." : "Start talking by text or voice, or start listening to talk hands-free.",
            state.Talked, false,
            [new("Start talking", actions.Talk, Primary: true, Id: "Talk"), new("Start listening", actions.Listen, Id: "Listen")])
    ];
}
