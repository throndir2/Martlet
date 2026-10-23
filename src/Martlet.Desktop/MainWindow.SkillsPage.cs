using System.IO;
using System.Text.Json;
using Martlet.Core.Contracts;
using Martlet.Core.Settings;

namespace Martlet.Desktop;

/// <summary>The services MainWindow gives Companion › Skills (<see cref="SkillsPage"/> in Pages\Skills), and the save the page
/// starts: it writes the newest saved settings.</summary>
public partial class MainWindow : ISkillsPageServices
{
    private SkillsPage? skillsPage;

    private SkillsPage SkillsPage => skillsPage ??= new(this);

    SkillSettings? ISkillsPageServices.Saved => homeSettings?.Skills;
    ReplyPromptRecord? ISkillsPageServices.LastReply => conversation?.LastReplyPrompt;
    void ISkillsPageServices.UseAutoSave(AutoSave autoSave) => tabAutoSave = autoSave;
    void ISkillsPageServices.MarkEdited() => tabEdited = true;
    Task<bool> ISkillsPageServices.SaveAsync(IReadOnlyList<Skill> skills, Action<SkillSettings?> saved) => SaveSkillsAsync(skills, saved);
    void ISkillsPageServices.Refresh()
    {
        tabEdited = false;
        RenderTab();
    }
    void ISkillsPageServices.ShowSkillDirectory(Func<Skill, Task<string?>> add, Func<IReadOnlyList<Skill>> current)
    {
        if (closing) return;
        new SkillDirectoryWindow(add, current) { Owner = this }.ShowDialog();
    }

    /// <summary>The Skills page's save: validates the skills and writes them into the newest saved settings. Returns false to be
    /// tried again shortly when another window saved the settings meanwhile; a skill that can't be saved says why.</summary>
    private async Task<bool> SaveSkillsAsync(IReadOnlyList<Skill> typed, Action<SkillSettings?> saved)
    {
        SkillSettings? skills;
        try
        {
            skills = SkillSettings.Normalize(typed);
            skills?.Validate();
        }
        catch (ContractException error)
        {
            ActionText.Text = "Skills not saved yet: " + error.Message;
            return true;
        }
        if (store is null || setupService is null) return true;
        if (closing) return false;
        ChangeTurns.Turn? turn = null;
        var token = lifetime.Token;
        try
        {
            turn = await ChangeTurnAsync();
            var loaded = await setupService.LoadAsync(token);
            if (loaded.Error is not null) throw new InvalidOperationException(loaded.Error.Summary);
            var updated = SetupSettings.Begin(loaded.Settings) with { Skills = skills };
            updated.Validate();
            var result = await setupService.SaveAsync(updated, loaded.Revision, token);
            if (!result.Save.Saved)
            {
                if (result.Save.Error?.Code == ErrorCode.SettingsConflict) return false;
                throw new InvalidOperationException(result.Summary);
            }
            homeSettings = updated;
            saved(skills);
            ActionText.Text = "Skills saved; reload an open conversation to use them.";
        }
        catch (OperationCanceledException) { }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException or ContractException or JsonException)
        {
            ActionText.Text = "Skills not saved: " + error.Message;
        }
        finally
        {
            turn?.Dispose();
            if (!closing) RenderHome();
        }
        return true;
    }
}
