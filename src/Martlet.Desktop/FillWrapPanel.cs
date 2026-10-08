using System.Windows;
using System.Windows.Controls;

namespace Martlet.Desktop;

/// <summary>Wraps its children from left to right like a <see cref="WrapPanel"/>, but its last child takes the rest of the line it
/// starts on: at least <see cref="FillMinimum"/> wide, else a whole line of its own (from <see cref="FillIndent"/>). A dense editor
/// row uses it so its last box or note fills the row on a wide window and moves under the other fields on a narrow one. Each line is
/// as tall as its tallest child.</summary>
internal sealed class FillWrapPanel : Panel
{
    /// <summary>The narrowest the last child may be beside the others; with less room left it starts a new line.</summary>
    public double FillMinimum { get; set; } = 240;

    /// <summary>Where the last child starts when it takes a line of its own.</summary>
    public double FillIndent { get; set; }

    protected override Size MeasureOverride(Size availableSize)
    {
        var children = InternalChildren;
        if (children.Count == 0) return default;
        var room = new Size(availableSize.Width, double.PositiveInfinity);
        foreach (UIElement child in children) child.Measure(room);
        // The last child then lays its content out in the room it gets.
        children[children.Count - 1].Measure(new Size(Flow(availableSize.Width).Slots[^1].Width, double.PositiveInfinity));
        return Flow(availableSize.Width).Size;
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var slots = Flow(finalSize.Width).Slots;
        for (var i = 0; i < slots.Length; i++) InternalChildren[i].Arrange(slots[i]);
        return finalSize;
    }

    private (Rect[] Slots, Size Size) Flow(double width)
    {
        var children = InternalChildren;
        var slots = new Rect[children.Count];
        double x = 0, top = 0, line = 0, widest = 0;
        var first = 0;
        for (var i = 0; i < children.Count; i++)
        {
            var desired = children[i].DesiredSize;
            var fill = i == children.Count - 1 && !double.IsInfinity(width);
            if (x > 0 && (fill ? width - x < FillMinimum : x + desired.Width > width))
            {
                Wrap(i);
                if (fill) x = Math.Min(FillIndent, width);
            }
            var w = fill ? width - x : desired.Width;
            slots[i] = new Rect(x, top, w, desired.Height);
            x += w;
            line = Math.Max(line, desired.Height);
            widest = Math.Max(widest, x);
        }
        Wrap(children.Count);
        return (slots, new Size(widest, top));

        // Every child on the line gets the line's height, so each one's vertical alignment places it on the line.
        void Wrap(int next)
        {
            for (var j = first; j < next; j++) slots[j].Height = line;
            top += line;
            (x, line, first) = (0, 0, next);
        }
    }
}
