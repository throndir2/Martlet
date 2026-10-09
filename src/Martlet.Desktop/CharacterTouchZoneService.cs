using System.Globalization;
using System.IO;
using Martlet.Avatar.Hosting;
using Martlet.Core.Contracts;
using Martlet.Providers;

namespace Martlet.Desktop;

/// <summary>The touch zones of the character this PC shows (Companion › Touch › Touch zones): a first guess placed with no AI
/// when the page opens on a model with none (<see cref="EstimateAsync"/>), then found per model by the Thinking model (when it
/// can see) step by step in a snapshot of the character (<see cref="TouchZoneDetection"/>), bound to the model's drawables or
/// bones, saved per model in character-touch-zones.json with the pictures the model saw, and what a touch on each does.</summary>
internal sealed class CharacterTouchZoneService(string? dataDirectory)
{
    /// <summary>A file whose zones (JSON as a vision model answers about the whole snapshot) stand in for the vision model: every
    /// request of a detection is answered from them (FIXTURE - NOT AI), so MCP verification runs the real snapshot, pictures,
    /// probe, binding and saving without a vision request.</summary>
    internal const string FixtureVariable = "MARTLET_TOUCH_ZONES_FIXTURE";

    /// <summary>With <see cref="FixtureVariable"/>, the request (1 for the first) at which the FIXTURE - NOT AI stand-in fails
    /// instead of answering, as when the vision model or the computer it runs on stops answering part way.</summary>
    internal const string FixtureFailAtVariable = "MARTLET_TOUCH_ZONES_FIXTURE_FAIL_AT";

    private readonly Dictionary<string, long> rested = new(StringComparer.Ordinal);
    private CharacterTouchZoneSettings? current;
    private TouchZoneSent? sent;
    private string? modelId;
    private string? detection;
    private string? lastMatch, noticed, noticedLast;
    private bool busy, estimating;

    /// <summary>Raised (on any thread) when the zones, the detection status or the last touch change.</summary>
    internal event Action? Changed;

    internal string? DataDirectory => dataDirectory;
    internal string? ModelId => Volatile.Read(ref modelId);
    /// <summary>The loaded model's zones, or null before any were found or saved.</summary>
    internal CharacterTouchZoneSettings? Current => Volatile.Read(ref current);
    /// <summary>How finding zones went (or is going), or null before it was asked.</summary>
    internal string? Detection => Volatile.Read(ref detection);
    /// <summary>Which zone the last touch landed in and what it did.</summary>
    internal string? LastMatch => Volatile.Read(ref lastMatch);
    /// <summary>What Martlet noticed that waits for a reply, and when a touch reply would start (null before anything).</summary>
    internal string? Noticed => Volatile.Read(ref noticed);
    /// <summary>Which reply took the last touches Martlet noticed and what it was told.</summary>
    internal string? NoticedLast => Volatile.Read(ref noticedLast);
    internal bool Busy => Volatile.Read(ref busy);
    /// <summary>Whether the first guess at the zones is being placed now (<see cref="EstimateAsync"/>; <see cref="Busy"/> too).</summary>
    internal bool Estimating => Volatile.Read(ref estimating);

    /// <summary>Whether the loaded model has no zones and no picture yet, so the Touch zones page places a first guess
    /// (<see cref="EstimateAsync"/>).</summary>
    internal bool NeedsFirstGuess => dataDirectory is not null && ModelId is not null && !Busy && SnapshotPath is null && Current is not { Zones.Count: > 0 };

    /// <summary>The snapshot the loaded model's zones were found in (a PNG), or null.</summary>
    internal string? SnapshotPath => dataDirectory is not null && ModelId is { } id && CharacterTouchZones.SnapshotPath(dataDirectory, id) is var path &&
        File.Exists(path) ? path : null;

    /// <summary>What the loaded model's last detection sent to the vision model, or null before one ran.</summary>
    internal TouchZoneSent? Sent => Volatile.Read(ref sent);

    /// <summary>The folder with the pictures the vision model saw in the loaded model's last detection, or null.</summary>
    internal string? SentFolder => dataDirectory is not null && ModelId is { } id && CharacterTouchZones.SentFolder(dataDirectory, id) is var folder &&
        Directory.Exists(folder) ? folder : null;

    /// <summary>The first picture the vision model saw (the whole character with its grid) when that detection saved the zones
    /// shown (its snapshot is the one under the boxes), or null.</summary>
    internal string? SentWholePicture => SentFolder is { } folder && Sent is { Saved: true } sent &&
        sent.Pictures.FirstOrDefault(p => p.Kind == nameof(ZoneAskKind.Parts)) is { } first &&
        Path.Combine(folder, first.File) is var path && File.Exists(path) ? path : null;

    /// <summary>Loads the zones of the model with <paramref name="id"/> unless they are already loaded.</summary>
    internal void Follow(string? id)
    {
        if (id == ModelId) return;
        Volatile.Write(ref current, id is null || dataDirectory is null ? null : CharacterTouchZones.Load(dataDirectory, id));
        Volatile.Write(ref sent, id is null || dataDirectory is null ? null : CharacterTouchZones.LoadSent(dataDirectory, id));
        Volatile.Write(ref modelId, id);
        Volatile.Write(ref detection, null);
        lock (rested) rested.Clear();
        lock (streaks) streaks.Clear();
        Changed?.Invoke();
    }

    /// <summary>Saves the loaded model's zones; returns why they couldn't be saved, or null.</summary>
    internal async Task<string?> SaveAsync(CharacterTouchZoneSettings settings, CancellationToken token)
    {
        if (dataDirectory is null) return "Martlet's data folder isn't available.";
        try
        {
            var saved = await CharacterTouchZones.SaveAsync(dataDirectory, settings, DateTimeOffset.Now, token);
            if (saved.ModelId == ModelId) Volatile.Write(ref current, saved);
        }
        catch (Exception error) when (error is ContractException or IOException or UnauthorizedAccessException) { return error.Message; }
        Changed?.Invoke();
        return null;
    }

    /// <summary>What the Touch zones page says while the first guess is placed.</summary>
    internal const string FirstGuessDrawing = "Drawing the character to place its first zones (no AI, nothing is sent)...";

    /// <summary>A first guess at the loaded model's zones with no vision model, while it has none and no picture: takes a picture
    /// of the character (<paramref name="character"/> gives its profile, or null when it isn't the loaded model's; drawn off
    /// screen in its rest pose, as Detect zones does: <see cref="AvatarController.ZoneSnapshotAsync"/>) and places the zones Detect
    /// zones looks for from the model's own named parts, its skeleton and the body's proportions
    /// (<see cref="TouchZoneDetection.Estimate"/>). They are saved with the picture, bound to the model's drawables or bones and
    /// marked <see cref="CharacterTouchZoneSettings.ByEstimate"/>, so the Touch zones page shows the character and its zones at
    /// once; Detect zones then has the Thinking model find them. Nothing is sent. <see cref="Busy"/> and <see cref="Estimating"/>
    /// are set, and <see cref="Detection"/> says so, before this returns its task. Returns what happened.</summary>
    internal async Task<string> EstimateAsync(AvatarController avatar, Func<Task<AvatarProfile?>> character, CancellationToken token)
    {
        if (!NeedsFirstGuess || ModelId is not { } id || dataDirectory is null) return Detection ?? "";
        Volatile.Write(ref busy, true);
        Volatile.Write(ref estimating, true);
        Report(FirstGuessDrawing);
        const string Again = " Open this page again to try once more, or press Detect zones.";
        try
        {
            if (await character() is not { } profile)
            {
                Volatile.Write(ref detection, null);
                return "";
            }
            if (await avatar.ZoneSnapshotAsync(profile, token) is not { } shot)
                return Report("Martlet couldn't draw the character for its first zones." + Again);
            ZonePixels snapshot;
            try { snapshot = await Task.Run(() => TouchZoneImages.Decode(shot.Png), token); }
            catch (Exception error) when (error is NotSupportedException or FileFormatException or ArgumentException or InvalidOperationException or IOException)
            {
                return Report("The character's picture couldn't be used for its first zones." + Again);
            }
            var crop = new TouchZoneBox(shot.Picture.CropLeft, shot.Picture.CropTop, shot.Picture.CropWidth, shot.Picture.CropHeight);
            var hints = TouchZoneDetection.Hints(shot.Probe, crop);
            var before = Current;
            var result = await Task.Run(() => TouchZoneDetection.Estimate(snapshot, hints, TouchZoneDetection.For(before)), token);
            foreach (var step in result.Steps) ErrorLog.Info($"First touch zones: {step}.");
            // Another model loaded, or zones made, while the picture was taken: those stay as they are.
            if (ModelId != id || Current is { Zones.Count: > 0 }) return Detection ?? "";
            if (result.Zones is not { Count: > 0 } zones)
                return Report("Martlet couldn't place any zones on the character's picture. Press Detect zones to have your Thinking model find them.");
            try
            {
                await CharacterTouchZones.SaveSnapshotAsync(dataDirectory, id, shot.Png, token);
                await CharacterTouchZones.SaveSnapshotProbeAsync(dataDirectory, id, shot.Probe is { } probe ? new(crop, probe) : null, token);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                return Report($"Martlet couldn't keep the character's picture for its first zones: {error.Message}");
            }
            if (await SaveAsync(CharacterTouchZones.Estimated(before, id, zones, crop, shot.Probe, DateTimeOffset.Now), token) is { } why)
                return Report("The first zones were placed but couldn't be saved: " + why);
            var bound = Current?.Zones.Count(z => z.Drawables.Count > 0 || z.Bones.Count > 0) ?? 0;
            ErrorLog.Info($"Placed a first guess at {zones.Count} touch zones with no AI ({bound} bound to the model's parts{Following(Current)}).");
            return Report($"First zones: Martlet placed {Zones(zones.Count)} at {DateTime.Now:t} from the character's own parts and shape, with no AI " +
                "and nothing sent." + FollowingText(Current) + " Move a box into place, or press Detect zones and your Thinking model finds them.");
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { return Detection ?? ""; }
        finally
        {
            Volatile.Write(ref estimating, false);
            Volatile.Write(ref busy, false);
            Changed?.Invoke();
        }
    }

    /// <summary>Takes a picture of <paramref name="profile"/>'s character (drawn off screen in its rest pose, so it needn't show:
    /// <see cref="AvatarController.ZoneSnapshotAsync"/>) and finds its zones step by step with the Thinking model (through
    /// <paramref name="ask"/>, one picture each: <see cref="TouchZoneDetection"/>), saving the zones as they are found, bound to
    /// the model's drawables or bones, and keeping every picture sent (<see cref="Sent"/>). It looks for the default zones and the
    /// ones the owner added (<see cref="TouchZoneDetection.For"/>), and asks what is special about the character (animal ears, a
    /// tail, a hat, a bow...), each a zone named as the model sees it; a zone the owner added that it can't place stays where it was.
    /// Over a first guess (<see cref="EstimateAsync"/>), the first guess's zones it hasn't found yet stay on the picture until it
    /// is done, and then its zones replace them. When a request fails part way, finding
    /// zones stops there: the zones from before (and their picture) come back, or, with none before, the zones found until then
    /// are kept. Returns what happened.</summary>
    internal async Task<string> DetectAsync(AvatarController avatar, AvatarProfile profile,
        Func<string, string, string, BoundedImage?, CancellationToken, Task<(string? Answer, string? Failure)>> ask, CancellationToken token)
    {
        if (Estimating) return Detection ?? "";
        if (ModelId is not { } id) return Report("Martlet is still reading the character. Try again in a moment.");
        Volatile.Write(ref busy, true);
        Report("Taking a picture of the character...");
        var fixture = Environment.GetEnvironmentVariable(FixtureVariable) is { Length: > 0 } file ? file : null;
        var failAt = fixture is not null && int.TryParse(Environment.GetEnvironmentVariable(FixtureFailAtVariable), NumberStyles.Integer,
            CultureInfo.InvariantCulture, out var number) && number > 0 ? number : 0;
        var tag = fixture is null ? "" : "FIXTURE - NOT AI: ";
        var pictures = new List<TouchZoneSentPicture>();
        var steps = new List<string>();
        var requests = 0;
        // Whether this detection saved zones (and so its snapshot), so its pictures belong to the boxes shown.
        var pictureKept = false;
        try
        {
            if (await avatar.ZoneSnapshotAsync(profile, token) is not { } shot) return Report("Martlet couldn't take a picture of the character. Try again.");
            ZonePixels snapshot;
            try { snapshot = await Task.Run(() => TouchZoneImages.Decode(shot.Png), token); }
            catch (Exception error) when (error is NotSupportedException or FileFormatException or ArgumentException or InvalidOperationException or IOException)
            {
                return Report("The character's picture couldn't be used. Try again.");
            }
            // The fixture's zones stand in for the vision model: every request is answered from them (FIXTURE - NOT AI).
            var truth = fixture is null ? null
                : CharacterTouchZones.Parse(File.Exists(fixture) ? await File.ReadAllTextAsync(fixture, token) : null, snapshot.Width, snapshot.Height,
                    special: true);
            var crop = new TouchZoneBox(shot.Picture.CropLeft, shot.Picture.CropTop, shot.Picture.CropWidth, shot.Picture.CropHeight);
            var hints = TouchZoneDetection.Hints(shot.Probe, crop);
            var before = Current;
            // The picture the zones from before were found in, and its probe: they go back with them when this detection fails part way.
            var earlierPicture = before is { Zones.Count: > 0 } && SnapshotPath is { } earlierPath ? await ReadAsync(earlierPath, token) : null;
            var earlierProbe = earlierPicture is not null && dataDirectory is not null ? CharacterTouchZones.LoadSnapshotProbe(dataDirectory, id) : null;
            var folder = dataDirectory is null ? null : CharacterTouchZones.ClearSent(dataDirectory, id);
            // What the model told of its parts goes with the pictures, so MCP can replay this detection on the same picture.
            if (folder is not null && shot.Probe is { } probe)
            {
                try { await CharacterTouchZones.SaveProbeAsync(dataDirectory!, id, new(crop, probe), token); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException) { ErrorLog.Warn($"Couldn't keep where the character's parts are: {error.Message}"); }
            }
            IReadOnlyList<CharacterTouchZone>? latest = null, shown = null;
            ErrorLog.Info($"Finding touch zones: a {snapshot.Width}x{snapshot.Height} picture of the character in its rest pose, drawn off screen " +
                $"({shot.Png.Length / 1024} KB), " +
                (shot.Picture.Zoom < 1 ? FormattableString.Invariant($"zoomed out to {shot.Picture.Zoom:0.##}x to show the parts drawn past the model's own canvas, ") : "") +
                (hints is null ? "no probe" : $"{hints.Bones.Count} bones and {hints.NamedParts} named parts from the model" +
                    (hints.ModelParts > 0 ? $" ({hints.NamedModelParts} of its {hints.ModelParts} parts named in its DisplayInfo file" +
                        (hints.Named ? ", so the close-ups hold their parts and boxes that miss their part move onto it" : "") + ")" : "")) + ".");

            async Task<(string? Answer, string? Failure)> AskAsync(ZoneAsk zoneAsk, CancellationToken cancel)
            {
                // The zones found so far show on the picture before the next request (with a first guess's others, not found yet).
                if (latest is { Count: > 0 } found && !ReferenceEquals(found, shown))
                {
                    shown = found;
                    await KeepAsync(id, before, found, pictureKept ? null : shot.Png, crop, shot.Probe, cancel, partial: true);
                    pictureKept = true;
                }
                var image = TouchZoneImages.Encode(zoneAsk.Picture);
                var name = $"{pictures.Count + 1:00}-{zoneAsk.Step.Replace(' ', '-')}.{(image.MediaType == ImageMediaType.Png ? "png" : "jpg")}";
                if (folder is not null) await File.WriteAllBytesAsync(Path.Combine(folder, name), image.Content.ToArray(), cancel);
                pictures.Add(new(name, zoneAsk.Step, zoneAsk.Kind.ToString(), image.Width, image.Height, image.ByteCount, image.MimeType));
                requests++;
                ErrorLog.Info($"Finding touch zones ({zoneAsk.Step}): {(truth is null ? "sending" : "FIXTURE - NOT AI, not sending")} a {image.Width}x{image.Height} " +
                    $"{image.MimeType} picture ({image.ByteCount / 1024} KB) with a grid{(zoneAsk.Marks.Count > 0 ? $" and {zoneAsk.Marks.Count} numbered boxes" : "")}.");
                if (truth is not null)
                    return requests == failAt ? (null, "a simulated failure") : (TouchZoneDetection.Oracle(zoneAsk, truth), null);
                if (fixture is not null) return (null, null);
                return await ask($"Finding touch zones ({zoneAsk.Step})", zoneAsk.Instructions, zoneAsk.Text, image, cancel);
            }

            // It looks for the default zones and the ones the owner added; with Include intimate zones on (it is unless the owner
            // turned it off), the intimate ones must be found, and so must the ones the owner added.
            var options = TouchZoneDetection.For(before);
            var result = await Task.Run(() => TouchZoneDetection.RunAsync(snapshot, hints, AskAsync, progress =>
            {
                latest = progress.Zones;
                Report(tag + progress.Text);
            }, token, options), token);
            steps.AddRange(result.Steps);
            foreach (var step in result.Steps) ErrorLog.Info($"Finding touch zones: {step}.");
            if (result.Failure is { } failure)
            {
                // A request failed (the model, or the computer it runs on, stopped answering): finding zones stopped there.
                ErrorLog.Warn($"Finding touch zones stopped at request {requests}: {failure}.");
                var stopped = tag + $"Finding zones stopped at request {requests}: couldn't ask the Thinking model ({failure}).";
                if (before is { Zones.Count: > 0 } earlier)
                {
                    // The zones found so far were saved over the earlier ones as they came: those, and their picture, go back.
                    if (pictureKept && await RestoreAsync(earlier, earlierPicture, earlierProbe) is { } lost)
                        return Report(stopped + " The zones from before couldn't be put back: " + lost);
                    pictureKept = false;
                    var n = earlier.Zones.Count;
                    return Report(stopped + (earlier.DetectedBy == CharacterTouchZoneSettings.ByEstimate
                        ? $" The {Zones(n)} of the first guess {(n == 1 ? "is" : "are")} kept."
                        : $" Your {Zones(n)} from before {(n == 1 ? "is" : "are")} kept.") + " Try again when it answers.");
                }
                if (result.Zones is not { Count: > 0 } partial) return Report(tag + $"Couldn't ask the Thinking model ({failure}).");
                if (await KeepAsync(id, before, partial, pictureKept ? null : shot.Png, crop, shot.Probe, token) is { } unsaved)
                    return Report("The zones were found but couldn't be saved: " + unsaved);
                pictureKept = true;
                return Report(stopped + $" The {Zones(partial.Count)} found until then {(partial.Count == 1 ? "is" : "are")} kept. " +
                    "Press Detect again to find the rest.");
            }
            if (result.Zones is null)
                return Report(tag + "The Thinking model's answers had no zones Martlet could read. Try again, or choose a model that can see (Companion › Vision).");
            if (await KeepAsync(id, before, result.Zones, pictureKept ? null : shot.Png, crop, shot.Probe, token) is { } why)
                return Report("The zones were found but couldn't be saved: " + why);
            pictureKept = true;
            var settings = Current!;
            var bound = settings.Zones.Count(z => z.Drawables.Count > 0 || z.Bones.Count > 0);
            var checks = pictures.Count(p => p.Kind == nameof(ZoneAskKind.Check));
            // The zones special to the character it found, by the names they show with.
            var special = settings.Zones.Where(z => !z.Added && TouchZoneDetection.IsSpecial(z.Id) && result.Zones.Any(r => r.Id == z.Id))
                .Select(z => z.Name.ToLowerInvariant()).ToArray();
            ErrorLog.Info($"The Thinking model found {result.Zones.Count} touch zones in {requests} requests ({bound} bound to the model's parts" +
                (special.Length > 0 ? $", {special.Length} special to the character" : "") + $"{Following(settings)}).");
            return Report(tag + $"Found {result.Zones.Count} zone{(result.Zones.Count == 1 ? "" : "s")}" +
                (special.Length > 0 ? $" ({special.Length} special to this character: {List(special)})" : "") + $" at {DateTime.Now:t} in {requests} " +
                $"request{(requests == 1 ? "" : "s")} ({checks} check{(checks == 1 ? "" : "s")} of the boxes); {bound} follow the model's parts as it moves." +
                FollowingText(settings));
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            return Report("Finding zones was stopped. The zones found until then are kept.");
        }
        finally
        {
            if (dataDirectory is not null && pictures.Count > 0)
            {
                var record = new TouchZoneSent(DateTimeOffset.Now, fixture is not null, requests, [.. pictures], [.. steps], pictureKept);
                Volatile.Write(ref sent, record);
                try { await CharacterTouchZones.SaveSentAsync(dataDirectory, id, record, CancellationToken.None); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException) { ErrorLog.Warn($"Couldn't keep what finding zones sent: {error.Message}"); }
            }
            Volatile.Write(ref busy, false);
            Changed?.Invoke();
        }
    }

    // Saves zones found (so far) in the snapshot, with the owner's choices for each zone from before this detection, and the
    // snapshot they belong to with its probe. While the detection runs (partial) over a first guess, the first guess's zones not
    // found yet stay. Returns why they couldn't be saved, or null.
    private async Task<string?> KeepAsync(string id, CharacterTouchZoneSettings? before, IReadOnlyList<CharacterTouchZone> zones, byte[]? png,
        TouchZoneBox crop, RendererZoneProbe? probe, CancellationToken token, bool partial = false)
    {
        if (dataDirectory is not null && png is not null)
        {
            try
            {
                await CharacterTouchZones.SaveSnapshotAsync(dataDirectory, id, png, token);
                await CharacterTouchZones.SaveSnapshotProbeAsync(dataDirectory, id, probe is null ? null : new(crop, probe), token);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { ErrorLog.Warn($"Couldn't keep the touch zones' picture: {error.Message}"); }
        }
        return await SaveAsync(CharacterTouchZones.Detected(before, id, zones, crop, probe, DateTimeOffset.Now, whole: true,
            keepUnfound: partial && before?.DetectedBy == CharacterTouchZoneSettings.ByEstimate), token);
    }

    // Puts the zones from before a detection back, with the picture they were found in and its probe. Returns why they couldn't
    // be, or null.
    private async Task<string?> RestoreAsync(CharacterTouchZoneSettings earlier, byte[]? picture, TouchZoneProbeFile? probe)
    {
        if (dataDirectory is not null && picture is not null)
        {
            try
            {
                await CharacterTouchZones.SaveSnapshotAsync(dataDirectory, earlier.ModelId, picture, CancellationToken.None);
                await CharacterTouchZones.SaveSnapshotProbeAsync(dataDirectory, earlier.ModelId, probe, CancellationToken.None);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { ErrorLog.Warn($"Couldn't put back the touch zones' picture: {error.Message}"); }
        }
        return await SaveAsync(earlier, CancellationToken.None);
    }

    /// <summary>Saves the owner's edits to the loaded model's zones (a box moved, a zone or an area added), bound again to the model
    /// with the probe of their picture (<see cref="CharacterTouchZones.Rebind"/>), so each area takes what its box holds and a
    /// zone on a tail follows all of it. Returns why they couldn't be saved (or null), and whether binding changed the zones'
    /// areas, so the page shows them again.</summary>
    internal async Task<(string? Why, bool Reshaped)> SaveEditedAsync(CharacterTouchZoneSettings settings, CancellationToken token)
    {
        var bound = dataDirectory is null ? settings : await Task.Run(() =>
            CharacterTouchZones.Rebind(settings, CharacterTouchZones.LoadSnapshotProbe(dataDirectory, settings.ModelId)), token);
        var reshaped = !ReferenceEquals(bound, settings) && bound.Zones.Zip(settings.Zones).Any(pair =>
            pair.First.AllAreas.Count != pair.Second.AllAreas.Count || pair.First.Follows != pair.Second.Follows ||
            pair.First.AllAreas.Zip(pair.Second.AllAreas).Any(areas => areas.First.Box != areas.Second.Box));
        return (await SaveAsync(bound, token), reshaped);
    }

    // " (the tail follows the model's 尾巴 in 6 areas)" for the log, or "".
    private static string Following(CharacterTouchZoneSettings? settings) =>
        settings?.Zones.Where(z => z.Follows is not null).Select(z => $"{z.Name.ToLowerInvariant()} follows the model's {z.Follows} in {z.AllAreas.Count} areas")
            .ToArray() is { Length: > 0 } following ? "; " + string.Join(", ", following) : "";

    /// <summary>" Tail follows the model's own 尾巴 wherever it moves, in 6 areas." for the page, or "".</summary>
    internal static string FollowingText(CharacterTouchZoneSettings? settings) =>
        string.Concat(settings?.Zones.Where(z => z.Follows is not null)
            .Select(z => $" {z.Name} follows the model's own {z.Follows} wherever it moves, in {z.AllAreas.Count} area{(z.AllAreas.Count == 1 ? "" : "s")}.") ?? []);

    // ", area 3 of 6" for a touch on one area of a zone with several, or "".
    private static string AreaText(TouchZoneMatch match) => match.Area is { } area ? $", area {area + 1} of {match.Zone.AllAreas.Count}" : "";

    private static async Task<byte[]?> ReadAsync(string path, CancellationToken token)
    {
        try { return await File.ReadAllBytesAsync(path, token); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return null; }
    }

    private static string Zones(int count) => count == 1 ? "1 zone" : $"{count} zones";

    // "a", "a and b", "a, b and c".
    private static string List(IReadOnlyList<string> items) =>
        items.Count == 1 ? items[0] : string.Join(", ", items.Take(items.Count - 1)) + " and " + items[^1];

    /// <summary>A touch on the showing character: the zone it landed in plays its reaction (unless the zone is resting), and
    /// <paramref name="notice"/> gets every zone the touch landed in that Martlet notices (zones can overlap,
    /// <see cref="CharacterTouchZones.Touched"/>; the matched zone first), resting or not: every touch adds up. When the
    /// reaction says so, <paramref name="look"/> turns the eyes to the mouse pointer for that many seconds. Returns the match,
    /// or null.</summary>
    internal TouchZoneMatch? React(CharacterTouch touch, Func<CharacterTouchZone, int, TouchReactionPlan> planFor,
        Func<CharacterActionSource, string, double, Task> play, Action<IReadOnlyList<CharacterTouchZone>> notice, string kind = "touch",
        Action<double, string>? look = null)
    {
        var settings = Current;
        var match = CharacterTouchZones.Match(settings, touch);
        var when = DateTime.Now.ToString("T", System.Globalization.CultureInfo.CurrentCulture);
        var what = kind == "stroke" ? "A stroke" : "A touch";
        if (match is null)
        {
            Volatile.Write(ref lastMatch, $"{what} at {when} landed on no zone in use.");
            Changed?.Invoke();
            return null;
        }
        var zone = match.Zone;
        var how = Describe(match);
        var touched = CharacterTouchZones.Touched(settings, touch, match);
        var heard = touched.Where(z => z.Reaction.Notices).ToArray();
        if (heard.Length > 0) notice(heard);
        // "Groin (box), with Left thigh, at ...": the other zones it landed in, where zones overlap.
        var with = touched.Count > 1 ? ", with " + Names(touched.Skip(1).Select(z => z.Name)) + "," : "";
        var noticed = heard.Length == 0 ? null : heard.Length < touched.Count ? Names(heard.Select(z => z.Name)) : touched.Count == 1 ? "it" : "them";
        var now = Environment.TickCount64;
        lock (rested)
        {
            if (rested.TryGetValue(zone.Id, out var until) && now < until)
            {
                Volatile.Write(ref lastMatch, $"{zone.Name} ({how}){with} at {when}: resting, so nothing played" +
                    (noticed is null ? "." : $", but Martlet noticed {noticed}."));
                Changed?.Invoke();
                return match;
            }
            rested[zone.Id] = now + (long)(zone.Reaction.CooldownSeconds * 1000);
        }
        var repeats = Repeat(zone.Id, now);
        var reaction = planFor(zone, repeats);
        var plan = reaction.Actions;
        for (var i = 0; i < plan.Count; i++) play(plan[i], $"a {kind} on {zone.Name.ToLowerInvariant()}", i == 0 ? reaction.LingerSeconds : 0).Forget();
        if (reaction.LookSeconds > 0) look?.Invoke(reaction.LookSeconds, $"a {kind} on {zone.Name.ToLowerInvariant()}");
        Autoplay(reaction, $"a {kind} on {zone.Name.ToLowerInvariant()}", play);
        Volatile.Write(ref lastMatch, (kind == "stroke" ? "Stroke: " : "") + $"{zone.Name} ({how}){with} at {when}: " +
            (plan.Count == 0 ? "nothing to play" : "played " + string.Join(", ", plan.Select(s => s.Name))) + Describe(reaction, repeats) +
            (noticed is null ? "." : $", and Martlet noticed {noticed}."));
        ErrorLog.Info($"{(kind == "stroke" ? "Stroke" : "Touch")} on {string.Join(" + ", touched.Select(z => z.Id))} ({how}{(touch.Held ? ", held" : "")}): " +
            $"{plan.Count} played from {reaction.From} for {zone.Id}{(reaction.Escalated ? " (escalated)" : "")}" +
            $"{(heard.Length > 0 ? ", Martlet noticed " + string.Join(" + ", heard.Select(z => z.Id)) : "")}.");
        Changed?.Invoke();
        return match;
    }

    /// <summary>Plays the zone's autoplay list on its own: after the reaction, each emote or gesture in turn for the zone's step.</summary>
    internal static void Autoplay(TouchReactionPlan reaction, string reason, Func<CharacterActionSource, string, double, Task> play)
    {
        if (reaction.Autoplay is not { Count: > 0 } list) return;
        var step = Math.Max(1, reaction.AutoplaySeconds);
        Task.Run(async () =>
        {
            await Task.Delay(TimeSpan.FromSeconds(step));
            foreach (var source in list) await play(source, reason + " (autoplay)", step);
        }).Forget();
    }

    /// <summary>How a touch found its zone, for the last-touch line and the log: "box", or "box, traced to the rest pose" when
    /// the boxes were compared with where the touched point was in the rest pose the zones were found in, and for a zone with
    /// several areas which one ("drawable, area 3 of 6").</summary>
    internal static string Describe(TouchZoneMatch match) => (match.Traced ? match.How + ", traced to the rest pose" : match.How) + AreaText(match);

    // "Left thigh", "Left thigh and Hips", "Left thigh, Hips and Groin".
    private static string Names(IEnumerable<string> names)
    {
        var list = names.ToArray();
        return list.Length == 1 ? list[0] : string.Join(", ", list[..^1]) + " and " + list[^1];
    }

    private readonly Dictionary<string, (int Count, long Last)> streaks = new(StringComparer.Ordinal);

    /// <summary>How many touches in a row the zone has had, this one included (each within the repeat window of the one before).</summary>
    private int Repeat(string zoneId, long now)
    {
        lock (streaks)
        {
            var count = streaks.TryGetValue(zoneId, out var streak) && now - streak.Last <= CharacterTouchTemperaments.RepeatWindowSeconds * 1000
                ? streak.Count + 1 : 1;
            streaks[zoneId] = (count, now);
            return count;
        }
    }

    /// <summary>" (loves it, from the persona's temperament, looks at your mouse for 3 s, touch 3 in a row: escalated)" for the
    /// last-touch line.</summary>
    internal static string Describe(TouchReactionPlan reaction, int repeats) =>
        " (" + (reaction.Attitude is { } attitude ? attitude + " it, " : "") + reaction.From switch
        {
            TouchReactionPlan.FromOwner => "your pick for the zone",
            TouchReactionPlan.FromTemperament => "from the persona's temperament",
            _ => "built-in reaction"
        } + (reaction.LookSeconds > 0 ? System.FormattableString.Invariant($", looks at your mouse for {reaction.LookSeconds:0.#} s") : "") +
        (repeats > 1 ? $", touch {repeats} in a row" : "") + (reaction.Escalated ? ": escalated" : "") + ")";

    /// <summary>Sets how finding zones goes (<see cref="Detection"/>) and returns it.</summary>
    internal string Report(string text)
    {
        Volatile.Write(ref detection, text);
        Changed?.Invoke();
        return text;
    }

    /// <summary>The conversation's touch status changed: what waits and what the last touches did.</summary>
    internal void SetNoticed(string waiting, string? last)
    {
        Volatile.Write(ref noticed, waiting);
        Volatile.Write(ref noticedLast, last);
        Changed?.Invoke();
    }

    /// <summary>Sets the last-touch line (a Try).</summary>
    internal void Note(string text)
    {
        Volatile.Write(ref lastMatch, text);
        Changed?.Invoke();
    }
}
