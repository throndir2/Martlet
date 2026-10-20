using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Threading;
using Martlet.HostClient;
using Martlet.Core.Creations;
using Martlet.Presentation;

namespace Martlet.Desktop;

/// <summary>Creations: everything Martlet made (songs now, other kinds later), kept in the signed-in account's folder and the
/// same on every Martlet computer where that account is signed in, through the paired hosts (docs/CREATIONS.md, docs/ACCOUNTS.md). The page lists them and shows each one's text and details, with
/// Rename and Delete. It has no Play, Show or Activate: Martlet performs its creations itself when asked in conversation
/// (perform_creation).</summary>
public partial class MainWindow
{
    private readonly DispatcherTimer creationTimer = new() { Interval = TimeSpan.FromSeconds(30) };
    private bool creationBusy, creationQueued;
    private CreationSync? creationSync;
    private string creationStatus = "No other Martlet computers are paired yet, so your creations stay on this PC.";
    private string? selectedCreation;

    /// <summary>Where the signed-in account keeps its creations: its account folder (docs/ACCOUNTS.md); the data folder when this
    /// PC's account session couldn't be opened; null until Martlet can use its data folder.</summary>
    private string? CreationsFolder => accounts?.AccountFolder ?? store?.DataDirectory;

    /// <summary>An account change step (at start and on every switch): the creations kept in the data folder before accounts move
    /// once into the folder of the first account that signs in here (on an updated desktop, the owner), recorded in
    /// accounts\creations-moved.json. A move that fails is logged and tried again at the next change; it never stops a switch.</summary>
    internal static async Task MoveDataFolderCreationsAsync(AccountChange change, CancellationToken token)
    {
        if (CreationAccounts.Moved(change.HouseholdFolder) is not null) return;
        try
        {
            var move = await Task.Run(() => CreationAccounts.MoveDataFolderCreationsOnceAsync(change.HouseholdFolder, change.To, change.ToFolder,
                DateTimeOffset.UtcNow, token), token);
            if (move.Creations + move.Assets > 0)
                ErrorLog.Info($"Creations: moved {move.Creations} creation{(move.Creations == 1 ? "" : "s")} and {move.Assets} asset " +
                    $"file{(move.Assets == 1 ? "" : "s")} from the data folder to account {AccountSession.Short(change.To)}.");
        }
        catch (Exception error) when (CreationStore.IsFailure(error))
        {
            ErrorLog.Warn("Creations: couldn't move the data folder's creations to the account; trying again at the next account change.", error);
        }
    }

    private static string Capitalize(string text) => text.Length == 0 ? text : char.ToUpper(text[0], CultureInfo.CurrentCulture) + text[1..];

    private static string SizeText(long bytes) => bytes >= 1024 * 1024 ? $"{bytes / (1024d * 1024):0.#} MB" : $"{Math.Max(1, bytes / 1024)} KB";
}
