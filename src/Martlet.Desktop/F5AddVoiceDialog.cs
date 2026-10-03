using System.IO;
using System.Media;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Martlet.Core.Contracts;
using Martlet.Core.Voices;
using Martlet.F5;
using Microsoft.Win32;

namespace Martlet.Desktop;

/// <summary>Adds a voice: one recording or several of the same voice, each with its exact transcript, and the owner's
/// voice-rights confirmation (voice-rights-v1), which the store requires. The voice engines copy the voice for each reply;
/// nothing is trained. Several recordings become one voice: Martlet joins them after a short pause into one recording (and
/// their transcripts into one), which every engine can use, and remembers where each lies, so engines that learn from several
/// recordings (XTTS-v2, GPT-SoVITS) get each one. Martlet keeps its own copy, so the originals can be moved or deleted
/// afterwards, and shares the voice with the owner's paired Martlet computers. When speech-to-text is available without the
/// cloud (<see cref="RecordingTranscriber"/>), each recording's words are filled in as soon as it is chosen, for the owner to
/// check and correct.</summary>
internal sealed class F5AddVoiceDialog : ThemedWindow
{
    private readonly string dataDirectory;
    private readonly string destination;
    private readonly RecordingTranscriber? transcriber;
    private readonly SemaphoreSlim listening = new(1, 1);
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

    /// <summary>One recording's row: its file, its exact words and its buttons.</summary>
    private sealed class Recording
    {
        internal TextBox Path { get; } = new() { MinWidth = 300 };
        internal TextBox Transcript { get; } = new() { AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 48, MaxHeight = 120 };
        internal Label FileLabel { get; } = new() { Padding = new Thickness(0, 10, 0, 4) };
        internal Label WordsLabel { get; } = new() { Padding = new Thickness(0, 8, 0, 4) };
        internal Button Browse { get; } = new() { Content = "_Browse...", Margin = new Thickness(8, 0, 0, 0), MinWidth = 90 };
        internal Button Play { get; } = new() { Content = "_Play", Margin = new Thickness(8, 0, 0, 0), MinWidth = 72 };
        internal Button Drop { get; } = new() { Content = "Remove", Margin = new Thickness(8, 0, 0, 0), MinWidth = 72 };
        /// <summary>How filling in the words went (never the words).</summary>
        internal TextBlock Heard { get; } = new() { TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed, Margin = new Thickness(0, 6, 8, 0) };
        internal Button Fill { get; } = new() { Content = "_Fill in the words", Margin = new Thickness(0, 6, 0, 0), MinWidth = 130, VerticalAlignment = VerticalAlignment.Top };
        internal StackPanel Root { get; } = new();
        /// <summary>Cancels the words being filled in (null when none are).</summary>
        internal CancellationTokenSource? Listening { get; set; }
        /// <summary>The words last filled in, so choosing another file replaces them while they are unedited.</summary>
        internal string? Filled { get; set; }

        internal void Say(string? text)
        {
            Heard.Text = text ?? "";
            Heard.Visibility = text is null ? Visibility.Collapsed : Visibility.Visible;
        }
        /// <summary>The chosen file's length, or why Martlet can't use it (null while no file is chosen).</summary>
        internal int? Milliseconds { get; private set; }
        internal bool Unusable { get; private set; }
        internal bool TooLarge { get; private set; }

        internal void Measure()
        {
            (Milliseconds, Unusable, TooLarge) = (null, false, false);
            var file = Path.Text;
            if (!File.Exists(file)) return;
            try
            {
                if (new FileInfo(file).Length > SpeakingVoiceLibrary.MaximumAudioBytes) TooLarge = true;
                else Milliseconds = PcmWaveInfo.Inspect(File.ReadAllBytes(file), SpeakingVoiceLibrary.MaximumAudioBytes).DurationMilliseconds;
            }
            catch (Exception failure) when (failure is IOException or UnauthorizedAccessException or ContractException or OverflowException)
            {
                Unusable = true;
            }
        }
    }

    private F5AddVoiceDialog(string dataDirectory, string destination, RecordingTranscriber? transcriber)
    {
        this.dataDirectory = dataDirectory;
        this.destination = destination;
        this.transcriber = transcriber;
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
        var about = new TextBlock
        {
            Text = (transcriber is null
                    ? "Choose a clear WAV recording (mono 16-bit PCM) and type its exact words: F5-TTS, GPT-SoVITS and Dia read them " +
                        "along with it. With Parakeet downloaded in Companion › Listening, Martlet fills them in for you. "
                    : $"Choose a clear WAV recording (mono 16-bit PCM) and Martlet fills in its words with {transcriber.Name}. Check " +
                        "them and fix anything it misheard: F5-TTS, GPT-SoVITS and Dia read them along with it. ") +
                "You can add several recordings of the same voice: XTTS-v2 and GPT-SoVITS learn from each one, and the other " +
                $"engines hear them joined, one after another with a short pause ({SpeakingVoiceLibrary.MaximumDurationMilliseconds / 1000} " +
                "seconds in all). Martlet keeps a copy and shares it with your paired Martlet computers, so any of them can speak " +
                "with this voice.",
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 6)
        };
        AutomationProperties.SetAutomationId(about, "F5AddVoiceAbout");
        root.Children.Add(about);

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
            player?.Stop();
            foreach (var row in recordings) row.Listening?.Cancel();
        };
        AddRecording();
    }

    /// <summary>The newly added voice (bound to <paramref name="destination"/>), or null when canceled. <paramref name="transcriber"/>
    /// fills in each recording's words when there is one; the caller disposes it.</summary>
    internal static F5ReferenceSnapshot? Add(Window owner, string dataDirectory, string destination, RecordingTranscriber? transcriber)
    {
        var dialog = new F5AddVoiceDialog(dataDirectory, destination, transcriber) { Owner = owner };
        return dialog.ShowDialog() == true ? dialog.added : null;
    }

    /// <summary>Adds an empty recording row (null when the voice already has the most recordings it may).</summary>
    private Recording? AddRecording()
    {
        if (recordings.Count >= SpeakingVoiceLibrary.MaximumClips) return null;
        var row = new Recording();
        row.Browse.Click += (_, _) => Browse(row);
        row.Play.Click += async (_, _) => await PlayAsync(row);
        row.Fill.Click += async (_, _) => await FillAsync(row);
        row.Drop.Click += (_, _) =>
        {
            row.Listening?.Cancel();
            recordings.Remove(row);
            list.Children.Remove(row.Root);
            Renumber();
        };
        row.Path.TextChanged += (_, _) =>
        {
            row.Measure();
            Summarize();
            // A new file's words replace none or the ones filled in for the last file, never words the owner typed.
            if (row.Milliseconds is not null && (row.Transcript.Text.Trim().Length == 0 || row.Transcript.Text == row.Filled))
                FillAsync(row).Forget();
            else
            {
                row.Listening?.Cancel();
                row.Say(null);
                ShowFill(row);
            }
        };
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
        var heard = new DockPanel();
        DockPanel.SetDock(row.Fill, Dock.Right);
        heard.Children.Add(row.Fill);
        heard.Children.Add(row.Heard);
        row.Heard.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
        row.Root.Children.Add(row.FileLabel);
        row.Root.Children.Add(pick);
        row.Root.Children.Add(row.WordsLabel);
        row.Root.Children.Add(row.Transcript);
        row.Root.Children.Add(heard);
        ShowFill(row);
        recordings.Add(row);
        list.Children.Add(row.Root);
        Renumber();
        return row;
    }

    /// <summary>Fill in the words is shown only with a transcriber, and works once a usable file is chosen and nothing is
    /// being filled in for it.</summary>
    private void ShowFill(Recording row)
    {
        row.Fill.Visibility = transcriber is null ? Visibility.Collapsed : Visibility.Visible;
        row.Fill.IsEnabled = row.Milliseconds is not null && row.Listening is null;
    }

    /// <summary>Fills in <paramref name="row"/>'s words with <see cref="transcriber"/>, one recording at a time. Words typed while
    /// it listens are kept. The status line (<c>F5AddVoiceHeard</c>) says how it went, never the words.</summary>
    private async Task FillAsync(Recording row)
    {
        if (transcriber is null || row.Milliseconds is not { } milliseconds) return;
        row.Listening?.Cancel();
        if (milliseconds > transcriber.MaximumMilliseconds)
        {
            row.Say($"This recording is too long for {transcriber.Name} to fill in its words. Type them.");
            return;
        }
        using var stop = new CancellationTokenSource();
        row.Listening = stop;
        ShowFill(row);
        var file = row.Path.Text;
        var before = row.Transcript.Text;
        row.Say($"Filling in the words with {transcriber.Name}...");
        var waited = false;
        try
        {
            await listening.WaitAsync(stop.Token);
            waited = true;
            var text = await transcriber.TranscribeAsync(await File.ReadAllBytesAsync(file, stop.Token), stop.Token);
            stop.Token.ThrowIfCancellationRequested();
            if (row.Transcript.Text != before) row.Say("Kept the words you typed.");
            else if (text.Length == 0) row.Say($"{transcriber.Name} heard no words in this recording. Type them.");
            else if (!SpeakingVoiceLibrary.IsTranscript(text)) row.Say($"{transcriber.Name} heard more words than a voice can keep. Type them.");
            else
            {
                row.Transcript.Text = row.Filled = text;
                var words = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;
                row.Say($"Filled in by {transcriber.Name}: {words} word{(words == 1 ? "" : "s")}. Check them and fix anything it misheard.");
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            ErrorLog.Warn($"Filling in a voice recording's words with {transcriber.Name} failed.", error);
            row.Say($"Couldn't fill in the words with {transcriber.Name}: {RecordingTranscriber.Reason(error)}. Type them.");
        }
        finally
        {
            if (waited) listening.Release();
            if (ReferenceEquals(row.Listening, stop)) row.Listening = null;
            ShowFill(row);
        }
    }

    /// <summary>Labels and automation IDs by position: the first recording keeps the single-recording IDs
    /// (<c>F5AddVoicePath</c>, <c>F5AddVoiceTranscript</c>, <c>F5AddVoicePlay</c>, <c>F5AddVoiceBrowse</c>, <c>F5AddVoiceFill</c>,
    /// <c>F5AddVoiceHeard</c>); recording n of several adds "-n" and has <c>F5AddVoiceDrop-n</c>.</summary>
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
            AutomationProperties.SetName(row.Fill, several ? $"Fill in the words of recording {i + 1}" : "Fill in the words");
            AutomationProperties.SetName(row.Drop, $"Remove recording {i + 1}");
            AutomationProperties.SetAutomationId(row.Path, "F5AddVoicePath" + suffix);
            AutomationProperties.SetAutomationId(row.Transcript, "F5AddVoiceTranscript" + suffix);
            AutomationProperties.SetAutomationId(row.Play, "F5AddVoicePlay" + suffix);
            AutomationProperties.SetAutomationId(row.Browse, "F5AddVoiceBrowse" + suffix);
            AutomationProperties.SetAutomationId(row.Fill, "F5AddVoiceFill" + suffix);
            AutomationProperties.SetAutomationId(row.Heard, "F5AddVoiceHeard" + suffix);
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
        var dialog = new OpenFileDialog { Filter = "WAV recordings (*.wav)|*.wav", CheckFileExists = true, Multiselect = true };
        if (dialog.ShowDialog(this) != true || dialog.FileNames.Length == 0) return;
        row.Path.Text = dialog.FileNames[0];
        foreach (var file in dialog.FileNames.Skip(1))
        {
            var next = recordings.FirstOrDefault(r => r.Path.Text.Length == 0) ?? AddRecording();
            if (next is null)
            {
                Show($"A voice can have at most {SpeakingVoiceLibrary.MaximumClips} recordings.");
                break;
            }
            next.Path.Text = file;
        }
        if (name.Text.Length == 0) name.Text = Path.GetFileNameWithoutExtension(dialog.FileNames[0]);
    }

    /// <summary>Says how many recordings the voice has and, once their files are chosen, how long they are together (or which
    /// one Martlet can't use). Readable as <c>F5AddVoiceRecordings</c>; never the paths or words.</summary>
    private void Summarize()
    {
        var count = recordings.Count;
        var index = recordings.FindIndex(r => r.TooLarge || r.Unusable);
        var problem = index < 0 ? null
            : recordings[index].TooLarge ? $"Recording {index + 1} is larger than 4 MB."
            : $"Recording {index + 1} isn't a mono 16-bit PCM WAV Martlet can use.";
        var chosen = count > 0 && recordings.All(r => r.Milliseconds is not null);
        var seconds = (recordings.Sum(r => r.Milliseconds ?? 0) + (count - 1) * SpeakingVoiceLibrary.ClipPauseMilliseconds) / 1000d;
        summary.Text = problem ?? (count == 1
            ? chosen ? $"One recording, {seconds:0.#} seconds." : "One recording. Add more recordings of the same voice if you have them."
            : $"{count} recordings make one voice" + (chosen ? $", {seconds:0.#} seconds joined with the pauses" +
                (seconds * 1000 > SpeakingVoiceLibrary.MaximumDurationMilliseconds
                    ? $": more than {SpeakingVoiceLibrary.MaximumDurationMilliseconds / 1000} seconds, so leave some out." : ".")
                : "; choose each recording's file."));
    }

    private async Task PlayAsync(Recording row)
    {
        error.Visibility = Visibility.Collapsed;
        if (!File.Exists(row.Path.Text))
        {
            Show("Choose the recording (a WAV file) to play it.");
            return;
        }
        try
        {
            if (new FileInfo(row.Path.Text).Length > F5ReferenceLimits.MaximumAudioFileBytes)
            {
                Show("The recording must be a mono 16-bit PCM WAV of at most 4 MB.");
                return;
            }
            Play(await File.ReadAllBytesAsync(row.Path.Text));
        }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException or InvalidOperationException)
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
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException or InvalidOperationException)
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

    /// <summary>The recordings joined (null, after saying why, when one isn't chosen or is too large). Throws
    /// <see cref="ContractException"/> naming a recording Martlet can't use.</summary>
    private async Task<JoinedVoiceRecording?> JoinAsync()
    {
        var parts = new List<(ReadOnlyMemory<byte> Wave, string Transcript)>();
        for (var i = 0; i < recordings.Count; i++)
        {
            var file = recordings[i].Path.Text;
            if (!File.Exists(file))
            {
                Show($"Choose recording {i + 1} (a WAV file), or remove it.");
                return null;
            }
            if (new FileInfo(file).Length > SpeakingVoiceLibrary.MaximumAudioBytes)
            {
                Show($"Recording {i + 1} is larger than 4 MB.");
                return null;
            }
            parts.Add((await File.ReadAllBytesAsync(file), recordings[i].Transcript.Text));
        }
        return await Task.Run(() => SpeakingVoiceRecordings.Join(parts));
    }

    private async Task AcceptAsync()
    {
        error.Visibility = Visibility.Collapsed;
        player?.Stop();
        var several = recordings.Count > 1;
        var missing = recordings.FindIndex(r => !File.Exists(r.Path.Text));
        var unsaid = recordings.FindIndex(r => r.Transcript.Text.Trim().Length == 0);
        var problem = missing >= 0 ? several ? $"Choose recording {missing + 1} (a WAV file), or remove it." : "Choose the recording (a WAV file)."
            : name.Text.Trim().Length == 0 ? "Give the voice a name."
            : unsaid >= 0 && recordings[unsaid].Listening is not null
                ? several ? $"Martlet is still filling in the words of recording {unsaid + 1}. Wait a moment, or type them."
                    : "Martlet is still filling in the words. Wait a moment, or type them."
            : unsaid >= 0 ? several ? $"Type exactly what recording {unsaid + 1} says." : "Type exactly what the recording says."
            : rights.IsChecked != true ? "Confirm that you may use this voice."
            : null;
        if (problem is not null)
        {
            Show(problem);
            return;
        }
        ok.IsEnabled = false;
        string? staged = null;
        try
        {
            if (F5Voices.View(dataDirectory).Live.Count >= SpeakingVoiceLibrary.MaximumVoices)
            {
                Show("The voice list is full. Remove one you no longer use first.");
                return;
            }
            var source = Path.GetFullPath(recordings[0].Path.Text);
            var words = recordings[0].Transcript.Text.Trim();
            JoinedVoiceRecording? joined = null;
            if (several)
            {
                // Several recordings become one: joined after a short pause, with where each lies kept in the shared list.
                joined = await JoinAsync();
                if (joined is null) return;
                var folder = Path.Combine(dataDirectory, "speaking-voices-joining");
                Directory.CreateDirectory(folder);
                staged = source = Path.Combine(folder, $"{Guid.NewGuid():N}.wav");
                await File.WriteAllBytesAsync(staged, joined.Wave);
                words = joined.Transcript;
            }
            using (var store = F5Voices.Open(dataDirectory))
            {
                added = await store.SnapshotAsync(new()
                {
                    PresetName = name.Text.Trim(),
                    AbsoluteSourcePath = source,
                    Transcript = words,
                    Rights = new()
                    {
                        AcknowledgementId = Guid.NewGuid(),
                        Basis = (basis.SelectedItem as ComboBoxItem)?.Tag is F5VoiceRightsBasis chosenBasis ? chosenBasis : F5VoiceRightsBasis.OwnVoice,
                        StatementVersion = F5ReferenceLimits.RightsStatementVersion,
                        ProcessingDestinationId = destination,
                        AcknowledgedAtUtc = DateTimeOffset.UtcNow,
                        Confirmed = true
                    }
                });
            }
            F5Voices.Add(dataDirectory, added, words, joined);
            DialogResult = true;
        }
        catch (F5Exception failure) { Show(F5Voices.Describe(failure)); }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException or ArgumentException or ContractException)
        {
            Show(failure.Message);
        }
        finally
        {
            ok.IsEnabled = true;
            if (staged is not null)
            {
                try
                {
                    File.Delete(staged);
                    Directory.Delete(Path.GetDirectoryName(staged)!);
                }
                catch (Exception failure) when (failure is IOException or UnauthorizedAccessException) { }
            }
        }
    }

    private void Show(string text)
    {
        error.Text = text;
        error.Visibility = Visibility.Visible;
    }
}
