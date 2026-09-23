using System.Net;

namespace Martlet.Host.Setup;

// Do not let an exception from the network body retain a signed URI in an inner exception.
internal sealed class ArtifactDownloadContent : HttpContent
{
    private readonly HttpContent inner;
    private readonly long? contentLength;

    internal ArtifactDownloadContent(HttpContent inner)
    {
        this.inner = inner;
        contentLength = inner.Headers.ContentLength;
        foreach (var header in inner.Headers)
            Headers.TryAddWithoutValidation(header.Key, header.Value);
    }

    protected override bool TryComputeLength(out long length)
    {
        length = contentLength ?? 0;
        return contentLength.HasValue;
    }

    protected override Task<Stream> CreateContentReadStreamAsync() =>
        CreateContentReadStreamAsync(CancellationToken.None);

    protected override async Task<Stream> CreateContentReadStreamAsync(CancellationToken cancellationToken)
    {
        try
        {
            return new RedactedStream(await inner.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false));
        }
        catch (OperationCanceledException)
        {
            throw new OperationCanceledException("Artifact transfer canceled.", cancellationToken);
        }
        catch (Exception error) when (error is IOException or HttpRequestException or InvalidOperationException)
        {
            throw new IOException("Artifact response stream failed.");
        }
    }

    protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
        SerializeToStreamAsync(stream, context, CancellationToken.None);

    protected override async Task SerializeToStreamAsync(
        Stream stream, TransportContext? context, CancellationToken cancellationToken)
    {
        using var source = await CreateContentReadStreamAsync(cancellationToken).ConfigureAwait(false);
        await source.CopyToAsync(stream, 64 * 1024, cancellationToken).ConfigureAwait(false);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            inner.Dispose();
        base.Dispose(disposing);
    }

    private sealed class RedactedStream(Stream inner) : Stream
    {
        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

        public override Task<int> ReadAsync(
            byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            try
            {
                return await inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw new OperationCanceledException("Artifact transfer canceled.", cancellationToken);
            }
            catch (Exception error) when (error is IOException or HttpRequestException or InvalidOperationException)
            {
                throw new IOException("Artifact response stream failed.");
            }
        }

        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
                inner.Dispose();
            base.Dispose(disposing);
        }
    }
}
