using System.IO;
using Martlet.Avatar.Hosting;
using Martlet.Conversation;
using Martlet.Desktop;

namespace Martlet.Desktop.Tests;

public sealed class CharacterProfileLocalStoreTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "martlet-profile-local-" + Guid.NewGuid().ToString("N"));

    private static readonly CharacterProfileLocal Tall = new(new RendererPlacement(true, 2120, 300, 520, 693, @"\\.\DISPLAY2", 200, 260),
        GazeMode.Near, false, TouchInterrupts.Intimate);
    private static readonly CharacterProfileLocal Small = new(new RendererPlacement(false, 40, 600, 300, 400));
    private static readonly CharacterProfileLocal Now = new(new RendererPlacement(false, 900, 500, 420, 560), GazeMode.Ahead);

    public void Dispose()
    {
        if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
    }

    [Fact]
    public void EachProfileKeepsItsOwnPlaceEyesAndTouchChoiceAndThePcRemembersWhichItUses()
    {
        var (tall, small) = (Guid.NewGuid(), Guid.NewGuid());
        Assert.True(CharacterProfileLocalStore.Save(directory, CharacterProfilesHere.Empty.With(tall, Tall).With(small, Small).Using(tall)));

        var loaded = CharacterProfileLocalStore.Load(directory);
        Assert.Equal((Tall, Small, (Guid?)tall), (loaded.For(tall), loaded.For(small), loaded.InUse));
        Assert.Null(loaded.For(Guid.NewGuid()));
        // MCP reads the choices as words.
        var text = File.ReadAllText(Path.Combine(directory, CharacterProfileLocalStore.FileName));
        Assert.Contains("\"GazeUsual\":\"near\"", text);
        Assert.Contains("\"TouchInterrupts\":\"intimate\"", text);
        Assert.Contains($"\"InUse\":\"{tall}\"", text);
    }

    [Fact]
    public void ARemovedProfilesChoicesAndUseGoAndNothingLeftRemovesTheFile()
    {
        var (kept, removed) = (Guid.NewGuid(), Guid.NewGuid());
        var here = CharacterProfilesHere.Empty.With(kept, Small).With(removed, Tall).Using(removed).Of([kept]);
        Assert.Equal(new[] { kept }, here.Profiles.Keys);
        Assert.Null(here.InUse);

        Assert.True(CharacterProfileLocalStore.Save(directory, here));
        Assert.True(CharacterProfileLocalStore.Save(directory, here.With(kept, null)));
        Assert.False(File.Exists(Path.Combine(directory, CharacterProfileLocalStore.FileName)));
        Assert.Equal(CharacterProfilesHere.Empty.Profiles, CharacterProfileLocalStore.Load(directory).Profiles);
    }

    [Fact]
    public void ChangesGoToTheProfileInUseOrOneThatKeepsNothingYetButNeverOverAnotherProfilesOwnChoices()
    {
        var (a, b, c) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        var here = CharacterProfilesHere.Empty.With(a, Small).With(b, Tall).Using(a);

        // Martlet is A, whose choices this PC uses: A keeps the change.
        var remembered = here.Remember(a, Now)!;
        Assert.Equal((Now, Tall, (Guid?)a), (remembered.For(a), remembered.For(b), remembered.InUse));
        // Martlet became C by hand, and C keeps nothing here yet: C takes on what this PC uses.
        var adopted = here.Remember(c, Now)!;
        Assert.Equal((Now, Small, (Guid?)c), (adopted.For(c), adopted.For(a), adopted.InUse));
        // Martlet became B by hand, but this PC doesn't use B's own choices: they stay as B left them.
        Assert.Null(here.Remember(b, Now));
        // Before any profile was used here (an update from an older Martlet), the profile Martlet is takes them on.
        Assert.Equal((Guid?)a, CharacterProfilesHere.Empty.Remember(a, Now)!.InUse);
    }

    [Fact]
    public void SwitchingBringsBackWhatTheProfileKeepsOrGivesItWhatThisPcUsesNow()
    {
        var (a, b, c) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        var here = CharacterProfilesHere.Empty.With(a, Small).With(b, Tall).Using(a);

        var (toB, keptB) = here.Switch(b, Now);
        Assert.Equal((Tall, (Guid?)b, Small, Tall), (keptB, toB.InUse, toB.For(a), toB.For(b)));

        var (toC, keptC) = here.Switch(c, Now);
        Assert.Null(keptC);
        Assert.Equal((Now, (Guid?)c, Small), (toC.For(c), toC.InUse, toC.For(a)));
    }

    [Fact]
    public void UnusableValuesAreDroppedAndAnUnreadableFileKeepsNothing()
    {
        var id = Guid.NewGuid();
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, CharacterProfileLocalStore.FileName);
        File.WriteAllText(path, """{"Profiles":{"ID":{"Placement":{"Locked":true,"Left":0,"Top":0,"Width":-5,"Height":560},"GazeUsual":"ahead","GazeFree":false,"TouchInterrupts":7}},"InUse":"00000000-0000-0000-0000-000000000000"}"""
            .Replace("ID", id.ToString()));
        var loaded = CharacterProfileLocalStore.Load(directory);
        Assert.Equal(new CharacterProfileLocal(null, GazeMode.Ahead, false, TouchInterrupts.Any), loaded.For(id));
        Assert.Null(loaded.InUse);

        File.WriteAllText(path, "not json");
        Assert.Empty(CharacterProfileLocalStore.Load(directory).Profiles);
        Assert.True(CharacterProfileLocalStore.Save(directory, CharacterProfilesHere.Empty.With(id, new CharacterProfileLocal(null))));
        Assert.Equal(new CharacterProfileLocal(null), CharacterProfileLocalStore.Load(directory).For(id));
    }

    [Fact]
    public void TheProfileRowSaysWhatItKeepsWithoutNames()
    {
        Assert.StartsWith("On this PC: nothing yet.", MainWindow.ProfileHereText(null));
        Assert.Equal("On this PC: its own spot and size (520 × 693, locked) · Eyes: Follow your mouse when it's near, replies can't change it · " +
            "While it talks: only intimate touches stop it.", MainWindow.ProfileHereText(Tall));
        Assert.Equal("On this PC: no spot of its own · Eyes: As the personality decides · While it talks: any touch stops it.",
            MainWindow.ProfileHereText(new CharacterProfileLocal(null)));
    }
}
