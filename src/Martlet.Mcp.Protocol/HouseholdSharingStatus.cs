using System.IO;
using System.Text.Json;
using Martlet.Core.Sharing;
using Martlet.Core.Sync;

namespace Martlet.Mcp;

/// <summary>household_sharing: what each account of a desktop's household shares (docs/ACCOUNTS.md, "Sharing"), from that data
/// folder's copy of the household settings (shared-settings.json, the <c>sharing.&lt;account&gt;</c> entries) and the account in
/// use (accounts\session.json). Account IDs, character keys (the first 8 hex digits of a character's ID), modes, memory space
/// IDs and counts only: never a name, a personality's text or a lorebook entry. Read-only; contacts nothing.</summary>
internal static class HouseholdSharingStatus
{
    internal static object Read(string dataDirectory)
    {
        var current = CurrentAccount(dataDirectory);
        var path = Path.Combine(dataDirectory, SharedSettingsState.FileName);
        if (!File.Exists(path)) return new { state = "none", currentAccount = current?.ToString("N") };
        var document = SharedSettingsState.Load(dataDirectory).Document;
        var entries = document.Settings.Where(s => HouseholdSharing.IsKey(s.Key)).ToArray();
        var all = HouseholdSharing.All(entries.Select(s => (s.Key, s.Value)));
        var own = current is { } me ? all.GetValueOrDefault(me) : null;
        return new
        {
            state = "loaded",
            currentAccount = current?.ToString("N"),
            entries = entries.Length,
            unreadable = entries.Length - all.Count,
            accounts = entries.Where(s => HouseholdSharing.AccountOf(s.Key) is { } id && all.ContainsKey(id)).Select(s =>
            {
                var sharing = all[HouseholdSharing.AccountOf(s.Key)!.Value];
                return new
                {
                    account = sharing.AccountId.ToString("N"),
                    current = sharing.AccountId == current,
                    copy = sharing.Characters.Count(c => c.Mode == CharacterShareMode.Copy),
                    together = sharing.Characters.Count(c => c.Mode == CharacterShareMode.Together),
                    joined = sharing.Joined.Count,
                    newFactsAboutMe = sharing.NewFactsAboutMe,
                    bytes = System.Text.Encoding.UTF8.GetByteCount(s.Value),
                    updatedBy = s.UpdatedBy,
                    changedAt = s.UpdatedAt,
                    characters = sharing.Characters.Select(c => new
                    {
                        key = c.Key,
                        mode = c.Mode == CharacterShareMode.Copy ? "copy" : "together",
                        space = c.Mode == CharacterShareMode.Together ? c.Space : null,
                        lorebooks = c.Lorebooks.Count,
                        lorebookEntries = c.Lorebooks.Sum(b => b.Entries.Count),
                        look = c.ModelId is null ? "keep" : c.ModelId == Martlet.Core.Settings.CharacterProfile.BuiltInModel ? "builtin" : "shared",
                        voice = c.VoiceId is not null
                    }).ToArray(),
                    joinedCharacters = sharing.Joined.Select(j => new
                    {
                        account = j.AccountId.ToString("N"),
                        key = j.CharacterId.ToString("N")[..8],
                        space = MemorySpaceId.Character(j.CharacterId),
                        stillShared = all.GetValueOrDefault(j.AccountId)?.Find(j.CharacterId)?.Mode == CharacterShareMode.Together
                    }).ToArray()
                };
            }).ToArray(),
            // What the account in use sees under Household characters: the characters other people share.
            householdCharacters = current is { } you ? all.Where(e => e.Key != you).Sum(e => e.Value.Characters.Count) : 0,
            ownShared = own?.Characters.Count ?? 0,
            ownJoined = own?.Joined.Count ?? 0,
            newFactsAboutMe = own?.NewFactsAboutMe ?? false
        };
    }

    /// <summary>The account in use on that device (accounts\session.json's <c>current</c>), or null without accounts.</summary>
    internal static Guid? CurrentAccount(string dataDirectory)
    {
        try
        {
            var path = Path.Combine(dataDirectory, "accounts", "session.json");
            if (!File.Exists(path)) return null;
            using var document = JsonDocument.Parse(File.ReadAllBytes(path));
            return document.RootElement.ValueKind == JsonValueKind.Object && document.RootElement.TryGetProperty("current", out var value) &&
                value.ValueKind == JsonValueKind.String && Guid.TryParse(value.GetString(), out var id) ? id : null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException) { return null; }
    }

    /// <summary>What the account in use shares, or null without accounts or a readable entry.</summary>
    internal static HouseholdSharing? Own(string dataDirectory)
    {
        if (CurrentAccount(dataDirectory) is not { } me || !File.Exists(Path.Combine(dataDirectory, SharedSettingsState.FileName))) return null;
        var entry = SharedSettingsState.Load(dataDirectory).Document.Find(HouseholdSharing.Key(me));
        return entry is null ? HouseholdSharing.Empty(me) : HouseholdSharing.Read(entry.Value) is { AccountId: var id } read && id == me ? read : null;
    }

    /// <summary>How the account in use shares the character profile <paramref name="character"/>: private, copy, together, or
    /// joined (someone else's character shared together that it talks to); null without accounts.</summary>
    internal static string? ModeOf(HouseholdSharing? own, Guid character) => own is null ? null
        : own.HasJoined(character) ? "joined"
        : own.ModeOf(character) switch { CharacterShareMode.Copy => "copy", CharacterShareMode.Together => "together", _ => "private" };
}
