using System.Globalization;
using Martlet.Avatar.Hosting;
using Martlet.Core.Settings;

namespace Martlet.Desktop;

/// <summary>Companion › Touch's words, without WPF: who decided the active persona's touch temperament (or what it uses instead),
/// the personas' names, and when each change the character made was made and how it ended. <see cref="TouchPage"/> draws them.</summary>
internal static class TouchPageModel
{
    /// <summary>Who decided the persona's own temperament, or what it uses instead (after "For Mira: ").</summary>
    internal static string TemperamentStatus(TouchTemperamentSet saved, Guid personaId)
    {
        if (saved.UsesBuiltIn(personaId)) return "built-in reactions, as you chose.";
        if (saved.CustomOf(personaId) is { } custom) return $"your custom temperament \"{custom.Name}\".";
        var own = saved.Own(personaId);
        return own?.Source switch
        {
            CharacterTouchTemperament.ByOwner => "your own choices.",
            CharacterTouchTemperament.ByFixture => "FIXTURE - NOT AI: read from " + CharacterTemperamentService.FixtureVariable + ".",
            CharacterTouchTemperament.ByThinking => "decided by the Thinking model from the personality" +
                (own.DecidedAt is { } at ? $" on {at.ToLocalTime().ToString("g", CultureInfo.CurrentCulture)}." : "."),
            _ => "built-in reactions (not decided yet)."
        };
    }

    /// <summary>The personas' names in Personality's order ("Mira and Aki"); a persona that isn't on this PC counts as another
    /// persona.</summary>
    internal static string PersonaNames(IReadOnlyList<PersonaProfile> personas, IReadOnlyList<Guid> ids)
    {
        var names = personas.Where(p => ids.Contains(p.Id)).Select(p => p.Name).ToList();
        var others = ids.Count(id => personas.All(p => p.Id != id));
        if (others > 0) names.Add(others == 1 ? "another persona" : $"{others} other personas");
        return names.Count switch { 0 => "no persona", 1 => names[0], _ => string.Join(", ", names.Take(names.Count - 1)) + " and " + names[^1] };
    }

    /// <summary>When and by what a change was made.</summary>
    internal static string Made(CharacterReactionChange change, DateTimeOffset now)
    {
        var at = change.At.ToLocalTime();
        var when = at.Date == now.ToLocalTime().Date ? "at " + at.ToString("t", CultureInfo.CurrentCulture) : "on " + at.ToString("g", CultureInfo.CurrentCulture);
        return when + (change.By == "reactions" ? " by How I react" : change.By is { } by ? $" by the check-in {by}" : "");
    }

    /// <summary>When and how a change ended.</summary>
    internal static string Ended(CharacterReactionChange change)
    {
        var at = (change.EndedAt ?? change.Until).ToLocalTime().ToString("g", CultureInfo.CurrentCulture);
        return change.EndedBy switch
        {
            CharacterReactionChange.ByOwner => $"You undid it on {at}",
            CharacterReactionChange.ByCharacter => $"The character undid it on {at}",
            CharacterReactionChange.ByReplaced => $"A newer change replaced it on {at}",
            CharacterReactionChange.ByReset => $"Reset undid it on {at}",
            _ => $"It ended on {at}"
        };
    }
}
