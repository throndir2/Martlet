using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Martlet.Core.Settings;
using Martlet.Core.Voices;
using Martlet.F5;

namespace Martlet.Desktop;

/// <summary>Companion › Voice › Voices: the voices Martlet speaks with, copied from short recordings (no training). There are
/// no built-in voices: a new list starts with a few starter voices, and every voice can be heard, used in one click and
/// removed. MainWindow.Voices.cs adds, records, plays, removes and chooses them and shares them with your paired computers.</summary>
internal sealed partial class VoiceCards
{
    internal Border Voices(SetupRoute? route)
    {
        var f5 = route?.RouteType == SetupRouteType.GatewayF5 ? route : null;
        var destination = f5?.GatewaySnapshot?.DestinationId ?? services.F5Destination;
        // The engine that speaks (or will): some clone only recordings of certain lengths (GPT-SoVITS: 3-10 seconds).
        var engine = SpeechEngines.ForRoute(f5?.GatewaySnapshot?.RouteId) ?? SpeakingEngineChoice.Current;
        string? Cannot(VoiceItem item) => SpeechEngines.ReferenceProblem(engine, item.DurationMilliseconds, item.Clips);
        string Language(string? transcript) => engine == SpeechEngines.GptSovits && transcript is not null
            ? SpeechEngines.ReferenceLanguage(transcript) == "ja" ? " Japanese recording." : " English recording."
            : "";
        var stack = new List<UIElement>
        {
            Heading("Voices"),
            HelpTip.Explain($"{engine.Name} copies these voices from short recordings" + (engine.SampleLimits is { } limits ? $" ({limits})" : "") +
                ". A voice can have several recordings" + (engine.MultipleReferences ? $"; {engine.Name} learns from each." : ", heard one after another.") +
                " They're shared with your paired computers.", new Thickness(0, 0, 0, 10), "VoicesAbout", "your voices", wiki: "Voices#your-voices")
        };
        var library = SpeakingVoiceLibrary.Empty;
        IReadOnlyDictionary<string, F5ReferenceSnapshot> local = new Dictionary<string, F5ReferenceSnapshot>();
        F5ReferenceSnapshot? applied = null;
        try
        {
            if (services.VoiceLibrary() is { } saved)
            {
                library = saved;
                (local, applied) = services.LocalVoices(destination);
            }
        }
        catch (F5Exception error) { stack.Add(Note(F5Voices.Describe(error), new Thickness(0, 0, 0, 8))); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            stack.Add(Note("Martlet couldn't read your voices: " + error.Message, new Thickness(0, 0, 0, 8)));
        }
        var items = VoiceItem.List(library, local);
        // The voice in use: the one the speaking route keeps when a computer speaks, else the one applied on this PC.
        var inUseId = f5?.Reference?.ReferenceRevision ?? applied?.ReferenceRevision;
        var mark = f5 is null ? "chosen" : "in use";
        var inUse = items.FirstOrDefault(i => i.Id == inUseId);
        var status = Note($"{items.Count} voice{(items.Count == 1 ? "" : "s")}; " + (inUse is not null
                ? $"{mark}: {(inUse.Starter ? inUse.Name : "one of your recordings")}."
                : inUseId is not null ? $"{mark}: a voice no longer in the list."
                : items.FirstOrDefault(i => i.Available) is { } first ? $"none {mark} yet, so Martlet starts with {(first.Starter ? first.Name : "your first voice")}."
                : "add a voice to speak with."),
            new Thickness(0, 0, 0, 4));
        AutomationProperties.SetAutomationId(status, "F5VoicesStatus");
        stack.Add(status);
        var shared = Note(services.SharedVoicesStatus, new Thickness(0, 0, 0, 10));
        AutomationProperties.SetAutomationId(shared, "F5VoicesShared");
        stack.Add(shared);

        var chosenId = library.ChosenVoice?.Id;
        // A voice made from several recordings says how many and whether the engine learns from each or hears them joined.
        string Recorded(VoiceItem item) => item.Clips is { Count: > 1 } clips
            ? $"{clips.Count} recordings, {item.DurationMilliseconds / 1000d:0.#} seconds joined, added {item.AddedAt.ToLocalTime():d}; " +
              (SpeechEngines.UsesClips(engine, clips) ? $"{engine.Name} learns from each recording."
                  : $"{engine.Name} hears them one after another with a short pause.")
            : $"{item.DurationMilliseconds / 1000d:0.#} second recording, added {item.AddedAt.ToLocalTime():d}.";
        foreach (var item in items)
        {
            var used = item.Id == inUseId;
            var cannot = Cannot(item);
            var detail = (item.Note ?? Recorded(item)) +
                (item.Available ? "" : " Copying to this PC...") + (cannot is null ? Language(item.Transcript) : " " + cannot);
            stack.Add(VoiceRow(item.Name, detail, used, mark, item.Key, status: item.Starter,
                () => services.PlayVoice(item), () => services.UseVoice(item.Id, destination),
                used || item.Id == chosenId ? null : () => services.RemoveVoice(item, destination), cannot, item.Available));
        }
        // A voice still in use here that is not in the list (removed on another computer, or a recording Martlet no longer ships).
        if (inUseId is not null && inUse is null)
        {
            var name = f5?.Reference?.PresetName ?? applied?.PresetName ?? "Previous voice";
            stack.Add(VoiceRow(name, "No longer in your voice list.", true, mark,
                inUseId[..16], status: false, () => services.PlayApplied(applied, name), () => Task.CompletedTask, null));
        }
        stack.Add(Row(PageButton("Add a voice...", () => services.AddVoice(destination), id: "F5AddVoice"),
            PageButton("Record your own voice...", () => services.RecordVoice(destination), id: "F5RecordVoice")));
        return Card([.. stack]);
    }

    /// <summary>One voice: its name (with the in-use mark), a detail line, and Play, Use and (when it may go) Remove. Controls
    /// are identified by <paramref name="key"/>; starter recordings' titles are also readable status (<c>F5VoiceRow-key</c>),
    /// never the names of the owner's own recordings, and every voice's detail line (<c>F5VoiceDetail-key</c>: its length or
    /// recordings, where it comes from and why the engine can't use it; never its name or words) is readable status.
    /// <paramref name="cannot"/> says why the speaking engine cannot use it and <paramref name="available"/> is false while its
    /// recording is still being copied here (Use is then off).</summary>
    private static DockPanel VoiceRow(string name, string detail, bool inUse, string mark, string key, bool status, Func<Task> play, Func<Task> use,
        Func<Task>? remove, string? cannot = null, bool available = true)
    {
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        var title = new TextBlock { Text = name + (inUse ? $"  \u00b7  {mark}" : "") + (cannot is null ? "" : "  \u00b7  wrong length for this engine"),
            FontSize = 15, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap };
        if (status) AutomationProperties.SetAutomationId(title, $"F5VoiceRow-{key}");
        text.Children.Add(title);
        var line = Note(detail, new Thickness(0, 2, 0, 0));
        AutomationProperties.SetAutomationId(line, $"F5VoiceDetail-{key}");
        text.Children.Add(line);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        Button Small(string label, Func<Task> run, string id)
        {
            var button = PageButton(label, () => run().Forget(), id: $"F5Voice{id}-{key}");
            button.MinWidth = 72;
            button.Margin = new Thickness(8, 0, 0, 0);
            AutomationProperties.SetName(button, $"{label} {name}");
            return button;
        }
        var playButton = Small("Play", play, "Play");
        playButton.IsEnabled = available;
        buttons.Children.Add(playButton);
        var useButton = Small(inUse ? "In use" : "Use", use, "Use");
        useButton.IsEnabled = !inUse && cannot is null && available;
        if (cannot is not null)
        {
            useButton.ToolTip = cannot;
            ToolTipService.SetShowOnDisabled(useButton, true);
            AutomationProperties.SetHelpText(useButton, cannot);
        }
        buttons.Children.Add(useButton);
        if (remove is not null) buttons.Children.Add(Small("Remove", remove, "Remove"));
        var row = new DockPanel { Margin = new Thickness(0, 0, 0, 10) };
        DockPanel.SetDock(buttons, Dock.Right);
        row.Children.Add(buttons);
        row.Children.Add(text);
        return row;
    }
}
