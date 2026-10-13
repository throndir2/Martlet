using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using Martlet.Core.Installation;
using Martlet.Doctor;
using Martlet.Mcp.Client;
using Martlet.Mcp.Shared;
using Martlet.Core.Settings;

namespace Martlet.Mcp;

/// <summary>martlet_guide's "Characters" area: making, changing and checking characters: personalities, looks, voices, motions
/// and touch. Each Tool(...) registers one tool: its name, description, input schema, handler and, when it needs one, its
/// permission (<see cref="ToolPermission"/>). A new tool of this area goes here.</summary>
internal sealed partial class McpServer
{
    private McpTool[] CharacterTools() =>
    [
        Tool("characters_list", "List Martlet's characters: each personality (name, whether Martlet is it now, how long its " +
            "instructions are, and the instructions themselves with includeText) with its character profile (its look and voice), " +
            "plus the looks (character models) and voices a character can use, and the limits. Use the names or IDs with the " +
            "other character_* tools. " + DataDirectoryNote + " Read-only.", new
        {
            includeText = new { type = "boolean" },
            dataDirectory = new { type = "string" }
        },
            async (arguments, cancellation) => await CharacterConfiguration.ListAsync(DataDirectory(arguments), OptionalBool(arguments, "includeText") ?? false,
                cancellation)),
        Tool("character_find_looks", "Find looks (3D VRM character models) online for a character you name: the VRoid Hub models " +
            "their creators let anyone download and use as a character, the linked VRoid Hub account's own and hearted models, and " +
            "the free (CC0 and CC-BY) models of the Open Source Avatars registry. Each look has its creator, source, license, every " +
            "condition of use, whether Martlet may use it, a picture and page address, and its look key: give that key as look to " +
            "character_create or character_update. vroidhub:<id> looks are shown from VRoid Hub with the owner's linked account and " +
            "never saved; osa:<project>/<avatar> looks are downloaded once and join the owner's characters. Only the query goes to " +
            "VRoid Hub and GitHub. sources: vroidhub, osa or both (default). " + DataDirectoryNote + " Read-only.", new
        {
            query = new { type = "string", maxLength = 100 },
            count = new { type = "integer", minimum = 1, maximum = 50 },
            sources = new { type = "array", items = new { type = "string", @enum = new[] { "vroidhub", "osa" } } },
            dataDirectory = new { type = "string" }
        }, ["query"],
            async (arguments, cancellation) => await CharacterLooks.FindAsync(DataDirectory(arguments), RequiredString(arguments, "query"),
                OptionalInt(arguments, "count"), OptionalStrings(arguments, "sources"), cancellation)),
        Tool("character_find_on_web", "The backup finder for a character's look or voice, when character_find_looks finds no model " +
            "Martlet can use or the character needs a voice: searches the web (DuckDuckGo) for pages that may have a model of it " +
            "(VRM, Live2D, MMD or VRChat; Booth, VRoid Hub, nizima, Sketchfab...) and pages with recordings of its voice (voice " +
            "lines on wikis, clips), plus searches on those sites to open. Martlet downloads nothing from them: the owner opens " +
            "them, checks the terms, downloads what they may use and adds it in the app (Companion › Character › Add a character; " +
            "Companion › Voice › Add a voice). models and voices choose what to look for (both by default). Only the query leaves " +
            "this PC. Read-only.", new
        {
            query = new { type = "string", maxLength = 100 },
            models = new { type = "boolean" },
            voices = new { type = "boolean" }
        }, ["query"],
            async (arguments, cancellation) => await CharacterLooks.FindOnWebAsync(RequiredString(arguments, "query"),
                OptionalBool(arguments, "models") ?? true, OptionalBool(arguments, "voices") ?? true, cancellation)),
        Tool("character_create", "Make a new character in Martlet: a personality (name and personality, the instructions that say who " +
            "the character is and how it talks, up to 8,192 characters) and a character profile that gives it a look and a voice " +
            "(look: 'builtin', a look's name or ID from characters_list, or a look key from character_find_looks; voice: a voice's " +
            "name or ID; both optional, profile " +
            "false makes only the personality). Or import a SillyTavern, Chub or other Tavern character card with cardPath (an " +
            "absolute path to a PNG, JSON or CHARX card; name and personality then override the card's), which also keeps the card's " +
            "keyword lorebook entries for it. Martlet becomes the new character at once unless use is false; a running Martlet " +
            "follows by itself. Needs the server started with --allow-changes. " + DataDirectoryNote, new
        {
            name = new { type = "string", maxLength = 64 },
            personality = new { type = "string", maxLength = 8192 },
            cardPath = new { type = "string" },
            look = new { type = "string", maxLength = 128 },
            voice = new { type = "string", maxLength = 128 },
            profile = new { type = "boolean" },
            use = new { type = "boolean" },
            dataDirectory = new { type = "string" }
        },
            async (arguments, cancellation) => await CharacterConfiguration.CreateAsync(DataDirectory(arguments),
                OptionalString(arguments, "name"), OptionalString(arguments, "personality"), OptionalString(arguments, "cardPath"),
                OptionalString(arguments, "look"), OptionalString(arguments, "voice"), OptionalBool(arguments, "profile"),
                OptionalBool(arguments, "use"), cancellation), ToolPermission.Changes),
        Tool("character_update", "Change a character: character is its name, ID or profile key (from characters_list); name renames " +
            "it, personality replaces its instructions, look and voice change its profile's look and voice (look may also be a look " +
            "key from character_find_looks; 'keep' keeps whatever " +
            "Martlet uses now; a character without a profile gets one). Needs --allow-changes. " + DataDirectoryNote, new
        {
            character = new { type = "string", maxLength = 128 },
            name = new { type = "string", maxLength = 64 },
            personality = new { type = "string", maxLength = 8192 },
            look = new { type = "string", maxLength = 128 },
            voice = new { type = "string", maxLength = 128 },
            dataDirectory = new { type = "string" }
        }, ["character"],
            async (arguments, cancellation) => await CharacterConfiguration.UpdateAsync(DataDirectory(arguments),
                RequiredString(arguments, "character"), OptionalString(arguments, "name"), OptionalString(arguments, "personality"),
                OptionalString(arguments, "look"), OptionalString(arguments, "voice"), cancellation), ToolPermission.Changes),
        Tool("character_use", "Switch Martlet to a character (its name, ID or profile key): Martlet talks with its personality from " +
            "the next reply. A profile's look and voice change on this PC when the profile is used in the app; the result says how " +
            "(followUp). Needs --allow-changes. " + DataDirectoryNote, new
        {
            character = new { type = "string", maxLength = 128 },
            dataDirectory = new { type = "string" }
        }, ["character"],
            async (arguments, cancellation) => await CharacterConfiguration.UseAsync(DataDirectory(arguments),
                RequiredString(arguments, "character"), cancellation), ToolPermission.Changes),
        Tool("character_delete", "Delete a character (its name, ID or profile key): its personality and its profiles. profileOnly " +
            "removes only the profile and keeps the personality. Martlet keeps at least one personality; looks and voices stay. " +
            "Needs --allow-changes. " + DataDirectoryNote, new
        {
            character = new { type = "string", maxLength = 128 },
            profileOnly = new { type = "boolean" },
            dataDirectory = new { type = "string" }
        }, ["character"],
            async (arguments, cancellation) => await CharacterConfiguration.DeleteAsync(DataDirectory(arguments),
                RequiredString(arguments, "character"), OptionalBool(arguments, "profileOnly") ?? false, cancellation), ToolPermission.Changes),
        Tool("f5_voices", "List Martlet's starter voices (key, name, female, cute, licence, transcript, format; each clip is checked " +
            "against its SHA-256 and F5's reference rules; a new voice list starts with them, after which they are ordinary voices) and " +
            "the default voice; from a data directory, the shared speaking-voice list (speaking-voices.json: live voices, which starter " +
            "voices are in it or removed, how many of the owner's own, tombstones, the voice chosen on all computers and, for each voice " +
            "made from several recordings, how many, their lengths and which engines learn from each or hear them joined), this PC's " +
            "recordings (the F5 voice store: which starter voices, how many own and which is applied); which voice the speaking route " +
            "uses and on which self-hosted engine, host and model; and the voice engines (Chatterbox " +
            "Turbo, the default; F5-TTS; XTTS-v2; GPT-SoVITS; Dia: host role, gateway route, model, weights licence, GPU memory, reference " +
            "length bounds, tag catalog, summary, languages, whether it learns from several recordings, the feature chips Companion > " +
            "Voice > Voice engine shows, its rundown (voice cloning, laughs & sighs, emotions: yes, partly or no) and where it runs " +
            "(GPU with its typical and peak graphics memory, CPU or online); the same rundown for the OpenAI voice; each " +
            "starter voice lists the engines that can clone it and its language) with the one chosen on this desktop " +
            "(never own voices' names, transcripts or audio). Plays nothing and contacts nothing.", new
        {
            dataDirectory = new { type = "string" }
        },
            arguments => F5Voices(arguments)),
        Tool("character_models", "Read the shared character models from a data directory (character-models.json and the copies in " +
            "character-models, docs/CLUSTER.md \"The shared character models\"): live characters and tombstones, total size, and for " +
            "each character its key (first 16 hex digits of its ID, as in CharacterModelState-<key>), renderer, files, pieces, size, " +
            "the computer it was added on, whether this PC's copy is complete and whether this PC shows it (avatar.json); copies " +
            "still waiting in character-models-incoming; and what this PC shows (built-in, a shared copy, a copy no longer listed or " +
            "a model file outside the list). Never returns character names or file paths. Read-only; contacts nothing.", new
        {
            dataDirectory = new { type = "string" }
        },
            arguments => CharacterModels(arguments)),
        Tool("character_profiles", "Read the character profiles (Companion > Profiles) from a data directory: each profile's key (first " +
            "8 hex digits of its ID, as in CharacterProfileState-<key>), whether its personality is saved, what its look is (keep, " +
            "builtin, a listed character whose copy is ready or still copying here, or missing) and its voice (keep, listed or " +
            "missing); the profile switched to last; which profile matches what Martlet uses now (the active persona, the look " +
            "in avatar.json and the voice the speaking route keeps or the shared list chose); and what each profile keeps on this " +
            "PC (character-profiles-local.json: its place, size, monitor and lock, its usual gaze and whether replies may change " +
            "it, and which touches stop it while it talks) with the profile whose choices this PC uses; and how the account in use shares " +
            "each profile with the household (sharing: private, copy, together, or joined for someone else's character shared together; " +
            "null without accounts). Never returns names. " +
            "Read-only; contacts nothing.", new
        {
            dataDirectory = new { type = "string" }
        },
            arguments => CharacterProfiles(arguments)),
        Tool("character_actions", "Read a character model's emotes and motions as Companion > Emotes and motions uses " +
            "them (Martlet.Avatar.Hosting, docs/AVATARS.md \"Emotes and motions\"): modelPath (a .model3.json or .vrm on this PC) or the " +
            "model dataDirectory's avatar.json shows. Returns the renderer, the model's key, how many files the renderer reads (a VTube " +
            "Studio model's .vtube.json and loose .exp3/.motion3 files included) and what came from VTube Studio's settings, then each " +
            "expression, motion group and Martlet gesture the model's rig supports (nod, shake, tilt, bow, sway; the blush levels on every model, " +
            "faintest first: blush, blush_deep and blush_fierce, one showing at a time; the blush is the model's own ParamCheek or blush expression, " +
            "or a glow Martlet draws on the cheeks when it has neither, and Martlet draws the stronger levels over the model's own blush; " +
            "Live2D smile, surprise; VRM wave, shrug, bounce; " +
            "and the voice emotes linked to every voice sound and tone: laugh, chuckle, sigh, gasp, cough, clear_throat, groan, sniff, shush, inhale, exhale, " +
            "mumble, hum, sneeze, whistle, happy, sarcastic, angry, fear, crying, whispering, dramatic; and the overlay emotes drawn over the face of any " +
            "Live2D model or VRM with a head: sweat, anger, hearts, sparkles, tears, gloom, question, exclaim, sleepy, music; then the held face parts " +
            "eyes_up (Live2D ParamEyeBallY, VRM eye bones) and mouth_open (Live2D ParamMouthOpenY, a VRM's oh or aa mouth), which stay on with held " +
            "gestures that move other parts of the face; and last more overlay emotes: heart_eyes, star_eyes, tongue_out, drool, steam, dizzy, idea, " +
            "ellipsis) with what it changes, its tag, voice cue, when to use it (use: the owner's or the Thinking model's text, null when empty; hint: what the reply prompt says, which is Martlet's own hint while use is null), whether " +
            "it is on, its mode (brief, or lingering: stays on after {tag} until {/tag}; modeSaved false when it is the default, " +
            "vtsToggle when a VTube Studio ToggleExpression hotkey turns it on) and whether replies are offered it for engine (a voice engine key; \"none\" or absent: a voice without tags); the " +
            "saved settings (character-actions.json in dataDirectory) or the defaults from the model's names; " +
            "blushLevels (the model's blush levels, faintest first: level, n, id, kind, name, tag, mode and offered); the reply prompt and tags " +
            "(lingering emotes add their {/tag} off tags); with showing (tags the character would show now, as \"glasses\" or " +
            "\"glasses:12\" for 12 minutes) also showingNote, the line the newest message's notes get; " +
            "and the Thinking naming prompt. With voiceTag (a voice's tag such as \"[laugh]\" or \"(sighs)\", or a reply tag such as " +
            "\"{nod}\", \"{/glasses}\" or a combo's \"{flustered}\" or \"{/flustered}\"), also what it sets off (setsOff: kind, name and " +
            "whether it holds; with several expressions or motions on one cue, one picked at random; a combo's parts that are on) or " +
            "turns off (turnsOff: the lingering emote, or a combo's lingering parts) and the combo it names (combo). " +
            "Also the owner's combos for the model (combos: n as in CharacterComboTag-<n>, tag, each part's id, kind, name, tag, " +
            "whether it is on and its mode, use, hint, whether it is on, whether it has a lingering part and whether replies are " +
            "offered it); with combos (strings such as \"flustered: blush hearts nod | when flattered\", parsed as the Combos " +
            "section's boxes are) those replace the saved ones, or combosProblem says why they can't be saved. " +
            "With answer (a simulated Thinking reply such as \"1: blush | - | when shy\"), also what the " +
            "production parser makes of it. Reads only; contacts nothing and never returns the model's path.", new
        {
            dataDirectory = new { type = "string" }, modelPath = new { type = "string" }, engine = new { type = "string" },
            answer = new { type = "string" }, voiceTag = new { type = "string" }, showing = new { type = "array", items = new { type = "string" } },
            combos = new { type = "array", items = new { type = "string" } }
        },
            async (arguments, cancellation) => await CharacterActionsCheckAsync(arguments, cancellation)),
        Tool("character_physical_check", "What Martlet makes of a stroke across the locked character and of moves and zooms " +
            "(Martlet.Avatar.Hosting CharacterStrokes and CharacterPhysicalWords with Martlet.Conversation's TouchLedger), headless, " +
            "with no desktop and no model request. stroke is a JSON CharacterStroke {\"id\",\"phase\":\"end\",\"aspect\",\"samples\":" +
            "[{\"x\",\"y\",\"ms\",\"touch\":CharacterTouch or null}]} summarized against the touch zones saved for modelId in dataDirectory " +
            "(or the rough zones before any were found): zones crossed (where zones overlap, each one it was on), main zone, ms, length, speed, pace (slow, steady or quick), " +
            "passes, dx and dy (where it ended from where it began, page heights), sideways and way (down, up or null), and words: how " +
            "the ledger says its whole path (where, such as \"down from your chest over your stomach to your thighs\", label, pace, " +
            "times). changes is a JSON array of RendererPhysical {\"kind\":\"moved|home|zoomed|zoom_reset|panned\",\"dx\",\"dy\"," +
            "\"screenWidth\",\"fromScreen\",\"toScreen\",\"zoomFrom\",\"zoomTo\",\"focus\"}. Returns each change's ledger kind and words, " +
            "the plain line the next reply would carry (\"They slowly stroked your hair 4 times, then moved you to their other " +
            "monitor.\"), the history line, whether it would start a reply on its own (touches, strokes and moves do), character " +
            "(what the talk window and the Thinking model call the character: the name of the persona dataDirectory's settings " +
            "use, else Martlet) and note (the talk window's note for a reply to them alone, \"You touched Ivy (touch: hair " +
            "stroke x4, moved)\", or null when they wouldn't start one). noticeAll " +
            "(default true) treats every zone as having Martlet notices on; false uses the zones' own setting. personaId takes how " +
            "that persona feels about the zones from character-temperaments.json (feeling). earlier (0 to 20) records the same " +
            "stroke that many times before, a minute apart, each taken by a reply, so the line names the places the user keeps " +
            "coming back to (often). said and answering stand for a touch that stopped Martlet talking (what it had said aloud and " +
            "the message it was answering). touchInterrupts (any, intimate or never; default any) says whether the stroke would " +
            "stop Martlet talking (interrupts). Returns told (what Thinking hears of the touches), message (a touch-only reply's " +
            "whole message, Companion > Prompts > Touched) and notes (Touched, with your message), with the prompts saved in " +
            "dataDirectory's settings.json.", new
        {
            dataDirectory = new { type = "string" }, modelId = new { type = "string" }, stroke = new { type = "string" },
            changes = new { type = "string" }, noticeAll = new { type = "boolean" }, personaId = new { type = "string" },
            earlier = new { type = "integer", minimum = 0, maximum = 20 }, said = new { type = "string", maxLength = 4096 },
            answering = new { type = "string", maxLength = 4096 }, touchInterrupts = new { type = "string", @enum = new[] { "any", "intimate", "never" } }
        },
            arguments => PhysicalCheck.Run(DataDirectory(arguments), OptionalString(arguments, "modelId"),
                OptionalString(arguments, "stroke"), OptionalString(arguments, "changes"), OptionalBool(arguments, "noticeAll") ?? true,
                OptionalString(arguments, "personaId"), OptionalInt(arguments, "earlier") ?? 0, OptionalString(arguments, "said"),
                OptionalString(arguments, "answering"), OptionalString(arguments, "touchInterrupts"))),
        Tool("character_touch_zones", "Companion > Touch > Touch zones (Martlet.Avatar.Hosting CharacterTouchZones and TouchZoneDetection; " +
            "docs/AVATARS.md \"Touch zones\") with NO vision request: the zones Martlet knows (which are intimate, and the defaults Detect " +
            "zones looks for), the step-by-step vision requests for the model (parts on the whole character, zones on each close-up, " +
            "what is special about the character on the whole character, checks of the numbered boxes; wanted: the default zones and the " +
            "ones the owner added, required: the ones it must end with, special: at most maximum zones special to the character, each a " +
            "zone of its own named as the model sees it (an extra such as animal ears or a tail keeps Martlet's ID), and the special zones " +
            "found before, whose IDs the model is asked to keep), " +
            "what the production parser makes " +
            "of answer (a simulated vision reply about the whole picture: JSON boxes as fractions or named edges, pixels of a width x height " +
            "picture or Qwen-style 0..1000 bbox_2d grounding) bound to probe (a simulated renderer zones probe: {\"drawables\":[{\"id\",\"left\"," +
            "\"top\",\"right\",\"bottom\",\"part\"}],\"bones\":[{\"bone\",\"x\",\"y\"}],\"parts\":[{\"id\",\"name\",\"parent\"}]} in page fractions, " +
            "parts being a Live2D model's own parts with their DisplayInfo names, such as 头 or 右腿; or probePath, a probe.json as Detect zones " +
            "keeps it with the pictures it sent, which also gives crop) with crop (\"left,top,width,height\": where the " +
            "snapshot sat on the page); hints shows what the probe's part names place (areas, with the character's own side) and the " +
            "close-ups' windows they give; the zones saved for the model (modelPath, modelId or the model dataDirectory's avatar.json shows) in " +
            "character-touch-zones.json with what its last detection sent, and with touch (a CharacterTouch: {\"x\",\"y\",\"hitAreas\"," +
            "\"drawables\",\"bone\",\"node\",\"hair\",\"mesh\",\"material\",\"wholeX\",\"wholeY\",\"restX\",\"restY\",\"restWholeX\"," +
            "\"restWholeY\"}; rest* is where the touched point was in the rest pose, which the boxes compare with when given) the zone " +
            "it lands in, how it was found, whether it was traced to the rest pose (traced), the point compared (at), touched (every " +
            "zone it lands in, that one first: where zones overlap, each other zone in use whose box holds the point, on the same " +
            "part of the body as the touch, leaving out a zone whose box frames a smaller one there), what it plays (the matched " +
            "zone's reaction), noticing (the touched zones Martlet notices) and noticed (the line Martlet hears for them all, such as " +
            "\"They poked your groin and your left thigh once.\"). detect runs the production detection on snapshotPath (a PNG of the character, transparent " +
            "around it), composing and encoding every picture it would send (previewDirectory keeps them), with a FIXTURE - NOT AI stand-in " +
            "that answers from answer's zones (guess, a wrong first answer, makes the checks correct it; checks sets the rounds, 0 to 5; " +
            "failAt makes that request fail, as a model that stopped answering; answer's zones may also name things special to the " +
            "character, such as {\"id\":\"ribbon\",\"name\":\"hair ribbon\",...}, which the stand-in lists when asked what is special; " +
            "special sets the most special zones, 0 to 10, 6 by default, 0 not asking); with includeIntimate on (the default) the intimate zones " +
            "must be found: asked for again on the whole character, then worked out from the zones around them; it " +
            "reports each request, the steps and how far the found boxes are from answer's. add (zone IDs, comma-separated, such as " +
            "\"hand_left,tail\") adds zones as the owner does with Add zone (added, in the middle of the picture; one the model has is " +
            "marked added): detect then looks for them too and must end with them, and one it can't place stays where it was. estimate " +
            "runs the first guess the Touch zones page places on a model with no zones and no picture (TouchZoneDetection.Estimate) on " +
            "snapshotPath with the probe's hints (its face, named parts and skeleton), with no vision request: the zones it places, the " +
            "wanted ones it couldn't place and the steps (what placed each). save writes " +
            "the parsed (or detected, or estimated) zones (or, with add alone, the zones with the ones added) (and " +
            "snapshotPath as their picture, and with detect the pictures sent; includeIntimate sets Include intimate zones) into an explicit, " +
            "disposable dataDirectory as Detect zones would, each zone with no reaction list yet given the one a new zone gets (as " +
            "the desktop saves them). Each saved zone reports reactions (its reaction list as saved, in play order: emote, gesture and " +
            "motion IDs and sound:<cue> voice sounds, every entry kept; null when not filled yet), seed (the list a new zone gets: " +
            "Defaults), plays (what it plays on the model) and sounds (the cues of its voice sounds). temperament (a simulated Thinking answer for Touch temperament: {\"groups\":{\"head\":" +
            "{\"attitude\":2,\"reactions\":[\"hearts\",\"blush\"],\"linger\":3}},\"zones\":{...},\"escalation\":{\"after\":3,...}}) or " +
            "personaId (the temperament that persona uses in the dataDirectory's character-temperaments.json: its own, the built-in " +
            "reactions or a custom one) seeds the list of a zone not filled yet and gives every zone its attitude, linger, look and, " +
            "with repeats (touches in a " +
            "row), escalation; personality shows the request Thinking gets. The result's temperament also lists the categories with " +
            "the zone kinds each covers (intimate holds every intimate kind), the custom temperaments and which temperament each persona " +
            "uses. Contacts nothing; never returns the model's path.", new
        {
            dataDirectory = new { type = "string" }, modelPath = new { type = "string" }, modelId = new { type = "string" },
            answer = new { type = "string" }, width = new { type = "integer" }, height = new { type = "integer" },
            crop = new { type = "string" }, probe = new { type = "string" }, touch = new { type = "string" },
            save = new { type = "boolean" }, includeIntimate = new { type = "boolean" }, snapshotPath = new { type = "string" },
            temperament = new { type = "string" }, personaId = new { type = "string" }, personality = new { type = "string" },
            repeats = new { type = "integer", minimum = 1 }, detect = new { type = "boolean" }, guess = new { type = "string" },
            previewDirectory = new { type = "string" }, checks = new { type = "integer", minimum = 0, maximum = 5 },
            failAt = new { type = "integer", minimum = 1 }, probePath = new { type = "string" }, add = new { type = "string" },
            estimate = new { type = "boolean" }, special = new { type = "integer", minimum = 0, maximum = 10 }
        },
            async (arguments, cancellation) => await TouchZonesCheck.RunAsync(DataDirectory(arguments),
                OptionalString(arguments, "dataDirectory") is not null, OptionalString(arguments, "modelPath"), OptionalString(arguments, "modelId"),
                OptionalString(arguments, "answer"), OptionalInt(arguments, "width"), OptionalInt(arguments, "height"),
                OptionalString(arguments, "crop"), OptionalString(arguments, "probe"), OptionalString(arguments, "touch"),
                OptionalBool(arguments, "save") ?? false, OptionalBool(arguments, "includeIntimate"), OptionalString(arguments, "snapshotPath"),
                cancellation, OptionalString(arguments, "temperament"), OptionalString(arguments, "personaId"), OptionalString(arguments, "personality"),
                OptionalInt(arguments, "repeats"), OptionalBool(arguments, "detect") ?? false, OptionalString(arguments, "guess"),
                OptionalString(arguments, "previewDirectory"), OptionalInt(arguments, "checks"), OptionalInt(arguments, "failAt"),
                OptionalString(arguments, "probePath"), OptionalString(arguments, "add"), OptionalBool(arguments, "estimate") ?? false,
                OptionalInt(arguments, "special"))),
        Tool("character_reaction_changes", "Companion > Touch > Changes the character made (Martlet.Avatar.Hosting " +
            "CharacterReactionChanges and CharacterReactionTools; docs/CONVERSATION.md \"How I react\") with NO model request: the " +
            "Touch reactions tool set the How I react check-in calls (its tools and limits), the changes the character made itself in " +
            "the dataDirectory's character-reaction-changes.json (what, why, when, until, whether in effect, who ended it), and what " +
            "each zone of the model (modelId, or modelPath to read its emotes; else a FIXTURE catalog of Martlet's gestures) plays and " +
            "how personaId's character feels about each category and zone before and after. tool (read_touch_reactions, " +
            "change_touch_feeling, change_zone_reactions, set_touch_mood or undo_touch_change) with arguments (the JSON a model would " +
            "send, such as {\"target\":\"head\",\"feeling\":\"dislikes\",\"why\":\"...\"}) runs one call as the desktop runs it, with " +
            "every bound; undo (a change id, or all) ends changes as the owner's Undo does. save writes the call's change or the undo " +
            "into an explicit, disposable dataDirectory; at (an ISO time) sets the time; checkInId the check-in that calls (reactions). " +
            "read is what read_touch_reactions returns after it. Contacts nothing.", new
        {
            dataDirectory = new { type = "string" }, personaId = new { type = "string" }, modelId = new { type = "string" },
            modelPath = new { type = "string" }, tool = new { type = "string" }, arguments = new { type = "string" },
            undo = new { type = "string" }, save = new { type = "boolean" }, at = new { type = "string" }, checkInId = new { type = "string" }
        },
            async (arguments, cancellation) => await ReactionChangesCheck.RunAsync(DataDirectory(arguments),
                OptionalString(arguments, "dataDirectory") is not null, OptionalString(arguments, "personaId"), OptionalString(arguments, "modelId"),
                OptionalString(arguments, "modelPath"), OptionalString(arguments, "tool"), OptionalString(arguments, "arguments"),
                OptionalString(arguments, "undo"), OptionalBool(arguments, "save") ?? false, OptionalString(arguments, "at"),
                OptionalString(arguments, "checkInId"), cancellation)),
        Tool("character_eyes", "Companion > Eyes > Where the face is (Martlet.Avatar.Hosting CharacterEyes; docs/AVATARS.md \"Eyes\") " +
            "with NO vision request: the request the vision model gets (a close-up of the face, 1.6 face widths square, about 768 pixels " +
            "with a grid; its instructions, message, the check message and the message after an unreadable answer), what the production " +
            "parser, checks and conversion make of answer (a simulated vision reply about the close-up: {\"left\":{\"iris\":{\"left\",\"top\"," +
            "\"right\",\"bottom\"},\"eye\":{...},\"cheek\":{...}},\"right\":{...},\"mouth\":{...}} as fractions, pixels or 0..1000, or flat keys " +
            "such as left_iris and left_cheek) and, when it fails, of second (the answer to the check with its boxes drawn and numbered, " +
            "or to the question again): the steps, the boxes (eyes, cheeks, mouth), the problems Martlet's checks found and the renderer's " +
            "face hint (each eye's iris {x,y,r} and opening {x,y,rx,ry}, and cheekLeft, cheekRight and mouth {x,y,r} when they pass, in " +
            "face widths from the face's middle, roll removed). Without snapshotPath the close-up is exactly 1.6 face widths around an " +
            "upright face; with snapshotPath (a PNG of the character, transparent around it) and face (\"x,y,width[,rollDegrees]\", the " +
            "face's middle and width as fractions of the snapshot) the production close-up is composed and encoded as the desktop sends " +
            "it (previewDirectory keeps the pictures). save writes the measurement (with the pictures and the picture of its boxes) for " +
            "the model (modelPath, modelId or the model dataDirectory's avatar.json shows) into an explicit, disposable dataDirectory, as " +
            "Measure the face would (marked FIXTURE - NOT AI); forget removes it. eyesFrom (mesh, bones, vision or estimate: what the " +
            "renderer says the eyes use) shows the status line the section shows. Contacts nothing; never returns the model's path.", new
        {
            dataDirectory = new { type = "string" }, modelPath = new { type = "string" }, modelId = new { type = "string" },
            answer = new { type = "string" }, second = new { type = "string" }, snapshotPath = new { type = "string" }, face = new { type = "string" },
            previewDirectory = new { type = "string" }, save = new { type = "boolean" }, forget = new { type = "boolean" },
            eyesFrom = new { type = "string", @enum = new[] { "mesh", "bones", "vision", "estimate" } }
        },
            async (arguments, cancellation) => await CharacterEyesCheck.RunAsync(DataDirectory(arguments), OptionalString(arguments, "dataDirectory") is not null,
                OptionalString(arguments, "modelPath"), OptionalString(arguments, "modelId"), OptionalString(arguments, "answer"),
                OptionalString(arguments, "second"), OptionalString(arguments, "snapshotPath"), OptionalString(arguments, "face"),
                OptionalString(arguments, "previewDirectory"), OptionalBool(arguments, "save") ?? false, OptionalBool(arguments, "forget") ?? false,
                OptionalString(arguments, "eyesFrom"), cancellation)),
        Tool("character_gaze", "Where the character looks (Companion > Eyes > Where the character looks, the overlay's Eyes " +
            "menu and Companion > Vision > Glances at your screen; docs/SCREEN_COMMENTARY.md \"Where the character looks\"): usual " +
            "is the usual gaze saved in a data directory's talk-preferences.json (GazeUsual: personality, mouse, near, ahead or " +
            "window; GazeFree: whether the character may change it in replies), the gaze of the touch temperament the persona uses in " +
            "character-temperaments.json (its own or a custom one; the active persona, or personaId), the gaze that applies and who set it, what every reply is told about it and the " +
            "note while its own choice holds the eyes. aim rehearses the production CharacterGaze.Aim the overlay runs for each " +
            "gaze (a mouse far from and near the character, a window, where you work in it, a touch's look at the mouse, a glance), " +
            "and watch rehearses the production WindowWatch the window gaze uses on a sequence of moments (switching windows, the " +
            "pointer moving over the window or onto the character, typing, the desktop in front), each with what the eyes watch " +
            "(pointer, text cursor or middle) and where. saved is the glances " +
            "choice (DecideGaze: usual gaze unless Martlet decides), then a rehearsal of the production decision " +
            "(Martlet.Avatar.Hosting CharacterGaze and GazeDirector) on " +
            "generated 1920x1080 pictures (NOT screenshots; nothing is captured): a notification popping up, the same spot again soon " +
            "and later, another change right after a glance, a notification behind the character, the character's own motion, its " +
            "speech bubble, a new scene, a change by the mouse and changes all over, each with the expected and actual verdict and " +
            "the spot looked at (ok: all scenarios, aims and watch moments as expected). Also where each look tag points on one and two screens, " +
            "the gaze tags, the screen glance's look instructions (the data directory's edited prompts included) and what the " +
            "production segmenter makes of answers with look tags (spoken, shown, quiet, the look and gaze cues); answer replaces " +
            "the sample answers. Reads only; contacts nothing.", new
        {
            dataDirectory = new { type = "string" }, answer = new { type = "string", maxLength = 2000 }, personaId = new { type = "string" }
        },
            arguments => GazeCheck.Run(DataDirectory(arguments), OptionalString(arguments, "answer"), OptionalString(arguments, "personaId"))),
        Tool("character_theme", "A character model's colors and palettes as Settings > Appearance makes them (docs/UI_DESIGN.md " +
            "\"Character palettes\"): modelPath (a .model3.json or .vrm on this PC), else the model dataDirectory's avatar.json shows, " +
            "else the built-in character. Returns the main colors read from its textures (hex, share, kind, name), the colors the " +
            "rules build from (tint, accents, lightest and darkest), Martlet's rule-based light and dark palettes with their " +
            "lowest contrasts and any rule problems, and whether dataDirectory's character-themes.json has the model's colors. With " +
            "previewDirectory (an absolute folder), writes PNG pictures of Martlet's window in each palette (label names the files). " +
            "Never returns the model's path.", new
        {
            dataDirectory = new { type = "string" }, modelPath = new { type = "string" }, previewDirectory = new { type = "string" },
            label = new { type = "string", maxLength = 40 }
        },
            async (arguments, cancellation) => await CharacterThemeCheck.RunAsync(OptionalString(arguments, "modelPath"), OptionalString(arguments, "dataDirectory"),
                OptionalString(arguments, "previewDirectory"), OptionalString(arguments, "label"), cancellation)),
        Tool("character_models_selftest", "Rehearse the shared character models end to end with the production code: two real " +
            "gateways on 127.0.0.1 (pinned TLS, in-memory character-models.json and pieces) and three simulated desktops using the " +
            "desktop's paired client and Martlet.Avatar.Hosting's import and reconcile engine over a temporary folder, with generated " +
            "Live2D-folder and VRM-file fixtures (NOT real models; nothing rendered). Checks importing (copies read exactly like the " +
            "originals with the renderer's file reader), sharing the list and every 3 MiB piece, a new desktop copying both characters, " +
            "resuming an interrupted copy, passing characters on to a host the first desktop never reached, removal everywhere (host " +
            "pieces and desktop copies deleted), keeping the copy a computer shows, stale copies, a host restart, piece checks and the " +
            "list's limits. Loopback only; the temporary folder is deleted and the credential vault is not touched.", new { },
            async (_, cancellation) => await NodeLinkCheckAsync(cancellation, "characters")),
        Tool("motion_status", "Read where Martlet makes body motions for VRM characters (docs/MOTION_HOST.md) for a data directory: " +
            "the Motion list (Companion › Motion, the motion area of pools.json: its members in order, each on or off and kept for " +
            "which companion PCs, and the computers this PC tries with the production planner MotionPool.Members), the older motion " +
            "choice it was made from (motion.json, the shared motion setting: automatic, off or one computer; used while no list " +
            "exists), the computers the shared plan (cluster.json) says run the HY-Motion Motion role and whether this PC is paired " +
            "with each (hosts.json), the computer a motion goes to first, the fixture switch (MARTLET_MOTION_FIXTURE=1), the route " +
            "(martlet.gateway.motion.v1), the two models and their graphics memory minimums, and the request bounds (seconds, prompt " +
            "length and words, seed). Files only; read-only.", new
        {
            dataDirectory = new { type = "string" },
            deviceId = new { type = "string", maxLength = 64 }
        },
            arguments => MotionCheck.Status(DataDirectory(arguments), OptionalString(arguments, "deviceId"))),
        Tool("motion_pool_check", "Rehearse the motion pool (MotionPool through WorkQueue, what the desktop's PoolMotionMaker runs) with " +
            "simulated motion computers, NOT real hosts or models: the Motion list's order (members on, paired here and kept for this PC; " +
            "This PC as its own host service; an empty list or none on is off), the older choice before a list (automatic: every computer " +
            "running the role, this PC's own first; one computer; off), the first list made from it, a free first computer taking the " +
            "motion with no other asked, a busy one (motion.busy) or one not answering passed over for the next, every one busy waiting " +
            "for the first to free, a motion that fails on its computer not made again elsewhere, and an empty list refused " +
            "(motion.unavailable). Returns ok and each step.", new { },
            async (_, cancellation) => await MotionCheck.PoolCheckAsync(cancellation)),
        Tool("motion_check", "Make one body motion from an English prompt and report the VRMA clip (VRM Animation): frames, fps, " +
            "seconds, seed, bytes, SHA-256 and its glTF facts (VRMC_vrm_animation version, humanoid bones, channels, keyframes). " +
            "place \"fixture\" (the default: the FIXTURE - NOT AI wave maker), \"worker\" (a Motion role's worker on loopback at " +
            "endpoint, such as http://127.0.0.1:50090/: GET /status, then POST /generate; a worker started with serve --random-weights " +
            "runs HY-Motion's code with RANDOM weights, FIXTURE - NOT AI) or \"host\" (through a paired computer's gateway route as " +
            "the desktop asks it: dataDirectory with its pairings, hostId or the first computer of the Motion list motion_status names). free unloads the " +
            "model after. With saveDirectory (absolute) it writes motion.vrma there. A real motion needs an NVIDIA GPU with 24 GB+.", new
        {
            place = new { type = "string", @enum = new[] { "fixture", "worker", "host" } },
            endpoint = new { type = "string", maxLength = 64 },
            dataDirectory = new { type = "string", maxLength = 260 },
            hostId = new { type = "string", maxLength = 128 },
            prompt = new { type = "string", maxLength = Martlet.Core.Motion.MotionRequest.MaximumPromptCharacters },
            seconds = new { type = "number", minimum = Martlet.Core.Motion.MotionRequest.MinimumSeconds, maximum = Martlet.Core.Motion.MotionRequest.MaximumSeconds },
            seed = new { type = "integer", minimum = 0, maximum = Martlet.Core.Motion.MotionRequest.MaximumSeed },
            free = new { type = "boolean" },
            saveDirectory = new { type = "string", maxLength = 260 }
        },
            async (arguments, cancellation) => await MotionCheck.RunAsync(OptionalString(arguments, "place"), OptionalString(arguments, "endpoint"),
                OptionalString(arguments, "dataDirectory") is null ? null : DataDirectory(arguments), OptionalString(arguments, "hostId"),
                OptionalString(arguments, "prompt"), OptionalDouble(arguments, "seconds"), OptionalInt(arguments, "seed"),
                OptionalBool(arguments, "free") == true, OptionalString(arguments, "saveDirectory"), cancellation)),
        Tool("character_status", "Read Companion > Personality and Character as saved in a data directory (they save on their own, " +
            "with no Save button): the personas (name, whether Martlet uses it, speech breaks, instruction " +
            "length; never the instructions), the character model (built-in character name or the own model's file type, never its " +
            "path; renderer, lip-sync mode, show at startup, the lip-sync host's ID), whether the character's position is locked on " +
            "this PC and where (placement), whether clicks pass through the character on this PC (clickThrough), whether Martlet's " +
            "voice is muted (voice: Speak Martlet's replies aloud, which the " +
            "character's Mute voice / Unmute voice menu item changes) and the lorebooks (counts only). Read-only.", new
        {
            dataDirectory = new { type = "string" }
        },
            async (arguments, cancellation) => await CharacterStatusAsync(arguments, cancellation))
    ];

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

    /// <summary>The character profiles saved in a data directory (Companion › Profiles), by key, with whether each part
    /// resolves here and which one matches what Martlet uses now. Names are the owner's and are never returned.</summary>
    private static object CharacterProfiles(JsonElement arguments)
    {
        var directory = DataDirectory(arguments);
        var settingsPath = Path.Combine(directory, "settings.json");
        var settings = File.Exists(settingsPath) ? Martlet.Core.Settings.SettingsJson.Read(File.ReadAllBytes(settingsPath)) : null;
        var companion = settings?.Companion;
        string? shownPath = null;
        try
        {
            using var avatar = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(directory, "avatar.json")));
            shownPath = avatar.RootElement.TryGetProperty("model_path", out var value) ? value.GetString() : null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException) { }
        var library = Martlet.Avatar.Hosting.SharedCharacterModels.Load(directory) ?? Martlet.Core.Characters.CharacterModelLibrary.Empty;
        var modelNow = shownPath is null || Martlet.Avatar.Hosting.BundledLive2D.IsBuiltIn(shownPath)
            ? Martlet.Core.Settings.CharacterProfile.BuiltInModel
            : Martlet.Avatar.Hosting.SharedCharacterModels.ForPath(directory, library, shownPath)?.Id;
        Martlet.Core.Voices.SpeakingVoiceLibrary voices;
        try { voices = Martlet.Core.Voices.SpeakingVoiceLibrary.Parse(File.ReadAllBytes(Path.Combine(directory, "speaking-voices.json"))); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or Martlet.Core.Contracts.ContractException)
        {
            voices = Martlet.Core.Voices.SpeakingVoiceLibrary.Empty.Seed(Martlet.F5.F5SharedVoices.Starters);
        }
        var voiceNow = settings?.Setup?.Routes.FirstOrDefault(r => r.Role == Martlet.Core.Settings.SetupRole.Tts) is
            { RouteType: Martlet.Core.Settings.SetupRouteType.GatewayF5, Reference: { } reference }
            ? reference.ReferenceRevision : voices.ChosenVoice?.Id;
        var current = companion?.CurrentCharacter(modelNow, voiceNow);
        var profiles = companion?.CharacterList ?? [];
        var (hereState, hereInUse, here) = ProfilesHere(directory);
        var sharing = HouseholdSharingStatus.Own(directory);
        return new
        {
            state = settings is null ? "no-settings" : profiles.Count == 0 ? "none" : "loaded",
            count = profiles.Count,
            lastUsed = profiles.FirstOrDefault(c => c.Id == companion!.ActiveCharacterId)?.Key,
            current = current?.Key,
            look = modelNow is null ? "model-file-outside-list" : modelNow == Martlet.Core.Settings.CharacterProfile.BuiltInModel ? "builtin"
                : "shared:" + Martlet.Avatar.Hosting.SharedCharacterModels.Key(modelNow),
            voiceChosen = voiceNow is not null,
            hereState,
            hereInUse = profiles.FirstOrDefault(c => c.Id == hereInUse)?.Key,
            profiles = profiles.Select(p => new
            {
                key = p.Key,
                personaSaved = companion!.Personas.Any(persona => persona.Id == p.PersonaId),
                personaActive = p.PersonaId == companion.ActivePersonaId,
                look = p.ModelId switch
                {
                    null => "keep",
                    Martlet.Core.Settings.CharacterProfile.BuiltInModel => "builtin",
                    var id => library.Live.FirstOrDefault(m => m.Id == id) is { } model
                        ? Martlet.Avatar.Hosting.SharedCharacterModels.IsComplete(directory, model) ? "ready" : "copying"
                        : "missing"
                },
                voice = p.VoiceId is null ? "keep" : voices.Find(p.VoiceId) is { Removed: false } ? "listed" : "missing",
                inUse = p.Id == current?.Id,
                here = here.GetValueOrDefault(p.Id),
                // How the account in use shares it with the household (household_sharing); null without accounts.
                sharing = HouseholdSharingStatus.ModeOf(sharing, p.Id)
            }).ToArray()
        };
    }

    /// <summary>character-profiles-local.json in a data directory (Martlet.Desktop's CharacterProfileLocalStore): the profile whose
    /// choices this PC uses (InUse) and what each character profile keeps on this PC, by profile ID: its place (device-independent
    /// pixels, the monitor and whether it is locked; null without one), its usual gaze (<c>personality</c> or a gaze's word),
    /// whether replies may change it, and which touches stop it while it talks (<c>any</c>, <c>intimate</c> or <c>never</c>). No
    /// file means no profile keeps anything here.</summary>
    private static (string State, Guid? InUse, Dictionary<Guid, object> Profiles) ProfilesHere(string directory)
    {
        var path = Path.Combine(directory, "character-profiles-local.json");
        if (!File.Exists(path)) return ("none", null, []);
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllBytes(path));
            if (document.RootElement.ValueKind != JsonValueKind.Object ||
                !document.RootElement.TryGetProperty("Profiles", out var saved) || saved.ValueKind != JsonValueKind.Object)
                return ("unreadable", null, []);
            Guid? inUse = document.RootElement.TryGetProperty("InUse", out var used) && used.ValueKind == JsonValueKind.String &&
                Guid.TryParse(used.GetString(), out var usedId) ? usedId : null;
            var profiles = new Dictionary<Guid, object>();
            foreach (var entry in saved.EnumerateObject())
            {
                if (!Guid.TryParse(entry.Name, out var id) || entry.Value.ValueKind != JsonValueKind.Object) continue;
                var kept = entry.Value;
                object? place = null;
                if (kept.TryGetProperty("Placement", out var at) && at.ValueKind == JsonValueKind.Object)
                {
                    double? Number(string name) => at.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number &&
                        value.TryGetDouble(out var number) ? number : null;
                    place = new
                    {
                        locked = at.TryGetProperty("Locked", out var locked) && locked.ValueKind == JsonValueKind.True,
                        left = Number("Left"), top = Number("Top"), width = Number("Width"), height = Number("Height"),
                        screen = at.TryGetProperty("Screen", out var screen) && screen.ValueKind == JsonValueKind.String ? screen.GetString() : null
                    };
                }
                profiles[id] = new
                {
                    place,
                    gaze = kept.TryGetProperty("GazeUsual", out var usual) && usual.ValueKind == JsonValueKind.String ? usual.GetString() : "personality",
                    gazeFree = !(kept.TryGetProperty("GazeFree", out var free) && free.ValueKind == JsonValueKind.False),
                    touchInterrupts = kept.TryGetProperty("TouchInterrupts", out var touches) && touches.ValueKind == JsonValueKind.String
                        ? touches.GetString() : "any"
                };
            }
            return ("loaded", inUse, profiles);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        {
            return ("unreadable", null, []);
        }
    }

    /// <summary>The shared character models as the desktop keeps them in a data directory (Martlet.Avatar.Hosting's
    /// SharedCharacterModels): keys, renderers, sizes and whether each copy is complete here. Names and paths are the owner's
    /// and are never returned.</summary>
    private static object CharacterModels(JsonElement arguments)
    {
        var directory = DataDirectory(arguments);
        string? shownPath = null;
        try
        {
            using var avatar = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(directory, "avatar.json")));
            shownPath = avatar.RootElement.TryGetProperty("model_path", out var value) ? value.GetString() : null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException) { }
        var path = Path.Combine(directory, Martlet.Avatar.Hosting.SharedCharacterModels.LibraryFile);
        Martlet.Core.Characters.CharacterModelLibrary? library = null;
        string state;
        if (!File.Exists(path)) state = "none";
        else
        {
            library = Martlet.Avatar.Hosting.SharedCharacterModels.Load(directory);
            state = library is null ? "unreadable" : "loaded";
        }
        library ??= Martlet.Core.Characters.CharacterModelLibrary.Empty;
        var shown = Martlet.Avatar.Hosting.SharedCharacterModels.ForPath(directory, library, shownPath);
        var shownKey = Martlet.Avatar.Hosting.SharedCharacterModels.KeyOfPath(directory, shownPath);
        var incoming = Path.Combine(directory, Martlet.Avatar.Hosting.SharedCharacterModels.IncomingDirectoryName);
        var root = Martlet.Avatar.Hosting.SharedCharacterModels.Root(directory);
        return new
        {
            state,
            live = library.Live.Count,
            tombstones = library.Models.Count(m => m.Removed),
            totalBytes = library.Live.Sum(m => m.Bytes),
            revision = library.Revision,
            models = library.Live.Select(m => new
            {
                key = Martlet.Avatar.Hosting.SharedCharacterModels.Key(m.Id), renderer = m.Renderer, files = m.Files!.Count,
                pieces = m.Files.Sum(f => f.Chunks.Count), bytes = m.Bytes, addedBy = m.AddedBy, addedAt = m.AddedAt, updatedBy = m.UpdatedBy,
                ready = Martlet.Avatar.Hosting.SharedCharacterModels.IsComplete(directory, m), shown = shown?.Id == m.Id
            }),
            copies = Directory.Exists(root) ? Directory.EnumerateDirectories(root).Count() : 0,
            incoming = Directory.Exists(incoming)
                ? Directory.EnumerateDirectories(incoming).Select(d => new { key = Path.GetFileName(d)[..Math.Min(16, Path.GetFileName(d).Length)],
                    pieces = Directory.EnumerateFiles(d).Count() }).ToArray()
                : [],
            showing = shownPath is null ? "built-in (nothing saved)"
                : Martlet.Avatar.Hosting.BundledLive2D.IsBuiltIn(shownPath) ? "built-in"
                : shown is not null ? "shared:" + Martlet.Avatar.Hosting.SharedCharacterModels.Key(shown.Id)
                : shownKey is not null ? "unlisted-copy:" + shownKey
                : "model-file-outside-list"
        };
    }

    private static object F5Voices(JsonElement arguments)
    {
        var directory = DataDirectory(arguments);
        var starters = Martlet.F5.F5BundledVoices.All.Select(voice =>
        {
            try
            {
                var format = voice.Check();
                return (object)new
                {
                    key = voice.Key, name = voice.Name, female = voice.Female, cute = voice.Cute, description = voice.Description,
                    licence = voice.Licence, transcript = voice.Transcript,
                    sha256 = voice.AudioSha256, sampleRate = format.SampleRate, durationMs = format.DurationMilliseconds, valid = true,
                    // The engines that can clone this clip (GPT-SoVITS needs 3-10 s) and the language its transcript is read in.
                    engines = Martlet.Core.Settings.SpeechEngines.All
                        .Where(engine => Martlet.Core.Settings.SpeechEngines.ReferenceProblem(engine, format.DurationMilliseconds) is null)
                        .Select(engine => engine.Key).ToArray(),
                    language = Martlet.Core.Settings.SpeechEngines.ReferenceLanguage(voice.Transcript)
                };
            }
            catch (Martlet.F5.F5Exception error)
            {
                return new { key = voice.Key, name = voice.Name, valid = false, problem = error.Failure.ToString() };
            }
        }).ToArray();
        // A starter voice's key, a retired recording's key (retired-sample, retired-librivox-annie-anime...) or "own".
        static string Kind(string sha256) => Martlet.F5.F5BundledVoices.ForAudio(sha256)?.Key ??
            Martlet.F5.F5BundledVoices.RetiredForAudio(sha256)?.Key ?? "own";
        static bool Retired(string kind) => Martlet.F5.F5BundledVoices.Retired.Any(voice => voice.Key == kind);
        // A voice's ID is its reference revision, so a starter voice's ID is known even after only its tombstone remains, and so
        // is a former starter voice's.
        var starterIds = Martlet.F5.F5BundledVoices.All.Select(voice => (voice.Key, voice.AudioSha256, voice.Transcript))
            .Concat(Martlet.F5.F5BundledVoices.Retired.Where(voice => voice.Transcript is not null)
                .Select(voice => (voice.Key, voice.AudioSha256, Transcript: voice.Transcript!)))
            .ToDictionary(voice => Martlet.Core.Voices.SpeakingVoiceLibrary.ReferenceId(voice.AudioSha256, voice.Transcript), voice => voice.Key);
        string KindOfId(string id) => starterIds.GetValueOrDefault(id) ?? "own";
        // The shared list (speaking-voices.json, the file Martlet.Desktop's F5Voices keeps; absent until a voice is first used,
        // changed or shared). Own voices are counted, never named.
        object library;
        var libraryPath = Path.Combine(directory, "speaking-voices.json");
        if (!File.Exists(libraryPath)) library = new { state = "none" };
        else
        {
            try
            {
                var shared = Martlet.Core.Voices.SpeakingVoiceLibrary.Parse(File.ReadAllBytes(libraryPath));
                var live = shared.Live;
                library = new
                {
                    state = "loaded", voices = live.Count, revision = shared.Revision,
                    starters = live.Select(v => KindOfId(v.Id)).Where(kind => kind != "own" && !Retired(kind)).ToArray(),
                    // Former starter voices still listed (an older Martlet's list, until this desktop next brings its voices in step).
                    retired = live.Select(v => KindOfId(v.Id)).Where(Retired).ToArray(),
                    own = live.Count(v => KindOfId(v.Id) == "own"),
                    removed = shared.Voices.Count(v => v.Removed),
                    removedStarters = shared.Voices.Where(v => v.Removed).Select(v => KindOfId(v.Id)).Where(kind => kind != "own").ToArray(),
                    chosen = shared.ChosenVoice is { } chosenVoice ? KindOfId(chosenVoice.Id) : null,
                    chosenBy = shared.ChosenVoice is not null ? shared.Chosen!.UpdatedBy : null,
                    // Voices made from several recordings, by the key the Voices page uses (the ID's first 16 hex digits): how many
                    // recordings, each one's length, the joined length, the engines that can clone it and those that learn from each.
                    severalRecordings = live.Where(v => v.Clips is not null).Select(v => new
                    {
                        key = v.Id[..16], recordings = v.Clips!.Count, clipMs = v.ClipMilliseconds, durationMs = v.DurationMilliseconds,
                        sampleRate = v.SampleRate,
                        engines = Martlet.Core.Settings.SpeechEngines.All
                            .Where(engine => Martlet.Core.Settings.SpeechEngines.ReferenceProblem(engine, v.DurationMilliseconds, v.ClipMilliseconds) is null)
                            .Select(engine => engine.Key).ToArray(),
                        learnsFromEach = Martlet.Core.Settings.SpeechEngines.All
                            .Where(engine => Martlet.Core.Settings.SpeechEngines.UsesClips(engine, v.ClipMilliseconds))
                            .Select(engine => engine.Key).ToArray()
                    }).ToArray()
                };
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or Martlet.Core.Contracts.ContractException)
            {
                library = new { state = "unreadable", problem = error is Martlet.Core.Contracts.ContractException ? error.Message : error.GetType().Name };
            }
        }
        var storeDirectory = Path.Combine(directory, "f5-voices");
        object list;
        if (!File.Exists(Path.Combine(storeDirectory, ".martlet-f5-references.v1.json"))) list = new { state = "none" };
        else
        {
            try
            {
                using var store = Martlet.F5.F5ReferencePresetStore.Open(storeDirectory);
                var inspection = store.Inspect();
                var latest = inspection.Presets.Select(p => (p.Id, Kind: Kind(p.Snapshots.LastOrDefault()?.AudioSha256 ?? ""))).ToArray();
                list = new
                {
                    state = "loaded", voices = latest.Length,
                    starters = latest.Where(p => p.Kind != "own" && !Retired(p.Kind)).Select(p => p.Kind).Distinct().ToArray(),
                    own = latest.Count(p => p.Kind == "own"),
                    retired = latest.Where(p => Retired(p.Kind)).Select(p => p.Kind).Distinct().ToArray(),
                    applied = latest.Where(p => p.Id == inspection.AppliedPresetId).Select(p => p.Kind).FirstOrDefault()
                };
            }
            catch (Martlet.F5.F5Exception error) { list = new { state = error.Failure == Martlet.F5.F5Failure.Busy ? "busy" : "unreadable", problem = error.Failure.ToString() }; }
        }
        // The voice the speaking (TTS) route keeps, which is what F5 actually speaks with.
        object speaking;
        var settingsPath = Path.Combine(directory, "settings.json");
        if (!File.Exists(settingsPath)) speaking = new { state = "none" };
        else
        {
            try
            {
                var settings = Martlet.Core.Settings.SettingsJson.Read(File.ReadAllBytes(settingsPath));
                var route = settings.Setup?.Routes.FirstOrDefault(r => r.Role == Martlet.Core.Settings.SetupRole.Tts);
                speaking = new
                {
                    state = "loaded", route = route?.RouteType.ToString(),
                    voice = route?.Reference is { } reference ? Kind(reference.AudioSha256) : null,
                    engine = route?.GatewaySnapshot is { } snapshot
                        ? Martlet.Core.Settings.SpeechEngines.ForRoute(snapshot.RouteId)?.Key : null,
                    host = route?.RouteType == Martlet.Core.Settings.SetupRouteType.GatewayF5 ? route.Gateway?.HostId : null,
                    model = route?.RouteType == Martlet.Core.Settings.SetupRouteType.GatewayF5 ? route.ModelId : null
                };
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or Martlet.Core.Contracts.ContractException or JsonException)
            {
                speaking = new { state = "unreadable", problem = error is Martlet.Core.Contracts.ContractException ? error.Message : error.GetType().Name };
            }
        }
        var fallback = Martlet.F5.F5BundledVoices.Default;
        // The self-hosted voice engines (F5-TTS, XTTS-v2, GPT-SoVITS, Dia) and the one chosen on this desktop (speaking-engine.txt, the file
        // Martlet.Desktop's SpeakingEngineChoice keeps; a speaking route on a host's engine wins over it).
        string? chosen = null;
        try { chosen = File.ReadAllText(Path.Combine(directory, "speaking-engine.txt")).Trim(); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        var engines = Martlet.Core.Settings.SpeechEngines.All.Select(engine => new
        {
            key = engine.Key, name = engine.Name, hostRole = engine.HostRoleKind, routeId = engine.RouteId, path = engine.Path,
            model = engine.DefaultModel, weightsLicence = engine.WeightsLicense, nonCommercial = engine.NonCommercial,
            minimumGpuMemoryGb = engine.MinimumGpuMemoryGb,
            minimumReferenceMs = engine.MinimumReferenceMilliseconds, maximumReferenceMs = engine.MaximumReferenceMilliseconds,
            summary = engine.Summary, languages = engine.Languages, streams = engine.StreamsWhileGenerating, features = engine.Features,
            abilities = Abilities(engine.Abilities, engine.Tags), runsOn = RunsOn(engine.Footprint, engine.RunsOn),
            @default = engine == Martlet.Core.Settings.SpeechEngines.Default,
            supportsTags = engine.SupportsTags, multipleReferences = engine.MultipleReferences,
            tags = engine.Tags.Select(tag => new { text = tag.Text, kind = tag.Kind.ToString(), usage = tag.Usage }).ToArray()
        }).ToArray();
        // The other ways Martlet speaks, with the same rundown Companion › Voice shows (VoiceEngineAbilities-<key>,
        // VoiceEngineRunsOn-<key>).
        var catalog = Martlet.Core.Planning.FootprintCatalog.Default;
        var openAiVoice = catalog.Find(Martlet.Core.Planning.FootprintCatalog.OpenAiVoiceId);
        var otherVoices = new[]
        {
            new { key = "openai", name = "OpenAI voice", abilities = Abilities(Martlet.Core.Settings.VoiceAbilities.OpenAiVoice, []),
                runsOn = RunsOn(openAiVoice, openAiVoice?.WhereItRuns ?? "") }
        };
        return new
        {
            @default = fallback.Key, defaultName = fallback.Name, defaultFemale = fallback.Female, defaultCute = fallback.Cute,
            cute = Martlet.F5.F5BundledVoices.All.Where(voice => voice.Cute).Select(voice => voice.Key).ToArray(), starters,
            retired = Martlet.F5.F5BundledVoices.Retired.Select(voice => new { key = voice.Key, name = voice.Name }).ToArray(), library, list, speaking,
            engines, otherVoices, chosenEngine = Martlet.Core.Settings.SpeechEngines.ForKey(chosen)?.Key ?? Martlet.Core.Settings.SpeechEngines.Default.Key,
            // Chatterbox Original's General and Expressive style as Companion › Voice saved it (Resemble's suggestions until then).
            chatterboxStyle = ChatterboxStyleReport(directory)
        };

        static object ChatterboxStyleReport(string directory)
        {
            var style = Martlet.Core.Settings.ChatterboxStyle.Load(directory);
            return new
            {
                saved = File.Exists(Path.Combine(directory, Martlet.Core.Settings.ChatterboxStyle.FileName)),
                generalExaggeration = style.GeneralExaggeration, generalCfgWeight = style.GeneralCfgWeight,
                expressiveExaggeration = style.ExpressiveExaggeration, expressiveCfgWeight = style.ExpressiveCfgWeight,
                summary = style.Describe()
            };
        }

        // What a voice can do: cloning, sounds and emotions (yes, partly or no), the tags that do it and the rundown's words.
        static object Abilities(Martlet.Core.Settings.VoiceAbilities abilities, IReadOnlyList<Martlet.Core.Settings.VoiceTag> tags) => new
        {
            cloning = abilities.Cloning, sounds = abilities.Sounds, emotions = abilities.Emotions.ToString(),
            items = abilities.Items.Select(item => new
            {
                name = item.Name, level = item.Level.ToString(), note = item.Note, help = item.Help,
                tags = abilities.TagsFor(item, tags).Select(tag => tag.Text).ToArray()
            }).ToArray(),
            summary = abilities.Describe()
        };

        // Where a voice runs: on a GPU (with its graphics memory in GB: typical, most and the smallest card), the CPU or
        // online, as the footprint catalog says, and the line Companion › Voice shows.
        static object RunsOn(Martlet.Core.Planning.ComponentOption? option, string text) => new
        {
            on = option is null ? "gpu" : !option.IsLocal ? "online" : option.UsesGpu ? "gpu" : "cpu",
            vramGb = option?.UsesGpu == true ? option.Usual.VramGb : 0, peakVramGb = option?.UsesGpu == true ? option.GpuGb : 0,
            minimumGpuGb = option?.MinGpuGb ?? 0, evidence = option?.Evidence.ToString(), text
        };
    }

    /// <summary>Companion › Personality and Character as saved in a data directory: settings.json's personas, avatar.json (read as
    /// JSON; the field names match Martlet.Avatar.Hosting's AvatarProfile) and lorebooks.json. Persona instructions, model paths
    /// and lorebook text are personal and never returned.</summary>
    private static async Task<object> CharacterStatusAsync(JsonElement arguments, CancellationToken cancellation)
    {
        var directory = DataDirectory(arguments);
        var loaded = await new Martlet.Core.Settings.SettingsStore(directory).LoadAsync(cancellation);
        var companion = loaded.Settings?.Companion;
        object personality = new
        {
            state = loaded.State switch
            {
                Martlet.Core.Settings.SettingsLoadState.Loaded => "loaded",
                Martlet.Core.Settings.SettingsLoadState.FirstRun => "none",
                _ => "unreadable"
            },
            problem = loaded.Error?.Summary,
            active = companion?.Personas.FirstOrDefault(p => p.Id == companion.ActivePersonaId)?.Name,
            personas = companion?.Personas.Select(p => new
            {
                name = p.Name, active = p.Id == companion.ActivePersonaId, instructionCharacters = p.Text.Length,
                speechBreaks = Breaks(p.SpokenBreaks)
            }).ToArray() ?? []
        };

        object character;
        var avatar = Path.Combine(directory, "avatar.json");
        if (!File.Exists(avatar)) character = new { state = "none" };
        else
        {
            try
            {
                using var document = JsonDocument.Parse(await File.ReadAllBytesAsync(avatar, cancellation));
                var root = document.RootElement;
                string? Text(JsonElement parent, string name) =>
                    parent.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
                var model = Text(root, "model_path") ?? "";
                const string builtIn = "builtin:";
                character = new
                {
                    state = "loaded",
                    model = model.StartsWith(builtIn, StringComparison.Ordinal) ? "built-in" : "own model",
                    builtInCharacter = model.StartsWith(builtIn, StringComparison.Ordinal) ? model[builtIn.Length..] : null,
                    ownModelType = model.StartsWith(builtIn, StringComparison.Ordinal) ? null
                        : model.EndsWith(".model3.json", StringComparison.OrdinalIgnoreCase) ? ".model3.json" : Path.GetExtension(model).ToLowerInvariant(),
                    renderer = Text(root, "renderer"),
                    lipSync = Text(root, "lip_sync") ?? "auto",
                    autoShow = root.TryGetProperty("auto_show", out var show) && show.ValueKind == JsonValueKind.True,
                    lipSyncHost = root.TryGetProperty("remote_host", out var host) && host.ValueKind == JsonValueKind.Object ? Text(host, "host_id") : null
                };
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
            {
                character = new { state = "unreadable", problem = error.GetType().Name };
            }
        }

        // Lorebooks live in the folder of the account whose settings the data folder holds (docs/ACCOUNTS.md); a data folder from
        // before accounts keeps them itself.
        var loreFolder = Martlet.Core.Sync.AccountWorkingCopy.Load(directory) is { } signedIn
            ? Martlet.Core.Sync.AccountWorkingCopy.Folder(directory, signedIn.Account) : directory;
        var lore = await new Martlet.Core.Lorebooks.LorebookStore(loreFolder).LoadAsync(cancellation);
        object lorebooks = new
        {
            state = lore.Loaded ? File.Exists(Path.Combine(loreFolder, Martlet.Core.Lorebooks.LorebookStore.FileName)) ? "loaded" : "none" : "unreadable",
            account = loreFolder == directory ? null : Path.GetFileName(loreFolder),
            books = lore.Library.Books.Count,
            on = lore.Library.Books.Count(book => book.Activation != Martlet.Core.Lorebooks.LorebookActivation.Off),
            entries = lore.Library.Books.Sum(book => book.Entries.Count)
        };
        return new { personality, character, placement = CharacterPlacement(directory), clickThrough = CharacterClickThrough(directory),
            voice = CharacterVoice(directory), lorebooks };
    }

    /// <summary>character-click-through.json in a data directory (Martlet.Desktop's CharacterClickThroughStore): whether clicks
    /// pass through the character on this PC. No file means it catches clicks.</summary>
    private static object CharacterClickThrough(string directory)
    {
        var path = Path.Combine(directory, "character-click-through.json");
        if (!File.Exists(path)) return new { state = "none", on = false };
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllBytes(path));
            if (document.RootElement.ValueKind != JsonValueKind.Object) return new { state = "unreadable", on = false, problem = "NotAnObject" };
            return new
            {
                state = "loaded",
                on = document.RootElement.TryGetProperty("ClickThrough", out var on) && on.ValueKind == JsonValueKind.True
            };
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        {
            return new { state = "unreadable", on = false, problem = error.GetType().Name };
        }
    }

    /// <summary>Whether Martlet's voice is muted and how loud it is, from talk-preferences.json in a data directory as the desktop
    /// reads it (TalkPreferences): SpeakReplies (Companion › Voice's Speak Martlet's replies aloud) is on unless saved
    /// off, and Mute voice / Unmute voice on the character's right-click menu change the same choice; VoiceVolume (Companion ›
    /// Voice's Voice volume, 0 to 1) is full unless saved lower.</summary>
    private static object CharacterVoice(string directory)
    {
        var file = TalkPreferences.Read(directory);
        var (speak, volume) = (file.Preferences.SpeakReplies, file.Preferences.VoiceVolume);
        return file.State switch
        {
            SettingsFileState.Missing => new { state = "none", speakReplies = speak, muted = !speak, volume },
            SettingsFileState.Unreadable => (object)new { state = "unreadable", speakReplies = speak, muted = !speak, volume, problem = file.Problem },
            _ => new { state = "loaded", speakReplies = speak, muted = !speak, volume }
        };
    }

    /// <summary>character-placement.json in a data directory (Martlet.Desktop's CharacterPlacementStore): where the character
    /// was last left on this PC (device-independent pixels), on which monitor and whether it is locked there. No file means it
    /// shows at its default spot.</summary>
    private static object CharacterPlacement(string directory)
    {
        var path = Path.Combine(directory, "character-placement.json");
        if (!File.Exists(path)) return new { state = "none", locked = false };
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllBytes(path));
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return new { state = "unreadable", locked = false, problem = "NotAnObject" };
            double? Number(string name) => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number &&
                value.TryGetDouble(out var number) ? number : null;
            return new
            {
                state = "loaded", locked = root.TryGetProperty("Locked", out var locked) && locked.ValueKind == JsonValueKind.True,
                left = Number("Left"), top = Number("Top"), width = Number("Width"), height = Number("Height"),
                screen = root.TryGetProperty("Screen", out var screen) && screen.ValueKind == JsonValueKind.String ? screen.GetString() : null,
                screenLeft = Number("ScreenLeft"), screenTop = Number("ScreenTop")
            };
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        {
            return new { state = "unreadable", locked = false, problem = error.GetType().Name };
        }
    }
}
