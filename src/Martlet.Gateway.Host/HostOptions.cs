using System.Net;

namespace Martlet.Gateway.Host;

internal sealed record HostOptions(bool Create, string State, string HostId, GatewayOrigin Origin)
{
    internal const string Help = """
        Martlet local host control (Windows / local NTFS; default No)
        No arguments, help, --help or -h: show this help without opening state.
        init --state <absolute-private-path> --host-id <id> --origin <https-loopback-ip:port>
        open --state <absolute-private-path> --host-id <expected-id> --origin <https-loopback-ip:port>
        Example origin: https://127.0.0.1:9443 (canonical IP, explicit port, no trailing slash).
        init requires an absent path with an existing parent; open never creates missing state.
        Both require local interactive confirmation and open state WITHOUT a listener.
        Console commands: start, pair, list, revoke, stop, help.
        Authority-changing actions default to No. No unattended/approval flags exist.
        Pairing is permanent until deliberate revocation; connectivity is separate.
        Empty engine registry: no installed models or inference availability are advertised.
        LAN, services, discovery, browser UI and remote administration are unavailable.
        Never supply tokens or device secrets in arguments, URLs, redirected input or logs.
        """;

    internal static HostOptions? Parse(string[] args)
    {
        if (args.Length == 0 || args is ["help" or "--help" or "-h"])
            return null;
        if (args.Length != 7 || args[0] is not ("init" or "open"))
            throw new HostInputException();
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = 1; i < args.Length; i += 2)
        {
            if (args[i] is not ("--state" or "--host-id" or "--origin") ||
                !values.TryAdd(args[i], args[i + 1]))
                throw new HostInputException();
        }
        var state = values["--state"];
        var id = values["--host-id"];
        if (!Fields.Printable(state, 240) || !Fields.Identifier(id) ||
            state.Length < 4 || !char.IsAsciiLetter(state[0]) || state[1] != ':' ||
            state[2] != '\\' || state.Contains('/') || state.EndsWith('\\') ||
            state.Any(c => c is '<' or '>' or '"' or '|' or '?' or '*') ||
            state.AsSpan(2).Contains(':') ||
            state.Split('\\').Skip(1).Any(part => part is "" or "." or ".." ||
                part.EndsWith(' ') || part.EndsWith('.')))
            throw new HostInputException();
        GatewayOrigin origin;
        try { origin = new(values["--origin"]); }
        catch (GatewayProtocolException) { throw new HostInputException(); }
        if (!IPAddress.IsLoopback(origin.Address))
            throw new HostInputException();
        return new(args[0] == "init", state, id, origin);
    }
}

internal static class Fields
{
    internal static bool Printable(string? value, int maximum) =>
        value is { Length: > 0 } && value.Length <= maximum &&
        value.All(c => c is >= ' ' and <= '~');

    internal static bool Identifier(string? value) =>
        Printable(value, 64) && char.IsAsciiLetterOrDigit(value![0]) &&
        value.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-');

    internal static string Display(string value, int maximum) =>
        new(value.Take(maximum).Select(c => c is >= ' ' and <= '~' ? c : '?').ToArray());
}

internal sealed class HostInputException : Exception;
internal sealed class HostConsoleException : Exception;
internal sealed class HostEndOfInputException : Exception;
