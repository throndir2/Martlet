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
/// Companion › Singing (an optional extra): Martlet writes a song from lyrics and a style and sings it with the voice it speaks
/// with, on the <c>singing</c> host role. A song has two parts, each with its own pool list (the shared list control,
/// docs/SINGING.md#the-two-singing-pools): the music (<see cref="PoolAreas.Singing"/>: ACE-Step 1.5 writes it with its own singer;
/// with none on, Martlet doesn't sing) and the voice match (<see cref="PoolAreas.VoiceMatch"/>: SoulX-Singer-SVC or VevoSing
/// matches the singing to the voice; with none on, songs keep the music model's own singer), which may be other computers. In
/// the standard order: Now; the two lists, where each member's Settings say where Singing stands there (with the voice matches
/// read from its singing service through the gateway), set the role up after a confirmation naming the downloads, licences and
/// terms, and choose that member's own quality (music: High quality by default, or Fast) or model (voice match: SoulX-Singer
/// by default, or VevoSing, added with its own confirmation); what Singing is (the role's facts); then the voice match models
/// compared. Songs are only ever performed by Martlet in conversation (through <see cref="SongMaker"/>), never played from a
/// button here. Automation IDs: <c>SingingNow</c>, the lists' <c>Pool-singing-...</c> and <c>Pool-voice-match-...</c> (and per
/// member <c>Pool-&lt;area&gt;-State-&lt;key&gt;</c>, <c>Pool-&lt;area&gt;-SetUp-&lt;key&gt;</c>,
/// <c>Pool-singing-Quality-&lt;key&gt;</c>, <c>Pool-voice-match-Model-&lt;key&gt;</c> and
/// <c>Pool-voice-match-AddVevo-&lt;key&gt;</c>), <c>Picker-Singing-Role</c>, <c>SingingGpu</c> and
/// <c>Picker-SingingVoiceMatch-&lt;SoulX|VevoSing&gt;</c>.
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

    /// <summary>What a Set up adds on a computer: the Singing role, VevoSing, or ACE-Step 1.5 XL.</summary>
    internal enum SingingAddition { Role, VevoSing, Xl }

    private string? singingPendingHost;
    private bool singingPendingVevo;
    private bool singingPendingXl;
    private readonly Dictionary<string, string> singingXlFailures = new(StringComparer.Ordinal);
    /// <summary>Why the last setup failed, by computer (a host ID, or <see cref="SingingThisPc"/>): Singing, and VevoSing.</summary>
    private readonly Dictionary<string, string> singingFailures = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> singingVevoFailures = new(StringComparer.Ordinal);
    /// <summary>What each computer's singing service last reported through its gateway (the voice matches set up there), by
    /// host ID; null when it couldn't be read. Read when the page shows a computer that sings, and again after a setup.</summary>
    private readonly Dictionary<string, SongClient.SingingService?> singingServices = new(StringComparer.Ordinal);
    private readonly HashSet<string> singingReads = new(StringComparer.Ordinal);


    /// <summary>The song maker Martlet's conversation uses: <see cref="SongClient"/> on the singing pools, or the FIXTURE - NOT AI
    /// maker when MARTLET_SINGING_FIXTURE is 1; null before Martlet has its data directory.</summary>
    internal ISongMaker? SongMaker => store is null ? null : SongClient.For(store.DataDirectory);

    /// <summary>Companion › Singing in the standard order: Now (whether Martlet sings, where and with what), the music list and
    /// the voice match list (the shared list control; each member's Settings say where Singing stands there, set it up, and
    /// choose its own quality or model), what Singing is (the role's facts), then the voice match models compared.</summary>
    private void RenderSingingTab(Panel page)
    {
        var preferences = store is null ? new SingingPreferences() : SingingPreferences.Load(store.DataDirectory);
        EnsureSingingPool(preferences);
        EnsureVoiceMatchPool(preferences);
        var thisPc = ThisPcHost();
        var listed = store is not null && WorkSharingRoster.Shared.Pool(store.DataDirectory, PoolAreas.Singing) is not null;
        var members = store is null ? [] : SongClient.Members(store.DataDirectory);
        var matchOff = store is not null && SongClient.VoiceMatchOff(store.DataDirectory);
        var matchers = store is null || matchOff ? [] : SongClient.Members(store.DataDirectory, PoolAreas.VoiceMatch);
        var active = store is not null && SongClient.IsSetUp(store.DataDirectory);
        var own = NetworkMap.Hosts(Inputs()).Where(h => !h.Shared).ToArray();
        var readySomewhere = own.Any(h => Offers(h, HostRoles.Singing)) || thisPc is not null && Offers(thisPc, HostRoles.Singing);
        // The voice matches set up on each member that sings, read once from its singing service.
        var singers = members.Concat(matchers).Select(m => m.Host).OfType<PairedHost>().DistinctBy(h => h.HostId).Where(h => Offers(h, HostRoles.Singing)).ToArray();
        foreach (var host in singers.Where(h => !singingServices.ContainsKey(h.HostId))) ReadSingingServiceAsync(host).Forget();
        string Name(string hostId) => hostId == thisPc?.HostId ? "this PC" : hostId;
        string Place(PoolMember member, PairedHost? host, PoolArea area) => host is null ? SingingCloud.Describe(member, area) : Name(host.HostId);
        IReadOnlyList<string> where = listed ? [.. members.Select(m => Place(m.Member, m.Host, PoolAreas.Singing)).Distinct()]
            : preferences.Host is { } saved ? [Name(saved)] : [];
        IReadOnlyList<string> matchWhere = [.. matchers.Select(m => Place(m.Member, m.Host, PoolAreas.VoiceMatch)).Distinct()];
        var quality = members.Count > 0 ? SingingPool.Quality(members[0].Member) : SongQuality.HighQuality;
        var model = matchers.Count == 0 ? SongVoiceMatch.SoulX : matchers[0].Host is null ? SongVoiceMatch.ElevenLabs : SingingPool.VoiceMatch(matchers[0].Member);

        // ---------- Now ----------
        var nowText = new TextBlock { Text = SingingNow(preferences, active, where, listed, readySomewhere, matchWhere, matchOff, quality, model),
            FontSize = 15, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 6) };
        AutomationProperties.SetAutomationId(nowText, "SingingNow");
        var now = new List<UIElement> { Heading("Now"), nowText,
            Note("Ask Martlet to sing you a song. It writes the song in the background while you keep talking, then sings it with " +
                "the voice it speaks with.", new Thickness(0, 0, 0, 0)) };
        if (active && store is not null && SongClient.SpeakingVoiceId(store.DataDirectory) is null && !DesktopSongSource.Fixture)
            now.Add(Warning("There's no voice to sing with yet. Choose a voice on Companion › Voice."));
        page.Children.Add(Card([.. now]));

        // ---------- the two pools: the music, then the voice match ----------
        page.Children.Add(PoolListCard(SingingListOptions(PoolAreas.Singing, "Music: where Martlet writes songs",
            "The computers that write a song's music with ACE-Step 1.5 and its own singer, in order. A song goes to the first one " +
            "that isn't making another song; when all are busy it waits on the one with the shortest line. With none on, Martlet " +
            "doesn't sing. Open a computer's Settings to set Singing up there or choose its quality (High quality by default).")));
        page.Children.Add(PoolListCard(SingingListOptions(PoolAreas.VoiceMatch, "Voice match: where Martlet matches the singing to its voice",
            "The computers that match a song's singing to the voice Martlet speaks with, in order. They can be other computers " +
            "than the ones that write the music. With none on, songs keep the music model's own singer. Open a computer's " +
            "Settings to set Singing up there, choose its model (SoulX-Singer by default) or add VevoSing.")));

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

        // ---------- the voice match models compared; each computer in the voice match list chooses its own ----------
        page.Children.Add(Card(Heading("Voice match models"),
            Note("Each computer in the voice match list uses SoulX-Singer unless its Settings choose VevoSing. \"In use\" is the " +
                "first computer's model.", new Thickness(0, 0, 0, 4)),
            OptionPickerBody("SingingVoiceMatch", OptionalExtras.VoiceMatches(model))));
    }

    /// <summary>The list options of one of Singing's pools: the members' lines and Settings, which computers can be added, and
    /// a computer added that doesn't sing yet showing its Settings (where Set up is).</summary>
    private PoolListOptions SingingListOptions(PoolArea area, string heading, string intro) => new()
    {
        Area = area,
        Heading = heading,
        Intro = intro,
        Status = member => member.Kind == PoolMemberKind.Cloud ? SingingCloudStatus(area, member) : SingingMemberStatus(area, member),
        Settings = member => member.Kind == PoolMemberKind.Cloud ? SingingCloudSettings(area, member) : SingingMemberSettings(area, member),
        CanAdd = host => !host.Shared && HostCan(host.HostId, HostRoles.Singing) is not { Allowed: false },
        AddCloud = () => SingingCloudAddForm(area),
        Saved = (before, after) =>
        {
            foreach (var added in after.Members.Where(m => before.Find(m.Key) is null))
                if (SingingMemberHost(added) is not { } host || !Offers(host, HostRoles.Singing))
                    poolExpanded.Add(area.Id + " " + added.Key);
            return Task.CompletedTask;
        }
    };

    /// <summary>The paired computer a singing pool member sings on: this PC's own host service for this PC, else the computer
    /// (a card's computer); null when it isn't paired here (or this PC has no host service yet).</summary>
    private PairedHost? SingingMemberHost(PoolMember member) =>
        member.Kind == PoolMemberKind.ThisPc ? ThisPcHost() : FindHost(member.HostId) is { Shared: false } host ? host : null;

    /// <summary>The key a setup on <paramref name="member"/> is followed by: <see cref="SingingThisPc"/> for this PC (its own host
    /// service, or none yet), else the host ID.</summary>
    private string SingingSetupKey(PoolMember member) => SingingMemberHost(member) is { } host && host.HostId != ThisPcHost()?.HostId
        ? host.HostId : member.Kind == PoolMemberKind.ThisPc || member.HostId == ThisPcHost()?.HostId ? SingingThisPc : member.HostId ?? SingingThisPc;

    /// <summary>A member's line in one of Singing's lists: whether Singing runs there (and its voice matches), is being set up,
    /// failed, or why it can't sing; then its quality (music) or its model (voice match).</summary>
    private string? SingingMemberStatus(PoolArea area, PoolMember member)
    {
        var key = SingingSetupKey(member);
        if (singingPendingHost == key) return singingPendingVevo ? "adding VevoSing..." : singingPendingXl ? "adding ACE-Step 1.5 XL..." : "setting Singing up...";
        var host = SingingMemberHost(member);
        var parts = new List<string>();
        SongClient.SingingService? service = null;
        if (host is not null && Offers(host, HostRoles.Singing))
        {
            singingServices.TryGetValue(host.HostId, out service);
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
        if (area == PoolAreas.VoiceMatch)
        {
            var model = SingingPool.VoiceMatch(member);
            parts.Add(model == SongVoiceMatch.VevoSing && service is not null && !service.Has(SongVoiceMatch.VevoSing)
                ? "VevoSing chosen, but it isn't set up here: SoulX-Singer until you add it" : SingingPool.Name(model));
        }
        else
        {
            var model = SingingPool.MusicModel(member);
            var music = model == SongMusicModel.AceStep15Xl
                ? service is not null && !service.Has(SongMusicModel.AceStep15Xl)
                    ? "ACE-Step 1.5 XL chosen, but it isn't set up here: ACE-Step 1.5 until you add it" : "ACE-Step 1.5 XL"
                : SingingPool.Quality(member) == SongQuality.Fast ? "fast" : "high quality";
            parts.Add(service is { Kinds.Count: > 0 } && !service.Singer.Splits ? music + "; makes whole songs until Martlet is updated there" : music);
        }
        return string.Join("; ", parts);
    }

    /// <summary>A member's Settings in one of Singing's lists (automation IDs <c>Pool-&lt;area&gt;-&lt;part&gt;-&lt;member key&gt;</c>):
    /// where Singing stands there (State), Set up Singing here (SetUp) where it doesn't run yet, then for the music its own
    /// quality (Quality: High quality, the default, or Fast), and for the voice match Add VevoSing here (AddVevo) where it runs
    /// without VevoSing and its own model (Model: SoulX-Singer, the default, or VevoSing).</summary>
    private UIElement? SingingMemberSettings(PoolArea area, PoolMember member)
    {
        var id = $"Pool-{area.Id}-";
        var host = SingingMemberHost(member);
        var thisPc = ThisPcHost();
        var onThisPc = member.Kind == PoolMemberKind.ThisPc || host is not null && host.HostId == thisPc?.HostId;
        var key = SingingSetupKey(member);
        var pending = singingPendingHost == key;
        var ready = host is not null && Offers(host, HostRoles.Singing);
        SongClient.SingingService? service = null;
        var known = ready && singingServices.TryGetValue(host!.HostId, out service);
        var vevo = service?.Has(SongVoiceMatch.VevoSing) == true;
        var xl = service?.Has(SongMusicModel.AceStep15Xl) == true;
        var match = area == PoolAreas.VoiceMatch;
        var cannot = ready ? null : onThisPc ? ThisPcCannotSing()
            : host is null ? "It isn't paired with this PC."
            : hostChecks.GetValueOrDefault(host.HostId)?.Reachable == false ? "It isn't reachable right now."
            : CannotHand(host.HostId, HostRoles.Singing, "singing");
        var state = pending ? singingPendingVevo ? "Adding VevoSing here..." : singingPendingXl ? "Adding ACE-Step 1.5 XL here..." : "Setting Singing up here..."
            : ready ? (service is null ? "Singing is ready here." : (vevo ? "Singing is ready here with SoulX-Singer and VevoSing" : "Singing is ready here with SoulX-Singer") +
                (xl ? ", and ACE-Step 1.5 XL." : "."))
            : cannot ?? (singingFailures.GetValueOrDefault(key) is { } why ? $"Setting Singing up failed: {why}" : "Singing isn't set up here yet.");
        if (ready && !pending && service is not null && !service.Singer.Splits)
            state += " Its Martlet is too old to make the music and the voice match apart: it makes whole songs until Martlet is updated there.";
        if (match && ready && !pending && singingVevoFailures.GetValueOrDefault(key) is { } vevoFailed) state += $" Adding VevoSing failed: {vevoFailed}";
        if (!match && ready && !pending && singingXlFailures.GetValueOrDefault(key) is { } xlFailed) state += $" Adding ACE-Step 1.5 XL failed: {xlFailed}";
        var panel = new StackPanel();
        var line = Note(state, new Thickness(0, 0, 0, 4));
        if (cannot is not null || !pending && (ready ? match ? singingVevoFailures.ContainsKey(key) : singingXlFailures.ContainsKey(key)
            : singingFailures.ContainsKey(key)))
            line.SetResourceReference(TextBlock.ForegroundProperty, "WarningBrush");
        AutomationProperties.SetAutomationId(line, id + "State-" + member.Key);
        panel.Children.Add(line);
        var setUpHost = onThisPc ? null : host;
        Button? action = null;
        if (!ready)
        {
            action = PageButton(pending ? "Setting up..." : "Set up Singing here", () => SetUpSingingAsync(setUpHost, SingingAddition.Role).Forget(),
                primary: true, id: id + "SetUp-" + member.Key);
            action.IsEnabled = !pending && singingPendingHost is null && cannot is null;
            if (cannot is not null)
            {
                action.ToolTip = cannot;
                ToolTipService.SetShowOnDisabled(action, true);
                AutomationProperties.SetHelpText(action, cannot);
            }
        }
        else if (match && known && !vevo)
        {
            action = PageButton(pending ? "Adding VevoSing..." : "Add VevoSing here", () => SetUpSingingAsync(setUpHost, SingingAddition.VevoSing).Forget(),
                id: id + "AddVevo-" + member.Key);
            action.IsEnabled = !pending && singingPendingHost is null;
        }
        else if (!match && known && !xl)
        {
            action = PageButton(pending ? "Adding ACE-Step 1.5 XL..." : "Add ACE-Step 1.5 XL here", () => SetUpSingingAsync(setUpHost, SingingAddition.Xl).Forget(),
                id: id + "AddXl-" + member.Key);
            action.IsEnabled = !pending && singingPendingHost is null;
        }
        if (action is not null) panel.Children.Add(Row(action));
        var choice = new ComboBox { MinHeight = 30, MinWidth = 300, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 4, 0, 0) };
        if (match)
        {
            choice.Items.Add(new ComboBoxItem { Content = "SoulX-Singer (recommended: keeps the tune and the words)", Tag = SongVoiceMatch.SoulX });
            choice.Items.Add(new ComboBoxItem { Content = "VevoSing (closer to the voice; personal, non-commercial use only)", Tag = SongVoiceMatch.VevoSing });
            choice.SelectedIndex = SingingPool.VoiceMatch(member) == SongVoiceMatch.VevoSing ? 1 : 0;
            AutomationProperties.SetName(choice, $"Voice match model on {member.Name}");
            AutomationProperties.SetAutomationId(choice, id + "Model-" + member.Key);
            choice.SelectionChanged += (_, _) =>
            {
                if ((choice.SelectedItem as ComboBoxItem)?.Tag is not SongVoiceMatch chosen || chosen == SingingPool.VoiceMatch(member)) return;
                SaveSingingMember(area, SingingPool.WithVoiceMatch(member, chosen), $"{member.Name} matches voices with {SingingPool.Name(chosen)}." +
                    (chosen == SongVoiceMatch.VevoSing && known && !vevo ? " Add VevoSing there first; until then it uses SoulX-Singer." : ""));
            };
            panel.Children.Add(Labeled("Model here", choice));
        }
        else
        {
            choice.Items.Add(new ComboBoxItem { Content = "High quality (recommended: the clearest words)", Tag = SongQuality.HighQuality });
            choice.Items.Add(new ComboBoxItem { Content = "Fast (a few seconds quicker, less clear words)", Tag = SongQuality.Fast });
            choice.SelectedIndex = SingingPool.Quality(member) == SongQuality.Fast ? 1 : 0;
            AutomationProperties.SetName(choice, $"Quality on {member.Name}");
            AutomationProperties.SetAutomationId(choice, id + "Quality-" + member.Key);
            choice.SelectionChanged += (_, _) =>
            {
                if ((choice.SelectedItem as ComboBoxItem)?.Tag is not SongQuality chosen || chosen == SingingPool.Quality(member)) return;
                SaveSingingMember(area, SingingPool.WithQuality(member, chosen),
                    $"{member.Name} writes music in {(chosen == SongQuality.HighQuality ? "high" : "fast")} quality.");
            };
            panel.Children.Add(Labeled("Quality here", choice));
            var models = new ComboBox { MinHeight = 30, MinWidth = 300, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 4, 0, 0) };
            models.Items.Add(new ComboBoxItem { Content = "ACE-Step 1.5 (recommended: fits beside the voice on a 6 GB+ card)", Tag = SongMusicModel.AceStep15 });
            models.Items.Add(new ComboBoxItem { Content = "ACE-Step 1.5 XL (the highest quality; about 20 GB more, a 12 GB+ card)", Tag = SongMusicModel.AceStep15Xl });
            models.SelectedIndex = SingingPool.MusicModel(member) == SongMusicModel.AceStep15Xl ? 1 : 0;
            AutomationProperties.SetName(models, $"Music model on {member.Name}");
            AutomationProperties.SetAutomationId(models, id + "MusicModel-" + member.Key);
            models.SelectionChanged += (_, _) =>
            {
                if ((models.SelectedItem as ComboBoxItem)?.Tag is not SongMusicModel chosen || chosen == SingingPool.MusicModel(member)) return;
                SaveSingingMember(area, SingingPool.WithMusicModel(member, chosen), $"{member.Name} writes music with {SingingPool.Name(chosen)}." +
                    (chosen == SongMusicModel.AceStep15Xl && known && !xl ? " Add ACE-Step 1.5 XL there first; until then it uses ACE-Step 1.5." : ""));
            };
            panel.Children.Add(Labeled("Music model here", models));
            if (SingingPool.MusicModel(member) == SongMusicModel.AceStep15Xl)
                panel.Children.Add(Note("ACE-Step 1.5 XL always writes in high quality (its SFT model, 50 steps).", new Thickness(0, 4, 0, 0)));
        }
        return panel;
    }

    /// <summary>Saves one member's settings in one of Singing's lists (pools.json, shared with your other computers).</summary>
    private void SaveSingingMember(PoolArea area, PoolMember member, string done)
    {
        if (store is null) return;
        var list = WorkSharingRoster.Shared.Pool(store.DataDirectory, area) ?? new PoolList { Area = area.Id };
        if (!PoolSettings.SaveFor(store.DataDirectory, area, list.With(member)))
        {
            ActionText.Text = $"Couldn't save the {area.Title} list on this PC.";
            return;
        }
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
        if (store is null || WorkSharingRoster.Shared.Pool(store.DataDirectory, PoolAreas.Singing) is not null) return;
        var own = NetworkMap.Hosts(Inputs()).Where(h => !h.Shared).ToArray();
        if (preferences.Host is not { } saved || own.All(h => h.HostId != saved)) return;
        var list = new PoolList { Area = PoolAreas.Singing.Id };
        foreach (var id in own.Where(h => h.HostId != saved && Offers(h, HostRoles.Singing)).Select(h => h.HostId).Prepend(saved))
            list = list.With(PoolMember.Computer(id) with { Off = preferences.Off });
        if (!PoolSettings.SaveFor(store.DataDirectory, PoolAreas.Singing, list)) return;
        QueueSettingsSync();
        ErrorLog.Info($"Pools: the Singing list was made from Companion › Singing's computer: {string.Join(", ", list.Members.Select(m => m.Name))}" +
            (preferences.Off ? " (all off: singing was off)." : "."));
    }

    /// <summary>Makes the voice match list once from the music list (the same computers, on and off alike, each with the voice
    /// match Companion › Singing chose before), so the computers that sang whole songs keep matching the voice. Until it is
    /// made, songs use the music list for their voice match too (<see cref="SongClient.Members"/>).</summary>
    private void EnsureVoiceMatchPool(SingingPreferences preferences)
    {
        if (store is null || WorkSharingRoster.Shared.Pool(store.DataDirectory, PoolAreas.VoiceMatch) is not null ||
            WorkSharingRoster.Shared.Pool(store.DataDirectory, PoolAreas.Singing) is not { Members.Count: > 0 } music)
            return;
        var list = new PoolList
        {
            Area = PoolAreas.VoiceMatch.Id,
            Members = [.. music.Members.Select(m => SingingPool.WithVoiceMatch(m.WithSetting(SingingPool.QualitySetting, null), preferences.OlderVoiceMatch))]
        };
        if (!PoolSettings.SaveFor(store.DataDirectory, PoolAreas.VoiceMatch, list)) return;
        QueueSettingsSync();
        ErrorLog.Info($"Pools: the Voice match list was made from the Singing list: {string.Join(", ", list.Members.Select(m => m.Name))} " +
            $"({SingingPool.Name(preferences.OlderVoiceMatch)}).");
    }

    /// <summary>Companion › Singing's Now line (SingingNow). <paramref name="where"/>: the computers that write the music, in
    /// order (the list's members that are on; before the list, the computer Martlet sings on). <paramref name="listed"/>: the
    /// list exists. <paramref name="matchWhere"/>: the computers that match the voice; <paramref name="matchOff"/>: the voice match
    /// list has nothing on. <paramref name="quality"/> and <paramref name="model"/>: the first computer's in each list.</summary>
    internal static string SingingNow(SingingPreferences preferences, bool active, IReadOnlyList<string> where, bool listed, bool readySomewhere,
        IReadOnlyList<string>? matchWhere = null, bool matchOff = false, SongQuality quality = SongQuality.HighQuality,
        SongVoiceMatch model = SongVoiceMatch.SoulX)
    {
        var stays = readySomewhere ? " Singing stays set up on your computers." : "";
        if (listed && where.Count == 0 && !DesktopSongSource.Fixture)
            return $"Off. {OptionalExtras.OffMeans(CompanionTab.Singing)}: no computer in the music list is on." + stays;
        if (!listed && preferences.Off) return $"Off. {OptionalExtras.OffMeans(CompanionTab.Singing)}." + stays;
        if (!active) return "Not set up yet. Add one of your computers to the music list below and set Singing up there, or add a cloud provider.";
        static string Place(IReadOnlyList<string> computers) => computers.Count switch
        {
            0 => "this PC",
            1 => computers[0],
            _ => $"{computers[0]} (or the first free one of {computers.Count} computers)"
        };
        var music = DesktopSongSource.Fixture ? "this PC (FIXTURE - NOT AI)" : Place(where);
        var written = $"Martlet writes the music on {music} in {(quality == SongQuality.Fast ? "fast" : "high")} quality";
        if (matchOff) return written + ". Voice match is off: songs keep the music model's own singer.";
        var matched = DesktopSongSource.Fixture ? music : Place(matchWhere is { Count: > 0 } ? matchWhere : where);
        return $"{written}, and matches the voice on {matched} with {SingingPool.Name(model)}.";
    }

    /// <summary>Saves a change to singing.json and shows <paramref name="done"/>; the next song uses it. A change of the main
    /// choice (<paramref name="choice"/>) shows the option in use again.</summary>
    private void SaveSinging(Func<SingingPreferences, SingingPreferences> change, string? done, bool choice = true)
    {
        if (store is null) return;
        try
        {
            var next = change(SingingPreferences.Load(store.DataDirectory));
            next.Save(store.DataDirectory);
            ErrorLog.Info($"Singing: {(next.Off ? "off" : "on")}, host {next.Host ?? "none"}.");
            if (done is not null) ActionText.Text = done;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            ActionText.Text = $"Couldn't save the singing choices: {error.Message}";
            return;
        }
        if (choice) ForgetPicker("Singing");
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

    /// <summary>What adding ACE-Step 1.5 XL downloads and agrees to (role.conf's XL terms in short).</summary>
    internal const string XlTerms =
        "ACE-Step 1.5 XL downloads about 20 GB more: its SFT model (huggingface.co/ACE-Step/acestep-v15-xl-sft, MIT). It is ACE-Step's " +
        "largest music model: the clearest singing and music, but slower and it needs a 12 GB+ NVIDIA graphics card (16 GB+ is better " +
        "beside the voice). ACE-Step 1.5 stays set up, and each computer's Music model switches between them.";

    /// <summary>Sets the singing role up on <paramref name="host"/> (null: this PC), or adds VevoSing or ACE-Step 1.5 XL where it
    /// runs, after one confirmation naming the downloads, licences and terms, through the same martlet-host add as every role. A
    /// plain Set up installs ACE-Step 1.5 and SoulX-Singer only; an addition keeps what the computer already has. Progress shows in
    /// the run window; the card says setting up, ready (with what is set up there) or failed and why.</summary>
    private async Task SetUpSingingAsync(PairedHost? host, SingingAddition addition)
    {
        if (store is null || setupService is null || closing) return;
        // The Singing card follows one setup at a time (what it shows while setting up); everything else goes ahead meanwhile.
        if (singingPendingHost is not null)
        {
            ActionText.Text = $"Singing is still being set up on {(singingPendingHost == SingingThisPc ? "this PC" : singingPendingHost)}. " +
                "Its window shows progress; set up the next one when it's done.";
            return;
        }
        var vevosing = addition == SingingAddition.VevoSing;
        var xl = addition == SingingAddition.Xl;
        var where = host is null ? "this PC" : host.HostId;
        if (host is null && ThisPcCannotSing() is { } cannot) { ActionText.Text = $"Singing can't run on this PC. {cannot}"; return; }
        var thisPc = ThisPcHost();
        var confirmed = addition switch
        {
            SingingAddition.VevoSing => ConfirmationDialog.Confirm(this,
                $"Add VevoSing to Singing on {where}?\n\nVevoSing is a second voice match: a little closer to the voice and cleaner, but it " +
                "may drift off-key. SoulX-Singer stays set up, and each computer's voice match model switches between them.\n\n" + VevoSingTerms,
                "Add VevoSing"),
            SingingAddition.Xl => ConfirmationDialog.Confirm(this, $"Add ACE-Step 1.5 XL to Singing on {where}?\n\n" + XlTerms, "Add ACE-Step 1.5 XL"),
            _ => ConfirmationDialog.Confirm(this,
                $"Set up Singing on {where}?\n\n" +
                (host is null && thisPc is null
                    ? machine.DockerInstalled ? "Martlet first sets up its host service in Docker Desktop. "
                        : "Martlet first installs Docker Desktop (it asks for its own terms) and sets up its host service. "
                    : "") +
                "It needs an NVIDIA graphics card with 6 GB+ and is a large download; Martlet shows the progress. " + SingingGpuNote +
                "\n\n" + SingingTerms(vevosing: false),
                "Set up Singing")
        };
        if (!confirmed) return;
        // An addition keeps what the computer has: its VevoSing when adding XL, its XL when adding VevoSing.
        var service = (host ?? thisPc) is { } known ? singingServices.GetValueOrDefault(known.HostId) : null;
        var answers = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["choice.SINGING_MODEL"] = "ace-step-v15-soulx-svc",
            ["choice.SINGING_VOICE_MATCHES"] = vevosing || service?.Has(SongVoiceMatch.VevoSing) == true ? "soulx-vevosing" : "soulx",
            ["choice.SINGING_MUSIC_MODELS"] = xl || service?.Has(SongMusicModel.AceStep15Xl) == true ? "standard-xl" : "standard"
        };
        var key = host is null || host.HostId == thisPc?.HostId ? SingingThisPc : host.HostId;
        var failures = addition switch { SingingAddition.VevoSing => singingVevoFailures, SingingAddition.Xl => singingXlFailures, _ => singingFailures };
        void Failed(string why) => failures[key] = why;
        failures.Remove(key);
        singingPendingHost = key;
        singingPendingVevo = vevosing;
        singingPendingXl = xl;
        var what = addition switch { SingingAddition.VevoSing => "VevoSing", SingingAddition.Xl => "ACE-Step 1.5 XL", _ => "Singing" };
        RenderTab();
        if (host is not null)
        {
            try
            {
                var done = await RunHostActionAsync(host, HostRoles.Get(HostRoles.Singing).Add, answers);
                if (closing) return;
                singingServices.Remove(host.HostId);
                if (done is null) Failed($"Setting it up on {host.HostId} stopped. Its run window has details.");
                else if (await WaitForSingingAsync(host, addition, lifetime.Token) is { } why)
                    Failed($"Setup finished, but Martlet can't see {what} on {host.HostId} yet ({why}). Check it in a minute.");
                else
                {
                    // Setting the role up from the page's main choice uses it: Martlet sings there (and isn't off any more).
                    if (addition == SingingAddition.Role) SaveSinging(p => p with { Off = false, Host = host.HostId }, null);
                    ActionText.Text = addition == SingingAddition.Role ? $"Singing is ready on {host.HostId}. Ask Martlet to sing you a song."
                        : $"{what} is ready on {host.HostId}.";
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception error) when (ClusterSync.IsHostFailure(error) || error is JsonException) { Failed(error.Message); }
            finally
            {
                singingPendingHost = null;
                singingPendingVevo = false;
                singingPendingXl = false;
                if (!closing && openTab is not null) RenderTab();
            }
            return;
        }
        var dataDirectory = store.DataDirectory;
        var setup = setupService;
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
                run.Status(addition switch
                {
                    SingingAddition.VevoSing => "Adding VevoSing on this PC (about 4.5 GB)...",
                    SingingAddition.Xl => "Adding ACE-Step 1.5 XL on this PC (about 20 GB)...",
                    _ => "Installing Singing on this PC (a large download)..."
                });
                var exit = await HostLocal.EngineAsync(target, ["add", HostRoles.Singing], SetupProgress(run, what, why => stopped ??= why),
                    run.Token, answers: chosen);
                if (exit != 0)
                    throw new InvalidOperationException($"Installing {what} stopped{(stopped is null ? $" (exit {exit})" : ": " + stopped)}. The output has details.");
                run.Status($"Checking {what} on this PC...");
                singingServices.Remove(pc.HostId);
                if (await WaitForSingingAsync(pc, addition, run.Token) is { } why)
                {
                    stopped = $"Setup finished, but Martlet can't see {what} yet ({why}). Check this PC in a minute.";
                    throw new InvalidOperationException(stopped);
                }
                if (addition == SingingAddition.Role) Dispatcher.Invoke(() => SaveSinging(p => p with { Off = false, Host = pc.HostId }, null));
                return addition == SingingAddition.Role ? "Singing is ready on this PC. Ask Martlet to sing you a song." : $"{what} is ready on this PC.";
            }

            string? status;
            if (thisPc is not null)
                status = await HostRunWindow.RunAsync(this, addition == SingingAddition.Role ? "Set up Singing on this PC" : $"Add {what} on this PC",
                    run => Continue(run, thisPc)) ??
                    (stopped is null ? $"{what} wasn't set up. The run window has details." : $"{what} wasn't set up: {stopped}");
            else
            {
                (_, status) = await HostsWindow.SetUpThisPcAsync(this, new AvatarProfileStore(dataDirectory), setup,
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
            singingPendingXl = false;
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
    /// singing route and its singing service answers set up (with VevoSing or ACE-Step 1.5 XL, for that <paramref name="addition"/>).
    /// Returns null once it does, else what it saw last.</summary>
    private async Task<string?> WaitForSingingAsync(PairedHost host, SingingAddition addition, CancellationToken token)
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
                    var added = addition switch
                    {
                        SingingAddition.VevoSing => service?.Has(SongVoiceMatch.VevoSing) == true,
                        SingingAddition.Xl => service?.Has(SongMusicModel.AceStep15Xl) == true,
                        _ => true
                    };
                    if (service is { State: not "not_provisioned" } && added) return null;
                    why = service is null ? "it offers no singing route" : !added
                        ? $"its singing service doesn't list {(addition == SingingAddition.Xl ? "ACE-Step 1.5 XL" : "VevoSing")}"
                        : $"its singing service is {service.State}";
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                catch (Exception error) when (ClusterSync.IsHostFailure(error) || error is JsonException) { why = error.Message; }
            }            if (attempt >= 12) return why;
            await Task.Delay(TimeSpan.FromSeconds(5), token);
        }
    }
}
