using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace Martlet.Desktop;

/// <summary>The rows of a page's long lists (Companion › Emotes and motions, Touch) added a batch at a time. While a drawing that
/// adds them gradually is under way, only the first rows join their panels at once; the rest wait and join a batch at a time at
/// background priority once the page has drawn, so clicks and typing go first and the page shows at once instead of after
/// laying out every row. Every row is built at once, so saving reads them all; only laying them out waits. Rows join their
/// panels in the order they were added, so the page ends up exactly as if drawn at once. On the UI thread.</summary>
internal sealed class RowBatches(int first = 6, int batch = 6)
{
    private readonly List<(Panel Panel, UIElement Row)> waiting = [];
    private int shown, drawing;

    /// <summary>Whether the drawing under way adds its rows gradually.</summary>
    internal bool Gradually { get; private set; }

    /// <summary>Starts a drawing: the rows added until <see cref="End"/> belong to it, and an older drawing's batches stop.</summary>
    internal void Begin(bool gradually)
    {
        drawing++;
        waiting.Clear();
        shown = 0;
        Gradually = gradually;
    }

    /// <summary>Adds <paramref name="row"/> to <paramref name="panel"/> now, or, after the first rows of a gradual drawing, once
    /// the page has drawn.</summary>
    internal void Add(Panel panel, UIElement row)
    {
        if (Gradually && (waiting.Count > 0 || shown >= first)) waiting.Add((panel, row));
        else
        {
            panel.Children.Add(row);
            shown++;
        }
    }

    /// <summary>Ends the drawing: its waiting rows join a batch at a time. Once the last batch is laid out,
    /// <paramref name="done"/> gets how many rows the drawing added and in how many batches (the first being the rows added
    /// at once). A newer drawing, or <see cref="Stop"/>, stops it.</summary>
    internal void End(Dispatcher dispatcher, Action<int, int>? done = null)
    {
        Gradually = false;
        if (waiting.Count == 0) return;
        (Panel Panel, UIElement Row)[] rows = [.. waiting];
        waiting.Clear();
        var current = drawing;
        var total = shown + rows.Length;
        var next = 0;
        var batches = 1;
        dispatcher.InvokeAsync(AddBatch, DispatcherPriority.Background);

        void AddBatch()
        {
            if (current != drawing) return;
            for (var end = Math.Min(rows.Length, next + batch); next < end; next++) rows[next].Panel.Children.Add(rows[next].Row);
            batches++;
            if (next < rows.Length) dispatcher.InvokeAsync(AddBatch, DispatcherPriority.Background);
            else if (done is not null)
                dispatcher.InvokeAsync(() =>
                {
                    if (current == drawing) done(total, batches);
                }, DispatcherPriority.Background);
        }
    }

    /// <summary>Stops adding the waiting rows, as when the window closes.</summary>
    internal void Stop() => drawing++;
}
