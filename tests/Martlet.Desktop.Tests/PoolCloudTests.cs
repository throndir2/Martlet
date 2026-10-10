using System.IO;
using Martlet.Avatar.Hosting;
using Martlet.Core.Cluster;
using Martlet.Core.Settings;
using Martlet.Core.Sync;
using Martlet.Desktop;
using Martlet.Providers;

namespace Martlet.Desktop.Tests;

public sealed class PoolCloudTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 9, 22, 0, 0, TimeSpan.Zero);

    private static PoolMember Agreed(PoolMember member, PoolArea area) => member.WithConsent(area.Id, Now);

    [Fact]
    public void A_cloud_member_uses_its_own_key_or_the_route_the_job_kept_aside()
    {
        var directory = Directory.CreateTempSubdirectory("martlet-pool-cloud-").FullName;
        try
        {
            var openai = Agreed(PoolMember.Cloud("openai", "gpt-4o-mini-tts").WithSetting(PoolSettingKeys.Voice, "alloy"), PoolAreas.Speaking);
            // No key on this PC: it can't take a turn here.
            Assert.False(PoolCloud.Usable(directory, SetupRole.Tts, openai));
            var key = Guid.NewGuid();
            Assert.True(new PoolKeys().With(PoolAreas.Speaking.Id, openai.Key, key).Save(directory));
            WorkSharingRoster.Forget();
            var use = PoolCloud.Find(directory, SetupRole.Tts, openai);
            Assert.NotNull(use);
            Assert.Equal((SetupRouteType.OpenAi, "openai-tts", "alloy", key), (use.Type, use.Alias, use.Voice, use.CredentialId));
            // A Speaking member needs a voice; Listening's key is its own.
            Assert.Null(PoolCloud.Find(directory, SetupRole.Tts, openai.WithSetting(PoolSettingKeys.Voice, null)));
            Assert.Null(PoolCloud.Find(directory, SetupRole.Stt, Agreed(PoolMember.Cloud("openai", "gpt-4o-mini-tts"), PoolAreas.Listening)));

            // The route Speaking kept aside when it moved to a computer: that route's key and voice.
            var saved = new SharedRoute { Type = SharedRoute.OpenAi, Model = "tts-1-hd", Voice = "nova" }.Build(SetupRole.Tts) with
            {
                CredentialId = Guid.NewGuid()
            };
            JobSavedRoute.Save(directory, HostJob.Speaking.SavedFile, saved);
            WorkSharingRoster.Forget();
            var member = PoolCloud.MemberOf(saved)!;
            Assert.Equal("cloud:openai/tts-1-hd", member.Key);
            var kept = PoolCloud.Find(directory, SetupRole.Tts, member with { Settings = new Dictionary<string, string>() });
            Assert.Equal((SetupRouteType.OpenAi, "nova", saved.CredentialId!.Value), (kept!.Type, kept.Voice, kept.CredentialId));

            // ElevenLabs with its own key: its own alias and origin, the cloned voice from its settings.
            var eleven = Agreed(PoolMember.Cloud("elevenlabs", ElevenLabsSetup.ModelIds[0]).WithSetting(PoolSettingKeys.Voice, "voice0123456789abcde"),
                PoolAreas.Speaking);
            Assert.True(PoolKeys.Load(directory).With(PoolAreas.Speaking.Id, eleven.Key, Guid.NewGuid()).Save(directory));
            WorkSharingRoster.Forget();
            var elevenUse = PoolCloud.Find(directory, SetupRole.Tts, eleven);
            Assert.Equal((SetupRouteType.ElevenLabs, ElevenLabsSetup.Alias, ElevenLabsSetup.Origin), (elevenUse!.Type, elevenUse.Alias, elevenUse.Origin));

            // The Speaking list's stops: the computer first, then the cloud member that has a key here.
            HostRegistry.Save(directory, [new PairedHost { Pairing = new AvatarRemoteHost
            {
                Origin = "https://192.168.1.11:9443/", HostId = "m1-host", SpkiFingerprint = "sha256:" + new string('a', 64),
                DeviceId = "desktop-test", CredentialId = new string('B', 22)
            } }]);
            Assert.True(PoolSettings.SaveFor(directory, PoolAreas.Speaking, new PoolList
            {
                Area = PoolAreas.Speaking.Id, Members = [PoolMember.Computer("m1-host"), openai, Agreed(PoolMember.Cloud("openai", "tts-1"), PoolAreas.Speaking)]
            }));
            WorkSharingRoster.Forget();
            var places = WorkSharingRoster.Places(directory, WorkSharingJobs.Speaking, "chatterbox", null, "m1-host",
                m => PoolCloud.Usable(directory, SetupRole.Tts, m));
            Assert.Equal(["m1-host", openai.Key], places.Select(p => p.Host?.HostId ?? p.Cloud!.Key));
            // Order (the computers only) is unchanged for its other callers.
            Assert.Equal(["m1-host"], WorkSharingRoster.Order(directory, WorkSharingJobs.Speaking, "chatterbox", null, "m1-host").Select(p => p.Host!.HostId));
        }
        finally
        {
            WorkSharingRoster.Forget();
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public void A_rate_limit_is_busy_and_a_missing_key_or_outage_moves_on()
    {
        Assert.Equal(WorkRefusal.Busy, WorkSharingRoster.Classify(new PoolCloud.Refused(ProviderFailureCode.RateLimited, 429)));
        Assert.Equal(WorkRefusal.Unavailable, WorkSharingRoster.Classify(new PoolCloud.Refused(ProviderFailureCode.CredentialUnavailable)));
        Assert.Equal(WorkRefusal.Unavailable, WorkSharingRoster.Classify(new PoolCloud.Refused(ProviderFailureCode.Server, 500)));
        Assert.Equal(WorkRefusal.Unavailable, WorkSharingRoster.Classify(new ElevenLabsException(ProviderFailureCode.QuotaExceeded)));
        Assert.Equal(WorkRefusal.Busy, WorkSharingRoster.Classify(new ElevenLabsException(ProviderFailureCode.RateLimited)));
        Assert.Equal(WorkRefusal.Unavailable, WorkSharingRoster.Classify(new CredentialUnavailableException()));
        Assert.Equal(WorkRefusal.None, WorkSharingRoster.Classify(new PoolCloud.Refused(ProviderFailureCode.VoiceUnsupported)));
    }
}
