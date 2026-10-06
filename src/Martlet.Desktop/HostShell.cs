using System.Buffers.Binary;
using System.Buffers.Text;
using System.IO;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Martlet.Core.Settings;
using Martlet.Credentials.Windows;
using Renci.SshNet;
using Renci.SshNet.Common;

namespace Martlet.Desktop;

/// <summary>An SSH account on a Linux Martlet host: user@computer or user@computer:port.</summary>
internal sealed partial record HostShellTarget(string User, string Host, int Port = 22)
{
    [GeneratedRegex(@"\A(?<user>[A-Za-z0-9._-]{1,64})@(?<host>[A-Za-z0-9][A-Za-z0-9.-]{0,252})(?::(?<port>[0-9]{1,5}))?\z")]
    private static partial Regex Pattern();

    internal static HostShellTarget Parse(string? text)
    {
        var match = Pattern().Match(text?.Trim() ?? "");
        var port = match.Groups["port"].Success ? int.Parse(match.Groups["port"].Value, System.Globalization.CultureInfo.InvariantCulture) : 22;
        if (!match.Success || port is < 1 or > 65535)
            throw new InvalidOperationException("Enter the SSH target as user@computer, for example me@gpu-pc.");
        return new(match.Groups["user"].Value, match.Groups["host"].Value, port);
    }

    /// <summary>The key the pinned host key is stored under (the computer, not the account).</summary>
    internal string Machine => $"{Host.ToLowerInvariant()}:{Port}";

    /// <summary>Scope for a remembered sudo password in Windows Credential Manager.</summary>
    internal string Account => $"{User}@{Machine}";

    public override string ToString() => Port == 22 ? $"{User}@{Host}" : $"{User}@{Host}:{Port}";
}

/// <summary>One command for <see cref="HostShell.RunAsync"/>.</summary>
internal sealed record HostShellCommand
{
    /// <summary>POSIX sh text, run by <c>sh -c</c> as the SSH account, without a terminal (so nothing can prompt).</summary>
    public required string Command { get; init; }
    /// <summary>Written to the command's stdin first (for example answers or a script for <c>bash -s</c>). Use it for
    /// secrets: stdin never appears in process lists, arguments or logs.</summary>
    public string? Input { get; init; }
    /// <summary>When set, stdin stays open after <see cref="Input"/> until this completes; its text (if any) is written
    /// last, then stdin is closed. Without it stdin closes right after <see cref="Input"/>.</summary>
    public Task<string?>? MoreInput { get; init; }
    /// <summary>Streams raw bytes to stdin after <see cref="Input"/> (for example a tar of files for <c>tar -xf -</c>),
    /// then stdin is closed.</summary>
    public Func<Stream, CancellationToken, Task>? Write { get; init; }
    /// <summary>Make plain <c>sudo</c> work inside <see cref="Command"/>: Martlet asks for the sudo password in the
    /// desktop when the account needs one (optionally remembered in Windows Credential Manager), checks it with
    /// <c>sudo -S</c>, and hands it to the command over the SSH channel through a private one-run askpass helper.</summary>
    public bool Sudo { get; init; }
    /// <summary>The host key saved with a paired host (<see cref="PairedHost.SshHostKey"/>); must match as well.</summary>
    public string? PinnedHostKey { get; init; }
}

/// <summary>Outcome of <see cref="HostShell.RunAsync"/>: the remote exit code (-1 if it ended by a signal) and the host
/// key the connection was pinned to ("ssh-ed25519 SHA256:...").</summary>
internal sealed record HostShellResult(int ExitCode, string HostKey);

internal sealed record HostShellSudo(string Password, bool Remember);

/// <summary>Questions only the owner can answer. Called on a background thread; implementations marshal to the UI.</summary>
internal interface IHostShellPrompts
{
    /// <summary>First connection to a computer: show its host key fingerprint; true pins it for good.</summary>
    bool TrustHostKey(HostShellTarget target, string hostKey);
    /// <summary>The account's password, asked once to install Martlet's key. Never stored; null cancels.</summary>
    string? LoginPassword(HostShellTarget target, bool retry);
    /// <summary>The account's sudo password (masked); null runs without sudo rights.</summary>
    HostShellSudo? SudoPassword(HostShellTarget target, bool retry);
}

/// <summary>Raised for problems the owner can act on; the message is shown as is.</summary>
internal sealed class HostShellException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>For background runs: never asks, so anything needing an answer (password, new host key, sudo password that
/// was not remembered) fails instead.</summary>
internal sealed class NoHostShellPrompts : IHostShellPrompts
{
    internal static readonly NoHostShellPrompts Instance = new();
    public bool TrustHostKey(HostShellTarget target, string hostKey) => false;
    public string? LoginPassword(HostShellTarget target, bool retry) => null;
    public HostShellSudo? SudoPassword(HostShellTarget target, bool retry) => null;
}

/// <summary>
/// Runs commands on Linux Martlet hosts over SSH from the desktop, with no console window and no typed password after
/// the first connection. This is Martlet's one SSH transport; reuse it rather than starting ssh.exe.
/// <list type="bullet">
/// <item>Authenticates with a Martlet-owned ECDSA key (<c>ssh\martlet_ecdsa</c> in Martlet's data directory, readable by
/// this Windows user only). On first contact it asks for the account password once, appends the public key to
/// <c>~/.ssh/authorized_keys</c> and never stores the password.</item>
/// <item>Pins each computer's host key on first use (the owner sees the fingerprint) in <c>ssh\known_hosts.json</c> and
/// refuses to connect when it changes.</item>
/// <item>Streams stdout and stderr lines (ANSI codes removed) to <c>output</c> as they arrive; cancelling the token
/// signals the remote command and closes the connection.</item>
/// </list>
/// Example: <c>await new HostShell(dataDirectory, prompts).RunAsync(HostShellTarget.Parse("me@gpu-pc"),
/// new() { Command = "bash -s", Input = script, Sudo = true }, progress, token)</c>.
/// </summary>
internal sealed partial class HostShell(string dataDirectory, IHostShellPrompts prompts)
{
    private static readonly SemaphoreSlim FileGate = new(1, 1);
    // sudo passwords accepted during this instance's lifetime (one flow), so a flow asks at most once per account.
    private readonly Dictionary<string, string> sudoPasswords = new(StringComparer.Ordinal);
    private string Directory => Path.Combine(dataDirectory, "ssh");
    private string KeyPath => Path.Combine(Directory, "martlet_ecdsa");
    private string KnownHostsPath => Path.Combine(Directory, "known_hosts.json");

    [GeneratedRegex(@"\x1b\[[0-9;?]*[ -/]*[@-~]|\x1b[()][A-Za-z0-9]|\r")]
    private static partial Regex Ansi();

    /// <summary>Runs <paramref name="command"/> on <paramref name="target"/> and returns when it exits.</summary>
    public Task<HostShellResult> RunAsync(HostShellTarget target, HostShellCommand command, IProgress<string>? output,
        CancellationToken token) => Task.Run(async () =>
    {
        var key = await LoadKeyAsync(token);
        var (client, hostKey) = await ConnectAsync(target, key, command.PinnedHostKey, token);
        using (client)
        {
            string? sudo = null;
            if (command.Sudo) sudo = await SudoAsync(client, target, output, token);
            var text = Wrap(command.Command, sudo is not null);
            using var run = client.CreateCommand(text);
            var execute = run.ExecuteAsync(token);
            var input = run.CreateInputStream();
            var feeding = FeedAsync(input, sudo, command, execute, token);
            var reading = PumpAsync(run, execute, output);
            try { await execute; }
            catch (OperationCanceledException)
            {
                output?.Report("Canceled. The host may finish its current step.");
                throw;
            }
            finally
            {
                await reading;
                await feeding;
            }
            return new HostShellResult(run.ExitStatus ?? -1, hostKey);
        }
    }, token);

    /// <summary>The public half of Martlet's SSH key ("ecdsa-sha2-nistp256 AAAA... martlet@PC"), creating the key once.</summary>
    public async Task<string> PublicKeyAsync(CancellationToken token) => (await LoadKeyAsync(token)).Public;

    /// <summary>Forgets a computer's pinned host key, for example after it was reinstalled.</summary>
    public async Task ForgetHostKeyAsync(HostShellTarget target, CancellationToken token)
    {
        await FileGate.WaitAsync(token);
        try
        {
            var hosts = ReadKnownHosts();
            if (hosts.Remove(target.Machine)) WriteKnownHosts(hosts);
        }
        finally { FileGate.Release(); }
    }

    /// <summary>The host key pinned for a computer, if it was contacted before.</summary>
    public string? PinnedHostKey(HostShellTarget target) =>
        ReadKnownHosts().TryGetValue(target.Machine, out var key) ? key : null;

    /// <summary>Removes a remembered sudo password.</summary>
    internal static void ForgetSudo(HostShellTarget target) => new WindowsCredentialStore().DeleteSshSudoSecret(target.Account);

    // ---------- command text ----------

    internal static string Quote(string text) => "'" + text.Replace("'", "'\\''", StringComparison.Ordinal) + "'";

    /// <summary>The exact remote command: <c>sh -c</c> around the caller's text, after a private askpass preamble when
    /// sudo is used. The preamble reads one line (the sudo password) from stdin into a 0600 file in a 0700 temporary
    /// directory, puts a <c>sudo</c> wrapper that uses it first on PATH and removes both when the command ends.</summary>
    internal static string Wrap(string command, bool sudo)
    {
        if (!sudo) return "sh -c " + Quote(command);
        var preamble = string.Join('\n',
            "__martlet_umask=$(umask); umask 077",
            "__martlet_dir=$(mktemp -d) || exit 97",
            "trap 'rm -rf \"$__martlet_dir\"' EXIT",
            "trap 'exit 129' HUP; trap 'exit 130' INT; trap 'exit 143' TERM",
            "IFS= read -r __martlet_pw || exit 97",
            "printf '%s\\n' \"$__martlet_pw\" > \"$__martlet_dir/pw\"; __martlet_pw=",
            "if __martlet_sudo=$(command -v sudo); then",
            "  mkdir \"$__martlet_dir/bin\"",
            "  printf '#!/bin/sh\\ncat \"%s/pw\"\\n' \"$__martlet_dir\" > \"$__martlet_dir/askpass\"",
            // sudo-rs (Ubuntu 25.10's default sudo) has no -A: validate with -S from the password file, then run the
            // command from the same wrapper process so it uses that credential (sudo-rs keys it by parent process).
            "  if \"$__martlet_sudo\" -V 2>/dev/null | grep -q '^Sudo version'; then",
            "    printf '#!/bin/sh\\nexec %s -A \"$@\"\\n' \"$__martlet_sudo\" > \"$__martlet_dir/bin/sudo\"",
            "  else",
            "    printf '#!/bin/sh\\n%s -S -p \"\" -v < \"%s/pw\" >/dev/null 2>&1\\n%s \"$@\"\\n' \"$__martlet_sudo\" \"$__martlet_dir\" \"$__martlet_sudo\" > \"$__martlet_dir/bin/sudo\"",
            "  fi",
            "  chmod 700 \"$__martlet_dir/askpass\" \"$__martlet_dir/bin/sudo\"",
            "  export SUDO_ASKPASS=\"$__martlet_dir/askpass\" PATH=\"$__martlet_dir/bin:$PATH\"",
            "fi",
            "umask \"$__martlet_umask\"",
            "");
        return "sh -c " + Quote(preamble + command);
    }

    /// <summary>Strips terminal colour/cursor codes from a line of remote output.</summary>
    internal static string Clean(string line) => Ansi().Replace(line, "");

    // ---------- connection ----------

    private async Task<(SshClient Client, string HostKey)> ConnectAsync(HostShellTarget target, (PrivateKeyFile File, string Public) key,
        string? pinned, CancellationToken token)
    {
        var trust = new HostKeyCheck(this, prompts, target, pinned);
        var client = Client(trust, new PrivateKeyAuthenticationMethod(target.User, key.File), target);
        try
        {
            await client.ConnectAsync(token);
            return (client, trust.Seen!);
        }
        catch (SshAuthenticationException) { client.Dispose(); }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            client.Dispose();
            throw trust.Explain(error, target);
        }

        // First contact: the account password, once, to install Martlet's key. It is not kept.
        for (var attempt = 0; ; attempt++)
        {
            var password = prompts.LoginPassword(target, attempt > 0) ?? throw new OperationCanceledException(token);
            var keyboard = new KeyboardInteractiveAuthenticationMethod(target.User);
            keyboard.AuthenticationPrompt += (_, e) =>
            {
                foreach (var prompt in e.Prompts) prompt.Response = password;
            };
            using var first = Client(trust, new PasswordAuthenticationMethod(target.User, password), target, keyboard);
            try { await first.ConnectAsync(token); }
            catch (SshAuthenticationException) when (attempt < 2) { continue; }
            catch (SshAuthenticationException error)
            {
                throw new HostShellException($"{target} did not accept that password. Enable password sign-in or add Martlet's SSH key, then try again.", error);
            }
            catch (Exception error) when (error is not OperationCanceledException) { throw trust.Explain(error, target); }
            using var install = first.CreateCommand("sh -c " + Quote(
                "umask 077; mkdir -p ~/.ssh && chmod 700 ~/.ssh && touch ~/.ssh/authorized_keys && chmod 600 ~/.ssh/authorized_keys && " +
                $"(grep -qxF {Quote(key.Public)} ~/.ssh/authorized_keys || printf '%s\\n' {Quote(key.Public)} >> ~/.ssh/authorized_keys)"));
            await install.ExecuteAsync(token);
            if (install.ExitStatus != 0)
                throw new HostShellException($"Martlet could not set up SSH access on {target}: {install.Error.Trim()}");
            break;
        }

        client = Client(trust, new PrivateKeyAuthenticationMethod(target.User, key.File), target);
        try
        {
            await client.ConnectAsync(token);
            return (client, trust.Seen!);
        }
        catch (SshAuthenticationException error)
        {
            client.Dispose();
            throw new HostShellException($"Martlet added its SSH key on {target}, but the computer did not accept it. Check SSH public-key sign-in there.", error);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            client.Dispose();
            throw trust.Explain(error, target);
        }
    }

    private static SshClient Client(HostKeyCheck trust, AuthenticationMethod method, HostShellTarget target,
        AuthenticationMethod? alternative = null)
    {
        var methods = alternative is null ? new[] { method } : new[] { method, alternative };
        var client = new SshClient(new ConnectionInfo(target.Host, target.Port, target.User, methods)
        {
            Timeout = TimeSpan.FromSeconds(20)
        })
        { KeepAliveInterval = TimeSpan.FromSeconds(30) };
        client.HostKeyReceived += (_, e) => e.CanTrust = trust.Check(e.HostKeyName, e.FingerPrintSHA256);
        return client;
    }

    /// <summary>Pins the host key on first use and refuses a changed one.</summary>
    private sealed class HostKeyCheck(HostShell shell, IHostShellPrompts prompts, HostShellTarget target, string? pinned)
    {
        internal string? Seen;
        private string? mismatch;
        private bool declined;

        internal bool Check(string algorithm, string fingerprint)
        {
            var key = $"{algorithm} SHA256:{fingerprint}";
            Seen = key;
            var saved = shell.PinnedHostKey(target);
            if (saved is null && pinned is not null) saved = pinned;
            if (saved is not null)
            {
                if (saved == key && (pinned is null || pinned == key))
                {
                    shell.Pin(target, key);
                    return true;
                }
                mismatch = saved;
                return false;
            }
            if (!prompts.TrustHostKey(target, key)) { declined = true; return false; }
            shell.Pin(target, key);
            return true;
        }

        internal Exception Explain(Exception error, HostShellTarget target) =>
            mismatch is not null
                ? new HostShellException($"{target.Host}'s SSH identity changed. Martlet refused to connect. If that computer was reinstalled, reset SSH trust and connect again.", error)
                : declined ? new OperationCanceledException("Computer not trusted.", error)
                : error switch
                {
                    System.Net.Sockets.SocketException or SshOperationTimeoutException =>
                        new HostShellException($"Could not reach {target.Host} over SSH ({error.Message}). Check that it is on, " +
                            "on your network and runs an SSH server.", error),
                    SshException or IOException or ProxyException => new HostShellException($"SSH to {target} failed: {error.Message}", error),
                    _ => error
                };
    }

    // ---------- sudo ----------

    private async Task<string?> SudoAsync(SshClient client, HostShellTarget target, IProgress<string>? output, CancellationToken token)
    {
        using (var probe = client.CreateCommand("sh -c " + Quote("command -v sudo >/dev/null 2>&1 || exit 3; sudo -n true 2>/dev/null")))
        {
            await probe.ExecuteAsync(token);
            if (probe.ExitStatus is 0 or 3) return null;
        }
        var store = new WindowsCredentialStore();
        string? remembered;
        lock (sudoPasswords) sudoPasswords.TryGetValue(target.Account, out remembered);
        if (remembered is null)
            using (var read = store.ReadSshSudoSecret(target.Account))
                read.Secret?.Use(value => remembered = Encoding.UTF8.GetString(Base64Url.DecodeFromChars(value)));
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var remember = false;
            var password = attempt == 0 ? remembered : null;
            if (password is null)
            {
                var answer = prompts.SudoPassword(target, attempt > 0);
                if (answer is null)
                {
                    output?.Report("No sudo password entered. Steps that need administrator rights will stop.");
                    return null;
                }
                (password, remember) = (answer.Password, answer.Remember);
            }
            using var check = client.CreateCommand("sh -c " + Quote("sudo -S -k -p '' true"));
            var run = check.ExecuteAsync(token);
            using (var stdin = check.CreateInputStream())
            {
                var bytes = Encoding.UTF8.GetBytes(password + "\n");
                await stdin.WriteAsync(bytes, token);
                CryptographicOperations.ZeroMemory(bytes);
            }
            await run;
            if (check.ExitStatus == 0)
            {
                if (remember) Remember(store, target, password);
                lock (sudoPasswords) sudoPasswords[target.Account] = password;
                return password;
            }
            if (password == remembered)
            {
                store.DeleteSshSudoSecret(target.Account);
                lock (sudoPasswords) sudoPasswords.Remove(target.Account);
            }
        }
        throw new HostShellException($"The sudo password for {target} did not work.");
    }

    private static void Remember(WindowsCredentialStore store, HostShellTarget target, string password)
    {
        var encoded = Base64Url.EncodeToString(Encoding.UTF8.GetBytes(password));
        if (encoded.Length > SecretLease.MaximumLength) return;
        using var lease = new SecretLease(encoded);
        store.WriteSshSudoSecret(target.Account, lease);
    }

    // ---------- streams ----------

    private static async Task FeedAsync(Stream input, string? sudo, HostShellCommand command, Task execute, CancellationToken token)
    {
        try
        {
            var first = (sudo is null ? "" : sudo + "\n") + (command.Input ?? "");
            if (first.Length > 0)
            {
                var bytes = Encoding.UTF8.GetBytes(first);
                await input.WriteAsync(bytes, token);
                await input.FlushAsync(token);
                CryptographicOperations.ZeroMemory(bytes);
            }
            if (command.Write is { } write)
            {
                await write(input, token);
                await input.FlushAsync(token);
            }
            if (command.MoreInput is { } more)
            {
                await Task.WhenAny(more, execute);
                if (!execute.IsCompleted && more.IsCompletedSuccessfully && more.Result is { Length: > 0 } last)
                {
                    await input.WriteAsync(Encoding.UTF8.GetBytes(last), token);
                    await input.FlushAsync(token);
                }
            }
        }
        catch (Exception error) when (error is IOException or ObjectDisposedException or SshException or OperationCanceledException) { }
        finally
        {
            try { input.Dispose(); } catch (Exception error) when (error is SshException or ObjectDisposedException or IOException) { }
        }
    }

    // SSH.NET's pipe streams are read by polling their length, so nothing blocks after the command ends.
    private static async Task PumpAsync(SshCommand run, Task execute, IProgress<string>? output)
    {
        var streams = new[] { new LineReader(run.OutputStream), new LineReader(run.ExtendedOutputStream) };
        while (true)
        {
            var done = execute.IsCompleted;
            var read = false;
            foreach (var reader in streams) read |= reader.Drain(output);
            if (done) break;
            if (!read) await Task.Delay(50);
        }
        foreach (var reader in streams) reader.Flush(output);
    }

    // Lines end at "\n"; a bare "\r" (progress bars such as a model download) replaces the line, and such updates are
    // shown at most every two seconds so progress streams without flooding the output.
    private sealed class LineReader(Stream stream)
    {
        private readonly Decoder decoder = new UTF8Encoding(false).GetDecoder();
        private readonly StringBuilder line = new();
        private readonly byte[] buffer = new byte[4096];
        private readonly char[] chars = new char[4097];
        private bool carriage;
        private long shown, partial;
        private StringBuilder? escape;

        internal bool Drain(IProgress<string>? output)
        {
            var any = false;
            try
            {
                while (stream.Length > 0)
                {
                    var count = stream.Read(buffer, 0, (int)Math.Min(buffer.Length, stream.Length));
                    if (count <= 0) break;
                    any = true;
                    var decoded = decoder.GetChars(buffer, 0, count, chars, 0);
                    for (var i = 0; i < decoded; i++)
                    {
                        var c = chars[i];
                        if (escape is not null)
                        {
                            // Terminal control sequence: dropped; "cursor to column" (ESC[nG) rewrites the line like "\r".
                            escape.Append(c);
                            if (escape.Length == 1 && c != '[' || escape.Length > 1 && c is >= '@' and <= '~' || escape.Length > 32)
                            {
                                if (escape.Length > 1 && c == 'G') carriage = true;
                                escape = null;
                            }
                            continue;
                        }
                        if (c == '\x1b') { escape = new(); continue; }
                        if (carriage && c != '\n')
                        {
                            if (System.Diagnostics.Stopwatch.GetElapsedTime(shown) >= TimeSpan.FromSeconds(2) && line.Length > 0)
                            {
                                Emit(output);
                                shown = System.Diagnostics.Stopwatch.GetTimestamp();
                            }
                            else line.Clear();
                        }
                        carriage = c == '\r';
                        if (carriage) continue;
                        if (c == '\n') Emit(output);
                        else if (line.Length < 8192) line.Append(c);
                    }
                    partial = System.Diagnostics.Stopwatch.GetTimestamp();
                }
                // A line that stays unfinished (a progress display without newlines) still shows every few seconds.
                if (line.Length > 0 && System.Diagnostics.Stopwatch.GetElapsedTime(partial) >= TimeSpan.FromSeconds(3) &&
                    System.Diagnostics.Stopwatch.GetElapsedTime(shown) >= TimeSpan.FromSeconds(3))
                {
                    Emit(output);
                    shown = System.Diagnostics.Stopwatch.GetTimestamp();
                }
            }
            catch (Exception error) when (error is ObjectDisposedException or IOException or NotSupportedException) { }
            return any;
        }

        internal void Flush(IProgress<string>? output) { if (line.Length > 0) Emit(output); }

        private void Emit(IProgress<string>? output)
        {
            output?.Report(Clean(line.ToString()));
            line.Clear();
        }
    }

    // ---------- Martlet's key and pinned host keys ----------

    private async Task<(PrivateKeyFile File, string Public)> LoadKeyAsync(CancellationToken token)
    {
        await FileGate.WaitAsync(token);
        try
        {
            System.IO.Directory.CreateDirectory(Directory);
            if (!File.Exists(KeyPath))
            {
                using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
                WritePrivate(KeyPath, ecdsa.ExportECPrivateKeyPem());
                File.WriteAllText(KeyPath + ".pub", PublicKey(ecdsa.ExportParameters(false)) + "\n");
            }
            using var reader = File.OpenRead(KeyPath);
            var file = new PrivateKeyFile(reader);
            string text;
            using (var ecdsa = ECDsa.Create())
            {
                ecdsa.ImportFromPem(File.ReadAllText(KeyPath));
                text = PublicKey(ecdsa.ExportParameters(false));
            }
            return (file, text);
        }
        finally { FileGate.Release(); }
    }

    /// <summary>OpenSSH public key line for a P-256 key.</summary>
    internal static string PublicKey(ECParameters key)
    {
        var point = new byte[65];
        point[0] = 4;
        key.Q.X!.CopyTo(point, 1);
        key.Q.Y!.CopyTo(point, 33);
        using var blob = new MemoryStream();
        Span<byte> length = stackalloc byte[4];
        foreach (var part in new[] { Encoding.ASCII.GetBytes("ecdsa-sha2-nistp256"), Encoding.ASCII.GetBytes("nistp256"), point })
        {
            BinaryPrimitives.WriteInt32BigEndian(length, part.Length);
            blob.Write(length);
            blob.Write(part);
        }
        var machine = new string(Environment.MachineName.Where(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_').ToArray());
        return $"ecdsa-sha2-nistp256 {Convert.ToBase64String(blob.ToArray())} martlet@{(machine.Length == 0 ? "desktop" : machine)}";
    }

    // Created with an ACL that grants this Windows user alone, before any key bytes are written (also the network key).
    internal static void WritePrivate(string path, string pem)
    {
        var security = new FileSecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new FileSystemAccessRule(WindowsIdentity.GetCurrent().User!, FileSystemRights.FullControl,
            AccessControlType.Allow));
        var temporary = path + ".tmp";
        using (var stream = new FileInfo(temporary).Create(FileMode.Create, FileSystemRights.FullControl, FileShare.None, 4096,
            FileOptions.None, security))
        {
            stream.Write(Encoding.ASCII.GetBytes(pem));
        }
        File.Move(temporary, path);
    }

    private void Pin(HostShellTarget target, string key)
    {
        FileGate.Wait();
        try
        {
            var hosts = ReadKnownHosts();
            if (hosts.TryGetValue(target.Machine, out var existing) && existing == key) return;
            hosts[target.Machine] = key;
            WriteKnownHosts(hosts);
        }
        finally { FileGate.Release(); }
    }

    private sealed record KnownHosts(int Version, Dictionary<string, string> Hosts);

    private Dictionary<string, string> ReadKnownHosts()
    {
        try
        {
            var document = JsonSerializer.Deserialize<KnownHosts>(File.ReadAllBytes(KnownHostsPath), Json);
            return document?.Version == 1 ? new(document.Hosts, StringComparer.Ordinal) : new(StringComparer.Ordinal);
        }
        catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException) { return new(StringComparer.Ordinal); }
        catch (JsonException error)
        {
            throw new HostShellException($"Martlet could not read its saved SSH trust ({error.Message}). Reset SSH trust and try again.", error);
        }
    }

    private void WriteKnownHosts(Dictionary<string, string> hosts)
    {
        System.IO.Directory.CreateDirectory(Directory);
        var temporary = KnownHostsPath + ".tmp";
        File.WriteAllBytes(temporary, JsonSerializer.SerializeToUtf8Bytes(new KnownHosts(1, hosts), Json));
        File.Move(temporary, KnownHostsPath, overwrite: true);
    }

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
}
