using System.Security.Cryptography;
using Martlet.Core.Voices;

namespace Martlet.F5.Tests;

public sealed class BundledVoicesTests
{
    [Fact]
    public void StarterVoicesAreTheSixRecordingsAsReadWithAnnieFirst()
    {
        Assert.Equal(["librivox-annie", "librivox-woollybee", "jenny-dioco", "lj-speech", "arctic-slt", "arctic-bdl"],
            F5BundledVoices.All.Select(voice => voice.Key));
        Assert.Equal("librivox-annie", F5BundledVoices.Default.Key);
        // The Jenny TTS dataset's licence: the voice is referred to as "Jenny (Dioco)".
        Assert.Equal("Jenny (Dioco)", F5BundledVoices.Find("jenny-dioco")!.Name);
        Assert.DoesNotContain(F5BundledVoices.All, voice => voice.Name.Contains("anime", StringComparison.OrdinalIgnoreCase));
        foreach (var voice in F5BundledVoices.All)
        {
            voice.Check();
            Assert.Equal(voice.AudioSha256, Convert.ToHexStringLower(SHA256.HashData(voice.ReadAudio())));
            Assert.False(F5BundledVoices.IsRetired(voice.AudioSha256));
        }
    }

    [Fact]
    public void AStarterVoiceAddedInAnUpdateJoinsAnOlderListOnceAndARemovedOneNeverComesBack()
    {
        var now = DateTimeOffset.UtcNow;
        string Id(string key) => F5BundledVoices.Find(key) is { } voice ? SpeakingVoiceLibrary.ReferenceId(voice.AudioSha256, voice.Transcript)
            : throw new InvalidOperationException(key);
        var jenny = F5BundledVoices.Find("jenny-dioco")!;
        // A list from before Jenny, with LJ removed by the owner.
        var older = SpeakingVoiceLibrary.Empty.Seed(F5SharedVoices.Starters.Where(voice => voice.AudioSha256 != jenny.AudioSha256))
            .Remove(Id("lj-speech"), "owner-desktop", now);
        Assert.Null(older.Find(Id("jenny-dioco")));

        var updated = F5SharedVoices.WithStarters(older);

        Assert.True(updated.Find(Id("jenny-dioco")) is { Removed: false, Revision: SpeakingVoiceLibrary.StarterRevision, UpdatedBy: SpeakingVoiceLibrary.StarterWriter });
        Assert.True(updated.Find(Id("lj-speech"))!.Removed);
        Assert.Same(updated, F5SharedVoices.WithStarters(updated));
        var removed = updated.Remove(Id("jenny-dioco"), "owner-desktop", now);
        Assert.True(F5SharedVoices.WithStarters(removed).Find(Id("jenny-dioco"))!.Removed);
    }

    [Fact]
    public void TheAnimeVoicesAndTheF5SampleAreRetired()
    {
        Assert.Equal(["retired-sample", "retired-librivox-annie-anime", "retired-librivox-woollybee-anime"],
            F5BundledVoices.Retired.Select(voice => voice.Key));
        foreach (var voice in F5BundledVoices.Retired)
        {
            Assert.Null(F5BundledVoices.ForAudio(voice.AudioSha256));
            Assert.Same(voice, F5BundledVoices.RetiredForAudio(voice.AudioSha256.ToUpperInvariant()));
        }
    }

    [Fact]
    public async Task ReconcileDropsRetiredVoicesFromAnOlderListWithoutWaitingForThem()
    {
        using var scope = new F5TestScope();
        var anime = F5BundledVoices.Retired.Where(voice => voice.Transcript is not null).ToArray();
        var animeIds = anime.Select(voice => SpeakingVoiceLibrary.ReferenceId(voice.AudioSha256, voice.Transcript!)).ToArray();
        // An older Martlet's list: its starter entries at revision 1, the anime voices among them, the first one chosen.
        var older = SpeakingVoiceLibrary.Empty
            .Seed(anime.Select(voice => (voice.Name, voice.Transcript!, voice.AudioSha256, 7_000, (string?)null)))
            .Seed(F5SharedVoices.Starters)
            .Choose(animeIds[0], "older-desktop", DateTimeOffset.UtcNow);

        var result = await F5SharedVoices.ReconcileAsync(scope.StoreDirectory, Path.Combine(scope.Root, "incoming"), F5TestData.Destination,
            older, (_, _) => Task.FromResult<byte[]?>(null), "this-desktop", DateTimeOffset.UtcNow);

        Assert.All(animeIds, id => Assert.True(result.Library.Find(id)!.Removed));
        Assert.Equal(F5BundledVoices.All.Select(voice => voice.AudioSha256), result.Library.Live.Select(voice => voice.AudioSha256));
        Assert.Equal(F5BundledVoices.All.Count, result.Local.Count);
        Assert.Equal(F5BundledVoices.All.Count, result.Added);
        Assert.Equal(0, result.Waiting);
        Assert.Null(result.Library.ChosenVoice);
        // An older copy merged back in can't bring them back.
        Assert.All(animeIds, id => Assert.True(SpeakingVoiceLibrary.Merge(result.Library, older).Find(id)!.Removed));
        Assert.Same(result.Library, F5SharedVoices.WithoutRetired(result.Library, "this-desktop", DateTimeOffset.UtcNow));
    }
}
