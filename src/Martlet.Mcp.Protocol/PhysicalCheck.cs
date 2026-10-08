using System.Text.Json;
using Martlet.Avatar.Hosting;
using Martlet.Conversation;

namespace Martlet.Mcp;

/// <summary>character_physical_check: what Martlet makes of a stroke across the locked character and of moves and zooms, as the
/// desktop does it, with no desktop and no model request. A simulated stroke (samples with the renderer page's hit tests) is
/// summarized (zones crossed, pace, passes, which way) against the model's saved touch zones (or the rough zones before any were
/// found) and worded as its whole path (<see cref="CharacterPhysicalWords.Stroke(StrokeSummary, IReadOnlyList{CharacterTouchZone})"/>),
/// and it and simulated changes (RendererPhysical: moved, home, zoomed, zoom_reset, panned) are recorded in a touch ledger, which
/// gives the plain line the next reply would carry. Contacts nothing.</summary>
internal static class PhysicalCheck
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    internal static object Run(string dataDirectory, string? modelId, string? stroke, string? changes, bool noticeAll)
    {
        var settings = modelId is null ? null : CharacterTouchZones.Load(dataDirectory, modelId);
        var ledger = new TouchLedger();
        var at = TimeSpan.FromSeconds(1);
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
            for (var i = 0; words is not null && i < words.Times; i++)
                ledger.Record(new PhysicalEvent(PhysicalKind.Stroke, at, words.Where, words.Label, words.Pace, words.Hint));
            summary = new
            {
                result.Zones, result.Main, result.Ms, result.Length, result.Speed, result.Pace, result.Passes, result.Hits, result.Samples,
                result.Dx, result.Dy, result.Sideways, way = result.Way, noticed = noticed.Select(z => z.Id).ToArray(),
                words = words is null ? null : new { where = words.Where, label = words.Label, pace = words.Pace, times = words.Times, hint = words.Hint }
            };
        }
        var said = new List<object>();
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
                said.Add(new { change.Kind, ledgerKind = kind, detail, startsTurn = PhysicalKinds.StartsTurn(physical) });
            }
        }
        var burst = ledger.Peek(at);
        return new { stroke = summary, changes = said, line = burst?.Line, history = burst?.HistoryLine, startsTurn = burst?.StartsTurn ?? false };
    }
}
