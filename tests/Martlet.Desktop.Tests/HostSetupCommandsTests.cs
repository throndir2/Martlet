using System.Windows;
using System.Windows.Controls;
using Martlet.Desktop;

namespace Martlet.Desktop.Tests;

public sealed class HostSetupCommandsTests
{
    private static HostSetupTarget Target(HostSetupMethod method, string ssh = "me@192.168.1.20", string address = "192.168.1.20",
        string? hostId = null) => new(method, ssh, address, hostId, "1.2.3");

    [Fact]
    public void This_pc_docker_builds_this_version_then_runs_the_engine_with_the_lan_address()
    {
        var script = HostSetupCommands.Script(Target(HostSetupMethod.ThisPcDocker, hostId: "gaming-pc-host"), HostAction.Setup);
        Assert.Contains("docker image inspect martlet-host:1.2.3 >NUL 2>&1 || docker build -t martlet-host:1.2.3 -f deploy/host/Dockerfile https://github.com/throndir2/Martlet.git#v1.2.3 || docker build -t martlet-host:1.2.3 -f deploy/host/Dockerfile https://github.com/throndir2/Martlet.git#main", script);
        Assert.Contains("docker run --rm -it -u 0 -v /var/run/docker.sock:/var/run/docker.sock -e MARTLET_HOST_ADDRESS=192.168.1.20 -e MARTLET_HOST_ID=gaming-pc-host martlet-host:1.2.3 --yes setup", script);
    }

    [Fact]
    public void Ssh_methods_run_the_same_engine_unattended_through_the_in_app_runner()
    {
        var docker = HostSetupCommands.DockerShell(Target(HostSetupMethod.SshDocker), HostAction.Add(HostRoles.Audio2Face));
        Assert.EndsWith("$D run --rm -it -u 0 -v /var/run/docker.sock:/var/run/docker.sock martlet-host:1.2.3 add audio2face", docker);
        var native = HostSetupCommands.NativeShell(Target(HostSetupMethod.SshNative), HostAction.Setup);
        Assert.EndsWith("MARTLET_HOST_ADDRESS=192.168.1.20 ~/Martlet/deploy/host/martlet-host setup", native);
        Assert.EndsWith("$D run --rm -i --log-driver none -u 0 -v /var/run/docker.sock:/var/run/docker.sock martlet-host:1.2.3 --yes pair --device-id d --name 'PC'",
            HostSetupCommands.RemoteShell(Target(HostSetupMethod.SshDocker), "pair --device-id d --name 'PC'", false));
        Assert.Contains("-e MARTLET_HOST_ADDRESS=192.168.1.20 martlet-host:1.2.3 --yes setup",
            HostSetupCommands.RemoteShell(Target(HostSetupMethod.SshDocker), "setup", true));
        var remoteNative = HostSetupCommands.RemoteShell(Target(HostSetupMethod.SshNative), "setup", true);
        Assert.EndsWith("MARTLET_HOST_ADDRESS=192.168.1.20 ~/Martlet/deploy/host/martlet-host --yes setup", remoteNative);
        Assert.Contains("git clone -q --depth 1 https://github.com/throndir2/Martlet.git ~/.cache/martlet/clone </dev/null", remoteNative);
        Assert.Contains("Stopped: git is not installed here", remoteNative);
        var supplied = HostSetupCommands.RemoteShell(Target(HostSetupMethod.SshNative), "setup", true, supplied: true);
        Assert.DoesNotContain("git ", supplied);
        Assert.EndsWith("MARTLET_SUPPLY=$HOME/.cache/martlet/supply MARTLET_HOST_ADDRESS=192.168.1.20 ~/Martlet/deploy/host/martlet-host --yes setup",
            supplied);
        Assert.EndsWith("martlet-host remove ollama", HostSetupCommands.NativeShell(Target(HostSetupMethod.SshNative), HostAction.Remove(HostRoles.Ollama)));
        Assert.Throws<ArgumentOutOfRangeException>(() => HostSetupCommands.Engine(HostAction.Add("x; rm -rf ~")));
        foreach (var method in new[] { HostSetupMethod.SshDocker, HostSetupMethod.SshNative })
            Assert.Throws<InvalidOperationException>(() => HostSetupCommands.Script(Target(method), HostAction.Pair));
        Assert.Throws<InvalidOperationException>(() => HostSetupCommands.RemoteShell(Target(HostSetupMethod.ThisPcDocker), "status", false));
    }

    [Fact]
    public void Runner_wraps_commands_for_sh_and_keeps_sudo_passwords_off_the_command_line()
    {
        Assert.Equal("'it'\\''s'", HostShell.Quote("it's"));
        Assert.Equal("sh -c 'echo '\\''hi'\\'''", HostShell.Wrap("echo 'hi'", sudo: false));
        var sudo = HostShell.Wrap("sudo id -u", sudo: true);
        Assert.StartsWith("sh -c '", sudo);
        Assert.Contains("IFS= read -r __martlet_pw", sudo);
        Assert.Contains("SUDO_ASKPASS", sudo);
        Assert.EndsWith("sudo id -u'", sudo);
        Assert.Equal("bold", HostShell.Clean("\u001b[1mbold\u001b[0m\r"));
        Assert.Equal(new HostShellTarget("me", "gpu-pc", 2222), HostShellTarget.Parse("me@gpu-pc:2222"));
        Assert.Equal("me@gpu-pc", HostShellTarget.Parse(" me@gpu-pc ").ToString());
        Assert.Equal("gpu-pc:22", HostShellTarget.Parse("me@GPU-PC").Machine);
        foreach (var bad in new[] { "gpu-pc", "me@gpu pc", "me@host;calc", "me@host:0", "'me'@host" })
            Assert.Throws<InvalidOperationException>(() => HostShellTarget.Parse(bad));
        using var key = System.Security.Cryptography.ECDsa.Create(System.Security.Cryptography.ECCurve.NamedCurves.nistP256);
        var line = HostShell.PublicKey(key.ExportParameters(false)).Split(' ');
        Assert.Equal("ecdsa-sha2-nistp256", line[0]);
        Assert.Equal(104, Convert.FromBase64String(line[1]).Length);
        Assert.StartsWith("martlet@", line[2]);
    }

    [Fact]
    public void Role_inputs_come_from_the_hosts_role_conf()
    {
        var role = HostRemote.ParseRole([
            "role.title=NVIDIA Audio2Face-3D lip-sync", "role.requires=gpu docker nvidia-toolkit", "role.terms=Accept NVIDIA's terms.",
            "role.installed=no", "role.secret=ngc_api_key|NVIDIA NGC API key (https://org.ngc.nvidia.com/setup/api-key)|stored",
            "role.choice=A2F_3D_MODEL_NAME|Audio2Face face model|claire mark james|claire"
        ]);
        Assert.False(role.Installed);
        Assert.Equal(new HostRoleSecret("ngc_api_key", "NVIDIA NGC API key (https://org.ngc.nvidia.com/setup/api-key)", true), Assert.Single(role.Secrets));
        var choice = Assert.Single(role.Choices);
        Assert.Equal(("A2F_3D_MODEL_NAME", "claire"), (choice.Variable, choice.Default));
        Assert.Equal(["claire", "mark", "james"], choice.Options);
        Assert.False(choice.Suggested || role.GpuOrCpu);
        var ollama = HostRemote.ParseRole(["role.choice=OLLAMA_MODEL|Model|llama3.2:3b qwen2.5:7b|llama3.2:3b", "role.suggested=OLLAMA_MODEL", "role.accelerator=gpu cpu"]);
        Assert.True(Assert.Single(ollama.Choices).Suggested && ollama.GpuOrCpu);
        var probe = new HostProbe(false, false, "password", "Ubuntu 24.04.1 LTS", "x86_64", "192.168.1.20", "gpu");
        Assert.Contains("Docker is not installed", HostRemote.Blocker(HostSetupMethod.SshDocker, probe, "me@gpu"));
        Assert.Null(HostRemote.Blocker(HostSetupMethod.SshNative, probe, "me@gpu"));
        // One SSH choice: Docker when the account can use it (directly or with sudo), otherwise native on Ubuntu.
        Assert.Equal(HostSetupMethod.SshNative, HostRemote.Choose(probe));
        Assert.Equal(HostSetupMethod.SshDocker, HostRemote.Choose(probe with { Docker = true }));
        Assert.Equal(HostSetupMethod.SshNative, HostRemote.Choose(probe with { Docker = true, Sudo = "none" }));
        Assert.Equal(HostSetupMethod.SshDocker, HostRemote.Choose(probe with { Docker = true, DockerAccess = true, Sudo = "none", OperatingSystem = "Debian 12" }));
        var debian = probe with { OperatingSystem = "Debian GNU/Linux 12" };
        Assert.Contains("Install Docker Engine", HostRemote.Blocker(HostRemote.Choose(debian), debian, "me@gpu"));
        Assert.True(HostRemote.NeedsSudo(HostSetupMethod.SshDocker, probe with { Docker = true }));
        Assert.False(HostRemote.NeedsSudo(HostSetupMethod.SshDocker, probe with { Docker = true, DockerAccess = true }));

        // A computer without internet access: Martlet sends a native host what setup and update need, and says plainly what it can't send.
        Assert.Null(HostRemote.OfflineBlocker(HostSetupMethod.SshNative, HostVerb.Setup, "me@gpu"));
        Assert.Null(HostRemote.OfflineBlocker(HostSetupMethod.SshNative, HostVerb.Update, "me@gpu"));
        Assert.Contains("Ubuntu host it runs without Docker", HostRemote.OfflineBlocker(HostSetupMethod.SshDocker, HostVerb.Setup, "me@gpu"));
        Assert.Contains("Docker images and models", HostRemote.OfflineBlocker(HostSetupMethod.SshNative, HostVerb.Add, "me@gpu"));
    }

    // "martlet-host describe audio2face" output: the local engine needs no key; the NIM engine's key and terms belong to it.
    private static readonly string[] Audio2FaceDescribe =
    [
        "role.title=NVIDIA Audio2Face-3D lip-sync (needs an NVIDIA GPU with 4 GB+ memory)", "role.requires=gpu docker nvidia-toolkit",
        "role.terms=", "role.installed=no",
        "role.terms_when=A2F_ENGINE=local|The local engine is NVIDIA's open-source Audio2Face-3D SDK (MIT).",
        "role.terms_when=A2F_ENGINE=nim|The nim engine is NVIDIA's Audio2Face-3D NIM container from nvcr.io.",
        "role.secret=ngc_api_key|NVIDIA NGC API key (create one at https://org.ngc.nvidia.com/setup/api-key)|missing",
        "role.secret_when=ngc_api_key|A2F_ENGINE=nim",
        "role.choice=A2F_ENGINE|Audio2Face engine (local needs no NVIDIA account; nim needs an NGC API key)|local nim|local",
        "role.choice=A2F_3D_MODEL_NAME|Audio2Face face model|claire mark james|claire"
    ];

    [Fact]
    public void Role_variants_ask_for_their_own_secret_and_terms_only_when_chosen()
    {
        var role = HostRemote.ParseRole(Audio2FaceDescribe);
        Assert.Equal(("A2F_ENGINE", "nim"), role.SecretWhen["ngc_api_key"]);
        Assert.Equal([("A2F_ENGINE", "local"), ("A2F_ENGINE", "nim")], role.TermsWhen.Select(t => (t.Variable, t.Value)));
        Assert.Equal(["local", "nim"], role.Choices[0].Options);

        RunSta(() =>
        {
            var dialog = HostInputDialog.RoleDialog("gpu-pc", "audio2face", role);
            var engine = Find<ComboBox>(dialog, "HostInput-choice.A2F_ENGINE");
            var key = Find<PasswordBox>(dialog, "HostInput-secret.ngc_api_key");
            var terms = Find<TextBlock>(dialog, "HostInputTerms-A2F_ENGINE");
            Assert.Equal("local", engine.SelectedItem);
            Assert.Equal(Visibility.Collapsed, key.Visibility);
            Assert.StartsWith("The local engine", terms.Text);
            Assert.Equal(new Dictionary<string, string> { ["choice.A2F_ENGINE"] = "local", ["choice.A2F_3D_MODEL_NAME"] = "claire" },
                HostInputDialog.Cleaned(dialog.Answers()));

            engine.SelectedItem = "nim";
            key.Password = "nvapi-test";
            Assert.Equal(Visibility.Visible, key.Visibility);
            Assert.StartsWith("The nim engine", terms.Text);
            Assert.Equal("nvapi-test", dialog.Answers()["secret.ngc_api_key"]);

            engine.SelectedItem = "local";
            Assert.False(dialog.Answers().ContainsKey("secret.ngc_api_key"));
            dialog.Close();
        });
    }

    // "martlet-host describe stt": whisper has the GPU option and its own models; Parakeet runs on the CPU with its own.
    private static readonly string[] SttDescribe =
    [
        "role.title=Speech-to-text: the bot's hearing", "role.requires=docker",
        "role.terms=Paired desktops send your recorded speech to this host one utterance at a time.", "role.installed=no",
        "role.terms_when=STT_ENGINE=whisper|whisper.cpp (MIT) runs as its official container.",
        "role.gpu_when=STT_ENGINE=whisper",
        "role.choice_when=STT_ENGINE=whisper|STT_MODEL|Whisper model|base small medium large-v3-turbo|small",
        "role.suggested_when=STT_ENGINE=whisper|STT_MODEL",
        "role.terms_when=STT_ENGINE=parakeet|Parakeet runs on this host's CPU with sherpa-onnx.",
        "role.choice_when=STT_ENGINE=parakeet|STT_MODEL|Parakeet model|parakeet-tdt-110m-en parakeet-tdt-0.6b-v2-int8 parakeet-tdt-0.6b-v3-int8|parakeet-tdt-110m-en",
        "role.choice=STT_ENGINE|Speech recognizer|whisper parakeet|whisper",
        "role.accelerator=gpu cpu"
    ];

    [Fact]
    public void Listening_on_another_computer_offers_whisper_and_parakeet_with_their_own_models()
    {
        var role = HostRemote.ParseRole(SttDescribe);
        Assert.Equal(("STT_ENGINE", "whisper"), role.GpuWhen);
        Assert.True(role.GpuOrCpu);
        var whisper = role.Choices.Single(c => c.When == ("STT_ENGINE", "whisper"));
        var parakeet = role.Choices.Single(c => c.When == ("STT_ENGINE", "parakeet"));
        Assert.True(whisper.Suggested);
        Assert.False(parakeet.Suggested);
        Assert.Equal("choice.STT_MODEL@STT_ENGINE=parakeet", parakeet.Key);
        Assert.Equal(["parakeet-tdt-110m-en", "parakeet-tdt-0.6b-v2-int8", "parakeet-tdt-0.6b-v3-int8"], parakeet.Options);

        // A computer without an NVIDIA graphics card: Parakeet, the fastest on a processor; with one: whisper.
        var cpuOnly = new Martlet.Core.Installation.HostHardware("imouto-host", "https://192.168.1.30:9443", DateTimeOffset.UnixEpoch,
            DateTimeOffset.UnixEpoch, "docker", "Ubuntu 24.04", null, "Ryzen 7", 16, 32, "docker", "no", []);
        var english = System.Globalization.CultureInfo.GetCultureInfo("en-US");
        var recommended = ListeningAdvisor.HostAnswers(cpuOnly, english);
        Assert.Equal("parakeet", recommended["choice.STT_ENGINE"].Value);
        Assert.Equal("whisper", ListeningAdvisor.HostAnswers(cpuOnly with
        {
            Gpus = [new Martlet.Core.Installation.HostGpu("NVIDIA GeForce RTX 4070", "nvidia", 12282, "590.1")]
        }, english)["choice.STT_ENGINE"].Value);
        Assert.False(ListeningAdvisor.HostAnswers(cpuOnly, System.Globalization.CultureInfo.GetCultureInfo("ja-JP")).ContainsKey("choice.STT_ENGINE"));
        Assert.Equal("parakeet-tdt-0.6b-v3-int8",
            ListeningAdvisor.HostAnswers(null, System.Globalization.CultureInfo.GetCultureInfo("de-DE"))["choice.STT_MODEL@STT_ENGINE=parakeet"].Value);

        RunSta(() =>
        {
            var dialog = HostInputDialog.RoleDialog("imouto-host", "stt", role, recommended, agent: true);
            var engine = Find<ComboBox>(dialog, "HostInput-choice.STT_ENGINE");
            var runOn = Find<ComboBox>(dialog, "HostInput-choice.accelerator");
            var whisperModel = Find<ComboBox>(dialog, "HostInput-choice.STT_MODEL@STT_ENGINE=whisper");
            var parakeetModel = Find<ComboBox>(dialog, "HostInput-choice.STT_MODEL@STT_ENGINE=parakeet");
            var terms = Find<TextBlock>(dialog, "HostInputTerms-STT_ENGINE");
            Assert.Equal("parakeet", engine.SelectedItem);
            Assert.Equal(Visibility.Collapsed, runOn.Visibility);
            Assert.Equal(Visibility.Collapsed, whisperModel.Visibility);
            Assert.Equal(Visibility.Visible, parakeetModel.Visibility);
            Assert.Equal("Parakeet TDT 110M (English)", parakeetModel.SelectedItem);
            Assert.StartsWith("Parakeet runs", terms.Text);
            Assert.Equal(new Dictionary<string, string> { ["choice.STT_ENGINE"] = "parakeet", ["choice.STT_MODEL"] = "parakeet-tdt-110m-en" },
                HostInputDialog.Cleaned(dialog.Answers()));
            parakeetModel.SelectedItem = "Parakeet TDT 0.6B v3 (25 European languages)";
            Assert.Equal("parakeet-tdt-0.6b-v3-int8", HostInputDialog.Cleaned(dialog.Answers())!["choice.STT_MODEL"]);

            engine.SelectedItem = "whisper";
            Assert.Equal(Visibility.Visible, runOn.Visibility);
            Assert.Equal(Visibility.Visible, whisperModel.Visibility);
            Assert.Equal(Visibility.Collapsed, parakeetModel.Visibility);
            Assert.StartsWith("whisper.cpp", terms.Text);
            // Automatic leaves the GPU or CPU and the model to the host.
            Assert.Equal(new Dictionary<string, string> { ["choice.STT_ENGINE"] = "whisper" }, HostInputDialog.Cleaned(dialog.Answers()));
            whisperModel.SelectedItem = "medium";
            Assert.Equal("medium", HostInputDialog.Cleaned(dialog.Answers())!["choice.STT_MODEL"]);
            dialog.Close();
        });
    }

    // "martlet-host describe ollama" on a host with an RTX 3090 (running chatterbox) and an RTX 3080.
    private static readonly string[] TwoGpuDescribe =
    [
        "role.title=Ollama", "role.requires=docker", "role.terms=", "role.installed=no",
        "role.choice=OLLAMA_MODEL|Model|gemma4:e2b gemma4:26b|gemma4:e2b", "role.suggested=OLLAMA_MODEL", "role.accelerator=gpu cpu",
        "role.gpu=GPU-3090aaaa-0000|NVIDIA GeForce RTX 3090|24576|chatterbox",
        "role.gpu=GPU-3080bbbb-1111|NVIDIA GeForce RTX 3080|10240|"
    ];

    [Fact]
    public void Each_role_can_be_put_on_its_own_graphics_card_when_the_host_has_several()
    {
        var role = HostRemote.ParseRole(TwoGpuDescribe);
        Assert.Equal(["GPU-3090aaaa-0000", "GPU-3080bbbb-1111"], role.Gpus.Select(g => g.Id));
        Assert.Equal(["chatterbox"], role.Gpus[0].UsedBy);
        Assert.Empty(role.Gpus[1].UsedBy);
        Assert.Equal("NVIDIA GeForce RTX 3080 (10 GB), free", role.Gpus[1].Describe());
        var oneCard = HostRemote.ParseRole(TwoGpuDescribe[..^1]);
        Assert.Single(oneCard.Gpus);

        RunSta(() =>
        {
            var dialog = HostInputDialog.RoleDialog("gpu-pc", "ollama", role);
            var card = Find<ComboBox>(dialog, "HostInput-choice.gpu");
            Assert.StartsWith("Automatic", (string)card.SelectedItem);
            Assert.False(HostInputDialog.Cleaned(dialog.Answers())!.ContainsKey("choice.gpu"));
            card.SelectedItem = ((IEnumerable<string>)card.ItemsSource).Single(t => t.Contains("3080", StringComparison.Ordinal));
            Assert.Equal("GPU-3080bbbb-1111", dialog.Answers()["choice.gpu"]);
            card.SelectedItem = ((IEnumerable<string>)card.ItemsSource).Single(t => t.StartsWith("All cards", StringComparison.Ordinal));
            Assert.Equal("all", dialog.Answers()["choice.gpu"]);
            dialog.Close();

            // One card (or none): nothing to choose.
            var single = HostInputDialog.RoleDialog("gpu-pc", "ollama", oneCard);
            Assert.Null(FindOrNull<ComboBox>(single, "HostInput-choice.gpu"));
            single.Close();

            // One-click installs ask only for the card, preselecting Martlet's suggestion; never for one card or the processor.
            var answers = new Dictionary<string, string> { ["choice.accelerator"] = "gpu", ["choice.gpu"] = "GPU-3080bbbb-1111" };
            var gpuOnly = HostInputDialog.GpuDialog("this PC", "Whisper", role, answers)!;
            Assert.Equal("GPU-3080bbbb-1111", gpuOnly.Answers()["choice.gpu"]);
            gpuOnly.Close();
            Assert.Null(HostInputDialog.GpuDialog("this PC", "Whisper", oneCard, answers));
            Assert.Null(HostInputDialog.GpuDialog("this PC", "Whisper", role, new Dictionary<string, string> { ["choice.accelerator"] = "cpu" }));
        });
    }

    // "martlet-host describe deep-thinking" for the role installed on diva: gemma4:26b on the CPU.
    private static readonly string[] InstalledDeepThinking =
    [
        "role.title=Deep thinking", "role.requires=docker", "role.terms=Ollama (MIT).", "role.installed=yes",
        "role.choice=OLLAMA_MODEL|Deep thinking model|gemma4:e2b gemma4:e4b gemma4:26b|gemma4:e2b", "role.suggested=OLLAMA_MODEL",
        "role.accelerator=gpu cpu", "role.choice_current=OLLAMA_MODEL|gemma4:26b", "role.accelerator_current=cpu"
    ];

    [Fact]
    public void An_installed_roles_dialog_changes_its_settings_starting_from_what_it_runs_with()
    {
        var role = HostRemote.ParseRole(InstalledDeepThinking);
        Assert.Equal("gemma4:26b", role.Current["OLLAMA_MODEL"]);
        Assert.Equal("cpu", role.AcceleratorCurrent);
        var older = HostRemote.ParseRole(InstalledDeepThinking.Where(l => !l.Contains("_current=", StringComparison.Ordinal)));
        Assert.Empty(older.Current);
        Assert.Null(older.AcceleratorCurrent);
        Assert.Empty(HostRemote.ParseRole(["role.choice_current=OLLAMA_MODEL", "role.accelerator_current=tpu"]).Current);

        // Changing a role reuses martlet-host add: the same command, with a title of its own.
        var change = HostAction.Change(HostRoles.DeepThinking);
        Assert.Equal("add deep-thinking", HostSetupCommands.Engine(change));
        Assert.Equal(("Change", "Add"), (change.AddVerb, HostRoles.Get(HostRoles.DeepThinking).Add.AddVerb));

        RunSta(() =>
        {
            var dialog = HostInputDialog.RoleDialog("diva", HostRoles.DeepThinking, role);
            Assert.Equal("Change deep-thinking on diva", Find<TextBlock>(dialog, "HostInputHeading").Text);
            Assert.Equal("_Apply", Find<Button>(dialog, "HostInputOk").Content);
            var model = Find<ComboBox>(dialog, "HostInput-choice.OLLAMA_MODEL");
            var accelerator = Find<ComboBox>(dialog, "HostInput-choice.accelerator");
            // What it runs with now is chosen; Automatic isn't offered (an unanswered choice keeps it anyway).
            Assert.Equal("gemma4:26b", model.SelectedItem);
            Assert.Equal("cpu", accelerator.SelectedItem);
            Assert.DoesNotContain("automatic", (IEnumerable<string>)model.ItemsSource);
            Assert.DoesNotContain("automatic", (IEnumerable<string>)accelerator.ItemsSource);
            Assert.Equal(new Dictionary<string, string> { ["choice.accelerator"] = "cpu", ["choice.OLLAMA_MODEL"] = "gemma4:26b" },
                HostInputDialog.Cleaned(dialog.Answers()));
            model.SelectedItem = "gemma4:e4b";
            accelerator.SelectedItem = "gpu";
            Assert.Equal(new Dictionary<string, string> { ["choice.accelerator"] = "gpu", ["choice.OLLAMA_MODEL"] = "gemma4:e4b" },
                HostInputDialog.Cleaned(dialog.Answers()));
            dialog.Close();

            // Not installed: the Add dialog, with the host's suggestion.
            var add = HostInputDialog.RoleDialog("diva", HostRoles.DeepThinking, role with { Installed = false, Current = new Dictionary<string, string>(), AcceleratorCurrent = null });
            Assert.Equal("Add deep-thinking on diva", Find<TextBlock>(add, "HostInputHeading").Text);
            Assert.Equal("automatic", Find<ComboBox>(add, "HostInput-choice.OLLAMA_MODEL").SelectedItem);
            Assert.Empty(HostInputDialog.Cleaned(add.Answers())!);
            add.Close();

            // An older host that doesn't say what it runs with: Apply sets it up with the choices shown, and the dialog says so.
            var old = HostInputDialog.RoleDialog("diva", HostRoles.DeepThinking, older);
            Assert.Equal("Change deep-thinking on diva", Find<TextBlock>(old, "HostInputHeading").Text);
            Assert.Equal("automatic", Find<ComboBox>(old, "HostInput-choice.OLLAMA_MODEL").SelectedItem);
            old.Close();

            // On a host with several cards it stays on the card it runs on now (or every card), even against a recommendation.
            var spread = HostRemote.ParseRole([.. TwoGpuDescribe.Select(l => l == "role.installed=no" ? "role.installed=yes" : l), "role.gpu_current=all"]);
            var cards = HostInputDialog.RoleDialog("gpu-pc", "ollama", spread,
                new Dictionary<string, (string, string)> { ["choice.gpu"] = ("GPU-3080bbbb-1111", "it has the most graphics memory free") });
            Assert.Equal("all", HostInputDialog.Cleaned(cards.Answers())!["choice.gpu"]);
            cards.Close();
            var pinned = HostInputDialog.RoleDialog("gpu-pc", "ollama", spread with { GpuCurrent = "GPU-3090aaaa-0000" });
            Assert.Equal("GPU-3090aaaa-0000", HostInputDialog.Cleaned(pinned.Answers())!["choice.gpu"]);
            pinned.Close();
        });
    }

    [Fact]
    public void Listening_goes_on_the_card_with_the_most_memory_free()
    {
        GpuNow[] cards =
        [
            new("NVIDIA GeForce RTX 3090", 24, 20, "590.1", "GPU-3090aaaa-0000"),
            new("NVIDIA GeForce RTX 3080", 10, 1, "590.1", "GPU-3080bbbb-1111")
        ];
        var advice = ListeningAdvisor.Advise(cards, null, [], 12, true);
        Assert.True(advice.UseGpu);
        Assert.Equal("GPU-3080bbbb-1111", advice.GpuId);
        Assert.Equal("GPU-3080bbbb-1111", advice.InstallAnswers(gpu: true)["choice.gpu"]);
        Assert.False(advice.InstallAnswers(gpu: false).ContainsKey("choice.gpu"));
        Assert.Null(ListeningAdvisor.Advise([cards[0]], null, [], 12, true).GpuId);
        Assert.Equal("GPU-3080bbbb-1111", ListeningAdvisor.Parse("NVIDIA GeForce RTX 3080, 10240, 1024, 590.1, GPU-3080bbbb-1111")!.Id);
    }

    private static T Find<T>(DependencyObject root, string id) where T : DependencyObject =>
        LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>()
            .Select(child => child is T match && System.Windows.Automation.AutomationProperties.GetAutomationId(child) == id
                ? match : FindOrNull<T>(child, id))
            .FirstOrDefault(found => found is not null) ?? throw new InvalidOperationException($"{id} not found.");

    private static T? FindOrNull<T>(DependencyObject root, string id) where T : DependencyObject
    {
        try { return Find<T>(root, id); }
        catch (InvalidOperationException) { return null; }
    }

    private static void RunSta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { action(); }
            catch (Exception error) { failure = error; }
            finally { System.Windows.Threading.Dispatcher.CurrentDispatcher.InvokeShutdown(); }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(20)));
        if (failure is not null) throw new Xunit.Sdk.XunitException(failure.ToString());
    }

    [Theory]
    [InlineData("me@host & calc", "192.168.1.20")]
    [InlineData("me@host\"x", "192.168.1.20")]
    [InlineData("me@192.168.1.20", "8.8.8.8")]
    [InlineData("me@192.168.1.20", "192.168.1.20 & calc")]
    public void Unsafe_targets_and_public_addresses_are_rejected(string ssh, string address) =>
        Assert.Throws<InvalidOperationException>(() =>
            HostSetupCommands.RemoteShell(Target(HostSetupMethod.SshDocker, ssh, address), "setup", true));

    [Fact]
    public void Updates_rebuild_this_version_and_unattended_runs_never_prompt()
    {
        var native = HostSetupCommands.NativeShell(Target(HostSetupMethod.SshNative), HostAction.Update);
        Assert.Contains("git -C ~/Martlet fetch -q --depth 1 origin tag v1.2.3 && git -C ~/Martlet -c advice.detachedHead=false checkout -q v1.2.3", native);
        Assert.EndsWith("~/Martlet/deploy/host/martlet-host update", native);
        var remote = HostSetupCommands.UnattendedScript(Target(HostSetupMethod.SshDocker), HostAction.Update)
            .Split("\r\n").Single(l => l.StartsWith("ssh ", StringComparison.Ordinal));
        Assert.StartsWith("ssh -T -o BatchMode=yes -o ConnectTimeout=15 me@192.168.1.20 \"", remote);
        Assert.Contains("D='sudo -n docker'", remote);
        Assert.EndsWith("$D run --rm -u 0 -v /var/run/docker.sock:/var/run/docker.sock martlet-host:1.2.3 update\"", remote);
        Assert.Equal(2, remote.Count(c => c == '"'));
        var local = HostSetupCommands.UnattendedScript(Target(HostSetupMethod.ThisPcDocker), HostAction.Update);
        Assert.Contains("docker run --rm -u 0 -v /var/run/docker.sock:/var/run/docker.sock martlet-host:1.2.3 update", local);
        Assert.Contains("docker run --rm -it -u 0", HostSetupCommands.Script(Target(HostSetupMethod.ThisPcDocker), HostAction.Update));
        Assert.Throws<InvalidOperationException>(() => HostSetupCommands.UnattendedScript(Target(HostSetupMethod.OnHost), HostAction.Update));
    }

    [Fact]
    public void Update_preferences_and_host_versions()
    {
        var root = Path.Combine(Path.GetTempPath(), "Martlet.Updates." + Guid.NewGuid().ToString("N"));
        try
        {
            Assert.Equal(new UpdatePreferences(), UpdatePreferences.Load(root));
            var chosen = new UpdatePreferences { IntervalMinutes = 15, AutoInstall = true, AutoUpdateHosts = true };
            UpdatePreferences.Save(root, chosen);
            Assert.Equal(chosen, UpdatePreferences.Load(root));
            File.WriteAllText(Path.Combine(root, UpdatePreferences.FileName), "{\"intervalMinutes\":7}");
            Assert.Equal(60, UpdatePreferences.Load(root).IntervalMinutes);
            File.WriteAllText(Path.Combine(root, UpdatePreferences.FileName), "not json");
            Assert.Throws<InvalidDataException>(() => UpdatePreferences.Load(root));

            var updates = Directory.CreateDirectory(AppUpdateInstaller.UpdatesDirectory(root)).FullName;
            File.WriteAllText(Path.Combine(updates, "last-install.txt"), "0 1.2.3\r\n");
            Assert.Equal(("Martlet updated to 1.2.3.", (string?)null), AppUpdateInstaller.TakeLastResult(root, "1.2.3"));
            Assert.Null(AppUpdateInstaller.TakeLastResult(root, "1.2.3"));
            File.WriteAllText(Path.Combine(updates, "last-install.txt"), "2 1.3.0\r\n");
            Assert.Equal("1.3.0", AppUpdateInstaller.TakeLastResult(root, "1.2.3")?.Failed);
            File.WriteAllText(Path.Combine(updates, "Martlet-1.2.3-win-x64.exe"), "old");
            File.WriteAllText(Path.Combine(updates, "Martlet-1.3.0-win-x64.exe"), "next");
            AppUpdateInstaller.CleanUp(root, "1.2.3");
            Assert.Equal(["Martlet-1.3.0-win-x64.exe"], Directory.GetFiles(updates, "*.exe").Select(Path.GetFileName));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }

        Assert.True(AppVersions.IsOlder(null, "0.3.0"));
        Assert.True(AppVersions.IsOlder("0.2.0", "0.3.0"));
        Assert.False(AppVersions.IsOlder("0.3.0", "0.3.0"));
        Assert.False(AppVersions.IsOlder("0.4.0", "0.3.0"));
    }

    [Fact]
    public void Suggested_ids_are_valid_gateway_identifiers()
    {
        Assert.Equal("gaming-pc-host", HostSetupCommands.SuggestedHostId("GAMING-PC"));
        Assert.Equal("martlet-host", HostSetupCommands.SuggestedHostId("---"));
        Assert.StartsWith("desktop-", HostSetupCommands.SuggestedDeviceId());
    }

    [Fact]
    public void This_pc_gateway_reads_its_version_and_whether_it_runs()
    {
        Assert.Equal(new Martlet.Core.Nodes.OwnHostReading("0.38.0", true), HostSetupCommands.ParseGateway("true martlet-host:0.38.0\r\n"));
        Assert.Equal(new Martlet.Core.Nodes.OwnHostReading("0.38.1", false), HostSetupCommands.ParseGateway("false martlet-host:0.38.1"));
        Assert.Equal(new Martlet.Core.Nodes.OwnHostReading(null, true), HostSetupCommands.ParseGateway("true martlet-host:latest"));
        Assert.Equal(new Martlet.Core.Nodes.OwnHostReading(null, true), HostSetupCommands.ParseGateway("true ubuntu:24.04"));
        Assert.Null(HostSetupCommands.ParseGateway(""));
        Assert.Null(HostSetupCommands.ParseGateway("maybe martlet-host:0.38.0"));
    }

    [Fact]
    public void This_pc_script_starts_docker_desktop_and_waits_before_running()
    {
        var script = HostSetupCommands.Script(Target(HostSetupMethod.ThisPcDocker), HostAction.Pair);
        Assert.True(script.IndexOf("Docker Desktop.exe", StringComparison.Ordinal) < script.IndexOf("\r\n:ready\r\n", StringComparison.Ordinal));
        Assert.True(script.IndexOf("\r\n:ready\r\n", StringComparison.Ordinal) < script.IndexOf("docker run", StringComparison.Ordinal));
        Assert.Contains("for /l %%i in (1,1,100) do (", script);
        Assert.Equal(2, HostSetupCommands.Preview(Target(HostSetupMethod.ThisPcDocker), HostAction.Pair).Split("\r\n").Length);
    }

    [Fact]
    public void Firewall_rule_is_scoped_to_the_host_port_on_private_networks_and_local_subnet()
    {
        var script = WindowsFirewall.ApplyScript(null);
        Assert.Contains("New-NetFirewallRule -Name 'Martlet-Host-Gateway'", script);
        Assert.Contains("-Direction Inbound -Action Allow -Protocol TCP -LocalPort 9443 -Profile Private,Domain -RemoteAddress LocalSubnet", script);
        Assert.DoesNotContain("Set-NetConnectionProfile", script);
        Assert.Contains("Set-NetConnectionProfile -InterfaceIndex 14 -NetworkCategory Private;", WindowsFirewall.ApplyScript(14));
        Assert.Contains("-Direction Inbound -Action Allow -Protocol UDP -LocalPort 9444 -Profile Private,Domain -RemoteAddress LocalSubnet", script);
        Assert.Contains("-Direction Inbound -Action Allow -Protocol TCP -LocalPort 9444 -Profile Private,Domain -RemoteAddress LocalSubnet", script);
        Assert.DoesNotContain("Martlet-Host-Gateway", WindowsFirewall.ApplyScript(null, gateway: false));
        Assert.Throws<InvalidOperationException>(() => WindowsFirewall.ProbeScript("192.168.1.2'; calc; '"));
        Assert.Equal(new WindowsFirewall.State(true, "Public", 7, false, true), WindowsFirewall.Parse("True|Public|7|False|True\r\n"));
        Assert.Equal(new WindowsFirewall.State(false, null, null, false), WindowsFirewall.Parse("unexpected"));
    }
}
