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

    public void Dispose()
    {
        if (Directory.Exists(directory)) Directory.Delete(directory, true);
    }
}
