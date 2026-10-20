using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Martlet.Core.Speakers;

namespace Martlet.Desktop;

// Companion › People › Voices Martlet knows: one entry per voice, with its names, its clips and its actions.
internal sealed partial class PeoplePage
{
    private Border VoiceEntry(KnownVoice voice, IReadOnlyList<KnownVoice> all)
    {
        var mine = services.Voices.IsYours(voice);
        var stack = new List<UIElement>
        {
            OptionTitle(voice.DisplayName, model.Whose(voice)),
            Note(PeoplePageModel.Heard(voice), new Thickness(0, 2, 0, 4))
        };
        if (model.Twins(voice) is { } twins) stack.Add(Warning(twins));
        stack.Add(VoiceNames(voice));
        if (VoiceClipRow(voice) is { } clips) stack.Add(clips);
        stack.Add(VoiceActions(voice, all));
        return Option(stack, mine);
    }

    /// <summary>Every name the voice goes by as a chip, the one shown first: click a name to show it (which also confirms a
    /// learned one), × to remove it. The box adds a name; on a voice the owner hasn't named, it becomes the name shown.</summary>
    private WrapPanel VoiceNames(KnownVoice voice)
    {
        var row = new WrapPanel { Margin = new Thickness(0, 2, 0, 0) };
        var names = new[] { voice.DisplayName }.Concat(voice.OtherNames).Where(_ => voice.Named).ToArray();
        for (var index = 0; index < names.Length; index++)
        {
            var text = names[index];
            var shown = index == 0;
            var entry = voice.Names.FirstOrDefault(n => string.Equals(n.Text, text, StringComparison.OrdinalIgnoreCase));
            var learned = entry?.Source == VoiceNameSource.Conversation && !string.Equals(voice.Name, text, StringComparison.OrdinalIgnoreCase);
            var label = new Button
            {
                Content = learned ? text + " ?" : text, Padding = new Thickness(0), Margin = new Thickness(0),
                FontWeight = shown ? FontWeights.SemiBold : FontWeights.Normal,
                ToolTip = (learned ? $"Heard in conversation{(entry is { Uses: > 1 } ? $" {entry.Uses} times" : "")}. " : "") +
                    (shown && !learned ? "The name Martlet uses." : shown ? "Click to confirm it." : "Click to make it the name Martlet uses.")
            };
            label.SetResourceReference(FrameworkElement.StyleProperty, "LinkButton");
            label.SetResourceReference(Control.ForegroundProperty, "TextBrush");
            AutomationProperties.SetName(label, shown ? $"{text}, the name shown" : $"Show {text} as the name");
            AutomationProperties.SetAutomationId(label, $"PeopleNameShow-{voice.Number}-{index}");
            label.Click += (_, _) => { if (!shown || learned) model.SaveNames(voice, _ => text, all => all); };
            var remove = new Button { Content = "\u00d7", Padding = new Thickness(6, 0, 0, 0), Margin = new Thickness(0), ToolTip = $"Remove {text}" };
            remove.SetResourceReference(FrameworkElement.StyleProperty, "LinkButton");
            AutomationProperties.SetName(remove, $"Remove {text}");
            AutomationProperties.SetAutomationId(remove, $"PeopleNameRemove-{voice.Number}-{index}");
            remove.Click += (_, _) => model.SaveNames(voice,
                shownName => string.Equals(shownName, text, StringComparison.OrdinalIgnoreCase) ? null : shownName,
                all => all.Where(n => !string.Equals(n, text, StringComparison.OrdinalIgnoreCase)));
            var chip = new Border { Child = new StackPanel { Orientation = Orientation.Horizontal, Children = { label, remove } } };
            chip.SetResourceReference(FrameworkElement.StyleProperty, "Chip");
            chip.VerticalAlignment = VerticalAlignment.Center;
            if (shown) chip.SetResourceReference(Border.BorderBrushProperty, "AccentBrush");
            row.Children.Add(chip);
        }

        var full = voice.Names.Count >= VoiceRoster.MaximumNames;
        var box = new TextBox { Width = 170, MaxLength = VoiceRoster.MaximumNameLength, IsEnabled = !full,
            VerticalContentAlignment = VerticalAlignment.Center, Padding = new Thickness(8, 3, 8, 3), MinHeight = 30 };
        AutomationProperties.SetName(box, voice.Named ? $"Add another name for {voice.DisplayName}" : $"Name {voice.DisplayName}");
        AutomationProperties.SetAutomationId(box, "PeopleAddName-" + voice.Number);
        var hint = Note(full ? $"{VoiceRoster.MaximumNames} names at most" : voice.Named ? "Add a name..." : "Who is this?", new Thickness(10, 0, 0, 0));
        hint.IsHitTestVisible = false;
        hint.VerticalAlignment = VerticalAlignment.Center;
        box.TextChanged += (_, _) => hint.Visibility = box.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        void Add() => model.AddName(voice, box.Text);
        box.KeyDown += (_, args) => { if (args.Key == System.Windows.Input.Key.Enter) Add(); };
        var field = new Grid { Margin = new Thickness(0, 4, 6, 0), VerticalAlignment = VerticalAlignment.Center };
        field.Children.Add(box);
        field.Children.Add(hint);
        row.Children.Add(field);
        var add = PageButton("Add", Add, link: true, id: "PeopleAddNameButton-" + voice.Number);
        add.IsEnabled = !full;
        add.Margin = new Thickness(0, 4, 0, 0);
        add.VerticalAlignment = VerticalAlignment.Center;
        row.Children.Add(add);
        return row;
    }

    /// <summary>The voice's last few clips, until the owner names it: play one to hear who it is.</summary>
    private WrapPanel? VoiceClipRow(KnownVoice voice)
    {
        if (!VoiceClips.Wanted(voice)) return null;
        var clips = services.Voices.Clips.List(voice.Id);
        if (clips.Count == 0) return null;
        var row = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
        var label = Note($"Hear them ({clips.Count}):", new Thickness(0, 0, 10, 0));
        label.VerticalAlignment = VerticalAlignment.Center;
        AutomationProperties.SetAutomationId(label, "PeopleClips-" + voice.Number);
        row.Children.Add(label);
        for (var index = 0; index < clips.Count; index++)
        {
            var clip = clips[index];
            var play = PageButton($"\u25b6 {PeoplePageModel.Ago(clip.At)}", () => services.PlayClip(clip), link: true, id: $"PeopleClip-{voice.Number}-{index}");
            play.ToolTip = $"{clip.Seconds:0.#} s, {clip.At.LocalDateTime:g}";
            row.Children.Add(play);
        }
        return row;
    }

    /// <summary>This voice is (yours), Same person as (then Merge), what Martlet remembers about them and Forget, on one line.</summary>
    private WrapPanel VoiceActions(KnownVoice voice, IReadOnlyList<KnownVoice> all)
    {
        var row = new WrapPanel { Margin = new Thickness(0, 10, 0, 0) };
        var you = services.Voices.Account is { } signedIn ? services.AccountName(signedIn) : null;
        var owner = new CheckBox { Content = you is null ? "This is me" : $"This voice is {you}", IsChecked = services.Voices.IsYours(voice),
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 16, 6),
            ToolTip = "Links this voice to your account, so Martlet knows it's you talking (a voice never signs anyone in)." };
        AutomationProperties.SetName(owner, "This voice is mine");
        AutomationProperties.SetAutomationId(owner, "PeopleLink-" + voice.Number);
        owner.Checked += (_, _) => model.Link(voice, true);
        owner.Unchecked += (_, _) => model.Link(voice, false);
        row.Children.Add(owner);

        var targets = all.Where(v => v.Id != voice.Id).ToArray();
        if (targets.Length > 0)
        {
            var merge = new ComboBox { Width = 190, ItemsSource = targets.Select(v => v.Named ? $"{v.DisplayName} (Voice {v.Number})" : $"Voice {v.Number}").ToArray() };
            AutomationProperties.SetName(merge, $"Same person as (merge {voice.DisplayName} into)");
            AutomationProperties.SetAutomationId(merge, "PeopleMergeTarget-" + voice.Number);
            var hint = Note("Same person as...", new Thickness(10, 0, 0, 0));
            hint.IsHitTestVisible = false;
            hint.VerticalAlignment = VerticalAlignment.Center;
            var field = new Grid { Margin = new Thickness(0, 0, 8, 6), VerticalAlignment = VerticalAlignment.Center };
            field.Children.Add(merge);
            field.Children.Add(hint);
            row.Children.Add(field);
            var mergeButton = PageButton("Merge", () =>
            {
                if (merge.SelectedIndex >= 0) model.Merge(voice, targets[merge.SelectedIndex]);
            }, link: true, id: "PeopleMerge-" + voice.Number);
            mergeButton.IsEnabled = false;
            mergeButton.VerticalAlignment = VerticalAlignment.Center;
            mergeButton.Margin = new Thickness(0, 0, 16, 6);
            merge.SelectionChanged += (_, _) =>
            {
                hint.Visibility = merge.SelectedIndex < 0 ? Visibility.Visible : Visibility.Collapsed;
                mergeButton.IsEnabled = merge.SelectedIndex >= 0;
            };
            row.Children.Add(mergeButton);
        }

        var memories = PageButton("Memories", () => services.OpenMemory(voice.Id), link: true, id: "PeopleMemories-" + voice.Number);
        memories.ToolTip = "What Martlet remembers about them";
        foreach (var link in new[] { memories, PageButton("Forget", () => model.Forget(voice), link: true, id: "PeopleForget-" + voice.Number) })
        {
            link.VerticalAlignment = VerticalAlignment.Center;
            link.Margin = new Thickness(0, 0, 16, 6);
            row.Children.Add(link);
        }
        return row;
    }
}
