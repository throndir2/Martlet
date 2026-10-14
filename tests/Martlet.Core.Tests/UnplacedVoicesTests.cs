using Martlet.Core.Speakers;
using Xunit;

namespace Martlet.Core.Tests;

/// <summary>Voices heard only a little (docs/VOICES.md#voices-heard-only-a-little): short remarks and uncertain matches of the
/// last 30 minutes add up to a new voice once a few agree and sound like nobody in the list.</summary>
public sealed class UnplacedVoicesTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 14, 18, 0, 0, TimeSpan.Zero);

    private static float[] Voice(int seed)
    {
        var random = new Random(seed);
        return VoicePrints.Normalize(Enumerable.Range(0, VoicePrints.Dimension).Select(_ => (float)(random.NextDouble() - 0.5)).ToArray());
    }

    // The voice with noise of its own, as one short utterance gives: noise is the noise's size against the voice's, so the
    // utterance scores about 1/sqrt(1 + noise²) against the voice and 1/(1 + noise²) against another utterance of it.
    private static float[] Heard(float[] voice, int variant, double noise)
    {
        var random = new Random(variant * 7919 + 13);
        var amplitude = noise * Math.Sqrt(12) / Math.Sqrt(VoicePrints.Dimension);
        return VoicePrints.Normalize(voice.Select(v => v + (float)((random.NextDouble() - 0.5) * amplitude)).ToArray());
    }

    // A voice that scores about cosine against known: a different person who sounds a little alike.
    private static float[] Alike(float[] known, int seed, double cosine)
    {
        var other = Voice(seed);
        var along = VoicePrints.Cosine(other, known);
        var orthogonal = VoicePrints.Normalize(other.Select((v, i) => (float)(v - along * known[i])).ToArray());
        return VoicePrints.Normalize(known.Select((v, i) => (float)(cosine * v + Math.Sqrt(1 - cosine * cosine) * orthogonal[i])).ToArray());
    }

    private static VoiceRoster Owner() => VoiceRoster.Empty.Add(Voice(1), 30, "desk-a", Start.AddDays(-1)).Roster;

    [Fact]
    public void ShortRemarksOfANewVoiceAddUpToANewVoice()
    {
        var roster = Owner();
        var unplaced = new UnplacedVoices();
        var guest = Voice(5);
        Assert.Null(unplaced.Note(Heard(guest, 1, 0.5), 1.2, Start, roster));
        Assert.Null(unplaced.Note(Heard(guest, 2, 0.5), 1.2, Start.AddMinutes(4), roster));
        Assert.Equal(2, unplaced.Count);

        var group = unplaced.Note(Heard(guest, 3, 0.5), 1.2, Start.AddMinutes(9), roster);

        Assert.NotNull(group);
        Assert.Equal(3, group.Utterances);
        Assert.Equal(3.6, group.Seconds, 2);
        Assert.True(VoicePrints.Cosine(group.Voiceprint, guest) > 0.9);
        Assert.Equal(VoiceMatchKind.New, roster.Identify(group.Voiceprint).Kind);
        Assert.Equal(0, unplaced.Count);
    }

    [Fact]
    public void TooFewTooShortOrTooOldRemarksAddNothing()
    {
        var roster = Owner();
        var guest = Voice(5);

        var two = new UnplacedVoices();
        Assert.Null(two.Note(Heard(guest, 1, 0.5), 1.5, Start, roster));
        Assert.Null(two.Note(Heard(guest, 2, 0.5), 1.5, Start.AddMinutes(1), roster));

        var short_ = new UnplacedVoices();
        for (var i = 0; i < 3; i++) Assert.Null(short_.Note(Heard(guest, i, 0.5), 0.8, Start.AddMinutes(i), roster));

        // The first two are more than 30 minutes old when the third comes: they were forgotten.
        var old = new UnplacedVoices();
        Assert.Null(old.Note(Heard(guest, 1, 0.5), 1.5, Start, roster));
        Assert.Null(old.Note(Heard(guest, 2, 0.5), 1.5, Start.AddMinutes(1), roster));
        Assert.Null(old.Note(Heard(guest, 3, 0.5), 1.5, Start.AddMinutes(32), roster));
        Assert.Equal(1, old.Count);
    }

    [Fact]
    public void DifferentPeopleNeverMakeOneVoice()
    {
        var roster = Owner();
        var unplaced = new UnplacedVoices();
        for (var seed = 10; seed < 16; seed++) Assert.Null(unplaced.Note(Heard(Voice(seed), seed, 0.5), 1.5, Start.AddMinutes(seed - 10), roster));
        Assert.Equal(6, unplaced.Count);
    }

    [Fact]
    public void SomeoneWhoSoundsALittleLikeAKnownVoiceIsAddedOnlyWhenClearlyApart()
    {
        var roster = Owner();
        var owner = Voice(1);
        // Each remark is an uncertain match for the owner (0.45-0.70): one by one, Martlet never adds it.
        var sibling = Alike(owner, 7, 0.52);
        var first = Heard(sibling, 1, 0.3);
        Assert.Equal(VoiceMatchKind.Unsure, roster.Identify(first).Kind);
        var unplaced = new UnplacedVoices();
        Assert.Null(unplaced.Note(first, 2.5, Start, roster));
        Assert.Null(unplaced.Note(Heard(sibling, 2, 0.3), 2.5, Start.AddMinutes(1), roster));
        var group = unplaced.Note(Heard(sibling, 3, 0.3), 2.5, Start.AddMinutes(2), roster);
        Assert.NotNull(group);
        Assert.True(VoicePrints.Cosine(group.Voiceprint, sibling) > 0.95);

        // The owner far from the microphone: each remark is uncertain too, but together they sound like the owner, so no new
        // voice is made from them.
        var far = Alike(owner, 8, 0.8);
        var remark = Heard(far, 0, 0.9);
        Assert.Equal(VoiceMatchKind.Unsure, roster.Identify(remark).Kind);
        var waiting = new UnplacedVoices();
        Assert.Null(waiting.Note(remark, 2.5, Start, roster));
        for (var i = 1; i < 8; i++) Assert.Null(waiting.Note(Heard(far, i, 0.9), 2.5, Start.AddMinutes(i), roster));
    }

    [Fact]
    public void RemarksOfAVoiceAddedMeanwhileAreThatVoiceAndForgotten()
    {
        var guest = Voice(5);
        var unplaced = new UnplacedVoices();
        Assert.Null(unplaced.Note(Heard(guest, 1, 0.5), 1.5, Start, Owner()));
        Assert.Null(unplaced.Note(Heard(guest, 2, 0.5), 1.5, Start.AddMinutes(1), Owner()));
        // The guest then spoke long enough to be added at once: the third remark completes a group that is that voice.
        var roster = Owner().Add(guest, 4, "desk-a", Start.AddMinutes(2)).Roster;
        Assert.Null(unplaced.Note(Heard(guest, 3, 0.5), 1.5, Start.AddMinutes(3), roster));
        Assert.Equal(0, unplaced.Count);

        var again = new UnplacedVoices();
        again.Note(Heard(guest, 1, 0.5), 1.5, Start, Owner());
        again.Note(Heard(Voice(9), 1, 0.5), 1.5, Start, Owner());
        again.Forget(guest);
        Assert.Equal(1, again.Count);
        again.Clear();
        Assert.Equal(0, again.Count);
    }
}
