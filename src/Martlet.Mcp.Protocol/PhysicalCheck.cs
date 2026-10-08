using System.IO;
using System.Text.Json;
using Martlet.Avatar.Hosting;
using Martlet.Conversation;
using Martlet.Core.Contracts;
using Martlet.Core.Settings;
using Martlet.Core.Speakers;

namespace Martlet.Mcp;

/// <summary>character_physical_check: what Martlet makes of a stroke across the locked character and of moves and zooms, as the
/// desktop does it, with no desktop and no model request. A simulated stroke (samples with the renderer page's hit tests) is
/// summarized (zones crossed, pace, passes, which way) against the model's saved touch zones (or the rough zones before any were
/// found) and worded as its whole path (<see cref="CharacterPhysicalWords.Stroke(StrokeSummary, IReadOnlyList{CharacterTouchZone})"/>),
/// with how a persona feels about those zones (<see cref="CharacterTouchTemperaments.Feeling"/>), and it and simulated changes
/// (RendererPhysical: moved, home, zoomed, zoom_reset, panned) are recorded in a touch ledger, which gives the plain line the
/// next reply would carry, the places the user keeps coming back to (after earlier strokes), for a touch that stopped Martlet
/// talking the whole message a reaction would get, and the talk window's note for a reply to them alone, by the name of the
/// persona the data directory's settings use. Contacts nothing.</summary>
internal static class PhysicalCheck
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    internal static object Run(string dataDirectory, string? modelId, string? stroke, string? changes, bool noticeAll, string? personaId = null,
        int earlier = 0, string? said = null, string? answering = null, string? touchInterrupts = null)
    {
        var settings = modelId is null ? null : CharacterTouchZones.Load(dataDirectory, modelId);
        // The talk window calls the character by the name of the persona Martlet uses, as it does.
        var character = CompanionNames.Character(Persona(dataDirectory));
        var prompts = Prompts(dataDirectory);
        var temperament = Guid.TryParse(personaId, out var persona) ? CharacterTouchTemperaments.LoadSet(dataDirectory).For(persona) : null;
        var interrupts = touchInterrupts switch
        {
            null or "any" => TouchInterrupts.Any,
            "intimate" => TouchInterrupts.Intimate,
            "never" => TouchInterrupts.Never,
            _ => throw new ArgumentException("touchInterrupts must be any, intimate or never.")
        };
        var ledger = new TouchLedger();
        // Late enough that earlier strokes, a minute apart, have times of their own.
        var at = TimeSpan.FromMinutes(30);
        object? summary = null;
        if (stroke is not null)
        {
            var given = JsonSerializer.Deserialize<CharacterStroke>(stroke, Web) ?? throw new ArgumentException("stroke must be a CharacterStroke object.");
            var zones = new Dictionary<string, CharacterTouchZone>(StringComparer.Ordinal);
            string? ZoneOf(CharacterTouch touch)
            {
                if (CharacterTouchZones.Match(settings, touch) is not { } match) return null;
                zones[match.Zone.Id] = match.Zone;
                return match.Zone.Id;
            }
            var result = CharacterStrokes.Summarize(given.Samples, given.Aspect, ZoneOf);
            var noticed = result.Distinct.Select(id => zones[id]).Where(z => noticeAll || z.Reaction.Notices).ToArray();
            var words = CharacterPhysicalWords.Stroke(result, noticed);
            var feeling = CharacterTouchTemperaments.Feeling(temperament, noticed);
            var intimate = noticed.Any(z => CharacterTouchZones.Kind(z.Id)?.Intimate == true);
            bool? stops = null;
            if (words is not null)
            {
                PhysicalEvent Event(TimeSpan when) => new(PhysicalKind.Stroke, when, words.Where, words.Label, words.Pace, words.Hint,
                    [.. noticed.Select(CharacterTouchZones.Part)], intimate, feeling);
                // The same stroke earlier, a minute apart, each taken by a reply, as the desktop's ledger keeps them.
                for (var round = Math.Clamp(earlier, 0, 20); round > 0; round--)
                {
                    var when = at - TimeSpan.FromMinutes(round);
                    for (var i = 0; i < words.Times; i++) ledger.Record(Event(when));
                    ledger.Drain(when);
                }
                for (var i = 0; i < words.Times; i++) ledger.Record(Event(at));
                stops = PhysicalKinds.Interrupts(interrupts, Event(at));
            }
            summary = new
            {
                result.Zones, result.Main, result.Ms, result.Length, result.Speed, result.Pace, result.Passes, result.Hits, result.Samples,
                result.Dx, result.Dy, result.Sideways, way = result.Way, noticed = noticed.Select(z => z.Id).ToArray(),
                words = words is null ? null : new { where = words.Where, label = words.Label, pace = words.Pace, times = words.Times, hint = words.Hint },
                feeling, intimate, interrupts = stops
            };
        }
        var moved = new List<object>();
        if (changes is not null)
        {
            var list = JsonSerializer.Deserialize<RendererPhysical[]>(changes, Web) ?? throw new ArgumentException("changes must be an array.");
            foreach (var change in list)
            {
                if (!change.IsValid) throw new ArgumentException($"The change '{change.Kind}' isn't valid.");
                var focus = change.Focus is { } touch && CharacterTouchZones.Match(settings, touch) is { } match ? CharacterTouchZones.Part(match.Zone) : null;
                var (kind, detail) = CharacterPhysicalWords.Describe(change, focus);
                var physical = Enum.Parse<PhysicalKind>(kind);
                at += TimeSpan.FromSeconds(1);
                ledger.Record(new PhysicalEvent(physical, at, Detail: detail));
                moved.Add(new { change.Kind, ledgerKind = kind, detail, startsTurn = PhysicalKinds.StartsTurn(physical) });
            }
        }
        if (said is not null || answering is not null) ledger.CutIn(new(said, answering, at));
        var burst = ledger.Peek(at);
        var told = burst is null ? null : TouchWording.Told(prompts, burst);
        return new
        {
            stroke = summary, changes = moved, line = burst?.Line, history = burst?.HistoryLine, startsTurn = burst?.StartsTurn ?? false,
            character, note = burst is { StartsTurn: true } ? burst.Note(character) : null,
            often = burst?.Often?.Select(h => new { place = h.Place, count = h.Count, minutes = Math.Round(h.Over.TotalMinutes, 1) }).ToArray(),
            told,
            message = told is null ? null : PromptSettings.Fill(prompts, PromptCatalog.Touched, ("touches", told), ("silent", StayQuiet.Marker)),
            notes = told is null ? null : PromptSettings.Fill(prompts, PromptCatalog.TouchedNotes, ("touches", told))
        };
    }

    /// <summary>The name of the persona the data directory's settings use, or null (none saved, or unreadable).</summary>
    private static string? Persona(string dataDirectory)
    {
        var path = Path.Combine(dataDirectory, "settings.json");
        try
        {
            return File.Exists(path) && SettingsJson.Read(File.ReadAllBytes(path)).Companion is { } companion
                ? companion.Personas.FirstOrDefault(persona => persona.Id == companion.ActivePersonaId)?.Name : null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or ContractException)
        {
            return null;
        }
    }

    // The prompts saved in the data directory's settings.json (Companion › Prompts), or null for the built-in ones.
    private static PromptSettings? Prompts(string dataDirectory)
    {
        try
        {
            var path = Path.Combine(dataDirectory, "settings.json");
            return File.Exists(path) ? SettingsJson.Read(File.ReadAllBytes(path)).Prompts : null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or NotSupportedException or
            InvalidOperationException or ContractException) { return null; }
    }
}
