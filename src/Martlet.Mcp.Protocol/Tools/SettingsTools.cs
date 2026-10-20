using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using Martlet.Core.Installation;
using Martlet.Doctor;
using Martlet.Mcp.Client;

namespace Martlet.Mcp;

/// <summary>martlet_guide's "Settings" area: Martlet's settings, prompts, API keys, setup runs and the recommended setup. Each
/// Tool(...) registers one tool: its name, description, input schema, handler and, when it needs one, its permission (<see
/// cref="ToolPermission"/>). A new tool of this area goes here.</summary>
internal sealed partial class McpServer
{
    private McpTool[] SettingsTools() =>
    [
        Tool("settings_get", "Read Martlet's settings (settings.json) or one part of them by path (snake_case, dots and [index], for " +
            "example generation, generation.temperature or companion.personas[0].name). Without a path it also lists the parts and " +
            "which of them settings_set may change. Returns the revision to pass to settings_set. Holds no API keys. " +
            DataDirectoryNote + " Read-only.", new
        {
            path = new { type = "string", maxLength = 256 },
            dataDirectory = new { type = "string" }
        },
            async (arguments, cancellation) => await SettingsConfiguration.GetAsync(DataDirectory(arguments), OptionalString(arguments, "path"), cancellation)),
        Tool("settings_schema", "Show what a part of Martlet's settings can hold: the JSON schema of settings.json at path (property " +
            "names, types, allowed values; for example generation or memory), cut short below depth levels (default 2 for the whole " +
            "file, 4 for a path). Read-only.", new
        {
            path = new { type = "string", maxLength = 256 },
            depth = new { type = "integer", minimum = 1, maximum = 12 }
        },
            arguments => SettingsConfiguration.Schema(OptionalString(arguments, "path"), OptionalInt(arguments, "depth"))),
        Tool("settings_set", "Change one part of Martlet's settings: path (as for settings_get) and value (any JSON; null removes it, " +
            "which puts an optional value back to its default). Changes companion (personalities and profiles), generation (how " +
            "replies are made), prompts and memory; where Martlet thinks, listens and speaks, its API keys and devices change only " +
            "in the app, because they need the owner's consent. Checked by Martlet's own settings rules before it is saved; revision " +
            "(from settings_get) refuses the change if the settings changed since. A running Martlet follows by itself. Needs " +
            "--allow-changes. " + DataDirectoryNote, new
        {
            path = new { type = "string", maxLength = 256 },
            value = new { },
            revision = new { type = "string", maxLength = 128 },
            dataDirectory = new { type = "string" }
        }, ["path", "value"],
            async (arguments, cancellation) => await SettingsConfiguration.SetAsync(DataDirectory(arguments),
                RequiredString(arguments, "path"), RequiredValue(arguments, "value"), OptionalString(arguments, "revision"), cancellation), ToolPermission.Changes),
        Tool("api_keys_status", "Read the API keys of this PC's Martlet network from a data directory (api-keys.json, docs/API.md): for " +
            "each key its ID, name, scopes, who made it and when, expiry and whether it is revoked or expired. Read-only; contacts " +
            "nothing and never returns a key or its verifier.", new
        {
            dataDirectory = new { type = "string" }
        },
            arguments => ApiKeysStatus(arguments)),
        Tool("openai_endpoint_status", "Read Share › Share your language models (the OpenAI-compatible endpoint, docs/OPENAI_ENDPOINT.md) " +
            "from a data directory: the address people use (openai-endpoint.json, shared), the front door (the host and port it is " +
            "open on, where it listens, the upstream a proxy sends to, and the last Check address result), each key with the openai " +
            "scope (ID, name, priority, who made it, expiry, revoked or expired; api-keys.json), the models each host offered when the " +
            "Share page last read them and the models of hosts friends share with this PC (openai-models.json), and the texts Show " +
            "setup gives (the ? help, Tailscale Funnel, Nginx Proxy Manager, Nginx, Caddy, Cloudflare Tunnel). Read-only; contacts " +
            "nothing and never returns a key or its verifier.", new
        {
            dataDirectory = new { type = "string" }
        },
            arguments => OpenAiEndpointStatus(arguments)),
        Tool("settings_sync_status", "Read one Martlet on every computer (the settings shared through the paired hosts) from a data " +
            "directory's shared-settings.json: whether sync is on, each shared setting (thinking, listening, speaking, thinking-fallback, " +
            "companion, replies, prompts, memory, lorebooks, character, character-actions, talk, speech-display, appearance, " +
            "voice-recognition, voice-id, smart-home, updates) with its scope (household or account), which computer changed it and when, " +
            "its revision, whether it uses an " +
            "API key (never the key or its digest), the provider and model of each job's route, the values of non-personal settings, " +
            "and whether this PC still has the same value (\"same\", \"different\", \"adopting\" while the files don't hold the signed-in " +
            "account's value yet, or \"unknown\" for its own files). With accounts it also reads accounts\\working-copy.json (whose " +
            "settings the files hold, and since when) and that account's own copy in accounts\\<id>\\shared-settings.json with the same " +
            "fields, and lists the account folders on this PC (IDs only). Read-only; contacts nothing and reads no credentials.", new
        {
            dataDirectory = new { type = "string" }
        },
            arguments => SettingsSyncStatus(arguments)),
        Tool("settings_sync_selftest", "Rehearse one Martlet on every computer end to end with the production code: two real gateways on " +
            "127.0.0.1 (pinned TLS, signed requests, in-memory shared-settings.json) and three simulated desktops with real settings.json, " +
            "lorebooks.json and shared-settings.json in a temporary folder, an in-memory stand-in for Windows Credential Manager, the " +
            "desktop's paired client, sync engine and settings sections. Walks the owner's case (a PC on OpenRouter becomes a host, the " +
            "other PC still on NVIDIA Build becomes the companion and takes OpenRouter, its model and key), model and key changes, offline " +
            "edits on both sides (different and the same setting; the later edit wins), a host that missed a change, a stale copy, a " +
            "newer Martlet's setting, a Parakeet model a new computer lacks, a new computer, the Thinking fallback and its key, lorebooks, " +
            "no keys in desktop files, an unsigned request refused, and the shared files (keeping voice clips, touch zones, VRoid Hub credits, " +
            "Discord with its bot token, Telegram with its token) going from one desktop to another while running each bot stays on the first. A simulated Linux companion (Martlet.Companion's settings, guardrails, paired hosts and sync) takes a desktop's Thinking route, key and personality, keeps its push-to-talk key and character file, shares a persona edit back to the desktop and keeps its own listening, with the reason, when Parakeet is shared. Loopback only; the folder is deleted and the vault untouched.", new { },
            async (_, cancellation) => await NodeLinkCheckAsync(cancellation, "settings")),
        Tool("prompts_status", "Read Companion > Prompts from a data directory's settings.json: every internal prompt Martlet sends " +
            "to the Thinking model (id, group, title, placeholders) and whether it uses the built-in text, is edited or is emptied " +
            "(sent as nothing), with its character count and estimated tokens (Martlet's own request-size estimate, about a token " +
            "per three UTF-8 bytes), plus the tokens of all prompts together. With id, also returns that one prompt's effective text " +
            "(the saved edit or the built-in text) exactly as Martlet uses it. shortFirstSentence: Companion > Replies > Short " +
            "first sentence (on by default) and what closes a spoken and an unspoken reply's instructions with it, exactly as the " +
            "desktop sends them. adultContent: Companion > Replies > Adult content (off by default), its prompt's state and, while " +
            "on, the instructions it adds to every reply and remark. personalContent: Companion > Replies > Personal content " +
            "(Private by default), the prompt that fills {private} in the screen and camera prompts with its text, and whether " +
            "screen looks see password managers and private windows. Read-only.", new
        {
            dataDirectory = new { type = "string" },
            id = new { type = "string", maxLength = 64 }
        },
            async (arguments, cancellation) => await PromptsStatusAsync(arguments, cancellation)),
        Tool("chattiness_status", "Companion > Vision > How often it comments (the same choice as Listening > Watch along) as saved " +
            "in a data directory's talk-preferences.json: the choice (Quiet, Normal, Chatty or Martlet decides; Normal by default), " +
            "whether vision (on by default) and hearing the PC are on (replies are told about Martlet decides only while one is), " +
            "what vision looks at (the whole screen by default), the level Martlet " +
            "decides starts at, the tags a reply switches the level with, what Martlet decides tells the Thinking model and the " +
            "note that says the level (Companion > Prompts, from settings.json's edits), then a rehearsal: sample replies (or reply) " +
            "through the production speech segmenter and chat stripper with those tags offered, returning what is spoken and shown, " +
            "whether it stays silent, the tags found and the level they switch to. The level a running conversation picked shows in " +
            "the talk window's LiveChattiness line and the desktop log. Reads no credentials and contacts nothing.", new
        {
            dataDirectory = new { type = "string" },
            reply = new { type = "string", maxLength = 1024 }
        },
            async (arguments, cancellation) => await ChattinessCheck.RunAsync(DataDirectory(arguments), OptionalString(arguments, "reply"), cancellation)),
        Tool("setup_run_status", "Applying the recommended setup to all your computers and the Configuring state (docs/CLUSTER.md), " +
            "from a data directory: every computer's published run (shared-settings.json, setup-run.<device>: who started it and when, " +
            "whether it is active, its summary, each computer's state Pending/Configuring/Done/Failed/NeedsAttention with its step " +
            "and step count, when it finished), who does each job in cluster.json (with failover) and Sharing work (work-sharing.json). " +
            "Machine IDs, role names and counts only. Read-only.", new
        {
            dataDirectory = new { type = "string" }
        },
            async (arguments, cancellation) => await SetupRunCheck.StatusAsync(DataDirectory(arguments), cancellation)),
        Tool("setup_run_check", "Rehearse applying a recommended setup with the production executor (SetupExecutor) on a fixture " +
            "recommendation against simulated computers (FIXTURE, NOT real hosts): the preflight (a role's terms and variant terms, " +
            "a graphics card by UUID, an NGC key the owner enters, a change that needs someone there, a computer without a host " +
            "service, the Thinking pool joining by itself, downloads), then the run: the host commands sent in order with their " +
            "arguments and the secret only to its role, terms recorded as accepted, the plan assignments with failover, Sharing work, " +
            "one cluster check, a failed step that doesn't stop the others, a change missing from the review skipped, and the run " +
            "record's states as every computer reads it from the shared settings. In-process; no network, model or credential.", new { },
            async (_, cancellation) => await SetupRunCheck.RunAsync(cancellation)),
        Tool("recommended_setup_status", "Home's Recommended setup without the desktop: builds the network recommender's request with " +
            "the desktop's own builder from a data directory (hosts.json, host-hardware.json, cluster.json, settings.json, " +
            "work-sharing.json, thinking-pool.json, speaking-engine.txt; every host counts as online, roles are the shared plan's " +
            "record) or, with fixture \"network\", from a built-in four-computer network, or with fixture \"offline\", from a small " +
            "companion PC whose two hosts haven't answered for 155 minutes, or with fixture \"served\", from a PC alone with an " +
            "RTX 5090 whose model apps serve qwen3-32b and llama3.3:70b, or with fixture \"hostmodels\", from a host with an RTX " +
            "4090 that keeps qwen2.5:14b downloaded while Prefer models your hosts already have is on, or with fixture \"better\", from a " +
            "host with an RTX 4090 that thinks with Gemma 4 E2B and made-up measurements, or with fixture \"bighost\", from a host with " +
            "six RTX 5090s whose Deep thinking gets a bigger model from the model catalog, or with fixture \"bigsplit\", the same host whose Deep thinking runs llama3.3:70b split across two cards (all NOT real computers), runs the production " +
            "recommender (NetworkRecommender) and lists the computers (kind, planned or left as they are, manageable), today's jobs " +
            "and Thinking pool, the chat models your model apps serve (servedModels: Use models your apps already run, on unless " +
            "recommended-setup.json turns it off), the chat models your hosts run or keep downloaded (hostModels: Prefer models " +
            "your hosts already have, off unless recommended-setup.json turns it on), the recommended changes (summary, why, benefit, downloads, someone needed at the computer), each " +
            "computer's recommended roles and load, and whether a companion PC in use would ask (declined setups in " +
            "recommended-setup.json count). Each run also says the recommendation preferences it planned with (preferences: reply " +
            "quality and its first-word target, online services and the planners' hosting preference, prefer models that hear you, " +
            "the host graphics card share, the two model choices, each companion PC's games answer and which computers keep their " +
            "card for games), read from recommendation-preferences.json or the fixture; the preferences argument plans with other " +
            "ones instead (each field optional, nothing is saved; locks [{ job, locked }] locks or unlocks thinking, listening or lip-sync). " +
            "It plans with the numbers measured on your computers (measured: each Thinking model's first word from model-speed.json and " +
            "its memory from model-memory.json, here and on paired hosts, and the estimates they replaced), and lists each job's lock " +
            "(locks: Kept, Locked, Unlocked or ChangedByHand, and the choice it remembers), each job's clearly better choice " +
            "(suggestions: smarter at the same speed or as smart with less memory; a locked job's is only shown, an unlocked job's is " +
            "in the changes), whether Home's Recommended setup button says A better setup is available (betterSetup; a review the owner " +
            "saw, kept in recommended-setup.json, stops it) and whether the Thinking or If Thinking fails model is retired on its server, " +
            "with the replacement Martlet proposes (retired). Read-only; reads no keys and contacts nothing, except that lookOnThisPc (with a data " +
            "directory) asks the model apps on this PC (127.0.0.1 only) which models they serve, as the desktop does. It plans as the " +
            "desktop does, with the model catalog (the data directory's daily copy, else the snapshot shipped with Martlet; a fixture " +
            "uses the snapshot), its local facts and the data directory's route facts (model-abilities.json), plus Martlet's own list; " +
            "catalog \"seed\" plans with Martlet's own list alone. planning says where each option came from (Seed: Martlet's own list, " +
            "Catalog: a hosted model the catalog chose, LocalFacts: an open-weight model sized from its local facts, Served: your model " +
            "app): each job's option, each Deep thinking role, the online Deep thinking suggestion (onlineDeepThinking), every option " +
            "the catalog gave and each provider's suggested models (providerModels), with smartness in words or LMArena's rating with its " +
            "credit, never a rank number.", new
        {
            dataDirectory = new { type = "string" },
            fixture = new { type = "string", @enum = new[] { "network", "offline", "served", "hostmodels", "better", "bighost", "bigsplit" } },
            catalog = new { type = "string", @enum = new[] { "models", "seed" } },
            lookOnThisPc = new { type = "boolean" },
            preferences = new
            {
                type = "object",
                properties = new
                {
                    quality = new { type = "string", @enum = new[] { "balanced", "quick", "smarter" } },
                    online = new { type = "string", @enum = new[] { "backup", "never", "yes" } },
                    preferHearing = new { type = "boolean" },
                    hostGpuShare = new { type = "integer", @enum = new[] { 90, 75, 50 } },
                    useServedModels = new { type = "boolean" },
                    preferHostModels = new { type = "boolean" },
                    games = new
                    {
                        type = "array",
                        items = new { type = "object", properties = new { device = new { type = "string" }, plays = new { type = "boolean" } } }
                    },
                    locks = new
                    {
                        type = "array",
                        items = new
                        {
                            type = "object",
                            properties = new { job = new { type = "string", @enum = new[] { "thinking", "listening", "lip-sync" } }, locked = new { type = "boolean" } }
                        }
                    }
                }
            }
        },
            async (arguments, cancellation) => OptionalString(arguments, "fixture") is { } setupFixture
                ? await RecommendedSetupStatus.RunAsync(null, setupFixture, cancellation, preferences: OptionalObject(arguments, "preferences"),
                    catalogChoice: OptionalString(arguments, "catalog"))
                : await RecommendedSetupStatus.RunAsync(DataDirectory(arguments), null, cancellation,
                    OptionalBool(arguments, "lookOnThisPc") == true, OptionalObject(arguments, "preferences"), OptionalString(arguments, "catalog")))
    ];

    /// <summary>The network's API keys as the desktop keeps them in a data directory (the file name matches Martlet.Desktop's
    /// MainWindow.ApiKeys). Names, scopes and stamps only: the verifier is never returned, and the key itself is kept nowhere.</summary>
    private static object ApiKeysStatus(JsonElement arguments)
    {
        var path = Path.Combine(DataDirectory(arguments), "api-keys.json");
        if (!File.Exists(path)) return new { state = "none" };
        Martlet.Core.Access.ApiKeyList list;
        try { list = Martlet.Core.Access.ApiKeyList.Parse(File.ReadAllBytes(path)); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or Martlet.Core.Contracts.ContractException)
        {
            return new { state = "unreadable", problem = error.Message };
        }
        var now = DateTimeOffset.UtcNow;
        return new
        {
            state = "loaded",
            live = list.Live(now).Count,
            revoked = list.Keys.Count(k => k.Revoked),
            expired = list.Keys.Count(k => !k.Revoked && k.Expired(now)),
            keys = list.Keys.Select(k => new
            {
                id = k.Id, name = k.Name, scopes = k.Scopes, createdBy = k.CreatedBy, createdAt = k.CreatedAt, expiresAt = k.ExpiresAt,
                revoked = k.Revoked, expired = !k.Revoked && k.Expired(now), updatedBy = k.UpdatedBy, hasVerifier = k.Verifier is not null
            })
        };
    }

    /// <summary>Share › Share your language models: the endpoint's shared settings, its keys (never a key or verifier), the
    /// models the Share page last read and the proxy configuration for the front door (the network roster gives its origin).</summary>
    private static object OpenAiEndpointStatus(JsonElement arguments)
    {
        var directory = DataDirectory(arguments);
        var (settings, state) = Martlet.Core.Access.OpenAiEndpointSettings.Read(directory);
        Martlet.Core.Access.ApiKeyList keys;
        try { keys = File.Exists(Path.Combine(directory, "api-keys.json")) ? Martlet.Core.Access.ApiKeyList.Parse(File.ReadAllBytes(Path.Combine(directory, "api-keys.json"))) : Martlet.Core.Access.ApiKeyList.Empty; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or Martlet.Core.Contracts.ContractException)
        {
            keys = Martlet.Core.Access.ApiKeyList.Empty;
        }
        JsonElement? models = null;
        try
        {
            var path = Path.Combine(directory, "openai-models.json");
            if (File.Exists(path)) models = JsonDocument.Parse(File.ReadAllBytes(path)).RootElement.Clone();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException) { }
        Martlet.Core.Network.NetworkRoster? roster = null;
        try
        {
            var path = Path.Combine(directory, Martlet.Avatar.Audio2Face.Remote.NetworkLocalState.FileName);
            if (File.Exists(path)) roster = Martlet.Avatar.Audio2Face.Remote.NetworkLocalState.Parse(File.ReadAllBytes(path)).Roster;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or Martlet.Core.Contracts.ContractException) { }
        var hosts = roster?.ActiveHosts.OrderBy(h => h.Id, StringComparer.Ordinal).ToArray() ?? [];
        // The open front door the Share page last saw (openai-models.json frontDoor); else the chosen host on the default port.
        JsonElement? open = models is { ValueKind: JsonValueKind.Object } read && read.TryGetProperty("frontDoor", out var seen) &&
            seen.ValueKind == JsonValueKind.Object ? seen : null;
        var chosen = hosts.FirstOrDefault(h => h.Id == (open?.GetProperty("hostId").GetString() ?? settings.FrontDoorHostId)) ?? hosts.FirstOrDefault();
        var port = open?.GetProperty("port").GetInt32() ?? Martlet.Core.Access.OpenAiEndpoint.DefaultFrontDoorPort;
        var upstreamHost = chosen is not null && Uri.TryCreate(chosen.Origin, UriKind.Absolute, out var home) ? home.Host : "<front door address>";
        var upstream = $"https://{upstreamHost}:{port}";
        var serverName = settings.PublicAddress is { } address && Uri.TryCreate(address, UriKind.Absolute, out var uri) ? uri.Host : "martlet.example.com";
        var now = DateTimeOffset.UtcNow;
        return new
        {
            settings = new { state, publicAddress = settings.PublicAddress, frontDoorHostId = settings.FrontDoorHostId },
            frontDoor = new
            {
                open = open is not null, hostId = chosen?.Id, port, upstream,
                listening = open?.GetProperty("listening"),
                lastAddressCheck = models is { ValueKind: JsonValueKind.Object } m && m.TryGetProperty("addressCheck", out var check) &&
                    check.ValueKind == JsonValueKind.String ? check.GetString() : null
            },
            paths = Martlet.Core.Access.OpenAiEndpoint.RelayPaths.Prepend(Martlet.Core.Access.OpenAiEndpoint.ModelsPath),
            keys = keys.Keys.Where(k => k.Allows(Martlet.Core.Access.ApiKeyScopes.OpenAi)).Select(k => new
            {
                id = k.Id, name = k.Name, scopes = k.Scopes,
                priority = k.Allows(Martlet.Core.Access.ApiKeyScopes.EqualPriority) ? "equal-to-background-work" : "your-martlet-first",
                createdBy = k.CreatedBy, createdAt = k.CreatedAt, expiresAt = k.ExpiresAt, revoked = k.Revoked,
                expired = !k.Revoked && k.Expired(now), updatedBy = k.UpdatedBy
            }),
            models,
            setup = new
            {
                help = Martlet.Core.Access.OpenAiEndpoint.ExposeHelp,
                tailscale = Martlet.Core.Access.OpenAiEndpoint.TailscaleCommand(port),
                proxyManager = Martlet.Core.Access.OpenAiEndpoint.ProxyManagerSteps(serverName, upstreamHost, port),
                nginx = Martlet.Core.Access.OpenAiEndpoint.NginxConfig(serverName, upstream),
                caddy = Martlet.Core.Access.OpenAiEndpoint.CaddyConfig(serverName, upstream),
                cloudflareTunnel = Martlet.Core.Access.OpenAiEndpoint.CloudflareTunnelConfig(serverName, upstream)
            }
        };
    }

    /// <summary>The settings this PC shares with the owner's other computers (shared-settings.json, the name
    /// Martlet.Core.Sync.SharedSettingsState uses). Personal text (personality, prompts, lorebooks) and keys are never returned:
    /// only who changed each setting and when, its size, whether it uses a key, and for jobs the provider and model.</summary>
    private static object SettingsSyncStatus(JsonElement arguments)
    {
        var directory = DataDirectory(arguments);
        var sync = Martlet.Core.Cluster.ClusterSyncChoice.Describe(directory);
        // Each account's settings (docs/ACCOUNTS.md): the account whose settings the data folder's files hold, its own copy, and
        // the accounts that have a folder here (IDs only).
        var marker = Martlet.Core.Sync.AccountWorkingCopy.Load(directory);
        string[] folders;
        try
        {
            var accountsDirectory = Path.Combine(directory, Martlet.Core.Sync.AccountWorkingCopy.DirectoryName);
            folders = Directory.Exists(accountsDirectory)
                ? [.. Directory.GetDirectories(accountsDirectory).Select(Path.GetFileName).OfType<string>()
                    .Where(n => n.Length == 32 && n.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f')).Order(StringComparer.Ordinal)]
                : [];
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { folders = []; }
        object? Account()
        {
            if (marker is not { } m) return null;
            var folder = Martlet.Core.Sync.AccountWorkingCopy.Folder(directory, m.Account);
            if (!File.Exists(Path.Combine(folder, Martlet.Core.Sync.SharedSettingsState.FileName)))
                return new { id = m.Account.ToString("N"), filesSince = m.Since, state = "none" };
            var (own, seen) = Martlet.Core.Sync.SharedSettingsState.Load(folder);
            return new
            {
                id = m.Account.ToString("N"), filesSince = m.Since, state = "loaded", revision = own.Revision, count = own.Settings.Count,
                settings = Describe(own, seen)
            };
        }
        object Accounts() => new
        {
            filesHold = marker?.Account.ToString("N"),
            folders = folders.Select(id => new
            {
                id, settingsCopy = File.Exists(Path.Combine(directory, Martlet.Core.Sync.AccountWorkingCopy.DirectoryName, id,
                    Martlet.Core.Sync.SharedSettingsState.FileName))
            }).ToArray()
        };
        if (!File.Exists(Path.Combine(directory, Martlet.Core.Sync.SharedSettingsState.FileName)))
            return new { sync, state = "none", account = Account(), accounts = Accounts() };
        var (document, observed) = Martlet.Core.Sync.SharedSettingsState.Load(directory);
        object? Route(Martlet.Core.Sync.SharedSetting setting)
        {
            try
            {
                if (setting.Key is "thinking" or "listening" or "speaking")
                {
                    var route = Martlet.Core.Sync.SharedRoute.Parse(setting.Value);
                    return new { type = route.Type, origin = route.Origin, model = route.Model, voice = route.Voice };
                }
                if (setting.Key == "thinking-fallback" && setting.Value != "null")
                {
                    using var parsed = JsonDocument.Parse(setting.Value);
                    return new { origin = parsed.RootElement.GetProperty("origin").GetString(), model = parsed.RootElement.GetProperty("model").GetString() };
                }
                if (setting.Key is "memory" or "appearance" or "appearance-custom" or "talk" or "speech-display" or "voice-recognition" or "smart-home" or "updates" or
                    "model-abilities" or "work-sharing" or "conversation-history" or Martlet.Core.Motion.MotionSettings.SharedKey || Martlet.Core.Sync.SharedSettings.IsDeviceKey(setting.Key))
                {
                    using var parsed = JsonDocument.Parse(setting.Value);
                    return parsed.RootElement.Clone();
                }
            }
            catch (Exception error) when (error is JsonException or Martlet.Core.Contracts.ContractException or KeyNotFoundException or InvalidOperationException) { }
            return null;
        }
        object[] Describe(Martlet.Core.Sync.SharedSettings copy, IReadOnlyDictionary<string, string> seenHere) => [.. copy.Settings.Select(s => new
        {
            key = s.Key, scope = Martlet.Core.Sync.SettingScopes.Of(s.Key), updatedBy = s.UpdatedBy, updatedAt = s.UpdatedAt, revision = s.Revision,
            usesKey = s.SecretSha256 is not null, characters = s.Value.Length, off = s.Value == "null",
            here = !seenHere.TryGetValue(s.Key, out var seen) ? "unknown"
                : seen == Martlet.Core.Sync.SharedSettingsNode.AdoptingDigest ? "adopting"
                : seen == Martlet.Core.Sync.SharedSettings.ContentDigest(s.Value, s.SecretSha256) ? "same" : "different",
            value = Route(s)
        })];
        return new
        {
            sync, state = "loaded", revision = document.Revision, count = document.Settings.Count,
            settings = Describe(document, observed),
            account = Account(),
            accounts = Accounts()
        };
    }

    /// <summary>Companion › Prompts as saved in a data directory's settings.json: each prompt's state, and one prompt's
    /// effective text on request.</summary>
    private static async Task<object> PromptsStatusAsync(JsonElement arguments, CancellationToken cancellation)
    {
        var id = OptionalString(arguments, "id");
        if (id is not null && Martlet.Core.Settings.PromptCatalog.Find(id) is null) throw new ArgumentException($"Unknown prompt '{id}'.");
        var loaded = await new Martlet.Core.Settings.SettingsStore(DataDirectory(arguments)).LoadAsync(cancellation);
        var prompts = loaded.Settings?.Prompts;
        var generation = loaded.Settings?.Generation;
        var state = loaded.State switch
        {
            Martlet.Core.Settings.SettingsLoadState.Loaded => "loaded",
            Martlet.Core.Settings.SettingsLoadState.FirstRun => "none",
            _ => "unreadable"
        };
        string Of(string prompt) => prompts?.Overrides.TryGetValue(prompt, out var text) != true ? "builtin"
            : string.IsNullOrWhiteSpace(text) ? "empty" : "edited";
        var list = Martlet.Core.Settings.PromptCatalog.All.Select(p =>
        {
            var text = Martlet.Core.Settings.PromptSettings.Text(prompts, p.Id);
            return new
            {
                id = p.Id, group = p.Group, title = p.Title, placeholders = p.Placeholders, state = Of(p.Id),
                characters = text.Length,
                tokens = string.IsNullOrWhiteSpace(text) ? 0 : Martlet.Providers.BoundedTextInput.TextTokens(text)
            };
        }).ToArray();
        return new
        {
            state, problem = loaded.Error?.Summary, total = list.Length,
            edited = list.Count(p => p.state == "edited"), emptied = list.Count(p => p.state == "empty"),
            tokens = list.Sum(p => p.tokens), prompts = list,
            prompt = id is null ? null : new { id, state = Of(id), text = Martlet.Core.Settings.PromptSettings.Text(prompts, id) },
            // Companion › Replies › Short first sentence (on by default) and what closes a reply's instructions with it, exactly
            // as the desktop sends it (the production PromptSettings.ReplyClosing): for a spoken reply the short first sentence
            // prompt, then reply length; for a reply that isn't spoken, reply length alone. The same every time, so caches keep it.
            shortFirstSentence = new
            {
                on = Martlet.Core.Settings.GenerationSettings.StartsShort(generation),
                chosen = generation?.ShortFirstSentence is not null,
                prompt = Of(Martlet.Core.Settings.PromptCatalog.ShortFirstSentence),
                spokenClosing = Martlet.Core.Settings.PromptSettings.ReplyClosing(prompts, generation, true, Martlet.Conversation.StayQuiet.Marker),
                unspokenClosing = Martlet.Core.Settings.PromptSettings.ReplyClosing(prompts, generation, false, Martlet.Conversation.StayQuiet.Marker)
            },
            // Companion › Replies › Adult content (off by default) and the instructions it adds after the One moment prompt of
            // every reply and remark (never in a Discord call), exactly as the desktop sends them; null while it is off.
            adultContent = new
            {
                on = Martlet.Core.Settings.GenerationSettings.Adult(generation),
                prompt = Of(Martlet.Core.Settings.PromptCatalog.AdultContent),
                instructions = Martlet.Core.Settings.GenerationSettings.Adult(generation)
                    ? Martlet.Core.Settings.PromptSettings.Fill(prompts, Martlet.Core.Settings.PromptCatalog.AdultContent) : null
            },
            // Companion › Replies › Personal content (Private by default): the level, the prompt that fills {private} and its
            // text, and whether screen looks see password managers and private windows, exactly as the desktop uses them.
            personalContent = new
            {
                level = Martlet.Core.Settings.GenerationSettings.Personal(generation).ToString(),
                prompt = Martlet.Core.Settings.GenerationSettings.Personal(generation) == Martlet.Core.Settings.PersonalContent.Private
                    ? Martlet.Core.Settings.PromptCatalog.PersonalPrivate : Martlet.Core.Settings.PromptCatalog.PersonalShared,
                instructions = Martlet.Core.Settings.PromptSettings.PersonalText(
                    Martlet.Core.Settings.PromptSettings.ForPersonal(prompts, generation)),
                seesPrivateWindows = Martlet.Core.Settings.GenerationSettings.Personal(generation) == Martlet.Core.Settings.PersonalContent.Everything
            }
        };
    }
}
