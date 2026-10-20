using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Media;
using Martlet.Avatar.Hosting;
using Martlet.Conversation;
using Martlet.Core.Settings;
using Martlet.Presentation;

namespace Martlet.Desktop;

/// <summary>Companion › Touch › Touch temperament: how the active persona's character acts (never what it says) when each
/// part of it is touched: how much it likes it (hates to craves) and which emotes, gestures and face symbols play, in one table
/// with a line per category (each names the parts it covers; intimate parts have their own) and per part that reacts
/// differently. The Thinking model decides the persona's own temperament from the personality in the background after the
/// personality is saved (Re-decide from personality asks again); the owner's edits here win and stay until they re-decide. The
/// owner can make named custom temperaments and choose, per persona, its own, the built-in reactions or a custom one; editing a
/// custom one changes it for each persona that uses it. Under Touch zones each zone plays its own reaction list; the temperament
/// fills a new zone's list and gives every zone its feeling, linger, look and escalation.</summary>
public partial class MainWindow
{
    private readonly CharacterTemperamentService characterTemperaments;
    private bool decidingTemperament;
    // What the last Uses, Create, Rename or Delete did (or why not), for the next card's line under them.
    private string? temperamentUseNote;

    private void WireCharacterTemperament()
    {
        characterTemperaments.Changed += () => Dispatcher.InvokeAsync(() =>
        {
            touchPage?.ShowDecision();
            // The Touch page shows the temperament; the Eyes page shows the gaze it decides.
            if (closing || openTab is not (CompanionTab.Touch or CompanionTab.Eyes) || CompanionContent.IsKeyboardFocusWithin || tabEdited) return;
            if (!characterTemperaments.Busy && !decidingTemperament) RenderTab();
        });
        // A decided (or edited, or shared) temperament may change the character's usual gaze.
        characterTemperaments.Changed += avatar.Gaze.Refresh;
        WireCharacterGaze();
    }

    private Task<(string? Answer, string? Failure)> AskThinkingForTemperamentAsync(string purpose, string instructions, string text, CancellationToken token) =>
        conversation is { } live ? live.AskHelperAsync(HelperJobKind.Temperament, purpose, instructions, text, null, token)
            : Task.FromResult<(string?, string?)>((null, "Thinking isn't set up yet"));

    /// <summary>After Personality closes: each persona whose personality changed meaningfully (or is new) has its touch
    /// temperament decided again in the background, once Martlet isn't replying.</summary>
    private void PersonalitiesSaved(IReadOnlyDictionary<Guid, string>? before)
    {
        if (closing || homeSettings?.Companion is not { } companion) return;
        foreach (var persona in companion.Personas)
        {
            if (before is not null && before.TryGetValue(persona.Id, out var old) &&
                CharacterTouchTemperaments.Digest(old) == CharacterTouchTemperaments.Digest(persona.Text)) continue;
            characterTemperaments.PersonalitySaved(persona, () => conversation?.Replying == true || openConversation?.HearingYou == true,
                AskThinkingForTemperamentAsync, lifetime.Token);
        }
    }

    private async Task DecideTemperamentAsync()
    {
        if (decidingTemperament || homeSettings?.Companion?.ActivePersona is not { } persona) return;
        decidingTemperament = true;
        try { await characterTemperaments.DecideAsync(persona, AskThinkingForTemperamentAsync, lifetime.Token, use: true); }
        finally
        {
            decidingTemperament = false;
            tabEdited = false;
            if (!closing && openTab == CompanionTab.Touch) RenderTab();
        }
    }


}
