using System.Globalization;
using Martlet.Desktop;

namespace Martlet.Desktop.Tests;

public sealed class DeviceCapacityTests
{
    private static readonly CapacityComponent Thinking = new("thinking", "Thinking", new(8, 4, 4, 10));
    private static readonly CapacityComponent Deep = new("deep", "Deep Thinking model", new(6, 2, 2, 20));
    private static readonly CapacitySpecs Gpu24 = new(24, 64, 16, 500);

    public DeviceCapacityTests() => CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;

    [Fact]
    public void Bars_add_up_each_components_share_and_the_headroom()
    {
        var bars = DeviceCapacity.Bars(Gpu24, [Thinking, Deep], new CapacityLive(9.5, null));
        var vram = bars.Single(b => b.Resource == CapacityResource.GraphicsMemory);
        Assert.Equal(14, vram.Planned);
        Assert.Equal(58, vram.PlannedPercent);
        Assert.Equal(10, vram.Headroom);
        Assert.Equal([33d, 25d], vram.Shares.Select(s => s.Percent));
        Assert.Equal("Graphics memory: 14 of 24 GB planned (58%), 10 GB free. In use now: 9.5 GB (40%).", vram.Text);
        Assert.Equal("Processor: 6 of 16 threads planned (38%), 10 threads free.", bars.Single(b => b.Resource == CapacityResource.Processor).Text);
        Assert.Equal("Thinking: 33% graphics memory, 6% memory, 25% processor, 2% disk.", DeviceCapacity.ShareText(Thinking, bars));
    }

    [Fact]
    public void An_over_full_or_unreported_device_says_so()
    {
        var small = new CapacitySpecs(8, null, 8, null);
        var bars = DeviceCapacity.Bars(small, [Thinking, Deep]);
        var vram = bars.Single(b => b.Resource == CapacityResource.GraphicsMemory);
        Assert.True(vram.Over);
        Assert.Equal(0, vram.Headroom);
        Assert.Equal("Graphics memory: 14 of 8 GB planned (175%), 6 GB more than it can give.", vram.Text);
        Assert.Equal("Memory: 6 GB planned; this device hasn't reported how much it has.", bars.Single(b => b.Resource == CapacityResource.Memory).Text);
        Assert.Equal("Disk: 30 GB planned; this device hasn't reported how much it has.", bars.Single(b => b.Resource == CapacityResource.Disk).Text);
        Assert.Equal("Graphics memory: nothing planned of 8 GB, all free.", DeviceCapacity.Bars(small, [])[0].Text);
    }

    [Fact]
    public void A_job_that_grows_while_it_works_shows_a_range_and_a_tight_card()
    {
        // Thinking holds 8 GB throughout; the voice usually holds 4 GB and grows to 6 GB while it speaks.
        var voice = new CapacityComponent("voice", "Voice (Dia)", new(6, 4, 1, 15), new(4, 3, 1, 15));
        var bars = DeviceCapacity.Bars(Gpu24, [Thinking, voice]);
        var vram = bars.Single(b => b.Resource == CapacityResource.GraphicsMemory);
        Assert.Equal(12, vram.Usual);
        Assert.Equal(14, vram.Planned);
        Assert.False(vram.Tight);
        Assert.Equal("Graphics memory: 12-14 of 24 GB planned (50-58%), 10 GB free.", vram.Text);
        Assert.Equal([(33d, 33d), (25d, 17d)], vram.Shares.Select(s => (s.Percent, s.UsualPercent)));
        Assert.Equal("Voice (Dia): 17-25% graphics memory, 5-6% memory, 6% processor, 3% disk.", DeviceCapacity.ShareText(voice, bars));

        // 12 GB for Martlet: what they usually hold fits, but not what they take at their busiest.
        var tight = DeviceCapacity.Bars(new CapacitySpecs(13, 64, 16, 500), [Thinking, voice], usable: new CapacitySpecs(12, 60, 14, 490));
        var card = tight.Single(b => b.Resource == CapacityResource.GraphicsMemory);
        Assert.True(card.Tight);
        Assert.False(card.Over);
        Assert.Equal("Graphics memory: 12-14 of 13 GB planned (92-108%), tight: at their busiest the jobs can need 2 GB more than it can give, " +
            "and slow down or fail.", card.Text);
        Assert.Equal("Left free: 52 GB memory, 9 processor threads, 465 GB disk space. " +
            "Tight on graphics memory: at their busiest the jobs can need more than it has.", DeviceCapacity.HeadroomText(tight));

        // On an 11 GB card even what they usually hold is too much.
        var over = DeviceCapacity.Bars(new CapacitySpecs(11, 64, 16, 500), [Thinking, voice]).Single(b => b.Resource == CapacityResource.GraphicsMemory);
        Assert.True(over.Over);
        Assert.False(over.Tight);
        Assert.Equal("Graphics memory: 12-14 of 11 GB planned (109-127%), 1-3 GB more than it can give.", over.Text);
    }

    [Fact]
    public void Also_fits_and_network_lines_read_plainly()
    {
        // Most important part first; a part one copy of is enough names what would run.
        Assert.Equal(["Room for Voice (Chatterbox Turbo) here.", "Room for 2 more Deep thinking models (Gemma 4 12B) here."],
            DeviceCapacity.AlsoFits([new("voice", "voice engine", "Voice (Chatterbox Turbo)", 3, Many: false),
                new("deep", "Deep thinking model", "Gemma 4 12B", 2), new("x", "Nothing", null, 0)], "here"));
        Assert.Equal("Your computers could also run another Thinking model (Gemma 4 E4B) and Voice (Chatterbox Turbo).",
            DeviceCapacity.NetworkFits([new("think", "Thinking model", "Gemma 4 E4B", 1), new("voice", "voice engine", "Voice (Chatterbox Turbo)", 7, Many: false)]));
        Assert.Equal("Your computers have no room for more right now; add a computer to run more.", DeviceCapacity.NetworkFits([]));
        Assert.Equal("Totals across 2 computers: 32 GB graphics memory, 96 GB memory, 24 processor threads, 500 GB free disk.",
            DeviceCapacity.Totals([Gpu24, new(8, 32, 8, null)]));
        Assert.Equal("On your computers: Thinking, Listening. Online: Speaking (ElevenLabs). Not set up: Lip-sync.",
            DeviceCapacity.Coverage(["Thinking", "Listening"], ["Speaking (ElevenLabs)"], ["Lip-sync"]));
        Assert.Equal("On your computers: Thinking. Every part of Martlet is covered.", DeviceCapacity.Coverage(["Thinking"], [], []));
    }
}
