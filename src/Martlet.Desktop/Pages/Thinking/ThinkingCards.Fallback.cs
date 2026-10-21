using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Martlet.Core.Contracts;
using Martlet.Core.Settings;
using Martlet.Presentation;
using Martlet.Providers;

namespace Martlet.Desktop;

/// <summary>Companion › Thinking › If Thinking fails (saving stays in MainWindow.Fallback.cs).</summary>
internal sealed partial class ThinkingCards
{
    /// <summary>If Thinking fails: what it is now (<c>FallbackNow</c>), then an option picker (<c>Picker-Fallback-&lt;key&gt;</c>)
    /// with Off first and every provider, each with what it costs and what leaves this PC; the shown provider's details hold
    /// its fields and Use as fallback, Off's details hold Turn off.</summary>
    internal Border Fallback()
    {
        var saved = services.Settings?.ThinkingFallback;
        var thinking = services.Settings.ThinkingRoute();
        var now = Note(MainWindow.FallbackStatus(saved, thinking), new Thickness(0, 0, 0, 8));
        AutomationProperties.SetAutomationId(now, "FallbackNow");
        var providers = MainWindow.FallbackProviders;
        var savedProvider = saved is null ? null : providers.FirstOrDefault(p => p.BaseUrl == saved.Origin) ?? MainWindow.CustomCloud;
        // Add your key (FreeKeyPrompt) for If Thinking fails: NVIDIA Build's free keys, ready for the one the owner pastes.
        if (services.FreeKeyForFallback && saved is null) services.ShowInPicker("Fallback", ChatCompletionsEndpointCatalog.NvidiaBuildId);
        // A fallback that is off runs nothing extra anyway (Thinking does), so its Off needs no facts.
        var off = PickerOption.Off("If Thinking fails, Martlet says so and you try again", saved is null,
            saved is null ? null : () => PageButton("Turn off", () => services.SaveFallback(null, "", "", new PasswordBox(), true), id: "FallbackOff"))
            with { Facts = [] };
        var options = JobOptions.Providers(providers, Martlet.Core.Planning.PlanComponent.Thinking,
                savedProvider is null ? null : JobOptions.ProviderKey(savedProvider),
                "your messages, recent conversation and any screen or camera picture of a reply Thinking couldn't give",
                ChatCompletionsEndpointCatalog.NvidiaBuildId)
            .Select(option => option with { Details = () => FallbackFields(MainWindow.FallbackProviders.First(p => JobOptions.ProviderKey(p) == option.Key), saved, thinking) })
            .Prepend(off)
            .ToList();

        return Card(
            Heading("If Thinking fails"),
            HelpTip.Explain("If Thinking fails before it says anything, Martlet asks this model instead. Screen and camera glances use " +
                "it too, so choose a model that sees images.", new Thickness(0, 0, 0, 8), "FallbackAbout", "the Thinking fallback",
                wiki: "Thinking#when-thinking-fails-or-is-slow"),
            now,
            // Off and the recommended provider; Show N more lists the others.
            services.OptionPickerBody("Fallback", options, rows: 2));
    }

    /// <summary>The fields of fallback provider <paramref name="p"/>: its address (a custom server), model, key, the consent box
    /// and Use as fallback.</summary>
    private IEnumerable<UIElement> FallbackFields(MainWindow.CloudProvider p, ThinkingFallbackSettings? saved, SetupRoute? thinking)
    {
        var baseUrl = new TextBox { MaxLength = 2048, Width = 420, HorizontalAlignment = HorizontalAlignment.Left, Text = saved?.Origin ?? "" };
        AutomationProperties.SetName(baseUrl, "Fallback API base URL");
        AutomationProperties.SetAutomationId(baseUrl, "FallbackBaseUrl");
        InputHint.SetText(baseUrl, "https://llm.example.com/v1");
        string Url() => p.BaseUrl is { Length: > 0 } fixedUrl ? fixedUrl : baseUrl.Text.Trim();
        var model = new TextBox { MaxLength = 128, Width = 420, HorizontalAlignment = HorizontalAlignment.Left,
            Text = saved is not null && saved.Origin == Url() ? saved.ModelId : p.DefaultModel ?? "" };
        AutomationProperties.SetName(model, "Fallback model ID");
        AutomationProperties.SetAutomationId(model, "FallbackModel");
        InputHint.SetText(model, p.DefaultModel is { Length: > 0 } suggested ? suggested : "llama-3.1-8b-instruct");
        var key = new PasswordBox { MaxLength = SecretLease.MaximumLength, Width = 420, HorizontalAlignment = HorizontalAlignment.Left };
        AutomationProperties.SetName(key, "Fallback API key");
        AutomationProperties.SetAutomationId(key, "FallbackKey");
        InputHint.SetText(key, services.SavedKeyWhere(Url()) is not null ? "Leave empty to use your saved key"
            : p.NeedsKey ? $"Paste your {p.Name} API key" : "Only if your server needs a key");
        if (services.FreeKeyForFallback) services.FocusFreeKey(key);
        var url = Url();
        var elsewhere = saved?.CredentialId is not null && saved.Origin == url ? null : services.SavedKeyWhere(url);
        var keyStatus = Note(saved?.CredentialId is not null && saved.Origin == url
                ? $"Its {p.Name} key is saved; leave this empty to keep it, or paste a new key."
            : thinking is { RouteType: SetupRouteType.ChatCompletions, CredentialId: not null } && thinking.Origin == url
                ? "Leave this empty to use Thinking's key for the same provider, or paste another key."
            : elsewhere is not null
                ? $"Your {p.Name} key from {elsewhere} is saved: leave this empty to use it here too, or paste a new key."
            : p.NeedsKey ? $"Paste your {p.Name} API key; Martlet saves it in Windows Credential Manager."
            : "Add a key only if your server needs one.", new Thickness(0, 4, 0, 0));
        AutomationProperties.SetAutomationId(keyStatus, "FallbackKeyStatus");
        var consent = new CheckBox { Margin = new Thickness(0, 12, 0, 8), IsChecked = saved is not null && saved.Origin == url,
            Content = new TextBlock { TextWrapping = TextWrapping.Wrap,
                Text = $"I choose {p.Name} as the fallback for Thinking: when Thinking fails, my messages, recent " +
                    "conversation and any screen or camera image of that reply go there instead, and requests may cost money." } };
        AutomationProperties.SetAutomationId(consent, "FallbackConsent");
        baseUrl.TextChanged += (_, _) => { if (!baseUrl.IsKeyboardFocusWithin) return; services.MarkEdited(); consent.IsChecked = false; };
        model.TextChanged += (_, _) => { if (!model.IsKeyboardFocusWithin) return; services.MarkEdited(); consent.IsChecked = false; };
        key.PasswordChanged += (_, _) => services.MarkEdited();

        if (p == MainWindow.CustomCloud)
        {
            yield return new Label { Content = "API base URL (without /chat/completions)", Target = baseUrl, Padding = new Thickness(0, 8, 0, 4) };
            yield return baseUrl;
        }
        yield return new Label { Content = "Fallback _model ID", Target = model, Padding = new Thickness(0, 8, 0, 4) };
        yield return model;
        yield return new Label { Content = "Fallback API _key", Target = key, Padding = new Thickness(0, 8, 0, 4) };
        yield return key;
        yield return keyStatus;
        yield return consent;
        // Like a cloud provider for Thinking, a fallback is an explicit commitment (data sent elsewhere, possible costs).
        yield return Row(PageButton("Use as fallback", () => services.SaveFallback(p, Url(), model.Text.Trim(), key, consent.IsChecked == true),
            primary: true, id: "FallbackSave"));
    }
}
