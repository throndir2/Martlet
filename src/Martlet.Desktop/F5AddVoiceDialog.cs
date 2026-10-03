using System.IO;
using System.Media;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Threading;
using Martlet.Audio.Windows;
using Martlet.Core.Contracts;
using Martlet.Core.Voices;
using Martlet.F5;
using Microsoft.Win32;

namespace Martlet.Desktop;

/// <summary>Adds a voice: one recording or several of the same voice, each with its exact transcript, and the owner's
/// voice-rights confirmation (voice-rights-v1), which the store requires. A recording can be almost any audio or video file:
/// <see cref="VoiceRecordingImport"/> turns each into the mono 16-bit WAV the voice engines take, and the line under it says
/// what it found and whether it converts it. The voice engines copy the voice for each reply; nothing is trained. Several
/// recordings become one voice: Martlet joins them after a short pause into one recording (and their transcripts into one),
/// which every engine can use, and remembers where each lies, so engines that learn from several recordings (XTTS-v2,
/// GPT-SoVITS) get each one. Martlet keeps its own copy, so the originals can be moved or deleted afterwards, and shares the
/// voice with the owner's paired Martlet computers.</summary>
internal sealed class F5AddVoiceDialog : ThemedWindow
{
    private const string RecordingHint = "MP3, M4A, WAV, FLAC, OGG, the sound of a video and most other audio files work. " +
        "Martlet converts the recording for you.";
    private readonly string dataDirectory;
    private readonly string destination;
    private readonly List<Recording> recordings = [];
    private readonly StackPanel list = new();
    private readonly TextBox name = new();
    private readonly ComboBox basis = new();
    private readonly CheckBox rights = new()
    {
        Content = new TextBlock
        {
            Text = "I have permission to use this voice with AI, and the transcripts match the recordings.",
            TextWrapping = TextWrapping.Wrap
        },
        Margin = new Thickness(0, 10, 0, 0)
    };
    private readonly TextBlock summary = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0) };
    private readonly TextBlock error = new() { TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed, Margin = new Thickness(0, 8, 0, 0) };
    private readonly Button more = new() { Content = "Add _another recording", MinWidth = 180, HorizontalAlignment = HorizontalAlignment.Left };
    private readonly Button playJoined = new() { Content = "Play _joined", MinWidth = 110, Margin = new Thickness(8, 0, 0, 0) };
    private readonly Button ok = new() { Content = "_Add voice", IsDefault = true, MinWidth = 110 };
    private F5ReferenceSnapshot? added;
    private SoundPlayer? player;
    private bool closed;

    /// <summary>One recording's row: its file, what Martlet found in it, its exact words and its buttons.</summary>
    private sealed class Recording
    {
        internal TextBox Path { get; } = new() { MinWidth = 300 };
        internal TextBlock Found { get; } = new() { Text = RecordingHint, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0) };
        internal TextBox Transcript { get; } = new() { AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 48, MaxHeight = 120 };
        internal Label FileLabel { get; } = new() { Padding = new Thickness(0, 10, 0, 4) };
        internal Label WordsLabel { get; } = new() { Padding = new Thickness(0, 8, 0, 4) };
        internal Button Browse { get; } = new() { Content = "_Browse...", Margin = new Thickness(8, 0, 0, 0), MinWidth = 90 };
        internal Button Play { get; } = new() { Content = "_Play", Margin = new Thickness(8, 0, 0, 0), MinWidth = 72 };
        internal Button Drop { get; } = new() { Content = "Remove", Margin = new Thickness(8, 0, 0, 0), MinWidth = 72 };
        internal StackPanel Root { get; } = new();
        /// <summary>Reads the file once typing in <see cref="Path"/> settles.</summary>
        internal DispatcherTimer Settle { get; } = new() { Interval = TimeSpan.FromMilliseconds(400) };
        internal CancellationTokenSource? Preparing { get; set; }
        internal Task<VoiceRecording?>? Pending { get; set; }
        internal string? PendingStamp { get; set; }
        /// <summary>The WAV Martlet keeps for the chosen file, once read (null while none is chosen, it is being read or it
        /// can't be used).</summary>
        internal VoiceRecording? Ready { get; set; }
        internal bool Reading { get; set; }
        internal bool Failed { get; set; }

        internal void Forget()
        {
            Settle.Stop();
            Preparing?.Cancel();
            (Pending, PendingStamp, Ready, Reading, Failed) = (null, null, null, false, false);
        }
    }

    private F5AddVoiceDialog(string dataDirectory, string destination)
    {
        this.dataDirectory = dataDirectory;
        this.destination = destination;
        SetResourceReference(StyleProperty, "AppWindowStyle");
        Title = "Martlet - Add a voice";
        Width = 660;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        AutomationProperties.SetAutomationId(this, "F5AddVoiceDialog");
        var root = new StackPanel { Margin = new Thickness(24) };
        var heading = new TextBlock { Text = "Add a voice", TextWrapping = TextWrapping.Wrap };
        heading.SetResourceReference(StyleProperty, "SectionHeading");
        root.Children.Add(heading);
        root.Children.Add(new TextBlock
        {
            Text = "Choose a clear recording, in almost any audio format, and type its exact words. You can add several recordings of " +
                "the same voice: XTTS-v2 and GPT-SoVITS learn from each one, and the other engines hear them joined, one after another " +
                $"with a short pause ({SpeakingVoiceLibrary.MaximumDurationMilliseconds / 1000} seconds in all). Martlet keeps a copy and " +
                "shares it with your paired Martlet computers, so any of them can speak with this voice.",
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 6)
        });

        root.Children.Add(new ScrollViewer { Content = list, MaxHeight = 420, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        AutomationProperties.SetAutomationId(more, "F5AddVoiceMore");
        more.Click += (_, _) => AddRecording()?.Path.Focus();
        AutomationProperties.SetAutomationId(playJoined, "F5AddVoicePlayJoined");
        playJoined.Click += async (_, _) => await PlayJoinedAsync();
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 0) };
        actions.Children.Add(more);
        actions.Children.Add(playJoined);
        root.Children.Add(actions);
        AutomationProperties.SetAutomationId(summary, "F5AddVoiceRecordings");
        summary.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
        root.Children.Add(summary);

        AutomationProperties.SetName(name, "Voice name");
        AutomationProperties.SetName(basis, "Whose voice it is");
        AutomationProperties.SetAutomationId(name, "F5AddVoiceName");
        AutomationProperties.SetAutomationId(basis, "F5AddVoiceBasis");
        AutomationProperties.SetAutomationId(error, "F5AddVoiceProblem");
        AutomationProperties.SetAutomationId(rights, "F5VoiceRights");
        root.Children.Add(new Label { Content = "_Name", Target = name, Padding = new Thickness(0, 10, 0, 4) });
        root.Children.Add(name);
        root.Children.Add(new Label { Content = "W_hose voice is it?", Target = basis, Padding = new Thickness(0, 8, 0, 4) });
        basis.Items.Add(new ComboBoxItem { Content = "My own voice", Tag = F5VoiceRightsBasis.OwnVoice });
        basis.Items.Add(new ComboBoxItem { Content = "Someone who gave me explicit permission", Tag = F5VoiceRightsBasis.ExplicitPermission });
        basis.SelectedIndex = 0;
        root.Children.Add(basis);
        root.Children.Add(rights);

        error.SetResourceReference(TextBlock.ForegroundProperty, "WarningBrush");
        root.Children.Add(error);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 14, 0, 0) };
        var cancel = new Button { Content = "_Cancel", IsCancel = true, MinWidth = 90, Margin = new Thickness(0, 0, 12, 0) };
        ok.SetResourceReference(StyleProperty, "PrimaryButton");
        AutomationProperties.SetAutomationId(ok, "F5AddVoiceOk");
        ok.Click += async (_, _) => await AcceptAsync();
        buttons.Children.Add(cancel);
        buttons.Children.Add(ok);
        root.Children.Add(buttons);
        Content = root;
        Closed += (_, _) =>
        {
            closed = true;
            foreach (var row in recordings) row.Forget();
            player?.Stop();
        };
        AddRecording();
    }

    /// <summary>The newly added voice (bound to <paramref name="destination"/>), or null when canceled.</summary>
    internal static F5ReferenceSnapshot? Add(Window owner, string dataDirectory, string destination)
    {
        var dialog = new F5AddVoiceDialog(dataDirectory, destination) { Owner = owner };
        return dialog.ShowDialog() == true ? dialog.added : null;
    }

    /// <summary>Adds an empty recording row (null when the voice already has the most recordings it may).</summary>
    private Recording? AddRecording()
    {
        if (recordings.Count >= SpeakingVoiceLibrary.MaximumClips) return null;
        var row = new Recording();
        row.Browse.Click += (_, _) => Browse(row);
        row.Play.Click += async (_, _) => await PlayAsync(row);
        row.Drop.Click += (_, _) =>
        {
            row.Forget();
            recordings.Remove(row);
            list.Children.Remove(row.Root);
            Renumber();
        };
        row.Path.TextChanged += (_, _) =>
        {
            row.Forget();
            if (row.Path.Text.Trim().Length == 0) row.Found.Text = RecordingHint;
            else row.Settle.Start();
            Summarize();
        };
        row.Settle.Tick += (_, _) => PrepareAsync(row).Forget();
        row.FileLabel.Target = row.Path;
        row.WordsLabel.Target = row.Transcript;
        var pick = new DockPanel();
        DockPanel.SetDock(row.Drop, Dock.Right);
        DockPanel.SetDock(row.Play, Dock.Right);
        DockPanel.SetDock(row.Browse, Dock.Right);
        pick.Children.Add(row.Drop);
        pick.Children.Add(row.Play);
        pick.Children.Add(row.Browse);
        pick.Children.Add(row.Path);
        row.Root.Children.Add(row.FileLabel);
        row.Root.Children.Add(pick);
        row.Root.Children.Add(row.Found);
        row.Root.Children.Add(row.WordsLabel);
        row.Root.Children.Add(row.Transcript);
        recordings.Add(row);
        list.Children.Add(row.Root);
        Renumber();
        return row;
    }

    /// <summary>Labels and automation IDs by position: the first recording keeps the single-recording IDs
    /// (<c>F5AddVoicePath</c>, <c>F5AddVoiceRecording</c>, <c>F5AddVoiceTranscript</c>, <c>F5AddVoicePlay</c>,
    /// <c>F5AddVoiceBrowse</c>); recording n of several adds "-n" and has <c>F5AddVoiceDrop-n</c>.</summary>
    private void Renumber()
    {
        var several = recordings.Count > 1;
        for (var i = 0; i < recordings.Count; i++)
        {
            var row = recordings[i];
            var suffix = i == 0 ? "" : $"-{i + 1}";
            row.FileLabel.Content = several ? $"Recording {i + 1}" : "_Recording";
            row.WordsLabel.Content = several ? $"Transcript of recording {i + 1}" : "_Transcript";
            AutomationProperties.SetName(row.Path, several ? $"Recording {i + 1} file" : "Recording file");
            AutomationProperties.SetName(row.Transcript, several ? $"Exact words in recording {i + 1}" : "Exact words in the recording");
            AutomationProperties.SetName(row.Drop, $"Remove recording {i + 1}");
            AutomationProperties.SetAutomationId(row.Path, "F5AddVoicePath" + suffix);
            AutomationProperties.SetAutomationId(row.Found, "F5AddVoiceRecording" + suffix);
            AutomationProperties.SetAutomationId(row.Transcript, "F5AddVoiceTranscript" + suffix);
            AutomationProperties.SetAutomationId(row.Play, "F5AddVoicePlay" + suffix);
            AutomationProperties.SetAutomationId(row.Browse, "F5AddVoiceBrowse" + suffix);
            AutomationProperties.SetAutomationId(row.Drop, $"F5AddVoiceDrop-{i + 1}");
            row.Drop.Visibility = several ? Visibility.Visible : Visibility.Collapsed;
        }
        more.IsEnabled = recordings.Count < SpeakingVoiceLibrary.MaximumClips;
        playJoined.Visibility = several ? Visibility.Visible : Visibility.Collapsed;
        Summarize();
    }

    /// <summary>Picks recordings for <paramref name="row"/>: the first file goes to it and any others to new rows.</summary>
    private void Browse(Recording row)
    {
        var dialog = new OpenFileDialog { Filter = VoiceRecordingImport.DialogFilter, CheckFileExists = true, Multiselect = true };
        if (dialog.ShowDialog(this) != true || dialog.FileNames.Length == 0) return;
        row.Path.Text = dialog.FileNames[0];
        PrepareAsync(row).Forget();
        foreach (var file in dialog.FileNames.Skip(1))
        {
            var next = recordings.FirstOrDefault(r => r.Path.Text.Length == 0) ?? AddRecording();
            if (next is null)
            {
                Show($"A voice can have at most {SpeakingVoiceLibrary.MaximumClips} recordings.");
                break;
            }
            next.Path.Text = file;
            PrepareAsync(next).Forget();
        }
        if (name.Text.Length == 0) name.Text = Path.GetFileNameWithoutExtension(dialog.FileNames[0]);
    }

    /// <summary>Reads <paramref name="row"/>'s file into the WAV Martlet would keep (off the UI thread) and says under it what
    /// it found, or why it can't be used. The same file, unchanged since, is read only once (a failure is tried again).</summary>
    private Task<VoiceRecording?> PrepareAsync(Recording row)
    {
        row.Settle.Stop();
        if (closed) return Task.FromResult<VoiceRecording?>(null);
        var chosen = row.Path.Text.Trim();
        var stamp = Stamp(chosen);
        if (row.Pending is { } pending && row.PendingStamp == stamp &&
            !(pending.IsCompleted && (!pending.IsCompletedSuccessfully || pending.Result is null))) return pending;
        row.Preparing?.Cancel();
        row.Preparing = new CancellationTokenSource();
        row.PendingStamp = stamp;
        return row.Pending = PrepareAsync(row, chosen, row.Preparing.Token);
    }

    private async Task<VoiceRecording?> PrepareAsync(Recording row, string chosen, CancellationToken token)
    {
        (row.Ready, row.Failed, row.Reading) = (null, false, chosen.Length > 0);
        if (chosen.Length == 0)
        {
            row.Found.Text = RecordingHint;
            Summarize();
            return null;
        }
        row.Found.Text = "Reading the recording...";
        Summarize();
        try
        {
            var ready = await Task.Run(() => VoiceRecordingImport.Prepare(chosen, token), token);
            if (token.IsCancellationRequested) return null;
            (row.Ready, row.Reading, row.Found.Text) = (ready, false, ready.Describe());
            Summarize();
            return ready;
        }
        catch (VoiceRecordingException failure)
        {
            if (token.IsCancellationRequested) return null;
            (row.Failed, row.Reading, row.Found.Text) = (true, false, failure.Message);
            Summarize();
            return null;
        }
        catch (OperationCanceledException) { return null; }
        catch (Exception failure) when (!ErrorLog.IsFatal(failure))
        {
            ErrorLog.Warn("Couldn't read a recording for Add a voice", failure);
            if (token.IsCancellationRequested) return null;
            (row.Failed, row.Reading, row.Found.Text) = (true, false,
                "Martlet couldn't read this recording. Try an MP3, M4A, WAV or FLAC recording.");
            Summarize();
            return null;
        }
    }

    /// <summary>The recording <paramref name="row"/>'s path names now, waiting out a file chosen again while it was read.</summary>
    private async Task<VoiceRecording?> ReadyAsync(Recording row)
    {
        Task<VoiceRecording?> task;
        VoiceRecording? ready;
        do
        {
            task = PrepareAsync(row);
            ready = await task;
        }
        while (!ReferenceEquals(task, row.Pending) && recordings.Contains(row) && !closed);
        return ready;
    }

    /// <summary>Which file, in which version, a path names: a changed file is read again.</summary>
    private static string Stamp(string chosen)
    {
        try
        {
            var file = new FileInfo(chosen);
            return file.Exists ? $"{file.FullName}|{file.Length}|{file.LastWriteTimeUtc.Ticks}" : chosen + "|missing";
        }
        catch (Exception failure) when (failure is ArgumentException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return chosen + "|invalid";
        }
    }

    /// <summary>Says how many recordings the voice has and, once their files are read, how long they are together (or which
    /// one Martlet can't use). Readable as <c>F5AddVoiceRecordings</c>; never the paths or words.</summary>
    private void Summarize()
    {
        var count = recordings.Count;
        var index = recordings.FindIndex(r => r.Failed);
        var reading = recordings.Any(r => r.Reading);
        var chosen = count > 0 && recordings.All(r => r.Ready is not null);
        var milliseconds = recordings.Sum(r => r.Ready?.DurationMilliseconds ?? 0) + (count - 1) * SpeakingVoiceLibrary.ClipPauseMilliseconds;
        var seconds = milliseconds / 1000d;
        summary.Text = index >= 0 ? count == 1 ? "Martlet can't use this recording." : $"Martlet can't use recording {index + 1}."
            : reading ? count == 1 ? "Reading the recording..." : "Reading the recordings..."
            : count == 1
                ? chosen
                    ? milliseconds < SpeakingVoiceLibrary.MinimumDurationMilliseconds
                        ? $"One recording, {seconds:0.#} seconds: a voice from one recording needs at least " +
                            $"{SpeakingVoiceLibrary.MinimumDurationMilliseconds / 1000} second."
                        : $"One recording, {seconds:0.#} seconds."
                    : "One recording. Add more recordings of the same voice if you have them."
                : $"{count} recordings make one voice" + (chosen ? $", {seconds:0.#} seconds joined with the pauses" +
                    (milliseconds > SpeakingVoiceLibrary.MaximumDurationMilliseconds
                        ? $": more than {SpeakingVoiceLibrary.MaximumDurationMilliseconds / 1000} seconds, so leave some out." : ".")
                    : "; choose each recording's file.");
    }

    private async Task PlayAsync(Recording row)
    {
        error.Visibility = Visibility.Collapsed;
        if (row.Path.Text.Trim().Length == 0)
        {
            Show("Choose the recording to play it.");
            return;
        }
        if (await ReadyAsync(row) is not { } ready)
        {
            Show(row.Found.Text);
            return;
        }
        try { Play(ready.Wave); }
        catch (Exception failure) when (failure is IOException or InvalidOperationException)
        {
            Show("This recording can't be played: " + failure.Message);
        }
    }

    /// <summary>Plays the recordings joined as the voice will keep them, so the owner hears what most engines get.</summary>
    private async Task PlayJoinedAsync()
    {
        error.Visibility = Visibility.Collapsed;
        try
        {
            if (await JoinAsync() is { } joined) Play(joined.Wave);
        }
        catch (ContractException failure) { Show(failure.Message); }
        catch (Exception failure) when (failure is IOException or InvalidOperationException)
        {
            Show("These recordings can't be played: " + failure.Message);
        }
    }

    private void Play(byte[] audio)
    {
        player?.Stop();
        player = new SoundPlayer(new MemoryStream(audio));
        player.Play();
    }

    /// <summary>The recordings, each converted to the WAV Martlet keeps, joined (null, after saying why, when one isn't chosen
    /// or can't be used). Throws <see cref="ContractException"/> when together they are too long.</summary>
    private async Task<JoinedVoiceRecording?> JoinAsync()
    {
        var parts = new List<(ReadOnlyMemory<byte> Wave, string Transcript)>();
        for (var i = 0; i < recordings.Count; i++)
        {
            if (recordings[i].Path.Text.Trim().Length == 0)
            {
                Show($"Choose recording {i + 1}, or remove it.");
                return null;
            }
            if (await ReadyAsync(recordings[i]) is not { } ready)
            {
                Show($"Recording {i + 1}: {recordings[i].Found.Text}");
                return null;
            }
            parts.Add((ready.Wave, recordings[i].Transcript.Text));
        }
        return await Task.Run(() => SpeakingVoiceRecordings.Join(parts));
    }

    private async Task AcceptAsync()
    {
        error.Visibility = Visibility.Collapsed;
        player?.Stop();
        var several = recordings.Count > 1;
        var missing = recordings.FindIndex(r => r.Path.Text.Trim().Length == 0);
        var unsaid = recordings.FindIndex(r => r.Transcript.Text.Trim().Length == 0);
        var problem = missing >= 0 ? several ? $"Choose recording {missing + 1}, or remove it." : "Choose the recording."
            : name.Text.Trim().Length == 0 ? "Give the voice a name."
            : unsaid >= 0 ? several ? $"Type exactly what recording {unsaid + 1} says." : "Type exactly what the recording says."
            : rights.IsChecked != true ? "Confirm that you may use this voice."
            : null;
        if (problem is not null)
        {
            Show(problem);
            return;
        }
        ok.IsEnabled = false;
        try
        {
            if (F5Voices.View(dataDirectory).Live.Count >= SpeakingVoiceLibrary.MaximumVoices)
            {
                Show("The voice list is full. Remove one you no longer use first.");
                return;
            }
            byte[] wave;
            var words = recordings[0].Transcript.Text.Trim();
            JoinedVoiceRecording? joined = null;
            if (several)
            {
                // Several recordings become one: joined after a short pause, with where each lies kept in the shared list.
                joined = await JoinAsync();
                if (joined is null) return;
                (wave, words) = (joined.Wave, joined.Transcript);
            }
            else
            {
                if (await ReadyAsync(recordings[0]) is not { } ready)
                {
                    Show(recordings[0].Found.Text);
                    return;
                }
                if (ready.DurationMilliseconds < SpeakingVoiceLibrary.MinimumDurationMilliseconds)
                {
                    Show($"A voice from one recording needs {SpeakingVoiceLibrary.MinimumDurationMilliseconds / 1000} to " +
                        $"{SpeakingVoiceLibrary.MaximumDurationMilliseconds / 1000} seconds.");
                    return;
                }
                wave = ready.Wave;
            }
            added = await F5Voices.SnapshotAsync(dataDirectory, name.Text.Trim(), wave, words, new()
            {
                AcknowledgementId = Guid.NewGuid(),
                Basis = (basis.SelectedItem as ComboBoxItem)?.Tag is F5VoiceRightsBasis chosenBasis ? chosenBasis : F5VoiceRightsBasis.OwnVoice,
                StatementVersion = F5ReferenceLimits.RightsStatementVersion,
                ProcessingDestinationId = destination,
                AcknowledgedAtUtc = DateTimeOffset.UtcNow,
                Confirmed = true
            }, CancellationToken.None);
            F5Voices.Add(dataDirectory, added, words, joined);
            DialogResult = true;
        }
        catch (F5Exception failure) { Show(F5Voices.Describe(failure)); }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException)
        {
            // These messages usually name a path, and the problem line is returned to automation.
            Show("Martlet couldn't save the voice on this PC. Check that there is free disk space, then try again.");
        }
        catch (Exception failure) when (failure is ArgumentException or ContractException)
        {
            Show(failure.Message);
        }
        finally { ok.IsEnabled = true; }
    }

    private void Show(string text)
    {
        error.Text = text;
        error.Visibility = Visibility.Visible;
    }
}
