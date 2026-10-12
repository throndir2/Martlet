using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using Martlet.Core.Settings;

namespace Martlet.Desktop;

/// <summary>Companion › Memory › Finding by meaning (docs/SEARCH.md, "Finding by meaning"): whether Martlet uses an embedding
/// model to find memories, guide sections and reports that say the same thing in other words (on by default, with nomic-embed-text
/// in Ollama on this PC's processor), which model, why it can't be used now, and Download (after one question). The choice is the
/// same on all your computers (the meaning-search shared setting); each computer makes its own vectors.</summary>
public partial class MainWindow
{
    private TextBlock? meaningStatus;
    private Button? meaningDownload;
    private bool meaningWired;

    private Border MeaningSearchCard(MeaningSearch meaning)
    {
        if (!meaningWired)
        {
            meaningWired = true;
            meaning.Changed += () => Dispatcher.InvokeAsync(() => ShowMeaningSearch(meaning));
        }
        var choice = meaning.Choice;
        var status = Note(meaning.Status, new Thickness(0, 2, 0, 8));
        AutomationProperties.SetAutomationId(status, "MeaningSearchStatus");
        AutomationProperties.SetLiveSetting(status, AutomationLiveSetting.Polite);
        meaningStatus = status;
        var on = new CheckBox { Content = "Find memories, guide sections and reports by meaning", IsChecked = choice.On };
        AutomationProperties.SetAutomationId(on, "MeaningSearchOn");
        var model = new TextBox { Text = choice.Model, MinWidth = 220, MaxLength = 128, IsEnabled = choice.On, Margin = new Thickness(0, 10, 0, 0) };
        AutomationProperties.SetAutomationId(model, "MeaningSearchModel");
        AutomationProperties.SetName(model, "Embedding model in Ollama");
        meaningDownload = PageButton("Download", () => DownloadMeaningModelAsync(meaning).Forget(), id: "MeaningSearchDownload");
        meaningDownload.Visibility = meaning.NeedsDownload ? Visibility.Visible : Visibility.Collapsed;
        var check = PageButton("Check again", () => CheckMeaningSearchAsync(meaning).Forget(), id: "MeaningSearchCheck");
        void Save(MeaningSearchFile next)
        {
            if (next == meaning.Choice) return;
            if (!meaning.Choose(next)) ErrorLog.Warn("Couldn't save the finding by meaning choice on this PC.");
            // Your other computers take the choice on their next check (the meaning-search shared setting).
            QueueSettingsSync();
            model.IsEnabled = next.On;
            ShowMeaningSearch(meaning);
        }
        on.Checked += (_, _) => Save(meaning.Choice with { Place = MeaningSearchFile.ThisPc });
        on.Unchecked += (_, _) => Save(meaning.Choice with { Place = MeaningSearchFile.Off });
        void Commit()
        {
            var name = model.Text.Trim();
            if (name == meaning.Choice.Model) return;
            if (!MeaningSearchFile.IsModelName(name))
            {
                ShowMeaningSearch(meaning, "That isn't a model name Ollama can have, such as nomic-embed-text or embeddinggemma.");
                return;
            }
            Save(meaning.Choice with { Model = name });
        }
        model.LostKeyboardFocus += (_, _) => Commit();
        model.KeyDown += (_, e) => { if (e.Key == Key.Enter) Commit(); };
        if (!meaning.Ready && !meaning.NeedsDownload) CheckMeaningSearchAsync(meaning).Forget();
        return Card(Heading("Finding by meaning"), status, on,
            HelpTip.Explain("An embedding model turns each memory, guide section and report section into numbers that show what it " +
                "means, so Martlet also finds the ones that say the same thing in other words (\"what should I cook?\" finds \"You are " +
                "vegetarian\"). Martlet uses it only away from the start of its answers: when it remembers after a reply, when it " +
                "tidies your memories, and when it looks something up with its memory and guide search tools. It runs in Ollama on " +
                "this PC's processor, so it never takes the graphics card from the conversation's model, and nothing leaves this PC. " +
                "Without it, Martlet finds by words only. The choice is the same on all your computers.",
                new Thickness(24, 2, 0, 0), "MeaningSearch", "finding by meaning"),
            model,
            HelpTip.Explain("The embedding model in Ollama. nomic-embed-text is small and quick on a processor; embeddinggemma also " +
                "reads other languages well. Martlet asks before it downloads one.", new Thickness(0, 4, 0, 0), "MeaningSearchModelHelp",
                "the embedding model"),
            Row(meaningDownload, check));
    }

    private async Task CheckMeaningSearchAsync(MeaningSearch meaning)
    {
        try { await Task.Run(() => meaning.CheckAsync(lifetime.Token, force: true)); }
        catch (OperationCanceledException) { return; }
        if (!closing) ShowMeaningSearch(meaning);
    }

    /// <summary>Downloads the chosen embedding model into this PC's Ollama in a run window, after one question; Ollama is started
    /// when it isn't running.</summary>
    private async Task DownloadMeaningModelAsync(MeaningSearch meaning)
    {
        var name = meaning.Choice.Model;
        if (LocalOllama.Executable() is null)
        {
            ShowMeaningSearch(meaning, "Ollama isn't installed on this PC. Install it on Companion › Thinking first.");
            return;
        }
        var size = name == MeaningSearchFile.DefaultModel ? $" ({MeaningSearchFile.DefaultModelSize})" : "";
        if (!ConfirmationDialog.Confirm(this,
                $"Download {name}{size} with Ollama on this PC? Martlet then uses it to find memories, guide sections and reports by " +
                "meaning, on this PC's processor. The model's own license applies.",
                "Download embedding model", "Download", questionId: "MeaningSearchDownloadQuestion"))
            return;
        var done = await HostRunWindow.RunAsync(this, $"Download {name}", async run =>
        {
            var models = await LocalOllama.ModelsAsync(TimeSpan.FromSeconds(3), run.Token);
            if (models is null && LocalOllama.Start())
                for (var attempt = 0; models is null && attempt < 15; attempt++)
                {
                    await Task.Delay(TimeSpan.FromSeconds(1), run.Token);
                    models = await LocalOllama.ModelsAsync(TimeSpan.FromSeconds(2), run.Token);
                }
            if (models is null) throw new InvalidOperationException("Ollama didn't answer on this PC.");
            await LocalOllama.PullAsync(name, run.Status, run.Output, run.Token);
            return $"{name} is downloaded. Martlet finds by meaning with it now.";
        }, join: true);
        if (closing) return;
        await CheckMeaningSearchAsync(meaning);
        if (done is not null) SyncMeaningAfterDownload();
    }

    private void SyncMeaningAfterDownload() => conversation?.SyncMeaningMemory();

    /// <summary>Follows the state: its line, and Download only while the model isn't downloaded.</summary>
    private void ShowMeaningSearch(MeaningSearch meaning, string? note = null)
    {
        if (closing || meaningStatus is null) return;
        meaningStatus.Text = note is null ? meaning.Status : meaning.Status + " " + note;
        if (meaningDownload is not null) meaningDownload.Visibility = meaning.NeedsDownload ? Visibility.Visible : Visibility.Collapsed;
    }
}
