using Martlet.Avatar.Hosting;
using Martlet.Core.Planning;
using Martlet.Desktop;

namespace Martlet.Desktop.Tests;

public sealed class ConfiguringStateTests
{
    private static AvatarRemoteHost Remote(string id, string ip) => new()
    {
        Origin = $"https://{ip}:9443", HostId = id, SpkiFingerprint = "sha256:" + new string('a', 64),
        DeviceId = "desktop-test", CredentialId = new string('B', 22)
    };

    [Fact]
    public void The_map_shows_configuring_with_the_step_over_any_other_status()
    {
        PairedHost[] hosts =
        [
            new() { Pairing = Remote("own-host", "127.0.0.1"), Method = HostSetupMethod.ThisPcDocker },
            new() { Pairing = Remote("gpu-box", "192.168.1.20") },
            new() { Pairing = Remote("idle-box", "192.168.1.30") }
        ];
        var checks = new Dictionary<string, HostCheck> { ["gpu-box"] = new(false, "Not reachable."), ["idle-box"] = new(true, "", new Dictionary<string, string>()) };
        MartletComputer[] computers = [new("desk-b", "DESK-B", ComputerStanding.Member, null, true, Role: DeviceRole.Companion)];
        var configuring = new Dictionary<string, string>
        {
            ["gpu-box"] = "Installing Chatterbox Turbo (2 of 4)",
            ["own-host"] = "Switching thinking to gpu-box",
            ["desk-b"] = "Removing F5-TTS"
        };
        var nodes = NetworkMap.Build(new(MachineInfo.Unknown, DeviceRole.Companion, null, null, false, checks, Hosts: hosts, Computers: computers,
            Configuring: configuring));
        var gpu = nodes.Single(n => n.Id == "host:gpu-box");
        Assert.Equal(NodeHealth.Configuring, gpu.Health);
        Assert.Equal("Configuring: Installing Chatterbox Turbo (2 of 4)", gpu.HealthText);
        Assert.Equal("Configuring: Switching thinking to gpu-box", nodes.Single(n => n.Id == "this-pc").HealthText);
        Assert.Equal("Configuring: Removing F5-TTS", nodes.Single(n => n.Id == "pc:desk-b").HealthText);
        Assert.NotEqual(NodeHealth.Configuring, nodes.Single(n => n.Id == "host:idle-box").Health);

        var calm = NetworkMap.Build(new(MachineInfo.Unknown, DeviceRole.Companion, null, null, false, checks, Hosts: hosts));
        Assert.Equal(NodeHealth.Attention, calm.Single(n => n.Id == "host:gpu-box").Health);
    }

    [Fact]
    public void A_job_no_host_does_next_is_switched_here_only_where_the_Companion_page_can_and_otherwise_says_where()
    {
        static Martlet.Core.Settings.SetupRoute Route(Martlet.Core.Settings.SetupRole role, Martlet.Core.Settings.SetupRouteType type, string origin, string model) => new()
        {
            Role = role, RouteType = type, ProviderAlias = "test", Origin = origin, ModelId = model, ConfigurationRevision = Guid.NewGuid()
        };
        var openAi = Route(Martlet.Core.Settings.SetupRole.Llm, Martlet.Core.Settings.SetupRouteType.OpenAi, "https://api.openai.com/v1", "gpt-5");
        var facts = new SetupRouteFacts(false, openAi, OllamaMissing: false, ParakeetInstalled: _ => false, WindowsVoice: "Microsoft Zira");

        // Thinking: from OpenAI to Gemma 4 E2B in this PC's Ollama, with the download's terms; nothing to do when it already runs.
        var local = SetupRoutes.Read("thinking", "gemma4:e2b", facts);
        Assert.Equal(SetupStepVerdict.Ready, local.Verdict);
        Assert.False(local.InUse);
        Assert.Contains("from ollama.com", local.Terms);
        var ollama = Route(Martlet.Core.Settings.SetupRole.Llm, Martlet.Core.Settings.SetupRouteType.ChatCompletions, MainWindow.LocalOllamaBaseUrl, "gemma4:e2b");
        Assert.True(SetupRoutes.Read("thinking", "gemma4:e2b", facts with { Route = ollama }).InUse);
        Assert.Equal(SetupStepVerdict.NeedsOwner, SetupRoutes.Read("thinking", "gemma4:e2b", facts with { OllamaMissing = true }).Verdict);

        // A hosted provider: only when this PC uses it already; otherwise the owner chooses it (with a key) in Companion.
        Assert.True(SetupRoutes.Read("thinking", "hosted:openai", facts).InUse);
        var nvidia = SetupRoutes.Read("thinking", "hosted:nvidia-build", facts);
        Assert.Equal(SetupStepVerdict.NeedsOwner, nvidia.Verdict);
        Assert.Contains("Companion › Thinking", nvidia.Text);

        // Listening with Parakeet (the catalog's v3 is the int8 download), and a Windows voice.
        var parakeet = SetupRoutes.Read("listening", "parakeet-tdt-0.6b-v3-cpu", facts with { Route = null });
        Assert.Equal(SetupStepVerdict.Ready, parakeet.Verdict);
        Assert.Contains("CC BY 4.0", parakeet.Terms);
        Assert.Null(SetupRoutes.Read("listening", "parakeet-tdt-0.6b-v3-cpu", facts with { Route = null, ParakeetInstalled = _ => true }).Terms);
        Assert.Equal(SetupStepVerdict.CannotApply, SetupRoutes.Read("listening", "parakeet-tdt-0.6b-v3-cpu", facts with { ParakeetInstalled = null }).Verdict);
        Assert.Contains("Microsoft Zira", SetupRoutes.Read("speaking", "windows-speech", facts with { Route = null }).Text);
        Assert.Equal(SetupStepVerdict.NeedsOwner, SetupRoutes.Read("speaking", "windows-speech", facts with { Route = null, WindowsVoice = null }).Verdict);
        Assert.Contains("Companion › Voice", SetupRoutes.Read("speaking", "hosted:openai-tts", facts with { Route = null }).Text);

        // A host PC uses no jobs.
        Assert.Contains("on your companion PC", SetupRoutes.Read("thinking", "gemma4:e2b", facts with { HostPc = true }).Text);
    }

    [Fact]
    public void A_host_change_shows_while_it_runs_and_ends_with_it()
    {
        var changes = 0;
        void Count() => Interlocked.Increment(ref changes);
        HostActivity.Changed += Count;
        try
        {
            using (var install = HostActivity.Begin("test-gpu", "Installing Chatterbox Turbo"))
            {
                Assert.Equal("Installing Chatterbox Turbo", HostActivity.Now["test-gpu"]);
                install.Update("waiting for Martlet on test-gpu");
                Assert.Equal("Installing Chatterbox Turbo: waiting for Martlet on test-gpu", HostActivity.Now["test-gpu"]);
                using (HostActivity.Begin("test-gpu", "Updating to Martlet 9.9.9"))
                    Assert.Equal("Updating to Martlet 9.9.9", HostActivity.Now["test-gpu"]);
                Assert.StartsWith("Installing", HostActivity.Now["test-gpu"], StringComparison.Ordinal);
            }
            Assert.False(HostActivity.Now.ContainsKey("test-gpu"));
            Assert.Equal(5, changes);
        }
        finally { HostActivity.Changed -= Count; }
    }

    [Fact]
    public void A_roles_describe_lines_become_the_arguments_its_install_runs_with()
    {
        var inputs = HostRemote.ParseRole(
        [
            "role.title=Speech-to-text",
            "role.terms=Paired desktops send your recorded speech to this host.",
            "role.installed=yes",
            "role.accelerator=yes",
            "role.gpu_when=STT_ENGINE=whisper",
            "role.choice=STT_ENGINE|Speech recognizer|whisper parakeet|whisper",
            "role.choice_when=STT_ENGINE=whisper|STT_MODEL|Whisper model|base small medium large-v3-turbo|small",
            "role.choice_when=STT_ENGINE=parakeet|STT_MODEL|Parakeet model|parakeet-tdt-110m-en parakeet-tdt-0.6b-v3-int8|parakeet-tdt-110m-en",
            "role.terms_when=STT_ENGINE=whisper|whisper.cpp (MIT) runs as its official container.",
            "role.terms_when=STT_ENGINE=parakeet|Parakeet runs on this host's CPU with sherpa-onnx.",
            "role.choice_current=STT_ENGINE|whisper",
            "role.choice_current=STT_MODEL|small",
            "role.gpu=GPU-11111111-aaaa|NVIDIA GeForce RTX 4090|24564|ollama",
            "role.gpu=GPU-22222222-bbbb|NVIDIA GeForce RTX 3060|12288|",
            "role.secret=hf_token|your Hugging Face token|missing",
            "role.secret_when=hf_token|STT_ENGINE=parakeet"
        ]);
        var needs = SetupRoleReading.Needs(inputs);
        var specs = new MachineSpecs("gpu-box", "gpu-box")
        {
            Gpus = [new("NVIDIA GeForce RTX 4090", GpuVendor.Nvidia, 24), new("NVIDIA GeForce RTX 3060", GpuVendor.Nvidia, 12)]
        };
        var whisper = new SetupChange(SetupChangeKind.ChangeModel, "gpu-box", "Listen with Whisper large-v3-turbo on the RTX 3060.", "w")
            { RoleKind = "stt", Model = "large-v3-turbo", GpuIndex = 1 };
        var (arguments, problem) = SetupExecutor.Arguments(whisper, needs, specs);
        Assert.Null(problem);
        Assert.Equal("whisper", arguments["choice.STT_ENGINE"]);
        Assert.Equal("large-v3-turbo", arguments["choice.STT_MODEL"]);
        Assert.Equal("GPU-22222222-bbbb", arguments["choice.gpu"]);
        Assert.Equal("gpu", arguments["choice.accelerator"]);

        var parakeet = whisper with { Model = "parakeet-tdt-0.6b-v3-int8", GpuIndex = null };
        (arguments, problem) = SetupExecutor.Arguments(parakeet, needs, specs);
        Assert.Null(problem);
        Assert.Equal("parakeet", arguments["choice.STT_ENGINE"]);
        Assert.False(arguments.ContainsKey("choice.accelerator"));
        // Only the Parakeet variant asks for the token.
        Assert.Equal("parakeet", needs.Secrets.Single().When!.Value);
        Assert.Equal("Parakeet runs on this host's CPU with sherpa-onnx.", needs.TermsWhen.Single(t => t.When.Value == "parakeet").Text);
    }
}
