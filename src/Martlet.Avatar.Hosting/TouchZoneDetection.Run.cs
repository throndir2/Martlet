namespace Martlet.Avatar.Hosting;

/// <summary>What a check answered: the boxes it called right, the corrected ones (fractions of the picture checked), the zones
/// it said aren't there, the zones it added, and whether it said every box is right.</summary>
public sealed record ZoneVerdict(IReadOnlySet<int> Right, IReadOnlyDictionary<int, TouchZoneBox> Corrected, IReadOnlySet<int> Gone,
    IReadOnlyDictionary<string, TouchZoneBox> Added, bool Done);

public static partial class TouchZoneDetection
{
    /// <summary>Finds the zones of the character in <paramref name="snapshot"/> (the renderer's picture, transparent around the
    /// character) with <paramref name="ask"/>, the vision model. It looks only for the zones <paramref name="options"/> names
    /// (<see cref="ZoneDetectionOptions.Zones"/> and <see cref="ZoneDetectionOptions.Required"/>). Every request's picture is
    /// composed here; <paramref name="progress"/>
    /// hears each step. Stops at the first request that fails (no vision model, or the model or the computer it runs on stopped
    /// answering), with the zones found until then and why; an answer with nothing usable in it is not a failure.</summary>
    public static async Task<ZoneDetectionResult> RunAsync(ZonePixels snapshot, ZoneHints? hints,
        Func<ZoneAsk, CancellationToken, Task<(string? Answer, string? Failure)>> ask, Action<ZoneDetectionProgress>? progress,
        CancellationToken token, ZoneDetectionOptions? options = null)
    {
        options ??= new();
        // The zones it looks for, the ones it must end with among them.
        var wanted = options.Zones.Concat(options.Required).ToHashSet(StringComparer.Ordinal);
        var extras = Extras.Where(wanted.Contains).ToArray();
        var whole = new TouchZoneBox(0, 0, 1, 1);
        var figure = TouchZonePictures.OpaqueBounds(snapshot, new(0, 0, snapshot.Width, snapshot.Height))?.Fraction(snapshot.Width, snapshot.Height) ?? whole;
        var backdrop = TouchZonePictures.Backdrop(snapshot);
        // Live2D characters face the viewer; a VRM's own shoulders tell (null for a side view).
        var faces = hints is { Bones.Count: > 0 } ? hints.FacesViewer : true;
        var zones = new Dictionary<string, TouchZoneBox>(StringComparer.Ordinal);
        // The names the vision model gave the zones special to the character.
        var labels = new Dictionary<string, string>(StringComparer.Ordinal);
        var steps = new List<string>();
        var requests = 0;
        string? failure = null;

        // A failed request stops the detection: the next ones would most likely fail the same way, each after its own wait.
        async Task<string?> Ask(ZoneAsk zoneAsk)
        {
            token.ThrowIfCancellationRequested();
            requests++;
            var (answer, why) = await ask(zoneAsk, token).ConfigureAwait(false);
            if (answer is null && why is not null)
            {
                failure = why;
                steps.Add($"{zoneAsk.Step}: the request failed ({why}), so finding zones stopped");
            }
            return answer;
        }
        void Report(string text) => progress?.Invoke(new(text, Zones(zones, labels), requests));
        // What a zone is, as the vision model is told: a zone special to the character by the name it gave it.
        string What(string id) => labels.TryGetValue(id, out var label) ? label.ToLowerInvariant() : Describe(id);
        ZonePixels Compose(TouchZoneBox region, IReadOnlyList<MarkedBox>? marks = null) =>
            TouchZonePictures.Compose(snapshot, region, options.Edge, options.MaximumZoom, backdrop, grid: true, marks);

        // 1. The whole character: where its parts are.
        Report("Step 1: asking the Thinking model where the head, body and legs are, on the whole character with a grid...");
        var picture = Compose(whole);
        var answer = await Ask(new(ZoneAskKind.Parts, "parts", PartsInstructions, PartsText(hints, extras), picture, whole, PartsFor(extras), []))
            .ConfigureAwait(false);
        if (failure is not null) return new(null, failure, requests, steps);
        var parts = ReadBoxes(answer, picture.Width, picture.Height, PartId);
        foreach (var extra in extras)
            if (parts.TryGetValue(extra, out var box)) zones[extra] = box;
        // The model's own named parts give a close-up's window when they can: it then holds all of that part, whatever the vision
        // model saw. Each close-up asks only for the zones the detection looks for.
        var regions = Regions.Select(r =>
        {
            var found = parts.TryGetValue(r.Id, out var b) && Sensible(b, figure);
            var named = NamedRegion(r.Id, hints);
            return (Region: r with { Zones = [.. r.Zones.Where(wanted.Contains)] }, Found: found || named is not null,
                Box: named ?? (found ? b! : Fallback(r.Id, figure, hints)), Named: named is not null);
        }).ToArray();
        var tidy = Tidy(zones, snapshot, faces);
        steps.Add($"parts: found {string.Join(", ", parts.Keys)}" + (parts.Count == 0 ? "nothing" : "") +
            (regions.Any(r => !r.Found) ? $"; guessed {string.Join(", ", regions.Where(r => !r.Found).Select(r => r.Region.Id))} from the character's outline" : "") +
            (regions.Any(r => r.Named) ? $"; took {string.Join(", ", regions.Where(r => r.Named).Select(r => r.Region.Id))} from the model's own named parts" : "") +
            Notes(tidy));

        // 2. Each part close up: its zones, then the model checks them. A part with no zones to look for isn't asked about.
        var number = 1;
        foreach (var (region, _, box, _) in regions)
        {
            if (region.Zones.Count == 0) continue;
            number++;
            var crop = Crop(box, snapshot);
            Report($"Step {number}: finding the zones of {region.What} in a close-up...");
            picture = Compose(crop);
            answer = await Ask(new(ZoneAskKind.Zones, region.Id, ZonesInstructions, ZonesText(region, hints, crop), picture, crop, region.Zones, []))
                .ConfigureAwait(false);
            if (failure is not null) break;
            var found = ReadBoxes(answer, picture.Width, picture.Height, CharacterTouchZones.Normalize).Where(z => region.Zones.Contains(z.Key)).ToArray();
            foreach (var (id, zone) in found) zones[id] = zone.Within(crop);
            tidy = Tidy(zones, snapshot, faces);
            steps.Add($"{region.Id}: " + (answer is null ? "no answer" : $"{found.Length} zones") + Notes(tidy));
            await CheckAsync(region.Id, region.What, region.Zones, crop).ConfigureAwait(false);
            if (failure is not null) break;
        }
        // 3. Zones that must be found (the ones the owner added, and the intimate ones with Include intimate zones on) that the
        // close-ups missed, or a check removed: asked for once more, on the whole character.
        if (failure is null && options.Required.Where(id => !zones.ContainsKey(id)).ToArray() is { Length: > 0 } missing)
        {
            number++;
            Report($"Step {number}: asking again for {missing.Length} zone{(missing.Length == 1 ? "" : "s")} the close-ups missed, on the whole character...");
            picture = Compose(whole);
            answer = await Ask(new(ZoneAskKind.Zones, MissingStep, MissingInstructions, MissingText(missing, hints), picture, whole, missing, []))
                .ConfigureAwait(false);
            if (failure is null)
            {
                var again = ReadBoxes(answer, picture.Width, picture.Height, CharacterTouchZones.Normalize).Where(z => missing.Contains(z.Key)).ToArray();
                foreach (var (id, zone) in again) zones[id] = zone;
                tidy = Tidy(zones, snapshot, faces);
                steps.Add($"{MissingStep}: asked again for {string.Join(", ", missing)}; " +
                    (again.Length == 0 ? answer is null ? "no answer" : "it found none of them" : "it found " + string.Join(", ", again.Select(z => z.Key))) +
                    Notes(tidy));
            }
        }
        // 4. What is special about this character (animal ears, a tail, wings, a hat, a bow, something it holds...), on the whole
        // character: each becomes a zone, named as the vision model sees it, with the tail, wings or animal ears the model's own
        // part names place.
        var special = new List<string>();
        if (failure is null && options.MaximumSpecial > 0)
        {
            number++;
            Report($"Step {number}: asking what is special about this character ({SpecialExamples}...), on the whole character...");
            picture = Compose(whole);
            answer = await Ask(new(ZoneAskKind.Special, SpecialStep, SpecialInstructions(options.MaximumSpecial), SpecialText(hints, options.SpecialBefore),
                picture, whole, [.. options.SpecialBefore.Select(b => b.Id)], [])).ConfigureAwait(false);
            if (failure is null)
            {
                var said = ReadSpecial(answer, picture.Width, picture.Height, zones.Keys, options.MaximumSpecial);
                foreach (var (id, label, box) in said)
                {
                    zones[id] = box;
                    if (label is not null) labels[id] = label;
                    special.Add(id);
                }
                var named = NamedSpecialPlaces(hints, zones);
                foreach (var (id, box, _) in named)
                {
                    zones[id] = box;
                    special.Add(id);
                }
                tidy = Tidy(zones, snapshot, faces);
                steps.Add($"{SpecialStep}: " + (said.Count == 0 ? answer is null ? "no answer" : "nothing special found"
                        : "found " + string.Join(", ", said.Select(s => s.Label is null ? s.Id : $"{s.Id} ({s.Label})"))) +
                    (named.Count == 0 ? "" : "; added " + string.Join(", ", named.Select(n => $"{n.Id} from the model's own {n.What}"))) + Notes(tidy));
            }
        }
        // 5. The zones special to it, and a tail, wings or held item the owner added, checked on the whole character.
        var onWhole = extras.Concat(special).Distinct(StringComparer.Ordinal).ToArray();
        if (failure is null && onWhole.Any(zones.ContainsKey)) await CheckAsync(SpecialStep, "the whole character", onWhole, whole).ConfigureAwait(false);

        var finished = Finish(zones, snapshot, hints);
        if (finished.Length > 0) steps.Add("finally: " + string.Join("; ", finished));
        // The zones that must be found and still aren't are worked out from the zones around them.
        if (failure is null && Derive(zones, options.Required, snapshot, faces, regions.ToDictionary(r => r.Region.Id, r => (r.Box, r.Found)), figure, hints)
            is { Count: > 0 } derived)
            steps.Add("worked out " + string.Join("; ", derived));
        var result = Zones(zones, labels);
        Report(failure is null ? $"Done: {result.Count} zones after {requests} requests."
            : $"Stopped: request {requests} failed ({failure}); {result.Count} zones found until then.");
        return new(result.Count == 0 ? null : result, failure, requests, steps);

        async Task CheckAsync(string step, string what, IReadOnlyList<string> ids, TouchZoneBox crop)
        {
            for (var round = 1; round <= options.Checks; round++)
            {
                var present = ids.Where(zones.ContainsKey).ToArray();
                if (present.Length == 0) return;
                var marks = present.Select((id, i) => new ZoneMark(i + 1, id, Clip(zones[id].Relative(crop)) ?? new(0, 0, 0.01, 0.01))).ToArray();
                var problems = Problems(marks, zones, snapshot, faces, hints, crop);
                Report($"Checking the zones of {what}, round {round} of {options.Checks}" +
                    (problems.Count > 0 ? $" ({problems.Count} problem{(problems.Count == 1 ? "" : "s")} measured)" : "") + "...");
                var drawn = marks.Select(m => new MarkedBox(m.Number, m.Box, TouchZonePictures.MarkColors[(m.Number - 1) % TouchZonePictures.MarkColors.Count])).ToArray();
                var checking = Compose(crop, drawn);
                var reply = await Ask(new(ZoneAskKind.Check, $"{step} check {round}", CheckInstructions,
                    CheckText(what, marks, ids.Where(id => !zones.ContainsKey(id)), problems, hints, crop, What), checking, crop, ids, marks)).ConfigureAwait(false);
                if (failure is not null) return;
                var verdict = ReadCheck(reply, marks, ids, checking.Width, checking.Height);
                if (verdict is null)
                {
                    steps.Add($"{step} check {round}: no answer Martlet could read");
                    return;
                }
                var changed = new List<string>();
                foreach (var (n, corrected) in verdict.Corrected)
                {
                    var mark = marks.First(m => m.Number == n);
                    // A correction that fits back onto the same pixels changes nothing.
                    var fitted = Fit(mark.Id, corrected.Within(crop), snapshot);
                    if (Moved(mark.Box, corrected) <= Noise || Moved(zones[mark.Id].Relative(crop), fitted.Relative(crop)) <= Noise) continue;
                    zones[mark.Id] = fitted;
                    changed.Add("moved " + mark.Id);
                }
                foreach (var n in verdict.Gone)
                {
                    var mark = marks.First(m => m.Number == n);
                    zones.Remove(mark.Id);
                    changed.Add("removed " + mark.Id);
                }
                foreach (var (id, added) in verdict.Added)
                    if (zones.TryAdd(id, added.Within(crop))) changed.Add("added " + id);
                tidy = Tidy(zones, snapshot, faces);
                steps.Add($"{step} check {round}: {verdict.Right.Count} of {marks.Length} right" +
                    (changed.Count > 0 ? "; " + string.Join(", ", changed) : "; nothing to change") + Notes(tidy));
                if (changed.Count == 0) return;
            }
        }
    }

    private static string Notes(IReadOnlyList<string> notes) => notes.Count == 0 ? "" : "; " + string.Join(", ", notes);

    private static List<CharacterTouchZone> Zones(Dictionary<string, TouchZoneBox> zones, IReadOnlyDictionary<string, string>? labels = null) =>
        [.. zones.OrderBy(z => CharacterTouchZones.Order(z.Key)).Select(z => new CharacterTouchZone
        {
            Id = z.Key, Label = labels?.GetValueOrDefault(z.Key), Box = z.Value.Clamped(), Enabled = true
        })];

    /// <summary>The largest distance any edge moved from <paramref name="from"/> to <paramref name="to"/>.</summary>
    public static double Moved(TouchZoneBox from, TouchZoneBox to) => new[]
    {
        Math.Abs(from.X - to.X), Math.Abs(from.Y - to.Y), Math.Abs(from.X + from.Width - to.X - to.Width), Math.Abs(from.Y + from.Height - to.Y - to.Height)
    }.Max();

    // ---------- reading answers ----------

    /// <summary>The boxes of a parts or zones answer by ID (as <paramref name="normalize"/> names them), as fractions of the
    /// <paramref name="width"/> by <paramref name="height"/> picture it was about.</summary>
    public static Dictionary<string, TouchZoneBox> ReadBoxes(string? answer, int width, int height, Func<string?, string?> normalize)
    {
        var raw = new List<(string, double[])>();
        foreach (var entry in ZoneAnswers.Entries(answer))
            if (normalize(ZoneAnswers.Name(entry)) is { } id && ZoneAnswers.RawBox(entry) is { } box) raw.Add((id, box));
        return ZoneAnswers.Scale(raw, width, height).ToDictionary(z => z.Key, z => z.Box, StringComparer.Ordinal);
    }

    /// <summary>A part's ID as the parts step names them, or null.</summary>
    public static string? PartId(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        var slug = string.Join("_", name.Trim().ToLowerInvariant().Split([' ', '-', '/', '.', ',', '&'], StringSplitOptions.RemoveEmptyEntries));
        return slug switch
        {
            "head" or "head_and_hair" or "head_hair" => "head",
            "upper_body" or "upperbody" or "upper" or "torso" or "body" or "upper_body_and_arms" => "upper_body",
            "lower_body" or "lowerbody" or "lower" or "legs" or "lower_body_and_legs" => "lower_body",
            "tail" or "tails" => "tail",
            "wings" or "wing" => "wings",
            "held_item" or "held_object" or "item" or "weapon" or "prop" => "held_item",
            _ => null
        };
    }

    /// <summary>A check's answer about <paramref name="marks"/> (the boxes drawn) on a <paramref name="width"/> by
    /// <paramref name="height"/> picture; zones added must be among <paramref name="ids"/>. Null when nothing could be read.</summary>
    public static ZoneVerdict? ReadCheck(string? answer, IReadOnlyList<ZoneMark> marks, IReadOnlyCollection<string> ids, int width, int height)
    {
        var entries = ZoneAnswers.Entries(answer);
        if (entries.Count == 0) return null;
        var right = new HashSet<int>();
        var gone = new HashSet<int>();
        var raw = new List<(string, double[])>();
        foreach (var entry in entries)
        {
            // A zone of its own (special to the character) goes by its own ID.
            var name = ZoneAnswers.Name(entry);
            var own = CharacterTouchZones.Slug(name);
            var id = own is not null && (ids.Contains(own) || marks.Any(m => m.Id == own)) ? own : CharacterTouchZones.Normalize(name);
            var mark = ZoneAnswers.Int(entry, "n", "number", "#") is { } n ? marks.FirstOrDefault(m => m.Number == n) : null;
            mark ??= id is null ? null : marks.FirstOrDefault(m => m.Id == id);
            var ok = ZoneAnswers.Bool(entry, "ok", "correct", "right", "accurate");
            var visible = ZoneAnswers.Bool(entry, "visible", "present", "exists");
            var remove = ZoneAnswers.Bool(entry, "remove", "delete");
            var box = ZoneAnswers.RawBox(entry);
            if (mark is not null)
            {
                if (visible == false || remove == true) gone.Add(mark.Number);
                else if (ok == true) right.Add(mark.Number);
                else if (box is not null) raw.Add(("#" + mark.Number.ToString(System.Globalization.CultureInfo.InvariantCulture), box));
            }
            else if (id is not null && ids.Contains(id) && box is not null && visible != false && remove != true) raw.Add((id, box));
        }
        var corrected = new Dictionary<int, TouchZoneBox>();
        var added = new Dictionary<string, TouchZoneBox>(StringComparer.Ordinal);
        foreach (var (key, box) in ZoneAnswers.Scale(raw, width, height))
            if (key.StartsWith('#')) corrected[int.Parse(key[1..], System.Globalization.CultureInfo.InvariantCulture)] = box;
            else added[key] = box;
        var done = ZoneAnswers.RootBool(answer, "done", "all_ok", "finished") ?? (corrected.Count == 0 && gone.Count == 0 && added.Count == 0);
        return new(right, corrected, gone, added, done);
    }
}
