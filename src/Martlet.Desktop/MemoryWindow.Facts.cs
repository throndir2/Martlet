using System.ComponentModel;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Martlet.Core.Contracts;
using Martlet.Core.Settings;
using Martlet.Core.Speakers;
using Martlet.Memory;
using Microsoft.Win32;

namespace Martlet.Desktop;

/// <summary>The Memory window's facts: the people filter, the list, search, the editor (add, change, delete) and where each fact matters.</summary>
public partial class MemoryWindow
{
    /// <summary>The Show and Belongs to choices: everyone, each voice Martlet knows (yours first, then named ones), and the
    /// forgotten voices facts still belong to.</summary>
    private void RenderPeople()
    {
        roster = voices();
        var live = roster.Live.OrderByDescending(yours).ThenByDescending(v => v.Named).ThenBy(v => v.Number)
            .Select(v => new PersonOption(PersonName(v), PersonKind.Voice, v.Id)).ToArray();
        var forgotten = facts.Any(f => f.VoiceId is { } id && roster.Resolve(id) is null);
        // Keep what was shown; a voice merged meanwhile shows the voice it joined.
        var wanted = PersonFilter.SelectedItem as PersonOption;
        var wantedVoice = MemoryPeople.Canonical(wanted?.VoiceId, roster);
        var show = showPerson is { } person ? MemoryPeople.Canonical(person, roster) : null;
        PersonOption[] filters =
        [
            new("All facts", PersonKind.All), new("Everyone's (not tied to a voice)", PersonKind.Everyone), .. live,
            .. forgotten ? new[] { new PersonOption("Forgotten voices", PersonKind.Forgotten) } : Array.Empty<PersonOption>()
        ];
        rendering = true;
        PersonFilter.ItemsSource = filters;
        PersonFilter.SelectedItem = show is not null ? filters.FirstOrDefault(o => o.VoiceId == show) ?? filters[0]
            : filters.FirstOrDefault(o => o.Kind == wanted?.Kind && o.VoiceId == wantedVoice) ?? filters[0];
        rendering = false;
        showPerson = null;
        // Reading the facts again never changes whose the fact you are writing or changing is, or where it matters.
        ResetPersonChoice(editing is { } held ? facts.FirstOrDefault(f => f.Id == held.Id) : null,
            PersonChoice.SelectedItem as PersonOption);
        ResetWhereChoice(editing is { } kept ? facts.FirstOrDefault(f => f.Id == kept.Id) : null, WhereChoice.SelectedItem as WhereOption);
    }

    /// <summary>One Where it matters option: everywhere (null), any game ("gaming") or one game.</summary>
    internal sealed record WhereOption(string Label, string? Activity)
    {
        public override string ToString() => Label;
    }

    /// <summary>The Where it matters options: everywhere, any game, the game played now and every game a fact is kept for.
    /// <paramref name="keep"/> is a choice already made, kept while it is offered. A new fact is for everywhere.</summary>
    private void ResetWhereChoice(MemoryFact? fact, WhereOption? keep = null)
    {
        var options = new List<WhereOption> { new("Everywhere", null), new("While gaming (any game)", MemoryActivity.Gaming) };
        foreach (var activity in facts.Select(f => f.Activity).Append(fact?.Activity).Append(CurrentActivity.Now).Append(keep?.Activity)
                     .OfType<string>().Where(a => MemoryActivity.Name(a) is not null))
            if (!options.Any(o => MemoryActivity.Same(o.Activity, activity)))
                options.Add(new(char.ToUpperInvariant(MemoryActivity.Describe(activity)[0]) + MemoryActivity.Describe(activity)[1..], activity));
        var wanted = keep?.Activity ?? (keep is null ? fact?.Activity : null);
        WhereChoice.ItemsSource = options;
        WhereChoice.SelectedItem = options.FirstOrDefault(o => MemoryActivity.Same(o.Activity, wanted)) ?? options[0];
    }

    private string? SelectedWhere() => (WhereChoice.SelectedItem as WhereOption)?.Activity;

    /// <summary>The Belongs to options; a fact whose voice was forgotten keeps that voice as an option. <paramref name="keep"/> is
    /// a choice already made, kept while it is still offered (a voice merged meanwhile becomes the voice it joined).</summary>
    private void ResetPersonChoice(MemoryFact? fact, PersonOption? keep = null)
    {
        var options = new List<PersonOption> { new(EveryoneLabel, PersonKind.Everyone) };
        options.AddRange(((IEnumerable<PersonOption>?)PersonFilter.ItemsSource ?? []).Where(o => o.Kind == PersonKind.Voice));
        if (fact?.VoiceId is { } id && roster.Resolve(id) is null)
            options.Add(new("A forgotten voice", PersonKind.Forgotten, id));
        PersonChoice.ItemsSource = options;
        var voice = keep is not null
                ? options.Any(o => o.VoiceId == keep.VoiceId) ? keep.VoiceId : MemoryPeople.Canonical(keep.VoiceId, roster)
            : fact is not null ? MemoryPeople.Canonical(fact.VoiceId, roster) is { } canonical && roster.Resolve(canonical) is not null
                ? canonical : fact.VoiceId
            // A new fact belongs to the voice shown, else to yours: you typed it.
            : PersonFilter.SelectedItem is PersonOption { Kind: PersonKind.Voice } shown ? shown.VoiceId
            : roster.Live.FirstOrDefault(yours)?.Id;
        PersonChoice.SelectedItem = options.FirstOrDefault(o => o.VoiceId == voice) ?? options[0];
    }

    private void RenderFacts()
    {
        var filter = PersonFilter.SelectedItem as PersonOption;
        var words = (SearchBox?.Text ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        var history = ShowHistoryChoice?.IsChecked == true;
        var shown = facts.Where(fact => (history || !fact.IsHistory) && filter?.Kind switch
            {
                PersonKind.Everyone => fact.VoiceId is null,
                PersonKind.Voice => MemoryPeople.Canonical(fact.VoiceId, roster) == filter.VoiceId,
                PersonKind.Forgotten => fact.VoiceId is { } id && roster.Resolve(id) is null,
                _ => true
            })
            .Select(fact => new FactItem(fact, PersonName(fact.VoiceId), SpaceOf(fact), SpaceName(fact)))
            // A word finds a fact by its text, whose it is or its hidden search words.
            .Where(item => words.All(word => item.Fact.Content.Contains(word, StringComparison.CurrentCultureIgnoreCase) ||
                item.Fact.SearchWords?.Contains(word, StringComparison.CurrentCultureIgnoreCase) == true ||
                (item.Person ?? EveryoneLabel).Contains(word, StringComparison.CurrentCultureIgnoreCase)))
            .OrderByDescending(item => item.Fact.UpdatedAtUtc).ToArray();
        // Keep what was selected (an edited fact stays selected with its new revision).
        var keep = FactsList.SelectedItems.Cast<FactItem>().Select(item => item.Fact.Id).ToHashSet();
        rendering = true;
        FactsList.ItemsSource = shown;
        foreach (var item in shown.Where(item => keep.Contains(item.Fact.Id)))
            FactsList.SelectedItems.Add(item);
        rendering = false;
        var all = filter?.Kind is null or PersonKind.All && words.Length == 0 && !history;
        FactStatus.Text = factsLine = Summary(shown.Length, all);
        DeleteShownButton.Visibility = all ? Visibility.Collapsed : Visibility.Visible;
        DeleteShownButton.Content = (words.Length > 0 ? shown.Length == 1 ? "Delete the 1 found" : $"Delete all {shown.Length} found"
            : filter?.Kind switch
            {
                PersonKind.Voice => $"Delete all of {ShortName(filter.VoiceId!)}'s facts ({shown.Length})",
                PersonKind.Everyone => $"Delete all facts not tied to a voice ({shown.Length})",
                PersonKind.Forgotten => $"Delete forgotten voices' facts ({shown.Length})",
                _ => $"Delete all {shown.Length} shown"
            }).Replace("_", "__", StringComparison.Ordinal);
        ShowSelection();
    }

    private void ShowHistory_Changed(object sender, RoutedEventArgs e)
    {
        if (rendering || closed) return;
        RenderFacts();
        RenderActions();
    }

    private string ShortName(string voiceId) =>
        roster.Resolve(voiceId) is { } voice ? voice.Named ? voice.DisplayName : $"Voice {voice.Number}" : "a forgotten voice";

    private void Search_Changed(object sender, TextChangedEventArgs e)
    {
        if (rendering || closed) return;
        RenderFacts();
        RenderActions();
    }

    /// <summary>How many facts and whose (counts only, never a name or a fact). Replaced facts (history) are counted apart.</summary>
    private string Summary(int shown, bool all)
    {
        var current = facts.Where(f => !f.IsHistory).ToArray();
        var text = current.Length == 1 ? "1 fact remembered" : $"{current.Length} facts remembered";
        var people = current.Select(f => f.VoiceId is { } id ? roster.Resolve(id)?.Id : null).OfType<string>().Distinct().Count();
        var owned = current.Count(f => f.VoiceId is { } id && roster.Resolve(id) is not null);
        var forgotten = current.Count(f => f.VoiceId is { } id && roster.Resolve(id) is null);
        if (owned > 0) text += $": {owned} belong{(owned == 1 ? "s" : "")} to {(people == 1 ? "1 person" : $"{people} people")} Martlet knows by voice";
        if (forgotten > 0) text += $"{(owned > 0 ? "," : ":")} {forgotten} to a forgotten voice";
        // How many are kept in each space besides yours (counts only).
        foreach (var space in current.Where(f => elsewhere.ContainsKey(f.Id)).GroupBy(SpaceName))
            text += $"; {space.Count()} in {space.Key}";
        var games = current.Count(f => f.Activity is not null);
        if (games > 0) text += $"; {games} kept for {(current.Where(f => f.Activity is not null).Select(f => f.Activity!.ToLowerInvariant()).Distinct().Count() is var kinds && kinds == 1 ? "1 game or activity" : $"{kinds} games or activities")}";
        var replaced = facts.Count - current.Length;
        if (replaced > 0) text += $"; {replaced} replaced kept as history";
        return text + (all ? "." : $"; showing {shown}.");
    }

    /// <summary>The name of the memory space a fact is kept in, or null for the active space.</summary>
    private string? SpaceName(MemoryFact fact) =>
        elsewhere.TryGetValue(fact.Id, out var space) ? MemorySpaces.Label(space.Id, service.Account) : null;

    /// <summary>The memory space a fact is kept in (null: the active space).</summary>
    private string? SpaceOf(MemoryFact fact) => elsewhere.GetValueOrDefault(fact.Id)?.Id;

    /// <summary>Whether this device may change the fact: facts someone else shares with this account are read-only.</summary>
    private bool Changeable(MemoryFact fact) => elsewhere.GetValueOrDefault(fact.Id) is not { Writable: false };

    private void PersonFilter_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (rendering || closed) return;
        RenderFacts();
        if (FactsList.SelectedItem is null) ResetPersonChoice(null);
        RenderActions();
    }

    /// <summary>A voice as the window names it: its name (and "you" for yours), else "Voice 3".</summary>
    private string PersonName(KnownVoice voice) =>
        (voice.Named ? voice.DisplayName : $"Voice {voice.Number}") + (yours(voice) ? " (you)" : "");

    private string? PersonName(string? voiceId) =>
        voiceId is null ? null : roster.Resolve(voiceId) is { } voice ? PersonName(voice) : "A forgotten voice";

    private string? SelectedVoice() => (PersonChoice.SelectedItem as PersonOption)?.VoiceId;

    private async void SaveFact_Click(object sender, RoutedEventArgs e)
    {
        if (!RequireCurrentEnabledConfiguration(FactStatus))
            return;
        MemoryMutationReceipt? receipt = null;
        var retention = SelectedRetention(existing: null);
        var content = FactContent.Text;
        var voice = SelectedVoice();
        var where = SelectedWhere();
        if (string.IsNullOrWhiteSpace(content))
        {
            FactStatus.Text = "Write the fact first.";
            return;
        }
        if (retention is null)
        {
            FactStatus.Text = "Choose how long to keep it first.";
            return;
        }
        await RunAsync(async token =>
            receipt = await service.SaveFactAsync(
                configurationRevision, content, retention, voice, token, activity: where).ConfigureAwait(false),
            "Couldn't add the fact.");
        if (closed || receipt is null)
            return;
        DisposeExportPreview();
        FactsList.UnselectAll();
        ResetEditor();
        await RefreshFactsAsync();
        FactStatus.Text = "Fact added; " + factsLine;
    }

    private async void EditFact_Click(object sender, RoutedEventArgs e)
    {
        if (!RequireCurrentEnabledConfiguration(FactStatus))
            return;
        if (SingleSelected() is not { } fact)
            return;
        MemoryMutationReceipt? receipt = null;
        var retention = SelectedRetention(fact);
        var content = FactContent.Text;
        var voice = SelectedVoice();
        var where = SelectedWhere();
        if (retention is null)
        {
            FactStatus.Text = "Choose how long to keep it first.";
            return;
        }
        await RunAsync(async token =>
            receipt = await service.EditFactAsync(
                configurationRevision, fact, content, retention, voice, token, SpaceOf(fact), where).ConfigureAwait(false),
            "Couldn't edit the fact; refresh and try again.");
        if (closed || receipt is null)
            return;
        DisposeExportPreview();
        await RefreshFactsAsync();
        FactStatus.Text = "Fact updated; " + factsLine;
    }

    private async void DeleteFact_Click(object sender, RoutedEventArgs e)
    {
        var selected = SelectedFacts();
        if (selected.Count == 1)
        {
            if (!RequireCurrentEnabledConfiguration(FactStatus))
                return;
            var fact = selected[0];
            if (!confirm(this, $"Delete this remembered fact?\n\n\"{PreviewFact(fact.Content)}\"", "Delete remembered fact"))
                return;
            MemoryDeleteReceipt? receipt = null;
            await RunAsync(async token =>
                receipt = await service.DeleteFactAsync(configurationRevision, fact, token, SpaceOf(fact)).ConfigureAwait(false),
                "Couldn't delete the fact; refresh and try again.");
            if (closed || receipt is null)
                return;
            DisposeExportPreview();
            await RefreshFactsAsync();
            FactStatus.Text = "Fact deleted; " + factsLine;
        }
        else if (selected.Count > 1)
            await DeleteManyAsync(selected, $"Delete the {selected.Count} selected facts?", "Delete remembered facts");
    }

    /// <summary>Deletes every fact listed now: one person's (Show), what the search found, or both.</summary>
    private async void DeleteShown_Click(object sender, RoutedEventArgs e)
    {
        var shown = ((IEnumerable<FactItem>?)FactsList.ItemsSource ?? []).Select(item => item.Fact).ToArray();
        if (shown.Length == 0) return;
        var what = string.IsNullOrWhiteSpace(SearchBox.Text) && PersonFilter.SelectedItem is PersonOption { Kind: PersonKind.Voice, VoiceId: { } voice }
            ? $"all {shown.Length} of {ShortName(voice)}'s facts" : shown.Length == 1 ? "the 1 fact shown" : $"all {shown.Length} facts shown";
        await DeleteManyAsync(shown, $"Delete {what} for good?", "Delete remembered facts");
    }

    /// <summary>Forgets everything in your own memories (on every computer the memory sync reaches); the household's memories
    /// and memories shared with you stay.</summary>
    private async void DeleteAll_Click(object sender, RoutedEventArgs e)
    {
        var mine = facts.Where(f => !elsewhere.ContainsKey(f.Id)).ToArray();
        if (mine.Length == 0) return;
        await DeleteManyAsync(mine,
            $"Delete everything Martlet remembers ({(mine.Length == 1 ? "1 fact" : $"{mine.Length} facts")}) about everyone, for good" +
            (elsewhere.Count > 0 ? " (the household's memories stay)?" : "?"),
            "Delete all memories");
    }

    private async Task DeleteManyAsync(IReadOnlyCollection<MemoryFact> doomed, string question, string title)
    {
        if (!RequireCurrentEnabledConfiguration(FactStatus) || !confirm(this, question, title))
            return;
        MemoryExpiryReceipt? receipt = null;
        // Each memory space deletes its own facts; facts shared with you (read-only here) stay.
        await RunAsync(async token =>
        {
            var deleted = 0;
            foreach (var space in doomed.Where(Changeable).GroupBy(SpaceOf))
                deleted += (receipt = await service.DeleteFactsAsync(configurationRevision, [.. space], token, space.Key)
                    .ConfigureAwait(false)).DeletedFacts;
            receipt = receipt is null ? null : new(deleted, receipt.StoreRevision);
        }, "Couldn't delete the facts; refresh and try again.");
        if (closed || receipt is null)
            return;
        DisposeExportPreview();
        FactsList.UnselectAll();
        await RefreshFactsAsync();
        FactStatus.Text = (receipt.DeletedFacts == 1 ? "Deleted 1 fact; " : $"Deleted {receipt.DeletedFacts} facts; ") + factsLine;
    }

    private void NewFact_Click(object sender, RoutedEventArgs e)
    {
        FactsList.UnselectAll();
        ResetEditor();
        RenderActions();
        FactContent.Focus();
    }

    private IReadOnlyList<MemoryFact> SelectedFacts() => FactsList.SelectedItems.Cast<FactItem>().Select(item => item.Fact).ToArray();

    private MemoryFact? SingleSelected() => FactsList.SelectedItems.Count == 1 && FactsList.SelectedItem is FactItem { Fact: var fact } ? fact : null;

    /// <summary>The editor for a new fact: empty, for the voice shown (else yours), kept until deleted.</summary>
    private void ResetEditor()
    {
        editing = null;
        if (FactContent is null) return;
        FactContent.Clear();
        FactDetails.Text = "";
        EditorHeading.Text = "New fact";
        RetentionChoice.ItemsSource = NewRetentionOptions;
        RetentionChoice.SelectedIndex = 0;
        ResetPersonChoice(null);
        ResetWhereChoice(null);
    }
    private void FactsList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (rendering || closed) return;
        ShowSelection();
    }

    /// <summary>The editor follows the selection: one fact to change, several to delete, none for a new fact (what was typed for
    /// a new fact stays).</summary>
    private void ShowSelection()
    {
        var selected = SelectedFacts();
        if (selected.Count == 0)
        {
            if (editing is not null) ResetEditor();
            RenderActions();
            return;
        }
        if (selected.Count > 1)
        {
            editing = null;
            FactContent.Clear();
            EditorHeading.Text = $"{selected.Count} facts selected";
            FactDetails.Text = "Delete them together, or select just one to change it.";
            RenderActions();
            return;
        }
        var fact = selected[0];
        EditorHeading.Text = "Selected fact";
        if (editing != (fact.Id, fact.Revision))
        {
            editing = (fact.Id, fact.Revision);
            FactContent.Text = fact.Content;
            ResetPersonChoice(fact);
            ResetWhereChoice(fact);
            RetentionChoice.ItemsSource = NewRetentionOptions.Append(
                new RetentionOption("Keep current retention", null, KeepCurrent: true));
            RetentionChoice.SelectedIndex = NewRetentionOptions.Length;
        }
        var earlier = facts.Count(f => f.ReplacedBy == fact.Id);
        FactDetails.Text =
            $"Belongs to: {PersonName(fact.VoiceId) ?? "everyone (not tied to a voice)"}\n" +
            $"Matters: {MemoryActivity.Describe(fact.Activity)}\n" +
            (service.Account is null ? "" : $"Kept in: {SpaceName(fact) ?? MemorySpaces.Label(activeSpace ?? service.Account.Space, service.Account)}" +
                (Changeable(fact) ? "" : " (only the person who shares it can change it)") + "\n") +
            $"Created: {When(fact.CreatedAtUtc)} ({MemoryPromptContext.Source(fact.CreatedFrom.SourceKind)})\n" +
            $"Updated: {When(fact.UpdatedAtUtc)} ({MemoryPromptContext.Source(fact.LastModifiedBy.SourceKind)})\n" +
            $"Retention: {RetentionText(fact.Retention)}" +
            (fact.ReplacedAtUtc is { } replaced
                ? $"\nReplaced: {When(replaced)} (kept as history; never recalled)" +
                    (fact.ReplacedBy is { } by ? "\nReplaced by: " + (facts.FirstOrDefault(f => f.Id == by) is { } newer
                        ? PreviewFact(newer.Content) : "a fact that was deleted") : "")
                : "") +
            (earlier > 0 ? $"\nEarlier versions: {earlier} (Show replaced facts)" : "") +
            (fact.SearchWords is { } searchWords ? $"\nSearch words: {searchWords}" : "");
        RenderActions();
    }

    private MemoryRetention? SelectedRetention(MemoryFact? existing)
    {
        if (RetentionChoice.SelectedItem is not RetentionOption selected)
            return null;
        if (selected.KeepCurrent)
            return existing?.Retention;
        return selected.Duration is { } duration
            ? MemoryRetention.ExpiringAt(DateTimeOffset.UtcNow + duration)
            : MemoryRetention.UntilDeleted();
    }
}
