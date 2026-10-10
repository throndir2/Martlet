using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Martlet.Avatar.Audio2Face.Remote;
using Martlet.Avatar.Hosting;
using Martlet.Core.Cluster;
using Martlet.Core.Contracts;
using Martlet.Core.Singing;

namespace Martlet.Desktop;

/// <summary>
/// Companion › Singing (an optional extra): Martlet writes a song from lyrics and a style and sings it with the voice it speaks with,
/// on the <c>singing</c> host role (ACE-Step 1.5 writes the music, SoulX-Singer-SVC or VevoSing matches the singing to the
/// voice). In the standard order: Now; the singing pool (the shared list control, docs/SINGING.md#the-singing-pool: the
/// computers that make songs, in order, with no separate on/off switch: with none on, Martlet doesn't sing); each member's
/// Settings say where Singing stands there (with the voice matches read from its singing service through the gateway), set
/// the role up after a confirmation naming the downloads, licences and terms, add VevoSing with its own confirmation, and
/// choose its own quality; what Singing is (the role's facts); then the song choices (<see cref="SingingPreferences"/>:
/// quality, and the voice match as a second option picker). Songs are only ever performed by Martlet in conversation (through
/// <see cref="SongMaker"/>), never played from a button here. Automation IDs: <c>SingingNow</c>, the list's
/// <c>Pool-singing-...</c> (and per member <c>Pool-singing-State-&lt;key&gt;</c>, <c>Pool-singing-SetUp-&lt;key&gt;</c>,
/// <c>Pool-singing-AddVevo-&lt;key&gt;</c>, <c>Pool-singing-Quality-&lt;key&gt;</c>), <c>Picker-Singing-Role</c>,
/// <c>SingingGpu</c>, <c>SingingQuality</c>, <c>Picker-SingingVoiceMatch-&lt;SoulX|VevoSing&gt;</c>, <c>SingingUseSoulX</c>,
/// <c>SingingUseVevoSing</c> and <c>SingingVoiceMatchState</c>.
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

    private string? singingPendingHost;
    private bool singingPendingVevo;
    /// <summary>Why the last setup failed, by computer (a host ID, or <see cref="SingingThisPc"/>): Singing, and VevoSing.</summary>
    private readonly Dictionary<string, string> singingFailures = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> singingVevoFailures = new(StringComparer.Ordinal);
    /// <summary>What each computer's singing service last reported through its gateway (the voice matches set up there), by
    /// host ID; null when it couldn't be read. Read when the page shows a computer that sings, and again after a setup.</summary>
    private readonly Dictionary<string, SongClient.SingingService?> singingServices = new(StringComparer.Ordinal);
    private readonly HashSet<string> singingReads = new(StringComparer.Ordinal);


    /// <summary>The song maker Martlet's conversation uses: <see cref="SongClient"/> on the singing pool, or the FIXTURE - NOT AI
    /// maker when MARTLET_SINGING_FIXTURE is 1; null before Martlet has its data directory.</summary>
    internal ISongMaker? SongMaker => store is null ? null : SongClient.For(store.DataDirectory);

    /// <summary>Companion › Singing in the standard order: Now (whether Martlet sings, where and with what), the singing pool
    /// (the shared list control: the computers that sing, in order; with none on, Martlet doesn't sing; each one's Settings say
    /// where Singing stands there, set it up, add VevoSing or give it its own quality), what Singing is (the role's facts), then
    /// the song choices (quality, and the voice match as a second option picker).</summary>
    private void RenderSingingTab(Panel page)
    {
        var preferences = store is null ? new SingingPreferences() : SingingPreferences.Load(store.DataDirectory);
        EnsureSingingPool(preferences);
        var thisPc = ThisPcHost();
        var listed = store is not null && WorkSharingRoster.Pool(store.DataDirectory, PoolAreas.Singing) is not null;
        var members = store is null ? [] : SongClient.Members(store.DataDirectory);
        var active = store is not null && SongClient.IsSetUp(store.DataDirectory);
        var own = NetworkMap.Hosts(Inputs()).Where(h => !h.Shared).ToArray();
        var readySomewhere = own.Any(h => Offers(h, HostRoles.Singing)) || thisPc is not null && Offers(thisPc, HostRoles.Singing);
        // The voice matches set up on each member that sings, read once from its singing service.
        var singers = members.Select(m => m.Host).DistinctBy(h => h.HostId).Where(h => Offers(h, HostRoles.Singing)).ToArray();
        foreach (var host in singers.Where(h => !singingServices.ContainsKey(h.HostId))) ReadSingingServiceAsync(host).Forget();
        string Name(string hostId) => hostId == thisPc?.HostId ? "this PC" : hostId;
        IReadOnlyList<string> where = listed ? [.. members.Select(m => Name(m.Host.HostId)).Distinct()]
            : preferences.Host is { } saved ? [Name(saved)] : [];

        // ---------- Now ----------
        var nowText = new TextBlock { Text = SingingNow(preferences, active, where, listed, readySomewhere), FontSize = 15,
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 6) };
        AutomationProperties.SetAutomationId(nowText, "SingingNow");
        var now = new List<UIElement> { Heading("Now"), nowText,
            Note("Ask Martlet to sing you a song. It writes the song in the background while you keep talking, then sings it with " +
                "the voice it speaks with.", new Thickness(0, 0, 0, 0)) };
        if (active && store is not null && SongClient.SpeakingVoiceId(store.DataDirectory) is null && !DesktopSongSource.Fixture)
            now.Add(Warning("There's no voice to sing with yet. Choose a voice on Companion › Voice."));
        page.Children.Add(Card([.. now]));

        // ---------- the singing pool: the computers that sing, in order ----------
        page.Children.Add(PoolListCard(new PoolListOptions
        {
            Area = PoolAreas.Singing,
            Heading = "Where Martlet sings",
            Intro = "The computers that make songs, in order. A song goes to the first one that isn't singing another song and has " +
                "the voice match; when all are busy it waits on the one with the shortest line. With none on, Martlet doesn't sing. " +
                "Open a computer's Settings to set Singing up there, add VevoSing or choose its own quality.",
            Status = SingingMemberStatus,
            Settings = SingingMemberSettings,
            CanAdd = host => !host.Shared && HostCan(host.HostId, HostRoles.Singing) is not { Allowed: false },
            Saved = (before, after) =>
            {
                // A computer added that doesn't sing yet shows its Settings, where Set up is.
                foreach (var added in after.Members.Where(m => before.Find(m.Key) is null))
                    if (SingingMemberHost(added) is not { } host || !Offers(host, HostRoles.Singing))
                        poolExpanded.Add(PoolAreas.Singing.Id + " " + added.Key);
                return Task.CompletedTask;
            }
        }));

        // ---------- what Singing is: the role's facts ----------
        var role = OptionalExtras.SingingChoices(active, readySomewhere).First(o => !o.IsOff) with
        {
            Details = () =>
            {
                var gpu = HelpTip.Explain(SingingGpuNote, new Thickness(0, 6, 0, 0), "SingingGpu", "singing on the graphics card");
                AutomationProperties.SetAutomationId(gpu, "SingingGpu");
                return [gpu];
            }
        };
        page.Children.Add(Card(Heading("What Singing is"), OptionPickerBody("Singing", [role])));

        // ---------- configuration: the song choices, kept on this PC (singing.json) and read when a song starts ----------
        var quality = new ComboBox { MinHeight = 30, MinWidth = 300, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 4, 0, 0) };
        quality.Items.Add(new ComboBoxItem { Content = "Fast (recommended)", Tag = SongQuality.Fast });
        quality.Items.Add(new ComboBoxItem { Content = "High quality (clearer words, a few seconds more)", Tag = SongQuality.HighQuality });
        quality.SelectedIndex = preferences.Quality == SongQuality.HighQuality ? 1 : 0;
        AutomationProperties.SetName(quality, "Quality");
        AutomationProperties.SetAutomationId(quality, "SingingQuality");
        quality.SelectionChanged += (_, _) => SaveSinging(p => p with { Quality = quality.SelectedIndex == 1 ? SongQuality.HighQuality : SongQuality.Fast },
            null, choice: false);
        // VevoSing chosen while no computer in the list that sings has it: songs use SoulX-Singer until one does.
        var vevoNowhere = preferences.VoiceMatch == SongVoiceMatch.VevoSing && singers.Length > 0 &&
            singers.All(h => singingServices.TryGetValue(h.HostId, out var service) && service?.Has(SongVoiceMatch.VevoSing) != true);
        var matches = OptionalExtras.VoiceMatches(preferences.VoiceMatch).Select(option =>
        {
            var match = Enum.Parse<SongVoiceMatch>(option.Key);
            var use = preferences.VoiceMatch == match ? null : (Func<Button?>)(() => PageButton($"Use {option.Name}",
                () => SaveSinging(p => p with { VoiceMatch = match }, $"Songs now use {option.Name}.", choice: false), primary: true,
                id: "SingingUse" + option.Key));
            if (match != SongVoiceMatch.VevoSing || !vevoNowhere) return option with { Action = use };
            return option with
            {
                Action = use,
                Details = () =>
                {
                    var missing = Note("VevoSing isn't set up on any computer in the list. Songs use SoulX-Singer until you add it: " +
                        "open a computer's Settings above and choose Add VevoSing here.", new Thickness(0, 6, 0, 0));
                    AutomationProperties.SetAutomationId(missing, "SingingVoiceMatchState");
                    return [missing];
                }
            };
        }).ToList();
        page.Children.Add(Card(Heading("Song choices"),
            Note("Kept on this PC and used from the next song. A computer with its own quality in the list above uses that instead.",
                new Thickness(0, 0, 0, 4)),
            Labeled("Quality", quality),
            new TextBlock { Text = "Voice match", FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 12, 0, 6) },
            OptionPickerBody("SingingVoiceMatch", matches)));
    }

    /// <summary>The paired computer a singing pool member sings on: this PC's own host service for this PC, else the computer
    /// (a card's computer); null when it isn't paired here (or this PC has no host service yet).</summary>
    private PairedHost? SingingMemberHost(PoolMember member) =>
        member.Kind == PoolMemberKind.ThisPc ? ThisPcHost() : FindHost(member.HostId) is { Shared: false } host ? host : null;

    /// <summary>The key a setup on <paramref name="member"/> is followed by: <see cref="SingingThisPc"/> for this PC (its own host
    /// service, or none yet), else the host ID.</summary>
    private string SingingSetupKey(PoolMember member) => SingingMemberHost(member) is { } host && host.HostId != ThisPcHost()?.HostId
        ? host.HostId : member.Kind == PoolMemberKind.ThisPc || member.HostId == ThisPcHost()?.HostId ? SingingThisPc : member.HostId ?? SingingThisPc;

    /// <summary>A singing pool member's line in the list: whether Singing runs there (and its voice matches), is being set up,
    /// failed, or why it can't sing.</summary>
    private string? SingingMemberStatus(PoolMember member)
    {
        var key = SingingSetupKey(member);
        if (singingPendingHost == key) return singingPendingVevo ? "adding VevoSing..." : "setting Singing up...";
        var host = SingingMemberHost(member);
        var parts = new List<string>();
        if (host is not null && Offers(host, HostRoles.Singing))
        {
            singingServices.TryGetValue(host.HostId, out var service);
            parts.Add(service is null ? "runs Singing" : service.Has(SongVoiceMatch.VevoSing)
                ? "runs Singing with SoulX-Singer and VevoSing" : "runs Singing with SoulX-Singer");
        }
        else if (host is null)
            parts.Add(member.Kind == PoolMemberKind.ThisPc ? ThisPcCannotSing() ?? "doesn't run Singing yet; Settings sets it up" : "not paired with this PC");
        else
        {
            var check = hostChecks.GetValueOrDefault(host.HostId);
            parts.Add(check?.Reachable == false ? "not reachable right now"
                : check?.Reachable == true ? "doesn't run Singing yet; Settings sets it up" : "not checked yet");
        }
        if (singingFailures.GetValueOrDefault(key) is { } failed) parts.Add("setup failed: " + failed.TrimEnd('.'));
        if (member.Kind == PoolMemberKind.Gpu) parts.Add("its computer picks the card");
        if (SingingPool.Quality(member) is { } quality) parts.Add(quality == SongQuality.HighQuality ? "high quality" : "fast");
        return string.Join("; ", parts);
    }

    /// <summary>A singing pool member's Settings (automation IDs <c>Pool-singing-&lt;part&gt;-&lt;member key&gt;</c>): where Singing
    /// stands there (State), Set up Singing here (SetUp) where it doesn't run yet, Add VevoSing here (AddVevo) where it runs
    /// without VevoSing, and its own quality (Quality: the song choice, Fast or High quality).</summary>
    private UIElement? SingingMemberSettings(PoolMember member)
    {
        var id = $"Pool-{PoolAreas.Singing.Id}-";
        var host = SingingMemberHost(member);
        var thisPc = ThisPcHost();
        var onThisPc = member.Kind == PoolMemberKind.ThisPc || host is not null && host.HostId == thisPc?.HostId;
        var key = SingingSetupKey(member);
        var pending = singingPendingHost == key;
        var ready = host is not null && Offers(host, HostRoles.Singing);
        SongClient.SingingService? service = null;
        var known = ready && singingServices.TryGetValue(host!.HostId, out service);
        var vevo = service?.Has(SongVoiceMatch.VevoSing) == true;
        var cannot = ready ? null : onThisPc ? ThisPcCannotSing()
            : host is null ? "It isn't paired with this PC."
            : hostChecks.GetValueOrDefault(host.HostId)?.Reachable == false ? "It isn't reachable right now."
            : CannotHand(host.HostId, HostRoles.Singing, "singing");
        var state = pending ? singingPendingVevo ? "Adding VevoSing here..." : "Setting Singing up here..."
            : ready ? (service is null ? "Singing is ready here." : vevo ? "Singing is ready here with SoulX-Singer and VevoSing."
                : "Singing is ready here with SoulX-Singer.")
            : cannot ?? (singingFailures.GetValueOrDefault(key) is { } why ? $"Setting Singing up failed: {why}" : "Singing isn't set up here yet.");
        if (ready && !pending && singingVevoFailures.GetValueOrDefault(key) is { } vevoFailed) state += $" Adding VevoSing failed: {vevoFailed}";
        var panel = new StackPanel();
        var line = Note(state, new Thickness(0, 0, 0, 4));
        if (cannot is not null || !pending && (ready ? singingVevoFailures.ContainsKey(key) : singingFailures.ContainsKey(key)))
            line.SetResourceReference(TextBlock.ForegroundProperty, "WarningBrush");
        AutomationProperties.SetAutomationId(line, id + "State-" + member.Key);
        panel.Children.Add(line);
        var setUpHost = onThisPc ? null : host;
        Button? action = null;
        if (!ready)
        {
            action = PageButton(pending ? "Setting up..." : "Set up Singing here", () => SetUpSingingAsync(setUpHost, vevosing: false).Forget(),
                primary: true, id: id + "SetUp-" + member.Key);
            action.IsEnabled = !pending && singingPendingHost is null && cannot is null;
            if (cannot is not null)
            {
                action.ToolTip = cannot;
                ToolTipService.SetShowOnDisabled(action, true);
                AutomationProperties.SetHelpText(action, cannot);
            }
        }
        else if (known && !vevo)
        {
            action = PageButton(pending ? "Adding VevoSing..." : "Add VevoSing here", () => SetUpSingingAsync(setUpHost, vevosing: true).Forget(),
                id: id + "AddVevo-" + member.Key);
            action.IsEnabled = !pending && singingPendingHost is null;
        }
        if (action is not null) panel.Children.Add(Row(action));
        var quality = new ComboBox { MinHeight = 30, MinWidth = 260, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 4, 0, 0) };
        quality.Items.Add(new ComboBoxItem { Content = "The song choice below", Tag = null });
        quality.Items.Add(new ComboBoxItem { Content = "Fast", Tag = SongQuality.Fast });
        quality.Items.Add(new ComboBoxItem { Content = "High quality", Tag = SongQuality.HighQuality });
        quality.SelectedIndex = SingingPool.Quality(member) switch { SongQuality.Fast => 1, SongQuality.HighQuality => 2, _ => 0 };
        AutomationProperties.SetName(quality, $"Quality on {member.Name}");
        AutomationProperties.SetAutomationId(quality, id + "Quality-" + member.Key);
        quality.SelectionChanged += (_, _) =>
        {
            var chosen = (quality.SelectedItem as ComboBoxItem)?.Tag as SongQuality?;
            if (chosen == SingingPool.Quality(member)) return;
            SaveSingingMember(SingingPool.WithQuality(member, chosen), chosen is null
                ? $"{member.Name} makes songs in the song choice's quality."
                : $"{member.Name} makes songs in {(chosen == SongQuality.HighQuality ? "high quality" : "fast")} quality.");
        };
        panel.Children.Add(Labeled("Quality here", quality));
        return panel;
    }

    /// <summary>Saves one singing pool member's settings in the list (pools.json, shared with your other computers).</summary>
    private void SaveSingingMember(PoolMember member, string done)
    {
        if (store is null) return;
        var list = WorkSharingRoster.Pool(store.DataDirectory, PoolAreas.Singing) ?? new PoolList { Area = PoolAreas.Singing.Id };
        if (!PoolSettings.SaveFor(store.DataDirectory, PoolAreas.Singing, list.With(member)))
        {
            ActionText.Text = "Couldn't save the Singing list on this PC.";
            return;
        }
        WorkSharingRoster.Forget();
        QueueSettingsSync();
        ErrorLog.Info("Pools: " + done);
        ActionText.Text = done + " Your other computers follow it.";
        Dispatcher.InvokeAsync(() => { if (!closing && openTab == CompanionTab.Singing) RenderTab(); });
    }

    /// <summary>Makes the singing pool list once from Companion › Singing's older choice (the computer Martlet sang on, then
    /// your other computers that run Singing; all off when singing was off), so the choice isn't lost. Only when this PC sang on
    /// one of your computers: the list is shared, and an empty one made here would turn singing off on your other computers.</summary>
    private void EnsureSingingPool(SingingPreferences preferences)
    {
        if (store is null || WorkSharingRoster.Pool(store.DataDirectory, PoolAreas.Singing) is not null) return;
        var own = NetworkMap.Hosts(Inputs()).Where(h => !h.Shared).ToArray();
        if (preferences.Host is not { } saved || own.All(h => h.HostId != saved)) return;
        var list = new PoolList { Area = PoolAreas.Singing.Id };
        foreach (var id in own.Where(h => h.HostId != saved && Offers(h, HostRoles.Singing)).Select(h => h.HostId).Prepend(saved))
            list = list.With(PoolMember.Computer(id) with { Off = preferences.Off });
        if (!PoolSettings.SaveFor(store.DataDirectory, PoolAreas.Singing, list)) return;
        WorkSharingRoster.Forget();
        QueueSettingsSync();
        ErrorLog.Info($"Pools: the Singing list was made from Companion › Singing's computer: {string.Join(", ", list.Members.Select(m => m.Name))}" +
            (preferences.Off ? " (all off: singing was off)." : "."));
    }

    /// <summary>Companion › Singing's Now line (SingingNow). <paramref name="where"/>: the computers songs go to, in order (the
    /// list's members that are on; before the list, the computer Martlet sings on). <paramref name="listed"/>: the list exists.</summary>
    internal static string SingingNow(SingingPreferences preferences, bool active, IReadOnlyList<string> where, bool listed, bool readySomewhere)
    {
        var stays = readySomewhere ? " Singing stays set up on your computers." : "";
        if (listed && where.Count == 0 && !DesktopSongSource.Fixture)
            return $"Off. {OptionalExtras.OffMeans(CompanionTab.Singing)}: no computer in the list is on." + stays;
        if (!listed && preferences.Off) return $"Off. {OptionalExtras.OffMeans(CompanionTab.Singing)}." + stays;
        if (!active) return "Not set up yet. Add one of your computers to the list below and set Singing up there.";
        var place = DesktopSongSource.Fixture ? "this PC (FIXTURE - NOT AI)" : where.Count switch
        {
            0 => "this PC",
            1 => where[0],
            _ => $"{where[0]} (or the first free one of {where.Count} computers)"
        };
        return $"Martlet sings on {place} with {(preferences.VoiceMatch == SongVoiceMatch.VevoSing ? "VevoSing" : "SoulX-Singer")}, " +
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
        var key = host is null || host.HostId == thisPc?.HostId ? SingingThisPc : host.HostId;
        var failures = vevosing ? singingVevoFailures : singingFailures;
        void Failed(string why) => failures[key] = why;
        failures.Remove(key);
        singingPendingHost = key;
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
