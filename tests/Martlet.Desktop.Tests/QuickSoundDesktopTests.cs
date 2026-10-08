using System.IO;
using Martlet.Conversation;
using Martlet.Desktop;
using Martlet.Providers.Tests;

namespace Martlet.Desktop.Tests;

/// <summary>Companion › Voice › Quick sounds while Martlet thinks, through the desktop's conversation controller: the clips are
/// made with the reply's own voice (never automatically with a paid cloud voice), and a slow spoken reply gets one in front of
/// its own voice while a fast one gets none.</summary>
public sealed class QuickSoundDesktopTests
{
    [Fact]
    public async Task A_paid_cloud_voice_makes_quick_sounds_only_on_the_owners_click()
    {
        await using var fixture = await LiveFixture.Create();
        var controller = fixture.Controller;
        Assert.Equal(QuickSoundState.Off, controller.QuickSoundStatus.State);
        controller.QuickSounds = QuickSoundOptions.Of(true, 700);
        // The fixture's voice is OpenAI's, a paid cloud voice: nothing is made until the owner clicks.
        var waiting = controller.QuickSoundStatus;
        Assert.Equal(QuickSoundState.NeedsClick, waiting.State);
        Assert.Contains("OpenAI", waiting.Voice);
        Assert.Contains("paid cloud voice", MainWindow.QuickSoundsText(true, waiting, 700));
        Assert.Equal(0, fixture.Tts.Calls);

        Assert.True(controller.MakeQuickSounds());
        await fixture.Advance(() => controller.QuickSoundStatus.State is QuickSoundState.Ready or QuickSoundState.Failed);
        var ready = controller.QuickSoundStatus;
        Assert.Equal(QuickSoundState.Ready, ready.State);
        Assert.Null(ready.Problem);
        // One short request per quick sound, each through the reply's own voice and its one-use permission (the fixture checks
        // the key and host of every request).
        Assert.Equal(QuickSoundPhrases.Base.Count, ready.Clips);
        Assert.Equal(QuickSoundPhrases.Base.Count, fixture.Tts.Calls);
        Assert.StartsWith("On: 4 quick sounds in OpenAI", MainWindow.QuickSoundsText(true, ready, 700));
        // Nothing played while they were made.
        Assert.Equal(0, fixture.Output.Opens);

        // Turned off and on again, the same voice keeps its clips for this run.
        controller.QuickSounds = QuickSoundOptions.Off;
        Assert.Equal(QuickSoundState.Off, controller.QuickSoundStatus.State);
        Assert.Equal("Off.", MainWindow.QuickSoundsText(false, controller.QuickSoundStatus, 700));
    }

    [Fact]
    public async Task A_slow_spoken_reply_gets_a_quick_sound_in_front_and_a_fast_one_none()
    {
        await using var fixture = await LiveFixture.Create();
        var controller = fixture.Controller;
        // A long delay first, so the fast reply is fast however busy the test machine is.
        controller.QuickSounds = QuickSoundOptions.Of(true, 1500);
        Assert.True(controller.MakeQuickSounds());
        await fixture.Advance(() => controller.QuickSoundStatus.State == QuickSoundState.Ready);

        var fast = fixture.Start("Hello?", voice: true);
        await fixture.Finish(fast);
        Assert.Equal(ConversationState.Completed, fast.Turn!.Snapshot.State);
        Assert.Null(fast.Turn.Snapshot.Timings!.QuickSoundAfter);
        Assert.Equal(1, fixture.Output.Opens);

        controller.QuickSounds = QuickSoundOptions.Of(true, 700);
        var answer = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Llm.Respond = async (_, token) =>
        {
            await answer.Task.WaitAsync(token);
            return TextRecordingHandler.Sse(string.Concat(TextFixtures.Trace()));
        };
        var slow = fixture.Start("And now?", voice: true);
        // The quick sound plays on its own run while the reply has no audio yet.
        await fixture.Advance(() => fixture.Output.Opens == 2);
        Assert.Null(slow.Turn!.Snapshot.FirstAudioAfter);
        answer.SetResult();
        await fixture.Finish(slow);
        var result = slow.Turn.Snapshot;
        Assert.Equal(ConversationState.Completed, result.State);
        Assert.Equal("Hello fixture.", slow.Turn.Content.Text);
        Assert.Equal(3, fixture.Output.Opens);
        var quick = result.Timings!.QuickSoundAfter!.Value;
        Assert.InRange(quick.TotalMilliseconds, 700, 1000);
        Assert.True(result.FirstAudioAfter > quick);
    }

    [Fact]
    public void Quick_sound_choices_are_saved_on_this_pc_and_a_strange_delay_reads_as_the_default()
    {
        var directory = Path.Combine(Path.GetTempPath(), "Martlet.Talk." + Guid.NewGuid().ToString("N"));
        try
        {
            Assert.False(new TalkPreferences().QuickSounds);
            Assert.False(new TalkPreferences().QuickSoundOptions.Enabled);
            Assert.True(new TalkPreferences(QuickSounds: true, QuickSoundDelayMs: 1000).Save(directory));
            var read = TalkPreferences.Load(directory);
            Assert.True(read.QuickSounds);
            Assert.Equal(TimeSpan.FromSeconds(1), read.QuickSoundOptions.Delay);
            Assert.True((read with { QuickSoundDelayMs = 123 }).Save(directory));
            Assert.Equal(700, TalkPreferences.Load(directory).QuickSoundDelayMs);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }
}
