using System.Security.Cryptography;
using Martlet.Core.Voices;

namespace Martlet.F5.Tests;

public sealed class BundledVoicesTests
{
    [Fact]
    public void StarterVoicesAreTheSixRecordingsAsReadWithJennyFirstAndTheDefault()
    {
        Assert.Equal(["jenny-dioco", "librivox-annie", "librivox-woollybee", "lj-speech", "arctic-slt", "arctic-bdl"],
            F5BundledVoices.All.Select(voice => voice.Key));
        Assert.Equal("jenny-dioco", F5BundledVoices.Default.Key);
        Assert.Equal(["librivox-annie", "librivox-woollybee"], F5BundledVoices.All.Where(voice => voice.Cute).Select(voice => voice.Key));
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
    public async Task A057ListLosesTheAnimeVoicesWithoutWaitingForThemAndStartsWithJenny()
    {
        using var scope = new F5TestScope();
        var anime = F5BundledVoices.Retired.Where(voice => voice.Transcript is not null).ToArray();
        var animeIds = anime.Select(voice => SpeakingVoiceLibrary.ReferenceId(voice.AudioSha256, voice.Transcript!)).ToArray();
        // The list Martlet 0.57.0 starts with: seven starter entries at revision 1, "Annie (cute anime girl)" first and chosen.
        var older = SpeakingVoiceLibrary.Empty.Seed(new[] { "retired-librivox-annie-anime", "librivox-annie",
                "retired-librivox-woollybee-anime", "librivox-woollybee", "lj-speech", "arctic-slt", "arctic-bdl" }.Select(Entry))
            .Choose(animeIds[0], "older-desktop", DateTimeOffset.UtcNow);

        var result = await F5SharedVoices.ReconcileAsync(scope.StoreDirectory, Path.Combine(scope.Root, "incoming"), F5TestData.Destination,
            older, (_, _) => Task.FromResult<byte[]?>(null), "this-desktop", DateTimeOffset.UtcNow);

        Assert.All(animeIds, id => Assert.True(result.Library.Find(id)!.Removed));
        // The chosen voice left the list, so Martlet speaks with the first voice: Jenny, the default.
        Assert.Null(result.Library.ChosenVoice);
        Assert.Equal(F5BundledVoices.All.Select(voice => voice.AudioSha256), result.Library.Live.Select(voice => voice.AudioSha256));
        Assert.Equal(F5BundledVoices.Default.AudioSha256, result.Library.Live[0].AudioSha256);
        Assert.Equal(F5BundledVoices.All.Count, result.Local.Count);
        Assert.Equal(F5BundledVoices.All.Count, result.Added);
        Assert.Equal(0, result.Waiting);
        // An older copy merged back in can't bring them back.
        Assert.All(animeIds, id => Assert.True(SpeakingVoiceLibrary.Merge(result.Library, older).Find(id)!.Removed));
        Assert.Same(result.Library, F5SharedVoices.WithoutRetired(result.Library, "this-desktop", DateTimeOffset.UtcNow));
    }

    private static (string Name, string Transcript, string AudioSha256, int DurationMilliseconds, string? Note) Entry(string key)
    {
        if (F5BundledVoices.Find(key) is { } starter)
            return (starter.Name, starter.Transcript, starter.AudioSha256, starter.Check().DurationMilliseconds, starter.Description);
        var retired = F5BundledVoices.Retired.Single(voice => voice.Key == key);
        return (retired.Name, retired.Transcript!, retired.AudioSha256, 7_000, null);
    }
}
