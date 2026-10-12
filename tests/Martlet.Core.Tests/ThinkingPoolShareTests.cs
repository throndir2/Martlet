using Martlet.Core.Lorebooks;
using Martlet.Core.Settings;
using Martlet.Core.Sync;

namespace Martlet.Core.Tests;

/// <summary>The Thinking pool's cloud providers and servers, with their keys, are the same on all your computers
/// (docs/CONVERSATION.md, "The Thinking pool"); each PC keeps its own paired computers and models in Ollama on this PC.</summary>
public sealed class ThinkingPoolShareTests : IDisposable
{
    private const string Nvidia = "https://integrate.api.nvidia.com/v1";
    private const string OpenRouter = "https://openrouter.ai/api/v1";
    private const string NvidiaKey = "nvapi-lab-0123456789abcdefghijklmnopqrstuvwxyz";
    private static readonly DateTimeOffset Start = new(2026, 10, 11, 12, 0, 0, TimeSpan.Zero);
    private readonly string root = Path.Combine(Path.GetTempPath(), "martlet-thinking-pool-share-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }

    private static DeepThinkingSettings Endpoint(string origin, string model, Guid? key = null, int? slots = null) =>
        new() { Place = DeepThinkingPlace.Endpoint, Origin = origin, ModelId = model, CredentialId = key, Slots = slots, ChosenAt = Start };

    private static DeepThinkingSettings Host(string host) => new()
    {
        Place = DeepThinkingPlace.Host, HostId = host, HostOrigin = "https://192.168.1.20:9443", HostSpkiFingerprint = "sha256:ab",
        HostDeviceId = "desktop-a", HostCredentialId = Guid.NewGuid(), ModelId = "gemma4-e2b", ChosenAt = Start
    };

    [Fact]
    public void Only_cloud_providers_and_servers_travel_with_their_choices_and_one_copy_of_each_key()
    {
        Guid nemotron = Guid.NewGuid(), llama = Guid.NewGuid();
        var pool = new ThinkingPoolSettings
        {
            Members = [Host("diva"), Endpoint(Nvidia, "nvidia/nemotron-ultra", nemotron, 2), Endpoint("http://127.0.0.1:11434/v1", "gemma4:e2b")],
            OffMembers = [Endpoint(Nvidia, "meta/llama-4", llama), Endpoint(OpenRouter, "x-ai/grok")]
        }.WithJobs($"endpoint:{Nvidia}|nvidia/nemotron-ultra", quick: false).WithMedia($"endpoint:{Nvidia}|nvidia/nemotron-ultra", true)
         .WithSmarts($"endpoint:{Nvidia}|meta/llama-4", ThinkingSmarts.Fast);

        var (shared, keys) = ThinkingPoolShare.From(pool, m => m.CredentialId is not null ? NvidiaKey : null);

        Assert.Equal(["nvidia/nemotron-ultra", "meta/llama-4", "x-ai/grok"], shared.Members.Select(m => m.Model));
        Assert.Equal([true, false, false], shared.Members.Select(m => m.On));
        Assert.Equal([NvidiaKey], keys);
        Assert.Equal([0, 0, null], shared.Members.Select(m => m.Key));
        Assert.False(shared.Members[0].QuickJobs);
        Assert.True(shared.Members[0].Media);
        Assert.Equal(2, shared.Members[0].Slots);
        Assert.Equal(ThinkingSmarts.Fast, shared.Members[1].Smarts);
        Assert.DoesNotContain(NvidiaKey, ThinkingPoolShare.Write(shared), StringComparison.Ordinal);
    }

    [Fact]
    public void Following_keeps_this_pcs_own_members_and_reads_back_the_same()
    {
        var shared = new SharedThinkingPool
        {
            Members =
            [
                new() { Origin = Nvidia, Model = "nvidia/nemotron-ultra", Key = 0, QuickJobs = false, Answers = true },
                new() { Origin = OpenRouter, Model = "x-ai/grok", On = false, Smarts = ThinkingSmarts.Smart }
            ]
        };
        var local = new ThinkingPoolSettings
        {
            Members = [Host("imouto"), Endpoint(OpenRouter, "old/model", Guid.NewGuid()), Endpoint("http://127.0.0.1:11434/v1", "qwen3:4b")]
        }.WithJobs($"endpoint:{OpenRouter}|old/model", @long: false).WithJobs("host:imouto", quick: false);
        var key = Guid.NewGuid();

        var next = ThinkingPoolShare.Apply(local, shared, s => s.Key is null ? null : key, Start);

        Assert.Equal(["host:imouto", "endpoint:http://127.0.0.1:11434/v1|qwen3:4b", $"endpoint:{Nvidia}|nvidia/nemotron-ultra"],
            next.Members.Select(m => m.Key));
        Assert.Equal([$"endpoint:{OpenRouter}|x-ai/grok"], next.OffMembers.Select(m => m.Key));
        Assert.Equal(key, next.Members[2].CredentialId);
        Assert.Equal(["host:imouto", $"endpoint:{Nvidia}|nvidia/nemotron-ultra"], next.NoQuickJobs);
        Assert.Empty(next.NoLongJobs);
        Assert.True(next.Answers($"endpoint:{Nvidia}|nvidia/nemotron-ultra"));
        var (again, keys) = ThinkingPoolShare.From(next, _ => NvidiaKey);
        Assert.Equal(ThinkingPoolShare.Write(shared), ThinkingPoolShare.Write(again));
        Assert.Equal([NvidiaKey], keys);
    }

    [Fact]
    public void Keys_travel_as_one_secret_the_shared_settings_accept()
    {
        var bundle = ThinkingPoolShare.Bundle([NvidiaKey, "sk-or-v1.abc_DEF-123"]);
        Assert.True(SharedSettings.IsSecret(bundle));
        Assert.Equal([NvidiaKey, "sk-or-v1.abc_DEF-123"], ThinkingPoolShare.Unbundle(bundle));
        Assert.Null(ThinkingPoolShare.Bundle([]));
        Assert.Empty(ThinkingPoolShare.Unbundle(null));
        Assert.Throws<Contracts.ContractException>(() => ThinkingPoolShare.Unbundle("not-a-bundle"));
    }

    [Fact]
    public void A_member_on_the_computer_itself_or_a_newer_format_is_never_followed()
    {
        Assert.Throws<Contracts.ContractException>(() => ThinkingPoolShare.Parse(
            """{"schema_version":1,"members":[{"origin":"http://127.0.0.1:11434/v1","model":"qwen3:4b"}]}"""));
        Assert.Null(ThinkingPoolShare.Parse("""{"schema_version":2,"members":[]}"""));
    }

    private sealed class MemoryVault : ICredentialStore
    {
        private readonly Dictionary<string, string> items = new(StringComparer.Ordinal);
        private static string Target(CredentialBinding b) => $"{b.ProfileId:N}/{b.CredentialId:N}/{b.ScopeDigest()}";

        public CredentialError Write(CredentialBinding binding, SecretLease secret)
        {
            binding.Validate();
            secret.Use(chars => items[Target(binding)] = new string(chars));
            return CredentialError.None;
        }

        public CredentialReadResult Read(CredentialBinding binding) =>
            items.TryGetValue(Target(binding), out var value) ? new(CredentialError.None, new SecretLease(value)) : new(CredentialError.Missing, null);

        public CredentialError Delete(CredentialBinding binding) => items.Remove(Target(binding)) ? CredentialError.None : CredentialError.Missing;

        internal bool Has(string value) => items.ContainsValue(value);
    }

    private sealed class Pc
    {
        internal string Data { get; }
        internal MemoryVault Vault { get; } = new();
        internal SetupService Setup { get; }
        internal SharedSettingsNode Node { get; }

        internal Pc(string data, string device)
        {
            Data = data;
            Directory.CreateDirectory(data);
            Setup = new SetupService(new SettingsStore(data), Vault);
            var sections = new AppSettingsSections(Setup, Vault, data, new LorebookStore(data));
            Node = new SharedSettingsNode(data, device, sections.Sections, sections.Invalidate);
        }

        internal async Task<Guid> ProfileAsync()
        {
            var loaded = await Setup.LoadAsync();
            if (loaded.Settings is { } settings) return settings.Profile.Id;
            var begun = SetupSettings.Begin(null);
            var saved = await Setup.SaveAsync(begun with { Profile = begun.Profile with { Kind = ProfileKind.Api } }, loaded.Revision);
            Assert.True(saved.Save.Saved, saved.Summary);
            return saved.Settings.Profile.Id;
        }
    }

    [Fact]
    public async Task NVIDIA_models_and_their_key_added_on_one_companion_pc_reach_the_next_one_and_leave_with_it()
    {
        var imouto = new Pc(Path.Combine(root, "imouto"), "desktop-imouto");
        var lumine = new Pc(Path.Combine(root, "lumine"), "desktop-lumine");
        var profile = await imouto.ProfileAsync();
        await lumine.ProfileAsync();
        await imouto.Node.SyncAsync([], true, Start, CancellationToken.None);
        await lumine.Node.SyncAsync([imouto.Node.Document], true, Start, CancellationToken.None);

        // On IMOUTO the owner adds two NVIDIA Build models with one key, as Companion › Thinking pool does.
        var id = Guid.NewGuid();
        using (var lease = new SecretLease(NvidiaKey)) imouto.Vault.Write(Endpoint(Nvidia, "x").Binding(profile, id), lease);
        Assert.True(new ThinkingPoolSettings { Members = [Endpoint(Nvidia, "nvidia/nemotron-ultra", id), Endpoint(Nvidia, "meta/llama-4", id)] }
            .Save(imouto.Data));
        var recorded = await imouto.Node.SyncAsync([], true, Start.AddMinutes(1), CancellationToken.None);
        Assert.Contains(AppSettingsSections.ThinkingPool, recorded.Recorded);

        // LUMINE already had a model in its own Ollama; it keeps it and gets both NVIDIA models with the key.
        Assert.True(new ThinkingPoolSettings { Members = [Endpoint("http://127.0.0.1:11434/v1", "qwen3:4b")] }.Save(lumine.Data));
        var followed = await lumine.Node.SyncAsync([imouto.Node.Document], true, Start.AddMinutes(2), CancellationToken.None);
        Assert.Contains(followed.Applied, a => a.Key == AppSettingsSections.ThinkingPool);
        Assert.False(followed.Waiting.ContainsKey(AppSettingsSections.ThinkingPool));
        var pool = ThinkingPoolSettings.Load(lumine.Data);
        Assert.Equal(["endpoint:http://127.0.0.1:11434/v1|qwen3:4b", $"endpoint:{Nvidia}|nvidia/nemotron-ultra", $"endpoint:{Nvidia}|meta/llama-4"],
            pool.Members.Select(m => m.Key));
        Assert.True(lumine.Vault.Has(NvidiaKey));
        Assert.Equal(pool.Members[1].CredentialId, pool.Members[2].CredentialId);
        Assert.DoesNotContain(NvidiaKey, File.ReadAllText(Path.Combine(lumine.Data, SharedSettingsState.FileName)), StringComparison.Ordinal);

        // A later sync on LUMINE records nothing: what it follows reads back the same.
        var quiet = await lumine.Node.SyncAsync([imouto.Node.Document], true, Start.AddMinutes(3), CancellationToken.None);
        Assert.Empty(quiet.Recorded);

        // The owner removes the NVIDIA models on IMOUTO: LUMINE drops them and their key, and keeps its own model.
        Assert.True(new ThinkingPoolSettings().Save(imouto.Data));
        await imouto.Node.SyncAsync([], true, Start.AddMinutes(4), CancellationToken.None);
        await lumine.Node.SyncAsync([imouto.Node.Document], true, Start.AddMinutes(5), CancellationToken.None);
        Assert.Equal(["endpoint:http://127.0.0.1:11434/v1|qwen3:4b"], ThinkingPoolSettings.Load(lumine.Data).All.Select(m => m.Key));
        Assert.False(lumine.Vault.Has(NvidiaKey));
    }
}
