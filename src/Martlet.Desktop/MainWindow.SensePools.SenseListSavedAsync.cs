using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Martlet.Conversation;
using Martlet.Core.Cluster;
using Martlet.Core.Contracts;
using Martlet.Core.Settings;
using Martlet.Credentials.Windows;
using Martlet.Presentation;
using Martlet.Providers;
using Martlet.Providers.Ollama;

namespace Martlet.Desktop;

/// <summary>More of MainWindow.SensePools.cs, in a file of its own so that each file stays under 20 KB.</summary>
public partial class MainWindow
{
    /// <summary>After the list was saved: keys of members that left are removed (when no other member uses them), the running
    /// conversation follows the list, and a new first model's server is asked what it takes.</summary>
    private async Task SenseListSavedAsync(SenseKind kind, PoolList before, PoolList after)
    {
        if (store is null) return;
        var area = SenseLists.Area(kind);
        var directory = store.DataDirectory;
        var keys = PoolKeys.Load(directory);
        var gone = before.Members.Where(m => after.Find(m.Key) is null && keys.For(area.Id, m.Key) is not null).ToArray();
        if (gone.Length > 0)
        {
            var next = keys;
            foreach (var member in gone) next = next.With(area.Id, member.Key, null);
            if (next.Save(directory) && homeSettings?.Profile.Id is { } profile && profile != Guid.Empty)
            {
                var vault = new WindowsCredentialStore();
                foreach (var member in gone)
                {
                    var credential = keys.For(area.Id, member.Key)!.Value;
                    var origin = member.Kind == PoolMemberKind.Cloud ? SenseLists.BaseUrl(member) : member.Address?.TrimEnd('/');
                    if (origin is not null && !next.Keys.Values.Contains(credential))
                        await Task.Run(() => vault.Delete(KeyPlace(origin).Binding(profile, credential)), CancellationToken.None);
                }
            }
        }
        conversation?.ReloadSenseModels();
        var thinking = homeSettings.ThinkingRoute();
        var setup = SavedSenseSetup();
        var route = SenseRouting.For(kind, setup.Senses, thinking, SavedModelAbilities());
        ErrorLog.Info($"{(kind == SenseKind.Image ? "Image" : "Audio")} models: {SenseNow(kind, setup.Senses, setup.For(kind).Count, thinking)} {route.Why}");
        if (setup.Senses.Place(kind) is { Place: DeepThinkingPlace.Endpoint } first) await CheckChosenModelAsync(first, thinking);
    }
}
