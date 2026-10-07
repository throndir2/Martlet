using System.IO;
using System.Text.Json;
using System.Windows;
using Martlet.Avatar.Hosting;
using Martlet.Avatar.RendererHost.Logging;

namespace Martlet.Avatar.RendererHost;

// Strokes on a locked character and what the user does to the overlay (moves, zooms, pans), for Martlet's touch ledger.
internal sealed partial class RendererWindow
{
    // ---------- hit-testing batches of points ----------

    private int hitBatch;
    private readonly Dictionary<int, (double X, double Y)[]> hitPoints = [];
    private readonly Dictionary<int, Action<CharacterTouch?[]>> hitAnswers = [];

    /// <summary>Asks the page what of the character is at each point (fractions of the page) in one message; the answer comes
    /// unprompted (<see cref="TouchesAnswered"/>), a null for each miss. Without the page every point is a miss at once.</summary>
    private void HitTest((double X, double Y)[] points, Action<CharacterTouch?[]> answered)
    {
        if (points.Length == 0) { answered([]); return; }
        if (browser.CoreWebView2 is null || failure.Failed || closed) { answered(new CharacterTouch?[points.Length]); return; }
        var id = ++hitBatch;
        // A page that stops answering never piles up requests.
        foreach (var stale in hitAnswers.Keys.Where(key => key <= id - 32).ToArray())
        {
            var lost = hitAnswers[stale];
            var count = hitPoints[stale].Length;
            hitAnswers.Remove(stale);
            hitPoints.Remove(stale);
            lost(new CharacterTouch?[count]);
        }
        hitPoints[id] = points;
        hitAnswers[id] = answered;
        try
        {
            browser.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(new
            {
                kind = "touches", data = new { id, points = points.Select(p => new { x = p.X, y = p.Y }).ToArray() }
            }, RendererProtocol.Json));
        }
        catch (Exception error) when (error is InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
            hitAnswers.Remove(id);
            hitPoints.Remove(id);
            answered(new CharacterTouch?[points.Length]);
        }
    }

    private void TouchesAnswered(JsonElement answer)
    {
        if (answer.ValueKind != JsonValueKind.Object || !answer.TryGetProperty("id", out var id) || !id.TryGetInt32(out var number) ||
            !hitAnswers.Remove(number, out var answered) || !hitPoints.Remove(number, out var points)) return;
        var touches = new CharacterTouch?[points.Length];
        if (answer.TryGetProperty("hits", out var hits) && hits.ValueKind == JsonValueKind.Array)
        {
            var i = 0;
            foreach (var item in hits.EnumerateArray())
            {
                if (i >= points.Length) break;
                var (hit, touch) = ReadTouch(item, points[i].X, points[i].Y);
                touches[i++] = hit && touch.IsValid ? touch : null;
            }
        }
        answered(touches);
    }

    private (double X, double Y) Fraction(Point at) => viewport.ActualWidth <= 0 || viewport.ActualHeight <= 0 ? (0.5, 0.5)
        : (Math.Round(Math.Clamp(at.X / viewport.ActualWidth, 0, 1), 4), Math.Round(Math.Clamp(at.Y / viewport.ActualHeight, 0, 1), 4));

    // Unprompted messages to Martlet go out one after another, in order.
    private Task unprompted = Task.CompletedTask;

    private void SendUnprompted<T>(string kind, T data, string what)
    {
        if (!CanRequest) return;
        var before = unprompted;
        unprompted = SendAfterAsync(before, kind, data, what);
    }

    private async Task SendAfterAsync<T>(Task before, string kind, T data, string what)
    {
        await before.ConfigureAwait(true);
        if (!CanRequest) return;
        try
        {
            await requesting.WaitAsync(lifetime.Token);
            try
            {
                await RendererProtocol.WriteAsync(requests!, RendererProtocol.Message(kind, activation, data), lifetime.Token)
                    .WaitAsync(TimeSpan.FromSeconds(2), lifetime.Token);
            }
            finally { requesting.Release(); }
        }
        catch (Exception error) when (error is IOException or ObjectDisposedException or OperationCanceledException or TimeoutException)
        {
            ErrorLog.Warn($"{what} couldn't reach Martlet.", error);
        }
    }

    // ---------- stroking a locked character ----------

    private const int StrokeSampleMs = 40, StrokeFlushMs = 120;

    private sealed class StrokeCapture(Point start, long since)
    {
        internal Point Start { get; } = start;
        internal long Since { get; } = since;
        internal int Id { get; set; }
        internal bool Active => Id > 0;
        internal Point Last { get; set; } = start;
        internal long Sampled { get; set; }
        internal long Flushed { get; set; }
        internal int Count { get; set; }
        internal List<(double X, double Y, int Ms)> Unsent { get; } = [];
        internal List<string> Zones { get; } = [];
        internal int Hits { get; set; }
    }

    private StrokeCapture? stroke;
    private int strokeId;
    private bool strokeReleasing;

    /// <summary>The left button went down on the locked character: holds the mouse so a drag across it (a stroke) keeps
    /// coming here even off the character's own pixels.</summary>
    private void BeginStroke(Point at, long now)
    {
        if (!placementLocked || camera is not null) return;
        stroke = new StrokeCapture(at, now);
        viewport.CaptureMouse();
    }

    /// <summary>The pointer moved while the locked character was pressed: once it has gone beyond the drag distance this is a
    /// stroke, sampled every 40 ms (at most <see cref="CharacterStroke.MaximumSamples"/> samples) and hit-tested in batches.</summary>
    private void TrackStroke(Point at, long now)
    {
        if (stroke is not { } s) return;
        s.Last = at;
        if (!s.Active)
        {
            if (!Moved(s.Start, at)) return;
            s.Id = ++strokeId;
            press = null;
            Sample(s, s.Start, s.Since);
        }
        if (now - s.Sampled >= StrokeSampleMs && s.Count < CharacterStroke.MaximumSamples - 1) Sample(s, at, now);
        if (s.Unsent.Count > 0 && (s.Flushed == 0 || now - s.Flushed >= StrokeFlushMs)) FlushStroke(s, "move", now);
    }

    private void Sample(StrokeCapture s, Point at, long now)
    {
        var (x, y) = Fraction(at);
        s.Unsent.Add((x, y, (int)Math.Clamp(now - s.Since, 0, CharacterStroke.MaximumMs)));
        s.Sampled = now;
        s.Count++;
    }

    /// <summary>The button came up (or the mouse was lost): a stroke sends its last samples as its "end"; a press that never
    /// moved far enough was a tap, handled as one.</summary>
    private void EndStroke(Point at, long now)
    {
        if (stroke is not { } s) return;
        stroke = null;
        strokeReleasing = true;
        try { if (viewport.IsMouseCaptured) viewport.ReleaseMouseCapture(); }
        finally { strokeReleasing = false; }
        if (!s.Active) return;
        Sample(s, at, now);
        FlushStroke(s, "end", now);
    }

    private void FlushStroke(StrokeCapture s, string phase, long now)
    {
        s.Flushed = now;
        var batches = s.Unsent.Chunk(CharacterStroke.MaximumBatch).ToArray();
        s.Unsent.Clear();
        if (batches.Length == 0) batches = [[]];
        var aspect = viewport.ActualWidth > 0 && viewport.ActualHeight > 0 ? Math.Round(viewport.ActualWidth / viewport.ActualHeight, 4) : 1;
        for (var b = 0; b < batches.Length; b++)
        {
            var batch = batches[b];
            var batchPhase = b == batches.Length - 1 ? phase : "move";
            HitTest([.. batch.Select(p => (p.X, p.Y))], touches =>
            {
                var samples = batch.Select((p, i) => new StrokeSample(p.X, p.Y, p.Ms, touches[i])).ToArray();
                foreach (var touch in touches)
                {
                    if (touch is null) continue;
                    s.Hits++;
                    if (s.Zones.Count == 0 || s.Zones[^1] != touch.CoarseZone) s.Zones.Add(touch.CoarseZone);
                }
                var message = new CharacterStroke(s.Id, batchPhase, aspect, samples);
                SendUnprompted("stroke", message, "A stroke on the character");
                if (batchPhase == "end")
                {
                    viewport.LastStroke = JsonSerializer.Serialize(new
                    {
                        n = s.Id, samples = s.Count, hits = s.Hits, ms = samples.Length > 0 ? samples[^1].Ms : 0, zones = s.Zones.Take(16)
                    }, RendererProtocol.Json);
                    ErrorLog.Info($"The locked character was stroked ({s.Count} samples, {s.Hits} on the character" +
                        (s.Zones.Count > 0 ? $": {string.Join(", ", s.Zones.Take(8))}" : "") + ").");
                }
            });
        }
    }

    /// <summary>UI Automation's stroke (Martlet's MCP character_stroke): drags across the locked character along
    /// <paramref name="points"/> (fractions of the page), one point every <paramref name="stepMs"/> milliseconds, like a press,
    /// a drag and a release.</summary>
    private void StrokeAlong(IReadOnlyList<(double X, double Y)> points, int stepMs)
    {
        if (!placementLocked || camera is not null)
            throw new InvalidOperationException("Stroking works on a locked character. Lock its position in Martlet first.");
        if (stroke is not null) throw new InvalidOperationException("A stroke is already going on.");
        if (points.Count < 2) throw new ArgumentException("A stroke needs at least two points.");
        if (viewport.ActualWidth <= 0 || viewport.ActualHeight <= 0) throw new InvalidOperationException("The character isn't shown yet.");
        Point At(int i) => new(points[i].X * viewport.ActualWidth, points[i].Y * viewport.ActualHeight);
        stroke = new StrokeCapture(At(0), Environment.TickCount64);
        var next = 1;
        var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(Math.Clamp(stepMs, 10, 2000)) };
        timer.Tick += (_, _) =>
        {
            if (closed || stroke is null) { timer.Stop(); return; }
            var now = Environment.TickCount64;
            if (next < points.Count - 1) TrackStroke(At(next++), now);
            else
            {
                timer.Stop();
                TrackStroke(At(next), now);
                EndStroke(At(next), now);
            }
        };
        timer.Start();
    }

    // ---------- what the user does to the overlay ----------

    private readonly OverlayChangeTracker overlayChanges = new();
    private System.Windows.Threading.DispatcherTimer? overlayTimer;

    private OverlayState OverlayNow()
    {
        var screen = CurrentScreen();
        return new(Left, Top, screen?.Name, FrameWidth * viewZoom, viewZoom, viewX, viewY, screen?.Work.Width ?? SystemParameters.WorkArea.Width);
    }

    /// <summary>Makes a change the user asked for (<paramref name="change"/>) and notes it for Martlet: outside the camera
    /// view, once it settles, it is sent as one "physical" message.</summary>
    private void Physically(string kind, Action change, (double X, double Y)? anchor = null)
    {
        var before = camera is null ? OverlayNow() : default;
        change();
        if (camera is null) NoteOverlay(kind, before, anchor);
    }

    private void NoteOverlay(string kind, OverlayState before, (double X, double Y)? anchor = null)
    {
        if (camera is not null || !CanRequest) return;
        overlayChanges.Note(kind, before, OverlayNow(), Environment.TickCount64, anchor);
        if (overlayTimer is null)
        {
            overlayTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
            overlayTimer.Tick += (_, _) => SettleOverlayChanges();
            Closed += (_, _) => overlayTimer.Stop();
        }
        overlayTimer.Start();
    }

    private void SettleOverlayChanges()
    {
        if (closed) { overlayTimer?.Stop(); return; }
        foreach (var change in overlayChanges.Due(Environment.TickCount64)) SendOverlayChange(change);
        if (!overlayChanges.Pending) overlayTimer?.Stop();
    }

    private void SendOverlayChange(OverlayChange change)
    {
        var physical = new RendererPhysical(change.Kind, Math.Round(change.After.Left - change.Before.Left), Math.Round(change.After.Top - change.Before.Top),
            Math.Round(change.After.ScreenWidth), change.Before.Screen is { } from ? ScreenLabel(from) : null,
            change.After.Screen is { } to ? ScreenLabel(to) : null, Math.Round(change.Before.Scale, 1), Math.Round(change.After.Scale, 1));
        // A zoom into the view says what it closed in on; a pan what the view now centers on.
        (double X, double Y)? focus = change.Kind switch
        {
            "zoomed" when change.After.Zoom > 1 && change.After.Scale > change.Before.Scale => change.Anchor ?? (0.5, 0.3),
            "panned" => (0.5, 0.5),
            _ => null
        };
        void Send(RendererPhysical message)
        {
            if (!message.IsValid) return;
            SendUnprompted("physical", message, "A change to the character's place or zoom");
            viewport.LastPhysical = JsonSerializer.Serialize(new
            {
                kind = message.Kind, dx = message.Dx, dy = message.Dy, from = message.FromScreen, to = message.ToScreen,
                zoomFrom = message.ZoomFrom, zoomTo = message.ZoomTo, focus = message.Focus?.CoarseZone
            }, RendererProtocol.Json);
        }
        if (focus is not { } point) { Send(physical); return; }
        HitTest([point], touches => Send(physical with { Focus = touches.FirstOrDefault() }));
    }
}
