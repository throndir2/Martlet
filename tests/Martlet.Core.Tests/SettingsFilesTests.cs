using System.Text.RegularExpressions;
using Martlet.Core.Settings;
using Martlet.Core.Sync;

namespace Martlet.Core.Tests;

/// <summary>The settings file registry (<see cref="SettingsFiles"/>) agrees with the shared settings and docs/CLUSTER.md.</summary>
public sealed class SettingsFilesTests
{
    [Fact]
    public void Each_file_is_listed_once_and_its_scope_matches_its_shared_setting()
    {
        Assert.Equal(SettingsFiles.All.Count, SettingsFiles.All.Select(f => f.FileName).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.Contains(SettingsFiles.TalkPreferences, SettingsFiles.All);
        foreach (var file in SettingsFiles.All)
        {
            if (file.Scope == SettingsFileScope.Local)
            {
                Assert.True(file.SharedKey is null, $"{file.FileName} is local, so no shared setting carries it.");
                Assert.False(string.IsNullOrWhiteSpace(file.StaysHere), $"{file.FileName} is local, so it needs the reason it stays here.");
            }
            else
            {
                Assert.False(string.IsNullOrWhiteSpace(file.SharedKey), $"{file.FileName} is shared, so it needs its shared setting.");
                Assert.True(SettingScopes.IsAccountKey(file.SharedKey!) == (file.Scope == SettingsFileScope.Account),
                    $"{file.FileName}: SettingScopes says '{file.SharedKey}' is {SettingScopes.Of(file.SharedKey!)}, the registry says {file.Scope}.");
            }
        }
        Assert.Same(SettingsFiles.TalkPreferences, SettingsFiles.Find(@"talk-preferences.json"));
        Assert.Equal("conversations/platform-changes.json", SettingsFiles.Find(@"conversations\platform-changes.json")?.FileName);
    }

    [Fact]
    public void Every_file_that_stays_on_this_computer_has_its_row_in_cluster_docs()
    {
        var rows = StaysHereRows();
        Assert.NotEmpty(rows);
        foreach (var file in SettingsFiles.All.Where(f => f.StaysHere is not null))
        {
            var named = rows.Where(row => row.What.Replace('\\', '/').Contains($"`{file.FileName}`", StringComparison.Ordinal)).ToArray();
            Assert.True(named.Length > 0,
                $"docs/CLUSTER.md, What stays with each computer, has no row that names `{file.FileName}`. Add one with the reason \"{file.StaysHere}\".");
            Assert.True(named.Any(row => row.Why == file.StaysHere),
                $"docs/CLUSTER.md gives `{file.FileName}` the reason \"{named[0].Why}\"; SettingsFiles says \"{file.StaysHere}\".");
        }
    }

    // The table under "### What stays with each computer": each row's two cells.
    private static IReadOnlyList<(string What, string Why)> StaysHereRows()
    {
        var lines = File.ReadAllLines(ClusterDocs());
        var start = Array.FindIndex(lines, line => line.Trim() == "### What stays with each computer");
        Assert.True(start >= 0, "docs/CLUSTER.md has no \"### What stays with each computer\" section.");
        var rows = new List<(string, string)>();
        foreach (var line in lines.Skip(start + 1).TakeWhile(line => !line.StartsWith('#')))
        {
            var cells = Row().Match(line);
            if (cells.Success && cells.Groups[1].Value.Trim() is var what and not "Stays here" && !what.StartsWith("---", StringComparison.Ordinal))
                rows.Add((what, cells.Groups[2].Value.Trim()));
        }
        return rows;
    }

    private static string ClusterDocs()
    {
        for (var folder = new DirectoryInfo(AppContext.BaseDirectory); folder is not null; folder = folder.Parent)
        {
            var path = Path.Combine(folder.FullName, "docs", "CLUSTER.md");
            if (File.Exists(path)) return path;
        }
        throw new FileNotFoundException("docs/CLUSTER.md was not found above " + AppContext.BaseDirectory);
    }

    private static Regex Row() => new(@"^\|(.*)\|(.*)\|\s*$");
}
