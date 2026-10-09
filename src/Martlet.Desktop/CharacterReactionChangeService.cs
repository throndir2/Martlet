using System.IO;
using Martlet.Avatar.Hosting;
using Martlet.Core.Contracts;

namespace Martlet.Desktop;

/// <summary>The character's own changes to how it reacts to touches (Companion › Touch › Changes the character made), made by the
/// How I react check-in through the Touch reactions tools and kept in character-reaction-changes.json on this PC. A touch, a
/// stroke and the touch line use them over the owner's zones and temperament while they last
/// (<see cref="Temperament"/>, <see cref="Zone"/>); the owner undoes them on the Touch page or with Reset.</summary>
internal sealed class CharacterReactionChangeService(string? dataDirectory)
{
    private readonly object gate = new();
    private IReadOnlyList<CharacterReactionChange>? saved;

    internal event Action? Changed;

    /// <summary>Every change kept: read once, then kept up to date by each change made here.</summary>
    internal IReadOnlyList<CharacterReactionChange> Saved
    {
        get
        {
            if (dataDirectory is null) return [];
            lock (gate) return saved ??= CharacterReactionChanges.Load(dataDirectory);
        }
    }

    /// <summary>The persona's changes in effect now, oldest first.</summary>
    internal IReadOnlyList<CharacterReactionChange> Active(Guid? personaId) =>
        personaId is { } id ? CharacterReactionChanges.Active(Saved, id, DateTimeOffset.UtcNow) : [];

    /// <summary>The owner's <paramref name="temperament"/> with the persona's changes in effect over it.</summary>
    internal CharacterTouchTemperament? Temperament(CharacterTouchTemperament? temperament, Guid? personaId) =>
        personaId is { } id ? CharacterReactionChanges.Temperament(temperament, Active(id), id) : temperament;

    /// <summary>The zone with the persona's changes in effect over its reaction list.</summary>
    internal CharacterTouchZone Zone(CharacterTouchZone zone, CharacterTouchTemperament? temperament, CharacterActionCatalog? catalog, Guid? personaId) =>
        CharacterReactionChanges.Zone(zone, temperament, Active(personaId), catalog);

    /// <summary>Runs one Touch reactions tool call and saves what it changed. Returns its answer (a failure as a result, never an
    /// exception, except cancellation).</summary>
    internal async Task<ReactionToolAnswer> CallAsync(string tool, string? argumentsJson, ReactionToolContext context, CancellationToken token)
    {
        if (dataDirectory is null) return new("Martlet's data folder isn't available, so nothing changed.", true);
        ReactionToolAnswer? answer = null;
        try
        {
            var next = await CharacterReactionChanges.UpdateAsync(dataDirectory, now =>
            {
                answer = CharacterReactionTools.Call(tool, argumentsJson, context, now);
                return answer.Changes;
            }, token);
            lock (gate) saved = next;
        }
        catch (Exception error) when (error is ContractException or IOException or UnauthorizedAccessException)
        {
            return new("The change couldn't be saved: " + error.Message, true);
        }
        if (answer?.Changes is not null)
        {
            ErrorLog.Info($"The character changed how it reacts to touches ({tool}).");
            Changed?.Invoke();
        }
        return answer ?? new("Nothing happened.", true);
    }

    /// <summary>Ends one change (<paramref name="id"/>), or every change in effect of <paramref name="personaId"/> (every persona's
    /// when null), as <paramref name="by"/> (the owner, or Reset). Returns why it failed, or null.</summary>
    internal async Task<string?> EndAsync(string? id, Guid? personaId, string by, CancellationToken token)
    {
        if (dataDirectory is null) return "Martlet's data folder isn't available.";
        try
        {
            var next = await CharacterReactionChanges.UpdateAsync(dataDirectory, now =>
                CharacterReactionChanges.End(now, personaId, by, DateTimeOffset.UtcNow, id), token);
            lock (gate) saved = next;
        }
        catch (Exception error) when (error is ContractException or IOException or UnauthorizedAccessException) { return error.Message; }
        Changed?.Invoke();
        return null;
    }
}
