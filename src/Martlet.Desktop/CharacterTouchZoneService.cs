using System.IO;
using Martlet.Avatar.Hosting;
using Martlet.Core.Contracts;
using Martlet.Providers;

namespace Martlet.Desktop;

/// <summary>The touch zones of the character this PC shows (Companion › Character › Touch zones): found once per model by the
/// Thinking model (when it can see) in a snapshot of the character, bound to the model's drawables or bones, saved per model in
/// character-touch-zones.json, and what a touch on each does.</summary>
internal sealed class CharacterTouchZoneService(string? dataDirectory)
{
    internal const string FixtureVariable = "MARTLET_TOUCH_ZONES_FIXTURE";

    private readonly Dictionary<string, long> rested = new(StringComparer.Ordinal);
    private CharacterTouchZoneSettings? current;
    private string? modelId;
    private string? detection;
    private string? lastMatch;
    private bool busy;

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
    internal bool Busy => Volatile.Read(ref busy);

    /// <summary>The snapshot the loaded model's zones were found in (a PNG), or null.</summary>
    internal string? SnapshotPath => dataDirectory is not null && ModelId is { } id && CharacterTouchZones.SnapshotPath(dataDirectory, id) is var path &&
        File.Exists(path) ? path : null;

    /// <summary>Loads the zones of the model with <paramref name="id"/> unless they are already loaded.</summary>
    internal void Follow(string? id)
    {
        if (id == ModelId) return;
        Volatile.Write(ref current, id is null || dataDirectory is null ? null : CharacterTouchZones.Load(dataDirectory, id));
        Volatile.Write(ref modelId, id);
        Volatile.Write(ref detection, null);
        lock (rested) rested.Clear();
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

    /// <summary>Takes a snapshot of the showing character and asks the Thinking model (through <paramref name="ask"/>, with the
    /// picture) where its zones are, then binds them to the model's drawables or bones and saves them. Returns what happened.</summary>
    internal async Task<string> DetectAsync(AvatarController avatar,
        Func<string, string, string, BoundedImage?, CancellationToken, Task<(string? Answer, string? Failure)>> ask, CancellationToken token)
    {
        if (ModelId is not { } id || !avatar.IsShowing) return Report("Show the character first.");
        Volatile.Write(ref busy, true);
        Report("Taking a picture of the character...");
        try
        {
            if (await avatar.ZoneSnapshotAsync(token) is not { } shot) return Report("Martlet couldn't take a picture of the character. Try again.");
            BoundedImage image;
            try { image = new(shot.Png, ImageMediaType.Png, shot.Picture.Width, shot.Picture.Height); }
            catch (ContractException) { return Report("The character's picture couldn't be used. Try again."); }
            Report(Environment.GetEnvironmentVariable(FixtureVariable) is { Length: > 0 }
                ? "FIXTURE - NOT AI: reading the zones from MARTLET_TOUCH_ZONES_FIXTURE instead of asking the Thinking model..."
                : "Asking the Thinking model where the character's touch zones are...");
            var (answer, failure) = await ask("Finding touch zones", CharacterTouchZones.DetectionInstructions, CharacterTouchZones.DetectionList, image, token);
            return await FoundAsync(id, answer, failure, shot.Png, shot.Picture, shot.Probe, token);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { return Report("Finding zones was stopped."); }
        finally { Volatile.Write(ref busy, false); Changed?.Invoke(); }
    }

    /// <summary>What the vision model's <paramref name="answer"/> about <paramref name="picture"/> gives, saved for the model.</summary>
    internal async Task<string> FoundAsync(string id, string? answer, string? failure, byte[] png, RendererPicture picture, RendererZoneProbe? probe,
        CancellationToken token)
    {
        var found = CharacterTouchZones.Parse(answer, picture.Width, picture.Height);
        if (found is null)
            return Report(failure is null
                ? "The Thinking model's answer had no zones Martlet could read. Try again, or choose a model that can see (Companion › Vision)."
                : $"Couldn't ask the Thinking model ({failure}).");
        var crop = new TouchZoneBox(picture.CropLeft, picture.CropTop, picture.CropWidth, picture.CropHeight);
        var settings = CharacterTouchZones.Detected(Current, id, found, crop, probe, DateTimeOffset.Now);
        if (await SaveAsync(settings, token) is { } why) return Report("The zones were found but couldn't be saved: " + why);
        if (dataDirectory is not null)
        {
            try { await CharacterTouchZones.SaveSnapshotAsync(dataDirectory, id, png, token); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { ErrorLog.Warn($"Couldn't keep the touch zones' picture: {error.Message}"); }
        }
        var bound = settings.Zones.Count(z => z.Drawables.Count > 0 || z.Bones.Count > 0);
        ErrorLog.Info($"The Thinking model found {found.Count} touch zones ({bound} bound to the model's parts).");
        return Report((Environment.GetEnvironmentVariable(FixtureVariable) is { Length: > 0 } ? "FIXTURE - NOT AI: " : "") +
            $"Found {found.Count} zone{(found.Count == 1 ? "" : "s")} at {DateTime.Now:t}; {bound} follow the model's parts as it moves.");
    }

    /// <summary>A touch on the showing character: the zone it landed in plays its reaction (unless the zone is resting) and may
    /// tell the character. Returns the match, or null.</summary>
    internal TouchZoneMatch? React(CharacterTouch touch, Func<CharacterTouchZone, IReadOnlyList<CharacterActionSource>> planFor, Func<CharacterActionSource, string, Task> play,
        Action<string> tell)
    {
        var match = CharacterTouchZones.Match(Current, touch);
        var when = DateTime.Now.ToString("T", System.Globalization.CultureInfo.CurrentCulture);
        if (match is null)
        {
            Volatile.Write(ref lastMatch, $"A touch at {when} landed on no zone in use.");
            Changed?.Invoke();
            return null;
        }
        var zone = match.Zone;
        var now = Environment.TickCount64;
        lock (rested)
        {
            if (rested.TryGetValue(zone.Id, out var until) && now < until)
            {
                Volatile.Write(ref lastMatch, $"{zone.Name} ({match.How}) at {when}: resting, so nothing played.");
                Changed?.Invoke();
                return match;
            }
            rested[zone.Id] = now + (long)(zone.Reaction.CooldownSeconds * 1000);
        }
        var plan = planFor(zone);
        foreach (var source in plan) play(source, $"a touch on {zone.Name.ToLowerInvariant()}").Forget();
        var narration = CharacterTouchZones.Narration(zone);
        if (narration is not null) tell(narration);
        Volatile.Write(ref lastMatch, $"{zone.Name} ({match.How}) at {when}: " +
            (plan.Count == 0 ? "nothing to play" : "played " + string.Join(", ", plan.Select(s => s.Name))) +
            (narration is null ? "." : ", and told the character."));
        ErrorLog.Info($"Touch on {zone.Id} ({match.How}): {plan.Count} played{(narration is null ? "" : ", told the character")}.");
        Changed?.Invoke();
        return match;
    }

    private string Report(string text)
    {
        Volatile.Write(ref detection, text);
        Changed?.Invoke();
        return text;
    }

    /// <summary>Sets the last-touch line (a Try).</summary>
    internal void Note(string text)
    {
        Volatile.Write(ref lastMatch, text);
        Changed?.Invoke();
    }
}
