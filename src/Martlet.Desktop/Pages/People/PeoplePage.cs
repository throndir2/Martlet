using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;

namespace Martlet.Desktop;

/// <summary>Companion › People, the UI only: Now (which the window makes, as Home shares it), Recognize voices (on or off, where it
/// runs and keeping clips), the Voice recognition list of places, Your computers (sharing the list) and Voices Martlet knows, with
/// each voice's names, clips and actions (PeoplePage.Voices.cs). What each line says and each change is in
/// <see cref="PeoplePageModel"/>; what the page needs from the app is <see cref="IPeoplePageServices"/>.</summary>
internal sealed partial class PeoplePage(IPeoplePageServices services)
{
    private PeoplePageModel model = null!;

    /// <summary>Draws the page's cards into <paramref name="page"/>.</summary>
    internal void Render(Panel page)
    {
        model = new PeoplePageModel(services);
        page.Children.Add(services.NowCard());
        page.Children.Add(RecognitionCard());
        if (services.Voices.Available) page.Children.Add(services.RecognitionPoolCard());
        page.Children.Add(SharingCard());
        page.Children.Add(VoiceListCard());
    }

    /// <summary>People's main choice: whether Martlet recognizes voices (with an explicit Off), then whether it keeps clips of
    /// voices you haven't named.</summary>
    private Border RecognitionCard()
    {
        var voices = services.Voices;
        var children = new List<UIElement>
        {
            HelpTip.After(Heading("Recognize voices"), "recognizing voices",
                "Voice matching happens on this PC; the voiceprints come from this PC or a computer in the Where it runs list.",
                "PeopleRecognize", wiki: "Listening-and-Voice-ID#people")
        };
        if (!voices.Available)
        {
            children.Add(Warning("Voice recognition needs Martlet's data folder, which isn't available."));
            return Card([.. children]);
        }
        children.AddRange(MainWindow.OnOffChoices("PeopleRecognize", "PeopleRecognizeOff", "Recognize voices in conversations",
            "Martlet learns people's voices and names as you talk.",
            "Martlet doesn't check who is talking, and saved voices are kept.", voices.Enabled,
            voices.Included ? null : "Reinstall Martlet to recognize voices: its voice recognition files are missing.",
            on => { if (on != voices.Enabled) model.SetRecognition(on); }));
        children.Add(services.RecognitionWhere());
        var clips = new CheckBox
        {
            Content = $"Keep the last {VoiceClips.MaximumClips} clips of voices you haven't named, so you can hear who they are",
            IsChecked = voices.Clips.Enabled, Margin = new Thickness(0, 6, 0, 0)
        };
        clips.ToolTip = "Clips stay on the computer that heard them and are deleted once you name the voice, mark it as yours or " +
            "forget it. This choice is the same on all your computers.";
        AutomationProperties.SetAutomationId(clips, "PeopleKeepClips");
        clips.Checked += (_, _) => model.SetKeepClips(true);
        clips.Unchecked += (_, _) => model.SetKeepClips(false);
        children.Add(clips);
        return Card([.. children]);
    }

    private Border SharingCard()
    {
        var sync = PageButton(services.SyncBusy ? "Syncing..." : "Sync now", services.SyncNow, link: true, id: "PeopleSync");
        sync.IsEnabled = !services.SyncBusy;
        var syncStatus = Note(services.SyncStatus, new Thickness(0, 6, 0, 0));
        AutomationProperties.SetAutomationId(syncStatus, "PeopleSyncStatus");
        return Card(Heading("Your computers"),
            HelpTip.Explain("The people Martlet recognizes by voice, and their names, are always shared with all your computers. " +
                "This doesn't depend on Keep Martlet the same on all my computers, and hosts a friend shares never get them.",
                new Thickness(0, 0, 0, 0), "PeopleSharing", "sharing voices", wiki: "Listening-and-Voice-ID#sharing-with-your-computers"),
            syncStatus,
            Row(sync));
    }

    private Border VoiceListCard()
    {
        var voices = model.Known;
        var heading = Heading(model.CountText);
        AutomationProperties.SetAutomationId(heading, "PeopleVoiceCount");
        var children = new List<UIElement> { heading };
        if (voices.Count == 0)
        {
            children.Add(Note("None yet: talk to Martlet with recognition on, and new voices appear here.", new Thickness(0, 0, 0, 0)));
            return Card([.. children]);
        }
        children.Add(HelpTip.Explain("Type a name to say who a voice is, and add any other names they go by. " +
            "Click a name to make it the one Martlet uses; a name with ? was only heard in conversation.",
            new Thickness(0, 0, 0, 4), "PeopleNames", "naming voices", wiki: "Listening-and-Voice-ID#people"));
        var links = Note(model.LinkText, new Thickness(0, 0, 0, 4));
        AutomationProperties.SetAutomationId(links, "PeopleLinkStatus");
        children.Add(links);
        foreach (var voice in voices) children.Add(VoiceEntry(voice, voices));
        children.Add(Row(PageButton("Forget all voices", model.ForgetAll, link: true, id: "PeopleForgetAll")));
        return Card([.. children]);
    }
}
