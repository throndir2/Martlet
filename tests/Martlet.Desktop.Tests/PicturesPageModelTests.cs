using Martlet.Core.Pictures;
using Martlet.Core.Cluster;
using Martlet.Providers.Pictures;

namespace Martlet.Desktop.Tests;

/// <summary>Companion › Picture making's page model (Pages\Pictures), without WPF.</summary>
public sealed class PicturesPageModelTests
{
    [Fact]
    public void NowSaysWherePicturesAreDrawnOrWhyTheyAreOff()
    {
        if (PictureClient.Fixture) return;
        var empty = new PoolList { Area = PoolAreas.PictureMaking.Id };
        Assert.StartsWith("Off: ", PicturesPageModel.Now(empty, []), StringComparison.Ordinal);
        Assert.EndsWith("until you add a place below.", PicturesPageModel.Now(empty, []), StringComparison.Ordinal);
        var thisPc = PoolMember.ThisPc();
        var withThisPc = empty.With(thisPc);
        Assert.Equal("Off: no place in the Picture making list is on and usable on this PC.", PicturesPageModel.Now(withThisPc, []));
        var computer = PoolMember.Computer("studio");
        Assert.Equal($"Martlet draws on {PictureMakingPoolMembers.Describe(thisPc)}.", PicturesPageModel.Now(withThisPc, [thisPc]));
        Assert.Equal($"Martlet draws on {PictureMakingPoolMembers.Describe(thisPc)}, or {PictureMakingPoolMembers.Describe(computer)} when it is busy (in this order).",
            PicturesPageModel.Now(withThisPc.With(computer), [thisPc, computer]));
    }
}
