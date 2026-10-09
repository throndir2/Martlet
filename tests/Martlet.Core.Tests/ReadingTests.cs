using Martlet.Core.Reading;

namespace Martlet.Core.Tests;

public sealed class ReadingTests
{
    [Fact]
    public void Lines_come_in_reading_order_without_blanks_or_repeats()
    {
        var lines = ScreenText.Order(
        [
            new("VICTORY", 380, 250, 240, 60),
            new("Score: 12450", 700, 38, 180, 28),
            new("HEALTH 87 / 100", 40, 40, 220, 26),
            new("  ", 10, 10, 5, 5),
            new("Score: 12450", 700, 300, 180, 28),
            new("Player2\u0007 eliminated Player5", 40, 516, 300, 18)
        ]);
        Assert.Equal(["HEALTH 87 / 100", "Score: 12450", "VICTORY", "Player2  eliminated Player5"], lines.Select(l => l.Text));
        Assert.Equal("HEALTH 87 / 100\nScore: 12450\nVICTORY\nPlayer2  eliminated Player5", ScreenText.Join(lines));
    }

    [Fact]
    public void Joined_text_is_cut_at_whole_lines()
    {
        var lines = Enumerable.Range(0, 10).Select(i => new ReadLine(new string('a', 30) + i, 0, i * 20, 100, 18)).ToList();
        var text = ScreenText.Join(lines, 70);
        Assert.Equal(2, text.Split('\n').Length - 1);
        Assert.EndsWith("\n…", text, StringComparison.Ordinal);
        Assert.True(text.Length <= 72);
        Assert.Equal(ScreenText.MaximumLineCharacters, ScreenText.Order([new(new string('x', 500), 0, 0, 10, 10)])[0].Text.Length);
    }

    [Fact]
    public void Text_change_counts_words_in_common()
    {
        Assert.Equal(0, ScreenText.Change(null, null));
        Assert.Equal(0, ScreenText.Change("HEALTH 87", "health  87!"));
        Assert.Equal(1, ScreenText.Change("", "VICTORY"));
        Assert.Equal(1, ScreenText.Change("HEALTH 87", "VICTORY"));
        Assert.Equal(2 / 3.0, ScreenText.Change("Score 12450", "Score 12500"), 3);
        Assert.Equal(["score", "12", "450"], ScreenText.Words("Score: 12,450"));
    }

    [Fact]
    public void Reading_settings_default_to_windows_ocr_on_this_pc_and_round_trip()
    {
        var directory = Directory.CreateTempSubdirectory("martlet-reading-").FullName;
        try
        {
            var (none, state) = ReadingSettings.Read(directory);
            Assert.Equal(("none", ReadingPlace.ThisPc, true), (state, none.Place, none.On));
            Assert.Equal("Windows OCR on this PC", none.Describe());

            var host = new ReadingSettings { Place = ReadingPlace.Host, HostId = "gpu-pc", ChosenAt = DateTimeOffset.UnixEpoch };
            Assert.True(host.Save(directory));
            Assert.Equal((host, "loaded"), ReadingSettings.Read(directory));
            Assert.Contains("\"Place\": \"Host\"", File.ReadAllText(Path.Combine(directory, ReadingSettings.FileName)), StringComparison.Ordinal);
            Assert.Equal("gpu-pc's Reading role", host.Describe());

            Assert.Throws<Martlet.Core.Contracts.ContractException>(() => new ReadingSettings { Place = ReadingPlace.ThisPc, HostId = "gpu-pc" }.Validate());
            File.WriteAllText(Path.Combine(directory, ReadingSettings.FileName), "{\"Place\":\"Somewhere\"}");
            Assert.Equal((ReadingPlace.ThisPc, "unreadable"), (ReadingSettings.Read(directory).Settings.Place, ReadingSettings.Read(directory).State));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public void Both_reading_engines_are_in_the_platform_catalog()
    {
        Assert.Equal("ocr", Martlet.Core.Platforms.PlatformCatalog.EngineForHostRole("ocr"));
        Assert.NotNull(Martlet.Core.Platforms.PlatformCatalog.Engine("windows-ocr"));
        Assert.True(Martlet.Core.Installation.SharedGpu.ProcessorOnly("ocr", "rapidocr-ppocrv4"));
        Assert.True(Martlet.Core.Installation.SharedGpu.ProcessorOnly("ocr", null));
        Assert.False(Martlet.Core.Installation.SharedGpu.ProcessorOnly("ocr", "ppocrv5-server"));
    }
}
