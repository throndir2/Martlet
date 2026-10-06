using Martlet.Core.Speakers;
using Martlet.Desktop;
using Xunit;

namespace Martlet.Desktop.Tests;

/// <summary>The last few clips of a voice nobody has named yet, kept on this PC until the owner says who it is.</summary>
public sealed class VoiceClipsTests : IDisposable
{
    private static readonly DateTimeOffset Start = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);
    private readonly string directory = Path.Combine(Path.GetTempPath(), "martlet-clips-" + Guid.NewGuid().ToString("N"));

    private static float[] Print(int seed)
    {
        var random = new Random(seed);
        return VoicePrints.Normalize(Enumerable.Range(0, VoicePrints.Dimension).Select(_ => (float)(random.NextDouble() - 0.5)).ToArray());
    }

    private static float[] Tone(double seconds) =>
        Enumerable.Range(0, (int)(seconds * 16_000)).Select(i => (float)Math.Sin(i / 10.0) * 0.3f).ToArray();

    private (LocalVoices Voices, KnownVoice A, KnownVoice B) Two()
    {
        var roster = VoiceRoster.Empty;
        (roster, var a) = roster.Add(Print(1), 3, "desk", Start);
        (roster, var b) = roster.Add(Print(2), 3, "desk", Start);
        roster = roster.AddHeardName(a!.Id, "no name yet", "desk", Start).AddHeardName(a.Id, "Sam", "desk", Start);
        var voices = new LocalVoices(directory, "desk-test");
        voices.Merge(roster);
        return (voices, a, b!);
    }

    [Fact]
    public void AVoiceKeepsItsNewestClipsUntilItIsNamed()
    {
        var (voices, a, b) = Two();
        using var _ = voices;
        for (var i = 0; i < VoiceClips.MaximumClips + 2; i++) voices.Clips.Save(a.Id, Tone(i == 0 ? 12 : 1), Start.AddMinutes(i));
        voices.Clips.Save(b.Id, Tone(0.2), Start); // too short to hear anything
        var clips = voices.Clips.List(a.Id);
        Assert.Equal(VoiceClips.MaximumClips, clips.Count);
        Assert.Equal(Start.AddMinutes(VoiceClips.MaximumClips + 1), clips[0].At);
        Assert.Equal(1, clips[0].Seconds);
        Assert.Empty(voices.Clips.List(b.Id));
        Assert.Empty(voices.Clips.List("..\\..\\x"));

        // A learned name isn't the owner saying who it is; typing one is.
        voices.SetNames(a.Id, null, ["Sam"]);
        Assert.Equal(VoiceClips.MaximumClips, voices.Clips.List(a.Id).Count);
        voices.SetNames(a.Id, "Samantha", ["Sam"]);
        Assert.Empty(voices.Clips.List(a.Id));
        Assert.Equal((0, 0), voices.Clips.Count());
    }

    [Fact]
    public void MergingMovesClipsAndForgettingOrTurningOffDeletesThem()
    {
        var (voices, a, b) = Two();
        using var _ = voices;
        voices.Clips.Save(a.Id, Tone(1), Start);
        voices.Clips.Save(b.Id, Tone(1), Start.AddMinutes(1));
        voices.Join(a.Id, b.Id);
        Assert.Equal(2, voices.Clips.List(b.Id).Count);
        Assert.Equal((1, 2), voices.Clips.Count());
        voices.Forget(b.Id);
        Assert.Equal((0, 0), voices.Clips.Count());

        var (again, c, _) = Two();
        using var __ = again;
        again.Clips.Save(c.Id, Tone(1), Start);
        again.Clips.SetEnabled(false);
        Assert.Equal((0, 0), again.Clips.Count());
        again.Clips.Save(c.Id, Tone(1), Start);
        Assert.Empty(again.Clips.List(c.Id));
        Assert.False(new LocalVoices(directory, "desk-test").Clips.Enabled);
    }

    [Fact]
    public void PlaceholdersLearnedByMistakeAreDropped()
    {
        var (voices, a, _) = Two();
        using var _ = voices;
        Assert.Equal(1, voices.DropCompanionNames(CompanionNames.Martlet));
        Assert.Equal(new[] { "Sam" }, voices.Roster.Resolve(a.Id)!.Names.Select(n => n.Text).ToArray());
    }

    public void Dispose()
    {
        try { Directory.Delete(directory, recursive: true); }
        catch (DirectoryNotFoundException) { }
    }
}
