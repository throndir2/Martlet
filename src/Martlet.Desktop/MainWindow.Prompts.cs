using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Martlet.Core.Contracts;
using Martlet.Core.Settings;

namespace Martlet.Desktop;

/// <summary>The Companion page's Prompts tab: every internal prompt Martlet sends to the Thinking model (persona wrapper, styles,
/// reply length, always listening, tools, voices, lore, memory, screen and camera glances, remembering, learning names and the
/// smart home notes), each editable. Saved edits replace the built-in text everywhere it is used.</summary>
public partial class MainWindow
{
    private void RenderPromptsTab(Panel page)
    {
        var saved = homeSettings?.Prompts;
        var now = Note(DescribePrompts(saved), new Thickness(0, 2, 0, 0));
        AutomationProperties.SetAutomationId(now, "PromptsNow");
        page.Children.Add(Card(Heading("Now"), now));

        page.Children.Add(Card(Heading("How prompts work"),
            Note("Martlet builds each request from these prompts plus your persona, matching lore, remembered facts and the recent " +
                "conversation. Words in braces, such as {name}, are filled in by Martlet when the prompt is sent; keep them where you " +
                "want that text. Empty a prompt to send nothing for it. Martlet reads the answers to Remembering and Learning names, so " +
                "keep their line formats. Edits save as you type; reload an open conversation to use them.", new Thickness(0, 0, 0, 0))));

        var boxes = new Dictionary<string, TextBox>(StringComparer.Ordinal);
        var states = new List<Action>();
        // There is no Save button: edits save a moment after typing stops, into the newest saved settings.
        var autoSave = new AutoSave(() => SavePromptsFromAsync(boxes, prompts =>
        {
            saved = prompts;
            now.Text = DescribePrompts(prompts);
            foreach (var show in states) show();
        }));
        tabAutoSave = autoSave;
        foreach (var group in PromptCatalog.All.GroupBy(p => p.Group))
        {
            var children = new List<UIElement> { Heading(group.Key) };
            foreach (var prompt in group)
            {
                var title = new TextBlock { Text = prompt.Title, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 12, 0, 0), TextWrapping = TextWrapping.Wrap };
                var state = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0) };
                AutomationProperties.SetAutomationId(state, "PromptState-" + prompt.Id);
                var help = prompt.Help + (prompt.Placeholders.Count == 0 ? ""
                    : " Fills in: " + string.Join(", ", prompt.Placeholders.Select(p => "{" + p + "}")) + ".");
                var box = new TextBox
                {
                    Text = PromptSettings.Text(saved, prompt.Id), AcceptsReturn = true, AcceptsTab = false, TextWrapping = TextWrapping.Wrap,
                    MaxLength = PromptSettings.MaximumTextCharacters, MinHeight = 56, MaxHeight = 260,
                    VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Margin = new Thickness(0, 6, 0, 0)
                };
                AutomationProperties.SetAutomationId(box, "Prompt-" + prompt.Id);
                AutomationProperties.SetName(box, prompt.Title + " prompt");
                AutomationProperties.SetHelpText(box, help);
                void ShowState() => state.Text = PromptState(prompt, box.Text, saved);
                ShowState();
                states.Add(ShowState);
                box.TextChanged += (_, _) => { tabEdited = true; ShowState(); autoSave.Changed(); };
                boxes[prompt.Id] = box;
                var reset = PageButton("Use built-in text", () => box.Text = prompt.Default, link: true, id: "PromptReset-" + prompt.Id);
                children.Add(title);
                children.Add(Note(help, new Thickness(0, 2, 0, 0)));
                children.Add(state);
                children.Add(box);
                children.Add(Row(reset));
            }
            page.Children.Add(Card([.. children]));
        }

        var defaults = PageButton("Use all built-in prompts", () =>
        {
            foreach (var (id, box) in boxes) box.Text = PromptCatalog.Default(id);
            autoSave.SaveNowAsync().Forget();
        }, id: "PromptsDefaults");
        page.Children.Add(Card(Heading("Built-in prompts"),
            Note("Your edits save on their own and apply to every new reply, glance and memory check. Reload an open conversation to use them.",
                new Thickness(0, 0, 0, 4)),
            Row(defaults)));
    }

    /// <summary>A prompt's state in words, against what is saved: built in, edited, empty (sent as nothing), and whether it is
    /// still saving.</summary>
    private static string PromptState(PromptDefinition prompt, string text, PromptSettings? saved)
    {
        var state = string.IsNullOrWhiteSpace(text) ? "Empty: nothing is sent for this prompt."
            : text == prompt.Default ? "Built-in text." : "Edited.";
        return text.Replace("\r\n", "\n", StringComparison.Ordinal) == PromptSettings.Text(saved, prompt.Id) ? state : state + " Saving...";
    }

    internal static string DescribePrompts(PromptSettings? prompts)
    {
        var total = PromptCatalog.All.Count;
        if (prompts is null) return $"All {total} prompts use Martlet's built-in text.";
        var empty = prompts.Overrides.Count(o => string.IsNullOrWhiteSpace(o.Value));
        var edited = prompts.Overrides.Count - empty;
        var parts = new List<string>();
        if (edited > 0) parts.Add($"{edited} of {total} prompts edited");
        if (empty > 0) parts.Add($"{empty} emptied (not sent)");
        return string.Join(", ", parts) + ". The rest use Martlet's built-in text.";
    }

    /// <summary>The Prompts page's auto-save: validates the edits and writes them into the newest saved settings. Returns false
    /// to be tried again shortly while another change holds the settings; a prompt that can't be saved says why.</summary>
    private async Task<bool> SavePromptsFromAsync(IReadOnlyDictionary<string, TextBox> boxes, Action<PromptSettings?> saved)
    {
        PromptSettings? prompts;
        try
        {
            prompts = PromptSettings.Normalize(boxes.ToDictionary(b => b.Key, b => b.Value.Text.Replace("\r\n", "\n", StringComparison.Ordinal),
                StringComparer.Ordinal));
            prompts?.Validate();
        }
        catch (ContractException error)
        {
            ActionText.Text = "Prompts not saved yet: " + error.Message;
            return true;
        }
        if (store is null || setupService is null || closing) return true;
        if (savingTab || assigningRole || setupOperations.IsRunning) return false;
        savingTab = true;
        var token = lifetime.Token;
        try
        {
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
            ActionText.Text = "Prompts saved. Reload an open conversation to use them.";
        }
        catch (OperationCanceledException) { }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException or ContractException or JsonException)
        {
            ActionText.Text = "Prompts not saved: " + error.Message;
        }
        finally
        {
            savingTab = false;
            // The page stays as typed (tabEdited); only Home and the other summaries refresh.
            if (!closing) RenderHome();
        }
        return true;
    }
}
