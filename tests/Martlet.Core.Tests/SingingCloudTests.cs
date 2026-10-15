using System.Buffers.Binary;
using System.Text.Json;
using Martlet.Core.Cluster;
using Martlet.Core.Singing;

namespace Martlet.Core.Tests;

public sealed class SingingCloudTests
{
    [Fact]
    public void Cloud_members_join_the_pool_only_when_this_pc_can_use_them()
    {
        var eleven = PoolMember.Cloud(SingingCloud.ElevenLabs, SingingCloud.ElevenLabsMusicModel).WithConsent(PoolAreas.Singing.Id, DateTimeOffset.UnixEpoch);
        var fal = PoolMember.Cloud(SingingCloud.Fal, SingingCloud.FalMiniMax).WithConsent(PoolAreas.Singing.Id, DateTimeOffset.UnixEpoch);
        var list = new PoolList { Area = PoolAreas.Singing.Id, Members = [eleven, PoolMember.Computer("m1"), fal] };

        var members = SingingPool.Members(list, "desk-1", null, null, ["m1"], [], PoolAreas.Singing, m => m.Provider == SingingCloud.ElevenLabs);

        Assert.Equal([SingingCloud.ElevenLabs, null], members.Select(m => m.Member.Provider));
        Assert.Equal([null, "m1"], members.Select(m => m.HostId));
        // A provider the owner did not agree to is never used.
        var unagreed = list with { Members = [PoolMember.Cloud(SingingCloud.Mureka, SingingCloud.MurekaModel)] };
        Assert.Empty(SingingPool.Members(unagreed, "desk-1", null, null, [], [], PoolAreas.Singing, _ => true));
        Assert.True(SingingCloud.WritesMusic(eleven));
        Assert.False(SingingCloud.MatchesVoice(fal));
        Assert.Equal("fal.ai (MiniMax Music)", SingingCloud.Describe(fal, PoolAreas.Singing));
        Assert.Equal("ElevenLabs Voice Changer", SingingCloud.Describe(eleven, PoolAreas.VoiceMatch));
        var mureka = PoolMember.Cloud(SingingCloud.Mureka, SingingCloud.MurekaModel).WithSetting(SingingCloud.VoiceSetting, SingingCloud.OwnVoice);
        Assert.True(SingingCloud.SingsInVoice(mureka));
    }

    [Fact]
    public void Xl_is_used_only_where_it_is_set_up()
    {
        var xl = SingingPool.WithMusicModel(PoolMember.Computer("m1"), SongMusicModel.AceStep15Xl);
        Assert.Equal(SongMusicModel.AceStep15Xl, SingingPool.MusicModel(xl));
        Assert.Equal(SongMusicModel.AceStep15, SingingPool.MusicModel(SingingPool.WithMusicModel(xl, SongMusicModel.AceStep15)));

        using var plain = JsonDocument.Parse("""{"state":"ready","kinds":["song","music","match","separate"]}""");
        using var withXl = JsonDocument.Parse("""{"state":"ready","music_models":["ace-step-v15","ace-step-v15-xl"]}""");
        var plainState = SingerState.Parse(plain.RootElement);
        Assert.True(plainState.Separates);
        Assert.Equal(SongMusicModel.AceStep15, SingingPool.MusicWith(xl, plainState));
        Assert.Equal(SongMusicModel.AceStep15Xl, SingingPool.MusicWith(xl, SingerState.Parse(withXl.RootElement)));
        Assert.False(SingerState.Parse(withXl.RootElement).Separates);
    }

    [Fact]
    public void Singing_keys_save_and_share_only_known_providers()
    {
        var directory = Directory.CreateTempSubdirectory("martlet-singing-keys-").FullName;
        try
        {
            var id = Guid.NewGuid();
            Assert.True(new SingingKeys().With(SingingCloud.Mureka, id).With(SingingCloud.Fal, Guid.Empty).Save(directory));
            var loaded = SingingKeys.Load(directory);
            Assert.Equal(id, loaded.For(SingingCloud.Mureka));
            Assert.Null(loaded.For(SingingCloud.Fal));

            Assert.Equal([SingingCloud.Mureka, SingingCloud.Fal], SingingKeys.Parse(SingingKeys.Share([SingingCloud.Mureka, SingingCloud.Fal])));
            Assert.Null(SingingKeys.Parse("""{"providers":["suno"]}"""));
            Assert.Equal(["a", "b"], SingingKeys.Unbundle(SingingKeys.Bundle(["a", "b"])));
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public void An_unseparated_mix_splits_into_middle_and_sides_that_add_back_up()
    {
        var mix = Track(2, i => (short)(i * 7 % 2000), i => (short)(-i * 3 % 1500));
        var (vocals, backing) = SongVoice.Unseparated(mix);
        Assert.Equal(1, vocals.Channels);
        Assert.Equal(2, backing.Channels);
        Assert.Equal(mix.Frames, vocals.Frames);
        for (var i = 0; i < mix.Frames; i++)
        {
            var middle = Sample(vocals, i);
            Assert.Equal(Sample(mix, 2 * i), middle + Sample(backing, 2 * i));
            Assert.Equal(Sample(mix, 2 * i + 1), middle + Sample(backing, 2 * i + 1));
        }
    }

    [Fact]
    public void Matched_vocals_are_silent_where_the_song_does_not_sing_and_lines_follow_the_singing()
    {
        // One second of silence, one second sung, one second of silence.
        var original = Track(1, i => i is >= 48_000 and < 96_000 ? (short)(Math.Sin(i * 0.05) * 8000) : (short)0);
        var converted = Track(1, _ => 3000);
        var gated = SongVoice.Gate(converted, original);
        Assert.Equal(original.Frames, gated.Frames);
        Assert.Equal(0, Sample(gated, 10_000));
        Assert.Equal(0, Sample(gated, 130_000));
        Assert.NotEqual(0, Sample(gated, 70_000));

        var lines = SongVoice.SpreadLines("[verse]\nfirst line\nsecond line", original);
        Assert.Equal(2, lines.Count);
        Assert.InRange(lines[0].Start.TotalSeconds, 0.95, 1.05);
        Assert.InRange(lines[1].End!.Value.TotalSeconds, 1.9, 2.05);
    }

    private static SongTrack Track(int channels, Func<int, short> left, Func<int, short>? right = null)
    {
        const int Frames = 144_000;
        var pcm = new byte[Frames * channels * 2];
        for (var i = 0; i < Frames; i++)
        {
            BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan(i * channels * 2), left(i));
            if (channels == 2) BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan(i * 4 + 2), (right ?? left)(i));
        }
        return new SongTrack(channels == 2 ? SongTrackKind.Backing : SongTrackKind.Vocals, 48_000, channels, pcm);
    }

    private static int Sample(SongTrack track, int index) => BinaryPrimitives.ReadInt16LittleEndian(track.Pcm16.Span[(2 * index)..]);
}
