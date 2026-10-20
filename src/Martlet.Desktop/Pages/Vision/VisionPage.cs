using System.IO;
using System.Net.Http;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Martlet.Conversation;
using Martlet.Home;
using Martlet.Presentation;

namespace Martlet.Desktop;

/// <summary>Companion › Vision (an optional extra, in the standard order), the UI only: Now (whether vision is on, what Martlet
/// looks at, whether it can see, the image model's lines and Turn vision off or on), then the image models' list (empty:
/// Thinking's own model takes the pictures), then what it looks at, how chatty it is, its glances and screen summary, and what
/// it sends. Vision is on by default, at your whole screen. Pressing Start watching is the consent; What Martlet sends says
/// exactly what is captured and where it is sent.
/// <para>What each line says and what each choice saves is in <see cref="VisionPageModel"/>; what the page needs from the app is
/// <see cref="IVisionPageServices"/>. The image models' list (<paramref name="imageModelList"/>) and its Now lines
/// (<paramref name="imageModelLines"/>) are shared with Companion › Hearing and built on the shared pool list, so MainWindow
/// still makes them (MainWindow.SensePools.cs and MainWindow.SenseModels.cs). MainWindow keeps one page for the window's life:
/// it keeps the cameras it found.</para></summary>
internal sealed class VisionPage(IVisionPageServices services, Func<UIElement> imageModelList, Func<IEnumerable<UIElement>> imageModelLines)
{
    // The cameras found on request (Find cameras, and Use a Home Assistant camera), kept while Martlet runs.
    private IReadOnlyList<CameraDevice>? cameras;
    private IReadOnlyList<(HomeCamera Camera, Uri Snapshot)>? homeCameras;
    private bool findingCameras, findingHomeCameras;

    /// <summary>Draws the page's cards into <paramref name="page"/>.</summary>
    internal void Render(Panel page)
    {
        var model = new VisionPageModel(services);
        var prefs = model.Prefs;
        var source = model.Source;

        var nowText = new TextBlock { Text = model.NowText, FontSize = 15, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 6) };
        AutomationProperties.SetAutomationId(nowText, "VisionNow");
        var now = new List<UIElement> { Heading("Now"), nowText };
        if (model.SourceMissing is { } missing) now.Add(Warning(missing));
        var advice = model.Advice;
        var adviceText = model.CanSee ? Note(advice, new Thickness(0, 0, 0, 6)) : Warning(advice);
        AutomationProperties.SetAutomationId(adviceText, "VisionStatus");
        now.Add(adviceText);
        now.AddRange(imageModelLines());
        // Turn vision off or on (VisionToggle): whether Martlet looks at all. The list below says which models take the pictures.
        Button? turnOn = null;
        if (prefs.Watch)
            now.Add(Row(PageButton("Turn vision off", () => model.SetWatch(false), primary: true, id: "VisionToggle")));
        else
        {
            turnOn = PageButton("Turn vision on", () => model.SetWatch(true), primary: true, id: "VisionToggle");
            turnOn.IsEnabled = model.CanSee && model.Chosen;
            if (!turnOn.IsEnabled)
            {
                var why = model.TurnOnBlocked!;
                turnOn.ToolTip = why;
                ToolTipService.SetShowOnDisabled(turnOn, true);
                AutomationProperties.SetHelpText(turnOn, why);
            }
            now.Add(Row(turnOn));
        }
        page.Children.Add(Card([.. now]));

        page.Children.Add(imageModelList());

        var looks = new List<UIElement> { Heading("What Martlet looks at") };
        foreach (var (kind, title, detail) in VisionPageModel.Sources)
        {
            var option = Choice("VisionSource", title, detail, source.Kind == kind, "VisionSource-" + kind);
            option.Checked += (_, _) => model.ChooseSource(kind);
            looks.Add(option);
        }
        if (source.Kind == WatchKind.Camera) looks.AddRange(CameraChoice(model));
        else if (source.Kind == WatchKind.Url) looks.AddRange(AddressChoice(model, turnOn));
        page.Children.Add(Card([.. looks]));

        page.Children.Add(Card([Heading("How often it comments"),
            HelpTip.Explain("Most looks end silently, and Martlet won't interrupt while you're talking. This also sets how often " +
                "Martlet reacts to what this PC plays (Listening › Watch along).", new Thickness(0, 0, 0, 8), "VisionChattiness",
                "how often it comments", wiki: "Vision#how-chatty-she-is"),
            .. ChattinessPicker.Create("VisionChattiness", () => services.Talk.ScreenChattiness, services.DecidedChattiness, model.SetChattiness)]));
        page.Children.Add(GazeCard(model));
        // Which model makes the screen summaries (SenseJobRouting): the image model, else a Thinking pool model that sees.
        var poolSees = model.PoolSees();
        page.Children.Add(ScreenSummaryCard(model, model.ScreenSummaryDecision()));

        // What Martlet captures and where it goes, for what it looks at and how chatty it is (Turn vision on is above).
        var disclosure = Note(model.Disclosure(poolSees), new Thickness(0, 0, 0, 0));
        AutomationProperties.SetAutomationId(disclosure, "VisionDisclosure");
        page.Children.Add(Card(Heading("What Martlet sends"), disclosure));
    }

    /// <summary>A camera: the cameras to choose from, Find cameras, and that the camera opens only while Martlet looks.</summary>
    private IEnumerable<UIElement> CameraChoice(VisionPageModel model)
    {
        var prefs = model.Prefs;
        var choices = model.Cameras(cameras);
        var box = new ComboBox { Width = 360, DisplayMemberPath = nameof(CameraDevice.Name), ItemsSource = choices,
            SelectedItem = choices.FirstOrDefault(c => c.Id == prefs.CameraId) };
        AutomationProperties.SetName(box, "Which camera Martlet looks through");
        AutomationProperties.SetAutomationId(box, "VisionCamera");
        box.SelectionChanged += (_, _) =>
        {
            if (box.SelectedItem is CameraDevice picked) model.ChooseCamera(picked);
        };
        var find = PageButton(findingCameras ? "Looking..." : "Find cameras", () => FindCamerasAsync().Forget(), id: "VisionFindCameras");
        find.IsEnabled = !findingCameras;
        find.Margin = new Thickness(10, 0, 0, 0);
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(28, 0, 0, 6) };
        row.Children.Add(box);
        row.Children.Add(find);
        yield return row;
        yield return Note(choices.Count == 0 ? "Find cameras to choose one; nothing opens until Martlet looks."
            : "The camera opens only while Martlet looks.", new Thickness(28, 0, 0, 0));
    }

    /// <summary>A phone or network camera address: the address box and, with Home Assistant connected, its cameras.</summary>
    private IEnumerable<UIElement> AddressChoice(VisionPageModel model, Button? turnOn)
    {
        var address = new TextBox { Width = 420, HorizontalAlignment = HorizontalAlignment.Left, Text = services.TypedAddress ?? model.Prefs.VideoAddress,
            Margin = new Thickness(28, 0, 0, 6) };
        AutomationProperties.SetName(address, "Phone or network camera address");
        AutomationProperties.SetAutomationId(address, "VisionAddress");
        InputHint.SetText(address, "http://192.168.1.20:8080/shot.jpg");
        address.TextChanged += (_, _) =>
        {
            var typed = address.Text.Trim();
            model.TypeAddress(typed);
            if (turnOn is not null && !services.Talk.Watch) turnOn.IsEnabled = model.CanSee && typed.Length > 0;
        };
        yield return address;
        yield return HelpTip.Explain("Example: http://192.168.1.20:8080/shot.jpg (a password in it is used for this session only).",
            new Thickness(28, 0, 0, 0), "VisionAddress", "camera addresses", wiki: "Vision#what-she-looks-at");
        if (!services.SmartHomeConnected) yield break;
        // Home Assistant cameras use their snapshot address; Martlet adds the saved Home Assistant token when it looks.
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(28, 8, 0, 0) };
        if (homeCameras is { Count: > 0 } found)
        {
            var pick = new ComboBox { Width = 300, ItemsSource = found.Select(c => c.Camera.Name).ToArray(),
                SelectedIndex = found.ToList().FindIndex(c => c.Snapshot.AbsoluteUri == services.Talk.VideoAddress) };
            AutomationProperties.SetName(pick, "Home Assistant camera");
            AutomationProperties.SetAutomationId(pick, "VisionHomeCamera");
            pick.SelectionChanged += (_, _) =>
            {
                if (pick.SelectedIndex < 0) return;
                model.ChooseHomeCamera(found[pick.SelectedIndex].Snapshot);
            };
            row.Children.Add(pick);
        }
        var list = PageButton(findingHomeCameras ? "Looking..." : homeCameras is null ? "Use a Home Assistant camera" : "Look again",
            () => FindHomeCamerasAsync().Forget(), id: "VisionHomeCameras");
        list.IsEnabled = !findingHomeCameras;
        list.Margin = new Thickness(homeCameras is { Count: > 0 } ? 10 : 0, 0, 0, 0);
        row.Children.Add(list);
        yield return row;
    }

    /// <summary>Companion › Vision › Glances at your screen: the character keeps its usual gaze (the default; Companion ›
    /// Character › Where the character looks), or Martlet decides with each new screenshot of your screen whether to glance at
    /// something on it.</summary>
    private static Border GazeCard(VisionPageModel model)
    {
        var prefs = model.Prefs;
        var mouse = Choice("VisionGaze", "Keep its usual gaze",
            "The character's eyes do what Companion › Eyes › Where the character looks says: by default, they follow your mouse.",
            !prefs.DecideGaze, "VisionGaze-Mouse");
        var decide = Choice("VisionGaze", "Martlet decides",
            "With each new screenshot of your screen, the character keeps its usual gaze or glances at something interesting on " +
            "the screen: something that just popped up or moved, or what it is about to remark on.",
            prefs.DecideGaze, "VisionGaze-Martlet");
        mouse.Checked += (_, _) => model.SetDecideGaze(false);
        decide.Checked += (_, _) => model.SetDecideGaze(true);
        var status = Note(model.GazeStatus(), new Thickness(0, 2, 0, 0));
        AutomationProperties.SetAutomationId(status, "VisionGazeStatus");
        return Card(Heading("Glances at your screen"), mouse, decide, status,
            HelpTip.Explain("It decides only while vision watches your active window or whole screen and the character shows. " +
                "Screenshots are compared on this PC, and the Thinking model chooses only during the looks Martlet already takes, so " +
                "nothing extra is sent.", new Thickness(0, 6, 0, 0), "VisionGaze", "glances", wiki: "Vision#glances-at-your-screen"));
    }

    /// <summary>Companion › Vision › Screen summary over time (on by default): while Martlet watches, the model that
    /// <see cref="SenseJobRouting"/> picks for <see cref="SenseJobKind.ScreenSummary"/> (<paramref name="decision"/>: the image model,
    /// else a Thinking model that sees in the Thinking pool, named in the status) sums up in the background what changed on the
    /// screen, and the next reply gets it as a note.</summary>
    private static Border ScreenSummaryCard(VisionPageModel model, SenseDecision decision)
    {
        var prefs = model.Prefs;
        var canSee = decision.Taker != SenseTaker.Nobody;
        var box = new CheckBox { Content = "Screen summary over time", IsChecked = prefs.ScreenSummary, Margin = new Thickness(0, 0, 0, 6) };
        AutomationProperties.SetAutomationId(box, "VisionScreenSummary");
        box.Checked += (_, _) => model.SetScreenSummary(true);
        box.Unchecked += (_, _) => model.SetScreenSummary(false);
        var text = VisionPageModel.ScreenSummaryStatus(prefs.ScreenSummary, prefs.Watch, decision);
        var status = prefs.ScreenSummary && prefs.Watch && !canSee ? Warning(text) : Note(text, new Thickness(0, 0, 0, 6));
        AutomationProperties.SetAutomationId(status, "VisionScreenSummaryStatus");
        return Card(Heading("Screen summary over time"), box, status,
            HelpTip.Explain("Each summary is one more request with a picture, which may cost more, and a reply never waits for it. " +
                "Martlet keeps the last pictures that changed in memory and sends a small sheet of them to your image model or a " +
                "Thinking pool model that sees, never to the model your conversation uses.", new Thickness(0, 6, 0, 0),
                "VisionScreenSummary", "screen summaries", wiki: "Vision#screen-summary-over-time"));
    }

    /// <summary>Lists the cameras Windows offers to desktop apps, on request only; nothing is opened.</summary>
    private async Task FindCamerasAsync()
    {
        if (findingCameras) return;
        findingCameras = true;
        services.Redraw();
        try
        {
            var found = await Task.Run(() => new VideoInput().Cameras());
            cameras = found;
            services.ShowStatus(found.Count == 0
                ? "Windows doesn't see any cameras, so connect a camera or enter a camera address."
                : $"Found {found.Count} camera{(found.Count == 1 ? "" : "s")}.");
            var talk = services.Talk;
            if (found.FirstOrDefault(c => c.Id == talk.CameraId) is { } same) services.SaveTalk(talk with { CameraName = same.Name });
            else if (found.FirstOrDefault() is { } first) services.SaveTalk(talk with { CameraId = first.Id, CameraName = first.Name });
        }
        catch (Exception error) when (error is VideoSourceException or System.Runtime.InteropServices.ExternalException)
        {
            services.ShowStatus(error is VideoSourceException ? error.Message : "Windows couldn't list cameras right now; try again in a moment.");
        }
        finally { findingCameras = false; }
        services.RedrawIfOpen();
    }

    /// <summary>Lists Home Assistant's cameras on request; picking one fills in its snapshot address.</summary>
    private async Task FindHomeCamerasAsync()
    {
        if (findingHomeCameras) return;
        findingHomeCameras = true;
        services.Redraw();
        try
        {
            homeCameras = await services.HomeCamerasAsync(services.Lifetime);
            services.ShowStatus(homeCameras.Count == 0 ? "Home Assistant has no cameras."
                : $"Home Assistant has {homeCameras.Count} camera{(homeCameras.Count == 1 ? "" : "s")}: pick one above.");
        }
        catch (HomeAssistantException error) { services.ShowStatus(error.Message); }
        catch (Exception error) when (error is HttpRequestException or TaskCanceledException or IOException)
        {
            if (!services.Closing) services.ShowStatus("Couldn't reach Home Assistant: check that it is running, then try again.");
        }
        finally { findingHomeCameras = false; }
        services.RedrawIfOpen();
    }
}
