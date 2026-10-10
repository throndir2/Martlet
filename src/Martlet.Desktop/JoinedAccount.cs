using System.IO;
using System.Text.Json;
using Martlet.Core.Accounts;

namespace Martlet.Desktop;

/// <summary>
/// joined-account.json in Martlet's data folder: the household account this PC signed in to when it joined through a host
/// (Join with an invite, docs/NETWORK.md) and the host's signed attestation of it (docs/ACCOUNTS.md). The desktop's account
/// session signs that account in on this device once this PC is a member of the network (the attestation checks against the
/// roster then). No secret: the attestation names this device and is a public fact of the account directory.
/// </summary>
internal static class JoinedAccount
{
    internal const string FileName = "joined-account.json";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    internal sealed record Record(string HostId, Guid AccountId, string Login, string Attestation, DateTimeOffset At);

    internal static void Save(string directory, string hostId, Guid accountId, AccountAttestation attestation)
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, FileName);
        var temporary = Path.Combine(directory, $"joined-account.{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllBytes(temporary, JsonSerializer.SerializeToUtf8Bytes(
                new Record(hostId, accountId, attestation.Login.ToString(), attestation.ToText(), DateTimeOffset.UtcNow), Json));
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    /// <summary>The account this PC joined as, or null (none, or unreadable).</summary>
    internal static Record? Load(string directory)
    {
        try
        {
            var path = Path.Combine(directory, FileName);
            return File.Exists(path) ? JsonSerializer.Deserialize<Record>(File.ReadAllBytes(path), Json) : null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException) { return null; }
    }

    /// <summary>Forgets it once the account is signed in here (or the statement can't be used any more).</summary>
    internal static void Forget(string directory)
    {
        try { File.Delete(Path.Combine(directory, FileName)); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }
}
