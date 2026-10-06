using System.Text;
using System.Xml.Linq;
using Martlet.Gateway.Host.Linux;

namespace Martlet.Gateway.Host.Linux.Tests;

public sealed class MacHostTests
{
    private static readonly MacFacts M2Pro = new("15.5", "24.5.0", "Apple M2 Pro", true, 12, 32UL << 30, "Apple M2 Pro", 22UL << 30, false);
    private static readonly MacFacts Intel = new("15.7", "24.6.0", "Intel(R) Core(TM) i7-9750H CPU @ 2.60GHz", false, 12, 16UL << 30,
        "AMD Radeon Pro 5300M", 4UL << 30, true);

    [Fact]
    public void Nvidia_roles_are_refused_with_the_catalog_reason_and_ollama_and_whisper_are_served()
    {
        Assert.Null(MacHost.Refusal("ollama"));
        Assert.Null(MacHost.Refusal("stt"));
        Assert.Null(MacHost.Refusal("deep-thinking"));
        Assert.Contains("NVIDIA", MacHost.Refusal("f5"), StringComparison.Ordinal);
        Assert.Contains("NVIDIA", MacHost.Refusal("audio2face"), StringComparison.Ordinal);
        Assert.Contains("CUDA", MacHost.Refusal("xtts"), StringComparison.Ordinal);
        Assert.Contains("isn't a Martlet host role", MacHost.Refusal("unknown"), StringComparison.Ordinal);
    }

    [Fact]
    public void Model_suggestions_follow_the_gpu_working_set_and_intel_macs_get_the_smallest()
    {
        Assert.Equal(22 * 1024, MacHost.ModelBudgetMb(M2Pro));
        Assert.Equal("gemma4:26b", MacHost.SuggestOllamaModel(MacHost.ModelBudgetMb(M2Pro)));
        Assert.Equal("gemma4:e4b", MacHost.SuggestOllamaModel(MacHost.ModelBudgetMb(M2Pro with { MemoryBytes = 16UL << 30, GpuWorkingSetBytes = null })));
        Assert.Equal("gemma4:e2b", MacHost.SuggestOllamaModel(MacHost.ModelBudgetMb(M2Pro with { MemoryBytes = 8UL << 30, GpuWorkingSetBytes = null })));
        Assert.Equal(0, MacHost.ModelBudgetMb(Intel));
        Assert.Equal("gemma4:e2b", MacHost.SuggestOllamaModel(MacHost.ModelBudgetMb(Intel)));
        Assert.Equal("large-v3-turbo", MacHost.SuggestWhisperModel(M2Pro));
        Assert.Equal("small", MacHost.SuggestWhisperModel(M2Pro with { MemoryBytes = 8UL << 30, GpuWorkingSetBytes = 5UL << 30 }));
        Assert.Equal("small", MacHost.SuggestWhisperModel(Intel));
        Assert.Equal("base", MacHost.SuggestWhisperModel(Intel with { MemoryBytes = 4UL << 30 }));
        Assert.Equal(48UL << 30, MacHost.EstimatedWorkingSet(64UL << 30));
    }

    [Fact]
    public void Installed_ollama_models_are_used_without_downloading()
    {
        (string, long)[] installed = [("llama3.2:3b", 2L << 30), ("qwen3:32b", 20L << 30), ("gemma4:12b", 8L << 30)];
        Assert.Null(MacHost.PickInstalledOllamaModel([], 22528, "gemma4:26b"));
        Assert.Equal("gemma4:12b", MacHost.PickInstalledOllamaModel(installed, 22528, "gemma4:12b"));
        Assert.Equal("qwen3:32b", MacHost.PickInstalledOllamaModel(installed, 22528, "gemma4:26b"));
        Assert.Equal("gemma4:12b", MacHost.PickInstalledOllamaModel(installed, 10000, "gemma4:e4b"));
        Assert.Equal("llama3.2:3b", MacHost.PickInstalledOllamaModel([("qwen3:32b", 20L << 30), ("llama3.2:3b", 2L << 30)], 0, "gemma4:e2b"));
    }

    [Fact]
    public void Machine_report_says_macos_with_chip_unified_memory_and_working_set()
    {
        var json = MacHost.MachineJson(M2Pro, new DateTimeOffset(2026, 10, 6, 12, 0, 0, TimeSpan.Zero));
        var report = GatewayMachineReport.Parse(Encoding.UTF8.GetBytes(json));
        Assert.NotNull(report);
        Assert.Equal("native", report.Method);
        Assert.Equal("macos", report.Platform);
        Assert.Equal("arm64", report.Architecture);
        Assert.Equal("15.5", report.OsVersion);
        Assert.Equal("Apple M2 Pro", report.Chip);
        Assert.True(report.UnifiedMemory);
        Assert.Equal(22, report.GpuWorkingSetGb);
        Assert.Equal(32, report.MemoryGb);
        var gpu = Assert.Single(report.Gpus);
        Assert.Equal("apple", gpu.Vendor);
        Assert.Equal(22 * 1024, gpu.MemoryMb);

        var intel = GatewayMachineReport.Parse(Encoding.UTF8.GetBytes(MacHost.MachineJson(Intel, DateTimeOffset.UtcNow)));
        Assert.NotNull(intel);
        Assert.Equal("x64", intel.Architecture);
        Assert.False(intel.UnifiedMemory);
        Assert.Equal(4, intel.GpuWorkingSetGb);
        Assert.Equal("amd", Assert.Single(intel.Gpus).Vendor);
        Assert.Equal(new[] { "battery" }, intel.Features);
    }

    [Fact]
    public void Generated_config_is_accepted_by_the_strict_parser_with_only_mac_roles()
    {
        var layout = new MacHostLayout("/Users/ada");
        var text = MacHost.ConfigJson("ada-mac", layout, "privateIp", "https://192.168.1.30:9443", 501, 20,
            [new("ollama", new Uri("http://127.0.0.1:11434/"), "gemma4:e4b"), new("stt", new Uri("http://127.0.0.1:8178/"), "small")]);
        var config = HostConfiguration.Parse(Encoding.UTF8.GetBytes(text));
        Assert.Equal("ada-mac", config.HostId);
        Assert.Equal("/Users/ada/Library/Application Support/Martlet/Host/private/state", config.StateDirectory);
        Assert.Equal(new[] { "ollama", "stt" }, config.Roles.Select(r => r.Kind));
        Assert.All(config.Roles, r => Assert.Null(MacHost.Refusal(r.Kind)));
        config.CheckPlacement(layout.ConfigPath);
    }

    [Fact]
    public void Launch_agent_is_a_valid_plist_that_restarts_on_failure()
    {
        var plist = MacHost.AgentPlist(MacHostLayout.GatewayLabel,
            ["/Applications/Martlet.app/Contents/Resources/host/Martlet.Gateway.Host.Linux", "serve", "--config", "/Users/a&b/host.json"],
            "/Users/a&b/gateway.log");
        var document = XDocument.Parse(plist.Replace("<!DOCTYPE plist PUBLIC \"-//Apple//DTD PLIST 1.0//EN\" \"http://www.apple.com/DTDs/PropertyList-1.0.dtd\">", "",
            StringComparison.Ordinal));
        var strings = document.Descendants("string").Select(s => s.Value).ToArray();
        Assert.Contains(MacHostLayout.GatewayLabel, strings);
        Assert.Contains("/Users/a&b/host.json", strings);
        Assert.Contains("Interactive", strings);
        Assert.Contains("SuccessfulExit", document.Descendants("key").Select(k => k.Value));
        Assert.Contains("<key>KeepAlive</key><true/>", MacHost.AgentPlist("x", ["a"], "/l", restartAlways: true), StringComparison.Ordinal);
    }

    [Fact]
    public void Host_id_and_address_defaults()
    {
        Assert.Equal("adas-macbook-pro", MacHost.DefaultHostId("Ada's MacBook Pro"));
        Assert.Equal("mac-host", MacHost.DefaultHostId("---"));
        Assert.True(MacHost.PrivateV4(System.Net.IPAddress.Parse("192.168.1.30")));
        Assert.True(MacHost.PrivateV4(System.Net.IPAddress.Parse("172.20.0.4")));
        Assert.False(MacHost.PrivateV4(System.Net.IPAddress.Parse("8.8.8.8")));
    }

    [Fact]
    public async Task Mac_commands_refuse_other_systems_and_show_help()
    {
        using var output = new StringWriter();
        if (!OperatingSystem.IsMacOS())
            Assert.Equal(4, await HostApplication.RunAsync(["macos-status"], output));
        Assert.Contains("macos-setup", output.ToString() + MacHost.Help, StringComparison.Ordinal);
        using var help = new StringWriter();
        Assert.Equal(0, await HostApplication.RunAsync(["macos-setup", "help"], help));
        Assert.Contains("launchd", help.ToString(), StringComparison.Ordinal);
    }
}
