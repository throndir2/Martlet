using System.IO;
using Martlet.Avatar.Audio2Face.Remote;
using Martlet.Core.Contracts;
using Martlet.Core.Network;

namespace Martlet.Desktop;

/// <summary>This PC's Martlet network key (ECDSA P-256, network\device_ecdsa in Martlet's data folder, created with an ACL
/// for this Windows user alone) and its network state (network.json beside the other local preferences).</summary>
internal static class NetworkIdentity
{
    internal const string Folder = "network";
    internal const string KeyFile = "device_ecdsa";
    private static readonly object FileGate = new();

    internal static string KeyPath(string dataDirectory) => Path.Combine(dataDirectory, Folder, KeyFile);

    /// <summary>This PC's network key, made the first time it is needed.</summary>
    internal static NetworkKey LoadOrCreate(string dataDirectory, string deviceId)
    {
        var path = KeyPath(dataDirectory);
        if (File.Exists(path)) return NetworkKey.FromPem(deviceId, File.ReadAllText(path));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var key = NetworkKey.Create(deviceId);
        try { HostShell.WritePrivate(path, key.ExportPem()); }
        catch
        {
            key.Dispose();
            throw;
        }
        return key;
    }

    /// <summary>Throws away this PC's network key after it was removed from a network; the next one is new, so it can ask to
    /// join again.</summary>
    internal static void Retire(string dataDirectory)
    {
        var path = KeyPath(dataDirectory);
        if (File.Exists(path)) File.Delete(path);
    }

    /// <summary>The device ID this PC pairs with: the one its pairings already use (the most common, if they differ), or
    /// its suggested ID.</summary>
    internal static string DeviceId(IReadOnlyList<PairedHost> hosts) =>
        hosts.GroupBy(h => h.Pairing.DeviceId, StringComparer.Ordinal).OrderByDescending(g => g.Count())
            .ThenBy(g => g.Key, StringComparer.Ordinal).Select(g => g.Key).FirstOrDefault() ?? HostSetupCommands.SuggestedDeviceId();

    /// <summary>This PC's network state; an unreadable file starts empty (the hosts' rosters restore a member).</summary>
    internal static NetworkLocalState Load(string dataDirectory)
    {
        lock (FileGate)
        {
            try
            {
                var state = NetworkLocalState.Parse(File.ReadAllBytes(Path.Combine(dataDirectory, NetworkLocalState.FileName)));
                HostRoutes.Update(state.Roster);
                return state;
            }
            catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException) { return NetworkLocalState.Empty; }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or ContractException)
            {
                ErrorLog.Warn($"{NetworkLocalState.FileName} could not be read ({error.Message}); starting outside a network until a host restores it.");
                return NetworkLocalState.Empty;
            }
        }
    }

    internal static void Save(string dataDirectory, NetworkLocalState state)
    {
        lock (FileGate)
        {
            Directory.CreateDirectory(dataDirectory);
            var path = Path.Combine(dataDirectory, NetworkLocalState.FileName);
            var temporary = Path.Combine(dataDirectory, $"network.{Guid.NewGuid():N}.tmp");
            try
            {
                File.WriteAllBytes(temporary, state.Write());
                File.Move(temporary, path, overwrite: true);
                HostRoutes.Update(state.Roster);
            }
            finally
            {
                if (File.Exists(temporary)) File.Delete(temporary);
            }
        }
    }

    /// <summary>Records that the owner paired <paramref name="hostId"/> here on purpose, so the next network sync adds it to
    /// the network even if it was removed from it before.</summary>
    internal static void Adopt(string dataDirectory, string hostId)
    {
        lock (FileGate)
        {
            try { Save(dataDirectory, Load(dataDirectory).WithAdopted(hostId)); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or ContractException)
            {
                ErrorLog.Warn($"Could not note that {hostId} was paired on purpose in {NetworkLocalState.FileName}: {error.Message}");
            }
        }
    }
}
