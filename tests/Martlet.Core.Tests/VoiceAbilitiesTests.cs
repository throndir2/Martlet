using Martlet.Core.Planning;
using Martlet.Core.Settings;

namespace Martlet.Core.Tests;

public sealed class VoiceAbilitiesTests
{
    private static string Rundown(SpeechEngine engine) => engine.Abilities.Describe();

    [Fact]
    public void Every_voice_has_the_three_items_in_order()
    {
        var all = SpeechEngines.All.Select(engine => engine.Abilities).Append(VoiceAbilities.WindowsVoice).Append(VoiceAbilities.OpenAiVoice);
        foreach (var abilities in all)
            Assert.Equal(["Voice cloning", "Laughs & sighs", "Emotions"], abilities.Items.Select(item => item.Name));
    }

    [Fact]
    public void Chatterbox_turbo_laughs_and_sighs_but_only_whispers_for_emotions()
    {
        Assert.Equal("Voice cloning: yes. Laughs & sighs: yes. Emotions: whispering only.", Rundown(SpeechEngines.Chatterbox));
        var abilities = SpeechEngines.Chatterbox.Abilities;
        var emotions = abilities.Items[2];
        Assert.Equal(AbilityLevel.Partly, emotions.Level);
        // The tones it reads but doesn't perform ([angry], [happy]...) are not offered as emotions.
        Assert.Equal(["[whispering]"], abilities.TagsFor(emotions, SpeechEngines.Chatterbox.Tags).Select(tag => tag.Text));
        Assert.Equal(9, abilities.TagsFor(abilities.Items[1], SpeechEngines.Chatterbox.Tags).Count);
    }

    [Fact]
    public void Other_engines_clone_and_dia_laughs_but_none_has_emotions()
    {
        Assert.Equal("Voice cloning: yes. Laughs & sighs: yes. Emotions: no.", Rundown(SpeechEngines.Dia));
        foreach (var engine in new[] { SpeechEngines.F5, SpeechEngines.Xtts, SpeechEngines.GptSovits })
            Assert.Equal("Voice cloning: yes. Laughs & sighs: no. Emotions: no.", Rundown(engine));
        Assert.Contains("tone of your recording", SpeechEngines.F5.Abilities.Items[2].Help);
    }

    [Fact]
    public void Windows_and_openai_voices_have_none()
    {
        foreach (var abilities in new[] { VoiceAbilities.WindowsVoice, VoiceAbilities.OpenAiVoice })
        {
            Assert.Equal("Voice cloning: no. Laughs & sighs: no. Emotions: no.", abilities.Describe());
            Assert.All(abilities.Items, item => Assert.Empty(abilities.TagsFor(item, SpeechEngines.Chatterbox.Tags)));
        }
    }

    [Fact]
    public void Chips_no_longer_repeat_the_rundown_or_the_graphics_card()
    {
        foreach (var engine in SpeechEngines.All)
            Assert.DoesNotContain(engine.Features, feature => feature is "Voice cloning" or "Laughs & sighs" or "Emotions" || feature.Contains("GPU"));
    }

    [Fact]
    public void Each_voice_says_where_it_runs_and_how_much_graphics_memory_it_takes()
    {
        Assert.Equal("Runs on an NVIDIA GPU: about 3.7 GB of graphics memory, up to 4.2 GB (6 GB+ card).", SpeechEngines.Chatterbox.RunsOn);
        Assert.Equal("Runs on an NVIDIA GPU: about 4.4 GB of graphics memory, up to 9.8 GB (8 GB+ card).", SpeechEngines.Dia.RunsOn);
        Assert.Equal("Runs on the CPU: no graphics card needed.",
            FootprintCatalog.Default.Find(FootprintCatalog.WindowsVoiceId)!.WhereItRuns);
        Assert.Equal("Runs online: nothing runs on your computers.",
            FootprintCatalog.Default.Find(FootprintCatalog.OpenAiVoiceId)!.WhereItRuns);
        // The line's smallest card is the one the engine row checks this PC against; Nano also runs without one.
        foreach (var engine in SpeechEngines.All.Where(engine => engine.NeedsGpu))
        {
            Assert.Equal(engine.MinimumGpuMemoryGb, engine.Footprint!.MinGpuGb);
            Assert.StartsWith("Runs on an NVIDIA GPU: about ", engine.RunsOn);
            Assert.EndsWith("card).", engine.RunsOn);
        }
        Assert.False(SpeechEngines.ChatterboxNano.NeedsGpu);
        Assert.Matches(@"^Runs on an NVIDIA GPU: about [\d.]+ GB of graphics memory, up to [\d.]+ GB \(4 GB\+ card\); without one, on the CPU\.$",
            SpeechEngines.ChatterboxNano.RunsOn);
    }

    [Fact]
    public void The_chatterbox_family_says_what_each_model_can_do()
    {
        Assert.Equal("Voice cloning: yes. Laughs & sighs: yes. Emotions: whispering only.", Rundown(SpeechEngines.ChatterboxNano));
        Assert.Equal("Voice cloning: yes. Laughs & sighs: no. Emotions: calm or expressive.", Rundown(SpeechEngines.ChatterboxOriginal));
        var original = SpeechEngines.ChatterboxOriginal.Abilities;
        Assert.Equal(["[expressive]", "[whispering]"], original.TagsFor(original.Items[2], SpeechEngines.ChatterboxOriginal.Tags).Select(t => t.Text));
        Assert.Equal(["chatterbox", "chatterbox-original", "chatterbox-nano"], SpeechEngines.All.Take(3).Select(e => e.Key));
        Assert.All(SpeechEngines.All.Take(3), engine => Assert.True(SpeechEngines.IsChatterbox(engine)));
        Assert.False(SpeechEngines.IsChatterbox(SpeechEngines.F5));
    }

    [Fact]
    public void Chatterbox_style_starts_with_resembles_tips_saves_and_refuses_values_out_of_range()
    {
        Assert.Equal(new ChatterboxStyle(0.5, 0.5, 0.7, 0.3), ChatterboxStyle.Default);
        Assert.Equal("General: exaggeration 0.5, CFG weight 0.5. Expressive: exaggeration 0.7, CFG weight 0.3.", ChatterboxStyle.Default.Describe());
        var directory = Path.Combine(Path.GetTempPath(), "martlet-style-" + Guid.NewGuid().ToString("N"));
        try
        {
            Assert.Equal(ChatterboxStyle.Default, ChatterboxStyle.Load(directory));
            var mine = new ChatterboxStyle(0.4, 0.6, 1.2, 0.2);
            mine.Save(directory);
            Assert.Equal(mine, ChatterboxStyle.Load(directory));
            Assert.Throws<ArgumentOutOfRangeException>(() => (mine with { GeneralCfgWeight = 1.5 }).Save(directory));
            // A damaged or out-of-range file reads as the defaults.
            File.WriteAllText(Path.Combine(directory, ChatterboxStyle.FileName),
                "{\"general\":{\"exaggeration\":5,\"cfgWeight\":0.5},\"expressive\":{\"exaggeration\":0.7,\"cfgWeight\":0.3}}");
            Assert.Equal(ChatterboxStyle.Default, ChatterboxStyle.Load(directory));
            File.WriteAllText(Path.Combine(directory, ChatterboxStyle.FileName), "not json");
            Assert.Equal(ChatterboxStyle.Default, ChatterboxStyle.Load(directory));
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
        Assert.Equal("{\"general\":{\"exaggeration\":0.5,\"cfg_weight\":0.5},\"expressive\":{\"exaggeration\":0.7,\"cfg_weight\":0.3}}",
            ChatterboxStyle.Default.Wire().ToJsonString());
    }

    [Theory]
    [InlineData(EmotionSupport.Tags, AbilityLevel.Yes, "Emotions: yes")]
    [InlineData(EmotionSupport.Intensity, AbilityLevel.Partly, "Emotions: calm or expressive")]
    [InlineData(EmotionSupport.None, AbilityLevel.No, "Emotions: no")]
    public void Emotion_levels_read_plainly(EmotionSupport support, AbilityLevel level, string text)
    {
        var emotions = new VoiceAbilities(true, false, support).Items[2];
        Assert.Equal(level, emotions.Level);
        Assert.Equal(text, emotions.ToString());
    }
}
