using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Martlet.Core.Contracts;
using Martlet.Core.Settings;

namespace Martlet.Desktop;

/// <summary>A job page's Keys from before: the keys Martlet set aside when the job stopped using them (a cloud provider's key
/// after a switch, or an old pairing key). They stay in Windows Credential Manager, so switching back needs no new key, until
/// the owner removes them here. The card shows only when the job has such a key.</summary>
public partial class MainWindow
{
    /// <summary>The keys set aside for <paramref name="role"/>, newest first.</summary>
    internal static IReadOnlyList<PendingCredentialRemoval> OldKeys(AppSettings? settings, SetupRole role) =>
        (settings?.Setup?.PendingRemovals ?? []).Where(removal => removal.Role == role).Reverse().ToArray();

    /// <summary>A key set aside, in words: "your OpenAI key", "your OpenRouter key", "your key for 192.168.1.5:8000" or
    /// "the pairing key for diva-host".</summary>
    internal static string OldKeyName(PendingCredentialRemoval removal) => removal.Scope switch
    {
        null or { RouteType: SetupRouteType.OpenAi } => "your OpenAI key",
        { RouteType: SetupRouteType.ElevenLabs } => "your ElevenLabs key",
        { RouteType: SetupRouteType.ChatCompletions } scope => ChatCompletionsEndpointCatalog.Named(scope.Origin) is { } named
            ? $"your {named.Name} key"
            : $"your key for {(Uri.TryCreate(scope.Origin, UriKind.Absolute, out var uri) ? uri.Authority : scope.Origin)}",
        { HostId: { Length: > 0 } host } => $"the pairing key for {host}",
        _ => "an old pairing key"
    };

    private Border? OldKeysCard(CompanionTab section, SetupRole role)
    {
        var keys = OldKeys(homeSettings, role);
        if (keys.Count == 0) return null;
        var stack = new List<UIElement>
        {
            Heading("Keys from before"),
            Note($"When {HostJob.For(role)!.Title} stopped using these keys, Martlet kept them in Windows Credential Manager, so " +
                "switching back needs no new key. Remove a key you don't need anymore.", new Thickness(0, 0, 0, 8))
        };
        Border? card = null;
        var left = keys.Count;
        for (var i = 0; i < keys.Count; i++)
        {
            var removal = keys[i];
            var name = OldKeyName(removal);
            var text = new TextBlock { Text = Capitalized(name), TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
            AutomationProperties.SetAutomationId(text, $"SetupOldKey-{section}-{i}");
            var row = new DockPanel { Margin = new Thickness(0, 0, 0, 6) };
            var remove = PageButton("Remove", () => RemoveAsync().Forget(), id: $"SetupOldKeyRemove-{section}-{i}");
            AutomationProperties.SetName(remove, $"Remove {name}");
            remove.VerticalAlignment = VerticalAlignment.Center;
            DockPanel.SetDock(remove, Dock.Right);
            row.Children.Add(remove);
            row.Children.Add(text);
            stack.Add(row);

            // A page with unsaved edits isn't drawn again after the removal, so the row (and the card after the last one) goes here.
            async Task RemoveAsync()
            {
                if (!await RemoveOldKeyAsync(removal)) return;
                row.Visibility = Visibility.Collapsed;
                if (--left == 0 && card is not null) card.Visibility = Visibility.Collapsed;
            }
        }
        card = Card([.. stack]);
        return card;
    }

    /// <summary>Removes a key set aside for good, after the owner confirms: it is deleted from Windows Credential Manager and
    /// leaves the list. Returns whether it was removed.</summary>
    private async Task<bool> RemoveOldKeyAsync(PendingCredentialRemoval removal)
    {
        if (store is null || setupService is null || closing) return false;
        var name = OldKeyName(removal);
        if (!ConfirmationDialog.Confirm(this, $"Remove {name} from this PC? You can't undo this.", "Remove a key from before",
                "Remove", "Keep it", "OldKeyRemoveQuestion"))
            return false;
        ChangeTurns.Turn? turn = null;
        var token = lifetime.Token;
        try
        {
            turn = await ChangeTurnAsync();
            var loaded = await setupService.LoadAsync(token);
            if (loaded.Error is not null) throw new InvalidOperationException(loaded.Error.Summary);
            var settings = SetupSettings.Begin(loaded.Settings);
            // Another change can have used the key again, or removed it, since the page was drawn.
            if (settings.Setup?.PendingRemovals.Contains(removal) != true)
            {
                homeSettings = loaded.Settings;
                ActionText.Text = $"{Capitalized(name)} isn't kept from before anymore.";
                return false;
            }
            var service = setupService;
            var removed = await Task.Run(() => service.RemoveDetachedAsync(settings, loaded.Revision, removal, token), token);
            if (!removed.Save.Saved) throw new InvalidOperationException(removed.Summary);
            homeSettings = removed.Settings;
            FollowSavedSetup(removed.Save.Revision);
            ActionText.Text = $"Removed {name} from this PC.";
            return true;
        }
        catch (OperationCanceledException) { return false; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException or ContractException or JsonException)
        {
            ActionText.Text = error.Message;
            return false;
        }
        finally
        {
            turn?.Dispose();
            if (!closing) RenderHome();
        }
    }
}
