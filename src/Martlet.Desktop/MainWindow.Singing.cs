using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Martlet.Avatar.Audio2Face.Remote;
using Martlet.Avatar.Hosting;
using Martlet.Core.Contracts;
using Martlet.Core.Singing;

namespace Martlet.Desktop;

/// <summary>
/// Companion › Voice › Singing: Martlet writes a song from lyrics and a style and sings it with the voice it speaks with,
/// on the <c>singing</c> host role (ACE-Step 1.5 writes the music, SoulX-Singer-SVC or VevoSing matches the singing to the
/// voice). Like a Voice engine row: chips, one button that sets the role up on the shown computer (this PC, or one picked
/// under Another of your computers) after a confirmation naming the downloads, licences and terms, and where it stands
/// there (with the voice matches set up there, read from its singing service through the gateway), and its choices
/// (<see cref="SingingPreferences"/>). A plain Set up installs SoulX-Singer only; choosing VevoSing where it isn't set up
/// offers Add VevoSing there, with its own confirmation. Songs are only ever performed by Martlet in conversation (through
/// <see cref="SongMaker"/>), never played from a button here. Automation IDs: <c>SingingHost-&lt;host&gt;</c>
/// ("SingingHost-this-pc"), <c>SingingEngine</c>, <c>SingingFeatures</c>, <c>SingingState</c>, <c>SingingSetUp</c>,
/// <c>SingingGpu</c>, <c>SingingSetUpVevo</c>, <c>SingingQuality</c>, <c>SingingVoiceMatch</c> and
/// <c>SingingVoiceMatchState</c>.
/// </summary>
public partial class MainWindow
{
    internal const string SingingThisPc = "this-pc";


    internal static readonly IReadOnlyList<string> SingingFeatures =
        ["NVIDIA GPU 6 GB+, shared", "Docker", "Sings in your cloned voice", "With backing music", "A few minutes per song",
            "ACE-Step MIT · SoulX-Singer Apache-2.0"];

    /// <summary>Whether Singing needs a graphics card of its own, in words (the GPU chip's tip and the card's note).</summary>
    internal const string SingingGpuNote =
        "It doesn't need a graphics card of its own. It shares one with the voice and listening, using 5-7 GB of it only while a " +
        "song is being made (one stage at a time) and freeing it when idle. While a song is made it competes with them and with " +
        "a local Thinking model (replies stay prompt, a song gets a little slower), so a second computer's card is best when " +
        "this one is busy.";

    private string? singingHost;
    private string? singingPendingHost;
    private bool singingPendingVevo;
    private string? singingFailure;
    private string? singingVevoFailure;
    /// <summary>What each computer's singing service last reported through its gateway (the voice matches set up there), by
    /// host ID; null when it couldn't be read. Read when the card shows a computer that sings, and again after a setup.</summary>
    private readonly Dictionary<string, SongClient.SingingService?> singingServices = new(StringComparer.Ordinal);
    private readonly HashSet<string> singingReads = new(StringComparer.Ordinal);


    /// <summary>The song maker Martlet's conversation uses: <see cref="SongClient"/> on the paired host with the singing role, or
    /// the FIXTURE - NOT AI maker when MARTLET_SINGING_FIXTURE is 1; null before Martlet has its data directory.</summary>
    internal ISongMaker? SongMaker => store is null ? null : SongClient.For(store.DataDirectory);

    private Border SingingCard()
    {
        var stack = new List<UIElement>
        {
            Heading("Singing"),
            Note("Martlet writes a song from lyrics and a style and sings it with the voice it speaks with, with backing music. " +
                "It runs on an NVIDIA graphics card on this PC or another of your computers and frees it again when idle.",
                new Thickness(0, 0, 0, 4))
        };
        var thisPc = ThisPcHost();
        var others = NetworkMap.Hosts(Inputs()).Where(h => h.HostId != thisPc?.HostId).ToArray();
        var sings = others.FirstOrDefault(h => hostChecks.GetValueOrDefault(h.HostId)?.Offers?.ContainsKey(HostRoles.Singing) == true);
        var shown = singingHost ?? (thisPc is not null && Offers(thisPc, HostRoles.Singing) ? SingingThisPc : sings?.HostId ?? SingingThisPc);
        if (others.Length > 0)
        {
            var pills = new WrapPanel { Margin = new Thickness(0, 2, 0, 2) };
            foreach (var (id, label) in new[] { (SingingThisPc, "This PC") }.Concat(others.Select(h => (h.HostId, h.HostId))))
            {
                var pill = new RadioButton { Content = label, GroupName = "SingingHost", IsChecked = id == shown };
                pill.SetResourceReference(StyleProperty, "FilterPill");
                AutomationProperties.SetName(pill, label);
                AutomationProperties.SetAutomationId(pill, "SingingHost-" + id);
                pill.Checked += (_, _) =>
                {
                    if (singingHost == id) return;
                    singingHost = id;
                    RenderTab();
                };
                pills.Children.Add(pill);
            }
            stack.Add(pills);
        }
        var target = shown == SingingThisPc ? thisPc : FindHost(shown);
        var onThisPc = shown == SingingThisPc;
        var where = onThisPc ? "this PC" : shown;
        var ready = target is not null && Offers(target, HostRoles.Singing);
        var pending = singingPendingHost == shown;
        var addingVevo = pending && singingPendingVevo;
        // The voice matches set up there, from its singing service (read once the card shows a computer that sings).
        SongClient.SingingService? service = null;
        var serviceKnown = ready && singingServices.TryGetValue(target!.HostId, out service);
        if (ready && !serviceKnown) ReadSingingServiceAsync(target!).Forget();
        var vevo = service?.Has(SongVoiceMatch.VevoSing) == true;
        var cannot = ready ? null : onThisPc ? ThisPcCannotSing() : CannotHand(shown, HostRoles.Singing, "singing");
        var state = addingVevo ? $"Adding VevoSing on {where}..."
            : ready ? $"Ready on {where}" + (service is null ? "." : vevo ? " with SoulX-Singer and VevoSing." : " with SoulX-Singer.")
            : pending ? $"Setting up on {where}..."
            : cannot ?? (singingFailure is { } failed ? $"Setup failed on {where}: {failed}" : $"Not set up on {where} yet.");
        if (ready && !addingVevo && singingVevoFailure is { } vevoFailed) state += $" Adding VevoSing failed: {vevoFailed}";
        var setUp = PageButton(ready ? "Ready" : pending ? "Setting up..." : "Set up", () => SetUpSingingAsync(onThisPc ? null : target, vevosing: false).Forget(),
            primary: !ready, id: "SingingSetUp");
        setUp.IsEnabled = !ready && !pending && cannot is null;
        if (cannot is not null)
        {
            setUp.ToolTip = cannot;
            ToolTipService.SetShowOnDisabled(setUp, true);
            AutomationProperties.SetHelpText(setUp, cannot);
        }
        var title = OptionTitle("Singing", ready ? "ready" : null, 15);
        AutomationProperties.SetName(title, "Singing" + (ready ? " · ready" : ""));
        AutomationProperties.SetAutomationId(title, "SingingEngine");
        var text = new StackPanel();
        text.Children.Add(title);
        text.Children.Add(Note("ACE-Step 1.5 writes the music; SoulX-Singer matches the singing to your voice from a short recording.",
            new Thickness(0, 2, 0, 0)));
        var chips = Chips("singing", SingingFeatures, feature => feature switch
        {
            "NVIDIA GPU 6 GB+, shared" => SingingGpuNote,
            "Sings in your cloned voice" => "No training: the voice is matched from its recording in your voice library.",
            "ACE-Step MIT · SoulX-Singer Apache-2.0" => "VevoSing, an optional second voice match, is CC-BY-NC-ND-4.0 (personal, non-commercial use only).",
            _ => null
        });
        AutomationProperties.SetAutomationId(chips, "SingingFeatures");
        text.Children.Add(chips);
        var stateLine = Note(state, new Thickness(0, 4, 0, 0));
        if (cannot is not null || (ready ? singingVevoFailure : singingFailure) is not null && !pending)
            stateLine.SetResourceReference(TextBlock.ForegroundProperty, "WarningBrush");
        AutomationProperties.SetAutomationId(stateLine, "SingingState");
        text.Children.Add(stateLine);
        setUp.VerticalAlignment = VerticalAlignment.Top;
        setUp.Margin = new Thickness(12, 0, 0, 0);
        var row = new DockPanel();
        DockPanel.SetDock(setUp, Dock.Right);
        row.Children.Add(setUp);
        row.Children.Add(text);
        var option = new Border { Child = row, BorderThickness = new Thickness(ready ? 2 : 1), CornerRadius = new CornerRadius(12),
            Padding = new Thickness(14, 12, 14, 12), Margin = new Thickness(0, 8, 0, 0) };
        option.SetResourceReference(Border.BorderBrushProperty, ready ? "AccentBrush" : "BorderBrush");
        stack.Add(option);
        var gpu = Note(SingingGpuNote, new Thickness(0, 6, 0, 0));
        AutomationProperties.SetAutomationId(gpu, "SingingGpu");
        stack.Add(gpu);

        // Choices, kept on this PC (singing.json) and read when a song starts.
        var preferences = store is null ? new SingingPreferences() : SingingPreferences.Load(store.DataDirectory);
        var quality = new ComboBox { MinHeight = 30, MinWidth = 300, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 8, 0, 0) };
        quality.Items.Add(new ComboBoxItem { Content = "Fast (recommended)", Tag = SongQuality.Fast });
        quality.Items.Add(new ComboBoxItem { Content = "High quality (clearer words, a few seconds more)", Tag = SongQuality.HighQuality });
        quality.SelectedIndex = preferences.Quality == SongQuality.HighQuality ? 1 : 0;
        AutomationProperties.SetName(quality, "Quality");
        AutomationProperties.SetAutomationId(quality, "SingingQuality");
        var match = new ComboBox { MinHeight = 30, MinWidth = 300, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 8, 0, 0) };
        match.Items.Add(new ComboBoxItem { Content = "SoulX-Singer (recommended: keeps the tune and words)", Tag = SongVoiceMatch.SoulX });
        match.Items.Add(new ComboBoxItem { Content = "VevoSing (closer to the voice; personal, non-commercial use only)", Tag = SongVoiceMatch.VevoSing });
        match.SelectedIndex = preferences.VoiceMatch == SongVoiceMatch.VevoSing ? 1 : 0;
        AutomationProperties.SetName(match, "Voice match");
        AutomationProperties.SetAutomationId(match, "SingingVoiceMatch");
        void SaveChoices()
        {
            if (store is null) return;
            try
            {
                (SingingPreferences.Load(store.DataDirectory) with
                {
                    Quality = quality.SelectedIndex == 1 ? SongQuality.HighQuality : SongQuality.Fast,
                    VoiceMatch = match.SelectedIndex == 1 ? SongVoiceMatch.VevoSing : SongVoiceMatch.SoulX
                }).Save(store.DataDirectory);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                ActionText.Text = $"Couldn't save the singing choices: {error.Message}";
            }
        }
        quality.SelectionChanged += (_, _) => SaveChoices();
        match.SelectionChanged += (_, _) =>
        {
            SaveChoices();
            // Choosing VevoSing where it isn't set up offers Add VevoSing there at once.
            Dispatcher.BeginInvoke(() => { if (!closing && openTab == CompanionTab.Voice) RenderTab(); });
        };
        stack.Add(Labeled("Quality", quality));
        stack.Add(Labeled("Voice match", match));
        if (match.SelectedIndex == 1 && ready && (serviceKnown && !vevo || addingVevo))
        {
            var missing = Note(addingVevo ? $"Adding VevoSing on {where}. Songs use SoulX-Singer until it's ready."
                : $"VevoSing isn't set up on {where}. Songs use SoulX-Singer until you add it.", new Thickness(0, 6, 0, 0));
            AutomationProperties.SetAutomationId(missing, "SingingVoiceMatchState");
            stack.Add(missing);
            var add = PageButton(addingVevo ? "Adding VevoSing..." : "Add VevoSing there",
                () => SetUpSingingAsync(onThisPc ? null : target, vevosing: true).Forget(), link: true, id: "SingingSetUpVevo");
            add.IsEnabled = !pending;
            stack.Add(Row(add));
        }

        return Card([.. stack]);
    }

    private bool Offers(PairedHost host, string kind) => hostChecks.GetValueOrDefault(host.HostId)?.Offers?.ContainsKey(kind) == true;

    /// <summary>Reads <paramref name="host"/>'s singing service through its gateway (the voice matches set up there) for the
    /// card, then shows the card again.</summary>
    private async Task ReadSingingServiceAsync(PairedHost host)
    {
        if (!singingReads.Add(host.HostId)) return;
        SongClient.SingingService? read = null;
        try { read = await SongClient.ReadServiceAsync(host.Pairing, lifetime.Token); }
        catch (OperationCanceledException) { return; }
        catch (Exception error) when (ClusterSync.IsHostFailure(error) || error is JsonException)
        {
            ErrorLog.Info($"Singing: couldn't read the singing service on {host.HostId} ({error.Message}).");
        }
        finally { singingReads.Remove(host.HostId); }
        singingServices[host.HostId] = read;
        if (!closing && openTab == CompanionTab.Voice && !CompanionContent.IsKeyboardFocusWithin) RenderTab();
    }

    /// <summary>Remembers in singing.json which computer runs Singing after a check of the paired computers (kept while that
    /// computer can't be reached, cleared once it answers without Singing), for <see cref="SongClient.IsSetUp"/>.</summary>
    private void NoteSingingHost()
    {
        if (store is null) return;
        try
        {
            var saved = SingingPreferences.Load(store.DataDirectory);
            var running = hostChecks.Where(c => c.Value.Reachable == true && c.Value.Offers?.ContainsKey(HostRoles.Singing) == true)
                .Select(c => c.Key).ToList();
            var next = saved.Host is { } host && (running.Contains(host) ||
                hostChecks.GetValueOrDefault(host)?.Reachable != true && running.Count == 0)
                ? host
                : running.FirstOrDefault();
            if (next != saved.Host) (saved with { Host = next }).Save(store.DataDirectory);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            ErrorLog.Warn($"Couldn't remember the singing computer: {error.Message}");
        }
    }

    /// <summary>Why this PC can't sing: no NVIDIA graphics card, or one with less than 6 GB; null when it can or Martlet
    /// hasn't read this PC's hardware yet.</summary>
    private string? ThisPcCannotSing()
    {
        if (ReferenceEquals(machine, MachineInfo.Unknown)) return null;
        if (machine.ArmRefusal("singing") is { } arm) return arm;
        var nvidia = machine.Gpus.Where(g => g.IsNvidia).OrderByDescending(g => g.MemoryGb ?? 0).FirstOrDefault();
        if (nvidia is null)
            return $"Needs an NVIDIA graphics card; this PC has {(machine.Gpus.Count == 0 ? "none" : string.Join(", ", machine.Gpus.Select(g => g.Describe())))}.";
        return nvidia.MemoryGb is { } gb && gb < 5.75 ? $"Needs an NVIDIA graphics card with 6 GB+; this PC has {nvidia.Describe()}." : null;
    }


    /// <summary>What setting Singing up downloads and agrees to, for its confirmation (role.conf's terms in short).</summary>
    internal static string SingingTerms(bool vevosing) =>
        "It builds a container with the pinned ACE-Step 1.5 (MIT), SoulX-Singer (Apache-2.0), Demucs (MIT) and PyTorch, then downloads " +
        "about 16 GB of pinned models: ACE-Step 1.5 turbo and SFT with its planner and text encoder (MIT), SoulX-Singer-SVC and RMVPE " +
        "(Apache-2.0), OpenAI Whisper base (Apache-2.0) and the Demucs vocals model (MIT)." +
        (vevosing ? " " + VevoSingTerms : "") +
        " Songs are sung in a voice from your voice library, cloned from its recording, which goes to that computer: only use voices " +
        "that are yours or that you have permission to use, and never to imitate someone without permission or to deceive. ACE-Step " +
        "asks you to check that songs are original and to say they are AI-generated.";

    /// <summary>What adding VevoSing downloads and agrees to (role.conf's VevoSing terms in short).</summary>
    internal const string VevoSingTerms =
        "VevoSing downloads about 4.5 GB more: Amphion's Vevo1.5 models (huggingface.co/amphion/Vevo1.5), licensed CC-BY-NC-ND-4.0: " +
        "personal, non-commercial use only, and nothing made from them may be shared as a derivative; and OpenAI Whisper medium (MIT). " +
        "Choose it only for your own non-commercial use.";

    /// <summary>Sets the singing role up on <paramref name="host"/> (null: this PC) after one confirmation naming its
    /// downloads, licences and terms, through the same martlet-host add as every role: a plain Set up installs SoulX-Singer
    /// only; <paramref name="vevosing"/> adds the VevoSing voice match where Singing already runs (its own confirmation).
    /// Progress shows in the run window; the card says setting up, ready (with the voice matches there) or failed and why.</summary>
    private async Task SetUpSingingAsync(PairedHost? host, bool vevosing)
    {
        if (store is null || setupService is null || closing) return;
        // The Singing card follows one setup at a time (what it shows while setting up); everything else goes ahead meanwhile.
        if (singingPendingHost is not null)
        {
            ActionText.Text = $"Singing is still being set up on {(singingPendingHost == SingingThisPc ? "this PC" : singingPendingHost)}. " +
                "Its window shows progress; set up the next one when it's done.";
            return;
        }
        var where = host is null ? "this PC" : host.HostId;
        if (host is null && ThisPcCannotSing() is { } cannot) { ActionText.Text = $"Singing can't run on this PC. {cannot}"; return; }
        var thisPc = ThisPcHost();
        var confirmed = vevosing
            ? ConfirmationDialog.Confirm(this,
                $"Add VevoSing to Singing on {where}?\n\nVevoSing is a second voice match: a little closer to the voice and cleaner, but it " +
                "may drift off-key. SoulX-Singer stays set up, and Voice match switches between them.\n\n" + VevoSingTerms,
                "Add VevoSing")
            : ConfirmationDialog.Confirm(this,
                $"Set up Singing on {where}?\n\n" +
                (host is null && thisPc is null
                    ? machine.DockerInstalled ? "Martlet first sets up its host service in Docker Desktop. "
                        : "Martlet first installs Docker Desktop (it asks for its own terms) and sets up its host service. "
                    : "") +
                "It needs an NVIDIA graphics card with 6 GB+ and is a large download; Martlet shows the progress. " + SingingGpuNote +
                "\n\n" + SingingTerms(vevosing: false),
                "Set up Singing");
        if (!confirmed) return;
        var answers = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["choice.SINGING_MODEL"] = "ace-step-v15-soulx-svc",
            ["choice.SINGING_VOICE_MATCHES"] = vevosing ? "soulx-vevosing" : "soulx"
        };
        void Failed(string why)
        {
            if (vevosing) singingVevoFailure = why;
            else singingFailure = why;
        }
        if (vevosing) singingVevoFailure = null;
        else singingFailure = null;
        singingPendingHost = host?.HostId ?? SingingThisPc;
        singingPendingVevo = vevosing;
        var what = vevosing ? "VevoSing" : "Singing";
        RenderTab();
        if (host is not null)
        {
            try
            {
                var done = await RunHostActionAsync(host, HostRoles.Get(HostRoles.Singing).Add, answers);
                if (closing) return;
                singingServices.Remove(host.HostId);
                if (done is null) Failed($"Setting it up on {host.HostId} stopped. Its run window has details.");
                else if (await WaitForSingingAsync(host, vevosing, lifetime.Token) is { } why)
                    Failed($"Setup finished, but Martlet can't see {what} on {host.HostId} yet ({why}). Check it in a minute.");
                else ActionText.Text = vevosing ? $"VevoSing is ready on {host.HostId}." : $"Singing is ready on {host.HostId}. Ask Martlet to sing you a song.";
            }
            catch (OperationCanceledException) { }
            catch (Exception error) when (ClusterSync.IsHostFailure(error) || error is JsonException) { Failed(error.Message); }
            finally
            {
                singingPendingHost = null;
                singingPendingVevo = false;
                if (!closing && openTab is not null) RenderTab();
            }
            return;
        }
        var dataDirectory = store.DataDirectory;
        var service = setupService;
        try
        {
            // Why martlet-host stopped ("Stopped: ..."), for the card when the run fails.
            string? stopped = null;
            async Task<string> Continue(HostRunWindow run, PairedHost pc)
            {
                var target = pc.Target(Version);
                await HostLocal.EnsureDockerAsync(run, Martlet.Core.Installation.ContinueSetupKind.Docker);
                await HostLocal.EnsureImageAsync(target, run);
                var inputs = await HostLocal.DescribeAsync(target, HostRoles.Singing, run.Output, run.Token);
                var chosen = HostInputDialog.WithGpu(run, "this PC", what, inputs, answers) ?? throw new OperationCanceledException();
                run.Status(vevosing ? "Adding VevoSing on this PC (about 4.5 GB)..." : "Installing Singing on this PC (a large download)...");
                var exit = await HostLocal.EngineAsync(target, ["add", HostRoles.Singing], SetupProgress(run, what, why => stopped ??= why),
                    run.Token, answers: chosen);
                if (exit != 0)
                    throw new InvalidOperationException($"Installing {what} stopped{(stopped is null ? $" (exit {exit})" : ": " + stopped)}. The output has details.");
                run.Status($"Checking {what} on this PC...");
                singingServices.Remove(pc.HostId);
                if (await WaitForSingingAsync(pc, vevosing, run.Token) is { } why)
                {
                    stopped = $"Setup finished, but Martlet can't see {what} yet ({why}). Check this PC in a minute.";
                    throw new InvalidOperationException(stopped);
                }
                return vevosing ? "VevoSing is ready on this PC." : "Singing is ready on this PC. Ask Martlet to sing you a song.";
            }

            string? status;
            if (thisPc is not null)
                status = await HostRunWindow.RunAsync(this, vevosing ? "Add VevoSing on this PC" : "Set up Singing on this PC", run => Continue(run, thisPc)) ??
                    (stopped is null ? $"{what} wasn't set up. The run window has details." : $"{what} wasn't set up: {stopped}");
            else
            {
                (_, status) = await HostsWindow.SetUpThisPcAsync(this, new AvatarProfileStore(dataDirectory), service,
                    text => ActionText.Text = text, lifetime.Token, Continue, "Set up Singing on this PC");
                await ReadMachineAsync();
            }
            if (!closing && status is not null)
            {
                ActionText.Text = status;
                if (!status.StartsWith($"{what} is ready", StringComparison.Ordinal)) Failed(status);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException or ContractException or
            JsonException or ArgumentException or Audio2FaceHostException)
        {
            Failed(error.Message);
            ActionText.Text = error.Message;
        }
        finally
        {
            singingPendingHost = null;
            singingPendingVevo = false;
        }
        if (!closing && openTab is not null) RenderTab();
    }

    /// <summary>martlet-host's output for the run window, with the singing role's own progress (the image build's steps, the
    /// service starting, each pinned model file's download) as its status line, so the window (and MCP, through
    /// HostRunStatus) shows how far setting up is; <paramref name="stopped"/> gets why martlet-host stopped, if it does.</summary>
    private static IProgress<string> SetupProgress(HostRunWindow run, string what, Action<string> stopped) => new Progress<string>(line =>
    {
        run.Output.Report(line);
        if (line.StartsWith("Stopped: ", StringComparison.Ordinal)) stopped(line["Stopped: ".Length..].Trim());
        else if (SingingDownloadPattern().Match(line) is { Success: true } download)
            run.Status($"{what}: downloading {download.Groups["file"].Value}, {download.Groups["percent"].Value}% of {download.Groups["mib"].Value} MiB...");
        else if (SingingModelPattern().Match(line) is { Success: true } model)
            run.Status($"{what}: getting {model.Groups["file"].Value}...");
        else if (SingingBuildPattern().Match(line) is { Success: true } step)
            run.Status($"{what}: building the singing image (step {step.Groups["step"].Value} of {step.Groups["of"].Value})...");
        else if (line.Contains("Waiting for singing", StringComparison.Ordinal))
            run.Status($"{what}: starting the singing service...");
    });

    [GeneratedRegex(@"^(?<file>[\w.-]+): (?<percent>\d{1,3})% of (?<mib>\d+) MiB$")]
    private static partial Regex SingingDownloadPattern();

    [GeneratedRegex(@"^Model file (?<file>\S+) \(")]
    private static partial Regex SingingModelPattern();

    [GeneratedRegex(@"^#\d+ \[singing[^\]]*? (?<step>\d+)/(?<of>\d+)\]")]
    private static partial Regex SingingBuildPattern();

    /// <summary>After martlet-host added the role, checks <paramref name="host"/> (about a minute) until its gateway offers the
    /// singing route and its singing service answers set up (with VevoSing, for <paramref name="vevosing"/>). Returns null
    /// once it does, else what it saw last.</summary>
    private async Task<string?> WaitForSingingAsync(PairedHost host, bool vevosing, CancellationToken token)
    {
        for (var attempt = 0; ; attempt++)
        {
            var check = await HostControl.CheckAsync(host.Pairing, HardwareStore, token);
            hostChecks[host.HostId] = check;
            NoteSingingHost();
            var why = check.Text;
            if (check.Routes?.Any(r => r.RouteId == Audio2FaceHostConnection.SongRouteId) == true)
            {
                try
                {
                    var service = await SongClient.ReadServiceAsync(host.Pairing, token);
                    singingServices[host.HostId] = service;
                    if (service is { State: not "not_provisioned" } && (!vevosing || service.Has(SongVoiceMatch.VevoSing))) return null;
                    why = service is null ? "it offers no singing route" : vevosing && !service.Has(SongVoiceMatch.VevoSing)
                        ? "its singing service doesn't list VevoSing" : $"its singing service is {service.State}";
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                catch (Exception error) when (ClusterSync.IsHostFailure(error) || error is JsonException) { why = error.Message; }
            }
            if (attempt >= 12) return why;
            await Task.Delay(TimeSpan.FromSeconds(5), token);
        }
    }
}
