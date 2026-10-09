using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Martlet.Avatar.Hosting;
using Martlet.Conversation;
using Martlet.Discord;
using Martlet.Messaging;

namespace Martlet.Desktop;

/// <summary>The Now line of the Companion pages that aren't jobs or optional extras (Speech bubbles, Emotes and motions, Eyes,
/// Touch, People, Tools, Smart home, Discord and Messaging): what the page uses now, in one line, and any problem that stops it,
/// from what Martlet already knows (drawing it fetches nothing and is never on a reply's path). The words come from the pure
/// builders here, so tests read them. Each line's automation ID is <c>&lt;Page&gt;Now</c>, its problem's
/// <c>&lt;Page&gt;NowProblem</c>. Also the explicit On/Off main choice those pages share.</summary>
public partial class MainWindow
{
    // ---------- the words ----------

    private static string Plural(int count, string word) => $"{count} {word}{(count == 1 ? "" : "s")}";

    /// <summary>Companion › Speech bubbles: where Martlet's spoken words show.</summary>
    internal static string SpeechBubblesNow(SpeechDisplayPreferences prefs) =>
        !prefs.SpeechBubbles && !prefs.Subtitles ? "Off: no speech bubbles or subtitles."
        : !prefs.SpeechBubbles ? "Subtitles at the bottom of the screen; speech bubbles off."
        : $"Speech bubbles: {(prefs.StaticBubble ? "in one place" : "beside the character")}; subtitles {(prefs.Subtitles ? "on" : "off")}.";

    /// <summary>Why no speech bubble shows now, or null.</summary>
    internal static string? SpeechBubblesProblem(SpeechDisplayPreferences prefs, bool showing) =>
        prefs.SpeechBubbles && !showing ? "The character is hidden, so no speech bubble shows until it does." : null;

    /// <summary>Companion › Emotes and motions: how many of the model's emotes and motions, and of Martlet's own gestures, are on,
    /// and the combos that are on.</summary>
    internal static string EmotesNow(int emotesOn, int emotes, int motionsOn, int motions, int combos, int gesturesOn = 0, int gestures = 0)
    {
        static string Part(int on, int all, string word) => all == 0 ? $"no {word}s" : on == all ? Plural(on, word) : $"{on} of {all} {word}s";
        var parts = new List<string> { Part(emotesOn, emotes, "emote"), Part(motionsOn, motions, "motion") };
        if (gestures > 0) parts.Add(Part(gesturesOn, gestures, "Martlet gesture"));
        var line = $"{string.Join(", ", parts.Take(parts.Count - 1))} and {parts[^1]} on, {(combos == 0 ? "no combos" : Plural(combos, "combo"))}.";
        return char.ToUpperInvariant(line[0]) + line[1..];
    }

    /// <summary>Companion › Eyes: where the character usually looks (<paramref name="usual"/>; null: as the personality decides,
    /// <paramref name="personality"/>, else your mouse), whether replies may change it, and where its eyes come from.</summary>
    internal static string EyesNow(GazeMode? usual, GazeMode? personality, bool free, string? eyesFrom, bool measured, bool showing)
    {
        static string Lower(string text) => text.Length == 0 ? text : char.ToLowerInvariant(text[0]) + text[1..];
        var gaze = usual is { } chosen ? Lower(CharacterGaze.Label(chosen))
            : Lower(CharacterGaze.Label(personality ?? GazeMode.Mouse)) + ", as the personality decides";
        var eyes = eyesFrom switch
        {
            RendererEyesFrom.Mesh => "from the model's own meshes",
            RendererEyesFrom.Bones => "from the model's own eye bones",
            RendererEyesFrom.Vision => "measured with vision",
            RendererEyesFrom.Estimate => "only estimated",
            _ when measured => "measured with vision",
            _ => showing ? "being read" : "read when the character shows"
        };
        return $"Where it looks: {gaze}{(free ? "; replies may change it" : "")}. Eyes: {eyes}.";
    }

    /// <summary>Companion › Touch: how many touch zones are on and who placed them, and what a touch does while Martlet talks.</summary>
    internal static string TouchNow(CharacterTouchZoneSettings? zones, TouchInterrupts interrupts)
    {
        var all = zones?.Zones.Count ?? 0;
        var on = zones is null ? 0 : zones.Zones.Count(zones.Active);
        var placed = zones?.DetectedBy switch
        {
            CharacterTouchZoneSettings.ByVision => ", found by the Thinking model",
            CharacterTouchZoneSettings.ByEstimate => ", a first guess",
            CharacterTouchZoneSettings.ByOwner => ", placed by you",
            _ => ""
        };
        var count = all == 0 ? "No touch zones yet, so a click reacts to the rough part it lands on"
            : (on == all ? Plural(on, "touch zone") : $"{on} of {all} touch zones") + " on" + placed;
        var talking = interrupts switch
        {
            TouchInterrupts.Any => "a touch stops Martlet mid-sentence",
            TouchInterrupts.Intimate => "only an intimate touch stops Martlet mid-sentence",
            _ => "touches wait until Martlet finishes speaking"
        };
        return $"{count}; {talking}.";
    }

    /// <summary>Companion › People: whether Martlet recognizes voices, and how many it knows.</summary>
    internal static string PeopleNow(bool enabled, int voices, int named) =>
        enabled
            ? $"Voice recognition on: {(voices == 0 ? "no voices learned yet" : $"{Plural(voices, "voice")} known, {named} with a name")}."
            : $"Off: Martlet doesn't check who is talking{(voices == 0 ? "" : $"; {Plural(voices, "saved voice")} kept")}.";

    /// <summary>Companion › Tools: how many MCP servers are on, and whether the terminal is.</summary>
    internal static string ToolsNow(int serversOn, int servers, bool terminal)
    {
        var mcp = servers == 0 ? "no MCP servers"
            : serversOn == servers ? Plural(servers, "MCP server") + " on"
            : serversOn == 0 ? Plural(servers, "MCP server") + " off"
            : $"{serversOn} of {servers} MCP servers on";
        return serversOn == 0 && !terminal ? $"Off: {mcp}, and the terminal is off." : $"Tools: {mcp}, the terminal {(terminal ? "on" : "off")}.";
    }

    /// <summary>Companion › Smart home: which Home Assistant Martlet is connected to and what it may do with it.</summary>
    internal static string SmartHomeNow(HomePreferences saved, bool connected)
    {
        if (!connected) return "Off: Home Assistant isn't connected.";
        var where = (saved.LocationName.Length > 0 ? saved.LocationName : "Home Assistant") + (saved.Address.Length > 0 ? $" at {saved.Address}" : "");
        if (!saved.Control) return $"Off: connected to {where}, but Martlet doesn't control your smart home.";
        return $"Smart home: connected to {where}; Martlet may control lights, switches, climate, media, scenes and blinds" +
            (saved.AllowSensitive ? ", and locks, doors and alarms after a Yes each time" : "") +
            (saved.ModelTools ? ". Flexible requests are on." : ".");
    }

    /// <summary>Companion › Discord: whether Martlet's bot is on Discord, and in how many servers.</summary>
    internal static string DiscordNow(DiscordPreferences saved, DiscordBotStatus status) =>
        !saved.Configured ? "Off: Martlet's Discord bot isn't set up yet."
        : !saved.Enabled ? "Off: Martlet doesn't connect to Discord."
        : status.State switch
        {
            DiscordBotState.Online => $"Discord: the bot{(status.BotName is { Length: > 0 } name ? $" {name}" : "")} is connected to " +
                $"{DiscordSetup.Servers(status.Servers)}.",
            DiscordBotState.Connecting => "Discord: connecting...",
            DiscordBotState.Failed => "Discord: on, but not connected.",
            _ => "Discord: on, but not connected yet."
        };

    /// <summary>Why the Discord bot isn't connected while it is on, or null.</summary>
    internal static string? DiscordProblem(DiscordPreferences saved, DiscordBotStatus status) =>
        saved.Configured && saved.Enabled && status.State == DiscordBotState.Failed ? status.Problem ?? "Discord closed the connection." : null;

    /// <summary>Companion › Messaging: what Martlet does with each messaging app on this PC.</summary>
    internal static string MessagingNow(IReadOnlyList<(string Name, ChannelPreferences Saved, MessagingStatus Status, bool Running)> apps)
    {
        if (!apps.Any(a => a.Saved is { Connected: true, Enabled: true })) return "Off: Martlet answers no messaging app.";
        static string State(ChannelPreferences saved, MessagingStatus status, bool running)
        {
            if (!saved.Connected) return "not set up";
            if (!saved.Enabled) return "off";
            var chats = saved.Chats.Count == 0 ? "no chats paired yet" : Plural(saved.Chats.Count, "paired chat");
            return status.State switch
            {
                MessagingState.Running => $"answering {saved.Handle} ({chats})",
                MessagingState.Connecting => $"connecting {saved.Handle}",
                MessagingState.Retrying => $"can't reach {saved.Handle} right now",
                MessagingState.Failed => $"{saved.Handle} stopped",
                _ when !running => "not running on this PC",
                _ => $"starting {saved.Handle}"
            };
        }
        return string.Join("; ", apps.Select(a => $"{a.Name}: {State(a.Saved, a.Status, a.Running)}")) + ".";
    }

    /// <summary>The first messaging app that is on but can't answer, and why, or null.</summary>
    internal static string? MessagingProblem(IReadOnlyList<(string Name, ChannelPreferences Saved, MessagingStatus Status, bool Running)> apps) =>
        apps.FirstOrDefault(a => a.Saved is { Connected: true, Enabled: true } && a.Status.State is MessagingState.Retrying or MessagingState.Failed) is
            { Name: not null } failing ? $"{failing.Name}: {failing.Status.Problem ?? "it stopped."}" : null;

    // ---------- the cards ----------

    /// <summary>A Now card whose line and problem change while the page shows (<see cref="ShowNow"/>): the problem's text block
    /// is always there, collapsed while there is no problem.</summary>
    private (Border Card, TextBlock Text, TextBlock Problem) LiveNowCard(string id, string text, string? problem)
    {
        var now = new StackPanel();
        now.Children.Add(Heading("Now"));
        var status = new TextBlock { FontSize = 15, TextWrapping = TextWrapping.Wrap };
        AutomationProperties.SetAutomationId(status, id);
        AutomationProperties.SetLiveSetting(status, AutomationLiveSetting.Polite);
        now.Children.Add(status);
        var problemText = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0) };
        problemText.SetResourceReference(TextBlock.ForegroundProperty, "WarningBrush");
        AutomationProperties.SetAutomationId(problemText, id + "Problem");
        now.Children.Add(problemText);
        ShowNow(status, problemText, text, problem);
        return (Card(now), status, problemText);
    }

    private static void ShowNow(TextBlock status, TextBlock problemText, string text, string? problem)
    {
        status.Text = text;
        problemText.Text = problem ?? "";
        problemText.Visibility = problem is null ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>The main choice of a page whose feature can be off: the feature (<paramref name="id"/>) and an explicit Off
    /// (<paramref name="offId"/>), each saying what it means. <paramref name="blocked"/> says why the feature can't be chosen yet
    /// (its choice is then disabled); <paramref name="set"/> saves a choice.</summary>
    private static RadioButton[] OnOffChoices(string id, string offId, string title, string detail, string offDetail, bool on, string? blocked,
        Action<bool> set)
    {
        var yes = Choice(id, title, blocked is null || on ? detail : $"{detail} {blocked}", on, id);
        yes.IsEnabled = blocked is null || on;
        var no = Choice(id, "Off", offDetail, !on, offId);
        yes.Checked += (_, _) => set(true);
        no.Checked += (_, _) => set(false);
        return [yes, no];
    }

    // ---------- Speech bubbles ----------

    private (TextBlock Text, TextBlock Problem)? speechBubblesNow;
    private readonly Dictionary<(bool Bubbles, bool Subtitles), RadioButton> speechDisplayChoices = [];

    /// <summary>Companion › Speech bubbles: Now, where Martlet's words show (with Off), then where the bubble goes.</summary>
    private void RenderSpeechBubblesTab(Panel page)
    {
        var prefs = captions.Preferences;
        var (card, text, problem) = LiveNowCard("SpeechBubblesNow", SpeechBubblesNow(prefs), SpeechBubblesProblem(prefs, avatar.IsShowing));
        speechBubblesNow = (text, problem);
        page.Children.Add(card);
        page.Children.Add(SpeechDisplayChoiceCard(prefs));
        page.Children.Add(SpeechDisplayCard());
    }

    /// <summary>Where Martlet's spoken words show: speech bubbles, subtitles, both or Off. The same saved choices as the
    /// character window's, applied from Martlet's next sentence.</summary>
    private Border SpeechDisplayChoiceCard(SpeechDisplayPreferences prefs)
    {
        speechDisplayChoices.Clear();
        var stack = new List<UIElement> { Heading("Where Martlet's words show") };
        foreach (var (bubbles, subtitles, title, detail, id) in new[]
        {
            (true, false, "Speech bubbles", "Beside the character, while it shows.", "SetupCharacterSpeechBubbles"),
            (false, true, "Subtitles", "At the bottom of the active screen. Subtitles are hidden from screen capture.", "SetupCharacterSubtitles"),
            (true, true, "Speech bubbles and subtitles", "Both: a bubble beside the character and subtitles at the bottom of the screen.",
                "SetupCharacterSpeechBoth"),
            (false, false, "Off", "No speech bubbles or subtitles. The talk window still shows what Martlet says.", "SetupCharacterSpeechOff")
        })
        {
            var option = Choice("SpeechDisplay", title, detail, prefs.SpeechBubbles == bubbles && prefs.Subtitles == subtitles, id);
            option.Checked += (_, _) => ChooseSpeechDisplay(bubbles, subtitles);
            speechDisplayChoices[(bubbles, subtitles)] = option;
            stack.Add(option);
        }
        return Card([.. stack]);
    }

    private void ChooseSpeechDisplay(bool bubbles, bool subtitles)
    {
        if (showingSpeechDisplay) return;
        var prefs = captions.Preferences;
        if (prefs.SpeechBubbles == bubbles && prefs.Subtitles == subtitles) return;
        if (!captions.Update(prefs with { SpeechBubbles = bubbles, Subtitles = subtitles }))
            ShowSpeechDisplay("Applied for now, but couldn't save your speech display settings.");
    }

    /// <summary>Follows the saved choices on the page's main choice and Now line, whichever window changed them.</summary>
    private void ShowSpeechDisplayChoice(SpeechDisplayPreferences prefs)
    {
        if (speechDisplayChoices.TryGetValue((prefs.SpeechBubbles, prefs.Subtitles), out var chosen)) chosen.IsChecked = true;
        if (speechBubblesNow is var (text, problem)) ShowNow(text, problem, SpeechBubblesNow(prefs), SpeechBubblesProblem(prefs, avatar.IsShowing));
    }

    // ---------- Emotes and motions, Eyes and Touch ----------

    /// <summary>Companion › Emotes and motions: Now, then the emotes, motions and combos.</summary>
    private void RenderEmotesTab(Panel page)
    {
        var actions = CharacterActionsCard();
        page.Children.Add(EmotesNowCard());
        page.Children.Add(actions);
    }

    private Border EmotesNowCard()
    {
        if (characterActions.Problem is { } problem)
            return PageNowCard("Martlet couldn't read this character's emotes and motions.", null, "EmotesNow", problem);
        if (characterActions.Current is not { } catalog) return PageNowCard("Reading the character's emotes and motions...", null, "EmotesNow");
        int Count(CharacterActionKind kind, bool on) => catalog.Entries.Count(e => e.Source.Kind == kind && (!on || e.Action.Enabled));
        return PageNowCard(EmotesNow(Count(CharacterActionKind.Expression, true), Count(CharacterActionKind.Expression, false),
                Count(CharacterActionKind.Motion, true), Count(CharacterActionKind.Motion, false), catalog.Combos.Count(c => c.Enabled),
                Count(CharacterActionKind.Gesture, true), Count(CharacterActionKind.Gesture, false)),
            null, "EmotesNow", avatar.IsShowing ? null : "The character is hidden, so replies don't play them until it shows.");
    }

    private Border EyesNowCard()
    {
        var prefs = Talk;
        var personality = characterTemperaments.For(homeSettings?.Companion?.ActivePersonaId)?.Gaze;
        var showing = avatar.IsShowing;
        var from = showing ? avatar.EyesFrom : null;
        var problem = from == RendererEyesFrom.Estimate && characterEyes.Current is null && !EyesSight().CanSee
            ? "The eyes are only estimated and no model that can see pictures is set up to measure them, so emotes drawn over the eyes may not fit."
            : null;
        return PageNowCard(EyesNow(prefs.GazeUsual, personality, prefs.GazeFree, from, characterEyes.Current is not null, showing), null, "EyesNow", problem);
    }

    private Border TouchNowCard() => characterActions.Current is null
        ? PageNowCard("Reading the character...", null, "TouchNow")
        : PageNowCard(TouchNow(characterTouchZones.Current, Talk.TouchInterrupts), null, "TouchNow",
            avatar.IsShowing ? null : "The character is hidden, so touches do nothing until it shows.");

    // ---------- People, Tools, Discord and Messaging ----------

    private Border PeopleNowCard()
    {
        if (!localVoices.Available) return PageNowCard("Off: voice recognition is unavailable.", null, "PeopleNow",
            "Voice recognition needs Martlet's data folder, which isn't available.");
        var voices = localVoices.Roster.Live;
        var problem = localVoices.Enabled && !localVoices.Included
            ? "Voice recognition files are missing from this Martlet installation. Reinstall Martlet to recognize voices."
            : localVoices.LoadError;
        return PageNowCard(PeopleNow(localVoices.Active, voices.Count, voices.Count(v => v.Named)), null, "PeopleNow", problem);
    }

    private Border ToolsNowCard(McpToolService service)
    {
        var terminal = service.Terminal;
        string? problem = null;
        if (service.ConfigurationError is { } error) problem = $"mcp.json has a problem, so no servers run: {error}";
        else if (terminal.Enabled && TerminalStatus(service, terminal) is (var why, true)) problem = why;
        else if (service.HasEnabledServers && homeSettings?.Setup?.Routes.FirstOrDefault(r => r.Role == Martlet.Core.Settings.SetupRole.Llm) is
                 { RouteType: not (Martlet.Core.Settings.SetupRouteType.OpenAi or Martlet.Core.Settings.SetupRouteType.ChatCompletions) })
            problem = "Your Thinking model can't use tools here. Use OpenAI or a Chat Completions endpoint (such as Ollama on this PC) in Companion › Thinking.";
        return PageNowCard(ToolsNow(service.Servers.Count(s => !s.Disabled), service.Servers.Count, terminal.Enabled), null, "ToolsNow", problem);
    }

    private Border DiscordNowCard(DiscordPreferences saved, DiscordBotStatus status) =>
        PageNowCard(DiscordNow(saved, status), null, "DiscordNow", DiscordProblem(saved, status));

    private IReadOnlyList<(string Name, ChannelPreferences Saved, MessagingStatus Status, bool Running)> MessagingApps(MessagingPreferences saved) =>
        [.. new[] { MessagingApp.Telegram, MessagingApp.WhatsApp }.Select(app =>
            (MessagingService.Name(app), saved[app], messaging.Status(app), messaging.Running(app)))];

    private Border MessagingNowCard(MessagingPreferences saved)
    {
        var apps = MessagingApps(saved);
        return PageNowCard(MessagingNow(apps), null, "MessagingNow", MessagingProblem(apps));
    }

    /// <summary>Companion › Messaging's main choice: for each app, whether Martlet answers it on this PC (On) or not (Off). An
    /// app that isn't set up yet can only be Off.</summary>
    private Border MessagingChoiceCard(MessagingPreferences saved)
    {
        var stack = new List<UIElement>
        {
            Heading("Answer messages"),
            Note("Choose which apps Martlet answers on this PC. Set an app up below before you turn it on.", new Thickness(0, 0, 0, 4))
        };
        foreach (var app in new[] { MessagingApp.Telegram, MessagingApp.WhatsApp })
        {
            var name = MessagingService.Name(app);
            var prefs = saved[app];
            var prefix = app == MessagingApp.WhatsApp ? "MessagingWhatsApp" : "MessagingTelegram";
            var on = prefs.Enabled && prefs.Connected;
            var title = new TextBlock { Text = name, FontWeight = FontWeights.SemiBold, Width = 110, VerticalAlignment = VerticalAlignment.Center };
            var yes = new RadioButton { Content = "On", GroupName = prefix + "Answer", IsChecked = on, IsEnabled = prefs.Connected,
                Margin = new Thickness(0, 0, 18, 0), VerticalAlignment = VerticalAlignment.Center };
            AutomationProperties.SetName(yes, $"{name}: On, Martlet answers {(prefs.Connected ? prefs.Handle : name)} on this PC");
            AutomationProperties.SetAutomationId(yes, prefix + "On");
            var no = new RadioButton { Content = "Off", GroupName = prefix + "Answer", IsChecked = !on, VerticalAlignment = VerticalAlignment.Center };
            AutomationProperties.SetName(no, $"{name}: Off, Martlet doesn't answer {name} on this PC");
            AutomationProperties.SetAutomationId(no, prefix + "Off");
            yes.Checked += (_, _) => SetMessagingAnswers(app, prefs, true);
            no.Checked += (_, _) => SetMessagingAnswers(app, prefs, false);
            var row = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
            row.Children.Add(title);
            row.Children.Add(yes);
            row.Children.Add(no);
            stack.Add(row);
            stack.Add(Note(prefs.Connected ? $"{prefs.Handle}." : $"Not set up yet: set it up under {name} below.", new Thickness(110, 2, 0, 0)));
        }
        return Card([.. stack]);
    }

    private void SetMessagingAnswers(MessagingApp app, ChannelPreferences saved, bool on)
    {
        if (on == (saved.Enabled && saved.Connected)) return;
        if (on && Role == DeviceRole.Host)
        {
            ActionText.Text = HostHasNoCompanionText;
            RenderTab();
            return;
        }
        messaging.SetEnabled(app, on);
        ActionText.Text = on ? $"Martlet answers {saved.Handle} on this PC." : $"Martlet stopped answering {MessagingService.Name(app)} on this PC.";
        RenderTab();
    }
}
