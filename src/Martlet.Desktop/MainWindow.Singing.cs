using System.IO;
using System.Text.Json;
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
/// there, and its choices (<see cref="SingingPreferences"/>). Songs are only ever performed by Martlet in conversation
/// (through <see cref="SongMaker"/>), never played from a button here. Automation IDs: <c>SingingHost-&lt;host&gt;</c>
/// ("SingingHost-this-pc"), <c>SingingEngine</c>, <c>SingingFeatures</c>, <c>SingingState</c>, <c>SingingSetUp</c>,
/// <c>SingingSetUpVevo</c>, <c>SingingQuality</c> and <c>SingingVoiceMatch</c>.
/// </summary>
public partial class MainWindow
{
    internal const string SingingThisPc = "this-pc";


    internal static readonly IReadOnlyList<string> SingingFeatures =
        ["NVIDIA GPU 6 GB+", "Docker", "Sings in your cloned voice", "With backing music", "A few minutes per song",
            "ACE-Step MIT · SoulX-Singer Apache-2.0"];

    private string? singingHost;
    private string? singingPendingHost;
    private string? singingFailure;


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
        var cannot = ready ? null : onThisPc ? ThisPcCannotSing() : CannotHand(shown, HostRoles.Singing, "singing");
        var state = ready ? $"Ready on {where}." : pending ? $"Setting up on {where}..." : cannot
            ?? (singingFailure is { } failed ? $"Setup failed on {where}: {failed}" : $"Not set up on {where} yet.");
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
            "Sings in your cloned voice" => "No training: the voice is matched from its recording in your voice library.",
            "ACE-Step MIT · SoulX-Singer Apache-2.0" => "VevoSing, an optional second voice match, is CC-BY-NC-ND-4.0 (personal, non-commercial use only).",
            _ => null
        });
        AutomationProperties.SetAutomationId(chips, "SingingFeatures");
        text.Children.Add(chips);
        var stateLine = Note(state, new Thickness(0, 4, 0, 0));
        if (cannot is not null || singingFailure is not null && !ready && !pending)
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

        // Choices, kept on this PC (singing.json) and read when a song starts.
        var preferences = store is null ? new SingingPreferences() : SingingPreferences.Load(store.DataDirectory);
        var quality = new ComboBox { MinHeight = 30, MinWidth = 300, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 8, 0, 0) };
        quality.Items.Add(new ComboBoxItem { Content = "Fast (recommended)", Tag = SongQuality.Fast });
        quality.Items.Add(new ComboBoxItem { Content = "High quality (clearer words, a few seconds more)", Tag = SongQuality.HighQuality });
        quality.SelectedIndex = preferences.Quality == SongQuality.HighQuality ? 1 : 0;
        AutomationProperties.SetName(quality, "Quality");
        AutomationProperties.SetAutomationId(quality, "SingingQuality");
        var match = new ComboBox { MinHeight = 30, MinWidth = 300, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 8, 0, 0) };
        match.Items.Add(new ComboBoxItem { Content = "SoulX-Singer (keeps the tune and words)", Tag = SongVoiceMatch.SoulX });
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
        match.SelectionChanged += (_, _) => SaveChoices();
        stack.Add(Labeled("Quality", quality));
        stack.Add(Labeled("Voice match", match));
        if (match.SelectedIndex == 1 && target is not null && ready)
            stack.Add(Row(PageButton("Add VevoSing there", () => SetUpSingingAsync(onThisPc ? null : target, vevosing: true).Forget(),
                link: true, id: "SingingSetUpVevo")));

        return Card([.. stack]);
    }

    private bool Offers(PairedHost host, string kind) => hostChecks.GetValueOrDefault(host.HostId)?.Offers?.ContainsKey(kind) == true;

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
        (vevosing ? " VevoSing adds Amphion's Vevo1.5 models (CC-BY-NC-ND-4.0: personal, non-commercial use only; nothing made from " +
            "them may be shared as a derivative) and OpenAI Whisper medium (MIT)." : "") +
        " Songs are sung in a voice from your voice library, cloned from its recording, which goes to that computer: only use voices " +
        "that are yours or that you have permission to use, and never to imitate someone without permission or to deceive. ACE-Step " +
        "asks you to check that songs are original and to say they are AI-generated.";

    /// <summary>Sets the singing role up on <paramref name="host"/> (null: this PC) after one confirmation naming its
    /// downloads, licences and terms; with <paramref name="vevosing"/> it adds the VevoSing voice match too.</summary>
    private async Task SetUpSingingAsync(PairedHost? host, bool vevosing)
    {
        if (store is null || setupService is null || closing) return;
        if (assigningRole || hostBusy) { ActionText.Text = "Another change is still finishing. Try again in a moment."; return; }
        var where = host is null ? "this PC" : host.HostId;
        if (host is null && ThisPcCannotSing() is { } cannot) { ActionText.Text = $"Singing can't run on this PC. {cannot}"; return; }
        var thisPc = ThisPcHost();
        if (!ConfirmationDialog.Confirm(this,
                $"Set up Singing on {where}{(vevosing ? " with VevoSing" : "")}?\n\n" +
                (host is null && thisPc is null
                    ? machine.DockerInstalled ? "Martlet first sets up its host service in Docker Desktop. "
                        : "Martlet first installs Docker Desktop (it asks for its own terms) and sets up its host service. "
                    : "") +
                "It needs an NVIDIA graphics card with 6 GB+ and is a large download; Martlet shows the progress. It runs beside the " +
                "voice engine and frees the graphics card after five idle minutes.\n\n" + SingingTerms(vevosing),
                vevosing ? "Add VevoSing" : "Set up Singing"))
            return;
        var answers = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["choice.SINGING_MODEL"] = "ace-step-v15-soulx-svc",
            ["choice.SINGING_VOICE_MATCHES"] = vevosing ? "soulx-vevosing" : "soulx"
        };
        singingFailure = null;
        singingPendingHost = host?.HostId ?? SingingThisPc;
        RenderTab();
        if (host is not null)
        {
            LaunchOnHost(host, HostRoles.Get(HostRoles.Singing).Add, answers);
            WatchSingingSetupAsync(host).Forget();
            return;
        }
        var dataDirectory = store.DataDirectory;
        var service = setupService;
        assigningRole = true;
        hostBusy = true;
        try
        {
            async Task<string> Continue(HostRunWindow run, PairedHost pc)
            {
                var target = pc.Target(Version);
                await HostLocal.EnsureDockerAsync(run, Martlet.Core.Installation.ContinueSetupKind.Docker);
                await HostLocal.EnsureImageAsync(target, run.Status, run.Output, run.Token);
                run.Status("Installing Singing on this PC (a large download)...");
                var exit = await HostLocal.EngineAsync(target, ["add", HostRoles.Singing], run.Output, run.Token, answers: answers);
                if (exit != 0) throw new InvalidOperationException($"Installing Singing stopped (exit {exit}). The output has details.");
                for (var attempt = 0; ; attempt++)
                {
                    var check = await HostControl.CheckAsync(pc.Pairing, HardwareStore, run.Token);
                    hostChecks[pc.HostId] = check;
                    NoteSingingHost();
                    if (check.Routes?.Any(r => r.RouteId == Audio2FaceHostConnection.SongRouteId) == true) break;
                    if (attempt >= 12)
                        throw new InvalidOperationException($"Setup finished, but Martlet can't see Singing yet ({check.Text}). Check this PC in a minute.");
                    await Task.Delay(TimeSpan.FromSeconds(5), run.Token);
                }
                return "Singing is ready on this PC. Ask Martlet to sing you a song.";
            }

            string? status;
            if (thisPc is not null)
                status = await HostRunWindow.RunAsync(this, "Set up Singing on this PC", run => Continue(run, thisPc)) ??
                    "Singing wasn't set up. The run window has details.";
            else
            {
                (_, status) = await HostsWindow.SetUpThisPcAsync(this, new AvatarProfileStore(dataDirectory), service,
                    text => ActionText.Text = text, lifetime.Token, Continue, "Set up Singing on this PC");
                await ReadMachineAsync();
            }
            if (!closing && status is not null)
            {
                ActionText.Text = status;
                if (!status.StartsWith("Singing is ready", StringComparison.Ordinal)) singingFailure = status;
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException or ContractException or
            JsonException or ArgumentException or Audio2FaceHostException)
        {
            singingFailure = error.Message;
            ActionText.Text = error.Message;
        }
        finally
        {
            assigningRole = false;
            hostBusy = false;
            singingPendingHost = null;
        }
        if (!closing && openTab is not null) RenderTab();
    }

    /// <summary>After an install started on another computer, checks it until its singing route appears (up to an hour, as the
    /// models are a large download).</summary>
    private async Task WatchSingingSetupAsync(PairedHost host)
    {
        try
        {
            for (var attempt = 0; attempt < 240 && !closing; attempt++)
            {
                await Task.Delay(TimeSpan.FromSeconds(15), lifetime.Token);
                var check = await HostControl.CheckAsync(host.Pairing, HardwareStore, lifetime.Token);
                hostChecks[host.HostId] = check;
                NoteSingingHost();
                if (check.Routes?.Any(r => r.RouteId == Audio2FaceHostConnection.SongRouteId) == true)
                {
                    ActionText.Text = $"Singing is ready on {host.HostId}.";
                    return;
                }
            }
            singingFailure = $"Singing didn't appear on {host.HostId} within an hour; check its run output.";
        }
        catch (OperationCanceledException) { }
        catch (Exception error) when (ClusterSync.IsHostFailure(error)) { singingFailure = error.Message; }
        finally
        {
            singingPendingHost = null;
            if (!closing && openTab is not null) RenderTab();
        }
    }
}
