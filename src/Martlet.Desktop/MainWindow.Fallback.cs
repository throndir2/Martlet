using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Martlet.Core.Contracts;
using Martlet.Core.Settings;
using Martlet.Credentials.Windows;
using Martlet.Presentation;
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
        fallback is null ? "Off: when Thinking fails, Martlet says so and you try again."
        : fallback.Same(thinking) ? $"Same as Thinking ({fallback.ModelId}), so it adds nothing; choose another model or provider."
        : $"{LiveConversationConfiguration.FallbackName(fallback)}: {fallback.ModelId}" +
            (fallback.CredentialId is not null ? " (its own key saved)"
                : fallback.UsesThinkingKey(thinking) ? " (uses Thinking's key)" : " (no key)");

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
        string? borrowedFrom = null;
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
            var thinking = settings.ThinkingRoute();
            ThinkingFallbackSettings? chosen = null;
            if (provider is not null)
            {
                _ = ChatCompletionsSetup.BaseUri(url);
                ChatCompletionsSetup.ModelId(model);
                var keep = old?.Origin == url ? old.CredentialId : null;
                chosen = new() { Origin = url, ModelId = model, CredentialId = keep, ConfigurationRevision = Guid.NewGuid() };
                if (chosen.Same(thinking))
                    throw new ContractException(ErrorCode.InvalidContract, "That's what Thinking uses already, so choose another model or provider.");
                // No key pasted, none of its own and not Thinking's: a key for the same provider saved anywhere else is copied.
                if (key is null && keep is null && !chosen.UsesThinkingKey(thinking) && SavedKeyWhere(url) is { } from &&
                    await ReadSavedKeyAsync(url) is { } copy)
                {
                    key = copy;
                    borrowedFrom = from;
                }
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
                : $"If Thinking fails, Martlet now asks {provider!.Name} ({model})." +
                    (borrowedFrom is not null ? $" It uses your {provider.Name} key from {borrowedFrom}."
                        : key is null ? "" : " Its API key is saved in Windows Credential Manager.") + OpenConversationFollows;
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
