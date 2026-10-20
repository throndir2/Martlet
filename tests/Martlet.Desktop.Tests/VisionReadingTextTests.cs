using Martlet.Core.Reading;
using Martlet.Core.Text;
using Xunit;

namespace Martlet.Desktop.Tests;

/// <summary>Vision's, Reading's and the cameras' status lines have one sentence each (#992, #1001): the reason and the next step
/// stay in that sentence, and the longer explanation is in the wiki.</summary>
public sealed class VisionReadingTextTests
{
    [Fact]
    public void Vision_choices_reading_paces_and_camera_errors_are_one_sentence()
    {
        foreach (var (_, title, detail) in VisionPageModel.Sources) Assert.True(Sentences.Count(detail) == 1, $"{title}: {detail}");
        foreach (var ms in ScreenReadPace.ChoicesMs)
        {
            var pace = ReadingPageModel.EveryText(ms);
            Assert.True(Sentences.Count(pace) == 1, pace);
        }
        foreach (var code in new[] { unchecked((int)0x80070005), unchecked((int)0xC00D3704), unchecked((int)0xC00D36C3), unchecked((int)0xC00D36B4),
                     unchecked((int)0x80072EE7), unchecked((int)0x80004005) })
        foreach (var camera in new[] { true, false })
        {
            var advice = MediaReader.Explain(code, camera);
            Assert.True(Sentences.Count(advice) == 1, advice);
        }
        Assert.Equal("Windows is blocking camera access: allow desktop apps to use the camera in Windows Settings.",
            MediaReader.Explain(unchecked((int)0x80070005), camera: true));
        Assert.Equal(1, Sentences.Count(WindowsScreenTextReader.NoLanguage));
        Assert.Contains("Language & region", WindowsScreenTextReader.NoLanguage);
    }

    [Fact]
    public void Reading_role_setup_terms_are_one_sentence_and_say_when_screenshots_leave_this_PC()
    {
        foreach (var model in OptionalExtras.ReadingModels)
        {
            var here = MainWindow.ReadingTerms(model, elsewhere: false);
            var there = MainWindow.ReadingTerms(model, elsewhere: true);
            Assert.True(Sentences.Count(here) == 1, here);
            Assert.True(Sentences.Count(there) == 1, there);
            Assert.Contains(model.Download, here);
            Assert.DoesNotContain("screenshots", here);
            Assert.Contains("this PC sends it screenshots that are read in memory and not kept", there);
            Assert.Equal(model.NeedsNvidia, here.Contains("driver 580 or newer", StringComparison.Ordinal));
        }
    }
}
