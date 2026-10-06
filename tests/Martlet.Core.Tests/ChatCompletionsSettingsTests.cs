using System.Text;
using Martlet.Core.Contracts;
using Martlet.Core.Settings;
using Martlet.Credentials.Windows;

namespace Martlet.Core.Tests;

public sealed class ChatCompletionsSettingsTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "Martlet.Chat.Settings", Guid.NewGuid().ToString("N"));
    private SettingsStore Store => new(directory);
    private static AppSettings Settings => ChatCompletionsSetup.SelectRoute(SetupSettings.Begin(null),
        "https://example.test/api/v1", "org/model:q4");

    [Theory]
    [InlineData("https://example.test/v1")]
    [InlineData("https://example.test:8443/custom/v1")]
    [InlineData("https://example.test")]
    [InlineData("http://127.0.0.1:8080/v1")]
    [InlineData("http://[::1]:8080/v1")]
    public void Explicit_canonical_remote_https_and_literal_loopback_http_are_supported(string baseUrl)
    {
        var settings = ChatCompletionsSetup.SelectRoute(Settings, baseUrl, "vendor/model:q4_k_m");
        var route = settings.Setup!.Routes.Single();
        settings.Validate();
        Assert.Equal(baseUrl, route.Origin);
        Assert.True(route.Enabled);
        Assert.Null(route.CredentialId);
        Assert.Equal(SetupRouteType.ChatCompletions, route.RouteType);
        Assert.True(route.Selection().AllowNetworkDisclosure);
        var roundtrip = SettingsJson.Read(ContractJson.Write(settings));
        Assert.Equal(route, roundtrip.Setup!.Routes.Single());
    }

    [Theory]
    [InlineData("http://example.test/v1")]
    [InlineData("http://192.168.1.2:8080/v1")]
    [InlineData("http://localhost:8080/v1")]
    [InlineData("https://user:secret@example.test/v1")]
    [InlineData("https://example.test/v1?key=value")]
    [InlineData("https://example.test/v1#fragment")]
    [InlineData("https://example.test/v1/")]
    [InlineData("https://example.test/v1/chat/completions")]
    [InlineData("https://example.test/path/../v1")]
    [InlineData("https://example.test/%2f/v1")]
    [InlineData("https://example.test\\v1")]
    [InlineData("file:///v1")]
    [InlineData("http://2130706433:8080/v1")]
    public void Unsafe_or_ambiguous_endpoints_fail_before_persistence(string baseUrl) =>
        Assert.Throws<ContractException>(() => ChatCompletionsSetup.SelectRoute(Settings, baseUrl, "model"));

    [Fact]
    public void Endpoint_model_and_credential_changes_invalidate_exact_selection()
    {
        var settings = Settings;
        var route = settings.Setup!.Routes.Single();
        route = route with { CredentialId = Guid.NewGuid() };
        route = route with { Consent = route.Selection() };
        settings = SetupSettings.ReplaceRoute(settings, route);
        var model = ChatCompletionsSetup.SelectRoute(settings, route.Origin, "another:model").Setup!.Routes.Single();
        Assert.Equal(route.CredentialId, model.CredentialId);
        Assert.Null(model.Consent);
        Assert.NotEqual(route.Selection().SelectionSha256, model.Selection().SelectionSha256);
        var endpoint = ChatCompletionsSetup.SelectRoute(settings, "https://example.test/other/v1", route.ModelId).Setup!.Routes.Single();
        Assert.Null(endpoint.CredentialId);
        Assert.Null(endpoint.Consent);
        Assert.NotEqual(route.ConfigurationRevision, endpoint.ConfigurationRevision);
        Assert.Throws<ContractException>(() => (endpoint with { Consent = route.Consent }).Validate());
        Assert.Throws<ContractException>(() => (route with { RouteType = SetupRouteType.OpenAi }).Validate());
    }

    [Fact]
    public void Named_endpoints_are_explicit_and_keep_generic_route_contract()
    {
        Assert.Equal(new[] { "OpenRouter", "NVIDIA Build", "Google Gemini" },
            ChatCompletionsEndpointCatalog.NamedEndpoints.Select(endpoint => endpoint.Name));
        Assert.Equal(new[] { ChatCompletionsEndpointCatalog.OpenRouterBaseUrl,
                ChatCompletionsEndpointCatalog.NvidiaBuildBaseUrl, ChatCompletionsEndpointCatalog.GeminiBaseUrl },
            ChatCompletionsEndpointCatalog.NamedEndpoints.Select(endpoint => endpoint.BaseUrl));
        Assert.Equal(new[] { "openrouter", "nvidia-build", "google-gemini" },
            ChatCompletionsEndpointCatalog.NamedEndpoints.Select(endpoint => endpoint.Id));
        Assert.All(ChatCompletionsEndpointCatalog.NamedEndpoints, endpoint =>
        {
            Assert.Same(endpoint, ChatCompletionsEndpointCatalog.ById(endpoint.Id));
            Assert.StartsWith("https://", endpoint.KeyUrl);
        });
        foreach (var endpoint in ChatCompletionsEndpointCatalog.NamedEndpoints)
        {
            var selected = ChatCompletionsSetup.SelectRoute(Settings, endpoint.BaseUrl, "synthetic/model:v1");
            var route = selected.Setup!.Routes.Single();
            Assert.Equal(endpoint.BaseUrl, route.Origin);
            Assert.Equal("synthetic/model:v1", route.ModelId);
            Assert.Null(route.CredentialId);
            Assert.Null(route.Consent);
            Assert.Equal(SetupRouteType.ChatCompletions, route.RouteType);
            Assert.Equal(route, SettingsJson.Read(ContractJson.Write(selected)).Setup!.Routes.Single());
            Assert.Throws<ContractException>(() => ChatCompletionsSetup.SelectRoute(Settings, endpoint.BaseUrl, ""));
        }
    }

    [Fact]
    public void Named_endpoint_switch_clears_key_and_consent_but_exact_model_change_keeps_scoped_key()
    {
        var selected = ChatCompletionsSetup.SelectRoute(Settings,
            ChatCompletionsEndpointCatalog.OpenRouterBaseUrl, "synthetic/model:v1");
        var keyed = selected.Setup!.Routes.Single() with { CredentialId = Guid.NewGuid() };
        keyed = keyed with { Consent = keyed.Selection() };
        selected = SetupSettings.ReplaceRoute(selected, keyed);
        Assert.Equal(keyed.Selection(), selected.Setup!.Routes.Single().Consent);
        Assert.Same(selected, ChatCompletionsSetup.SelectRoute(selected, keyed.Origin, keyed.ModelId));
        var modelChanged = ChatCompletionsSetup.SelectRoute(selected, keyed.Origin, "synthetic/model:v2");
        Assert.Equal(keyed.CredentialId, modelChanged.Setup!.Routes.Single().CredentialId);
        Assert.Null(modelChanged.Setup.Routes.Single().Consent);
        var endpointChanged = ChatCompletionsSetup.SelectRoute(selected,
            ChatCompletionsEndpointCatalog.NvidiaBuildBaseUrl, keyed.ModelId);
        var route = endpointChanged.Setup!.Routes.Single();
        Assert.Null(route.CredentialId);
        Assert.Null(route.Consent);
        Assert.NotEqual(keyed.ConfigurationRevision, route.ConfigurationRevision);
        Assert.Throws<ContractException>(() => (route with { Consent = keyed.Consent }).Validate());
        Assert.Equal(ChatCompletionsEndpointCatalog.NvidiaBuildBaseUrl,
            CredentialBinding.For(endpointChanged, SetupRole.Llm, Guid.NewGuid()).Origin);
    }

    [Fact]
    public async Task Named_route_restart_preserves_only_approved_endpoint_model_and_credential_reference()
    {
        var selected = ChatCompletionsSetup.SelectRoute(Settings,
            ChatCompletionsEndpointCatalog.NvidiaBuildBaseUrl, "synthetic/model:v1");
        var keyed = selected.Setup!.Routes.Single() with { CredentialId = Guid.NewGuid() };
        selected = SetupSettings.ReplaceRoute(selected, keyed with { Consent = keyed.Selection() });
        Assert.True((await Store.SaveAsync(selected, null)).Saved);
        var loaded = (await Store.LoadAsync()).Settings!;
        Assert.Equal(selected.Setup!.Routes.Single(), loaded.Setup!.Routes.Single());
        Assert.Equal(loaded.Setup.Routes.Single().Selection(), loaded.Setup.Routes.Single().Consent);
        var binding = CredentialBinding.For(loaded, SetupRole.Llm, keyed.CredentialId!.Value);
        Assert.Equal(SetupRouteType.ChatCompletions, binding.RouteType);
        Assert.Equal(ChatCompletionsEndpointCatalog.NvidiaBuildBaseUrl, binding.Origin);
        Assert.Equal(keyed.CredentialId, loaded.Setup.Routes.Single().CredentialId);
        Assert.Throws<ContractException>(() =>
            (loaded.Setup.Routes.Single() with { ModelId = "synthetic/changed-model" }).Validate());
    }

    [Fact]
    public async Task Credential_rotation_detach_cleanup_and_recovery_keep_exact_custom_namespace()
    {
        using var native = new FakeCredentialNative();
        var vault = new WindowsCredentialStore(native);
        var service = new SetupService(Store, vault);
        using var secret = new SecretLease("synthetic.local-test.key");
        var saved = await service.ReplaceCredentialAsync(Settings, null, SetupRole.Llm, secret);
        Assert.True(saved.Save.Saved, saved.Summary);
        var route = saved.Settings.Setup!.Routes.Single();
        Assert.StartsWith($"write Martlet/v3/{saved.Settings.Profile.Id:N}/chat-completions/", Assert.Single(native.Events));
        using (var read = vault.Read(CredentialBinding.For(saved.Settings, SetupRole.Llm, route.CredentialId!.Value)))
            Assert.Equal(CredentialError.None, read.Error);
        var changedBinding = CredentialBinding.For(saved.Settings, SetupRole.Llm, route.CredentialId!.Value) with
        {
            Origin = "https://example.test/other/v1"
        };
        using (var read = vault.Read(changedBinding)) Assert.Equal(CredentialError.Missing, read.Error);
        Assert.DoesNotContain("synthetic.local-test.key", await File.ReadAllTextAsync(Store.FilePath));
        var loaded = await Store.LoadAsync();
        var rotated = await service.ReplaceCredentialAsync(loaded.Settings!, loaded.Revision, SetupRole.Llm, secret);
        Assert.True(rotated.Save.Saved, rotated.Summary);
        var pending = Assert.Single(rotated.Settings.Setup!.PendingRemovals);
        Assert.Equal(SetupRouteType.ChatCompletions, pending.Scope!.RouteType);
        Assert.Equal(route.Origin, pending.Scope.Origin);
        loaded = await Store.LoadAsync();
        var cleaned = await service.RemoveDetachedAsync(loaded.Settings!, loaded.Revision, pending);
        Assert.True(cleaned.Save.Saved, cleaned.Summary);

        var backup = Path.Combine(directory, "chat.martlet-config");
        await Store.CreateConfigurationSnapshotAsync(backup);
        var plan = await Store.PreviewConfigurationRestoreAsync(backup);
        var candidate = SettingsJson.Read(Encoding.UTF8.GetBytes(plan.CandidateJson));
        var restored = candidate.Setup!.Routes.Single();
        Assert.False(restored.Enabled);
        Assert.Null(restored.CredentialId);
        Assert.Null(restored.Consent);
        Assert.Equal(SetupRouteType.ChatCompletions, Assert.Single(candidate.Setup.PendingRemovals).Scope!.RouteType);
        await Store.RestoreConfigurationAsync(plan, plan.Approve(plan.SnapshotDigest, plan.Destination, plan.ExpectedRevision));
        loaded = await Store.LoadAsync();
        pending = Assert.Single(loaded.Settings!.Setup!.PendingRemovals);
        cleaned = await service.RemoveDetachedAsync(loaded.Settings, loaded.Revision, pending);
        Assert.True(cleaned.Save.Saved, cleaned.Summary);
    }

    [Fact]
    public async Task Local_windows_routes_roundtrip_exact_ids_without_provider_credentials_and_restore_off()
    {
        const string recognizer = @"HKEY_LOCAL_MACHINE\Speech\Recognizers\Installed English";
        const string voice = @"HKEY_LOCAL_MACHINE\Speech\Voices\Installed Voice";
        var settings = WindowsSpeechSetup.SelectTts(WindowsSpeechSetup.SelectStt(Settings, recognizer), voice);
        settings = SetupSettings.SetRouteEnabled(settings, SetupRole.Stt, true, true);
        settings = SetupSettings.SetRouteEnabled(settings, SetupRole.Tts, true, true);
        var local = settings.Setup!.Routes.Where(route => route.Role != SetupRole.Llm).ToArray();
        Assert.All(local, route =>
        {
            Assert.True(route.Selection().AllowLocalProcess);
            Assert.False(route.Selection().AllowNetworkDisclosure);
            Assert.False(route.Selection().AllowPotentialCost);
            Assert.Throws<ContractException>(() => route.WithCredential(Guid.NewGuid()));
            Assert.Throws<ContractException>(() => (route with { CredentialId = Guid.NewGuid(), Consent = null }).Validate());
        });
        var roundtrip = SettingsJson.Read(ContractJson.Write(settings));
        Assert.Equal(recognizer, roundtrip.Setup!.Routes.Single(route => route.Role == SetupRole.Stt).ModelId);
        Assert.Equal(voice, roundtrip.Setup.Routes.Single(route => route.Role == SetupRole.Tts).VoiceId);
        Assert.True((await Store.SaveAsync(settings, null)).Saved);
        var backup = Path.Combine(directory, "local.martlet-config");
        await Store.CreateConfigurationSnapshotAsync(backup);
        var plan = await Store.PreviewConfigurationRestoreAsync(backup);
        await Store.RestoreConfigurationAsync(plan, plan.Approve(plan.SnapshotDigest, plan.Destination, plan.ExpectedRevision));
        var restored = (await Store.LoadAsync()).Settings!;
        Assert.All(restored.Setup!.Routes, route => { Assert.False(route.Enabled); Assert.Null(route.Consent); });
    }

    [Fact]
    public void Keyless_and_local_credential_checks_do_not_consult_vault()
    {
        using var native = new FakeCredentialNative();
        var service = new SetupService(Store, new WindowsCredentialStore(native));
        var settings = WindowsSpeechSetup.SelectTts(WindowsSpeechSetup.SelectStt(Settings, "installed recognizer"), "installed voice");
        foreach (var role in Enum.GetValues<SetupRole>())
            Assert.Equal(CredentialError.None, service.CheckCredential(settings, role));
        Assert.Empty(native.Events);
        var openAi = SetupSettings.SelectRoute(settings, SetupRole.Llm, "gpt-4.1-mini-2025-04-14", null);
        Assert.Equal(CredentialError.Missing, service.CheckCredential(openAi, SetupRole.Llm));
    }

    [Fact]
    public async Task Switching_providers_sets_old_keys_aside_and_switching_back_uses_them_again()
    {
        using var native = new FakeCredentialNative();
        var vault = new WindowsCredentialStore(native);
        var service = new SetupService(Store, vault);
        var openRouter = ChatCompletionsEndpointCatalog.OpenRouterBaseUrl;
        var nvidia = ChatCompletionsEndpointCatalog.NvidiaBuildBaseUrl;
        const string ollama = "http://127.0.0.1:11434/v1";

        async Task<(AppSettings Settings, string? Revision)> Switch(AppSettings settings, string? revision, string baseUrl,
            string model, SecretLease? key = null)
        {
            var old = settings.Setup!.Routes.SingleOrDefault(route => route.Role == SetupRole.Llm);
            var next = ChatCompletionsSetup.SelectRoute(settings, baseUrl, model);
            if (key is null && SetupSettings.SetAsideCredentials(next, SetupRole.Llm, baseUrl).FirstOrDefault() is { } setAside)
                next = SetupSettings.ReattachSetAsideCredential(next, setAside);
            next = SetupSettings.QueueReplacedCredential(next, old);
            if (key is not null)
            {
                var staged = await service.SaveAsync(next, revision);
                Assert.True(staged.Save.Saved, staged.Summary);
                var stored = await service.ReplaceCredentialAsync(staged.Settings, staged.Save.Revision, SetupRole.Llm, key);
                Assert.True(stored.Save.Saved, stored.Summary);
                (next, revision) = (stored.Settings, stored.Save.Revision);
            }
            var chosen = next.Setup!.Routes.Single(route => route.Role == SetupRole.Llm);
            next = SetupSettings.ReplaceRoute(next, chosen with { Consent = chosen.Selection() });
            var saved = await service.SaveAsync(next, revision);
            Assert.True(saved.Save.Saved, saved.Summary);
            return (next, saved.Save.Revision);
        }

        using var first = new SecretLease("synthetic.openrouter.key");
        using var second = new SecretLease("synthetic.nvidia.key");
        var (settings, revision) = await Switch(Settings, null, openRouter, "synthetic/model:v1", first);
        var openRouterKey = settings.Setup!.Routes.Single().CredentialId!.Value;
        (settings, revision) = await Switch(settings, revision, nvidia, "synthetic/model:v1", second);
        var nvidiaKey = settings.Setup!.Routes.Single().CredentialId!.Value;
        Assert.Equal(openRouterKey, Assert.Single(settings.Setup.PendingRemovals).CredentialId);

        // Switching to a local server while an older key is already set aside sets this one aside too; nothing is deleted.
        (settings, revision) = await Switch(settings, revision, ollama, "gemma4:12b");
        Assert.Null(settings.Setup!.Routes.Single().CredentialId);
        Assert.Equal(new[] { openRouterKey, nvidiaKey }, settings.Setup.PendingRemovals.Select(item => item.CredentialId));
        Assert.Empty(SetupSettings.SetAsideCredentials(settings, SetupRole.Llm, ollama));
        Assert.Empty(SetupSettings.SetAsideCredentials(settings, SetupRole.Llm, null));
        Assert.Empty(SetupSettings.SetAsideCredentials(settings, SetupRole.Tts, openRouter));
        Assert.Equal(nvidiaKey, Assert.Single(SetupSettings.SetAsideCredentials(settings, SetupRole.Llm, nvidia)).CredentialId);
        Assert.DoesNotContain(native.Events, item => item.StartsWith("delete", StringComparison.Ordinal));

        // A key only fits its own destination.
        var atOllama = ChatCompletionsSetup.SelectRoute(settings, ollama, "gemma4:27b");
        Assert.Throws<ContractException>(() => SetupSettings.ReattachSetAsideCredential(atOllama, settings.Setup.PendingRemovals[0]));

        // Switching back to OpenRouter uses its key again, without typing it.
        (settings, revision) = await Switch(settings, revision, openRouter, "synthetic/model:v2");
        var back = settings.Setup!.Routes.Single();
        Assert.Equal(openRouterKey, back.CredentialId);
        Assert.Equal(back.Selection(), back.Consent);
        Assert.Equal(nvidiaKey, Assert.Single(settings.Setup.PendingRemovals).CredentialId);
        Assert.Equal(CredentialError.None, service.CheckCredential(settings, SetupRole.Llm));
        using (var read = vault.Read(CredentialBinding.For(settings, SetupRole.Llm, openRouterKey)))
            read.Secret!.Use(value => Assert.Equal("synthetic.openrouter.key", new string(value)));
        var loaded = await Store.LoadAsync();
        Assert.Equal(revision, loaded.Revision);
        Assert.Equal(openRouterKey, loaded.Settings!.Setup!.Routes.Single().CredentialId);
    }

    public void Dispose()
    {
        if (Directory.Exists(directory)) Directory.Delete(directory, true);
    }
}
