using System.Windows.Controls;
using Martlet.Core.Installation;
using Martlet.Discord.Calls;
using Martlet.Home;
using Martlet.HostClient;
using Martlet.Mcp.Client;
using Martlet.Messaging;

namespace Martlet.Desktop.Tests;

/// <summary>The page models of Companion › Smart home, Discord and Messaging (Pages\SmartHome, Pages\Discord, Pages\Messaging),
/// without WPF.</summary>
public sealed class CompanionIntegrationPageModelTests
{
    [Fact]
    public void MessagingModelReadsEachAppOnceAndSaysItsStatusLine()
    {
        var services = new MessagingFake
        {
            Preferences = new MessagingPreferences
            {
                Telegram = new TelegramPreferences { BotUsername = "martlet_bot", Enabled = true, Chats = [new("1", "Sam")] }
            },
            States = { [MessagingApp.Telegram] = new(MessagingState.Running) }
        };
        var model = new MessagingPageModel(services);
        Assert.Equal("Telegram: answering @martlet_bot (1 paired chat); WhatsApp: not set up.", model.NowText);
        Assert.Null(model.ProblemText);
        Assert.Equal("Answering @martlet_bot on this PC (1 paired chat).", model.StatusLine(MessagingApp.Telegram));
        Assert.StartsWith("Not connected: follow the steps", model.StatusLine(MessagingApp.WhatsApp), StringComparison.Ordinal);
        Assert.Equal("Martlet answers this chat:", MessagingPageModel.ChatsText(services.Preferences.Telegram));
        Assert.Equal(new Uri("https://t.me/martlet_bot?start=123456"),
            MessagingPageModel.PairingLink(services.Preferences.Telegram, new("123456", DateTimeOffset.UtcNow)));
        Assert.Equal(("MessagingWhatsApp", "Messaging"), (MessagingPageModel.Prefix(MessagingApp.WhatsApp), MessagingPageModel.Prefix(MessagingApp.Telegram)));
    }

    [Fact]
    public void SmartHomeModelSaysTheConnectionSharingAndToolsLines()
    {
        var off = new SmartHomeFake();
        var model = new SmartHomePageModel(off);
        Assert.Equal("Off: Home Assistant isn't connected.", model.NowText);
        Assert.Equal("Not connected.", model.ConnectionText);
        Assert.False(model.NeedsManagement);
        Assert.Equal("Off.", model.ToolsText);
        Assert.Equal("None of your computers is connected to Home Assistant.", model.ShareText);
        off.SharedAddress = "http://ha.local:8123";
        Assert.Equal("Your other computers use Home Assistant at http://ha.local:8123, and this PC connects to it by itself within a minute.",
            new SmartHomePageModel(off).ShareText);
        off.ClusterEnabled = false;
        Assert.StartsWith("Keep Martlet the same on all my computers (Settings) is off", new SmartHomePageModel(off).ShareText, StringComparison.Ordinal);

        var on = new SmartHomeFake
        {
            Connected = true, ModelToolsEnabled = true,
            Preferences = new HomePreferences("http://ha.local:8123", Guid.NewGuid(), Control: true, LocationName: "Home", Version: "2026.9.4", ModelTools: true)
        };
        model = new SmartHomePageModel(on);
        Assert.Equal("Connected to Home at http://ha.local:8123 (Home Assistant 2026.9.4).", model.ConnectionText);
        Assert.True(model.NeedsManagement);
        Assert.Equal("On: it connects when you open a talk window.", model.ToolsText);
        Assert.Equal("Martlet gives this connection to your other computers on the next check.", model.ShareText);
        Assert.Equal("Not read yet.", model.ManageText);
        on.ManageLoadedFor = "http://ha.local:8123";
        Assert.False(new SmartHomePageModel(on).NeedsManagement);
        Assert.Equal("Added Hue to Home Assistant.", SmartHomePageModel.FlowText("Hue", new HomeFlowStep(HomeStepKind.Done, "flow", null, [], [], null, null)));
    }

    [Fact]
    public void DiscordModelSaysTheCameraLines()
    {
        var saved = new DiscordCallPreferences();
        Assert.Equal("The camera view is closed.", DiscordPageModel.CameraText(open: false, showing: true, saved));
        Assert.StartsWith("The camera view is on, but the character isn't showing yet", DiscordPageModel.CameraText(open: true, showing: false, saved),
            StringComparison.Ordinal);
        Assert.Equal("No picture chosen yet: set up Companion › Picture making to have Martlet draw one.",
            DiscordPageModel.PictureText(saved, hasPicture: false, canDraw: false));
        Assert.Equal("No picture chosen yet.", DiscordPageModel.PictureText(saved, hasPicture: false, canDraw: true));
        Assert.Equal("Choose Picture above to use the saved picture again.", DiscordPageModel.PictureText(saved, hasPicture: true, canDraw: true));
    }

    private sealed class MessagingFake : IMessagingPageServices
    {
        internal Dictionary<MessagingApp, MessagingStatus> States { get; } = [];
        public MessagingPreferences Preferences { get; init; } = new();
        public MessagingStatus Status(MessagingApp app) => States.GetValueOrDefault(app, new(MessagingState.Off));
        public bool Running(MessagingApp app) => States.ContainsKey(app);
        public PairingCode? Pairing(MessagingApp app) => null;
        public string? Cloudflared => null;
        public CloudflaredInstall? OwnCloudflared => null;
        public string? TelegramNote => null;
        public string? WhatsAppNote => null;
        public bool TelegramBusy => false;
        public bool WhatsAppBusy => false;
        public (string Token, string Secret, string PhoneId, string AccountId, string Address)? WhatsAppDraft { get; set; }
        public void ConnectTelegram(PasswordBox token, Button connect) => throw new NotSupportedException();
        public void ConnectWhatsApp(PasswordBox token, PasswordBox secret, string phoneId, string accountId, string address) => throw new NotSupportedException();
        public void GetCloudflared() => throw new NotSupportedException();
        public void SetAnswers(MessagingApp app, ChannelPreferences saved, bool on) => throw new NotSupportedException();
        public void SetSpeakReplies(MessagingApp app, bool speak) => throw new NotSupportedException();
        public void Pair(MessagingApp app) => throw new NotSupportedException();
        public void CancelPairing(MessagingApp app) => throw new NotSupportedException();
        public void RemoveChat(MessagingApp app, MessagingChat chat) => throw new NotSupportedException();
        public void Disconnect(MessagingApp app) => throw new NotSupportedException();
        public void OpenInBrowser(Uri address) => throw new NotSupportedException();
    }

    private sealed class SmartHomeFake : ISmartHomePageServices
    {
        public HomePreferences Preferences { get; init; } = new();
        public bool Connected { get; init; }
        public bool ModelToolsEnabled { get; init; }
        public (McpServerStatus? Status, bool Conflict) HomeAssistantTools() => (null, false);
        public bool ClusterEnabled { get; set; } = true;
        public IReadOnlyList<PairedHost> Hosts() => [];
        public IReadOnlyList<HostHardware> Hardware() => [];
        public IReadOnlyList<HomeAction> RecentActions => [];
        public string? AddressDraft { get; set; }
        public IReadOnlyList<FoundHomeAssistant>? Found => null;
        public string? FindStatus => null;
        public bool Finding => false;
        public Uri? SetupTarget => null;
        public string? SetupStatus => null;
        public string? SharedAddress { get; set; }
        public string ShareStatus => "Not checked yet.";
        public HomeSystem? System => null;
        public IReadOnlyList<HomeDiscovery>? Discoveries => null;
        public IReadOnlyList<HomeUpdate>? Updates => null;
        public HomeBackups? Backups => null;
        public string? ManageStatus => null;
        public string? DevicesStatus => null;
        public bool ManageBusy => false;
        public string? ManageLoadedFor { get; set; }
        public void Find() => throw new NotSupportedException();
        public void UseFound(FoundHomeAssistant found) => throw new NotSupportedException();
        public void CheckAddress(string address, bool signInIfReady = false) => throw new NotSupportedException();
        public void SignIn(string address) => throw new NotSupportedException();
        public void Connect(string address, PasswordBox token, Button connect) => throw new NotSupportedException();
        public void Disconnect() => throw new NotSupportedException();
        public void SetUp(Uri target, string name, string username, PasswordBox password, PasswordBox confirm, bool control, Button setUp) =>
            throw new NotSupportedException();
        public void CancelSetup() => throw new NotSupportedException();
        public void Install(PairedHost host) => throw new NotSupportedException();
        public void SyncShare() => throw new NotSupportedException();
        public void SaveControl(bool control, bool allowSensitive, bool modelTools) => throw new NotSupportedException();
        public void AddDiscovered(HomeDiscovery found) => throw new NotSupportedException();
        public void IgnoreDiscovered(HomeDiscovery found) => throw new NotSupportedException();
        public void AddMqtt(PairedHost host) => throw new NotSupportedException();
        public void RefreshManagement() => throw new NotSupportedException();
        public void InstallUpdate(HomeUpdate update) => throw new NotSupportedException();
        public void Restart() => throw new NotSupportedException();
        public void Backup() => throw new NotSupportedException();
        public void OpenHomePage(string path) => throw new NotSupportedException();
        public Border OptionPicker(string id, string heading, string? note, IReadOnlyList<PickerOption> options) =>
            throw new NotSupportedException();
    }
}
