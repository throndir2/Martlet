using Martlet.Desktop;

namespace Martlet.Desktop.Tests;

public sealed class VoiceVolumeTests
{
    [Fact]
    public void Voice_volume_is_full_by_default_saved_on_this_pc_and_clamped_when_loaded()
    {
        Assert.Equal(1.0, new TalkPreferences().VoiceVolume);
        Assert.Equal(1.0, TalkPreferences.Load(null).VoiceVolume);
        var directory = Directory.CreateTempSubdirectory("martlet-volume-").FullName;
        try
        {
            Assert.Equal(1.0, TalkPreferences.Load(directory).VoiceVolume);
            Assert.True((new TalkPreferences() with { VoiceVolume = 0.35 }).Save(directory));
            Assert.Equal(0.35, TalkPreferences.Load(directory).VoiceVolume);
            File.WriteAllText(Path.Combine(directory, "talk-preferences.json"), "{\"VoiceVolume\":4}");
            Assert.Equal(1.0, TalkPreferences.Load(directory).VoiceVolume);
            File.WriteAllText(Path.Combine(directory, "talk-preferences.json"), "{\"VoiceVolume\":-1}");
            Assert.Equal(0.0, TalkPreferences.Load(directory).VoiceVolume);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Theory]
    [InlineData(1.0, "100%")]
    [InlineData(0.8, "80%")]
    [InlineData(0.0, "0%")]
    [InlineData(2.0, "100%")]
    public void Voice_volume_reads_as_a_percentage(double volume, string label) =>
        Assert.Equal(label, MainWindow.VoiceVolumeLabel(volume));

    [Fact]
    public async Task Singing_follows_the_voice_volume_clamped()
    {
        await using var singing = new ConversationSinging(null, null, null);
        Assert.Equal(1.0, singing.VoiceVolume);
        singing.VoiceVolume = 0.4;
        Assert.Equal(0.4, singing.VoiceVolume);
        singing.VoiceVolume = double.PositiveInfinity;
        Assert.Equal(1.0, singing.VoiceVolume);
    }
}
