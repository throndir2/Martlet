using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Martlet.Core.Settings;
using Martlet.Presentation;
using Martlet.Providers;

namespace Martlet.Desktop;

/// <summary>A cloud provider on a job page (Where it runs › A cloud provider, and Voice's cloud voices).</summary>
internal sealed partial class JobPage
{
    /// <summary>A cloud provider for a job, as an option picker (<c>Picker-Cloud&lt;page&gt;-&lt;provider&gt;</c>: "openai",
    /// "nvidia-build", "openrouter", "google-gemini", "custom"): each row says what it costs and how soon it answers; the
    /// shown provider's details hold its model, key, consent and Use button. Voice's list is the cloud voices: OpenAI's voice
    /// and ElevenLabs with a voice cloned from yours. A job with one provider (Listening) shows only its details.</summary>
    private UIElement CloudCard(CompanionTab section, HostJob job, SetupRoute? route)
    {
        var role = job.Role;
        var cloudRoute = route is not null && (route.RouteType is null or SetupRouteType.OpenAi ||
            route.RouteType == SetupRouteType.ChatCompletions && !MainWindow.IsLocalOllama(route) && !MainWindow.IsLocalServer(route)) ? route : null;
        var id = "Cloud" + section;
        IReadOnlyList<MainWindow.CloudProvider> providers = role == SetupRole.Llm ? MainWindow.ThinkingProviders : [MainWindow.OpenAiCloud];
        var saved = cloudRoute is null ? null
            : cloudRoute.RouteType == SetupRouteType.ChatCompletions ? providers.FirstOrDefault(p => p.BaseUrl == cloudRoute.Origin) ?? MainWindow.CustomCloud
            : MainWindow.OpenAiCloud;
        // Add your key (FreeKeyPrompt): NVIDIA Build's free keys, ready for the one the owner pastes.
        if (services.FocusFreeKey && role == SetupRole.Llm && cloudRoute is null)
            services.ShowPickerOption(id, ChatCompletionsEndpointCatalog.NvidiaBuildId);

        if (role == SetupRole.Tts)
        {
            var voices = JobOptions.CloudVoices(saved is not null, route?.RouteType == SetupRouteType.ElevenLabs)
                .Select(option => option with
                {
                    Details = option.Key == SpeechEngines.ElevenLabs.Key ? () => services.ElevenLabsFields(route)
                        : () => CloudFields(section, job, cloudRoute, MainWindow.OpenAiCloud)
                }).ToList();
            return services.OptionPicker(id, "Cloud voice", voices);
        }
        var options = JobOptions.Providers(providers, role == SetupRole.Llm ? Martlet.Core.Planning.PlanComponent.Thinking : Martlet.Core.Planning.PlanComponent.Listening,
                saved is null ? null : JobOptions.ProviderKey(saved), MainWindow.Lowered(job.Sent), role == SetupRole.Llm ? ChatCompletionsEndpointCatalog.NvidiaBuildId : null)
            .Select(option => option with { Details = () => CloudFields(section, job, cloudRoute, providers.First(p => JobOptions.ProviderKey(p) == option.Key)) })
            .ToList();
        return services.OptionPicker(id, "Cloud provider", options, rows: 3);
    }

    /// <summary>The fields of cloud provider <paramref name="p"/> for a job: its address (a custom server), model (and voice),
    /// key, the consent box and its Use button. The automation IDs are the page's (<c>SetupCloudModel-&lt;page&gt;</c>,
    /// <c>SetupCloudKey-&lt;page&gt;</c>, <c>SetupCloudConsent-&lt;page&gt;</c>, <c>SetupCloudSave-&lt;page&gt;</c>...).</summary>
    private IEnumerable<UIElement> CloudFields(CompanionTab section, HostJob job, SetupRoute? cloudRoute, MainWindow.CloudProvider p)
    {
        var role = job.Role;
        var settings = services.Settings;
        var baseUrl = new TextBox { MaxLength = 2048, Width = 420, HorizontalAlignment = HorizontalAlignment.Left,
            Text = cloudRoute?.RouteType == SetupRouteType.ChatCompletions ? cloudRoute.Origin : "" };
        AutomationProperties.SetAutomationId(baseUrl, "SetupCloudBaseUrl");
        InputHint.SetText(baseUrl, "https://llm.example.com/v1");

        var model = new ComboBox { MinHeight = 30, MaxWidth = 420, MinWidth = 300, HorizontalAlignment = HorizontalAlignment.Left };
        AutomationProperties.SetName(model, "Model");
        AutomationProperties.SetAutomationId(model, "SetupCloudModel-" + section);
        var modelText = new TextBox { MaxLength = 128, Width = 420, HorizontalAlignment = HorizontalAlignment.Left };
        AutomationProperties.SetName(modelText, "Model ID");
        AutomationProperties.SetAutomationId(modelText, "SetupCloudModelId-" + section);
        InputHint.SetText(modelText, p.DefaultModel ?? "gemma4:e4b");
        var voice = new ComboBox { ItemsSource = OpenAiSpeechSynthesisCatalog.SupportedVoices, MinHeight = 30, MaxWidth = 420, MinWidth = 300,
            HorizontalAlignment = HorizontalAlignment.Left, SelectedItem = cloudRoute?.VoiceId is { } v && OpenAiSpeechSynthesisCatalog.SupportsVoice(v) ? v : OpenAiSpeechSynthesisCatalog.DefaultVoice };
        AutomationProperties.SetName(voice, "Voice");
        AutomationProperties.SetAutomationId(voice, "SetupCloudVoice");

        var key = new PasswordBox { MaxLength = SecretLease.MaximumLength, Width = 420, HorizontalAlignment = HorizontalAlignment.Left };
        AutomationProperties.SetName(key, "API key");
        AutomationProperties.SetAutomationId(key, "SetupCloudKey-" + section);
        if (services.FocusFreeKey && role == SetupRole.Llm)
        {
            services.FreeKeyFocused();
            services.FocusWhenShown(key);
        }
        var keySavedMark = new TextBlock
        {
            Text = "••••••••  Key saved (type to replace)", IsHitTestVisible = false, VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(12, 0, 0, 0), Opacity = 0.7
        };
        keySavedMark.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
        var keyField = new Grid { Width = 420, HorizontalAlignment = HorizontalAlignment.Left, Children = { key, keySavedMark } };
        var consent = new CheckBox { Margin = new Thickness(0, 8, 0, 6) };
        AutomationProperties.SetAutomationId(consent, "SetupCloudConsent-" + section);

        string? ChatUrl() => p.Chat ? p.BaseUrl is { Length: > 0 } fixedUrl ? fixedUrl : baseUrl.Text.Trim() : null;
        bool SameAsSaved() => cloudRoute is not null && (p.Chat
            ? cloudRoute.RouteType == SetupRouteType.ChatCompletions && cloudRoute.Origin == ChatUrl()
            : cloudRoute.RouteType is null or SetupRouteType.OpenAi);
        // A key this job used with the provider before, set aside when it switched away, is used again.
        bool SetAside() => !(SameAsSaved() && cloudRoute!.CredentialId is not null) &&
            SetupSettings.SetAsideCredentials(settings, role, ChatUrl()).Count > 0;
        bool HasKey() => SameAsSaved() && cloudRoute!.CredentialId is not null || SetAside();
        var defaultModel = p.Chat ? p.DefaultModel : role switch
        {
            SetupRole.Llm => OpenAiTextGenerationCatalog.DefaultModelId,
            SetupRole.Stt => OpenAiTranscriptionCatalog.DefaultModelId,
            _ => OpenAiSpeechSynthesisCatalog.DefaultModelId
        };
        IReadOnlyList<string> catalog = p.Chat ? (p.DefaultModel is { } d ? [d] : []) : role switch
        {
            SetupRole.Llm => OpenAiTextGenerationCatalog.SupportedModelIds,
            SetupRole.Stt => OpenAiTranscriptionCatalog.SupportedModelIds,
            _ => OpenAiSpeechSynthesisCatalog.SupportedModelIds
        };
        var value = SameAsSaved() ? cloudRoute!.ModelId : defaultModel ?? "";
        if (p.Chat) modelText.Text = value;
        else
        {
            model.ItemsSource = catalog;
            model.SelectedItem = catalog.Contains(value, StringComparer.Ordinal) ? value : defaultModel;
        }
        var keySaved = HasKey();
        // The saved-key mark fills the empty box, so the example shows only while no key is saved.
        var keyHint = p.BaseUrl == ChatCompletionsEndpointCatalog.NvidiaBuildBaseUrl ? "Paste your NVIDIA Build key (nvapi-…)"
            : p.BaseUrl == ChatCompletionsEndpointCatalog.OpenRouterBaseUrl ? "Paste your OpenRouter key (sk-or-v1-…)"
            : p == MainWindow.CustomCloud ? "Paste the server's key, if it needs one"
            : $"Paste your {p.Name} API key";
        void RefreshKeyMark()
        {
            using var entered = key.SecurePassword;
            keySavedMark.Visibility = keySaved && entered.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
            InputHint.SetText(key, keySaved ? null : keyHint);
        }
        RefreshKeyMark();
        var keyStatus = Note(SetAside() ? $"Your {p.Name} key from before is still saved: leave this empty to use it again, or paste a new key."
                : keySaved ? $"Your {p.Name} key is saved: leave this empty to keep it, or paste a new key."
                : p.BaseUrl == ChatCompletionsEndpointCatalog.NvidiaBuildBaseUrl
                    ? $"Paste your {p.Name} API key (it starts with nvapi-); Martlet saves it in Windows Credential Manager."
                : p.NeedsKey ? $"Paste your {p.Name} API key; Martlet saves it in Windows Credential Manager."
                : "Add a key only if your server needs one.", new Thickness(0, 4, 0, 0));
        AutomationProperties.SetAutomationId(keyStatus, "SetupCloudKeyStatus-" + section);
        var retired = SameAsSaved() && p.Chat ? ChatCompletionsEndpointCatalog.RetiredOn(p.BaseUrl, cloudRoute!.ModelId, services.SavedModelAbilities()) : null;
        // The recommended model is in the provider's summary above and prefilled; the hint adds only what it doesn't say. A
        // retired model says only that, so the line stays one sentence.
        var hint = Note((retired is not null ? $"{retired.Server} no longer supports {cloudRoute!.ModelId}, so choose another model."
            : p == MainWindow.OpenAiCloud || p.BaseUrl == ChatCompletionsEndpointCatalog.NvidiaBuildBaseUrl ? ""
            : p.BaseUrl == ChatCompletionsEndpointCatalog.OpenRouterBaseUrl ? "Any exact OpenRouter model ID works."
            : ChatCompletionsEndpointCatalog.Named(p.BaseUrl)?.Guidance is { } guidance ? guidance
            : "Use the server's HTTPS base URL and its exact model ID; for a model app on this PC (LM Studio, llama.cpp and " +
                "others), choose This PC › Model app.").Trim(), new Thickness(0, 4, 0, 0));
        AutomationProperties.SetAutomationId(hint, "SetupCloudHint-" + section);
        if (hint.Text.Length == 0) hint.Visibility = Visibility.Collapsed;
        // One sentence that names what is sent there and that requests may cost money (OpenAiSetup.Boundary says the same).
        consent.Content = new TextBlock { TextWrapping = TextWrapping.Wrap,
            Text = $"I choose {p.Name} for {job.Job}, and {MainWindow.Lowered(job.Sent)} will be sent there" +
                (FreeKeyPrompt.IsFree(p.BaseUrl) ? "." : ", where requests may cost money.") };
        consent.IsChecked = SameAsSaved() && cloudRoute!.Consent is not null;
        modelText.TextChanged += (_, _) => { if (!modelText.IsKeyboardFocusWithin) return; services.MarkEdited(); consent.IsChecked = false; };
        model.SelectionChanged += (_, _) => { services.MarkEdited(); consent.IsChecked = false; };
        baseUrl.TextChanged += (_, _) => { services.MarkEdited(); consent.IsChecked = false; keySaved = HasKey(); RefreshKeyMark(); };
        voice.SelectionChanged += (_, _) => { services.MarkEdited(); consent.IsChecked = false; };
        key.PasswordChanged += (_, _) => { services.MarkEdited(); RefreshKeyMark(); };

        if (role == SetupRole.Tts) yield return MainWindow.AbilitiesLine("openai", VoiceAbilities.OpenAiVoice, []);
        if (p == MainWindow.CustomCloud)
        {
            yield return new Label { Content = "API _base URL (without /chat/completions)", Target = baseUrl, Padding = new Thickness(0, 6, 0, 3) };
            yield return baseUrl;
        }
        Control modelControl = p.Chat ? modelText : model;
        yield return new Label { Content = "_Model", Target = modelControl, Padding = new Thickness(0, 6, 0, 3) };
        yield return modelControl;
        yield return hint;
        if (role == SetupRole.Tts)
        {
            yield return new Label { Content = "_Voice", Target = voice, Padding = new Thickness(0, 6, 0, 3) };
            yield return voice;
        }
        yield return new Label { Target = key, Padding = new Thickness(0, 6, 0, 3),
            Content = p == MainWindow.CustomCloud ? "API _key (only if the server needs one)" : $"Your {p.Name} _key" };
        yield return keyField;
        yield return keyStatus;
        if (p.BaseUrl == ChatCompletionsEndpointCatalog.NvidiaBuildBaseUrl && !keySaved)
        {
            var getKey = PageButton(FreeKeyPrompt.GetLabel, () => services.OpenKeyPage(keyStatus), link: true, id: "SetupCloudGetKey-" + section);
            getKey.HorizontalAlignment = HorizontalAlignment.Left;
            yield return getKey;
        }
        // What leaves this PC and what it costs are in the facts above (their help adds OpenAiSetup.Disclosure) and in the box.
        consent.ToolTip = OpenAiSetup.Disclosure;
        yield return consent;
        // A cloud provider is a commitment (a key, data sent elsewhere, possible costs), so it stays an explicit action named for
        // what it does rather than an automatic save.
        yield return Row(PageButton(p == MainWindow.CustomCloud ? "Use this server" : $"Use {p.Name}",
            () => services.SaveCloud(job, p, baseUrl.Text.Trim(), p.Chat ? modelText.Text.Trim() : model.SelectedItem as string ?? "",
                role == SetupRole.Tts ? voice.SelectedItem as string : null, key, consent.IsChecked == true),
            primary: true, id: "SetupCloudSave-" + section));
    }
}
