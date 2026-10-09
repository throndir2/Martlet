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
    private TextBlock? touchZonesLast, touchZonesNoticed, touchZonesNoticedLast;
    private bool detectingTouchZones;

    private static readonly Color[] ZoneColors =
    [
        Color.FromRgb(0xE9, 0x4F, 0x64), Color.FromRgb(0x3B, 0x82, 0xF6), Color.FromRgb(0x10, 0xB9, 0x81), Color.FromRgb(0xF5, 0x9E, 0x0B),
        Color.FromRgb(0x8B, 0x5C, 0xF6), Color.FromRgb(0xEC, 0x48, 0x99), Color.FromRgb(0x06, 0xB6, 0xD4), Color.FromRgb(0x84, 0xCC, 0x16)
    ];

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
            if (touchZonesLast is not null) touchZonesLast.Text = characterTouchZones.LastMatch ?? TouchZonesIdle();
            if (touchZonesNoticed is not null) touchZonesNoticed.Text = characterTouchZones.Noticed ?? TouchZonesNoticedIdle;
            if (touchZonesNoticedLast is not null) touchZonesNoticedLast.Text = characterTouchZones.NoticedLast ?? "";
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
            ? RendererZoneView.Of(characterTouchZones.Current, showZonesOnCharacter, [.. ZoneColors.Select(c => $"#{c.R:X2}{c.G:X2}{c.B:X2}")]) : null;

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
        var temperament = characterTemperaments.For(homeSettings?.Companion?.ActivePersonaId);
        // Where zones overlap the touch is on each of them: Martlet hears every one it notices, in one line.
        characterTouchZones.React(touch, (zone, repeats) => TouchPlan(zone, catalog, temperament, repeats), PlayTouchAsync,
            zones => Dispatcher.InvokeAsync(() =>
            {
                if (CharacterPhysicalWords.Touch(zones) is not { } words) return;
                NoticePhysical(touch.Held ? PhysicalKind.Hold : words.Pat ? PhysicalKind.Pat : PhysicalKind.Tap, words.Where, words.Label,
                    hint: words.Hint, zones: [.. zones.Select(CharacterTouchZones.Part)],
                    intimate: zones.Any(z => CharacterTouchZones.Kind(z.Id)?.Intimate == true),
                    feeling: CharacterTouchTemperaments.Feeling(temperament, zones));
            }),
            look: avatar.Gaze.Attend);
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

    private const string TouchZonesNoticedIdle = "Nothing waits for Martlet.";

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
    private bool showTouchZonesSent;
    // The zones whose rows show their box and Delete (More), while Martlet runs.
    private readonly HashSet<string> openTouchZoneRows = new(StringComparer.Ordinal);
    // The zone map's zoom and the picture's point at the middle of its frame (fractions of the picture), for the model they belong
    // to: drawing the page again (Add zone, each step of Detect zones...) keeps them, and opening it again shows the whole picture.
    private double touchZonesZoom = 1;
    private Point touchZonesCenter = new(0.5, 0.5);
    private string? touchZonesViewModel;
    // The models whose first zones the page placed (or tried to) since Martlet started, so drawing the page again doesn't repeat a
    // try that failed (opening the page again does).
    private readonly HashSet<string> firstTouchZonesTried = new(StringComparer.Ordinal);
    private const double TouchZonesPictureHeight = 600, TouchZonesPictureWidth = 440;

    /// <summary>Touch zones › When you touch Martlet while it talks (<see cref="TalkPreferences.TouchInterrupts"/>, this PC): a
    /// touch it notices stops the reply or remark it is saying, like talking over it, and its reaction knows what it was saying.</summary>
    private void AddTouchInterrupts(List<UIElement> stack)
    {
        stack.Add(new TextBlock
        {
            Text = "When you touch Martlet while it talks", FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 8, 0, 6),
            TextWrapping = TextWrapping.Wrap
        });
        foreach (var (choice, word, title, detail) in new (TouchInterrupts, string, string, string)[]
        {
            (TouchInterrupts.Any, "any", "It stops to react (recommended)",
                "Any touch Martlet notices stops what it is saying, like talking over it. It reacts a moment after your last touch " +
                "and decides whether to pick up where it left off."),
            (TouchInterrupts.Intimate, "intimate", "It stops only for intimate touches",
                $"Only a touch on an intimate part ({CharacterTouchZones.IntimateParts}) stops it; other touches wait until it finishes."),
            (TouchInterrupts.Never, "never", "It finishes first",
                "Your touches wait, and Martlet reacts to them after what it is saying.")
        })
        {
            var option = Choice("TouchInterrupts", title, detail, Talk.TouchInterrupts == choice, "TouchInterrupt-" + word);
            option.Checked += (_, _) => { if (Talk.TouchInterrupts != choice) SaveTalk(Talk with { TouchInterrupts = choice }); };
            stack.Add(option);
        }
    }

    // Pictures decoded once at about the size they show, kept while their file stays the same (Detect again and Measure the eyes
    // write new pictures under the same names), so drawing a page again doesn't decode them again. On the UI thread.
    private static readonly Dictionary<(string Path, int Height, int Width), (DateTime Written, long Length, BitmapImage Picture)> decodedPictures = [];

    // A picture file decoded at about the size it shows (the snapshot can be 2048 pixels tall).
    private static BitmapImage PictureAt(string path, int decodeHeight = (int)(TouchZonesPictureHeight * 2), int decodeWidth = 0)
    {
        var file = new FileInfo(path);
        var (key, written, length) = ((path, decodeHeight, decodeWidth), file.LastWriteTimeUtc, file.Length);
        if (decodedPictures.TryGetValue(key, out var kept) && kept.Written == written && kept.Length == length) return kept.Picture;
        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        // The file's own changes are followed above, not by WPF's image cache.
        bitmap.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
        if (decodeHeight > 0) bitmap.DecodePixelHeight = decodeHeight;
        if (decodeWidth > 0) bitmap.DecodePixelWidth = decodeWidth;
        bitmap.UriSource = new Uri(path);
        bitmap.EndInit();
        bitmap.Freeze();
        if (decodedPictures.Count >= 4) decodedPictures.Clear();
        decodedPictures[key] = (written, length, bitmap);
        return bitmap;
    }

    private static void OpenTouchZonePictures(string folder)
    {
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"\"{folder}\"") { UseShellExecute = false })?.Dispose(); }
        catch (System.ComponentModel.Win32Exception) { }
    }

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
                ? "Finding zones was stopped. The zones found until then are kept."
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

    /// <summary>Whether Detect zones has a model that can see its pictures, the line that says which (<c>TouchZonesVision</c>)
    /// and, when none can, why the button is off (<c>TouchZonesDetectNote</c>). A Thinking pool member that can see
    /// (<paramref name="poolMember"/>, its name) takes the pictures first, else the image model of its own while pictures go to it
    /// (<paramref name="imageModel"/>, its name; Companion › Vision), else the Thinking model (<paramref name="thinking"/>) unless
    /// it is known to be text-only. With <paramref name="fixture"/> a FIXTURE - NOT AI stand-in answers instead.</summary>
    internal static (bool CanSee, string Line, string? Off) TouchZonesSight(SetupRoute? thinking, ModelAbilities? abilities, string? poolMember,
        bool fixture, string? imageModel = null)
    {
        if (fixture) return (true, "FIXTURE - NOT AI: a stand-in answers Detect zones from a file, so no picture is sent.", null);
        var advice = LiveConversationConfiguration.VisionAdvice(thinking, abilities);
        if (poolMember is not null)
            return (true, $"{poolMember} in your Thinking pool can see pictures, so it finds the zones first." +
                (imageModel is not null ? $" Otherwise your image model, {imageModel}, does." : thinking is null ? "" : " " + advice), null);
        if (imageModel is not null)
            return (true, $"Your image model, {imageModel} (Companion › Vision), finds the zones: pictures go to it, not to the Thinking model.", null);
        if (thinking is not null && LiveConversationConfiguration.Vision(thinking, abilities) != VisionSupport.Unsupported) return (true, advice, null);
        return (false, advice, thinking is null
            ? "Detect zones is off: no model that can see pictures is set up. Set up a vision-capable model in Companion › Thinking or Companion › Thinking pool."
            : "Detect zones is off: the Thinking model is text-only, and no Thinking pool member can see pictures. " +
              "Choose a vision-capable model in Companion › Thinking or Companion › Thinking pool.");
    }

    private Border CharacterTouchZonesCard()
    {
        var catalog = characterActions.Current;
        characterTouchZones.Follow(catalog?.Inventory.ModelId);
        // Zones with no reaction list yet show (and save) the list they play now.
        characterTouchZones.FillLists();
        renderedZonesModel = characterTouchZones.ModelId;
        // A model with no zones and no picture gets a first guess at once (drawn off screen, placed with no AI), so the picture and
        // its zones show before Detect zones. One that couldn't be placed is tried again when the page opens again.
        if (catalog is not null && !detectingTouchZones && characterTouchZones.NeedsFirstGuess &&
            (firstTouchZonesTried.Add(catalog.Inventory.ModelId) || openingTab))
            EstimateTouchZonesAsync().Forget();
        var settings = characterTouchZones.Current;
        var temperament = characterTemperaments.For(homeSettings?.Companion?.ActivePersonaId);
        var stack = new List<UIElement>
        {
            Heading("Touch zones"),
            Note("Click the character (a click, not a drag) and it reacts to where you touched it: a pat on the head, a poke on " +
                "the cheek, holding its hand. With its position locked, drag across it to stroke it: each part you cross reacts. " +
                "Each zone plays its own list of emotes, gestures and motions, in order, on its own when you touch it: what the list " +
                "shows is exactly what plays. Add, remove and reorder them; a new zone's list starts from the persona's touch " +
                "temperament or the built-in reactions. Martlet notices adds up your touches on a zone and tells your Thinking model, " +
                "with what you say next or in a short reply of its own. The first time this page shows a model, Martlet places a first " +
                "guess at its zones with no AI and nothing sent; Detect zones has your Thinking model find them in pictures of the " +
                "character (never its files). Changes save as you make them, for this model.", new Thickness(0, 0, 0, 8))
        };
        var status = Note(catalog is null ? "Reading the character..." : TouchZonesStatusText(settings), new Thickness(0, 0, 0, 4));
        AutomationProperties.SetAutomationId(status, "TouchZonesStatus");
        stack.Add(status);
        // From the saved setup, so it is right before the talk window opens.
        conversation?.ReadThinkingPoolOnce();
        var seer = conversation?.ThinkingPool.Find(ThinkingJobKind.TouchZones, ThinkingCapability.Text | ThinkingCapability.Vision);
        var sight = TouchZonesSight(homeSettings?.Setup?.Routes.FirstOrDefault(r => r.Role == SetupRole.Llm), SavedModelAbilities(),
            seer is null ? null : seer.Name + (seer.Model is { Length: > 0 } model ? $" ({model})" : ""),
            Environment.GetEnvironmentVariable(CharacterTouchZoneService.FixtureVariable) is { Length: > 0 }, HelperImageModel());
        var vision = Note(sight.Line, new Thickness(0, 0, 0, 4));
        AutomationProperties.SetAutomationId(vision, "TouchZonesVision");
        stack.Add(vision);
        if (characterTouchZones.Detection is { } detection)
        {
            var found = Note(detection, new Thickness(0, 0, 0, 4));
            AutomationProperties.SetAutomationId(found, "TouchZonesDetection");
            AutomationProperties.SetLiveSetting(found, AutomationLiveSetting.Polite);
            stack.Add(found);
        }
        touchZonesLast = Note(characterTouchZones.LastMatch ?? TouchZonesIdle(), new Thickness(0, 0, 0, 4));
        AutomationProperties.SetAutomationId(touchZonesLast, "TouchZonesLast");
        AutomationProperties.SetLiveSetting(touchZonesLast, AutomationLiveSetting.Polite);
        stack.Add(touchZonesLast);
        touchZonesNoticed = Note(characterTouchZones.Noticed ?? TouchZonesNoticedIdle, new Thickness(0, 0, 0, 4));
        AutomationProperties.SetAutomationId(touchZonesNoticed, "TouchZonesNoticed");
        stack.Add(touchZonesNoticed);
        touchZonesNoticedLast = Note(characterTouchZones.NoticedLast ?? "", new Thickness(0, 0, 0, 4));
        AutomationProperties.SetAutomationId(touchZonesNoticedLast, "TouchZonesNoticedLast");
        stack.Add(touchZonesNoticedLast);
        physicalLastText = Note(PhysicalLastText(), new Thickness(0, 0, 0, 4));
        AutomationProperties.SetAutomationId(physicalLastText, "CharacterPhysicalLast");
        AutomationProperties.SetLiveSetting(physicalLastText, AutomationLiveSetting.Polite);
        stack.Add(physicalLastText);
        AddTouchInterrupts(stack);
        var saveState = Note("", new Thickness(0, 0, 0, 4));
        AutomationProperties.SetAutomationId(saveState, "TouchZonesSaveState");
        AutomationProperties.SetLiveSetting(saveState, AutomationLiveSetting.Polite);
        stack.Add(saveState);

        var busy = detectingTouchZones || characterTouchZones.Busy;
        var detect = PageButton(busy && !characterTouchZones.Estimating ? "Detecting..."
            : settings is { Zones.Count: > 0 } && settings.DetectedBy != CharacterTouchZoneSettings.ByEstimate ? "Detect again" : "Detect zones",
            () => DetectTouchZonesAsync().Forget(), id: "TouchZonesDetect");
        // Only a missing model that can see turns it off (the picture is drawn off screen, so the character needn't show), and
        // the note says so.
        detect.IsEnabled = !busy && conversation is not null && catalog is not null && sight.CanSee;
        AutomationProperties.SetHelpText(detect, "Shows your Thinking model pictures of the character (never its files), step by step, to find and check " +
            "where its parts are. Martlet draws the character off screen in its rest pose, so it works while the character is hidden. First " +
            "the whole character on a plain backdrop with a grid, then a close-up of each part; the model checks its own numbered boxes " +
            "until it says they are right. Martlet fits each box to the character and ties each zone to the model's own parts, so it " +
            "follows the character as it moves.");
        Button? stopDetecting = null;
        if (busy && detectTouchZones is { } running)
        {
            // Stop only stops asking: the zones found until then stay.
            stopDetecting = PageButton("Stop", () =>
            {
                try { running.Cancel(); }
                catch (ObjectDisposedException) { }
            }, id: "TouchZonesStop");
            AutomationProperties.SetHelpText(stopDetecting, "Stops finding zones. The zones found until then are kept.");
        }
        stack.Add(Row(detect, stopDetecting));
        if (sight.Off is { } why)
        {
            var off = Warning(why);
            AutomationProperties.SetAutomationId(off, "TouchZonesDetectNote");
            stack.Add(off);
        }
        if (characterTouchZones.Sent is { } sent)
        {
            var seen = Note(sent.Describe(), new Thickness(0, 4, 0, 4));
            AutomationProperties.SetAutomationId(seen, "TouchZonesSent");
            stack.Add(seen);
            if (characterTouchZones.SentFolder is { } sentFolder)
            {
                var open = PageButton("Open the pictures", () => OpenTouchZonePictures(sentFolder), id: "TouchZonesSentOpen");
                AutomationProperties.SetHelpText(open, "Opens the folder with every picture Thinking saw in the last detection.");
                stack.Add(Row(open));
            }
        }
        if (catalog is null) return Card([.. stack]);

        var modelId = catalog.Inventory.ModelId;
        var intimate = new CheckBox
        {
            Content = $"Include intimate zones ({CharacterTouchZones.IntimateParts})", IsChecked = settings?.IncludeIntimate != false,
            Margin = new Thickness(0, 4, 0, 4)
        };
        AutomationProperties.SetAutomationId(intimate, "TouchZonesIntimate");
        stack.Add(intimate);

        var rows = new List<ZoneRow>();
        var autoSave = new AutoSave(async () =>
        {
            if (characterTouchZones.ModelId != modelId) return true;
            var next = (characterTouchZones.Current ?? new CharacterTouchZoneSettings { ModelId = modelId, DetectedBy = CharacterTouchZoneSettings.ByOwner }) with
            {
                IncludeIntimate = intimate.IsChecked == true,
                Zones = rows.Where(r => !r.Deleted).Select(r => r.Read()).ToArray()
            };
            // Bound again to the model: each area takes what its box holds now, and a zone on a tail follows all of it.
            var (why, reshaped) = await characterTouchZones.SaveEditedAsync(next, lifetime.Token);
            saveState.Text = why is null ? "All changes saved." : "Not saved: " + why;
            saveState.SetResourceReference(TextBlock.ForegroundProperty, why is null ? "MutedBrush" : "WarningBrush");
            if (why is null) { tabEdited = false; status.Text = TouchZonesStatusText(characterTouchZones.Current); }
            // Binding changed a zone's areas (a box moved onto a tail now follows all of it): the page shows them, once the box
            // isn't being dragged.
            if (why is null && reshaped && Mouse.LeftButton != MouseButtonState.Pressed && openTab == CompanionTab.Touch) RenderTab();
            return true;
        });
        void Edited()
        {
            tabEdited = true;
            saveState.Text = "Saving...";
            autoSave.Changed();
        }
        intimate.Checked += (_, _) => Edited();
        intimate.Unchecked += (_, _) => Edited();

        // The snapshot with each zone as a colored, labeled box; drag a box to move it, its corner to resize it. It shows in a frame
        // that zooms (ZoomFrame): zoomed in, the picture grows while the boxes' lines, labels and corners keep their size, so a drag
        // moves a box by smaller steps.
        var canvas = new SeenCanvas { ClipToBounds = true };
        AutomationProperties.SetAutomationId(canvas, "TouchZonesPicture");
        AutomationProperties.SetName(canvas, "Touch zones on the character's picture");
        ZoomFrame? frame = null;
        if (characterTouchZones.SnapshotPath is { } picture)
        {
            try
            {
                var bitmap = PictureAt(picture);
                var scale = Math.Min(TouchZonesPictureHeight / bitmap.PixelHeight, TouchZonesPictureWidth / bitmap.PixelWidth);
                // A page opened again, or another model's picture, shows the whole picture.
                if (openingTab || touchZonesViewModel != modelId) (touchZonesZoom, touchZonesCenter, touchZonesViewModel) = (1, new Point(0.5, 0.5), modelId);
                var map = new ZoomFrame(canvas, bitmap.PixelWidth * scale, bitmap.PixelHeight * scale, touchZonesZoom, touchZonesCenter)
                {
                    Margin = new Thickness(0, 8, 0, 8)
                };
                AutomationProperties.SetAutomationId(map, "TouchZonesMap");
                AutomationProperties.SetName(map, "Zone map");
                AutomationProperties.SetHelpText(map, TouchZonesZoomHelp);
                var image = new Image { Width = map.PictureWidth, Height = map.PictureHeight, Stretch = Stretch.Fill };
                canvas.Children.Add(image);
                canvas.Background = new SolidColorBrush(Color.FromArgb(0x18, 0x80, 0x80, 0x80));
                // The whole character as Thinking saw it (the same framing, on its backdrop with the grid) shows instead when asked.
                // At 1x a picture is decoded at about the size it shows; zoomed in, at its own size, so it stays sharp.
                var seenPath = characterTouchZones.SentWholePicture;
                void ShowPicture()
                {
                    var path = showTouchZonesSent && seenPath is not null ? seenPath : picture;
                    try { image.Source = map.Zoom > 1 ? PictureAt(path, decodeHeight: 0) : PictureAt(path); }
                    catch (Exception error) when (error is IOException or NotSupportedException or UriFormatException or InvalidOperationException)
                    {
                        image.Source = bitmap;
                    }
                }
                ShowPicture();
                if (seenPath is not null)
                {
                    var view = new CheckBox
                    {
                        Content = "Show the picture Thinking saw (on a plain backdrop, with its grid)", IsChecked = showTouchZonesSent,
                        Margin = new Thickness(0, 8, 0, 0)
                    };
                    AutomationProperties.SetAutomationId(view, "TouchZonesSentView");
                    view.Checked += (_, _) => { showTouchZonesSent = true; ShowPicture(); };
                    view.Unchecked += (_, _) => { showTouchZonesSent = false; ShowPicture(); };
                    stack.Add(view);
                }
                stack.Add(TouchZonesZoomRow(map, () =>
                {
                    image.Width = map.PictureWidth;
                    image.Height = map.PictureHeight;
                    ShowPicture();
                    foreach (var row in rows) row.Place();
                }));
                stack.Add(map);
                frame = map;
            }
            catch (Exception error) when (error is IOException or NotSupportedException or UriFormatException or InvalidOperationException) { frame = null; }
        }
        if (frame is not null)
        {
            // The zones' areas over the character as it moves (a tail's swing with it), for this session.
            var onCharacter = new CheckBox
            {
                Content = "Show the zones on the character", IsChecked = showZonesOnCharacter, Margin = new Thickness(0, 0, 0, 8)
            };
            AutomationProperties.SetAutomationId(onCharacter, "TouchZonesShowOnCharacter");
            AutomationProperties.SetHelpText(onCharacter, "Draws each area of the zones in use over your character as it moves, in its zone's color. " +
                "An area that follows the model's own parts, such as a tail's, moves with them. Only until Martlet closes.");
            onCharacter.Checked += (_, _) => { showZonesOnCharacter = true; SendTouchZoneView(); };
            onCharacter.Unchecked += (_, _) => { showZonesOnCharacter = false; SendTouchZoneView(); };
            stack.Add(onCharacter);
        }

        var reactionItems = new List<(string Id, string Label)>();
        foreach (var (source, action) in catalog.Entries.Where(e => e.Action.Enabled))
            reactionItems.Add((source.Id, $"{source.Name}  \u00b7  " + source.Kind switch
            {
                CharacterActionKind.Expression => "emote", CharacterActionKind.Motion => "motion", _ => "gesture"
            }));
        var showing = avatar.IsShowing && characterActions.For(avatar.InspectedProfile?.ModelPath) is not null;
        // The zones' rows, then the Add zone note and controls; on a page just opened they join a batch at a time (their boxes show
        // on the picture at once).
        var list = new StackPanel();
        stack.Add(list);
        var index = 0;
        foreach (var zone in (settings?.Zones ?? []).Take(CharacterTouchZones.MaximumZones))
        {
            var row = new ZoneRow(this, zone, index++, catalog, reactionItems, settings!, showing, Edited, temperament);
            rows.Add(row);
            AddRow(list, row.View);
            if (frame is not null) row.Draw(canvas, frame, ZoneColors[(row.Number) % ZoneColors.Length]);
        }

        // Add a zone Detect zones doesn't look for (or missed): it starts in the middle of the picture, or of the part the zoomed-in
        // map shows; move it into place, or press Detect again and the Thinking model places it too.
        var addNote = Note($"Detect zones looks for the {TouchZoneDetection.DefaultParts}, and for anything special to this character, such as " +
            $"{TouchZoneDetection.SpecialExamples}. Add any other zone here: it starts in the middle " +
            "of the picture (zoomed in, of the part you see). Move it into place, or press Detect again and your Thinking model places it too.",
            new Thickness(0, 16, 0, 0));
        AutomationProperties.SetAutomationId(addNote, "TouchZonesAddNote");
        AddRow(list, addNote);
        var missing = CharacterTouchZones.Kinds.Where(k => settings?.Zones.Any(z => z.Id == k.Id) != true).ToArray();
        if (missing.Length > 0)
        {
            var kinds = Compact(new ComboBox { ItemsSource = missing.Select(k => k.Label).ToArray(), SelectedIndex = 0, MinWidth = 180 });
            AutomationProperties.SetName(kinds, "Zone to add");
            AutomationProperties.SetAutomationId(kinds, "TouchZonesAddKind");
            var add = Compact(PageButton("Add zone", () =>
            {
                var kind = missing[Math.Max(0, kinds.SelectedIndex)];
                var current = characterTouchZones.Current ?? new CharacterTouchZoneSettings { ModelId = modelId, DetectedBy = CharacterTouchZoneSettings.ByOwner };
                var added = current with
                {
                    IncludeIntimate = intimate.IsChecked == true,
                    Zones = [.. rows.Where(r => !r.Deleted).Select(r => r.Read()),
                        new CharacterTouchZone { Id = kind.Id, Box = frame is { } map ? AddedZoneBox(map.Zoom, map.Center) : AddedZoneBox(1, default), Added = true }]
                };
                SaveAndRender(added);
            }, id: "TouchZonesAdd"));
            AutomationProperties.SetHelpText(add, "Adds the zone in the middle of the picture, or of the part you see when it is zoomed in. Detect again " +
                "looks for it too, and keeps it where it is when it can't find it.");
            var addRow = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) };
            addRow.Children.Add(kinds);
            add.Margin = new Thickness(8, 0, 0, 0);
            addRow.Children.Add(add);
            AddRow(list, addRow);
        }
        stack.Add(TouchZonesResetSection(modelId, autoSave));
        var card = Card([.. stack]);
        card.Unloaded += (_, _) => { if (autoSave.Pending) autoSave.SaveNowAsync().Forget(); };
        return card;

        async void SaveAndRender(CharacterTouchZoneSettings next)
        {
            var (why, _) = await characterTouchZones.SaveEditedAsync(next, lifetime.Token);
            tabEdited = false;
            if (why is not null) saveState.Text = "Not saved: " + why;
            else if (openTab == CompanionTab.Touch) RenderTab();
        }
    }

    private const string TouchZonesZoomHelp = "Zoom in to move and resize the boxes precisely. Ctrl+wheel over the picture zooms where " +
        "the pointer is. Zoomed in, drag the picture (not a box) or scroll to look around it; Ctrl+drag or a middle-button drag moves it " +
        "from anywhere.";

    /// <summary>The zone map's zoom: Zoom in, Zoom out and Reset zoom, how far it is zoomed in (TouchZonesZoom: "Zoom 2x") and how to
    /// zoom and look around with the mouse. <paramref name="zoomed"/> places the picture and its boxes again at each zoom, and the
    /// page keeps the zoom and the part of the picture shown while it is drawn again.</summary>
    private StackPanel TouchZonesZoomRow(ZoomFrame map, Action zoomed)
    {
        var zoomIn = Compact(PageButton("Zoom in", () => map.ZoomIn(), id: "TouchZonesZoomIn"));
        var zoomOut = Compact(PageButton("Zoom out", () => map.ZoomOut(), id: "TouchZonesZoomOut"));
        var reset = Compact(PageButton("Reset zoom", () => map.ZoomTo(1), id: "TouchZonesZoomReset"));
        AutomationProperties.SetHelpText(reset, "Shows the whole picture again.");
        var level = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 0, 6) };
        level.SetResourceReference(StyleProperty, "Muted");
        AutomationProperties.SetAutomationId(level, "TouchZonesZoom");
        AutomationProperties.SetLiveSetting(level, AutomationLiveSetting.Polite);
        void Show()
        {
            level.Text = $"Zoom {map.Zoom.ToString("0.#", CultureInfo.CurrentCulture)}x";
            zoomIn.IsEnabled = map.CanZoomIn;
            zoomOut.IsEnabled = reset.IsEnabled = map.CanZoomOut;
        }
        Show();
        map.Zoomed += () => { zoomed(); Show(); };
        map.ViewChanged += () => (touchZonesZoom, touchZonesCenter) = (map.Zoom, map.Center);
        var buttons = Row(zoomIn, zoomOut, reset);
        buttons.Children.Add(level);
        var row = new StackPanel();
        row.Children.Add(buttons);
        row.Children.Add(Note(TouchZonesZoomHelp, new Thickness(0)));
        return row;
    }

    /// <summary>Where Add zone puts a zone: a fifth of the picture each way in its middle or, zoomed in to <paramref name="zoom"/>, in
    /// the middle of the part the map shows (<paramref name="center"/>, fractions of the picture) and as large on the screen as at 1x.</summary>
    internal static TouchZoneBox AddedZoneBox(double zoom, Point center)
    {
        if (!(zoom > 1)) return new(0.4, 0.4, 0.2, 0.2);
        var size = 0.2 / zoom;
        return new(Math.Clamp(center.X - size / 2, 0, 1 - size), Math.Clamp(center.Y - size / 2, 0, 1 - size), size, size);
    }

    private static string TouchZonesStatusText(CharacterTouchZoneSettings? settings) =>
        settings is null || settings.Zones.Count == 0
            ? "No zones found yet for this model. Until then a click reacts to the rough part (head, face, body, arm, hand, leg)."
            : $"{settings.Zones.Count} zone{(settings.Zones.Count == 1 ? "" : "s")}, {settings.Zones.Count(settings.Active)} in use" +
              (settings.DetectedBy == CharacterTouchZoneSettings.ByVision && settings.DetectedAt is { } at
                ? $". Found by the Thinking model on {at.ToLocalTime().ToString("g", CultureInfo.CurrentCulture)}."
                : settings.DetectedBy == CharacterTouchZoneSettings.ByEstimate
                    ? ". A first guess Martlet placed from the character's own parts and shape, with no AI. Detect zones has your Thinking model find them."
                    : ". Made by you.");

    private void TryTouchZone(CharacterTouchZone zone, CharacterActionCatalog catalog)
    {
        var reaction = TouchPlan(zone, catalog, characterTemperaments.For(homeSettings?.Companion?.ActivePersonaId), 1);
        var plan = reaction.Actions;
        for (var i = 0; i < plan.Count; i++) PlayTouchAsync(plan[i], $"a try of {zone.Name.ToLowerInvariant()}", i == 0 ? reaction.LingerSeconds : 0).Forget();
        if (reaction.LookSeconds > 0) avatar.Gaze.Attend(reaction.LookSeconds, $"a try of {zone.Name.ToLowerInvariant()}");
        characterTouchZones.Note($"Tried {zone.Name}: " + (plan.Count == 0 ? "nothing to play on this model" : "played " + string.Join(", ", plan.Select(s => s.Name))) +
                CharacterTouchZoneService.Describe(reaction, 1) + "." +
                (zone.Reaction.Notices ? " Martlet notices touches here" + (CharacterTouchZones.Narration(zone) is { } line ? $" (your words: \"{line}\")." : ".") : ""));
    }

    // The picture and each zone's box on it take part in UI Automation (a Canvas and a Border have no automation peer of their
    // own), so Martlet's MCP sees the boxes (TouchZoneRect-<n>, with their bounds) on the picture (TouchZonesPicture).
    private sealed class SeenCanvas : Canvas
    {
        protected override System.Windows.Automation.Peers.AutomationPeer OnCreateAutomationPeer() =>
            new System.Windows.Automation.Peers.FrameworkElementAutomationPeer(this);
    }

    private sealed class SeenBorder : Border
    {
        protected override System.Windows.Automation.Peers.AutomationPeer OnCreateAutomationPeer() =>
            new System.Windows.Automation.Peers.FrameworkElementAutomationPeer(this);
    }

    /// <summary>One zone's row, compact: on, name and what it is (its parts, how the persona feels about it), Try and More; its
    /// reaction list, exactly what plays, in order (each entry a chip: ‹ plays it earlier, × removes it; Add a reaction adds one
    /// at the end, Defaults fills the list again with what a new zone gets); then how long it rests and Martlet notices with the
    /// owner's optional words (shown while Martlet notices the zone). More shows the zone's box and Delete. A zone has one box, or
    /// several areas (separated by | in its box field); Add area and Remove area add one or take the last away. A zone that
    /// follows the model's own part (a tail) shows that part's areas: moving one of them places the zone there again, and Martlet
    /// finds what of the model it follows from there. Its areas show on the picture.</summary>
    private sealed class ZoneRow
    {
        private const string AddChoice = "Add a reaction...";
        private readonly CharacterTouchZone zone;
        private readonly CheckBox on, notices;
        private readonly TextBox name, narration, rest, box;
        private readonly ComboBox add;
        private readonly WrapPanel plays = new() { VerticalAlignment = VerticalAlignment.Center };
        private readonly List<string> reactions;
        private readonly Button? addArea, removeArea;
        private readonly IReadOnlyList<(string Id, string Label)> items;
        private readonly Action edited;
        // The box field as the row showed it first: while it reads the same, the zone keeps its areas exactly as saved.
        private readonly string shown;
        // Its areas follow the model's own part; the area the owner moved last places the zone again.
        private readonly bool follows;
        private int moved = -1;
        internal int Number { get; }
        internal bool Deleted { get; private set; }
        internal StackPanel View { get; } = new() { Margin = new Thickness(0, 12, 0, 0) };

        internal ZoneRow(MainWindow window, CharacterTouchZone zone, int number, CharacterActionCatalog catalog,
            IReadOnlyList<(string Id, string Label)> items, CharacterTouchZoneSettings settings, bool showing, Action edited,
            CharacterTouchTemperament? temperament)
        {
            this.zone = zone;
            this.items = items;
            this.edited = edited;
            follows = zone.Follows is not null;
            Number = number;
            var kind = CharacterTouchZones.Kind(zone.Id);
            on = RowSwitch(zone.Enabled);
            AutomationProperties.SetName(on, $"Use {zone.Name}");
            AutomationProperties.SetAutomationId(on, $"TouchZoneOn-{number}");
            name = Compact(new TextBox { Text = zone.Name, Width = 150, MaxLength = CharacterTouchZones.MaximumLabelLength });
            AutomationProperties.SetName(name, $"Name of {zone.Name}");
            AutomationProperties.SetAutomationId(name, $"TouchZoneName-{number}");
            var state = new TextBlock
            {
                Text = Describe(zone, settings, CharacterTouchTemperaments.Attitude(temperament, zone.Id)),
                VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 8, 0), FontSize = 13, TextWrapping = TextWrapping.Wrap
            };
            state.SetResourceReference(StyleProperty, "Muted");
            AutomationProperties.SetAutomationId(state, $"TouchZoneState-{number}");
            var tryIt = Compact(PageButton("Try", () => window.TryTouchZone(Read(), catalog), id: $"TouchZoneTry-{number}"));
            tryIt.IsEnabled = showing;
            AutomationProperties.SetHelpText(tryIt, "Plays this zone's reaction list on the character.");
            // More shows the box and Delete; the page keeps it open while it is drawn again.
            var open = window.openTouchZoneRows.Contains(zone.Id);
            var more = Compact(PageButton(open ? "Less" : "More", () => { }, id: $"TouchZoneMore-{number}"));
            more.Margin = new Thickness(6, 0, 0, 0);
            AutomationProperties.SetHelpText(more, "Shows or hides the zone's box and Delete.");
            var delete = Compact(PageButton("Delete", () =>
            {
                Deleted = true;
                View.Visibility = Visibility.Collapsed;
                foreach (var (rectangle, label) in drawn)
                {
                    rectangle.Visibility = Visibility.Collapsed;
                    if (label is not null) label.Visibility = Visibility.Collapsed;
                }
                edited();
            }, id: $"TouchZoneDelete-{number}"));
            AutomationProperties.SetHelpText(delete, "Deletes this zone. Detect again finds it again when it is one of the zones it looks for.");
            // Level with the name, also when a narrow window puts the note under it.
            tryIt.VerticalAlignment = more.VerticalAlignment = VerticalAlignment.Top;
            // The note sits beside the name, or under it when the window is too narrow for both.
            var named = new StackPanel { Orientation = Orientation.Horizontal };
            named.Children.Add(on);
            named.Children.Add(name);
            var title = new FillWrapPanel { FillMinimum = 160, FillIndent = RowIndent - 10 };
            title.Children.Add(named);
            title.Children.Add(state);
            var header = new DockPanel();
            DockPanel.SetDock(more, Dock.Right);
            DockPanel.SetDock(tryIt, Dock.Right);
            header.Children.Add(more);
            header.Children.Add(tryIt);
            header.Children.Add(title);

            // The reaction list: exactly what plays, in order. A zone not filled yet shows what it plays now, and saves it so.
            reactions = [.. CharacterTouchZones.List(zone, catalog, temperament)];
            add = Compact(new ComboBox { ItemsSource = new[] { AddChoice }.Concat(items.Select(i => i.Label)).ToArray(), SelectedIndex = 0, Width = 170 });
            AutomationProperties.SetName(add, $"Add a reaction to {zone.Name}");
            AutomationProperties.SetAutomationId(add, $"TouchZoneReactionAdd-{number}");
            AutomationProperties.SetHelpText(add, $"Adds an emote, gesture or motion at the end of the list. A zone plays up to {CharacterTouchZones.MaximumActions}.");
            add.Margin = new Thickness(0, 4, 6, 0);
            add.SelectionChanged += (_, _) =>
            {
                if (add.SelectedIndex <= 0) return;
                var id = items[add.SelectedIndex - 1].Id;
                add.SelectedIndex = 0;
                if (reactions.Count >= CharacterTouchZones.MaximumActions || reactions.Contains(id, StringComparer.Ordinal)) return;
                reactions.Add(id);
                ShowReactions();
                edited();
            };
            var defaults = PageButton("Defaults", () =>
            {
                reactions.Clear();
                reactions.AddRange(CharacterTouchZones.DefaultReactions(zone, catalog, temperament));
                ShowReactions();
                edited();
            }, link: true, id: $"TouchZoneReactionDefaults-{number}");
            defaults.VerticalAlignment = VerticalAlignment.Center;
            defaults.Margin = new Thickness(4, 4, 0, 0);
            AutomationProperties.SetHelpText(defaults, "Fills the list again with what a new zone gets: the persona's touch temperament for this part, " +
                "else the zone's built-in reactions (and the model's own tap motion).");
            addPanel = new StackPanel { Orientation = Orientation.Horizontal };
            addPanel.Children.Add(add);
            addPanel.Children.Add(defaults);
            ShowReactions();
            var list = new DockPanel { Margin = new Thickness(RowIndent, 6, 0, 0) };
            var playsLabel = RowLabel("Plays", add, width: 44);
            playsLabel.VerticalAlignment = VerticalAlignment.Top;
            playsLabel.Margin = new Thickness(0, 6, 0, 0);
            DockPanel.SetDock(playsLabel, Dock.Left);
            list.Children.Add(playsLabel);
            list.Children.Add(plays);

            notices = new CheckBox
            {
                Content = "Martlet notices", IsChecked = zone.Reaction.Notices, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 6)
            };
            AutomationProperties.SetAutomationId(notices, $"TouchZoneNotices-{number}");
            AutomationProperties.SetHelpText(notices, "Touches here go to your Thinking model: with what you say next, or in a short reply of their own.");
            narration = Compact(new TextBox
            {
                Text = zone.Reaction.Narration is { } own && own != kind?.Narration ? own : "", MinWidth = 150,
                MaxLength = CharacterTouchZones.MaximumNarrationLength
            });
            AutomationProperties.SetName(narration, $"Your own words for touching {zone.Name} (optional hint)");
            AutomationProperties.SetAutomationId(narration, $"TouchZoneNarration-{number}");
            // Only a zone Martlet notices sends the owner's words, so the box shows only then.
            var words = WithHint(narration, "Your own words for the touch (optional)");
            words.Margin = new Thickness(0, 0, 0, 6);
            words.Visibility = zone.Reaction.Notices ? Visibility.Visible : Visibility.Collapsed;
            rest = Compact(new TextBox { Text = zone.Reaction.CooldownSeconds.ToString("0.#", CultureInfo.CurrentCulture), Width = 48 });
            AutomationProperties.SetName(rest, $"Seconds {zone.Name} rests after a touch");
            AutomationProperties.SetAutomationId(rest, $"TouchZoneCooldown-{number}");
            shown = BoxesText(zone.AllAreas.Select(a => a.Box));
            // Wide enough for four numbers with two decimals, as a drag on the zoomed-in picture writes them.
            box = Compact(new TextBox { Text = shown, MinWidth = 160, MaxWidth = 420 });
            AutomationProperties.SetName(box, $"Box of {zone.Name}: left, top, width, height in percent of the picture, each area after a |");
            AutomationProperties.SetAutomationId(box, $"TouchZoneBox-{number}");
            box.ToolTip = follows
                ? $"Each area of the model's own {zone.Follows}, where it was in the picture. The zone follows them wherever they move; " +
                  "move one onto another part to place the zone there instead."
                : "Left, top, width and height, in percent of the picture; several areas are separated by |. Or drag a box on the picture, " +
                  "or its corner to resize it; zoom the picture in for smaller steps.";
            if (!follows)
            {
                // An area starts beside the last one; move it into place.
                addArea = Compact(PageButton("Add area", () =>
                {
                    if (ParsedBoxes() is not { } boxes || boxes.Count >= CharacterTouchZones.MaximumAreas) return;
                    var last = boxes[^1];
                    var x = last.X + last.Width + 0.01 + Math.Min(last.Width, 0.2) <= 1 ? last.X + last.Width + 0.01 : Math.Max(0, last.X - Math.Min(last.Width, 0.2) - 0.01);
                    box.Text = BoxesText([.. boxes, new TouchZoneBox(x, last.Y, Math.Min(last.Width, 0.2), Math.Min(last.Height, 0.2)).Clamped()]);
                }, id: $"TouchZoneAddArea-{number}"));
                AutomationProperties.SetHelpText(addArea, "Adds another box to this zone, beside its last one. A touch in any of its boxes is a touch on the zone.");
                removeArea = Compact(PageButton("Remove area", () =>
                {
                    if (ParsedBoxes() is { Count: > 1 } boxes) box.Text = BoxesText(boxes.Take(boxes.Count - 1));
                }, id: $"TouchZoneRemoveArea-{number}"));
                AutomationProperties.SetHelpText(removeArea, "Takes this zone's last box away.");
                removeArea.Margin = new Thickness(6, 0, 0, 0);
            }

            on.Checked += (_, _) => edited();
            on.Unchecked += (_, _) => edited();
            name.TextChanged += (_, _) => edited();
            notices.Checked += (_, _) => { words.Visibility = Visibility.Visible; edited(); };
            notices.Unchecked += (_, _) => { words.Visibility = Visibility.Collapsed; edited(); };
            narration.TextChanged += (_, _) => edited();
            rest.TextChanged += (_, _) => edited();
            box.TextChanged += (_, _) => { Place(); edited(); };

            // Under the list: how long it rests and Martlet notices; the owner's words take the rest of the line, or a line of
            // their own.
            var fields = new FillWrapPanel { Margin = new Thickness(RowIndent, 6, 0, 0), FillMinimum = 200 };
            var rests = RowGroup(RowLabel("Rests", rest, width: 44), rest, RowLabel("seconds", rest, 6, 0));
            fields.Children.Add(rests);
            fields.Children.Add(notices);
            fields.Children.Add(words);
            // More: the box and Delete, out of the way until wanted.
            var details = new WrapPanel { Margin = new Thickness(RowIndent, 0, 0, 0), Visibility = open ? Visibility.Visible : Visibility.Collapsed };
            details.Children.Add(addArea is null ? RowGroup(RowLabel("Box", box, width: 44), box)
                : RowGroup(RowLabel("Box", box, width: 44), box, Spaced(addArea), removeArea!));
            details.Children.Add(RowGroup(delete));
            more.Click += (_, _) =>
            {
                var opened = details.Visibility != Visibility.Visible;
                details.Visibility = opened ? Visibility.Visible : Visibility.Collapsed;
                more.Content = opened ? "Less" : "More";
                if (opened) window.openTouchZoneRows.Add(zone.Id);
                else window.openTouchZoneRows.Remove(zone.Id);
            };
            View.Children.Add(header);
            View.Children.Add(list);
            View.Children.Add(fields);
            View.Children.Add(details);
            ShowAreaButtons();
        }

        // Add a reaction and Defaults, after the list's last chip.
        private readonly StackPanel addPanel;

        /// <summary>Shows the reaction list as chips, in the order they play: each with ‹ (play it earlier; not on the first) and
        /// × (remove it), then Add a reaction (off while the list is full) and Defaults. An entry the model doesn't have (or a voice
        /// sound) shows by its name and stays.</summary>
        private void ShowReactions()
        {
            plays.Children.Clear();
            if (reactions.Count == 0)
            {
                var none = new TextBlock { Text = "nothing", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 4, 8, 0) };
                none.SetResourceReference(StyleProperty, "Muted");
                AutomationProperties.SetAutomationId(none, $"TouchZoneReactionNone-{Number}");
                plays.Children.Add(none);
            }
            for (var k = 0; k < reactions.Count; k++)
            {
                var index = k;
                var label = Label(reactions[k]);
                var text = new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center };
                text.SetResourceReference(StyleProperty, "ChipText");
                if (!Known(reactions[k])) text.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
                AutomationProperties.SetAutomationId(text, $"TouchZoneReactionItem-{Number}-{k + 1}");
                var content = new StackPanel { Orientation = Orientation.Horizontal };
                if (k > 0)
                {
                    var earlier = ChipButton("\u2039", $"Play {label} earlier", $"TouchZoneReactionEarlier-{Number}-{k + 1}", () =>
                    {
                        (reactions[index - 1], reactions[index]) = (reactions[index], reactions[index - 1]);
                        ShowReactions();
                        edited();
                    });
                    earlier.Padding = new Thickness(0, 0, 6, 0);
                    content.Children.Add(earlier);
                }
                content.Children.Add(text);
                content.Children.Add(ChipButton("\u00d7", $"Remove {label}", $"TouchZoneReactionRemove-{Number}-{k + 1}", () =>
                {
                    reactions.RemoveAt(index);
                    ShowReactions();
                    edited();
                }));
                var chip = new Border { Child = content, VerticalAlignment = VerticalAlignment.Center };
                chip.SetResourceReference(StyleProperty, "Chip");
                plays.Children.Add(chip);
            }
            add.IsEnabled = reactions.Count < CharacterTouchZones.MaximumActions;
            plays.Children.Add(addPanel);
        }

        private static Button ChipButton(string content, string name, string id, Action run)
        {
            var button = new Button { Content = content, Padding = new Thickness(6, 0, 0, 0), Margin = new Thickness(0), ToolTip = name };
            button.SetResourceReference(StyleProperty, "LinkButton");
            AutomationProperties.SetName(button, name);
            AutomationProperties.SetAutomationId(button, id);
            button.Click += (_, _) => run();
            return button;
        }

        // Whether the model has the entry in use (a voice sound counts: the voice plays it).
        private bool Known(string entry) => CharacterTouchReaction.IsSound(entry) || items.Any(i => i.Id == entry);

        // "Blush  ·  emote" as Add a reaction names it; a voice sound "laugh  ·  sound"; an entry the model doesn't have (or has
        // turned off) by its name, "F05  ·  not on this model".
        private string Label(string entry) =>
            items.FirstOrDefault(i => i.Id == entry).Label ??
            (CharacterTouchReaction.IsSound(entry) ? $"{entry[CharacterTouchReaction.SoundPrefix.Length..]}  \u00b7  sound"
                : $"{(entry.IndexOf(':') is var colon and >= 0 ? entry[(colon + 1)..] : entry)}  \u00b7  not on this model");

        private static Button Spaced(Button button)
        {
            button.Margin = new Thickness(8, 0, 0, 0);
            return button;
        }

        // "top_of_head  ·  3 parts  ·  added by you  ·  loves it".
        private static string Describe(CharacterTouchZone zone, CharacterTouchZoneSettings settings, string? attitude)
        {
            var kind = CharacterTouchZones.Kind(zone.Id);
            var parts = zone.Drawables.Count > 0 ? $"{zone.Drawables.Count} part{(zone.Drawables.Count == 1 ? "" : "s")}"
                : zone.Bones.Count > 0 ? string.Join(", ", zone.Bones.Take(3)) : "box only";
            var areas = zone.AllAreas.Count;
            var shape = zone.Follows is { } part ? $"follows the model's own {part} wherever it moves: {parts} in {areas} area{(areas == 1 ? "" : "s")}"
                : areas > 1 ? $"{areas} areas, {parts}" : parts;
            return $"{zone.Id}  \u00b7  {shape}" + (zone.Added ? "  \u00b7  added by you" : TouchZoneDetection.IsSpecial(zone.Id) ? "  \u00b7  special to this character" : "") +
                (kind?.Intimate == true && !settings.IncludeIntimate ? "  \u00b7  intimate, off" : "") +
                (attitude is null ? "" : $"  \u00b7  {attitude} it");
        }

        // A tenth of a percent at 1x; a hundredth once the zoomed-in picture is over a thousand pixels, so a drag's steps stay under a
        // pixel.
        private string BoxText(TouchZoneBox b) =>
            string.Join(", ", new[] { b.X, b.Y, b.Width, b.Height }.Select(v => (v * 100).ToString(
                frame is { } map && Math.Max(map.PictureWidth, map.PictureHeight) > 1000 ? "0.##" : "0.#", CultureInfo.CurrentCulture)));

        private string BoxesText(IEnumerable<TouchZoneBox> boxes) => string.Join(" | ", boxes.Select(BoxText));

        private static TouchZoneBox? ParsedBox(string text)
        {
            var values = text.Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries)
                .Select(t => double.TryParse(t, NumberStyles.Float, CultureInfo.CurrentCulture, out var v) ? v / 100 : double.NaN).ToArray();
            if (values.Length != 4 || values.Any(v => !double.IsFinite(v))) return null;
            var parsed = new TouchZoneBox(values[0], values[1], values[2], values[3]);
            return parsed.Valid ? parsed : null;
        }

        /// <summary>Each area's box as the field reads now (one to <see cref="CharacterTouchZones.MaximumAreas"/>), or null when one of
        /// them can't be read.</summary>
        private List<TouchZoneBox>? ParsedBoxes()
        {
            var parts = box.Text.Split('|', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length is 0 or > CharacterTouchZones.MaximumAreas) return null;
            var boxes = parts.Select(ParsedBox).ToList();
            return boxes.All(b => b is not null) ? [.. boxes.Select(b => b!)] : null;
        }

        /// <summary>The zone as the row shows it now (an unreadable box or rest keeps the saved one). A zone that follows the model's
        /// own part keeps its areas until the owner moves one: the zone is then that one box, and saving finds what it follows
        /// from there.</summary>
        internal CharacterTouchZone Read()
        {
            IReadOnlyList<string> actions = [.. reactions];
            var given = name.Text.Trim();
            var line = narration.Text.Trim();
            var shaped = zone;
            if (box.Text != shown && ParsedBoxes() is { } boxes)
            {
                if (follows)
                {
                    var place = boxes[moved >= 0 && moved < boxes.Count ? moved : 0];
                    shaped = zone with { Box = place, Areas = null, Follows = null, Drawables = [], Bones = [] };
                }
                else if (boxes.Count == 1) shaped = zone with { Box = boxes[0], Areas = null };
                else shaped = CharacterTouchZones.Compose(zone, [.. boxes.Select((b, i) =>
                    (zone.Areas is { } had && i < had.Count ? had[i] : new() { Box = b }) with { Box = b, FromModel = false })], null);
            }
            return shaped with
            {
                Enabled = on.IsChecked == true,
                Label = given.Length == 0 || given == CharacterTouchZones.Kind(zone.Id)?.Label ? null : given,
                Reaction = new()
                {
                    Actions = actions, Notices = notices.IsChecked == true,
                    Narration = line.Length == 0 || line == CharacterTouchZones.Kind(zone.Id)?.Narration ? null : line,
                    CooldownSeconds = double.TryParse(rest.Text, NumberStyles.Float, CultureInfo.CurrentCulture, out var seconds) &&
                        seconds is >= 0 and <= CharacterTouchReaction.MaximumCooldown ? seconds : zone.Reaction.CooldownSeconds
                }
            };
        }

        // Each area's box on the picture, and its name tag (on the first area only).
        private readonly List<(Border Rectangle, TextBlock? Label)> drawn = [];
        private Canvas? picture;
        private Color color;
        private ZoomFrame? frame;

        /// <summary>Draws the zone's areas on the picture in <paramref name="map"/>; dragging one moves it, dragging its corner
        /// resizes it. Zoomed in, the same drag moves it by smaller steps.</summary>
        internal void Draw(Canvas canvas, ZoomFrame map, Color color)
        {
            (picture, frame, this.color) = (canvas, map, color);
            Place();
        }

        // One area's box on the picture: the first holds the zone's name; an area that follows the model's own part is drawn lighter.
        private (Border Rectangle, TextBlock? Label) AreaBox(int index)
        {
            var grip = new Rectangle
            {
                Width = 8, Height = 8, Fill = new SolidColorBrush(color), HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Bottom, Cursor = Cursors.SizeNWSE
            };
            var inside = new Grid();
            inside.Children.Add(grip);
            var areas = zone.AllAreas.Count;
            var rectangle = new SeenBorder
            {
                BorderBrush = new SolidColorBrush(color), BorderThickness = new Thickness(follows ? 1 : 1.5), Child = inside, Cursor = Cursors.SizeAll,
                Background = new SolidColorBrush(Color.FromArgb(follows ? (byte)0x1C : (byte)0x30, color.R, color.G, color.B)),
                ToolTip = index == 0 && areas <= 1 ? zone.Name : $"{zone.Name}, area {index + 1}" + (follows ? $" of the model's own {zone.Follows}" : "")
            };
            AutomationProperties.SetAutomationId(rectangle, index == 0 ? $"TouchZoneRect-{Number}" : $"TouchZoneRect-{Number}-{index + 1}");
            AutomationProperties.SetName(rectangle, index == 0 ? zone.Name : $"{zone.Name} {index + 1}");
            TextBlock? label = null;
            if (index == 0)
            {
                label = new TextBlock
                {
                    Text = zone.Name, Foreground = Brushes.White, FontSize = 10, Padding = new Thickness(2, 0, 2, 0), TextWrapping = TextWrapping.NoWrap,
                    Background = new SolidColorBrush(Color.FromArgb(0xC0, color.R, color.G, color.B)), VerticalAlignment = VerticalAlignment.Top,
                    HorizontalAlignment = HorizontalAlignment.Left, IsHitTestVisible = false
                };
            }
            picture!.Children.Add(rectangle);
            if (label is not null) picture.Children.Add(label);
            Point? from = null;
            var resizing = false;
            var start = new TouchZoneBox(0, 0, 0.1, 0.1);
            rectangle.MouseLeftButtonDown += (_, e) =>
            {
                if (ParsedBoxes() is not { } boxes || index >= boxes.Count) return;
                from = e.GetPosition(picture);
                resizing = ReferenceEquals(e.OriginalSource, grip);
                start = boxes[index];
                rectangle.CaptureMouse();
                e.Handled = true;
            };
            rectangle.MouseMove += (_, e) =>
            {
                if (from is not { } origin || frame is not { } map || ParsedBoxes() is not { } boxes || index >= boxes.Count) return;
                var at = e.GetPosition(picture);
                double dx = (at.X - origin.X) / map.PictureWidth, dy = (at.Y - origin.Y) / map.PictureHeight;
                // At least a fiftieth of the picture each way at 1x; zoomed in, as small as that shows on the screen.
                var least = 0.02 / map.Zoom;
                boxes[index] = resizing
                    ? start with { Width = Within(start.Width + dx, least, 1 - start.X), Height = Within(start.Height + dy, least, 1 - start.Y) }
                    : start with { X = Within(start.X + dx, 0, 1 - start.Width), Y = Within(start.Y + dy, 0, 1 - start.Height) };
                moved = index;
                box.Text = BoxesText(boxes);
            };
            rectangle.MouseLeftButtonUp += (_, e) =>
            {
                from = null;
                rectangle.ReleaseMouseCapture();
                e.Handled = true;
            };
            return (rectangle, label);
        }

        // Math.Clamp that a box typed past the picture's edge can't make throw.
        private static double Within(double value, double least, double most) => Math.Clamp(value, least, Math.Max(least, most));

        /// <summary>Places each area's box on the picture at the frame's zoom, where the field says: it adds the boxes of areas
        /// added and hides those taken away.</summary>
        internal void Place()
        {
            ShowAreaButtons();
            if (picture is null || frame is null || ParsedBoxes() is not { } boxes) return;
            double width = frame.PictureWidth, height = frame.PictureHeight;
            while (drawn.Count < boxes.Count) drawn.Add(AreaBox(drawn.Count));
            for (var i = 0; i < drawn.Count; i++)
            {
                var (rectangle, label) = drawn[i];
                var visible = i < boxes.Count && !Deleted;
                rectangle.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
                if (label is not null) label.Visibility = rectangle.Visibility;
                if (!visible) continue;
                var b = boxes[i];
                Canvas.SetLeft(rectangle, b.X * width);
                Canvas.SetTop(rectangle, b.Y * height);
                rectangle.Width = Math.Max(4, b.Width * width);
                rectangle.Height = Math.Max(4, b.Height * height);
                if (label is null) continue;
                Canvas.SetLeft(label, b.X * width + 1);
                Canvas.SetTop(label, b.Y * height + 1);
            }
        }

        // Add area shows while the zone has room for one more, Remove area while it has more than one.
        private void ShowAreaButtons()
        {
            var count = ParsedBoxes()?.Count ?? 1;
            if (addArea is not null) addArea.Visibility = count < CharacterTouchZones.MaximumAreas ? Visibility.Visible : Visibility.Collapsed;
            if (removeArea is not null) removeArea.Visibility = count > 1 ? Visibility.Visible : Visibility.Collapsed;
        }
    }
}
