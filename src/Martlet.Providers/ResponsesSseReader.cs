using System.Text;

namespace Martlet.Providers;

internal sealed record ResponsesSseEvent(string? EventType, ReadOnlyMemory<byte> Data)
{
    public override string ToString() => nameof(ResponsesSseEvent);
}

// Pull-based: one bounded record and a small read-ahead buffer, never a producer queue.
internal sealed class ResponsesSseReader(Stream stream, TextGenerationLimits limits, Action ensureActive, long? expectedLength)
{
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private readonly byte[] buffer = new byte[4096];
    private readonly byte[] line = new byte[limits.MaxEventBytes];
    private readonly byte[] data = new byte[limits.MaxEventBytes];
    private int offset;
    private int available;
    private int totalBytes;
    private int events;
    private bool firstLine = true;

    public async ValueTask<ResponsesSseEvent?> ReadAsync(CancellationToken token)
    {
        int recordBytes = 0;
        int dataLength = 0;
        bool hasData = false;
        string? eventType = null;
        while (true)
        {
            int length = 0;
            bool eof = false;
            while (true)
            {
                ensureActive();
                if (offset == available)
                {
                    available = await stream.ReadAsync(buffer.AsMemory(0,
                        Math.Min(buffer.Length, limits.MaxStreamBytes - totalBytes + 1)), token).ConfigureAwait(false);
                    ensureActive();
                    offset = 0;
                    totalBytes += available;
                    if (totalBytes > limits.MaxStreamBytes)
                        throw new ResponseProtocolException(ProviderFailureCode.ResponseTooLarge);
                    if (expectedLength is { } expected && (totalBytes > expected || (available == 0 && totalBytes != expected)))
                        throw new ResponseProtocolException(ProviderFailureCode.ResponseTruncated);
                    if (available == 0) { eof = true; break; }
                }
                byte value = buffer[offset++];
                if (++recordBytes > limits.MaxEventBytes)
                    throw new ResponseProtocolException(ProviderFailureCode.ResponseTooLarge);
                if (value == (byte)'\n') break;
                line[length++] = value;
            }
            if (eof)
            {
                if (recordBytes != 0 || hasData)
                    throw new ResponseProtocolException(ProviderFailureCode.ResponseTruncated);
                return null;
            }

            if (length > 0 && line[length - 1] == (byte)'\r')
                length--;
            string text;
            try { text = Utf8.GetString(line, 0, length); }
            catch (DecoderFallbackException) { throw new ResponseProtocolException(ProviderFailureCode.ResponseSchema); }
            if (firstLine)
            {
                firstLine = false;
                text = text.TrimStart('\uFEFF');
            }
            if (text.Length == 0)
            {
                if (hasData)
                {
                    if (++events > limits.MaxEvents)
                        throw new ResponseProtocolException(ProviderFailureCode.ResponseTooLarge);
                    return new(eventType, data.AsMemory(0, dataLength).ToArray());
                }
                // Heartbeats are bounded by total bytes/time even when they dispatch no event.
                recordBytes = 0;
                eventType = null;
                continue;
            }
            if (text[0] == ':')
                continue;
            int colon = text.IndexOf(':');
            string field = colon < 0 ? text : text[..colon];
            string valueText = colon < 0 ? "" : text[(colon + 1)..];
            if (valueText.StartsWith(' '))
                valueText = valueText[1..];
            if (field == "event")
            {
                if (eventType is not null || valueText.Length > 128)
                    throw new ResponseProtocolException(ProviderFailureCode.ResponseSchema);
                eventType = valueText;
            }
            else if (field == "data")
            {
                int byteCount = Utf8.GetByteCount(valueText);
                int needed = dataLength + byteCount + (hasData ? 1 : 0);
                if (needed > data.Length)
                    throw new ResponseProtocolException(ProviderFailureCode.ResponseTooLarge);
                if (hasData) data[dataLength++] = (byte)'\n';
                dataLength += Utf8.GetBytes(valueText, data.AsSpan(dataLength));
                hasData = true;
            }
            // SSE id/retry/extension fields do not cause reconnects or inference retries.
        }
    }

    public async Task VerifyDeclaredEndAsync(CancellationToken token)
    {
        if (expectedLength is null)
            return;
        bool sentinel = false;
        while (await ReadAsync(token).ConfigureAwait(false) is { } item)
        {
            if (sentinel || !item.Data.Span.SequenceEqual("[DONE]"u8) || item.EventType is not null)
                throw new ResponseProtocolException(ProviderFailureCode.ResponseSchema);
            sentinel = true;
        }
    }
}

internal sealed class ResponseProtocolException(ProviderFailureCode code) : Exception("The Responses stream violated its supported contract.")
{
    public ProviderFailureCode Code { get; } = code;
}
