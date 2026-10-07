using System.Globalization;
using Martlet.Core.Installation;
using Martlet.Core.Planning;
using Martlet.Desktop;

namespace Martlet.Desktop.Tests;

public sealed class DeviceCapacityInputsTests
{
    // Fixed numbers so the tests don't move when the footprint catalog's seed numbers are refined.
    private static readonly FootprintCatalog Catalog = new(
    [
        new ComponentOption
        {
            Id = "think:e4b", Component = PlanComponent.Thinking, DisplayName = "Gemma 4 E4B", ModelId = "gemma4:e4b", HostRoleKind = "ollama",
            Gpu = GpuRequirement.AnyGpu, Steady = new(6, 2, 2, 5), Peak = new(6, 2, 2, 5), QualityTier = 2
        },
        new ComponentOption
        {
            Id = "deep:12b", Component = PlanComponent.DeepThinking, DisplayName = "Gemma 4 12B (deep thinking)", ModelId = "gemma4:12b",
            HostRoleKind = "deep-thinking", Gpu = GpuRequirement.AnyGpu, Steady = new(8, 2, 2, 8), Peak = new(8, 2, 2, 8), QualityTier = 3
        },
        new ComponentOption
        {
            Id = "character", Component = PlanComponent.Character, DisplayName = "Live2D or VRM character", RunsInApp = true,
            Steady = new(0, 0.5, 0.5, 0.2), Peak = new(0, 0.5, 0.5, 0.2)
        }
    ]);

    public DeviceCapacityInputsTests() => CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;

    private static (NetworkInputs Inputs, List<NetworkNode> Nodes) Network()
    {
        var machine = new MachineInfo("DESK", "Windows 11", "CPU", 8, 16, [], null, false, false);
        var hardware = new HostHardware("gpu-box", "https://192.168.1.20:8443", DateTimeOffset.Now, DateTimeOffset.Now, "docker", "Ubuntu 24.04",
            null, "Ryzen", 32, 64, "docker", "yes", [new HostGpu("NVIDIA GeForce RTX 5090", "nvidia", 32 * 1024, "570")]);
        var checks = new Dictionary<string, HostCheck>
        {
            ["gpu-box"] = new(true, "Connected", new Dictionary<string, string> { ["ollama"] = "gemma4:e4b", ["deep-thinking"] = "gemma4:12b" })
        };
        var inputs = new NetworkInputs(machine, DeviceRole.Companion, null, null, false, checks, [hardware]);
        List<NetworkNode> nodes =
        [
            new("this-pc", NodeKind.ThisPc, "This PC", "DESK", "", NodeHealth.Ready, "Ready",
                [new("App", "Martlet companion", "", DeviceComponent.App), new("Character", "Character", "", DeviceComponent.Character)], [], [], []),
            new("host:gpu-box", NodeKind.Host, "gpu-box", "192.168.1.20", "", NodeHealth.Ready, "Connected",
                [new("Thinks", "Thinking", "", DeviceComponent.Job(Martlet.Core.Settings.SetupRole.Llm)),
                 new("Deep", "Deep thinking", "", DeviceComponent.Standby("deep-thinking"))], [], [], [], "gpu-box"),
            new("cloud:api.example.com", NodeKind.Cloud, "Example Voice", "api.example.com", "", NodeHealth.Ready, "Ready",
                [new("Speaks", "Speaking", "", DeviceComponent.Job(Martlet.Core.Settings.SetupRole.Tts))], [], [], [])
        ];
        return (inputs, nodes);
    }

    [Fact]
    public void Todays_jobs_and_host_roles_become_the_engines_current_setup()
    {
        var (inputs, nodes) = Network();
        var current = DeviceCapacityInputs.Current(inputs, nodes, Catalog);
        Assert.Equal(
        [
            new CurrentAssignment(PlanComponent.Character, "character", "this-pc"),
            new CurrentAssignment(PlanComponent.Thinking, "think:e4b", "gpu-box"),
            new CurrentAssignment(PlanComponent.DeepThinking, "deep:12b", "gpu-box")
        ], current);
        Assert.Equal(["this-pc", "gpu-box"], DeviceCapacityInputs.Machines(inputs, nodes, 500).Select(m => m.Id));
    }

    [Fact]
    public void A_host_shows_each_jobs_share_what_is_left_and_what_else_fits()
    {
        var (inputs, nodes) = Network();
        var machines = DeviceCapacityInputs.Machines(inputs, nodes, 500);
        var current = DeviceCapacityInputs.Current(inputs, nodes, Catalog);
        var plan = PlacementEngine.Measure(new PlanRequest(machines) { Current = current }, Catalog);

        var view = DeviceCapacityInputs.Device(nodes[1], machines, plan, current, Catalog, null)!;
        Assert.Equal("NVIDIA GeForce RTX 5090 (32 GB) · 64 GB memory · 32 processor threads", view.Specs);
        var vram = view.Bars.Single(b => b.Resource == CapacityResource.GraphicsMemory);
        Assert.Equal(14, vram.Planned);
        Assert.Equal(44, vram.PlannedPercent);
        Assert.Equal("Graphics memory: 14 of 32 GB planned (44%), 15 GB free for Martlet.", vram.Text);
        Assert.Equal(["Thinking (Gemma 4 E4B)", "Gemma 4 12B (deep thinking)"], view.Components.Select(c => c.Name));
        Assert.Equal("Gemma 4 12B (deep thinking): 25% graphics memory, 3% memory, 6% processor.",
            DeviceCapacity.ShareText(view.Components[1], view.Bars));
        // 28.8 GB of the card is Martlet's to use (the rest stays with the driver): 14.8 GB free fits one more 8 GB model or two 6 GB ones.
        Assert.Equal(["Room for 2 more Thinking models (Gemma 4 E4B) here.", "Room for another Deep thinking model (Gemma 4 12B) here."], view.AlsoFits);

        var network = DeviceCapacityInputs.Network(nodes, machines, plan, Catalog);
        Assert.Equal("On your computers: Character (This PC), Thinking (gpu-box), Deep thinking (gpu-box). Online: Voice (Example Voice). " +
            "Not set up: Listening, Lip-sync, Singing, Pictures.", network.Coverage);
        Assert.Equal("Totals across 2 computers: 32 GB graphics memory, 80 GB memory, 40 processor threads, 500 GB free disk.", network.Totals);
        Assert.Equal("Your computers could also run 2 more Thinking models (Gemma 4 E4B) and another Deep thinking model (Gemma 4 12B).", network.Fits);
    }

    [Fact]
    public void A_host_shows_what_its_jobs_usually_hold_and_the_most_they_take()
    {
        var (inputs, nodes) = Network();
        // The deep thinking model usually holds 5 GB and grows to 8 GB while it thinks.
        var catalog = new FootprintCatalog(Catalog.Options.Select(o => o.Id == "deep:12b" ? o with { Steady = new(5, 2, 2, 8) } : o));
        var machines = DeviceCapacityInputs.Machines(inputs, nodes, 500);
        var current = DeviceCapacityInputs.Current(inputs, nodes, catalog);
        var plan = PlacementEngine.Measure(new PlanRequest(machines) { Current = current }, catalog);

        var view = DeviceCapacityInputs.Device(nodes[1], machines, plan, current, catalog, null)!;
        Assert.Equal("Graphics memory: 11-14 of 32 GB planned (34-44%), 15 GB free for Martlet.",
            view.Bars.Single(b => b.Resource == CapacityResource.GraphicsMemory).Text);
        Assert.Equal("Gemma 4 12B (deep thinking): 16-25% graphics memory, 3% memory, 6% processor.",
            DeviceCapacity.ShareText(view.Components[1], view.Bars));
    }

    [Fact]
    public void Joining_hands_the_engine_the_other_computers_and_what_they_run_today()
    {
        var (inputs, nodes) = Network();
        var network = DeviceCapacityInputs.JoinNetwork(inputs, nodes, Catalog);
        Assert.Equal(["gpu-box"], network.Machines.Select(m => m.Id));
        // This PC is the newcomer: its own character is left out; the host's jobs stay as today's setup.
        Assert.Equal(
        [
            new CurrentAssignment(PlanComponent.Thinking, "think:e4b", "gpu-box"),
            new CurrentAssignment(PlanComponent.DeepThinking, "deep:12b", "gpu-box")
        ], network.Current);
        // A host that hasn't reported its hardware isn't a machine the engine can place on, so its jobs aren't either.
        Assert.Empty(DeviceCapacityInputs.JoinNetwork(inputs with { HostHardware = [] }, nodes, Catalog).Current);
    }

    [Fact]
    public void A_host_without_a_hardware_report_still_lists_its_jobs()
    {
        var (inputs, nodes) = Network();
        inputs = inputs with { HostHardware = [] };
        var machines = DeviceCapacityInputs.Machines(inputs, nodes, null);
        var current = DeviceCapacityInputs.Current(inputs, nodes, Catalog);
        var plan = PlacementEngine.Measure(new PlanRequest(machines) { Current = current }, Catalog);
        var view = DeviceCapacityInputs.Device(nodes[1], machines, plan, current, Catalog, null)!;
        Assert.Equal("This device hasn't reported its hardware yet. Use Check connection.", view.Specs);
        Assert.Equal(2, view.Components.Count);
        Assert.Empty(view.AlsoFits);
        Assert.Equal("This device hasn't reported its hardware yet, so Martlet can't tell what is left.", view.Headroom);
    }
}
