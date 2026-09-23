namespace Martlet.Host.Setup;

internal static class ArtifactContentTransfer
{
    internal static async ValueTask<long> CopyAsync(Stream source, long expectedBytes, long offset,
        Func<CancellationToken, ValueTask> checkCurrent,
        Func<ReadOnlyMemory<byte>, CancellationToken, ValueTask> write,
        CancellationToken cancellationToken, Func<int, int>? readWindow = null, Action<int>? bytesRead = null)
    {
        var buffer = new byte[65_536];
        var position = offset;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await checkCurrent(cancellationToken).ConfigureAwait(false);
            var remaining = expectedBytes - position;
            if (remaining < 0) throw new ArtifactAcquisitionException(ArtifactAcquisitionFailure.SizeExceeded);
            var maximum = (int)Math.Min(buffer.Length, checked(remaining + 1));
            maximum = readWindow?.Invoke(maximum) ?? maximum;
            int count;
            try { count = await source.ReadAsync(buffer.AsMemory(0, maximum), cancellationToken).ConfigureAwait(false); }
            catch (Exception error) when (error is IOException or HttpRequestException)
            {
                throw new ArtifactAcquisitionException(ArtifactAcquisitionFailure.TransportFailed);
            }
            bytesRead?.Invoke(count);
            if (count == 0) return position;
            if (count > remaining)
                throw new ArtifactAcquisitionException(ArtifactAcquisitionFailure.SizeExceeded);
            await write(buffer.AsMemory(0, count), cancellationToken).ConfigureAwait(false);
            position = checked(position + count);
        }
    }
}
