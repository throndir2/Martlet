namespace Martlet.Desktop;

/// <summary>Whose voice is whose (docs/ACCOUNTS.md, Voices): the People list is one household list, and each voice can link to
/// one account. "Your voice" is a voice linked to the signed-in account (<see cref="LocalVoices.IsYours"/>). A voice only tells
/// Martlet who is talking: it never switches the account, unlocks settings or spends money.</summary>
public partial class MainWindow
{
    /// <summary>Tells the voice list who is signed in and who the household owner is, now and whenever that changes: at a switch
    /// (only between replies) and when the account directory brings the owner account. A voice an older Martlet marked as the
    /// owner's then links to the owner account.</summary>
    private void InitializeVoiceLinks()
    {
        UseVoiceAccount();
        if (accounts is null) return;
        accounts.AccountChanged += () => Dispatcher.BeginInvoke(UseVoiceAccount);
        accounts.Changed += () => Dispatcher.BeginInvoke(UseVoiceAccount);
    }

    private void UseVoiceAccount()
    {
        if (closing) return;
        localVoices.UseAccount(accounts?.AccountId, accounts?.OwnerId);
    }

    /// <summary>The name an account goes by on People ("This voice is Sam"); null when the household's account list doesn't
    /// know it.</summary>
    private string? VoiceAccountName(Guid account)
    {
        if (accounts is null) return null;
        var name = account == accounts.AccountId ? accounts.Current.Name
            : accounts.SignedIn.FirstOrDefault(v => v.Id == account)?.Name
              ?? (accounts.Directory.Find(account) is { Removed: false } entry ? entry.Name : null);
        return string.IsNullOrWhiteSpace(name) ? null : name;
    }
}
