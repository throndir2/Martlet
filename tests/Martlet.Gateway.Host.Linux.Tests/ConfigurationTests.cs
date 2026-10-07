using System.Text;
using System.Text.Json;
using Martlet.Gateway.Host.Linux;
using Martlet.Gateway.Persistence;
using Martlet.Gateway.Persistence.Portable.Tests;

namespace Martlet.Gateway.Host.Linux.Tests;

public sealed class ConfigurationTests
{
    internal static byte[] Config(string origin = "https://127.0.0.1:9443", string mode = "loopback") =>
        JsonSerializer.SerializeToUtf8Bytes(new
        {
            schemaVersion = 1, hostId = "fixture-host", stateDirectory = "/srv/martlet/store",
            storageBackend = "linuxServicePermissions", binding = new { mode, origin },
            serviceUid = 1000, serviceGid = 1000
        });

    internal static void WriteConfig(FakeLinuxFileSystem fs, byte[] bytes)
    {
        var parent = fs.OpenRoot();
        var srv = fs.OpenAt(parent, "srv", 0x10000 | 0x20000 | 0x80000, 0, 0xe);
        var dir = fs.OpenAt(srv, "martlet", 0x10000 | 0x20000 | 0x80000, 0, 0xe);
        if (!fs.Parent.Children.ContainsKey("host.json"))
        {
            var fd = fs.OpenAt(dir, "host.json", 2 | 0x40 | 0x80 | 0x20000 | 0x80000, 0x180, 0xf);
            fs.Close(fd);
        }
        fs.Parent.Children["host.json"].Bytes = bytes.ToArray();
        fs.Close(dir);
        fs.Close(srv);
        fs.Close(parent);
    }

    [Theory]
    [InlineData("\"schemaVersion\":1", "\"schemaVersion\":2")]
    [InlineData("\"schemaVersion\":1", "\"schemaVersion\":1,\"schemaVersion\":1")]
    [InlineData("\"schemaVersion\":1", "\"schemaVersion\":1,\"approved\":true")]
    [InlineData("\"serviceUid\":1000", "\"serviceUid\":0")]
    [InlineData("\"serviceUid\":1000", "\"serviceUid\":\"1000\"")]
    [InlineData("\"hostId\":\"fixture-host\"", "\"hostId\":null")]
    [InlineData("/srv/martlet/store", "/srv/../store")]
    [InlineData("linuxServicePermissions", "windowsCurrentUserDpapi")]
    [InlineData("https://127.0.0.1:9443", "https://0.0.0.0:9443")]
    [InlineData("https://127.0.0.1:9443", "https://localhost:9443")]
    [InlineData("https://127.0.0.1:9443", "https://192.168.1.1:9443")]
    [InlineData("https://127.0.0.1:9443", "https://127.0.0.2:9443")]
    public void Invalid_configuration_is_not_coerced(string before, string after) =>
        Assert.Throws<HostInputException>(() => HostConfiguration.Parse(
            Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(Config()).Replace(before, after, StringComparison.Ordinal))));

    [Theory]
    [InlineData("https://10.2.3.4:9443")]
    [InlineData("https://172.16.0.1:9443")]
    [InlineData("https://192.168.1.2:9443")]
    [InlineData("https://[fd12::1]:9443")]
    public void Private_origin_requires_explicit_mode(string origin) =>
        Assert.Equal(origin, HostConfiguration.Parse(Config(origin, "privateIp")).Binding.Origin.CanonicalOrigin);

    private static byte[] RoleConfig(string roles) =>
        Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(Config("https://192.168.1.2:9443", "privateIp"))
            .Replace("\"serviceGid\":1000", $"\"serviceGid\":1000,\"roles\":{roles}", StringComparison.Ordinal));

    private static byte[] Audio2FaceRole(string endpoint, string model = "claire") =>
        RoleConfig($"[{{\"kind\":\"audio2face\",\"endpoint\":\"{endpoint}\",\"model\":\"{model}\"}}]");

    [Fact]
    public void Published_binding_is_only_accepted_inside_a_container()
    {
        var published = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(Config("https://192.168.1.2:9443", "published")));
        var previous = HostConfiguration.InsideContainer;
        try
        {
            HostConfiguration.InsideContainer = () => false;
            Assert.Throws<HostInputException>(() => HostConfiguration.Parse(published));
            HostConfiguration.InsideContainer = () => true;
            var binding = HostConfiguration.Parse(published).Binding;
            Assert.Equal("https://192.168.1.2:9443", binding.Origin.CanonicalOrigin);
            Assert.Equal(System.Net.IPAddress.Any, binding.ListenAddress);
            Assert.Throws<HostInputException>(() => HostConfiguration.Parse(Config("https://127.0.0.1:9443", "published")));
            Assert.Null(HostConfiguration.Parse(Config("https://192.168.1.2:9443", "privateIp")).Binding.ListenAddress);
        }
        finally { HostConfiguration.InsideContainer = previous; }
    }

    [Fact]
    public void Pairing_code_carries_the_whole_invitation_and_parses_on_the_desktop()
    {
        var token = new string('T', 43);
        var card = new GatewayPairingCard
        {
            PairingId = "AAAAAAAAAAAAAAAAAAAAAA", HostId = "gpu-host", Origin = "https://192.168.1.20:9443",
            SpkiFingerprint = "sha256:" + new string('a', 64), Token = new GatewaySecret(token),
            ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(5)
        };
        var code = PairingCode.Format(card);
        Assert.StartsWith("martlet-pair-v1.", code, StringComparison.Ordinal);
        Assert.DoesNotContain('\n', code);
        var parsed = Martlet.Avatar.Audio2Face.Remote.HostPairingCode.Parse("  " + code + "\r\n");
        Assert.Equal((card.Origin, card.HostId, card.SpkiFingerprint, card.PairingId, token),
            (parsed.Origin, parsed.HostId, parsed.SpkiFingerprint, parsed.PairingId, parsed.Token));
        Assert.DoesNotContain(token, parsed.ToString(), StringComparison.Ordinal);
        foreach (var bad in new[] { "", "martlet-pair-v1.", "martlet-pair-v1.e30", code[..^4], code.Replace("v1", "v2") })
            Assert.Throws<Martlet.Avatar.Audio2Face.Remote.Audio2FaceHostException>(
                () => Martlet.Avatar.Audio2Face.Remote.HostPairingCode.Parse(bad));
    }

    [Fact]
    public void Configuration_rendered_by_martlet_host_parses()
    {
        // Exact shape written by deploy/host/martlet-host render_config (checked in a local harness).
        const string rendered = "{\"schemaVersion\":1,\"hostId\":\"gpu-host\",\"stateDirectory\":\"/home/u/.local/share/martlet/host/private/state\"," +
            "\"storageBackend\":\"linuxServicePermissions\",\"binding\":{\"mode\":\"privateIp\",\"origin\":\"https://192.168.1.20:9443\"}," +
            "\"serviceUid\":1000,\"serviceGid\":1000,\"roles\":[{\"kind\":\"audio2face\",\"endpoint\":\"http://127.0.0.1:52000/\",\"model\":\"mark\"}]}\n";
        var config = HostConfiguration.Parse(Encoding.UTF8.GetBytes(rendered));
        Assert.Equal("mark", Assert.Single(config.Roles).Model);
        Assert.Empty(HostConfiguration.Parse(Encoding.UTF8.GetBytes(rendered.Replace(
            "{\"kind\":\"audio2face\",\"endpoint\":\"http://127.0.0.1:52000/\",\"model\":\"mark\"}", "", StringComparison.Ordinal))).Roles);
        // Roles pinned to a card or added to run on the processor name where they run (render_config, checked in a local harness).
        var placed = HostConfiguration.Parse(Encoding.UTF8.GetBytes(rendered.Replace(
            "{\"kind\":\"audio2face\",\"endpoint\":\"http://127.0.0.1:52000/\",\"model\":\"mark\"}",
            "{\"kind\":\"deep-thinking\",\"endpoint\":\"http://127.0.0.1:11435/\",\"model\":\"qwen3:8b\",\"slots\":2,\"gpus\":[\"GPU-aaaa-1111\"]}," +
            "{\"kind\":\"f5\",\"endpoint\":\"http://127.0.0.1:50051/\",\"model\":\"f5-tts-v1\"}," +
            "{\"kind\":\"ollama\",\"endpoint\":\"http://127.0.0.1:11434/\",\"model\":\"gemma4:e4b\",\"gpus\":[\"GPU-aaaa-1111\"]}," +
            "{\"kind\":\"stt\",\"endpoint\":\"http://127.0.0.1:8178/\",\"model\":\"large-v3-turbo\",\"gpus\":[\"cpu\"]}",
            StringComparison.Ordinal))).Roles;
        Assert.Equal(new string[]?[] { ["GPU-aaaa-1111"], null, ["GPU-aaaa-1111"], ["cpu"] }, placed.Select(r => r.Gpus?.ToArray()));
        Assert.Equal(2, placed[0].Slots);
    }

    [Fact]
    public void Host_roles_are_declared_uniformly_and_only_reach_this_hosts_loopback_services()
    {
        var config = HostConfiguration.Parse(Audio2FaceRole("http://127.0.0.1:52000/"));
        var role = Assert.Single(config.Roles);
        Assert.Equal(("audio2face", new Uri("http://127.0.0.1:52000/"), "claire"), (role.Kind, role.Endpoint, role.Model));
        Assert.IsType<Martlet.Gateway.Audio2Face.Audio2FaceRelayWorker>(NativeHostPlatform.RoleWorker(role));
        var ollama = Assert.Single(HostConfiguration.Parse(RoleConfig(
            "[{\"kind\":\"ollama\",\"endpoint\":\"http://127.0.0.1:11434/\",\"model\":\"llama3.2:3b\"}]")).Roles);
        Assert.IsType<Martlet.Gateway.Ollama.OllamaRelayWorker>(NativeHostPlatform.RoleWorker(ollama));
        Assert.Throws<HostInputException>(() => NativeHostPlatform.RoleWorker(ollama with { Model = "Not/A:Valid:Tag" }));
        // The deep-thinking role is a second Ollama of its own beside the conversation model's, on Deep thinking's route.
        var both = HostConfiguration.Parse(RoleConfig(
            "[{\"kind\":\"ollama\",\"endpoint\":\"http://127.0.0.1:11434/\",\"model\":\"gemma4:e4b\"}," +
            "{\"kind\":\"deep-thinking\",\"endpoint\":\"http://127.0.0.1:11435/\",\"model\":\"qwen3:8b\"}]")).Roles;
        var routes = both.Select(r => ((Martlet.Gateway.Ollama.OllamaRelayWorker)NativeHostPlatform.RoleWorker(r)).Route).ToArray();
        Assert.Equal([Martlet.Core.Settings.SelfHostSetup.OllamaRouteId, Martlet.Core.Settings.SelfHostSetup.DeepThinkingRouteId],
            routes.Select(r => r.RouteId));
        Assert.Equal(["gemma4-e4b", "qwen3-8b"], routes.Select(r => r.ModelId));
        var stt = Assert.Single(HostConfiguration.Parse(RoleConfig(
            "[{\"kind\":\"stt\",\"endpoint\":\"http://127.0.0.1:8178/\",\"model\":\"large-v3-turbo\"}]")).Roles);
        Assert.IsType<Martlet.Gateway.Stt.SttRelayWorker>(NativeHostPlatform.RoleWorker(stt));
        Assert.Throws<HostInputException>(() => NativeHostPlatform.RoleWorker(stt with { Model = "Large:V3" }));
        var pictures = Assert.Single(HostConfiguration.Parse(RoleConfig(
            "[{\"kind\":\"pictures\",\"endpoint\":\"http://127.0.0.1:50086/\",\"model\":\"z-image-turbo\"}]")).Roles);
        Assert.IsType<Martlet.Gateway.Pictures.PictureRelayWorker>(NativeHostPlatform.RoleWorker(pictures));
        Assert.Equal(Martlet.Gateway.GatewayInferenceRoute.PictureRouteId,
            ((Martlet.Gateway.Pictures.PictureRelayWorker)NativeHostPlatform.RoleWorker(pictures)).Route.RouteId);
        var ocr = Assert.Single(HostConfiguration.Parse(RoleConfig(
            "[{\"kind\":\"ocr\",\"endpoint\":\"http://127.0.0.1:50087/\",\"model\":\"rapidocr-ppocrv4\"}]")).Roles);
        Assert.IsType<Martlet.Gateway.Ocr.OcrRelayWorker>(NativeHostPlatform.RoleWorker(ocr));
        Assert.Equal(Martlet.Gateway.GatewayInferenceRoute.OcrRouteId,
            ((Martlet.Gateway.Ocr.OcrRelayWorker)NativeHostPlatform.RoleWorker(ocr)).Route.RouteId);
        Assert.Empty(HostConfiguration.Parse(Config()).Roles);
        Assert.Empty(HostConfiguration.Parse(RoleConfig("[]")).Roles);
        foreach (var endpoint in new[] { "http://192.168.1.5:52000/", "http://localhost:52000/", "https://127.0.0.1:52000/",
            "http://127.0.0.1:52000/path", "http://127.0.0.1:52000" })
            Assert.Throws<HostInputException>(() => HostConfiguration.Parse(Audio2FaceRole(endpoint)));
        Assert.Throws<HostInputException>(() => HostConfiguration.Parse(Audio2FaceRole("http://127.0.0.1:52000/", "bad model")));
        Assert.Throws<HostInputException>(() => HostConfiguration.Parse(
            RoleConfig("[{\"kind\":\"unknown\",\"endpoint\":\"http://127.0.0.1:52000/\",\"model\":\"m\"}]")));
        Assert.Throws<HostInputException>(() => HostConfiguration.Parse(RoleConfig(
            "[{\"kind\":\"audio2face\",\"endpoint\":\"http://127.0.0.1:52000/\",\"model\":\"a\"}," +
            "{\"kind\":\"audio2face\",\"endpoint\":\"http://127.0.0.1:52001/\",\"model\":\"b\"}]")));
        Assert.Throws<HostInputException>(() => HostConfiguration.Parse(
            RoleConfig("[{\"kind\":\"audio2face\",\"endpoint\":\"http://127.0.0.1:52000/\",\"model\":\"a\",\"extra\":1}]")));
    }

    [Fact]
    public void A_role_may_say_which_graphics_cards_it_runs_on_and_its_route_advertises_them()
    {
        const string card = "GPU-1a2b3c4d-0000-1111-2222-333344445555";
        static IReadOnlyList<HostRole> Roles(string roles) => HostConfiguration.Parse(RoleConfig(roles)).Roles;
        // The shape martlet-host renders: a pinned card's UUID, or "cpu" for a role added to run on the processor.
        var roles = Roles(
            "[{\"kind\":\"ollama\",\"endpoint\":\"http://127.0.0.1:11434/\",\"model\":\"gemma4:e4b\",\"gpus\":[\"" + card + "\"]}," +
            "{\"kind\":\"deep-thinking\",\"endpoint\":\"http://127.0.0.1:11435/\",\"model\":\"qwen3:8b\",\"slots\":2,\"gpus\":[\"1\"]}," +
            "{\"kind\":\"stt\",\"endpoint\":\"http://127.0.0.1:8178/\",\"model\":\"large-v3-turbo\",\"gpus\":[\"cpu\"]}," +
            "{\"kind\":\"ocr\",\"endpoint\":\"http://127.0.0.1:50087/\",\"model\":\"rapidocr-ppocrv4\"}," +
            "{\"kind\":\"pictures\",\"endpoint\":\"http://127.0.0.1:50086/\",\"model\":\"z-image-turbo\"}]");
        Assert.Equal([card], roles[0].Gpus);
        Assert.Null(roles[3].Gpus);
        var routes = roles.Select(r => NativeHostPlatform.RoleWorker(r).Route).ToArray();
        Assert.Equal([card], routes[0].Gpus);
        Assert.Equal(Martlet.Gateway.GatewayLane.Live, routes[0].Lane);
        Assert.Equal(["1"], routes[1].Gpus);
        Assert.Equal((Martlet.Gateway.GatewayLane.Pool, 2), (routes[1].Lane, routes[1].MaximumConcurrency));
        Assert.Equal(["cpu"], routes[2].Gpus);
        // Reading never uses the graphics card; a role that doesn't say where it runs counts as the whole host.
        Assert.Equal(["cpu"], routes[3].Gpus);
        Assert.Empty(routes[4].Gpus);
        foreach (var bad in new[] { "[]", "\"" + card + "\"", "[\"cpu\",\"0\"]", "[\"gpu0\"]", "[1]", "[\"0\",\"0\"]", "null" })
            Assert.Throws<HostInputException>(() => Roles(
                "[{\"kind\":\"ollama\",\"endpoint\":\"http://127.0.0.1:11434/\",\"model\":\"gemma4:e4b\",\"gpus\":" + bad + "}]"));
    }

    [Fact]
    public void Deep_thinking_role_may_run_several_thinks_at_once_and_advertises_its_slots()
    {
        static IReadOnlyList<HostRole> Roles(string kind, string slots) => HostConfiguration.Parse(RoleConfig(
            $"[{{\"kind\":\"{kind}\",\"endpoint\":\"http://127.0.0.1:11435/\",\"model\":\"qwen3:8b\",\"slots\":{slots}}}]")).Roles;
        var deep = Assert.Single(Roles("deep-thinking", "3"));
        Assert.Equal(3, deep.Slots);
        var route = ((Martlet.Gateway.Ollama.OllamaRelayWorker)NativeHostPlatform.RoleWorker(deep)).Route;
        Assert.Equal((Martlet.Core.Settings.SelfHostSetup.DeepThinkingRouteId, 3), (route.RouteId, route.MaximumConcurrency));
        // Without slots (every install before the choice) it runs one at a time, as before.
        var one = Assert.Single(HostConfiguration.Parse(RoleConfig(
            "[{\"kind\":\"deep-thinking\",\"endpoint\":\"http://127.0.0.1:11435/\",\"model\":\"qwen3:8b\"}]")).Roles);
        Assert.Equal(1, ((Martlet.Gateway.Ollama.OllamaRelayWorker)NativeHostPlatform.RoleWorker(one)).Route.MaximumConcurrency);
        // Only the deep-thinking role has slots, from one to the bound.
        Assert.Throws<HostInputException>(() => Roles("ollama", "2"));
        foreach (var bad in new[] { "0", "5", "\"2\"", "-1", "1.5" })
            Assert.Throws<HostInputException>(() => Roles("deep-thinking", bad));
    }

    [Fact]
    public void Default_No_binding_factories_are_inert_even_with_invalid_inputs()
    {
        Assert.False(DurableGatewayHost.CreateNewForBinding(null!, null!, null!, default, null!, null!).Enabled);
        Assert.False(DurableGatewayHost.OpenExistingForBinding(null!, null!, default, null!, null!, null!).Enabled);
        Assert.False(DurableGatewayHost.RebindForLocalHost(null!, null!, default, null!, null!, null!).Enabled);
    }

    [Theory]
    [InlineData("/srv/martlet")]
    [InlineData("/srv/martlet/host.json")]
    [InlineData("/srv/martlet/service-approval.json")]
    [InlineData("/srv/martlet/service-approval.staging")]
    [InlineData("/srv/martlet/service-approval.staging/identity")]
    public async Task Control_file_collisions_fail_before_creating_state(string state)
    {
        using var platform = new FixturePlatform();
        ConfigurationTests.WriteConfig(platform.Fs, Encoding.UTF8.GetBytes(
            Encoding.UTF8.GetString(ConfigurationTests.Config()).Replace("/srv/martlet/store", state, StringComparison.Ordinal)));
        using var output = new StringWriter();
        platform.Terminal = new("yes");
        Assert.Equal(2, await platform.Run("init", output));
        Assert.Equal(0, platform.Opens);
        Assert.Single(platform.Fs.Parent.Children);
    }

    [Fact]
    public void Passive_config_and_receipt_custody_never_create_authority_or_lock()
    {
        using var fs = new FakeLinuxFileSystem();
        WriteConfig(fs, Config());
        fs.Calls.Clear();
        using (var dir = new LinuxControlDirectory("/srv/martlet/host.json", fs))
        {
            var config = HostConfiguration.Parse(dir.Read(LinuxControlDirectory.Config, 8192)!);
            config.CheckIdentity(dir);
            Assert.Null(dir.Read(LinuxControlDirectory.Approval, 8192));
            Assert.Single(fs.Parent.Children);
        }
        Assert.Equal(0, fs.OpenHandles);
        Assert.DoesNotContain(fs.Calls, call => call.StartsWith("write:", StringComparison.Ordinal) || call == "lock");
    }

    [Fact]
    public void Receipt_is_bound_to_bytes_uid_gid_and_identity_and_removed_without_touching_state()
    {
        using var fs = new FakeLinuxFileSystem();
        WriteConfig(fs, Config());
        using var dir = new LinuxControlDirectory("/srv/martlet/host.json", fs);
        var config = HostConfiguration.Parse(Config());
        var identity = new GatewayHostIdentity { HostId = "fixture-host", SpkiFingerprint = "sha256:" + new string('a', 64) };
        dir.WriteApproval(ServiceApproval.Create(config, identity));
        var approval = ServiceApproval.Parse(dir.Read(LinuxControlDirectory.Approval, 8192)!);
        approval.Check(config, dir);
        Assert.Equal(identity, approval.Identity);
        Assert.Throws<HostApprovalException>(() => (approval with { ServiceGid = 1001 }).Check(config, dir));
        Assert.Throws<HostApprovalException>(() => (approval with { HostId = "different" }).Check(config, dir));
        WriteConfig(fs, Config().Concat(new byte[] { 32 }).ToArray());
        Assert.Throws<HostApprovalException>(() => approval.Check(config, dir));
        var changed = HostConfiguration.Parse(fs.Parent.Children["host.json"].Bytes);
        Assert.Throws<HostApprovalException>(() => approval.Check(changed, dir));
        dir.RemoveApproval();
        Assert.Null(dir.Read(LinuxControlDirectory.Approval, 8192));
        Assert.Single(fs.Parent.Children);
    }

    [Theory]
    [InlineData("file-mode")]
    [InlineData("file-owner")]
    [InlineData("file-links")]
    [InlineData("file-acl")]
    [InlineData("parent-mode")]
    [InlineData("parent-acl")]
    [InlineData("filesystem")]
    public void Unsafe_custody_fails_without_repair(string problem)
    {
        using var fs = new FakeLinuxFileSystem();
        WriteConfig(fs, Config());
        var file = fs.Parent.Children["host.json"];
        switch (problem)
        {
            case "file-mode": file.Identity = file.Identity with { Mode = 0x81a4 }; break;
            case "file-owner": file.Identity = file.Identity with { User = 2000 }; break;
            case "file-links": file.Identity = file.Identity with { Links = 2 }; break;
            case "file-acl": file.Acl = true; break;
            case "parent-mode": fs.Parent.Identity = fs.Parent.Identity with { Mode = 0x41ed }; break;
            case "parent-acl": fs.Parent.Acl = true; break;
            case "filesystem": fs.SupportedFileSystem = false; break;
        }
        var bytes = file.Bytes.ToArray();
        Assert.Throws<GatewayPersistenceException>(() =>
        {
            using var dir = new LinuxControlDirectory("/srv/martlet/host.json", fs);
            dir.Read(LinuxControlDirectory.Config, 8192);
        });
        Assert.Equal(bytes, file.Bytes);
        Assert.Equal(0, fs.OpenHandles);
    }

    [Fact]
    public void Character_models_and_pieces_are_kept_beside_host_json()
    {
        using var fs = new FakeLinuxFileSystem();
        WriteConfig(fs, Config());
        using var dir = new LinuxControlDirectory("/srv/martlet/host.json", fs);
        var storage = new ControlCharacterModelStorage(dir);
        Assert.Null(storage.LoadLibrary());
        var list = Encoding.UTF8.GetBytes("{\"schema_version\":1,\"models\":[]}");
        storage.SaveLibrary(list);
        Assert.Equal(list, storage.LoadLibrary());
        var piece = new byte[3 * 1024 * 1024];
        new Random(7).NextBytes(piece);
        var sha256 = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(piece));
        Assert.False(storage.HasChunk(sha256));
        Assert.Null(storage.LoadChunk(sha256));
        storage.SaveChunk(sha256, piece);
        Assert.True(storage.HasChunk(sha256));
        Assert.Equal(piece, storage.LoadChunk(sha256));
        Assert.Equal(0x8180, fs.Parent.Children[$"character-model-chunk-{sha256}.bin"].Identity.Mode);
        Assert.DoesNotContain(fs.Parent.Children.Keys, name => name.EndsWith(".staging", StringComparison.Ordinal));
        Assert.Throws<GatewayPersistenceException>(() => storage.SaveChunk(sha256, new byte[3 * 1024 * 1024 + 1]));
        Assert.Throws<GatewayPersistenceException>(() => storage.HasChunk("../" + sha256[3..]));
        Assert.Throws<GatewayPersistenceException>(() => storage.LoadChunk(sha256.ToUpperInvariant()));
        storage.RemoveChunk(sha256);
        Assert.False(storage.HasChunk(sha256));
        storage.RemoveChunk(sha256);
        Assert.Equal(["character-models.json", "host.json"], fs.Parent.Children.Keys.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void Creations_and_pieces_are_kept_beside_host_json()
    {
        using var fs = new FakeLinuxFileSystem();
        WriteConfig(fs, Config());
        using var dir = new LinuxControlDirectory("/srv/martlet/host.json", fs);
        var storage = new ControlCreationStorage(dir);
        Assert.Null(storage.LoadLibrary());
        var list = Encoding.UTF8.GetBytes("{\"schema_version\":1,\"creations\":[]}");
        storage.SaveLibrary(list);
        Assert.Equal(list, storage.LoadLibrary());
        var piece = new byte[3 * 1024 * 1024];
        new Random(11).NextBytes(piece);
        var sha256 = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(piece));
        Assert.False(storage.HasChunk(sha256));
        storage.SaveChunk(sha256, piece);
        Assert.True(storage.HasChunk(sha256));
        Assert.Equal(piece, storage.LoadChunk(sha256));
        Assert.Equal(0x8180, fs.Parent.Children[$"creation-chunk-{sha256}.bin"].Identity.Mode);
        Assert.DoesNotContain(fs.Parent.Children.Keys, name => name.EndsWith(".staging", StringComparison.Ordinal));
        Assert.Throws<GatewayPersistenceException>(() => storage.SaveChunk(sha256, new byte[3 * 1024 * 1024 + 1]));
        Assert.Throws<GatewayPersistenceException>(() => storage.HasChunk("../" + sha256[3..]));
        storage.RemoveChunk(sha256);
        Assert.False(storage.HasChunk(sha256));
        Assert.Equal(["creations.json", "host.json"], fs.Parent.Children.Keys.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void Staging_leftover_is_not_overwritten_or_mistaken_for_approval()
    {
        using var fs = new FakeLinuxFileSystem();
        WriteConfig(fs, Config());
        using var dir = new LinuxControlDirectory("/srv/martlet/host.json", fs);
        var bytes = ServiceApproval.Create(HostConfiguration.Parse(Config()),
            new() { HostId = "fixture-host", SpkiFingerprint = "sha256:" + new string('a', 64) });
        fs.Fault = call => { if (call == "rename:service-approval.staging:service-approval.json") throw new IOException(); };
        // The injected write failure leaves the exact staging inode for explicit reconciliation.
        fs.TransferLimit = 0;
        Assert.Throws<GatewayPersistenceException>(() => dir.WriteApproval(bytes));
        fs.TransferLimit = int.MaxValue;
        Assert.Throws<GatewayPersistenceException>(() => dir.WriteApproval(bytes));
        Assert.Null(dir.Read(LinuxControlDirectory.Approval, 8192));
    }
}
