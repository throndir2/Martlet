using Martlet.Core.Characters;
using Martlet.Core.Settings;
using Martlet.Core.Voices;

namespace Martlet.Desktop;

/// <summary>Companion › Profiles' words, without WPF. The page makes one each time it draws: it reads the profiles, your looks and
/// voices and what Martlet is now once, and says what Now, the status line and each profile's lines show. <see cref="ProfilesPage"/>
/// draws it, and <see cref="IProfilesPageServices"/> is all it reads in the app.</summary>
internal sealed class ProfilesPageModel
{
    private readonly IProfilesPageServices services;

    internal ProfilesPageModel(IProfilesPageServices services)
    {
        this.services = services;
        Companion = services.Settings?.Companion;
        Profiles = Companion?.CharacterList ?? [];
        Library = services.Library();
        Voices = services.Voices();
        (ModelId, VoiceId, Current) = services.CharacterNow();
        var lookNow = ModelId is null ? "a model file outside your characters" : services.LookName(ModelId, Library);
        var mix = $"{lookNow}, the voice {(VoiceId is null ? "Martlet's engine picks" : MainWindow.VoiceName(VoiceId, Voices))} and the personality " +
            $"{Companion?.ActivePersona.Name ?? "Martlet"}";
        NowText = Current is not null ? $"{Current.Name}: {mix}."
            : Profiles.Count == 0 ? $"Martlet is {mix}; save this as a profile to switch back to it in one step."
            : $"No profile matches what Martlet uses now: {mix}.";
        StatusText = Profiles.Count == 0 ? "No profiles yet."
            : $"{Profiles.Count} profile{(Profiles.Count == 1 ? "" : "s")}. " +
              (Current is not null ? "One of them is in use." : "None matches what Martlet uses now.");
    }

    internal CompanionSettings? Companion { get; }
    internal IReadOnlyList<CharacterProfile> Profiles { get; }
    internal CharacterModelLibrary Library { get; }
    internal SpeakingVoiceLibrary Voices { get; }
    /// <summary>The look shown and the voice chosen now, and the profile that matches them (null when none does).</summary>
    internal string? ModelId { get; }
    internal string? VoiceId { get; }
    internal CharacterProfile? Current { get; }
    /// <summary>The Now card's line (CharacterProfilesNow).</summary>
    internal string NowText { get; }
    /// <summary>How many profiles there are and whether one is in use (CharacterProfilesStatus).</summary>
    internal string StatusText { get; }

    /// <summary>What a profile sets: its look, voice and personality.</summary>
    internal string Detail(CharacterProfile profile) =>
        $"Look: {services.LookName(profile.ModelId, Library)} · Voice: {MainWindow.VoiceName(profile.VoiceId, Voices)} · " +
        $"Personality: {MainWindow.PersonaName(profile.PersonaId, Companion)}";

    /// <summary>A profile's state line: in use, ready, or why it can't switch everything here.</summary>
    internal string State(CharacterProfile profile) =>
        profile.Id == Current?.Id ? "In use."
        : services.ProfileProblem(profile, Library, Voices) is { } problem ? problem + " Using it switches the rest." : "Ready.";

    /// <summary><paramref name="name"/>, or with a number after it when a profile already has that name.</summary>
    internal static string UniqueName(string name, CompanionSettings? companion)
    {
        var taken = new HashSet<string>((companion?.CharacterList ?? []).Select(c => c.Name), StringComparer.OrdinalIgnoreCase);
        if (!taken.Contains(name)) return name;
        for (var i = 2; ; i++)
            if (!taken.Contains($"{name} {i}")) return $"{name} {i}";
    }
}
