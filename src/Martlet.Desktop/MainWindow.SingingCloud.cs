using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Martlet.Core.Cluster;
using Martlet.Core.Contracts;
using Martlet.Core.Settings;
using Martlet.Core.Singing;
using Martlet.Presentation;

namespace Martlet.Desktop;

/// <summary>
/// Companion › Singing's cloud providers (<see cref="SingingCloud"/>): a cloud member's line and Settings in the music and voice match
/// lists (its key, and for Mureka whether it sings in Martlet's voice), and each list's Add form (the provider, its model, a key and the
/// owner's agreement that each song may cost money). One key per provider serves both lists; it is kept in Windows Credential Manager
/// and travels to the owner's other computers (<see cref="SingingCloudKeys"/>). Automation IDs: the Add form's
/// <c>SingingCloudProvider-&lt;area&gt;</c>, <c>SingingCloudModel-&lt;area&gt;</c>, <c>SingingCloudKey-&lt;area&gt;</c>,
/// <c>SingingCloudKeyStatus-&lt;area&gt;</c>, <c>SingingCloudConsent-&lt;area&gt;</c>, <c>SingingCloudAdd-&lt;area&gt;</c> and
/// <c>SingingCloudState-&lt;area&gt;</c>; a member's <c>Pool-&lt;area&gt;-CloudKey-&lt;key&gt;</c>,
/// <c>Pool-&lt;area&gt;-CloudKeyStatus-&lt;key&gt;</c>, <c>Pool-&lt;area&gt;-CloudSaveKey-&lt;key&gt;</c> and
/// <c>Pool-singing-OwnVoice-&lt;key&gt;</c>.
/// </summary>
public partial class MainWindow
{
    /// <summary>What the last Add did, by area ID (shown under the form).</summary>
    private readonly Dictionary<string, string> singingCloudStates = new(StringComparer.Ordinal);

    private async Task SaveSingingKeyAsync(string provider, PasswordBox box)
    {
        if (store is null || closing) return;
        var name = SingingCloud.Name(provider);
        using (var entered = box.SecurePassword)
            if (entered.Length == 0) { ActionText.Text = $"Paste your {name} API key first."; return; }
        var key = TakeKey(box);
        try
        {
            await SingingCloudKeys.SaveAsync(store.DataDirectory, provider, key, lifetime.Token);
            QueueSettingsSync();
            ErrorLog.Info($"Singing: saved a {name} key for Singing.");
            ActionText.Text = $"Saved the {name} key for Singing in Windows Credential Manager, and your other computers get it too.";
        }
        catch (OperationCanceledException) { }
        catch (Exception error) when (error is InvalidOperationException or ContractException) { ActionText.Text = error.Message; }
        finally
        {
            key.Dispose();
            if (!closing && openTab == CompanionTab.Singing) RenderTab();
        }
    }

    private async Task AddSingingCloudAsync(PoolArea area, string provider, string model, PasswordBox keyBox, bool consent)
    {
        if (store is null || closing) return;
        var name = SingingCloud.Name(provider);
        string Say(string text) => ActionText.Text = singingCloudStates[area.Id] = text;
        if (!consent)
        {
            Say($"Tick the box to agree, then press Add {name}.");
            RenderTab();
            return;
        }
        if (!SingingCloud.Model(model))
        {
            Say("Write the model as the provider names it (letters, digits and - _ . / :).");
            RenderTab();
            return;
        }
        var directory = store.DataDirectory;
        SecretLease? key = null;
        try
        {
            var member = PoolMember.Cloud(provider, model).WithConsent(area.Id, DateTimeOffset.UtcNow);
            var list = WorkSharingRoster.Shared.Pool(directory, area) ?? new PoolList { Area = area.Id };
            if (list.Find(member.Key) is not null)
                throw new ContractException(ErrorCode.InvalidContract, $"{SingingCloud.Describe(member, area)} is in the list already.");
            using (var entered = keyBox.SecurePassword)
                if (entered.Length > 0) key = TakeKey(keyBox);
            if (key is null && !SingingCloudKeys.Has(directory, provider)) throw new ContractException(ErrorCode.InvalidContract, $"Paste your {name} API key first.");
            if (key is not null) await SingingCloudKeys.SaveAsync(directory, provider, key, lifetime.Token);
            if (!PoolSettings.SaveFor(directory, area, list.With(member)))
                throw new InvalidOperationException($"Couldn't save the {area.Title} list on this PC.");
            QueueSettingsSync();
            ErrorLog.Info($"Singing: added {SingingCloud.Describe(member, area)} to the {area.Title} list (the owner agreed to pay).");
            Say($"{SingingCloud.Describe(member, area)} is now last in the list (move it up to use it sooner)" +
                (key is null ? "." : ", and its API key is saved in Windows Credential Manager."));
        }
        catch (OperationCanceledException) { }
        catch (Exception error) when (error is ContractException or InvalidOperationException) { Say(error.Message); }
        finally
        {
            key?.Dispose();
            if (!closing && openTab == CompanionTab.Singing) RenderTab();
        }
    }
}
