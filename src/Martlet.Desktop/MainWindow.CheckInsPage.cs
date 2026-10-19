using System.IO;
using System.Text.Json;
using Martlet.Conversation;
using Martlet.Core.Contracts;
using Martlet.Core.Settings;

namespace Martlet.Desktop;

/// <summary>The services MainWindow gives Companion › Check-ins (<see cref="CheckInsPage"/> in Pages\CheckIns). The check-in
/// runner they read and drive is in MainWindow.CheckIns.cs.</summary>
public partial class MainWindow : ICheckInsPageServices
{
    private CheckInsPage? checkInsPage;

    /// <summary>Companion › Check-ins. One page for the window's life: the runner tells it when a check-in's state changes.</summary>
    private CheckInsPage CheckInsPage => checkInsPage ??= new(this);

    CheckInSettings ICheckInsPageServices.Choices => checkInSettings;
    PromptSettings? ICheckInsPageServices.Prompts => homeSettings?.Prompts;
    string? ICheckInsPageServices.DataDirectory => store?.DataDirectory;
    bool ICheckInsPageServices.IsCompanion => Role == DeviceRole.Companion;
    bool ICheckInsPageServices.CanTalk => conversation is not null;
    DesktopThinkingPool? ICheckInsPageServices.Pool => conversation?.ThinkingPool;
    bool ICheckInsPageServices.UsesFixture => CheckInFixture() is not null;
    CheckInRunState ICheckInsPageServices.RunState(string id)
    {
        var counts = checkInCounts.GetValueOrDefault(id);
        return new(checkInRunning == id || checkInTriggeredRunning == id, checkInTriggeredRunning == id, checkInWaits.GetValueOrDefault(id),
            checkInFired.GetValueOrDefault(id), checkInRuns.GetValueOrDefault(id), counts.Runs, counts.Acted);
    }
    (string Name, CheckInRun Run)? ICheckInsPageServices.LastRun => checkInLast;

    bool ICheckInsPageServices.Closing => closing;
    void ICheckInsPageServices.ShowStatus(string text) => ActionText.Text = text;
    void ICheckInsPageServices.Redraw() => RenderTab();
    void ICheckInsPageServices.MarkEdited() => tabEdited = true;
    void ICheckInsPageServices.UseAutoSave(AutoSave autoSave) => tabAutoSave = autoSave;
    AutoSave? ICheckInsPageServices.OpenAutoSave => tabAutoSave;
    void ICheckInsPageServices.ReadThinkingPoolOnce() => conversation?.ReadThinkingPoolOnce();
    void ICheckInsPageServices.OpenThinkingPool() => OpenCompanion(CompanionTab.DeepThinking);
    void ICheckInsPageServices.LookNow()
    {
        if (checkInRunning is not null) return;
        try { LookAtCheckIns(); }
        catch (Exception error) when (error is InvalidOperationException or IOException or UnauthorizedAccessException or ContractException)
        {
            ErrorLog.Warn("Check-ins: couldn't look at the check-ins.", error);
        }
    }
    Task ICheckInsPageServices.CheckNowAsync(string id) => CheckInNowAsync(id);
    bool ICheckInsPageServices.Save(CheckInSettings next, string? said) => SaveCheckIns(next, said);
    void ICheckInsPageServices.Forget(string id)
    {
        checkInRuns.Remove(id);
        checkInCounts.Remove(id);
        checkInWaits.Remove(id);
        checkInFired.Remove(id);
    }

    /// <summary>Writes the built-in check-in prompts in <paramref name="edits"/> into the newest saved settings, so every other
    /// prompt (and an edit from Companion › Prompts) stays as saved. Returns false to be tried again shortly when another window
    /// saved the settings meanwhile.</summary>
    async Task<bool> ICheckInsPageServices.SavePromptsAsync(IReadOnlyDictionary<string, string> edits, Action saved)
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
            var all = new Dictionary<string, string>(StringComparer.Ordinal);
            if (loaded.Settings?.Prompts is { } current)
                foreach (var (id, text) in current.Overrides) all[id] = text;
            foreach (var (id, text) in edits) all[id] = text;
            var prompts = PromptSettings.Normalize(all);
            prompts?.Validate();
            var updated = SetupSettings.Begin(loaded.Settings) with { Prompts = prompts };
            updated.Validate();
            var result = await setupService.SaveAsync(updated, loaded.Revision, token);
            if (!result.Save.Saved)
            {
                if (result.Save.Error?.Code == ErrorCode.SettingsConflict) return false;
                throw new InvalidOperationException(result.Summary);
            }
            homeSettings = updated;
            saved();
            ActionText.Text = "Check-in prompts saved. Check-ins use them from their next run.";
        }
        catch (OperationCanceledException) { }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException or ContractException or JsonException)
        {
            ActionText.Text = "Check-in prompts not saved: " + error.Message;
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
