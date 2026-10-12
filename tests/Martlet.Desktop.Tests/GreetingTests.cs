using System.IO;

namespace Martlet.Desktop.Tests;

public sealed class GreetingTests
{
    [Fact]
    public void GreetingIsOnByDefaultAndForSettingsSavedBeforeIt()
    {
        var directory = Path.Combine(Path.GetTempPath(), "martlet-greeting-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            Assert.True(new BackgroundPreferences().GreetAtStart);
            File.WriteAllText(Path.Combine(directory, "background.json"),
                """{"CloseToTray":true,"StartInTray":false,"HintShown":true,"StartCompanion":true}""");
            var older = BackgroundPreferences.Load(directory);
            Assert.True(older.GreetAtStart);
            Assert.True(older.StartCompanion);

            Assert.True((older with { GreetAtStart = false }).Save(directory));
            Assert.False(BackgroundPreferences.Load(directory).GreetAtStart);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public void OpenAppsNeverListsMartletItself()
    {
        var apps = OpenApps.Read();
        Assert.True(apps.Count <= Martlet.Conversation.Greetings.MaximumApps);
        Assert.DoesNotContain(apps, app => app.Name.StartsWith("Martlet", StringComparison.OrdinalIgnoreCase) || app.Name.Length == 0);
        Assert.True(apps.Count(app => app.Front) <= 1);
    }
}
