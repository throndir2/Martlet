using System.Runtime.CompilerServices;
using Martlet.Avatar.Audio2Face.Remote;
using Martlet.Avatar.Hosting;
using Martlet.Core.Contracts;
using Martlet.Core.Settings;
using Martlet.Credentials.Windows;

namespace Martlet.Desktop;

/// <summary>A paired Martlet host that relays Audio2Face from another computer.</summary>
internal interface IAvatarHostLink : IDisposable
{
    string Authority { get; }
    /// <summary>True when the host currently advertises its Audio2Face relay (cached; rechecked at most every 30 s).</summary>
    Task<bool> ReadyAsync(CancellationToken token);
    IAsyncEnumerable<RemoteFaceFrame> AnimateAsync(CorrelationIds ids, long epoch, int sampleRate, ReadOnlyMemory<byte> pcm,
        CancellationToken token);
    void Invalidate();
}

internal sealed class GatewayAvatarHostLink(Audio2FaceHostConnection connection, TimeProvider? clock = null) : IAvatarHostLink
{
    private static readonly TimeSpan Recheck = TimeSpan.FromSeconds(30);
    private readonly TimeProvider time = clock ?? TimeProvider.System;
    private Audio2FaceHostRoute? route;
    private DateTimeOffset checkedAt = DateTimeOffset.MinValue;

    public string Authority => new Uri(connection.Pairing.Origin).Authority;

    public async Task<bool> ReadyAsync(CancellationToken token)
    {
        if (Volatile.Read(ref route) is not null) return true;
        if (time.GetUtcNow() - checkedAt < Recheck) return false;
        checkedAt = time.GetUtcNow();
        try { Volatile.Write(ref route, await connection.ReadRouteAsync(token).ConfigureAwait(false)); }
        catch (Audio2FaceHostException) { route = null; }
        return route is not null;
    }

    public IAsyncEnumerable<RemoteFaceFrame> AnimateAsync(CorrelationIds ids, long epoch, int sampleRate,
        ReadOnlyMemory<byte> pcm, CancellationToken token) =>
        connection.AnimateAsync(Volatile.Read(ref route) ?? throw new InvalidOperationException("The host isn't ready."),
            ids, epoch, sampleRate, pcm, token);

    public void Invalidate()
    {
        Volatile.Write(ref route, null);
        checkedAt = time.GetUtcNow();
    }

    public void Dispose() => connection.Dispose();

    /// <summary>Opens the saved pairing with its device secret from Windows Credential Manager.</summary>
    internal static IAvatarHostLink? Open(AvatarRemoteHost host)
    {
        using var read = new WindowsCredentialStore().ReadAvatarHostSecret(host.HostId, host.CredentialId);
        if (read.Error != CredentialError.None || read.Secret is null) return null;
        IAvatarHostLink? link = null;
        read.Secret.Use(secret => link = new GatewayAvatarHostLink(new Audio2FaceHostConnection(Pairing(host), secret)));
        return link;
    }

    internal static Audio2FaceHostPairing Pairing(AvatarRemoteHost host) => new()
    {
        Origin = host.Origin, HostId = host.HostId, SpkiFingerprint = host.SpkiFingerprint,
        DeviceId = host.DeviceId, CredentialId = host.CredentialId
    };
}

/// <summary>Growing copy of one generated-speech sentence's PCM, for chunked relay requests.</summary>
internal sealed class PcmAccumulator
{
    private readonly object gate = new();
    private byte[] data = new byte[48_000];
    private int length;
    private TaskCompletionSource changed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal bool Completed { get; private set; }
    internal bool Failed { get; private set; }
    internal long Samples { get { lock (gate) return length / 2; } }

    internal void Append(ReadOnlySpan<byte> pcm)
    {
        TaskCompletionSource signal;
        lock (gate)
        {
            if (Completed) return;
            if (length + pcm.Length > data.Length) Array.Resize(ref data, Math.Max(data.Length * 2, length + pcm.Length));
            pcm.CopyTo(data.AsSpan(length));
            length += pcm.Length;
            signal = changed;
            changed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }
        signal.TrySetResult();
    }

    internal void Complete(bool failed)
    {
        TaskCompletionSource signal;
        lock (gate)
        {
            Completed = true;
            Failed |= failed;
            signal = changed;
        }
        signal.TrySetResult();
    }

    /// <summary>Waits until at least <paramref name="samples"/> samples exist or the sentence ended.</summary>
    internal async Task WaitForAsync(long samples, CancellationToken token)
    {
        while (true)
        {
            Task wait;
            lock (gate)
            {
                if (Completed || length / 2 >= samples) return;
                wait = changed.Task;
            }
            await wait.WaitAsync(token).ConfigureAwait(false);
        }
    }

    internal ReadOnlyMemory<byte> Slice(long startSample, long endSample)
    {
        lock (gate) return data.AsSpan((int)startSample * 2, (int)(endSample - startSample) * 2).ToArray();
    }
}

internal static class RemoteChunks
{
    /// <summary>First chunk half a second (fast start), then one second, each with half a second of prior context.</summary>
    internal static async IAsyncEnumerable<(long From, long Start, long End)> PlanAsync(PcmAccumulator pcm, int sampleRate,
        [EnumeratorCancellation] CancellationToken token)
    {
        long start = 0;
        var first = true;
        while (true)
        {
            var target = start + (first ? sampleRate / 2 : sampleRate);
            await pcm.WaitForAsync(target, token).ConfigureAwait(false);
            if (pcm.Failed) yield break;
            var end = Math.Min(target, pcm.Samples);
            if (end <= start) yield break;
            yield return (Math.Max(0, start - sampleRate / 2), start, end);
            start = end;
            first = false;
        }
    }
}
