using Martlet.Avatar.Hosting;
using Martlet.Core.Contracts;
using Martlet.Core.Settings;
using Martlet.Desktop;

namespace Martlet.Desktop.Tests;

public sealed class HostRolesTests
{
    private static AvatarRemoteHost Remote(string id, string ip) => new()
    {
        Origin = $"https://{ip}:9443", HostId = id, SpkiFingerprint = "sha256:" + new string('a', 64),
        DeviceId = "desktop-test", CredentialId = new string('B', 22)
    };

    private static HostPairings Pairings(string directory) =>
        new(directory, new AvatarProfileStore(directory), new SetupService(new SettingsStore(directory), new UnusedVault()));

    [Fact]
    public async Task A_new_pc_pairs_with_a_host_before_anything_is_set_up_on_it()
    {
        using var scope = new AvatarHostingTests.Scope();
        var pairings = Pairings(scope.DirectoryPath);
        await pairings.CheckCanKeepAsync(default);
        var (none, noProfile) = await pairings.LoadAsync(default);
        Assert.Empty(none);
        Assert.Null(noProfile);

        var (host, lipSync) = await pairings.AddAsync(Remote("gpu-a", "192.168.1.20"), HostSetupMethod.OnHost, null, default, adopt: false);
        Assert.False(lipSync);
        Assert.Equal(HostSetupMethod.Agent, host.Method);
        var (hosts, profile) = await pairings.LoadAsync(default);
        Assert.Equal("gpu-a", Assert.Single(hosts).HostId);
        Assert.Null(profile);
        // Pairing alone saves no settings: Setup on this PC stays untouched until a job is chosen.
        Assert.Equal(SettingsLoadState.FirstRun, (await new SettingsStore(scope.DirectoryPath).LoadAsync()).State);
    }

    [Fact]
    public async Task Handing_lip_sync_to_a_host_on_a_new_pc_saves_its_first_settings()
    {
        using var scope = new AvatarHostingTests.Scope();
        var pairings = Pairings(scope.DirectoryPath);
        var gpu = new PairedHost { Pairing = Remote("gpu-a", "192.168.1.20") };

        var (before, after) = await pairings.AssignLipSyncAsync(gpu, off: false, default);

        Assert.Null(before.RemoteHost);
        Assert.Equal("gpu-a", after.RemoteHost?.HostId);
        var loaded = await new SettingsStore(scope.DirectoryPath).LoadAsync();
        Assert.Equal(SettingsLoadState.Loaded, loaded.State);
        Assert.Empty(loaded.Settings!.Setup!.Routes);
        Assert.Equal(loaded.Settings.Profile.Id, after.ProfileId);
        var (saved, _) = await pairings.LoadProfileAsync(default);
        Assert.Equal("gpu-a", saved?.RemoteHost?.HostId);
        Assert.Equal(after.ProfileId, (await pairings.EnsureProfileAsync(default)).Profile.ProfileId);
    }

    [Fact]
    public async Task Pairing_stops_before_spending_a_code_only_when_settings_cannot_be_read()
    {
        using var scope = new AvatarHostingTests.Scope();
        File.WriteAllText(Path.Combine(scope.DirectoryPath, "settings.json"), "{not json");
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => Pairings(scope.DirectoryPath).CheckCanKeepAsync(default));
        Assert.DoesNotContain("Complete Setup", error.Message, StringComparison.Ordinal);
        Assert.Contains("Settings", error.Message, StringComparison.Ordinal);
    }

    private sealed class UnusedVault : ICredentialStore
    {
        public CredentialError Write(CredentialBinding binding, SecretLease secret) => throw new InvalidOperationException();
        public CredentialReadResult Read(CredentialBinding binding) => throw new InvalidOperationException();
        public CredentialError Delete(CredentialBinding binding) => throw new InvalidOperationException();
    }

    [Fact]
    public void Registry_keeps_every_paired_host_and_lists_an_older_lip_sync_pairing()
    {
        using var scope = new AvatarHostingTests.Scope();
        Assert.Empty(HostRegistry.Load(scope.DirectoryPath));
        var a = new PairedHost { Pairing = Remote("gpu-a", "192.168.1.20"), Method = HostSetupMethod.SshDocker, SshTarget = "me@gpu-a" };
        var b = new PairedHost { Pairing = Remote("gpu-b", "192.168.1.30"), Method = HostSetupMethod.SshNative, SshTarget = "me@gpu-b" };
        HostRegistry.Save(scope.DirectoryPath, HostRegistry.Upsert(HostRegistry.Upsert([], a), b));
        var loaded = HostRegistry.Load(scope.DirectoryPath, Remote("legacy", "192.168.1.40"), thisPcAddress: "192.168.1.40");
        Assert.Equal(new[] { "gpu-a", "gpu-b", "legacy" }, loaded.Select(h => h.HostId));
        Assert.Equal(a, loaded[0]);
        Assert.Equal(HostSetupMethod.ThisPcDocker, loaded[2].Method);
        Assert.True(loaded[1].CanLaunch);
        Assert.Contains("martlet-host --yes add audio2face", HostSetupCommands.RemoteShell(loaded[1].Target("0.2.0"), HostSetupCommands.Engine(HostAction.Add(HostRoles.Audio2Face)), false), StringComparison.Ordinal);
        File.WriteAllText(Path.Combine(scope.DirectoryPath, HostRegistry.FileName), "{not json");
        Assert.Throws<InvalidDataException>(() => HostRegistry.Load(scope.DirectoryPath));
    }

    [Fact]
    public void Registry_treats_a_this_pc_pairing_on_another_address_as_that_other_computer()
    {
        using var scope = new AvatarHostingTests.Scope();
        var here = new PairedHost { Pairing = Remote("here-host", "192.168.1.10"), Method = HostSetupMethod.ThisPcDocker };
        var pasted = new PairedHost { Pairing = Remote("gaming-host", "192.168.1.50"), Method = HostSetupMethod.ThisPcDocker };
        HostRegistry.Save(scope.DirectoryPath, [here, pasted]);
        var loaded = HostRegistry.Load(scope.DirectoryPath, thisPcAddress: "192.168.1.10");
        Assert.Equal(HostSetupMethod.ThisPcDocker, loaded[0].Method);
        Assert.Equal(HostSetupMethod.OnHost, loaded[1].Method);
        Assert.False(loaded[1].CanLaunch);
        Assert.All(HostRegistry.Load(scope.DirectoryPath), h => Assert.Equal(HostSetupMethod.ThisPcDocker, h.Method));
    }

    [Fact]
    public void Map_shows_who_handles_lip_sync_and_offers_to_hand_it_to_other_hosts()
    {
        using var scope = new AvatarHostingTests.Scope();
        var hosts = new[]
        {
            new PairedHost { Pairing = Remote("gpu-a", "192.168.1.20"), Method = HostSetupMethod.SshDocker, SshTarget = "me@gpu-a" },
            new PairedHost { Pairing = Remote("gpu-b", "192.168.1.30") }
        };
        var checks = new Dictionary<string, HostCheck>
        {
            ["gpu-a"] = new(true, "Reachable. Runs Audio2Face (model claire).",
                new Dictionary<string, string> { ["audio2face"] = "claire", ["ollama"] = "llama3.2-3b", ["stt"] = "small" }),
            ["gpu-b"] = new(true, "Reachable. Not running Audio2Face.", new Dictionary<string, string>())
        };
        var avatar = scope.Profile() with { RemoteHost = hosts[1].Pairing };
        var nodes = NetworkMap.Build(new(MachineInfo.Unknown, DeviceRole.Companion, null, avatar, false, checks, Hosts: hosts));
        var a = nodes.Single(n => n.Id == "host:gpu-a");
        var b = nodes.Single(n => n.Id == "host:gpu-b");
        var pc = nodes.Single(n => n.Id == "this-pc");
        Assert.Contains(a.Roles, r => r.Chip == "Lip-sync" && r.Detail.Contains("Assign lip-sync", StringComparison.Ordinal));
        Assert.Contains(a.Commands, c => c.Action == NodeAction.UseForLipSync && c.Argument == "gpu-a" && c.Primary);
        Assert.Contains(a.Commands, c => c.Action == NodeAction.RemoveRole && c.Argument == "gpu-a/audio2face");
        Assert.Contains(a.Roles, r => r.Chip == "Thinks" && r.Detail.Contains("Assign thinking", StringComparison.Ordinal));
        Assert.Contains(a.Commands, c => c.Action == NodeAction.UseForThinking && c.Argument == "gpu-a" && c.Primary);
        Assert.Contains(a.Roles, r => r.Chip == "Listens" && r.Detail.Contains("Assign listening", StringComparison.Ordinal));
        Assert.Contains(a.Commands, c => c.Action == NodeAction.UseForListening && c.Argument == "gpu-a" && c.Primary);
        Assert.Contains(b.Commands, c => c.Action == NodeAction.InstallRole && c.Argument == "gpu-b/stt");
        Assert.Contains(b.Commands, c => c.Action == NodeAction.InstallRole && c.Argument == "gpu-b/ollama");
        Assert.Contains(b.Roles, r => r.Chip == "Lip-sync" && r.Detail.StartsWith("Handles lip-sync", StringComparison.Ordinal));
        Assert.DoesNotContain(b.Commands, c => c.Action == NodeAction.UseForLipSync);
        Assert.Contains(b.Commands, c => c.Action == NodeAction.InstallRole && c.Argument == "gpu-b/audio2face");
        Assert.Contains(b.Facts, f => f.Label == "Connection" && f.Value.StartsWith("Run commands", StringComparison.Ordinal));
        Assert.Contains(pc.Commands, c => c.Action == NodeAction.LipSyncThisPc);
        Assert.Equal(LipSyncHandler.Host, NetworkMap.LipSync(avatar));
        Assert.Equal(LipSyncHandler.Loudness, NetworkMap.LipSync(avatar with { LipSync = AvatarLipSync.Loudness }));
        Assert.Equal(LipSyncHandler.ThisPc, NetworkMap.LipSync(avatar with { RemoteHost = null }));
    }

    [Fact]
    public void Map_shows_this_PCs_own_host_service_trouble_on_this_PC_and_never_offers_lip_sync_back_to_itself()
    {
        using var scope = new AvatarHostingTests.Scope();
        var own = new PairedHost { Pairing = Remote("imouto-host", "127.0.0.1"), Method = HostSetupMethod.ThisPcDocker };
        var checks = new Dictionary<string, HostCheck> { ["imouto-host"] = new(false, "Not reachable.") };
        var avatar = scope.Profile() with { RemoteHost = own.Pairing };
        var pc = NetworkMap.Build(new(MachineInfo.Unknown, DeviceRole.Companion, null, avatar, false, checks, Hosts: [own],
            OwnHostTrouble: "Docker Desktop isn't running")).Single(n => n.Id == "this-pc");
        Assert.Equal(NodeHealth.Attention, pc.Health);
        Assert.Equal("Host service not working", pc.HealthText);
        Assert.Contains(pc.Roles, r => r.Component == DeviceComponent.HostService &&
            r.Detail.StartsWith("Not working: Docker Desktop isn't running.", StringComparison.Ordinal));
        Assert.Contains(pc.Commands, c => c.Action == NodeAction.LipSyncThisPc && c.Label == "Do lip-sync without the host service");
        Assert.DoesNotContain(pc.Commands, c => c.Label == "Take lip-sync back to this PC");

        var silent = NetworkMap.Build(new(MachineInfo.Unknown, DeviceRole.Companion, null, avatar, false, checks, Hosts: [own]))
            .Single(n => n.Id == "this-pc");
        Assert.Equal("Host service not answering", silent.HealthText);
    }

    private static string RolesDirectory()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (Directory.Exists(Path.Combine(directory.FullName, "deploy", "host", "roles")))
                return Path.Combine(directory.FullName, "deploy", "host", "roles");
        throw new DirectoryNotFoundException("deploy/host/roles wasn't found above the test output.");
    }

    [Fact]
    public void Listening_hosts_parakeet_engine_downloads_the_same_models_as_this_pc()
    {
        // workers/parakeet's catalog mirrors ParakeetModels file for file: the same pinned revision, sizes and SHA-256.
        var repository = Directory.GetParent(RolesDirectory())!.Parent!.Parent!.FullName;
        var service = File.ReadAllText(Path.Combine(repository, "workers", "parakeet", "host", "martlet_parakeet_host.py"));
        foreach (var model in Martlet.Sherpa.ParakeetModels.All)
        {
            Assert.Contains($"\"{model.Id}\", \"{model.Name}\", \"{model.Languages}\",", service);
            Assert.Contains($"\"{model.Repository}\", \"{model.Revision}\",", service);
            Assert.Contains($"\"{model.NoticeFile}\",", service);
            foreach (var download in model.Downloads)
            {
                var size = download.Bytes.ToString("#,0", System.Globalization.CultureInfo.InvariantCulture).Replace(',', '_');
                Assert.Contains($"ModelFile(\"{download.Source.Segments[^1]}\", {size}, \"{download.Sha256}\")", service);
            }
        }
        // The stt role offers every one of them, by the same IDs, when its engine is parakeet.
        var conf = File.ReadAllLines(Path.Combine(RolesDirectory(), HostRoles.Stt, "role.conf"));
        var parakeet = conf.SkipWhile(line => line != "[STT_ENGINE=parakeet]").TakeWhile(line => line != "[end]")
            .Single(line => line.StartsWith("choice=STT_MODEL|", StringComparison.Ordinal)).Split('|');
        Assert.Equal(Martlet.Sherpa.ParakeetModels.All.Select(m => m.Id), parakeet[2].Split(' '));
        Assert.Equal(Martlet.Core.Settings.LocalSpeechSetup.Parakeet110mEnglishModelId, parakeet[3]);
    }

    [Fact]
    public void Nvidia_roles_are_x86_64_only_in_the_engine_and_the_catalog_and_stt_has_an_arm64_whisper_of_the_same_commit()
    {
        // The NVIDIA CUDA roles pin x86_64-only packages: martlet-host refuses them on ARM64 hosts (requires=x86_64), and so
        // does the platform catalog before an install is offered. A role built on the same CUDA image can also run on the CPU
        // (Chatterbox Nano): it is x86_64-only without needing the NVIDIA toolkit.
        foreach (var dir in Directory.GetDirectories(RolesDirectory()))
        {
            var kind = Path.GetFileName(dir);
            var requires = File.ReadAllLines(Path.Combine(dir, "role.conf"))
                .Single(l => l.StartsWith("requires=", StringComparison.Ordinal))["requires=".Length..].Split(' ');
            Assert.True(!requires.Contains("nvidia-toolkit") || requires.Contains("x86_64"), $"{kind}: requires={string.Join(' ', requires)}");
            if (Martlet.Core.Platforms.PlatformCatalog.EngineForHostRole(kind) is { } engine)
            {
                var arm = Martlet.Core.Platforms.PlatformCatalog.Check(engine, Martlet.Core.Platforms.PlatformSide.Host,
                    new Martlet.Core.Platforms.PlatformDevice { Platform = Martlet.Core.Platforms.DevicePlatform.Linux, Name = "pi", Arm64 = true });
                Assert.True(requires.Contains("x86_64") == (arm.Verdict == Martlet.Core.Platforms.PlatformVerdict.No), $"{kind}: {arm.Verdict} {arm.Reason}");
            }
        }
        var stt = Path.Combine(RolesDirectory(), HostRoles.Stt);
        var x64 = System.Text.RegularExpressions.Regex.Match(File.ReadAllText(Path.Combine(stt, "compose.yaml")), @"whisper\.cpp:main-([0-9a-f]{40})");
        var arm64 = System.Text.RegularExpressions.Regex.Match(File.ReadAllText(Path.Combine(stt, "compose.arm64.yaml")), @"whisper\.cpp:main-arm64-([0-9a-f]{40})");
        Assert.True(x64.Success && arm64.Success);
        Assert.Equal(x64.Groups[1].Value, arm64.Groups[1].Value);
        Assert.Contains("arm64=compose.arm64.yaml", File.ReadAllLines(Path.Combine(stt, "role.conf")));
    }

    [Fact]
    public void Every_worker_a_role_builds_from_is_in_the_host_image()
    {
        // A role that builds its image from ${MARTLET_SOURCE}/workers/<name> needs that folder in the martlet-host image.
        var repository = Directory.GetParent(RolesDirectory())!.Parent!.Parent!.FullName;
        var image = File.ReadAllText(Path.Combine(repository, "deploy", "host", "Dockerfile"));
        var built = Directory.GetFiles(RolesDirectory(), "compose*.yaml", SearchOption.AllDirectories)
            .SelectMany(file => System.Text.RegularExpressions.Regex.Matches(File.ReadAllText(file), @"\$\{MARTLET_SOURCE[^}]*\}/workers/([a-z0-9-]+)")
                .Select(match => match.Groups[1].Value))
            .Distinct().ToArray();
        Assert.Contains("parakeet", built);
        Assert.All(built, worker => Assert.Contains($"COPY workers/{worker} /opt/martlet/source/workers/{worker}", image));
    }

    [Fact]
    public void Host_image_builds_with_the_legacy_builder()
    {
        // Hosts without buildx use the legacy builder, which leaves BuildKit's automatic platform arguments empty:
        // "FROM --platform=$BUILDPLATFORM" then fails with "failed to parse platform".
        var repository = Directory.GetParent(RolesDirectory())!.Parent!.Parent!.FullName;
        var from = File.ReadAllLines(Path.Combine(repository, "deploy", "host", "Dockerfile"))
            .Where(line => line.StartsWith("FROM ", StringComparison.Ordinal)).ToArray();
        Assert.NotEmpty(from);
        Assert.All(from, line => Assert.DoesNotContain("--platform", line));
    }

    [Fact]
    public void Deep_thinking_role_suggests_the_same_models_as_the_Thinking_role()
    {
        // Thinking (ollama) and Deep thinking mirror each other: same models, default and suggestions by GPU memory.
        static string[] Choices(string kind) => File.ReadAllLines(Path.Combine(RolesDirectory(), kind, "role.conf"))
            .Where(line => line.StartsWith("choice=OLLAMA_MODEL|", StringComparison.Ordinal) ||
                line.StartsWith("choice_by_vram=OLLAMA_MODEL|", StringComparison.Ordinal))
            // A choice line is VAR|label|values|default: the label may differ, the rest may not.
            .Select(line => line.StartsWith("choice=", StringComparison.Ordinal)
                ? string.Join('|', line.Split('|').Where((_, i) => i != 1))
                : line)
            .ToArray();
        var thinking = Choices("ollama");
        Assert.Equal(2, thinking.Length);
        Assert.Equal(thinking, Choices(HostRoles.DeepThinking));
        // Martlet's thinks-at-once recommendation covers the same models and suggestion, and the role publishes its slots.
        var deep = File.ReadAllLines(Path.Combine(RolesDirectory(), HostRoles.DeepThinking, "role.conf"));
        Assert.Equal(string.Join(' ', DeepThinkingFit.Models), deep.Single(l => l.StartsWith("choice=OLLAMA_MODEL|", StringComparison.Ordinal)).Split('|')[2]);
        Assert.Equal("choice_by_vram=OLLAMA_MODEL|" + string.Join(' ', DeepThinkingFit.SuggestedByVram.Select(s => $"{s.MiB}@{s.Model}")),
            deep.Single(l => l.StartsWith("choice_by_vram=OLLAMA_MODEL|", StringComparison.Ordinal)));
        Assert.Equal("1 2 3 4", deep.Single(l => l.StartsWith("choice=OLLAMA_NUM_PARALLEL|", StringComparison.Ordinal)).Split('|')[2]);
        Assert.Contains("slots_from=OLLAMA_NUM_PARALLEL", deep);
        Assert.Contains("OLLAMA_NUM_PARALLEL: ${OLLAMA_NUM_PARALLEL:-1}",
            File.ReadAllText(Path.Combine(RolesDirectory(), HostRoles.DeepThinking, "compose.yaml")));
    }

    [Fact]
    public void Every_routed_host_role_is_listed_where_roles_are_added()
    {
        // HostRoles feeds the host dashboard's Add roles step, the Devices map's Install commands and Martlet hosts' role cards.
        var roles = Directory.GetDirectories(RolesDirectory())
            .Select(d => (Kind: Path.GetFileName(d), Conf: File.ReadAllLines(Path.Combine(d, "role.conf"))))
            .ToArray();
        var routed = roles.Where(r => r.Conf.Any(line => line.StartsWith("gateway_kind=", StringComparison.Ordinal))).ToArray();
        Assert.Equal(routed.Select(r => r.Kind).Order(StringComparer.Ordinal), HostRoles.All.Select(r => r.Kind).Order(StringComparer.Ordinal));
        Assert.All(routed, r => Assert.Contains("gateway_kind=" + r.Kind, r.Conf));
        Assert.Contains(HostRoles.All, r => r.Kind == HostRoles.DeepThinking && r.Name == "Thinking pool" &&
            r.RouteId == Martlet.Core.Settings.SelfHostSetup.DeepThinkingRouteId);
        // The one route-less role, Home Assistant, is set up from Smart home on a Linux host (Docker Desktop can't run it).
        Assert.Equal([HomeAssistantHosts.Role], roles.Except(routed).Select(r => r.Kind));
        Assert.All(HostRoles.All, r => Assert.NotNull(Martlet.Core.Platforms.PlatformCatalog.EngineForHostRole(r.Kind)));
        Assert.Equal(HostRoles.All.Count, HostRoles.All.Select(r => r.RouteId).Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(HostRoles.DeepThinking, HostRoles.ForRoute(Martlet.Core.Settings.SelfHostSetup.DeepThinkingRouteId)?.Kind);
    }

    [Fact]
    public void Map_lists_a_hosts_Deep_thinking_role_and_offers_it_where_it_is_missing()
    {
        var hosts = new[]
        {
            new PairedHost { Pairing = Remote("gpu-a", "192.168.1.20"), Method = HostSetupMethod.SshDocker, SshTarget = "me@gpu-a" },
            new PairedHost { Pairing = Remote("gpu-b", "192.168.1.30") }
        };
        var checks = new Dictionary<string, HostCheck>
        {
            ["gpu-a"] = new(true, "Reachable.", new Dictionary<string, string> { ["ollama"] = "gemma4-e4b", ["deep-thinking"] = "qwen3-8b" }),
            ["gpu-b"] = new(true, "Reachable.", new Dictionary<string, string>())
        };
        NetworkNode Host(string id, string? deepThinkingHost) =>
            NetworkMap.Build(new(MachineInfo.Unknown, DeviceRole.Companion, null, null, false, checks, Hosts: hosts,
                DeepThinkingHosts: deepThinkingHost is null ? null : [deepThinkingHost])).Single(n => n.Id == "host:" + id);

        var a = Host("gpu-a", null);
        Assert.Contains(a.Roles, r => r.Chip == "Thinking pool" &&
            r.Detail == "Ready (qwen3-8b). It joins the Thinking pool by itself unless you keep it out in Companion > Thinking pool.");
        Assert.Contains(a.Commands, c => c.Action == NodeAction.Companion && c.Argument == nameof(CompanionTab.DeepThinking) &&
            c.Label == "Show the Thinking pool" && c.Component == DeviceComponent.Standby(HostRoles.DeepThinking));
        // A computer the owner keeps out says so, and its command opens the page where it is ticked again.
        var kept = NetworkMap.Build(new(MachineInfo.Unknown, DeviceRole.Companion, null, null, false, checks, Hosts: hosts,
            ThinkingPoolLeft: ["gpu-a"])).Single(n => n.Id == "host:gpu-a");
        Assert.Contains(kept.Roles, r => r.Chip == "Thinking pool" &&
            r.Detail == "Ready (qwen3-8b). You keep it out of the Thinking pool: tick it in Companion > Thinking pool to add it again.");
        Assert.Contains(kept.Commands, c => c.Action == NodeAction.Companion && c.Label == "Add it to the Thinking pool");
        Assert.Contains(a.Commands, c => c.Action == NodeAction.RemoveRole && c.Argument == "gpu-a/deep-thinking");
        Assert.DoesNotContain(a.Commands, c => c.Action == NodeAction.InstallRole && c.Argument == "gpu-a/deep-thinking");
        // A role it runs can have its settings (its model...) changed from here, on that role's row.
        Assert.Contains(a.Commands, c => c.Action == NodeAction.ChangeRole && c.Argument == "gpu-a/deep-thinking" &&
            c.Label == "Change Thinking pool settings" && c.Component == DeviceComponent.Standby(HostRoles.DeepThinking));
        Assert.Contains(a.Commands, c => c.Action == NodeAction.ChangeRole && c.Argument == "gpu-a/ollama");
        Assert.DoesNotContain(Host("gpu-b", null).Commands, c => c.Action == NodeAction.ChangeRole);
        var thinking = Host("gpu-a", "gpu-a");
        Assert.Contains(thinking.Roles, r => r.Chip == "Thinking pool" && r.Detail == "Thinks things over in the background for this PC (qwen3-8b).");
        Assert.DoesNotContain(thinking.Commands, c => c.Action == NodeAction.Companion && c.Argument == nameof(CompanionTab.DeepThinking));
        Assert.Contains(Host("gpu-b", null).Commands, c => c.Action == NodeAction.InstallRole && c.Argument == "gpu-b/deep-thinking" &&
            c.Label == "Install Thinking pool");
    }

    [Fact]
    public void A_voice_engine_sharing_a_Windows_hosts_graphics_card_is_warned_about()
    {
        var wsl = new Martlet.Core.Installation.HostHardware("diva-host", "https://192.168.1.45:9443", DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow, "docker", "Docker Desktop", "5.15.167.4-microsoft-standard-WSL2", null, null, null, "Docker 29.8.1",
            "yes", []) { Platform = "linux" };
        var linux = wsl with { HostId = "linux-host", OperatingSystem = "Ubuntu 24.04", Kernel = "6.8.0-45-generic" };
        var offers = new Dictionary<string, string> { ["chatterbox"] = "chatterbox-turbo", ["audio2face"] = "claire", ["stt"] = "large-v3-turbo" };
        var warning = Martlet.Core.Installation.SharedGpu.Warning("diva-host", true, "Chatterbox Turbo", ["Lip-sync", "Listening"]);

        // Every role is either a voice engine or one of the other roles that can use the card (under the same name as here).
        Assert.All(HostRoles.All.Where(r => !HostRoles.Speaks(r.Kind)), role =>
            Assert.Contains((role.Kind, role.Name), Martlet.Core.Installation.SharedGpu.OtherGpuRoles));

        // The Devices map shows it on a Windows host that runs a voice engine beside other roles, not on a Linux one.
        var hosts = new[]
        {
            new PairedHost { Pairing = Remote("diva-host", "192.168.1.45") },
            new PairedHost { Pairing = Remote("linux-host", "192.168.1.60") }
        };
        var checks = new Dictionary<string, HostCheck>
        {
            ["diva-host"] = new(true, "Reachable.", offers),
            ["linux-host"] = new(true, "Reachable.", offers)
        };
        var nodes = NetworkMap.Build(new(MachineInfo.Unknown, DeviceRole.Companion, null, null, false, checks, [wsl, linux], hosts));
        Assert.Equal(warning, nodes.Single(n => n.Id == "host:diva-host").SharedGpu);
        Assert.Null(nodes.Single(n => n.Id == "host:linux-host").SharedGpu);
        var quiet = new Dictionary<string, HostCheck> { ["diva-host"] = new(true, "Reachable.", new Dictionary<string, string> { ["chatterbox"] = "chatterbox-turbo" }) };
        Assert.Null(NetworkMap.Build(new(MachineInfo.Unknown, DeviceRole.Companion, null, null, false, quiet, [wsl], hosts))
            .Single(n => n.Id == "host:diva-host").SharedGpu);
    }

    [Fact]
    public void Another_companion_PC_can_be_made_a_host_PC_from_the_map_and_an_ask_shows_until_it_switches()
    {
        NetworkNode Pc(MartletComputer computer) =>
            NetworkMap.Build(new(MachineInfo.Unknown, DeviceRole.Host, null, null, false, new Dictionary<string, HostCheck>(),
                Computers: [computer])).Single(n => n.Id == "pc:" + computer.DeviceId);
        var companion = new MartletComputer("desktop-b", "DESK-B", ComputerStanding.Member, null, true, Role: DeviceRole.Companion);

        var make = Assert.Single(Pc(companion).Commands, c => c.Component == DeviceComponent.Member);
        Assert.Equal((NodeAction.MakeHostPc, "Make it a host PC", "desktop-b"), (make.Action, make.Label, make.Argument));

        // While the ask waits, the computer's line says so and the command withdraws it.
        var asked = Pc(companion with { Asked = DeviceRole.Host, AskedBy = "this PC", AskedAt = DateTimeOffset.UtcNow });
        Assert.Contains(asked.Roles, r => r.Component == DeviceComponent.Member &&
            r.Detail.Contains("Asked by this PC", StringComparison.Ordinal) && r.Detail.Contains("to become a host PC", StringComparison.Ordinal));
        var keep = Assert.Single(asked.Commands, c => c.Component == DeviceComponent.Member);
        Assert.Equal((NodeAction.MakeCompanionPc, "Keep it a companion PC"), (keep.Action, keep.Label));

        // A host PC can be made a companion PC again; a computer that hasn't said what it is, or asks to join, gets neither.
        Assert.Contains(Pc(companion with { Role = DeviceRole.Host }).Commands, c => c.Action == NodeAction.MakeCompanionPc && c.Label == "Make it a companion PC");
        Assert.DoesNotContain(Pc(companion with { Role = null }).Commands, c => c.Action is NodeAction.MakeHostPc or NodeAction.MakeCompanionPc);
        Assert.DoesNotContain(Pc(companion with { Standing = ComputerStanding.Asking, CheckNumber = "123456", Through = "gpu-a" }).Commands,
            c => c.Action is NodeAction.MakeHostPc or NodeAction.MakeCompanionPc);
    }

    [Fact]
    public void A_host_PC_without_its_host_service_says_so_and_Settings_lists_each_computer_with_the_maps_switch()
    {
        var hostPc = new MartletComputer("desktop-b", "DESK-B", ComputerStanding.Member, "Active now on gpu-a.", true, Role: DeviceRole.Host);
        NetworkNode Device(MartletComputer computer, IReadOnlyList<PairedHost>? hosts = null) =>
            NetworkMap.Build(new(MachineInfo.Unknown, DeviceRole.Companion, null, null, false, new Dictionary<string, HostCheck>(),
                Hosts: hosts, Computers: [computer])).Single(n => n.Roles.Any(r => r.Component == DeviceComponent.Member));
        string Member(NetworkNode node) => node.Roles.Single(r => r.Component == DeviceComponent.Member).Detail;

        // A host PC with no host service yet does no work for the others: its row says so instead of naming a host service.
        Assert.Contains(NetworkMap.NoHostServiceYet, Member(Device(hostPc)), StringComparison.Ordinal);
        Assert.Equal("Host PC. " + NetworkMap.NoHostServiceYet + " Active now on gpu-a.", NetworkMap.RoleLine(hostPc, null));
        // One that said it runs a host service, or whose host service (named after it) this PC is paired with, runs it.
        Assert.Contains("Runs Martlet's host service (desk-b-host)", Member(Device(hostPc with { HostId = "desk-b-host" })), StringComparison.Ordinal);
        var paired = Device(hostPc, [new PairedHost { Pairing = Remote("desk-b-host", "192.168.1.52") }]);
        Assert.Equal("host:desk-b-host", paired.Id);
        Assert.Contains("Runs Martlet's host service (desk-b-host)", Member(paired), StringComparison.Ordinal);
        Assert.Equal("Host PC, runs desk-b-host. Active now on gpu-a.", NetworkMap.RoleLine(hostPc, "desk-b-host"));

        // Settings offers the same switch as the map: a host PC is made a companion PC again, a companion PC a host PC, and
        // while an ask waits the switch withdraws it; a computer that hasn't said what it is, or asks to join, gets none.
        Assert.Equal((NodeAction.MakeCompanionPc, "Make it a companion PC"), (NetworkMap.RoleCommand(hostPc)!.Action, NetworkMap.RoleCommand(hostPc)!.Label));
        var companion = hostPc with { Role = DeviceRole.Companion, HostId = "desk-b-host" };
        Assert.Equal(NodeAction.MakeHostPc, NetworkMap.RoleCommand(companion)!.Action);
        Assert.Equal("Companion PC that also runs a host service (desk-b-host). Active now on gpu-a.", NetworkMap.RoleLine(companion, null));
        var asked = companion with { Asked = DeviceRole.Host, AskedBy = "this PC", AskedAt = DateTimeOffset.UtcNow };
        Assert.Equal("Keep it a companion PC", NetworkMap.RoleCommand(asked)!.Label);
        Assert.Contains("Asked by this PC", NetworkMap.RoleLine(asked, null), StringComparison.Ordinal);
        Assert.Null(NetworkMap.RoleCommand(hostPc with { Role = null }));
        Assert.Contains("older Martlet", NetworkMap.RoleLine(hostPc with { Role = null }, null), StringComparison.Ordinal);
        Assert.Null(NetworkMap.RoleCommand(companion with { Standing = ComputerStanding.Asking }));
    }

    [Fact]
    public void Add_a_computer_offers_only_ways_to_add_one_each_saying_what_it_does()
    {
        NetworkNode Add(DeviceRole role) =>
            NetworkMap.Build(new(MachineInfo.Unknown, role, null, null, false, new Dictionary<string, HostCheck>())).Single(n => n.Kind == NodeKind.Add);

        // Nothing in its details only looks like a button: no job rows or notes, just choices, each a card with a line on what it does.
        var add = Add(DeviceRole.Companion);
        Assert.Empty(add.Roles);
        Assert.Empty(add.Notes);
        Assert.Equal([NodeAction.AddComputer, NodeAction.PrepareComputer, NodeAction.HostThisPc], add.Commands.Select(c => c.Action));
        Assert.All(add.Commands, c => Assert.False(string.IsNullOrWhiteSpace(c.Detail)));
        Assert.True(add.Commands.Single(c => c.Primary).Action == NodeAction.AddComputer);
        Assert.Contains("one-use code", add.Commands[0].Detail, StringComparison.Ordinal);

        // A host PC already runs host services, so it isn't offered to run them.
        Assert.DoesNotContain(Add(DeviceRole.Host).Commands, c => c.Action == NodeAction.HostThisPc);
    }
}
