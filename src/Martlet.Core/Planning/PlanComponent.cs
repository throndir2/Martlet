namespace Martlet.Core.Planning;

/// <summary>A job Martlet needs done somewhere in the user's network or by a hosted provider. The order of the values is
/// the default priority (<see cref="ComponentRanking"/>); see docs/RECOMMENDATIONS.md.</summary>
public enum PlanComponent { Thinking, Voice, Listening, Character, LipSync, DeepThinking, Singing, Pictures }

/// <summary>Required: Martlet cannot converse without it, so it always gets at least a baseline (hosted counts).
/// Core: the voice conversation needs it; it always gets at least its processor baseline. Optional: dropped when nothing has room.</summary>
public enum ComponentNecessity { Required, Core, Optional }

public sealed record ComponentInfo(PlanComponent Component, int Rank, ComponentNecessity Necessity, string Name, string Why);

/// <summary>The priority ranking of components: the single source of truth the placement engine, the setup advisor, the
/// wizard and the Devices view use. Rank 1 is the most important.</summary>
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
        new(PlanComponent.DeepThinking, 6, ComponentNecessity.Optional, "Deep thinking",
            "Thinks things over in the background with a bigger model. Nice to have, never on the reply path."),
        new(PlanComponent.Singing, 7, ComponentNecessity.Optional, "Singing", "Sings songs on request. Needs a large NVIDIA card."),
        new(PlanComponent.Pictures, 8, ComponentNecessity.Optional, "Pictures", "Draws pictures on request. Needs a large NVIDIA card.")
    ];

    public static ComponentInfo Of(PlanComponent component) => All.First(info => info.Component == component);

    public static string Name(PlanComponent component) => Of(component).Name;

    /// <summary>The order the engine hands out local resources for <paramref name="preference"/>. Voice first because only
    /// it cannot be offloaded for free; Thinking's local model comes after advanced lip-sync when a hosted endpoint can cover
    /// it, and first when the user wants everything local.</summary>
    public static IReadOnlyList<PlanStep> ClaimOrder(HostingPreference preference, bool thinkingFirst = false) =>
        preference == HostingPreference.PreferLocal
        ?
        [
            PlanStep.Character, PlanStep.ThinkingPrimary, PlanStep.Voice, PlanStep.Listening, PlanStep.LipSync,
            PlanStep.ListeningUpgrade, PlanStep.DeepThinking, PlanStep.Singing, PlanStep.Pictures
        ]
        : thinkingFirst
        ?
        [
            PlanStep.Character, PlanStep.ThinkingPrimary, PlanStep.Voice, PlanStep.Listening, PlanStep.LipSync,
            PlanStep.ThinkingFallback, PlanStep.ListeningUpgrade, PlanStep.DeepThinking, PlanStep.Singing, PlanStep.Pictures
        ]
        :
        [
            PlanStep.Character, PlanStep.Voice, PlanStep.Listening, PlanStep.LipSync, PlanStep.ThinkingPrimary,
            PlanStep.ThinkingFallback, PlanStep.ListeningUpgrade, PlanStep.DeepThinking, PlanStep.Singing, PlanStep.Pictures
        ];
}

/// <summary>One step of the engine's claim order (<see cref="ComponentRanking.ClaimOrder"/>).</summary>
public enum PlanStep
{
    Character, ThinkingPrimary, ThinkingFallback, Voice, Listening, ListeningUpgrade, LipSync, DeepThinking, Singing, Pictures
}
