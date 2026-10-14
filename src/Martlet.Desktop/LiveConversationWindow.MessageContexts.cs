using System.Windows;
using Martlet.Conversation;

namespace Martlet.Desktop;

// What was sent: each of Martlet's messages keeps what its Thinking requests carried (MessageContext), and its What was sent link
// opens MessageContextWindow. Only the newest messages keep it, so a long conversation doesn't hold every picture and recording.
public partial class LiveConversationWindow
{
    /// <summary>How many of the newest messages keep what was sent; older ones let it go (their link goes too).</summary>
    internal const int ContextsKept = 40;
    private readonly Queue<ChatMessage> withContext = new();
    private int contextLinks;
    private MessageContextWindow? contextViewer;

    /// <summary>The open What was sent window, if any (for tests and MCP checks).</summary>
    internal MessageContextWindow? ContextViewer => contextViewer is { IsLoaded: true } open ? open : null;

    // Keeps what operation's turn sent with message (again, once more of it is known), and lets the oldest go past ContextsKept.
    private void KeepContext(ChatMessage message, LiveConversationOperation operation)
    {
        if (MessageContext.Of(operation.Turn, Kind(operation), CharacterName) is not { } context) return;
        if (message.Context is null) withContext.Enqueue(message);
        // Each link has its own number (LiveMessageContext-<n>), so MCP can tell them apart and click one.
        if (message.ContextNumber == 0) message.ContextNumber = ++contextLinks;
        message.Context = context;
        while (withContext.Count > ContextsKept) withContext.Dequeue().Context = null;
    }

    private static string Kind(LiveConversationOperation operation) =>
        operation.Commentary ? "remark" : operation.Report ? "report" : operation.Touch ? "reaction to a touch"
            : operation.Remote ? "reply to a message from a chat app" : "reply";

    // Opens What was sent for the message whose link was clicked (one window, showing the newest choice).
    private void MessageContext_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: ChatMessage { Context: { } context } }) return;
        OpenContext(context);
    }

    internal void OpenContext(MessageContext context)
    {
        if (contextViewer is { IsLoaded: true } open)
        {
            open.Display(context);
            open.Activate();
            return;
        }
        contextViewer = new MessageContextWindow(context) { Owner = this };
        contextViewer.Closed += (_, _) => contextViewer = null;
        contextViewer.Show();
    }
}
