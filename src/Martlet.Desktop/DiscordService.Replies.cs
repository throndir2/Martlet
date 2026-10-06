using Martlet.Core.Lorebooks;
using Martlet.Core.Settings;

namespace Martlet.Desktop;

internal sealed partial class DiscordService
{
    /// <summary>The reply engine once wired (null before, or without settings).</summary>
    internal DiscordReplyEngine? ReplyEngine => Replies as DiscordReplyEngine;

    /// <summary>Wires <see cref="DiscordService.Replies"/> to Martlet's persona, Thinking route, lorebooks and memory. Each turn
    /// reads the saved settings afresh, so changes in Companion apply to the next Discord reply. <paramref name="conversation"/>
    /// is the local conversation, which always comes first on a model the two share.</summary>
    internal void UseReplies(ISetupService settings, ICredentialStore vault, LiveConversationController? conversation,
        DesktopMemoryService? memory, LorebookStore? lorebooks)
    {
        if (Replies is not null) return;
        Replies = new DiscordReplyEngine(settings, vault, directory, () => conversation?.Replying == true, memory, lorebooks);
    }
}
