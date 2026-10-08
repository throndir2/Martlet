using System.IO;
using System.Windows.Controls;
using Martlet.Avatar.Hosting;
using Martlet.Conversation;

namespace Martlet.Desktop;

/// <summary>What the user does to the character with the mouse beyond a tap: strokes across a locked character (each zone it
/// crosses reacts at once, the first zone's emote held until the stroke ends) and moving, zooming, panning, locking, hiding
/// and showing it. Each goes into the conversation's touch ledger (<see cref="NoticePhysical"/>), so Martlet knows: strokes and
/// moves, like taps, can start a short reply of their own, the rest wait for the next reply.</summary>
public partial class MainWindow
{
    private readonly CharacterStrokeTracker strokes = new();
    private readonly Dictionary<string, CharacterTouchZone> strokeZones = new(StringComparer.Ordinal);
    private CharacterActionSource? strokeHold;
    private bool strokeHoldWasOn;
    private string? physicalLast;
    private TextBlock? physicalLastText;

    private void WireCharacterPhysical()
    {
        avatar.Stroked += OnCharacterStroked;
        avatar.PhysicalChanged += change => Dispatcher.InvokeAsync(() => OnCharacterPhysical(change));
    }

    /// <summary>The Touch zones card's line on the last stroke and the last move, zoom or other change.</summary>
    private string PhysicalLastText() => physicalLast ?? "Lock the character's position, then drag across it to stroke it.";

    private void ShowPhysicalLast(string text)
    {
        physicalLast = text;
        if (physicalLastText is { } line) line.Text = text;
    }

    // Off the UI thread (the renderer's request relay), one batch after another.
    private void OnCharacterStroked(CharacterStroke batch)
    {
        if (closing || !avatar.IsShowing) return;
        var catalog = characterActions.For(avatar.InspectedProfile?.ModelPath);
        characterTouchZones.Follow(catalog?.Inventory.ModelId ?? characterTouchZones.ModelId);
        var settings = characterTouchZones.Current;
        var temperament = characterTemperaments.For(homeSettings?.Companion?.ActivePersonaId);
        string? ZoneOf(CharacterTouch touch)
        {
            if (CharacterTouchZones.Match(settings, touch) is not { } match) return null;
            lock (strokeZones) strokeZones[match.Zone.Id] = match.Zone;
            return match.Zone.Id;
        }
        var (entered, ended) = strokes.Add(batch, ZoneOf);
        foreach (var (touch, _) in entered)
        {
            // Each zone the stroke reaches plays its reaction (unless it is resting); the first one's emote stays on while
            // the stroke goes on.
            var first = strokeHold is null;
            characterTouchZones.React(touch, (zone, repeats) =>
            {
                var plan = TouchPlan(zone, catalog, temperament, repeats);
                if (first && plan.Actions.FirstOrDefault(s => s.Kind is CharacterActionKind.Expression or CharacterActionKind.Gesture) is { } hold)
                {
                    strokeHold = hold;
                    strokeHoldWasOn = avatar.Held.Holds(hold.Id);
                }
                return plan;
            }, (source, reason, linger) => source == strokeHold && !strokeHoldWasOn ? HoldForStrokeAsync(source, reason)
                : PlayTouchAsync(source, reason, linger), _ => { }, "stroke", avatar.Gaze.Attend);
        }
        if (ended is null) return;
        if (strokeHold is { } held)
        {
            strokeHold = null;
            if (!strokeHoldWasOn) StopStrokeHoldAsync(held).Forget();
        }
        CharacterTouchZone[] crossed;
        lock (strokeZones) crossed = [.. ended.Distinct.Select(id => strokeZones.GetValueOrDefault(id)).OfType<CharacterTouchZone>()];
        Dispatcher.InvokeAsync(() => StrokeEnded(ended, crossed));
    }

    private async Task HoldForStrokeAsync(CharacterActionSource source, string reason)
    {
        try { await avatar.PlayActionAsync(source, reason, null, lifetime.Token, hold: true); }
        catch (Exception error) when (error is OperationCanceledException || RendererFailures.Is(error, lifetime.Token)) { }
    }

    private async Task StopStrokeHoldAsync(CharacterActionSource source)
    {
        try { await avatar.StopActionAsync(source, "the end of a stroke", lifetime.Token); }
        catch (Exception error) when (error is OperationCanceledException || RendererFailures.Is(error, lifetime.Token)) { }
    }

    /// <summary>A stroke ended: Martlet notices it on the crossed zones that have Martlet notices on (like a tap there), one
    /// ledger entry for each pass (at most 8), with its whole path: "They slowly stroked down from your chest over your stomach
    /// to your thighs once", "They slowly stroked your hair 4 times".</summary>
    private void StrokeEnded(StrokeSummary summary, IReadOnlyList<CharacterTouchZone> crossed)
    {
        if (closing) return;
        var labels = crossed.Select(z => z.Name.ToLowerInvariant()).ToArray();
        var words = CharacterPhysicalWords.Stroke(summary, [.. crossed.Where(z => z.Reaction.Notices)]);
        for (var i = 0; words is not null && i < words.Times; i++)
            NoticePhysical(PhysicalKind.Stroke, words.Where, words.Label, words.Pace, words.Hint);
        var when = DateTime.Now.ToString("T", System.Globalization.CultureInfo.CurrentCulture);
        var text = $"Last stroke at {when}: " + (labels.Length == 0 ? "the character" : string.Join(" → ", labels)) +
            $", {summary.Pace}, {summary.Passes} pass{(summary.Passes == 1 ? "" : "es")}, {summary.Ms / 1000.0:0.0} s, " +
            $"{summary.Hits} of {summary.Samples} samples on the character" +
            (words is null ? "; no zone it crossed has Martlet notices on." : $"; Martlet noticed it: they {PhysicalKinds.Phrase(PhysicalKind.Stroke,
                words.Where, words.Pace)}" + (words.Times > 1 ? $" {words.Times} times." : "."));
        ShowPhysicalLast(text);
        ErrorLog.Info($"The character was stroked: {string.Join(", ", summary.Zones)} ({summary.Pace}, {summary.Passes} passes, " +
            $"{summary.Ms} ms, length {summary.Length:0.###}, speed {summary.Speed:0.###}).");
    }

    /// <summary>The user moved, sent home, zoomed or panned the character and it settled.</summary>
    private void OnCharacterPhysical(RendererPhysical change)
    {
        if (closing) return;
        string? Focus() => change.Focus is { } touch && CharacterTouchZones.Match(characterTouchZones.Current, touch) is { } match
            ? CharacterTouchZones.Part(match.Zone) : null;
        var (kind, detail) = CharacterPhysicalWords.Describe(change, Focus());
        NoticeCharacterChange(Enum.Parse<PhysicalKind>(kind), detail);
    }

    /// <summary>Records what the user did to the character in the touch ledger and shows it on the Touch zones card.</summary>
    private void NoticeCharacterChange(PhysicalKind kind, string? detail = null)
    {
        if (closing) return;
        NoticePhysical(kind, detail: detail);
        var when = DateTime.Now.ToString("T", System.Globalization.CultureInfo.CurrentCulture);
        ShowPhysicalLast($"Last change at {when}: they {PhysicalKinds.Phrase(kind, null, detail)}.");
        ErrorLog.Info($"The character was {kind.ToString().ToLowerInvariant()}{(detail is null ? "" : $" ({detail})")}.");
    }
}
