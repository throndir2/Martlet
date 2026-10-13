namespace Martlet.Conversation.Tests;

public sealed class ThinkingJobKindsTests
{
    [Fact]
    public void Every_kind_has_exactly_one_row()
    {
        Assert.Equal(Enum.GetValues<ThinkingJobKind>().Order(), ThinkingJobKinds.Rows.Select(row => row.Kind).Order());
        Assert.All(ThinkingJobKinds.Rows, row =>
        {
            Assert.False(string.IsNullOrWhiteSpace(row.Label));
            Assert.False(string.IsNullOrWhiteSpace(row.TypeName));
            Assert.True(row.Needs.HasFlag(ThinkingCapability.Text));
            Assert.Same(row, ThinkingJobKinds.Row(row.Kind));
        });
    }

    [Fact]
    public void Every_helper_job_runs_as_exactly_one_kind()
    {
        var helpers = ThinkingJobKinds.Rows.SelectMany(row => row.Helpers).Select(helper => helper.Kind).ToArray();
        Assert.Equal(Enum.GetValues<HelperJobKind>().Order(), helpers.Order());
        Assert.Equal(ThinkingJobKind.Memory, ThinkingJobKinds.Helper(HelperJobKind.MemoryBrief).Pool.Kind);
        Assert.Equal(ThinkingJobKind.Naming, ThinkingJobKinds.Helper(HelperJobKind.Temperament).Pool.Kind);
        Assert.Equal(ThinkingJobKind.TouchZones, ThinkingJobKinds.Helper(HelperJobKind.Eyes).Pool.Kind);
        Assert.Equal(ThinkingJobKind.TouchZones, ThinkingJobKinds.Helper(HelperJobKind.CharacterWriting).Pool.Kind);
    }

    [Fact]
    public void The_saved_keys_never_change_and_share_one_format()
    {
        // thinking-pool.json (Runs on), the status files and the automation IDs (ThinkingPoolRunsOn-<key>) keep these keys: a
        // change needs a migration of the saved files.
        Assert.Equal(["barge-in-judge", "end-of-turn-judge", "digest", "think-longer", "touch-zones", "memory", "naming", "research", "check-in"],
            ThinkingJobKinds.All.Select(ThinkingJobKinds.Name));
        Assert.All(ThinkingJobKinds.All, kind => Assert.Equal(kind, ThinkingJobKinds.Parse(ThinkingJobKinds.Name(kind))));
        Assert.Null(ThinkingJobKinds.Parse("touch_zones"));
        var keys = ThinkingJobKinds.Rows.Select(row => row.Key).Concat(ThinkingJobKinds.Rows.SelectMany(row => row.Helpers).Select(h => h.Key)).ToArray();
        Assert.All(keys, key => Assert.Matches("^[a-z]+(-[a-z]+)*$", key));
        Assert.Equal("touch-zones", ThinkingJobKinds.Helper(HelperJobKind.TouchZones).Helper.Key);
        Assert.Equal(ThinkingJobKinds.Rows.Count, ThinkingJobKinds.Rows.Select(row => row.Key).Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void The_rows_keep_the_rules_the_lists_had()
    {
        Assert.Equal([ThinkingJobKind.BargeInJudge, ThinkingJobKind.EndOfTurnJudge, ThinkingJobKind.Digest],
            ThinkingJobKinds.All.Where(ThinkingJobKinds.IsFast));
        Assert.Equal(
            [ThinkingPriority.BargeInJudge, ThinkingPriority.EndOfTurnJudge, ThinkingPriority.Digest, ThinkingPriority.ThinkLonger,
                ThinkingPriority.TouchZones, ThinkingPriority.Helper, ThinkingPriority.Helper, ThinkingPriority.Research, ThinkingPriority.Helper],
            ThinkingJobKinds.All.Select(ThinkingJobKinds.Priority));
        Assert.Equal([ThinkingJobKind.BargeInJudge, ThinkingJobKind.EndOfTurnJudge], ThinkingJobKinds.All.Where(LiveFloorRules.ServesTheTurn));
        Assert.Equal([ThinkingJobKind.Digest, ThinkingJobKind.ThinkLonger, ThinkingJobKind.Memory, ThinkingJobKind.Naming,
            ThinkingJobKind.Research, ThinkingJobKind.CheckIn], ThinkingJobKinds.All.Where(LiveFloorRules.Stops));
        Assert.Equal(ThinkingPriority.Research, ThinkingJobKinds.Helper(HelperJobKind.MemoryBrief).Helper.Priority);
        Assert.Equal(ThinkingPriority.Helper, ThinkingJobKinds.Helper(HelperJobKind.Eyes).Helper.Priority);
        Assert.Equal([HelperJobKind.TouchZones, HelperJobKind.CharacterWriting],
            ThinkingJobKinds.Rows.SelectMany(row => row.Helpers).Where(h => h.OwnerWaits).Select(h => h.Kind).Order());
    }

    [Fact]
    public void Each_label_names_all_the_work_its_kind_controls()
    {
        Assert.Contains("sound", ThinkingJobKinds.Row(ThinkingJobKind.Digest).Label, StringComparison.Ordinal);
        Assert.Contains("eyes", ThinkingJobKinds.Row(ThinkingJobKind.TouchZones).Label, StringComparison.Ordinal);
        Assert.Contains("temperament", ThinkingJobKinds.Row(ThinkingJobKind.Naming).Label, StringComparison.Ordinal);
        Assert.Contains("memory brief", ThinkingJobKinds.Row(ThinkingJobKind.Memory).Label, StringComparison.Ordinal);
    }
}
