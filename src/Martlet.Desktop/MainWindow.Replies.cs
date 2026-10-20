using System.IO;
using System.Text.Json;
using Martlet.Core.Contracts;
using Martlet.Core.Settings;

namespace Martlet.Desktop;

/// <summary>Saving the reply settings (Companion › Replies, drawn by <see cref="RepliesPage"/> in Pages\Replies; Thinking pool
/// saves Thinking longer the same way).</summary>
public partial class MainWindow
{
    /// <summary>Saves the reply settings <paramref name="update"/> makes of the newest saved ones, after any change still saving.
    /// Returns false to be tried again shortly when another window saved the settings meanwhile.</summary>
    private async Task<bool> SaveRepliesAsync(Func<GenerationSettings?, GenerationSettings?> update, Action<GenerationSettings?> saved,
        string done)
    {
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
            var generation = update(loaded.Settings?.Generation);
            if (Equals(loaded.Settings?.Generation, generation)) return true;
            var updated = SetupSettings.Begin(loaded.Settings) with { Generation = generation };
            updated.Validate();
            var result = await setupService.SaveAsync(updated, loaded.Revision, token);
            if (!result.Save.Saved)
            {
                if (result.Save.Error?.Code == ErrorCode.SettingsConflict) return false;
                throw new InvalidOperationException(result.Summary);
            }
            homeSettings = updated;
            saved(generation);
            ActionText.Text = done;
        }
        catch (OperationCanceledException) { }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException or ContractException or JsonException)
        {
            ActionText.Text = "Reply settings not saved: " + error.Message;
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
