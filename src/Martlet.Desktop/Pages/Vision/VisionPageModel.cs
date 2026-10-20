using Martlet.Conversation;
using Martlet.Core.Settings;

namespace Martlet.Desktop;

/// <summary>Companion › Vision's decisions and settings, without WPF. The page makes one each time it draws: it reads how you
/// talk, the Thinking route and where pictures go once, says what each line of the page shows, and saves the choices the page
/// changes (talk-preferences.json). Which model takes each picture job is <see cref="SenseJobRouting"/>'s decision; the model
/// only shows it. <see cref="VisionPage"/> draws it, and <see cref="IVisionPageServices"/> is all it reads and changes in the app.</summary>
internal sealed class VisionPageModel
{
    /// <summary>What Martlet can look at, in the order of the What Martlet looks at choices.</summary>
    internal static readonly (WatchKind Kind, string Title, string Detail)[] Sources =
    [
        (WatchKind.ActiveWindow, "My active window", "The window you're using, without Martlet's own windows, password managers and private browsers."),
        (WatchKind.ActiveScreen, "My whole screen", "Every monitor with the taskbar and pop-ups, without Martlet's own windows, password managers and private browsers."),
        (WatchKind.Camera, "A camera", "A webcam, a capture card or your phone connected as a webcam."),
        (WatchKind.Url, "A phone or network camera address", "A snapshot or video stream address on your network.")
    ];

    private readonly IVisionPageServices services;

    internal VisionPageModel(IVisionPageServices services)
    {
        this.services = services;
        Prefs = services.Talk;
        Thinking = services.Settings.ThinkingRoute();
        Abilities = services.SavedModelAbilities();
        // Martlet can see when its pictures go to Thinking or to an image model of its own (docs/SENSE_MODELS.md).
        Image = services.ImageRoute(Thinking, Abilities);
        CanSee = Thinking is not null && Image.Path != SensePath.None;
        Source = SourceOf(Prefs, services.TypedAddress);
        Chosen = Source.IsScreen || Source.Id.Length > 0;
        Chattiness = ChattinessTags.Choice(Prefs.ScreenChattiness);
    }

    /// <summary>How you talk with Martlet, as saved when the page drew.</summary>
    internal TalkPreferences Prefs { get; }
    /// <summary>The conversation's own Thinking route, or null before Thinking is set up.</summary>
    internal SetupRoute? Thinking { get; }
    internal ModelAbilities Abilities { get; }
    /// <summary>Where a picture with a reply goes.</summary>
    internal SenseRoute Image { get; }
    /// <summary>Whether Martlet can see: Thinking is set up and its pictures go somewhere.</summary>
    internal bool CanSee { get; }
    /// <summary>What Martlet looks at.</summary>
    internal WatchSource Source { get; }
    /// <summary>Whether what it looks at is chosen: a screen, or a camera or an address.</summary>
    internal bool Chosen { get; }
    internal ChattinessChoice Chattiness { get; }

    /// <summary>What Martlet looks at: the choice in <paramref name="prefs"/>, with a camera address typed this session
    /// (<paramref name="typedAddress"/>, password included) before the saved one.</summary>
    internal static WatchSource SourceOf(TalkPreferences prefs, string? typedAddress) => (WatchKind)Math.Clamp(prefs.ScreenScope, 0, 3) switch
    {
        WatchKind.Camera => new(WatchKind.Camera, prefs.CameraId, prefs.CameraName),
        WatchKind.Url when WatchSource.Normalize(typedAddress ?? prefs.VideoAddress) is { Length: > 0 } address =>
            new(WatchKind.Url, address, WatchSource.SafeName(address)),
        var kind => new(kind)
    };

    // ---------- the Now card ----------

    /// <summary>The Now card's first line (VisionNow).</summary>
    internal string NowText => Prefs.Watch
        ? $"On: Martlet looks at {Source.Label} now and then (comments: {CommentsLabel(Chattiness)})."
        : $"Off: {OptionalExtras.OffMeans(CompanionTab.Vision)}.";

    /// <summary>What to choose before vision can look, or null.</summary>
    internal string? SourceMissing => Prefs.Watch && !Chosen
        ? Source.Kind == WatchKind.Camera ? "Choose a camera below." : "Enter the camera address below."
        : null;

    /// <summary>Whether Martlet can see, or what it needs (VisionStatus).</summary>
    internal string Advice => LiveConversationConfiguration.VisionAdvice(Thinking, Abilities, Image);

    /// <summary>Why Turn vision on is off, or null when it can be pressed.</summary>
    internal string? TurnOnBlocked => !CanSee ? Advice
        : Chosen ? null
        : Source.Kind == WatchKind.Camera ? "Choose a camera below first." : "Enter the camera address below first.";

    /// <summary>How the comments line reads: the level, or Martlet decides with the level it picked while a conversation runs.</summary>
    internal string CommentsLabel(ChattinessChoice choice) =>
        choice != ChattinessChoice.MartletDecides ? choice.ToString()
        : services.DecidedChattiness is { } live ? $"Martlet decides ({ChattinessTags.Name(live)} right now)" : "Martlet decides";

    // ---------- glances and the screen summary ----------

    /// <summary>The glances' status line (VisionGazeStatus).</summary>
    internal string GazeStatus()
    {
        var usual = services.UsualGaze;
        if (!Prefs.DecideGaze) return usual;
        if (!Prefs.Watch) return "Turn vision on above so Martlet can decide; until then, " + Lowered(usual);
        if (SourceOf(Prefs, services.TypedAddress) is { IsScreen: false }) return "Martlet decides only while it watches your screen, so with a camera, " + Lowered(usual);
        return services.GazeNow;
    }

    // "The character follows your mouse." in the middle of a sentence.
    private static string Lowered(string text) => text.Length == 0 ? text : char.ToLowerInvariant(text[0]) + text[1..];

    /// <summary>Which model makes the screen summaries (<see cref="SenseJobRouting"/>): the image model, else a Thinking pool
    /// model that sees.</summary>
    internal SenseDecision ScreenSummaryDecision() => services.SenseDecision(SenseJobKind.ScreenSummary);

    /// <summary>The screen summary's status, from the decision of which model makes them (<paramref name="decision"/>): it names
    /// that model and, when it is the image model, the Thinking pool model that takes over when it can't.</summary>
    internal static string ScreenSummaryStatus(bool on, bool vision, SenseDecision decision) =>
        !on ? "Off: replies know only the newest picture."
        : !vision ? "On, but nothing is summed up until you turn vision on above."
        : decision.Taker switch
        {
            SenseTaker.SenseModel => $"On: while Martlet watches, your image model ({decision.Name}) sums up what changed for your next message" +
                (decision.Then is { Taker: SenseTaker.PoolMember } then ? $", and the Thinking pool does it when it can't ({then.Name})." : "."),
            SenseTaker.PoolMember => $"On: while Martlet watches, a Thinking pool model that sees ({decision.Name}) sums up what changed for your next message.",
            _ => "Off for now: no image model and no Thinking pool model that sees takes them, so choose an image model above or add " +
                "one that sees in Companion › Thinking pool."
        };

    /// <summary>What Martlet captures and where it goes (VisionDisclosure), for what it looks at and how chatty it is, with the
    /// Thinking pool models that see (<paramref name="poolSees"/>) for the screen summary.</summary>
    internal string Disclosure(IReadOnlyList<SensePoolUsers.User> poolSees) =>
        LiveConversationConfiguration.ScreenDisclosure(Thinking, Chattiness, Source, Image) +
        SensePoolUsers.SummaryDisclosure(Prefs.ScreenSummary, poolSees, Image.Path == SensePath.Described);

    /// <summary>The Thinking pool models that see, which may make the screen summaries.</summary>
    internal IReadOnlyList<SensePoolUsers.User> PoolSees() =>
        SensePoolUsers.For(SenseKind.Image, ThinkingPoolSettings.Load(services.DataDirectory), Abilities);

    /// <summary>The cameras to choose from: the saved one, then those Windows offers (<paramref name="found"/>).</summary>
    internal List<CameraDevice> Cameras(IReadOnlyList<CameraDevice>? found)
    {
        var cameras = new List<CameraDevice>();
        if (Prefs.CameraId.Length > 0) cameras.Add(new(Prefs.CameraId, Prefs.CameraName.Length > 0 ? Prefs.CameraName : "Saved camera"));
        foreach (var camera in found ?? [])
        {
            cameras.RemoveAll(c => c.Id == camera.Id);
            cameras.Add(camera);
        }
        return cameras;
    }

    // ---------- the choices the page saves (talk-preferences.json) ----------

    /// <summary>Turns vision off or on, draws the page again and says so.</summary>
    internal void SetWatch(bool on)
    {
        services.SaveTalk(services.Talk with { Watch = on }, render: true);
        services.ShowStatus(on ? "Vision is on: press Start watching on Home or in the talk window when you want Martlet to look."
            : "Vision is off.");
    }

    internal void ChooseSource(WatchKind kind) => services.SaveTalk(services.Talk with { ScreenScope = (int)kind }, render: true);

    internal void ChooseCamera(CameraDevice picked)
    {
        if (picked.Id != services.Talk.CameraId) services.SaveTalk(services.Talk with { CameraId = picked.Id, CameraName = picked.Name }, render: true);
    }

    /// <summary>Keeps a typed camera address for this session (with its password) and saves it without the password.</summary>
    internal void TypeAddress(string typed)
    {
        services.TypedAddress = typed.Length == 0 ? null : typed;
        services.SaveTalk(services.Talk with { VideoAddress = WatchSource.WithoutCredentials(WatchSource.Normalize(typed)) });
    }

    internal void ChooseHomeCamera(Uri snapshot)
    {
        services.TypedAddress = null;
        services.SaveTalk(services.Talk with { VideoAddress = snapshot.AbsoluteUri }, render: true);
    }

    internal void SetChattiness(int choice) => services.SaveTalk(services.Talk with { ScreenChattiness = choice }, render: true);

    internal void SetDecideGaze(bool decide)
    {
        if (services.Talk.DecideGaze != decide) services.SaveTalk(services.Talk with { DecideGaze = decide }, render: true);
    }

    internal void SetScreenSummary(bool on)
    {
        if (services.Talk.ScreenSummary != on) services.SaveTalk(services.Talk with { ScreenSummary = on }, render: true);
    }
}
