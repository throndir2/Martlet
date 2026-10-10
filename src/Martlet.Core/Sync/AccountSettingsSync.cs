namespace Martlet.Core.Sync;

/// <summary>How a computer keeps the household's settings and the signed-in account's settings in step with the other computers
/// (docs/ACCOUNTS.md, "Account settings"), and how it switches the files in use from one account to another.
/// <para>Older Martlets keep every setting in the household document. So that they keep working, the owner's account takes the
/// account entries an older computer writes there (<see cref="SettingScopes.IsAccountKey"/>), and the household document takes
/// the owner's account entries back. Both are last-writer-wins merges of the same entries, so every copy converges. Other
/// accounts never read or write those entries.</para></summary>
public static class AccountSettingsSync
{
    /// <summary>The account entries of <paramref name="settings"/>, each as it is (revision, writer and value).</summary>
    public static SharedSettings AccountPart(SharedSettings settings) => settings.Only(SettingScopes.IsAccountKey);

    /// <summary>One sync of this computer: first the signed-in account's settings (<paramref name="account"/>, always applied:
    /// they are that account's own), then the household's (applied when <paramref name="shared"/>, as "Keep Martlet the same on
    /// all my computers" says). With <paramref name="owner"/>, the account entries older computers left in the household's
    /// copies go into the account's copy, and the account's go back into the household's.</summary>
    public static async Task<(SharedSettingsResult Household, SharedSettingsResult? Account)> SyncAsync(SharedSettingsNode household,
        SharedSettingsNode? account, bool owner, IReadOnlyList<SharedSettings> householdCopies, IReadOnlyList<SharedSettings> accountCopies,
        bool shared, DateTimeOffset now, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(household);
        SharedSettingsResult? accountResult = null;
        if (account is not null)
            accountResult = await account.SyncAsync(owner ? [.. accountCopies, .. householdCopies.Select(AccountPart)] : accountCopies,
                true, now, token).ConfigureAwait(false);
        var bridged = owner && accountResult is not null ? [.. householdCopies, AccountPart(accountResult.Document)] : householdCopies;
        var householdResult = await household.SyncAsync(bridged, shared, now, token).ConfigureAwait(false);
        return (householdResult, accountResult);
    }

    /// <summary>Switches the files in use from the account of <paramref name="from"/> (null at a first sign-in) to the account
    /// of <paramref name="to"/>: records the files into the outgoing account's copy first, then gives them the incoming
    /// account's settings (its copy merged with <paramref name="copies"/>, the hosts' copies) and writes the working-copy
    /// marker. When giving them the incoming account's settings fails, the files go back to the outgoing account's and the
    /// marker is left as it was, then the error is thrown. Returns the settings still waiting, with why.</summary>
    public static async Task<IReadOnlyDictionary<string, string>> SwitchAsync(SharedSettingsNode? from, SharedSettingsNode to,
        IEnumerable<SharedSettings> copies, string dataDirectory, Guid account, DateTimeOffset now, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(to);
        if (from is not null) await from.SyncAsync([], false, now, token).ConfigureAwait(false);
        IReadOnlyDictionary<string, string> waiting;
        try { waiting = await to.AdoptAsync(copies, token).ConfigureAwait(false); }
        catch when (from is not null)
        {
            try { await from.AdoptAsync(CancellationToken.None).ConfigureAwait(false); }
            catch (Exception error) when (error is not OperationCanceledException) { }
            throw;
        }
        AccountWorkingCopy.Save(dataDirectory, account, now);
        return waiting;
    }
}
