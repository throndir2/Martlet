using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Martlet.Core.Contracts;
using Martlet.Core.Settings;
using Martlet.Credentials.Windows;
using Martlet.Providers;

namespace Martlet.Desktop;

/// <summary>Companion › Thinking › If Thinking fails: a second OpenAI-compatible endpoint and model that answers a reply (or a
/// screen or camera glance) when Thinking fails before saying anything: an error, a rate limit or no answer within 15 seconds.</summary>
public partial class MainWindow
{
    private const string OpenAiChatBaseUrl = "https://api.openai.com/v1";

    internal static IReadOnlyList<CloudProvider> FallbackProviders =>
    [
        .. ChatCompletionsEndpointCatalog.NamedEndpoints.Select(e => new CloudProvider(e.Name, e.BaseUrl, true, Martlet.Core.Planning.PlanningCatalog.SuggestedModel(e.Id) ?? e.DefaultModelId, true)),
        new("OpenAI", OpenAiChatBaseUrl, true, OpenAiTextGenerationCatalog.DefaultModelId, true),
        new("Ollama on this PC", LocalOllamaBaseUrl, true, LocalChatModels[0].Id, false),
        CustomCloud
    ];

    /// <summary>The fallback in words for its status line ("FallbackNow").</summary>
    internal static string FallbackStatus(ThinkingFallbackSettings? fallback, SetupRoute? thinking) =>
        fallback is null ? "Off. If Thinking fails, Martlet says so and you try again."
        : fallback.Same(thinking) ? $"Same as Thinking ({fallback.ModelId}), so it adds nothing. Choose another model or provider."
        : $"{LiveConversationConfiguration.FallbackName(fallback)}: {fallback.ModelId}" +
            (fallback.CredentialId is not null ? " (its own key saved)"
                : fallback.UsesThinkingKey(thinking) ? " (uses Thinking's key)" : " (no key)");

    /// <summary>If Thinking fails: what it is now (<c>FallbackNow</c>), then an option picker (<c>Picker-Fallback-&lt;key&gt;</c>)
    /// with Off first and every provider, each with what it costs and what leaves this PC; the shown provider's details hold
    /// its fields and Use as fallback, Off's details hold Turn off.</summary>
    private Border FallbackCard()
    {
        var saved = homeSettings?.ThinkingFallback;
        var thinking = homeSettings?.Setup?.Routes.FirstOrDefault(r => r.Role == SetupRole.Llm);
        var now = Note(FallbackStatus(saved, thinking), new Thickness(0, 0, 0, 8));
        AutomationProperties.SetAutomationId(now, "FallbackNow");
        var savedProvider = saved is null ? null : FallbackProviders.FirstOrDefault(p => p.BaseUrl == saved.Origin) ?? CustomCloud;
        // Add your key (FreeKeyPrompt) for If Thinking fails: NVIDIA Build's free keys, ready for the one the owner pastes.
        if (freeKeyFocus && freeKeyPreset == FreeKeyUse.Fallback && saved is null) pickerShown["Fallback"] = ChatCompletionsEndpointCatalog.NvidiaBuildId;
        // A fallback that is off runs nothing extra anyway (Thinking does), so its Off needs no facts.
        var off = PickerOption.Off("If Thinking fails, Martlet says so and you try again", saved is null,
            saved is null ? null : () => PageButton("Turn off", () => SaveFallbackAsync(null, "", "", new PasswordBox(), true).Forget(), id: "FallbackOff"))
            with { Facts = [] };
        var options = JobOptions.Providers(FallbackProviders, Martlet.Core.Planning.PlanComponent.Thinking,
                savedProvider is null ? null : JobOptions.ProviderKey(savedProvider),
                "your messages, recent conversation and any screen or camera picture of a reply Thinking couldn't give",
                ChatCompletionsEndpointCatalog.NvidiaBuildId)
            .Select(option => option with { Details = () => FallbackFields(FallbackProviders.First(p => JobOptions.ProviderKey(p) == option.Key), saved, thinking) })
            .Prepend(off)
            .ToList();

        return Card(
            Heading("If Thinking fails"),
            Note("If Thinking fails before it says anything (an error, a rate limit or no answer in 15 seconds), Martlet asks this " +
                "one instead. Screen and camera glances use it too, so choose a model that sees images.", new Thickness(0, 0, 0, 8)),
            now,
            // Off and the recommended provider; Show N more lists the others.
            OptionPickerBody("Fallback", options, rows: 2));
    }

    /// <summary>The fields of fallback provider <paramref name="p"/>: its address (a custom server), model, key, the consent box
    /// and Use as fallback.</summary>
    private IEnumerable<UIElement> FallbackFields(CloudProvider p, ThinkingFallbackSettings? saved, SetupRoute? thinking)
    {
        var baseUrl = new TextBox { MaxLength = 2048, Width = 420, HorizontalAlignment = HorizontalAlignment.Left, Text = saved?.Origin ?? "" };
        AutomationProperties.SetName(baseUrl, "Fallback API base URL");
        AutomationProperties.SetAutomationId(baseUrl, "FallbackBaseUrl");
        string Url() => p.BaseUrl is { Length: > 0 } fixedUrl ? fixedUrl : baseUrl.Text.Trim();
        var model = new TextBox { MaxLength = 128, Width = 420, HorizontalAlignment = HorizontalAlignment.Left,
            Text = saved is not null && saved.Origin == Url() ? saved.ModelId : p.DefaultModel ?? "" };
        AutomationProperties.SetName(model, "Fallback model ID");
        AutomationProperties.SetAutomationId(model, "FallbackModel");
        var key = new PasswordBox { MaxLength = SecretLease.MaximumLength, Width = 420, HorizontalAlignment = HorizontalAlignment.Left };
        AutomationProperties.SetName(key, "Fallback API key");
        AutomationProperties.SetAutomationId(key, "FallbackKey");
        if (freeKeyFocus && freeKeyPreset == FreeKeyUse.Fallback)
        {
            freeKeyFocus = false;
            FocusWhenShown(key);
        }
        var url = Url();
        var keyStatus = Note(saved?.CredentialId is not null && saved.Origin == url
                ? $"Its {p.Name} key is saved. Leave this empty to keep it, or paste a new key."
            : thinking is { RouteType: SetupRouteType.ChatCompletions, CredentialId: not null } && thinking.Origin == url
                ? "Leave this empty to use Thinking's key for the same provider, or paste another key."
            : p.NeedsKey ? $"Paste your {p.Name} API key. Martlet saves it in Windows Credential Manager."
            : "Add a key only if your server needs one.", new Thickness(0, 4, 0, 0));
        AutomationProperties.SetAutomationId(keyStatus, "FallbackKeyStatus");
        var consent = new CheckBox { Margin = new Thickness(0, 12, 0, 8), IsChecked = saved is not null && saved.Origin == url,
            Content = new TextBlock { TextWrapping = TextWrapping.Wrap,
                Text = $"I choose {p.Name} as the fallback for Thinking. When Thinking fails, your messages, recent " +
                    "conversation and any screen or camera image of that reply are sent there instead, and requests may cost money." } };
        AutomationProperties.SetAutomationId(consent, "FallbackConsent");
        baseUrl.TextChanged += (_, _) => { if (!baseUrl.IsKeyboardFocusWithin) return; tabEdited = true; consent.IsChecked = false; };
        model.TextChanged += (_, _) => { if (!model.IsKeyboardFocusWithin) return; tabEdited = true; consent.IsChecked = false; };
        key.PasswordChanged += (_, _) => tabEdited = true;

        if (p == CustomCloud)
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
        yield return Row(PageButton("Use as fallback", () => SaveFallbackAsync(p, Url(), model.Text.Trim(), key, consent.IsChecked == true).Forget(),
            primary: true, id: "FallbackSave"));
    }
    /// <summary>Saves the fallback (or turns it off when <paramref name="provider"/> is null). A new key is written to Windows
    /// Credential Manager first and removed again if the settings can't be saved; the key it replaces is removed after the save.</summary>
    private async Task SaveFallbackAsync(CloudProvider? provider, string url, string model, PasswordBox keyBox, bool consent)
    {
        if (store is null || setupService is null || closing) return;
        if (provider is not null && !consent)
        {
            ActionText.Text = $"Tick the box to confirm {provider.Name} as the fallback for Thinking, then press Use as fallback.";
            return;
        }
        ChangeTurns.Turn? turn = null;
        var token = lifetime.Token;
        var vault = new WindowsCredentialStore();
        SecretLease? key = null;
        CredentialBinding? written = null;
        try
        {
            turn = await ChangeTurnAsync();
            using (var entered = keyBox.SecurePassword)
                if (entered.Length > 0 && provider is not null) key = TakeKey(keyBox);
            var loaded = await setupService.LoadAsync(token);
            if (loaded.Error is not null) throw new InvalidOperationException(loaded.Error.Summary);
            var settings = UseModels(SetupSettings.Begin(loaded.Settings));
            var old = settings.ThinkingFallback;
            var thinking = settings.Setup?.Routes.SingleOrDefault(r => r.Role == SetupRole.Llm);
            ThinkingFallbackSettings? chosen = null;
            if (provider is not null)
            {
                _ = ChatCompletionsSetup.BaseUri(url);
                ChatCompletionsSetup.ModelId(model);
                var keep = old?.Origin == url ? old.CredentialId : null;
                chosen = new() { Origin = url, ModelId = model, CredentialId = keep, ConfigurationRevision = Guid.NewGuid() };
                if (chosen.Same(thinking))
                    throw new ContractException(ErrorCode.InvalidContract, "That's what Thinking uses already. Choose another model or provider.");
                if (key is null && provider.NeedsKey && keep is null && !chosen.UsesThinkingKey(thinking))
                    throw new ContractException(ErrorCode.InvalidContract, $"Paste your {provider.Name} API key first.");
                if (key is not null)
                {
                    chosen = chosen with { CredentialId = Guid.NewGuid() };
                    var binding = chosen.Binding(settings.Profile.Id, chosen.CredentialId!.Value);
                    var lease = key;
                    var error = await Task.Run(() => vault.Write(binding, lease), token);
                    if (error != CredentialError.None) throw new InvalidOperationException(CredentialMessages.Describe(error));
                    written = binding;
                }
            }
            var updated = settings with { ThinkingFallback = chosen };
            var result = await setupService.SaveAsync(updated, loaded.Revision, token);
            if (!result.Save.Saved) throw new InvalidOperationException(result.Summary);
            written = null;
            FollowSavedSetup(result.Save.Revision);
            if (old?.CredentialId is { } oldKey && oldKey != chosen?.CredentialId)
            {
                var oldBinding = old.Binding(settings.Profile.Id, oldKey);
                await Task.Run(() => vault.Delete(oldBinding), CancellationToken.None);
            }
            homeSettings = updated;
            ActionText.Text = chosen is null
                ? "The Thinking fallback is off."
                : $"If Thinking fails, Martlet now asks {provider!.Name} ({model}).{(key is null ? "" : " Its API key is saved in Windows Credential Manager.")}{OpenConversationFollows}";
        }
        catch (OperationCanceledException) { }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException or ContractException or JsonException)
        {
            ActionText.Text = error.Message;
        }
        finally
        {
            key?.Dispose();
            if (written is { } orphan) vault.Delete(orphan);
            turn?.Dispose();
            if (!closing)
            {
                tabEdited = false;
                RenderHome();
            }
        }
    }
}
