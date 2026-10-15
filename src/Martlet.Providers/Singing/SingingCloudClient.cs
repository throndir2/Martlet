using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Martlet.Core.Singing;

namespace Martlet.Providers.Singing;

/// <summary>Audio a cloud provider sent: an encoded file (MP3, FLAC, WAV), or raw mono 16-bit PCM at <see cref="RawPcmRate"/>.</summary>
public sealed record CloudAudio(byte[] Bytes, int? RawPcmRate = null);

/// <summary>A song a cloud provider wrote: its mix, its own stems when it gave them, lyric lines when it timed them, what wrote it,
/// and whether it already sings in the voice Martlet speaks with (then no voice match follows).</summary>
public sealed record CloudSong(CloudAudio Mix, string Generator, bool SungInVoice = false, CloudAudio? Vocals = null, CloudAudio? Backing = null)
{
    public IReadOnlyList<SongLyricLine> Lines { get; init; } = [];
}

/// <summary>A cloud provider that writes a song from its lyrics and style (Singing's music list).</summary>
public interface ICloudSongWriter : IDisposable
{
    /// <summary>Writes <paramref name="request"/>'s song. <paramref name="voiceRecording"/> is the voice's recording (a WAV), for a
    /// provider that sings in it; empty otherwise. Failures throw <see cref="SongException"/> (<c>singing.busy</c> for a rate limit,
    /// <c>singing.unavailable</c> for a missing or refused key, an exhausted quota or an outage).</summary>
    Task<CloudSong> WriteAsync(SongRequest request, ReadOnlyMemory<byte> voiceRecording, IProgress<double>? progress, CancellationToken token);
}

/// <summary>A cloud provider that changes a song's sung vocals into the voice (Singing's voice match list).</summary>
public interface ICloudVoiceChanger : IDisposable
{
    /// <summary>Changes <paramref name="vocalsWav"/> (48 kHz mono WAV) into the voice of <paramref name="recordingWav"/>, named
    /// <paramref name="voiceName"/> on the provider (made once, then found again by that name).</summary>
    Task<CloudAudio> ChangeAsync(string voiceName, ReadOnlyMemory<byte> recordingWav, ReadOnlyMemory<byte> vocalsWav, CancellationToken token);
}

/// <summary>Plain HTTP for Singing's cloud providers: one client per provider, its errors as <see cref="SongException"/>s, bounded answers.</summary>
public abstract class SingingCloudClient : IDisposable
{
    public const int MaximumAudioBytes = 96 * 1024 * 1024;
    protected readonly HttpClient Http;
    protected readonly Uri Origin;
    protected readonly string Key;

    protected SingingCloudClient(Uri origin, string key, HttpMessageHandler? handler)
    {
        if (origin.Scheme != Uri.UriSchemeHttps && !(origin.Scheme == Uri.UriSchemeHttp && origin.IsLoopback))
            throw new ArgumentException("A singing provider is reached over HTTPS (or a loopback fixture).", nameof(origin));
        Origin = origin;
        Key = key;
        Http = new HttpClient(handler ?? new SocketsHttpHandler
        {
            AllowAutoRedirect = false, UseCookies = false, AutomaticDecompression = DecompressionMethods.All, ConnectTimeout = TimeSpan.FromSeconds(15)
        }, disposeHandler: true) { Timeout = TimeSpan.FromMinutes(10) };
    }

    protected abstract string Name { get; }
    protected abstract void Authorize(HttpRequestMessage request);

    /// <summary>Refuses a missing key before anything is sent.</summary>
    protected void RequireKey()
    {
        if (string.IsNullOrWhiteSpace(Key) || Key.Length > 1024 || Key.Any(c => char.IsControl(c) || char.IsWhiteSpace(c)))
            throw new SongException(SongErrorCodes.Unavailable, $"Add your {Name} API key on Companion › Singing.");
    }

    protected Task<byte[]> PostJsonForBytesAsync(string path, JsonNode body, CancellationToken token) =>
        SendAsync(() => new HttpRequestMessage(HttpMethod.Post, new Uri(Origin, path))
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json")
        }, token);

    protected async Task<JsonNode> PostJsonAsync(string path, JsonNode body, CancellationToken token) =>
        Parse(await PostJsonForBytesAsync(path, body, token).ConfigureAwait(false));

    protected async Task<JsonNode> GetJsonAsync(Uri url, CancellationToken token) =>
        Parse(await SendAsync(() => new HttpRequestMessage(HttpMethod.Get, url), token).ConfigureAwait(false));

    /// <summary>Downloads a file the provider's answer pointed to (an HTTPS URL; no key is sent there).</summary>
    protected async Task<byte[]> DownloadAsync(string? url, CancellationToken token)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps && !(uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback))
            throw Failed($"{Name} gave no audio address Martlet will open.");
        return await SendAsync(() => new HttpRequestMessage(HttpMethod.Get, uri), token, authorize: false).ConfigureAwait(false);
    }

    protected async Task<byte[]> SendAsync(Func<HttpRequestMessage> make, CancellationToken token, bool authorize = true)
    {
        using var request = make();
        if (authorize) Authorize(request);
        HttpResponseMessage response;
        try { response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false); }
        catch (HttpRequestException error) { throw new SongException(SongErrorCodes.Unavailable, $"{Name} isn't reachable ({error.Message}).", error); }
        catch (TaskCanceledException error) when (!token.IsCancellationRequested)
        {
            throw new SongException(SongErrorCodes.Unavailable, $"{Name} didn't answer in time.", error);
        }
        using (response)
        {
            await using var stream = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
            using var buffer = new MemoryStream();
            var chunk = new byte[81920];
            int read;
            while ((read = await stream.ReadAsync(chunk, token).ConfigureAwait(false)) > 0)
            {
                buffer.Write(chunk, 0, read);
                if (buffer.Length > MaximumAudioBytes) throw Failed($"{Name}'s answer was too large.");
            }
            var bytes = buffer.ToArray();
            if (response.IsSuccessStatusCode) return bytes;
            var said = Describe(bytes);
            var status = (int)response.StatusCode;
            throw new SongException(status switch
            {
                429 => SongErrorCodes.Busy,
                401 or 402 or 403 or 404 or 408 or >= 500 => SongErrorCodes.Unavailable,
                _ => SongErrorCodes.Failed
            }, $"{Name} refused the song (HTTP {status}{(said is null ? "" : ": " + said)}).");
        }
    }

    protected SongException Failed(string message) => new(SongErrorCodes.Failed, message);

    /// <summary>Polls <paramref name="poll"/> every <paramref name="interval"/> until it returns a result, for up to 15 minutes.</summary>
    protected static async Task<T> PollAsync<T>(Func<CancellationToken, Task<T?>> poll, TimeSpan interval, CancellationToken token) where T : class
    {
        var until = DateTimeOffset.UtcNow + TimeSpan.FromMinutes(15);
        while (true)
        {
            if (await poll(token).ConfigureAwait(false) is { } done) return done;
            if (DateTimeOffset.UtcNow > until) throw new SongException(SongErrorCodes.TimedOut, "The cloud provider took too long to make the song.");
            await Task.Delay(interval, token).ConfigureAwait(false);
        }
    }

    private JsonNode Parse(byte[] bytes)
    {
        try { return JsonNode.Parse(bytes) ?? throw Failed($"{Name} gave an empty answer."); }
        catch (JsonException) { throw Failed($"{Name}'s answer wasn't JSON."); }
    }

    private static string? Describe(byte[] body)
    {
        var text = Encoding.UTF8.GetString(body.AsSpan(0, Math.Min(body.Length, 600))).ReplaceLineEndings(" ").Trim();
        return text.Length == 0 ? null : text.Length > 240 ? text[..240] + "..." : text;
    }

    public void Dispose()
    {
        Http.Dispose();
        GC.SuppressFinalize(this);
    }
}

/// <summary>The sections of a song's lyrics as a music provider wants them: each section's name and lines (at most 30 lines of 200
/// characters), and its share of the song's length by its lines, within the provider's bounds.</summary>
public static class CloudLyrics
{
    public static IReadOnlyList<(string Name, IReadOnlyList<string> Lines, int Milliseconds)> Sections(string lyrics, int seconds,
        int minimumMilliseconds = 3_000, int maximumMilliseconds = 120_000)
    {
        var groups = new List<(string Name, List<string> Lines)>();
        string? last = null;
        foreach (var (section, text) in SongLyrics.Parse(lyrics))
        {
            var name = section.Length == 0 ? "verse" : section;
            if (groups.Count == 0 || last != section || groups[^1].Lines.Count >= 30) groups.Add((name, []));
            last = section;
            groups[^1].Lines.Add(text.Length > 200 ? text[..200] : text);
        }
        if (groups.Count == 0) groups.Add(("verse", []));
        var total = seconds * 1000;
        var weights = groups.Select(g => g.Lines.Count + 1).ToArray();
        var sum = weights.Sum();
        var lengths = weights.Select(w => Math.Clamp((int)((long)total * w / sum), minimumMilliseconds, maximumMilliseconds)).ToArray();
        // The last section takes what rounding left, so the song is as long as asked (within each section's bounds).
        lengths[^1] = Math.Clamp(lengths[^1] + total - lengths.Sum(), minimumMilliseconds, maximumMilliseconds);
        return [.. groups.Select((g, i) => (char.ToUpperInvariant(g.Name[0]) + g.Name[1..], (IReadOnlyList<string>)g.Lines, lengths[i]))];
    }
}
