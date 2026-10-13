using System.Reflection;
using Martlet.Core.Settings;
using Martlet.Desktop;

namespace Martlet.Desktop.Tests;

/// <summary>Each registered settings file has its store (<see cref="SettingsFiles"/>), and talk-preferences.json's store reads
/// the file the same way for the desktop and for the MCP checks.</summary>
public sealed class SettingsFileStoreTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "Martlet.SettingsFileStore", Guid.NewGuid().ToString("N"));

    public SettingsFileStoreTests() => Directory.CreateDirectory(directory);

    public void Dispose()
    {
        try { Directory.Delete(directory, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public void Every_registered_store_exists_and_names_its_file()
    {
        var assemblies = new[] { typeof(MainWindow).Assembly }.Concat(typeof(MainWindow).Assembly.GetReferencedAssemblies()
            .Where(name => name.Name?.StartsWith("Martlet.", StringComparison.Ordinal) == true).Select(Assembly.Load)).ToArray();
        foreach (var file in SettingsFiles.All)
        {
            var store = assemblies.Select(a => a.GetType(file.Store)).FirstOrDefault(type => type is not null);
            Assert.True(store is not null, $"{file.FileName}: there is no store type {file.Store}.");
            var names = store!.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)
                .Where(field => field.IsLiteral && field.FieldType == typeof(string)).Select(field => (string?)field.GetRawConstantValue());
            Assert.True(names.Contains(Path.GetFileName(file.FileName)), $"{file.Store} has no constant with the file name {file.FileName}.");
        }
        Assert.Equal(TalkPreferences.FileName, SettingsFiles.TalkPreferences.FileName);
    }

    [Fact]
    public void A_missing_file_gives_the_defaults()
    {
        var file = TalkPreferences.Read(directory);
        Assert.Equal(SettingsFileState.Missing, file.State);
        Assert.Equal(new TalkPreferences(), file.Preferences);
        Assert.Null(file.AsSaved);
        Assert.Equal("default (no talk-preferences.json)", file.Source(nameof(TalkPreferences.ScreenSummary)));
        Assert.Equal(SettingsFileState.Missing, TalkPreferences.Read(null).State);
    }

    [Fact]
    public void A_saved_file_reads_as_saved_and_names_where_each_value_came_from()
    {
        Assert.True((new TalkPreferences() with { ScreenSummary = false, QuickSounds = true, VoiceVolume = 0.25 }).Save(directory));
        var file = TalkPreferences.Read(directory);
        Assert.Equal(SettingsFileState.Loaded, file.State);
        Assert.False(file.Preferences.ScreenSummary);
        Assert.True(file.Preferences.QuickSounds);
        Assert.Equal(0.25, file.Preferences.VoiceVolume);
        Assert.Equal(TalkPreferences.Load(directory), file.Preferences);
        Assert.Equal("talk-preferences.json", file.Source(nameof(TalkPreferences.ScreenSummary)));
    }

    [Fact]
    public void An_older_file_is_updated_and_keeps_its_own_values_as_saved()
    {
        File.WriteAllText(TalkPreferences.PathIn(directory), """{"Version":2,"BargeIn":true,"HearVoice":false,"HandsFree":false}""");
        var file = TalkPreferences.Read(directory);
        Assert.False(file.Preferences.BargeIn);
        Assert.Null(file.Preferences.HearVoice);
        Assert.False(file.Preferences.HandsFree);
        Assert.True(file.AsSaved!.BargeIn);
        Assert.Equal(2, file.AsSaved.Version);
        Assert.True(file.Has(nameof(TalkPreferences.BargeIn)));
        Assert.False(file.Has(nameof(TalkPreferences.ScreenSummary)));
        Assert.Equal("default", file.Source(nameof(TalkPreferences.ScreenSummary)));
    }

    [Fact]
    public void An_unreadable_file_gives_the_defaults_and_says_why()
    {
        File.WriteAllText(TalkPreferences.PathIn(directory), "{ not json");
        var file = TalkPreferences.Read(directory);
        Assert.Equal(SettingsFileState.Unreadable, file.State);
        Assert.Equal(new TalkPreferences(), file.Preferences);
        Assert.StartsWith("Json", file.Problem);
        Assert.Equal("default (talk-preferences.json unreadable)", file.Source(nameof(TalkPreferences.QuickSounds)));
    }
}
