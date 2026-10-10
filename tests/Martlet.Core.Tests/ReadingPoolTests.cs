using Martlet.Core.Cluster;
using Martlet.Core.Reading;

namespace Martlet.Core.Tests;

public sealed class ReadingPoolTests
{
    private static string[] Keys(PoolList list) => [.. list.Members.Select(m => m.Key + (m.Setting(PoolSettingKeys.Engine) is { } e ? "=" + e : ""))];

    [Fact]
    public void The_older_choice_becomes_the_list()
    {
        Assert.Equal(["this-pc=windows-ocr"], Keys(ReadingPool.FromChoice(new ReadingSettings(), "desk-host", ["m3-host"])));
        Assert.Empty(ReadingPool.FromChoice(new ReadingSettings { Place = ReadingPlace.Off }, "desk-host", ["m3-host"]).Members);
        Assert.Equal(["host:gpu-pc", "this-pc=ocr", "host:m3-host"],
            Keys(ReadingPool.FromChoice(new ReadingSettings { Place = ReadingPlace.Host, HostId = "gpu-pc" }, "desk-host", ["desk-host", "m3-host", "gpu-pc"])));
        Assert.Equal(["this-pc=ocr"], Keys(ReadingPool.FromChoice(new ReadingSettings { Place = ReadingPlace.Host, HostId = "desk-host" }, "desk-host", [])));
    }

    [Fact]
    public void The_first_member_on_is_the_older_choice_for_the_recommended_setup()
    {
        PoolList List(params PoolMember[] members) => new() { Area = PoolAreas.Reading.Id, Members = members };
        Assert.Equal(ReadingPlace.Off, ReadingPool.Choice(List(), "desk-host").Place);
        Assert.Equal(ReadingPlace.Off, ReadingPool.Choice(List(ReadingPool.Windows() with { Off = true }), "desk-host").Place);
        Assert.Equal(ReadingPlace.ThisPc, ReadingPool.Choice(List(PoolMember.ThisPc()), "desk-host").Place);
        Assert.Equal(new ReadingSettings { Place = ReadingPlace.Host, HostId = "desk-host" },
            ReadingPool.Choice(List(ReadingPool.Windows() with { Off = true }, ReadingPool.ThisPcRole()), "desk-host"));
        Assert.Equal("gpu-pc", ReadingPool.Choice(List(PoolMember.Gpu("gpu-pc", 2)), null).HostId);
    }

    [Fact]
    public void Reads_go_to_the_members_on_in_order_each_computer_once()
    {
        var list = new PoolList
        {
            Area = PoolAreas.Reading.Id,
            Members =
            [
                PoolMember.Computer("gpu-pc"), PoolMember.Gpu("gpu-pc", 2), ReadingPool.Windows(), PoolMember.Computer("off-pc") with { Off = true },
                PoolMember.Computer("kept-pc") with { OnlyFor = ["desk-2"] }, PoolMember.Computer("nas-host"), PoolMember.Computer("desk-host")
            ]
        };
        var targets = ReadingPool.Targets(list, "desk-1", "desk-host", id => id is not "nas-host");
        Assert.Equal(["host:gpu-pc", "this-pc", "host:desk-host"], targets.Select(t => t.Key));
        Assert.Null(targets[1].HostId);

        // This PC with the role is its own host service; without one it can't read.
        var role = new PoolList { Area = PoolAreas.Reading.Id, Members = [ReadingPool.ThisPcRole(), PoolMember.Computer("desk-host")] };
        Assert.Equal(["host:desk-host"], ReadingPool.Targets(role, "desk-1", "desk-host", _ => true).Select(t => t.Key));
        Assert.Equal(["host:desk-host"], ReadingPool.Targets(role, "desk-1", null, _ => true).Select(t => t.Key));
        Assert.Equal("the Reading role on this PC", ReadingPool.Describe(ReadingPool.ThisPcRole()));
        Assert.Equal("Windows OCR on this PC", ReadingPool.Describe(PoolMember.ThisPc()));
    }

    [Fact]
    public void Each_pc_keeps_its_own_reading_list_and_an_empty_one_is_off()
    {
        Assert.False(PoolAreas.Reading.Shared);
        Assert.False(PoolAreas.Reading.Required);
        Assert.Equal("Off", PoolAreas.Reading.WhenEmpty);
        Assert.True(PoolRouting.Order(PoolAreas.Reading, new PoolList { Area = "reading" }, "desk-1").Off);
    }
}
