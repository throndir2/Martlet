using Martlet.Core.Settings;

namespace Martlet.Core.Tests;

public sealed class SetupRouteLookupTests
{
    [Fact]
    public void Thinking_route_is_the_thinking_role_route_or_null()
    {
        Assert.Null(((AppSettings?)null).ThinkingRoute());
        var listening = WindowsSpeechSetup.SelectStt(SetupSettings.Begin(null), "installed recognizer");
        Assert.Null(listening.ThinkingRoute());

        var settings = ChatCompletionsSetup.SelectRoute(listening, "https://example.test/api/v1", "org/model:q4");
        var thinking = settings.Setup!.Routes.Single(r => r.Role == SetupRole.Llm);
        Assert.Same(thinking, settings.ThinkingRoute());
        Assert.Same(thinking, settings.Setup.ThinkingRoute());
        Assert.Same(thinking, settings.Setup.Routes.ThinkingRoute());
    }

    [Fact]
    public void Thinking_route_does_not_check_whether_the_route_is_on()
    {
        var settings = ChatCompletionsSetup.SelectRoute(SetupSettings.Begin(null), "https://example.test/api/v1", "org/model:q4");
        var off = settings.Setup!.Routes.Single() with { Enabled = false };
        Assert.Same(off, new[] { off }.ThinkingRoute());
    }
}
