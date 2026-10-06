using Martlet.Core.Settings;

namespace Martlet.Core.Tests;

public sealed class PromptSettingsTests
{
    [Fact]
    public void RetiredPromptEditsAreDroppedSoOlderSettingsStillLoad()
    {
        var settings = new PromptSettings
        {
            Overrides = new Dictionary<string, string> { ["character_theme"] = "old", [PromptCatalog.Persona] = "Name: {name}" }
        };
        settings.Validate();
        Assert.Equal([PromptCatalog.Persona], settings.Overrides.Keys);
        Assert.Null(PromptCatalog.Find("character_theme"));
    }
}
