using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Martlet.Avatar.Audio2Face.Remote;
using Martlet.Core.Accounts;
using Martlet.Core.Contracts;
using Martlet.Core.Lorebooks;
using Martlet.Core.Network;
using Martlet.Core.Settings;
using Martlet.Core.Sync;
using Martlet.Gateway;

namespace Martlet.NodeLinkCheck;

/// <summary>
/// Rehearses one Martlet on every computer (shared settings, docs/CLUSTER.md) end to end on this PC with the production code:
/// two real gateways (Kestrel, pinned TLS) on 127.0.0.1 with an in-memory shared-settings.json, and three simulated desktops,
/// each with a real settings.json, lorebooks.json and shared-settings.json in a temporary folder, an in-memory stand-in for
/// Windows Credential Manager, the desktop's paired client and the real sync engine and settings sections. It walks through
/// the owner's story (a PC that switched to OpenRouter becomes a host, the other PC becomes the companion and must take
/// OpenRouter, its model and its key), changes made on each computer, offline edits on both sides, a host that missed a
/// change, a stale copy, a setting from a newer Martlet, one a computer can't use yet and a new computer. Nothing leaves
/// loopback; the folder is deleted afterwards and the real credential vault is never touched.
/// </summary>
internal static class SettingsRehearsal
{
    private const string OpenRouter = ChatCompletionsEndpointCatalog.OpenRouterBaseUrl;
    private const string NvidiaBuild = ChatCompletionsEndpointCatalog.NvidiaBuildBaseUrl;
    private const string OpenRouterKey = "sk-or-v1-lab-desktop-a-0123456789abcdef";
    private const string RotatedKey = "sk-or-v1-lab-desktop-a-rotated-fedcba987654";
    private const string NvidiaKey = "nvapi-lab-desktop-b-0123456789";
    private const string FallbackKey = "sk-lab-fallback-key-0123456789";
    private const string PoolKey = "nvapi-lab-thinking-pool-key-0123456789";
    private const string Parakeet = LocalSpeechSetup.ParakeetV2EnglishModelId;
    private static readonly Guid SamAccount = Guid.Parse("5a3f0c9e-8b7d-4e21-a6c3-b2f1d0e9a8b7");
    private static readonly Guid AlexAccount = Guid.Parse("0b1c2d3e-4f50-6172-8394-a5b6c7d8e9f0");

    // Text as it appears inside a shared setting's JSON value.
    private static string Json(string text) => System.Text.Json.JsonSerializer.Serialize(text)[1..^1];

    internal static async Task<(bool Ok, object Report)> RunAsync(CancellationToken token)
    {
        var steps = new List<(string Name, bool Ok, string Detail)>();
        var started = DateTimeOffset.UtcNow;
        var root = Path.Combine(Path.GetTempPath(), "martlet-settings-rehearsal-" + Guid.NewGuid().ToString("N"));
        // The household's account directory on the hosts: a host serves an account's settings only to a device where that account
        // is signed in (GatewayMemorySpaceAccess.cs). Sam, the owner, and Alex are both signed in on lab-desktop-d.
        using (var founder = NetworkKey.Create("lab-desktop-d"))
        {
            var login = AccountLoginKey.ForWindows("lab-desktop-d", "S-1-5-21-1-2-3-1001");
            LabHost.Accounts = AccountDirectory.Empty
                .Put(founder, Account.Create("Sam", AccountRoles.Owner, SamAccount).WithDevice(AccountDevice.For("lab-desktop-d", login, started)), started)
                .Put(founder, Account.Create("Alex", AccountRoles.Member, AlexAccount).WithDevice(AccountDevice.For("lab-desktop-d", login, started)), started)
                .Write();
        }
        await using var h1 = await LabHost.StartAsync("lab-settings-1");
        await using var h2 = await LabHost.StartAsync("lab-settings-2");
        LabHost[] hosts = [h1, h2];
        var a = new LabDesktop("lab-desktop-a", Path.Combine(root, "a"));
        var b = new LabDesktop("lab-desktop-b", Path.Combine(root, "b"));
        var c = new LabDesktop("lab-desktop-c", Path.Combine(root, "c"));
        SharedSettings? early = null;

        async Task Run(string name, Func<Task<(bool Ok, string Detail)>> action)
        {
            try
            {
                var (ok, detail) = await action();
                steps.Add((name, ok, detail));
            }
            catch (Exception error) when (error is not OperationCanceledException || !token.IsCancellationRequested)
            {
                steps.Add((name, false, $"{error.GetType().Name}: {error.Message}"));
            }
        }

        try
        {
            await Run("Desktops A, B and C pair with lab-settings-1 and lab-settings-2 (signed, pinned connections)", async () =>
            {
                foreach (var desktop in new[] { a, b, c })
                foreach (var host in hosts)
                    await desktop.PairAsync(host);
                return (true, "each desktop paired with both hosts");
            });

            await Run("Before the update: B chose NVIDIA Build with its key on Sept 1; A chose OpenRouter with its key on Sept 20 and gave the personality its own words", async () =>
            {
                await b.ChooseAsync(SetupRole.Llm, s => ChatCompletionsSetup.SelectRoute(s, NvidiaBuild, ChatCompletionsEndpointCatalog.NvidiaBuildDefaultModelId),
                    NvidiaKey, new DateTimeOffset(2026, 9, 1, 12, 0, 0, TimeSpan.Zero));
                await a.ChooseAsync(SetupRole.Llm, s => ChatCompletionsSetup.SelectRoute(s, OpenRouter, "google/gemma-4-26b-a4b-it"),
                    OpenRouterKey, new DateTimeOffset(2026, 9, 20, 12, 0, 0, TimeSpan.Zero));
                await a.EditAsync(s =>
                {
                    var persona = s.Companion!.ActivePersona;
                    return s with { Companion = s.Companion.Update(persona.Id, "Martlet", "Martlet is cheerful and remembers the owner's projects.") };
                });
                var bRoute = (await b.SettingsAsync()).ThinkingRoute()!;
                return (bRoute.Origin == NvidiaBuild && b.Key(await b.SettingsAsync(), SetupRole.Llm) == NvidiaKey,
                    "B: NVIDIA Build; A: OpenRouter, google/gemma-4-26b-a4b-it, own personality text");
            });

            await Run("After the update B (now the companion) syncs first: it shares NVIDIA Build stamped with when its key was saved, not now", async () =>
            {
                var result = await b.SyncAsync(hosts, token);
                var entry = result.Document.Find(AppSettingsSections.Thinking);
                var h1Copy = await b.ReadAsync(h1, token);
                return (entry is { UpdatedBy: "lab-desktop-b" } && DateTimeOffset.FromUnixTimeMilliseconds(entry.Revision).Date == new DateTime(2026, 9, 1) &&
                        h1Copy.Find(AppSettingsSections.Thinking)?.Revision == entry.Revision,
                    $"recorded {string.Join(", ", result.Recorded)}; thinking stamped {DateTimeOffset.FromUnixTimeMilliseconds(entry?.Revision ?? 0):u}; both hosts hold it");
            });

            await Run("A (now the host PC) syncs: its OpenRouter key was saved later, so OpenRouter wins; its personality is shared too", async () =>
            {
                var result = await a.SyncAsync(hosts, token);
                var thinking = result.Document.Find(AppSettingsSections.Thinking);
                var applied = string.Join(", ", result.Applied.Select(x => x.Key));
                var route = (await a.SettingsAsync()).ThinkingRoute()!;
                return (thinking is { UpdatedBy: "lab-desktop-a" } && route.Origin == OpenRouter && result.Recorded.Contains(AppSettingsSections.Companion) &&
                        !result.Applied.Any(x => x.Key == AppSettingsSections.Thinking),
                    $"recorded {string.Join(", ", result.Recorded)}; A kept OpenRouter (took: {(applied.Length == 0 ? "nothing" : applied)})");
            });

            await Run("B follows: OpenRouter, the same model and A's key in its own credential store, the choice recorded as made; its NVIDIA key is set aside, not deleted", async () =>
            {
                var oldKey = (await b.SettingsAsync()).ThinkingRoute()!.CredentialId;
                var result = await b.SyncAsync(hosts, token);
                var settings = await b.SettingsAsync();
                var route = settings.ThinkingRoute()!;
                var persona = settings.Companion!.ActivePersona;
                var aPersona = (await a.SettingsAsync()).Companion!.ActivePersona;
                var pending = settings.Setup!.PendingRemovals.Any(p => p.CredentialId == oldKey);
                return (route is { RouteType: SetupRouteType.ChatCompletions, Origin: OpenRouter, ModelId: "google/gemma-4-26b-a4b-it" } &&
                        route.Consent == route.Selection() && b.Key(settings, SetupRole.Llm) == OpenRouterKey && pending && b.Vault.Has(NvidiaKey) &&
                        persona.Id == aPersona.Id && persona.Text == aPersona.Text,
                    $"took {string.Join(", ", result.Applied.Select(x => $"{x.Key} from {x.By}"))}; route {route.Origin} {route.ModelId}; key matches A's: " +
                    $"{b.Key(settings, SetupRole.Llm) == OpenRouterKey}; NVIDIA key set aside: {pending}; personality matches: {persona.Text == aPersona.Text}");
            });
            early = (await a.ReadAsync(h1, token)).WithoutSecrets();

            await Run("The same case in the other order on another host (lab-settings-3): the OpenRouter PC syncs first, then the NVIDIA Build PC; the result is the same", async () =>
            {
                await using var h3 = await LabHost.StartAsync("lab-settings-3");
                var d = new LabDesktop("lab-desktop-d", Path.Combine(root, "d"));
                var e = new LabDesktop("lab-desktop-e", Path.Combine(root, "e"));
                await d.PairAsync(h3);
                await e.PairAsync(h3);
                await d.ChooseAsync(SetupRole.Llm, s => ChatCompletionsSetup.SelectRoute(s, NvidiaBuild, ChatCompletionsEndpointCatalog.NvidiaBuildDefaultModelId),
                    NvidiaKey, new DateTimeOffset(2026, 9, 1, 12, 0, 0, TimeSpan.Zero));
                await e.ChooseAsync(SetupRole.Llm, s => ChatCompletionsSetup.SelectRoute(s, OpenRouter, "google/gemma-4-26b-a4b-it"),
                    OpenRouterKey, new DateTimeOffset(2026, 9, 20, 12, 0, 0, TimeSpan.Zero));
                await e.SyncAsync([h3], token);
                var first = await d.SyncAsync([h3], token);
                var again = await e.SyncAsync([h3], token);
                var settings = await d.SettingsAsync();
                var route = settings.ThinkingRoute()!;
                return (route.Origin == OpenRouter && d.Key(settings, SetupRole.Llm) == OpenRouterKey && !first.Recorded.Contains(AppSettingsSections.Thinking) &&
                        again.Applied.Count == 0,
                    $"the NVIDIA Build PC took {route.Origin} with the OpenRouter key and shared nothing over it; the OpenRouter PC took nothing back");
            });

            await Run("A changes only the model: B follows and reuses the key it already has (no new credential written)", async () =>
            {
                await a.ChooseAsync(SetupRole.Llm, s => ChatCompletionsSetup.SelectRoute(s, OpenRouter, "anthropic/claude-sonnet-5"), null);
                await a.SyncAsync(hosts, token);
                var writes = b.Vault.Writes;
                await b.SyncAsync(hosts, token);
                var route = (await b.SettingsAsync()).ThinkingRoute()!;
                return (route.ModelId == "anthropic/claude-sonnet-5" && b.Vault.Writes == writes && b.Key(await b.SettingsAsync(), SetupRole.Llm) == OpenRouterKey,
                    $"B uses {route.ModelId}; credentials written on B: {b.Vault.Writes - writes}");
            });

            await Run("A pastes a new OpenRouter key: B takes the new key and removes the old one it only had from A; its own NVIDIA key stays set aside", async () =>
            {
                await a.ChooseAsync(SetupRole.Llm, s => ChatCompletionsSetup.SelectRoute(s, OpenRouter, "anthropic/claude-sonnet-5"), RotatedKey);
                await a.SyncAsync(hosts, token);
                await b.SyncAsync(hosts, token);
                var settings = await b.SettingsAsync();
                var setAside = settings.Setup!.PendingRemovals.Count;
                return (b.Key(settings, SetupRole.Llm) == RotatedKey && !b.Vault.Has(OpenRouterKey) && b.Vault.Has(NvidiaKey) && setAside == 1,
                    $"B's key is the new one: {b.Key(settings, SetupRole.Llm) == RotatedKey}; old shared key kept: {b.Vault.Has(OpenRouterKey)}; " +
                    $"own NVIDIA key kept: {b.Vault.Has(NvidiaKey)}; keys set aside on B: {setAside}");
            });

            await Run("Offline on both: A changes reply settings while B edits a prompt; once both sync, each computer has both changes", async () =>
            {
                await a.EditAsync(s => s with { Generation = new GenerationSettings { Temperature = 0.9 } });
                await a.SyncAsync([], token);
                await b.EditAsync(s => s with { Prompts = PromptSettings.Normalize(new Dictionary<string, string> { [PromptCatalog.All[0].Id] = "Keep it short and kind." }) });
                await b.SyncAsync([], token);
                await a.SyncAsync(hosts, token);
                await b.SyncAsync(hosts, token);
                await a.SyncAsync(hosts, token);
                var sa = await a.SettingsAsync();
                var sb = await b.SettingsAsync();
                return (sa.Generation?.Temperature == 0.9 && sb.Generation?.Temperature == 0.9 &&
                        sa.Prompts?.Overrides.Count == 1 && sb.Prompts?.Overrides.Count == 1,
                    $"A: temperature {sa.Generation?.Temperature}, {sa.Prompts?.Overrides.Count ?? 0} prompt edit; B: temperature {sb.Generation?.Temperature}, {sb.Prompts?.Overrides.Count ?? 0} prompt edit");
            });

            await Run("Offline on both, the same setting: A edits the personality first, B five minutes later; whichever syncs first, B's later edit wins everywhere", async () =>
            {
                var at = DateTimeOffset.UtcNow.AddMinutes(1);
                await a.EditPersonaAsync("Martlet keeps answers short.");
                await a.SyncAsync([], token, at);
                await b.EditPersonaAsync("Martlet is playful and curious.");
                await b.SyncAsync([], token, at.AddMinutes(5));
                await a.SyncAsync(hosts, token, at.AddMinutes(6));
                await b.SyncAsync(hosts, token, at.AddMinutes(6));
                await a.SyncAsync(hosts, token, at.AddMinutes(7));
                var ta = (await a.SettingsAsync()).Companion!.ActivePersona.Text;
                var tb = (await b.SettingsAsync()).Companion!.ActivePersona.Text;
                return (ta == "Martlet is playful and curious." && tb == ta, $"A: \"{ta}\"; B: \"{tb}\"");
            });

            await Run("A change a host missed reaches it later: lab-settings-1 is down while A turns memory off, then restarts with its saved copy and gets the change on A's next sync", async () =>
            {
                await h1.StopAsync();
                await a.EditAsync(s => s with { Memory = s.Memory!.Configure(false, s.Memory.StoragePolicy, s.Memory.CustomDirectory) });
                await a.SyncAsync(hosts, token);
                var h2Has = (await a.ReadAsync(h2, token)).Find(AppSettingsSections.Memory)?.Value;
                await h1.StartAsync();
                foreach (var desktop in new[] { a, b, c }) await desktop.PairAsync(h1);
                var restarted = await a.ReadAsync(h1, token);
                var before = restarted.Find(AppSettingsSections.Memory)?.Value;
                await a.SyncAsync(hosts, token);
                var after = (await a.ReadAsync(h1, token)).Find(AppSettingsSections.Memory)?.Value;
                await b.SyncAsync(hosts, token);
                var bMemory = (await b.SettingsAsync()).Memory!.Enabled;
                return (h2Has == "{\"enabled\":false}" && restarted.Settings.Count > 0 && before != after && after == "{\"enabled\":false}" && !bMemory,
                    $"while down: lab-settings-2 has memory {h2Has}; after restart lab-settings-1 served {restarted.Settings.Count} saved settings " +
                    $"(memory {before ?? "not yet"}), then {after}; B's memory on: {bMemory}");
            });

            await Run("A stale copy can't undo newer changes: the copy from before the model change is merged into lab-settings-2", async () =>
            {
                var merged = await a.MergeAsync(h2, early!, token);
                var thinking = SharedRoute.Parse(merged.Find(AppSettingsSections.Thinking)!.Value);
                return (thinking.Model == "anthropic/claude-sonnet-5", $"lab-settings-2 still says {thinking.Model}");
            });

            await Run("A setting from a newer Martlet passes through hosts and this version's desktops untouched", async () =>
            {
                var future = (await a.ReadAsync(h2, token)).Put("future-feature", "{\"x\":1}", null, "lab-desktop-z", DateTimeOffset.UtcNow);
                await a.MergeAsync(h2, future, token);
                await b.SyncAsync(hosts, token);
                var kept = (await b.ReadAsync(h1, token)).Find("future-feature");
                return (kept?.Value == "{\"x\":1}" && b.Node.Document.Find("future-feature") is not null,
                    kept is null ? "lost" : "kept by B and passed on to lab-settings-1");
            });

            await Run("A Parakeet model for Listening: B uses it; new computer C doesn't have it, waits without recording a change, then follows once it is downloaded", async () =>
            {
                await a.ChooseAsync(SetupRole.Stt, s => LocalSpeechSetup.SelectParakeet(s, Parakeet), null);
                await a.SyncAsync(hosts, token);
                await b.SyncAsync(hosts, token);
                var bModel = (await b.SettingsAsync()).Setup!.Routes.SingleOrDefault(r => r.Role == SetupRole.Stt)?.ModelId;
                c.Available = (_, route, _) => Task.FromResult(route.Type == SharedRoute.Parakeet ? "The Parakeet model isn't downloaded on this PC." : null);
                var first = await c.SyncAsync(hosts, token);
                var waiting = first.Waiting.GetValueOrDefault(AppSettingsSections.Listening);
                var second = await c.SyncAsync(hosts, token);
                var listeningBy = second.Document.Find(AppSettingsSections.Listening)?.UpdatedBy;
                c.Available = (_, _, _) => Task.FromResult<string?>(null);
                var third = await c.SyncAsync(hosts, token);
                var cModel = (await c.SettingsAsync()).Setup!.Routes.SingleOrDefault(r => r.Role == SetupRole.Stt)?.ModelId;
                return (bModel == Parakeet && waiting is not null && !second.Recorded.Contains(AppSettingsSections.Listening) && listeningBy == "lab-desktop-a" &&
                        third.Applied.Any(x => x.Key == AppSettingsSections.Listening) && cModel == Parakeet,
                    $"B: {bModel}; C waited (\"{waiting}\"), recorded nothing for it, then took {cModel}");
            });

            await Run("New computer C took everything else on its first sync: OpenRouter with the key, the personality, replies, prompts and memory off; its defaults overrode nothing", async () =>
            {
                var settings = await c.SettingsAsync();
                var route = settings.ThinkingRoute();
                var aDoc = await a.ReadAsync(h1, token);
                var byC = aDoc.Settings.Where(s => s.UpdatedBy == "lab-desktop-c").Select(s => s.Key).ToArray();
                return (route?.Origin == OpenRouter && c.Key(settings, SetupRole.Llm) == RotatedKey && settings.Companion!.ActivePersona.Text == "Martlet is playful and curious." &&
                        settings.Generation?.Temperature == 0.9 && settings.Prompts?.Overrides.Count == 1 && settings.Memory!.Enabled == false && byC.Length == 0,
                    $"C: {route?.Origin} {route?.ModelId}, key matches: {c.Key(settings, SetupRole.Llm) == RotatedKey}; settings written by C: {byC.Length}");
            });

            await Run("A Thinking fallback with its own key follows, and turning it off removes B's copy of that key", async () =>
            {
                await a.EditFallbackAsync(new() { Origin = NvidiaBuild, ModelId = ChatCompletionsEndpointCatalog.NvidiaBuildDefaultModelId, ConfigurationRevision = Guid.NewGuid() }, FallbackKey);
                await a.SyncAsync(hosts, token);
                await b.SyncAsync(hosts, token);
                var on = (await b.SettingsAsync()).ThinkingFallback;
                var had = b.Vault.Has(FallbackKey);
                await a.EditFallbackAsync(null, null);
                await a.SyncAsync(hosts, token);
                await b.SyncAsync(hosts, token);
                var off = (await b.SettingsAsync()).ThinkingFallback;
                return (on?.Origin == NvidiaBuild && had && off is null && !b.Vault.Has(FallbackKey),
                    $"B's fallback: {on?.Origin} with its key: {had}; after A turned it off: {(off is null ? "off" : off.Origin)}, key kept: {b.Vault.Has(FallbackKey)}");
            });

            await Run("NVIDIA Build models in the Thinking pool follow with their key, beside B's own model; removing them removes B's copy of the key", async () =>
            {
                var profile = (await a.SettingsAsync()).Profile.Id;
                var id = Guid.NewGuid();
                DeepThinkingSettings Nvidia(string model) => new() { Place = DeepThinkingPlace.Endpoint, Origin = NvidiaBuild, ModelId = model, CredentialId = id };
                using (var lease = new SecretLease(PoolKey)) a.Vault.Write(Nvidia("x").Binding(profile, id), lease);
                new ThinkingPoolSettings { Members = [Nvidia(ChatCompletionsEndpointCatalog.NvidiaBuildDefaultModelId), Nvidia("meta/llama-4-maverick-17b-128e-instruct")] }
                    .Save(a.Data);
                new ThinkingPoolSettings { Members = [new() { Place = DeepThinkingPlace.Endpoint, Origin = "http://127.0.0.1:11434/v1", ModelId = "qwen3:4b" }] }.Save(b.Data);
                await a.SyncAsync(hosts, token);
                await b.SyncAsync(hosts, token);
                var on = ThinkingPoolSettings.Load(b.Data).Members.Select(m => m.ModelId).ToArray();
                var had = b.Vault.Has(PoolKey);
                new ThinkingPoolSettings().Save(a.Data);
                await a.SyncAsync(hosts, token);
                await b.SyncAsync(hosts, token);
                var off = ThinkingPoolSettings.Load(b.Data).Members.Select(m => m.ModelId).ToArray();
                return (on.Length == 3 && on[0] == "qwen3:4b" && had && off is ["qwen3:4b"] && !b.Vault.Has(PoolKey) && !b.FileContains(PoolKey),
                    $"B's pool: {string.Join(", ", on)} with the key: {had}; after A removed them: {string.Join(", ", off)}, key kept: {b.Vault.Has(PoolKey)}");
            });

            await Run("Thinking pool choices (Backup for slow replies, Busy pool) follow and keep B's own models", async () =>
            {
                (ThinkingPoolSettings.Load(a.Data) with { BackupThinking = true, BackupDelayMs = 1200, RetriesOnFailure = 3, PreemptLowerPriority = false })
                    .Save(a.Data);
                await a.SyncAsync(hosts, token);
                await b.SyncAsync(hosts, token);
                var pool = ThinkingPoolSettings.Load(b.Data);
                var members = pool.Members.Select(m => m.ModelId).ToArray();
                return (pool is { BackupThinking: true, BackupDelayMs: 1200, RetriesOnFailure: 3, PreemptLowerPriority: false } && members is ["qwen3:4b"],
                    $"B's pool: Backup {pool.BackupThinking} after {pool.BackupDelayMs} ms, retries {pool.RetriesOnFailure}, " +
                    $"higher priority first {pool.PreemptLowerPriority}; models {string.Join(", ", members)}");
            });

            await Run("Lorebooks follow", async () =>
            {
                var saved = await a.Lore.UpdateAsync(library => library with
                {
                    Books = [new Lorebook { Id = Guid.NewGuid(), Name = "Owner's projects", Entries = [new LorebookEntry { Uid = 1, Keys = ["robot"], Content = "The owner builds a robot." }] }]
                });
                await a.SyncAsync(hosts, token);
                await b.SyncAsync(hosts, token);
                var books = (await b.Lore.LoadAsync(token)).Library.Books;
                return (saved.Saved && books.Count == 1 && books[0].Name == "Owner's projects", $"B has {books.Count} lorebook(s)");
            });

            await Run("Desktops keep no API key on disk; the hosts keep them in their private copy; every computer ends with the same settings", async () =>
            {
                var leaks = new[] { a, b, c }.Where(d => d.FileContains(OpenRouterKey) || d.FileContains(RotatedKey) || d.FileContains(FallbackKey)).Select(d => d.DeviceId).ToArray();
                var hostHas = h1.SavedText?.Contains(RotatedKey, StringComparison.Ordinal) == true;
                foreach (var desktop in new[] { a, b, c }) await desktop.SyncAsync(hosts, token);
                var digests = new[] { a, b, c }.Select(d => d.Node.Document.SettingsDigest()).Distinct().Count();
                var hostDigests = new[] { await a.ReadAsync(h1, token), await a.ReadAsync(h2, token) }.Select(d => d.SettingsDigest()).Distinct().Count();
                return (leaks.Length == 0 && hostHas && digests == 1 && hostDigests == 1,
                    $"keys in desktop files: {(leaks.Length == 0 ? "none" : string.Join(", ", leaks))}; host copy holds the key: {hostHas}; " +
                    $"distinct copies: desktops {digests}, hosts {hostDigests}");
            });

            await Run("Only paired devices may read the shared settings: an unsigned request is refused", async () =>
            {
                using var handler = new HttpClientHandler { ServerCertificateCustomValidationCallback = (_, _, _, _) => true };
                using var client = new HttpClient(handler);
                using var response = await client.GetAsync(h1.Origin + "/martlet/v1/settings", token);
                var body = await response.Content.ReadAsStringAsync(token);
                return (response.StatusCode != HttpStatusCode.OK && !body.Contains(RotatedKey, StringComparison.Ordinal),
                    $"HTTP {(int)response.StatusCode}, no key in the answer");
            });

            // Accounts (docs/ACCOUNTS.md): D runs this Martlet, with each person's settings apart; A, B and C act as older Martlets.
            var d = new LabAccountDesktop("lab-desktop-d", Path.Combine(root, "d"));
            await Run("An updated desktop D signs in as the owner (Sam): Sam's own copy takes the personality the older desktops share", async () =>
            {
                foreach (var host in hosts) await d.PairAsync(host);
                await d.SignInAsync(hosts, SamAccount, token);
                await d.SyncAsync(hosts, owner: true, token);
                var text = (await d.SettingsAsync()).Companion!.ActivePersona.Text;
                var shared = (await a.SettingsAsync()).Companion!.ActivePersona.Text;
                var samCopy = await d.ReadAccountAsync(h1, SamAccount, token);
                return (text == shared && samCopy.Find(AppSettingsSections.Companion)?.Value.Contains(Json(shared), StringComparison.Ordinal) == true &&
                        samCopy.Find(AppSettingsSections.Thinking) is null && AccountWorkingCopy.Load(d.Directory)?.Account == SamAccount,
                    $"D's personality matches the older desktops': {text == shared}; Sam's copy on lab-settings-1 has {samCopy.Settings.Count} " +
                    $"setting(s) ({string.Join(", ", samCopy.Settings.Select(s => s.Key))}), no route or key");
            });

            await Run("The owner edits the personality on older desktop B, then on D: each reaches the other", async () =>
            {
                await b.EditPersonaAsync("Martlet helps the owner with the garden.");
                await b.SyncAsync(hosts, token);
                await d.SyncAsync(hosts, owner: true, token);
                var onD = (await d.SettingsAsync()).Companion!.ActivePersona.Text;
                await d.EditPersonaAsync("Martlet helps the owner with the garden and the bees.");
                await d.SyncAsync(hosts, owner: true, token);
                await b.SyncAsync(hosts, token);
                var onB = (await b.SettingsAsync()).Companion!.ActivePersona.Text;
                return (onD == "Martlet helps the owner with the garden." && onB == "Martlet helps the owner with the garden and the bees.",
                    $"D took B's edit: {onD == "Martlet helps the owner with the garden."}; B took D's edit: {onB.EndsWith("bees.", StringComparison.Ordinal)}");
            });

            await Run("Alex signs in on D: the files take Alex's settings (the default personality), and nothing of Sam's goes into Alex's copy", async () =>
            {
                await d.SignInAsync(hosts, AlexAccount, token);
                await d.SyncAsync(hosts, owner: false, token);
                var text = (await d.SettingsAsync()).Companion!.ActivePersona.Text;
                var alexCopy = await d.ReadAccountAsync(h1, AlexAccount, token);
                var leaked = alexCopy.Settings.Any(s => s.Value.Contains("bees", StringComparison.Ordinal));
                return (text == CompanionSettings.Create().Personas[0].Text && !leaked && AccountWorkingCopy.Load(d.Directory)?.Account == AlexAccount,
                    $"D shows the default personality: {text == CompanionSettings.Create().Personas[0].Text}; Sam's words in Alex's copy: {leaked}");
            });

            await Run("Alex's own personality goes only to Alex's copy: the household copy and the older desktops keep Sam's", async () =>
            {
                await d.EditPersonaAsync("Martlet helps Alex practice the cello.");
                await d.SyncAsync(hosts, owner: false, token);
                await b.SyncAsync(hosts, token);
                var alexCopy = await d.ReadAccountAsync(h2, AlexAccount, token);
                var household = await d.ReadAsync(h1, token);
                var onB = (await b.SettingsAsync()).Companion!.ActivePersona.Text;
                return (alexCopy.Find(AppSettingsSections.Companion)?.Value.Contains("cello", StringComparison.Ordinal) == true &&
                        household.Find(AppSettingsSections.Companion)?.Value.Contains("bees", StringComparison.Ordinal) == true && onB.EndsWith("bees.", StringComparison.Ordinal),
                    $"Alex's copy on lab-settings-2 has Alex's words; the household copy and B keep Sam's: {onB.EndsWith("bees.", StringComparison.Ordinal)}");
            });

            await Run("Back to Sam on D: Sam's personality returns; the hosts keep both people's settings apart", async () =>
            {
                await d.SignInAsync(hosts, SamAccount, token);
                await d.SyncAsync(hosts, owner: true, token);
                var text = (await d.SettingsAsync()).Companion!.ActivePersona.Text;
                var kept = h1.Server.AccountSettings;
                return (text == "Martlet helps the owner with the garden and the bees." &&
                        kept.SequenceEqual(new[] { AlexAccount.ToString("N"), SamAccount.ToString("N") }.Order(StringComparer.Ordinal)),
                    $"D shows Sam's words: {text.EndsWith("bees.", StringComparison.Ordinal)}; lab-settings-1 keeps settings for {kept.Count} people");
            });

            await Run("A person's settings never carry an API key, and an unsigned request for them is refused", async () =>
            {
                var keyed = SharedSettings.Empty.Put(AppSettingsSections.Companion, "{}", OpenRouterKey, "lab-desktop-d", DateTimeOffset.UtcNow);
                var refused = false;
                try { await d.MergeAccountAsync(h1, SamAccount, keyed, token); }
                catch (ArgumentException) { refused = true; }
                using var handler = new HttpClientHandler { ServerCertificateCustomValidationCallback = (_, _, _, _) => true };
                using var client = new HttpClient(handler);
                using var response = await client.GetAsync(h1.Origin + "/martlet/v1/settings/accounts/" + SamAccount.ToString("N"), token);
                var body = await response.Content.ReadAsStringAsync(token);
                var leaks = d.FileContains(OpenRouterKey) || d.FileContains(RotatedKey);
                return (refused && response.StatusCode != HttpStatusCode.OK && !body.Contains("bees", StringComparison.Ordinal) && !leaks,
                    $"a copy with a key refused before sending: {refused}; unsigned: HTTP {(int)response.StatusCode}; keys in D's files: {leaks}");
            });

            // The files #946, #951, #958 and #979 made shared travel through the real hosts like every other setting, keys included.
            var e = new LabDesktop("lab-desktop-e", Path.Combine(root, "e"), files: true);
            var f = new LabDesktop("lab-desktop-f", Path.Combine(root, "f"), files: true);
            await Run("E and F share keeping voice clips, touch zones, eyes, VRoid Hub credits, Discord with its bot token and messaging with its secrets; who runs each bot stays on E", async () =>
            {
                foreach (var desktop in new[] { e, f })
                foreach (var host in hosts)
                    await desktop.PairAsync(host);
                await f.SyncAsync(hosts, token);
                var at = DateTimeOffset.UtcNow;
                Martlet.Core.Speakers.VoiceChoices.Write(e.Data, Martlet.Core.Speakers.VoiceChoices.ClipsFile, false);
                await Martlet.Avatar.Hosting.CharacterTouchZones.SaveAsync(e.Data, new Martlet.Avatar.Hosting.CharacterTouchZoneSettings
                {
                    ModelId = "lab-model", Crop = new(0, 0, 1, 1), DetectedBy = Martlet.Avatar.Hosting.CharacterTouchZoneSettings.ByOwner,
                    Zones = [new() { Id = "top_of_head", Box = new(0.3, 0, 0.4, 0.2) }]
                }, at, token);
                Martlet.Avatar.Hosting.VRoidHubLooks.Remember(e.Data, new("1234567890", "Lab look", "Lab maker", "https://hub.vroid.com/", ["Credit"], true, null, at));
                const string botToken = "lab-discord-bot-token-for-the-settings-rehearsal";
                const string telegramToken = "123456789:AAHlabTelegramTokenForTheRehearsal";
                var discordId = Guid.NewGuid();
                var telegramId = Guid.NewGuid();
                e.Tokens.Write(discordId, botToken);
                e.Tokens.Write(Martlet.Messaging.MessagingApp.Telegram, telegramId, telegramToken);
                new Martlet.Discord.DiscordPreferences { ApplicationId = 123456789012345678, CredentialId = discordId, Enabled = true, OwnerUserId = 42,
                    People = [new(77, "Alex")] }.Save(e.Data);
                new Martlet.Messaging.MessagingPreferences { Telegram = new() { Enabled = true, CredentialId = telegramId, BotName = "Lab", BotUsername = "lab_bot",
                    Chats = [new("1001", "Sam")] } }.Save(e.Data);
                var recorded = await e.SyncAsync(hosts, token);
                var onHost = await e.ReadAsync(h2, token);
                await f.SyncAsync(hosts, token);
                var discord = Martlet.Discord.DiscordPreferences.Load(f.Data);
                var messaging = Martlet.Messaging.MessagingPreferences.Load(f.Data);
                var keys = new[] { Martlet.Core.Speakers.VoiceChoices.ClipsKey, Martlet.Avatar.Hosting.CharacterTouchZones.SharedKey,
                    Martlet.Avatar.Hosting.VRoidHubLooks.SharedKey, Martlet.Discord.DiscordSharing.SharedKey, Martlet.Messaging.MessagingSharing.SharedKey };
                var travelled = keys.All(k => onHost.Find(k) is not null);
                var clips = !Martlet.Core.Speakers.VoiceChoices.KeepClips(f.Data);
                var zones = Martlet.Avatar.Hosting.CharacterTouchZones.Load(f.Data, "lab-model")?.Zones.Count == 1;
                var credit = Martlet.Avatar.Hosting.VRoidHubLooks.Load(f.Data).Count == 1;
                var bot = discord is { ApplicationId: 123456789012345678, OwnerUserId: 42, Enabled: false } && f.Tokens.Read(discord.CredentialId) == botToken;
                var telegram = messaging.Telegram is { BotUsername: "lab_bot", Enabled: false, Chats.Count: 1 } &&
                    f.Tokens.Read(Martlet.Messaging.MessagingApp.Telegram, messaging.Telegram.CredentialId) == telegramToken;
                var noKeyInFiles = !f.FileContains(botToken) && !f.FileContains(telegramToken) && !onHost.Find(Martlet.Discord.DiscordSharing.SharedKey)!.Value.Contains(botToken, StringComparison.Ordinal);
                return (travelled && clips && zones && credit && bot && telegram && noKeyInFiles && recorded.Recorded.Count >= keys.Length,
                    $"on lab-settings-2: {travelled}; F keeps no clips: {clips}; F has E's touch zones: {zones} and VRoid Hub credit: {credit}; " +
                    $"Discord bot, owner and token on F (not connecting there): {bot}; Telegram bot, chat and token on F (not answering there): {telegram}; " +
                    $"no token in F's files or the shared value: {noKeyInFiles}");
            });
            await Run("E and F share the Vision list (moved from pools-local.json) with its key, a check-in without its script and Chatterbox's style", async () =>
            {
                await e.EditAsync(s => s);
                await f.EditAsync(s => s);
                var profile = (await e.SettingsAsync()).Profile.Id;
                var server = Martlet.Core.Cluster.PoolMember.Cloud("llm-lan", "qwen3-vl", "https://llm.example.com/v1");
                new Martlet.Core.Cluster.PoolSettings().With(new Martlet.Core.Cluster.PoolList { Area = Martlet.Core.Cluster.PoolAreas.Vision.Id, Members = [server] })
                    .Save(e.Data, shared: false);
                const string key = "sk-lab-vision-key-for-the-rehearsal";
                var credential = Guid.NewGuid();
                using (var lease = new SecretLease(key))
                    e.Vault.Write(Martlet.Core.Cluster.PoolKeyShare.Binding(Martlet.Core.Cluster.PoolAreas.Vision, server, profile, credential)!, lease);
                Martlet.Core.Cluster.PoolKeys.Load(e.Data).With(Martlet.Core.Cluster.PoolAreas.Vision.Id, server.Key, credential).Save(e.Data);
                new Martlet.Conversation.CheckInSettings().With(new Martlet.Conversation.CustomCheckIn
                {
                    Id = "c1", Name = "Tea break", On = true, Task = "Remind me to drink tea.", Script = "Get-Date"
                }).Save(e.Data);
                new ChatterboxStyle(0.8, 0.4, 1.2, 0.2).Save(e.Data);
                await e.SyncAsync(hosts, token);
                var onHost = await e.ReadAsync(h1, token);
                await f.SyncAsync(hosts, token);
                await f.SyncAsync(hosts, token);
                var list = Martlet.Core.Cluster.PoolSettings.LoadFor(f.Data, Martlet.Core.Cluster.PoolAreas.Vision)?.Members.Select(m => m.Key).ToArray() ?? [];
                var keyOnF = f.Vault.Has(key) && Martlet.Core.Cluster.PoolKeys.Load(f.Data).For(Martlet.Core.Cluster.PoolAreas.Vision.Id, server.Key) is not null;
                var checkIn = Martlet.Conversation.CheckInSettings.Load(f.Data).Custom.SingleOrDefault();
                var style = ChatterboxStyle.Load(f.Data) == new ChatterboxStyle(0.8, 0.4, 1.2, 0.2);
                var noSecrets = !onHost.Find(Martlet.Core.Cluster.PoolKeyShare.SharedKey)!.Value.Contains(key, StringComparison.Ordinal) &&
                    !onHost.Find(Martlet.Conversation.CheckInSettings.SharedKey)!.Value.Contains("Get-Date", StringComparison.Ordinal) && !f.FileContains(key);
                return (list.SequenceEqual([server.Key]) && keyOnF && checkIn is { Name: "Tea break", Script: "" } && style && noSecrets,
                    $"F has E's Vision list: {list.SequenceEqual([server.Key])}; its key in F's own credential store: {keyOnF}; " +
                    $"the check-in without its script: {checkIn is { Name: "Tea break", Script: "" }}; Chatterbox's style: {style}; " +
                    $"no key or script in the shared values or F's files: {noSecrets}");
            });        }
        finally
        {
            try { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }

        var ok = steps.All(s => s.Ok);
        return (ok, new
        {
            ok,
            passed = steps.Count(s => s.Ok),
            total = steps.Count,
            seconds = Math.Round((DateTimeOffset.UtcNow - started).TotalSeconds, 1),
            scope = "Two real gateways on 127.0.0.1 (Kestrel, pinned TLS, signed requests) with an in-memory shared-settings.json, and three " +
                "simulated desktops using the desktop's paired client, the real sync engine and the real settings sections over real " +
                "settings.json, lorebooks.json and shared-settings.json files in a temporary folder, with an in-memory stand-in for Windows " +
                "Credential Manager; a fourth simulated desktop on this Martlet keeps each person's settings apart (the household's node, " +
                "the signed-in account's node in accounts\\<id>, the owner's bridge to older desktops, switching account) against the " +
                "hosts' /settings/accounts routes; two more simulated desktops share keeping voice clips, touch zones, VRoid Hub credits, Discord " +
                "with its bot token and messaging with its secrets through the same hosts (stand-in credential stores). Not covered: the desktop window and its 15-second sync, its own preference files " +
                "(character, how you talk, speech bubbles, theme), the account picker, the Linux host's files, a real Credential Manager " +
                "and two real computers on a LAN.",
            steps = steps.Select(s => new { step = s.Name, ok = s.Ok, detail = s.Detail })
        });
    }

    /// <summary>Stands in for Windows Credential Manager: keys by binding, with the time each was written.</summary>
    private sealed class LabVault : ICredentialStore, ICredentialTimes
    {
        private readonly Dictionary<string, (string Value, DateTimeOffset At)> items = new(StringComparer.Ordinal);
        internal int Writes { get; private set; }
        internal DateTimeOffset? NextWrittenAt { get; set; }

        private static string Target(CredentialBinding binding) => $"{binding.ProfileId:N}/{binding.CredentialId:N}/{binding.ScopeDigest()}";

        public CredentialError Write(CredentialBinding binding, SecretLease secret)
        {
            binding.Validate();
            string? value = null;
            secret.Use(chars => value = new string(chars));
            items[Target(binding)] = (value!, NextWrittenAt ?? DateTimeOffset.UtcNow);
            NextWrittenAt = null;
            Writes++;
            return CredentialError.None;
        }

        public CredentialReadResult Read(CredentialBinding binding) =>
            items.TryGetValue(Target(binding), out var item) ? new(CredentialError.None, new SecretLease(item.Value)) : new(CredentialError.Missing, null);

        public CredentialError Delete(CredentialBinding binding) => items.Remove(Target(binding)) ? CredentialError.None : CredentialError.Missing;

        public DateTimeOffset? WrittenAt(CredentialBinding binding) => items.TryGetValue(Target(binding), out var item) ? item.At : null;

        internal bool Has(string value) => items.Values.Any(i => i.Value == value);
    }

    /// <summary>Stands in for Windows Credential Manager's Discord bot token and messaging secrets.</summary>
    private sealed class LabTokens : Martlet.Discord.IDiscordTokenVault, Martlet.Messaging.IMessagingSecretVault
    {
        private readonly Dictionary<string, string> items = new(StringComparer.Ordinal);
        public string? Read(Guid credentialId) => items.GetValueOrDefault("discord/" + credentialId.ToString("N"));
        public bool Write(Guid credentialId, string token) { items["discord/" + credentialId.ToString("N")] = token; return true; }
        public void Delete(Guid credentialId) => items.Remove("discord/" + credentialId.ToString("N"));
        public string? Read(Martlet.Messaging.MessagingApp app, Guid credentialId) => items.GetValueOrDefault($"{app}/{credentialId:N}");
        public bool Write(Martlet.Messaging.MessagingApp app, Guid credentialId, string secret) { items[$"{app}/{credentialId:N}"] = secret; return true; }
        public void Delete(Martlet.Messaging.MessagingApp app, Guid credentialId) => items.Remove($"{app}/{credentialId:N}");
        internal bool Has(string value) => items.ContainsValue(value);
    }

    /// <summary>A simulated desktop: real settings files in its folder, its pairings (secrets in memory), the real sync engine and
    /// sections, and the steps the owner takes on Martlet's pages.</summary>
    private sealed class LabDesktop
    {
        private readonly Dictionary<string, (Audio2FaceHostPairing Pairing, string Secret)> pairings = new(StringComparer.Ordinal);
        private readonly string directory;
        internal string DeviceId { get; }
        internal string Data => directory;
        internal LabVault Vault { get; } = new();
        internal SetupService Setup { get; }
        internal LorebookStore Lore { get; }
        internal SharedSettingsNode Node { get; }
        internal Func<SetupRole, SharedRoute, CancellationToken, Task<string?>> Available { get; set; } = (_, _, _) => Task.FromResult<string?>(null);

        /// <summary>The bot token and messaging secrets this desktop keeps (its stand-in for Windows Credential Manager).</summary>
        internal LabTokens Tokens { get; } = new();

        /// <summary>With <paramref name="files"/>, the desktop also shares the files #946, #951, #958 and #979 made shared: keeping
        /// voice clips, the character models' touch zones, eyes and VRoid Hub credits, Discord and messaging (with the real sections).</summary>
        internal LabDesktop(string deviceId, string directory, bool files = false)
        {
            DeviceId = deviceId;
            this.directory = directory;
            Directory.CreateDirectory(directory);
            Setup = new SetupService(new SettingsStore(directory), Vault);
            Lore = new LorebookStore(directory);
            var sections = new AppSettingsSections(Setup, Vault, directory, Lore, available: (role, route, token) => Available(role, route, token));
            IEnumerable<ISharedSection> shared = sections.Sections;
            if (files)
                shared = [.. shared, Martlet.Core.Speakers.VoiceChoices.ClipsSection(directory), Martlet.Avatar.Hosting.CharacterSharedSections.TouchZones(directory),
                    Martlet.Avatar.Hosting.CharacterSharedSections.Eyes(directory), Martlet.Avatar.Hosting.CharacterSharedSections.VRoidHub(directory),
                    Martlet.Discord.DiscordSharing.Section(directory, Tokens), Martlet.Messaging.MessagingSharing.Section(directory, Tokens),
                    StoreSections.Pools(directory), Martlet.Core.Cluster.PoolKeyShare.Section(directory, Vault), Martlet.Core.Pictures.PictureWorkflowFiles.Section(directory),
                    ChatterboxStyle.Section(directory), Martlet.Core.Planning.RecommendedSetupMemory.Section(directory), Martlet.Conversation.CheckInSettings.Section(directory)];
            Node = new SharedSettingsNode(directory, deviceId, [.. shared], sections.Invalidate);
        }

        internal async Task PairAsync(LabHost host)
        {
            var card = host.Server.Pairing.OpenWindow(new() { DeviceId = DeviceId, DisplayName = DeviceId.ToUpperInvariant(), Roles = [GatewayRole.Voice] });
            pairings[host.HostId] = await Audio2FaceHostClient.PairAsync(host.Origin, host.HostId, host.Identity.SpkiFingerprint, DeviceId,
                card.PairingId, card.Token.Reveal());
        }

        private Audio2FaceHostConnection Connect(LabHost host)
        {
            var (pairing, secret) = pairings[host.HostId];
            return new Audio2FaceHostConnection(pairing, secret);
        }

        internal async Task<SharedSettings> ReadAsync(LabHost host, CancellationToken token)
        {
            using var connection = Connect(host);
            return await connection.ReadSettingsAsync(token);
        }

        internal async Task<SharedSettings> MergeAsync(LabHost host, SharedSettings settings, CancellationToken token)
        {
            using var connection = Connect(host);
            return await connection.MergeSettingsAsync(settings, token);
        }

        /// <summary>What the desktop's sync does: read every reachable host's copy, sync, give each the merged copy.</summary>
        internal async Task<SharedSettingsResult> SyncAsync(IEnumerable<LabHost> hosts, CancellationToken token, DateTimeOffset? now = null)
        {
            var copies = new List<SharedSettings>();
            var reachable = new List<LabHost>();
            foreach (var host in hosts.Where(h => h.Running))
            {
                try
                {
                    copies.Add(await ReadAsync(host, token));
                    reachable.Add(host);
                }
                catch (Exception error) when (error is HttpRequestException or Audio2FaceHostException or IOException or OperationCanceledException) { }
            }
            var result = await Node.SyncAsync(copies, true, now ?? DateTimeOffset.UtcNow, token);
            foreach (var host in reachable) await MergeAsync(host, result.Document, token);
            return result;
        }

        internal async Task<AppSettings> SettingsAsync() => (await Setup.LoadAsync()).Settings ?? throw new InvalidOperationException("No settings yet.");

        internal string? Key(AppSettings settings, SetupRole role)
        {
            var route = settings.Setup!.Routes.SingleOrDefault(r => r.Role == role);
            if (route?.CredentialId is not { } id) return null;
            using var read = Vault.Read(CredentialBinding.For(settings.Profile.Id, route, id));
            string? value = null;
            read.Secret?.Use(chars => value = new string(chars));
            return value;
        }

        internal bool FileContains(string text) => Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)
            .Any(path => File.ReadAllText(path).Contains(text, StringComparison.Ordinal));

        private async Task<(AppSettings Settings, string? Revision)> BeginAsync()
        {
            var loaded = await Setup.LoadAsync();
            var settings = SetupSettings.Begin(loaded.Settings);
            return (settings with { Profile = settings.Profile with { Kind = ProfileKind.Api } }, loaded.Revision);
        }

        private static void Require(SetupSaveResult result)
        {
            if (!result.Save.Saved) throw new InvalidOperationException(result.Summary);
        }

        /// <summary>Chooses a job's route as Companion's pages do: the route, then its key, then the confirmed choice.</summary>
        internal async Task ChooseAsync(SetupRole role, Func<AppSettings, AppSettings> select, string? key, DateTimeOffset? keyWrittenAt = null)
        {
            var (settings, revision) = await BeginAsync();
            var old = settings.Setup!.Routes.SingleOrDefault(r => r.Role == role);
            var updated = SetupSettings.QueueReplacedCredential(select(settings), old);
            if (key is not null)
            {
                var staged = await Setup.SaveAsync(updated, revision);
                Require(staged);
                Vault.NextWrittenAt = keyWrittenAt;
                using var lease = new SecretLease(key);
                var stored = await Setup.ReplaceCredentialAsync(staged.Settings, staged.Save.Revision, role, lease);
                Require(stored);
                updated = stored.Settings;
                revision = stored.Save.Revision;
            }
            var route = updated.Setup!.Routes.Single(r => r.Role == role);
            Require(await Setup.SaveAsync(SetupSettings.ReplaceRoute(updated, route with { Consent = route.Selection() }), revision));
        }

        internal async Task EditAsync(Func<AppSettings, AppSettings> change)
        {
            var (settings, revision) = await BeginAsync();
            Require(await Setup.SaveAsync(change(settings), revision));
        }

        internal Task EditPersonaAsync(string text) => EditAsync(s =>
        {
            var persona = s.Companion!.ActivePersona;
            return s with { Companion = s.Companion.Update(persona.Id, persona.Name, text) };
        });

        /// <summary>Saves the Thinking fallback as Companion › Thinking does (its own key first; the replaced key removed after).</summary>
        internal async Task EditFallbackAsync(ThinkingFallbackSettings? fallback, string? key)
        {
            var (settings, revision) = await BeginAsync();
            var old = settings.ThinkingFallback;
            if (fallback is not null && key is not null)
            {
                fallback = fallback with { CredentialId = Guid.NewGuid() };
                using var lease = new SecretLease(key);
                Vault.Write(fallback.Binding(settings.Profile.Id, fallback.CredentialId!.Value), lease);
            }
            Require(await Setup.SaveAsync(settings with { ThinkingFallback = fallback }, revision));
            if (old?.CredentialId is { } oldKey && oldKey != fallback?.CredentialId) Vault.Delete(old.Binding(settings.Profile.Id, oldKey));
        }
    }

    /// <summary>A simulated desktop on this Martlet, with accounts: the household's settings on one node, the signed-in account's
    /// on another in its folder, switched and synced as the desktop does (<see cref="AccountSettingsSync"/>).</summary>
    private sealed class LabAccountDesktop
    {
        private readonly Dictionary<string, (Audio2FaceHostPairing Pairing, string Secret)> pairings = new(StringComparer.Ordinal);
        private readonly SharedSettingsNode household;
        private SharedSettingsNode? account;
        private Guid? signedIn;
        internal string DeviceId { get; }
        internal string Directory { get; }
        internal LabVault Vault { get; } = new();
        internal SetupService Setup { get; }

        internal LabAccountDesktop(string deviceId, string directory)
        {
            DeviceId = deviceId;
            Directory = directory;
            System.IO.Directory.CreateDirectory(directory);
            Setup = new SetupService(new SettingsStore(directory), Vault);
            var sections = new AppSettingsSections(Setup, Vault, directory, null);
            household = new SharedSettingsNode(directory, deviceId, [.. sections.Sections.Where(s => !SettingScopes.IsAccountKey(s.Key))],
                sections.Invalidate);
        }

        internal async Task PairAsync(LabHost host)
        {
            var card = host.Server.Pairing.OpenWindow(new() { DeviceId = DeviceId, DisplayName = DeviceId.ToUpperInvariant(), Roles = [GatewayRole.Voice] });
            pairings[host.HostId] = await Audio2FaceHostClient.PairAsync(host.Origin, host.HostId, host.Identity.SpkiFingerprint, DeviceId,
                card.PairingId, card.Token.Reveal());
        }

        private Audio2FaceHostConnection Connect(LabHost host)
        {
            var (pairing, secret) = pairings[host.HostId];
            return new Audio2FaceHostConnection(pairing, secret);
        }

        internal async Task<SharedSettings> ReadAsync(LabHost host, CancellationToken token)
        {
            using var connection = Connect(host);
            return await connection.ReadSettingsAsync(token);
        }

        internal async Task<SharedSettings> ReadAccountAsync(LabHost host, Guid id, CancellationToken token)
        {
            using var connection = Connect(host);
            return await connection.ReadAccountSettingsAsync(id, token);
        }

        internal async Task<SharedSettings> MergeAccountAsync(LabHost host, Guid id, SharedSettings settings, CancellationToken token)
        {
            using var connection = Connect(host);
            return await connection.MergeAccountSettingsAsync(id, settings, token);
        }

        private SharedSettingsNode NodeFor(Guid id)
        {
            var folder = AccountWorkingCopy.Folder(Directory, id);
            var sections = new AppSettingsSections(Setup, Vault, Directory, new LorebookStore(folder));
            return new SharedSettingsNode(folder, DeviceId, [.. sections.Sections.Where(s => SettingScopes.IsAccountKey(s.Key))], sections.Invalidate,
                account: true);
        }

        /// <summary>What a switch does: record the files into the outgoing account's copy, read the incoming account's copies
        /// from the hosts, give the files its settings, and write the working-copy marker.</summary>
        internal async Task SignInAsync(IEnumerable<LabHost> hosts, Guid id, CancellationToken token)
        {
            var copies = new List<SharedSettings>();
            foreach (var host in hosts.Where(h => h.Running)) copies.Add(await ReadAccountAsync(host, id, token));
            var next = NodeFor(id);
            await AccountSettingsSync.SwitchAsync(account, next, copies, Directory, id, DateTimeOffset.UtcNow, token);
            account = next;
            signedIn = id;
        }

        /// <summary>What the desktop's sync does with accounts: read the household's and the account's copies, sync both (with
        /// the owner's bridge), and give each host the merged copies.</summary>
        internal async Task SyncAsync(IEnumerable<LabHost> hosts, bool owner, CancellationToken token)
        {
            var reachable = hosts.Where(h => h.Running).ToArray();
            var householdCopies = new List<SharedSettings>();
            var accountCopies = new List<SharedSettings>();
            foreach (var host in reachable)
            {
                householdCopies.Add(await ReadAsync(host, token));
                if (signedIn is { } id) accountCopies.Add(await ReadAccountAsync(host, id, token));
            }
            var (h, a) = await AccountSettingsSync.SyncAsync(household, account, owner, householdCopies, accountCopies, true, DateTimeOffset.UtcNow, token);
            foreach (var host in reachable)
            {
                using var connection = Connect(host);
                await connection.MergeSettingsAsync(h.Document, token);
                if (a is not null && signedIn is { } id) await connection.MergeAccountSettingsAsync(id, a.Document, token);
            }
        }

        internal async Task<AppSettings> SettingsAsync() => (await Setup.LoadAsync()).Settings ?? throw new InvalidOperationException("No settings yet.");

        internal async Task EditPersonaAsync(string text)
        {
            var loaded = await Setup.LoadAsync();
            var settings = SetupSettings.Begin(loaded.Settings);
            settings = settings with { Profile = settings.Profile with { Kind = ProfileKind.Api } };
            var persona = settings.Companion!.ActivePersona;
            var saved = await Setup.SaveAsync(settings with { Companion = settings.Companion.Update(persona.Id, persona.Name, text) }, loaded.Revision);
            if (!saved.Save.Saved) throw new InvalidOperationException(saved.Summary);
        }

        internal bool FileContains(string text) => System.IO.Directory.EnumerateFiles(Directory, "*", SearchOption.AllDirectories)
            .Any(path => File.ReadAllText(path).Contains(text, StringComparison.Ordinal));
    }

    /// <summary>A real gateway on 127.0.0.1 with an in-memory shared-settings.json that survives a restart.</summary>
    private sealed class LabHost : IAsyncDisposable, IGatewaySettingsStorage, IGatewayAuditSink
    {
        private X509Certificate2 certificate = null!;
        private GatewayListenerHandle? listener;
        internal GatewayServer Server { get; private set; } = null!;
        internal GatewayHostIdentity Identity { get; private set; } = null!;
        internal string HostId { get; private init; } = "";
        internal string Origin { get; private set; } = "";
        internal bool Running => listener is not null;
        private byte[]? saved;
        internal string? SavedText => saved is null ? null : Encoding.UTF8.GetString(saved);

        internal static async Task<LabHost> StartAsync(string hostId)
        {
            var host = new LabHost { HostId = hostId, certificate = Certificate() };
            host.Identity = GatewayHostIdentity.FromCertificate(hostId, host.certificate);
            try
            {
                await host.StartAsync();
                return host;
            }
            catch
            {
                await host.DisposeAsync();
                throw;
            }
        }

        internal async Task StartAsync()
        {
            Origin = $"https://127.0.0.1:{FreePort()}";
            var origin = new GatewayOrigin(Origin);
            Server = new GatewayServer(Identity, origin, [], this);
            Server.AttachSettingsStorage(this);
            if (Accounts is { } accounts) Server.AttachAccountStorage(new AccountFile(accounts));
            listener = await Server.StartAsync(new GatewayTlsBinding(origin, Identity, certificate), new KestrelGatewayListenerFactory());
        }

        internal async Task StopAsync()
        {
            if (listener is not null) await listener.DisposeAsync();
            listener = null;
        }

        /// <summary>The household's account directory every lab host starts with (accounts.json).</summary>
        internal static byte[]? Accounts { get; set; }

        private sealed class AccountFile(byte[] bytes) : IGatewayAccountStorage
        {
            public byte[]? Load() => bytes;
            public void Save(byte[] value) { }
        }

        public byte[]? Load() => saved;
        public void Save(byte[] bytes) => saved = (byte[])bytes.Clone();
        public void Record(GatewayAuditEvent gatewayEvent) { }

        public async ValueTask DisposeAsync()
        {
            await StopAsync();
            certificate?.Dispose();
        }

        private static int FreePort()
        {
            var probe = new TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            try { return ((IPEndPoint)probe.LocalEndpoint).Port; }
            finally { probe.Stop(); }
        }

        private static X509Certificate2 Certificate()
        {
            using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            var request = new CertificateRequest("CN=Martlet settings rehearsal (fixture)", key, HashAlgorithmName.SHA256);
            request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
            request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
            request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection { new("1.3.6.1.5.5.7.3.1") }, true));
            var names = new SubjectAlternativeNameBuilder();
            names.AddIpAddress(IPAddress.Loopback);
            request.CertificateExtensions.Add(names.Build());
            var now = DateTimeOffset.UtcNow;
            using var ephemeral = request.CreateSelfSigned(now.AddDays(-1), now.AddDays(1));
            var password = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
            var pfx = ephemeral.Export(X509ContentType.Pkcs12, password);
            try { return X509CertificateLoader.LoadPkcs12(pfx, password, X509KeyStorageFlags.UserKeySet); }
            finally { CryptographicOperations.ZeroMemory(pfx); }
        }
    }
}
