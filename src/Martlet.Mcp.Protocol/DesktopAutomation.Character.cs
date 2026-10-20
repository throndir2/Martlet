using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Automation;
using System.Windows.Automation.Text;

namespace Martlet.Mcp;

internal sealed partial class DesktopAutomation
{
    /// <summary>Taps the showing character at <paramref name="x"/>, <paramref name="y"/> (fractions 0..1 of the overlay's
    /// drawing, +y down; needs --allow-ui-effects) through MoveAvatar's UI Automation value, like a click there held for
    /// <paramref name="holdMs"/> (600 or more: a hold), <paramref name="repeat"/> times <paramref name="gapMs"/> apart, or each
    /// of <paramref name="taps"/> (x, y, holdMs) in turn, waiting for the renderer's hit test after each; without a point it only
    /// reads the last tap. Returns the last tap as the overlay reports it and, when Companion › Touch › Touch zones shows,
    /// what Martlet noticed (TouchZonesNoticed, TouchZonesNoticedLast) after <paramref name="settleMs"/>.</summary>
    internal async Task<object> TouchCharacterAsync(double? x, double? y, int? holdMs = null, int? repeat = null, int? gapMs = null,
        IReadOnlyList<(double X, double Y, int HoldMs)>? taps = null, int? settleMs = null)
    {
        if ((x is null) != (y is null)) throw new ArgumentException("Give both x and y, or neither to read the last tap.");
        if (taps is not null && x is not null) throw new ArgumentException("Give x and y, or taps, not both.");
        var element = Find("MoveAvatar");
        if (!element.TryGetCurrentPattern(ValuePattern.Pattern, out var pattern))
            throw new InvalidOperationException("The character overlay can't be tapped through UI Automation.");
        var value = (ValuePattern)pattern;
        static object? Read(string text) => string.IsNullOrEmpty(text) ? null : System.Text.Json.JsonDocument.Parse(text).RootElement.Clone();
        if (x is null && taps is null) return new { touched = false, last = Read(value.Current.Value), noticed = Noticed() };
        if (!allowEffects) throw new InvalidOperationException("Tapping the character requires --allow-ui-effects.");
        var times = repeat ?? 1;
        if (times is < 1 or > MaximumTaps) throw new ArgumentException($"repeat is 1 to {MaximumTaps}.");
        if (holdMs is < 0 or > 10_000) throw new ArgumentException("holdMs is 0 to 10000.");
        if (gapMs is < 0 or > 10_000) throw new ArgumentException("gapMs is 0 to 10000.");
        if (settleMs is < 0 or > 15_000) throw new ArgumentException("settleMs is 0 to 15000.");
        var sequence = taps ?? Enumerable.Repeat((x!.Value, y!.Value, holdMs ?? 0), times).ToArray();
        if (sequence.Count is < 1 or > MaximumTaps) throw new ArgumentException($"taps holds 1 to {MaximumTaps} taps.");
        if (sequence.Any(t => t.X is not (>= 0 and <= 1) || t.Y is not (>= 0 and <= 1) || t.HoldMs is < 0 or > 10_000))
            throw new ArgumentException("Each tap's x and y are fractions from 0 to 1 and holdMs is 0 to 10000.");
        if (value.Current.IsReadOnly) throw new InvalidOperationException("The character can't be tapped until it has loaded.");
        var answered = 0;
        string after = value.Current.Value;
        for (var i = 0; i < sequence.Count; i++)
        {
            if (i > 0) await Task.Delay(gapMs ?? 150);
            var (tx, ty, held) = sequence[i];
            var before = value.Current.Value;
            value.SetValue(held > 0 ? FormattableString.Invariant($"{tx},{ty},{held}") : FormattableString.Invariant($"{tx},{ty}"));
            var waited = System.Diagnostics.Stopwatch.StartNew();
            while ((after = value.Current.Value) == before && waited.Elapsed < TimeSpan.FromSeconds(3)) await Task.Delay(50);
            if (after != before) answered++;
        }
        if (settleMs is > 0) await Task.Delay(settleMs.Value);
        return answered == 0
            ? new { touched = false, taps = sequence.Count, answered, last = Read(after), noticed = Noticed(), note = (string?)"The renderer didn't answer the tap within 3 seconds." }
            : new { touched = true, taps = sequence.Count, answered, last = Read(after), noticed = Noticed(), note = (string?)null };
    }

    // What Martlet noticed, when Companion › Touch › Touch zones shows (null otherwise).
    private object? Noticed()
    {
        string? Text(string id)
        {
            try { return Find(id).Current.Name; }
            catch (ArgumentException) { return null; }
        }
        var waiting = Text("TouchZonesNoticed");
        return waiting is null ? null : new { waiting, last = Text("TouchZonesNoticedLast"), zone = Text("TouchZonesLast") };
    }

    /// <summary>Strokes the showing, locked character along <paramref name="points"/> (fractions 0..1 of the overlay's drawing; needs
    /// --allow-ui-effects) through MoveAvatar's UI Automation value ("stroke:ms;x,y;..."), then waits for the overlay's stroke
    /// record; without points it only reads the last one. Returns the overlay's reading (last tap, last stroke, last change).</summary>
    internal async Task<object> StrokeCharacterAsync((double X, double Y)[]? points, int stepMs)
    {
        var element = Find("MoveAvatar");
        if (!element.TryGetCurrentPattern(ValuePattern.Pattern, out var pattern))
            throw new InvalidOperationException("The character overlay can't be stroked through UI Automation.");
        var value = (ValuePattern)pattern;
        static object? Read(string text) => string.IsNullOrEmpty(text) ? null : System.Text.Json.JsonDocument.Parse(text).RootElement.Clone();
        static string? Stroke(string text) => string.IsNullOrEmpty(text) ? null :
            System.Text.Json.JsonDocument.Parse(text).RootElement is { ValueKind: System.Text.Json.JsonValueKind.Object } root &&
            root.TryGetProperty("stroke", out var stroke) ? stroke.GetRawText() : null;
        if (points is null) return new { stroked = false, last = Read(value.Current.Value) };
        if (!allowEffects) throw new InvalidOperationException("Stroking the character requires --allow-ui-effects.");
        if (points.Length is < 2 or > 200) throw new ArgumentException("Give 2 to 200 points.");
        if (points.Any(p => p.X is not (>= 0 and <= 1) || p.Y is not (>= 0 and <= 1))) throw new ArgumentException("x and y are fractions from 0 to 1.");
        if (stepMs is < 10 or > 2000) throw new ArgumentException("stepMs is 10 to 2000.");
        if (value.Current.IsReadOnly) throw new InvalidOperationException("The character can't be stroked until it has loaded.");
        var before = Stroke(value.Current.Value);
        value.SetValue("stroke:" + string.Join(";", new[] { stepMs.ToString(System.Globalization.CultureInfo.InvariantCulture) }
            .Concat(points.Select(p => FormattableString.Invariant($"{p.X},{p.Y}")))));
        var waited = System.Diagnostics.Stopwatch.StartNew();
        var limit = TimeSpan.FromMilliseconds(points.Length * stepMs + 3000);
        string after;
        while (Stroke(after = value.Current.Value) == before && waited.Elapsed < limit) await Task.Delay(50);
        return Stroke(after) == before
            ? new { stroked = false, last = Read(after), note = "The overlay didn't finish the stroke in time." }
            : new { stroked = true, last = Read(after) };
    }

    internal const int MaximumFaceSamples = 60;

    /// <summary>Takes a picture of the showing character as it shows now through MoveAvatar's UI Automation value ("picture"):
    /// the renderer's own capture of the overlay's page, so Martlet's drawings over the face (the blush glow, overlay emotes
    /// such as heart eyes) are in it, cropped to the character. It changes nothing on the character, so it needs no
    /// --allow-ui-effects. The renderer writes the PNG to its file in the temp folder; with <paramref name="outputPath"/> (a full
    /// path to a .png file) it is copied there. Returns the picture's reading: the file, its size and the crop of the overlay's
    /// page it shows (fractions, like character_face's positions).</summary>
    internal async Task<object> PictureCharacterAsync(string? outputPath)
    {
        if (outputPath is not null && (!System.IO.Path.IsPathFullyQualified(outputPath) ||
            !outputPath.EndsWith(".png", StringComparison.OrdinalIgnoreCase)))
            throw new ArgumentException("outputPath is a full path to a .png file.");
        if (outputPath is not null) McpServer.RequireLocalNewFile(outputPath, "outputPath");
        var element = Find("MoveAvatar");
        if (!element.TryGetCurrentPattern(ValuePattern.Pattern, out var pattern))
            throw new InvalidOperationException("The character overlay can't be read through UI Automation.");
        var value = (ValuePattern)pattern;
        if (value.Current.IsReadOnly) throw new InvalidOperationException("The character's picture can't be taken until it has loaded.");
        static System.Text.Json.JsonElement? Picture(string text) => string.IsNullOrEmpty(text) ? null :
            System.Text.Json.JsonDocument.Parse(text).RootElement is { ValueKind: System.Text.Json.JsonValueKind.Object } root &&
            root.TryGetProperty("picture", out var picture) ? picture.Clone() : null;
        var before = Picture(value.Current.Value)?.GetRawText();
        value.SetValue("picture");
        var waited = Stopwatch.StartNew();
        System.Text.Json.JsonElement? after;
        while ((after = Picture(value.Current.Value))?.GetRawText() == before && waited.Elapsed < TimeSpan.FromSeconds(10)) await Task.Delay(50);
        if (after is not { } read || read.GetRawText() == before)
            return new { taken = false, note = "The renderer didn't take the picture within 10 seconds." };
        var taken = !read.TryGetProperty("error", out _);
        string? saved = null;
        if (taken && outputPath is not null && read.TryGetProperty("path", out var path) && path.GetString() is { } file && System.IO.File.Exists(file))
        {
            System.IO.File.Copy(file, outputPath, overwrite: false);
            saved = outputPath;
        }
        return new { taken, picture = read, saved };
    }

    /// <summary>Reads where Martlet draws over the showing character's face (the blush levels and overlay emotes)
    /// <paramref name="samples"/> times, <paramref name="gapMs"/> apart, through MoveAvatar's UI Automation value ("face"). It
    /// changes nothing, so it needs no --allow-ui-effects. Returns each reading (fractions of the overlay's drawing, +y down)
    /// and a summary: how the face is followed, how far it moved, turned and tilted, what of the character is under each
    /// cheek, and where the eyes came from, how each iris moved and how far each eye's opening closed.</summary>
    internal async Task<object> FaceCharacterAsync(int? samples, int? gapMs)
    {
        var count = samples ?? 1;
        if (count is < 1 or > MaximumFaceSamples) throw new ArgumentException($"samples is 1 to {MaximumFaceSamples}.");
        if (gapMs is < 0 or > 5000) throw new ArgumentException("gapMs is 0 to 5000.");
        var element = Find("MoveAvatar");
        if (!element.TryGetCurrentPattern(ValuePattern.Pattern, out var pattern))
            throw new InvalidOperationException("The character overlay can't be read through UI Automation.");
        var value = (ValuePattern)pattern;
        if (value.Current.IsReadOnly) throw new InvalidOperationException("The character's face can't be read until it has loaded.");
        static System.Text.Json.JsonElement? Face(string text) => string.IsNullOrEmpty(text) ? null :
            System.Text.Json.JsonDocument.Parse(text).RootElement is { ValueKind: System.Text.Json.JsonValueKind.Object } root &&
            root.TryGetProperty("face", out var face) ? face.Clone() : null;
        var faces = new List<System.Text.Json.JsonElement>();
        for (var i = 0; i < count; i++)
        {
            if (i > 0) await Task.Delay(gapMs ?? 250);
            var before = Face(value.Current.Value)?.GetRawText();
            value.SetValue("face");
            var waited = Stopwatch.StartNew();
            System.Text.Json.JsonElement? after;
            while ((after = Face(value.Current.Value))?.GetRawText() == before && waited.Elapsed < TimeSpan.FromSeconds(3)) await Task.Delay(20);
            if (after is { } read && read.GetRawText() != before) faces.Add(read);
        }
        return new { samples = count, read = faces.Count, faces, summary = FaceSummary(faces),
            note = faces.Count == 0 ? "The renderer didn't answer within 3 seconds." : null };
    }

    // How the readings went together: the tracking used, how far the face moved, scaled and tilted (fractions of the drawing,
    // degrees), per cheek the share of readings it was over the character, what it was mostly over (the topmost drawable,
    // mesh or bone) and for what share, how much of it showed at least and how wide it was against the face, where the eyes
    // came from and per eye how its iris moved and sat in its opening and how far the opening closed.
    internal static object FaceSummary(IReadOnlyList<System.Text.Json.JsonElement> faces)
    {
        static bool Is(System.Text.Json.JsonElement owner, string key, System.Text.Json.JsonValueKind kind) =>
            owner.ValueKind == System.Text.Json.JsonValueKind.Object && owner.TryGetProperty(key, out var value) && value.ValueKind == kind;
        static double[] Numbers(IEnumerable<System.Text.Json.JsonElement> items, string key) =>
            [.. items.Where(item => Is(item, key, System.Text.Json.JsonValueKind.Number)).Select(item => item.GetProperty(key).GetDouble())];
        static double Spread(double[] values) => values.Length == 0 ? 0 : Math.Round(values.Max() - values.Min(), 4);
        var found = faces.Where(face => Is(face, "found", System.Text.Json.JsonValueKind.True)).ToArray();
        object Cheek(string key)
        {
            var cheeks = found.Where(face => Is(face, key, System.Text.Json.JsonValueKind.Object)).Select(face => face.GetProperty(key)).ToArray();
            static string? Under(System.Text.Json.JsonElement cheek) => !Is(cheek, "hit", System.Text.Json.JsonValueKind.True) ? null
                : Is(cheek, "drawables", System.Text.Json.JsonValueKind.Array) && cheek.GetProperty("drawables").GetArrayLength() > 0
                    ? cheek.GetProperty("drawables")[0].GetString()
                : Is(cheek, "mesh", System.Text.Json.JsonValueKind.String) ? cheek.GetProperty("mesh").GetString()
                : Is(cheek, "bone", System.Text.Json.JsonValueKind.String) ? cheek.GetProperty("bone").GetString() : "character";
            var under = cheeks.Select(Under).ToArray();
            var most = under.OfType<string>().GroupBy(name => name).OrderByDescending(group => group.Count()).FirstOrDefault();
            double Share(int part) => cheeks.Length == 0 ? 0 : Math.Round((double)part / cheeks.Length, 2);
            var visible = Numbers(cheeks, "visible");
            var across = Numbers(cheeks, "across");
            return new { onCharacter = Share(under.Count(name => name is not null)), mostlyOver = most?.Key, mostlyOverShare = Share(most?.Count() ?? 0),
                visibleLeast = visible.Length == 0 ? (double?)null : visible.Min(),
                across = across.Length == 0 ? null : new { least = across.Min(), most = across.Max() } };
        }
        // Per eye: the share of readings with an iris, how far its middle moved, the share of readings with an opening whose
        // iris was inside it, the least and most the opening's box was high (a blink closes it) and the share it was closed.
        object Eye(string iris, string shape)
        {
            var irises = found.Where(face => Is(face, iris, System.Text.Json.JsonValueKind.Object)).Select(face => face.GetProperty(iris)).ToArray();
            var shapes = found.Where(face => Is(face, shape, System.Text.Json.JsonValueKind.Object)).Select(face => face.GetProperty(shape)).ToArray();
            static int Points(System.Text.Json.JsonElement opening) =>
                Is(opening, "points", System.Text.Json.JsonValueKind.Number) && opening.GetProperty("points").TryGetInt32(out var count) ? count : 0;
            static double Part(int part, int of) => of == 0 ? 0 : Math.Round((double)part / of, 2);
            var open = shapes.Where(opening => Points(opening) >= 3 && (Is(opening, "irisInside", System.Text.Json.JsonValueKind.True) ||
                Is(opening, "irisInside", System.Text.Json.JsonValueKind.False))).ToArray();
            var heights = shapes.Where(opening => Is(opening, "top", System.Text.Json.JsonValueKind.Number) && Is(opening, "bottom", System.Text.Json.JsonValueKind.Number))
                .Select(opening => Math.Round(opening.GetProperty("bottom").GetDouble() - opening.GetProperty("top").GetDouble(), 4)).ToArray();
            return new
            {
                iris = Part(irises.Length, found.Length),
                irisMoved = new { x = Spread(Numbers(irises, "x")), y = Spread(Numbers(irises, "y")) },
                irisInside = open.Length == 0 ? (double?)null : Part(open.Count(opening => Is(opening, "irisInside", System.Text.Json.JsonValueKind.True)), open.Length),
                opening = heights.Length == 0 ? null : new { least = heights.Min(), most = heights.Max() },
                closed = Part(shapes.Count(opening => Points(opening) == 0), shapes.Length)
            };
        }
        return new
        {
            found = found.Length,
            tracking = found.Where(face => Is(face, "tracking", System.Text.Json.JsonValueKind.String))
                .Select(face => face.GetProperty("tracking").GetString()).Distinct().ToArray(),
            moved = new { x = Spread(Numbers(found, "x")), y = Spread(Numbers(found, "y")), width = Spread(Numbers(found, "width")),
                tilt = Spread(Numbers(found, "tilt")) },
            cheekLeft = Cheek("cheekLeft"), cheekRight = Cheek("cheekRight"),
            eyesFrom = found.Where(face => Is(face, "eyesFrom", System.Text.Json.JsonValueKind.String))
                .Select(face => face.GetProperty("eyesFrom").GetString()).Distinct().ToArray(),
            eyeLeft = Eye("irisLeft", "eyeLeftShape"), eyeRight = Eye("irisRight", "eyeRightShape")
        };
    }

    /// <summary>Reads where the showing character looks now <paramref name="samples"/> times, <paramref name="gapMs"/> apart,
    /// through MoveAvatar's UI Automation value ("look"). It changes nothing, so it needs no --allow-ui-effects. Returns each
    /// reading (what the eyes are on, the direction and the point, the usual gaze, and the window you're using and what the eyes
    /// watch in it) and a summary: the targets and what the eyes watched in the window, and the range of the direction.</summary>
    internal async Task<object> LookCharacterAsync(int? samples, int? gapMs)
    {
        var count = samples ?? 1;
        if (count is < 1 or > MaximumLookSamples) throw new ArgumentException($"samples is 1 to {MaximumLookSamples}.");
        if (gapMs is < 0 or > 5000) throw new ArgumentException("gapMs is 0 to 5000.");
        var element = Find("MoveAvatar");
        if (!element.TryGetCurrentPattern(ValuePattern.Pattern, out var pattern))
            throw new InvalidOperationException("The character overlay can't be read through UI Automation.");
        var value = (ValuePattern)pattern;
        if (value.Current.IsReadOnly) throw new InvalidOperationException("Where the character looks can't be read until it has loaded.");
        static System.Text.Json.JsonElement? Look(string text) => string.IsNullOrEmpty(text) ? null :
            System.Text.Json.JsonDocument.Parse(text).RootElement is { ValueKind: System.Text.Json.JsonValueKind.Object } root &&
            root.TryGetProperty("look", out var look) ? look.Clone() : null;
        var looks = new List<System.Text.Json.JsonElement>();
        for (var i = 0; i < count; i++)
        {
            if (i > 0) await Task.Delay(gapMs ?? 250);
            var before = Look(value.Current.Value)?.GetRawText();
            value.SetValue("look");
            var waited = Stopwatch.StartNew();
            System.Text.Json.JsonElement? after;
            while ((after = Look(value.Current.Value))?.GetRawText() == before && waited.Elapsed < TimeSpan.FromSeconds(3)) await Task.Delay(20);
            if (after is { } read && read.GetRawText() != before) looks.Add(read);
        }
        return new { samples = count, read = looks.Count, looks, summary = LookSummary(looks),
            note = looks.Count == 0 ? "The renderer didn't answer within 3 seconds." : null };
    }

    // The targets and what the eyes watched in the window across the readings, and the least and most of the direction.
    internal static object LookSummary(IReadOnlyList<System.Text.Json.JsonElement> looks)
    {
        string[] Words(string name) => [.. looks.Select(look => look.TryGetProperty(name, out var word) &&
            word.ValueKind == System.Text.Json.JsonValueKind.String ? word.GetString() : null).OfType<string>().Distinct()];
        object? Range(string name)
        {
            double[] values = [.. looks.Where(look => look.TryGetProperty(name, out var number) && number.ValueKind == System.Text.Json.JsonValueKind.Number)
                .Select(look => look.GetProperty(name).GetDouble())];
            return values.Length == 0 ? null : new { least = values.Min(), most = values.Max() };
        }
        return new { targets = Words("target"), watching = Words("watching"), x = Range("x"), y = Range("y") };
    }

    /// <summary>Reads where each area of the showing character's touch zones is now (as Martlet last gave them; Touch zones › Show
    /// the zones on the character draws them) <paramref name="samples"/> times, <paramref name="gapMs"/> apart, through
    /// MoveAvatar's UI Automation value ("zones"). It changes nothing, so it needs no --allow-ui-effects. Returns each reading
    /// (fractions of the overlay's drawing, +y down) and a summary per area: what placed it and how far its middle moved.</summary>
    internal async Task<object> ZonesCharacterAsync(int? samples, int? gapMs)
    {
        var count = samples ?? 1;
        if (count is < 1 or > MaximumZoneSamples) throw new ArgumentException($"samples is 1 to {MaximumZoneSamples}.");
        if (gapMs is < 0 or > 5000) throw new ArgumentException("gapMs is 0 to 5000.");
        var element = Find("MoveAvatar");
        if (!element.TryGetCurrentPattern(ValuePattern.Pattern, out var pattern))
            throw new InvalidOperationException("The character overlay can't be read through UI Automation.");
        var value = (ValuePattern)pattern;
        if (value.Current.IsReadOnly) throw new InvalidOperationException("The character's touch zones can't be read until it has loaded.");
        static System.Text.Json.JsonElement? Zones(string text) => string.IsNullOrEmpty(text) ? null :
            System.Text.Json.JsonDocument.Parse(text).RootElement is { ValueKind: System.Text.Json.JsonValueKind.Object } root &&
            root.TryGetProperty("zones", out var zones) ? zones.Clone() : null;
        var readings = new List<System.Text.Json.JsonElement>();
        for (var i = 0; i < count; i++)
        {
            if (i > 0) await Task.Delay(gapMs ?? 250);
            var before = Zones(value.Current.Value)?.GetRawText();
            value.SetValue("zones");
            var waited = Stopwatch.StartNew();
            System.Text.Json.JsonElement? after;
            while ((after = Zones(value.Current.Value))?.GetRawText() == before && waited.Elapsed < TimeSpan.FromSeconds(3)) await Task.Delay(20);
            if (after is { } read && read.GetRawText() != before) readings.Add(read);
        }
        return new { samples = count, read = readings.Count, last = readings.LastOrDefault(), summary = ZonesSummary(readings),
            note = readings.Count == 0 ? "The renderer didn't answer within 3 seconds." : null };
    }

    // How the readings went together, per area (its zone and place among the zone's areas): what placed it, its last box and how
    // far its middle moved across the readings (fractions of the drawing).
    internal static object[] ZonesSummary(IReadOnlyList<System.Text.Json.JsonElement> readings)
    {
        var areas = new Dictionary<(string Zone, int Area), List<(string From, double Left, double Top, double Right, double Bottom)>>();
        foreach (var reading in readings)
        {
            if (reading.ValueKind != System.Text.Json.JsonValueKind.Object || !reading.TryGetProperty("areas", out var list) ||
                list.ValueKind != System.Text.Json.JsonValueKind.Array) continue;
            foreach (var area in list.EnumerateArray())
            {
                if (area.ValueKind != System.Text.Json.JsonValueKind.Object || !area.TryGetProperty("zone", out var zone) ||
                    !area.TryGetProperty("area", out var index) || !index.TryGetInt32(out var at)) continue;
                double Number(string name) => area.TryGetProperty(name, out var number) && number.ValueKind == System.Text.Json.JsonValueKind.Number
                    ? number.GetDouble() : double.NaN;
                var key = (zone.GetString() ?? "", at);
                if (!areas.TryGetValue(key, out var seen)) areas[key] = seen = [];
                seen.Add((area.TryGetProperty("from", out var from) ? from.GetString() ?? "" : "", Number("left"), Number("top"), Number("right"), Number("bottom")));
            }
        }
        static double Spread(IEnumerable<double> values)
        {
            var finite = values.Where(double.IsFinite).ToArray();
            return finite.Length == 0 ? 0 : Math.Round(finite.Max() - finite.Min(), 4);
        }
        return [.. areas.OrderBy(a => a.Key.Zone, StringComparer.Ordinal).ThenBy(a => a.Key.Area).Select(a => (object)new
        {
            zone = a.Key.Zone, area = a.Key.Area, from = a.Value.Select(v => v.From).Distinct().ToArray(),
            box = new[] { a.Value[^1].Left, a.Value[^1].Top, a.Value[^1].Right, a.Value[^1].Bottom },
            moved = new { x = Spread(a.Value.Select(v => (v.Left + v.Right) / 2)), y = Spread(a.Value.Select(v => (v.Top + v.Bottom) / 2)) }
        })];
    }

    /// <summary>Reads what the showing character's idle body does (a VRM's breath, arm hang, finger curl and sway)
    /// <paramref name="samples"/> times, <paramref name="gapMs"/> apart, through MoveAvatar's UI Automation value ("pose"). It
    /// changes nothing, so it needs no --allow-ui-effects. Returns each reading and a summary: how full the breath got, the
    /// breaths a minute, how the arms hang and the fingers curl, the sway and how far the head, shoulders and hands moved.</summary>
    internal async Task<object> PoseCharacterAsync(int? samples, int? gapMs)
    {
        var count = samples ?? 1;
        if (count is < 1 or > MaximumPoseSamples) throw new ArgumentException($"samples is 1 to {MaximumPoseSamples}.");
        if (gapMs is < 0 or > 5000) throw new ArgumentException("gapMs is 0 to 5000.");
        var element = Find("MoveAvatar");
        if (!element.TryGetCurrentPattern(ValuePattern.Pattern, out var pattern))
            throw new InvalidOperationException("The character overlay can't be read through UI Automation.");
        var value = (ValuePattern)pattern;
        if (value.Current.IsReadOnly) throw new InvalidOperationException("The character's pose can't be read until it has loaded.");
        static System.Text.Json.JsonElement? Pose(string text) => string.IsNullOrEmpty(text) ? null :
            System.Text.Json.JsonDocument.Parse(text).RootElement is { ValueKind: System.Text.Json.JsonValueKind.Object } root &&
            root.TryGetProperty("pose", out var pose) ? pose.Clone() : null;
        var poses = new List<System.Text.Json.JsonElement>();
        for (var i = 0; i < count; i++)
        {
            if (i > 0) await Task.Delay(gapMs ?? 250);
            var before = Pose(value.Current.Value)?.GetRawText();
            value.SetValue("pose");
            var waited = Stopwatch.StartNew();
            System.Text.Json.JsonElement? after;
            while ((after = Pose(value.Current.Value))?.GetRawText() == before && waited.Elapsed < TimeSpan.FromSeconds(3)) await Task.Delay(20);
            if (after is { } read && read.GetRawText() != before) poses.Add(read);
        }
        return new { samples = count, read = poses.Count, poses, summary = PoseSummary(poses),
            note = poses.Count == 0 ? "The renderer didn't answer within 3 seconds." : null };
    }

    // How the readings went together: whether the body idled, the least and most of the breath (inhale, 0 to 1), the breaths a
    // minute, each arm's angle from straight down and elbow bend, each hand's finger curl and the sway (degrees), and how far
    // each placed bone moved across the readings (fractions of the drawing).
    internal static object PoseSummary(IReadOnlyList<System.Text.Json.JsonElement> poses)
    {
        static System.Text.Json.JsonElement? At(System.Text.Json.JsonElement owner, string[] path)
        {
            foreach (var key in path)
            {
                if (owner.ValueKind != System.Text.Json.JsonValueKind.Object || !owner.TryGetProperty(key, out var next)) return null;
                owner = next;
            }
            return owner;
        }
        static double[] Numbers(IEnumerable<System.Text.Json.JsonElement> items, params string[] path) =>
            [.. items.Select(item => At(item, path)).Where(value => value is { ValueKind: System.Text.Json.JsonValueKind.Number })
                .Select(value => value!.Value.GetDouble())];
        static object? Range(double[] values) => values.Length == 0 ? null : new { least = values.Min(), most = values.Max() };
        static double Spread(double[] values) => values.Length == 0 ? 0 : Math.Round(values.Max() - values.Min(), 4);
        var found = poses.Where(pose => At(pose, ["found"]) is { ValueKind: System.Text.Json.JsonValueKind.True }).ToArray();
        object Arm(string side) => new { fromDown = Range(Numbers(found, "arms", side, "fromDown")), elbow = Range(Numbers(found, "arms", side, "elbow")) };
        var bones = found.Select(pose => At(pose, ["bones"])).Where(value => value is { ValueKind: System.Text.Json.JsonValueKind.Object })
            .SelectMany(value => value!.Value.EnumerateObject().Select(property => property.Name)).Distinct().ToArray();
        return new
        {
            found = found.Length,
            idle = found.Length > 0 && found.All(pose => At(pose, ["idle"]) is { ValueKind: System.Text.Json.JsonValueKind.True }),
            inhale = Range(Numbers(found, "breathing", "inhale")),
            perMinute = Range(Numbers(found, "breathing", "perMinute")),
            arms = new { left = Arm("left"), right = Arm("right") },
            curl = new { left = Range(Numbers(found, "curl", "left")), right = Range(Numbers(found, "curl", "right")) },
            sway = Range(Numbers(found, "sway")),
            // The VRMA clips seen playing and how much of the body they had (0 to 1).
            animations = found.Select(pose => At(pose, ["animation", "name"])).Where(value => value is { ValueKind: System.Text.Json.JsonValueKind.String })
                .Select(value => value!.Value.GetString()).Distinct().ToArray(),
            animationWeight = Range(Numbers(found, "animation", "weight")),
            moved = bones.ToDictionary(name => name, name => new { x = Spread(Numbers(found, "bones", name, "x")), y = Spread(Numbers(found, "bones", name, "y")) })
        };
    }

    /// <summary>Gives the showing VRM character a VRMA clip from the file at <paramref name="path"/> (or, without one, plays the clip
    /// it keeps as <paramref name="name"/>) through MoveAvatar's UI Automation value ("animation:{...}"), and waits for the
    /// renderer's answer. With <paramref name="samples"/> it then reads the pose (character_pose) to show the clip playing. It
    /// changes the character, so it needs --allow-ui-effects.</summary>
    internal async Task<object> AnimateCharacterAsync(string? path, string? name, bool play, bool inPlace, int? samples, int? gapMs)
    {
        if (!allowEffects) throw new InvalidOperationException("Playing a clip on the character requires --allow-ui-effects.");
        if (path is null && name is null) throw new ArgumentException("Give path (a .vrma file) or name (a clip the character keeps).");
        if (path is not null && !System.IO.Path.IsPathFullyQualified(path)) throw new ArgumentException("path is the full path of a .vrma file.");
        if (path is not null && !System.IO.File.Exists(path)) throw new ArgumentException("The .vrma file doesn't exist.");
        if (name is not null && !Martlet.Avatar.Hosting.VrmAnimationClip.IsValidName(name))
            throw new ArgumentException($"name is 1 to {Martlet.Avatar.Hosting.VrmAnimationClip.MaximumNameLength} characters without control characters.");
        if (samples is < 1 or > MaximumPoseSamples) throw new ArgumentException($"samples is 1 to {MaximumPoseSamples}.");
        if (gapMs is < 0 or > 5000) throw new ArgumentException("gapMs is 0 to 5000.");
        var element = Find("MoveAvatar");
        if (!element.TryGetCurrentPattern(ValuePattern.Pattern, out var pattern))
            throw new InvalidOperationException("The character overlay can't be reached through UI Automation.");
        var value = (ValuePattern)pattern;
        if (value.Current.IsReadOnly) throw new InvalidOperationException("The character can't play a clip until it has loaded.");
        static System.Text.Json.JsonElement? Played(string text) => string.IsNullOrEmpty(text) ? null :
            System.Text.Json.JsonDocument.Parse(text).RootElement is { ValueKind: System.Text.Json.JsonValueKind.Object } root &&
            root.TryGetProperty("animation", out var animation) ? animation.Clone() : null;
        var before = Played(value.Current.Value)?.GetRawText();
        value.SetValue("animation:" + System.Text.Json.JsonSerializer.Serialize(new { path, name, play, inPlace }));
        var waited = Stopwatch.StartNew();
        System.Text.Json.JsonElement? after;
        while ((after = Played(value.Current.Value))?.GetRawText() == before && waited.Elapsed < TimeSpan.FromSeconds(10)) await Task.Delay(20);
        var played = after is { } read && read.GetRawText() != before ? read : (System.Text.Json.JsonElement?)null;
        var pose = samples is { } count && played is not null ? await PoseCharacterAsync(count, gapMs) : null;
        return new { played, pose, note = played is null ? "The renderer didn't answer within 10 seconds." : null };
    }

    /// <summary>Reads who moves the showing character's mouth, the voice or its emotes, <paramref name="samples"/> times,
    /// <paramref name="gapMs"/> apart, through MoveAvatar's UI Automation value ("mouth"). With <paramref name="levels"/> (0 to
    /// 1, one every <paramref name="stepMs"/>; needs --allow-ui-effects) the mouth first moves as Martlet's loudness lip-sync moves
    /// it, without a sound ("voice:ms;level;..."), and the readings start at once. Returns each reading and a summary.</summary>
    internal async Task<object> MouthCharacterAsync(double[]? levels, int? stepMs, int? samples, int? gapMs)
    {
        var count = samples ?? 1;
        if (count is < 1 or > MaximumMouthSamples) throw new ArgumentException($"samples is 1 to {MaximumMouthSamples}.");
        if (gapMs is < 0 or > 5000) throw new ArgumentException("gapMs is 0 to 5000.");
        var step = stepMs ?? 50;
        if (levels is not null)
        {
            if (!allowEffects) throw new InvalidOperationException("Moving the character's mouth requires --allow-ui-effects.");
            if (levels.Length is < 1 or > MaximumVoiceLevels) throw new ArgumentException($"Give 1 to {MaximumVoiceLevels} levels.");
            if (levels.Any(level => level is not (>= 0 and <= 1))) throw new ArgumentException("Each level is a number from 0 to 1.");
            if (step is < 10 or > 1000) throw new ArgumentException("stepMs is 10 to 1000.");
        }
        var element = Find("MoveAvatar");
        if (!element.TryGetCurrentPattern(ValuePattern.Pattern, out var pattern))
            throw new InvalidOperationException("The character overlay can't be read through UI Automation.");
        var value = (ValuePattern)pattern;
        if (value.Current.IsReadOnly) throw new InvalidOperationException("The character's mouth can't be read until it has loaded.");
        static System.Text.Json.JsonElement? Mouth(string text) => string.IsNullOrEmpty(text) ? null :
            System.Text.Json.JsonDocument.Parse(text).RootElement is { ValueKind: System.Text.Json.JsonValueKind.Object } root &&
            root.TryGetProperty("mouth", out var mouth) ? mouth.Clone() : null;
        if (levels is not null)
            value.SetValue("voice:" + string.Join(";", new[] { step.ToString(System.Globalization.CultureInfo.InvariantCulture) }
                .Concat(levels.Select(level => level.ToString("R", System.Globalization.CultureInfo.InvariantCulture)))));
        var mouths = new List<System.Text.Json.JsonElement>();
        for (var i = 0; i < count; i++)
        {
            if (i > 0) await Task.Delay(gapMs ?? 250);
            var before = Mouth(value.Current.Value)?.GetRawText();
            value.SetValue("mouth");
            var waited = Stopwatch.StartNew();
            System.Text.Json.JsonElement? after;
            while ((after = Mouth(value.Current.Value))?.GetRawText() == before && waited.Elapsed < TimeSpan.FromSeconds(3)) await Task.Delay(20);
            if (after is { } read && read.GetRawText() != before) mouths.Add(read);
        }
        return new { played = levels?.Length ?? 0, stepMs = levels is null ? (int?)null : step, samples = count, read = mouths.Count, mouths,
            summary = MouthSummary(mouths), note = mouths.Count == 0 ? "The renderer didn't answer within 3 seconds." : null };
    }

    // How the readings went together: how many found the mouth, the share in which the voice had it (speaking), the least and
    // most of how much the voice had it, its loudness, the emotes' opening, the mouth's opening and a VRM's block, and the last
    // reading (where the mouth ended up).
    internal static object MouthSummary(IReadOnlyList<System.Text.Json.JsonElement> mouths)
    {
        static bool Is(System.Text.Json.JsonElement owner, string key, System.Text.Json.JsonValueKind kind) =>
            owner.ValueKind == System.Text.Json.JsonValueKind.Object && owner.TryGetProperty(key, out var value) && value.ValueKind == kind;
        static object? Range(IEnumerable<System.Text.Json.JsonElement> items, string key)
        {
            double[] values = [.. items.Where(item => Is(item, key, System.Text.Json.JsonValueKind.Number)).Select(item => item.GetProperty(key).GetDouble())];
            return values.Length == 0 ? null : new { least = values.Min(), most = values.Max() };
        }
        var found = mouths.Where(mouth => Is(mouth, "found", System.Text.Json.JsonValueKind.True)).ToArray();
        return new
        {
            found = found.Length,
            speaking = found.Length == 0 ? 0 : Math.Round((double)found.Count(mouth => Is(mouth, "speaking", System.Text.Json.JsonValueKind.True)) / found.Length, 2),
            voice = Range(found, "voice"), level = Range(found, "level"), emote = Range(found, "emote"), open = Range(found, "open"),
            blocked = Range(found, "blocked"),
            last = found.Length == 0 ? (System.Text.Json.JsonElement?)null : found[^1]
        };
    }
}
