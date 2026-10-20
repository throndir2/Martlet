using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using Martlet.Avatar.Hosting;

namespace Martlet.Desktop;

/// <summary>Companion › Eyes › Where the face is: where the shown model's eyes come from (its own meshes or eye bones, a vision
/// measurement or an estimate), whether the blush uses measured cheeks, Measure the face and Forget the measurement. The
/// measurement itself (also the one Martlet takes on its own for a new model) runs in the window (MainWindow.CharacterEyes.cs).</summary>
internal sealed partial class EyesPage
{
    private TextBlock? eyesStatus, eyesProgress;

    /// <summary>What the Eyes rows showed when the page was drawn: a new model, measurement or busy state draws them again.</summary>
    internal (string? Model, CharacterEyeMeasurement? Measurement, bool Busy) RenderedEyes { get; private set; }

    /// <summary>Shows where the eyes come from and how measuring goes on the page last drawn.</summary>
    internal void ShowEyes()
    {
        if (eyesStatus is not null) eyesStatus.Text = EyesPageModel.Status(services);
        if (eyesProgress is not null)
        {
            var progress = services.Eyes.Progress;
            eyesProgress.Text = progress ?? "";
            eyesProgress.Visibility = progress is null ? Visibility.Collapsed : Visibility.Visible;
        }
    }

    /// <summary>Companion › Eyes › Where the face is: where the eyes come from, how measuring went, Measure the face and Forget
    /// the measurement, and the close-up Thinking saw with its boxes.</summary>
    private Border FaceCard(EyesPageModel model)
    {
        var eyes = services.Eyes;
        RenderedEyes = (eyes.ModelId, eyes.Current, model.Busy);
        var stack = new List<UIElement>
        {
            Heading("Where the face is"),
            HelpTip.Explain("Some emotes are drawn over the eyes (heart eyes, star eyes, dizzy swirls), and the blush goes on the " +
                "cheeks. They fit the face when Martlet knows where its eyes and cheeks are. When a new character shows and a model " +
                "that can see is set up, your Thinking model measures its eyes, cheeks and mouth once, in a close-up of the face " +
                "drawn off screen in the rest pose (so it works while the character is hidden), in the background and never while " +
                "Martlet replies. The model's own eye data comes first unless it clearly misses the measured eye. Press Measure the " +
                "face to measure again at any time.", new Thickness(0, 0, 0, 4), "CharacterEyes", "emotes over the eyes and the blush")
        };
        eyesStatus = Note(EyesPageModel.Status(services), new Thickness(0, 0, 0, 4));
        AutomationProperties.SetAutomationId(eyesStatus, "CharacterEyesStatus");
        AutomationProperties.SetLiveSetting(eyesStatus, AutomationLiveSetting.Polite);
        stack.Add(eyesStatus);
        eyesProgress = Note(eyes.Progress ?? "", new Thickness(0, 0, 0, 4));
        eyesProgress.Visibility = eyes.Progress is null ? Visibility.Collapsed : Visibility.Visible;
        AutomationProperties.SetAutomationId(eyesProgress, "CharacterEyesProgress");
        AutomationProperties.SetLiveSetting(eyesProgress, AutomationLiveSetting.Polite);
        stack.Add(eyesProgress);

        var sight = services.Sight();
        var measure = PageButton(model.MeasureLabel, services.Measure, id: "CharacterEyesMeasure");
        measure.IsEnabled = model.CanMeasure(sight.CanSee);
        AutomationProperties.SetHelpText(measure, "Shows your Thinking model a close-up of the character's face (never its files) to find each " +
            "iris and eye, each cheek and the mouth. Martlet draws the character off screen in its rest pose, so it works while the character is hidden.");
        var forget = PageButton("Forget the measurement", services.Forget, id: "CharacterEyesForget");
        forget.IsEnabled = model.CanForget;
        AutomationProperties.SetHelpText(forget, "Deletes this model's measurement and its pictures. The eyes and the blush then use the model's own data or an estimate.");
        stack.Add(Row(measure, forget));
        if (!sight.CanSee)
        {
            var off = Warning("Measure the face is off: no model that can see pictures is set up; choose a vision-capable model in " +
                "Companion › Thinking or Companion › Thinking pool.");
            AutomationProperties.SetAutomationId(off, "CharacterEyesNote");
            stack.Add(off);
        }
        if (eyes.BoxesPicture is { } path)
        {
            try
            {
                var bitmap = PictureFiles.At(path, decodeHeight: 0, decodeWidth: 480);
                var picture = new Image { Source = bitmap, Width = 240, Height = 240 * bitmap.PixelHeight / Math.Max(1, bitmap.PixelWidth),
                    Stretch = Stretch.Uniform, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 4, 0, 4) };
                AutomationProperties.SetAutomationId(picture, "CharacterEyesPicture");
                AutomationProperties.SetName(picture, "The close-up Thinking saw, with its boxes: 1 and 2 the left iris and eye, 3 and 4 the right ones, " +
                    "5 and 6 the left and right cheeks, 7 the mouth");
                stack.Add(picture);
                stack.Add(Legend());
            }
            catch (Exception error) when (error is IOException or NotSupportedException or UriFormatException or InvalidOperationException) { }
            var open = PageButton("Open the pictures and answers", () => PictureFiles.OpenFolder(Path.GetDirectoryName(path)!), id: "CharacterEyesOpen");
            AutomationProperties.SetHelpText(open, "Opens the folder with each picture sent to the Thinking model and, beside it, a text file with " +
                "the exact instructions, message and answer.");
            stack.Add(Row(open));
        }
        return Card([.. stack]);
    }

    /// <summary>What each numbered box on the close-up is, each name in its box's color; "left" and "right" as you see them.</summary>
    private static TextBlock Legend()
    {
        var legend = Note("", new Thickness(0, 0, 0, 4));
        legend.Inlines.Add(new System.Windows.Documents.Run("Boxes, left and right as you see them: "));
        var marks = CharacterEyes.Marks(CharacterEyes.OldExample);
        for (var i = 0; i < marks.Count; i++)
        {
            var color = marks[i].Color;
            if (i > 0) legend.Inlines.Add(new System.Windows.Documents.Run(", "));
            legend.Inlines.Add(new System.Windows.Documents.Run($"{marks[i].Number} {CharacterEyes.MarkNames[marks[i].Number - 1]}")
            {
                Foreground = new SolidColorBrush(Color.FromRgb(color.R, color.G, color.B)), FontWeight = FontWeights.SemiBold
            });
        }
        AutomationProperties.SetAutomationId(legend, "CharacterEyesLegend");
        return legend;
    }
}
