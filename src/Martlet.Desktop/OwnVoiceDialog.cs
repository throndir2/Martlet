using System.IO;
using System.Media;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Threading;
using Martlet.Audio;
using Martlet.Audio.Windows;
using Martlet.Core.Contracts;
using Martlet.Core.Settings;
using Martlet.Core.Voices;
using Martlet.F5;

namespace Martlet.Desktop;

/// <summary>Companion › Voice › Voices › Record your own voice: the owner reads the lines of <see cref="OwnVoiceScript"/> out
/// loud, one recording each, and Martlet makes one voice of them, as Add a voice does with several recordings. The lines are
/// long enough for every voice engine and hold nearly every sound of spoken English, and their words are known, so there is
/// nothing to type. Each line stops recording by itself when the owner pauses (<see cref="VoiceSampleRecording.Line"/>), and
/// the silence before and after it is cut (<see cref="OwnVoiceTake"/>). Nothing is recorded until the owner ticks the box that
/// says it is their own voice and presses Record; the recordings stay in memory until Add voice keeps them.</summary>
internal sealed class OwnVoiceDialog : ThemedWindow
{
    private readonly string dataDirectory;
    private readonly string destination;
    private readonly SetupOperationRunner operations;
    private readonly IAudioSessionEvents sessionEvents;
    private readonly AudioChoice? input;
    private readonly ICaptureDeviceFactory devices;
    private readonly TimeProvider clock;
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromMilliseconds(80) };
    private readonly List<Line> lines = [];
    private readonly CheckBox consent = new()
    {
        Content = new TextBlock
        {
            Text = "This is my own voice. Martlet may record it on this PC and keep it as a voice, shared with my paired Martlet computers.",
            TextWrapping = TextWrapping.Wrap
        },
        Margin = new Thickness(0, 10, 0, 0)
    };
    private readonly ProgressBar level = new() { Height = 6, Minimum = 0, Maximum = 1, Margin = new Thickness(0, 10, 0, 0) };
    private readonly TextBlock recorder = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0) };
    private readonly TextBlock summary = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 0) };
    private readonly TextBox name = new() { Text = "My voice" };
    private readonly TextBlock error = new() { TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed, Margin = new Thickness(0, 8, 0, 0) };
    private readonly Button playAll = new() { Content = "Play _all", MinWidth = 110, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 8, 0, 0) };
    private readonly Button ok = new() { Content = "_Add voice", IsDefault = true, MinWidth = 110 };
    private VoiceSampleRecording? recording;
    private Line? recordingLine;
    private F5ReferenceSnapshot? added;
    private SoundPlayer? player;
    private bool closed;

    /// <summary>One line to read: its words, what became of its recording, and its Record and Play buttons.</summary>
    private sealed class Line(int number, string text)
    {
        internal int Number { get; } = number;
        internal string Text { get; } = text;
        internal TextBlock State { get; } = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0) };
        internal Button Record { get; } = new() { MinWidth = 120, Margin = new Thickness(0, 8, 10, 0) };
        internal Button Play { get; } = new() { Content = "Play", MinWidth = 72, Margin = new Thickness(0, 8, 10, 0) };
        internal OwnVoiceTake? Take { get; set; }
    }

    private OwnVoiceDialog(string dataDirectory, string destination, SetupOperationRunner operations, IAudioSessionEvents sessionEvents,
        AudioChoice? input, ICaptureDeviceFactory devices, TimeProvider clock)
    {
        this.dataDirectory = dataDirectory;
        this.destination = destination;
        this.operations = operations;
        this.sessionEvents = sessionEvents;
        this.input = input;
        this.devices = devices;
        this.clock = clock;
        SetResourceReference(StyleProperty, "AppWindowStyle");
        Title = "Martlet - Record your own voice";
        Width = 680;
        MaxHeight = SystemParameters.WorkArea.Height - 40;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        AutomationProperties.SetAutomationId(this, "OwnVoiceDialog");

        var root = new StackPanel { Margin = new Thickness(24) };
        var heading = new TextBlock { Text = "Record your own voice", TextWrapping = TextWrapping.Wrap };
        heading.SetResourceReference(StyleProperty, "SectionHeading");
        root.Children.Add(heading);
        var about = new TextBlock
        {
            Text = $"Read each of the {OwnVoiceScript.Lines.Count} lines out loud in your normal voice, at your normal pace. Together " +
                "they hold nearly every sound of spoken English, so Martlet can copy your voice well. Use a quiet room and the " +
                "microphone you talk to Martlet with. Recording stops by itself when you pause at the end of a line. Together " +
                $"the lines must stay under {SpeakingVoiceLibrary.MaximumDurationMilliseconds / 1000} seconds.",
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0)
        };
        AutomationProperties.SetAutomationId(about, "OwnVoiceAbout");
        root.Children.Add(about);
        AutomationProperties.SetAutomationId(consent, "OwnVoiceConsent");
        consent.Checked += (_, _) => Render();
        consent.Unchecked += (_, _) =>
        {
            recording?.Stop();
            Render();
        };
        root.Children.Add(consent);
        AutomationProperties.SetName(level, "Microphone level");
        AutomationProperties.SetAutomationId(level, "OwnVoiceLevel");
        root.Children.Add(level);
        AutomationProperties.SetAutomationId(recorder, "OwnVoiceRecorder");
        AutomationProperties.SetLiveSetting(recorder, AutomationLiveSetting.Polite);
        recorder.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
        root.Children.Add(recorder);

        var list = new StackPanel();
        foreach (var (text, index) in OwnVoiceScript.Lines.Select((text, index) => (text, index)))
            list.Children.Add(AddLine(new Line(index + 1, text)));
        root.Children.Add(list);

        AutomationProperties.SetAutomationId(playAll, "OwnVoicePlayAll");
        playAll.Click += async (_, _) => await PlayAllAsync();
        root.Children.Add(playAll);
        AutomationProperties.SetAutomationId(summary, "OwnVoiceSummary");
        root.Children.Add(summary);

        AutomationProperties.SetName(name, "Voice name");
        AutomationProperties.SetAutomationId(name, "OwnVoiceName");
        root.Children.Add(new Label { Content = "_Name", Target = name, Padding = new Thickness(0, 10, 0, 4) });
        root.Children.Add(name);

        AutomationProperties.SetAutomationId(error, "OwnVoiceProblem");
        error.SetResourceReference(TextBlock.ForegroundProperty, "WarningBrush");
        root.Children.Add(error);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 14, 0, 0) };
        var cancel = new Button { Content = "_Cancel", IsCancel = true, MinWidth = 90, Margin = new Thickness(0, 0, 12, 0) };
        AutomationProperties.SetAutomationId(cancel, "OwnVoiceCancel");
        ok.SetResourceReference(StyleProperty, "PrimaryButton");
        AutomationProperties.SetAutomationId(ok, "OwnVoiceOk");
        ok.Click += async (_, _) => await AcceptAsync();
        buttons.Children.Add(cancel);
        buttons.Children.Add(ok);
        root.Children.Add(buttons);
        Content = new ScrollViewer { Content = root, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };

        timer.Tick += (_, _) => Poll();
        sessionEvents.LockedChanged += SessionLocked;
        Closing += (_, _) => Shut();
        Render();
    }

    /// <summary>The newly recorded voice (bound to <paramref name="destination"/>), or null when canceled.</summary>
    internal static F5ReferenceSnapshot? Record(Window owner, string dataDirectory, string destination, SetupOperationRunner operations,
        IAudioSessionEvents sessionEvents, AudioChoice? input)
    {
        var dialog = new OwnVoiceDialog(dataDirectory, destination, operations, sessionEvents, input, new WasapiCaptureDeviceFactory(),
            TimeProvider.System) { Owner = owner };
        return dialog.ShowDialog() == true ? dialog.added : null;
    }

    /// <summary>A line's card: "Line n of N", its words (<c>OwnVoiceLine-n</c>), its state (<c>OwnVoiceLineState-n</c>) and its
    /// Record (<c>OwnVoiceRecord-n</c>, Stop while it records) and Play (<c>OwnVoicePlay-n</c>) buttons.</summary>
    private Border AddLine(Line line)
    {
        lines.Add(line);
        var count = OwnVoiceScript.Lines.Count;
        var stack = new StackPanel();
        var number = new TextBlock { Text = $"Line {line.Number} of {count}", FontWeight = FontWeights.SemiBold };
        stack.Children.Add(number);
        var words = new TextBlock { Text = $"\u201C{line.Text}\u201D", FontSize = 17, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0) };
        AutomationProperties.SetAutomationId(words, $"OwnVoiceLine-{line.Number}");
        stack.Children.Add(words);
        AutomationProperties.SetAutomationId(line.State, $"OwnVoiceLineState-{line.Number}");
        line.State.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
        stack.Children.Add(line.State);
        AutomationProperties.SetAutomationId(line.Record, $"OwnVoiceRecord-{line.Number}");
        AutomationProperties.SetAutomationId(line.Play, $"OwnVoicePlay-{line.Number}");
        AutomationProperties.SetName(line.Play, $"Play line {line.Number}");
        line.Record.Click += (_, _) => RecordOrStop(line);
        line.Play.Click += (_, _) => PlayLine(line);
        var row = new WrapPanel();
        row.Children.Add(line.Record);
        row.Children.Add(line.Play);
        stack.Children.Add(row);
        var card = new Border { Child = stack, Padding = new Thickness(14, 12, 14, 12), Margin = new Thickness(0, 12, 0, 0) };
        card.SetResourceReference(StyleProperty, "Chip");
        card.HorizontalAlignment = HorizontalAlignment.Stretch;
        return card;
    }

    private void SessionLocked(bool locked)
    {
        if (locked) Dispatcher.BeginInvoke(() => recording?.Stop());
    }

    /// <summary>Records <paramref name="line"/>, or, while it records, ends its recording now (as a pause would).</summary>
    private void RecordOrStop(Line line)
    {
        if (closed) return;
        if (recording is { } active)
        {
            if (ReferenceEquals(recordingLine, line)) active.Finish();
            return;
        }
        if (consent.IsChecked != true)
        {
            Show("Tick the box that says this is your own voice first.");
            return;
        }
        error.Visibility = Visibility.Collapsed;
        player?.Stop();
        recording = VoiceSampleRecording.Start(operations, devices, input, clock, VoiceSampleRecording.Line);
        if (recording is null)
        {
            recorder.Text = "Another Martlet task is using the microphone. Let it finish, then try again.";
            return;
        }
        recordingLine = line;
        recorder.Text = $"Recording line {line.Number}... read it out loud, then pause.";
        timer.Start();
        Render();
    }

    private void Poll()
    {
        if (recording is not { } active || recordingLine is not { } line)
        {
            timer.Stop();
            return;
        }
        level.Value = Math.Clamp((active.Level + 60) / 50, 0, 1);
        if (!active.Worker.Completion.IsCompleted) return;
        timer.Stop();
        (recording, recordingLine) = (null, null);
        level.Value = 0;
        if (active.Pcm is not { } pcm)
        {
            recorder.Text = active.Error ?? "Recording failed. Try again.";
            Render();
            return;
        }
        try { Keep(line, pcm, reachedLimit: pcm.Length >= active.Shape.MaximumPcmBytes - EnergyVoiceActivityDetector.FrameBytes); }
        finally
        {
            CryptographicOperations.ZeroMemory(pcm);
            active.Clear();
        }
        Render();
    }

    /// <summary>Keeps the speech in a line's new recording, unless it is too short or went on to the time limit (a line that
    /// was already recorded keeps its earlier recording then).</summary>
    private void Keep(Line line, byte[] pcm, bool reachedLimit)
    {
        var take = OwnVoiceTake.From(pcm);
        var earlier = line.Take is null ? "" : " Your earlier recording of it is kept.";
        if (take is null || take.SpeechSeconds < OwnVoiceTake.MinimumSpeechSeconds)
        {
            recorder.Text = $"Only {take?.SpeechSeconds ?? 0:0.0} seconds of speech was heard on line {line.Number}. " +
                "Move closer to the microphone or speak up, then record it again." + earlier;
            return;
        }
        if (reachedLimit)
        {
            recorder.Text = $"Line {line.Number} went on for {VoiceSampleRecording.Line.MaximumDuration.TotalSeconds:0} seconds without " +
                "a pause, so it may be cut off. Record it again and pause when you finish." + earlier;
            return;
        }
        line.Take = take;
        var next = lines.FirstOrDefault(l => l.Take is null);
        recorder.Text = next is null
            ? $"Line {line.Number} recorded. All the lines are recorded: play them, then add the voice."
            : $"Line {line.Number} recorded. Next, record line {next.Number}.";
    }

    private void PlayLine(Line line)
    {
        if (line.Take is { } take) Play(take.Wave);
    }

    /// <summary>Plays the lines joined as the voice will keep them, with the pause between them.</summary>
    private async Task PlayAllAsync()
    {
        error.Visibility = Visibility.Collapsed;
        try
        {
            if (await JoinAsync() is { } joined) Play(joined.Wave);
        }
        catch (ContractException failure) { Show(failure.Message); }
    }

    private void Play(byte[] audio)
    {
        try
        {
            player?.Stop();
            player = new SoundPlayer(new MemoryStream(audio));
            player.Play();
        }
        catch (Exception failure) when (failure is IOException or InvalidOperationException)
        {
            Show("This recording can't be played: " + failure.Message);
        }
    }

    /// <summary>Every line's recording joined into one voice (null, after saying why, while one is missing or together they
    /// are too long). Throws <see cref="ContractException"/> when the recordings can't be joined.</summary>
    private async Task<JoinedVoiceRecording?> JoinAsync()
    {
        if (Unready() is { } problem)
        {
            Show(problem);
            return null;
        }
        var takes = lines.Select(l => l.Take!).ToArray();
        return await Task.Run(() => Join(takes));
    }

    /// <summary>Why the lines can't become a voice yet: a line isn't recorded, or together they are too long. Null when they can.</summary>
    private string? Unready()
    {
        if (lines.FirstOrDefault(l => l.Take is null) is { } missing) return $"Record line {missing.Number} first.";
        var milliseconds = OwnVoiceTake.JoinedMilliseconds(lines.Select(l => l.Take!).ToArray());
        if (milliseconds <= SpeakingVoiceLibrary.MaximumDurationMilliseconds) return null;
        var longest = lines.MaxBy(l => l.Take!.DurationMilliseconds)!;
        return $"Together the lines last {milliseconds / 1000d:0.#} seconds, and a voice may last at most " +
            $"{SpeakingVoiceLibrary.MaximumDurationMilliseconds / 1000} seconds. Record line {longest.Number} again a little faster.";
    }

    /// <summary>The recordings of <see cref="OwnVoiceScript.Lines"/>, in order, joined into one voice whose transcript is the
    /// lines' words.</summary>
    private static JoinedVoiceRecording Join(IReadOnlyList<OwnVoiceTake> takes) =>
        SpeakingVoiceRecordings.Join(takes.Select((take, i) => ((ReadOnlyMemory<byte>)take.Wave, OwnVoiceScript.Lines[i])).ToArray());

    /// <summary>Adds the voice made from <paramref name="takes"/> (one recording of each line of <see cref="OwnVoiceScript"/>)
    /// to the owner's voices as their own voice, kept for <paramref name="destination"/>, and returns it. Throws
    /// <see cref="ContractException"/> when the recordings can't be joined and <see cref="F5Exception"/> when the voice
    /// store refuses them.</summary>
    internal static async Task<F5ReferenceSnapshot> AddVoiceAsync(string dataDirectory, string destination, string name,
        IReadOnlyList<OwnVoiceTake> takes, CancellationToken token)
    {
        var joined = await Task.Run(() => Join(takes), token);
        var added = await F5Voices.SnapshotAsync(dataDirectory, name, joined.Wave, joined.Transcript, new()
        {
            AcknowledgementId = Guid.NewGuid(),
            Basis = F5VoiceRightsBasis.OwnVoice,
            StatementVersion = F5ReferenceLimits.RightsStatementVersion,
            ProcessingDestinationId = destination,
            AcknowledgedAtUtc = DateTimeOffset.UtcNow,
            Confirmed = true
        }, token);
        F5Voices.Add(dataDirectory, added, joined.Transcript, joined);
        return added;
    }

    private async Task AcceptAsync()
    {
        error.Visibility = Visibility.Collapsed;
        player?.Stop();
        var problem = recording is not null ? "Finish the line you are recording first."
            : Unready() is { } unready ? unready
            : !SpeakingVoiceLibrary.IsName(name.Text.Trim()) ? "Give the voice a name."
            : consent.IsChecked != true ? "Tick the box that says this is your own voice."
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
                Show($"The voice list is full ({SpeakingVoiceLibrary.MaximumVoices:N0} voices). Remove one you no longer use first.");
                return;
            }
            added = await AddVoiceAsync(dataDirectory, destination, name.Text.Trim(), lines.Select(l => l.Take!).ToArray(),
                CancellationToken.None);
            DialogResult = true;
        }
        catch (F5Exception failure) { Show(F5Voices.Describe(failure)); }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException)
        {
            // These messages usually name a path, and the problem line is returned to automation.
            Show("Martlet couldn't save the voice on this PC. Check that there is free disk space, then try again.");
        }
        catch (Exception failure) when (failure is ArgumentException or ContractException) { Show(failure.Message); }
        finally
        {
            if (!closed) ok.IsEnabled = true;
        }
    }

    /// <summary>Each line's button and state, and the line on how many lines are recorded and how long they are together
    /// (<c>OwnVoiceSummary</c>; never the recordings themselves).</summary>
    private void Render()
    {
        var allowed = consent.IsChecked == true;
        foreach (var line in lines)
        {
            var now = ReferenceEquals(recordingLine, line);
            line.Record.Content = now ? "_Stop" : line.Take is null ? "_Record" : "Record _again";
            AutomationProperties.SetName(line.Record, now ? $"Stop recording line {line.Number}"
                : line.Take is null ? $"Record line {line.Number}" : $"Record line {line.Number} again");
            line.Record.IsEnabled = now || (allowed && recording is null);
            line.Play.IsEnabled = line.Take is not null && recording is null;
            line.State.Text = now ? "Recording..."
                : line.Take is { } take ? $"Recorded: {take.DurationMilliseconds / 1000d:0.0} seconds."
                : "Not recorded yet.";
        }
        var done = lines.Count(l => l.Take is not null);
        var milliseconds = OwnVoiceTake.JoinedMilliseconds(lines.Where(l => l.Take is not null).Select(l => l.Take!).ToArray());
        summary.Text = done == 0 ? $"No lines recorded yet. Record all {lines.Count} lines to make your voice."
            : $"{done} of {lines.Count} lines recorded, {milliseconds / 1000d:0.0} seconds together with the pauses" +
              (milliseconds > SpeakingVoiceLibrary.MaximumDurationMilliseconds
                  ? $": more than {SpeakingVoiceLibrary.MaximumDurationMilliseconds / 1000} seconds, so record the longest line again a little faster."
                  : done == lines.Count ? ". Ready to add." : ".");
        playAll.IsEnabled = done == lines.Count && recording is null;
        if (recording is null && (recorder.Text.Length == 0 || recorder.Text is TickFirst or PressRecord))
            recorder.Text = allowed ? PressRecord : TickFirst;
    }

    private const string TickFirst = "Tick the box above to start recording.";
    private const string PressRecord = "Press Record beside a line, then read it out loud.";

    private void Show(string text)
    {
        error.Text = text;
        error.Visibility = Visibility.Visible;
    }

    private void Shut()
    {
        if (closed) return;
        closed = true;
        timer.Stop();
        player?.Stop();
        if (recording is { } active)
        {
            active.Stop();
            _ = active.Worker.Completion.ContinueWith(_ => active.Clear(), TaskScheduler.Default);
        }
        foreach (var line in lines)
            if (line.Take is { } take) CryptographicOperations.ZeroMemory(take.Wave);
        sessionEvents.LockedChanged -= SessionLocked;
    }
}
