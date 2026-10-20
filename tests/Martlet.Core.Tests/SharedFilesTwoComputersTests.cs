using Martlet.Avatar.Hosting;
using Martlet.Core.Settings;
using Martlet.Core.Speakers;
using Martlet.Core.Sync;
using Martlet.Discord;
using Martlet.Discord.Calls;
using Martlet.Messaging;

namespace Martlet.Core.Tests;

/// <summary>Two simulated computers keep the files that #946, #951, #958 and #979 made shared the same (AGENTS.md, "Martlet is one
/// app"): keeping clips of unnamed voices, every character model's touch zones, eye measurement and VRoid Hub credit, the
/// character's own reaction changes, the Discord setup with its bot token, and Telegram and WhatsApp with their secrets. What
/// describes the computer (which one runs a bot, its devices, its webhook port) stays on each one.</summary>
public sealed class SharedFilesTwoComputersTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "martlet-shared-files-" + Guid.NewGuid().ToString("N"));
    private readonly DateTimeOffset start = DateTimeOffset.UtcNow;

    public void Dispose()
    {
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }

    private (string A, string B) Computers(string name) => (Path.Combine(root, name, "a"), Path.Combine(root, name, "b"));

    /// <summary>A computer's sync: what it changed is recorded, and what the other one has is taken.</summary>
    private static async Task<SharedSettingsResult> Sync(SharedSettingsNode node, SharedSettingsNode other, DateTimeOffset at) =>
        await node.SyncAsync([other.Document], true, at, CancellationToken.None);

    [Fact]
    public async Task Keeping_clips_of_unnamed_voices_is_one_choice_for_both_computers()
    {
        Assert.Contains(SettingsFiles.All, f => f.FileName == VoiceChoices.ClipsFile && f.Scope == SettingsFileScope.Shared && f.SharedKey == VoiceChoices.ClipsKey);
        Assert.False(SettingScopes.IsAccountKey(VoiceChoices.ClipsKey));
        var (a, b) = Computers("clips");
        var followed = new List<bool>();
        var nodeA = new SharedSettingsNode(a, "desktop-a", [VoiceChoices.ClipsSection(a)]);
        var nodeB = new SharedSettingsNode(b, "desktop-b", [VoiceChoices.ClipsSection(b, on => { followed.Add(on); VoiceChoices.Write(b, VoiceChoices.ClipsFile, on); })]);

        // B never chose: its default (on) never replaces a choice made elsewhere.
        await Sync(nodeB, nodeA, start);
        VoiceChoices.Write(a, VoiceChoices.ClipsFile, false);
        var recorded = await Sync(nodeA, nodeB, start.AddSeconds(15));
        Assert.Contains(VoiceChoices.ClipsKey, recorded.Recorded);
        var taken = await Sync(nodeB, nodeA, start.AddSeconds(30));
        Assert.Contains(taken.Applied, change => change.Key == VoiceChoices.ClipsKey && change.By == "desktop-a");
        Assert.Equal([false], followed);
        Assert.False(VoiceChoices.KeepClips(b));

        // Turned on again on B: A follows.
        VoiceChoices.Write(b, VoiceChoices.ClipsFile, true);
        await Sync(nodeB, nodeA, start.AddSeconds(45));
        await Sync(nodeA, nodeB, start.AddSeconds(60));
        Assert.True(VoiceChoices.KeepClips(a));
        Assert.Null(VoiceChoices.Parse("""{"on":"maybe"}"""));
    }

    [Fact]
    public async Task Touch_zones_eyes_and_credits_found_on_one_computer_reach_the_other()
    {
        var (a, b) = Computers("character");
        var nodeA = new SharedSettingsNode(a, "desktop-a", CharacterSharedSections.All(a));
        var followed = 0;
        var nodeB = new SharedSettingsNode(b, "desktop-b", CharacterSharedSections.All(b, touchZones: new(null, () => followed++)));
        await Sync(nodeB, nodeA, start);

        // The owner draws a zone and changes its words on A; A's vision measures the eyes; A shows a VRoid Hub character.
        var zones = new CharacterTouchZoneSettings
        {
            ModelId = "model-1", Crop = new(0, 0, 1, 1), DetectedBy = CharacterTouchZoneSettings.ByOwner,
            Zones = [new() { Id = "top_of_head", Box = new(0.3, 0, 0.4, 0.2) }, new() { Id = "hand_right", Box = new(0.05, 0.6, 0.1, 0.1), Bones = ["rightHand"] }]
        };
        await CharacterTouchZones.SaveAsync(a, zones, start);
        var boxes = CharacterEyes.Parse(EyesAnswer, 768, 768).Boxes!;
        await CharacterEyes.SaveAsync(a, CharacterEyeMeasurement.From("model-1", CharacterEyes.Hint(boxes, EyeCloseUp.Standard())) with { MeasuredAt = start });
        VRoidHubLooks.Remember(a, new("1234567890", "Mira", "Someone", "https://hub.vroid.com/characters/1/models/1234567890", ["Credit"], true, null, start));

        var recorded = await Sync(nodeA, nodeB, start.AddSeconds(15));
        Assert.Contains(CharacterTouchZones.SharedKey, recorded.Recorded);
        Assert.Contains(CharacterEyes.SharedKey, recorded.Recorded);
        Assert.Contains(VRoidHubLooks.SharedKey, recorded.Recorded);
        await Sync(nodeB, nodeA, start.AddSeconds(30));

        var onB = CharacterTouchZones.Load(b, "model-1");
        Assert.NotNull(onB);
        Assert.Equal(["top_of_head", "hand_right"], onB.Zones.Select(z => z.Id));
        Assert.Equal(CharacterTouchZoneSettings.ByOwner, onB.DetectedBy);
        Assert.Equal(1, followed);
        Assert.Equal(CharacterEyes.Load(a, "model-1")!.Hint, CharacterEyes.Load(b, "model-1")!.Hint);
        Assert.Equal("Mira by Someone (VRoid Hub)", Assert.Single(VRoidHubLooks.Load(b)).Credit);
        // The pictures stay where they were made: nothing but the files themselves travelled.
        Assert.False(Directory.Exists(Path.Combine(b, CharacterTouchZones.SnapshotFolder)));

        // The owner changes a zone on B: A takes it.
        await CharacterTouchZones.SaveAsync(b, onB with { Zones = [.. onB.Zones.Where(z => z.Id != "hand_right")] }, start.AddMinutes(1));
        await Sync(nodeB, nodeA, start.AddSeconds(45));
        await Sync(nodeA, nodeB, start.AddSeconds(60));
        Assert.Equal(["top_of_head"], CharacterTouchZones.Load(a, "model-1")!.Zones.Select(z => z.Id));

        // A setting from a newer Martlet waits and keeps this computer's own.
        var newer = nodeA.Document.Find(CharacterTouchZones.SharedKey)!;
        Assert.False(await CharacterTouchZones.ReplaceAllAsync(b, newer.Value.Replace("\"version\":2", "\"version\":9", StringComparison.Ordinal)));
        Assert.Single(CharacterTouchZones.LoadAll(b));
    }

    [Fact]
    public void The_characters_own_reaction_changes_are_each_persons()
    {
        Assert.True(SettingScopes.IsAccountKey(CharacterReactionChanges.SharedKey));
        Assert.Contains(SettingsFiles.All, f => f.FileName == CharacterReactionChanges.FileName && f.Scope == SettingsFileScope.Account);
        var (a, _) = Computers("reactions");
        var section = CharacterSharedSections.ReactionChanges(a);
        // An account that never had any starts with none, and the empty document travels as the default.
        Assert.Equal(CharacterReactionChanges.Share(a), section.Default);
    }

    [Fact]
    public async Task The_discord_setup_and_its_bot_token_travel_but_running_the_bot_stays_on_one_computer()
    {
        var (a, b) = Computers("discord");
        var vaultA = new Vault();
        var vaultB = new Vault();
        var nodeA = new SharedSettingsNode(a, "desktop-a", [DiscordSharing.Section(a, vaultA)]);
        var nodeB = new SharedSettingsNode(b, "desktop-b", [DiscordSharing.Section(b, vaultB)]);
        await Sync(nodeB, nodeA, start);

        // A runs the bot: its token, the owner, a friend, a channel rule; in calls its output device and the owner's name.
        var token = "lab-discord-bot-token-for-two-computers";
        var credential = Guid.NewGuid();
        vaultA.Write(credential, token);
        Assert.True(new DiscordPreferences
        {
            ApplicationId = 123456789012345678, CredentialId = credential, Enabled = true, OwnerUserId = 42, ServerChat = DiscordChatMode.Mentions,
            People = [new(77, "Alex")], Channels = [new(1, 2, "general", DiscordChatMode.Always)]
        }.Save(a));
        Assert.True(new DiscordCallPreferences { On = true, OwnerName = "Sam", OutputId = "cable-1", OutputName = "CABLE Input", BargeIn = false }.Save(a));

        var recorded = await Sync(nodeA, nodeB, start.AddSeconds(15));
        Assert.Contains(DiscordSharing.SharedKey, recorded.Recorded);
        Assert.NotNull(nodeA.Document.Find(DiscordSharing.SharedKey)!.SecretSha256);
        Assert.DoesNotContain(token, nodeA.Document.Find(DiscordSharing.SharedKey)!.Value, StringComparison.Ordinal);
        await Sync(nodeB, nodeA, start.AddSeconds(30));

        var onB = DiscordPreferences.Load(b);
        Assert.Equal(123456789012345678UL, onB.ApplicationId);
        Assert.Equal(42UL, onB.OwnerUserId);
        Assert.Equal("Alex", Assert.Single(onB.People).Name);
        Assert.Equal(DiscordChatMode.Mentions, onB.ServerChat);
        Assert.False(onB.Enabled);
        Assert.Equal(token, vaultB.Read(onB.CredentialId));
        var callsOnB = DiscordCallPreferences.Load(b);
        Assert.Equal("Sam", callsOnB.OwnerName);
        Assert.False(callsOnB.BargeIn);
        Assert.False(callsOnB.On);
        Assert.Null(callsOnB.OutputId);

        // A friend added on B reaches A; A keeps running the bot with its own credential.
        Assert.True((onB with { People = [.. onB.People, new(88, "Kim", MayCall: false)] }).Save(b));
        await Sync(nodeB, nodeA, start.AddSeconds(45));
        await Sync(nodeA, nodeB, start.AddSeconds(60));
        var onA = DiscordPreferences.Load(a);
        Assert.Equal(["Alex", "Kim"], onA.People.Select(p => p.Name));
        Assert.True(onA.Enabled);
        Assert.Equal(credential, onA.CredentialId);
        Assert.Equal("cable-1", DiscordCallPreferences.Load(a).OutputId);
    }

    [Fact]
    public async Task Telegram_and_whatsapp_travel_with_their_secrets_but_answering_stays_on_one_computer()
    {
        var (a, b) = Computers("messaging");
        var vaultA = new MessagingVault();
        var vaultB = new MessagingVault();
        var nodeA = new SharedSettingsNode(a, "desktop-a", [MessagingSharing.Section(a, vaultA)]);
        var nodeB = new SharedSettingsNode(b, "desktop-b", [MessagingSharing.Section(b, vaultB)]);
        await Sync(nodeB, nodeA, start);

        var token = "123456789:AAHlabTokenForTwoComputers_0123456789";
        var whatsApp = "{\"access\":\"EAAlab\",\"secret\":\"0123456789abcdef\"}";
        var telegramId = Guid.NewGuid();
        var whatsAppId = Guid.NewGuid();
        vaultA.Write(MessagingApp.Telegram, telegramId, token);
        vaultA.Write(MessagingApp.WhatsApp, whatsAppId, whatsApp);
        Assert.True(new MessagingPreferences
        {
            Telegram = new() { Enabled = true, CredentialId = telegramId, BotName = "Mira", BotUsername = "mira_bot", Chats = [new("1001", "Sam")], SpeakReplies = true },
            WhatsApp = new() { Enabled = true, CredentialId = whatsAppId, Name = "Mira", Number = "+1 555-0100", AppId = "app", BusinessAccountId = "waba",
                PhoneNumberId = "pn", Port = 51234, PublicAddress = "https://mira.example.com" }
        }.Save(a));

        var recorded = await Sync(nodeA, nodeB, start.AddSeconds(15));
        Assert.Contains(MessagingSharing.SharedKey, recorded.Recorded);
        var shared = nodeA.Document.Find(MessagingSharing.SharedKey)!;
        Assert.DoesNotContain("AAHlab", shared.Value, StringComparison.Ordinal);
        Assert.DoesNotContain("51234", shared.Value, StringComparison.Ordinal);
        await Sync(nodeB, nodeA, start.AddSeconds(30));

        var onB = MessagingPreferences.Load(b);
        Assert.Equal("mira_bot", onB.Telegram.BotUsername);
        Assert.Equal("Sam", Assert.Single(onB.Telegram.Chats).Name);
        Assert.True(onB.Telegram.SpeakReplies);
        Assert.False(onB.Telegram.Enabled);
        Assert.Equal(token, vaultB.Read(MessagingApp.Telegram, onB.Telegram.CredentialId));
        Assert.Equal(whatsApp, vaultB.Read(MessagingApp.WhatsApp, onB.WhatsApp.CredentialId));
        Assert.Equal("pn", onB.WhatsApp.PhoneNumberId);
        Assert.Equal(0, onB.WhatsApp.Port);
        Assert.Equal("", onB.WhatsApp.PublicAddress);

        // A chat paired on B reaches A, which still answers Telegram itself with its own credential and port.
        Assert.True((onB with { Telegram = onB.Telegram with { Chats = [.. onB.Telegram.Chats, new("1002", "Alex")] } }).Save(b));
        await Sync(nodeB, nodeA, start.AddSeconds(45));
        await Sync(nodeA, nodeB, start.AddSeconds(60));
        var onA = MessagingPreferences.Load(a);
        Assert.Equal(["Sam", "Alex"], onA.Telegram.Chats.Select(c => c.Name));
        Assert.True(onA.Telegram.Enabled);
        Assert.Equal(telegramId, onA.Telegram.CredentialId);
        Assert.Equal(51234, onA.WhatsApp.Port);

        // Disconnected on A: B forgets the bot and deletes its copy of the token.
        var bCredential = onB.Telegram.CredentialId;
        Assert.True((onA with { Telegram = new() }).Save(a));
        await Sync(nodeA, nodeB, start.AddSeconds(75));
        await Sync(nodeB, nodeA, start.AddSeconds(90));
        Assert.False(MessagingPreferences.Load(b).Telegram.Connected);
        Assert.Null(vaultB.Read(MessagingApp.Telegram, bCredential));
    }

    private const string EyesAnswer = """
        {"left":{"iris":{"left":0.34375,"top":0.45625,"right":0.40625,"bottom":0.51875},"eye":{"left":0.3125,"top":0.4625,"right":0.4375,"bottom":0.5125},
                 "cheek":{"left":0.25,"top":0.65,"right":0.375,"bottom":0.725}},
         "right":{"iris":{"left":0.59375,"top":0.45625,"right":0.65625,"bottom":0.51875},"eye":{"left":0.5625,"top":0.4625,"right":0.6875,"bottom":0.5125},
                 "cheek":{"left":0.625,"top":0.65,"right":0.75,"bottom":0.725}},
         "mouth":{"left":0.46875,"top":0.76875,"right":0.53125,"bottom":0.79375}}
        """;

    /// <summary>Stands in for a computer's Windows Credential Manager.</summary>
    private sealed class Vault : IDiscordTokenVault
    {
        private readonly Dictionary<Guid, string> tokens = [];
        public string? Read(Guid credentialId) => tokens.GetValueOrDefault(credentialId);
        public bool Write(Guid credentialId, string token) { tokens[credentialId] = token; return true; }
        public void Delete(Guid credentialId) => tokens.Remove(credentialId);
    }

    private sealed class MessagingVault : IMessagingSecretVault
    {
        private readonly Dictionary<(MessagingApp, Guid), string> secrets = [];
        public string? Read(MessagingApp app, Guid credentialId) => secrets.GetValueOrDefault((app, credentialId));
        public bool Write(MessagingApp app, Guid credentialId, string secret) { secrets[(app, credentialId)] = secret; return true; }
        public void Delete(MessagingApp app, Guid credentialId) => secrets.Remove((app, credentialId));
    }
}
