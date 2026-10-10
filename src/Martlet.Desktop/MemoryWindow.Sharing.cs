using System.Windows;
using System.Windows.Controls;
using Martlet.Core.Sync;
using Martlet.Memory;

namespace Martlet.Desktop;

/// <summary>A place the Memory window can share facts to: the household's memories, another person's memories or a character
/// shared together (docs/ACCOUNTS.md, "Sharing").</summary>
internal sealed record MemoryShareTarget(string Space, string Label);

/// <summary>What the Memory window needs to share facts, from the main window: the places to share to, how to give facts to a space
/// this PC doesn't keep (through the paired hosts; returns how many the space took), this PC's device ID, and *Share new memories
/// about me* with how to change it.</summary>
internal sealed record MemorySharingOptions(
    Func<IReadOnlyList<MemoryShareTarget>> Targets,
    Func<string, SharedMemories, CancellationToken, Task<int>> Give,
    string DeviceId,
    bool AboutMe,
    Func<bool, Task<bool>> SetAboutMe);

/// <summary>The Memory window's sharing (docs/ACCOUNTS.md, "Sharing"): *Share selected with* copies or moves the selected facts to the
/// household, another person or a character shared together, and *Share new memories about me with the household* sends new
/// facts about you to the household's memories. Both show only with accounts. Copies keep the words, whose fact it is and how
/// long it is kept. A move deletes the facts here only after the copy is made. <c>MemoryShareStatus</c> says what happened, with
/// counts only.</summary>
public partial class MemoryWindow
{
    private MemorySharingOptions? sharing;
    private bool renderingShare;

    /// <summary>Sharing for this window; null (no accounts) hides it.</summary>
    internal MemorySharingOptions? Sharing
    {
        get => sharing;
        init
        {
            sharing = value;
            if (value is null) return;
            SharePanel.Visibility = Visibility.Visible;
            ShareAboutMeChoice.Visibility = Visibility.Visible;
            renderingShare = true;
            ShareAboutMeChoice.IsChecked = value.AboutMe;
            renderingShare = false;
            FactsList.SelectionChanged += (_, _) => RenderShare();
            RenderShare();
        }
    }

    /// <summary>The places the selected facts can go: every target except a space all of them are in already.</summary>
    private void RenderShare()
    {
        if (sharing is null || closed) return;
        var selected = SelectedFacts();
        var current = (ShareTarget.SelectedItem as ComboBoxItem)?.Tag as string;
        renderingShare = true;
        try
        {
            ShareTarget.Items.Clear();
            foreach (var target in sharing.Targets())
            {
                if (selected.Count > 0 && selected.All(f => (SpaceOf(f) ?? activeSpace) == target.Space)) continue;
                var item = new ComboBoxItem { Content = target.Label, Tag = target.Space };
                ShareTarget.Items.Add(item);
                if (target.Space == current) ShareTarget.SelectedItem = item;
            }
            if (ShareTarget.SelectedIndex < 0 && ShareTarget.Items.Count > 0) ShareTarget.SelectedIndex = 0;
        }
        finally { renderingShare = false; }
        var ready = CurrentEnabledConfiguration() && !operations.IsRunning && selected.Count > 0 && ShareTarget.SelectedItem is not null;
        ShareCopyButton.IsEnabled = ready;
        ShareMoveButton.IsEnabled = ready && selected.All(Changeable);
        ShareTarget.IsEnabled = ShareTarget.Items.Count > 0;
    }

    private async void ShareCopy_Click(object sender, RoutedEventArgs e) => await ShareAsync(move: false);

    private async void ShareMove_Click(object sender, RoutedEventArgs e) => await ShareAsync(move: true);

    /// <summary>Copies (or moves) the selected facts to the chosen place. A place this PC keeps (the household's memories, the
    /// character in use) gets new facts here, and the memory sync takes them to your other computers; another person's memories
    /// get them through your paired hosts.</summary>
    private async Task ShareAsync(bool move)
    {
        if (sharing is not { } options || closed || !RequireCurrentEnabledConfiguration(ShareStatus)) return;
        if ((ShareTarget.SelectedItem as ComboBoxItem)?.Tag is not string target) return;
        // The status line names the kind of place, never a person (MCP reads it).
        var label = target == MemorySpaceId.Household ? "the household's memories"
            : target.StartsWith(MemorySpaceId.CharacterPrefix, StringComparison.Ordinal) ? "a shared character's memories" : "another person's memories";
        var chosen = SelectedFacts().Where(f => (SpaceOf(f) ?? activeSpace) != target).ToArray();
        if (chosen.Length == 0) return;
        if (move && !chosen.All(Changeable)) return;
        var shared = 0;
        var moved = 0;
        await RunAsync(async token =>
        {
            var spaces = await service.SpacesAsync(token).ConfigureAwait(false);
            if (spaces?.Find(target) is { Writable: true })
            {
                var now = service.UtcNow;
                foreach (var fact in chosen.Where(f => !f.Retention.HasExpired(now)))
                {
                    await service.SaveFactAsync(configurationRevision, fact.Content, fact.Retention, fact.VoiceId, token, target).ConfigureAwait(false);
                    shared++;
                }
            }
            else
                shared = await options.Give(target, MemorySharing.Gift(chosen, options.DeviceId, service.UtcNow), token).ConfigureAwait(false);
            if (!move || shared < chosen.Length) return;
            foreach (var group in chosen.GroupBy(SpaceOf))
                moved += (await service.DeleteFactsAsync(configurationRevision, [.. group], token, group.Key).ConfigureAwait(false)).DeletedFacts;
        }, "Couldn't share the facts. Refresh and try again.");
        if (closed) return;
        var facts = shared == 1 ? "1 fact" : $"{shared} facts";
        ShareStatus.Text = shared == 0 ? "Nothing was shared."
            : move && moved > 0 ? $"Moved {facts} to {label}."
            : move ? $"Copied {facts} to {label}; the facts here stay, because not all of them were taken."
            : $"Copied {facts} to {label}.";
        ErrorLog.Info($"Memory: {(move && moved > 0 ? "moved" : "copied")} {shared} of {chosen.Length} fact(s) to {label}.");
        RenderShare();
    }

    private async void ShareAboutMe_Changed(object sender, RoutedEventArgs e)
    {
        if (renderingShare || sharing is null || closed) return;
        var on = ShareAboutMeChoice.IsChecked == true;
        if (!await sharing.SetAboutMe(on))
        {
            renderingShare = true;
            ShareAboutMeChoice.IsChecked = !on;
            renderingShare = false;
            ShareStatus.Text = "Couldn't change this now. Try again in a moment.";
            return;
        }
        ShareStatus.Text = on ? "New facts about you now go to the household's memories." : "New facts about you now stay in your own memories.";
    }
}
