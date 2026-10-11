using Martlet.Core.Contracts;
using Martlet.Core.Voices;
using Xunit;

namespace Martlet.Core.Tests;

public sealed class SpeakingVoiceLibraryTests
{
    [Fact]
    public void AFullListAtItsMostStillFitsAndAFullListSaysSo()
    {
        // Every voice and tombstone at its most: text JSON escapes (no ASCII letters), the most recordings, the largest
        // numbers, revisions and writers.
        static string Text(int length, int seed) => string.Concat(Enumerable.Range(0, length).Select(i => (char)('а' + (i + seed) % 32)));
        static string Sha(int n) => n.ToString("x64");
        var at = new DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.FromHours(5.5)).AddTicks(1_234_567);
        var writer = new string('w', 64);
        var revision = long.MaxValue / 4;
        const int rate = 48_000;
        var each = (SpeakingVoiceLibrary.MaximumTranscriptLength - (SpeakingVoiceLibrary.MaximumClips - 1)) / SpeakingVoiceLibrary.MaximumClips;
        var samples = rate * SpeakingVoiceLibrary.MaximumDurationMilliseconds / 1000 / SpeakingVoiceLibrary.MaximumClips;
        var voices = Enumerable.Range(1, SpeakingVoiceLibrary.MaximumVoices).Select(n =>
        {
            var clips = Enumerable.Range(0, SpeakingVoiceLibrary.MaximumClips).Select(c => new SpeakingVoiceClip
            {
                Transcript = Text(c == 0 ? SpeakingVoiceLibrary.MaximumTranscriptLength - (SpeakingVoiceLibrary.MaximumClips - 1) * (each + 1) : each, n + c),
                StartSample = c * samples, SampleCount = samples
            }).ToArray();
            return SpeakingVoiceLibrary.Voice(Text(SpeakingVoiceLibrary.MaximumNameLength, n), SpeakingVoiceLibrary.JoinedTranscript(clips),
                Sha(n), SpeakingVoiceLibrary.MaximumDurationMilliseconds, SpeakingVoiceRights.ExplicitPermission,
                Text(SpeakingVoiceLibrary.MaximumNoteLength, n), at, revision, writer, at, clips, rate);
        }).ToArray();
        Assert.All(voices, v => Assert.Equal(SpeakingVoiceLibrary.MaximumTranscriptLength, v.Transcript!.Length));
        var tombstones = Enumerable.Range(1, SpeakingVoiceLibrary.MaximumTombstones).Select(n => new SpeakingVoice
        {
            Id = Sha(1_000_000 + n), Removed = true, Revision = revision, UpdatedAt = at, UpdatedBy = writer
        });
        var full = new SpeakingVoiceLibrary
        {
            SchemaVersion = SpeakingVoiceLibrary.SchemaVersion1, Voices = [.. voices, .. tombstones],
            Chosen = new() { VoiceId = voices[0].Id, Revision = revision, UpdatedAt = at, UpdatedBy = writer }
        };
        var bytes = full.Write();
        Assert.True(bytes.Length <= SpeakingVoiceLibrary.MaximumBytes, $"{bytes.Length:N0} of {SpeakingVoiceLibrary.MaximumBytes:N0} bytes");
        var parsed = SpeakingVoiceLibrary.Parse(bytes);
        Assert.Equal(SpeakingVoiceLibrary.MaximumVoices, parsed.Live.Count);

        var error = Assert.Throws<ContractException>(() => parsed.Add("One more", "Hello there.", Sha(7), 2_000,
            SpeakingVoiceRights.OwnVoice, "desk-a", at));
        Assert.Equal(ErrorCode.PayloadTooLarge, error.Code);
        Assert.Contains("full", error.Message);
    }

    [Fact]
    public void AMergeKeepsEveryLiveVoiceButAddStillRefusesPastTheLimit()
    {
        var now = new DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);
        static string Sha(int n) => n.ToString("x64");
        SpeakingVoice Voice(int n, string by) => SpeakingVoiceLibrary.Voice($"Voice {n}", "Hello there.", Sha(n), 2_000,
            SpeakingVoiceRights.OwnVoice, null, now, 2, by, now);
        var a = new SpeakingVoiceLibrary
        {
            SchemaVersion = SpeakingVoiceLibrary.SchemaVersion1,
            Voices = Enumerable.Range(1, SpeakingVoiceLibrary.MaximumVoices).Select(n => Voice(n, "desk-a")).ToArray()
        };
        var b = new SpeakingVoiceLibrary { SchemaVersion = SpeakingVoiceLibrary.SchemaVersion1, Voices = [Voice(50_001, "desk-b"), Voice(50_002, "desk-b")] };
        // Two computers' copies together pass the limit: no live voice is dropped, and the list reads back whole.
        var merged = SpeakingVoiceLibrary.Merge(a, b);
        Assert.Equal(SpeakingVoiceLibrary.MaximumVoices + 2, merged.Live.Count);
        var copy = SpeakingVoiceLibrary.Parse(merged.Write());
        Assert.Equal(merged.Digest(), copy.Digest());
        var error = Assert.Throws<ContractException>(() => copy.Add("One more", "Hello again.", Sha(60_000), 2_000,
            SpeakingVoiceRights.OwnVoice, "desk-a", now));
        Assert.Equal(ErrorCode.PayloadTooLarge, error.Code);

        // Tombstones past the limit: only the oldest leave.
        var tombstones = Enumerable.Range(1, SpeakingVoiceLibrary.MaximumTombstones + 5).Select(n => new SpeakingVoice
        {
            Id = Sha(1_000_000 + n), Removed = true, Revision = n, UpdatedAt = now, UpdatedBy = "desk-a"
        }).ToArray();
        var trimmed = SpeakingVoiceLibrary.Merge(SpeakingVoiceLibrary.Empty,
            new SpeakingVoiceLibrary { SchemaVersion = SpeakingVoiceLibrary.SchemaVersion1, Voices = tombstones });
        Assert.Equal(SpeakingVoiceLibrary.MaximumTombstones, trimmed.Voices.Count);
        Assert.All(Enumerable.Range(1, 5), n => Assert.Null(trimmed.Find(Sha(1_000_000 + n))));
    }
}
