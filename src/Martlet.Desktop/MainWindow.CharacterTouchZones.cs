using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using Martlet.Avatar.Hosting;
using Martlet.Conversation;
using Martlet.Core.Settings;
using Martlet.Presentation;
using Martlet.Providers;

namespace Martlet.Desktop;

/// <summary>Companion › Touch › Touch zones: where a left click on the character lands (the hair, an eye, a hand...)
/// and what the character does then. The first time the page shows a model with no zones, Martlet draws it off screen and places
/// a first guess at its zones with no AI (<see cref="TouchZoneDetection.Estimate"/>). The Thinking model then finds the zones in a
/// snapshot of the character (when it can see; Detect zones asks): the default zones (<see cref="TouchZoneDetection.Defaults"/>) and any the owner added with
/// Add zone. Martlet binds each to the model's drawables or bones so it follows the model as it moves,
/// and each zone plays its reaction list (its emotes, gestures, motions and voice sounds, in order), may be noticed by Martlet (the touches go to the Thinking model) and rests a few
/// seconds. Intimate zones work only with Include intimate zones on (on by default). Edits save as you make them, per model, on this PC.</summary>
public partial class MainWindow
{
    private readonly CharacterTouchZoneService characterTouchZones;
    private bool detectingTouchZones;

    private void WireCharacterTouchZones()
    {
        // Zones saved before reaction lists, and zones just found, get the list they play now once the model's emotes (and the
        // active persona, for its temperament: the settings are read) are known.
        characterTouchZones.Fill = settings => homeSettingsState is SettingsLoadState.FirstRun or SettingsLoadState.Loaded &&
            TouchZonesCatalog(settings.ModelId) is { } catalog
            ? CharacterTouchZones.Filled(settings, catalog, characterTemperaments.For(homeSettings?.Companion?.ActivePersonaId)) : settings;
        characterActions.Changed += () =>
        {
            characterTouchZones.Follow(characterActions.Current?.Inventory.ModelId);
            characterTouchZones.FillLists();
        };
        characterTouchZones.Changed += () => Dispatcher.InvokeAsync(() =>
        {
            // The showing character keeps the zones as they are now (sent only when they change).
            SendTouchZoneView();
            touchZonesPage?.ShowTouches();
            if (closing || openTab != CompanionTab.Touch || CompanionContent.IsKeyboardFocusWithin || tabEdited) return;
            // While the first guess is placed the page waits: it is drawn again once, when the guess is done.
            if (detectingTouchZones || characterTouchZones.Busy && !characterTouchZones.Estimating || renderedZonesModel != characterTouchZones.ModelId)
                RenderTab();
        });
        avatar.TouchRouter = OnCharacterTouched;
        // A character that shows (again) gets the zones of its model, so Show the zones on the character and Martlet's MCP find
        // them as it moves.
        avatar.UseZoneView(TouchZoneViewFor);
        WireCharacterTemperament();
        WireCharacterEyes();
    }

    // Touch zones › Show the zones on the character, for this session.
    private bool showZonesOnCharacter;

    /// <summary>The touch zones the showing character gets when its model is <paramref name="modelPath"/>: the zones in use of the
    /// model the Touch zones page follows, each in its row's color, drawn when Show the zones on the character is on; null for
    /// another model.</summary>
    private RendererZoneView? TouchZoneViewFor(string? modelPath) =>
        characterActions.For(modelPath)?.Inventory.ModelId is { } id && id == characterTouchZones.ModelId
            ? RendererZoneView.Of(characterTouchZones.Current, showZonesOnCharacter, [.. TouchZonesPage.ZoneColors.Select(c => $"#{c.R:X2}{c.G:X2}{c.B:X2}")]) : null;

    // Gives the showing character its zones as they are now (the avatar sends them only when they changed).
    private void SendTouchZoneView()
    {
        if (closing || !avatar.IsShowing) return;
        avatar.SendZoneViewAsync(TouchZoneViewFor(avatar.InspectedProfile?.ModelPath) ?? new(false, []), lifetime.Token).Forget();
    }

    private string? renderedZonesModel;

    private string TouchZonesIdle() => avatar.IsShowing ? "Click the character to try a zone." : "Show the character, then click it to try a zone.";

    // Off the UI thread (the renderer's request relay): the zone's reaction plays at once; what Martlet notices goes to the
    // conversation on the UI thread.
    private bool OnCharacterTouched(CharacterTouch touch)
    {
        if (closing || !avatar.IsShowing) return false;
        var catalog = characterActions.For(avatar.InspectedProfile?.ModelPath);
        characterTouchZones.Follow(catalog?.Inventory.ModelId ?? characterTouchZones.ModelId);
        var temperament = FeltTemperament();
        // Where zones overlap the touch is on each of them: Martlet hears every one it notices, in one line.
        characterTouchZones.React(touch, (zone, repeats) => TouchPlan(ChangedZone(zone, catalog), catalog, temperament, repeats), PlayTouchAsync,
            zones => Dispatcher.InvokeAsync(() =>
            {
                if (CharacterPhysicalWords.Touch(zones) is not { } words) return;
                NoticePhysical(touch.Held ? PhysicalKind.Hold : words.Pat ? PhysicalKind.Pat : PhysicalKind.Tap, words.Where, words.Label,
                    hint: words.Hint, zones: [.. zones.Select(CharacterTouchZones.Part)],
                    intimate: zones.Any(z => CharacterTouchZones.Kind(z.Id)?.Intimate == true),
                    feeling: CharacterTouchTemperaments.Feeling(temperament, zones));
            }),
            look: avatar.Gaze.Attend, sound: PlayTouchSoundsLater);
        return true;
    }

    /// <summary>The emotes and motions of the model with <paramref name="modelId"/>: the one Companion shows, else the showing
    /// character's; null when neither is that model.</summary>
    private CharacterActionCatalog? TouchZonesCatalog(string modelId) =>
        characterActions.Current is { } current && current.Inventory.ModelId == modelId ? current
            : characterActions.For(avatar.InspectedProfile?.ModelPath) is { } shown && shown.Inventory.ModelId == modelId ? shown : null;

    /// <summary>What touching <paramref name="zone"/> plays: its reaction list on the model, with how the active persona's touch
    /// temperament feels about it (its linger, look and escalation).</summary>
    private TouchReactionPlan TouchPlan(CharacterTouchZone zone, CharacterActionCatalog? catalog, CharacterTouchTemperament? temperament, int repeats) =>
        CharacterTouchZones.React(zone, catalog, temperament, repeats);

    /// <summary>Plays one reaction; with <paramref name="lingerSeconds"/> it stays on that long (unless it already showed).</summary>
    private async Task PlayTouchAsync(CharacterActionSource source, string reason, double lingerSeconds)
    {
        try
        {
            var linger = lingerSeconds > 0 && !avatar.Held.Holds(source.Id);
            var started = await avatar.PlayActionAsync(source, reason, null, lifetime.Token, hold: linger);
            if (!linger || !started || !avatar.Held.Holds(source.Id)) return;
            await Task.Delay(TimeSpan.FromSeconds(lingerSeconds), lifetime.Token);
            await avatar.StopActionAsync(source, reason + " (lingered)", lifetime.Token);
        }
        catch (Exception error) when (error is OperationCanceledException || RendererFailures.Is(error, lifetime.Token)) { }
    }

    /// <summary>Something the user did to the desktop character that Martlet notices (a touch on a zone with Martlet notices on,
    /// a stroke, moving or zooming it...): it goes to the conversation's touch ledger, waits for the next reply and, for touches
    /// (<see cref="PhysicalKinds.StartsTurn"/>), starts a short reply of its own when the user says nothing. On the UI thread.
    /// <paramref name="zone"/> is where, as the character hears it ("the top of your head"), <paramref name="label"/> its short
    /// name for the history ("top of head"), <paramref name="detail"/> more ("to another monitor"), <paramref name="hint"/>
    /// the owner's own words for it, <paramref name="zones"/> each place it touched (a stroke's zones), <paramref name="intimate"/>
    /// whether it touched an intimate zone and <paramref name="feeling"/> how the persona feels about it.</summary>
    internal void NoticePhysical(PhysicalKind kind, string? zone = null, string? label = null, string? detail = null, string? hint = null,
        IReadOnlyList<string>? zones = null, bool intimate = false, string? feeling = null)
    {
        if (closing || Role == DeviceRole.Host || conversation is null || ConversationSession() is not { } talk) return;
        if (!talk.IsVisible) talk.StartInBackground();
        var physical = new PhysicalEvent(kind, conversation.TouchNow, zone, label, detail, hint, zones, intimate, feeling);
        talk.Physical(physical);
        CheckInTouched(physical);
    }

    private CancellationTokenSource? detectTouchZones;
    // The models whose first zones the page placed (or tried to) since Martlet started, so drawing the page again doesn't repeat a
    // try that failed (opening the page again does).
    private readonly HashSet<string> firstTouchZonesTried = new(StringComparer.Ordinal);

    /// <summary>The first guess at the shown model's zones (<see cref="CharacterTouchZoneService.EstimateAsync"/>), placed when the
    /// page opens on a model with no zones and no picture: the character drawn off screen, its zones placed with no AI. The page
    /// being drawn already shows it is busy (the service is, before its first wait), and it is drawn again once, when the guess
    /// is done, with the picture and the zones.</summary>
    private async Task EstimateTouchZonesAsync()
    {
        try
        {
            await characterTouchZones.EstimateAsync(avatar, async () =>
            {
                var profile = avatar.IsShowing && avatar.InspectedProfile is { } shown ? shown : (await SavedCharacterAsync()).Shown;
                // Only the model the page follows: its zones are saved under its ID.
                return characterActions.For(profile.ModelPath)?.Inventory.ModelId is { } pictured && pictured == characterTouchZones.ModelId ? profile : null;
            }, lifetime.Token);
        }
        catch (Exception error) when (error is IOException or InvalidOperationException or UnauthorizedAccessException or
            Martlet.Core.Contracts.ContractException or System.Text.Json.JsonException or OperationCanceledException)
        {
            if (!closing && error is not OperationCanceledException) characterTouchZones.Report($"Martlet couldn't read which character to picture: {error.Message}");
        }
        finally
        {
            if (!closing && openTab == CompanionTab.Touch && !detectingTouchZones) RenderTab();
        }
    }

    private async Task DetectTouchZonesAsync()
    {
        if (conversation is null || detectingTouchZones || characterTouchZones.Estimating) return;
        detectingTouchZones = true;
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        detectTouchZones = stop;
        // Detecting... and Stop show at once. A click leaves the keyboard focus on Detect zones, and the page holds back its updates
        // while the focus is in it; rendering it again takes the focus off the old button, so each step's progress shows too.
        if (!closing && openTab == CompanionTab.Touch) RenderTab();
        try
        {
            // The picture is drawn off screen by a renderer of its own, in the character's rest pose: the character needn't show,
            // and the one on the desktop never moves.
            var profile = avatar.IsShowing && avatar.InspectedProfile is { } shown ? shown : (await SavedCharacterAsync()).Shown;
            characterTouchZones.Follow(characterActions.For(profile.ModelPath)?.Inventory.ModelId);
            // Each step's picture goes to a Thinking pool member that can see, else to the conversation's Thinking model after
            // any reply. MARTLET_TOUCH_ZONES_FIXTURE answers them instead (FIXTURE - NOT AI); see CharacterTouchZoneService.
            var talk = conversation;
            await characterTouchZones.DetectAsync(avatar, profile,
                (purpose, instructions, text, image, token) => talk.AskHelperAsync(HelperJobKind.TouchZones, purpose, instructions, text, image, token),
                stop.Token);
        }
        catch (Exception error) when (error is IOException or InvalidOperationException or UnauthorizedAccessException or
            Martlet.Core.Contracts.ContractException or System.Text.Json.JsonException or OperationCanceledException)
        {
            if (!closing) characterTouchZones.Report(error is OperationCanceledException
                ? "Finding zones was stopped, and the zones found until then are kept."
                : $"Martlet couldn't read which character to picture: {error.Message}");
        }
        finally
        {
            detectTouchZones = null;
            detectingTouchZones = false;
            tabEdited = false;
            if (!closing && openTab == CompanionTab.Touch) RenderTab();
        }
    }

    /// <summary>Companion › Touch › Touch zones (<see cref="TouchZonesPage"/>), for the model Companion shows: its zones with no
    /// reaction list yet get (and save) the list they play now, and a model with no zones and no picture gets a first guess at once
    /// (drawn off screen, placed with no AI), so the picture and its zones show before Detect zones. One that couldn't be placed is
    /// tried again when the page opens again.</summary>
    private Border CharacterTouchZonesCard()
    {
        var catalog = characterActions.Current;
        characterTouchZones.Follow(catalog?.Inventory.ModelId);
        characterTouchZones.FillLists();
        renderedZonesModel = characterTouchZones.ModelId;
        if (catalog is not null && !detectingTouchZones && characterTouchZones.NeedsFirstGuess &&
            (firstTouchZonesTried.Add(catalog.Inventory.ModelId) || openingTab))
            EstimateTouchZonesAsync().Forget();
        return TouchZonesPage.ZonesCard();
    }

    private void TryTouchZone(CharacterTouchZone zone, CharacterActionCatalog catalog)
    {
        var reaction = TouchPlan(zone, catalog, characterTemperaments.For(homeSettings?.Companion?.ActivePersonaId), 1);
        var plan = reaction.Actions;
        for (var i = 0; i < plan.Count; i++) PlayTouchAsync(plan[i], $"a try of {zone.Name.ToLowerInvariant()}", i == 0 ? reaction.LingerSeconds : 0).Forget();
        if (reaction.LookSeconds > 0) avatar.Gaze.Attend(reaction.LookSeconds, $"a try of {zone.Name.ToLowerInvariant()}");
        var sound = reaction.Sounds is { Count: > 0 } cues ? PlayTouchSounds(zone, cues, $"a try of {zone.Name.ToLowerInvariant()}") : null;
        characterTouchZones.Note($"Tried {zone.Name}: " + (plan.Count == 0 ? "nothing to play on this model" : "played " + string.Join(", ", plan.Select(s => s.Name))) +
                (sound is null ? "" : $"; voice sound {sound}") + CharacterTouchZoneService.Describe(reaction, 1) + "." +
                (zone.Reaction.Notices ? " Martlet notices touches here" + (CharacterTouchZones.Narration(zone) is { } line ? $" (your words: \"{line}\")." : ".") : ""));
    }
}
