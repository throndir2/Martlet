using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Threading;
using Martlet.Core.Contracts;
using Martlet.Core.Speakers;
using Martlet.Sherpa;

namespace Martlet.Desktop;

/// <summary>Companion › People: the voices Martlet recognizes and the names each goes by, and keeping that list the same on
/// all of the owner's computers. The list travels through the paired Martlet hosts (each keeps a copy, like the shared
/// who-does-what plan), so whichever computer becomes the companion recognizes the same people.</summary>
public partial class MainWindow
{
    private readonly DispatcherTimer voiceSyncTimer = new() { Interval = TimeSpan.FromSeconds(30) };
    private bool voiceSyncBusy, voiceSyncQueued, installingVoices, peopleStale;
    private string voiceSyncStatus = "Not synced yet.";
    private string? voiceInstallProgress;

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
        if (voiceSyncQueued || !localVoices.Sharing || closing) return;
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
    /// merged list. Hosts older than voice sharing are skipped.</summary>
    private async Task SyncVoicesAsync()
    {
        if (voiceSyncBusy || closing || store is null || !localVoices.Sharing) return;
        var hosts = NetworkMap.Hosts(Inputs());
        if (hosts.Count == 0)
        {
            voiceSyncStatus = "No paired Martlet host yet: the list stays on this PC until you add one.";
            return;
        }
        voiceSyncBusy = true;
        try
        {
            var results = await Task.WhenAll(hosts.Select(async host =>
            {
                try
                {
                    var copy = await ClusterSync.WithConnectionAsync(host.Pairing, connection => connection.ReadVoicesAsync(lifetime.Token));
                    localVoices.Merge(copy);
                    var mine = localVoices.Roster;
                    if (copy.Digest() != mine.Digest())
                        localVoices.Merge(await ClusterSync.WithConnectionAsync(host.Pairing, connection => connection.MergeVoicesAsync(mine, lifetime.Token)));
                    return (host.HostId, Ok: true, Old: false);
                }
                catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { throw; }
                catch (Martlet.Avatar.Audio2Face.Remote.Audio2FaceHostException error) when (error.Code is "request.invalid" or "request.not_found" or "route.not_found")
                {
                    return (host.HostId, Ok: false, Old: true);
                }
                catch (Exception error) when (error is OperationCanceledException || ClusterSync.IsHostFailure(error))
                {
                    return (host.HostId, Ok: false, Old: false);
                }
            }));
            var ok = results.Count(r => r.Ok);
            var old = results.Where(r => r.Old).Select(r => r.HostId).ToArray();
            voiceSyncStatus = $"Synced with {ok} of {results.Length} host{(results.Length == 1 ? "" : "s")} at {DateTime.Now:t}." +
                (old.Length > 0 ? $" {string.Join(", ", old)} {(old.Length == 1 ? "is" : "are")} older than voice sharing; update {(old.Length == 1 ? "it" : "them")}." : "");
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
        page.Children.Add(RecognitionCard());
        page.Children.Add(SharingCard());
        page.Children.Add(VoiceListCard());
    }

    private Border RecognitionCard()
    {
        var children = new List<UIElement> { Heading("Recognize who is talking") };
        if (!localVoices.Available)
        {
            children.Add(Warning("Voice recognition needs Martlet's data folder, which isn't available."));
            return Card([.. children]);
        }
        var size = SherpaComponents.Megabytes((localVoices.Root is { } root && SherpaComponents.IsInstalled(root, SherpaPart.Runtime)
            ? 0 : SherpaComponents.DownloadBytes(SherpaPart.Runtime)) + SherpaComponents.DownloadBytes(SherpaPart.Speakers));
        children.Add(new TextBlock
        {
            Text = localVoices.Active ? "On. Martlet recognizes voices in every conversation and learns the names people go by."
                : localVoices.Installed ? "Off. Martlet doesn't check who is talking."
                : "Off. Martlet can tell people apart by their voices, like AudioTranscriber does.",
            FontSize = 15, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 6)
        });
        children.Add(Note("Each thing said to Martlet is compared on this PC with the voices it knows: a known voice is named to the Thinking " +
            "model (\"Sam is speaking\"), a new one is added as Voice 1, Voice 2... and, after a reply, the Thinking model picks up the " +
            "names people use (\"I'm Sam\", \"thanks, Sam\"), so a voice collects every name it goes by. Only voiceprints (256 numbers per " +
            "sample) and names are kept, never recordings. It is a convenience, not proof of who someone is.", new Thickness(0, 0, 0, 8)));
        if (localVoices.LoadError is { } error) children.Add(Warning(error));
        if (voiceInstallProgress is { } progress) children.Add(Note(progress, new Thickness(0, 0, 0, 6)));
        if (!localVoices.Installed)
        {
            var install = PageButton(installingVoices ? "Downloading..." : "Download and turn on", () => InstallVoicesAsync().Forget(),
                primary: true, id: "PeopleInstall");
            install.IsEnabled = !installingVoices;
            children.Add(Note($"The first time, Martlet downloads {SherpaComponents.Disclosure(SherpaPart.Runtime)} and " +
                $"{SherpaComponents.Disclosure(SherpaPart.Speakers)}: {size} in all, checked against pinned SHA-256 hashes.",
                new Thickness(0, 0, 0, 0)));
            children.Add(Row(install));
        }
        else
        {
            var toggle = new CheckBox { Content = "Recognize voices in conversations", IsChecked = localVoices.Enabled, Margin = new Thickness(0, 4, 0, 0) };
            AutomationProperties.SetAutomationId(toggle, "PeopleRecognize");
            toggle.Checked += (_, _) => SetRecognition(true);
            toggle.Unchecked += (_, _) => SetRecognition(false);
            children.Add(toggle);
        }
        return Card([.. children]);
    }

    private void SetRecognition(bool on)
    {
        try
        {
            localVoices.SetEnabled(on);
            ActionText.Text = on ? "Voice recognition is on. An open conversation window uses it from the next thing you say."
                : "Voice recognition is off. The voices Martlet knows are kept until you forget them.";
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            ActionText.Text = $"Couldn't save {LocalVoices.EnabledFile}: {error.Message}";
        }
        RenderTab();
    }

    private async Task InstallVoicesAsync()
    {
        if (installingVoices || closing) return;
        var total = SherpaComponents.Megabytes(SherpaComponents.DownloadBytes(SherpaPart.Runtime) + SherpaComponents.DownloadBytes(SherpaPart.Speakers));
        if (!ConfirmationDialog.Confirm(this,
                $"Download voice recognition ({total})? Martlet downloads {SherpaComponents.Disclosure(SherpaPart.Runtime)} and " +
                $"{SherpaComponents.Disclosure(SherpaPart.Speakers)} into its data folder, checks every file against a pinned SHA-256 and " +
                "then recognizes voices on this PC. Nothing you say is uploaded for this; only voiceprints and names are saved.",
                "Download"))
            return;
        installingVoices = true;
        RenderTab();
        try
        {
            await localVoices.InstallAsync(new Progress<SherpaProgress>(p =>
            {
                voiceInstallProgress = $"Downloading {p.Part}: {p.Received * 100 / Math.Max(1, p.Total)}% of {SherpaComponents.Megabytes(p.Total)}...";
                ActionText.Text = voiceInstallProgress;
            }), lifetime.Token);
            localVoices.SetEnabled(true);
            voiceInstallProgress = null;
            ActionText.Text = "Voice recognition is downloaded and on. Talk to Martlet and the voices it hears appear here.";
        }
        catch (OperationCanceledException) { voiceInstallProgress = null; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException or
            System.Net.Http.HttpRequestException or InvalidOperationException)
        {
            voiceInstallProgress = null;
            ActionText.Text = "Downloading voice recognition stopped: " + error.Message;
        }
        finally
        {
            installingVoices = false;
            if (!closing && openTab == CompanionTab.People) RenderTab();
        }
    }

    private Border SharingCard()
    {
        var share = new CheckBox { Content = "Keep this list on my paired Martlet hosts, so any of my computers recognizes these people",
            IsChecked = localVoices.Sharing, IsEnabled = localVoices.Available };
        AutomationProperties.SetAutomationId(share, "PeopleShare");
        share.Checked += (_, _) => SetSharing(true);
        share.Unchecked += (_, _) => SetSharing(false);
        var sync = PageButton(voiceSyncBusy ? "Syncing..." : "Sync now", () => SyncVoicesAsync().Forget(), link: true, id: "PeopleSync");
        sync.IsEnabled = localVoices.Sharing && !voiceSyncBusy;
        return Card(Heading("On all my computers"), share,
            Note("While Martlet runs, this PC merges its list with each paired host's copy every 30 seconds over the pinned pairing " +
                "(the hosts only keep it). Switch which computer is your companion and it already knows everyone. Changes, renames, merges " +
                "and forgotten voices reach every computer; the newest change wins.", new Thickness(0, 6, 0, 0)),
            Note(localVoices.Sharing ? voiceSyncStatus : "Off: the list stays on this PC only.", new Thickness(0, 6, 0, 0)),
            Row(sync));
    }

    private void SetSharing(bool on)
    {
        try
        {
            localVoices.SetSharing(on);
            if (on) SyncVoicesAsync().Forget();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            ActionText.Text = $"Couldn't save {LocalVoices.SharingFile}: {error.Message}";
        }
        RenderTab();
    }

    private Border VoiceListCard()
    {
        var voices = localVoices.Roster.Live.OrderByDescending(v => v.Owner).ThenByDescending(v => v.Named)
            .ThenByDescending(v => v.LastHeardAt).ToArray();
        var children = new List<UIElement> { Heading(voices.Length == 0 ? "Voices Martlet knows" : $"Voices Martlet knows ({voices.Length})") };
        if (voices.Length == 0)
        {
            children.Add(Note("None yet. Turn recognition on and talk with Martlet; each new voice appears here, and its names fill in as " +
                "people are named in conversation. You can also name them yourself.", new Thickness(0, 0, 0, 0)));
            return Card([.. children]);
        }
        children.Add(Note("Give a voice its name, add the other names it goes by (comma-separated), merge two entries that are the " +
            "same person, or forget one. A voice that is merged or forgotten stays that way on all your computers.", new Thickness(0, 0, 0, 4)));
        foreach (var voice in voices) children.Add(VoiceEntry(voice, voices));
        children.Add(Row(PageButton("Forget all voices", ForgetAllVoices, link: true, id: "PeopleForgetAll")));
        return Card([.. children]);
    }

    private Border VoiceEntry(KnownVoice voice, IReadOnlyList<KnownVoice> all)
    {
        var stack = new List<UIElement>
        {
            OptionTitle(voice.DisplayName, voice.Owner ? "you" : voice.Named ? null : "no name yet"),
            Note($"{(voice.Named ? voice.Tag.Replace("V", "Voice ") + " · " : "")}heard {voice.Heard} time{(voice.Heard == 1 ? "" : "s")}, " +
                $"last {Ago(voice.LastHeardAt)}" + (voice.OtherNames.Count > 0 ? $" · also called {string.Join(", ", voice.OtherNames)}" : "") +
                (voice.MergedVoices > 0 ? $" · {voice.MergedVoices + 1} voices merged" : ""), new Thickness(0, 2, 0, 6))
        };
        var twins = all.Where(other => other.Id != voice.Id && other.Named && voice.Named &&
            string.Equals(other.DisplayName, voice.DisplayName, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (twins.Length > 0)
            stack.Add(Warning($"{string.Join(", ", twins.Select(t => t.Tag.Replace("V", "Voice ")))} also goes by {voice.DisplayName}. " +
                "If they're the same person, merge them below."));

        var name = new TextBox { Width = 220, Text = voice.Name ?? "", MaxLength = VoiceRoster.MaximumNameLength };
        AutomationProperties.SetName(name, $"Name of {voice.DisplayName}");
        AutomationProperties.SetAutomationId(name, "PeopleName-" + voice.Number);
        var others = new TextBox { Width = 300, Text = string.Join(", ", voice.Names.Select(n => n.Text)
            .Where(n => !string.Equals(n, voice.Name, StringComparison.OrdinalIgnoreCase))), MaxLength = 400 };
        AutomationProperties.SetName(others, $"Other names of {voice.DisplayName}, comma-separated");
        AutomationProperties.SetAutomationId(others, "PeopleOtherNames-" + voice.Number);
        var names = new WrapPanel { Margin = new Thickness(0, 0, 0, 4) };
        names.Children.Add(Labeled("Name", name, 60));
        names.Children.Add(Labeled("Also called", others, 90));
        stack.Add(names);

        var owner = new CheckBox { Content = "This is my voice", IsChecked = voice.Owner, Margin = new Thickness(0, 4, 0, 0) };
        AutomationProperties.SetAutomationId(owner, "PeopleOwner-" + voice.Number);
        owner.Checked += (_, _) => localVoices.SetOwner(voice.Id, true);
        owner.Unchecked += (_, _) => localVoices.SetOwner(voice.Id, false);
        stack.Add(owner);

        var merge = new ComboBox { Width = 220, ItemsSource = all.Where(v => v.Id != voice.Id).Select(v => v.DisplayName + " (" + v.Tag + ")").ToArray() };
        AutomationProperties.SetName(merge, $"Merge {voice.DisplayName} into");
        AutomationProperties.SetAutomationId(merge, "PeopleMergeTarget-" + voice.Number);
        var targets = all.Where(v => v.Id != voice.Id).ToArray();
        var mergeRow = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
        mergeRow.Children.Add(Labeled("Same person as", merge, 110));
        var mergeButton = PageButton("Merge", () =>
        {
            if (merge.SelectedIndex < 0) { ActionText.Text = "Choose who this voice also is, then Merge."; return; }
            MergeVoices(voice, targets[merge.SelectedIndex]);
        }, id: "PeopleMerge-" + voice.Number);
        mergeButton.Margin = new Thickness(10, 0, 0, 0);
        mergeButton.IsEnabled = targets.Length > 0;
        mergeRow.Children.Add(mergeButton);
        stack.Add(mergeRow);

        stack.Add(Row(
            PageButton("Save names", () => SaveVoiceNames(voice, name.Text, others.Text), primary: true, id: "PeopleSave-" + voice.Number),
            PageButton("Forget this voice", () => ForgetVoice(voice), link: true, id: "PeopleForget-" + voice.Number)));
        return Option(stack, voice.Owner);
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

    private void SaveVoiceNames(KnownVoice voice, string name, string others)
    {
        var typed = name.Trim();
        if (typed.Length > 0 && VoiceRoster.CleanName(typed) is null)
        {
            ActionText.Text = $"\"{typed}\" can't be used as a name: start with a letter and use at most {VoiceRoster.MaximumNameLength} characters.";
            return;
        }
        var list = others.Split([',', ';', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var rejected = list.Where(n => VoiceRoster.CleanName(n) is null).ToArray();
        try
        {
            localVoices.SetNames(voice.Id, typed.Length == 0 ? null : typed, list);
            ActionText.Text = $"Saved the names of {localVoices.Roster.Resolve(voice.Id)?.DisplayName ?? voice.DisplayName}." +
                (rejected.Length > 0 ? $" Left out: {string.Join(", ", rejected)}." : "");
        }
        catch (ContractException error) { ActionText.Text = error.Message; }
        peopleStale = false;
        RenderTab();
    }

    private void MergeVoices(KnownVoice from, KnownVoice into)
    {
        if (!ConfirmationDialog.Confirm(this,
                $"Merge {from.DisplayName} into {into.DisplayName}? Martlet then treats both voices as one person: the names and voiceprints " +
                $"combine under {into.DisplayName} on all your computers. This can't be split again; if you're unsure, rename instead.",
                "Merge"))
            return;
        localVoices.Join(from.Id, into.Id);
        ActionText.Text = $"Merged {from.DisplayName} into {into.DisplayName}.";
        RenderTab();
    }

    private void ForgetVoice(KnownVoice voice)
    {
        if (!ConfirmationDialog.Confirm(this,
                $"Forget {voice.DisplayName}? Its voiceprint and names are deleted here and from your other computers. If Martlet hears " +
                "this voice again, it starts over as a new voice.", "Forget"))
            return;
        localVoices.Forget(voice.Id);
        ActionText.Text = $"Forgot {voice.DisplayName}.";
        RenderTab();
    }

    private void ForgetAllVoices()
    {
        if (!ConfirmationDialog.Confirm(this,
                "Forget every voice Martlet knows? All voiceprints and names are deleted here and from your other computers. " +
                "Recognition stays on (turn it off above) and starts over.", "Forget all"))
            return;
        localVoices.ForgetAll();
        ActionText.Text = "Martlet forgot every voice.";
        RenderTab();
    }
}
