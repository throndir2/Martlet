using Martlet.Avatar.Hosting;
using Martlet.Conversation;
using Martlet.Discord;
using Martlet.Messaging;

namespace Martlet.Desktop.Tests;

/// <summary>The Now line of Companion › Speech bubbles, Emotes and motions, Eyes, Touch, People, Tools, Smart home, Discord and
/// Messaging (MainWindow.PageNow.cs): what each page uses now, in one line.</summary>
public sealed class PageNowTests
{
    [Fact]
    public void SpeechBubblesNowSaysWhereTheWordsShowAndOff()
    {
        Assert.Equal("Speech bubbles: beside the character; subtitles off.", MainWindow.SpeechBubblesNow(new SpeechDisplayPreferences()));
        Assert.Equal("Speech bubbles: in one place; subtitles on.",
            MainWindow.SpeechBubblesNow(new SpeechDisplayPreferences(SpeechBubbles: true, Subtitles: true, StaticBubble: true)));
        Assert.Equal("Subtitles at the bottom of the screen; speech bubbles off.",
            MainWindow.SpeechBubblesNow(new SpeechDisplayPreferences(SpeechBubbles: false, Subtitles: true)));
        Assert.Equal("Off: no speech bubbles or subtitles.", MainWindow.SpeechBubblesNow(new SpeechDisplayPreferences(SpeechBubbles: false)));

        Assert.Contains("hidden", MainWindow.SpeechBubblesProblem(new SpeechDisplayPreferences(), showing: false));
        Assert.Null(MainWindow.SpeechBubblesProblem(new SpeechDisplayPreferences(), showing: true));
        Assert.Null(MainWindow.SpeechBubblesProblem(new SpeechDisplayPreferences(SpeechBubbles: false, Subtitles: true), showing: false));
    }

    [Fact]
    public void EmotesNowCountsWhatIsOnAndTheCombos()
    {
        Assert.Equal("12 emotes and 4 motions on, 2 combos.", MainWindow.EmotesNow(12, 12, 4, 4, 2));
        Assert.Equal("10 of 14 emotes and 1 motion on, 1 combo.", MainWindow.EmotesNow(10, 14, 1, 1, 1));
        Assert.Equal("1 emote and no motions on, no combos.", MainWindow.EmotesNow(1, 1, 0, 0, 0));
        // The bundled character: Martlet's own gestures count too.
        Assert.Equal("No emotes, 1 motion and 37 of 39 Martlet gestures on, 8 combos.", MainWindow.EmotesNow(0, 0, 1, 1, 8, 37, 39));
    }

    [Fact]
    public void EyesNowSaysTheGazeAndWhereTheEyesComeFrom()
    {
        Assert.Equal("Where it looks: follow your mouse, as the personality decides; replies may change it. Eyes: from the model's own meshes.",
            MainWindow.EyesNow(null, null, free: true, RendererEyesFrom.Mesh, measured: false, showing: true));
        Assert.Equal("Where it looks: look straight ahead. Eyes: only estimated.",
            MainWindow.EyesNow(GazeMode.Ahead, GazeMode.Mouse, free: false, RendererEyesFrom.Estimate, measured: false, showing: true));
        Assert.Equal("Where it looks: watch the window you're using, as the personality decides. Eyes: read when the character shows.",
            MainWindow.EyesNow(null, GazeMode.Window, free: false, null, measured: false, showing: false));
        Assert.EndsWith("Eyes: measured with vision.", MainWindow.EyesNow(null, null, false, null, measured: true, showing: false));
    }

    [Fact]
    public void TouchNowCountsZonesThatWorkAndWhatATouchDoesWhileMartletTalks()
    {
        static CharacterTouchZone Zone(string id, bool enabled = true) => new() { Id = id, Box = new TouchZoneBox(0, 0, 0.1, 0.1), Enabled = enabled };
        var zones = new CharacterTouchZoneSettings
        {
            ModelId = "model", DetectedBy = CharacterTouchZoneSettings.ByVision,
            Zones = [Zone("top_of_head"), Zone("hair"), Zone("chest"), Zone("face", enabled: false)]
        };
        Assert.Equal("3 of 4 touch zones on, found by the Thinking model; a touch stops Martlet mid-sentence.",
            MainWindow.TouchNow(zones, TouchInterrupts.Any));
        // Without intimate zones the chest does nothing.
        Assert.StartsWith("2 of 4 touch zones on", MainWindow.TouchNow(zones with { IncludeIntimate = false }, TouchInterrupts.Any));
        Assert.Equal("2 touch zones on, placed by you; touches wait until Martlet finishes speaking.",
            MainWindow.TouchNow(zones with { DetectedBy = CharacterTouchZoneSettings.ByOwner, Zones = [Zone("hair"), Zone("top_of_head")] },
                TouchInterrupts.Never));
        Assert.Equal("No touch zones yet, so a click reacts to the rough part it lands on; only an intimate touch stops Martlet mid-sentence.",
            MainWindow.TouchNow(null, TouchInterrupts.Intimate));
    }

    [Fact]
    public void PeopleNowSaysWhetherVoicesAreRecognized()
    {
        Assert.Equal("Voice recognition on: 3 voices known, 2 with a name.", MainWindow.PeopleNow(enabled: true, voices: 3, named: 2));
        Assert.Equal("Voice recognition on: no voices learned yet.", MainWindow.PeopleNow(true, 0, 0));
        Assert.Equal("Off: Martlet doesn't check who is talking; 1 saved voice kept.", MainWindow.PeopleNow(false, 1, 1));
        Assert.Equal("Off: Martlet doesn't check who is talking.", MainWindow.PeopleNow(false, 0, 0));
    }

    [Fact]
    public void ToolsNowCountsServersAndTheTerminal()
    {
        Assert.Equal("Tools: 3 MCP servers on, the terminal off.", MainWindow.ToolsNow(3, 3, terminal: false));
        Assert.Equal("Tools: 1 of 2 MCP servers on, the terminal on.", MainWindow.ToolsNow(1, 2, terminal: true));
        Assert.Equal("Tools: no MCP servers, the terminal on.", MainWindow.ToolsNow(0, 0, terminal: true));
        Assert.Equal("Off: no MCP servers, and the terminal is off.", MainWindow.ToolsNow(0, 0, terminal: false));
        Assert.Equal("Off: 1 MCP server off, and the terminal is off.", MainWindow.ToolsNow(0, 1, terminal: false));
    }

    [Fact]
    public void SmartHomeNowSaysTheConnectionAndWhatMartletMayDo()
    {
        var saved = new HomePreferences(Address: "http://homeassistant.local:8123", CredentialId: Guid.NewGuid(), Control: true, LocationName: "Home");
        Assert.Equal("Smart home: connected to Home at http://homeassistant.local:8123; Martlet may control lights, switches, climate, media, " +
            "scenes and blinds.", MainWindow.SmartHomeNow(saved, connected: true));
        Assert.Equal("Smart home: connected to Home at http://homeassistant.local:8123; Martlet may control lights, switches, climate, media, " +
            "scenes and blinds, and locks, doors and alarms after a Yes each time. Flexible requests are on.",
            MainWindow.SmartHomeNow(saved with { AllowSensitive = true, ModelTools = true }, connected: true));
        Assert.Equal("Off: connected to Home at http://homeassistant.local:8123, but Martlet doesn't control your smart home.",
            MainWindow.SmartHomeNow(saved with { Control = false }, connected: true));
        Assert.Equal("Off: Home Assistant isn't connected.", MainWindow.SmartHomeNow(new HomePreferences(), connected: false));
    }

    [Fact]
    public void DiscordNowSaysWhetherTheBotIsOnAndWhy()
    {
        var saved = new DiscordPreferences { ApplicationId = 42, CredentialId = Guid.NewGuid(), Enabled = true };
        Assert.Equal("Off: Martlet's Discord bot isn't set up yet.", MainWindow.DiscordNow(new DiscordPreferences(), new(DiscordBotState.Off)));
        Assert.Equal("Off: Martlet doesn't connect to Discord.", MainWindow.DiscordNow(saved with { Enabled = false }, new(DiscordBotState.Off)));
        Assert.Equal("Discord: the bot Martlet is connected to 2 servers.",
            MainWindow.DiscordNow(saved, new(DiscordBotState.Online, BotName: "Martlet", Servers: 2)));
        Assert.Equal("Discord: connecting...", MainWindow.DiscordNow(saved, new(DiscordBotState.Connecting)));

        var failed = new DiscordBotStatus(DiscordBotState.Failed, Problem: "Discord rejected the bot token.");
        Assert.Equal("Discord: on, but not connected.", MainWindow.DiscordNow(saved, failed));
        Assert.Equal("Discord rejected the bot token.", MainWindow.DiscordProblem(saved, failed));
        Assert.Null(MainWindow.DiscordProblem(saved with { Enabled = false }, failed));
        Assert.Null(MainWindow.DiscordProblem(saved, new(DiscordBotState.Online, Servers: 1)));
    }

    [Fact]
    public void MessagingNowSaysWhatEachAppDoesAndTheFirstProblem()
    {
        var telegram = new TelegramPreferences { BotUsername = "martlet_bot", Enabled = true, Chats = [new MessagingChat("1", "Me")] };
        var whatsApp = new WhatsAppPreferences { PhoneNumberId = "99", Number = "+1 555-0100", Enabled = false };
        (string, ChannelPreferences, MessagingStatus, bool)[] apps =
        [
            ("Telegram", telegram, new MessagingStatus(MessagingState.Running), true),
            ("WhatsApp", whatsApp, new MessagingStatus(MessagingState.Off), false)
        ];
        Assert.Equal("Telegram: answering @martlet_bot (1 paired chat); WhatsApp: off.", MainWindow.MessagingNow(apps));
        Assert.Null(MainWindow.MessagingProblem(apps));

        (string, ChannelPreferences, MessagingStatus, bool)[] none =
        [
            ("Telegram", new TelegramPreferences(), new MessagingStatus(MessagingState.Off), false),
            ("WhatsApp", whatsApp, new MessagingStatus(MessagingState.Off), false)
        ];
        Assert.Equal("Off: Martlet answers no messaging app.", MainWindow.MessagingNow(none));

        (string, ChannelPreferences, MessagingStatus, bool)[] retrying =
        [
            ("Telegram", telegram, new MessagingStatus(MessagingState.Retrying, Problem: "Couldn't reach Telegram."), true),
            ("WhatsApp", new WhatsAppPreferences(), new MessagingStatus(MessagingState.Off), false)
        ];
        Assert.Equal("Telegram: can't reach @martlet_bot right now; WhatsApp: not set up.", MainWindow.MessagingNow(retrying));
        Assert.Equal("Telegram: Couldn't reach Telegram.", MainWindow.MessagingProblem(retrying));
    }
}
