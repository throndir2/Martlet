using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Martlet.Conversation;
using Martlet.Core.Cluster;
using Martlet.Core.Contracts;
using Martlet.Core.Settings;
using Martlet.Credentials.Windows;
using Martlet.Presentation;
using Martlet.Providers;
using Martlet.Providers.Ollama;

namespace Martlet.Desktop;

/// <summary>Companion › Vision's and Companion › Hearing's lists of models (docs/SENSE_MODELS.md, The image and audio pools): the
/// shared pool list (<see cref="PoolListCard"/>, <c>Pool-vision-...</c> and <c>Pool-hearing-...</c>) with each member's model in
/// its settings. A member is Ollama on this PC (this-pc), one of your computers (its Thinking pool role or its Ollama), one of its
/// graphics cards, a server you run (an address) or a cloud provider with its model and key. The first one that takes the kind
/// puts what it sees or hears into words for Thinking; the next ones take a job when it is busy. Which model takes each job with
/// a picture or a recording, also with an empty list, is <see cref="SenseJobRouting"/>'s decision; the page shows it
/// (<see cref="SavedSenseDecision"/>). Pictures and recordings go to a cloud provider only with the owner's agreement, and to a
/// server outside this PC only when the owner allows it in its settings.</summary>
public partial class MainWindow
{
    /// <summary>The sense pages whose cloud form is open.</summary>
    private readonly HashSet<SenseKind> senseCloudShown = [];

    /// <summary>This PC's image and audio models as the Vision and Hearing lists say now (made from sense-models.json once).</summary>
    private SenseSetup SavedSenseSetup() => store is null ? SenseSetup.Load(null, WorkSharingRoster.Device, null, migrate: false)
        : LiveConversationController.LoadSenseSetup(store.DataDirectory, SavedModelAbilities());

    /// <summary>The choices <see cref="SenseRouting"/> reads, from the lists.</summary>
    internal SenseModels SavedSenses() => SavedSenseSetup().Senses;

    /// <summary>Which model takes <paramref name="job"/> with the saved setup (<see cref="SenseJobRouting.Decide"/>), as the
    /// conversation decides it: the pool member from the conversation's own Thinking pool (the board its jobs go to).</summary>
    internal SenseDecision SavedSenseDecision(SenseJobKind job)
    {
        var thinking = homeSettings.ThinkingRoute();
        var abilities = SavedModelAbilities();
        var senses = SavedSenses();
        conversation?.ReadThinkingPoolOnce();
        var voice = true;
        if (job == SenseJobKind.ReplyVoice)
        {
            var audio = SenseRouting.For(SenseKind.Audio, senses, thinking, abilities);
            voice = (audio.Model is { } own ? Talk.HearVoiceFor(VoiceNotes.StaysOnThisPc(own)) : Talk.HearVoiceFor(thinking)).On;
        }
        var row = SenseJobRouting.Row(job);
        return SenseDecisionFor(job, senses, thinking, abilities,
            conversation is null && row.PoolKind is not null ? ThinkingPoolSettings.Load(store?.DataDirectory) : new(), conversation?.ThinkingPool,
            row.Order.Contains(SenseTaker.ThisPc) && Martlet.Sherpa.SoundTagger.Included(), voice);
    }

    /// <summary>Which model takes <paramref name="job"/>: the reply's route from <paramref name="senses"/> and Thinking, the pool member
    /// from <paramref name="live"/> (the conversation's Thinking pool) or, without one, the first member of the saved pool that takes
    /// the job (<see cref="SensePoolUsers"/>, the job board's rules), and whether the sound tagger is here (<paramref name="thisPc"/>).</summary>
    internal static SenseDecision SenseDecisionFor(SenseJobKind job, SenseModels senses, SetupRoute? thinking, ModelAbilities? abilities,
        ThinkingPoolSettings pool, DesktopThinkingPool? live, bool thisPc, bool voiceAllowed = true)
    {
        var row = SenseJobRouting.Row(job);
        var inputs = SenseJobInputs.For(row.Kind, senses, thinking, abilities) with { ThisPc = thisPc, VoiceAllowed = voiceAllowed };
        if (row.PoolKind is { } kind)
        {
            if (live?.Find(kind, row.Needs) is { } place)
                inputs = inputs with { PoolKey = place.Id, PoolName = SenseJobInputs.Member(place.Name, place.Model) };
            else if (live is null && SensePoolUsers.First(row, SensePoolUsers.For(row.Kind, pool, abilities)) is { } user)
                inputs = inputs with
                {
                    PoolKey = user.Member.Key, PoolName = SenseJobInputs.Member(user.Member.Computer(thinking), user.Member.Separate ? user.Member.ModelId : null)
                };
        }
        return SenseJobRouting.Decide(job, inputs);
    }

    /// <summary>What happens to each picture (or recording) job while nothing in Companion › Vision's (or Hearing's) list is on, from
    /// the decisions (<see cref="SenseJobRouting.Instead"/>); null for another area.</summary>
    internal string? SenseInstead(PoolArea area) =>
        area.Id == PoolAreas.Vision.Id || area.Id == PoolAreas.Hearing.Id
            ? SenseJobRouting.Instead(SenseJobRouting.Of(area.Id == PoolAreas.Vision.Id ? SenseKind.Image : SenseKind.Audio)
                .Select(row => SavedSenseDecision(row.Job)))
            : null;

    /// <summary>The list card of <paramref name="kind"/>'s models.</summary>
    private Border SensePoolCard(SenseKind kind)
    {
        var area = SenseLists.Area(kind);
        var image = kind == SenseKind.Image;
        var inputs = SenseInputs(kind);
        var setup = SavedSenseSetup();
        var thinking = homeSettings.ThinkingRoute();
        var abilities = SavedModelAbilities();
        var keys = PoolKeys.Load(store?.DataDirectory);
        var hosts = SenseLists.ReadHosts(store?.DataDirectory);
        var chosen = setup.Senses.Place(kind);
        PoolListOptions options = null!;
        options = new PoolListOptions
        {
            Area = area,
            Heading = image ? "Image models" : "Audio models",
            Intro = image
                ? "The models that look at pictures of your screen and camera and describe them in words for Thinking, in order. The " +
                  "first one that sees takes the pictures; the next ones take a picture when it is busy or can't. Empty: the line " +
                  "below says which model takes each picture instead. A reply never waits for an image model."
                : "The models that describe recordings of your voice and of what this PC plays in words for Thinking, " +
                  "in order. The first one that hears takes the recordings; the next ones take one when it is busy or can't. Empty: " +
                  "the line below says which model takes each recording instead. A reply never waits for an audio model.",
            Wiki = "Thinking#image-and-audio-models",
            Status = member => SenseMemberStatus(kind, member, SenseLists.Model(member, area, id => hosts.GetValueOrDefault(id), keys), chosen,
                thinking, abilities, keys),
            Settings = member => SenseMemberSettings(kind, options, member, thinking, keys),
            NewMember = member => SenseNewMember(kind, member, thinking),
            AddCloud = () => SenseCloudAdd(kind, options, thinking),
            Also = () => SensePoolAlso(kind, thinking, abilities),
            Instead = () => SenseInstead(area),
            Saved = (before, after) => SenseListSavedAsync(kind, before, after)
        };
        return PoolListCard(options);
    }

    /// <summary>Under the list: the Thinking pool's models that get the pictures (or recordings) too (<see cref="SensePoolUsers"/>),
    /// each with where it runs and what it gets them for or why it gets none, and Open Thinking pool. So the page names every model
    /// that gets them, cloud providers and servers in the Thinking pool included.</summary>
    private IEnumerable<UIElement> SensePoolAlso(SenseKind kind, SetupRoute? thinking, ModelAbilities abilities)
    {
        var id = $"Pool-{SenseLists.Area(kind).Id}-Also";
        var users = SensePoolUsers.For(kind, ThinkingPoolSettings.Load(store?.DataDirectory), abilities);
        var senseModel = SenseRouting.For(kind, SavedSenses(), thinking, abilities).Described;
        var elements = new List<UIElement>
        {
            new TextBlock { Text = $"Also gets your {SenseInputs(kind)}: the Thinking pool", FontWeight = FontWeights.SemiBold,
                TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 4) }
        };
        var summary = HelpTip.Explain(SensePoolUsers.Summary(kind, users), new Thickness(0, 0, 0, 8), "SensePoolAlso-" + SenseLists.Area(kind).Id,
            "the Thinking pool's models", wiki: "Thinking#image-and-audio-models");
        AutomationProperties.SetAutomationId(summary, id + "Summary");
        elements.Add(summary);
        for (var i = 0; i < users.Count; i++)
        {
            var user = users[i];
            var line = SensePoolUsers.Line(kind, user, senseModel);
            if (user.Gets && user.Member.HostId is { } host && HostPresence.IsOffline(host)) line = line.TrimEnd('.') + "; its computer doesn't answer now.";
            var row = NetworkRowFrame();
            row.Margin = new Thickness(0, 0, 0, 8);
            row.Children.Add(NetworkRowText($"{id}-{i}", user.Name, line));
            elements.Add(row);
        }
        elements.Add(Row(PageButton("Open Thinking pool", () => OpenCompanion(CompanionTab.DeepThinking), link: true, id: id + "Open")));
        return elements;
    }

    /// <summary>A member's state on the list: its model and what it is known to do, whether it takes the {inputs} first now, and
    /// what keeps it from taking any.</summary>
    private string? SenseMemberStatus(SenseKind kind, PoolMember member, DeepThinkingSettings? place, DeepThinkingSettings? chosen,
        SetupRoute? thinking, ModelAbilities abilities, PoolKeys keys)
    {
        var area = SenseLists.Area(kind);
        var image = kind == SenseKind.Image;
        var parts = new List<string>();
        var model = SenseLists.ModelOf(member);
        if (model is null) parts.Add("no model chosen yet: open Settings");
        else if (place is null)
            parts.Add(member.OnHost && FindHost(member.HostId) is null ? $"{member.HostId} isn't paired with this PC"
                : member.Kind == PoolMemberKind.Gpu ? "no Thinking pool model can run on that card"
                : member.Kind == PoolMemberKind.Address ? "Martlet needs an https address here (http works only for this PC's own 127.0.0.1)"
                : $"{model} can't be used as it is");
        else
        {
            var said = ModelSenses.For(place, abilities).Known(kind) switch
            {
                true => image ? "sees pictures" : "hears recordings",
                false => image ? "doesn't see pictures" : "doesn't hear recordings",
                null => image ? "not known yet whether it sees" : "not known yet whether it hears"
            };
            parts.Add($"{model}: {said}");
            if (SenseRouting.IsThinking(place, thinking)) parts.Add($"it is Thinking's own model, so Thinking takes the {SenseInputs(kind)} itself");
            else if (!member.Off && chosen?.Key == place.Key) parts.Add($"in use: it takes the {SenseInputs(kind)} first");
        }
        if (member.OnHost && member.HostId is { } host && hostChecks.GetValueOrDefault(host)?.Reachable == false) parts.Add("not reachable right now");
        if (!member.Off && !SenseLists.MayReceive(member, area) && member.Kind == PoolMemberKind.Address)
            parts.Add($"gets no {SenseInputs(kind)} until you allow it in Settings");
        if (member.Kind is PoolMemberKind.Cloud or PoolMemberKind.Address && place is not null && !SenseLists.OnThisPc(place.Origin))
            parts.Add(keys.For(area.Id, member.Key) is not null ? "its own key is saved on this PC"
                : place.UsesThinkingKey(thinking) ? "uses Thinking's key" : "no key on this PC");
        return parts.Count == 0 ? null : string.Join("; ", parts);
    }

    /// <summary>A member's settings: its model (for this PC, picked from Ollama with whether it fits beside Thinking's), which
    /// engine a computer uses, a server's permission to get pictures and recordings, and a key for a server or cloud provider.</summary>
    private UIElement? SenseMemberSettings(SenseKind kind, PoolListOptions options, PoolMember member, SetupRoute? thinking, PoolKeys keys)
    {
        var area = options.Area;
        var list = WorkSharingRoster.Shared.Pool(store?.DataDirectory, area) ?? new PoolList { Area = area.Id };
        var i = list.Members.ToList().FindIndex(m => m.Key == member.Key);
        var id = $"Pool-{area.Id}";
        void Save(PoolMember next, string done) => SavePoolList(options, list, list.With(next), done);
        if (member.Kind == PoolMemberKind.ThisPc)
            return SenseLocalPanel(kind, member, thinking, model => Save(member.WithSetting(PoolSettingKeys.Model, model),
                $"This PC's {SenseWord(kind)} model is now {model} in Ollama" +
                (ollamaModels is { } known && !LocalOllama.Serves(known, model) ? $"; download {model} to use it." : ".")));
        var panel = new StackPanel();
        if (member.Kind == PoolMemberKind.Cloud)
            panel.Children.Add(Note(SenseSent(kind, new DeepThinkingSettings
            {
                Place = DeepThinkingPlace.Endpoint, Origin = SenseLists.BaseUrl(member), ModelId = member.Model
            }), new Thickness(0, 0, 0, 6)));
        ComboBox? model = null;
        ComboBox? engine = null;
        if (member.Kind != PoolMemberKind.Cloud)
        {
            // A computer's own models, as its last check saw them.
            var offered = member.HostId is { } host && hostChecks.GetValueOrDefault(host)?.Routes is { } routes
                ? routes.Where(r => r.RouteId == SelfHostSetup.OllamaRouteId || SelfHostSetup.IsDeepThinkingRoute(r.RouteId))
                    .Select(r => r.ModelId).Distinct(StringComparer.Ordinal).ToArray()
                : [];
            model = new ComboBox
            {
                IsEditable = true, Width = 360, HorizontalAlignment = HorizontalAlignment.Left, ItemsSource = offered,
                Text = member.Setting(PoolSettingKeys.Model) ?? ""
            };
            AutomationProperties.SetAutomationId(model, $"{id}-Model-{i}");
            AutomationProperties.SetName(model, $"The {SenseWord(kind)} model on {member.Name}");
            model.LostKeyboardFocus += (_, _) => tabEdited = true;
            panel.Children.Add(new Label { Content = "_Model", Target = model, Padding = new Thickness(0, 0, 0, 4) });
            panel.Children.Add(model);
            if (member.Kind == PoolMemberKind.Computer)
            {
                engine = new ComboBox { Width = 360, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 6, 0, 0) };
                engine.Items.Add(new ComboBoxItem { Content = "Its Thinking pool role (a model of its own beside Thinking's)", Tag = SenseLists.RoleEngine });
                engine.Items.Add(new ComboBoxItem { Content = "Its Ollama", Tag = SenseLists.OllamaEngine });
                engine.SelectedIndex = member.Setting(PoolSettingKeys.Engine) == SenseLists.OllamaEngine ? 1 : 0;
                AutomationProperties.SetAutomationId(engine, $"{id}-Engine-{i}");
                AutomationProperties.SetName(engine, $"Where the {SenseWord(kind)} model runs on {member.HostId}");
                panel.Children.Add(engine);
            }
            if (member.Kind == PoolMemberKind.Gpu)
                panel.Children.Add(Note($"The Thinking pool model on graphics card {member.Card} of {member.HostId} takes the {SenseInputs(kind)}.",
                    new Thickness(0, 6, 0, 0)));
        }
        CheckBox? media = null;
        if (member.Kind == PoolMemberKind.Address && !SenseLists.OnThisPc(member.Address))
        {
            media = new CheckBox
            {
                Content = new TextBlock { Text = SenseConsent(kind, member.Name, onThisPc: false), TextWrapping = TextWrapping.Wrap },
                IsChecked = member.Setting(SenseLists.MediaSetting) == SenseLists.Allowed, Margin = new Thickness(0, 8, 0, 0)
            };
            AutomationProperties.SetAutomationId(media, $"{id}-Media-{i}");
            AutomationProperties.SetName(media, $"{member.Name} may receive {SenseInputs(kind)}");
            panel.Children.Add(media);
        }
        PasswordBox? key = null;
        if (member.Kind is PoolMemberKind.Cloud or PoolMemberKind.Address)
        {
            key = new PasswordBox { MaxLength = SecretLease.MaximumLength, Width = 360, HorizontalAlignment = HorizontalAlignment.Left };
            AutomationProperties.SetAutomationId(key, $"{SenseId(kind)}Key-{i}");
            AutomationProperties.SetName(key, $"The API key for {member.Name}");
            InputHint.SetText(key, member.Kind == PoolMemberKind.Cloud ? "Paste the provider's API key" : "Paste the server's key, if it needs one");
            panel.Children.Add(new Label { Content = "API _key", Target = key, Padding = new Thickness(0, 8, 0, 4) });
            panel.Children.Add(key);
            panel.Children.Add(Note(keys.For(area.Id, member.Key) is not null ? "A key is saved on this PC; paste a new one to replace it."
                : "Paste a key only if it needs one (Martlet saves it in Windows Credential Manager on this PC).", new Thickness(0, 4, 0, 0)));
        }
        var save = PageButton("Save", () => SaveSenseMemberAsync(kind, options, member, model?.Text.Trim(),
            (engine?.SelectedItem as ComboBoxItem)?.Tag as string, media?.IsChecked, key).Forget(), primary: true, id: $"{id}-Save-{i}");
        var row = Row(save);
        row.Margin = new Thickness(0, 8, 0, 0);
        panel.Children.Add(row);
        return panel;
    }

    private static DeepThinkingSettings KeyPlace(string origin) => new() { Place = DeepThinkingPlace.Endpoint, Origin = origin, ModelId = "key" };
}
