using System.Text;
using Martlet.Core.Contracts;
using Martlet.Core.Settings;
using Martlet.Core.Sync;

namespace Martlet.Desktop;

/// <summary>*Merge another account into this one*, memories: the merged account's memory space (<c>account-&lt;32 hex&gt;</c>) is
/// merged into the kept account's on every host of this PC's network that answers (facts keep their IDs and <c>voice_id</c>);
/// this PC's memory sync then brings them here. This PC must be bound to both accounts (the merge binds it to the merged one
/// first), since hosts serve an account's space only to its devices.</summary>
internal sealed class AccountMemoryMergeStep(Func<IReadOnlyList<ProveHost>> hosts) : IAccountMergeStep
{
    public string Name => "memories";

    public async Task MergeAsync(Guid into, Guid from, CancellationToken token)
    {
        var moved = 0;
        var problems = new List<string>();
        foreach (var choice in hosts())
        {
            try
            {
                using var connection = ClusterSync.Connect(choice.Host);
                var theirs = await connection.ReadMemorySpaceAsync("account-" + from.ToString("N"), token);
                if (theirs.Facts.Count > 0) await connection.MergeMemorySpaceAsync("account-" + into.ToString("N"), theirs, token);
                moved++;
                ErrorLog.Info($"Accounts: merged {theirs.Facts.Count} memory record(s) of {AccountSession.Short(from)} into {AccountSession.Short(into)} on {choice.Host.HostId}.");
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (Exception error) when (ClusterSync.IsHostFailure(error) || error is OperationCanceledException) { problems.Add($"{choice.Label}: {error.Message}"); }
        }
        if (moved == 0) throw new InvalidOperationException("No host could move the memories" + (problems.Count > 0 ? " (" + string.Join("; ", problems) + ")." : "."));
    }
}

/// <summary>*Merge another account into this one*, characters: the merged account's personalities and character profiles (from
/// its account settings on a host) join the kept account's, with the same IDs, so each character keeps its own memories; a name
/// already used gets a number. The kept account is the one in use, so <paramref name="changeCompanion"/> saves them as any
/// Companion change does. The merged account's other settings (its lorebooks, replies, look) stay with it.</summary>
internal sealed class AccountCharactersMergeStep(Func<IReadOnlyList<ProveHost>> hosts, Func<Func<CompanionSettings, CompanionSettings>, Task<bool>> changeCompanion)
    : IAccountMergeStep
{
    public string Name => "characters";

    public async Task MergeAsync(Guid into, Guid from, CancellationToken token)
    {
        CompanionSettings? theirs = null;
        var answered = false;
        var problems = new List<string>();
        foreach (var choice in hosts())
        {
            try
            {
                using var connection = ClusterSync.Connect(choice.Host);
                var settings = await connection.ReadAccountSettingsAsync(from, token);
                answered = true;
                if (settings.Find(AppSettingsSections.Companion) is { } companion)
                {
                    theirs = ContractJson.Read<CompanionSettings>(Encoding.UTF8.GetBytes(companion.Value));
                    break;
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (Exception error) when (ClusterSync.IsHostFailure(error) || error is OperationCanceledException) { problems.Add($"{choice.Label}: {error.Message}"); }
        }
        if (theirs is null)
        {
            if (answered) return;
            throw new InvalidOperationException("No host could give the other account's characters" + (problems.Count > 0 ? " (" + string.Join("; ", problems) + ")." : "."));
        }
        if (!await changeCompanion(mine => AddCharacters(mine, theirs)))
            throw new InvalidOperationException("The other account's characters couldn't be saved.");
    }

    /// <summary><paramref name="mine"/> with <paramref name="theirs"/>' personalities and character profiles it lacks (same IDs,
    /// names made unique). Throws <see cref="ContractException"/> when the account has no room for them.</summary>
    internal static CompanionSettings AddCharacters(CompanionSettings mine, CompanionSettings theirs)
    {
        var personas = mine.Personas.ToList();
        var moving = theirs.CharacterList.Where(c => mine.CharacterList.All(d => d.Id != c.Id)).ToArray();
        // A personality this account already has word for word (its default Martlet, for one) comes only when a moved character uses it.
        foreach (var persona in theirs.Personas.Where(p => personas.All(q => q.Id != p.Id)))
        {
            if (personas.Any(q => q.Name == persona.Name && q.Text == persona.Text) && moving.All(c => c.PersonaId != persona.Id)) continue;
            personas.Add(persona with { Name = Unique(persona.Name, personas.Select(p => p.Name), PersonaProfile.MaximumNameCharacters) });
        }
        var characters = mine.CharacterList.ToList();
        foreach (var character in moving)
            characters.Add(character with { Name = Unique(character.Name, characters.Select(c => c.Name), CharacterProfile.MaximumNameCharacters) });
        var next = mine with { Personas = personas, Characters = characters };
        next.Validate();
        return next;
    }

    private static string Unique(string name, IEnumerable<string> taken, int maximum)
    {
        var used = taken.ToHashSet(StringComparer.CurrentCultureIgnoreCase);
        if (!used.Contains(name)) return name;
        for (var number = 2; ; number++)
        {
            var suffix = " " + number;
            var candidate = (name.Length + suffix.Length > maximum ? name[..(maximum - suffix.Length)].TrimEnd() : name) + suffix;
            if (!used.Contains(candidate)) return candidate;
        }
    }
}

/// <summary>*Merge another account into this one*, voice links: every voice of the household's People list linked to the merged
/// account (*This voice is ...*) links to the kept one; the voice sync gives the change to the other computers.</summary>
internal sealed class AccountVoiceMergeStep(LocalVoices voices) : IAccountMergeStep
{
    public string Name => "voice links";

    public Task MergeAsync(Guid into, Guid from, CancellationToken token)
    {
        var moved = voices.MoveLinks(from, into);
        if (moved > 0) ErrorLog.Info($"Accounts: linked {moved} voice(s) of {AccountSession.Short(from)} to {AccountSession.Short(into)}.");
        return Task.CompletedTask;
    }
}
