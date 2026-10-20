using Martlet.Core.Lorebooks;
using Martlet.Core.Settings;

namespace Martlet.Desktop.Tests;

/// <summary>What Companion › Lorebook says about the lorebooks (<see cref="LorebookPageModel"/>), without a window.</summary>
public sealed class LorebookPageModelTests
{
    private static readonly PersonaProfile Mira = new() { Id = Guid.NewGuid(), ConfigurationRevision = Guid.NewGuid(), Name = "Mira", Text = "Shy." };

    private static LorebookLibrary Library(params Lorebook[] books) =>
        new() { SchemaVersion = LorebookLibrary.CurrentSchemaVersion, Books = books };

    [Fact]
    public void NowCountsTheBooksAndEntriesOnForThePersona()
    {
        Assert.Equal("No lorebooks yet: import one from SillyTavern or a character card, or write your own.",
            LorebookPageModel.Now(Library(), Mira));
        var world = new Lorebook
        {
            Id = Guid.NewGuid(), Name = "World",
            Entries = [new() { Uid = 1, Content = "A" }, new() { Uid = 2, Content = "B", Constant = true }, new() { Uid = 3, Enabled = false }]
        };
        var other = new Lorebook { Id = Guid.NewGuid(), Name = "Other", Activation = LorebookActivation.Off };
        Assert.Equal("1 of 2 lorebooks enabled for Mira, with 2 entries, 1 always on.", LorebookPageModel.Now(Library(world, other), Mira));
        Assert.Equal("1 lorebook, none enabled for Mira.", LorebookPageModel.Now(Library(other), Mira));
    }

    [Fact]
    public void ABooksLineSaysForWhomItIsOn()
    {
        var only = new Lorebook
        {
            Id = Guid.NewGuid(), Name = "Mira's", Activation = LorebookActivation.SelectedPersonas, PersonaIds = [Mira.Id],
            Entries = [new() { Uid = 1 }]
        };
        Assert.Equal("Only with Mira, 1 entry, used by the current persona.", LorebookPageModel.BookLine(only, Mira.Id, [Mira]));
        Assert.Equal("Only with no current persona, 1 entry.", LorebookPageModel.BookLine(only, Guid.NewGuid(), []));
    }
}
