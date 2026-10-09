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
/// Companion › Singing (an optional extra): Martlet writes a song from lyrics and a style and sings it with the voice it speaks with,
/// on the <c>singing</c> host role (ACE-Step 1.5 writes the music, SoulX-Singer-SVC or VevoSing matches the singing to the
/// voice). In the standard order: Now; the main choice (Off, or Martlet's Singing role: its details choose the computer, this PC
/// or one of your others, say where it stands there, with the voice matches set up there read from its singing service through
/// the gateway, and its button sets the role up after a confirmation naming the downloads, licences and terms, or sings there);
/// then the song choices (<see cref="SingingPreferences"/>: quality, and the voice match as a second option picker). Off keeps
/// the role set up and stops offering songs (singing.json). A plain Set up installs SoulX-Singer only; choosing VevoSing where it
/// isn't set up offers Add VevoSing there, with its own confirmation. Songs are only ever performed by Martlet in conversation
/// (through <see cref="SongMaker"/>), never played from a button here. Automation IDs: <c>SingingNow</c>,
/// <c>Picker-Singing-Off</c>, <c>Picker-Singing-Role</c>, <c>SingingTurnOff</c>, <c>SingingHost-&lt;host&gt;</c>
/// ("SingingHost-this-pc"), <c>SingingState</c>, <c>SingingGpu</c>, <c>SingingSetUp</c>, <c>SingingUse</c>,
/// <c>SingingQuality</c>, <c>Picker-SingingVoiceMatch-&lt;SoulX|VevoSing&gt;</c>, <c>SingingUseSoulX</c>,
/// <c>SingingUseVevoSing</c>, <c>SingingVoiceMatchState</c> and <c>SingingSetUpVevo</c>.
/// </summary>
public partial class MainWindow
{
    internal const string SingingThisPc = "this-pc";


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

    /// <summary>Companion › Singing in the standard order: Now (whether Martlet sings, where and with what, and any problem), the
    /// main choice (Off, or Martlet's Singing role on this PC or another of your computers: its details choose the computer, set
    /// it up and sing there), then the song choices (quality, and the voice match as a second option picker).</summary>
    private void RenderSingingTab(Panel page)
    {
        var preferences = store is null ? new SingingPreferences() : SingingPreferences.Load(store.DataDirectory);
        var thisPc = ThisPcHost();
        var others = NetworkMap.Hosts(Inputs()).Where(h => h.HostId != thisPc?.HostId).ToArray();
        var sings = others.FirstOrDefault(h => hostChecks.GetValueOrDefault(h.HostId)?.Offers?.ContainsKey(HostRoles.Singing) == true);
        var shown = singingHost ?? (preferences.Host is { } saved && others.Any(h => h.HostId == saved) ? saved
            : thisPc is not null && Offers(thisPc, HostRoles.Singing) ? SingingThisPc : sings?.HostId ?? SingingThisPc);
        var target = shown == SingingThisPc ? thisPc : FindHost(shown);
        var onThisPc = shown == SingingThisPc;
        var where = onThisPc ? "this PC" : shown;
        var ready = target is not null && Offers(target, HostRoles.Singing);
        var pending = singingPendingHost == shown;
        var addingVevo = pending && singingPendingVevo;
        // The voice matches set up there, from its singing service (read once the page shows a computer that sings).
        SongClient.SingingService? service = null;
        var serviceKnown = ready && singingServices.TryGetValue(target!.HostId, out service);
        if (ready && !serviceKnown) ReadSingingServiceAsync(target!).Forget();
        var vevo = service?.Has(SongVoiceMatch.VevoSing) == true;
        var cannot = ready ? null : onThisPc ? ThisPcCannotSing() : CannotHand(shown, HostRoles.Singing, "singing");
        var active = store is not null && SongClient.IsSetUp(store.DataDirectory);
        var readySomewhere = thisPc is not null && Offers(thisPc, HostRoles.Singing) || sings is not null;
        var singsThere = active && target is not null && (preferences.Host == target.HostId || DesktopSongSource.Fixture);

        // ---------- Now ----------
        var nowText = new TextBlock { Text = SingingNow(preferences, active, thisPc?.HostId, readySomewhere), FontSize = 15,
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 6) };
        AutomationProperties.SetAutomationId(nowText, "SingingNow");
        var now = new List<UIElement> { Heading("Now"), nowText,
            Note("Ask Martlet to sing you a song. It writes the song in the background while you keep talking, then sings it with " +
                "the voice it speaks with.", new Thickness(0, 0, 0, 0)) };
        if (active && store is not null && SongClient.SpeakingVoiceId(store.DataDirectory) is null && !DesktopSongSource.Fixture)
            now.Add(Warning("There's no voice to sing with yet. Choose a voice on Companion › Voice."));
        if (singingFailure is { } failed && !pending) now.Add(Warning("Setting Singing up failed: " + failed));
        page.Children.Add(Card([.. now]));

        // ---------- the main choice: Off, or Martlet's Singing role ----------
        var state = addingVevo ? $"Adding VevoSing on {where}..."
            : ready ? $"Ready on {where}" + (service is null ? "." : vevo ? " with SoulX-Singer and VevoSing." : " with SoulX-Singer.") +
                (singsThere ? " Martlet sings there." : "")
            : pending ? $"Setting up on {where}..."
            : cannot ?? (singingFailure is { } why ? $"Setup failed on {where}: {why}" : $"Not set up on {where} yet.");
        if (ready && !addingVevo && singingVevoFailure is { } vevoFailed) state += $" Adding VevoSing failed: {vevoFailed}";
        var options = OptionalExtras.SingingChoices(active, readySomewhere).Select(option => option.IsOff
            ? option with
            {
                Action = !active ? null : () => PageButton("Turn singing off", () => SaveSinging(p => p with { Off = true },
                    "Singing is off. It stays set up on your computers; choose Martlet's Singing role to sing again."), primary: true, id: "SingingTurnOff")
            }
            : option with
            {
                Details = () => SingingRoleDetails(others, shown, state, cannot is not null || (ready ? singingVevoFailure : singingFailure) is not null && !pending),
                Action = () =>
                {
                    if (ready)
                        return singsThere ? null : PageButton($"Sing on {where}", () => SaveSinging(p => p with { Off = false, Host = target!.HostId },
                            $"Martlet sings on {where}. Ask it to sing you a song."), primary: true, id: "SingingUse");
                    var setUp = PageButton(pending ? "Setting up..." : "Set up", () => SetUpSingingAsync(onThisPc ? null : target, vevosing: false).Forget(),
                        primary: true, id: "SingingSetUp");
                    setUp.IsEnabled = !pending && cannot is null;
                    if (cannot is not null)
                    {
                        setUp.ToolTip = cannot;
                        ToolTipService.SetShowOnDisabled(setUp, true);
                        AutomationProperties.SetHelpText(setUp, cannot);
                    }
                    return setUp;
                }
            }).ToList();
        page.Children.Add(OptionPicker("Singing", "How Martlet sings",
            "Off, or Martlet's Singing role on a computer with an NVIDIA graphics card. This choice stays on this PC.", options));
        if (PickerShown("Singing", options) == "Off") return;

        // ---------- configuration: the song choices, kept on this PC (singing.json) and read when a song starts ----------
        var quality = new ComboBox { MinHeight = 30, MinWidth = 300, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 4, 0, 0) };
        quality.Items.Add(new ComboBoxItem { Content = "Fast (recommended)", Tag = SongQuality.Fast });
        quality.Items.Add(new ComboBoxItem { Content = "High quality (clearer words, a few seconds more)", Tag = SongQuality.HighQuality });
        quality.SelectedIndex = preferences.Quality == SongQuality.HighQuality ? 1 : 0;
        AutomationProperties.SetName(quality, "Quality");
        AutomationProperties.SetAutomationId(quality, "SingingQuality");
        quality.SelectionChanged += (_, _) => SaveSinging(p => p with { Quality = quality.SelectedIndex == 1 ? SongQuality.HighQuality : SongQuality.Fast },
            null, choice: false);
        var matches = OptionalExtras.VoiceMatches(preferences.VoiceMatch).Select(option =>
        {
            var match = Enum.Parse<SongVoiceMatch>(option.Key);
            var use = preferences.VoiceMatch == match ? null : (Func<Button?>)(() => PageButton($"Use {option.Name}",
                () => SaveSinging(p => p with { VoiceMatch = match }, $"Songs now use {option.Name}.", choice: false), primary: true,
                id: "SingingUse" + option.Key));
            if (match != SongVoiceMatch.VevoSing || !(preferences.VoiceMatch == SongVoiceMatch.VevoSing && ready && (serviceKnown && !vevo || addingVevo)))
                return option with { Action = use };
            // VevoSing is chosen but isn't set up where Martlet sings: Add VevoSing there.
            return option with
            {
                Action = use,
                Details = () =>
                {
                    var missing = Note(addingVevo ? $"Adding VevoSing on {where}. Songs use SoulX-Singer until it's ready."
                        : $"VevoSing isn't set up on {where}. Songs use SoulX-Singer until you add it.", new Thickness(0, 6, 0, 0));
                    AutomationProperties.SetAutomationId(missing, "SingingVoiceMatchState");
                    var add = PageButton(addingVevo ? "Adding VevoSing..." : "Add VevoSing there",
                        () => SetUpSingingAsync(onThisPc ? null : target, vevosing: true).Forget(), link: true, id: "SingingSetUpVevo");
                    add.IsEnabled = !pending;
                    return [missing, Row(add)];
                }
            };
        }).ToList();
        page.Children.Add(Card(Heading("Song choices"),
            Note("Kept on this PC and used from the next song.", new Thickness(0, 0, 0, 4)),
            Labeled("Quality", quality),
            new TextBlock { Text = "Voice match", FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 12, 0, 6) },
            OptionPickerBody("SingingVoiceMatch", matches)));
    }

    /// <summary>The Singing role's details: the computer pills (with other computers paired), where it stands on the shown
    /// computer, and whether it needs a graphics card of its own.</summary>
    private IEnumerable<UIElement> SingingRoleDetails(IReadOnlyList<PairedHost> others, string shown, string state, bool problem)
    {
        if (others.Count > 0)
        {
            var pills = new WrapPanel { Margin = new Thickness(0, 6, 0, 2) };
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
            yield return pills;
        }
        var stateLine = Note(state, new Thickness(0, 4, 0, 0));
        if (problem) stateLine.SetResourceReference(TextBlock.ForegroundProperty, "WarningBrush");
        AutomationProperties.SetAutomationId(stateLine, "SingingState");
        yield return stateLine;
        var gpu = HelpTip.Explain(SingingGpuNote, new Thickness(0, 6, 0, 0), "SingingGpu", "singing on the graphics card");
        AutomationProperties.SetAutomationId(gpu, "SingingGpu");
        yield return gpu;
    }

    /// <summary>Companion › Singing's Now line (SingingNow).</summary>
    internal static string SingingNow(SingingPreferences preferences, bool active, string? thisPcHost, bool readySomewhere)
    {
        if (preferences.Off)
            return $"Off. {OptionalExtras.OffMeans(CompanionTab.Singing)}." + (readySomewhere ? " Singing stays set up on your computers." : "");
        if (!active) return "Not set up yet. Martlet sings once Singing is set up on one of your computers.";
        var where = DesktopSongSource.Fixture ? "this PC (FIXTURE - NOT AI)" : preferences.Host is null || preferences.Host == thisPcHost ? "this PC" : preferences.Host;
        return $"Martlet sings on {where} with {(preferences.VoiceMatch == SongVoiceMatch.VevoSing ? "VevoSing" : "SoulX-Singer")}, " +
            $"{(preferences.Quality == SongQuality.HighQuality ? "high quality" : "fast")}.";
    }

    /// <summary>Saves a change to singing.json and shows <paramref name="done"/>; the next song uses it. A change of the main
    /// choice (<paramref name="choice"/>: Off, or where Martlet sings) shows the option in use again; a song choice keeps the
    /// page as it is.</summary>
    private void SaveSinging(Func<SingingPreferences, SingingPreferences> change, string? done, bool choice = true)
    {
        if (store is null) return;
        try
        {
            var next = change(SingingPreferences.Load(store.DataDirectory));
            next.Save(store.DataDirectory);
            ErrorLog.Info($"Singing: {(next.Off ? "off" : "on")}, host {next.Host ?? "none"}, {next.VoiceMatch}, {next.Quality}.");
            if (done is not null) ActionText.Text = done;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            ActionText.Text = $"Couldn't save the singing choices: {error.Message}";
            return;
        }
        if (choice) ForgetPicker("Singing");
        ForgetPicker("SingingVoiceMatch");
        Dispatcher.BeginInvoke(() => { if (!closing && openTab == CompanionTab.Singing) RenderTab(); });
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
        if (!closing && openTab == CompanionTab.Singing && !CompanionContent.IsKeyboardFocusWithin) RenderTab();
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
                else
                {
                    // Setting the role up from the page's main choice uses it: Martlet sings there (and isn't off any more).
                    if (!vevosing) SaveSinging(p => p with { Off = false, Host = host.HostId }, null);
                    ActionText.Text = vevosing ? $"VevoSing is ready on {host.HostId}." : $"Singing is ready on {host.HostId}. Ask Martlet to sing you a song.";
                }
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
                target = await HostLocal.EngineForChangeAsync(target, HostRoles.Singing, run);
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
                if (!vevosing) Dispatcher.Invoke(() => SaveSinging(p => p with { Off = false, Host = pc.HostId }, null));
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
