using Martlet.Core.Cluster;
using Martlet.Core.Settings;

namespace Martlet.Core.Tests;

// The Vision and Hearing lists (docs/SENSE_MODELS.md, The image and audio pools): members as the places sense jobs go, who may
// receive pictures and recordings, and the first lists made from sense-models.json.
public sealed class SenseListsTests
{
    private static readonly SenseHost Diva = new("diva", "https://10.0.0.5:9443/", "sha256:" + new string('0', 64), "device", Guid.NewGuid());
    private static SenseHost? Hosts(string id) => id == "diva" ? Diva : null;
    private static PoolMember With(PoolMember member, string model) => member.WithSetting(PoolSettingKeys.Model, model);

    [Fact]
    public void Each_kind_of_member_becomes_the_place_its_jobs_go()
    {
        var area = PoolAreas.Vision;
        var key = Guid.NewGuid();
        var cloud = PoolMember.Cloud("openrouter", "qwen/qwen2.5-vl-72b-instruct", "https://openrouter.ai/api/v1");
        var server = With(PoolMember.Service("https://192.168.1.9:8443/v1/"), "qwen2.5vl:7b");
        var keys = new PoolKeys().With(area.Id, cloud.Key, key);

        var thisPc = SenseLists.Model(With(PoolMember.ThisPc(), "qwen2.5vl:3b"), area, Hosts, keys)!;
        Assert.Equal((DeepThinkingPlace.Endpoint, GenerationSupport.LocalOllamaChatBaseUrl, "qwen2.5vl:3b"), (thisPc.Place, thisPc.Origin, thisPc.ModelId));
        Assert.Equal(key, SenseLists.Model(cloud, area, Hosts, keys)!.CredentialId);
        Assert.Equal("https://openrouter.ai/api/v1", SenseLists.Model(cloud, area, Hosts, keys)!.Origin);
        Assert.Equal("https://192.168.1.9:8443/v1", SenseLists.Model(server, area, Hosts, keys)!.Origin);
        Assert.Null(SenseLists.Model(server, area, Hosts, keys)!.CredentialId);

        var role = SenseLists.Model(With(PoolMember.Computer("diva"), "qwen2.5vl:7b"), area, Hosts, keys)!;
        Assert.Equal(("host:diva", SelfHostSetup.DeepThinkingRouteId, Diva.Origin, Diva.CredentialId), (role.Key, role.HostRouteId, role.HostOrigin, role.HostCredentialId));
        var ollama = SenseLists.Model(With(PoolMember.Computer("diva"), "qwen2.5vl:7b").WithSetting(PoolSettingKeys.Engine, SenseLists.OllamaEngine), area, Hosts, keys)!;
        Assert.Equal(SelfHostSetup.OllamaRouteId, ollama.HostRoute);
        Assert.Equal("host:diva#gpu2", SenseLists.Model(With(PoolMember.Gpu("diva", 2), "qwen2.5vl:7b"), area, Hosts, keys)!.Key);

        // No model chosen, a computer not paired here, a card past the last Thinking pool card: no place on this PC.
        Assert.Null(SenseLists.Model(PoolMember.ThisPc(), area, Hosts, keys));
        Assert.Null(SenseLists.Model(With(PoolMember.Computer("ripley"), "qwen2.5vl:7b"), area, Hosts, keys));
        Assert.Null(SenseLists.Model(With(PoolMember.Gpu("diva", 8), "qwen2.5vl:7b"), area, Hosts, keys));
    }

    [Fact]
    public void Pictures_and_recordings_go_outside_this_pc_only_with_the_owners_agreement()
    {
        var area = PoolAreas.Hearing;
        Assert.True(SenseLists.MayReceive(PoolMember.ThisPc(), area));
        Assert.True(SenseLists.MayReceive(PoolMember.Computer("diva"), area));
        Assert.True(SenseLists.MayReceive(PoolMember.Service("http://127.0.0.1:1234/v1"), area));
        var server = PoolMember.Service("https://192.168.1.9:8443/v1");
        Assert.False(SenseLists.MayReceive(server, area));
        Assert.True(SenseLists.MayReceive(server.WithSetting(SenseLists.MediaSetting, SenseLists.Allowed), area));
        var cloud = PoolMember.Cloud("openrouter", "google/gemini-2.5-flash", "https://openrouter.ai/api/v1");
        Assert.False(SenseLists.MayReceive(cloud, area));
        Assert.True(SenseLists.MayReceive(cloud.WithConsent(area.Id, DateTimeOffset.UtcNow), area));
        // An agreement for pictures isn't one for recordings.
        Assert.False(SenseLists.MayReceive(cloud.WithConsent(PoolAreas.Vision.Id, DateTimeOffset.UtcNow), area));
    }

    [Fact]
    public void The_places_follow_the_list_order_and_leave_out_what_this_pc_may_not_use()
    {
        var area = PoolAreas.Vision;
        var list = new PoolList
        {
            Area = area.Id,
            Members =
            [
                With(PoolMember.Gpu("diva", 2), "qwen2.5vl:7b"),
                With(PoolMember.ThisPc(), "qwen2.5vl:3b") with { Off = true },
                With(PoolMember.Computer("diva"), "qwen2.5vl:7b") with { OnlyFor = ["other-pc"] },
                PoolMember.Cloud("openai", "gpt-4o", "https://api.openai.com/v1"),
                With(PoolMember.Service("https://192.168.1.9:8443/v1"), "qwen2.5vl:7b").WithSetting(SenseLists.MediaSetting, SenseLists.Allowed)
            ]
        };
        Assert.Equal(["host:diva#gpu2", "endpoint:https://192.168.1.9:8443/v1|qwen2.5vl:7b"],
            SenseLists.Models(area, list, "this-pc-device", Hosts, new PoolKeys()).Select(m => m.Key));
        Assert.Empty(SenseLists.Models(area, null, "this-pc-device", Hosts, new PoolKeys()));
    }

    [Fact]
    public void The_first_lists_come_from_sense_models_json_with_their_keys()
    {
        var key = Guid.NewGuid();
        var at = new DateTimeOffset(2026, 10, 8, 19, 0, 0, TimeSpan.Zero);
        var legacy = new SenseModels
        {
            Image = new() { Source = SenseSource.Own, Own = new() { Place = DeepThinkingPlace.Endpoint, Origin = "https://openrouter.ai/api/v1", ModelId = "acme/sight", CredentialId = key } },
            Audio = new() { Source = SenseSource.OtherSense }
        };
        var keys = new PoolKeys();
        var vision = SenseLists.FromSenseModels(SenseKind.Image, legacy, ref keys, at);
        var hearing = SenseLists.FromSenseModels(SenseKind.Audio, legacy, ref keys, at);
        var member = Assert.Single(vision.Members);
        Assert.Equal(("cloud:openrouter@https://openrouter.ai/api/v1/acme/sight", true), (member.Key, member.Consented(PoolAreas.Vision.Id)));
        // The audio model was the same as the image model: the same member, agreed to for recordings, with the same key.
        Assert.True(Assert.Single(hearing.Members).Consented(PoolAreas.Hearing.Id));
        Assert.Equal((key, key), (keys.For("vision", member.Key), keys.For("hearing", member.Key)));

        var local = new SenseModels { Image = new() { Source = SenseSource.Own, Own = new() { Place = DeepThinkingPlace.Endpoint, Origin = GenerationSupport.LocalOllamaChatBaseUrl, ModelId = "qwen2.5vl:7b" } } };
        var mine = Assert.Single(SenseLists.FromSenseModels(SenseKind.Image, local, ref keys, at).Members);
        Assert.Equal(("this-pc", "qwen2.5vl:7b"), (mine.Key, mine.Setting(PoolSettingKeys.Model)));
        Assert.Empty(SenseLists.FromSenseModels(SenseKind.Audio, local, ref keys, at).Members);

        var card = new DeepThinkingSettings
        {
            Place = DeepThinkingPlace.Host, ModelId = "qwen2.5vl:7b", HostId = "diva", HostOrigin = Diva.Origin, HostSpkiFingerprint = Diva.SpkiFingerprint,
            HostDeviceId = "device", HostCredentialId = Diva.CredentialId, HostRouteId = SelfHostSetup.DeepThinkingRouteIdFor(2)
        };
        Assert.Equal("host:diva#gpu2", SenseLists.Member(card, PoolAreas.Vision, at).Key);
        var onOllama = SenseLists.Member(card with { HostRouteId = null }, PoolAreas.Vision, at);
        Assert.Equal(("host:diva", SenseLists.OllamaEngine), (onOllama.Key, onOllama.Setting(PoolSettingKeys.Engine)));
    }

    [Fact]
    public void Paired_computers_are_read_from_hosts_json()
    {
        var directory = Path.Combine(Path.GetTempPath(), "martlet-sense-hosts-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var credential = new byte[16];
            credential[0] = 7;
            var id = System.Buffers.Text.Base64Url.EncodeToString(credential);
            File.WriteAllText(Path.Combine(directory, "hosts.json"), $$$"""
                {"version":1,"hosts":[{"pairing":{"origin":"https://10.0.0.5:9443/","hostId":"diva","spkiFingerprint":"sha256:pin","deviceId":"desk","credentialId":"{{{id}}}"}},
                {"pairing":{"hostId":"broken"}}]}
                """);
            var hosts = SenseLists.ReadHosts(directory);
            var diva = Assert.Single(hosts.Values);
            Assert.Equal(("diva", "https://10.0.0.5:9443/", "sha256:pin", "desk", new Guid(credential)), (diva.HostId, diva.Origin, diva.SpkiFingerprint, diva.DeviceId, diva.CredentialId));
            Assert.Empty(SenseLists.ReadHosts(Path.Combine(directory, "missing")));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }
}
