using System.Globalization;
using System.Text;
using Martlet.Core.Network;

namespace Martlet.Core.Accounts;

/// <summary>
/// Windows logins with the same name are one person (docs/ACCOUNTS.md, "Same name, same person"). Each PC makes an account
/// named after its Windows display name, so one person with Martlet on several PCs got one account per PC. A desktop folds an
/// account it made itself (signed in with its Windows login) into the household's <see cref="Keeper"/> account of the same
/// name, with the same steps as joining with the owner's approval (<see cref="OwnerJoin"/>). Every desktop picks the same
/// keeper, so the accounts never fold into each other. The binding of this PC to the keeper carries the
/// <see cref="Proof"/> proof (<see cref="AccountBindingRules"/>).
/// </summary>
public static class SamePerson
{
    /// <summary>The binding proof: the device made an account with the same name as this one.</summary>
    public const string Proof = "same-name";

    /// <summary>The name first start gives when Windows tells no display name: it names nobody, so it never matches.</summary>
    public const string FallbackName = "Me";

    /// <summary>Whether two account names are the same person's: equal after trimming, Unicode normalization (NFC) and case.</summary>
    public static bool SameName(string? a, string? b) =>
        Normal(a) is { } left && Normal(b) is { } right && string.Compare(left, right, CultureInfo.InvariantCulture, CompareOptions.IgnoreCase) == 0;

    private static string? Normal(string? name)
    {
        var clean = name?.Trim().Normalize(NormalizationForm.FormC);
        return string.IsNullOrEmpty(clean) || string.Equals(clean, FallbackName, StringComparison.OrdinalIgnoreCase) ? null : clean;
    }

    /// <summary>The live account with a <c>windows</c> login named <paramref name="name"/> that the others of that name fold into:
    /// the household's owner account first, then the higher role, then the one whose Windows login was added first, then the
    /// lower ID. Null when there is none.</summary>
    public static Account? Keeper(AccountDirectory directory, string name, NetworkRoster roster)
    {
        ArgumentNullException.ThrowIfNull(directory);
        ArgumentNullException.ThrowIfNull(roster);
        return directory.Live.Where(a => SameName(a.Name, name) && a.Logins.Any(l => l.Kind == AccountLoginKinds.Windows))
            .OrderBy(a => AccountBindingRules.IsHouseholdOwner(a, roster) ? 0 : 1)
            .ThenBy(a => a.Role switch { AccountRoles.Owner => 0, AccountRoles.Admin => 1, _ => 2 })
            .ThenBy(a => a.Logins.Where(l => l.Kind == AccountLoginKinds.Windows).Min(l => l.AddedAt))
            .ThenBy(a => a.Key, StringComparer.Ordinal).FirstOrDefault();
    }

    /// <summary>The fold for desktop <paramref name="deviceId"/>, or null when there is nothing to do: an account signed in here
    /// with this Windows login (not a Prove sign-in) that this device made (in the directory, or pending) has the same name as
    /// the <see cref="Keeper"/>, which is another account. <see cref="OwnerJoinPlan.Owner"/> is the keeper.</summary>
    public static OwnerJoinPlan? Plan(AccountDirectory directory, AccountSessionState state, NetworkRoster roster, string deviceId)
    {
        ArgumentNullException.ThrowIfNull(directory);
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(roster);
        if (roster.Desktop(deviceId) is not { Removed: false }) return null;
        foreach (var id in state.SignedIn.Where(id => state.ProofFor(id) is null))
        {
            var entry = directory.Find(id);
            if (entry is { Removed: false } && entry.CreatedBy == deviceId)
            {
                if (Keeper(directory, entry.Name, roster) is { } keeper && keeper.Id != id) return new(keeper.Id, id, true, SameName: true);
            }
            else if (entry is null && state.PendingFor(id) is { } pending && Keeper(directory, pending.Name, roster) is { } keeper)
                return new(keeper.Id, id, false, SameName: true);
        }
        return null;
    }
}
