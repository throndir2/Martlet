using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Martlet.Avatar.Audio2Face.Remote;
using Martlet.Avatar.Hosting;
using Martlet.Core.Contracts;
using Martlet.Core.Reading;

namespace Martlet.Desktop;

/// <summary>
/// Companion › Reading (docs/READING.md): where Martlet reads the text on your screen while it watches (reading.json, this PC's
/// own choice): Windows OCR on this PC (the default and recommended: on the processor, nothing leaves the PC), Martlet's
/// <c>ocr</c> host role (RapidOCR on the processor in Docker, set up on this PC or another of your computers with the same
/// martlet-host flow as every role, reached through its gateway) or off. Read my screen now reads the whole screen once with
/// the saved choice. Automation IDs: <c>ReadingNow</c>, <c>ReadingLast</c>, <c>ReadingPlace-&lt;place&gt;</c>,
/// <c>ReadingWindowsState</c>, <c>ReadingUseThisPc</c>, <c>ReadingHost-&lt;host&gt;</c>, <c>ReadingEngine</c>,
/// <c>ReadingFeatures</c>, <c>ReadingHostState</c>, <c>ReadingSetUp</c>, <c>ReadingUseHost</c>, <c>ReadingTurnOff</c>,
/// <c>ReadingTest</c>, <c>ReadingTestState</c> and <c>ReadingTestText</c>.
/// </summary>
public partial class MainWindow
{
    internal const string ReadingThisPc = "this-pc";
    internal const string ReadingModel = "rapidocr-ppocrv4";

    internal static readonly IReadOnlyList<string> ReadingFeatures =
        ["Processor only", "Docker", "RapidOCR, Apache-2.0", "Good with game fonts", "About 0.5-1 s a screenshot"];

    private const string ReadingTerms =
        "It builds a container from python:3.12 with hash-pinned RapidOCR 1.4.4 (Apache-2.0) and its PaddleOCR PP-OCRv4 models " +
        "(Apache-2.0, about 15 MB, inside the package), ONNX Runtime (MIT) and OpenCV (Apache-2.0). While Martlet watches your " +
        "screen, this PC sends screenshots to that computer. They are read in memory there and are not kept.";

    private ReadingPlace? readingShown;
    private string? readingHost;
    private string? readingPending;
    private string? readingFailure;
    private string? readingTestState;
    private string? readingTestText;
    private bool readingTesting;
    private bool? readingWindows;
    private bool readingWindowsBusy;

    private void RenderReadingTab(Panel page)
    {
        var saved = ReadingSettings.Load(store?.DataDirectory);
        var place = readingShown ?? saved.Place;
        if (readingWindows is null && !readingWindowsBusy) CheckWindowsReadingAsync().Forget();

        var now = new TextBlock { Text = ReadingNow(saved), FontSize = 15, TextWrapping = TextWrapping.Wrap };
        AutomationProperties.SetAutomationId(now, "ReadingNow");
        var last = Note(ScreenReader.LastReport is { } report ? "While watching: " + report
            : "Nothing read yet. Martlet reads while you watch your screen with it (Start watching).", new Thickness(0, 4, 0, 0));
        AutomationProperties.SetAutomationId(last, "ReadingLast");
        var test = Note(readingTesting ? "Reading your screen…" : readingTestState ?? "", new Thickness(0, 8, 0, 0));
        AutomationProperties.SetAutomationId(test, "ReadingTestState");
        var text = new TextBox
        {
            Text = readingTestText ?? "", IsReadOnly = true, TextWrapping = TextWrapping.Wrap, MaxHeight = 220, MinHeight = 60,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Margin = new Thickness(0, 6, 0, 0),
            Visibility = readingTestText is null ? Visibility.Collapsed : Visibility.Visible
        };
        AutomationProperties.SetName(text, "Text Martlet read on your screen");
        AutomationProperties.SetAutomationId(text, "ReadingTestText");
        var readNow = saved.On ? PageButton("Read my screen now", () => ReadScreenNowAsync().Forget(), id: "ReadingTest") : null;
        if (readNow is not null) readNow.IsEnabled = !readingTesting;
        page.Children.Add(Card(Heading("Now"), now,
            Note("While Martlet watches your screen, it reads the text on each changed screenshot, off to the side, so replies " +
                "never wait for it. New text (a score, \"Victory\", a new message) makes a look more likely, and each look gets the " +
                "text it read. What you type or say never waits for it. The text is never kept in the conversation.",
                new Thickness(0, 4, 0, 0)), last, test, text, Row(readNow)));

        var where = new StackPanel();
        where.Children.Add(Heading("Where it reads"));
        where.Children.Add(Note("This choice stays on this PC.", new Thickness(0, 0, 0, 10)));
        foreach (var (value, label, detail) in new (ReadingPlace, string, string)[]
        {
            (ReadingPlace.ThisPc, "Windows OCR on this PC (recommended)",
                "Built into Windows. Runs on this PC's processor in a fraction of a second. Nothing leaves this PC, and it's free."),
            (ReadingPlace.Host, "Martlet's Reading role",
                "RapidOCR in Docker, on this PC or another of your computers. Often better with game fonts. It runs on that " +
                "computer's processor (no graphics card needed). Screenshots go to that computer and are not kept."),
            (ReadingPlace.Off, "Off", "Martlet only looks at the pictures; it doesn't read the text on them separately.")
        })
        {
            var option = Choice("ReadingPlace", label + (value == saved.Place ? "  \u00b7  in use" : ""), detail, value == place, "ReadingPlace-" + value);
            option.Checked += (_, _) =>
            {
                readingShown = value;
                RenderTab();
            };
            where.Children.Add(option);
        }
        page.Children.Add(Card(where));

        page.Children.Add(place switch
        {
            ReadingPlace.ThisPc => ReadingThisPcCard(saved),
            ReadingPlace.Host => ReadingHostCard(saved),
            _ => Card(Heading("Off"), Note("Martlet still sees your screen while it watches, but it doesn't read the text on it separately.",
                    new Thickness(0, 0, 0, 0)),
                Row(saved.Place == ReadingPlace.Off ? null
                    : PageButton("Turn reading off", () => SaveReading(new ReadingSettings { Place = ReadingPlace.Off, ChosenAt = DateTimeOffset.Now },
                        "Reading is off."), primary: true, id: "ReadingTurnOff")))
        });
    }

    private static string ReadingNow(ReadingSettings saved) => saved.On
        ? $"Martlet reads the text on your screen with {saved.Describe()} while it watches."
        : "Off. Martlet doesn't read the text on your screen separately.";

    /// <summary>Saves this PC's choice; watching picks it up at its next screenshot.</summary>
    private bool SaveReading(ReadingSettings next, string done)
    {
        if (store is null) return false;
        try
        {
            if (!next.Save(store.DataDirectory)) throw new InvalidOperationException("Couldn't save Reading. Check access to Martlet's data folder.");
        }
        catch (Exception error) when (error is ContractException or InvalidOperationException)
        {
            ActionText.Text = error.Message;
            return false;
        }
        readingShown = null;
        readingTestState = null;
        readingTestText = null;
        ErrorLog.Info($"Reading: now {next.Describe()}.");
        ActionText.Text = done;
        if (!closing && openTab == CompanionTab.Reading) RenderTab();
        return true;
    }

    private async Task CheckWindowsReadingAsync()
    {
        readingWindowsBusy = true;
        try { readingWindows = await Task.Run(() => WindowsScreenTextReader.Available); }
        finally { readingWindowsBusy = false; }
        if (!closing && openTab == CompanionTab.Reading && !tabEdited) RenderTab();
    }

    // ---------- Windows OCR on this PC ----------

    private Border ReadingThisPcCard(ReadingSettings saved)
    {
        var inUse = saved.Place == ReadingPlace.ThisPc;
        var state = Note(readingWindows switch
        {
            true => "Windows can read text on this PC." + (inUse ? " Martlet reads here." : ""),
            false => WindowsScreenTextReader.NoLanguage,
            _ => "Checking Windows text recognition…"
        }, new Thickness(0, 4, 0, 0));
        if (readingWindows == false) state.SetResourceReference(TextBlock.ForegroundProperty, "WarningBrush");
        AutomationProperties.SetAutomationId(state, "ReadingWindowsState");
        return Card(Heading("Windows OCR on this PC"),
            Note("Windows' own text recognition, in your Windows languages. It needs no download and no graphics card, and it reads " +
                "a screenshot in tens of milliseconds on the processor.", new Thickness(0, 0, 0, 4)),
            state,
            Row(inUse ? null : PageButton("Read on this PC", () => SaveReading(new ReadingSettings { Place = ReadingPlace.ThisPc, ChosenAt = DateTimeOffset.Now },
                "Martlet now reads the text on your screen with Windows OCR on this PC."), primary: true, id: "ReadingUseThisPc")));
    }

    // ---------- Martlet's Reading role ----------

    private Border ReadingHostCard(ReadingSettings saved)
    {
        var stack = new List<UIElement>
        {
            Heading("Martlet's Reading role"),
            Note("RapidOCR with its PaddleOCR models (Apache-2.0), set up by Martlet in Docker like its other roles. It runs on the " +
                "processor, so it never takes the graphics card from the voice or thinking.", new Thickness(0, 0, 0, 4))
        };
        var thisPc = ThisPcHost();
        var others = NetworkMap.Hosts(Inputs()).Where(h => h.HostId != thisPc?.HostId).ToArray();
        var reads = others.FirstOrDefault(h => Offers(h, HostRoles.Ocr));
        var shown = readingHost ?? (saved.Place == ReadingPlace.Host && saved.HostId is { } chosen
            ? thisPc?.HostId == chosen ? ReadingThisPc : chosen
            : thisPc is not null && Offers(thisPc, HostRoles.Ocr) ? ReadingThisPc : reads?.HostId ?? ReadingThisPc);
        if (others.Length > 0)
        {
            var pills = new WrapPanel { Margin = new Thickness(0, 2, 0, 2) };
            foreach (var (id, label) in new[] { (ReadingThisPc, "This PC") }.Concat(others.Select(h => (h.HostId, h.HostId))))
            {
                var pill = new RadioButton { Content = label, GroupName = "ReadingHost", IsChecked = id == shown };
                pill.SetResourceReference(StyleProperty, "FilterPill");
                AutomationProperties.SetName(pill, label);
                AutomationProperties.SetAutomationId(pill, "ReadingHost-" + id);
                pill.Checked += (_, _) =>
                {
                    if (readingHost == id) return;
                    readingHost = id;
                    RenderTab();
                };
                pills.Children.Add(pill);
            }
            stack.Add(pills);
        }
        var onThisPc = shown == ReadingThisPc;
        var target = onThisPc ? thisPc : FindHost(shown);
        var where = onThisPc ? "this PC" : shown;
        var ready = target is not null && Offers(target, HostRoles.Ocr);
        var pending = readingPending == shown;
        var inUse = saved.Place == ReadingPlace.Host && target is not null && saved.HostId == target.HostId;
        var cannot = ready || onThisPc ? null : CannotHand(shown, HostRoles.Ocr, "reading");
        var state = ready ? $"Ready on {where}." + (inUse ? " Martlet reads there." : "")
            : pending ? $"Setting up on {where}..."
            : cannot ?? (readingFailure is { } failed ? $"Setup failed on {where}: {failed}" : $"Not set up on {where} yet.");
        var title = OptionTitle("Reading", ready ? "ready" : null, 15);
        AutomationProperties.SetAutomationId(title, "ReadingEngine");
        var chips = Chips("reading", ReadingFeatures, feature => feature switch
        {
            "Processor only" => "No graphics card needed: it never competes with the voice or thinking for the card.",
            "About 0.5-1 s a screenshot" => "Measured on a 24-thread desktop processor with a 1024 x 576 screenshot. It reads only " +
                "screenshots that changed, off to the side, so replies never wait for it.",
            _ => null
        });
        AutomationProperties.SetAutomationId(chips, "ReadingFeatures");
        var stateLine = Note(state, new Thickness(0, 4, 0, 0));
        if (cannot is not null || readingFailure is not null && !pending && !ready) stateLine.SetResourceReference(TextBlock.ForegroundProperty, "WarningBrush");
        AutomationProperties.SetAutomationId(stateLine, "ReadingHostState");
        var setUp = PageButton(ready ? "Ready" : pending ? "Setting up..." : "Set up", () => SetUpReadingAsync(onThisPc ? null : target).Forget(),
            primary: !ready, id: "ReadingSetUp");
        setUp.IsEnabled = !ready && !pending && cannot is null && readingPending is null;
        if (cannot is not null)
        {
            setUp.ToolTip = cannot;
            ToolTipService.SetShowOnDisabled(setUp, true);
            AutomationProperties.SetHelpText(setUp, cannot);
        }
        var details = new StackPanel { Children = { title, chips, stateLine } };
        setUp.VerticalAlignment = VerticalAlignment.Top;
        setUp.Margin = new Thickness(12, 0, 0, 0);
        var row = new DockPanel();
        DockPanel.SetDock(setUp, Dock.Right);
        row.Children.Add(setUp);
        row.Children.Add(details);
        var option = new Border { Child = row, BorderThickness = new Thickness(ready ? 2 : 1), CornerRadius = new CornerRadius(12),
            Padding = new Thickness(14, 12, 14, 12), Margin = new Thickness(0, 8, 0, 0) };
        option.SetResourceReference(Border.BorderBrushProperty, ready ? "AccentBrush" : "BorderBrush");
        stack.Add(option);
        if (ready && !inUse)
            stack.Add(Row(PageButton($"Read on {where}", () => UseReadingHost(target!.HostId, where), primary: true, id: "ReadingUseHost")));
        return Card([.. stack]);
    }

    private void UseReadingHost(string hostId, string where) =>
        SaveReading(new ReadingSettings { Place = ReadingPlace.Host, HostId = hostId, ChosenAt = DateTimeOffset.Now },
            $"Martlet now reads the text on your screen on {where}.");

    /// <summary>Sets the Reading role up on <paramref name="host"/> (null: this PC) after one confirmation naming its downloads,
    /// licences and what goes there, through the same martlet-host add as every role, then has Martlet read there.</summary>
    private async Task SetUpReadingAsync(PairedHost? host)
    {
        if (store is null || setupService is null || closing || readingPending is not null) return;
        var where = host is null ? "this PC" : host.HostId;
        var thisPc = ThisPcHost();
        if (!ConfirmationDialog.Confirm(this,
                $"Set up Reading on {where}?\n\n" +
                (host is null && thisPc is null
                    ? machine.DockerInstalled ? "Martlet first sets up its host service in Docker Desktop. "
                        : "Martlet first installs Docker Desktop (it asks for its own terms) and sets up its host service. "
                    : "") +
                "It runs on the processor and downloads about 200 MB of packages; Martlet shows the progress.\n\n" + ReadingTerms,
                "Set up Reading"))
            return;
        var answers = new Dictionary<string, string>(StringComparer.Ordinal) { ["choice.OCR_MODEL"] = ReadingModel };
        readingFailure = null;
        readingPending = host?.HostId ?? ReadingThisPc;
        RenderTab();
        try
        {
            if (host is not null)
            {
                var done = await RunHostActionAsync(host, HostRoles.Get(HostRoles.Ocr).Add, answers);
                if (closing) return;
                if (done is null) readingFailure = $"Setting it up on {host.HostId} stopped. Its run window has details.";
                else if (await WaitForReadingAsync(host, lifetime.Token) is { } why)
                    readingFailure = $"Setup finished, but Martlet can't see Reading on {host.HostId} yet ({why}). Check it in a minute.";
                else UseReadingHost(host.HostId, host.HostId);
                return;
            }
            string? stopped = null;
            async Task<string> Continue(HostRunWindow run, PairedHost pc)
            {
                var target = pc.Target(Version);
                await HostLocal.EnsureDockerAsync(run, Martlet.Core.Installation.ContinueSetupKind.Docker);
                target = await HostLocal.EngineForChangeAsync(target, HostRoles.Ocr, run);
                run.Status("Installing Reading on this PC...");
                var exit = await HostLocal.EngineAsync(target, ["add", HostRoles.Ocr], SetupProgress(run, "Reading", why => stopped ??= why),
                    run.Token, answers: answers);
                if (exit != 0)
                    throw new InvalidOperationException($"Installing Reading stopped{(stopped is null ? $" (exit {exit})" : ": " + stopped)}. The output has details.");
                run.Status("Checking Reading on this PC...");
                if (await WaitForReadingAsync(pc, run.Token) is { } why)
                    throw new InvalidOperationException(stopped = $"Setup finished, but Martlet can't see Reading yet ({why}). Check this PC in a minute.");
                Dispatcher.Invoke(() => UseReadingHost(pc.HostId, "this PC"));
                return "Reading is ready on this PC.";
            }
            string? status;
            if (thisPc is not null)
                status = await HostRunWindow.RunAsync(this, "Set up Reading on this PC", run => Continue(run, thisPc)) ??
                    (stopped is null ? "Reading wasn't set up. The run window has details." : $"Reading wasn't set up: {stopped}");
            else
            {
                (_, status) = await HostsWindow.SetUpThisPcAsync(this, new AvatarProfileStore(store.DataDirectory), setupService,
                    text => ActionText.Text = text, lifetime.Token, Continue, "Set up Reading on this PC");
                await ReadMachineAsync();
            }
            if (!closing && status is not null)
            {
                ActionText.Text = status;
                if (!status.StartsWith("Reading is ready", StringComparison.Ordinal)) readingFailure = status;
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException or ContractException or
            JsonException or ArgumentException or Audio2FaceHostException || ClusterSync.IsHostFailure(error))
        {
            readingFailure = error.Message;
            ActionText.Text = error.Message;
        }
        finally
        {
            readingPending = null;
            if (!closing && openTab is not null) RenderTab();
        }
    }

    /// <summary>After martlet-host added the role, checks <paramref name="host"/> (about a minute) until its gateway offers the
    /// reading route and RapidOCR answers ready. Returns null once it does, else what it saw last.</summary>
    private async Task<string?> WaitForReadingAsync(PairedHost host, CancellationToken token)
    {
        for (var attempt = 0; ; attempt++)
        {
            var check = await HostControl.CheckAsync(host.Pairing, HardwareStore, token);
            hostChecks[host.HostId] = check;
            var why = check.Text;
            if (check.Routes?.Any(r => r.RouteId == Audio2FaceHostConnection.OcrRouteId) == true && store is not null)
            {
                using var reader = new HostScreenTextReader(store.DataDirectory, host.HostId);
                try
                {
                    var state = await reader.StatusAsync(token);
                    if (state == "ready") return null;
                    why = $"its reader is {state}";
                }
                catch (ScreenReadException error) { why = error.Message; }
            }
            if (attempt >= 12) return why;
            await Task.Delay(TimeSpan.FromSeconds(5), token);
        }
    }

    // ---------- Read my screen now ----------

    /// <summary>Takes one picture of the whole screen (kept only in memory) and reads it with the saved choice, then shows what
    /// it read and how long it took. With the Reading role, that picture goes to that computer.</summary>
    private async Task ReadScreenNowAsync()
    {
        if (store is null || readingTesting || closing) return;
        using var reader = ScreenReader.For(store.DataDirectory);
        if (reader is null)
        {
            readingTestState = "Reading is off.";
            RenderTab();
            return;
        }
        readingTesting = true;
        readingTestText = null;
        RenderTab();
        var glancer = new ScreenGlancer();
        ScreenFrame? frame = null;
        try
        {
            var result = await Task.Run(() => glancer.Capture(ScreenScope.ActiveScreen));
            frame = result.Frame;
            if (frame?.CopyPixels() is not { } pixels)
            {
                readingTestState = result.Skip switch
                {
                    GlanceSkip.MartletInFront => "Martlet's own windows are never read, and they cover most of the screen. Make this " +
                        "window smaller or move it aside, then try again.",
                    GlanceSkip.Private => "A private window is in front, so Martlet won't read the screen.",
                    GlanceSkip.Blank => "The screen is black to Martlet (a locked screen, or protected video).",
                    _ => "Couldn't take a picture of the screen" + (result.Note is { } why ? $" ({why})." : ".")
                };
                return;
            }
            try
            {
                var read = await reader.ReadNowAsync(pixels, frame.Width, frame.Height, lifetime.Token);
                readingTestState = read.Describe();
                readingTestText = read.Text.Length > 0 ? read.Text : "(no text found)";
                ErrorLog.Info($"Reading: test read {read.Lines.Count} lines with {read.Engine} in {read.Took.TotalMilliseconds:0} ms.");
            }
            finally { Array.Clear(pixels); }
        }
        catch (ScreenReadException error) { readingTestState = error.Message; }
        catch (OperationCanceledException) { }
        catch (Exception error) when (error is InvalidOperationException or NotSupportedException or System.Runtime.InteropServices.ExternalException)
        {
            readingTestState = $"Couldn't read the screen ({error.Message}).";
        }
        finally
        {
            frame?.Clear();
            Task.Run(glancer.Release).Forget();
            readingTesting = false;
            if (!closing && openTab == CompanionTab.Reading) RenderTab();
        }
    }
}
