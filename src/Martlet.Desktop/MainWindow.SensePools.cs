using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Martlet.Conversation;
using Martlet.Core.Cluster;
using Martlet.Core.Contracts;
using Martlet.Core.Settings;
using Martlet.Credentials.Windows;
using Martlet.Providers;
using Martlet.Providers.Ollama;

namespace Martlet.Desktop;

/// <summary>Companion › Vision's and Companion › Hearing's lists of models (docs/SENSE_MODELS.md, The image and audio pools): the
/// shared pool list (<see cref="PoolListCard"/>, <c>Pool-vision-...</c> and <c>Pool-hearing-...</c>) with each member's model in
/// its settings. A member is Ollama on this PC (this-pc), one of your computers (its Thinking pool role or its Ollama), one of its
/// graphics cards, a server you run (an address) or a cloud provider with its model and key. The first one that takes the kind
/// puts what it sees or hears into words for Thinking; the next ones take a job when it is busy. An empty list: the text model
/// takes the pictures or recordings itself. Pictures and recordings go to a cloud provider only with the owner's agreement, and
/// to a server outside this PC only when the owner allows it in its settings.</summary>
public partial class MainWindow
{
    /// <summary>The sense pages whose cloud form is open.</summary>
    private readonly HashSet<SenseKind> senseCloudShown = [];

    /// <summary>This PC's image and audio models as the Vision and Hearing lists say now (made from sense-models.json once).</summary>
    private SenseSetup SavedSenseSetup() => store is null ? SenseSetup.Load(null, WorkSharingRoster.Device, null, migrate: false)
        : LiveConversationController.LoadSenseSetup(store.DataDirectory, SavedModelAbilities());

    /// <summary>The choices <see cref="SenseRouting"/> reads, from the lists.</summary>
    internal SenseModels SavedSenses() => SavedSenseSetup().Senses;

    /// <summary>The list card of <paramref name="kind"/>'s models.</summary>
    private Border SensePoolCard(SenseKind kind)
    {
        var area = SenseLists.Area(kind);
        var image = kind == SenseKind.Image;
        var inputs = SenseInputs(kind);
        var hadList = store is not null && WorkSharingRoster.Pool(store.DataDirectory, area) is not null;
        var setup = SavedSenseSetup();
        // Made from sense-models.json just now: the list control reads the new file.
        if (!hadList && setup.List(kind) is not null) WorkSharingRoster.Forget();
        var thinking = homeSettings?.Setup?.Routes.FirstOrDefault(r => r.Role == SetupRole.Llm);
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
                  "first one that sees takes the pictures; the next ones take a picture when it is busy or can't. Empty: Thinking's own " +
                  "model takes the pictures itself (an omni model such as Gemma 4 E2B sees them). A reply never waits for an image model."
                : "The models that hear the recordings of your voice and of what this PC plays and describe them in words for Thinking, " +
                  "in order. The first one that hears takes the recordings; the next ones take one when it is busy or can't. Empty: " +
                  "Thinking's own model takes them itself. A reply never waits for an audio model.",
            Status = member => SenseMemberStatus(kind, member, SenseLists.Model(member, area, id => hosts.GetValueOrDefault(id), keys), chosen,
                thinking, abilities, keys),
            Settings = member => SenseMemberSettings(kind, options, member, thinking, keys),
            NewMember = member => SenseNewMember(kind, member, thinking),
            AddCloud = () => SenseCloudAdd(kind, options, thinking),
            Saved = (before, after) => SenseListSavedAsync(kind, before, after)
        };
        return PoolListCard(options);
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
            var said = image ? SenseRouting.Sees(place, abilities) switch { VisionSupport.Supported => "sees pictures", VisionSupport.Unsupported => "doesn't see pictures", _ => "not known yet whether it sees" }
                : SenseRouting.Hears(place, abilities) switch { HearingSupport.Supported => "hears recordings", HearingSupport.Unsupported => "doesn't hear recordings", _ => "not known yet whether it hears" };
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
        var list = WorkSharingRoster.Pool(store?.DataDirectory, area) ?? new PoolList { Area = area.Id };
        var i = list.Members.ToList().FindIndex(m => m.Key == member.Key);
        var id = $"Pool-{area.Id}";
        void Save(PoolMember next, string done) => SavePoolList(options, list, list.With(next), done);
        if (member.Kind == PoolMemberKind.ThisPc)
            return SenseLocalPanel(kind, member, thinking, model => Save(member.WithSetting(PoolSettingKeys.Model, model),
                $"This PC's {SenseWord(kind)} model is now {model} in Ollama." +
                (ollamaModels is { } known && !LocalOllama.Serves(known, model) ? $" Download {model} to use it." : "")));
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
            panel.Children.Add(new Label { Content = "API _key", Target = key, Padding = new Thickness(0, 8, 0, 4) });
            panel.Children.Add(key);
            panel.Children.Add(Note(keys.For(area.Id, member.Key) is not null ? "A key is saved on this PC. Paste a new one to replace it."
                : "Paste a key only if it needs one. Martlet saves it in Windows Credential Manager on this PC.", new Thickness(0, 4, 0, 0)));
        }
        var save = PageButton("Save", () => SaveSenseMemberAsync(kind, options, member, model?.Text.Trim(),
            (engine?.SelectedItem as ComboBoxItem)?.Tag as string, media?.IsChecked, key).Forget(), primary: true, id: $"{id}-Save-{i}");
        var row = Row(save);
        row.Margin = new Thickness(0, 8, 0, 0);
        panel.Children.Add(row);
        return panel;
    }

    /// <summary>Saves a member's settings: its model, a computer's engine, a server's permission and a new key.</summary>
    private async Task SaveSenseMemberAsync(SenseKind kind, PoolListOptions options, PoolMember member, string? model, string? engine,
        bool? media, PasswordBox? keyBox)
    {
        if (store is null || closing) return;
        var area = options.Area;
        var next = member;
        try
        {
            if (model is not null)
            {
                if (model.Length == 0) throw new ContractException(ErrorCode.InvalidContract, "Enter a model first.");
                ChatCompletionsSetup.ModelId(model);
                next = next.WithSetting(PoolSettingKeys.Model, model);
            }
            if (engine is not null) next = next.WithSetting(PoolSettingKeys.Engine, engine);
            if (media is not null) next = next.WithSetting(SenseLists.MediaSetting, media == true ? SenseLists.Allowed : null);
            if (keyBox is not null)
            {
                using var entered = keyBox.SecurePassword;
                if (entered.Length > 0)
                {
                    var origin = member.Kind == PoolMemberKind.Cloud ? SenseLists.BaseUrl(member) : member.Address?.TrimEnd('/');
                    using var lease = TakeKey(keyBox);
                    await ReplaceSenseKeyAsync(area, member, origin!, lease);
                }
            }
        }
        catch (Exception error) when (error is ContractException or InvalidOperationException)
        {
            ActionText.Text = error.Message;
            return;
        }
        tabEdited = false;
        var list = WorkSharingRoster.Pool(store.DataDirectory, area) ?? new PoolList { Area = area.Id };
        SavePoolList(options, list, list.With(next), $"{member.Name}'s settings for {area.Title} are saved.");
    }

    /// <summary>A member's first settings: this PC gets an Ollama model that takes the kind (not Thinking's), a computer its
    /// Thinking pool role's model (else its Ollama's), a card its Thinking pool model, as the last checks saw them. A member
    /// without a model shows its settings.</summary>
    private PoolMember SenseNewMember(SenseKind kind, PoolMember member, SetupRoute? thinking)
    {
        var area = SenseLists.Area(kind);
        var next = member;
        switch (member.Kind)
        {
            case PoolMemberKind.ThisPc:
            {
                var beside = IsLocalOllama(thinking) ? thinking!.ModelId : null;
                var abilities = SavedModelAbilities();
                var suggested = kind == SenseKind.Image ? VisionModelCatalog.LocalRecommendations.Select(m => m.Tag) : LocalChatModels.Where(m => m.Hears).Select(m => m.Id);
                var pick = (ollamaModels ?? []).FirstOrDefault(m => !OllamaSideBySide.Same(m, beside) && LocalTakes(kind, m, abilities) == true) ??
                    suggested.FirstOrDefault(m => !OllamaSideBySide.Same(m, beside));
                if (pick is not null) next = next.WithSetting(PoolSettingKeys.Model, pick);
                break;
            }
            case PoolMemberKind.Computer when FindHost(member.HostId) is { } host && hostChecks.GetValueOrDefault(host.HostId) is { Reachable: true, Routes: { } routes }:
                if (ThinkingPoolAutoJoin.Route(PoolHost(host, routes), NetworkMap.ThinkingHost(homeSettings)) is { } offer)
                    next = next.WithSetting(PoolSettingKeys.Model, offer.ModelId)
                        .WithSetting(PoolSettingKeys.Engine, SelfHostSetup.IsDeepThinkingRoute(offer.RouteId) ? SenseLists.RoleEngine : SenseLists.OllamaEngine);
                break;
            case PoolMemberKind.Gpu when member.Card is { } card and <= SelfHostSetup.DeepThinkingMaximumCards &&
                hostChecks.GetValueOrDefault(member.HostId!)?.Routes?.FirstOrDefault(r => r.RouteId == SelfHostSetup.DeepThinkingRouteIdFor(card)) is { } route:
                next = next.WithSetting(PoolSettingKeys.Model, route.ModelId);
                break;
        }
        if (SenseLists.ModelOf(next) is null || member.Kind == PoolMemberKind.Address) poolExpanded.Add(area.Id + " " + member.Key);
        return next;
    }

    /// <summary>Add a cloud provider or server: a button that shows the form (<c>Pool-vision-AddCloud</c>), and the form: provider,
    /// base URL, model, key and the owner's agreement that pictures or recordings go there.</summary>
    private UIElement SenseCloudAdd(SenseKind kind, PoolListOptions options, SetupRoute? thinking)
    {
        var area = options.Area;
        var shown = senseCloudShown.Contains(kind);
        var button = PageButton(shown ? "Hide the cloud form" : "A cloud provider or server", () =>
        {
            if (!senseCloudShown.Remove(kind)) senseCloudShown.Add(kind);
            RenderTab();
        }, id: $"Pool-{area.Id}-AddCloud");
        AutomationProperties.SetName(button, $"Add a cloud provider or server to {area.Title}");
        var stack = new StackPanel();
        var line = Row(button);
        line.Margin = new Thickness(0, 4, 0, 0);
        stack.Children.Add(line);
        if (shown) stack.Children.Add(SenseCloudForm(kind, options, thinking));
        return stack;
    }

    /// <summary>The cloud form: a cloud provider, a model app on this PC or any OpenAI-compatible server, with its own key or
    /// Thinking's for the same base URL.</summary>
    private StackPanel SenseCloudForm(SenseKind kind, PoolListOptions options, SetupRoute? thinking)
    {
        var id = SenseId(kind);
        var word = SenseWord(kind);
        // A model app on this PC is offered by name once Martlet has looked (loopback only).
        if (localServers is null && !lookingForLocalServers) LookForSenseServersAsync().Forget();
        IReadOnlyList<CloudProvider> providers =
        [
            .. DeepThinkingProviders.Where(p => p != CustomCloud),
            .. OtherLocalServers().Select(s => new CloudProvider($"{s.Name} on this PC", s.ChatCompletionsBaseUrl, true, s.Models.FirstOrDefault(), s.NeedsKey)),
            CustomCloud
        ];
        var provider = new ComboBox { ItemsSource = providers, MinHeight = 30, MaxWidth = 420, MinWidth = 300, HorizontalAlignment = HorizontalAlignment.Left };
        AutomationProperties.SetName(provider, $"The {word} model's provider");
        AutomationProperties.SetAutomationId(provider, id + "Provider");
        provider.SelectedItem = providers[0];
        var baseUrl = new TextBox { MaxLength = 256, Width = 420, HorizontalAlignment = HorizontalAlignment.Left };
        AutomationProperties.SetName(baseUrl, $"The {word} model's API base URL");
        AutomationProperties.SetAutomationId(baseUrl, id + "BaseUrl");
        var baseUrlPanel = new StackPanel
        {
            Children = { new Label { Content = "API base URL (without /chat/completions)", Target = baseUrl, Padding = new Thickness(0, 8, 0, 4) }, baseUrl }
        };
        var model = new TextBox { MaxLength = 128, Width = 420, HorizontalAlignment = HorizontalAlignment.Left };
        AutomationProperties.SetName(model, $"The {word} model's ID");
        AutomationProperties.SetAutomationId(model, id + "ModelId");
        var key = new PasswordBox { MaxLength = SecretLease.MaximumLength, Width = 420, HorizontalAlignment = HorizontalAlignment.Left };
        AutomationProperties.SetName(key, $"The {word} model's API key");
        AutomationProperties.SetAutomationId(key, id + "Key");
        var keyStatus = Note("", new Thickness(0, 4, 0, 0));
        AutomationProperties.SetAutomationId(keyStatus, id + "KeyStatus");
        var consent = new CheckBox { Margin = new Thickness(0, 12, 0, 8) };
        AutomationProperties.SetAutomationId(consent, id + "Consent");
        var consentText = new TextBlock { TextWrapping = TextWrapping.Wrap };
        consent.Content = consentText;
        CloudProvider Selected() => provider.SelectedItem as CloudProvider ?? CustomCloud;
        string Url() => Selected().BaseUrl is { Length: > 0 } fixedUrl ? fixedUrl : baseUrl.Text.Trim();
        void Refresh()
        {
            var p = Selected();
            baseUrlPanel.Visibility = p == CustomCloud ? Visibility.Visible : Visibility.Collapsed;
            model.Text = p.DefaultModel ?? "";
            var url = Url();
            keyStatus.Text = thinking is { RouteType: SetupRouteType.ChatCompletions, CredentialId: not null } && thinking.Origin == url
                ? "Leave this empty to use Thinking's key for the same provider, or paste another key."
                : p.NeedsKey ? $"Paste your {p.Name} API key. Martlet saves it in Windows Credential Manager on this PC."
                : "Add a key only if your server needs one.";
            consentText.Text = SenseConsent(kind, p.Name, url.Length > 0 && OnThisPc(url));
            consent.IsChecked = false;
        }
        Refresh();
        provider.SelectionChanged += (_, _) => { tabEdited = true; Refresh(); };
        baseUrl.TextChanged += (_, _) =>
        {
            if (!baseUrl.IsKeyboardFocusWithin) return;
            tabEdited = true;
            consent.IsChecked = false;
        };
        model.TextChanged += (_, _) => { if (!model.IsKeyboardFocusWithin) return; tabEdited = true; consent.IsChecked = false; };
        key.PasswordChanged += (_, _) => tabEdited = true;

        var panel = new StackPanel { Margin = new Thickness(0, 6, 0, 0) };
        panel.Children.Add(Note($"Choose a model that {(kind == SenseKind.Image ? "sees pictures" : "hears recordings")}. It describes them " +
            "in words for Thinking and never answers you itself. It goes at the end of the list; move it up with Up.", new Thickness(0, 0, 0, 8)));
        panel.Children.Add(new Label { Content = "_Provider", Target = provider, Padding = new Thickness(0, 0, 0, 4) });
        panel.Children.Add(provider);
        panel.Children.Add(baseUrlPanel);
        panel.Children.Add(new Label { Content = "_Model ID", Target = model, Padding = new Thickness(0, 8, 0, 4) });
        panel.Children.Add(model);
        panel.Children.Add(new Label { Content = "API _key", Target = key, Padding = new Thickness(0, 8, 0, 4) });
        panel.Children.Add(key);
        panel.Children.Add(keyStatus);
        panel.Children.Add(consent);
        panel.Children.Add(Row(PageButton("Add to the list", () => AddSenseCloudAsync(kind, options, Selected(), Url(), model.Text.Trim(), key,
            consent.IsChecked == true, thinking).Forget(), primary: true, id: id + "SaveCloud")));
        return panel;
    }

    /// <summary>Adds a cloud provider or server to the list with the owner's agreement, its key saved first (Windows Credential
    /// Manager, pool-keys.json says which), or Thinking's for the same base URL.</summary>
    private async Task AddSenseCloudAsync(SenseKind kind, PoolListOptions options, CloudProvider provider, string url, string model,
        PasswordBox keyBox, bool consent, SetupRoute? thinking)
    {
        if (store is null || closing) return;
        var area = options.Area;
        if (!consent)
        {
            ActionText.Text = $"Tick the box to confirm {provider.Name} as the {SenseWord(kind)} model, then press Add to the list.";
            return;
        }
        try
        {
            var origin = ChatCompletionsSetup.BaseUri(url).AbsoluteUri.TrimEnd('/');
            ChatCompletionsSetup.ModelId(model);
            var member = PoolMember.Cloud(SenseLists.ProviderFor(origin), model, origin).WithConsent(area.Id, DateTimeOffset.UtcNow);
            using var entered = keyBox.SecurePassword;
            if (entered.Length > 0)
            {
                using var lease = TakeKey(keyBox);
                await ReplaceSenseKeyAsync(area, member, origin, lease);
            }
            else if (provider.NeedsKey && PoolKeys.Load(store.DataDirectory).For(area.Id, member.Key) is null &&
                !(thinking is { RouteType: SetupRouteType.ChatCompletions, CredentialId: not null } && thinking.Origin.TrimEnd('/') == origin))
                throw new ContractException(ErrorCode.InvalidContract, $"Paste your {provider.Name} API key first.");
            tabEdited = false;
            senseCloudShown.Remove(kind);
            var list = WorkSharingRoster.Pool(store.DataDirectory, area) ?? new PoolList { Area = area.Id };
            SavePoolList(options, list, list.With(member), $"{provider.Name} ({model}) is now in the {area.Title} list." +
                (entered.Length > 0 ? " Its API key is saved in Windows Credential Manager." : ""));
            // Ask its server quietly what the model takes, as for a first model.
            CheckChosenModelAsync(new() { Place = DeepThinkingPlace.Endpoint, Origin = origin, ModelId = model }, thinking).Forget();
        }
        catch (Exception error) when (error is ContractException or InvalidOperationException or UriFormatException or ArgumentException)
        {
            ActionText.Text = error.Message;
        }
    }

    /// <summary>Saves <paramref name="key"/> for <paramref name="member"/> in Windows Credential Manager, bound to
    /// <paramref name="origin"/>, and in pool-keys.json; the key it replaces is removed when no other member uses it.</summary>
    private async Task ReplaceSenseKeyAsync(PoolArea area, PoolMember member, string origin, SecretLease key)
    {
        var directory = store!.DataDirectory;
        var profile = homeSettings?.Profile.Id ?? Guid.Empty;
        if (profile == Guid.Empty) throw new InvalidOperationException("Set up Martlet first, then add the key.");
        var vault = new WindowsCredentialStore();
        var id = Guid.NewGuid();
        var binding = KeyPlace(origin).Binding(profile, id);
        var error = await Task.Run(() => vault.Write(binding, key), lifetime.Token);
        if (error != CredentialError.None) throw new InvalidOperationException(CredentialMessages.Describe(error));
        var keys = PoolKeys.Load(directory);
        var old = keys.For(area.Id, member.Key);
        var next = keys.With(area.Id, member.Key, id);
        if (!next.Save(directory))
        {
            vault.Delete(binding);
            throw new InvalidOperationException("Couldn't save which key the model uses. Check access to Martlet's data folder.");
        }
        if (old is { } gone && !next.Keys.Values.Contains(gone)) await Task.Run(() => vault.Delete(KeyPlace(origin).Binding(profile, gone)), CancellationToken.None);
    }

    private static DeepThinkingSettings KeyPlace(string origin) => new() { Place = DeepThinkingPlace.Endpoint, Origin = origin, ModelId = "key" };

    /// <summary>After the list was saved: keys of members that left are removed (when no other member uses them), the running
    /// conversation follows the list, and a new first model's server is asked what it takes.</summary>
    private async Task SenseListSavedAsync(SenseKind kind, PoolList before, PoolList after)
    {
        if (store is null) return;
        var area = SenseLists.Area(kind);
        var directory = store.DataDirectory;
        var keys = PoolKeys.Load(directory);
        var gone = before.Members.Where(m => after.Find(m.Key) is null && keys.For(area.Id, m.Key) is not null).ToArray();
        if (gone.Length > 0)
        {
            var next = keys;
            foreach (var member in gone) next = next.With(area.Id, member.Key, null);
            if (next.Save(directory) && homeSettings?.Profile.Id is { } profile && profile != Guid.Empty)
            {
                var vault = new WindowsCredentialStore();
                foreach (var member in gone)
                {
                    var credential = keys.For(area.Id, member.Key)!.Value;
                    var origin = member.Kind == PoolMemberKind.Cloud ? SenseLists.BaseUrl(member) : member.Address?.TrimEnd('/');
                    if (origin is not null && !next.Keys.Values.Contains(credential))
                        await Task.Run(() => vault.Delete(KeyPlace(origin).Binding(profile, credential)), CancellationToken.None);
                }
            }
        }
        conversation?.ReloadSenseModels();
        var thinking = homeSettings?.Setup?.Routes.FirstOrDefault(r => r.Role == SetupRole.Llm);
        var setup = SavedSenseSetup();
        var route = SenseRouting.For(kind, setup.Senses, thinking, SavedModelAbilities());
        ErrorLog.Info($"{(kind == SenseKind.Image ? "Image" : "Audio")} models: {SenseNow(kind, setup.Senses, setup.For(kind).Count, thinking)} {route.Why}");
        if (setup.Senses.Place(kind) is { Place: DeepThinkingPlace.Endpoint } first) await CheckChosenModelAsync(first, thinking);
    }
}
