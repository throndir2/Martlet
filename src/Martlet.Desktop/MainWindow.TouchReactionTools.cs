using Martlet.Avatar.Hosting;
using Martlet.Conversation;
using Martlet.Providers;

namespace Martlet.Desktop;

/// <summary>The Touch reactions tool set's handler (<see cref="TouchReactions"/>): a check-in's calls to read and change how the
/// character reacts to touches, run as the character itself for the active persona, with the shown model's zones and emotes and
/// the persona's temperament. Each change is bounded and saved by <see cref="CharacterReactionChangeService"/>; the owner sees
/// and undoes it on Companion › Touch › Changes the character made.</summary>
public partial class MainWindow
{
    private async ValueTask<ConversationToolResult> TouchReactionsToolAsync(TextToolCall call, CheckInToolContext context, CancellationToken token)
    {
        if (!Guid.TryParse(context.PersonaId, out var personaId))
            return new("No personality is active, so the character has no reactions of its own to change.", true);
        // What the character reacts with now: read on the UI thread, where the shown model and the settings live.
        var state = await Dispatcher.InvokeAsync(() => new ReactionToolContext
        {
            PersonaId = personaId,
            Who = homeSettings?.Companion?.Personas.FirstOrDefault(p => p.Id == personaId)?.Name is { Length: > 0 } name ? name : "Martlet",
            Temperament = characterTemperaments.For(personaId), Zones = characterTouchZones.Current,
            Catalog = characterActions.For(avatar.InspectedProfile?.ModelPath) ?? characterActions.Current,
            CheckInId = context.CheckInId, Run = context.Now, Now = DateTimeOffset.Now
        });
        var answer = await characterReactionChanges.CallAsync(call.Name, call.ArgumentsJson, state, token);
        return new(answer.Result, answer.Failed);
    }
}
