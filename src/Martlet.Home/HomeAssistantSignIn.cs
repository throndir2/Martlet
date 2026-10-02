using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using Martlet.Core.Settings;

namespace Martlet.Home;

/// <summary>"Sign in with Home Assistant": opens Home Assistant's own sign-in page in the browser, receives the one-time code
/// on a loopback address only this PC can reach, and mints Martlet's long-lived token from it. Home Assistant accepts a
/// loopback client whose return address has the same origin, so nothing has to be registered and no password passes
/// through Martlet.</summary>
public static class HomeAssistantSignIn
{
    public static readonly TimeSpan Timeout = TimeSpan.FromMinutes(5);
    private const int MaximumRequestBytes = 8192;

    /// <summary>Runs the sign-in. <paramref name="openBrowser"/> receives Home Assistant's sign-in address.</summary>
    public static async Task<SecretLease> SignInAsync(HomeAssistantClient client, Uri baseUri, string clientName, Action<Uri> openBrowser,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(openBrowser);
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start(1);
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var clientId = $"http://127.0.0.1:{port}/";
        var redirect = clientId + "signed-in";
        var state = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));
        openBrowser(new Uri(baseUri, "auth/authorize?response_type=code" +
            "&client_id=" + Uri.EscapeDataString(clientId) + "&redirect_uri=" + Uri.EscapeDataString(redirect) +
            "&state=" + state));
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(Timeout);
        string code;
        try { code = await ReceiveCodeAsync(listener, state, timeout.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new HomeAssistantException(HomeAssistantFailure.Timeout, "The sign-in wasn't finished within 5 minutes. Try again.");
        }
        var session = await client.ExchangeCodeAsync(baseUri, clientId, code, cancellationToken).ConfigureAwait(false);
        return await client.MintTokenAsync(baseUri, session, clientName, cancellationToken).ConfigureAwait(false);
    }

    // Answers browser requests on the loopback port until one carries the code for this sign-in.
    private static async Task<string> ReceiveCodeAsync(TcpListener listener, string state, CancellationToken cancellationToken)
    {
        while (true)
        {
            using var connection = await listener.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false);
            await using var stream = connection.GetStream();
            var target = await ReadTargetAsync(stream, cancellationToken).ConfigureAwait(false);
            var query = target is null ? null : Query(target);
            string? code = null;
            string message;
            if (query is null || !target!.StartsWith("/signed-in", StringComparison.Ordinal))
                message = "This address belongs to Martlet's Home Assistant sign-in.";
            else if (query.GetValueOrDefault("state") != state)
                message = "This sign-in doesn't match the one Martlet started. Start it again from Martlet.";
            else if (query.GetValueOrDefault("code") is { Length: > 0 and <= 512 } found)
            {
                code = found;
                message = "Signed in. You can close this tab and go back to Martlet.";
            }
            else message = "Home Assistant didn't sign you in. Go back to Martlet and try again.";
            var html = "<!doctype html><meta charset=\"utf-8\"><title>Martlet</title>" +
                "<body style=\"font-family:Segoe UI,sans-serif;margin:3em\"><h2>Martlet</h2><p>" + WebUtility.HtmlEncode(message) + "</p>";
            var body = Encoding.UTF8.GetBytes(html);
            var head = Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Type: text/html; charset=utf-8\r\nContent-Length: {body.Length}\r\n" +
                "Cache-Control: no-store\r\nReferrer-Policy: no-referrer\r\nConnection: close\r\n\r\n");
            try
            {
                await stream.WriteAsync(head, cancellationToken).ConfigureAwait(false);
                await stream.WriteAsync(body, cancellationToken).ConfigureAwait(false);
            }
            catch (IOException) { }
            if (code is not null) return code;
        }
    }

    // The request target of "GET <target> HTTP/1.1", or null for anything else.
    private static async Task<string?> ReadTargetAsync(NetworkStream stream, CancellationToken cancellationToken)
    {
        var buffer = new byte[MaximumRequestBytes];
        var length = 0;
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        limit.CancelAfter(TimeSpan.FromSeconds(10));
        try
        {
            while (length < buffer.Length)
            {
                var read = await stream.ReadAsync(buffer.AsMemory(length), limit.Token).ConfigureAwait(false);
                if (read == 0) break;
                length += read;
                if (buffer.AsSpan(0, length).IndexOf("\r\n"u8) >= 0) break;
            }
        }
        catch (Exception error) when (error is IOException || error is OperationCanceledException && !cancellationToken.IsCancellationRequested)
        {
            return null;
        }
        var line = Encoding.ASCII.GetString(buffer, 0, length);
        var end = line.IndexOf("\r\n", StringComparison.Ordinal);
        if (end < 0) return null;
        var parts = line[..end].Split(' ');
        return parts.Length == 3 && parts[0] == "GET" && parts[1].StartsWith('/') ? parts[1] : null;
    }

    private static Dictionary<string, string>? Query(string target)
    {
        var mark = target.IndexOf('?');
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        if (mark < 0) return values;
        foreach (var pair in target[(mark + 1)..].Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var equals = pair.IndexOf('=');
            if (equals <= 0) continue;
            try { values[Uri.UnescapeDataString(pair[..equals])] = Uri.UnescapeDataString(pair[(equals + 1)..].Replace('+', ' ')); }
            catch (UriFormatException) { return null; }
        }
        return values;
    }
}
