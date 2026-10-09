using System.Text;
using System.Text.RegularExpressions;

namespace Martlet.Core.Installation;

/// <summary>Where Ollama for Windows keeps its data on this PC: its logs and app database under
/// <c>%LOCALAPPDATA%\Ollama</c>, its default models folder <c>%USERPROFILE%\.ollama\models</c> and the folder it is
/// installed in (<paramref name="InstallDirectory"/>, with <c>ollama.exe</c> and the tray app <c>ollama app.exe</c>).</summary>
public sealed record OllamaPlaces(string DataDirectory, string DefaultModels, string? InstallDirectory)
{
    /// <summary>The server's output since the tray app last started (the app rotates it to server-1.log ... server-5.log).</summary>
    public string ServerLog => Path.Combine(DataDirectory, "server.log");

    /// <summary>The tray app's own log since it last started (rotated like <see cref="ServerLog"/>).</summary>
    public string AppLog => Path.Combine(DataDirectory, "app.log");

    /// <summary>The tray app's SQLite database; its <c>settings.models</c> is the app's Model location.</summary>
    public string Database => Path.Combine(DataDirectory, "db.sqlite");

    public string? Server => InstallDirectory is null ? null : Path.Combine(InstallDirectory, "ollama.exe");

    public string? App => InstallDirectory is null ? null : Path.Combine(InstallDirectory, "ollama app.exe");

    /// <summary>This PC's Ollama places for the current user. <see cref="InstallDirectory"/> is null when Ollama isn't installed.</summary>
    public static OllamaPlaces ThisPc()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var install = new[]
        {
            Path.Combine(local, "Programs", "Ollama"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Ollama")
        }.FirstOrDefault(directory => File.Exists(Path.Combine(directory, "ollama.exe")));
        return new(Path.Combine(local, "Ollama"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ollama", "models"), install);
    }
}

/// <summary>What Ollama's own logs say about its last starts. <paramref name="LastError"/> is the server's last
/// <c>Error: ...</c> line (it stops after it), <paramref name="AppReason"/> why the tray app couldn't use its Model location
/// (for example "The path cannot be traversed because it contains an untrusted mount point"), <paramref name="ModelsPath"/>
/// the OLLAMA_MODELS of the last start, <paramref name="FailedStarts"/> how many of the read starts stopped with an error,
/// <paramref name="ListeningVersion"/> the version the last start answers as, and <paramref name="Written"/> when the server
/// log last changed.</summary>
public sealed record OllamaLogReading(string? LastError, string? AppReason, string? ModelsPath, int Starts, int FailedStarts,
    bool LastStartFailed, string? ListeningVersion, DateTimeOffset? Written)
{
    public static OllamaLogReading None { get; } = new(null, null, null, 0, 0, false, null, null);
}

/// <summary>Reads Ollama for Windows' <c>server.log</c> and <c>app.log</c>: only their last part, shared, so a log that a crash
/// loop grew to tens of megabytes is cheap to read and Ollama keeps writing it. Nothing is changed or sent anywhere.</summary>
public static partial class OllamaLogs
{
    /// <summary>How much of the end of each log is read.</summary>
    public const int TailBytes = 256 * 1024;

    [GeneratedRegex(@"OLLAMA_MODELS:(?<path>.*?)(?= [A-Z][A-Z0-9_]*:|\]""|$)", RegexOptions.CultureInvariant)]
    private static partial Regex ModelsPattern();

    [GeneratedRegex(@"msg=""Listening on \S+ \(version (?<version>[^)""]+)\)""", RegexOptions.CultureInvariant)]
    private static partial Regex ListeningPattern();

    [GeneratedRegex(@"\berr=(?:""(?<quoted>(?:[^""\\]|\\.)*)""|(?<bare>\S+))", RegexOptions.CultureInvariant)]
    private static partial Regex ErrPattern();

    /// <summary>Reads both logs in <paramref name="places"/>; <see cref="OllamaLogReading.None"/> parts where a log isn't there.</summary>
    public static OllamaLogReading Read(OllamaPlaces places)
    {
        var server = Tail(places.ServerLog);
        var app = Tail(places.AppLog);
        DateTimeOffset? written = null;
        try { if (File.Exists(places.ServerLog)) written = File.GetLastWriteTimeUtc(places.ServerLog); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        return Parse(server, app, written);
    }

    /// <summary>The last <paramref name="maxBytes"/> of <paramref name="path"/> as lines, without a cut first line; empty when
    /// the file isn't there or can't be read.</summary>
    public static IReadOnlyList<string> Tail(string path, int maxBytes = TailBytes)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var start = Math.Max(0, stream.Length - maxBytes);
            stream.Seek(start, SeekOrigin.Begin);
            var buffer = new byte[stream.Length - start];
            var read = 0;
            while (read < buffer.Length && stream.Read(buffer, read, buffer.Length - read) is > 0 and var count) read += count;
            var text = Encoding.UTF8.GetString(buffer, 0, read);
            var lines = text.Split('\n').Select(line => line.TrimEnd('\r')).ToList();
            if (start > 0 && lines.Count > 0) lines.RemoveAt(0);
            return lines.Where(line => line.Length > 0).ToArray();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return [];
        }
    }

    /// <summary>What <paramref name="serverLines"/> and <paramref name="appLines"/> say. A start is a <c>msg="server config"</c>
    /// line; it failed when an <c>Error:</c> line follows before it listens or the next start. Lines before the first start
    /// (the read began inside a start) count for the last start only when no start follows.</summary>
    public static OllamaLogReading Parse(IEnumerable<string> serverLines, IEnumerable<string> appLines, DateTimeOffset? written = null)
    {
        string? lastError = null, models = null, version = null;
        int starts = 0, failed = 0;
        bool inStart = false, startFailed = false, listened = false, lastFailed = false;
        foreach (var line in serverLines)
        {
            if (line.Contains("msg=\"server config\"", StringComparison.Ordinal))
            {
                starts++;
                inStart = true;
                startFailed = listened = lastFailed = false;
                version = null;
                models = ModelsPattern().Match(line) is { Success: true } match && Unquote(match.Groups["path"].Value) is { Length: > 0 } path
                    ? path : null;
                continue;
            }
            if (line.StartsWith("Error: ", StringComparison.Ordinal))
            {
                lastError = Shorten(line["Error: ".Length..]);
                lastFailed = true;
                if (inStart && !startFailed)
                {
                    startFailed = true;
                    failed++;
                }
                continue;
            }
            if (ListeningPattern().Match(line) is { Success: true } listening)
            {
                version = listening.Groups["version"].Value;
                listened = true;
                lastFailed = false;
            }
        }
        string? reason = null;
        foreach (var line in appLines)
        {
            if (!line.Contains("msg=\"models path not accessible", StringComparison.Ordinal)) continue;
            var err = ErrPattern().Match(line);
            if (!err.Success) continue;
            var text = err.Groups["quoted"].Success ? Unquote(err.Groups["quoted"].Value) : err.Groups["bare"].Value;
            // "CreateFile C:\...\models: The path cannot be traversed because it contains an untrusted mount point."
            var colon = text.LastIndexOf(": ", StringComparison.Ordinal);
            reason = Shorten(colon >= 0 ? text[(colon + 2)..] : text);
        }
        return new(lastError, reason, models, starts, failed, lastFailed && !listened, listened ? version : null, written);
    }

    /// <summary>A Go-quoted log value (backslashes doubled, quotes escaped) as it was before quoting.</summary>
    internal static string Unquote(string value)
    {
        if (!value.Contains('\\', StringComparison.Ordinal)) return value.Trim();
        var text = new StringBuilder(value.Length);
        for (var i = 0; i < value.Length; i++)
        {
            if (value[i] == '\\' && i + 1 < value.Length && value[i + 1] is '\\' or '"')
            {
                text.Append(value[i + 1]);
                i++;
            }
            else text.Append(value[i]);
        }
        return text.ToString().Trim();
    }

    private static string Shorten(string text)
    {
        var line = string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return line.Length <= 400 ? line : line[..400] + "…";
    }
}
