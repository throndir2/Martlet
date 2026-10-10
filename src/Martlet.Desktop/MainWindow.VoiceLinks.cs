namespace Martlet.Desktop;

/// <summary>Whose voice is whose (docs/ACCOUNTS.md, Voices): the People list is one household list, and each voice can link to
/// one account. "Your voice" is a voice linked to the signed-in account (<see cref="LocalVoices.IsYours"/>). A voice only tells
/// Martlet who is talking: it never switches the account, unlocks settings or spends money.</summary>
public partial class MainWindow
{
    /// <summary>The name an account goes by on People ("This voice is Sam"); null when the household's account list doesn't
    /// know it.</summary>
    private string? VoiceAccountName(Guid account) => null;
}
