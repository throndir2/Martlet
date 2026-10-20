using Martlet.Companion.Platform;
using Martlet.Core.Sync;
using Personalities = Martlet.Core.Settings.CompanionSettings;

namespace Martlet.Companion.Tests;

/// <summary>The companion joins the shared settings (docs/CLUSTER.md, "One Martlet on every computer"), with two simulated
/// computers and an in-memory host copy, through the desktop's sync engine and the companion's sections.</summary>
public sealed class SharingTests : IDisposable
{
    private const string OpenRouter = "https://openrouter.ai/api/v1";
    private const string OpenRouterKey = "sk-or-v1-lab-desktop-0123456789abcdef";
    private const string OpenAiKey = "sk-lab-openai-desktop-0123456789";
    private readonly string root = Path.Combine(Path.GetTempPath(), "martlet-companion-sharing-" + Guid.NewGuid().ToString("N"));
    private SharedSettings host = SharedSettings.Empty;

    public void Dispose()
    {
        try { Directory.Delete(root, recursive: true); }
        catch (DirectoryNotFoundException) { }
    }

    private sealed record Computer(CompanionSettingsStore Store, CompanionGuardrails Guardrails, SessionCredentialStore Keys, CompanionSync Sync)
    {
        public CompanionSettings Settings => Store.Load(Guardrails).Settings;
        public string? Key(string name) => Keys.GetAsync(name, CancellationToken.None).Result;
    }

    private Computer Make(string name, string profile)
    {
        var folder = Path.Combine(root, name);
        var store = new CompanionSettingsStore(folder);
        var guardrails = new CompanionGuardrails(CompanionStatus.Profiles[profile]);
        var keys = new SessionCredentialStore();
        store.Save(CompanionSettingsStore.Defaults(guardrails));
        return new(store, guardrails, keys, new CompanionSync(folder, store, guardrails, keys, deviceId: name));
    }

    private async Task<SharedSettingsResult> SyncAsync(Computer computer)
    {
        var result = await computer.Sync.Node.SyncAsync([host], true, DateTimeOffset.UtcNow, CancellationToken.None);
        host = SharedSettings.Merge(host, result.Document);
        return result;
    }

    /// <summary>What a Windows desktop shared: OpenRouter with its key, an OpenAI voice with its key, Parakeet listening and two
    /// personalities, the active one Robin.</summary>
    private Personalities DesktopShares()
    {
        var at = DateTimeOffset.UtcNow.AddHours(-1);
        var personalities = Personalities.Create();
        personalities = personalities.Update(personalities.ActivePersonaId, "Robin", "Robin is calm and remembers the owner's games.");
        personalities = personalities.Add("Wren", personalities.ActivePersona).Select(personalities.ActivePersonaId);
        host = host
            .Put(AppSettingsSections.Thinking, AppSettingsSections.WriteRoute(new SharedRoute { Type = SharedRoute.ChatCompletions, Origin = OpenRouter, Model = "google/gemma-4-26b-a4b-it" }),
                OpenRouterKey, "desktop-windows", at)
            .Put(AppSettingsSections.Speaking, AppSettingsSections.WriteRoute(new SharedRoute { Type = SharedRoute.OpenAi, Model = "gpt-4o-mini-tts-2025-12-15", Voice = "coral" }),
                OpenAiKey, "desktop-windows", at)
            .Put(AppSettingsSections.Listening, AppSettingsSections.WriteRoute(new SharedRoute { Type = SharedRoute.Parakeet, Model = "parakeet-tdt-0.6b-v2-int8" }),
                null, "desktop-windows", at)
            .Put(AppSettingsSections.Companion, AppSettingsSections.WriteCompanion(personalities), null, "desktop-windows", at);
        return personalities;
    }

    [Fact]
    public async Task A_linux_companion_takes_the_desktops_choices_and_keys_and_keeps_its_own_computers_settings()
    {
        DesktopShares();
        var linux = Make("linux-companion", "linux-x64");
        linux.Store.Save(linux.Settings with { PushToTalkKey = "Control+Space", CharacterModel = "/home/me/robin.vrm" });

        var result = await SyncAsync(linux);

        var settings = linux.Settings;
        Assert.Equal(("chat-completions", OpenRouter, "google/gemma-4-26b-a4b-it"), (settings.Thinking, settings.ChatBaseUrl, settings.ChatModel));
        Assert.Equal(OpenRouterKey, linux.Key(CompanionSettings.ChatCompletionsKey));
        Assert.Equal(("openai-tts", "coral"), (settings.Speaking, settings.Voice));
        Assert.Equal(OpenAiKey, linux.Key(CompanionSettings.OpenAiSpeakingKey));
        Assert.Equal(("Robin", "Robin is calm and remembers the owner's games."), (settings.CharacterName, settings.Persona));
        // What describes this computer stays.
        Assert.Equal(("Control+Space", "/home/me/robin.vrm"), (settings.PushToTalkKey, settings.CharacterModel));
        // Parakeet can't run here: Listening keeps this computer's choice and says why.
        Assert.Equal("openai-stt", settings.Listening);
        Assert.True(result.Waiting.TryGetValue(AppSettingsSections.Listening, out var why) && why.StartsWith("Listening:", StringComparison.Ordinal), why);
        Assert.Contains(AppSettingsSections.Thinking, result.Applied.Select(a => a.Key));
        // Keys never reach the settings file or this computer's copy of the shared settings.
        var files = Directory.EnumerateFiles(linux.Store.Folder).Select(File.ReadAllText).ToArray();
        Assert.DoesNotContain(files, text => text.Contains(OpenRouterKey, StringComparison.Ordinal) || text.Contains(OpenAiKey, StringComparison.Ordinal));
        // Nothing was shared back as a change made here.
        Assert.Empty(result.Recorded);
    }

    [Fact]
    public async Task A_change_on_one_computer_reaches_the_other_with_its_key_and_keeps_the_other_personalities()
    {
        var shared = DesktopShares();
        var linux = Make("linux-companion", "linux-x64");
        var mac = Make("mac-companion", "macos-arm64");
        await SyncAsync(linux);
        await SyncAsync(mac);
        Assert.Equal("Robin", mac.Settings.CharacterName);

        // On the Linux companion the owner switches Thinking to OpenAI with a new key and edits the persona.
        await linux.Keys.SetAsync(CompanionSettings.OpenAiKey, "sk-lab-linux-new-key-0123456789", CancellationToken.None);
        linux.Store.Save(linux.Settings with { Thinking = "openai-llm", OpenAiModel = new CompanionSettings().OpenAiModel, Persona = "Robin is a cat." });
        var recorded = await SyncAsync(linux);
        Assert.Contains(AppSettingsSections.Thinking, recorded.Recorded);
        Assert.Contains(AppSettingsSections.Companion, recorded.Recorded);

        var followed = await SyncAsync(mac);
        Assert.Equal("openai-llm", mac.Settings.Thinking);
        Assert.Equal("sk-lab-linux-new-key-0123456789", mac.Key(CompanionSettings.OpenAiKey));
        Assert.Equal("Robin is a cat.", mac.Settings.Persona);
        Assert.Contains(AppSettingsSections.Thinking, followed.Applied.Select(a => a.Key));
        // Each computer keeps its own push-to-talk key (F8 is a media key on a Mac).
        Assert.Equal(("F8", "Control+Alt+T"), (linux.Settings.PushToTalkKey, mac.Settings.PushToTalkKey));

        // The desktop's other personality is still in the shared value, and the edit changed only the active one.
        var personalities = AppSettingsSections.ReadCompanion(host.Find(AppSettingsSections.Companion)!.Value);
        Assert.Equal(shared.Personas.Select(p => p.Id), personalities.Personas.Select(p => p.Id));
        Assert.Equal("Robin is a cat.", personalities.ActivePersona.Text);
        Assert.Equal(shared.Personas.Single(p => p.Name == "Wren").Text, personalities.Personas.Single(p => p.Name == "Wren").Text);

        // Nothing more changes once both computers agree.
        Assert.Empty((await SyncAsync(linux)).Recorded);
        Assert.Empty((await SyncAsync(mac)).Applied);
    }

    [Fact]
    public async Task Martlets_defaults_on_a_new_companion_never_override_a_choice_and_sync_can_be_turned_off()
    {
        var personalities = DesktopShares();
        var linux = Make("linux-companion", "linux-x64");
        linux.Sync.Enabled = false;
        Assert.Equal(0, (await linux.Sync.SyncPairedAsync(CancellationToken.None)).Hosts);
        linux.Sync.Enabled = true;

        var result = await SyncAsync(linux);
        Assert.Empty(result.Recorded);
        Assert.Equal(AppSettingsSections.WriteCompanion(personalities), host.Find(AppSettingsSections.Companion)!.Value);
        Assert.Equal("desktop-windows", host.Find(AppSettingsSections.Thinking)!.UpdatedBy);
    }
}
