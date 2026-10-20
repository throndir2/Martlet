using System.IO;
using System.Text.Json;
using Martlet.Core.Contracts;
using Martlet.Core.Settings;

namespace Martlet.Desktop;

/// <summary>The services MainWindow gives Companion › Prompts (<see cref="PromptsPage"/> in Pages\Prompts), and the save the page
/// starts: it writes the newest saved settings.</summary>
public partial class MainWindow : IPromptsPageServices
{
    private PromptsPage? promptsPage;

    private PromptsPage PromptsPage => promptsPage ??= new(this);

    PromptSettings? IPromptsPageServices.Saved => homeSettings?.Prompts;
    void IPromptsPageServices.UseAutoSave(AutoSave autoSave) => tabAutoSave = autoSave;
    void IPromptsPageServices.MarkEdited() => tabEdited = true;
    Task<bool> IPromptsPageServices.SaveAsync(IReadOnlyDictionary<string, string> texts, Action<PromptSettings?> saved) =>
        SavePromptsAsync(texts, saved);

    /// <summary>The Prompts page's auto-save: validates the edits and writes them into the newest saved settings, after any change
    /// still saving. Returns false to be tried again shortly when another window saved the settings meanwhile; a prompt that
    /// can't be saved says why.</summary>
    private async Task<bool> SavePromptsAsync(IReadOnlyDictionary<string, string> texts, Action<PromptSettings?> saved)
    {
        PromptSettings? prompts;
        try
        {
            prompts = PromptSettings.Normalize(texts.ToDictionary(b => b.Key, b => b.Value.Replace("\r\n", "\n", StringComparison.Ordinal),
                StringComparer.Ordinal));
            prompts?.Validate();
        }
        catch (ContractException error)
        {
            ActionText.Text = "Prompts not saved yet: " + error.Message;
            return true;
        }
        if (store is null || setupService is null) return true;
        // Closing has begun: the change is not saved, so it still counts as waiting.
        if (closing) return false;
        ChangeTurns.Turn? turn = null;
        var token = lifetime.Token;
        try
        {
            turn = await ChangeTurnAsync();
            var loaded = await setupService.LoadAsync(token);
            if (loaded.Error is not null) throw new InvalidOperationException(loaded.Error.Summary);
            var updated = SetupSettings.Begin(loaded.Settings) with { Prompts = prompts };
            updated.Validate();
            var result = await setupService.SaveAsync(updated, loaded.Revision, token);
            if (!result.Save.Saved)
            {
                if (result.Save.Error?.Code == ErrorCode.SettingsConflict) return false;
                throw new InvalidOperationException(result.Summary);
            }
            homeSettings = updated;
            saved(prompts);
            ActionText.Text = "Prompts saved; reload an open conversation to use them.";
        }
        catch (OperationCanceledException) { }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException or ContractException or JsonException)
        {
            ActionText.Text = "Prompts not saved: " + error.Message;
        }
        finally
        {
            turn?.Dispose();
            // The page stays as typed (tabEdited); only Home and the other summaries refresh.
            if (!closing) RenderHome();
        }
        return true;
    }
}
