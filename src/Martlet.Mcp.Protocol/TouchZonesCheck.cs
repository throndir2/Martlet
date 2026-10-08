using System.IO;
using System.Text.Json;
using Martlet.Avatar.Hosting;
using Martlet.Avatars;
using Martlet.Mcp.Shared;
using Martlet.Providers;

namespace Martlet.Mcp;

/// <summary>character_touch_zones: Companion › Character › Touch zones as Martlet.Avatar.Hosting's CharacterTouchZones runs them,
/// with NO vision request: the zone list and the step-by-step vision requests, what the production parser makes of a simulated
/// vision answer (fractions, pixels or 0..1000 grounding) and how it binds to a simulated drawables/bones probe, the zones saved
/// for the model in a data directory, and which zone a simulated touch lands in with what it plays (from the model's emotes and
/// gestures) and tells the character. With detect, the production detection (TouchZoneDetection) runs on a real snapshot PNG,
/// composing every picture it would send, with a FIXTURE - NOT AI stand-in answering from the given zones. With save (an
/// explicit, disposable data directory only) the zones are saved as the desktop saves detected ones, so the Touch zones section
/// can be checked with -Desktop. Contacts nothing; never returns the model's path.</summary>
internal static class TouchZonesCheck
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    internal static async Task<object> RunAsync(string dataDirectory, bool explicitDirectory, string? modelPath, string? modelId, string? answer,
        int? width, int? height, string? crop, string? probe, string? touch, bool save, bool? includeIntimate, string? snapshotPath,
        CancellationToken cancellation, string? temperamentAnswer = null, string? personaId = null, string? personality = null, int? repeats = null,
        bool detect = false, string? guess = null, string? previewDirectory = null, int? checks = null, int? failAt = null, string? probePath = null)
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
        if (probePath is not null)
        {
            // A probe.json as Detect zones keeps it (where the snapshot sat, and the probe), or a bare probe.
            using var file = JsonDocument.Parse(await File.ReadAllBytesAsync(probePath, cancellation));
            if (file.RootElement.TryGetProperty("probe", out var keptProbe))
            {
                probed = keptProbe.Deserialize<RendererZoneProbe>(Web);
                if (crop is null && file.RootElement.TryGetProperty("crop", out var keptCrop)) cropBox = keptCrop.Deserialize<TouchZoneBox>(Web) ?? cropBox;
            }
            else probed = file.RootElement.Deserialize<RendererZoneProbe>(Web);
        }
        var hints = TouchZoneDetection.Hints(probed, cropBox);
        var parsed = answer is null ? null : CharacterTouchZones.Parse(answer, w, h);
        var saved = CharacterTouchZones.Load(dataDirectory, id);
        CharacterTouchZoneSettings? detected = parsed is null ? null : CharacterTouchZones.Detected(saved, id, parsed, cropBox, probed, DateTimeOffset.Now);
        object? detection = null;
        (TouchZoneSent Sent, List<(string File, byte[] Bytes)> Pictures)? sent = null;
        if (detect)
        {
            if (snapshotPath is null) throw new ArgumentException("detect needs snapshotPath: a PNG of the character, transparent around it.");
            var snapshot = TouchZoneImages.Decode(await File.ReadAllBytesAsync(snapshotPath, cancellation));
            var truth = CharacterTouchZones.Parse(answer, snapshot.Width, snapshot.Height) ??
                throw new ArgumentException("detect needs answer: the zones a perfect vision model would find, as JSON about the whole snapshot.");
            var first = guess is null ? null : CharacterTouchZones.Parse(guess, snapshot.Width, snapshot.Height);
            // Include intimate zones is on unless it is turned off, here or in the saved zones: then the intimate zones must be found.
            var required = (includeIntimate ?? saved?.IncludeIntimate) == false ? [] : TouchZoneDetection.Erogenous;
            (detection, var found, sent) = await DetectAsync(snapshot, truth, first, hints, previewDirectory, checks,
                required, failAt, cancellation);
            detected = found is null ? null : CharacterTouchZones.Detected(saved, id, found, cropBox, probed, DateTimeOffset.Now, whole: true);
        }
        if (detected is not null && includeIntimate is { } intimate) detected = detected with { IncludeIntimate = intimate };
        string? wrote = null;
        if (save)
        {
            if (!explicitDirectory) throw new ArgumentException("save needs an explicit (disposable) dataDirectory.");
            if (detected is null) throw new ArgumentException(detect ? "save found no zones to save." : "save needs an answer the parser can read.");
            saved = await CharacterTouchZones.SaveAsync(dataDirectory, detected, DateTimeOffset.Now, cancellation);
            if (snapshotPath is not null) await CharacterTouchZones.SaveSnapshotAsync(dataDirectory, id, await File.ReadAllBytesAsync(snapshotPath, cancellation), cancellation);
            if (sent is { } pictures)
            {
                var folder = CharacterTouchZones.ClearSent(dataDirectory, id);
                foreach (var (file, bytes) in pictures.Pictures) await File.WriteAllBytesAsync(Path.Combine(folder, file), bytes, cancellation);
                await CharacterTouchZones.SaveSentAsync(dataDirectory, id, pictures.Sent, cancellation);
            }
            wrote = $"Saved {detected.Zones.Count} zones for the model in {CharacterTouchZones.FileName}" + (snapshotPath is null ? "." : " with the snapshot") +
                (sent is null ? "" : " and the pictures the detection sent") + (snapshotPath is null ? "" : ".");
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
            request = new
            {
                parts = new { instructions = TouchZoneDetection.PartsInstructions, text = TouchZoneDetection.PartsText(null) },
                zones = new
                {
                    instructions = TouchZoneDetection.ZonesInstructions,
                    regions = TouchZoneDetection.Regions.Select(r => new { r.Id, r.What, r.Zones }).ToArray(),
                    text = TouchZoneDetection.ZonesText(TouchZoneDetection.Regions[0], null, new(0, 0, 1, 1))
                },
                check = new { instructions = TouchZoneDetection.CheckInstructions },
                extras = TouchZoneDetection.Extras
            },
            parsed = answer is null ? null : parsed?.Select(Describe).ToArray() ?? [],
            hints = DescribeHints(hints),
            detection,
            detected = detected is null ? null : detected.Zones.Select(Describe).ToArray(),
            wrote,
            saved = settings is null ? null : new
            {
                zones = settings.Zones.Count, active = settings.Zones.Count(settings.Active), settings.DetectedBy, settings.IncludeIntimate, settings.Whole,
                crop = settings.Crop is { } at ? new { left = Math.Round(at.X, 4), top = Math.Round(at.Y, 4), width = Math.Round(at.Width, 4),
                    height = Math.Round(at.Height, 4) } : null,
                snapshot = File.Exists(CharacterTouchZones.SnapshotPath(dataDirectory, id)),
                sent = CharacterTouchZones.LoadSent(dataDirectory, id) is { } last
                    ? new
                    {
                        line = last.Describe(), last.Requests, pictures = last.Pictures.Count, last.Fixture, last.Steps,
                        probe = CharacterTouchZones.LoadProbe(dataDirectory, id) is { } kept ? DescribeHints(TouchZoneDetection.Hints(kept.Probe, kept.Crop)) : null
                    } : null,
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

    // The production detection on a real snapshot, with a FIXTURE - NOT AI stand-in answering from truth (guess answers the
    // close-ups first, so the checks have something to correct; at request failAt it fails instead, as a model that stopped
    // answering). Every picture is composed and encoded as the desktop sends it.
    private static async Task<(object Report, IReadOnlyList<CharacterTouchZone>? Zones, (TouchZoneSent Sent, List<(string File, byte[] Bytes)> Pictures)? Sent)>
        DetectAsync(ZonePixels snapshot, IReadOnlyList<CharacterTouchZone> truth, IReadOnlyList<CharacterTouchZone>? guess, ZoneHints? hints,
            string? previewDirectory, int? checks, IReadOnlyList<string> required, int? failAt, CancellationToken cancellation)
    {
        if (previewDirectory is not null) Directory.CreateDirectory(previewDirectory);
        var pictures = new List<(string File, byte[] Bytes)>();
        var sentPictures = new List<TouchZoneSentPicture>();
        var asked = new List<object>();
        var result = await TouchZoneDetection.RunAsync(snapshot, hints, async (ask, token) =>
        {
            var image = TouchZoneImages.Encode(ask.Picture);
            var file = $"{pictures.Count + 1:00}-{ask.Step.Replace(' ', '-')}.{(image.MediaType == ImageMediaType.Png ? "png" : "jpg")}";
            var bytes = image.Content.ToArray();
            pictures.Add((file, bytes));
            sentPictures.Add(new(file, ask.Step, ask.Kind.ToString(), image.Width, image.Height, image.ByteCount, image.MimeType));
            if (previewDirectory is not null) await File.WriteAllBytesAsync(Path.Combine(previewDirectory, file), bytes, token);
            var fails = pictures.Count == failAt;
            var reply = fails ? null : TouchZoneDetection.Oracle(ask, truth, guess);
            asked.Add(new
            {
                ask.Step, kind = ask.Kind.ToString(), picture = $"{image.Width}x{image.Height} {image.MimeType}, {image.ByteCount / 1024} KB", file,
                region = Edges(ask.Region), marks = ask.Marks.Count, text = ask.Text, answer = reply, failed = fails ? "a simulated failure" : null
            });
            return (reply, fails ? "a simulated failure" : (string?)null);
        }, null, cancellation, new ZoneDetectionOptions { Checks = Math.Clamp(checks ?? 2, 0, 5), Required = required });
        var found = result.Zones ?? [];
        var errors = found.Select(z => truth.FirstOrDefault(t => t.Id == z.Id) is { } t ? TouchZoneDetection.Moved(z.Box, t.Box) : double.NaN)
            .Where(double.IsFinite).ToArray();
        var report = new
        {
            fixture = "FIXTURE - NOT AI: a stand-in answered from the given zones; no vision request was made",
            snapshot = new { snapshot.Width, snapshot.Height }, requestCount = result.Requests, failure = result.Failure, result.Steps, asked,
            found = found.Count, given = truth.Count, missed = truth.Where(t => found.All(z => z.Id != t.Id)).Select(t => t.Id).ToArray(),
            required, requiredMissing = required.Where(id => found.All(z => z.Id != id)).ToArray(),
            worstEdge = errors.Length == 0 ? (double?)null : Math.Round(errors.Max(), 4),
            meanEdge = errors.Length == 0 ? (double?)null : Math.Round(errors.Average(), 4),
            zones = found.Select(Describe).ToArray()
        };
        return (report, result.Zones, (new TouchZoneSent(DateTimeOffset.Now, true, result.Requests, sentPictures, result.Steps, result.Zones is not null), pictures));
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

    // A box as its left, top, right and bottom edges (fractions of the snapshot), rounded.
    private static double[] Edges(TouchZoneBox box) => [.. new[] { box.X, box.Y, box.X + box.Width, box.Y + box.Height }.Select(v => Math.Round(v, 4))];

    // What the probe tells of the model's own parts: how many it has and names, the body parts its names place (with the side,
    // the character's own, for a part that comes in pairs) and the close-ups' windows they give, as edges in the snapshot.
    private static object? DescribeHints(ZoneHints? hints) => hints is null ? null : new
    {
        bones = hints.Bones.Count, hints.ModelParts, hints.NamedModelParts, hints.NamedParts, named = hints.Named,
        middle = hints.Middle is { } middle ? Math.Round(middle, 4) : (double?)null,
        areas = hints.Areas.Select(a => new { a.Part, a.Side, a.Drawables, box = Edges(a.Box) }).ToArray(),
        regions = TouchZoneDetection.Regions.ToDictionary(r => r.Id, r => TouchZoneDetection.NamedRegion(r.Id, hints) is { } box ? Edges(box) : null)
    };

    private static TouchZoneBox Box(string text)
    {
        var values = text.Split([',', ' '], StringSplitOptions.RemoveEmptyEntries)
            .Select(t => double.Parse(t, System.Globalization.CultureInfo.InvariantCulture)).ToArray();
        return values.Length == 4 ? new(values[0], values[1], values[2], values[3]) : throw new ArgumentException("crop is \"left,top,width,height\" (fractions of the page).");
    }
}
