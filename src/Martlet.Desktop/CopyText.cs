using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Threading;
#if MARTLET_RENDERER
using Martlet.Avatar.RendererHost.Logging;
#elif MARTLET_MCP
using Martlet.Mcp.Logging;
#endif

// Shared with the character renderer, which compiles Themes\Controls.xaml too.
namespace Martlet.Presentation;

/// <summary>Copy buttons for Martlet's text, so output, errors and reports paste straight into a chat when asking for help.
/// Every read-only text box shows one (its template in Themes\Controls.xaml) unless <see cref="ButtonProperty"/> is false;
/// it copies all of the box's text, after its <see cref="ContextProperty"/> when set (for example a run's title and status).
/// The button's automation ID is "Copy-" plus the box's, and it reads "Copied" for a few seconds afterwards.</summary>
public static class CopyText
{
    public static readonly RoutedUICommand Command = new("Copy all", "CopyAll", typeof(CopyText));

    /// <summary>Whether a read-only text box shows its Copy button (default true).</summary>
    public static readonly DependencyProperty ButtonProperty = DependencyProperty.RegisterAttached(
        "Button", typeof(bool), typeof(CopyText), new FrameworkPropertyMetadata(true));

    /// <summary>Text copied before the box's own text, separated by a blank line.</summary>
    public static readonly DependencyProperty ContextProperty = DependencyProperty.RegisterAttached(
        "Context", typeof(string), typeof(CopyText), new FrameworkPropertyMetadata(null));

    private static readonly DependencyProperty RestingProperty = DependencyProperty.RegisterAttached(
        "Resting", typeof(object), typeof(CopyText), new FrameworkPropertyMetadata(null));
    private static readonly DependencyProperty TimerProperty = DependencyProperty.RegisterAttached(
        "Timer", typeof(DispatcherTimer), typeof(CopyText), new FrameworkPropertyMetadata(null));

    public const string Help = "Copies all of this text, for example to paste into an AI chat when asking for help.";

    /// <summary>"Martlet x.y.z", for the context of copied reports.</summary>
    public static string Product { get; } =
        "Martlet " + (typeof(CopyText).Assembly.GetName().Version is { } version ? version.ToString(3) : "0.0.0");

    /// <summary>The template's button ID: "Copy-" plus the box's automation ID, or its name when it has none.</summary>
    public static IMultiValueConverter IdConverter { get; } = new CopyId();

    static CopyText() => CommandManager.RegisterClassCommandBinding(typeof(TextBox), new CommandBinding(Command, Copy));

    public static bool GetButton(DependencyObject element) => (bool)element.GetValue(ButtonProperty);
    public static void SetButton(DependencyObject element, bool value) => element.SetValue(ButtonProperty, value);
    public static string? GetContext(DependencyObject element) => (string?)element.GetValue(ContextProperty);
    public static void SetContext(DependencyObject element, string? value) => element.SetValue(ContextProperty, value);

    /// <summary>What a box's Copy button copies.</summary>
    internal static string Of(TextBox box) => GetContext(box) is { } context && !string.IsNullOrWhiteSpace(context)
        ? context.TrimEnd() + Environment.NewLine + Environment.NewLine + box.Text
        : box.Text;

    private static void Copy(object sender, ExecutedRoutedEventArgs e)
    {
        if (sender is not TextBox box) return;
        e.Handled = true;
        var copied = ToClipboard(Of(box));
        if (e.OriginalSource is Button button) Acknowledge(button, copied);
    }

    /// <summary>A dialog's own Copy button for <paramref name="text"/> (read when clicked).</summary>
    internal static Button DialogButton(string automationId, Func<string> text)
    {
        var button = new Button { Content = "Cop_y", MinWidth = 90 };
        AutomationProperties.SetAutomationId(button, automationId);
        AutomationProperties.SetHelpText(button, Help);
        button.Click += (_, _) => From(button, text());
        return button;
    }

    /// <summary>Copies <paramref name="text"/> for <paramref name="button"/> and shows on it whether that worked.</summary>
    internal static void From(Button button, string text) => Acknowledge(button, ToClipboard(text));

    /// <summary>Puts <paramref name="text"/> on the clipboard; false when another app holds it (WPF already retries).</summary>
    internal static bool ToClipboard(string text)
    {
        try
        {
            Clipboard.SetText(text);
            return true;
        }
        catch (ExternalException error)
        {
            ErrorLog.Warn("Couldn't copy to the clipboard", error);
            return false;
        }
    }

    private static void Acknowledge(Button button, bool copied)
    {
        if (button.GetValue(TimerProperty) is DispatcherTimer running) running.Stop();
        else button.SetValue(RestingProperty, button.Content);
        button.Content = copied ? "Copied" : "Couldn't copy";
        AutomationProperties.SetLiveSetting(button, AutomationLiveSetting.Polite);
        UIElementAutomationPeer.FromElement(button)?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
        var timer = new DispatcherTimer(DispatcherPriority.Normal, button.Dispatcher) { Interval = TimeSpan.FromSeconds(3) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            button.Content = button.GetValue(RestingProperty);
            button.ClearValue(RestingProperty);
            button.ClearValue(TimerProperty);
        };
        button.SetValue(TimerProperty, timer);
        timer.Start();
    }

    private sealed class CopyId : IMultiValueConverter
    {
        public object Convert(object[] values, Type targetType, object? parameter, CultureInfo culture) =>
            values.OfType<string>().FirstOrDefault(id => id.Length > 0) is { } id ? "Copy-" + id : "";

        public object[] ConvertBack(object value, Type[] targetTypes, object? parameter, CultureInfo culture) =>
            throw new NotSupportedException();
    }
}
