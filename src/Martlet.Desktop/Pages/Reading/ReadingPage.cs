using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Martlet.Core.Cluster;
using Martlet.Core.Reading;

namespace Martlet.Desktop;

/// <summary>
/// Companion › Reading (docs/READING.md, an optional extra), the UI only: the places that read the text on your screen while
/// Martlet watches, as one ordered list (<see cref="ReadingList"/>, pools-local.json, this PC's own). In the standard order: Now
/// (what reads, the newest read and Read my screen now, which reads the whole screen once with the list), then the list (the
/// shared pool list, which MainWindow builds: <paramref name="poolList"/>): This PC reads with Windows OCR (the default and
/// recommended: on the processor, nothing leaves the PC) or with Martlet's <c>ocr</c> host role on this PC; a computer or one of
/// its cards reads with its Reading role, set up with the same martlet-host flow as every role and reached through its gateway.
/// The role runs one of its models (<see cref="OptionalExtras.ReadingModels"/>): PP-OCRv5 mobile on the processor (recommended)
/// or on an NVIDIA graphics card, PP-OCRv5 server on an NVIDIA graphics card, or RapidOCR on the processor. A member's Settings
/// choose its engine (This PC) or show its model and Set up or Switch there. With nothing on, reading is off. Then Compare them.
/// What each line says is in <see cref="ReadingPageModel"/>; what the page needs from the app is <see cref="IReadingPageServices"/>.
/// Automation IDs: <c>ReadingNow</c>, <c>ReadingEvery</c> (Read
/// the screen: how often, talk-preferences.json's ReadEveryMs, shared as part of how you talk), <c>ReadingEveryStatus</c>,
/// <c>ReadingLast</c>, the
/// list's <c>Pool-reading-...</c>, <c>ReadingEngine-&lt;windows-ocr|ocr&gt;</c>, <c>ReadingWindowsState</c>,
/// <c>ReadingModel-&lt;place&gt;-&lt;model&gt;</c> (place: this-pc or the host; model: ppocrv5-mobile, ppocrv5-mobile-gpu,
/// ppocrv5-server or
/// rapidocr-ppocrv4), <c>ReadingModelNote-&lt;place&gt;</c>, <c>ReadingHostState-&lt;place&gt;</c>, <c>ReadingSetUp-&lt;place&gt;</c>,
/// <c>ReadingSwitch-&lt;place&gt;</c>, <c>PickerCompare-Reading</c>, <c>ReadingTest</c>, <c>ReadingTestState</c> and
/// <c>ReadingTestText</c>.
/// </summary>
internal sealed partial class ReadingPage(IReadingPageServices services, Func<PoolListOptions, UIElement> poolList)
{
    /// <summary>Draws the page's cards into <paramref name="page"/>.</summary>
    internal void Render(Panel page)
    {
        var model = new ReadingPageModel(services);
        var list = model.List;
        if (services.WindowsReads is null && !services.WindowsChecking) services.CheckWindows();

        var now = new TextBlock { Text = model.NowText, FontSize = 15, TextWrapping = TextWrapping.Wrap };
        AutomationProperties.SetAutomationId(now, "ReadingNow");
        var last = Note(ReadingPageModel.LastText, new Thickness(0, 4, 0, 0));
        AutomationProperties.SetAutomationId(last, "ReadingLast");
        var test = Note(model.TestStateText, new Thickness(0, 8, 0, 0));
        AutomationProperties.SetAutomationId(test, "ReadingTestState");
        var testText = services.TestText;
        var text = new TextBox
        {
            Text = testText ?? "", IsReadOnly = true, TextWrapping = TextWrapping.Wrap, MaxHeight = 220, MinHeight = 60,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Margin = new Thickness(0, 6, 0, 0),
            Visibility = testText is null ? Visibility.Collapsed : Visibility.Visible
        };
        AutomationProperties.SetName(text, "Text Martlet read on your screen");
        AutomationProperties.SetAutomationId(text, "ReadingTestText");
        var readNow = list.NoneOn ? null : PageButton("Read my screen now", services.ReadScreenNow, id: "ReadingTest");
        if (readNow is not null) readNow.IsEnabled = !services.Testing;
        var every = new ComboBox { Width = 240, ItemsSource = ReadingPageModel.EveryNames, SelectedIndex = model.EveryIndex };
        AutomationProperties.SetName(every, "How often Martlet reads the text on your screen while it watches");
        AutomationProperties.SetAutomationId(every, "ReadingEvery");
        every.SelectionChanged += (_, _) =>
        {
            if (every.SelectedIndex >= 0 && ScreenReadPace.ChoicesMs[every.SelectedIndex] is var ms && ms != services.Talk.ReadEveryMs)
                services.SaveTalk(services.Talk with { ReadEveryMs = ms }, render: true);
        };
        var everyRow = MainWindow.Labeled("Read the screen", every);
        everyRow.Margin = new Thickness(0, 10, 0, 0);
        var everyNote = Note(model.EveryStatus, new Thickness(0, 4, 0, 0));
        AutomationProperties.SetAutomationId(everyNote, "ReadingEveryStatus");
        page.Children.Add(Card(Heading("Now"), now,
            HelpTip.Explain("While Martlet watches your screen, it reads the text on it off to the side, so replies never wait for it. " +
                "New text (a score, \"Victory\", a new message) makes a look more likely, and the text is never kept in the conversation.",
                new Thickness(0, 4, 0, 0), "Reading", "reading", wiki: "Vision#reading-the-text-on-your-screen"),
            everyRow, everyNote, last, test, text, Row(readNow)));

        page.Children.Add(poolList(new PoolListOptions
        {
            Area = PoolAreas.Reading,
            Heading = "Where it reads",
            Intro = "The places that read your screen, in order. The first free one reads, and the next one when it is busy or doesn't " +
                "answer; with nothing on, Martlet doesn't read your screen. Press Settings to choose how a place reads; this list " +
                "stays on this PC.",
            Wiki = "Vision#reading-the-text-on-your-screen",
            Status = model.MemberStatus,
            Settings = member => MemberSettings(model, member),
            NewMember = model.NewMember
        }));

        // Compare them: Windows OCR beside the Reading role (with the model the first computer in the list runs).
        var options = model.CompareOptions();
        var open = services.Comparing("Reading");
        var compare = PageButton(open ? "Hide the comparison" : "Compare Windows OCR and the Reading role", () =>
        {
            services.ToggleCompare("Reading");
            services.Redraw();
        }, link: true, id: "PickerCompare-Reading");
        page.Children.Add(open ? Card(Heading("Compare them"), Row(compare), MainWindow.PickerTable("Reading", options)) : Card(Heading("Compare them"), Row(compare)));
    }
}
