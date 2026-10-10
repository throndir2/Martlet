using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Martlet.Avatar.Audio2Face.Remote;
using Martlet.Avatar.Hosting;
using Martlet.Core.Cluster;
using Martlet.Core.Contracts;
using Martlet.Core.Reading;

namespace Martlet.Desktop;

/// <summary>
/// Companion › Reading (docs/READING.md, an optional extra): the places that read the text on your screen while Martlet watches,
/// as one ordered list (<see cref="ReadingList"/>, pools-local.json, this PC's own). In the standard order: Now (what reads, the
/// newest read and Read my screen now, which reads the whole screen once with the list), then the list (the shared pool list,
/// <see cref="PoolListCard"/>): This PC reads with Windows OCR (the default and recommended: on the processor, nothing leaves
/// the PC) or with Martlet's <c>ocr</c> host role on this PC; a computer or one of its cards reads with its Reading role, set up
/// with the same martlet-host flow as every role and reached through its gateway. The role runs one of three models
/// (<see cref="OptionalExtras.ReadingModels"/>): PP-OCRv5 mobile on the processor (recommended), PP-OCRv5 server on an NVIDIA
/// graphics card, or RapidOCR on the processor. A member's Settings choose its engine (This PC) or show its model and Set up or
/// Switch there. With nothing on, reading is off. Then Compare them. Automation IDs: <c>ReadingNow</c>, <c>ReadingLast</c>, the
/// list's <c>Pool-reading-...</c>, <c>ReadingEngine-&lt;windows-ocr|ocr&gt;</c>, <c>ReadingWindowsState</c>,
/// <c>ReadingModel-&lt;place&gt;-&lt;model&gt;</c> (place: this-pc or the host; model: ppocrv5-mobile, ppocrv5-server or
/// rapidocr-ppocrv4), <c>ReadingModelNote-&lt;place&gt;</c>, <c>ReadingHostState-&lt;place&gt;</c>, <c>ReadingSetUp-&lt;place&gt;</c>,
/// <c>ReadingSwitch-&lt;place&gt;</c>, <c>PickerCompare-Reading</c>, <c>ReadingTest</c>, <c>ReadingTestState</c> and
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
        " While Martlet watches your screen and that computer is in your Reading list, this PC sends it screenshots. They are " +
        "read in memory there and are not kept.";

    /// <summary>The model each place's pills show (place: <see cref="ReadingThisPc"/> or a host ID), until it is set up there.</summary>
    private readonly Dictionary<string, string> readingModels = new(StringComparer.Ordinal);
    /// <summary>Why the last setup failed, by place.</summary>
    private readonly Dictionary<string, string> readingFailures = new(StringComparer.Ordinal);
    private string? readingPending;
    private string? readingTestState;
    private string? readingTestText;
    private bool readingTesting;
    private bool? readingWindows;
    private bool readingWindowsBusy;

    private void RenderReadingTab(Panel page)
    {
        if (store is not null) ReadingList.Ensure(store.DataDirectory);
        var list = ReadingList.Load(store?.DataDirectory);
        if (readingWindows is null && !readingWindowsBusy) CheckWindowsReadingAsync().Forget();

        var now = new TextBlock { Text = ReadingNow(list), FontSize = 15, TextWrapping = TextWrapping.Wrap };
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
        var readNow = list.NoneOn ? null : PageButton("Read my screen now", () => ReadScreenNowAsync().Forget(), id: "ReadingTest");
        if (readNow is not null) readNow.IsEnabled = !readingTesting;
        page.Children.Add(Card(Heading("Now"), now,
            HelpTip.Explain("While Martlet watches your screen, it reads the text on each changed screenshot, off to the side, so replies " +
                "never wait for it. New text (a score, \"Victory\", a new message) makes a look more likely, and each look gets the " +
                "text it read. What you type or say never waits for it. The text is never kept in the conversation.",
                new Thickness(0, 4, 0, 0), "Reading", "reading"), last, test, text, Row(readNow)));

        page.Children.Add(PoolListCard(new PoolListOptions
        {
            Area = PoolAreas.Reading,
            Heading = "Where it reads",
            Intro = "The places that read your screen, in order. The first free one reads; when it is busy or doesn't answer, the " +
                "next one does. This PC reads with Windows OCR or Martlet's Reading role, and your other computers with the Reading " +
                "role. Press Settings to choose how one reads. With nothing on, Martlet doesn't read your screen. This list stays on this PC.",
            Status = ReadingMemberStatus,
            Settings = ReadingMemberSettings,
            NewMember = ReadingNewMember
        }));

        // Compare them: Windows OCR beside the Reading role (with the model the first computer in the list runs).
        var first = list.Members.FirstOrDefault(m => !m.Off && !ReadingPool.UsesWindows(m));
        var firstHost = first is null ? null : ReadingPool.HostOf(first, ThisPcHost()?.HostId);
        var options = OptionalExtras.ReadingChoices(ReadingPool.Choice(list, ThisPcHost()?.HostId).Place, readingWindows,
            firstHost is null ? null : hostChecks.GetValueOrDefault(firstHost)?.Offers?.GetValueOrDefault(HostRoles.Ocr)).Where(o => !o.IsOff).ToList();
        var open = pickerCompare.Contains("Reading");
        var compare = PageButton(open ? "Hide the comparison" : "Compare Windows OCR and the Reading role", () =>
        {
            if (!pickerCompare.Remove("Reading")) pickerCompare.Add("Reading");
            RenderTab();
        }, link: true, id: "PickerCompare-Reading");
        page.Children.Add(open ? Card(Heading("Compare them"), Row(compare), PickerTable("Reading", options)) : Card(Heading("Compare them"), Row(compare)));
    }

    private static string ReadingNow(PoolList list)
    {
        var on = list.Members.Where(m => !m.Off).ToList();
        if (on.Count == 0) return $"Off. {OptionalExtras.OffMeans(CompanionTab.Reading)}.";
        return $"Martlet reads the text on your screen with {ReadingPool.Describe(on[0])} while it watches" +
            (on.Count > 1 ? $"; when that one is busy, the next of the {on.Count} in the list reads." : ".");
    }

    private async Task CheckWindowsReadingAsync()
    {
        readingWindowsBusy = true;
        try { readingWindows = await Task.Run(() => WindowsScreenTextReader.Available); }
        finally { readingWindowsBusy = false; }
        if (!closing && openTab == CompanionTab.Reading && !tabEdited) RenderTab();
    }

    // ---------- the list's members ----------

    /// <summary>Where a member reads with the Reading role: its place (<see cref="ReadingThisPc"/> or the host ID), the paired
    /// computer (null: this PC has no host service yet, or the computer isn't paired here) and its name in words.</summary>
    private (string Place, PairedHost? Host, string Where) ReadingRoleOf(PoolMember member) => member.Kind == PoolMemberKind.ThisPc
        ? (ReadingThisPc, ThisPcHost(), "this PC")
        : (member.HostId!, FindHost(member.HostId!) ?? FindSharedHost(member.HostId), member.HostId!);

    /// <summary>A member's state line: Windows OCR, or the model its computer's Reading role runs and whether that is the model
    /// its settings name.</summary>
    private string? ReadingMemberStatus(PoolMember member)
    {
        if (ReadingPool.UsesWindows(member))
            return readingWindows switch
            {
                true => "reads with Windows OCR on this PC's processor; nothing leaves this PC",
                false => "Windows OCR, but Windows has no text recognition language here, so the next one reads",
                _ => "reads with Windows OCR; checking it"
            };
        var (place, host, where) = ReadingRoleOf(member);
        var parts = new List<string>();
        if (member.Kind == PoolMemberKind.ThisPc) parts.Add("reads with Martlet's Reading role on this PC");
        if (readingPending == place) parts.Add("setting up...");
        else if (host is null) parts.Add(member.Kind == PoolMemberKind.ThisPc ? "not set up yet: press Settings" : $"{where} isn't paired with this PC");
        else
        {
            var check = hostChecks.GetValueOrDefault(host.HostId);
            if (check?.Offers?.TryGetValue(HostRoles.Ocr, out var offer) == true)
            {
                parts.Add("runs " + (OptionalExtras.ReadingModelOf(offer)?.Name ?? (string.IsNullOrEmpty(offer) ? "its reader" : offer)));
                if (OptionalExtras.ReadingModelOf(member.Setting(PoolSettingKeys.Model)) is { } wanted && wanted.Model != offer)
                    parts.Add($"its setting is {wanted.Name}: Switch in Settings");
            }
            else parts.Add(check?.Reachable == false ? "not reachable right now"
                : check?.Reachable == true ? "doesn't run Reading yet: Set up in Settings" : "not checked yet");
            if (readingFailures.GetValueOrDefault(place) is { } failed) parts.Add("setup failed: " + failed);
        }
        if (member.Kind == PoolMemberKind.Gpu) parts.Add("its computer picks the card");
        return string.Join("; ", parts);
    }

    /// <summary>A member's settings: This PC's engine (Windows OCR or the Reading role), and for the Reading role its model, where
    /// it stands and Set up or Switch there.</summary>
    private UIElement? ReadingMemberSettings(PoolMember member)
    {
        var stack = new StackPanel();
        if (member.Kind == PoolMemberKind.ThisPc)
        {
            var engines = new WrapPanel { Margin = new Thickness(0, 0, 0, 2) };
            foreach (var (engine, label) in new[] { (ReadingPool.WindowsOcr, "Windows OCR (recommended)"), (ReadingPool.Role, "Martlet's Reading role") })
            {
                var pill = new RadioButton { Content = label, GroupName = "ReadingEngine", IsChecked = ReadingPool.UsesWindows(member) == (engine == ReadingPool.WindowsOcr) };
                pill.SetResourceReference(StyleProperty, "FilterPill");
                AutomationProperties.SetName(pill, "This PC reads with " + label);
                AutomationProperties.SetAutomationId(pill, "ReadingEngine-" + engine);
                pill.Checked += (_, _) => SaveReadingMember(member.WithSetting(PoolSettingKeys.Engine, engine),
                    engine == ReadingPool.WindowsOcr ? "This PC now reads with Windows OCR." : "This PC now reads with Martlet's Reading role.");
                engines.Children.Add(pill);
            }
            stack.Children.Add(engines);
            if (ReadingPool.UsesWindows(member))
            {
                var state = Note(readingWindows switch
                {
                    true => "Windows can read text on this PC. It reads a screenshot in a fraction of a second, and nothing leaves this PC.",
                    false => WindowsScreenTextReader.NoLanguage,
                    _ => "Checking Windows text recognition…"
                }, new Thickness(0, 4, 0, 0));
                if (readingWindows == false) state.SetResourceReference(TextBlock.ForegroundProperty, "WarningBrush");
                AutomationProperties.SetAutomationId(state, "ReadingWindowsState");
                stack.Children.Add(state);
                return stack;
            }
        }
        foreach (var element in ReadingRolePanel(member)) stack.Children.Add(element);
        return stack;
    }

    /// <summary>The Reading role's settings of a member: the model pills (choosing one only shows it), the model's note, where
    /// it stands on that computer, and Set up there (or Switch to the picked model).</summary>
    private List<UIElement> ReadingRolePanel(PoolMember member)
    {
        var (place, target, where) = ReadingRoleOf(member);
        var onThisPc = member.Kind == PoolMemberKind.ThisPc;
        var offer = target is null ? null : hostChecks.GetValueOrDefault(target.HostId)?.Offers?.GetValueOrDefault(HostRoles.Ocr);
        var nvidia = target is not null && HardwareStore?.Find(target.HostId) is { } report
            ? report.Gpus.Any(g => g.IsNvidia) && report.NvidiaContainers != "no"
            : onThisPc && !ReferenceEquals(machine, MachineInfo.Unknown) ? machine.Gpus.Any(g => g.IsNvidia) : (bool?)null;
        var chosen = OptionalExtras.ReadingModelOf(readingModels.GetValueOrDefault(place)) ?? OptionalExtras.ReadingModelOf(member.Setting(PoolSettingKeys.Model)) ??
            OptionalExtras.ReadingModelOf(offer) ?? OptionalExtras.ReadingModels[0];
        if (chosen.NeedsNvidia && nvidia == false) chosen = OptionalExtras.ReadingModels[0];
        var stack = new List<UIElement>();

        // The model: PP-OCRv5 mobile (recommended), PP-OCRv5 server (an NVIDIA graphics card only) or RapidOCR.
        var models = new WrapPanel { Margin = new Thickness(0, 4, 0, 2) };
        foreach (var model in OptionalExtras.ReadingModels)
        {
            var label = model == OptionalExtras.ReadingModels[0] ? model.Name + " (recommended)" : model.Name;
            var pill = new RadioButton { Content = label, GroupName = "ReadingModel-" + place, IsChecked = model == chosen, IsEnabled = readingPending is null };
            pill.SetResourceReference(StyleProperty, "FilterPill");
            AutomationProperties.SetName(pill, $"{label} on {where}");
            AutomationProperties.SetAutomationId(pill, $"ReadingModel-{place}-{model.Model}");
            if (model.NeedsNvidia && nvidia == false)
            {
                var why = $"Needs an NVIDIA graphics card; {where} has none.";
                pill.IsEnabled = false;
                pill.ToolTip = why;
                ToolTipService.SetShowOnDisabled(pill, true);
                AutomationProperties.SetHelpText(pill, why);
            }
            pill.Checked += (_, _) =>
            {
                if (readingModels.GetValueOrDefault(place) == model.Model) return;
                readingModels[place] = model.Model;
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
        AutomationProperties.SetAutomationId(modelNote, "ReadingModelNote-" + place);
        stack.Add(modelNote);

        var ready = target is not null && Offers(target, HostRoles.Ocr);
        var runs = OptionalExtras.ReadingModelOf(offer)?.Name ?? (string.IsNullOrEmpty(offer) ? "its reader" : offer);
        var other = ready && !string.IsNullOrEmpty(offer) && offer != chosen.Model;
        var pending = readingPending == place;
        var cannot = ready || onThisPc ? null
            : target is null ? $"{where} isn't paired with this PC."
            : target.Shared ? $"A friend shares {where} with this PC; only its owner can set Reading up there."
            : CannotHand(where, HostRoles.Ocr, "reading");
        var failure = readingFailures.GetValueOrDefault(place);
        var state = pending && other ? $"Switching {where} to {chosen.Name}..."
            : ready ? $"Ready on {where} with {runs}." + (other && target is { Shared: true } ? " Only its owner can change the model." : "")
            : pending ? $"Setting up {chosen.Name} on {where}..."
            : cannot ?? (failure is not null ? $"Setup failed on {where}: {failure}" : $"Not set up on {where} yet.");
        var stateLine = Note(state, new Thickness(0, 4, 0, 0));
        if (cannot is not null || failure is not null && !pending && !ready) stateLine.SetResourceReference(TextBlock.ForegroundProperty, "WarningBrush");
        AutomationProperties.SetAutomationId(stateLine, "ReadingHostState-" + place);
        stack.Add(stateLine);
        if (ready)
        {
            if (other && target is { Shared: false })
            {
                var change = PageButton(pending ? "Switching..." : $"Switch to {chosen.Name}",
                    () => SetUpReadingAsync(onThisPc ? null : target, chosen, changing: true).Forget(), primary: true, id: "ReadingSwitch-" + place);
                change.IsEnabled = readingPending is null;
                stack.Add(Row(change));
            }
            return stack;
        }
        var setUp = PageButton(pending ? "Setting up..." : $"Set up {chosen.Name}", () => SetUpReadingAsync(onThisPc ? null : target, chosen).Forget(),
            primary: true, id: "ReadingSetUp-" + place);
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

    /// <summary>The settings a member gets when it is added: This PC reads with Windows OCR (or with the Reading role when Windows
    /// can't read here and this PC runs it); a computer keeps the model its Reading role runs.</summary>
    private PoolMember ReadingNewMember(PoolMember member)
    {
        if (member.Kind == PoolMemberKind.ThisPc)
            return readingWindows == false && ThisPcHost() is { } own && Offers(own, HostRoles.Ocr) ? ReadingPool.ThisPcRole() : ReadingPool.Windows();
        var offer = member.HostId is { } id ? hostChecks.GetValueOrDefault(id)?.Offers?.GetValueOrDefault(HostRoles.Ocr) : null;
        return string.IsNullOrEmpty(offer) ? member : member.WithSetting(PoolSettingKeys.Model, offer);
    }

    /// <summary>Saves <paramref name="next"/> in the Reading list (by its key); watching follows it at its next screenshot.</summary>
    private bool SaveReadingList(Func<PoolList, PoolList> change, string done)
    {
        if (store is null) return false;
        ReadingList.Ensure(store.DataDirectory);
        if (!PoolSettings.SaveFor(store.DataDirectory, PoolAreas.Reading, change(ReadingList.Load(store.DataDirectory))))
        {
            ActionText.Text = "Couldn't save the Reading list on this PC.";
            return false;
        }
        WorkSharingRoster.Forget();
        ErrorLog.Info("Pools: " + done);
        ActionText.Text = done;
        // Rebuilt after the click's own event finishes, so the control that changed isn't replaced under it.
        Dispatcher.InvokeAsync(() =>
        {
            if (!closing && openTab == CompanionTab.Reading) RenderTab();
        });
        return true;
    }

    private void SaveReadingMember(PoolMember next, string done) => SaveReadingList(list => list.With(next), done);

    /// <summary>Puts <paramref name="hostId"/> first in the Reading list (added when missing, on again when off), as Devices'
    /// Use for reading on a host a friend shares does.</summary>
    private void UseReadingHost(string hostId, string where)
    {
        var offer = hostChecks.GetValueOrDefault(hostId)?.Offers?.GetValueOrDefault(HostRoles.Ocr);
        SaveReadingList(list =>
        {
            var member = (list.Find(PoolMember.Computer(hostId).Key) ?? PoolMember.Computer(hostId)) with { Off = false };
            if (!string.IsNullOrEmpty(offer)) member = member.WithSetting(PoolSettingKeys.Model, offer);
            return list.With(member).Move(member.Key, -PoolSettings.MaximumMembers);
        }, $"Martlet now reads the text on your screen on {where} first.");
    }

    /// <summary>Whether this PC's Reading list reads on <paramref name="hostId"/> (a member that is on names it).</summary>
    private bool ReadsOn(string hostId) => store is not null &&
        ReadingList.Load(store.DataDirectory).Members.Any(m => !m.Off && m.OnHost && m.HostId == hostId);

    /// <summary>Takes <paramref name="hostId"/> out of the Reading list (a host forgotten). When nothing else was on, This PC reads
    /// with Windows OCR, as before.</summary>
    private void ForgetReadingHost(string hostId)
    {
        if (store is null || !ReadingList.Load(store.DataDirectory).Members.Any(m => m.OnHost && m.HostId == hostId)) return;
        SaveReadingList(list =>
        {
            var next = list with { Members = [.. list.Members.Where(m => !(m.OnHost && m.HostId == hostId))] };
            return next.NoneOn && !list.NoneOn ? next.With(ReadingPool.Windows()) : next;
        }, $"{hostId} is no longer in the Reading list.");
    }

    /// <summary>After the Reading role was set up (or switched) on <paramref name="hostId"/> (null: this PC), the members that read
    /// there keep its model (This PC reads with the role then).</summary>
    private void ReadingSetUpDone(string? hostId, OptionalExtras.ReadingRoleModel model, string where)
    {
        var own = ThisPcHost()?.HostId;
        readingModels.Remove(hostId ?? ReadingThisPc);
        SaveReadingList(list =>
        {
            var members = list.Members.Select(m => hostId is null ? m.Kind == PoolMemberKind.ThisPc
                    ? m.WithSetting(PoolSettingKeys.Engine, ReadingPool.Role).WithSetting(PoolSettingKeys.Model, model.Model) : m
                : ReadingPool.HostOf(m, own) == hostId ? m.WithSetting(PoolSettingKeys.Model, model.Model) : m).ToList();
            var next = list with { Members = members };
            // Set up from Devices or another page: the place joins the list at its end.
            if (members.All(m => hostId is null ? m.Kind != PoolMemberKind.ThisPc : ReadingPool.HostOf(m, own) != hostId))
                next = next.With(hostId is null ? ReadingPool.ThisPcRole().WithSetting(PoolSettingKeys.Model, model.Model)
                    : PoolMember.Computer(hostId).WithSetting(PoolSettingKeys.Model, model.Model));
            return next;
        }, $"Reading is ready on {where} with {model.Name}.");
    }

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
        readingFailures.Remove(host?.HostId ?? ReadingThisPc);
        readingPending = host?.HostId ?? ReadingThisPc;
        RenderTab();
        try
        {
            if (host is not null)
            {
                var done = await RunHostActionAsync(host, changing ? HostAction.Change(HostRoles.Ocr) : HostRoles.Get(HostRoles.Ocr).Add, answers);
                if (closing) return;
                if (done is null) readingFailures[host.HostId] = $"{(changing ? "Switching" : "Setting")} it {(changing ? "" : "up ")}on {host.HostId} stopped. Its run window has details.";
                else if (await WaitForReadingAsync(host, model.Model, lifetime.Token) is { } why)
                    readingFailures[host.HostId] = $"{(changing ? "The switch" : "Setup")} finished, but Martlet can't see {model.Name} on {host.HostId} yet ({why}). Check it in a minute.";
                else ReadingSetUpDone(host.HostId, model, host.HostId);
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
                Dispatcher.Invoke(() => ReadingSetUpDone(null, model, "this PC"));
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
                if (!status.StartsWith("Reading is ready", StringComparison.Ordinal)) readingFailures[ReadingThisPc] = status;
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException or ContractException or
            JsonException or ArgumentException or Audio2FaceHostException || ClusterSync.IsHostFailure(error))
        {
            readingFailures[host?.HostId ?? ReadingThisPc] = error.Message;
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
                try
                {
                    var state = await PoolScreenTextReader.StatusAsync(host, token);
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
