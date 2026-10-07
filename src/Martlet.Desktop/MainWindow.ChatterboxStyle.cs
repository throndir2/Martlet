using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Martlet.Core.Settings;

namespace Martlet.Desktop;

/// <summary>Companion › Voice › Chatterbox Original style: the exaggeration and CFG weight of its General and Expressive ways of
/// speaking (<see cref="ChatterboxStyle"/>), shown while Speaking uses Chatterbox Original or it is the chosen engine. The reply
/// picks Expressive for a sentence with [expressive]; every other sentence is General. Each slider saves at once and the next
/// sentence uses it. Reads as <c>ChatterboxStyle-&lt;name&gt;</c> (the sliders: GeneralExaggeration, GeneralCfgWeight,
/// ExpressiveExaggeration, ExpressiveCfgWeight), <c>ChatterboxStyleValue-&lt;name&gt;</c> (each value, "0.5"),
/// <c>ChatterboxStyleState</c> (what is saved) and <c>ChatterboxStyleReset</c>.</summary>
public partial class MainWindow
{
    private Border ChatterboxStyleCard()
    {
        var directory = store?.DataDirectory;
        var style = directory is null ? ChatterboxStyle.Default : ChatterboxStyle.Load(directory);
        var state = Note(StyleState(style), new Thickness(0, 10, 0, 0));
        AutomationProperties.SetAutomationId(state, "ChatterboxStyleState");
        var sliders = new Dictionary<string, Slider>();
        var resetting = false;

        void Save(ChatterboxStyle next)
        {
            if (next == style || directory is null) return;
            try
            {
                next.Save(directory);
                style = next;
                state.Text = StyleState(next);
                state.ClearValue(TextBlock.ForegroundProperty);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentOutOfRangeException)
            {
                state.Text = $"Couldn't save the style: {error.Message}";
                state.SetResourceReference(TextBlock.ForegroundProperty, "WarningBrush");
            }
        }

        UIElement Setting(string name, string label, double value, bool exaggeration, Func<ChatterboxStyle, double, ChatterboxStyle> with)
        {
            var slider = new Slider
            {
                Minimum = exaggeration ? ChatterboxStyle.MinimumExaggeration : ChatterboxStyle.MinimumCfgWeight,
                Maximum = exaggeration ? ChatterboxStyle.MaximumExaggeration : ChatterboxStyle.MaximumCfgWeight,
                Value = value, SmallChange = 0.05, LargeChange = exaggeration ? 0.25 : 0.1, TickFrequency = 0.05,
                IsSnapToTickEnabled = true, Width = 240, VerticalAlignment = VerticalAlignment.Center
            };
            AutomationProperties.SetName(slider, label);
            AutomationProperties.SetAutomationId(slider, "ChatterboxStyle-" + name);
            var shown = Note(ChatterboxStyle.Number(value), new Thickness(8, 0, 0, 0));
            shown.VerticalAlignment = VerticalAlignment.Center;
            AutomationProperties.SetAutomationId(shown, "ChatterboxStyleValue-" + name);
            slider.ValueChanged += (_, e) =>
            {
                var next = Math.Round(e.NewValue, 2);
                shown.Text = ChatterboxStyle.Number(next);
                if (!resetting) Save(with(style, next));
            };
            sliders[name] = slider;
            var scale = new StackPanel { Orientation = Orientation.Horizontal };
            scale.Children.Add(slider);
            scale.Children.Add(shown);
            return Labeled(exaggeration ? "Exaggeration" : "CFG weight", scale);
        }

        var reset = PageButton("Use Resemble's suggestions", () =>
        {
            var defaults = ChatterboxStyle.Default;
            resetting = true;
            sliders["GeneralExaggeration"].Value = defaults.GeneralExaggeration;
            sliders["GeneralCfgWeight"].Value = defaults.GeneralCfgWeight;
            sliders["ExpressiveExaggeration"].Value = defaults.ExpressiveExaggeration;
            sliders["ExpressiveCfgWeight"].Value = defaults.ExpressiveCfgWeight;
            resetting = false;
            Save(defaults);
        }, link: true, id: "ChatterboxStyleReset");

        static TextBlock Group(string text)
        {
            var heading = new TextBlock { Text = text, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 12, 0, 0) };
            return heading;
        }

        return Card(Heading("Chatterbox Original style"),
            Note("Martlet says each sentence in the General style, and a sentence the reply starts with [expressive] in the " +
                "Expressive style, so the conversation decides when to sound animated. Exaggeration is how much emotion (0.5 is " +
                "neutral; very high values can be unstable). CFG weight is how closely it follows your voice sample; lower is " +
                "slower and more deliberate. Resemble AI suggests 0.5 and 0.5 for everyday speech, and about 0.7 and 0.3 for " +
                "expressive or dramatic speech. If your voice sample speaks fast, try a CFG weight of about 0.3.", new Thickness(0, 0, 0, 0)),
            Group("General: most sentences"),
            Setting("GeneralExaggeration", "General exaggeration: how much emotion in most sentences", style.GeneralExaggeration, true,
                (s, v) => s with { GeneralExaggeration = v }),
            Setting("GeneralCfgWeight", "General CFG weight: lower is slower and more deliberate", style.GeneralCfgWeight, false,
                (s, v) => s with { GeneralCfgWeight = v }),
            Group("Expressive: sentences the reply starts with [expressive]"),
            Setting("ExpressiveExaggeration", "Expressive exaggeration: how much emotion in expressive sentences",
                style.ExpressiveExaggeration, true, (s, v) => s with { ExpressiveExaggeration = v }),
            Setting("ExpressiveCfgWeight", "Expressive CFG weight: lower is slower and more deliberate", style.ExpressiveCfgWeight, false,
                (s, v) => s with { ExpressiveCfgWeight = v }),
            state,
            Row(reset));
    }

    /// <summary>What the style card says is saved: "Saved on this PC. General: exaggeration 0.5, CFG weight 0.5. ..."</summary>
    internal static string StyleState(ChatterboxStyle style) =>
        (style == ChatterboxStyle.Default ? "Resemble's suggestions. " : "Saved on this PC. ") + style.Describe();
}
