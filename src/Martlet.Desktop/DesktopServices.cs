using Martlet.Audio.Windows;
using Martlet.Avatar.Hosting;
using Martlet.Core.Lorebooks;
using Martlet.Core.Settings;
using Martlet.Credentials.Windows;

namespace Martlet.Desktop;

/// <summary>
/// The services the main window uses, built in one place (#962): the settings and credential stores, audio setup, memory, the record
/// of conversations, lorebooks, smart home, Discord, MCP tools, messaging, voices, the character and its per-model services.
/// <see cref="Create"/> builds them for a data folder without any window, so a test can build them, look at them, or hand changed
/// ones to <see cref="MainWindow"/>. The main window wires them to each other and to its pages (events, callbacks); the services
/// here hold no window state. The live conversation is built by the window, because it needs the window's own callbacks.
/// </summary>
internal sealed class DesktopServices
{
    internal required SettingsStore? Store { get; init; }
    internal required SupportController Support { get; init; }
    /// <summary>Who uses Martlet now (docs/ACCOUNTS.md); null without a data folder.</summary>
    internal required AccountSession? Accounts { get; init; }
    internal required SetupOperationRunner SetupOperations { get; init; }
    internal required WindowsCredentialStore Vault { get; init; }
    internal required AudioSetupService AudioSetup { get; init; }
    internal required WindowsAudioSessionEvents AudioSessionEvents { get; init; }
    internal required ISetupService? SetupService { get; init; }
    internal required ICompanionSettingsService? CompanionService { get; init; }
    internal required DesktopMemoryService? Memory { get; init; }
    internal required DesktopConversationHistory? ConversationHistory { get; init; }
    /// <summary>Each account's own lorebooks, in its folder.</summary>
    internal required LorebookStore? Lorebooks { get; init; }
    internal required SmartHome SmartHome { get; init; }
    internal required DiscordService Discord { get; init; }
    internal required DiscordCallService DiscordCalls { get; init; }
    internal required McpToolService McpTools { get; init; }
    internal required MessagingService Messaging { get; init; }
    internal required VoiceIdentity VoiceIdentity { get; init; }
    internal required LocalVoices LocalVoices { get; init; }
    internal required ParakeetListener? Parakeet { get; init; }
    internal required ConfigurationRecoveryController? Recovery { get; init; }
    internal required AvatarController Avatar { get; init; }
    internal required SpeechCaptions Captions { get; init; }
    internal required CharacterActionService CharacterActions { get; init; }
    internal required CharacterTouchZoneService CharacterTouchZones { get; init; }
    internal required CharacterEyeService CharacterEyes { get; init; }
    internal required CharacterTemperamentService CharacterTemperaments { get; init; }
    internal required CharacterReactionChangeService CharacterReactionChanges { get; init; }
    internal required CharacterThemeService CharacterThemes { get; init; }

    /// <summary>Builds every service for <paramref name="store"/>'s data folder (none of them starts any work yet).</summary>
    internal static DesktopServices Create(SettingsStore? store, SupportController support)
    {
        var directory = store?.DataDirectory;
        var operations = new SetupOperationRunner();
        var vault = new WindowsCredentialStore();
        var accounts = MainWindow.OpenAccounts(store);
        var avatar = new AvatarController();
        var voiceIdentity = new VoiceIdentity(directory);
        voiceIdentity.Load();
        return new()
        {
            Store = store,
            Support = support,
            Accounts = accounts,
            SetupOperations = operations,
            Vault = vault,
            AudioSetup = new(operations, new WindowsAudioDeviceCatalog(), new WasapiCaptureDeviceFactory(), new WasapiDeviceFactory()),
            AudioSessionEvents = new(),
            SetupService = store is null ? null : new SetupService(store, vault),
            CompanionService = store is null ? null : new CompanionSettingsService(store),
            Memory = store is null ? null : new DesktopMemoryService(store),
            ConversationHistory = directory is null ? null : new DesktopConversationHistory(directory),
            Lorebooks = store is null ? null : new LorebookStore(accounts?.AccountFolder ?? store.DataDirectory),
            SmartHome = new(directory, vault),
            Discord = new(directory, vault),
            DiscordCalls = new(directory),
            McpTools = new(directory),
            Messaging = new(directory, vault),
            VoiceIdentity = voiceIdentity,
            LocalVoices = new(directory),
            Parakeet = directory is null ? null : new(LocalVoices.SpeechRoot(directory)),
            Recovery = store is null ? null : new(store, operations, () => !support.HasResources),
            Avatar = avatar,
            Captions = new(avatar, directory),
            CharacterActions = new(directory),
            CharacterTouchZones = new(directory),
            CharacterEyes = new(directory),
            CharacterTemperaments = new(directory),
            CharacterReactionChanges = new(directory),
            CharacterThemes = new(directory)
        };
    }
}
