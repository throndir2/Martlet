using System.IO;
using System.Text.Json;
using Martlet.Avatar.Hosting;
using Martlet.Avatars;
using Martlet.Conversation;

namespace Martlet.Mcp;

/// <summary>character_reaction_changes: the character's own changes to how it reacts to touches (Companion › Touch › Changes the
/// character made; docs/CONVERSATION.md#how-i-react) as Martlet.Avatar.Hosting's CharacterReactionChanges keeps them in a data
/// directory's character-reaction-changes.json, and one Touch reactions tool call (the How I react check-in's tools) run as the
/// desktop runs it, with the persona's temperament and the model's zones from the same data directory. Shows what each zone
/// plays and how the character feels about it before and after. With save (an explicit, disposable data directory only) the
/// call's change, or undo (the owner's Undo), is written. Contacts nothing: no model is asked.</summary>
internal static class ReactionChangesCheck
{
    internal static async Task<object> RunAsync(string dataDirectory, bool explicitDirectory, string? personaId, string? modelId,
        string? modelPath, string? tool, string? arguments, string? undo, bool save, string? at, string? checkInId, CancellationToken cancellation)
    {
        if ((save || undo is not null) && !explicitDirectory) throw new ArgumentException("save and undo need an explicit (disposable) dataDirectory.");
        if (tool is not null && !CharacterReactionTools.Names.Contains(tool))
            throw new ArgumentException("tool is one of " + string.Join(", ", CharacterReactionTools.Names) + ".");
        var now = at is null ? DateTimeOffset.Now : DateTimeOffset.Parse(at, System.Globalization.CultureInfo.InvariantCulture);
        var persona = Guid.TryParse(personaId, out var parsed) ? parsed : (Guid?)null;
        if ((tool is not null || undo is not null) && persona is null) throw new ArgumentException("tool and undo need personaId.");

        // The model's emotes, or Martlet's own gestures alone when no model can be read (FIXTURE catalog).
        CharacterActionCatalog catalog;
        string catalogFrom;
        try
        {
            if (modelPath is null) throw new InvalidOperationException("no modelPath");
            var renderer = modelPath.EndsWith(".vrm", StringComparison.OrdinalIgnoreCase) ? AvatarRenderer.Vrm : AvatarRenderer.Live2D;
            var inventory = await CharacterActionInventory.ReadAsync(renderer, modelPath, cancellation);
            catalog = new(inventory, CharacterActions.Merge(inventory, CharacterActions.Load(dataDirectory, inventory.ModelId)));
            modelId ??= inventory.ModelId;
            catalogFrom = "the model's own emotes and motions, and Martlet's gestures it supports";
        }
        catch (Exception error) when (error is InvalidOperationException or IOException or UnauthorizedAccessException or Martlet.Core.Contracts.ContractException)
        {
            var inventory = new CharacterActionInventory(modelId ?? "fixture-model", AvatarRenderer.Live2D,
                [.. CharacterActionInventory.AllGestures.Select(g => new CharacterActionSource(g.Id, CharacterActionKind.Gesture, g.Name, g.Does))]);
            catalog = new(inventory, CharacterActions.Merge(inventory, null));
            catalogFrom = "FIXTURE: Martlet's gestures only (no model was read)";
        }
        var zones = modelId is null ? null : CharacterTouchZones.Load(dataDirectory, modelId);
        var temperament = persona is { } id ? CharacterTouchTemperaments.Used(dataDirectory, id) : null;
        var before = CharacterReactionChanges.Load(dataDirectory);

        object? call = null;
        var after = before;
        if (tool is not null)
        {
            var context = new ReactionToolContext
            {
                PersonaId = persona!.Value, Who = "the character", Temperament = temperament, Zones = zones, Catalog = catalog,
                CheckInId = checkInId ?? "reactions", Run = now, Now = now
            };
            var answer = CharacterReactionTools.Call(tool, arguments ?? "{}", context, before);
            if (answer.Changes is { } changed)
            {
                after = changed;
                if (save) after = await CharacterReactionChanges.UpdateAsync(dataDirectory, _ => changed, cancellation);
            }
            call = new
            {
                tool, result = tool == CharacterReactionTools.Read && !explicitDirectory
                    ? "(read_touch_reactions holds the character's reasons: give an explicit, disposable dataDirectory to see it)" : answer.Result,
                failed = answer.Failed, changed = answer.Changes is not null, saved = save && answer.Changes is not null
            };
        }
        if (undo is not null)
        {
            var ended = CharacterReactionChanges.End(after, persona, CharacterReactionChange.ByOwner, now,
                undo.Equals(CharacterReactionChanges.All, StringComparison.OrdinalIgnoreCase) ? null : undo);
            after = save ? await CharacterReactionChanges.UpdateAsync(dataDirectory, _ => ended, cancellation) : ended;
        }

        object Effect(IReadOnlyList<CharacterReactionChange> changes)
        {
            if (persona is not { } who) return new { note = "Give personaId to see what touches play for that persona." };
            var active = CharacterReactionChanges.Active(changes, who, now);
            var felt = CharacterReactionChanges.Temperament(temperament, active, who);
            return new
            {
                active = active.Count,
                categories = CharacterTouchTemperaments.GroupIds.ToDictionary(g => g.Id, g =>
                    g.Id == CharacterTouchTemperaments.IntimateId && felt?.Groups.ContainsKey(g.Id) != true ? "as each part's own category"
                        : CharacterTouchTemperaments.AttitudeWord(CharacterReactionChanges.BaseAttitude(felt, g.Id))),
                zones = zones?.Zones.Where(zones.Active).Select(zone =>
                {
                    var changed = CharacterReactionChanges.Zone(zone, temperament, active, catalog);
                    var plan = CharacterTouchZones.React(changed, catalog, felt, 1);
                    return new
                    {
                        zone = zone.Id, feeling = plan.Attitude, plays = plan.Actions.Select(s => s.Name).ToArray(),
                        list = changed.Reaction.Actions, changedByCharacter = !ReferenceEquals(changed, zone)
                    };
                }).ToArray() ?? []
            };
        }

        return new
        {
            set = new
            {
                id = TouchReactions.SetId, tools = TouchReactions.Tools.Select(t => t.Name).ToArray(),
                limits = new
                {
                    perRun = CharacterReactionChanges.MaximumPerRun, perDay = CharacterReactionChanges.MaximumPerDay,
                    atOnce = CharacterReactionChanges.MaximumActive, shift = CharacterReactionChanges.MaximumShift,
                    hours = new[] { CharacterReactionChanges.MinimumHours, CharacterReactionChanges.MaximumHours }, defaultHours = CharacterReactionChanges.DefaultHours
                }
            },
            model = new { modelId, catalog = catalogFrom, zones = zones?.Zones.Count ?? 0 },
            temperament = persona is null ? null : CharacterTouchTemperaments.Summary(temperament),
            call,
            undo = undo is null ? null : new { undo, saved = save },
            changes = after.Select(c => new
            {
                c.Id, persona = c.PersonaId, c.Kind, c.Target, what = CharacterReactionChanges.Describe(c, zones, catalog),
                // The character's reasons come from the conversation: only from an explicit (disposable) data directory.
                why = explicitDirectory ? c.Why : null, at = c.At, until = c.Until, active = c.ActiveAt(now), c.EndedBy, by = c.By
            }).ToArray(),
            before = Effect(before),
            after = Effect(after),
            read = persona is { } p && explicitDirectory ? CharacterReactionTools.Describe(new()
                {
                    PersonaId = p, Who = "the character", Temperament = temperament, Zones = zones, Catalog = catalog, CheckInId = checkInId ?? "reactions",
                    Run = now, Now = now
                }, after) : null
        };
    }
}
