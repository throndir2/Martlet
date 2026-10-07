using System.Collections.Concurrent;
using Martlet.Core.Cluster;

namespace Martlet.Avatar.Audio2Face.Remote;

/// <summary>
/// The desktop's <see cref="ILiveGpuHold"/>: asks a paired host, with signed requests over its pinned pairing, to keep the
/// graphics cards behind the live routes free of Thinking pool work (POST /martlet/v1/priority/hold and /release). A hold is
/// best effort and never breaks a live turn: a host problem is logged once (until a call to that host works again) and the
/// call returns. A host older than GPU priority answers request.invalid; it is left alone for <see cref="OldHostPause"/>, so
/// an update there is noticed. Each host keeps one connection, which renewals reuse. Thread-safe.
/// </summary>
public sealed class HostLiveGpuHold : ILiveGpuHold, IDisposable
{
    /// <summary>The longest hold one call asks for; longer holds are shortened to it (renew to keep the cards).</summary>
    public static readonly TimeSpan MaximumTtl = Audio2FaceHostConnection.MaximumGpuHold;
    /// <summary>How long one hold or release may take before the live turn goes on without it.</summary>
    public static readonly TimeSpan CallTimeout = TimeSpan.FromSeconds(3);
    /// <summary>How long a host older than GPU priority is not asked again.</summary>
    public static readonly TimeSpan OldHostPause = TimeSpan.FromMinutes(10);
    private readonly Func<string, IHostGpuHoldChannel?> connect;
    private readonly Action<string>? log;
    private readonly TimeProvider clock;
    private readonly ConcurrentDictionary<string, IHostGpuHoldChannel> channels = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, DateTimeOffset> oldHosts = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, DateTimeOffset> held = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, byte> noted = new(StringComparer.Ordinal);
    private int disposed;

    /// <param name="connect">A paired connection to the host with this ID (for example an <see cref="Audio2FaceHostConnection"/>
    /// made with the pairing's secret); null when this PC isn't paired with it. Kept and reused until a call fails.</param>
    /// <param name="log">Where problems go (the desktop log), never with secrets.</param>
    public HostLiveGpuHold(Func<string, IHostGpuHoldChannel?> connect, Action<string>? log = null, TimeProvider? clock = null)
    {
        this.connect = connect ?? throw new ArgumentNullException(nameof(connect));
        this.log = log;
        this.clock = clock ?? TimeProvider.System;
    }

    /// <summary>When the last hold on <paramref name="hostId"/> ends, unless renewed; null when this PC holds nothing there.</summary>
    public DateTimeOffset? HeldUntil(string hostId) =>
        held.TryGetValue(hostId, out var until) && until > clock.GetUtcNow() ? until : null;

    public async Task HoldAsync(string hostId, IReadOnlyList<string> routeIds, TimeSpan ttl, CancellationToken token)
    {
        ArgumentException.ThrowIfNullOrEmpty(hostId);
        ArgumentNullException.ThrowIfNull(routeIds);
        token.ThrowIfCancellationRequested();
        var routes = routeIds.Where(id => !string.IsNullOrEmpty(id)).Distinct(StringComparer.Ordinal).Take(16).ToArray();
        if (routes.Length == 0 || ttl <= TimeSpan.Zero || OldHost(hostId)) return;
        if (ttl > MaximumTtl) ttl = MaximumTtl;
        HostGpuHold? grant = null;
        if (await TryAsync(hostId, "hold", async (channel, t) => grant = await channel.HoldGpusAsync(routes, ttl, t).ConfigureAwait(false),
                token).ConfigureAwait(false) && grant is not null)
            held[hostId] = grant.Until;
    }

    public async Task ReleaseAsync(string hostId, CancellationToken token)
    {
        ArgumentException.ThrowIfNullOrEmpty(hostId);
        token.ThrowIfCancellationRequested();
        // Nothing to end when this PC holds nothing there or its hold already ran out.
        if (!held.TryRemove(hostId, out var until) || until <= clock.GetUtcNow() || OldHost(hostId)) return;
        await TryAsync(hostId, "release", (channel, t) => channel.ReleaseGpusAsync(t), token).ConfigureAwait(false);
    }

    private bool OldHost(string hostId) => oldHosts.TryGetValue(hostId, out var until) && until > clock.GetUtcNow();

    // One call to the host within CallTimeout: true when it worked. A host problem is noted and returns false; only the caller's
    // own cancellation throws.
    private async Task<bool> TryAsync(string hostId, string what, Func<IHostGpuHoldChannel, CancellationToken, Task> call,
        CancellationToken token)
    {
        IHostGpuHoldChannel? channel;
        try { channel = Channel(hostId); }
        catch (Exception error) when (error is not OperationCanceledException and not OutOfMemoryException)
        {
            Note(hostId, "connect", $"Live turn first: couldn't connect to {hostId} to {what} its graphics card ({error.Message}); " +
                "the live turn goes on without the hold.");
            return false;
        }
        if (channel is null)
        {
            Note(hostId, "unpaired", $"Live turn first: this PC isn't paired with {hostId}, so its graphics card isn't held for live turns.");
            return false;
        }
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(CallTimeout);
        try
        {
            await call(channel, timeout.Token).ConfigureAwait(false);
            foreach (var key in noted.Keys.Where(k => k.StartsWith(hostId + "|", StringComparison.Ordinal)))
                noted.TryRemove(key, out _);
            return true;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Audio2FaceHostException error) when (error.Code == "request.invalid")
        {
            // A host older than GPU priority has no such endpoint: leave it alone for a while (it may be updated meanwhile).
            oldHosts[hostId] = clock.GetUtcNow() + OldHostPause;
            held.TryRemove(hostId, out _);
            Note(hostId, "old", $"Live turn first: Martlet host {hostId} doesn't hold graphics cards for live turns (an older " +
                "version); update it. Live turns there go on without the hold.");
            return false;
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            Drop(hostId, channel);
            var why = error is OperationCanceledException ? $"no answer within {CallTimeout.TotalSeconds:0} s"
                : error is Audio2FaceHostException host ? host.Code : error.GetType().Name;
            Note(hostId, why, $"Live turn first: couldn't {what} the graphics card on {hostId} ({why}); the live turn goes on without it.");
            return false;
        }
    }

    private IHostGpuHoldChannel? Channel(string hostId)
    {
        if (Volatile.Read(ref disposed) != 0) throw new ObjectDisposedException(nameof(HostLiveGpuHold));
        if (channels.TryGetValue(hostId, out var existing)) return existing;
        var created = connect(hostId);
        if (created is null) return null;
        if (channels.TryAdd(hostId, created)) return created;
        created.Dispose();
        return channels.TryGetValue(hostId, out existing) ? existing : null;
    }

    // A failed call may mean a changed pairing or a broken connection: the next call connects again.
    private void Drop(string hostId, IHostGpuHoldChannel channel)
    {
        if (channels.TryRemove(KeyValuePair.Create(hostId, channel))) channel.Dispose();
    }

    private void Note(string hostId, string kind, string message)
    {
        if (!noted.TryAdd(hostId + "|" + kind, 0)) return;
        try { log?.Invoke(message); }
        catch (Exception error) when (error is not OutOfMemoryException) { } // logging never breaks a live turn
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        foreach (var hostId in channels.Keys)
            if (channels.TryRemove(hostId, out var channel)) channel.Dispose();
    }

    public override string ToString() => nameof(HostLiveGpuHold);
}
