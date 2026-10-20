using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Martlet.Avatar.Hosting;

namespace Martlet.Desktop;

/// <summary>Companion › Touch › Touch zones, the UI only: where a left click on the character lands (the hair, an eye, a hand...)
/// and what the character does then. The status, vision, detection and last-touch lines, When you touch Martlet while it talks,
/// Detect zones and Stop, the zone map (the snapshot with each zone's box, which zooms), a row for each zone, Add zone and
/// Start over. Edits save as you make them, per model.
/// <para>What the lines say is in <see cref="TouchZonesPageModel"/>; what the page needs from the app is
/// <see cref="ITouchZonesPageServices"/>. Two parts are the window's own and come as builders: the last stroke's line
/// (<paramref name="physicalLastLine"/>, CharacterPhysicalLast, which the window updates) and the voice sounds' note
/// (<paramref name="voiceSoundsNote"/>). <paramref name="addRow"/> adds a row of the long zone list a batch at a time on a page
/// just opened. MainWindow keeps one page for the window's life: it keeps the map's zoom and which rows show More.</para></summary>
internal sealed partial class TouchZonesPage(ITouchZonesPageServices services, Action<Panel, UIElement> addRow, Func<UIElement> physicalLastLine,
    Func<UIElement> voiceSoundsNote)
{
    /// <summary>The colors of the zones' boxes, in the order of their rows (the showing character draws its zones in them too).</summary>
    internal static readonly Color[] ZoneColors =
    [
        Color.FromRgb(0xE9, 0x4F, 0x64), Color.FromRgb(0x3B, 0x82, 0xF6), Color.FromRgb(0x10, 0xB9, 0x81), Color.FromRgb(0xF5, 0x9E, 0x0B),
        Color.FromRgb(0x8B, 0x5C, 0xF6), Color.FromRgb(0xEC, 0x48, 0x99), Color.FromRgb(0x06, 0xB6, 0xD4), Color.FromRgb(0x84, 0xCC, 0x16)
    ];

    internal const double PictureHeight = 600, PictureWidth = 440;

    private TextBlock? last, noticed, noticedLast;
    private bool showSent;
    // The zones whose rows show their box and Delete (More), while Martlet runs.
    private readonly HashSet<string> openRows = new(StringComparer.Ordinal);
    // The zone map's zoom and the picture's point at the middle of its frame (fractions of the picture), for the model they belong
    // to: drawing the page again (Add zone, each step of Detect zones...) keeps them, and opening it again shows the whole picture.
    private double zoom = 1;
    private Point center = new(0.5, 0.5);
    private string? viewModel;

    /// <summary>Shows what the last touches did on the lines of the page last drawn.</summary>
    internal void ShowTouches()
    {
        var zones = services.Zones;
        if (last is not null) last.Text = zones.LastMatch ?? services.TouchIdle;
        if (noticed is not null) noticed.Text = zones.Noticed ?? TouchZonesPageModel.NoticedIdle;
        if (noticedLast is not null) noticedLast.Text = zones.NoticedLast ?? "";
    }

    /// <summary>The Touch zones card for the model the page follows.</summary>
    internal Border ZonesCard()
    {
        var model = new TouchZonesPageModel(services);
        var zones = services.Zones;
        var catalog = model.Catalog;
        var settings = model.Settings;
        var temperament = model.Temperament;
        var stack = new List<UIElement>
        {
            Heading("Touch zones"),
            HelpTip.Explain("Click the character (a click, not a drag) and it reacts to where you touched it: a pat on the head, a poke on " +
                "the cheek, holding its hand. With its position locked, drag across it to stroke it: each part you cross reacts. " +
                "Each zone plays its own list of emotes, gestures and motions, in order, on its own when you touch it: what the list " +
                "shows is exactly what plays. Add, remove and reorder them; a new zone's list starts from the persona's touch " +
                "temperament or the built-in reactions. Martlet notices adds up your touches on a zone and tells your Thinking model, " +
                "with what you say next or in a short reply of its own. The first time this page shows a model, Martlet places a first " +
                "guess at its zones with no AI and nothing sent; Detect zones has your Thinking model find them in pictures of the " +
                "character (never its files). Changes save as you make them, for this model.", new Thickness(0, 0, 0, 8), "TouchZones", "touch zones")
        };
        var status = Note(model.StatusText, new Thickness(0, 0, 0, 4));
        AutomationProperties.SetAutomationId(status, "TouchZonesStatus");
        stack.Add(status);
        var sight = model.Sight;
        var vision = Note(sight.Line, new Thickness(0, 0, 0, 4));
        AutomationProperties.SetAutomationId(vision, "TouchZonesVision");
        stack.Add(vision);
        if (zones.Detection is { } detection)
        {
            var found = Note(detection, new Thickness(0, 0, 0, 4));
            AutomationProperties.SetAutomationId(found, "TouchZonesDetection");
            AutomationProperties.SetLiveSetting(found, AutomationLiveSetting.Polite);
            stack.Add(found);
        }
        last = Note(zones.LastMatch ?? services.TouchIdle, new Thickness(0, 0, 0, 4));
        AutomationProperties.SetAutomationId(last, "TouchZonesLast");
        AutomationProperties.SetLiveSetting(last, AutomationLiveSetting.Polite);
        stack.Add(last);
        noticed = Note(zones.Noticed ?? TouchZonesPageModel.NoticedIdle, new Thickness(0, 0, 0, 4));
        AutomationProperties.SetAutomationId(noticed, "TouchZonesNoticed");
        stack.Add(noticed);
        noticedLast = Note(zones.NoticedLast ?? "", new Thickness(0, 0, 0, 4));
        AutomationProperties.SetAutomationId(noticedLast, "TouchZonesNoticedLast");
        stack.Add(noticedLast);
        stack.Add(physicalLastLine());
        AddTouchInterrupts(stack);
        var saveState = Note("", new Thickness(0, 0, 0, 4));
        AutomationProperties.SetAutomationId(saveState, "TouchZonesSaveState");
        AutomationProperties.SetLiveSetting(saveState, AutomationLiveSetting.Polite);
        stack.Add(saveState);

        var detect = PageButton(model.DetectLabel, services.Detect, id: "TouchZonesDetect");
        detect.IsEnabled = model.DetectEnabled;
        AutomationProperties.SetHelpText(detect, "Shows your Thinking model pictures of the character (never its files), step by step, to find and check " +
            "where its parts are. Martlet draws the character off screen in its rest pose, so it works while the character is hidden. First " +
            "the whole character on a plain backdrop with a grid, then a close-up of each part; the model checks its own numbered boxes " +
            "until it says they are right. Martlet fits each box to the character and ties each zone to the model's own parts, so it " +
            "follows the character as it moves.");
        Button? stopDetecting = null;
        if (model.Busy && services.DetectRun is { } running)
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
        if (zones.Sent is { } sent)
        {
            var seen = Note(sent.Describe(), new Thickness(0, 4, 0, 4));
            AutomationProperties.SetAutomationId(seen, "TouchZonesSent");
            stack.Add(seen);
            if (zones.SentFolder is { } sentFolder)
            {
                var open = PageButton("Open the pictures", () => PictureFiles.OpenFolder(sentFolder), id: "TouchZonesSentOpen");
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

        var rows = new List<TouchZoneRow>();
        var autoSave = new AutoSave(async () =>
        {
            if (zones.ModelId != modelId) return true;
            var next = (zones.Current ?? new CharacterTouchZoneSettings { ModelId = modelId, DetectedBy = CharacterTouchZoneSettings.ByOwner }) with
            {
                IncludeIntimate = intimate.IsChecked == true,
                Zones = rows.Where(r => !r.Deleted).Select(r => r.Read()).ToArray()
            };
            // Bound again to the model: each area takes what its box holds now, and a zone on a tail follows all of it.
            var (why, reshaped) = await zones.SaveEditedAsync(next, services.Lifetime);
            saveState.Text = why is null ? "All changes saved." : "Not saved: " + why;
            saveState.SetResourceReference(TextBlock.ForegroundProperty, why is null ? "MutedBrush" : "WarningBrush");
            if (why is null) { services.MarkEdited(false); status.Text = TouchZonesPageModel.StatusOf(zones.Current); }
            // Binding changed a zone's areas (a box moved onto a tail now follows all of it): the page shows them, once the box
            // isn't being dragged.
            if (why is null && reshaped && Mouse.LeftButton != MouseButtonState.Pressed && services.TouchOpen) services.Redraw();
            return true;
        });
        void Edited()
        {
            services.MarkEdited(true);
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
        if (zones.SnapshotPath is { } picture)
        {
            try
            {
                var bitmap = PictureFiles.At(picture);
                var scale = Math.Min(PictureHeight / bitmap.PixelHeight, PictureWidth / bitmap.PixelWidth);
                // A page opened again, or another model's picture, shows the whole picture.
                if (services.Opening || viewModel != modelId) (zoom, center, viewModel) = (1, new Point(0.5, 0.5), modelId);
                var map = new ZoomFrame(canvas, bitmap.PixelWidth * scale, bitmap.PixelHeight * scale, zoom, center)
                {
                    Margin = new Thickness(0, 8, 0, 8)
                };
                AutomationProperties.SetAutomationId(map, "TouchZonesMap");
                AutomationProperties.SetName(map, "Zone map");
                AutomationProperties.SetHelpText(map, ZoomHelp);
                var image = new Image { Width = map.PictureWidth, Height = map.PictureHeight, Stretch = Stretch.Fill };
                canvas.Children.Add(image);
                canvas.Background = new SolidColorBrush(Color.FromArgb(0x18, 0x80, 0x80, 0x80));
                // The whole character as Thinking saw it (the same framing, on its backdrop with the grid) shows instead when asked.
                // At 1x a picture is decoded at about the size it shows; zoomed in, at its own size, so it stays sharp.
                var seenPath = zones.SentWholePicture;
                void ShowPicture()
                {
                    var path = showSent && seenPath is not null ? seenPath : picture;
                    try { image.Source = map.Zoom > 1 ? PictureFiles.At(path, decodeHeight: 0) : PictureFiles.At(path); }
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
                        Content = "Show the picture Thinking saw (on a plain backdrop, with its grid)", IsChecked = showSent,
                        Margin = new Thickness(0, 8, 0, 0)
                    };
                    AutomationProperties.SetAutomationId(view, "TouchZonesSentView");
                    view.Checked += (_, _) => { showSent = true; ShowPicture(); };
                    view.Unchecked += (_, _) => { showSent = false; ShowPicture(); };
                    stack.Add(view);
                }
                stack.Add(ZoomRow(map, () =>
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
                Content = "Show the zones on the character", IsChecked = services.ShowZonesOnCharacter, Margin = new Thickness(0, 0, 0, 8)
            };
            AutomationProperties.SetAutomationId(onCharacter, "TouchZonesShowOnCharacter");
            AutomationProperties.SetHelpText(onCharacter, "Draws each area of the zones in use over your character as it moves, in its zone's color. " +
                "An area that follows the model's own parts, such as a tail's, moves with them. Only until Martlet closes.");
            onCharacter.Checked += (_, _) => { services.ShowZonesOnCharacter = true; services.SendZoneView(); };
            onCharacter.Unchecked += (_, _) => { services.ShowZonesOnCharacter = false; services.SendZoneView(); };
            stack.Add(onCharacter);
        }

        // The model's reactions, then the sounds the voice makes alone.
        var reactionItems = model.ReactionItems(services.VoiceSoundItems());
        stack.Add(voiceSoundsNote());
        var showing = services.CharacterShowing;
        // The zones' rows, then the Add zone note and controls; on a page just opened they join a batch at a time (their boxes show
        // on the picture at once).
        var list = new StackPanel();
        stack.Add(list);
        var index = 0;
        foreach (var zone in (settings?.Zones ?? []).Take(CharacterTouchZones.MaximumZones))
        {
            var row = new TouchZoneRow(zone, index++, catalog, reactionItems, settings!, showing, Edited, temperament, openRows,
                tried => services.TryZone(tried, catalog), services.HearSound);
            rows.Add(row);
            addRow(list, row.View);
            if (frame is not null) row.Draw(canvas, frame, ZoneColors[(row.Number) % ZoneColors.Length]);
        }

        // Add a zone Detect zones doesn't look for (or missed): it starts in the middle of the picture, or of the part the zoomed-in
        // map shows; move it into place, or press Detect again and the Thinking model places it too.
        var addNote = HelpTip.Explain($"Detect zones looks for the {TouchZoneDetection.DefaultParts}, and for anything special to this character, such as " +
            $"{TouchZoneDetection.SpecialExamples}. Add any other zone here: it starts in the middle " +
            "of the picture (zoomed in, of the part you see). Move it into place, or press Detect again and your Thinking model places it too.",
            new Thickness(0, 16, 0, 0), "TouchZoneDetect", "detecting zones");
        AutomationProperties.SetAutomationId(addNote, "TouchZonesAddNote");
        addRow(list, addNote);
        var missing = model.Missing;
        if (missing.Length > 0)
        {
            var kinds = Compact(new ComboBox { ItemsSource = missing.Select(k => k.Label).ToArray(), SelectedIndex = 0, MinWidth = 180 });
            AutomationProperties.SetName(kinds, "Zone to add");
            AutomationProperties.SetAutomationId(kinds, "TouchZonesAddKind");
            var add = Compact(PageButton("Add zone", () =>
            {
                var kind = missing[Math.Max(0, kinds.SelectedIndex)];
                var current = zones.Current ?? new CharacterTouchZoneSettings { ModelId = modelId, DetectedBy = CharacterTouchZoneSettings.ByOwner };
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
            var addLine = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) };
            addLine.Children.Add(kinds);
            add.Margin = new Thickness(8, 0, 0, 0);
            addLine.Children.Add(add);
            addRow(list, addLine);
        }
        stack.Add(StartOver(modelId, autoSave));
        var card = Card([.. stack]);
        card.Unloaded += (_, _) => { if (autoSave.Pending) autoSave.SaveNowAsync().Forget(); };
        return card;

        async void SaveAndRender(CharacterTouchZoneSettings next)
        {
            var (why, _) = await zones.SaveEditedAsync(next, services.Lifetime);
            services.MarkEdited(false);
            if (why is not null) saveState.Text = "Not saved: " + why;
            else if (services.TouchOpen) services.Redraw();
        }
    }
}
