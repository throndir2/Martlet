using System.IO;
using System.Text.Json;
using Martlet.Avatar.Hosting;
using Martlet.Core.Contracts;
using Martlet.Core.Settings;
using Martlet.Core.Sync;
using Martlet.Credentials.Windows;

namespace Martlet.Mcp;

/// <summary>Looks found online for MCP clients (character_find_looks, vroid_hub_status, and the look keys character_create and
/// character_update take): the same search Find a character uses in the app (VRoid Hub and the Open Source Avatars registry),
/// and the owner's VRoid Hub link on this PC without its tokens.</summary>
internal static class CharacterLooks
{
    private static readonly Lazy<CharacterModelSearch> Search = new(() => new CharacterModelSearch(CharacterModelSearch.CreateHttpClient()));

    internal static async Task<object> FindAsync(string directory, string query, int? count, IReadOnlyList<string>? sources, CancellationToken token)
    {
        var found = await Search.Value.SearchAsync(query, count ?? 12, LinkedTokens(directory), sources, token);
        return new
        {
            query,
            looks = found.Candidates.Select(c => new
            {
                look = c.Look, name = c.Name, character = c.Character, author = c.Author, source = c.Source == CharacterModelCandidate.VRoidHubSource ? "VRoid Hub" : "Open Source Avatars",
                license = c.License, conditions = c.Conditions, creditRequired = c.CreditRequired, credit = c.Credit, usable = c.Usable, own = c.Own,
                kept = c.Kept, vrmVersion = c.VrmVersion, megabytes = c.Bytes is { } bytes ? Math.Round(bytes / (1024d * 1024), 1) : (double?)null,
                description = c.Description, picture = c.ThumbnailUrl, page = c.PageUrl,
                howToUse = c.Kept ? "Downloaded once when used; it joins the owner's characters (shared with their Martlet computers)."
                    : "Shown from VRoid Hub each time with the owner's linked VRoid Hub account; never saved."
            }).ToArray(),
            notes = found.Notes,
            vroidHub = new { canLink = VRoidHubApp.Configured, linked = LinkedTokens(directory) is not null }
        };
    }

    /// <summary>The backup finder: web pages that may have the character's model or voice recordings, and searches on the sites
    /// that have them. Martlet downloads nothing from them: the owner opens them, checks the terms and adds what they may use.</summary>
    internal static async Task<object> FindOnWebAsync(string query, bool models, bool voices, CancellationToken token)
    {
        if (!models && !voices) throw new ArgumentException("Ask for models, voices or both.");
        using var web = new Martlet.Conversation.WebAccess();
        var found = await Martlet.Conversation.CharacterWebFinder.FindAsync(web, query, models, voices, token);
        static object Link(Martlet.Conversation.CharacterWebLink l) => new { title = l.Title, url = l.Url, site = l.Site, snippet = l.Snippet };
        return new
        {
            query,
            models = found.Models.Select(Link).ToArray(),
            voices = found.Voices.Select(Link).ToArray(),
            siteSearches = found.Searches.Select(l => new { kind = l.Kind, title = l.Title, url = l.Url }).ToArray(),
            notes = found.Notes,
            nextSteps = new[]
            {
                "These are web pages, not checked models or voices. Show them to the owner; the owner opens them and checks the creator's terms.",
                "A model: the owner downloads it (unzips it if needed) and adds the .vrm file or the Live2D .model3.json in Martlet: Companion › Character › Add a character.",
                "A voice: the owner downloads a clean clip of the character speaking (5 to 30 seconds, one voice, no music) and adds it in Companion › Voice › Add a voice. " +
                    "A character's voice belongs to its voice actor and studio: use it only for yourself, and only where their terms allow it.",
                "Martlet downloads nothing from these pages by itself."
            }
        };
    }

    internal static object VRoidHubStatus(string directory)
    {
        var tokens = LinkedTokens(directory);
        var shown = ShownModelPath(directory);
        return new
        {
            canLink = VRoidHubApp.Configured,
            account = AccountKey(directory) == "pc" ? "this PC (no Martlet account yet)" : "the account whose settings this data folder holds",
            linked = tokens is not null,
            linkedAs = tokens?.UserName,
            looks = VRoidHubLooks.Load(directory).Select(l => new
            {
                look = l.Path, name = l.Name, author = l.Author, conditions = l.Conditions, creditRequired = l.CreditRequired, page = l.PageUrl,
                usedAt = l.UsedAt, shown = shown == l.Path
            }).ToArray(),
            shownIsVRoidHub = RemoteCharacterModels.IsRemote(shown),
            note = "VRoid Hub models are never saved on disk: Martlet downloads them into memory each time it shows them."
        };
    }

    /// <summary>A look as character_create and character_update take it: a vroidhub: key is checked on VRoid Hub (the creator
    /// must let anyone use it as a character) and its credit kept for the app; an osa: key is downloaded and joins the owner's
    /// characters (its shared model ID is returned). Anything else is returned as it is.</summary>
    internal static async Task<string> PrepareAsync(string directory, string look, CancellationToken token)
    {
        var wanted = look.Trim();
        if (RemoteCharacterModels.IsRemote(wanted))
        {
            var id = RemoteCharacterModels.VRoidHubId(wanted) ?? throw new ArgumentException("A VRoid Hub look is vroidhub: and the model's ID (digits).");
            var tokens = LinkedTokens(directory);
            var candidate = await Search.Value.VRoidHub.ModelAsync(id, tokens?.UserId, token)
                ?? throw new ArgumentException("VRoid Hub doesn't show that model (it is private, age-restricted or gone).");
            if (!candidate.Usable) throw new ArgumentException("That VRoid Hub model's creator doesn't let others use it as a character.");
            VRoidHubLooks.Remember(directory, VRoidHubLook.From(candidate, DateTimeOffset.UtcNow));
            return candidate.Look;
        }
        if (wanted.StartsWith(OpenSourceAvatars.LookPrefix, StringComparison.Ordinal))
        {
            var candidate = await Search.Value.OpenSourceAvatars.FindAsync(wanted, token)
                ?? throw new ArgumentException("No Open Source Avatars model has that look key. Find one with character_find_looks.");
            var bytes = await Search.Value.OpenSourceAvatars.DownloadAsync(candidate, token);
            var name = candidate.CreditRequired ? $"{candidate.Name} (by {candidate.Author}, {candidate.License})" : candidate.Name;
            if (name.Length > Martlet.Core.Characters.CharacterModelLibrary.MaximumNameLength)
                name = name[..Martlet.Core.Characters.CharacterModelLibrary.MaximumNameLength].TrimEnd();
            var model = await SharedCharacterModels.ImportVrmAsync(directory, FileName(candidate.Name), bytes, name, Environment.MachineName.ToLowerInvariant(),
                DateTimeOffset.UtcNow, token);
            return model.Id;
        }
        return wanted;
    }

    private static string FileName(string name)
    {
        var safe = new string(name.Select(c => char.IsLetterOrDigit(c) || c is ' ' or '-' or '_' ? c : ' ').ToArray()).Trim();
        safe = System.Text.RegularExpressions.Regex.Replace(safe, @"\s+", " ");
        if (safe.Length > 60) safe = safe[..60].TrimEnd();
        if (safe.Length == 0 || !LocalAvatarFiles.IsSafeModelName(safe + ".vrm")) safe = "Character";
        return safe + ".vrm";
    }

    /// <summary>The Credential Manager key of the account whose settings the data folder holds ("pc" before accounts).</summary>
    private static string AccountKey(string directory)
    {
        try { return AccountWorkingCopy.Load(directory) is { } copy && copy.Account != Guid.Empty ? copy.Account.ToString("N") : "pc"; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or ContractException) { return "pc"; }
    }

    private static VRoidHubTokens? LinkedTokens(string directory)
    {
        try
        {
            return new WindowsCredentialStore().ReadVRoidHubTokens(AccountKey(directory), out var text) == CredentialError.None
                ? VRoidHubTokens.Read(text) : null;
        }
        catch (Exception error) when (error is PlatformNotSupportedException or InvalidOperationException) { return null; }
    }

    private static string? ShownModelPath(string directory)
    {
        try
        {
            var path = Path.Combine(directory, "avatar.json");
            if (!File.Exists(path) || new FileInfo(path).Length > 1024 * 1024) return null;
            using var document = JsonDocument.Parse(File.ReadAllBytes(path));
            foreach (var property in document.RootElement.EnumerateObject())
                if (property.Name.Equals("modelPath", StringComparison.OrdinalIgnoreCase) && property.Value.ValueKind == JsonValueKind.String)
                    return property.Value.GetString();
            return null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException) { return null; }
    }
}
