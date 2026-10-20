using Martlet.Core.Cluster;
using Martlet.Core.Reading;
using Martlet.HostClient;

namespace Martlet.Desktop.Tests;

/// <summary>Companion › Reading's page model (Pages\Reading), without WPF.</summary>
public sealed class ReadingPageModelTests
{
    [Fact]
    public void Now_says_what_reads_or_that_reading_is_off()
    {
        var empty = new PoolList { Area = PoolAreas.Reading.Id };
        Assert.StartsWith("Off: ", ReadingPageModel.Now(empty), StringComparison.Ordinal);
        var windows = ReadingPool.Windows();
        Assert.Equal($"Martlet reads the text on your screen with {ReadingPool.Describe(windows)} while it watches.",
            ReadingPageModel.Now(empty.With(windows)));
        Assert.EndsWith("the next of the 2 in the list reads.", ReadingPageModel.Now(empty.With(windows).With(PoolMember.Computer("studio"))),
            StringComparison.Ordinal);
        Assert.Equal(ScreenReadPace.ChoicesMs.Count(), ReadingPageModel.EveryNames.Length);
    }

    [Fact]
    public void Windows_OCR_state_and_a_new_This_PC_follow_whether_Windows_can_read()
    {
        var services = new FakeServices { WindowsReads = false };
        var model = new ReadingPageModel(services);
        Assert.Equal("Windows OCR, but Windows has no text recognition language here, so the next one reads", model.MemberStatus(ReadingPool.Windows()));
        Assert.Equal(WindowsScreenTextReader.NoLanguage, model.WindowsStateText);
        Assert.True(ReadingPool.UsesWindows(model.NewMember(PoolMember.ThisPc())));
        services.WindowsReads = true;
        Assert.Equal("reads with Windows OCR on this PC's processor; nothing leaves this PC", model.MemberStatus(ReadingPool.Windows()));
    }

    [Fact]
    public void An_unpaired_computer_cannot_be_set_up_and_a_card_without_NVIDIA_falls_back_to_the_processor_model()
    {
        var services = new FakeServices();
        var model = new ReadingPageModel(services);
        var gpuModel = OptionalExtras.ReadingModels.First(m => m.NeedsNvidia);
        var member = PoolMember.Computer("studio").WithSetting(PoolSettingKeys.Model, gpuModel.Key);
        var role = model.Role(member);
        Assert.Equal("studio", role.Place);
        Assert.Equal("studio isn't paired with this PC.", role.Cannot);
        Assert.Equal(role.Cannot, role.State);
        Assert.True(role.Warning);
        Assert.Equal(gpuModel, role.Chosen);
        Assert.Equal("studio isn't paired with this PC", model.MemberStatus(member));

        var thisPc = ReadingPool.ThisPcRole().WithSetting(PoolSettingKeys.Model, gpuModel.Key);
        services.ThisPcHasNvidia = false;
        var here = model.Role(thisPc);
        Assert.Equal(MainWindow.ReadingThisPc, here.Place);
        Assert.Null(here.Cannot);
        Assert.Equal(OptionalExtras.ReadingModels[0], here.Chosen);
        Assert.Equal("Not set up on this PC yet.", here.State);
        Assert.Equal("Needs an NVIDIA graphics card; this PC has none.", ReadingPageModel.ModelBlocked(here, gpuModel));
        Assert.Null(ReadingPageModel.ModelBlocked(here, OptionalExtras.ReadingModels[0]));
        Assert.EndsWith(" (recommended)", ReadingPageModel.ModelLabel(OptionalExtras.ReadingModels[0]), StringComparison.Ordinal);
        foreach (var each in OptionalExtras.ReadingModels) Assert.Contains(each.Download, ReadingPageModel.ModelNote(each));
    }

    private sealed class FakeServices : IReadingPageServices
    {
        public string? DataDirectory => null;
        public TalkPreferences Talk { get; set; } = new();
        public bool? WindowsReads { get; set; }
        public bool WindowsChecking => false;
        public string? Pending => null;
        public string? Failure(string place) => null;
        public string? PickedModel(string place) => null;
        public bool Testing => false;
        public string? TestState => null;
        public string? TestText => null;
        public bool Comparing(string id) => false;
        public PairedHost? ThisPcHost() => null;
        public PairedHost? FindHost(string? hostId) => null;
        public PairedHost? FindSharedHost(string? hostId) => null;
        public HostCheck? Check(string hostId) => null;
        public bool Offers(PairedHost host, string kind) => false;
        public string? CannotHand(string hostId, string roleKind, string job) => null;
        public bool? HostHasNvidia(string hostId) => null;
        public bool? ThisPcHasNvidia { get; set; }
        public void CheckWindows() { }
        public void SaveTalk(TalkPreferences next, bool render = false) => Talk = next;
        public void SaveMember(PoolMember next, string done) { }
        public void PickModel(string place, string modelKey) { }
        public void ToggleCompare(string id) { }
        public void SetUp(PairedHost? host, OptionalExtras.ReadingRoleModel model, bool changing) { }
        public void ReadScreenNow() { }
        public void Redraw() { }
    }
}
