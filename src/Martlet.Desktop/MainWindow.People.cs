using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Threading;
using Martlet.Core.Contracts;
using Martlet.Core.Speakers;

namespace Martlet.Desktop;

/// <summary>Companion › People: the voices Martlet recognizes and the names each goes by, and keeping that list the same on
/// all of the owner's computers. The list travels through the paired Martlet hosts (each keeps a copy, like the shared
/// who-does-what plan), so whichever computer becomes the companion recognizes the same people. People are always shared with
/// the household: the list syncs whatever "Keep Martlet the same on all my computers" says, and never with hosts a friend
/// shares.</summary>
public partial class MainWindow
{
    private readonly DispatcherTimer voiceSyncTimer = new() { Interval = TimeSpan.FromSeconds(30) };
    private bool voiceSyncBusy, voiceSyncQueued, peopleStale;
    private string voiceSyncStatus = "Not synced yet.";

    private void InitializeVoiceSync()
    {
        voiceSyncTimer.Tick += (_, _) => SyncVoicesAsync().Forget();
        localVoices.Changed += () => Dispatcher.BeginInvoke(() =>
        {
            if (closing) return;
            QueueVoiceSync();
            if (openTab != CompanionTab.People) return;
            // Never rebuild the page under the owner's typing; it refreshes when they leave the field.
            if (CompanionContent.IsKeyboardFocusWithin) peopleStale = true;
            else RenderTab();
        });
        localVoices.ClipsChanged += () => Dispatcher.BeginInvoke(() =>
        {
            if (closing || openTab != CompanionTab.People) return;
            if (CompanionContent.IsKeyboardFocusWithin) peopleStale = true;
            else RenderTab();
        });
        CompanionContent.LostKeyboardFocus += (_, _) =>
        {
            if (peopleStale && openTab == CompanionTab.People && !CompanionContent.IsKeyboardFocusWithin) RenderTab();
        };
    }

    private void StartVoiceSync()
    {
        if (store is null || closing) return;
        voiceSyncTimer.Start();
        SyncVoicesAsync().Forget();
    }

    /// <summary>Pushes a change soon (debounced), so another computer that becomes the companion sees it.</summary>
    private void QueueVoiceSync()
    {
        if (voiceSyncQueued || closing) return;
        voiceSyncQueued = true;
        SyncSoonAsync().Forget();

        async Task SyncSoonAsync()
        {
            try { await Task.Delay(TimeSpan.FromSeconds(3), lifetime.Token); }
            catch (OperationCanceledException) { return; }
            finally { voiceSyncQueued = false; }
            await SyncVoicesAsync();
        }
    }

    /// <summary>Reads every paired host's copy of the voice list, merges it here and gives each host whose copy differs the
    /// merged list. It runs whatever "Keep Martlet the same on all my computers" says, so Martlet learns everyone's voice on
    /// every computer. Only your own hosts take part (<see cref="NetworkMap.Hosts"/> leaves out hosts a friend shares). Hosts
    /// older than voice sharing are skipped; a host older than account links gets the list without them (docs/ACCOUNTS.md).</summary>
    private async Task SyncVoicesAsync()
    {
        if (voiceSyncBusy || closing || store is null) return;
        var hosts = NetworkMap.Hosts(Inputs());
        if (hosts.Count == 0)
        {
            voiceSyncStatus = "No other computers are paired yet.";
            return;
        }
        voiceSyncBusy = true;
        try
        {
            var results = await Task.WhenAll(hosts.Select(async host =>
            {
                try
                {
                    var linked = true;
                    VoiceRoster copy;
                    try { copy = await ClusterSync.WithConnectionAsync(host.Pairing, connection => connection.ReadVoicesAsync(lifetime.Token, linked: true)); }
                    catch (Martlet.Avatar.Audio2Face.Remote.Audio2FaceHostException error) when (error.Code is "request.invalid" or "request.not_found" or "route.not_found")
                    {
                        linked = false;
                        copy = await ClusterSync.WithConnectionAsync(host.Pairing, connection => connection.ReadVoicesAsync(lifetime.Token));
                    }
                    localVoices.Merge(copy);
                    var mine = localVoices.Roster;
                    if (copy.Digest() != (linked ? mine : mine.WithoutLinks()).Digest())
                        localVoices.Merge(await ClusterSync.WithConnectionAsync(host.Pairing,
                            connection => connection.MergeVoicesAsync(mine, lifetime.Token, linked)));
                    return (host.HostId, Ok: true, Old: false, Linked: linked);
                }
                catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { throw; }
                catch (Martlet.Avatar.Audio2Face.Remote.Audio2FaceHostException error) when (error.Code is "request.invalid" or "request.not_found" or "route.not_found")
                {
                    return (host.HostId, Ok: false, Old: true, Linked: false);
                }
                catch (Exception error) when (error is OperationCanceledException || ClusterSync.IsHostFailure(error))
                {
                    return (host.HostId, Ok: false, Old: false, Linked: false);
                }
            }));
            var ok = results.Count(r => r.Ok);
            var old = results.Where(r => r.Old).Select(r => r.HostId).ToArray();
            var unlinked = results.Where(r => r.Ok && !r.Linked).Select(r => r.HostId).ToArray();
            voiceSyncStatus = $"Synced with {ok} of {results.Length} computer{(results.Length == 1 ? "" : "s")} at {DateTime.Now:t}." +
                (old.Length > 0 ? $" Update {string.Join(", ", old)} to sync voices there." : "") +
                (unlinked.Length > 0 ? $" Update {string.Join(", ", unlinked)} to share whose voice is whose there." : "");
        }
        catch (OperationCanceledException) { }
        finally
        {
            voiceSyncBusy = false;
            if (!closing && openTab == CompanionTab.People && !CompanionContent.IsKeyboardFocusWithin) RenderTab();
        }
    }

    // ---------- the People page ----------

    private void RenderPeopleTab(Panel page)
    {
        peopleStale = false;
        page.Children.Add(PeopleNowCard());
        page.Children.Add(RecognitionCard());
        page.Children.Add(SharingCard());
        page.Children.Add(VoiceListCard());
    }

    /// <summary>People's main choice: whether Martlet recognizes voices (with an explicit Off), then whether it keeps clips of
    /// voices you haven't named.</summary>
    private Border RecognitionCard()
    {
        var children = new List<UIElement> { Heading("Recognize voices") };
        if (!localVoices.Available)
        {
            children.Add(Warning("Voice recognition needs Martlet's data folder, which isn't available."));
            return Card([.. children]);
        }
        children.AddRange(OnOffChoices("PeopleRecognize", "PeopleRecognizeOff", "Recognize voices in conversations",
            "Martlet learns people's voices and names as you talk. Voice matching happens on this PC.",
            "Martlet doesn't check who is talking. Saved voices are kept.", localVoices.Enabled,
            localVoices.Included ? null : "Reinstall Martlet to recognize voices: its voice recognition files are missing.",
            on => { if (on != localVoices.Enabled) SetRecognition(on); }));
        var clips = new CheckBox
        {
            Content = $"Keep the last {VoiceClips.MaximumClips} clips of voices you haven't named, so you can hear who they are",
            IsChecked = localVoices.Clips.Enabled, Margin = new Thickness(0, 6, 0, 0)
        };
        clips.ToolTip = "Clips stay on this PC and are deleted once you name the voice, mark it as yours or forget it.";
        AutomationProperties.SetAutomationId(clips, "PeopleKeepClips");
        clips.Checked += (_, _) => SetKeepClips(true);
        clips.Unchecked += (_, _) => SetKeepClips(false);
        children.Add(clips);
        return Card([.. children]);
    }

    private void SetRecognition(bool on)
    {
        try
        {
            localVoices.SetEnabled(on);
            QueueSettingsSync();
            ActionText.Text = on ? "Voice recognition is on. Open conversations use it from your next message."
                : "Voice recognition is off. Saved voices are kept.";
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            ActionText.Text = $"Couldn't save your choice: {error.Message}";
        }
        RenderTab();
    }

    private void SetKeepClips(bool on)
    {
        try
        {
            localVoices.Clips.SetEnabled(on);
            ActionText.Text = on ? "Martlet keeps a few clips of voices you haven't named." : "Martlet keeps no clips, and deleted the ones it had.";
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            ActionText.Text = $"Couldn't save your choice: {error.Message}";
        }
        RenderTab();
    }

    private Border SharingCard()
    {
        var sync = PageButton(voiceSyncBusy ? "Syncing..." : "Sync now", () => SyncVoicesAsync().Forget(), link: true, id: "PeopleSync");
        sync.IsEnabled = !voiceSyncBusy;
        var syncStatus = Note(voiceSyncStatus, new Thickness(0, 6, 0, 0));
        AutomationProperties.SetAutomationId(syncStatus, "PeopleSyncStatus");
        return Card(Heading("Your computers"),
            Note("The people Martlet recognizes by voice, and their names, are always shared with all your computers, so Martlet " +
                "learns everyone's voice everywhere. This doesn't depend on Keep Martlet the same on all my computers, and hosts a " +
                "friend shares never get them. Whether Martlet recognizes voices is shared with your other settings.", new Thickness(0, 0, 0, 0)),
            syncStatus,
            Row(sync));
    }

    private Border VoiceListCard()
    {
        var voices = localVoices.Roster.Live.OrderByDescending(localVoices.IsYours).ThenByDescending(v => v.Account is not null)
            .ThenByDescending(v => v.Named).ThenByDescending(v => v.LastHeardAt).ToArray();
        var heading = Heading(voices.Length == 0 ? "Voices Martlet knows" : $"Voices Martlet knows ({voices.Length})");
        AutomationProperties.SetAutomationId(heading, "PeopleVoiceCount");
        var children = new List<UIElement> { heading };
        if (voices.Length == 0)
        {
            children.Add(Note("None yet. Turn recognition on and talk to Martlet. New voices will appear here.", new Thickness(0, 0, 0, 0)));
            return Card([.. children]);
        }
        children.Add(Note("Type a name to say who a voice is, and add as many other names as they go by. Click a name to make it the " +
            "one Martlet uses; a name with ? was only heard in conversation. Changes sync to your computers.", new Thickness(0, 0, 0, 4)));
        var links = Note(PeopleLinkStatus(voices.Length, voices.Count(v => v.Account is not null), voices.Count(localVoices.IsYours),
            localVoices.Account is not null), new Thickness(0, 0, 0, 4));
        AutomationProperties.SetAutomationId(links, "PeopleLinkStatus");
        children.Add(links);
        foreach (var voice in voices) children.Add(VoiceEntry(voice, voices));
        children.Add(Row(PageButton("Forget all voices", ForgetAllVoices, link: true, id: "PeopleForgetAll")));
        return Card([.. children]);
    }

    /// <summary>People's link line: how many voices link to people's accounts and how many are yours. Counts only, no names.
    /// Before the desktop knows its account, only whether a voice is marked as yours.</summary>
    internal static string PeopleLinkStatus(int voices, int linked, int yours, bool accounts) =>
        !accounts ? yours == 0 ? "No voice is marked as yours yet. Tick This is me on yours." : "Martlet knows which voice is yours."
        : $"{linked} of {voices} voice{(voices == 1 ? "" : "s")} {(linked == 1 ? "is" : "are")} linked to people's accounts. " +
          (yours == 0 ? "None is yours yet: tick This voice is ... on yours." : $"Your account has {yours} voice{(yours == 1 ? "" : "s")}.") +
          " A voice only tells Martlet who is talking: it never signs anyone in.";

    private Border VoiceEntry(KnownVoice voice, IReadOnlyList<KnownVoice> all)
    {
        var mine = localVoices.IsYours(voice);
        var stack = new List<UIElement>
        {
            OptionTitle(voice.DisplayName, mine ? "you" : voice.Account is { } other ? $"{VoiceAccountName(other) ?? "someone else"}'s voice"
                : voice.Named ? null : "no name yet"),
            Note($"{(voice.Named ? $"Voice {voice.Number} · " : "")}heard {voice.Heard} time{(voice.Heard == 1 ? "" : "s")}, last {Ago(voice.LastHeardAt)}" +
                (voice.MergedVoices > 0 ? $" · {voice.MergedVoices + 1} voices merged" : ""), new Thickness(0, 2, 0, 4))
        };
        var twins = all.Where(other => other.Id != voice.Id && other.Named && voice.Named &&
            string.Equals(other.DisplayName, voice.DisplayName, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (twins.Length > 0)
            stack.Add(Warning($"{string.Join(", ", twins.Select(t => $"Voice {t.Number}"))} also goes by {voice.DisplayName}. " +
                "If they're the same person, merge them below."));
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
            label.SetResourceReference(StyleProperty, "LinkButton");
            label.SetResourceReference(ForegroundProperty, "TextBrush");
            AutomationProperties.SetName(label, shown ? $"{text}, the name shown" : $"Show {text} as the name");
            AutomationProperties.SetAutomationId(label, $"PeopleNameShow-{voice.Number}-{index}");
            label.Click += (_, _) => { if (!shown || learned) SaveVoiceNames(voice, _ => text, all => all); };
            var remove = new Button { Content = "\u00d7", Padding = new Thickness(6, 0, 0, 0), Margin = new Thickness(0), ToolTip = $"Remove {text}" };
            remove.SetResourceReference(StyleProperty, "LinkButton");
            AutomationProperties.SetName(remove, $"Remove {text}");
            AutomationProperties.SetAutomationId(remove, $"PeopleNameRemove-{voice.Number}-{index}");
            remove.Click += (_, _) => SaveVoiceNames(voice,
                shownName => string.Equals(shownName, text, StringComparison.OrdinalIgnoreCase) ? null : shownName,
                all => all.Where(n => !string.Equals(n, text, StringComparison.OrdinalIgnoreCase)));
            var chip = new Border { Child = new StackPanel { Orientation = Orientation.Horizontal, Children = { label, remove } } };
            chip.SetResourceReference(StyleProperty, "Chip");
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
        void Add()
        {
            var typed = box.Text.Trim();
            if (typed.Length == 0) return;
            if (VoiceRoster.CleanName(typed) is null)
            {
                ActionText.Text = $"Not added: names start with a letter and use at most {VoiceRoster.MaximumNameLength} characters.";
                return;
            }
            SaveVoiceNames(voice, shownName => shownName ?? typed, all => all.Append(typed));
        }
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
        var clips = localVoices.Clips.List(voice.Id);
        if (clips.Count == 0) return null;
        var row = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
        var label = Note($"Hear them ({clips.Count}):", new Thickness(0, 0, 10, 0));
        label.VerticalAlignment = VerticalAlignment.Center;
        AutomationProperties.SetAutomationId(label, "PeopleClips-" + voice.Number);
        row.Children.Add(label);
        for (var index = 0; index < clips.Count; index++)
        {
            var clip = clips[index];
            var play = PageButton($"\u25b6 {Ago(clip.At)}", () => PlayClip(clip), link: true, id: $"PeopleClip-{voice.Number}-{index}");
            play.ToolTip = $"{clip.Seconds:0.#} s, {clip.At.LocalDateTime:g}";
            row.Children.Add(play);
        }
        return row;
    }

    private void PlayClip(VoiceClip clip)
    {
        try
        {
            voicePlayer?.Stop();
            voicePlayer = new System.Media.SoundPlayer(clip.Path);
            voicePlayer.Play();
            ActionText.Text = $"Playing a clip from {Ago(clip.At)}.";
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            ActionText.Text = $"Couldn't play the clip: {error.Message}";
        }
    }

    /// <summary>This voice is (yours), Same person as (then Merge), what Martlet remembers about them and Forget, on one line.</summary>
    private WrapPanel VoiceActions(KnownVoice voice, IReadOnlyList<KnownVoice> all)
    {
        var row = new WrapPanel { Margin = new Thickness(0, 10, 0, 0) };
        var you = localVoices.Account is { } signedIn ? VoiceAccountName(signedIn) : null;
        var owner = new CheckBox { Content = you is null ? "This is me" : $"This voice is {you}", IsChecked = localVoices.IsYours(voice),
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 16, 6),
            ToolTip = "Links this voice to your account, so Martlet knows it's you talking. A voice never signs anyone in." };
        AutomationProperties.SetName(owner, "This voice is mine");
        AutomationProperties.SetAutomationId(owner, "PeopleLink-" + voice.Number);
        owner.Checked += (_, _) => LinkVoice(voice, true);
        owner.Unchecked += (_, _) => LinkVoice(voice, false);
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
                if (merge.SelectedIndex >= 0) MergeVoices(voice, targets[merge.SelectedIndex]);
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

        var memories = PageButton("Memories", () => OpenMemoryAsync(voice.Id).Forget(), link: true, id: "PeopleMemories-" + voice.Number);
        memories.ToolTip = "What Martlet remembers about them";
        foreach (var link in new[] { memories, PageButton("Forget", () => ForgetVoice(voice), link: true, id: "PeopleForget-" + voice.Number) })
        {
            link.VerticalAlignment = VerticalAlignment.Center;
            link.Margin = new Thickness(0, 0, 16, 6);
            row.Children.Add(link);
        }
        return row;
    }

    private static DockPanel Labeled(string label, UIElement control, double width)
    {
        var row = new DockPanel { Margin = new Thickness(0, 4, 16, 0), LastChildFill = false };
        row.Children.Add(new TextBlock { Text = label, Width = width, VerticalAlignment = VerticalAlignment.Center });
        row.Children.Add(control);
        return row;
    }

    private static string Ago(DateTimeOffset when)
    {
        var age = DateTimeOffset.UtcNow - when;
        return age < TimeSpan.FromMinutes(1) ? "just now"
            : age < TimeSpan.FromHours(1) ? $"{(int)age.TotalMinutes} min ago"
            : age < TimeSpan.FromDays(1) ? $"{(int)age.TotalHours} h ago"
            : when.LocalDateTime.ToString("d");
    }

    /// <summary>Changes a voice's names as they are now (the page may be older than the list): <paramref name="name"/> turns
    /// the name the owner chose (null when none) into the one to show (null leaves it to the names learned), and
    /// <paramref name="names"/> turns every name it goes by into the ones it should. The change syncs to your other computers
    /// shortly after.</summary>
    private void SaveVoiceNames(KnownVoice voice, Func<string?, string?> name, Func<IEnumerable<string>, IEnumerable<string>> names)
    {
        if (closing) return;
        try
        {
            if (localVoices.Roster.Resolve(voice.Id) is not { } current) return;
            localVoices.SetNames(current.Id, name(current.Name), names(current.Names.Select(n => n.Text)).ToArray());
            var now = localVoices.Roster.Resolve(voice.Id);
            ActionText.Text = $"Saved the names of {now?.DisplayName ?? voice.DisplayName}.";
        }
        catch (ContractException error) { ActionText.Text = error.Message; }
        // After the click returns: the page is rebuilt, the button with it.
        Dispatcher.BeginInvoke(() => { if (!closing && openTab == CompanionTab.People) RenderTab(); });
    }

    /// <summary>Links the voice to the signed-in account, or unlinks it; a voice linked to someone else's account moves to yours
    /// only after you confirm. The change syncs to your other computers shortly after.</summary>
    private void LinkVoice(KnownVoice voice, bool yours)
    {
        if (closing) return;
        var current = localVoices.Roster.Resolve(voice.Id);
        if (current is null) return;
        if (yours && current.Account is { } other && other != localVoices.Account &&
            !ConfirmationDialog.Confirm(this, $"{current.DisplayName} is {VoiceAccountName(other) ?? "someone else"}'s voice.\n\n" +
                "Make it yours instead? Martlet will treat this voice as you on all your computers.", "Make it mine"))
        {
            Dispatcher.BeginInvoke(() => { if (!closing && openTab == CompanionTab.People) RenderTab(); });
            return;
        }
        localVoices.Link(current.Id, yours);
        ActionText.Text = yours ? $"{current.DisplayName} is now your voice." : $"{current.DisplayName} is no longer linked to you.";
        // After the click returns: the page is rebuilt, the box with it.
        Dispatcher.BeginInvoke(() => { if (!closing && openTab == CompanionTab.People) RenderTab(); });
    }

    private void MergeVoices(KnownVoice from, KnownVoice into)
    {
        if (!ConfirmationDialog.Confirm(this,
                $"Merge {from.DisplayName} into {into.DisplayName}?\n\nMartlet will treat them as one person on all your computers. This can't be undone.",
                "Merge"))
            return;
        localVoices.Join(from.Id, into.Id);
        ActionText.Text = $"Merged {from.DisplayName} into {into.DisplayName}.";
        RenderTab();
    }

    private void ForgetVoice(KnownVoice voice)
    {
        if (!ConfirmationDialog.Confirm(this,
                $"Forget {voice.DisplayName}?\n\nIts saved voice and names will be deleted from all your computers. If Martlet hears it again, it will appear as new. " +
                "What Martlet remembers about them stays in Memory under Forgotten voices, where you can delete it.",
                "Forget"))
            return;
        localVoices.Forget(voice.Id);
        ActionText.Text = $"Forgot {voice.DisplayName}.";
        RenderTab();
    }

    private void ForgetAllVoices()
    {
        if (!ConfirmationDialog.Confirm(this,
                "Forget every voice?\n\nSaved voices and names will be deleted from all your computers. Recognition stays on and starts over. " +
                "What Martlet remembers about them stays in Memory under Forgotten voices, where you can delete it.",
                "Forget all"))
            return;
        localVoices.ForgetAll();
        ActionText.Text = "Martlet forgot every voice.";
        RenderTab();
    }
}
