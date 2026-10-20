using System.Security.Cryptography;
using System.Text;

namespace Martlet.Mcp.Client;

/// <summary>The names Martlet offers MCP servers' tools under in a reply: always the server's name, then the tool's
/// (<c>server_tool</c>), made safe for function calling. A name never depends on which other servers run, so the start of every
/// request stays the same for prompt caches.</summary>
public static class McpToolNames
{
    /// <summary>The longest tool name providers take.</summary>
    public const int MaxLength = 64;

    /// <summary>The name a reply offers <paramref name="tool"/> of <paramref name="server"/> under.</summary>
    public static string Offered(string server, string tool) => Safe(server + "_" + tool);

    /// <summary><paramref name="name"/> with only letters, digits, _ and -, at most <see cref="MaxLength"/> characters (a long one
    /// ends with a short hash of the whole name).</summary>
    public static string Safe(string name)
    {
        var safe = new string(name.Select(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-' ? c : '_').ToArray()).Trim('_');
        if (safe.Length == 0) safe = "tool";
        return safe.Length <= MaxLength ? safe : safe[..(MaxLength - 9)] + "_" + Hash(name);
    }

    /// <summary>A short hash of <paramref name="value"/> (8 hex characters).</summary>
    public static string Hash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)))[..8].ToLowerInvariant();
}
