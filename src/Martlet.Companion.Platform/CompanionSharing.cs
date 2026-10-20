using System.Text.Json;
using System.Text.Json.Serialization;
using Martlet.Avatar.Audio2Face.Remote;
using Martlet.Companion.Platform;
using Martlet.Core.Cluster;
using Martlet.Core.Contracts;
using Martlet.Core.Network;
using Martlet.Core.Settings;
using Martlet.Core.Sync;
using Martlet.Providers;
using ICredentialStore = Martlet.Companion.Platform.ICredentialStore;
using Personalities = Martlet.Core.Settings.CompanionSettings;

namespace Martlet.Companion;

/// <summary>The companion's settings that every one of the owner's computers shares (docs/CLUSTER.md, "One Martlet on every
/// computer"), in the same shared settings and the same values as the Windows desktop: <c>thinking</c>, <c>listening</c> and
/// <c>speaking</c> (provider, model, voice and the API key as the setting's secret) and <c>companion</c> (the personalities: the
/// character's name and persona are the active one). A shared choice this computer can't run (a Windows voice, Parakeet,
/// a model a newer Martlet added) keeps this computer's choice and says why (<see cref="CompanionGuardrails.Admit"/>). The
/// push-to-talk key, the character file, whether it shows, lip-sync and the cloud consent stay here.</summary>
public sealed class CompanionSharedSections
{
    private const string Newer = "It was chosen on a newer Martlet. Update Martlet on this computer to use it.";
    private readonly CompanionSettingsStore store;
    private readonly CompanionGuardrails guardrails;
    private readonly ICredentialStore credentials;

    public CompanionSharedSections(CompanionSettingsStore store, CompanionGuardrails guardrails, ICredentialStore credentials)
    {
        this.store = store;
        this.guardrails = guardrails;
        this.credentials = credentials;
        Sections =
        [
            new Section(AppSettingsSections.Thinking, "Thinking", ReadThinkingAsync, ApplyThinkingAsync),
            new Section(AppSettingsSections.Listening, "Listening", ReadListeningAsync, ApplyListeningAsync),
            new Section(AppSettingsSections.Speaking, "Speaking", ReadSpeakingAsync, ApplySpeakingAsync),
            new Section(AppSettingsSections.Companion, "Personality", ReadPersonalitiesAsync, ApplyPersonalitiesAsync)
        ];
    }

    public IReadOnlyList<ISharedSection> Sections { get; }

    private CompanionSettings Current() => store.Load(guardrails).Settings;

    private DateTimeOffset? SettingsTime() =>
        File.Exists(store.FilePath) ? new DateTimeOffset(File.GetLastWriteTimeUtc(store.FilePath), TimeSpan.Zero) : null;

    private async Task<string?> KeyAsync(string name, string? fallback, CancellationToken token)
    {
        var key = (await credentials.GetAsync(name, token))?.Trim();
        if (string.IsNullOrEmpty(key) && fallback is not null) key = (await credentials.GetAsync(fallback, token))?.Trim();
        return SharedSettings.IsSecret(key) ? key : null;
    }

    private async Task SetKeyAsync(string name, string? secret, CancellationToken token)
    {
        if (secret is null) await credentials.DeleteAsync(name, token);
        else await credentials.SetAsync(name, secret, token);
    }

    private SharedApply Admit(CompanionSettings incoming, CompanionSettings current, out CompanionSettings admitted)
    {
        (admitted, var refusals) = guardrails.Admit(incoming, current);
        return refusals.Count == 0 ? SharedApply.Done : SharedApply.Waiting(refusals[0]);
    }

    private static string Route(string type, string model, string? origin = null, string? voice = null) =>
        AppSettingsSections.WriteRoute(new SharedRoute { Type = type, Model = model, Origin = origin, Voice = voice });

    // ---------- Thinking ----------

    private async Task<SharedLocal?> ReadThinkingAsync(CancellationToken token)
    {
        var settings = Current();
        if (settings.Thinking == "openai-llm")
        {
            var key = await KeyAsync(CompanionSettings.OpenAiKey, null, token);
            return key is null ? null : new(Route(SharedRoute.OpenAi, settings.OpenAiModel), key, false, SettingsTime());
        }
        if (settings.Thinking != "chat-completions") return null;
        try
        {
            ChatCompletionsSetup.BaseUri(settings.ChatBaseUrl);
            ChatCompletionsSetup.ModelId(settings.ChatModel);
        }
        catch (ContractException) { return null; }
        var defaults = new CompanionSettings();
        var isDefault = settings.ChatBaseUrl == defaults.ChatBaseUrl && settings.ChatModel == defaults.ChatModel;
        return new(Route(SharedRoute.ChatCompletions, settings.ChatModel, settings.ChatBaseUrl),
            await KeyAsync(CompanionSettings.ChatCompletionsKey, null, token), isDefault, SettingsTime());
    }

    private async Task<SharedApply> ApplyThinkingAsync(SharedSetting setting, string? secret, CancellationToken token)
    {
        var shared = SharedRoute.Parse(setting.Value);
        var current = Current();
        CompanionSettings incoming;
        if (shared.Type == SharedRoute.OpenAi)
        {
            if (!OpenAiTextGenerationCatalog.SupportsModel(shared.Model))
                return SharedApply.Waiting($"The OpenAI model {shared.Model} isn't offered on this computer yet. Update Martlet here to use it.");
            incoming = current with { Thinking = "openai-llm", OpenAiModel = shared.Model };
        }
        else if (shared.Type == SharedRoute.ChatCompletions)
        {
            try
            {
                ChatCompletionsSetup.BaseUri(shared.Origin ?? "");
                ChatCompletionsSetup.ModelId(shared.Model);
            }
            catch (ContractException error) { return SharedApply.Waiting(error.Message); }
            incoming = current with { Thinking = "chat-completions", ChatBaseUrl = shared.Origin!, ChatModel = shared.Model };
        }
        else return SharedApply.Waiting(Newer);
        if (Admit(incoming, current, out var admitted) is { Applied: false } refused) return refused;
        if (shared.Type == SharedRoute.OpenAi) await SetKeyAsync(CompanionSettings.OpenAiKey, secret, token);
        else await SetKeyAsync(CompanionSettings.ChatCompletionsKey, secret, token);
        store.Save(admitted);
        return SharedApply.Done;
    }

    // ---------- Listening ----------

    private async Task<SharedLocal?> ReadListeningAsync(CancellationToken token)
    {
        var settings = Current();
        if (settings.Listening != "openai-stt") return null;
        var key = await KeyAsync(CompanionSettings.OpenAiListeningKey, CompanionSettings.OpenAiKey, token);
        return key is null ? null : new(Route(SharedRoute.OpenAi, settings.ListeningModel), key, false, SettingsTime());
    }

    private async Task<SharedApply> ApplyListeningAsync(SharedSetting setting, string? secret, CancellationToken token)
    {
        var shared = SharedRoute.Parse(setting.Value);
        var current = Current();
        var engine = shared.Type switch
        {
            SharedRoute.OpenAi => "openai-stt",
            SharedRoute.WindowsStt => "windows-speech",
            SharedRoute.Parakeet => "local-whisper",
            _ => null
        };
        if (engine is null) return SharedApply.Waiting(Newer);
        if (engine == "openai-stt" && !OpenAiTranscriptionCatalog.SupportsModel(shared.Model))
            return SharedApply.Waiting($"The OpenAI model {shared.Model} isn't offered on this computer yet. Update Martlet here to use it.");
        var incoming = current with { Listening = engine, ListeningModel = engine == "openai-stt" ? shared.Model : current.ListeningModel };
        if (Admit(incoming, current, out var admitted) is { Applied: false } refused) return refused;
        await SetKeyAsync(CompanionSettings.OpenAiListeningKey, secret, token);
        store.Save(admitted);
        return SharedApply.Done;
    }

    // ---------- Speaking ----------

    private async Task<SharedLocal?> ReadSpeakingAsync(CancellationToken token)
    {
        var settings = Current();
        if (settings.Speaking != "openai-tts") return null;
        var key = await KeyAsync(CompanionSettings.OpenAiSpeakingKey, CompanionSettings.OpenAiKey, token);
        return key is null ? null : new(Route(SharedRoute.OpenAi, settings.SpeakingModel, voice: settings.Voice), key, false, SettingsTime());
    }

    private async Task<SharedApply> ApplySpeakingAsync(SharedSetting setting, string? secret, CancellationToken token)
    {
        var shared = SharedRoute.Parse(setting.Value);
        if (shared.Type == SharedRoute.WindowsTts)
            return SharedApply.Waiting("Windows voices were removed from Martlet. Choose a voice for this computer in Settings.");
        if (shared.Type != SharedRoute.OpenAi) return SharedApply.Waiting(Newer);
        if (!OpenAiSpeechSynthesisCatalog.SupportsModel(shared.Model) || !OpenAiSpeechSynthesisCatalog.SupportsVoice(shared.Voice))
            return SharedApply.Waiting($"The OpenAI voice {shared.Voice} ({shared.Model}) isn't offered on this computer yet. Update Martlet here to use it.");
        var current = Current();
        var incoming = current with { Speaking = "openai-tts", SpeakingModel = shared.Model, Voice = shared.Voice! };
        if (Admit(incoming, current, out var admitted) is { Applied: false } refused) return refused;
        await SetKeyAsync(CompanionSettings.OpenAiSpeakingKey, secret, token);
        store.Save(admitted);
        return SharedApply.Done;
    }

    // ---------- Personality ----------

    private Task<SharedLocal?> ReadPersonalitiesAsync(CancellationToken token)
    {
        var settings = Current();
        var defaults = new CompanionSettings();
        var isDefault = settings.SharedPersonalities is null && settings.CharacterName == defaults.CharacterName && settings.Persona == defaults.Persona;
        Personalities personalities;
        try
        {
            personalities = settings.SharedPersonalities is { } kept ? AppSettingsSections.ReadCompanion(kept) : Personalities.Create();
        }
        catch (ContractException) { personalities = Personalities.Create(); }
        var active = personalities.ActivePersona;
        if (settings.SharedPersonalities is { } raw && active.Name == settings.CharacterName && active.Text == settings.Persona)
            return Task.FromResult<SharedLocal?>(new(raw, null, false, SettingsTime()));
        try { personalities = personalities.Update(active.Id, settings.CharacterName, settings.Persona); }
        catch (ContractException) { return Task.FromResult<SharedLocal?>(null); }
        // Kept, so the next read gives the same value (a change made here is a new revision of the active personality once).
        var value = AppSettingsSections.WriteCompanion(personalities);
        store.Save(settings with { SharedPersonalities = value });
        return Task.FromResult<SharedLocal?>(new(value, null, isDefault, SettingsTime()));
    }

    private Task<SharedApply> ApplyPersonalitiesAsync(SharedSetting setting, string? secret, CancellationToken token)
    {
        var personalities = AppSettingsSections.ReadCompanion(setting.Value);
        var active = personalities.ActivePersona;
        store.Save(Current() with { CharacterName = active.Name, Persona = active.Text, SharedPersonalities = setting.Value });
        return Task.FromResult(SharedApply.Done);
    }

    private sealed class Section(string key, string title, Func<CancellationToken, Task<SharedLocal?>> read,
        Func<SharedSetting, string?, CancellationToken, Task<SharedApply>> apply) : ISharedSection
    {
        public string Key => key;
        public string Title => title;
        public Task<SharedLocal?> ReadAsync(CancellationToken token) => read(token);
        public Task<SharedApply> ApplyAsync(SharedSetting setting, string? secret, CancellationToken token) => apply(setting, secret, token);
    }
}

/// <summary>The hosts this companion is paired with (hosts.json in its data folder, like the desktop's) and their device
/// secrets in the platform credential store. A host is paired with the address and short code it shows, as on Windows.</summary>
public sealed class CompanionHosts(string folder, ICredentialStore credentials)
{
    public const string FileName = "hosts.json";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    private sealed record Entry(Audio2FaceHostPairing Pairing);
    private sealed record Document(IReadOnlyList<Entry> Hosts);

    public string FilePath => Path.Combine(folder, FileName);

    public IReadOnlyList<Audio2FaceHostPairing> Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return [];
            var document = JsonSerializer.Deserialize<Document>(File.ReadAllBytes(FilePath), Json);
            return [.. (document?.Hosts ?? []).Select(h => h.Pairing).Where(Valid)];
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException) { return []; }
    }

    private static bool Valid(Audio2FaceHostPairing? pairing)
    {
        try { pairing?.Validate(); return pairing is not null; }
        catch (Audio2FaceHostException) { return false; }
    }

    private void Save(IEnumerable<Audio2FaceHostPairing> hosts)
    {
        Directory.CreateDirectory(folder);
        var temporary = FilePath + ".tmp";
        File.WriteAllBytes(temporary, JsonSerializer.SerializeToUtf8Bytes(new Document([.. hosts.Select(h => new Entry(h))]), Json));
        File.Move(temporary, FilePath, overwrite: true);
    }

    private static string SecretName(Audio2FaceHostPairing pairing) => "host-" + pairing.CredentialId;

    /// <summary>Keeps <paramref name="pairing"/> (replacing an earlier one with the same host) and its secret.</summary>
    public async Task AddAsync(Audio2FaceHostPairing pairing, string secret, CancellationToken token)
    {
        pairing.Validate();
        await credentials.SetAsync(SecretName(pairing), secret, token);
        var earlier = Load();
        foreach (var old in earlier.Where(h => h.HostId == pairing.HostId && h.CredentialId != pairing.CredentialId))
            await credentials.DeleteAsync(SecretName(old), token);
        Save([.. earlier.Where(h => h.HostId != pairing.HostId), pairing]);
    }

    public async Task RemoveAsync(string hostId, CancellationToken token)
    {
        var hosts = Load();
        foreach (var host in hosts.Where(h => h.HostId == hostId)) await credentials.DeleteAsync(SecretName(host), token);
        Save(hosts.Where(h => h.HostId != hostId));
    }

    /// <summary>Pairs with the host at <paramref name="address"/> that shows <paramref name="code"/> (docs/HOST_SETUP.md) and keeps it.</summary>
    public async Task<Audio2FaceHostPairing> PairAsync(string address, string code, string deviceId, CancellationToken token)
    {
        var (pairing, secret) = await Audio2FaceHostClient.PairWithCodeAsync(HostPairingInput.Origin(address), code, deviceId,
            Environment.MachineName, token);
        await AddAsync(pairing, secret, token);
        return pairing;
    }

    /// <summary>A signed connection to <paramref name="pairing"/>, or null when this computer no longer has its secret (keys kept
    /// only until Martlet quits, for example): pair again.</summary>
    public async Task<Audio2FaceHostConnection?> ConnectAsync(Audio2FaceHostPairing pairing, CancellationToken token) =>
        await credentials.GetAsync(SecretName(pairing), token) is { Length: > 0 } secret ? new Audio2FaceHostConnection(pairing, secret) : null;
}

/// <summary>What one sync of the companion did.</summary>
public sealed record CompanionSyncReport(int Hosts, int Reached, IReadOnlyList<string> Took, IReadOnlyList<string> Shared,
    IReadOnlyDictionary<string, string> Waiting, DateTimeOffset At)
{
    /// <summary>The report in words, for the settings page.</summary>
    public string Describe()
    {
        if (Hosts == 0) return "Not paired with a host yet: pair one to keep Martlet the same as on your other computers.";
        var lines = new List<string> { $"Checked {Reached} of {Hosts} host(s) at {At.ToLocalTime():t}." };
        if (Took.Count > 0) lines.Add("Took from your other computers: " + string.Join(", ", Took) + ".");
        if (Shared.Count > 0) lines.Add("Shared from this computer: " + string.Join(", ", Shared) + ".");
        lines.AddRange(Waiting.Select(w => $"Not on this computer yet ({w.Key}): {w.Value}"));
        return string.Join("\n", lines);
    }
}

/// <summary>Keeps the companion's shared settings the same as on the owner's other computers through the paired hosts, with the
/// desktop's sync engine (<see cref="SharedSettingsNode"/>): read every host's copy, record the changes made here, follow
/// what is newer elsewhere and give each host the merged copy. Its copy (without keys) is shared-settings.json in the data
/// folder. "Keep Martlet the same on all my computers" (cluster-sync.txt) turns it off. Call it on one thread at a time.</summary>
public sealed class CompanionSync
{
    private readonly string folder;
    private readonly CompanionSharedSections sections;

    public CompanionSync(string folder, CompanionSettingsStore store, CompanionGuardrails guardrails, ICredentialStore credentials,
        string? deviceId = null)
    {
        this.folder = folder;
        DeviceId = deviceId ?? DeviceIds.Ensure(folder).Id;
        sections = new CompanionSharedSections(store, guardrails, credentials);
        Node = new SharedSettingsNode(folder, DeviceId, sections.Sections);
        Hosts = new CompanionHosts(folder, credentials);
    }

    public string DeviceId { get; }
    public SharedSettingsNode Node { get; }
    public CompanionHosts Hosts { get; }
    public CompanionSyncReport? Last { get; private set; }

    public bool Enabled
    {
        get => ClusterSyncChoice.Enabled(folder);
        set => ClusterSyncChoice.Save(folder, value);
    }

    /// <summary>One sync through <paramref name="hosts"/> (signed connections to the paired hosts).</summary>
    public async Task<CompanionSyncReport> SyncAsync(IReadOnlyList<Audio2FaceHostConnection> hosts, CancellationToken token, int? paired = null)
    {
        var copies = new List<SharedSettings>();
        var reached = new List<Audio2FaceHostConnection>();
        foreach (var host in hosts)
        {
            try
            {
                copies.Add(await host.ReadSettingsAsync(token));
                reached.Add(host);
            }
            catch (Exception error) when (IsHostFailure(error, token)) { }
        }
        var result = await Node.SyncAsync(copies, true, DateTimeOffset.UtcNow, token);
        foreach (var host in reached)
        {
            try { await host.MergeSettingsAsync(result.Document, token); }
            catch (Exception error) when (IsHostFailure(error, token)) { }
        }
        string Title(string key) => sections.Sections.FirstOrDefault(s => s.Key == key)?.Title ?? key;
        Last = new(paired ?? hosts.Count, reached.Count, [.. result.Applied.Select(a => a.Title)], [.. result.Recorded.Select(Title)],
            result.Waiting.ToDictionary(w => Title(w.Key), w => w.Value), DateTimeOffset.UtcNow);
        return Last;
    }

    /// <summary>One sync through every paired host (none while sync is off).</summary>
    public async Task<CompanionSyncReport> SyncPairedAsync(CancellationToken token)
    {
        var paired = Enabled ? Hosts.Load() : [];
        var connections = new List<Audio2FaceHostConnection>();
        try
        {
            foreach (var pairing in paired)
                if (await Hosts.ConnectAsync(pairing, token) is { } connection) connections.Add(connection);
            return await SyncAsync(connections, token, paired.Count);
        }
        finally
        {
            foreach (var connection in connections) connection.Dispose();
        }
    }

    private static bool IsHostFailure(Exception error, CancellationToken token) =>
        error is Audio2FaceHostException or HttpRequestException or IOException or ContractException or InvalidOperationException or
            JsonException or TimeoutException || error is OperationCanceledException && !token.IsCancellationRequested;

    /// <summary>The sync as the headless status reports it: no keys.</summary>
    public static object Describe(string folder)
    {
        var (document, _) = SharedSettingsState.Load(folder);
        string[] keys = [AppSettingsSections.Thinking, AppSettingsSections.Listening, AppSettingsSections.Speaking, AppSettingsSections.Companion];
        return new
        {
            enabled = ClusterSyncChoice.Enabled(folder),
            deviceId = DeviceIds.Peek(folder)?.Id,
            pairedHosts = new CompanionHosts(folder, new SessionCredentialStore()).Load().Select(h => h.HostId).ToArray(),
            shared = keys.Select(key => document.Find(key) is { } entry
                ? new { key, updatedBy = (string?)entry.UpdatedBy, updatedAt = (DateTimeOffset?)entry.UpdatedAt }
                : new { key, updatedBy = (string?)null, updatedAt = (DateTimeOffset?)null }).ToArray(),
            staysHere = new[] { "pushToTalkKey", "characterModel", "showCharacter", "lipSync", "cloudConsent" }
        };
    }
}
