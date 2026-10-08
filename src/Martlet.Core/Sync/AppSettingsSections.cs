using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Martlet.Core.Contracts;
using Martlet.Core.Lorebooks;
using Martlet.Core.Settings;

namespace Martlet.Core.Sync;

/// <summary>A job's route as it travels between computers: the provider and model (and voice), never a credential ID or a
/// paired host. <see cref="Type"/> is "openai", "chat-completions" (OpenRouter, NVIDIA Build, any OpenAI-compatible server,
/// Ollama on the computer itself), "windows-stt", "windows-tts" or "parakeet". Routes to a paired host travel in the
/// who-does-what plan instead, and this is the route a computer uses when no host does the job.</summary>
public sealed record SharedRoute
{
    public const string OpenAi = "openai", ChatCompletions = "chat-completions", WindowsStt = "windows-stt",
        WindowsTts = "windows-tts", Parakeet = "parakeet";

    public required string Type { get; init; }
    public string? Origin { get; init; }
    public required string Model { get; init; }
    public string? Voice { get; init; }

    /// <summary>Reads a shared route's value. Throws <see cref="ContractException"/> for one a newer Martlet wrote.</summary>
    public static SharedRoute Parse(string json) => AppSettingsSections.Read<SharedRoute>(json);
    /// <summary>The shareable form of <paramref name="route"/>, or null for routes tied to one computer's setup (a paired host,
    /// a local whisper package).</summary>
    public static SharedRoute? From(SetupRoute route) => route.RouteType switch
    {
        null or SetupRouteType.OpenAi => new() { Type = OpenAi, Model = route.ModelId, Voice = route.VoiceId },
        SetupRouteType.ChatCompletions => new() { Type = ChatCompletions, Origin = route.Origin, Model = route.ModelId },
        SetupRouteType.LocalWindowsStt => new() { Type = WindowsStt, Model = route.ModelId },
        SetupRouteType.LocalWindowsTts => new() { Type = WindowsTts, Model = route.ModelId, Voice = route.VoiceId },
        SetupRouteType.LocalParakeet => new() { Type = Parakeet, Model = route.ModelId },
        _ => null
    };

    /// <summary>A new, enabled route for <paramref name="role"/> without a key. Throws <see cref="ContractException"/> for a
    /// route this Martlet doesn't know (written by a newer one) or that doesn't fit the role.</summary>
    public SetupRoute Build(SetupRole role)
    {
        var route = (Type, role) switch
        {
            (OpenAi, _) => Route(SetupRouteType.OpenAi, role, OpenAiSetup.Alias(role), OpenAiSetup.Origin, Model, role == SetupRole.Tts ? Voice : null),
            (ChatCompletions, SetupRole.Llm) => Route(SetupRouteType.ChatCompletions, role, ChatCompletionsSetup.Alias, Origin ?? "", Model, null),
            (WindowsStt, SetupRole.Stt) => Route(SetupRouteType.LocalWindowsStt, role, WindowsSpeechSetup.SttAlias, SelfHostSetup.LocalOrigin, Model, null),
            (WindowsTts, SetupRole.Tts) => Route(SetupRouteType.LocalWindowsTts, role, WindowsSpeechSetup.TtsAlias, SelfHostSetup.LocalOrigin,
                WindowsSpeechSetup.TtsModelId, Voice),
            // A Parakeet model a newer Martlet added waits for this PC's update, like any route this Martlet doesn't know.
            (Parakeet, SetupRole.Stt) when LocalSpeechSetup.IsParakeetModel(Model) =>
                Route(SetupRouteType.LocalParakeet, role, LocalSpeechSetup.ParakeetAlias, SelfHostSetup.LocalOrigin, Model, null),
            _ => throw new ContractException(ErrorCode.UnsupportedVersion, "It was chosen on a newer Martlet. Update this PC to use it.")
        };
        route.Validate();
        return route;
    }

    private static SetupRoute Route(SetupRouteType type, SetupRole role, string alias, string origin, string model, string? voice) => new()
    {
        RouteSchemaVersion = 1, RouteType = type, Enabled = true, Role = role, ProviderAlias = alias, Origin = origin,
        ModelId = model, VoiceId = voice, ConfigurationRevision = Guid.NewGuid()
    };
}

/// <summary>The settings in settings.json and lorebooks.json that every computer shares: how Martlet thinks, listens and speaks
/// when no paired host does the job (provider, model, voice and API key), the Thinking fallback, the character's personas,
/// reply settings, edited prompts, whether memory is on, and the lorebooks. Audio devices, where memory is stored and paired
/// hosts stay with each computer. Applying goes through the same settings rules as Martlet's own pages: a key the computer
/// already has is reused, a replaced key the owner typed here is listed for removal (never orphaned), a replaced key this
/// computer only had from another computer is removed, and the owner's choice is recorded as made.
/// Call <see cref="Invalidate"/> before each sync so every section reads the current files.</summary>
public sealed class AppSettingsSections
{
    public const string Thinking = "thinking", Listening = "listening", Speaking = "speaking", ThinkingFallback = "thinking-fallback",
        Companion = "companion", Replies = "replies", Prompts = "prompts", Memory = "memory", Lorebooks = "lorebooks";

    internal static readonly JsonSerializerOptions Json = CreateJson();
    private static readonly JsonSerializerOptions LoreJson = CreateLoreJson();

    private readonly ISetupService setup;
    private readonly ICredentialStore vault;
    private readonly string dataDirectory;
    private readonly LorebookStore? lorebooks;
    private readonly Func<SetupRole, SetupRoute?> savedRoute;
    private readonly Func<SetupRole, SharedRoute, CancellationToken, Task<string?>> available;
    private readonly Dictionary<Guid, string?> keys = [];
    private Task<SettingsLoadResult>? loaded;
    /// <summary>The lorebooks' shared form for the file revision it was made from (they can be large).</summary>
    private (string? Revision, SharedLocal? Local)? lore;

    /// <param name="savedRoute">The route a job returns to while a paired host does it (kept aside on this computer).</param>
    /// <param name="available">Why this computer can't use a route yet (a Windows voice not installed, a model not downloaded),
    /// or null when it can.</param>
    public AppSettingsSections(ISetupService setup, ICredentialStore vault, string dataDirectory, LorebookStore? lorebooks,
        Func<SetupRole, SetupRoute?>? savedRoute = null, Func<SetupRole, SharedRoute, CancellationToken, Task<string?>>? available = null)
    {
        this.setup = setup;
        this.vault = vault;
        this.dataDirectory = dataDirectory;
        this.lorebooks = lorebooks;
        this.savedRoute = savedRoute ?? (_ => null);
        this.available = available ?? ((_, _, _) => Task.FromResult<string?>(null));
        Sections =
        [
            new Section(Thinking, "Thinking", t => ReadRouteAsync(SetupRole.Llm, t), (s, k, t) => ApplyRouteAsync(SetupRole.Llm, "Thinking", s, k, t)),
            new Section(Listening, "Listening", t => ReadRouteAsync(SetupRole.Stt, t), (s, k, t) => ApplyRouteAsync(SetupRole.Stt, "Listening", s, k, t)),
            new Section(Speaking, "Speaking", t => ReadRouteAsync(SetupRole.Tts, t), (s, k, t) => ApplyRouteAsync(SetupRole.Tts, "Speaking", s, k, t)),
            new Section(ThinkingFallback, "Thinking fallback", ReadFallbackAsync, ApplyFallbackAsync),
            new Section(Companion, "Personality", ReadCompanionAsync, ApplyCompanionAsync),
            new Section(Replies, "Replies", ReadRepliesAsync, ApplyRepliesAsync),
            new Section(Prompts, "Prompts", ReadPromptsAsync, ApplyPromptsAsync),
            new Section(Memory, "Memory", ReadMemoryAsync, ApplyMemoryAsync),
            .. lorebooks is null ? Array.Empty<ISharedSection>() : [new Section(Lorebooks, "Lorebooks", ReadLorebooksAsync, ApplyLorebooksAsync)]
        ];
    }

    public IReadOnlyList<ISharedSection> Sections { get; }

    /// <summary>Forgets the settings read so far, so the next read sees the files as they are now.</summary>
    public void Invalidate() => loaded = null;

    private Task<SettingsLoadResult> LoadAsync(CancellationToken token) => loaded ??= setup.LoadAsync(token);

    private DateTimeOffset? SettingsTime() => FileTime(Path.Combine(dataDirectory, "settings.json"));

    private static DateTimeOffset? FileTime(string path)
    {
        try { return File.Exists(path) ? new DateTimeOffset(File.GetLastWriteTimeUtc(path), TimeSpan.Zero) : null; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return null; }
    }

    internal static string Write<T>(T value) => JsonSerializer.Serialize(value, Json);

    internal static T Read<T>(string json) where T : class
    {
        try { return JsonSerializer.Deserialize<T>(json, Json) ?? throw new ContractException(ErrorCode.InvalidContract, "It is empty."); }
        catch (Exception error) when (error is JsonException or NotSupportedException or InvalidOperationException)
        {
            throw new ContractException(ErrorCode.UnsupportedVersion, "It was saved by a newer Martlet. Update this PC to use it.");
        }
    }

    private static T ReadContract<T>(string json) where T : IContract
    {
        try { return ContractJson.Read<T>(Encoding.UTF8.GetBytes(json)); }
        catch (ContractException error)
        {
            throw new ContractException(error.Code, $"It can't be used here ({error.Message}). Update this PC if another computer runs a newer Martlet.");
        }
    }

    /// <summary>The settings to change (upgraded, like Setup does) and their revision, or why they can't be changed now.</summary>
    private async Task<(AppSettings? Settings, string? Revision, string? Problem)> EditAsync(CancellationToken token)
    {
        var result = await setup.LoadAsync(token).ConfigureAwait(false);
        if (result.Error is not null) return (null, null, result.Error.Summary);
        var settings = SetupSettings.Begin(result.Settings);
        if (settings.Profile.Kind != ProfileKind.Api) settings = settings with { Profile = settings.Profile with { Kind = ProfileKind.Api } };
        return (settings, result.Revision, null);
    }

    private async Task<SharedApply> SaveAsync(AppSettings next, string? revision, CancellationToken token)
    {
        var saved = await setup.SaveAsync(next, revision, token).ConfigureAwait(false);
        loaded = null;
        return saved.Save.Saved ? SharedApply.Done : SharedApply.Waiting(saved.Summary);
    }

    private string? ReadKey(CredentialBinding binding)
    {
        if (keys.TryGetValue(binding.CredentialId, out var cached)) return cached;
        using var read = vault.Read(binding);
        string? value = null;
        read.Secret?.Use(chars => value = new string(chars));
        if (read.Error == CredentialError.None) keys[binding.CredentialId] = value;
        // A key that is gone means there is nothing to share; a vault that doesn't answer keeps this PC's setting as it is.
        else if (read.Error is not (CredentialError.Missing or CredentialError.InvalidInput))
            throw new ContractException(ErrorCode.InvalidContract, CredentialMessages.Describe(read.Error));
        return value;
    }

    private bool KeyIs(CredentialBinding binding, string secret) => ReadKey(binding) is { } value &&
        System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(value), Encoding.UTF8.GetBytes(secret));

    // ---------- routes ----------

    private async Task<SharedLocal?> ReadRouteAsync(SetupRole role, CancellationToken token)
    {
        var result = await LoadAsync(token).ConfigureAwait(false);
        if (result.Settings is not { Setup: { } setupSettings } settings) return null;
        var route = setupSettings.Routes.FirstOrDefault(r => r.Role == role);
        if (route is not null && SelfHostSetup.IsGateway(route.RouteType)) route = savedRoute(role);
        if (route is null || route.RouteType is not null && route.Enabled != true || route.Consent != route.Selection() ||
            SharedRoute.From(route) is not { } shared)
            return null;
        string? secret = null;
        DateTimeOffset? changed = null;
        if (route.CredentialId is { } id)
        {
            var binding = CredentialBinding.For(settings.Profile.Id, route, id);
            secret = ReadKey(binding);
            if (secret is null || !SharedSettings.IsSecret(secret)) return null;
            changed = (vault as ICredentialTimes)?.WrittenAt(binding);
        }
        else if (shared.Type == SharedRoute.OpenAi) return null;
        return new(Write(shared), secret, false, changed ?? SettingsTime());
    }

    /// <summary>Hands a job a paired host does back to the shared route (the who-does-what plan says nobody's host does it now),
    /// with the shared key. Not yet when this computer can't use the route.</summary>
    public Task<SharedApply> HandBackAsync(SetupRole role, string title, SharedSetting setting, string? secret, CancellationToken token)
    {
        Invalidate();
        return ApplyRouteAsync(role, title, setting, secret, token, fromHost: true);
    }

    private async Task<SharedApply> ApplyRouteAsync(SetupRole role, string title, SharedSetting setting, string? secret, CancellationToken token,
        bool fromHost = false)
    {
        var shared = Read<SharedRoute>(setting.Value);
        if (await available(role, shared, token).ConfigureAwait(false) is { } why) return SharedApply.Waiting(why);
        var (settings, revision, problem) = await EditAsync(token).ConfigureAwait(false);
        if (settings is null) return SharedApply.Waiting(problem!);
        var current = settings.Setup!.Routes.SingleOrDefault(r => r.Role == role);
        var onHost = current is not null && SelfHostSetup.IsGateway(current.RouteType);
        if (onHost && !fromHost)
            return SharedApply.Waiting($"{title} runs on {current!.Gateway?.HostId ?? "a paired host"} now. This PC switches to it when " +
                $"{title.ToLowerInvariant()} comes back here.");
        var built = shared.Build(role);
        // A key this PC already holds for the same provider is used again: the current one, or one set aside earlier.
        Guid? reuse = null;
        PendingCredentialRemoval? pending = null;
        if (secret is not null)
        {
            var scope = CredentialBinding.For(settings.Profile.Id, built, Guid.NewGuid()).ScopeDigest();
            if (!onHost && current?.CredentialId is { } currentId && CredentialBinding.For(settings.Profile.Id, current, currentId) is var binding &&
                binding.ScopeDigest() == scope && KeyIs(binding, secret))
                reuse = currentId;
            else
                foreach (var removal in settings.Setup.PendingRemovals.Where(r => r.Role == role))
                {
                    CredentialBinding candidate;
                    try { candidate = CredentialBinding.For(settings, removal); candidate.Validate(); }
                    catch (ContractException) { continue; }
                    if (candidate.ScopeDigest() != scope || !KeyIs(candidate, secret)) continue;
                    reuse = removal.CredentialId;
                    pending = removal;
                    break;
                }
        }
        AppSettings next;
        if (onHost)
            // Like handing the job back to its Setup choice: the host's pairing stays with the host, a key set aside is reattached.
            next = HostHandoff.Back(settings, built with { CredentialId = reuse });
        else
        {
            next = settings;
            if (pending is not null)
                next = next with { Setup = next.Setup! with { PendingRemovals = next.Setup.PendingRemovals.Where(r => r != pending).ToArray() } };
            next = SetupSettings.ReplaceRoute(next, built with { CredentialId = reuse });
            next = SetupSettings.QueueReplacedCredential(next, current);
        }
        if (secret is not null && reuse is null)
        {
            var staged = await setup.SaveAsync(next, revision, token).ConfigureAwait(false);
            loaded = null;
            if (!staged.Save.Saved) return SharedApply.Waiting(staged.Summary);
            using var lease = new SecretLease(secret);
            var stored = await setup.ReplaceCredentialAsync(staged.Settings, staged.Save.Revision, role, lease, token).ConfigureAwait(false);
            if (!stored.Save.Saved) return SharedApply.Waiting(stored.Summary);
            next = stored.Settings;
            revision = stored.Save.Revision;
            if (next.Setup!.Routes.Single(r => r.Role == role).CredentialId is { } written) RememberSyncedKey(written);
        }
        var route = next.Setup!.Routes.Single(r => r.Role == role);
        var result = await SaveAsync(SetupSettings.ReplaceRoute(next, route with { Consent = route.Selection() }), revision, token).ConfigureAwait(false);
        if (result.Applied) await ForgetReplacedSyncedKeysAsync(role, token).ConfigureAwait(false);
        return result;
    }

    // Keys this computer saved because another computer shared them. When a newer shared key replaces one, the old one is
    // removed at once (another computer replaced it); keys the owner typed here are only set aside, as Martlet's pages do.
    private string SyncedKeysFile => Path.Combine(dataDirectory, "shared-keys.txt");

    private HashSet<Guid> SyncedKeys()
    {
        try { return File.ReadAllLines(SyncedKeysFile).Select(line => Guid.TryParse(line, out var id) ? id : Guid.Empty).Where(id => id != Guid.Empty).ToHashSet(); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return []; }
    }

    private void SaveSyncedKeys(HashSet<Guid> ids)
    {
        try { File.WriteAllLines(SyncedKeysFile, ids.Select(id => id.ToString("D")).Order(StringComparer.Ordinal)); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }

    private void RememberSyncedKey(Guid id)
    {
        var ids = SyncedKeys();
        if (ids.Add(id)) SaveSyncedKeys(ids);
    }

    private async Task ForgetReplacedSyncedKeysAsync(SetupRole role, CancellationToken token)
    {
        var ids = SyncedKeys();
        if (ids.Count == 0) return;
        for (var attempt = 0; attempt < SetupSettings.MaximumRetainedGatewayCredentials; attempt++)
        {
            var current = await setup.LoadAsync(token).ConfigureAwait(false);
            if (current.Settings?.Setup is not { } setupSettings) break;
            var removal = setupSettings.PendingRemovals.FirstOrDefault(p => p.Role == role && ids.Contains(p.CredentialId));
            if (removal is null) break;
            var removed = await setup.RemoveDetachedAsync(current.Settings, current.Revision, removal, token).ConfigureAwait(false);
            if (!removed.Save.Saved) break;
            ids.Remove(removal.CredentialId);
        }
        loaded = null;
        var kept = await setup.LoadAsync(token).ConfigureAwait(false);
        if (kept.Settings?.Setup is { } latest)
            ids.IntersectWith(latest.Routes.Select(r => r.CredentialId).Concat(latest.PendingRemovals.Select(p => (Guid?)p.CredentialId)).OfType<Guid>());
        SaveSyncedKeys(ids);
    }

    // ---------- Thinking fallback ----------

    private sealed record SharedFallback
    {
        public required string Origin { get; init; }
        public required string Model { get; init; }
    }

    private async Task<SharedLocal?> ReadFallbackAsync(CancellationToken token)
    {
        var result = await LoadAsync(token).ConfigureAwait(false);
        if (result.Settings is not { } settings) return null;
        if (settings.ThinkingFallback is not { } fallback) return new("null", null, true, SettingsTime());
        string? secret = null;
        DateTimeOffset? changed = null;
        if (fallback.CredentialId is { } id)
        {
            var binding = fallback.Binding(settings.Profile.Id, id);
            secret = ReadKey(binding);
            if (secret is null || !SharedSettings.IsSecret(secret)) return null;
            changed = (vault as ICredentialTimes)?.WrittenAt(binding);
        }
        return new(Write(new SharedFallback { Origin = fallback.Origin, Model = fallback.ModelId }), secret, false, changed ?? SettingsTime());
    }

    private async Task<SharedApply> ApplyFallbackAsync(SharedSetting setting, string? secret, CancellationToken token)
    {
        var shared = setting.Value == "null" ? null : Read<SharedFallback>(setting.Value);
        var (settings, revision, problem) = await EditAsync(token).ConfigureAwait(false);
        if (settings is null) return SharedApply.Waiting(problem!);
        var old = settings.ThinkingFallback;
        ThinkingFallbackSettings? chosen = null;
        CredentialBinding? written = null;
        try
        {
            if (shared is not null)
            {
                var keep = secret is not null && old is { CredentialId: { } oldId } && old.Origin == shared.Origin &&
                    KeyIs(old.Binding(settings.Profile.Id, oldId), secret) ? old.CredentialId : null;
                chosen = new() { Origin = shared.Origin, ModelId = shared.Model, CredentialId = keep, ConfigurationRevision = Guid.NewGuid() };
                chosen.Validate();
                if (secret is not null && keep is null)
                {
                    chosen = chosen with { CredentialId = Guid.NewGuid() };
                    var binding = chosen.Binding(settings.Profile.Id, chosen.CredentialId!.Value);
                    using var lease = new SecretLease(secret);
                    var error = vault.Write(binding, lease);
                    if (error != CredentialError.None) return SharedApply.Waiting(CredentialMessages.Describe(error));
                    written = binding;
                }
            }
            var result = await SaveAsync(settings with { ThinkingFallback = chosen }, revision, token).ConfigureAwait(false);
            if (!result.Applied) return result;
            written = null;
            if (old?.CredentialId is { } oldKey && oldKey != chosen?.CredentialId) vault.Delete(old.Binding(settings.Profile.Id, oldKey));
            return result;
        }
        finally
        {
            if (written is { } orphan) vault.Delete(orphan);
        }
    }

    // ---------- personality, replies, prompts, memory ----------

    private async Task<SharedLocal?> ReadCompanionAsync(CancellationToken token)
    {
        var result = await LoadAsync(token).ConfigureAwait(false);
        if (result.Settings?.Companion is not { } companion) return null;
        var persona = companion.Personas.Count == 1 ? companion.Personas[0] : null;
        var isDefault = persona is not null && persona.Name == "Martlet" && persona.Text == CompanionSettings.Create().Personas[0].Text &&
            persona.Breaks is null;
        return new(Write(companion), null, isDefault, SettingsTime());
    }

    private async Task<SharedApply> ApplyCompanionAsync(SharedSetting setting, string? secret, CancellationToken token)
    {
        var companion = ReadContract<CompanionSettings>(setting.Value);
        var (settings, revision, problem) = await EditAsync(token).ConfigureAwait(false);
        return settings is null ? SharedApply.Waiting(problem!) : await SaveAsync(settings with { Companion = companion }, revision, token).ConfigureAwait(false);
    }

    private async Task<SharedLocal?> ReadRepliesAsync(CancellationToken token)
    {
        var result = await LoadAsync(token).ConfigureAwait(false);
        if (result.Settings is not { } settings) return null;
        var generation = GenerationSettings.Normalize(settings.Generation);
        return new(generation is null ? "null" : Write(generation), null, generation is null, SettingsTime());
    }

    private async Task<SharedApply> ApplyRepliesAsync(SharedSetting setting, string? secret, CancellationToken token)
    {
        var generation = setting.Value == "null" ? null : GenerationSettings.Normalize(ReadContract<GenerationSettings>(setting.Value));
        var (settings, revision, problem) = await EditAsync(token).ConfigureAwait(false);
        return settings is null ? SharedApply.Waiting(problem!) : await SaveAsync(settings with { Generation = generation }, revision, token).ConfigureAwait(false);
    }

    private async Task<SharedLocal?> ReadPromptsAsync(CancellationToken token)
    {
        var result = await LoadAsync(token).ConfigureAwait(false);
        if (result.Settings is not { } settings) return null;
        if (settings.Prompts is not { IsDefault: false } prompts) return new("null", null, true, SettingsTime());
        var sorted = new PromptSettings { Overrides = new SortedDictionary<string, string>(prompts.Overrides.ToDictionary(), StringComparer.Ordinal) };
        return new(Write(sorted), null, false, SettingsTime());
    }

    private async Task<SharedApply> ApplyPromptsAsync(SharedSetting setting, string? secret, CancellationToken token)
    {
        var prompts = setting.Value == "null" ? null : ReadContract<PromptSettings>(setting.Value);
        if (prompts is { IsDefault: true }) prompts = null;
        var (settings, revision, problem) = await EditAsync(token).ConfigureAwait(false);
        return settings is null ? SharedApply.Waiting(problem!) : await SaveAsync(settings with { Prompts = prompts }, revision, token).ConfigureAwait(false);
    }

    private sealed record SharedMemory
    {
        public required bool Enabled { get; init; }
    }

    private async Task<SharedLocal?> ReadMemoryAsync(CancellationToken token)
    {
        var result = await LoadAsync(token).ConfigureAwait(false);
        if (result.Settings is not { } settings) return null;
        var enabled = AppSettings.ApplyMemoryDefault(settings).Memory?.Enabled ?? true;
        return new(Write(new SharedMemory { Enabled = enabled }), null, enabled, SettingsTime());
    }

    private async Task<SharedApply> ApplyMemoryAsync(SharedSetting setting, string? secret, CancellationToken token)
    {
        var shared = Read<SharedMemory>(setting.Value);
        var (settings, revision, problem) = await EditAsync(token).ConfigureAwait(false);
        if (settings is null) return SharedApply.Waiting(problem!);
        var memory = settings.Memory ?? MemorySettings.Create();
        return await SaveAsync(settings with { Memory = memory.Configure(shared.Enabled, memory.StoragePolicy, memory.CustomDirectory) },
            revision, token).ConfigureAwait(false);
    }

    // ---------- lorebooks ----------

    private async Task<SharedLocal?> ReadLorebooksAsync(CancellationToken token)
    {
        var result = await lorebooks!.LoadAsync(token).ConfigureAwait(false);
        // Lorebooks this PC can't read are kept as they are, never replaced by another computer's.
        if (!result.Loaded) throw new ContractException(ErrorCode.InvalidContract, result.Error ?? "They can't be read here.");
        if (lore is not { } cached || cached.Revision != result.Revision)
        {
            var value = JsonSerializer.Serialize(result.Library, LoreJson);
            lore = cached = (result.Revision, Encoding.UTF8.GetByteCount(value) > SharedSettings.MaximumValueBytes ? null
                : new SharedLocal(value, null, result.Library.Books.Count == 0, FileTime(lorebooks.FilePath)));
        }
        return cached.Local ?? throw new ContractException(ErrorCode.PayloadTooLarge,
            $"They are larger than {SharedSettings.MaximumValueBytes / (1024 * 1024)} MB, so this PC keeps its own.");
    }

    private async Task<SharedApply> ApplyLorebooksAsync(SharedSetting setting, string? secret, CancellationToken token)
    {
        LorebookLibrary library;
        try { library = JsonSerializer.Deserialize<LorebookLibrary>(setting.Value, LoreJson) ?? throw new JsonException(); }
        catch (Exception error) when (error is JsonException or NotSupportedException or InvalidOperationException)
        {
            return SharedApply.Waiting("They were saved by a newer Martlet. Update this PC to use them.");
        }
        library.Validate();
        var current = await lorebooks!.LoadAsync(token).ConfigureAwait(false);
        if (!current.Loaded) return SharedApply.Waiting(current.Error!);
        var saved = await lorebooks.SaveAsync(library, current.Revision, token).ConfigureAwait(false);
        return saved.Saved ? SharedApply.Done : SharedApply.Waiting(saved.Error ?? "They couldn't be saved.");
    }

    private static JsonSerializerOptions CreateJson()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            RespectNullableAnnotations = true,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
            MaxDepth = 16
        };
        options.Converters.Add(new ExactEnumConverterFactory());
        options.MakeReadOnly(populateMissingResolver: true);
        return options;
    }

    // The lorebook file's own JSON shape (camel case), without indentation.
    private static JsonSerializerOptions CreateLoreJson()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            MaxDepth = 16
        };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false));
        options.MakeReadOnly(populateMissingResolver: true);
        return options;
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

/// <summary>A shared setting backed by one of the desktop's own preference files, from plain delegates.</summary>
public sealed class DelegateSection(string key, string title, Func<CancellationToken, Task<SharedLocal?>> read,
    Func<SharedSetting, CancellationToken, Task<SharedApply>> apply) : ISharedSection
{
    public string Key => key;
    public string Title => title;
    public Task<SharedLocal?> ReadAsync(CancellationToken token) => read(token);
    public Task<SharedApply> ApplyAsync(SharedSetting setting, string? secret, CancellationToken token) => apply(setting, token);
}
