using Martlet.Core.Pictures;

namespace Martlet.Desktop.Tests;

public sealed class BackgroundDutiesTests
{
    [Fact]
    public void The_singing_and_pictures_computers_are_kept_for_their_work()
    {
        var directory = Path.Combine(Path.GetTempPath(), "martlet-duties-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(directory);
            Assert.Empty(BackgroundDuties.Of(directory));
            Assert.Null(BackgroundDuties.Singer(directory));
            Assert.Null(BackgroundDuties.Painter(directory));

            new SingingPreferences(Host: "singer").Save(directory);
            Assert.True(new PicturesSettings { Place = PicturePlace.Host, HostId = "painter" }.Save(directory));
            var kept = BackgroundDuties.Of(directory);
            Assert.Equal([BackgroundDuties.Singing], kept["singer"]);
            Assert.Equal([BackgroundDuties.Pictures], kept["painter"]);
            // The same keys as Deep thinking's places on those computers, so a hold keeps thinks off them.
            Assert.Equal("host:singer", BackgroundDuties.Singer(directory)!.Id);
            Assert.Equal("host:painter", BackgroundDuties.Painter(directory)!.Id);

            // One computer that sings and draws is kept for both.
            Assert.True(new PicturesSettings { Place = PicturePlace.Host, HostId = "singer" }.Save(directory));
            Assert.Equal([BackgroundDuties.Singing, BackgroundDuties.Pictures], BackgroundDuties.Of(directory)["singer"]);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }
}
