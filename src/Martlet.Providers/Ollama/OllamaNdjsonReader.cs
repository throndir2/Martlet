namespace Martlet.Providers.Ollama;

internal sealed class OllamaNdjsonReader(Stream stream, TextGenerationLimits limits,
    Action ensureActive, long? expectedLength)
{
    private readonly byte[] buffer = new byte[4096];
    private readonly byte[] line = new byte[limits.MaxEventBytes];
    private int offset, available, total, records;

    internal async ValueTask<ReadOnlyMemory<byte>?> ReadAsync(CancellationToken token)
    {
        while (true)
        {
            int length = 0;
            while (true)
            {
                ensureActive();
                if (offset == available)
                {
                    available = await stream.ReadAsync(buffer.AsMemory(0,
                        Math.Min(buffer.Length, limits.MaxStreamBytes - total + 1)), token).ConfigureAwait(false);
                    ensureActive();
                    offset = 0;
                    total += available;
                    if (total > limits.MaxStreamBytes) throw Failure(ProviderFailureCode.ResponseTooLarge);
                    if (expectedLength is { } expected &&
                        (total > expected || (available == 0 && total != expected)))
                        throw Failure(ProviderFailureCode.ResponseTruncated);
                    if (available == 0)
                    {
                        if (length != 0) throw Failure(ProviderFailureCode.ResponseTruncated);
                        return null;
                    }
                }
                if (length == line.Length) throw Failure(ProviderFailureCode.ResponseTooLarge);
                byte value = buffer[offset++];
                line[length++] = value;
                if (value == (byte)'\n') break;
            }
            length--;
            if (length > 0 && line[length - 1] == (byte)'\r') length--;
            if (length == 0) continue;
            if (++records > limits.MaxEvents) throw Failure(ProviderFailureCode.ResponseTooLarge);
            if (line.AsSpan(0, length).Contains((byte)'\r'))
                throw Failure(ProviderFailureCode.ResponseSchema);
            return line.AsMemory(0, length);
        }
    }

    internal async Task VerifyEndAsync(CancellationToken token)
    {
        if (await ReadAsync(token).ConfigureAwait(false) is not null)
            throw Failure(ProviderFailureCode.ResponseSchema);
    }

    private static OllamaChatProtocolException Failure(ProviderFailureCode code) => new(code);
}
