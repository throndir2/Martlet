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
/// Companion › Reading (docs/READING.md, an optional extra): where Martlet reads the text on your screen while it watches
/// (reading.json, this PC's own choice). In the standard order: Now (what it reads with, the newest read and Read my screen now,
/// which reads the whole screen once with the saved choice), then the main choice as an option picker: Off, Windows OCR on this
/// PC (the default and recommended: on the processor, nothing leaves the PC) or Martlet's <c>ocr</c> host role in Docker, set up
/// on this PC or another of your computers with the same martlet-host flow as every role and reached through its gateway. The
/// role runs one of three models (<see cref="OptionalExtras.ReadingModels"/>): PP-OCRv5 mobile on the processor (recommended),
/// PP-OCRv5 server on an NVIDIA graphics card, or RapidOCR on the processor. Each option's details hold where it stands and the
/// button that uses it. Automation IDs: <c>ReadingNow</c>, <c>ReadingLast</c>, <c>Picker-Reading-&lt;Off|ThisPc|Host&gt;</c>,
/// <c>ReadingWindowsState</c>, <c>ReadingUseThisPc</c>, <c>ReadingHost-&lt;host&gt;</c>, <c>ReadingModel-&lt;model&gt;</c>
/// (ppocrv5-mobile, ppocrv5-server or rapidocr-ppocrv4), <c>ReadingModelNote</c>, <c>ReadingHostState</c>, <c>ReadingSetUp</c>,
/// <c>ReadingSwitch</c>, <c>ReadingUseHost</c>, <c>ReadingTurnOff</c>, <c>ReadingTest</c>, <c>ReadingTestState</c> and
/// <c>ReadingTestText</c>.
/// </summary>
public partial class MainWindow
{
    internal const string ReadingThisPc = "this-pc";

    private const string RapidOcrTerms =
        "It builds a container from python:3.12 with hash-pinned RapidOCR 1.4.4 (Apache-2.0) and its PaddleOCR PP-OCRv4 models " +
        "(Apache-2.0, about 15 MB, inside the package), ONNX Runtime (MIT) and OpenCV (Apache-2.0).";

    private const string PpOcrV5Terms =
        "It builds a container from python:3.12 with hash-pinned RapidOCR 3.10.0 (Apache-2.0), ONNX Runtime 1.31.0 (MIT) and OpenCV " +
        "(Apache-2.0), and PaddleOCR's PP-OCRv5 mobile and server models (Apache-2.0, about 190 MB) from RapidOCR's ModelScope " +
        "release (or an identical Hugging Face copy), each SHA-256 checked.";

    private const string CudaTerms =
        " For the graphics card it also installs NVIDIA's CUDA 13 and cuDNN libraries from PyPI (NVIDIA's license), and it needs " +
        "an NVIDIA driver 580 or newer.";

    private const string ReadingDataTerms =
        " While Martlet watches your screen, this PC sends screenshots to that computer. They are read in memory there and are " +
        "not kept.";

    private string? readingHost;
    private string? readingModel;
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

        // The main choice: Off, Windows OCR on this PC or Martlet's Reading role. Each option's details hold its button.
        var options = OptionalExtras.ReadingChoices(saved.Place, readingWindows, ReadingShown(saved).Chosen.Model).Select(option => Enum.Parse<ReadingPlace>(option.Key) switch
        {
            ReadingPlace.ThisPc => option with { Details = () => ReadingThisPcPanel(saved) },
            ReadingPlace.Host => option with { Details = () => ReadingHostPanel(saved) },
            _ => option with
            {
                Details = () => [Note("Martlet still sees your screen while it watches, but it doesn't read the text on it separately.",
                    new Thickness(0, 0, 0, 0))],
                Action = saved.Place == ReadingPlace.Off ? null
                    : () => PageButton("Turn reading off", () => SaveReading(new ReadingSettings { Place = ReadingPlace.Off, ChosenAt = DateTimeOffset.Now },
                        "Reading is off."), primary: true, id: "ReadingTurnOff")
            }
        }).ToList();
        page.Children.Add(OptionPicker("Reading", "Where it reads", "This choice stays on this PC.", options));
    }
    private static string ReadingNow(ReadingSettings saved) => saved.On
        ? $"Martlet reads the text on your screen with {saved.Describe()} while it watches."
        : $"Off. {OptionalExtras.OffMeans(CompanionTab.Reading)}.";

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
        ForgetPicker("Reading");
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

    /// <summary>Windows OCR's details: whether Windows can read text here, and Read on this PC.</summary>
    private List<UIElement> ReadingThisPcPanel(ReadingSettings saved)
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
        return [state, Row(inUse ? null : PageButton("Read on this PC", () => SaveReading(new ReadingSettings { Place = ReadingPlace.ThisPc, ChosenAt = DateTimeOffset.Now },
            "Martlet now reads the text on your screen with Windows OCR on this PC."), primary: true, id: "ReadingUseThisPc"))];
    }

    // ---------- Martlet's Reading role ----------

    /// <summary>What the Reading role's details show: the computer (<see cref="ReadingThisPc"/> or a host ID) and that host, the
    /// other computers, the model the computer runs (its offer; null: not set up), the model to set up or switch to (the pill the
    /// owner picked, else the model it runs, else the recommended one, and never one that needs an NVIDIA card the computer
    /// doesn't have) and whether it has an NVIDIA card for containers (null: not known).</summary>
    private sealed record ReadingView(string Shown, PairedHost? Target, PairedHost[] Others, string? Offer,
        OptionalExtras.ReadingRoleModel Chosen, bool? Nvidia);

    private ReadingView ReadingShown(ReadingSettings saved)
    {
        var thisPc = ThisPcHost();
        // Hosts friends share with this PC read for this PC too, when their owner set Reading up there.
        var others = NetworkMap.Hosts(Inputs()).Where(h => h.HostId != thisPc?.HostId).Concat(sharedHosts).ToArray();
        var reads = others.FirstOrDefault(h => !h.Shared && Offers(h, HostRoles.Ocr));
        var shown = readingHost ?? (saved.Place == ReadingPlace.Host && saved.HostId is { } chosen
            ? thisPc?.HostId == chosen ? ReadingThisPc : chosen
            : thisPc is not null && Offers(thisPc, HostRoles.Ocr) ? ReadingThisPc : reads?.HostId ?? ReadingThisPc);
        var onThisPc = shown == ReadingThisPc;
        var target = onThisPc ? thisPc : FindHost(shown) ?? FindSharedHost(shown);
        var offer = target is null ? null : hostChecks.GetValueOrDefault(target.HostId)?.Offers?.GetValueOrDefault(HostRoles.Ocr);
        var nvidia = target is not null && HardwareStore?.Find(target.HostId) is { } report
            ? report.Gpus.Any(g => g.IsNvidia) && report.NvidiaContainers != "no"
            : onThisPc && !ReferenceEquals(machine, MachineInfo.Unknown) ? machine.Gpus.Any(g => g.IsNvidia) : (bool?)null;
        var model = OptionalExtras.ReadingModelOf(readingModel) ?? OptionalExtras.ReadingModelOf(offer) ?? OptionalExtras.ReadingModels[0];
        if (model.NeedsNvidia && nvidia == false) model = OptionalExtras.ReadingModels[0];
        return new(shown, target, others, offer, model, nvidia);
    }

    /// <summary>The Reading role's details: the computer pills (with other computers paired, or shared by a friend), the model
    /// pills, where it stands on the shown computer, Set up there (or Switch to the picked model), and Read there once it is ready.</summary>
    private List<UIElement> ReadingHostPanel(ReadingSettings saved)
    {
        var view = ReadingShown(saved);
        var (shown, target, chosen) = (view.Shown, view.Target, view.Chosen);
        var stack = new List<UIElement>
        {
            Note("Set up by Martlet in Docker like its other roles, on the computer and with the model you choose.", new Thickness(0, 0, 0, 4))
        };
        if (view.Others.Length > 0)
        {
            var pills = new WrapPanel { Margin = new Thickness(0, 2, 0, 2) };
            foreach (var (id, label) in new[] { (ReadingThisPc, "This PC") }.Concat(view.Others.Select(h => (h.HostId, h.Shared ? $"{h.HostId} (a friend's)" : h.HostId))))
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
        var where = onThisPc ? "this PC" : shown;

        // The model: PP-OCRv5 mobile (recommended), PP-OCRv5 server (an NVIDIA graphics card only) or RapidOCR.
        var models = new WrapPanel { Margin = new Thickness(0, 4, 0, 2) };
        foreach (var model in OptionalExtras.ReadingModels)
        {
            var label = model == OptionalExtras.ReadingModels[0] ? model.Name + " (recommended)" : model.Name;
            var pill = new RadioButton { Content = label, GroupName = "ReadingModel", IsChecked = model == chosen, IsEnabled = readingPending is null };
            pill.SetResourceReference(StyleProperty, "FilterPill");
            AutomationProperties.SetName(pill, label);
            AutomationProperties.SetAutomationId(pill, "ReadingModel-" + model.Model);
            if (model.NeedsNvidia && view.Nvidia == false)
            {
                var why = $"Needs an NVIDIA graphics card; {where} has none.";
                pill.IsEnabled = false;
                pill.ToolTip = why;
                ToolTipService.SetShowOnDisabled(pill, true);
                AutomationProperties.SetHelpText(pill, why);
            }
            pill.Checked += (_, _) =>
            {
                if (readingModel == model.Model) return;
                readingModel = model.Model;
                RenderTab();
            };
            models.Children.Add(pill);
        }
        stack.Add(models);
        var modelNote = Note(chosen.Model switch
        {
            "ppocrv5-mobile" => "PP-OCRv5 mobile runs on the processor, so it never takes the graphics card from the voice or thinking. " +
                $"It reads a 1920 x 1080 screenshot in about 2 s and a 4K one in about 4 s, and it read every small line on a 4K screen. Download: {chosen.Download}.",
            "ppocrv5-server" => "PP-OCRv5 server is the most accurate model. It runs on an NVIDIA graphics card (driver 580 or newer) " +
                $"beside the voice and thinking; on a processor it takes about a minute a screenshot. Download: {chosen.Download}.",
            _ => $"RapidOCR (PP-OCRv4) runs on the processor and is the smallest, but it misses more small text than PP-OCRv5. Download: {chosen.Download}."
        }, new Thickness(0, 0, 0, 0));
        AutomationProperties.SetAutomationId(modelNote, "ReadingModelNote");
        stack.Add(modelNote);

        var ready = target is not null && Offers(target, HostRoles.Ocr);
        var runs = OptionalExtras.ReadingModelOf(view.Offer)?.Name ?? (string.IsNullOrEmpty(view.Offer) ? "its reader" : view.Offer);
        var other = ready && !string.IsNullOrEmpty(view.Offer) && view.Offer != chosen.Model;
        var pending = readingPending == shown;
        var inUse = saved.Place == ReadingPlace.Host && target is not null && saved.HostId == target.HostId;
        var cannot = ready || onThisPc ? null
            : target is { Shared: true } ? $"A friend shares {shown} with this PC; only its owner can set Reading up there."
            : CannotHand(shown, HostRoles.Ocr, "reading");
        var state = pending && other ? $"Switching {where} to {chosen.Name}..."
            : ready ? $"Ready on {where} with {runs}." + (inUse ? " Martlet reads there." : "") +
                (other && target is { Shared: true } ? " Only its owner can change the model." : "")
            : pending ? $"Setting up {chosen.Name} on {where}..."
            : cannot ?? (readingFailure is { } failed ? $"Setup failed on {where}: {failed}" : $"Not set up on {where} yet.");
        var stateLine = Note(state, new Thickness(0, 4, 0, 0));
        if (cannot is not null || readingFailure is not null && !pending && !ready) stateLine.SetResourceReference(TextBlock.ForegroundProperty, "WarningBrush");
        AutomationProperties.SetAutomationId(stateLine, "ReadingHostState");
        stack.Add(stateLine);
        if (ready)
        {
            Button? change = null;
            if (other && target is { Shared: false })
            {
                change = PageButton(pending ? "Switching..." : $"Switch to {chosen.Name}",
                    () => SetUpReadingAsync(onThisPc ? null : target, chosen, changing: true).Forget(), primary: inUse, id: "ReadingSwitch");
                change.IsEnabled = readingPending is null;
            }
            var use = inUse ? null : PageButton($"Read on {where}", () => UseReadingHost(target!.HostId, where), primary: true, id: "ReadingUseHost");
            if (change is not null || use is not null) stack.Add(Row(use, change));
            return stack;
        }
        var setUp = PageButton(pending ? "Setting up..." : $"Set up {chosen.Name}", () => SetUpReadingAsync(onThisPc ? null : target, chosen).Forget(),
            primary: true, id: "ReadingSetUp");
        setUp.IsEnabled = !pending && cannot is null && readingPending is null;
        if (cannot is not null)
        {
            setUp.ToolTip = cannot;
            ToolTipService.SetShowOnDisabled(setUp, true);
            AutomationProperties.SetHelpText(setUp, cannot);
        }
        stack.Add(Row(setUp));
        return stack;
    }
    private void UseReadingHost(string hostId, string where) =>
        SaveReading(new ReadingSettings { Place = ReadingPlace.Host, HostId = hostId, ChosenAt = DateTimeOffset.Now },
            $"Martlet now reads the text on your screen on {where}.");

    /// <summary>Sets the Reading role up on <paramref name="host"/> (null: this PC) with <paramref name="model"/>, or switches it
    /// to that model (<paramref name="changing"/>), after one confirmation naming its downloads, licences and what goes there,
    /// through the same martlet-host add as every role, then has Martlet read there.</summary>
    private async Task SetUpReadingAsync(PairedHost? host, OptionalExtras.ReadingRoleModel model, bool changing = false)
    {
        if (store is null || setupService is null || closing || readingPending is not null) return;
        var where = host is null ? "this PC" : host.HostId;
        var thisPc = ThisPcHost();
        var verb = changing ? "Switch" : "Set up";
        if (!ConfirmationDialog.Confirm(this,
                (changing ? $"Switch Reading on {where} to {model.Name}?\n\n" : $"Set up Reading with {model.Name} on {where}?\n\n") +
                (host is null && thisPc is null
                    ? machine.DockerInstalled ? "Martlet first sets up its host service in Docker Desktop. "
                        : "Martlet first installs Docker Desktop (it asks for its own terms) and sets up its host service. "
                    : "") +
                $"It runs on {(model.NeedsNvidia ? "the NVIDIA graphics card" : "the processor")} and downloads {model.Download} of " +
                "packages and models; Martlet shows the progress.\n\n" +
                (model.Engine == "rapidocr" ? RapidOcrTerms : PpOcrV5Terms + (model.NeedsNvidia ? CudaTerms : "")) + ReadingDataTerms,
                $"{verb} Reading"))
            return;
        var answers = model.Answers();
        readingFailure = null;
        readingPending = host?.HostId ?? ReadingThisPc;
        RenderTab();
        try
        {
            if (host is not null)
            {
                var done = await RunHostActionAsync(host, changing ? HostAction.Change(HostRoles.Ocr) : HostRoles.Get(HostRoles.Ocr).Add, answers);
                if (closing) return;
                if (done is null) readingFailure = $"{(changing ? "Switching" : "Setting")} it {(changing ? "" : "up ")}on {host.HostId} stopped. Its run window has details.";
                else if (await WaitForReadingAsync(host, model.Model, lifetime.Token) is { } why)
                    readingFailure = $"{(changing ? "The switch" : "Setup")} finished, but Martlet can't see {model.Name} on {host.HostId} yet ({why}). Check it in a minute.";
                else UseReadingHost(host.HostId, host.HostId);
                return;
            }
            string? stopped = null;
            async Task<string> Continue(HostRunWindow run, PairedHost pc)
            {
                var target = pc.Target(Version);
                await HostLocal.EnsureDockerAsync(run, Martlet.Core.Installation.ContinueSetupKind.Docker);
                target = await HostLocal.EngineForChangeAsync(target, HostRoles.Ocr, run);
                var chosen = answers;
                if (model.NeedsNvidia)
                {
                    // Several graphics cards: the owner picks the one it reads on.
                    run.Status("Checking this PC's graphics cards for Reading...");
                    var inputs = await HostLocal.DescribeAsync(target, HostRoles.Ocr, run.Output, run.Token);
                    chosen = HostInputDialog.WithGpu(run, "this PC", "Reading", inputs, answers) ?? throw new OperationCanceledException();
                }
                run.Status(changing ? $"Switching Reading on this PC to {model.Name}..." : $"Installing Reading with {model.Name} on this PC...");
                var exit = await HostLocal.EngineAsync(target, ["add", HostRoles.Ocr], SetupProgress(run, "Reading", why => stopped ??= why),
                    run.Token, answers: chosen);
                if (exit != 0)
                    throw new InvalidOperationException($"Installing Reading stopped{(stopped is null ? $" (exit {exit})" : ": " + stopped)}. The output has details.");
                run.Status("Checking Reading on this PC...");
                if (await WaitForReadingAsync(pc, model.Model, run.Token) is { } why)
                    throw new InvalidOperationException(stopped = $"Setup finished, but Martlet can't see {model.Name} yet ({why}). Check this PC in a minute.");
                Dispatcher.Invoke(() => UseReadingHost(pc.HostId, "this PC"));
                return $"Reading is ready on this PC with {model.Name}.";
            }
            string? status;
            if (thisPc is not null)
                status = await HostRunWindow.RunAsync(this, $"{verb} Reading on this PC", run => Continue(run, thisPc)) ??
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

    /// <summary>After martlet-host added (or changed) the role, checks <paramref name="host"/> (about a minute) until its gateway
    /// offers the reading route with <paramref name="model"/> and its reader answers ready. Returns null once it does, else what it
    /// saw last; at once when the reader failed to load.</summary>
    private async Task<string?> WaitForReadingAsync(PairedHost host, string model, CancellationToken token)
    {
        for (var attempt = 0; ; attempt++)
        {
            var check = await HostControl.CheckAsync(host.Pairing, HardwareStore, token);
            hostChecks[host.HostId] = check;
            var why = check.Text;
            var offer = check.Offers?.GetValueOrDefault(HostRoles.Ocr);
            if (!string.IsNullOrEmpty(offer) && offer != model)
                why = $"it still reads with {OptionalExtras.ReadingModelOf(offer)?.Name ?? offer}";
            else if (check.Routes?.Any(r => r.RouteId == Audio2FaceHostConnection.OcrRouteId) == true && store is not null)
            {
                using var reader = new HostScreenTextReader(store.DataDirectory, host.HostId);
                try
                {
                    var state = await reader.StatusAsync(token);
                    if (state == "ready") return null;
                    if (state == "failed") return "its reader failed to load; the Reading role's log on that computer says why";
                    why = $"its reader is {state}";
                }
                catch (ScreenReadException error) { why = error.Message; }
            }
            if (attempt >= 12) return why;
            await Task.Delay(TimeSpan.FromSeconds(5), token);
        }
    }

    // ---------- Read my screen now ----------

    /// <summary>Takes one full-size picture of the whole screen (kept only in memory) and reads it with the saved choice, then
    /// shows what it read, how long it took and the picture's size. With the Reading role, that picture goes to that computer.</summary>
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
            var result = await Task.Run(() => glancer.CaptureText(ScreenScope.ActiveScreen));
            frame = result.Frame;
            if (frame?.TakePixels() is not { } pixels)
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
