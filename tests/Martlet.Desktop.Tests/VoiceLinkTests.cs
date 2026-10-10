using Martlet.Core.Speakers;
using Martlet.Desktop;
using Xunit;

namespace Martlet.Desktop.Tests;

/// <summary>Whose voice is whose (docs/ACCOUNTS.md, Voices): "your voice" is a voice linked to the signed-in account.</summary>
public sealed class VoiceLinkTests : IDisposable
{
    private static readonly DateTimeOffset Start = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid Sam = Guid.Parse("5a0d3b9e6c1f4e2a8b7d9c0e1f2a3b4c"), Alex = Guid.Parse("a1e7c3d5b9f24e6a8c0d2f4b6a8c0e1d");
    private readonly string directory = Path.Combine(Path.GetTempPath(), "martlet-voice-links-" + Guid.NewGuid().ToString("N"));

    private static float[] Print(int seed)
    {
        var random = new Random(seed);
        return VoicePrints.Normalize(Enumerable.Range(0, VoicePrints.Dimension).Select(_ => (float)(random.NextDouble() - 0.5)).ToArray());
    }

    /// <summary>Robert, marked "This is me" by an older Martlet, and Kim.</summary>
    private (LocalVoices Voices, KnownVoice Robert, KnownVoice Kim) Two()
    {
        var roster = VoiceRoster.Empty;
        (roster, var robert) = roster.Add(Print(1), 3, "desk", Start);
        (roster, var kim) = roster.Add(Print(2), 3, "desk", Start);
        roster = roster.SetNames(robert!.Id, "Robert", [], "desk", Start).SetOwner(robert.Id, true, "desk-old", Start)
            .SetNames(kim!.Id, "Kim", [], "desk", Start);
        var voices = new LocalVoices(directory, "desk-test");
        voices.Merge(roster);
        return (voices, robert, kim);
    }

    [Fact]
    public void TheOwnersVoiceLinksToTheOwnerAccountAndYoursFollowsWhoIsSignedIn()
    {
        var (voices, robert, kim) = Two();
        using var _ = voices;
        // Before the desktop knows its account, the voice marked as the owner's is yours, as before accounts.
        Assert.True(voices.IsYours(voices.Roster.Resolve(robert.Id)));
        Assert.False(voices.IsYours(voices.Roster.Resolve(kim.Id)));

        voices.UseAccount(Sam, owner: Sam);
        Assert.Equal(Sam, voices.Roster.Resolve(robert.Id)!.Account);
        Assert.Equal([robert.Id], voices.Yours.Select(v => v.Id));

        // Alex signs in on this PC: Robert's voice is Sam's, not Alex's. Kim's becomes Alex's; Alex can't unlink Sam's.
        voices.UseAccount(Alex, owner: Sam);
        Assert.Empty(voices.Yours);
        voices.Link(kim.Id, true);
        voices.Link(robert.Id, false);
        Assert.Equal(Alex, voices.Roster.Resolve(kim.Id)!.Account);
        Assert.False(voices.Roster.Resolve(kim.Id)!.Owner);
        Assert.Equal(Sam, voices.Roster.Resolve(robert.Id)!.Account);
        Assert.True(voices.Roster.Resolve(robert.Id)!.Owner);
        Assert.Equal([kim.Id], voices.Yours.Select(v => v.Id));

        // The links are saved with the list.
        using var again = new LocalVoices(directory, "desk-test");
        Assert.Equal(Alex, again.Roster.Resolve(kim.Id)!.Account);
        Assert.Equal(Sam, again.Roster.Resolve(robert.Id)!.Account);
    }

    [Fact]
    public void AVoiceAnOlderDesktopMarksLaterLinksToTheOwnerOnMerge()
    {
        var (voices, _, kim) = Two();
        using var _ = voices;
        voices.UseAccount(Alex, owner: Sam);
        var older = voices.Roster.WithoutLinks().SetOwner(kim.Id, true, "desk-old", Start.AddMinutes(5));
        voices.Merge(older);
        Assert.Equal(Sam, voices.Roster.Resolve(kim.Id)!.Account);
        Assert.False(voices.IsYours(voices.Roster.Resolve(kim.Id)));
    }

    [Fact]
    public void TheReplyAndMemoryKnowWhichSpeakerIsYou()
    {
        var (voices, robert, kim) = Two();
        using var _ = voices;
        voices.UseAccount(Sam, owner: Sam);
        var roster = voices.Roster;
        var sam = roster.Resolve(robert.Id)!;
        var other = roster.Resolve(kim.Id)!;
        var heard = new HeardVoices([new(other, VoiceMatchKind.Known, 0.9, 3, false), new(sam, VoiceMatchKind.Known, 0.8, 2, false) { Mine = true }], false);
        var text = VoicePromptContext.Block(heard)!;
        Assert.Contains($"Speaking now: Kim (voice {other.Tag}).", text);
        Assert.Contains($"Also heard in this message: Robert (voice {sam.Tag}; the signed-in user).", text);
        Assert.DoesNotContain("owner", text);

        // "Me" is whoever speaks; with nobody recognized (typed), the signed-in person's voice.
        Assert.Equal(other.Id, MemoryTools.Person("me", roster, other, sam).Voice);
        Assert.Equal(sam.Id, MemoryTools.Person("me", roster, null, sam).Voice);
        Assert.Null(MemoryTools.Person("me", roster, null, null).Voice);
        // A voice linked to someone keeps no clips.
        Assert.False(VoiceClips.Wanted(sam));
        Assert.True(VoiceClips.Wanted(VoiceRoster.Empty.Add(Print(3), 3, "desk", Start).Voice!));
    }

    [Fact]
    public void PeopleSaysHowManyVoicesAreLinkedWithoutNames()
    {
        Assert.Equal("No voice is marked as yours yet. Tick This is me on yours.", MainWindow.PeopleLinkStatus(2, 0, 0, accounts: false));
        Assert.StartsWith("1 of 3 voices is linked to people's accounts. Your account has 1 voice.", MainWindow.PeopleLinkStatus(3, 1, 1, accounts: true));
        Assert.StartsWith("2 of 2 voices are linked to people's accounts. None is yours yet", MainWindow.PeopleLinkStatus(2, 2, 0, accounts: true));
    }

    public void Dispose()
    {
        try { Directory.Delete(directory, recursive: true); }
        catch (DirectoryNotFoundException) { }
    }
}
