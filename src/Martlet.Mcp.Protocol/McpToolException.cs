namespace Martlet.Mcp;

/// <summary>An expected tool failure: the tool could not do what was asked, and its message says why and what to do. McpServer
/// returns the message as the tool's error text (isError), without the exception's type. The tools also use ArgumentException
/// (the arguments are wrong), InvalidOperationException (Martlet is not in a state for it), and IOException,
/// UnauthorizedAccessException and HttpRequestException (a file or a request failed) in the same way. An area whose code throws
/// exceptions of its own projects turns them into this one where it registers its tools (<see cref="McpServer.Expecting"/>).</summary>
internal sealed class McpToolException(string message, Exception? inner = null) : Exception(message, inner)
{
    /// <summary>Whether a tool failure is expected: its message alone goes to the assistant.</summary>
    internal static bool IsExpected(Exception error) =>
        error is McpToolException or ArgumentException or InvalidOperationException or System.IO.IOException or UnauthorizedAccessException or
            System.Net.Http.HttpRequestException or Martlet.Core.Contracts.ContractException;
}
