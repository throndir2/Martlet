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

/// <summary>More of MainWindow.SensePools.cs, in a file of its own so that each file stays under 20 KB.</summary>
public partial class MainWindow
{
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
        var list = WorkSharingRoster.Shared.Pool(store.DataDirectory, area) ?? new PoolList { Area = area.Id };
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
        InputHint.SetText(baseUrl, "https://llm.example.com/v1");
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
            InputHint.SetText(model, p.DefaultModel ?? "gemma4:e4b");
            var url = Url();
            var elsewhere = SavedKeyWhere(url);
            keyStatus.Text = thinking is { RouteType: SetupRouteType.ChatCompletions, CredentialId: not null } && thinking.Origin == url
                ? "Leave this empty to use Thinking's key for the same provider, or paste another key."
                : elsewhere is not null ? $"Your {p.Name} key from {elsewhere} is saved: leave this empty to use it here too, or paste a new key."
                : p.NeedsKey ? $"Paste your {p.Name} API key (Martlet saves it in Windows Credential Manager on this PC)."
                : "Add a key only if your server needs one.";
            InputHint.SetText(key, p.BaseUrl == ChatCompletionsEndpointCatalog.NvidiaBuildBaseUrl ? "Paste your NVIDIA Build key (nvapi-…)"
                : p.BaseUrl == ChatCompletionsEndpointCatalog.OpenRouterBaseUrl ? "Paste your OpenRouter key (sk-or-v1-…)"
                : p.NeedsKey && p != CustomCloud && !OnThisPc(url) ? $"Paste your {p.Name} API key"
                : "Paste the server's key, if it needs one");
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
        panel.Children.Add(HelpTip.Explain($"Choose a model that {(kind == SenseKind.Image ? "sees pictures" : "hears recordings")}. It describes them " +
            "in words for Thinking and never answers you itself. It goes at the end of the list; move it up with Up.", new Thickness(0, 0, 0, 8),
            "SenseCloud-" + options.Area.Id, "the cloud form", wiki: "Thinking#image-and-audio-models"));
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
            var thinkingKey = thinking is { RouteType: SetupRouteType.ChatCompletions, CredentialId: not null } && thinking.Origin.TrimEnd('/') == origin;
            string? borrowedFrom = null;
            if (entered.Length > 0)
            {
                using var lease = TakeKey(keyBox);
                await ReplaceSenseKeyAsync(area, member, origin, lease);
            }
            else if (PoolKeys.Load(store.DataDirectory).For(area.Id, member.Key) is null && !thinkingKey &&
                SavedKeyWhere(origin) is { } from && await ReadSavedKeyAsync(origin) is { } copy)
            {
                // A key for the same provider saved anywhere else in Martlet is copied, so it is never asked for twice.
                using (copy) await ReplaceSenseKeyAsync(area, member, origin, copy);
                borrowedFrom = from;
            }
            else if (provider.NeedsKey && PoolKeys.Load(store.DataDirectory).For(area.Id, member.Key) is null && !thinkingKey)
                throw new ContractException(ErrorCode.InvalidContract, $"Paste your {provider.Name} API key first.");
            tabEdited = false;
            senseCloudShown.Remove(kind);
            var list = WorkSharingRoster.Shared.Pool(store.DataDirectory, area) ?? new PoolList { Area = area.Id };
            SavePoolList(options, list, list.With(member), $"{provider.Name} ({model}) is now in the {area.Title} list" +
                (borrowedFrom is not null ? $", with your {provider.Name} key from {borrowedFrom}."
                    : entered.Length > 0 ? ", and its API key is saved in Windows Credential Manager." : "."));
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
            throw new InvalidOperationException("Couldn't save which key the model uses; check access to Martlet's data folder.");
        }
        if (old is { } gone && !next.Keys.Values.Contains(gone)) await Task.Run(() => vault.Delete(KeyPlace(origin).Binding(profile, gone)), CancellationToken.None);
    }
}
