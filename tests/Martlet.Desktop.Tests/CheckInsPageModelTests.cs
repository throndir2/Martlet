using Martlet.Conversation;
using Martlet.Core.Settings;

namespace Martlet.Desktop.Tests;

public sealed class CheckInsPageModelTests
{
    [Fact]
    public void A_built_in_card_keeps_only_what_differs_from_Martlets_own()
    {
        var standard = CheckIns.BuiltIn.First();
        var edit = new CheckInsPageModel.CheckInEdit(standard.Facts, standard.Conditions, standard.Outcome, ThinkingCapability.Text,
            standard.Screenshot, standard.Recording, standard.RecordingSeconds, standard.Script ?? "", standard.ToolSets)
        {
            Triggers = standard.Triggers, FromHour = standard.FromHour, UntilHour = standard.UntilHour, MostPerHour = standard.MostPerHour
        };

        var same = CheckInsPageModel.BuiltInChoice(standard.Id, on: false, everyIndex: 0, edit);
        Assert.Equal(new CheckInChoice(false, CheckIns.EveryChoices[0]), same);

        var changed = CheckInsPageModel.BuiltInChoice(standard.Id, on: true, everyIndex: 0,
            edit with { Facts = standard.Facts ^ CheckInFacts.Presence });
        Assert.Equal(standard.Facts ^ CheckInFacts.Presence, changed.Facts);
        Assert.Null(changed.Conditions);
        Assert.Null(changed.Outcome);
    }

    [Fact]
    public void Your_own_row_names_an_unnamed_check_in_and_keeps_its_task_on_one_line_ending()
    {
        var edit = CheckInsPageModel.EditOf(new CustomCheckIn { Id = "c1", Name = "x" });

        var row = CheckInsPageModel.CustomOf("c1", "   ", on: true, everyIndex: 0, "First line\r\nSecond line", edit);

        Assert.Equal("My check-in", row.Name);
        Assert.Equal("First line\nSecond line", row.Task);
        Assert.Equal(CheckIns.EveryChoices[0], row.EveryMinutes);
        Assert.True(row.On);
        Assert.Equal("1 hour", CheckInsPageModel.EveryText(60));
        Assert.Equal("1 minute", CheckInsPageModel.EveryText(1));
    }
}
