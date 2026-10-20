using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using Martlet.Core.Installation;
using Martlet.Doctor;
using Martlet.Mcp.Client;
using Martlet.Mcp.Shared;
using Martlet.Core.Settings;

namespace Martlet.Mcp;

internal sealed partial class McpServer
{
    /// <summary>character_actions: a character model's emotes and motions as Companion › Emotes and motions uses
    /// them (Martlet.Avatar.Hosting's inventory, saved settings and prompts), optionally with a simulated Thinking answer parsed
    /// by the production naming parser. Model-authored names and model-relative file names only; never the model's path.</summary>
    private static async Task<object> CharacterActionsCheckAsync(JsonElement arguments, CancellationToken cancellation)
    {
        var directory = OptionalString(arguments, "dataDirectory") is { } given ? given : null;
        var modelPath = OptionalString(arguments, "modelPath");
        string? renderer = null;
        if (modelPath is null && directory is not null)
        {
            try
            {
                using var avatar = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(directory, "avatar.json")));
                modelPath = avatar.RootElement.TryGetProperty("model_path", out var value) ? value.GetString() : null;
                renderer = avatar.RootElement.TryGetProperty("renderer", out var kind) ? kind.ToString() : null;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException) { }
        }
        if (modelPath is null) throw new ArgumentException("Give modelPath (a .model3.json or .vrm), or a dataDirectory whose avatar.json shows one.");
        var vrm = modelPath.EndsWith(".vrm", StringComparison.OrdinalIgnoreCase) || renderer?.Contains("vrm", StringComparison.OrdinalIgnoreCase) == true;
        var avatarRenderer = vrm ? Martlet.Avatars.AvatarRenderer.Vrm : Martlet.Avatars.AvatarRenderer.Live2D;
        Martlet.Avatar.Hosting.CharacterActionInventory inventory;
        IReadOnlyList<Martlet.Avatar.Hosting.AvatarAsset> assets;
        string path;
        try
        {
            path = Martlet.Avatar.Hosting.BundledLive2D.IsBuiltIn(modelPath) ? Martlet.Avatar.Hosting.BundledLive2D.ModelPath(modelPath) : modelPath;
            assets = await Martlet.Avatar.Hosting.LocalAvatarFiles.ReadModelAsync(avatarRenderer, path, cancellation);
            inventory = Martlet.Avatar.Hosting.CharacterActionInventory.From(avatarRenderer, vrm ? assets[0].Name : Path.GetFileName(path), assets);
        }
        catch (Martlet.Core.Contracts.ContractException error) { throw new InvalidOperationException("The model can't be read: " + error.Message); }
        catch (IOException error) { throw new InvalidOperationException("The model can't be read: " + error.Message); }
        Martlet.Core.Settings.PromptSettings? prompts = null;
        if (directory is not null && File.Exists(Path.Combine(directory, "settings.json")))
            prompts = Martlet.Core.Settings.SettingsJson.Read(File.ReadAllBytes(Path.Combine(directory, "settings.json"))).Prompts;
        var saved = directory is null ? null : Martlet.Avatar.Hosting.CharacterActions.Load(directory, inventory.ModelId);
        var catalog = new Martlet.Avatar.Hosting.CharacterActionCatalog(inventory, Martlet.Avatar.Hosting.CharacterActions.Merge(inventory, saved));
        // Combos to rehearse ("flustered: blush hearts nod | when flattered"), read as the Combos section's boxes are; they replace
        // the saved combos when they could be saved.
        string? combosProblem = null;
        if (OptionalStrings(arguments, "combos") is { } comboLines)
        {
            var written = new List<Martlet.Avatar.Hosting.CharacterCombo>();
            foreach (var line in comboLines)
            {
                var halves = line.Split('|', 2);
                var colon = halves[0].IndexOf(':');
                var ids = Martlet.Avatar.Hosting.CharacterActions.ParseParts(colon < 0 ? "" : halves[0][(colon + 1)..], catalog.Settings.Actions,
                    out var why);
                if (ids is null)
                {
                    combosProblem ??= why;
                    continue;
                }
                written.Add(new()
                {
                    Tag = (colon < 0 ? halves[0] : halves[0][..colon]).Trim().Trim('{', '}').Trim().ToLowerInvariant(), Parts = ids,
                    Use = halves.Length > 1 && halves[1].Trim() is { Length: > 0 } use ? use : null
                });
            }
            var rehearsed = catalog.Settings with { Combos = written.Count == 0 ? null : written };
            combosProblem ??= Martlet.Avatar.Hosting.CharacterActions.Problem(rehearsed);
            if (combosProblem is null) catalog = catalog with { Settings = rehearsed };
        }
        var key = OptionalString(arguments, "engine");
        var engine = key is null or "none" ? null
            : Martlet.Core.Settings.SpeechEngines.ForKey(key) ?? throw new ArgumentException($"Unknown voice engine '{key}'.");
        object Describe(Martlet.Avatar.Hosting.CharacterActionCatalog of)
        {
            var offered = of.Offered(engine).Select(e => e.Source.Id).ToHashSet(StringComparer.Ordinal);
            return of.Entries.Select((e, n) => new
            {
                n, id = e.Source.Id, kind = e.Source.Kind.ToString().ToLowerInvariant(), name = e.Source.Name, detail = e.Source.Detail,
                tag = e.Action.Tag, cue = e.Action.Cue, use = e.Action.Use,
                hint = Martlet.Avatar.Hosting.CharacterActions.Hint(e.Source, e.Action), enabled = e.Action.Enabled,
                offered = offered.Contains(e.Source.Id),
                mode = e.Action.Mode ?? Martlet.Avatar.Hosting.CharacterActions.DefaultMode(e.Source, e.Action.Tag),
                modeSaved = e.Action.Mode is not null, vtsToggle = e.Source.Toggle
            }).ToArray();
        }
        // Lingering emotes the character would be showing ("glasses" or "glasses:12" for 12 minutes), for the note replies get.
        var now = DateTimeOffset.Now;
        var showing = (OptionalStrings(arguments, "showing") ?? []).Select(text =>
        {
            var parts = text.Split(':', 2);
            var minutes = parts.Length > 1 && int.TryParse(parts[1], System.Globalization.CultureInfo.InvariantCulture, out var m) ? m : 0;
            var source = catalog.Entries.FirstOrDefault(e => string.Equals(e.Action.Tag, parts[0].Trim('{', '}'), StringComparison.OrdinalIgnoreCase)).Source ??
                throw new ArgumentException($"No emote has the tag '{parts[0]}'.");
            return new Martlet.Avatar.Hosting.HeldEmote(source, path, now - TimeSpan.FromMinutes(minutes));
        }).ToArray();
        var reply = catalog.Prompt(engine, prompts, showing, now);
        var naming = Martlet.Avatar.Hosting.CharacterActions.NamingPrompt(inventory, prompts);
        object? parsed = null;
        if (OptionalString(arguments, "answer") is { } answer)
        {
            var named = Martlet.Avatar.Hosting.CharacterActions.Parse(answer, inventory, catalog.Settings, DateTimeOffset.Now);
            parsed = named is null ? new { read = false } : new
            {
                read = true, problem = Martlet.Avatar.Hosting.CharacterActions.Problem(named), actions = Describe(catalog with { Settings = named }),
                prompt = (catalog with { Settings = named }).Prompt(engine, prompts)?.Instructions
            };
        }
        var extras = vrm ? null : Martlet.Avatar.Hosting.LocalAvatarFiles.Extras(assets, Path.GetFileName(path));
        var voiceTag = OptionalString(arguments, "voiceTag");
        // Each blush level the model has, faintest first: Martlet's gesture, or for the blush the model's own emote tagged blush
        // when it replaces the gesture.
        var offeredNow = catalog.Offered(engine).Select(e => e.Source.Id).ToHashSet(StringComparer.Ordinal);
        var rows = catalog.Entries.Select((e, n) => (e.Source, e.Action, N: n)).ToArray();
        var blushLevels = Martlet.Avatar.Hosting.CharacterActionInventory.BlushLevels.Select((level, i) =>
        {
            var row = rows.FirstOrDefault(r => r.Source.Id == "gesture:" + level);
            if (row.Source is null) row = rows.FirstOrDefault(r => string.Equals(r.Action.Tag, level, StringComparison.OrdinalIgnoreCase));
            return row.Source is null ? null : new
            {
                level = i + 1, n = row.N, id = row.Source.Id, kind = row.Source.Kind.ToString().ToLowerInvariant(), name = row.Source.Name,
                tag = row.Action.Tag, mode = row.Action.Mode ?? Martlet.Avatar.Hosting.CharacterActions.DefaultMode(row.Source, row.Action.Tag),
                offered = offeredNow.Contains(row.Source.Id)
            };
        }).Where(level => level is not null).ToArray();
        return new
        {
            renderer = avatarRenderer.ToString(), key = inventory.ModelId[..16], files = assets.Count,
            expressions = inventory.Sources.Count(s => s.Kind == Martlet.Avatar.Hosting.CharacterActionKind.Expression),
            motions = inventory.Sources.Count(s => s.Kind == Martlet.Avatar.Hosting.CharacterActionKind.Motion),
            gestures = inventory.Sources.Where(s => s.Kind == Martlet.Avatar.Hosting.CharacterActionKind.Gesture).Select(s => s.Name).ToArray(),
            fromVTubeStudio = extras is null ? null : new
            {
                expressions = extras.Expressions.Select(e => new { e.Name, e.File }), motions = extras.Motions.Select(m => new { m.Group, m.File })
            },
            saved = saved is not null, detectedBy = catalog.Settings.DetectedBy, detectedAt = catalog.Settings.DetectedAt,
            engine = engine?.Key, actions = Describe(catalog), blushLevels,
            replyPrompt = reply?.Instructions, replyTags = reply?.Tags, showingNote = reply?.Showing,
            namingPrompt = naming is { } ask ? new { instructions = ask.Instructions, list = ask.List } : null,
            combos = catalog.Combos.Select((c, n) => new
            {
                n, tag = c.Tag,
                parts = c.Parts.Select(id => catalog.Entries.FirstOrDefault(e => e.Source.Id == id) is { Source: { } source } part
                    ? new
                    {
                        id, kind = source.Kind.ToString().ToLowerInvariant(), name = source.Name, tag = part.Action.Tag, enabled = part.Action.Enabled,
                        mode = part.Action.Mode ?? Martlet.Avatar.Hosting.CharacterActions.DefaultMode(source, part.Action.Tag)
                    }
                    : new { id, kind = "missing", name = id, tag = (string?)null, enabled = false, mode = "" }).ToArray(),
                use = c.Use, hint = catalog.Hint(c), enabled = c.Enabled, lingers = catalog.Lingers(c),
                offered = reply?.Tags.Contains("{" + c.Tag + "}") == true
            }).ToArray(),
            combosProblem,
            // Martlet's own combos: whether the model was given each one, and its row in combos while one has its tag.
            martletCombos = Martlet.Avatar.Hosting.CharacterActions.MartletCombos.Select(m => new
            {
                tag = m.Tag, parts = m.Parts, use = m.Use, startsOn = m.Enabled, given = catalog.Settings.GivenCombos?.Contains(m.Tag) == true,
                n = catalog.Combos.Select((c, i) => (c.Tag, i)).Where(c => string.Equals(c.Tag, m.Tag, StringComparison.OrdinalIgnoreCase))
                    .Select(c => (int?)c.i).FirstOrDefault()
            }).ToArray(),
            voiceTag, setsOff = voiceTag is null ? null
                : catalog.For(voiceTag).Select(s => new
                {
                    kind = s.Kind.ToString().ToLowerInvariant(), name = s.Name, holds = voiceTag.StartsWith('{') && catalog.Lingers(s)
                }).ToArray(),
            turnsOff = voiceTag is null ? null : catalog.Off(voiceTag).Select(off => new { kind = off.Kind.ToString().ToLowerInvariant(), name = off.Name })
                .ToArray(),
            combo = voiceTag is not null && catalog.Combo(voiceTag) is { } voiceCombo ? voiceCombo.Tag : null,
            parsed
        };
    }
}
