using Martlet.Core.Settings;
using Martlet.Core.Sync;

namespace Martlet.Core.Tests;

public sealed class KnownProgramsFileTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 13, 18, 0, 0, TimeSpan.Zero);

    [Fact]
    public void A_program_is_kept_by_its_file_name_and_found_ignoring_case_and_exe()
    {
        var known = new KnownProgramsFile().With(new("StarRail.exe", " Honkai:\tStar Rail ", true, KnownProgramsFile.ByWeb, At));
        var found = Assert.IsType<KnownProgram>(known.Find("starrail"));
        Assert.Equal(("StarRail", "Honkai: Star Rail", true), (found.Program, found.Name, found.Game));
        Assert.Same(found, known.Find("STARRAIL.EXE"));
        Assert.Null(known.Find(""));
        var replaced = known.With(new("starrail", "Star Rail", false, KnownProgramsFile.ByOwner, At));
        Assert.Single(replaced.Programs);
        Assert.False(replaced.Find("StarRail")!.Game);
    }

    [Fact]
    public void Merging_keeps_both_computers_programs_the_newer_one_and_the_owners_choice()
    {
        var mine = new KnownProgramsFile()
            .With(new("StarRail", "Star Rail", true, KnownProgramsFile.ByFiles, At))
            .With(new("PaintStudio", "Paint Studio", false, KnownProgramsFile.ByOwner, At));
        var theirs = new KnownProgramsFile()
            .With(new("StarRail", "Honkai: Star Rail", true, KnownProgramsFile.ByWeb, At.AddMinutes(1)))
            .With(new("PaintStudio", "Paint Studio", true, KnownProgramsFile.ByWeb, At.AddHours(1)))
            .With(new("FrostRunner", "Frost Runner", true, KnownProgramsFile.ByFiles, At));
        var merged = KnownProgramsFile.Merge(theirs, mine);
        Assert.Equal(3, merged.Programs.Count);
        Assert.Equal("Honkai: Star Rail", merged.Find("StarRail")!.Name);
        Assert.Equal(KnownProgramsFile.ByOwner, merged.Find("PaintStudio")!.By);
        Assert.Equal(merged.Write(), KnownProgramsFile.Merge(mine, theirs).Write());
    }

    [Fact]
    public void A_saved_list_reads_back_and_a_broken_or_newer_one_is_refused()
    {
        var folder = Directory.CreateTempSubdirectory("martlet-known-programs-");
        try
        {
            var known = new KnownProgramsFile().With(new("StarRail", "Honkai: Star Rail", true, KnownProgramsFile.ByWeb, At));
            KnownProgramsFile.Save(folder.FullName, known);
            Assert.Equal(known.Write(), KnownProgramsFile.Load(folder.FullName).Write());
            Assert.Null(KnownProgramsFile.Parse("{\"schema_version\":2,\"programs\":[]}"));
            Assert.Null(KnownProgramsFile.Parse("{\"programs\":[{\"program\":\"a\",\"name\":\"A\",\"game\":true,\"by\":\"guess\",\"at\":\"2026-10-13T00:00:00Z\"}]}"));
            Assert.Null(KnownProgramsFile.Parse("not json"));
            Assert.Contains(SettingsFiles.All, file => file.FileName == KnownProgramsFile.FileName && file.Scope == SettingsFileScope.Shared &&
                file.SharedKey == KnownProgramsFile.SharedKey);
            Assert.Equal(SettingScopes.Household, SettingScopes.Of(KnownProgramsFile.SharedKey));
        }
        finally { folder.Delete(recursive: true); }
    }

    [Fact]
    public async Task Two_computers_end_with_both_lists_through_the_shared_settings()
    {
        var root = Directory.CreateTempSubdirectory("martlet-known-programs-sync-");
        try
        {
            var a = Path.Combine(root.FullName, "a");
            var b = Path.Combine(root.FullName, "b");
            KnownProgramsFile.Save(a, new KnownProgramsFile().With(new("StarRail", "Honkai: Star Rail", true, KnownProgramsFile.ByWeb, At)));
            KnownProgramsFile.Save(b, new KnownProgramsFile().With(new("FrostRunner", "Frost Runner", true, KnownProgramsFile.ByFiles, At)));
            var nodeA = new SharedSettingsNode(a, "desktop-a", [KnownProgramsFile.Section(a)]);
            var nodeB = new SharedSettingsNode(b, "desktop-b", [KnownProgramsFile.Section(b)]);
            var now = DateTimeOffset.UtcNow;
            for (var round = 0; round < 3; round++)
            {
                await nodeA.SyncAsync([nodeB.Document], true, now = now.AddSeconds(15), CancellationToken.None);
                await nodeB.SyncAsync([nodeA.Document], true, now = now.AddSeconds(15), CancellationToken.None);
            }
            var listA = KnownProgramsFile.Load(a);
            Assert.Equal(listA.Write(), KnownProgramsFile.Load(b).Write());
            Assert.NotNull(listA.Find("StarRail"));
            Assert.NotNull(listA.Find("FrostRunner"));
        }
        finally { root.Delete(recursive: true); }
    }
}
