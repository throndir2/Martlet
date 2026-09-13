using System.Globalization;
using System.Net.Http.Headers;

namespace Martlet.Providers;

internal sealed class SpeechProtocolException(ProviderFailureCode code) : Exception("Unsupported speech response.")
{
    public ProviderFailureCode Code { get; } = code;
}

internal static class SpeechResponseHeaders
{
    public static long? Validate(HttpContent content)
    {
        if (!content.Headers.TryGetValues("Content-Type", out var types) ||
            types.Count() != 1 || !MediaTypeHeaderValue.TryParse(types.Single(), out var type) ||
            !(string.Equals(type.MediaType, "application/octet-stream", StringComparison.OrdinalIgnoreCase) ||
              string.Equals(type.MediaType, "audio/pcm", StringComparison.OrdinalIgnoreCase)) ||
            type.Parameters.Count != 0 || content.Headers.Contains("Content-Encoding"))
            throw new SpeechProtocolException(ProviderFailureCode.SpeechMediaUnsupported);
        if (!content.Headers.TryGetValues("Content-Length", out var lengths))
            return null;
        if (lengths.Count() != 1 ||
            !long.TryParse(lengths.Single(), NumberStyles.None, CultureInfo.InvariantCulture, out long length))
            throw new SpeechProtocolException(ProviderFailureCode.ResponseSchema);
        return length;
    }
}

// One 20 ms frame, no producer task/queue. The final pull must observe real HTTP body completion.
internal sealed class SpeechPcmReader(Stream stream, long maximumBytes, long? declaredBytes, Action ensureActive)
{
    internal const int FrameBytes = 960;
    private readonly byte[] buffer = new byte[FrameBytes];
    private long received;
    private bool eof;
    private bool prefixChecked;

    public async ValueTask<ReadOnlyMemory<byte>> ReadFrameAsync(CancellationToken token)
    {
        ensureActive();
        if (declaredBytes > maximumBytes)
            throw new SpeechProtocolException(ProviderFailureCode.OutputAudioLimit);
        if (eof)
            return ReadOnlyMemory<byte>.Empty;
        int count = 0;
        while (count < buffer.Length)
        {
            // At most one extra byte to detect a limit or declared-length violation.
            int capacity = (int)Math.Min(buffer.Length - count, maximumBytes - received + 1);
            if (declaredBytes is { } expected)
                capacity = (int)Math.Min(capacity, expected - received + 1);
            ensureActive();
            int read = await stream.ReadAsync(buffer.AsMemory(count, capacity), token).ConfigureAwait(false);
            ensureActive();
            if (read == 0)
            {
                eof = true;
                if (declaredBytes is { } length && received != length || received % 2 != 0)
                    throw new SpeechProtocolException(ProviderFailureCode.ResponseTruncated);
                if (received == 0)
                    throw new SpeechProtocolException(ProviderFailureCode.EmptyAudio);
                break;
            }
            received += read;
            count += read;
            if (received > maximumBytes)
                throw new SpeechProtocolException(ProviderFailureCode.OutputAudioLimit);
            if (declaredBytes is { } declared && received > declared)
                throw new SpeechProtocolException(ProviderFailureCode.ResponseTruncated);
        }
        if (!prefixChecked && count > 0)
        {
            RejectRecognizableNonPcm(buffer.AsSpan(0, count));
            prefixChecked = true;
        }
        return buffer.AsMemory(0, count);
    }

    private static void RejectRecognizableNonPcm(ReadOnlySpan<byte> bytes)
    {
        if (bytes.StartsWith("RIFF"u8) || bytes.StartsWith("RIFX"u8) || bytes.StartsWith("RF64"u8) ||
            bytes.StartsWith("OggS"u8) || bytes.StartsWith("fLaC"u8) || bytes.StartsWith("ID3"u8) ||
            bytes.StartsWith<byte>([0x1f, 0x8b]) || bytes.StartsWith("PK\u0003\u0004"u8) ||
            (bytes.Length >= 2 && bytes[0] == 0xff && (bytes[1] & 0xe0) == 0xe0))
            throw new SpeechProtocolException(ProviderFailureCode.SpeechMediaUnsupported);

        // Raw PCM has no magic. Reject ambiguous JSON/markup prefixes conservatively, not as a universal decoder.
        var prefix = bytes[..Math.Min(bytes.Length, 64)];
        int offset = prefix.StartsWith("\uFEFF"u8) ? 3 : 0;
        while (offset < prefix.Length && prefix[offset] is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n')
            offset++;
        if (offset < prefix.Length && prefix[offset] is (byte)'{' or (byte)'[' or (byte)'<')
            throw new SpeechProtocolException(ProviderFailureCode.SpeechMediaUnsupported);
    }
}
