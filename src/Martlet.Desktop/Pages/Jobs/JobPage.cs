using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Martlet.Core.Settings;

namespace Martlet.Desktop;

/// <summary>Companion's job pages: Thinking, Listening and Voice. Each asks where the job runs (this PC by default, another of
/// your computers, or a cloud provider) and shows only that place's card, then the job's own cards. The Now card is in
/// JobPage.Now.cs, Your computers in JobPage.Computers.cs and the cloud provider in JobPage.Cloud.cs; the place cards and the
/// job's other cards come from the window (<see cref="IJobPageServices"/>).</summary>
internal sealed partial class JobPage(IJobPageServices services)
{
    /// <summary>Draws the page of <paramref name="section"/> (Thinking, Listening or Voice) into <paramref name="page"/>.</summary>
    internal void Render(Panel page, CompanionTab section)
    {
        var role = CompanionPages.JobRole(section)!.Value;
        var job = HostJob.For(role)!;
        var route = services.Settings?.Setup?.Routes.FirstOrDefault(r => r.Role == role);
        var thisPc = services.ThisPcHost();
        var networkHost = services.NetworkHost(job.Job);
        var current = JobPageModel.PlaceInUse(role, route, networkHost, thisPc?.HostId);
        var place = services.ShownPlace(section) ?? current;

        page.Children.Add(NowCard(section, job, route));
        // FIXTURE (MARTLET_SIMULATE_SITUATION): buttons that start a simulated game or make a host simulated away.
        if (role == SetupRole.Llm && services.SituationFixtureCard() is { } fixture) page.Children.Add(fixture);

        // Speaking and Listening: the pool list replaces the single place; the chosen member's own card shows below it.
        if (role != SetupRole.Llm) page.Children.Add(services.JobPoolCard(section, role, route));
        else page.Children.Add(WhereItRunsCard(section, section.ToString(), "Where it runs", null, route is null ? null : current, place,
            (MainWindow.JobPlace.ThisPc, "This PC (recommended)", role switch
            {
                SetupRole.Llm => "Ollama or your own model app on this PC's graphics card: private and free.",
                SetupRole.Tts => "A voice engine on this PC's NVIDIA graphics card, or Chatterbox Nano on the processor; audio stays here.",
                _ => "Parakeet on the processor (no setup) or Whisper on the graphics card; your voice stays here."
            }),
            (MainWindow.JobPlace.Computer, "Another of your computers",
                $"A paired computer does {job.Job} with its own graphics card, so this PC stays free."),
            (MainWindow.JobPlace.Cloud, "A cloud provider", role switch
            {
                SetupRole.Llm => "NVIDIA Build (free key), OpenAI, OpenRouter and others; your messages leave this PC, and some charge.",
                SetupRole.Tts => "OpenAI's voices, or ElevenLabs with your cloned voice: no graphics card needed, but reply text leaves " +
                    "this PC and requests cost money.",
                _ => "OpenAI with your API key; your recordings leave this PC, and requests cost money."
            })));

        if (place == MainWindow.JobPlace.ThisPc && role == SetupRole.Llm)
        {
            var app = services.ModelApp(route);
            page.Children.Add(services.LocalAppCard(route, app));
            page.Children.Add(app == MainWindow.LocalApp.Ollama ? services.LocalThinkingCard(route) : services.LocalServerCard(route));
        }
        else
            page.Children.Add(place switch
            {
                MainWindow.JobPlace.ThisPc when role == SetupRole.Tts => services.VoiceEnginesCard(route, thisPc, onThisPc: true),
                MainWindow.JobPlace.Computer when role == SetupRole.Tts => services.VoiceEnginesCard(route, thisPc, onThisPc: false),
                MainWindow.JobPlace.ThisPc => services.LocalListeningCard(route, thisPc),
                MainWindow.JobPlace.Computer => ComputersCard(job, route, role == SetupRole.Llm ? null : thisPc),
                _ => CloudCard(section, job, route)
            });

        // Thinking: the computers a reply may go to when the conversation's own model is busy (none by default).
        if (role == SetupRole.Llm) page.Children.Add(services.JobPoolCard(section, role, route));

        // Chatterbox Original's General and Expressive style, while it speaks or is the chosen engine.
        if (section == CompanionTab.Voice && place != MainWindow.JobPlace.Cloud &&
            (SpeechEngines.ForRoute(route?.GatewaySnapshot?.RouteId) == SpeechEngines.ChatterboxOriginal ||
             SpeakingEngineChoice.Current == SpeechEngines.ChatterboxOriginal))
            page.Children.Add(services.ChatterboxStyleCard());

        // No hosted provider has a key: a free one from NVIDIA Build keeps Martlet able to reply when your computers are offline or
        // have no room for thinking. It goes to If Thinking fails while Thinking has a place (a local model answers sooner), and
        // to Thinking itself when nothing does it.
        if (section == CompanionTab.Thinking && place != MainWindow.JobPlace.Cloud && services.OffersFreeKey)
        {
            var nobody = route is null && networkHost is null || services.CannotReply;
            var tip = Note(FreeKeyPrompt.Tip, new Thickness(0, 0, 0, 0));
            AutomationProperties.SetAutomationId(tip, "FreeKeyTip-Thinking");
            page.Children.Add(Card(Heading(FreeKeyPrompt.Title), tip,
                Row(PageButton(FreeKeyPrompt.AddLabel, () => services.OpenFreeKey(nobody), id: "FreeKeyAdd-Thinking"),
                    PageButton(FreeKeyPrompt.GetLabel, () => services.OpenKeyPage(tip), link: true, id: "FreeKeyGet-Thinking"))));
        }

        if (role == SetupRole.Llm) page.Children.Add(services.FallbackCard());

        // The voices the self-hosted engines copy from your recordings, wherever one can speak: this PC or another of your
        // computers. A cloud provider has its own voices.
        if (section == CompanionTab.Voice && place != MainWindow.JobPlace.Cloud) page.Children.Add(services.VoicesCard(route));

        if (section == CompanionTab.Voice)
        {
            page.Children.Add(AudioCard(output: true));
            page.Children.Add(services.SpeakRepliesCard());
            page.Children.Add(services.QuickSoundsCard());
        }
        if (section == CompanionTab.Listening)
        {
            page.Children.Add(AudioCard(output: false));
            page.Children.Add(services.TalkModeCard());
            page.Children.Add(services.EchoCard());
            page.Children.Add(services.PcAudioCard());
            // How you say things (the audio model, and whether a model hears your recording) is on Companion › Hearing.
            page.Children.Add(Card(Heading("How Martlet hears your tone"),
                Row(PageButton("Companion › Hearing", () => services.OpenCompanion(CompanionTab.Hearing), link: true, id: "ListeningOpenHearing"))));
            page.Children.Add(Card(Heading("Who is talking"),
                Note(services.VoiceRecognitionOn
                    ? "Voice recognition is on; manage known voices on People."
                    : "Turn on voice recognition so Martlet can learn who's speaking.",
                    new Thickness(0, 0, 0, 0)),
                Row(PageButton("Open People", () => services.OpenCompanion(CompanionTab.People), link: true, id: "OpenPeople"))));
        }

        if (services.OldKeysCard(section, role) is { } oldKeys) page.Children.Add(oldKeys);
    }

    /// <summary>The "Where it runs" chooser, the same on every job: one option per place, the one in use marked. Choosing a place
    /// shows that place's card below it; the card's own button commits the choice.</summary>
    private Border WhereItRunsCard(CompanionTab section, string id, string heading, string? note, MainWindow.JobPlace? inUse,
        MainWindow.JobPlace place, params (MainWindow.JobPlace Value, string Label, string Detail)[] options)
    {
        var where = new StackPanel();
        where.Children.Add(Heading(heading));
        if (note is not null) where.Children.Add(Note(note, new Thickness(0, 0, 0, 10)));
        foreach (var (value, label, detail) in options)
        {
            var text = new StackPanel();
            text.Children.Add(new TextBlock { Text = label + (value == inUse ? "  \u00b7  in use" : ""),
                FontSize = 15, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
            text.Children.Add(Note(detail, new Thickness(0, 2, 0, 0)));
            var option = new RadioButton { Content = text, GroupName = "Place-" + id, IsChecked = value == place, Margin = new Thickness(0, 0, 0, 10) };
            AutomationProperties.SetName(option, $"{label}: {detail}");
            AutomationProperties.SetAutomationId(option, $"Place-{id}-{value}");
            option.Checked += (_, _) => services.ShowPlace(section, value);
            where.Children.Add(option);
        }
        return Card(where);
    }

    /// <summary>The speakers (Voice) or microphone (Listening) Martlet uses, with Test or change.</summary>
    private Border AudioCard(bool output)
    {
        var choice = output ? services.Settings?.Audio?.Output : services.Settings?.Audio?.Input;
        var checkpoint = choice?.Checkpoint;
        var missing = services.AudioMissing(output);
        var what = output ? "Speakers" : "Microphone";
        var device = choice?.EndpointId is null
            ? output ? "your Windows default speakers" : "your Windows default microphone"
            : $"\"{choice.DisplayName}\"";
        var tested = checkpoint is not null ? $" (tested on {checkpoint.TestedAt.ToLocalTime():d})" : "";
        var text = missing
            ? choice?.EndpointId is null
                ? output ? "No speakers or headset found: plug them in or turn them on in Windows Sound settings."
                    : "No microphone found: plug one in or turn it on in Windows Sound settings."
                : $"{device} isn't connected: plug it in or pick another."
            : output ? $"Martlet speaks through {device}{tested}."
            : $"Martlet listens with {device}{tested} when you talk in the talk window.";
        var line = Note(text, new Thickness(0, 0, 0, 8));
        if (missing) line.SetResourceReference(TextBlock.ForegroundProperty, "WarningBrush");
        return Card(Heading(what), line,
            Row(PageButton(missing ? $"Fix {what.ToLowerInvariant()}" : $"Test or change {what.ToLowerInvariant()}",
                () => services.RunNodeAction(NodeAction.AudioSetup), primary: missing, id: "OpenAudioSetup")));
    }
}
