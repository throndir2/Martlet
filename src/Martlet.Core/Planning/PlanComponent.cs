namespace Martlet.Core.Planning;

/// <summary>A job Martlet needs done somewhere in the user's network or by a hosted provider. The order of the values is
/// the default priority (<see cref="ComponentRanking"/>); see docs/RECOMMENDED_SETUPS.md#the-priority-list.</summary>
public enum PlanComponent { Thinking, Voice, Listening, Character, LipSync, Vision, Reading, Hearing, DeepThinking, SmartHome, Singing, Pictures }

/// <summary>Required: Martlet cannot converse without it, so it always gets at least a baseline (hosted counts).
/// Core: the voice conversation needs it; it always gets at least its processor baseline. Optional: dropped when nothing has room.</summary>
public enum ComponentNecessity { Required, Core, Optional }

public sealed record ComponentInfo(PlanComponent Component, int Rank, ComponentNecessity Necessity, string Name, string Why);

/// <summary>The priority list: the order of Martlet's components, the single source of truth the placement engine, the
/// network recommender (Home's Recommended setup and the order Reconfigure sets things up in), the setup advisor, the wizard
/// and the Devices view use. Rank 1 is the most important. docs/RECOMMENDED_SETUPS.md#the-priority-list explains it.</summary>
public static class ComponentRanking
{
    public static IReadOnlyList<ComponentInfo> All { get; } =
    [
        new(PlanComponent.Thinking, 1, ComponentNecessity.Required, "Thinking",
            "Writes every reply. Without it Martlet cannot talk, but a hosted endpoint can do it, so it does not always need local hardware."),
        new(PlanComponent.Voice, 2, ComponentNecessity.Core, "Voice",
            "Speaks every reply. It cannot be offloaded for free, so it gets the first claim on a local NVIDIA card."),
        new(PlanComponent.Listening, 3, ComponentNecessity.Core, "Listening",
            "Turns speech into text. Parakeet on the processor is fast and accurate, so the graphics card is only an upgrade."),
        new(PlanComponent.Character, 4, ComponentNecessity.Core, "Character",
            "Draws the desktop character on the PC you talk to. It needs very little."),
        new(PlanComponent.LipSync, 5, ComponentNecessity.Core, "Lip-sync",
            "Moves the character's face with the voice. Loudness lip-sync always works; advanced lip-sync needs an NVIDIA card."),
        new(PlanComponent.Vision, 6, ComponentNecessity.Optional, "Vision",
            "Looks at your screen or a camera while you watch together. Thinking's own model sees by default, so it often needs nothing more; an image model of its own needs a graphics card or an online provider."),
        new(PlanComponent.Reading, 7, ComponentNecessity.Optional, "Reading",
            "Reads the small text on your screen exactly while Martlet watches: Windows OCR inside Martlet, or the Reading role (RapidOCR or PP-OCRv5)."),
        new(PlanComponent.Hearing, 8, ComponentNecessity.Optional, "Hearing",
            "Hears how you say things: tone, laughs and sighs. Thinking's own model hears by default; an audio model of its own needs a graphics card or an online provider."),
        new(PlanComponent.DeepThinking, 9, ComponentNecessity.Optional, "Deep thinking",
            "Thinks things over in the background with a bigger model. Nice to have, never on the reply path."),
        new(PlanComponent.SmartHome, 10, ComponentNecessity.Optional, "Smart home",
            "Controls your lights and devices through Home Assistant. Home Assistant runs on a processor, always on, on a Linux computer or your own hub."),
        new(PlanComponent.Singing, 11, ComponentNecessity.Optional, "Singing", "Sings songs on request. Needs a large NVIDIA card."),
        new(PlanComponent.Pictures, 12, ComponentNecessity.Optional, "Pictures", "Draws pictures on request. Needs a large NVIDIA card.")
    ];

    public static ComponentInfo Of(PlanComponent component) => All.First(info => info.Component == component);

    public static string Name(PlanComponent component) => Of(component).Name;

    /// <summary>The parts the owner can turn off: advanced lip-sync (the character's face then follows the voice's loudness)
    /// and every optional extra. Thinking, the voice, listening and the character are what a conversation needs.</summary>
    public static bool CanBeOff(PlanComponent component) =>
        component == PlanComponent.LipSync || Of(component).Necessity == ComponentNecessity.Optional;

    /// <summary>What a part being off means, in words.</summary>
    public static string OffMeans(PlanComponent component) => component switch
    {
        PlanComponent.LipSync => "the character's face follows the voice's loudness",
        PlanComponent.Vision => "Martlet doesn't look at your screen or camera",
        PlanComponent.Reading => "Martlet doesn't read the text on your screen",
        PlanComponent.Hearing => "Martlet gets only the words you say, not how you say them",
        PlanComponent.DeepThinking => "Martlet doesn't think things over in the background",
        PlanComponent.SmartHome => "Martlet doesn't control your smart home",
        PlanComponent.Singing => "Martlet doesn't sing",
        PlanComponent.Pictures => "Martlet doesn't draw pictures",
        _ => "it doesn't run"
    };

    /// <summary>The parts that may use a companion PC's graphics card: Thinking, then the voice. A companion PC often runs
    /// games, so everything else runs there on the processor, or is off. Vision and Hearing use a companion PC's card only
    /// as Thinking itself (an image or audio option that <see cref="ComponentOption.UsesThinking"/>, which takes nothing more);
    /// Reading and Smart home run on a processor (Reading with PP-OCRv5 server on a graphics card only when the owner chooses it).</summary>
    public static bool UsesCompanionCard(PlanComponent component) => component is PlanComponent.Thinking or PlanComponent.Voice;

    /// <summary>The parts this PC chooses on their own Companion page and that the network review doesn't place or turn off:
    /// Vision's image model and Hearing's audio model (sense-models.json), Reading (reading.json) and Smart home (the Home
    /// Assistant connection). The review shows where each runs; its page turns it on or off.</summary>
    public static bool SetOnPage(PlanComponent component) =>
        component is PlanComponent.Vision or PlanComponent.Reading or PlanComponent.Hearing or PlanComponent.SmartHome;

    /// <summary>Where the owner sets a part up in the app ("Companion › Vision").</summary>
    public static string Page(PlanComponent component) => "Companion \u203a " + component switch
    {
        PlanComponent.LipSync => "Lip-sync",
        PlanComponent.DeepThinking => "Thinking pool",
        _ => Name(component)
    };

    /// <summary>The order the engine hands out local resources for <paramref name="preference"/>. Voice first because only
    /// it cannot be offloaded for free; Thinking's local model comes after advanced lip-sync when a hosted endpoint can cover
    /// it, and first when the user wants everything local or online services only as a backup.</summary>
    public static IReadOnlyList<PlanStep> ClaimOrder(HostingPreference preference) => HostingRules.LiveLocal(preference)
        ?
        [
            PlanStep.Character, PlanStep.ThinkingPrimary, PlanStep.Voice, PlanStep.Listening, PlanStep.LipSync,
            PlanStep.ListeningUpgrade, PlanStep.Vision, PlanStep.Reading, PlanStep.Hearing, PlanStep.DeepThinking, PlanStep.SmartHome,
            PlanStep.Singing, PlanStep.Pictures
        ]
        :
        [
            PlanStep.Character, PlanStep.Voice, PlanStep.Listening, PlanStep.LipSync, PlanStep.ThinkingPrimary,
            PlanStep.ThinkingFallback, PlanStep.ListeningUpgrade, PlanStep.Vision, PlanStep.Reading, PlanStep.Hearing,
            PlanStep.DeepThinking, PlanStep.SmartHome, PlanStep.Singing, PlanStep.Pictures
        ];
}

/// <summary>One step of the engine's claim order (<see cref="ComponentRanking.ClaimOrder"/>).</summary>
public enum PlanStep
{
    Character, ThinkingPrimary, ThinkingFallback, Voice, Listening, ListeningUpgrade, LipSync, Vision, Reading, Hearing, DeepThinking,
    SmartHome, Singing, Pictures
}
