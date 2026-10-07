using System.IO;
using System.Text.Json;
using Martlet.Avatar.Hosting;
using Martlet.Avatars;

namespace Martlet.Mcp;

/// <summary>character_touch_zones: Companion › Character › Touch zones as Martlet.Avatar.Hosting's CharacterTouchZones runs them,
/// with NO vision request: the zone list and the vision request, what the production parser makes of a simulated vision answer
/// (fractions, pixels or 0..1000 grounding) and how it binds to a simulated drawables/bones probe, the zones saved for the model
/// in a data directory, and which zone a simulated touch lands in with what it plays (from the model's emotes and gestures) and
/// tells the character. With save (an explicit, disposable data directory only) the parsed zones are saved as the desktop saves
/// detected ones, so the Touch zones section can be checked with -Desktop. Contacts nothing; never returns the model's path.</summary>
internal static class TouchZonesCheck
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    internal static async Task<object> RunAsync(string dataDirectory, bool explicitDirectory, string? modelPath, string? modelId, string? answer,
        int? width, int? height, string? crop, string? probe, string? touch, bool save, bool? includeIntimate, string? snapshotPath,
        CancellationToken cancellation, string? temperamentAnswer = null, string? personaId = null, string? personality = null, int? repeats = null)
    {
        CharacterActionCatalog? catalog = null;
        string? problem = null;
        var renderer = AvatarRenderer.Live2D;
        if (modelPath is null)
        {
            try
            {
                using var avatar = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(dataDirectory, "avatar.json")));
                modelPath = avatar.RootElement.TryGetProperty("model_path", out var value) ? value.GetString() : null;
                if (avatar.RootElement.TryGetProperty("renderer", out var kind) && kind.ToString().Contains("vrm", StringComparison.OrdinalIgnoreCase))
                    renderer = AvatarRenderer.Vrm;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException) { }
        }
        if (modelPath is not null)
        {
            if (modelPath.EndsWith(".vrm", StringComparison.OrdinalIgnoreCase)) renderer = AvatarRenderer.Vrm;
            try
            {
                var inventory = await CharacterActionInventory.ReadAsync(renderer, modelPath, cancellation);
                catalog = new(inventory, CharacterActions.Merge(inventory, CharacterActions.Load(dataDirectory, inventory.ModelId)));
            }
            catch (Exception error) when (error is Martlet.Core.Contracts.ContractException or IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                problem = "The model can't be read: " + error.Message;
            }
        }
        var id = modelId ?? catalog?.Inventory.ModelId ?? throw new ArgumentException(
            problem ?? "Give modelPath (a .model3.json or .vrm), modelId, or a dataDirectory whose avatar.json shows a model.");

        int w = width ?? 400, h = height ?? 800;
        var cropBox = crop is null ? new TouchZoneBox(0, 0, 1, 1) : Box(crop);
        var probed = probe is null ? null : JsonSerializer.Deserialize<RendererZoneProbe>(probe, Web);
        var parsed = answer is null ? null : CharacterTouchZones.Parse(answer, w, h);
        var saved = CharacterTouchZones.Load(dataDirectory, id);
        CharacterTouchZoneSettings? detected = parsed is null ? null : CharacterTouchZones.Detected(saved, id, parsed, cropBox, probed, DateTimeOffset.Now);
        if (detected is not null && includeIntimate is { } intimate) detected = detected with { IncludeIntimate = intimate };
        string? wrote = null;
        if (save)
        {
            if (!explicitDirectory) throw new ArgumentException("save needs an explicit (disposable) dataDirectory.");
            if (detected is null) throw new ArgumentException("save needs an answer the parser can read.");
            saved = await CharacterTouchZones.SaveAsync(dataDirectory, detected, DateTimeOffset.Now, cancellation);
            if (snapshotPath is not null) await CharacterTouchZones.SaveSnapshotAsync(dataDirectory, id, await File.ReadAllBytesAsync(snapshotPath, cancellation), cancellation);
            wrote = $"Saved {detected.Zones.Count} zones for the model in {CharacterTouchZones.FileName}" + (snapshotPath is null ? "." : " with the snapshot.");
        }
        var settings = saved ?? detected;
        // The persona's touch temperament: a simulated Thinking answer, else the one saved for personaId in the data directory.
        var persona = Guid.TryParse(personaId, out var parsedPersona) ? parsedPersona : Guid.Empty;
        var temperament = temperamentAnswer is not null
            ? CharacterTouchTemperaments.Parse(temperamentAnswer, persona == Guid.Empty ? Guid.NewGuid() : persona,
                personality is null ? null : CharacterTouchTemperaments.Digest(personality), CharacterTouchTemperament.ByThinking, DateTimeOffset.Now)
            : persona == Guid.Empty ? null : CharacterTouchTemperaments.Load(dataDirectory, persona);
        var touches = Math.Max(1, repeats ?? 1);
        object? match = null;
        if (touch is not null)
        {
            var given = JsonSerializer.Deserialize<CharacterTouch>(touch, Web) ?? throw new ArgumentException("touch must be a CharacterTouch object.");
            var found = CharacterTouchZones.Match(settings, given);
            match = found is null ? new { zone = (string?)null, how = (string?)null, coarse = given.CoarseZone, plays = Array.Empty<string>(), notices = false, noticed = (string?)null }
                : new
                {
                    zone = found.Zone.Id, name = found.Zone.Name, how = found.How, coarse = given.CoarseZone,
                    plays = CharacterTouchZones.React(found.Zone, catalog, temperament, touches).Actions.Select(s => $"{s.Kind}: {s.Name}").ToArray(),
                    reaction = Reaction(CharacterTouchZones.React(found.Zone, catalog, temperament, touches)), repeats = touches,
                    notices = found.Zone.Reaction.Notices, noticed = Noticed(found.Zone, given), rests = found.Zone.Reaction.CooldownSeconds
                };
        }
        return new
        {
            model = new { renderer = renderer.ToString(), modelId = id, readable = catalog is not null, problem },
            zones = new
            {
                count = CharacterTouchZones.Kinds.Count,
                intimate = CharacterTouchZones.Kinds.Where(k => k.Intimate).Select(k => k.Id).ToArray()
            },
            request = new { instructions = CharacterTouchZones.DetectionInstructions, list = CharacterTouchZones.DetectionList },
            parsed = answer is null ? null : parsed?.Select(Describe).ToArray() ?? [],
            detected = detected is null ? null : detected.Zones.Select(Describe).ToArray(),
            wrote,
            saved = settings is null ? null : new
            {
                zones = settings.Zones.Count, active = settings.Zones.Count(settings.Active), settings.DetectedBy, settings.IncludeIntimate,
                snapshot = File.Exists(CharacterTouchZones.SnapshotPath(dataDirectory, id)),
                each = settings.Zones.Select(z => new
                {
                    z.Id, z.Name, z.Enabled, active = settings.Active(z), drawables = z.Drawables.Count, z.Bones,
                    plays = CharacterTouchZones.React(z, catalog, temperament, 1) is var r && r.From == TouchReactionPlan.FromOwner
                        ? string.Join(" + ", r.Actions.Select(s => s.Name)) : r.From + ": " + string.Join(" + ", r.Actions.Select(s => s.Name)),
                    notices = z.Reaction.Notices, hint = CharacterTouchZones.Narration(z)
                }).ToArray()
            },
            temperament = new
            {
                request = new
                {
                    instructions = CharacterTouchTemperaments.DecisionInstructions,
                    text = personality is null ? null : CharacterTouchTemperaments.DecisionRequest(personality)
                },
                vocabulary = CharacterTouchTemperaments.Vocabulary, attitudes = CharacterTouchTemperaments.AttitudeWords,
                read = temperamentAnswer is null ? (bool?)null : temperament is not null,
                used = temperament is null ? null : new
                {
                    temperament.Source, summary = CharacterTouchTemperaments.Summary(temperament),
                    gaze = temperament.Gaze is { } gaze ? CharacterGaze.Word(gaze) : null,
                    groups = temperament.Groups.ToDictionary(g => g.Key, g => Entry(g.Value)),
                    zones = temperament.Zones.ToDictionary(z => z.Key, z => Entry(z.Value)),
                    escalation = temperament.Escalation
                }
            },
            match
        };
    }

    private static object Entry(TouchTemperamentEntry entry) =>
        new
        {
            attitude = CharacterTouchTemperaments.AttitudeWord(entry.Attitude), reactions = entry.Reactions, linger = entry.LingerSeconds,
            look = entry.LookSeconds
        };

    private static object Reaction(TouchReactionPlan plan) =>
        new { from = plan.From, attitude = plan.Attitude, escalated = plan.Escalated, linger = plan.LingerSeconds, look = plan.LookSeconds };

    // What the Thinking model hears about this one touch when Martlet notices the zone (the ledger's line), or null.
    private static string? Noticed(CharacterTouchZone zone, CharacterTouch touch)
    {
        if (!zone.Reaction.Notices) return null;
        var ledger = new Martlet.Conversation.TouchLedger();
        ledger.Record(new(touch.Held ? Martlet.Conversation.PhysicalKind.Hold : CharacterTouchZones.Pats(zone) ? Martlet.Conversation.PhysicalKind.Pat
            : Martlet.Conversation.PhysicalKind.Tap, TimeSpan.Zero, CharacterTouchZones.Part(zone), zone.Name.ToLowerInvariant(),
            Hint: CharacterTouchZones.Narration(zone)));
        return ledger.Drain(TimeSpan.Zero)?.Line;
    }

    private static object Describe(CharacterTouchZone zone) => new
    {
        zone.Id, zone.Name, box = new[] { zone.Box.X, zone.Box.Y, zone.Box.Width, zone.Box.Height }.Select(v => Math.Round(v, 4)).ToArray(),
        zone.Drawables, zone.Bones
    };

    private static TouchZoneBox Box(string text)
    {
        var values = text.Split([',', ' '], StringSplitOptions.RemoveEmptyEntries)
            .Select(t => double.Parse(t, System.Globalization.CultureInfo.InvariantCulture)).ToArray();
        return values.Length == 4 ? new(values[0], values[1], values[2], values[3]) : throw new ArgumentException("crop is \"left,top,width,height\" (fractions of the page).");
    }
}
