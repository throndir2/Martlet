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

/// <summary>Companion › Character › Touch zones: where a left click on the character lands (top of the head, a cheek, a hand...)
/// and what the character does then. The Thinking model finds the zones once per model in a snapshot of the character (when it
/// can see; Detect zones asks again), Martlet binds each to the model's drawables or bones so it follows the model as it moves,
/// and each zone plays its emotes and gestures, may be noticed by Martlet (the touches go to the Thinking model) and rests a few
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
        characterActions.Changed += () => characterTouchZones.Follow(characterActions.Current?.Inventory.ModelId);
        characterTouchZones.Changed += () => Dispatcher.InvokeAsync(() =>
        {
            if (touchZonesLast is not null) touchZonesLast.Text = characterTouchZones.LastMatch ?? TouchZonesIdle();
            if (touchZonesNoticed is not null) touchZonesNoticed.Text = characterTouchZones.Noticed ?? TouchZonesNoticedIdle;
            if (touchZonesNoticedLast is not null) touchZonesNoticedLast.Text = characterTouchZones.NoticedLast ?? "";
            if (closing || openTab != CompanionTab.Character || CompanionContent.IsKeyboardFocusWithin || tabEdited) return;
            if (detectingTouchZones || characterTouchZones.Busy || renderedZonesModel != characterTouchZones.ModelId) RenderTab();
        });
        avatar.TouchRouter = OnCharacterTouched;
        WireCharacterTemperament();
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
        characterTouchZones.React(touch, (zone, repeats) => TouchPlan(zone, catalog, temperament, repeats), PlayTouchAsync,
            zone => Dispatcher.InvokeAsync(() =>
                NoticePhysical(touch.Held ? PhysicalKind.Hold : CharacterTouchZones.Pats(zone) ? PhysicalKind.Pat : PhysicalKind.Tap,
                    CharacterTouchZones.Part(zone), zone.Name.ToLowerInvariant(), hint: CharacterTouchZones.Narration(zone))),
            look: avatar.Gaze.Attend);
        return true;
    }

    /// <summary>What touching <paramref name="zone"/> plays: the owner's choice for the zone, else the active persona's touch
    /// temperament, else by default the model's own tap motion for that part (TapHead, TapBody...) when it has one, then the
    /// zone's default emotes and gestures.</summary>
    private TouchReactionPlan TouchPlan(CharacterTouchZone zone, CharacterActionCatalog? catalog, CharacterTouchTemperament? temperament, int repeats)
    {
        var plan = CharacterTouchZones.React(zone, catalog, temperament, repeats);
        if (plan.From != TouchReactionPlan.FromDefault || catalog is null) return plan;
        var part = CharacterTouchZones.Kind(zone.Id)?.Group == TouchZoneGroup.Head ? zone.Id.StartsWith("hair", StringComparison.Ordinal) ? "hair" : "head" : "body";
        var motions = catalog.Entries.Where(e => e.Action.Enabled && e.Source.Kind == CharacterActionKind.Motion).Select(e => e.Source).ToArray();
        return AvatarController.TouchMotion([.. motions.Select(m => m.Name)], part) is { } group && motions.FirstOrDefault(m => m.Name == group) is { } motion
            ? plan with { Actions = [motion, .. plan.Actions.Where(s => s != motion)] } : plan;
    }

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
        catch (Exception error) when (error is OperationCanceledException or IOException or InvalidOperationException or
            InvalidDataException or TimeoutException or ObjectDisposedException) { }
    }

    private const string TouchZonesNoticedIdle = "Nothing waits for Martlet.";

    /// <summary>Something the user did to the desktop character that Martlet notices (a touch on a zone with Martlet notices on,
    /// a stroke, moving or zooming it...): it goes to the conversation's touch ledger, waits for the next reply and, for touches
    /// (<see cref="PhysicalKinds.StartsTurn"/>), starts a short reply of its own when the user says nothing. On the UI thread.
    /// <paramref name="zone"/> is where, as the character hears it ("the top of your head"), <paramref name="label"/> its short
    /// name for the history ("top of head"), <paramref name="detail"/> more ("to another monitor") and <paramref name="hint"/>
    /// the owner's own words for it.</summary>
    internal void NoticePhysical(PhysicalKind kind, string? zone = null, string? label = null, string? detail = null, string? hint = null,
        IReadOnlyList<string>? zones = null)
    {
        if (closing || Role == DeviceRole.Host || conversation is null || ConversationSession() is not { } talk) return;
        if (!talk.IsVisible) talk.StartInBackground();
        talk.Physical(new PhysicalEvent(kind, conversation.TouchNow, zone, label, detail, hint, zones));
    }

    private CancellationTokenSource? detectTouchZones;
    private bool showTouchZonesSent;
    private const double TouchZonesPictureHeight = 600, TouchZonesPictureWidth = 440;

    // A picture file decoded once at about the size it shows (the snapshot can be 2048 pixels tall).
    private static BitmapImage PictureAt(string path)
    {
        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        // Detect again writes a new picture under the same name.
        bitmap.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
        bitmap.DecodePixelHeight = (int)(TouchZonesPictureHeight * 2);
        bitmap.UriSource = new Uri(path);
        bitmap.EndInit();
        bitmap.Freeze();
        return bitmap;
    }

    private static void OpenTouchZonePictures(string folder)
    {
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"\"{folder}\"") { UseShellExecute = false })?.Dispose(); }
        catch (System.ComponentModel.Win32Exception) { }
    }

    private async Task DetectTouchZonesAsync()
    {
        if (conversation is null || detectingTouchZones) return;
        detectingTouchZones = true;
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        detectTouchZones = stop;
        try
        {
            characterTouchZones.Follow(characterActions.For(avatar.InspectedProfile?.ModelPath)?.Inventory.ModelId);
            // Each step's picture goes to a Thinking pool member that can see, else to the conversation's Thinking model after
            // any reply. MARTLET_TOUCH_ZONES_FIXTURE answers them instead (FIXTURE - NOT AI); see CharacterTouchZoneService.
            var talk = conversation;
            await characterTouchZones.DetectAsync(avatar,
                (purpose, instructions, text, image, token) => talk.AskHelperAsync(HelperJobKind.TouchZones, purpose, instructions, text, image, token),
                stop.Token);
        }
        finally
        {
            detectTouchZones = null;
            detectingTouchZones = false;
            tabEdited = false;
            if (!closing && openTab == CompanionTab.Character) RenderTab();
        }
    }

    private Border CharacterTouchZonesCard()
    {
        var catalog = characterActions.Current;
        characterTouchZones.Follow(catalog?.Inventory.ModelId);
        renderedZonesModel = characterTouchZones.ModelId;
        var settings = characterTouchZones.Current;
        var temperament = characterTemperaments.For(homeSettings?.Companion?.ActivePersonaId);
        var stack = new List<UIElement>
        {
            Heading("Touch zones"),
            Note("Click the character (a click, not a drag) and it reacts to where you touched it: a pat on the head, a poke on " +
                "the cheek, holding its hand. With its position locked, drag across it to stroke it: each part you cross reacts, " +
                "and Martlet hears about it, like your moves and zooms. Detect zones shows your Thinking model pictures of the " +
                "character (never its files) on a plain backdrop with a grid: first the whole character, to find its head, body and " +
                "legs, then a close-up of each, to mark its zones. Then the model checks its own boxes, drawn and numbered on the " +
                "close-up, and corrects them until it says they are right. Between steps Martlet fits each box to the character's " +
                "pixels, puts left and right back the right way round and points out boxes that look wrong. It then ties each zone " +
                "to the model's own parts so it follows the character as it moves. " +
                "Choose what each zone plays, whether Martlet notices it and how long it rests. Martlet notices adds up your touches " +
                "and tells your Thinking model: with what you say next, or, when you say nothing, in a short reply of its own about " +
                "a second after your last touch. Changes save as you make them, for this model.", new Thickness(0, 0, 0, 8))
        };
        var status = Note(catalog is null ? "Reading the character..." : TouchZonesStatusText(settings), new Thickness(0, 0, 0, 4));
        AutomationProperties.SetAutomationId(status, "TouchZonesStatus");
        stack.Add(status);
        var thinking = conversation?.Configuration;
        var vision = Note(thinking is null ? "Set up Thinking to detect zones." : thinking.VisionAdvice(), new Thickness(0, 0, 0, 4));
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
        var saveState = Note("", new Thickness(0, 0, 0, 4));
        AutomationProperties.SetAutomationId(saveState, "TouchZonesSaveState");
        AutomationProperties.SetLiveSetting(saveState, AutomationLiveSetting.Polite);
        stack.Add(saveState);

        var busy = detectingTouchZones || characterTouchZones.Busy;
        var detect = PageButton(busy ? "Detecting..." : settings is { Zones.Count: > 0 } ? "Detect again" : "Detect zones",
            () => DetectTouchZonesAsync().Forget(), id: "TouchZonesDetect");
        detect.IsEnabled = !busy && conversation is not null && avatar.IsShowing && catalog is not null &&
            (thinking?.Vision() != VisionSupport.Unsupported || conversation.Helpers.PoolHas(HelperCapability.Vision));
        AutomationProperties.SetHelpText(detect, "Shows your Thinking model pictures of the character (never its files), step by step, to find and check where its parts are.");
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
            Content = "Include intimate zones (lips, neck, ears, chest, waist, hips and below)", IsChecked = settings?.IncludeIntimate != false,
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
            var why = await characterTouchZones.SaveAsync(next, lifetime.Token);
            saveState.Text = why is null ? "All changes saved." : "Not saved: " + why;
            saveState.SetResourceReference(TextBlock.ForegroundProperty, why is null ? "MutedBrush" : "WarningBrush");
            if (why is null) { tabEdited = false; status.Text = TouchZonesStatusText(characterTouchZones.Current); }
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

        // The snapshot with each zone as a colored, labeled box; drag a box to move it, its corner to resize it.
        var canvas = new Canvas { Margin = new Thickness(0, 8, 0, 8), HorizontalAlignment = HorizontalAlignment.Left, ClipToBounds = true };
        AutomationProperties.SetAutomationId(canvas, "TouchZonesPicture");
        AutomationProperties.SetName(canvas, "Touch zones on the character's picture");
        double width = 0, height = 0;
        if (characterTouchZones.SnapshotPath is { } picture)
        {
            try
            {
                var bitmap = PictureAt(picture);
                var scale = Math.Min(TouchZonesPictureHeight / bitmap.PixelHeight, TouchZonesPictureWidth / bitmap.PixelWidth);
                (width, height) = (bitmap.PixelWidth * scale, bitmap.PixelHeight * scale);
                canvas.Width = width;
                canvas.Height = height;
                var image = new Image { Source = bitmap, Width = width, Height = height, Stretch = Stretch.Fill };
                canvas.Children.Add(image);
                canvas.Background = new SolidColorBrush(Color.FromArgb(0x18, 0x80, 0x80, 0x80));
                // The whole character as Thinking saw it: the same framing, on its backdrop with the grid.
                if (characterTouchZones.SentWholePicture is { } seenPath)
                {
                    var seen = PictureAt(seenPath);
                    var view = new CheckBox
                    {
                        Content = "Show the picture Thinking saw (on a plain backdrop, with its grid)", IsChecked = showTouchZonesSent,
                        Margin = new Thickness(0, 8, 0, 0)
                    };
                    AutomationProperties.SetAutomationId(view, "TouchZonesSentView");
                    view.Checked += (_, _) => { showTouchZonesSent = true; image.Source = seen; };
                    view.Unchecked += (_, _) => { showTouchZonesSent = false; image.Source = bitmap; };
                    if (showTouchZonesSent) image.Source = seen;
                    stack.Add(view);
                }
            }
            catch (Exception error) when (error is IOException or NotSupportedException or UriFormatException or InvalidOperationException) { width = height = 0; }
        }
        if (width > 0) stack.Add(canvas);

        var reactionItems = new List<(string Id, string Label)>();
        foreach (var (source, action) in catalog.Entries.Where(e => e.Action.Enabled))
            reactionItems.Add((source.Id, $"{source.Name}  \u00b7  " + source.Kind switch
            {
                CharacterActionKind.Expression => "emote", CharacterActionKind.Motion => "motion", _ => "gesture"
            }));
        var showing = avatar.IsShowing && characterActions.For(avatar.InspectedProfile?.ModelPath) is not null;
        var index = 0;
        foreach (var zone in (settings?.Zones ?? []).Take(CharacterTouchZones.MaximumZones))
        {
            var row = new ZoneRow(this, zone, index++, catalog, reactionItems, settings!, showing, Edited, temperament);
            rows.Add(row);
            stack.Add(row.View);
            if (width > 0) row.Draw(canvas, width, height, ZoneColors[(row.Number) % ZoneColors.Length]);
        }

        // Add a zone the Thinking model missed: it starts in the middle of the picture; move it into place.
        var missing = CharacterTouchZones.Kinds.Where(k => settings?.Zones.Any(z => z.Id == k.Id) != true).ToArray();
        if (missing.Length > 0)
        {
            var kinds = new ComboBox { ItemsSource = missing.Select(k => k.Label).ToArray(), SelectedIndex = 0, MinWidth = 180, MinHeight = 26 };
            AutomationProperties.SetName(kinds, "Zone to add");
            AutomationProperties.SetAutomationId(kinds, "TouchZonesAddKind");
            var add = PageButton("Add zone", () =>
            {
                var kind = missing[Math.Max(0, kinds.SelectedIndex)];
                var current = characterTouchZones.Current ?? new CharacterTouchZoneSettings { ModelId = modelId, DetectedBy = CharacterTouchZoneSettings.ByOwner };
                var added = current with
                {
                    IncludeIntimate = intimate.IsChecked == true,
                    Zones = [.. rows.Where(r => !r.Deleted).Select(r => r.Read()), new CharacterTouchZone { Id = kind.Id, Box = new(0.4, 0.4, 0.2, 0.2) }]
                };
                SaveAndRender(added);
            }, id: "TouchZonesAdd");
            var addRow = new WrapPanel { Margin = new Thickness(0, 12, 0, 0) };
            addRow.Children.Add(kinds);
            add.Margin = new Thickness(8, 0, 0, 0);
            addRow.Children.Add(add);
            stack.Add(addRow);
        }
        var card = Card([.. stack]);
        card.Unloaded += (_, _) => { if (autoSave.Pending) autoSave.SaveNowAsync().Forget(); };
        return card;

        async void SaveAndRender(CharacterTouchZoneSettings next)
        {
            var why = await characterTouchZones.SaveAsync(next, lifetime.Token);
            tabEdited = false;
            if (why is not null) saveState.Text = "Not saved: " + why;
            else if (openTab == CompanionTab.Character) RenderTab();
        }
    }

    private static string TouchZonesStatusText(CharacterTouchZoneSettings? settings) =>
        settings is null || settings.Zones.Count == 0
            ? "No zones found yet for this model. Until then a click reacts to the rough part (head, face, body, arm, hand, leg)."
            : $"{settings.Zones.Count} zone{(settings.Zones.Count == 1 ? "" : "s")}, {settings.Zones.Count(settings.Active)} in use" +
              (settings.DetectedBy == CharacterTouchZoneSettings.ByVision && settings.DetectedAt is { } at
                ? $". Found by the Thinking model on {at.ToLocalTime().ToString("g", CultureInfo.CurrentCulture)}." : ". Made by you.");

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

    /// <summary>One zone's row: on, name, reaction (two picks), Martlet notices, hint, rest, box, Try and Delete, and its box on the picture.</summary>
    private sealed class ZoneRow
    {
        private const string DefaultChoice = "(default)", NothingChoice = "(nothing)", NoSecond = "(nothing else)";
        private readonly CharacterTouchZone zone;
        private readonly CheckBox on, notices;
        private readonly TextBox name, narration, rest, box;
        private readonly ComboBox first, second;
        private readonly IReadOnlyList<(string Id, string Label)> items;
        private readonly Action edited;
        internal int Number { get; }
        internal bool Deleted { get; private set; }
        internal StackPanel View { get; } = new() { Margin = new Thickness(0, 10, 0, 0) };

        internal ZoneRow(MainWindow window, CharacterTouchZone zone, int number, CharacterActionCatalog catalog,
            IReadOnlyList<(string Id, string Label)> items, CharacterTouchZoneSettings settings, bool showing, Action edited,
            CharacterTouchTemperament? temperament)
        {
            this.zone = zone;
            this.items = items;
            this.edited = edited;
            Number = number;
            var kind = CharacterTouchZones.Kind(zone.Id);
            on = new CheckBox { IsChecked = zone.Enabled, VerticalAlignment = VerticalAlignment.Center };
            AutomationProperties.SetName(on, $"Use {zone.Name}");
            AutomationProperties.SetAutomationId(on, $"TouchZoneOn-{number}");
            name = new TextBox { Text = zone.Name, Width = 160, MaxLength = CharacterTouchZones.MaximumLabelLength, Margin = new Thickness(6, 0, 0, 0) };
            AutomationProperties.SetName(name, $"Name of {zone.Name}");
            AutomationProperties.SetAutomationId(name, $"TouchZoneName-{number}");
            var state = new TextBlock
            {
                Text = Describe(zone, settings, CharacterTouchZones.React(zone with { Reaction = zone.Reaction with { Actions = null } }, catalog, temperament, 1)),
                VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0), TextWrapping = TextWrapping.Wrap
            };
            state.SetResourceReference(StyleProperty, "Muted");
            AutomationProperties.SetAutomationId(state, $"TouchZoneState-{number}");
            var tryIt = PageButton("Try", () => window.TryTouchZone(Read(), catalog), id: $"TouchZoneTry-{number}");
            tryIt.MinWidth = 60;
            tryIt.IsEnabled = showing;
            var delete = PageButton("Delete", () =>
            {
                Deleted = true;
                View.Visibility = Visibility.Collapsed;
                rectangle?.SetValue(UIElement.VisibilityProperty, Visibility.Collapsed);
                label?.SetValue(UIElement.VisibilityProperty, Visibility.Collapsed);
                edited();
            }, id: $"TouchZoneDelete-{number}");
            delete.MinWidth = 60;
            delete.Margin = tryIt.Margin = new Thickness(8, 0, 0, 0);
            var header = new DockPanel();
            DockPanel.SetDock(delete, Dock.Right);
            DockPanel.SetDock(tryIt, Dock.Right);
            header.Children.Add(delete);
            header.Children.Add(tryIt);
            header.Children.Add(on);
            header.Children.Add(name);
            header.Children.Add(state);

            var labels = new[] { DefaultChoice, NothingChoice }.Concat(items.Select(i => i.Label)).ToArray();
            var chosen = zone.Reaction.Actions;
            first = new ComboBox { ItemsSource = labels, MinWidth = 180, MinHeight = 26 };
            first.SelectedIndex = chosen is null ? 0 : chosen.Count == 0 ? 1 : Math.Max(0, IndexOf(chosen[0]) + 2);
            AutomationProperties.SetName(first, $"What {zone.Name} plays");
            AutomationProperties.SetAutomationId(first, $"TouchZoneReaction-{number}");
            second = new ComboBox { ItemsSource = new[] { NoSecond }.Concat(items.Select(i => i.Label)).ToArray(), MinWidth = 160, MinHeight = 26 };
            second.SelectedIndex = chosen is { Count: > 1 } ? Math.Max(0, IndexOf(chosen[1]) + 1) : 0;
            second.IsEnabled = first.SelectedIndex > 1;
            AutomationProperties.SetName(second, $"What else {zone.Name} plays");
            AutomationProperties.SetAutomationId(second, $"TouchZoneReaction2-{number}");
            notices = new CheckBox { Content = "Martlet notices", IsChecked = zone.Reaction.Notices, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 6, 0) };
            AutomationProperties.SetAutomationId(notices, $"TouchZoneNotices-{number}");
            AutomationProperties.SetHelpText(notices, "Touches here go to your Thinking model: with what you say next, or in a short reply of their own.");
            narration = new TextBox
            {
                Text = zone.Reaction.Narration is { } own && own != kind?.Narration ? own : "", MinWidth = 200,
                MaxLength = CharacterTouchZones.MaximumNarrationLength, IsEnabled = zone.Reaction.Notices
            };
            AutomationProperties.SetName(narration, $"Your own words for touching {zone.Name} (optional hint)");
            AutomationProperties.SetAutomationId(narration, $"TouchZoneNarration-{number}");
            rest = new TextBox { Text = zone.Reaction.CooldownSeconds.ToString("0.#", CultureInfo.CurrentCulture), Width = 72 };
            AutomationProperties.SetName(rest, $"Seconds {zone.Name} rests after a touch");
            AutomationProperties.SetAutomationId(rest, $"TouchZoneCooldown-{number}");
            box = new TextBox { Text = BoxText(zone.Box), Width = 190 };
            AutomationProperties.SetName(box, $"Box of {zone.Name}: left, top, width, height in percent of the picture");
            AutomationProperties.SetAutomationId(box, $"TouchZoneBox-{number}");

            on.Checked += (_, _) => edited();
            on.Unchecked += (_, _) => edited();
            name.TextChanged += (_, _) => edited();
            first.SelectionChanged += (_, _) => { second.IsEnabled = first.SelectedIndex > 1; edited(); };
            second.SelectionChanged += (_, _) => edited();
            notices.Checked += (_, _) => { narration.IsEnabled = true; edited(); };
            notices.Unchecked += (_, _) => { narration.IsEnabled = false; edited(); };
            narration.TextChanged += (_, _) => edited();
            rest.TextChanged += (_, _) => edited();
            box.TextChanged += (_, _) => { Place(); edited(); };

            var fields = new WrapPanel { Margin = new Thickness(24, 4, 0, 0) };
            fields.Children.Add(new Label { Content = "Plays", Target = first, Padding = new Thickness(0, 4, 6, 4) });
            fields.Children.Add(first);
            fields.Children.Add(new Label { Content = "and", Target = second, Padding = new Thickness(6, 4, 6, 4) });
            fields.Children.Add(second);
            fields.Children.Add(notices);
            fields.Children.Add(narration);
            var more = new WrapPanel { Margin = new Thickness(24, 4, 0, 0) };
            more.Children.Add(new Label { Content = "Rests (seconds)", Target = rest, Padding = new Thickness(0, 4, 6, 4) });
            more.Children.Add(rest);
            more.Children.Add(new Label { Content = "Box (%: left, top, width, height)", Target = box, Padding = new Thickness(12, 4, 6, 4) });
            more.Children.Add(box);
            View.Children.Add(header);
            View.Children.Add(fields);
            View.Children.Add(more);
        }

        private int IndexOf(string id)
        {
            for (var i = 0; i < items.Count; i++) if (items[i].Id == id) return i;
            return -1;
        }

        private static string Describe(CharacterTouchZone zone, CharacterTouchZoneSettings settings, TouchReactionPlan defaults)
        {
            var kind = CharacterTouchZones.Kind(zone.Id);
            var parts = zone.Drawables.Count > 0 ? $"{zone.Drawables.Count} part{(zone.Drawables.Count == 1 ? "" : "s")}"
                : zone.Bones.Count > 0 ? string.Join(", ", zone.Bones.Take(3)) : "box only";
            return $"{zone.Id}  \u00b7  {parts}" + (kind?.Intimate == true && !settings.IncludeIntimate ? "  \u00b7  intimate, off" : "") +
                (defaults.From == TouchReactionPlan.FromTemperament ? $"  \u00b7  temperament ({defaults.Attitude}): " : "  \u00b7  default: ") +
                (defaults.Actions.Count == 0 ? "nothing" : string.Join(" + ", defaults.Actions.Select(s => s.Name)));
        }

        private static string BoxText(TouchZoneBox b) => string.Join(", ", new[] { b.X, b.Y, b.Width, b.Height }
            .Select(v => (v * 100).ToString("0.#", CultureInfo.CurrentCulture)));

        private TouchZoneBox? ParsedBox()
        {
            var values = box.Text.Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries)
                .Select(t => double.TryParse(t, NumberStyles.Float, CultureInfo.CurrentCulture, out var v) ? v / 100 : double.NaN).ToArray();
            if (values.Length != 4 || values.Any(v => !double.IsFinite(v))) return null;
            var parsed = new TouchZoneBox(values[0], values[1], values[2], values[3]);
            return parsed.Valid ? parsed : null;
        }

        /// <summary>The zone as the row shows it now (an unreadable box or rest keeps the saved one).</summary>
        internal CharacterTouchZone Read()
        {
            IReadOnlyList<string>? actions = first.SelectedIndex switch
            {
                <= 0 => null,
                1 => [],
                var i => new[] { items[i - 2].Id }.Concat(second.SelectedIndex > 0 ? [items[second.SelectedIndex - 1].Id] : Array.Empty<string>())
                    .Distinct(StringComparer.Ordinal).ToArray()
            };
            var given = name.Text.Trim();
            var line = narration.Text.Trim();
            return zone with
            {
                Enabled = on.IsChecked == true,
                Label = given.Length == 0 || given == CharacterTouchZones.Kind(zone.Id)?.Label ? null : given,
                Box = ParsedBox() ?? zone.Box,
                Reaction = new()
                {
                    Actions = actions, Notices = notices.IsChecked == true,
                    Narration = line.Length == 0 || line == CharacterTouchZones.Kind(zone.Id)?.Narration ? null : line,
                    CooldownSeconds = double.TryParse(rest.Text, NumberStyles.Float, CultureInfo.CurrentCulture, out var seconds) &&
                        seconds is >= 0 and <= CharacterTouchReaction.MaximumCooldown ? seconds : zone.Reaction.CooldownSeconds
                }
            };
        }

        private Border? rectangle;
        private TextBlock? label;
        private double pictureWidth, pictureHeight;

        /// <summary>Draws the zone's box on the picture; dragging it moves the box, dragging its corner resizes it.</summary>
        internal void Draw(Canvas canvas, double width, double height, Color color)
        {
            (pictureWidth, pictureHeight) = (width, height);
            label = new TextBlock
            {
                Text = zone.Name, Foreground = Brushes.White, FontSize = 10, Padding = new Thickness(2, 0, 2, 0), TextWrapping = TextWrapping.NoWrap,
                Background = new SolidColorBrush(Color.FromArgb(0xC0, color.R, color.G, color.B)), VerticalAlignment = VerticalAlignment.Top,
                HorizontalAlignment = HorizontalAlignment.Left, IsHitTestVisible = false
            };
            var grip = new Rectangle
            {
                Width = 8, Height = 8, Fill = new SolidColorBrush(color), HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Bottom, Cursor = Cursors.SizeNWSE
            };
            var inside = new Grid();
            inside.Children.Add(grip);
            rectangle = new Border
            {
                BorderBrush = new SolidColorBrush(color), BorderThickness = new Thickness(1.5), Child = inside, Cursor = Cursors.SizeAll,
                Background = new SolidColorBrush(Color.FromArgb(0x30, color.R, color.G, color.B)), ToolTip = zone.Name
            };
            AutomationProperties.SetAutomationId(rectangle, $"TouchZoneRect-{Number}");
            AutomationProperties.SetName(rectangle, zone.Name);
            canvas.Children.Add(rectangle);
            canvas.Children.Add(label);
            Place();
            Point? from = null;
            var resizing = false;
            TouchZoneBox start = zone.Box;
            rectangle.MouseLeftButtonDown += (_, e) =>
            {
                from = e.GetPosition(canvas);
                resizing = ReferenceEquals(e.OriginalSource, grip);
                start = ParsedBox() ?? zone.Box;
                rectangle.CaptureMouse();
                e.Handled = true;
            };
            rectangle.MouseMove += (_, e) =>
            {
                if (from is not { } origin) return;
                var at = e.GetPosition(canvas);
                double dx = (at.X - origin.X) / pictureWidth, dy = (at.Y - origin.Y) / pictureHeight;
                var moved = resizing
                    ? start with { Width = Math.Clamp(start.Width + dx, 0.02, 1 - start.X), Height = Math.Clamp(start.Height + dy, 0.02, 1 - start.Y) }
                    : start with { X = Math.Clamp(start.X + dx, 0, 1 - start.Width), Y = Math.Clamp(start.Y + dy, 0, 1 - start.Height) };
                box.Text = BoxText(moved);
            };
            rectangle.MouseLeftButtonUp += (_, e) =>
            {
                from = null;
                rectangle.ReleaseMouseCapture();
                e.Handled = true;
            };
        }

        private void Place()
        {
            if (rectangle is null || pictureWidth <= 0 || ParsedBox() is not { } b) return;
            Canvas.SetLeft(rectangle, b.X * pictureWidth);
            Canvas.SetTop(rectangle, b.Y * pictureHeight);
            rectangle.Width = Math.Max(4, b.Width * pictureWidth);
            rectangle.Height = Math.Max(4, b.Height * pictureHeight);
            if (label is null) return;
            Canvas.SetLeft(label, b.X * pictureWidth + 1);
            Canvas.SetTop(label, b.Y * pictureHeight + 1);
        }
    }
}
