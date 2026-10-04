using System.IO;
using System.Text.Json;
using Martlet.Avatar.Hosting;
using Martlet.Conversation;
using Martlet.Core.Settings;

namespace Martlet.Mcp;

/// <summary>character_gaze: Companion › Vision › Where the character looks as saved in a data directory (talk-preferences.json
/// DecideGaze: the mouse unless Martlet decides), then a rehearsal of the production decision (Martlet.Avatar.Hosting's
/// CharacterGaze and GazeDirector) on generated pictures (NOT screenshots; nothing is captured or shown): each scenario is a
/// screenshot before and after on a 1920×1080 screen, compared as the desktop compares them, with the mouse and the
/// character's overlay where it says. Also where each look tag points, what a screen glance is told about them (the data
/// directory's edited prompts included), and what the production segmenter makes of glance answers that start with a look
/// tag (what is spoken and shown, whether it stays quiet, and the cue the character acts on). Reads only; contacts nothing.</summary>
internal static class GazeCheck
{
    private const int Width = 1024, Height = 576;
    private static readonly ScreenRect Screen = new(0, 0, 1920, 1080);
    private static readonly ScreenPoint Mouse = new(400, 500);
    // Out of the way at the top-left, and where the character stands by default (near the lower-right corner, where
    // notifications pop up).
    private static readonly ScreenRect Aside = new(0, 0, 300, 380), Corner = new(1266, 476, 840, 580);

    internal static object Run(string dataDirectory, string? answer)
    {
        var settings = File.Exists(Path.Combine(dataDirectory, "settings.json"))
            ? SettingsJson.Read(File.ReadAllBytes(Path.Combine(dataDirectory, "settings.json"))) : null;
        var prompt = CharacterGaze.Prompt(settings?.Prompts, StayQuiet.Marker);
        var answers = answer is not null ? new[] { answer }
            : new[] { "{look bottom right} [pass]", "{look top right} Ooh, Sam just messaged you.", "Nice dodge!" };
        var (scenarios, ok) = Scenarios();
        return new
        {
            saved = Saved(dataDirectory),
            ok,
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
            notTags = new[] { "{look}", "{look up}", "{blush}" }.Where(tag => !CharacterGaze.IsTag(tag)).ToArray(),
            prompt = prompt is null ? null : new { instructions = prompt.Instructions, tags = prompt.Tags },
            replies = answers.Select(text =>
            {
                var preview = SpeechTextPreview.For(text, null, CharacterGaze.Tags, SpeechBreaks.Default, StayQuiet.Marker);
                return new
                {
                    answer = text, spoken = preview.Spoken, shown = preview.Shown.Trim(), quiet = StayQuiet.IsQuiet(preview.Shown),
                    looks = preview.Cues.Where(c => CharacterGaze.IsTag(c.Tag))
                        .Select(c => new { c.Tag, place = CharacterGaze.Chosen(c.Tag, Screen)?.Place, afterPiece = c.Piece }).ToArray()
                };
            }).ToArray()
        };
    }

    // talk-preferences.json (Martlet.Desktop's TalkPreferences): the character follows the mouse unless DecideGaze is saved on.
    private static string Saved(string directory)
    {
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, "talk-preferences.json")));
            return document.RootElement.TryGetProperty("DecideGaze", out var value) && value.ValueKind == JsonValueKind.True
                ? "martlet decides" : "mouse";
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException) { return "mouse"; }
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
