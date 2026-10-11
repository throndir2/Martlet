using System.Collections.Concurrent;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;

namespace Martlet.Providers.LocalModels;

/// <summary>An answer from a public metadata source: the HTTP status (0 when it wasn't reached), the body for a success, and
/// why it failed in words.</summary>
internal readonly record struct MetadataAnswer(int Status, byte[]? Body, string? Problem, bool FromCache = false)
{
    public bool Ok => Status is >= 200 and <= 299 && Body is not null;
}

/// <summary>Reads public, keyless metadata (Hugging Face's model API, the Ollama registry) politely: each answer is kept for a
/// day, at most <c>budget</c> requests go out in any five minutes (Hugging Face allows about 500), and a 429 pauses requests
/// for as long as the server asks. Sends no key or anything said; follows the client's redirects.</summary>
internal sealed class PublicMetadataSource(HttpClient client, TimeProvider clock, int budget, string name)
{
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan Keep = TimeSpan.FromDays(1);
    private const int MaximumBytes = 8 * 1024 * 1024;
    private const int MaximumCached = 512;
    private readonly ConcurrentDictionary<string, (DateTimeOffset At, MetadataAnswer Answer)> cache = new(StringComparer.Ordinal);
    private readonly Queue<DateTimeOffset> sent = new();
    private readonly object gate = new();
    private DateTimeOffset pausedUntil;
    private int total;

    /// <summary>Requests sent to the source (not answered from the cache).</summary>
    public int Sent => Volatile.Read(ref total);

    public async Task<MetadataAnswer> GetAsync(Uri uri, string? accept, CancellationToken token)
    {
        var key = uri.AbsoluteUri + "|" + accept;
        var now = clock.GetUtcNow();
        if (cache.TryGetValue(key, out var kept) && now - kept.At < Keep) return kept.Answer with { FromCache = true };
        if (!Take(now, out var why)) return new(0, null, why);
        MetadataAnswer answer;
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(token);
        limit.CancelAfter(TimeSpan.FromSeconds(20));
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            if (accept is not null) request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(accept));
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, limit.Token).ConfigureAwait(false);
            var status = (int)response.StatusCode;
            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                var wait = response.Headers.RetryAfter?.Delta ?? (response.Headers.RetryAfter?.Date is { } date ? date - now : (TimeSpan?)null) ?? Window;
                lock (gate) pausedUntil = now + (wait < TimeSpan.Zero ? Window : wait > TimeSpan.FromHours(1) ? TimeSpan.FromHours(1) : wait);
                return new(status, null, $"{name} asked Martlet to slow down (429); it waits before asking again");
            }
            if (!response.IsSuccessStatusCode)
            {
                answer = new(status, null, status switch
                {
                    401 or 403 => $"{name} needs a sign-in for {uri.AbsolutePath} (a gated model)",
                    404 => $"{name} has no {uri.AbsolutePath}",
                    _ => $"{name} answered {status} for {uri.AbsolutePath}"
                });
                if (status is 401 or 403 or 404) Remember(key, now, answer);
                return answer;
            }
            if (response.Content.Headers.ContentLength > MaximumBytes) return new(status, null, $"{name}'s answer was too large");
            await using var stream = await response.Content.ReadAsStreamAsync(limit.Token).ConfigureAwait(false);
            using var buffer = new MemoryStream();
            var chunk = new byte[81920];
            int read;
            while ((read = await stream.ReadAsync(chunk, limit.Token).ConfigureAwait(false)) > 0)
            {
                if (buffer.Length + read > MaximumBytes) return new(status, null, $"{name}'s answer was too large");
                buffer.Write(chunk, 0, read);
            }
            answer = new(status, buffer.ToArray(), null);
            Remember(key, now, answer);
            return answer;
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            return new(0, null, $"{name} didn't answer within 20 seconds");
        }
        catch (HttpRequestException error)
        {
            return new(0, null, $"Couldn't reach {name} ({error.Message})");
        }
    }

    private bool Take(DateTimeOffset now, out string? why)
    {
        lock (gate)
        {
            why = null;
            if (now < pausedUntil)
            {
                why = $"{name} asked Martlet to slow down; it asks again after {pausedUntil:HH:mm} UTC";
                return false;
            }
            while (sent.Count > 0 && now - sent.Peek() >= Window) sent.Dequeue();
            if (sent.Count >= budget)
            {
                why = $"Martlet already asked {name} {budget} times in five minutes; it asks again in a few minutes";
                return false;
            }
            sent.Enqueue(now);
            total++;
            return true;
        }
    }

    private void Remember(string key, DateTimeOffset now, MetadataAnswer answer)
    {
        if (cache.Count >= MaximumCached)
            foreach (var old in cache.Where(entry => now - entry.Value.At >= Keep).Select(entry => entry.Key).ToArray()) cache.TryRemove(old, out _);
        if (cache.Count < MaximumCached) cache[key] = (now, answer);
    }
}
