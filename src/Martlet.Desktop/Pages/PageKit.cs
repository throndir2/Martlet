using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Martlet.Core.Settings;

namespace Martlet.Desktop;

/// <summary>The small builders every settings page uses: a card, its heading, a muted note, a warning line, a row of buttons, a
/// page button, a labelled row and the dense editor rows (a compact control, a row label, a row group and a row switch). They
/// only make WPF controls with the app's styles; they read and change nothing else.
/// Martlet.Desktop.csproj imports this class everywhere (<c>&lt;Using Include="Martlet.Desktop.PageKit" Static="true" /&gt;</c>),
/// so a page calls <c>Card(...)</c> or <c>Note(...)</c> directly.</summary>
internal static class PageKit
{
    /// <summary>A card (the CardStyle border) with <paramref name="children"/> stacked in it.</summary>
    internal static Border Card(params UIElement[] children)
    {
        var stack = new StackPanel();
        foreach (var child in children) stack.Children.Add(child);
        var card = new Border { Child = stack };
        card.SetResourceReference(FrameworkElement.StyleProperty, "CardStyle");
        return card;
    }

    /// <summary>A card's heading (the SectionHeading style).</summary>
    internal static TextBlock Heading(string text)
    {
        var heading = new TextBlock { Text = text, Margin = new Thickness(0, 0, 0, 6) };
        heading.SetResourceReference(FrameworkElement.StyleProperty, "SectionHeading");
        return heading;
    }

    /// <summary>A muted line of text that wraps.</summary>
    internal static TextBlock Note(string text, Thickness margin)
    {
        var note = new TextBlock { Text = text, Margin = margin };
        note.SetResourceReference(FrameworkElement.StyleProperty, "Muted");
        note.TextWrapping = TextWrapping.Wrap;
        return note;
    }

    /// <summary>A line of text in the warning colour that wraps.</summary>
    internal static TextBlock Warning(string text)
    {
        var warning = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 6) };
        warning.SetResourceReference(TextBlock.ForegroundProperty, "WarningBrush");
        return warning;
    }

    /// <summary>A wrapping row of <paramref name="buttons"/>; null buttons are left out.</summary>
    internal static WrapPanel Row(params Button?[] buttons)
    {
        var row = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
        foreach (var button in buttons.OfType<Button>())
        {
            button.Margin = new Thickness(0, 0, 10, 6);
            row.Children.Add(button);
        }
        return row;
    }

    /// <summary>A page button that runs <paramref name="run"/> when clicked: plain, <paramref name="primary"/> or a
    /// <paramref name="link"/>. <paramref name="id"/> is its automation ID; its automation name is its label.</summary>
    internal static Button PageButton(string label, Action run, bool primary = false, bool link = false, string? id = null)
    {
        var button = new Button { Content = label, MinWidth = link ? 0 : 96 };
        if (primary) button.SetResourceReference(FrameworkElement.StyleProperty, "PrimaryButton");
        else if (link) button.SetResourceReference(FrameworkElement.StyleProperty, "LinkButton");
        AutomationProperties.SetName(button, label);
        if (id is not null) AutomationProperties.SetAutomationId(button, id);
        button.Click += (_, _) => run();
        return button;
    }

    /// <summary>A large radio choice in <paramref name="group"/>: its <paramref name="title"/> in bold and its
    /// <paramref name="detail"/> under it. Its automation name is both; <paramref name="id"/> is its automation ID.</summary>
    internal static RadioButton Choice(string group, string title, string detail, bool isChecked, string id)
    {
        var text = new StackPanel();
        text.Children.Add(new TextBlock { Text = title, FontSize = 15, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
        text.Children.Add(Note(detail, new Thickness(0, 2, 0, 0)));
        var option = new RadioButton { Content = text, GroupName = group, IsChecked = isChecked, Margin = new Thickness(0, 0, 0, 10) };
        AutomationProperties.SetName(option, $"{title}: {detail}");
        AutomationProperties.SetAutomationId(option, id);
        return option;
    }

    /// <summary>A row with a 90-pixel <paramref name="label"/> on the left and <paramref name="control"/> beside it.</summary>
    internal static DockPanel TerminalRow(string label, UIElement control)
    {
        var row = new DockPanel { Margin = new Thickness(0, 8, 0, 0), LastChildFill = true };
        var name = new TextBlock { Text = label, Width = 90, VerticalAlignment = control is ComboBox ? VerticalAlignment.Center : VerticalAlignment.Top };
        DockPanel.SetDock(name, Dock.Left);
        row.Children.Add(name);
        if (control is ComboBox box) box.HorizontalAlignment = HorizontalAlignment.Left;
        row.Children.Add(control);
        return row;
    }

    // ---------- dense editor rows (Companion › Check-ins, Emotes and motions, and Touch's touch zones and touch temperament) ----------

    /// <summary>Where a dense row's fields start, under its name: an empty check box is 29 pixels wide (its box and the gap its
    /// template keeps before content), then a 6-pixel gap.</summary>
    internal const double RowIndent = 35;

    /// <summary>The check box that turns a dense row on, before its name.</summary>
    internal static CheckBox RowSwitch(bool on) =>
        new() { IsChecked = on, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, RowIndent - 29, 0) };

    /// <summary>Makes a text box, choice or button one of a dense row's compact controls: 32 pixels tall, on the row's centre line.</summary>
    internal static T Compact<T>(T control) where T : Control
    {
        control.SetResourceReference(FrameworkElement.StyleProperty, control switch
        {
            TextBox => "CompactTextBox", ComboBox => "CompactComboBox", _ => "CompactButton"
        });
        if (control is Button) control.MinWidth = 56;
        return control;
    }

    /// <summary>A short muted label in a dense row, on the centre line of the control it names. Labels that start a row's lines get
    /// the same <paramref name="width"/>, so the boxes after them line up.</summary>
    internal static Label RowLabel(string text, UIElement target, double left = 0, double right = 6, double width = 0)
    {
        var label = new Label
        {
            Content = text, Target = target, Padding = new Thickness(left, 0, right, 0), VerticalAlignment = VerticalAlignment.Center,
            MinWidth = width
        };
        label.SetResourceReference(Control.ForegroundProperty, "MutedBrush");
        return label;
    }

    /// <summary>Items of a dense row that stay together when the row wraps, such as a label and its box.</summary>
    internal static StackPanel RowGroup(params UIElement[] items)
    {
        var group = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 18, 6) };
        foreach (var item in items) group.Children.Add(item);
        return group;
    }

    /// <summary>Takes the key typed in <paramref name="box"/> as a <see cref="SecretLease"/> and clears the box and every copy
    /// of the key in memory.</summary>
    internal static SecretLease TakeKey(PasswordBox box)
    {
        using var secure = box.SecurePassword;
        var chars = new char[secure.Length];
        var pointer = Marshal.SecureStringToGlobalAllocUnicode(secure);
        try
        {
            Marshal.Copy(pointer, chars, 0, chars.Length);
            return new SecretLease(chars);
        }
        finally
        {
            box.Clear();
            CryptographicOperations.ZeroMemory(MemoryMarshal.AsBytes(chars.AsSpan()));
            Marshal.ZeroFreeGlobalAllocUnicode(pointer);
        }
    }

    // ---------- option rows, Now cards and hints ----------

    internal static TextBlock OptionTitle(string title, string? tag, double size = 16)
    {
        var text = new TextBlock { FontSize = size, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap };
        text.Inlines.Add(title);
        if (tag is not null)
        {
            var badge = new System.Windows.Documents.Run("  \u00b7  " + tag) { FontWeight = FontWeights.Normal, FontSize = size - 2 };
            badge.SetResourceReference(System.Windows.Documents.TextElement.ForegroundProperty, tag == "in use" ? "SuccessBrush" : "AccentBrush");
            text.Inlines.Add(badge);
        }
        return text;
    }

    internal static Border Option(IEnumerable<UIElement> children, bool inUse)
    {
        var stack = new StackPanel();
        foreach (var child in children) stack.Children.Add(child);
        var option = new Border { Child = stack, BorderThickness = new Thickness(inUse ? 2 : 1), CornerRadius = new CornerRadius(14),
            Padding = new Thickness(16), Margin = new Thickness(0, 10, 0, 0) };
        option.SetResourceReference(Border.BorderBrushProperty, inUse ? "AccentBrush" : "BorderBrush");
        return option;
    }

    /// <summary>A wrapping status line with an automation ID (MCP reads it as a value).</summary>
    internal static TextBlock Status(string text, string id, double size = 14)
    {
        var block = new TextBlock { Text = text, FontSize = size, TextWrapping = TextWrapping.Wrap };
        AutomationProperties.SetAutomationId(block, id);
        return block;
    }

    /// <summary>A page's Now card: what it uses, then any problem that stops it. <paramref name="id"/> names its status text for
    /// UI Automation ("<c>id</c>" and "<c>id</c>Problem").</summary>
    internal static Border PageNowCard(string text, Martlet.Core.Platforms.JobCoverage? problem, string? id = null, string? warning = null)
    {
        var now = new StackPanel();
        now.Children.Add(Heading("Now"));
        var status = new TextBlock { Text = text, FontSize = 15, TextWrapping = TextWrapping.Wrap };
        if (id is not null) AutomationProperties.SetAutomationId(status, id);
        now.Children.Add(status);
        // A job's coverage problem, or another reason (such as a character that did not close cleanly) it is not working.
        if ((problem is null ? warning : $"Needs attention: {problem.Problem} {problem.Effect}") is { } line)
        {
            var problemText = new TextBlock { Text = line, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0) };
            problemText.SetResourceReference(TextBlock.ForegroundProperty, "WarningBrush");
            if (id is not null) AutomationProperties.SetAutomationId(problemText, id + "Problem");
            now.Children.Add(problemText);
        }
        return Card(now);
    }

    /// <summary>A text box with a grey <paramref name="hint"/> over it while it is empty. The hint starts where typed text starts:
    /// the box's style (and so its padding) applies only once it is on the page, so the caret's first place sets the hint's margin.</summary>
    internal static Grid WithHint(TextBox box, string hint, string? id = null)
    {
        var shown = new TextBlock
        {
            Text = hint, IsHitTestVisible = false, TextTrimming = TextTrimming.CharacterEllipsis, TextWrapping = TextWrapping.NoWrap,
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(box.Padding.Left + 1, 0, box.Padding.Right + 1, 0),
            Visibility = box.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed
        };
        shown.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
        if (id is not null) AutomationProperties.SetAutomationId(shown, id);
        var placed = false;
        box.SizeChanged += (_, _) =>
        {
            // Text scrolled sideways moves the caret's first place, so only an empty box (or the first layout) is measured.
            if (placed && box.Text.Length > 0) return;
            var start = box.GetRectFromCharacterIndex(0);
            if (start.IsEmpty) return;
            shown.Margin = new Thickness(start.Left, 0, start.Left, 0);
            placed = true;
        };
        box.TextChanged += (_, _) => shown.Visibility = box.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        var grid = new Grid { VerticalAlignment = VerticalAlignment.Center };
        grid.Children.Add(box);
        grid.Children.Add(shown);
        return grid;
    }
}
