using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using Martlet.Core.Installation;
using Martlet.Doctor;
using Martlet.Mcp.Client;

namespace Martlet.Mcp;

/// <summary>martlet_guide's "The app's window" area: the running desktop's window and its character (AutomationPolicy says
/// which controls need --allow-ui-effects). Each Tool(...) registers one tool: its name, description, input schema, handler
/// and, when it needs one, its permission (<see cref="ToolPermission"/>). A new tool of this area goes here.</summary>
internal sealed partial class McpServer
{
    private McpTool[] UiTools() =>
    [
        Tool("ui_connect", "Attach to an already-running Martlet.Desktop (or, on a Windows dev run, Martlet.Companion) process in this " +
            "interactive session. Without pid it finds the Martlet desktop running in this Windows session. Returns its windows, " +
            "inTray (no main window shown) and ready: whether the main window is shown, enabled and has its navigation loaded. Right " +
            "after Martlet starts, call ui_connect again until ready is true before clicking (a busy PC takes longer).", new
        {
            pid = new { type = "integer", minimum = 1 }
        },
            arguments => desktop.Connect(OptionalInt(arguments, "pid"))),
        Tool("ui_snapshot", "Inspect automation IDs, enabled state and selected non-secret status fields of attached Martlet windows " +
            "(with a status line's tooltip details as help), " +
            "and whether each window can be resized, minimized and maximized. With layout, each control also returns its screen " +
            "bounds and, for text, where its first line of text sits (geometry only, never the text), and each window its bounds " +
            "and its monitor's work area. idPrefix keeps only controls whose automation ID starts with it (the first 200 controls are " +
            "returned, so a long page's later sections need it).", new
        {
            layout = new { type = "boolean" }, idPrefix = new { type = "string" }
        },
            arguments => desktop.Snapshot(OptionalBool(arguments, "layout") ?? false, OptionalString(arguments, "idPrefix"))),
        Tool("ui_long_texts", "List the texts that show now in the attached Martlet's windows with more than one sentence (text " +
            "blocks and check box or radio button labels): each one's window, its automation ID (or near: the nearest container's), " +
            "how many sentences it has and the text. Martlet 1.0 shows one short sentence and puts the rest behind a '?' " +
            "(Help-<id>) with a wiki link, so an open page should list none. Lists, tables, text boxes, help cards and controls " +
            "ui_snapshot does not read are left out. At most " + DesktopAutomation.MaximumLongTexts + " texts; count is all of them.",
            new { },
            _ => desktop.LongTexts()),
        Tool("ui_click", "Invoke an automation-ID control. Only safe navigation controls work without --allow-ui-effects. With " +
            "several windows that have the control (side-by-side run windows each have HostRunCancel), window names the one to use: " +
            "its title as ui_snapshot lists it (for example \"Martlet - Start Docker Desktop\"). With focus true the control takes the " +
            "keyboard focus first, as a mouse click gives it (its window comes to the front).", new
        {
            id = new { type = "string" }, window = new { type = "string" }, focus = new { type = "boolean" }
        }, ["id"],
            async (arguments, cancellation) => await desktop.ClickAsync(RequiredString(arguments, "id"), OptionalString(arguments, "window"),
                OptionalBool(arguments, "focus") ?? false), ToolPermission.UiEffects),
        Tool("ui_select", "Select a named option from a combo box. Requires --allow-ui-effects.", new
        {
            id = new { type = "string" }, item = new { type = "string" }
        }, ["id", "item"],
            arguments => desktop.Select(RequiredString(arguments, "id"), RequiredString(arguments, "item")), ToolPermission.UiEffects),
        Tool("ui_set_text", "Enter text into an editable control; an empty text clears it (requires --allow-ui-effects).", new
        {
            id = new { type = "string" }, text = new { type = "string" }
        }, ["id", "text"],
            arguments => desktop.SetText(RequiredString(arguments, "id"),
                OptionalString(arguments, "text") ?? throw new ArgumentException("Missing string 'text'.")), ToolPermission.UiEffects),
        Tool("ui_type", "Type the way a person does, one key at a time, to see how a page takes typing: with id that text box takes the " +
            "keyboard focus first; without it the keys go to whatever has the focus, so you can type, let the page draw again and type " +
            "on without clicking. Each character of text (at most " + DesktopAutomation.MaximumTyped + "), then each of keys (" +
            string.Join(", ", DesktopAutomation.TypeKeys.Keys) + "), is a key posted to that Martlet window only (window names one; " +
            "Martlet's main window by default), every stepMs (default 50). Returns the automation ID of the control that has the " +
            "keyboard focus afterwards (null when none of Martlet's controls has it); ui_snapshot marks it focused. Requires " +
            "--allow-ui-effects.", new
        {
            id = new { type = "string" }, text = new { type = "string", maxLength = DesktopAutomation.MaximumTyped },
            keys = new { type = "array", maxItems = DesktopAutomation.MaximumTyped, items = new { type = "string", @enum = DesktopAutomation.TypeKeys.Keys.ToArray() } },
            window = new { type = "string" },
            stepMs = new { type = "integer", minimum = 0, maximum = DesktopAutomation.MaximumTypeStepMs }
        },
            async (arguments, cancellation) => await desktop.TypeAsync(OptionalString(arguments, "id"), OptionalString(arguments, "text"),
                OptionalStrings(arguments, "keys"), OptionalString(arguments, "window"), OptionalInt(arguments, "stepMs")), ToolPermission.UiEffects),
        Tool("ui_toggle", "Toggle an enabled checkbox (requires --allow-ui-effects, except a checkbox that only changes what a " +
            "window shows: the Memory window's MemoryShowHistory).", new
        {
            id = new { type = "string" }
        }, ["id"],
            arguments => desktop.Toggle(RequiredString(arguments, "id")), ToolPermission.UiEffects),
        Tool("ui_set_range", "Set an enabled slider to a number within its range, such as Companion › Voice's VoiceVolume " +
            "(0 to 100); the result reports the slider's value and range (requires --allow-ui-effects).", new
        {
            id = new { type = "string" }, value = new { type = "number" }
        }, ["id", "value"],
            arguments => desktop.SetRange(RequiredString(arguments, "id"), RequiredDouble(arguments, "value")), ToolPermission.UiEffects),
        Tool("ui_move", "Move a movable control by dx, dy screen pixels through UI Automation's Transform pattern and report its " +
            "bounds before and after: the character overlay's MoveAvatar moves the character like a drag. ui_snapshot reports " +
            "movable for such controls (false while the character's position is locked, when this is refused). Requires " +
            "--allow-ui-effects.", new
        {
            id = new { type = "string" },
            dx = new { type = "integer", minimum = -DesktopAutomation.MaximumMove, maximum = DesktopAutomation.MaximumMove },
            dy = new { type = "integer", minimum = -DesktopAutomation.MaximumMove, maximum = DesktopAutomation.MaximumMove }
        }, ["id", "dx", "dy"],
            arguments => desktop.Move(RequiredString(arguments, "id"), RequiredInt(arguments, "dx"), RequiredInt(arguments, "dy")), ToolPermission.UiEffects),
        Tool("ui_scroll", "Read or scroll a control that scrolls through UI Automation's Scroll pattern, such as Companion > Touch's " +
            "zone map (TouchZonesMap) when it is zoomed in, or a page. horizontal and vertical (each optional, 0 to 100) are how far " +
            "along to scroll it each way; without them it only reads. Returns how far along it is each way (-1 when it can't scroll " +
            "that way) and which part of its content shows (shows: left, right, top and bottom in percent of the content). Scrolling " +
            "only changes what shows, so it needs no --allow-ui-effects.", new
        {
            id = new { type = "string" },
            horizontal = new { type = "number", minimum = 0, maximum = 100 },
            vertical = new { type = "number", minimum = 0, maximum = 100 }
        }, ["id"],
            arguments => desktop.Scroll(RequiredString(arguments, "id"), OptionalDouble(arguments, "horizontal"), OptionalDouble(arguments, "vertical"))),
        Tool("character_touch", "Tap the showing character like a left click that doesn't drag, at x, y (fractions 0 to 1 of the " +
            "character overlay's drawing, +y down; unzoomed, its head is near 0.5, 0.15), and return the renderer's hit test as " +
            "last: n, x, y, hit, zone (head, hair, face, body, arm, hand, leg or foot), hitAreas and drawables (Live2D), bone, node, " +
            "mesh and material (VRM), hair (Live2D: the topmost drawable sits in a part the model names as hair; VRM: a hair joint), " +
            "and rest ({x, y}: where the touched point of the character was in its rest pose, " +
            "traced on the touched mesh and drawn as the overlay frames it now; touch zones compare it with their boxes, so it " +
            "stays put while the head follows the mouse; null when the renderer can't trace it). Martlet then plays its tap " +
            "reaction (the desktop log records 'The character was " +
            "tapped on the ...'). holdMs presses that long (600 or more is a hold, up to 10000), repeat taps the same point up to " +
            "20 times gapMs apart (default 150), and taps ([{x, y, holdMs}], up to 20) taps a sequence of points instead. With " +
            "Companion > Touch > Touch zones showing, noticed reads what Martlet noticed after settleMs: waiting (the touch " +
            "line that waits for a reply and when a touch reply starts), last (which reply took the last touches and what " +
            "Thinking was told) and zone (TouchZonesLast). Tapping requires --allow-ui-effects; without x, y or taps it only " +
            "reads the last tap.", new
        {
            x = new { type = "number", minimum = 0, maximum = 1 },
            y = new { type = "number", minimum = 0, maximum = 1 },
            holdMs = new { type = "integer", minimum = 0, maximum = 10_000 },
            repeat = new { type = "integer", minimum = 1, maximum = DesktopAutomation.MaximumTaps },
            gapMs = new { type = "integer", minimum = 0, maximum = 10_000 },
            settleMs = new { type = "integer", minimum = 0, maximum = 15_000 },
            taps = new
            {
                type = "array", maxItems = DesktopAutomation.MaximumTaps,
                items = new
                {
                    type = "object",
                    properties = new
                    {
                        x = new { type = "number", minimum = 0, maximum = 1 },
                        y = new { type = "number", minimum = 0, maximum = 1 },
                        holdMs = new { type = "integer", minimum = 0, maximum = 10_000 }
                    },
                    required = new[] { "x", "y" }
                }
            }
        },
            async (arguments, cancellation) => await desktop.TouchCharacterAsync(OptionalDouble(arguments, "x"), OptionalDouble(arguments, "y"),
                OptionalInt(arguments, "holdMs"), OptionalInt(arguments, "repeat"), OptionalInt(arguments, "gapMs"), Taps(arguments),
                OptionalInt(arguments, "settleMs")), ToolPermission.UiEffects),
        Tool("character_stroke", "Stroke the showing character, whose position must be locked, like pressing the left button and " +
            "dragging across it: along points [[x, y], ...] (2 to 200, fractions 0 to 1 of the character overlay's drawing, +y " +
            "down; unzoomed, its hair is near 0.5, 0.1 and its face near 0.5, 0.2), one point every stepMs milliseconds (10 to " +
            "2000, default 40). Martlet hit-tests the path, plays each crossed zone's reaction (the first zone's emote held until " +
            "the stroke ends) and records the stroke in the touch ledger. Returns the overlay's stroke record as last.stroke (n, " +
            "samples, hits, ms, coarse zones crossed); Companion > Touch > Touch zones' CharacterPhysicalLast reads Martlet's " +
            "summary (zones, pace, passes). last.physical is the last settled move, zoom or pan (kind, dx, dy, from and to monitors, " +
            "zoomFrom, zoomTo, focus). Requires --allow-ui-effects; without points it only reads the last stroke.", new
        {
            points = new { type = "array", items = new { type = "array", items = new { type = "number", minimum = 0, maximum = 1 }, minItems = 2, maxItems = 2 } },
            stepMs = new { type = "integer", minimum = 10, maximum = 2000 }
        },
            async (arguments, cancellation) => await desktop.StrokeCharacterAsync(StrokePoints(arguments), OptionalInt(arguments, "stepMs") ?? 40), ToolPermission.UiEffects),
        Tool("character_face", "Read where Martlet draws over the showing character's face (its blush levels blush, blush_deep and blush_fierce, and overlay emotes " +
            "such as hearts or a sweat drop), samples times (1 to 60, default 1) gapMs apart (0 to 5000, default 250), as each frame " +
            "is drawn. Each reading (faces) has n, found, tracking (mesh: pinned to the Live2D model's own face meshes; bones: a " +
            "VRM's head bone; estimate: a Live2D model's head angles, when no face meshes were found), x, y and width (fractions of " +
            "the character overlay's drawing, +y down), tilt (degrees, clockwise), cheekLeft and cheekRight (x, y; visible, 0 to " +
            "1 as the cheek turns away; across, the cheek's width against the face's, below 1 on a turned head's far cheek; and " +
            "the renderer's hit test there: hit, drawables, bone, mesh), eyeLeft, eyeRight, mouth and top (x, y: the eye and mouth " +
            "points the overlay emotes such as tears or tongue_out are drawn from, and the top of the head; an eye with a known " +
            "iris has its point at the eye's middle; with a face measured by vision the cheeks and mouth are where vision saw " +
            "them), cheekSize (the measured cheeks' radius in face widths, which sizes the blush; null without a measurement), " +
            "the eyes for drawings over them: eyesFrom (mesh: a Live2D " +
            "model's iris and eye-white meshes; bones: a VRM's eye bones with its iris and eye-white meshes; vision: eyes measured " +
            "by vision fill what the model can't give; estimate: an eye has neither, so its iris and opening are left out), " +
            "irisLeft and irisRight (x, y, rx, ry: the iris's middle and radii, x and rx fractions of the drawing's width, y and ry " +
            "of its height; null when unknown) and eyeLeftShape and eyeRightShape (the eye's visible opening now: points, " +
            "triangles (null for an outline), its box left, top, right, bottom, and irisInside, whether the iris's middle is in " +
            "it; 0 points when the eye is closed or hidden), the overlays showing and pinned (Live2D: carriers, the mesh vertices " +
            "the face rides on; skin, the ID of the face's skin drawable when they are its vertices (the highest-drawn drawable " +
            "that holds the face's middle and both cheeks at rest), else null for the vertices that ride the head; " +
            "milliseconds, how long finding them took at load, and eyeMilliseconds, how long finding the eyes' " +
            "meshes took). summary says which tracking was used, how far the face moved (x, y, width, tilt), per cheek the share " +
            "of readings over the character, what it was mostly over and for what share, the least it showed and its across " +
            "range, eyesFrom (the sources seen) and per eye (eyeLeft, eyeRight) the share of readings with an iris, how far the " +
            "iris moved (irisMoved x, y), the share of open readings with the iris inside its opening, the opening's least and " +
            "most height (a blink closes it) and the share of readings it was closed. Reading changes nothing, so it needs no " +
            "--allow-ui-effects.", new
        {
            samples = new { type = "integer", minimum = 1, maximum = DesktopAutomation.MaximumFaceSamples },
            gapMs = new { type = "integer", minimum = 0, maximum = 5000 }
        },
            async (arguments, cancellation) => await desktop.FaceCharacterAsync(OptionalInt(arguments, "samples"), OptionalInt(arguments, "gapMs"))),
        Tool("character_picture", "Take a picture of the showing character as it shows now: the renderer's own capture of the " +
            "overlay's page (WebView2), so Martlet's drawings over the face (its blush glow and overlay emotes such as heart eyes) " +
            "are in it, cropped to the character with a little room. Zoom the character first (SetupCharacterZoomIn) to see small " +
            "parts such as the eyes larger. Returns taken, picture (n, path: the renderer's PNG file in the temp folder, replaced " +
            "each time; width and height in pixels; left, top, cropWidth and cropHeight: where it sits on the overlay's drawing, " +
            "as fractions like character_face's positions; or error) and saved, the copy at outputPath (a full path to a new .png " +
            "file on this PC; an existing file is never replaced) when given. Taking it changes nothing on the character, so it needs " +
            "no --allow-ui-effects; saving the copy at outputPath writes a file, so that needs --allow-changes.", new
        {
            outputPath = new { type = "string" }
        },
            async (arguments, cancellation) => await desktop.PictureCharacterAsync(OptionalString(arguments, "outputPath")))
            with { Writes = arguments => OptionalString(arguments, "outputPath") is not null },
        Tool("character_pose", "Read what the showing character's idle body does, samples times (1 to 60, default 1) gapMs apart (0 to " +
            "5000, default 250), as each frame is drawn. A VRM rests in a relaxed pose (arms hanging close to the body, elbows " +
            "softly bent, fingers curled) and breathes. Each reading (poses) has n, found (false for a Live2D model, whose own " +
            "breathing isn't read), renderer, idle, breathing (phase 0 to 1 of one breath; inhale, how full the chest is, 0 to 1; " +
            "perMinute), arms (left and right: fromDown, the upper arm's angle from straight down, and elbow, its bend, in degrees " +
            "measured on the posed bones), curl (left and right: the middle finger's curl in degrees, 0 straight), sway (the " +
            "spine's slow sideways lean, degrees), bones (head, neck, shoulders, upper arms, hands and upper legs: x, y as " +
            "fractions of the character overlay's drawing, +y down), animation (the VRMA clip that plays now: name, time, " +
            "duration, weight; null when none) and clips (the clips the character keeps). summary gives whether it idled, the " +
            "inhale, perMinute, arm, curl and sway ranges (least, most), the animations seen playing with their animationWeight " +
            "range and how far each bone moved (x, y). Reading changes nothing, so it needs no " +
            "--allow-ui-effects.", new
        {
            samples = new { type = "integer", minimum = 1, maximum = DesktopAutomation.MaximumPoseSamples },
            gapMs = new { type = "integer", minimum = 0, maximum = 5000 }
        },
            async (arguments, cancellation) => await desktop.PoseCharacterAsync(OptionalInt(arguments, "samples"), OptionalInt(arguments, "gapMs"))),
        Tool("character_animation", "Play a VRM Animation clip (.vrma: binary glTF with VRMC_vrm_animation 1.0) on the showing VRM " +
            "character, through the character overlay's UI Automation (MoveAvatar's value \"animation:{...}\"). path is the full " +
            "path of a .vrma file on this PC (at most 4 MB and 120 seconds); the renderer keeps the clip as name (default: the " +
            "file's name; 1 to 128 characters) while the model stays loaded, at most 16 clips, the oldest going first. Without " +
            "path it plays the clip kept as name. play (default true) plays it once: it blends in over the idle pose, then gives " +
            "the body back to the idle pose, breathing and sway by its end; lip-sync, blinking, the look, expressions and touches " +
            "keep working. inPlace (default true) keeps the hips at rest across the ground and moves them only up and down, so a " +
            "clip that walks or drifts keeps the character in its window. Returns played (n, name, kept, started, duration in " +
            "seconds, clips: every clip kept, file, and reason when it didn't start) and, with samples (1 to 60) gapMs apart " +
            "(default 250), pose: character_pose's readings, whose animation says which clip plays (name, time, duration, " +
            "weight: how much of the body it has) and clips the clips kept. It changes the character, so it needs " +
            "--allow-ui-effects.", new
        {
            path = new { type = "string" },
            name = new { type = "string", minLength = 1, maxLength = 128 },
            play = new { type = "boolean" },
            inPlace = new { type = "boolean" },
            samples = new { type = "integer", minimum = 1, maximum = DesktopAutomation.MaximumPoseSamples },
            gapMs = new { type = "integer", minimum = 0, maximum = 5000 }
        },
            async (arguments, cancellation) => await desktop.AnimateCharacterAsync(OptionalString(arguments, "path"), OptionalString(arguments, "name"),
                OptionalBool(arguments, "play") ?? true, OptionalBool(arguments, "inPlace") ?? true, OptionalInt(arguments, "samples"),
                OptionalInt(arguments, "gapMs")), ToolPermission.UiEffects),
        Tool("character_zones", "Read where each area of the showing character's touch zones is now, samples times (1 to 60, default " +
            "1) gapMs apart (0 to 5000, default 250), as each frame is drawn: the zones Martlet last gave the renderer (the saved " +
            "zones in use of the model it shows; Companion > Touch > Touch zones > Show the zones on the character draws them over " +
            "it). An area that follows Live2D drawables (a tail's, which swings) is the box around them where they are drawn now " +
            "(from: drawables); one that follows two or more of a VRM's spring-bone joints (a tail's) is the box around them where they " +
            "are now (nodes); one that follows VRM bones, or one joint, keeps its size around where they are now (bones); any other " +
            "stays at its box, moved by the view's zoom and pan (box). last has n, found, renderer, draw and areas (each zone, " +
            "area (0 for its first), from, left, top, right, bottom: fractions of the character overlay's drawing, +y down). " +
            "summary gives per area what placed it, its last box and how far its middle moved (x, y). Reading changes nothing, " +
            "so it needs no --allow-ui-effects.", new
        {
            samples = new { type = "integer", minimum = 1, maximum = DesktopAutomation.MaximumZoneSamples },
            gapMs = new { type = "integer", minimum = 0, maximum = 5000 }
        },
            async (arguments, cancellation) => await desktop.ZonesCharacterAsync(OptionalInt(arguments, "samples"), OptionalInt(arguments, "gapMs"))),
        Tool("character_mouth", "Read who moves the showing character's mouth, Martlet's voice or its emotes, samples times (1 to 60, " +
            "default 1) gapMs apart (0 to 5000, default 250), as each frame is drawn. While the voice speaks, until a second after " +
            "its last sound, it has the mouth: what the emotes put on the mouth (a held mouth_open, an expression that opens it, a " +
            "VRM emotion whose overrideMouth blocks lip-sync) gives way, and comes back once the voice is done. With levels (1 to " +
            $"{DesktopAutomation.MaximumVoiceLevels} loudness levels from 0 to 1, one every stepMs: 10 to 1000, default 50; needs " +
            "--allow-ui-effects) the mouth first moves as Martlet's loudness lip-sync moves it, without a sound, and the readings " +
            "start at once. Each reading (mouths) has n, found (false before a model shows), renderer, parameter (Live2D: the " +
            "parameter read, ParamMouthOpenY when the model has it), voice (0 to 1: how much the voice has the mouth), speaking " +
            "(the voice moved it within the last second), level (the voice's loudness on it), emote (how far the emotes open the " +
            "mouth before the voice takes it; a VRM: its held open mouth), open (how far it is open now, 0 at rest to 1) and, for " +
            "a VRM, blocked (how much the expressions showing block its mouth expressions). summary gives the share of readings " +
            "speaking, the voice, level, emote, open and blocked ranges (least, most) and the last reading. Reading alone changes " +
            "nothing, so it needs no --allow-ui-effects.", new
        {
            levels = new { type = "array", items = new { type = "number", minimum = 0, maximum = 1 }, minItems = 1, maxItems = DesktopAutomation.MaximumVoiceLevels },
            stepMs = new { type = "integer", minimum = 10, maximum = 1000 },
            samples = new { type = "integer", minimum = 1, maximum = DesktopAutomation.MaximumMouthSamples },
            gapMs = new { type = "integer", minimum = 0, maximum = 5000 }
        },
            async (arguments, cancellation) => await desktop.MouthCharacterAsync(MouthLevels(arguments), OptionalInt(arguments, "stepMs"),
                OptionalInt(arguments, "samples"), OptionalInt(arguments, "gapMs")), ToolPermission.UiEffects),
        Tool("character_look", "Read where the showing character looks now, samples times (1 to 60, default 1) gapMs apart (0 to " +
            "5000, default 250), as the overlay last turned its head and eyes (it does every 50 ms). Each reading (looks) has n, " +
            "target (mouse; window: where you work in the window you're using; point: a spot Martlet asked it to look at; or " +
            "ahead), x and y (the direction, -1 to 1, +x right, +y up), at (the point on the desktop in screen pixels, or null), " +
            "usual (the usual gaze: mouse, near, ahead or window), window (the window you're using: left, top, width and height, " +
            "never its title; null when none) and watching (for the window gaze: pointer, text cursor or middle). summary gives " +
            "the targets and watching seen and the x and y ranges (least, most). Reading changes nothing, so it needs no " +
            "--allow-ui-effects.", new
        {
            samples = new { type = "integer", minimum = 1, maximum = DesktopAutomation.MaximumLookSamples },
            gapMs = new { type = "integer", minimum = 0, maximum = 5000 }
        },
            async (arguments, cancellation) => await desktop.LookCharacterAsync(OptionalInt(arguments, "samples"), OptionalInt(arguments, "gapMs"))),
        Tool("ui_tray", "Martlet's notification-area icon. \"status\" (default) reads whether the icon is shown, whether the main " +
            "window is visible or hidden in the notification area, whether its menu is open (menuOpen, with the menu's menuBounds " +
            "[x, y, width, height] in physical screen pixels) and whether Martlet still runs. \"open\" and \"menu\" send the icon " +
            "what Explorer sends for a left click (show Martlet) and a right click (its menu, which opens beside the click; " +
            "ui_snapshot then lists the Tray* items), at the mouse pointer or at optional x, y (physical screen pixels, as Explorer " +
            "reports them). \"close\" presses the main window's close button, which hides Martlet in the notification area by " +
            "default or exits it, so it requires --allow-ui-effects.", new
        {
            action = new { type = "string", @enum = DesktopAutomation.TrayActions },
            x = new { type = "integer", minimum = short.MinValue, maximum = short.MaxValue },
            y = new { type = "integer", minimum = short.MinValue, maximum = short.MaxValue }
        },
            arguments => desktop.Tray(OptionalString(arguments, "action") ?? "status", OptionalInt(arguments, "x"), OptionalInt(arguments, "y")), ToolPermission.UiEffects)
    ];

    // character_touch's taps: [{x, y, holdMs}].
    private static IReadOnlyList<(double X, double Y, int HoldMs)>? Taps(JsonElement arguments)
    {
        if (arguments.ValueKind != JsonValueKind.Object || !arguments.TryGetProperty("taps", out var taps) || taps.ValueKind == JsonValueKind.Null)
            return null;
        if (taps.ValueKind != JsonValueKind.Array) throw new ArgumentException("'taps' must be an array of {x, y, holdMs}.");
        return taps.EnumerateArray().Take(DesktopAutomation.MaximumTaps + 1).Select(tap => (
            OptionalDouble(tap, "x") ?? throw new ArgumentException("Each tap needs x."),
            OptionalDouble(tap, "y") ?? throw new ArgumentException("Each tap needs y."),
            OptionalInt(tap, "holdMs") ?? 0)).ToArray();
    }

    /// <summary>character_stroke's points, [[x, y], ...], or null when none were given.</summary>
    private static (double X, double Y)[]? StrokePoints(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty("points", out var value) || value.ValueKind == JsonValueKind.Null)
            return null;
        if (value.ValueKind != JsonValueKind.Array) throw new ArgumentException("'points' must be an array of [x, y] pairs.");
        return [.. value.EnumerateArray().Select(point =>
            point.ValueKind == JsonValueKind.Array && point.GetArrayLength() == 2 && point[0].TryGetDouble(out var x) && point[1].TryGetDouble(out var y)
                ? (x, y) : throw new ArgumentException("Each point must be [x, y]."))];
    }

    /// <summary>character_mouth's loudness levels, [0.2, 0.8, ...], or null when none were given.</summary>
    private static double[]? MouthLevels(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty("levels", out var value) || value.ValueKind == JsonValueKind.Null)
            return null;
        if (value.ValueKind != JsonValueKind.Array) throw new ArgumentException("'levels' must be an array of numbers from 0 to 1.");
        return [.. value.EnumerateArray().Take(DesktopAutomation.MaximumVoiceLevels + 1).Select(level =>
            level.ValueKind == JsonValueKind.Number && level.TryGetDouble(out var number)
                ? number : throw new ArgumentException("Each level must be a number from 0 to 1."))];
    }
}
