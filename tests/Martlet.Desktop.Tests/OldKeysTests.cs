using Martlet.Core.Settings;
using Martlet.Desktop;

namespace Martlet.Desktop.Tests;

/// <summary>Companion › Thinking, Voice and Listening › Keys from before, which replaced the old Setup window's Credentials
/// step for removing keys Martlet set aside.</summary>
public sealed class OldKeysTests
{
    private static PendingCredentialRemoval Key(SetupRole role, CredentialScopeSettings? scope = null) =>
        new() { Role = role, CredentialId = Guid.NewGuid(), Scope = scope };

    private static CredentialScopeSettings Scope(SetupRouteType type, string origin, string? host = null) =>
        new() { SchemaVersion = 1, RouteType = type, ProviderAlias = "alias", Origin = origin, HostId = host };

    [Fact]
    public void EachKeyIsNamedForWhereItWasUsed()
    {
        Assert.Equal("your OpenAI key", MainWindow.OldKeyName(Key(SetupRole.Llm)));
        Assert.Equal("your OpenAI key", MainWindow.OldKeyName(Key(SetupRole.Tts, Scope(SetupRouteType.OpenAi, OpenAiSetup.Origin))));
        Assert.Equal("your OpenRouter key", MainWindow.OldKeyName(Key(SetupRole.Llm,
            Scope(SetupRouteType.ChatCompletions, ChatCompletionsEndpointCatalog.OpenRouterBaseUrl))));
        Assert.Equal("your key for 192.168.1.5:8000", MainWindow.OldKeyName(Key(SetupRole.Llm,
            Scope(SetupRouteType.ChatCompletions, "http://192.168.1.5:8000/v1"))));
        Assert.Equal("the pairing key for diva-host", MainWindow.OldKeyName(Key(SetupRole.Llm,
            Scope(SetupRouteType.GatewayOllama, "https://192.168.1.9:8443", "diva-host"))));
    }

    [Fact]
    public void AJobListsOnlyItsOwnKeysNewestFirst()
    {
        var older = Key(SetupRole.Llm);
        var listening = Key(SetupRole.Stt);
        var newer = Key(SetupRole.Llm, Scope(SetupRouteType.ChatCompletions, ChatCompletionsEndpointCatalog.OpenRouterBaseUrl));
        var settings = SetupSettings.Begin(AppSettings.CreateUnconfigured());
        settings = settings with { Setup = settings.Setup! with { PendingRemovals = [older, listening, newer] } };

        Assert.Equal(new[] { newer, older }, MainWindow.OldKeys(settings, SetupRole.Llm));
        Assert.Equal(new[] { listening }, MainWindow.OldKeys(settings, SetupRole.Stt));
        Assert.Empty(MainWindow.OldKeys(settings, SetupRole.Tts));
        Assert.Empty(MainWindow.OldKeys(null, SetupRole.Llm));
    }
}
