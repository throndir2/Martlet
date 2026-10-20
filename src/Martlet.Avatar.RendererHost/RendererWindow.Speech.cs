using System.ComponentModel;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using Martlet.Avatar.Hosting;
using Martlet.Avatar.RendererHost.Logging;
using Martlet.Presentation;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace Martlet.Avatar.RendererHost;

/// <summary>The speech bubble: its look, its text and where it sits beside the character.</summary>
internal sealed partial class RendererWindow
{
    private const double BubbleRadius = 16, BubblePadX = 16, BubblePadY = 10, TailLength = 22, TailHalfBase = 9,
        BubbleMargin = 18, ScreenMargin = 6, SpeechWidth = 280, WidestSpeech = 640, SpeechWidthStep = 60,
        ButtonGap = 8, ButtonHeight = 30, ButtonPadX = 14;
    // Beyond the longest sentence Martlet speaks at once (1,536 UTF-8 bytes); only bounds an arbitrary request.
    private const int MaximumSpeechCharacters = 2000;

    // The bubble's button: a pill in the palette's Accent with its On-accent text, named for screen readers and MCP.
    private static Button SpeechButton()
    {
        var border = new FrameworkElementFactory(typeof(Border));
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(ButtonHeight / 2));
        border.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Control.BackgroundProperty));
        border.SetValue(Border.PaddingProperty, new Thickness(ButtonPadX, 0, ButtonPadX, 0));
        var content = new FrameworkElementFactory(typeof(ContentPresenter));
        content.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
        content.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        border.AppendChild(content);
        var button = new Button
        {
            Template = new ControlTemplate(typeof(Button)) { VisualTree = border }, Height = ButtonHeight, Cursor = Cursors.Hand,
            FontSize = 14, FontFamily = new FontFamily("Segoe UI"), FontWeight = FontWeights.SemiBold, Focusable = false,
            Visibility = Visibility.Collapsed
        };
        button.SetResourceReference(Control.BackgroundProperty, "AccentBrush");
        button.SetResourceReference(Control.ForegroundProperty, "OnAccentBrush");
        AutomationProperties.SetAutomationId(button, "CharacterSpeechButton");
        return button;
    }

    // Martlet's own typeface (its windows' AppWindowStyle), a little larger to read over a game.
    private static TextBlock SpeechText() => new()
    {
        TextWrapping = TextWrapping.Wrap, MaxWidth = SpeechWidth, FontSize = 15, LineHeight = 21,
        FontFamily = new FontFamily("Segoe UI"), TextAlignment = TextAlignment.Left
    };

    // One continuous outline (rounded body unioned with a curved, tapering tail) with a soft halo, so the bubble reads as a
    // single comic-style shape rather than a box with a triangle stuck on. It is drawn wholly in Martlet's palette (Martlet's
    // own or the character's): a Surface card with an Accent outline, Text and a Glow halo (see ApplyOverlayTheme).
    private Popup CreateSpeechBubble()
    {
        speechText.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");
        AutomationProperties.SetAutomationId(speechText, "CharacterSpeech");
        AutomationProperties.SetLiveSetting(speechText, AutomationLiveSetting.Polite);
        speechShape.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, "SurfaceBrush");
        speechShape.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty, "AccentBrush");
        speechCanvas.Children.Add(speechShape);
        speechCanvas.Children.Add(speechText);
        speechCanvas.Children.Add(speechButton);
        speechButton.Click += (_, _) => Request(RendererRequest.ReadMore);
        speechCanvas.RenderTransform = speechPop;
        speechBubble.Child = speechCanvas;
        // The bubble is its own window, made again each time it opens: it lets clicks pass through too while that is on.
        speechBubble.Opened += (_, _) => ApplyClickThrough();
        LocationChanged += (_, _) => PlaceSpeech();
        SizeChanged += (_, _) => PlaceSpeech();
        Closed += (_, _) => speechBubble.IsOpen = false;
        return speechBubble;
    }

    internal RendererBubble ShowSpeech(RendererSay say)
    {
        speechPlacement = say;
        var button = say.Button is { } label && !string.IsNullOrWhiteSpace(label)
            ? label.Trim().Length > RendererSay.MaximumButtonCharacters ? label.Trim()[..RendererSay.MaximumButtonCharacters] + "…" : label.Trim()
            : null;
        speechButton.Content = speechButtonMeasure.Text = button ?? "";
        speechButton.Visibility = button is null ? Visibility.Collapsed : Visibility.Visible;
        AutomationProperties.SetName(speechButton, button ?? "");
        // Only a bubble with a button catches clicks (unless clicks pass through the character); speech alone never does.
        speechBubble.IsHitTestVisible = button is not null;
        if (string.IsNullOrWhiteSpace(say.Text))
        {
            speechBubble.IsOpen = false;
            speechText.Text = "";
            speechButton.Visibility = Visibility.Collapsed;
            speechBubble.IsHitTestVisible = false;
            return speechShown = new("hidden", 0, 0, 0, 0);
        }
        speechText.Text = say.Text.Length > MaximumSpeechCharacters ? say.Text[..MaximumSpeechCharacters] + "…" : say.Text;
        PlaceSpeech();
        speechBubble.IsOpen = true;
        if (SystemParameters.ClientAreaAnimation)
        {
            var pop = new System.Windows.Media.Animation.DoubleAnimation(0.86, 1, TimeSpan.FromMilliseconds(180))
            {
                EasingFunction = new System.Windows.Media.Animation.BackEase
                {
                    Amplitude = 0.35, EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut
                }
            };
            speechPop.BeginAnimation(ScaleTransform.ScaleXProperty, pop);
            speechPop.BeginAnimation(ScaleTransform.ScaleYProperty, pop);
        }
        // Lay the open bubble out now so the reply says whether the text as shown really lies inside the bubble's body, and in
        // which colors it is drawn.
        speechCanvas.UpdateLayout();
        return speechShown = speechShown with
        {
            TextFits = speechText.ActualWidth <= speechShown.Width - BubblePadX * 2 + 1 &&
                speechText.ActualHeight <= speechShown.Height - BubblePadY * 2 + 1,
            Colors = new(BrushHex(speechShape.Fill), BrushHex(speechShape.Stroke), BrushHex(speechText.Foreground),
                speechShape.Effect is System.Windows.Media.Effects.DropShadowEffect halo ? Hex(halo.Color) : null),
            Button = button
        };

        static string BrushHex(Brush? brush) => brush is SolidColorBrush solid ? Hex(solid.Color) : "";
        static string Hex(Color color) => $"#{color.R:X2}{color.G:X2}{color.B:X2}";
    }

    // By default the bubble follows the character's head through moves, zoom and pan: beside the head on whichever side has
    // room on its screen (left first, then right), else above it, then shifted by the configured offsets and kept on screen.
    // Static keeps it at one spot on the character's screen, with its tail still pointing toward the character.
    private void PlaceSpeech()
    {
        if (speechText.Text.Length == 0) return;
        var screen = WorkArea() ?? SystemParameters.WorkArea;
        screen.Inflate(-ScreenMargin, -ScreenMargin);
        var (width, height) = MeasureSpeech(screen);

        // The head in screen coordinates, followed through the camera: screen clip = fitted clip * zoom + pan. X is measured
        // from the frame's left edge and kept within the overlay (frame plus the room beside it).
        var top = double.IsFinite(contentTop) ? contentTop : 0.6;
        var frame = FrameWidth;
        double ScreenY(double clip) => Top + Math.Clamp(Height * (1 - (clip * viewZoom + viewY)) / 2, 0, Height);
        double ScreenX(double fraction) => Left + Math.Clamp(FrameOffset(Width) + fraction, 0, Width);
        var faceX = frame * (viewX + 1) / 2;
        var halfHead = frame * 0.11 * viewZoom;
        var faceY = ScreenY(top - 0.2);
        var headTop = ScreenY(top);
        var face = new Point(ScreenX(faceX), faceY);

        string placement;
        Rect body;
        Point tip;
        if (speechPlacement.Static)
        {
            placement = "static";
            body = new Rect(screen.Left + 10 + speechPlacement.OffsetX, screen.Top + 10 + speechPlacement.OffsetY, width, height);
            tip = face;
        }
        else
        {
            var candidates = new (string Name, Point Tip, Rect Body)[]
            {
                ("left", new Point(ScreenX(faceX - halfHead), faceY),
                    new Rect(ScreenX(faceX - halfHead) - TailLength - width, faceY + 14 - height, width, height)),
                ("right", new Point(ScreenX(faceX + halfHead), faceY),
                    new Rect(ScreenX(faceX + halfHead) + TailLength, faceY + 14 - height, width, height)),
                ("above", new Point(ScreenX(faceX), headTop - 4),
                    new Rect(ScreenX(faceX) - width * 0.4, headTop - 4 - TailLength - height, width, height))
            };
            for (var i = 0; i < candidates.Length; i++) candidates[i].Body.Offset(speechPlacement.OffsetX, speechPlacement.OffsetY);
            var chosen = candidates.FirstOrDefault(c => screen.Contains(c.Body));
            if (chosen.Name is null)
                chosen = candidates.OrderBy(c => Overflow(c.Body, screen)).First();
            (placement, tip, body) = chosen;
        }
        body = KeepInside(body, screen);
        speechShown = new(placement, Math.Round(body.Left), Math.Round(body.Top), Math.Round(body.Width), Math.Round(body.Height));

        // Lay the shape out on a canvas spanning the body and tail, with room for the shadow.
        var tail = Tail(body, tip, placement == "static" ? TailLength * 0.9 : TailLength * 2.5);
        var bounds = body;
        if (tail is not null) bounds.Union(tail.Value.Tip);
        bounds.Inflate(BubbleMargin, BubbleMargin);
        var origin = bounds.TopLeft;
        var local = new Rect(body.Left - origin.X, body.Top - origin.Y, width, height);
        var radius = Math.Min(BubbleRadius, height / 2);
        Geometry outline = new RectangleGeometry(local, radius, radius);
        if (tail is { } t)
            outline = new CombinedGeometry(GeometryCombineMode.Union, outline,
                TailGeometry(t.Base1 - (Vector)origin, t.Base2 - (Vector)origin, t.Tip - (Vector)origin));
        outline.Freeze();
        speechShape.Data = outline;
        speechCanvas.Width = bounds.Width;
        speechCanvas.Height = bounds.Height;
        Canvas.SetLeft(speechText, local.Left + BubblePadX);
        Canvas.SetTop(speechText, local.Top + BubblePadY);
        Canvas.SetLeft(speechButton, local.Left + BubblePadX);
        Canvas.SetTop(speechButton, local.Top + BubblePadY + speechTextHeight + ButtonGap);
        var pivot = tail?.Tip ?? new Point(body.Left + width / 2, body.Bottom);
        speechCanvas.RenderTransformOrigin = new Point((pivot.X - origin.X) / bounds.Width, (pivot.Y - origin.Y) / bounds.Height);
        speechBubble.HorizontalOffset = origin.X;
        speechBubble.VerticalOffset = origin.Y;
        if (speechBubble.IsOpen)
        {
            // Nudging the offset forces an open Popup to reposition after its owner moves.
            speechBubble.HorizontalOffset += 0.01;
            speechBubble.HorizontalOffset -= 0.01;
        }
    }

    // The bubble's body size for the shown text, measured on a stand-in that is never inside the (closable) popup. It keeps a
    // comfortable reading width, widening only as far as a long speech needs to stay within half the screen's height.
    private (double Width, double Height) MeasureSpeech(Rect screen)
    {
        speechMeasure.Text = speechText.Text;
        var widest = Math.Max(SpeechWidth, Math.Min(WidestSpeech, screen.Width - BubblePadX * 2 - TailLength));
        var tallest = Math.Max(screen.Height / 2 - BubblePadY * 2, speechMeasure.LineHeight);
        for (var wrap = SpeechWidth; ; wrap = Math.Min(widest, wrap + SpeechWidthStep))
        {
            speechMeasure.MaxWidth = wrap;
            speechMeasure.Measure(new Size(wrap, double.PositiveInfinity));
            if (speechMeasure.DesiredSize.Height <= tallest || wrap >= widest) break;
        }
        speechText.MaxWidth = speechMeasure.MaxWidth;
        speechTextHeight = Math.Ceiling(speechMeasure.DesiredSize.Height);
        var width = Math.Ceiling(speechMeasure.DesiredSize.Width);
        var height = speechTextHeight;
        if (speechButton.Visibility == Visibility.Visible)
        {
            speechButtonMeasure.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            width = Math.Max(width, Math.Ceiling(speechButtonMeasure.DesiredSize.Width) + ButtonPadX * 2);
            height += ButtonGap + ButtonHeight;
        }
        return (Math.Max(48, width + BubblePadX * 2), height + BubblePadY * 2);
    }

    private static double Overflow(Rect body, Rect screen) =>
        Math.Max(0, screen.Left - body.Left) + Math.Max(0, body.Right - screen.Right) +
        Math.Max(0, screen.Top - body.Top) + Math.Max(0, body.Bottom - screen.Bottom);

    private static Rect KeepInside(Rect body, Rect screen)
    {
        var x = Math.Max(screen.Left, Math.Min(body.Left, screen.Right - body.Width));
        var y = Math.Max(screen.Top, Math.Min(body.Top, screen.Bottom - body.Height));
        return new Rect(x, y, body.Width, body.Height);
    }

    // The tail leaves the body edge facing the target, as near to it as the rounded corners allow, and points at it
    // (shortened to the longest tail that still looks like a tail). None when the target is under the body.
    private static (Point Base1, Point Base2, Point Tip)? Tail(Rect body, Point target, double longest)
    {
        var dx = target.X < body.Left ? body.Left - target.X : target.X > body.Right ? target.X - body.Right : 0;
        var dy = target.Y < body.Top ? body.Top - target.Y : target.Y > body.Bottom ? target.Y - body.Bottom : 0;
        if (dx <= 0 && dy <= 0) return null;
        var inset = Math.Min(BubbleRadius, body.Height / 2) + TailHalfBase;
        Point center;
        Vector across;
        if (dx >= dy)
        {
            var y = body.Height > inset * 2 ? Math.Clamp(target.Y, body.Top + inset, body.Bottom - inset) : body.Top + body.Height / 2;
            center = new Point(target.X < body.Left ? body.Left + 3 : body.Right - 3, y);
            across = new Vector(0, TailHalfBase);
        }
        else
        {
            var x = body.Width > inset * 2 ? Math.Clamp(target.X, body.Left + inset, body.Right - inset) : body.Left + body.Width / 2;
            center = new Point(x, target.Y < body.Top ? body.Top + 3 : body.Bottom - 3);
            across = new Vector(TailHalfBase, 0);
        }
        var reach = target - center;
        if (reach.Length > longest) reach *= longest / reach.Length;
        if (reach.Length < 8) return null;
        return (center - across, center + across, center + reach);
    }

    private static PathGeometry TailGeometry(Point base1, Point base2, Point tip)
    {
        var middle = new Point((base1.X + base2.X) / 2, (base1.Y + base2.Y) / 2);
        var spine = middle + (tip - middle) * 0.5;
        Point Bend(Point from) => (from + (tip - from) * 0.5) + (spine - (from + (tip - from) * 0.5)) * 0.45;
        var figure = new PathFigure { StartPoint = base1, IsClosed = true, IsFilled = true };
        figure.Segments.Add(new QuadraticBezierSegment(Bend(base1), tip, true));
        figure.Segments.Add(new QuadraticBezierSegment(Bend(base2), base2, true));
        return new PathGeometry { Figures = { figure } };
    }
}
