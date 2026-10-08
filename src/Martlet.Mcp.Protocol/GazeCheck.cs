using System.IO;
using System.Text.Json;
using Martlet.Avatar.Hosting;
using Martlet.Conversation;
using Martlet.Core.Settings;

namespace Martlet.Mcp;

/// <summary>character_gaze: where the character looks as saved in a data directory. Its usual gaze (talk-preferences.json
/// GazeUsual and GazeFree: Companion › Character › Where the character looks and the overlay's Eyes menu; the persona's gaze in
/// character-temperaments.json; else the mouse), what replies are told about it and an aim rehearsal of each gaze (the
/// production CharacterGaze.Aim the overlay uses, with a mouse near and far from the character and a window, with and without a
/// place the user worked in it), and a watch rehearsal of the window gaze (the production WindowWatch on a sequence of moments).
/// Then Companion ›
/// Vision › Glances at your screen (DecideGaze: the usual gaze unless Martlet decides) and a rehearsal of the production decision
/// (Martlet.Avatar.Hosting's CharacterGaze and GazeDirector) on generated pictures (NOT screenshots; nothing is captured or
/// shown): each scenario is a screenshot before and after on a 1920×1080 screen, compared as the desktop compares them, with
/// the mouse and the character's overlay where it says. Also where each look tag points, what a screen glance is told about
/// them (the data directory's edited prompts included), and what the production segmenter makes of answers with look tags
/// (what is spoken and shown, whether it stays quiet, and the cues the character acts on). Reads only; contacts nothing.</summary>
internal static class GazeCheck
{
    private const int Width = 1024, Height = 576;
    private static readonly ScreenRect Screen = new(0, 0, 1920, 1080);
    private static readonly ScreenPoint Mouse = new(400, 500);
    // Out of the way at the top-left, and where the character stands by default (near the lower-right corner, where
    // notifications pop up).
    private static readonly ScreenRect Aside = new(0, 0, 300, 380), Corner = new(1266, 476, 840, 580);
    // The character's frame inside that overlay (the overlay is the frame plus room on each side).
    private static readonly ScreenRect Frame = new(1476, 476, 420, 580);

    internal static object Run(string dataDirectory, string? answer, string? personaId = null)
    {
        var settings = File.Exists(Path.Combine(dataDirectory, "settings.json"))
            ? SettingsJson.Read(File.ReadAllBytes(Path.Combine(dataDirectory, "settings.json"))) : null;
        var prompt = CharacterGaze.Prompt(settings?.Prompts, StayQuiet.Marker);
        string[] lookTags = [.. CharacterGaze.Tags, .. CharacterGaze.ModeTags];
        var answers = answer is not null ? new[] { answer }
            : new[] { "{look bottom right} [pass]", "{look top right} Ooh, Sam just messaged you.", "Nice dodge!",
                "{look ahead} Hmph. Whatever.", "Fine, fine. {look usual} I'm listening." };
        var (scenarios, ok) = Scenarios();
        var (aim, aimOk) = Aims();
        var (watch, watchOk) = Watch();
        var (owner, free) = Usual(dataDirectory);
        var persona = Guid.TryParse(personaId, out var id) ? id : settings?.Companion?.ActivePersonaId;
        var temperament = persona is { } chosenPersona ? CharacterTouchTemperaments.Used(dataDirectory, chosenPersona) : null;
        var gaze = new GazeSettings(owner, temperament?.Gaze, free);
        var reply = free ? CharacterGaze.ReplyPrompt(settings?.Prompts, gaze.Usual) : null;
        var other = CharacterGaze.Modes.First(m => m.Mode != gaze.Usual).Mode;
        return new
        {
            saved = Saved(dataDirectory),
            usual = new
            {
                choice = RendererGaze.ChoiceOf(owner), free, personality = temperament?.Gaze is { } decided ? CharacterGaze.Word(decided) : null,
                personaId = persona, gaze = CharacterGaze.Word(gaze.Usual), from = gaze.UsualFrom,
                // What every reply is told while the character may change where it looks (null when it may not, or the prompt is emptied).
                prompt = reply is null ? null : new { instructions = reply.Instructions, tags = reply.Tags },
                // The note a reply gets while its own choice holds the eyes, for example one minute after it chose another gaze.
                noteWhenChanged = CharacterGaze.Note(settings?.Prompts, gaze with { Chosen = other }, TimeSpan.FromMinutes(1))
            },
            ok = ok && aimOk && watchOk,
            aim,
            watch,
            grid = new { columns = CharacterGaze.Columns, rows = CharacterGaze.Rows, CharacterGaze.ChangeThreshold, CharacterGaze.CharacterChangeThreshold },
            holdSeconds = new { glance = CharacterGaze.GlanceHold.TotalSeconds, chosen = CharacterGaze.ChosenHold.TotalSeconds,
                gap = CharacterGaze.GlanceGap.TotalSeconds },
            scenarios,
            tags = CharacterGaze.Tags.Select(tag => new
            {
                tag,
                oneScreen = Point(CharacterGaze.Chosen(tag, Screen)),
                twoScreens = Point(CharacterGaze.Chosen(tag, new ScreenRect(-1920, 0, 3840, 1080)))
            }).ToArray(),
            modeTags = CharacterGaze.ModeTags.Select(tag => new
            {
                tag, gaze = CharacterGaze.TryMode(tag, out var mode) && mode is { } known ? CharacterGaze.Word(known) : "usual"
            }).ToArray(),
            notTags = new[] { "{look}", "{look up}", "{blush}" }.Where(tag => !CharacterGaze.IsLookTag(tag)).ToArray(),
            prompt = prompt is null ? null : new { instructions = prompt.Instructions, tags = prompt.Tags },
            replies = answers.Select(text =>
            {
                var preview = SpeechTextPreview.For(text, null, lookTags, SpeechBreaks.Default, StayQuiet.Marker);
                return new
                {
                    answer = text, spoken = preview.Spoken, shown = preview.Shown.Trim(), quiet = StayQuiet.IsQuiet(preview.Shown),
                    looks = preview.Cues.Where(c => CharacterGaze.IsTag(c.Tag))
                        .Select(c => new { c.Tag, place = CharacterGaze.Chosen(c.Tag, Screen)?.Place, afterPiece = c.Piece }).ToArray(),
                    gazes = preview.Cues.Where(c => CharacterGaze.IsModeTag(c.Tag))
                        .Select(c => new
                        {
                            c.Tag, gaze = CharacterGaze.TryMode(c.Tag, out var mode) && mode is { } known ? CharacterGaze.Word(known) : "usual",
                            afterPiece = c.Piece
                        }).ToArray()
                };
            }).ToArray()
        };
    }

    // talk-preferences.json (Martlet.Desktop's TalkPreferences): the character keeps its usual gaze unless DecideGaze is saved on.
    private static string Saved(string directory) =>
        Preferences(directory) is { } root && root.TryGetProperty("DecideGaze", out var value) && value.ValueKind == JsonValueKind.True
            ? "martlet decides" : "usual gaze";

    // talk-preferences.json's GazeUsual (null: as the personality decides) and GazeFree (on unless saved off).
    private static (GazeMode? Owner, bool Free) Usual(string directory)
    {
        if (Preferences(directory) is not { } root) return (null, true);
        var owner = root.TryGetProperty("GazeUsual", out var usual) && usual.ValueKind == JsonValueKind.String ? CharacterGaze.ModeOf(usual.GetString()) : null;
        return (owner, !(root.TryGetProperty("GazeFree", out var free) && free.ValueKind == JsonValueKind.False));
    }

    private static JsonElement? Preferences(string directory)
    {
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, "talk-preferences.json")));
            return document.RootElement.ValueKind == JsonValueKind.Object ? document.RootElement.Clone() : null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException) { return null; }
    }

    // Where the overlay turns the eyes for each gaze (CharacterGaze.Aim, the code the overlay runs every 50 ms): the character's
    // frame near the lower-right corner, the mouse far from it or right beside it, and a window being used, with or without a
    // place the user worked in it.
    private static (object[] Results, bool Ok) Aims()
    {
        ScreenPoint far = new(300, 300), near = new(1400, 700);
        ScreenRect window = new(100, 100, 1200, 800);
        var results = new List<object>();
        var ok = true;
        void Aim(string name, string expected, GazeMode mode, ScreenPoint? mouse, ScreenRect? used = null, bool attending = false,
            ScreenPoint? point = null, ScreenPoint? working = null, ScreenPoint? expectedAt = null)
        {
            var (target, at) = CharacterGaze.Aim(mode, point, attending, mouse, Frame, used, working);
            var right = target == expected && (expectedAt is null || at == expectedAt);
            ok &= right;
            results.Add(new
            {
                name, gaze = CharacterGaze.Word(mode), expected, target, ok = right,
                at = at is { } spot ? new { x = Math.Round(spot.X), y = Math.Round(spot.Y) } : null
            });
        }
        Aim("mouseFar", "mouse", GazeMode.Mouse, far);
        Aim("nearModeMouseFar", "ahead", GazeMode.Near, far);
        Aim("nearModeMouseNear", "mouse", GazeMode.Near, near);
        Aim("aheadModeMouseNear", "ahead", GazeMode.Ahead, near);
        Aim("windowMode", "window", GazeMode.Window, far, window, expectedAt: window.Middle);
        Aim("windowModeWorking", "window", GazeMode.Window, far, window, working: new(1150, 820), expectedAt: new(1150, 820));
        Aim("windowModeWorkedOutside", "window", GazeMode.Window, far, window, working: new(1700, 950), expectedAt: window.Middle);
        Aim("windowModeNoWindow", "ahead", GazeMode.Window, far);
        Aim("touchWhileAhead", "mouse", GazeMode.Ahead, far, attending: true);
        Aim("glanceWhileFollowing", "point", GazeMode.Mouse, far, point: new ScreenPoint(1700, 1000));
        return ([.. results], ok);
    }

    // What the window gaze watches (the production WindowWatch with CharacterGaze.Aim, as the overlay runs them every 50 ms) over
    // a sequence of moments: the window in use, the mouse pointer and whether it is over that window (not the character or
    // another window), and its text cursor.
    private static (object[] Results, bool Ok) Watch()
    {
        ScreenRect editor = new(100, 100, 1200, 800), chat = new(200, 150, 900, 700);
        ScreenPoint onCharacter = new(1600, 700);
        var watch = new WindowWatch();
        var results = new List<object>();
        var ok = true;
        void Moment(string name, string? expected, ScreenPoint? expectedAt, nint used, ScreenRect? area, ScreenPoint? pointer, bool over,
            ScreenPoint? textCursor = null)
        {
            watch.Update(used, pointer, over, textCursor);
            var (target, at) = CharacterGaze.Aim(GazeMode.Window, null, false, pointer, Frame, area, watch.Working);
            var watching = target == "window" ? watch.Watching(area) : null;
            var right = watching == expected && at == expectedAt;
            ok &= right;
            results.Add(new
            {
                name, expected, watching, target, ok = right,
                at = at is { } spot ? new { x = Math.Round(spot.X), y = Math.Round(spot.Y) } : null
            });
        }
        Moment("switchedWithTheKeyboard", WindowWatch.Middle, editor.Middle, 1, editor, onCharacter, false);
        Moment("pointerMovesOverTheWindow", WindowWatch.Pointer, new(400, 300), 1, editor, new(400, 300), true);
        Moment("typingWhileThePointerRests", WindowWatch.TextCursor, new(250, 180), 1, editor, new(400, 300), true, new(250, 180));
        Moment("typingOn", WindowWatch.TextCursor, new(320, 180), 1, editor, new(400, 300), true, new(320, 180));
        Moment("pointerOnTheCharacter", WindowWatch.TextCursor, new(320, 180), 1, editor, onCharacter, false, new(320, 180));
        Moment("pointerMovesOverTheWindowAgain", WindowWatch.Pointer, new(950, 640), 1, editor, new(950, 640), true, new(320, 180));
        Moment("pointerBackOnTheCharacter", WindowWatch.Pointer, new(950, 640), 1, editor, onCharacter, false, new(320, 180));
        Moment("anotherWindowWithATextCursor", WindowWatch.TextCursor, new(500, 700), 2, chat, onCharacter, false, new(500, 700));
        Moment("desktopInFront", null, null, 0, null, onCharacter, false);
        Moment("clickedBackIntoTheWindow", WindowWatch.Pointer, new(700, 500), 1, editor, new(700, 500), true);
        return ([.. results], ok);
    }

    private static object? Point(GazeSpot? spot) => spot is null ? null : new { x = Math.Round(spot.X), y = Math.Round(spot.Y), spot.Place };

    private static (object[] Results, bool Ok) Scenarios()
    {
        var clock = new SteppedClock();
        var desktop = Desktop();
        var results = new List<object>();
        var ok = true;
        void Run(GazeDirector director, string name, string expected, byte[] before, byte[] after, ScreenRect character,
            ScreenRect[]? hidden = null, double seconds = 10)
        {
            clock.Advance(TimeSpan.FromSeconds(seconds));
            var changes = CharacterGaze.Changes(CharacterGaze.Grid(before, Width, Height), CharacterGaze.Grid(after, Width, Height));
            var decision = director.Decide(changes, Screen, Mouse, [character], hidden);
            ok &= decision.Verdict.ToString() == expected;
            results.Add(new
            {
                name, expected, verdict = decision.Verdict.ToString(), ok = decision.Verdict.ToString() == expected,
                spot = decision.Spot is { } spot ? new
                {
                    x = Math.Round(spot.X), y = Math.Round(spot.Y), spot.Place, reason = spot.Reason.ToString(), holdSeconds = spot.Hold.TotalSeconds
                } : null
            });
        }
        // A notification card pops up near the lower-right corner, above the taskbar; one director sees it come and go.
        var toast = Paint(desktop, 1530, 900, 1900, 1030, 235);
        var watching = new GazeDirector(clock);
        Run(watching, "still", "Still", desktop, desktop, Aside);
        Run(watching, "notification", "Glance", desktop, toast, Aside);
        Run(watching, "anotherChangeRightAfter", "TooSoon", desktop, Paint(desktop, 100, 760, 420, 860, 235), Aside, seconds: 3);
        Run(watching, "sameSpotAgainSoon", "Seen", desktop, toast, Aside, seconds: 7);
        Run(watching, "sameSpotAgainLater", "Glance", desktop, toast, Aside, seconds: 21);
        Run(new(clock), "notificationBehindTheCharacter", "Glance", desktop, toast, Corner);
        Run(new(clock), "onlyTheCharacterMoved", "OnlyCharacter", desktop, Paint(desktop, 1500, 600, 1800, 900, -30), Corner);
        Run(new(clock), "speechBubble", "OnlyCharacter", desktop, Paint(desktop, 1000, 380, 1300, 470, 250), Corner,
            [new ScreenRect(990, 370, 320, 110)]);
        Run(new(clock), "newScene", "Everywhere", desktop, Paint(desktop, 0, 0, 1920, 1080, -90), Aside);
        Run(new(clock), "byTheMouse", "ByMouse", desktop, Paint(desktop, 330, 450, 480, 560, 235), Aside);
        var scattered = desktop;
        foreach (var (x, y) in new[] { (1000, 100), (1700, 150), (1100, 700), (1750, 650), (600, 950), (1300, 1000) })
            scattered = Paint(scattered, x, y, x + 60, y + 60, 250);
        Run(new(clock), "changesAllOver", "Scattered", desktop, scattered, Aside);
        return ([.. results], ok);
    }

    // A plain desktop: a soft diagonal gradient with a big window on it, as a 1024×576 BGRA screenshot of the 1920×1080 screen.
    private static byte[] Desktop()
    {
        var pixels = new byte[Width * Height * 4];
        for (var y = 0; y < Height; y++)
        for (var x = 0; x < Width; x++)
        {
            var grey = (byte)(60 + (x + y) * 60 / (Width + Height));
            if (x is > 150 and < 700 && y is > 80 and < 450) grey = 210;
            var i = (y * Width + x) * 4;
            pixels[i] = pixels[i + 1] = pixels[i + 2] = grey;
            pixels[i + 3] = 255;
        }
        return pixels;
    }

    // The picture with a screen rectangle painted one grey (or, with a negative value, darkened by that much).
    private static byte[] Paint(byte[] source, int left, int top, int right, int bottom, int grey)
    {
        var pixels = (byte[])source.Clone();
        int x0 = left * Width / Screen.Width, x1 = right * Width / Screen.Width, y0 = top * Height / Screen.Height, y1 = bottom * Height / Screen.Height;
        for (var y = Math.Max(0, y0); y < Math.Min(Height, y1); y++)
        for (var x = Math.Max(0, x0); x < Math.Min(Width, x1); x++)
        {
            var i = (y * Width + x) * 4;
            var value = (byte)(grey >= 0 ? grey : Math.Max(0, pixels[i] + grey));
            pixels[i] = pixels[i + 1] = pixels[i + 2] = value;
        }
        return pixels;
    }

    private sealed class SteppedClock : TimeProvider
    {
        private long ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => ticks;
        internal void Advance(TimeSpan time) => ticks += time.Ticks;
    }
}
