using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Martlet.Core.Cluster;
using Martlet.Core.Contracts;
using Martlet.Core.Settings;
using Martlet.Core.Singing;
using Martlet.HostClient;
using Martlet.Presentation;

namespace Martlet.Desktop;

/// <summary>Companion › Singing (an optional extra), the UI only: Now, the music and voice match lists (each member's Settings: where
/// Singing stands there, Set up, and its own quality or model), what Singing is and the voice match models compared. What each line
/// says is in <see cref="SingingPageModel"/>; what the page needs from the app is <see cref="ISingingPageServices"/>. MainWindow
/// builds the shared list control (<paramref name="poolList"/>) and the option picker (<paramref name="picker"/>) and keeps the work
/// the page shows (MainWindow.Singing.cs and MainWindow.SingingCloud.cs). MainWindow keeps one page for the window's life: it keeps
/// the cloud provider each Add form shows. The automation IDs are listed on MainWindow.Singing.cs and MainWindow.SingingCloud.cs.</summary>
internal sealed partial class SingingPage(ISingingPageServices services, Func<PoolListOptions, UIElement> poolList,
    Func<string, IReadOnlyList<PickerOption>, UIElement> picker)
{
    /// <summary>Draws Companion › Singing in the standard order into <paramref name="page"/>: Now (whether Martlet sings, where and with
    /// what), the music list and the voice match list (the shared list control; each member's Settings say where Singing stands
    /// there, set it up, and choose its own quality or model), what Singing is (the role's facts), then the voice match models
    /// compared.</summary>
    internal void Render(Panel page)
    {
        var preferences = services.DataDirectory is { } directory ? SingingPreferences.Load(directory) : new SingingPreferences();
        services.EnsurePools(preferences);
        var model = new SingingPageModel(services, preferences);
        services.ReadServices(model.Singers);

        // ---------- Now ----------
        var nowText = new TextBlock { Text = model.NowText, FontSize = 15, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 6) };
        AutomationProperties.SetAutomationId(nowText, "SingingNow");
        var now = new List<UIElement> { Heading("Now"), nowText,
            HelpTip.Explain("Ask Martlet to sing you a song. It writes the song in the background while you keep talking, then sings " +
                "it with the voice it speaks with.", new Thickness(0, 0, 0, 0), "SingingAbout", "singing", wiki: "Singing#how-a-song-is-made") };
        if (model.NoVoice) now.Add(Warning("There's no voice to sing with yet: choose one on Companion › Voice."));
        page.Children.Add(Card([.. now]));

        // ---------- the two pools: the music, then the voice match ----------
        page.Children.Add(poolList(ListOptions(model, PoolAreas.Singing, "Music: where Martlet writes songs",
            "The computers that write a song's music with ACE-Step 1.5 and its own singer, in order. A song goes to the first free " +
            "one; with none on, Martlet doesn't sing.") with { Wiki = "Singing#the-music-and-voice-match-lists" }));
        page.Children.Add(poolList(ListOptions(model, PoolAreas.VoiceMatch, "Voice match: where Martlet matches the singing to its voice",
            "The computers that match a song's singing to the voice Martlet speaks with, in order. With none on, songs keep the " +
            "music model's own singer.") with { Wiki = "Singing#the-music-and-voice-match-lists" }));

        // ---------- what Singing is: the role's facts ----------
        var role = OptionalExtras.SingingChoices(model.Active, model.ReadySomewhere).First(o => !o.IsOff) with
        {
            Details = () =>
            {
                var gpu = HelpTip.Explain(MainWindow.SingingGpuNote, new Thickness(0, 6, 0, 0), "SingingGpu", "singing on the graphics card",
                    wiki: "Singing#hardware");
                AutomationProperties.SetAutomationId(gpu, "SingingGpu");
                return [gpu];
            }
        };
        page.Children.Add(Card(Heading("What Singing is"), picker("Singing", [role])));

        // ---------- the voice match models compared; each computer in the voice match list chooses its own ----------
        page.Children.Add(Card(Heading("Voice match models"),
            HelpTip.Explain("Each computer in the voice match list uses SoulX-Singer unless its Settings choose another model. \"In use\" " +
                "is the first computer's model.", new Thickness(0, 0, 0, 4), "SingingVoiceMatchModels", "voice match models",
                wiki: "Singing#more-choices"),
            picker("SingingVoiceMatch", OptionalExtras.VoiceMatches(model.VoiceMatch))));
    }

    /// <summary>The list options of one of Singing's pools: the members' lines and Settings, which computers can be added, and
    /// a computer added that doesn't sing yet showing its Settings (where Set up is).</summary>
    private PoolListOptions ListOptions(SingingPageModel model, PoolArea area, string heading, string intro) => new()
    {
        Area = area,
        Heading = heading,
        Intro = intro,
        Status = member => member.Kind == PoolMemberKind.Cloud ? model.CloudStatus(area, member) : model.MemberStatus(area, member),
        Settings = member => member.Kind == PoolMemberKind.Cloud ? CloudSettings(area, member) : MemberSettings(model, area, member),
        CanAdd = services.CanAdd,
        AddCloud = () => CloudAddForm(area),
        Saved = (before, after) =>
        {
            foreach (var added in after.Members.Where(m => before.Find(m.Key) is null))
                if (model.MemberHost(added) is not { } host || !services.Offers(host, HostRoles.Singing))
                    services.Expand(area, added.Key);
            return Task.CompletedTask;
        }
    };

    /// <summary>A member's Settings in one of Singing's lists (automation IDs <c>Pool-&lt;area&gt;-&lt;part&gt;-&lt;member key&gt;</c>):
    /// where Singing stands there (State), Set up Singing here (SetUp) where it doesn't run yet, then for the music its own
    /// quality (Quality: High quality, the default, or Fast), and for the voice match Add VevoSing here (AddVevo) where it runs
    /// without VevoSing and its own model (Model: SoulX-Singer, the default, or VevoSing).</summary>
    private UIElement? MemberSettings(SingingPageModel model, PoolArea area, PoolMember member)
    {
        var id = $"Pool-{area.Id}-";
        var host = model.MemberHost(member);
        var thisPc = services.ThisPcHost();
        var onThisPc = member.Kind == PoolMemberKind.ThisPc || host is not null && host.HostId == thisPc?.HostId;
        var key = model.SetupKey(member);
        var pending = services.PendingHost == key;
        var ready = host is not null && services.Offers(host, HostRoles.Singing);
        SongClient.SingingService? service = null;
        var known = ready && services.TryService(host!.HostId, out service);
        var match = area == PoolAreas.VoiceMatch;
        bool Has(SingingAddOn addOn) => addOn.Music is { } m ? service?.Has(m) == true : service?.Has(addOn.Match!.Value) == true;
        var addOns = SingingModels.AddOns.Where(a => match ? a.Match is not null : a.Music is not null).ToArray();
        var cannot = ready ? null : onThisPc ? services.ThisPcCannotSing()
            : host is null ? "It isn't paired with this PC."
            : services.Reachable(host.HostId) == false ? "It isn't reachable right now."
            : services.CannotHand(host.HostId, HostRoles.Singing, "singing");
        var models = (match ? ["SoulX-Singer"] : new List<string> { "ACE-Step 1.5" }).Concat(addOns.Where(Has).Select(a => a.Name)).ToArray();
        var state = pending ? services.PendingAddOn is { } adding ? $"Adding {adding.Name} here..." : "Setting Singing up here..."
            : ready ? (service is null ? "Singing is ready here." : $"Singing is ready here with {string.Join(", ", models)}.")
            : cannot ?? (services.Failure(key) is { } why ? $"Setting Singing up failed: {why}" : "Singing isn't set up here yet.");
        if (ready && !pending && service is not null && !service.Singer.Splits)
            state = state.TrimEnd('.') + "; its Martlet is too old to make the music and the voice match apart, so it makes whole songs until Martlet is updated there.";
        var addFailed = false;
        if (ready && !pending)
            foreach (var addOn in addOns)
                if (services.AddOnFailure(key, addOn.Id) is { } failed)
                {
                    state = state.TrimEnd('.') + $"; adding {addOn.Name} failed: {failed}";
                    addFailed = true;
                }
        var panel = new StackPanel();
        var line = Note(state, new Thickness(0, 0, 0, 4));
        if (cannot is not null || !pending && (ready ? addFailed : services.Failure(key) is not null))
            line.SetResourceReference(TextBlock.ForegroundProperty, "WarningBrush");
        AutomationProperties.SetAutomationId(line, id + "State-" + member.Key);
        panel.Children.Add(line);
        var setUpHost = onThisPc ? null : host;
        var actions = new List<Button>();
        if (!ready)
        {
            var action = PageButton(pending ? "Setting up..." : "Set up Singing here", () => services.SetUp(setUpHost, null),
                primary: true, id: id + "SetUp-" + member.Key);
            action.IsEnabled = !pending && services.PendingHost is null && cannot is null;
            if (cannot is not null)
            {
                action.ToolTip = cannot;
                ToolTipService.SetShowOnDisabled(action, true);
                AutomationProperties.SetHelpText(action, cannot);
            }
            actions.Add(action);
        }
        else if (known)
            foreach (var addOn in addOns.Where(a => !Has(a)))
            {
                var addingThis = pending && services.PendingAddOn == addOn;
                var action = PageButton(addingThis ? $"Adding {addOn.Name}..." : $"Add {addOn.Name} here", () => services.SetUp(setUpHost, addOn),
                    id: id + "Add" + addOn.Button + "-" + member.Key);
                action.IsEnabled = !pending && services.PendingHost is null;
                AutomationProperties.SetHelpText(action, addOn.About);
                actions.Add(action);
            }
        if (actions.Count > 0) panel.Children.Add(Row([.. actions]));
        var choice = new ComboBox { MinHeight = 30, MinWidth = 300, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 4, 0, 0) };
        if (match)
        {
            choice.Items.Add(new ComboBoxItem { Content = "SoulX-Singer (recommended: keeps the tune and the words)", Tag = SongVoiceMatch.SoulX });
            foreach (var addOn in addOns) choice.Items.Add(new ComboBoxItem { Content = addOn.Label, Tag = addOn.Match!.Value });
            choice.SelectedIndex = Math.Max(0, Array.FindIndex(addOns, a => a.Match == SingingPool.VoiceMatch(member)) + 1);
            AutomationProperties.SetName(choice, $"Voice match model on {member.Name}");
            AutomationProperties.SetAutomationId(choice, id + "Model-" + member.Key);
            choice.SelectionChanged += (_, _) =>
            {
                if ((choice.SelectedItem as ComboBoxItem)?.Tag is not SongVoiceMatch chosen || chosen == SingingPool.VoiceMatch(member)) return;
                services.SaveMember(area, SingingPool.WithVoiceMatch(member, chosen), $"{member.Name} matches voices with {SingingPool.Name(chosen)}" +
                    (SingingModels.AddOn(chosen) is { } needed && known && !Has(needed)
                        ? $" once you add {needed.Name} there (SoulX-Singer until then)." : "."));
            };
            panel.Children.Add(MainWindow.Labeled("Model here", choice));
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
                services.SaveMember(area, SingingPool.WithQuality(member, chosen),
                    $"{member.Name} writes music in {(chosen == SongQuality.HighQuality ? "high" : "fast")} quality.");
            };
            panel.Children.Add(MainWindow.Labeled("Quality here", choice));
            var music = new ComboBox { MinHeight = 30, MinWidth = 300, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 4, 0, 0) };
            music.Items.Add(new ComboBoxItem { Content = "ACE-Step 1.5 (recommended: fits beside the voice on a 6 GB+ card)", Tag = SongMusicModel.AceStep15 });
            foreach (var addOn in addOns) music.Items.Add(new ComboBoxItem { Content = addOn.Label, Tag = addOn.Music!.Value });
            music.SelectedIndex = Math.Max(0, Array.FindIndex(addOns, a => a.Music == SingingPool.MusicModel(member)) + 1);
            AutomationProperties.SetName(music, $"Music model on {member.Name}");
            AutomationProperties.SetAutomationId(music, id + "MusicModel-" + member.Key);
            music.SelectionChanged += (_, _) =>
            {
                if ((music.SelectedItem as ComboBoxItem)?.Tag is not SongMusicModel chosen || chosen == SingingPool.MusicModel(member)) return;
                services.SaveMember(area, SingingPool.WithMusicModel(member, chosen), $"{member.Name} writes music with {SingingPool.Name(chosen)}" +
                    (SingingModels.AddOn(chosen) is { } needed && known && !Has(needed)
                        ? $" once you add {needed.Name} there (ACE-Step 1.5 until then)." : "."));
            };
            panel.Children.Add(MainWindow.Labeled("Music model here", music));
            if (SingingPool.MusicModel(member) != SongMusicModel.AceStep15)
                panel.Children.Add(Note($"{SingingPool.Name(SingingPool.MusicModel(member))} has one quality; the quality here is for ACE-Step 1.5.",
                    new Thickness(0, 4, 0, 0)));
        }
        return panel;
    }
}
