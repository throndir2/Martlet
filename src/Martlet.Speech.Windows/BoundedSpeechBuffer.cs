using Martlet.Providers;

namespace Martlet.Speech.Windows;

internal sealed class BoundedSpeechBuffer(int maximumBytes, TimeProvider clock,
    SpeechSynthesisLimits limits, long started, Action ensureLifetime) : Stream
{
    private readonly object sync = new();
    private readonly MemoryStream bytes = new();
    private long? lastWrite;
    private WindowsSpeechFailure failure;
    internal WindowsSpeechFailure Failure { get { lock (sync) return failure; } }

    internal void EnsureActive()
    {
        lock (sync)
        {
            ensureLifetime();
            if (failure != WindowsSpeechFailure.None) throw new WindowsSpeechException(failure);
            if (lastWrite is null && clock.GetElapsedTime(started) >= limits.FirstAudioTimeout)
                throw new WindowsSpeechException(WindowsSpeechFailure.FirstAudioTimeout);
            if (lastWrite is { } last && clock.GetElapsedTime(last) >= limits.IdleTimeout)
                throw new WindowsSpeechException(WindowsSpeechFailure.IdleTimeout);
        }
    }

    public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

    public override void Write(ReadOnlySpan<byte> buffer)
    {
        lock (sync)
        {
            EnsureActive();
            if (buffer.Length > maximumBytes - bytes.Length)
            {
                failure = WindowsSpeechFailure.AudioLimit;
                throw new IOException("Windows speech exceeded the configured PCM bound.");
            }
            if (buffer.IsEmpty) return;
            bytes.Write(buffer);
            lastWrite = clock.GetTimestamp();
        }
    }

    internal byte[] Complete()
    {
        lock (sync)
        {
            EnsureActive();
            if (bytes.Length == 0 || bytes.Length % 2 != 0)
                throw new WindowsSpeechException(WindowsSpeechFailure.InvalidAudio);
            return bytes.ToArray();
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) bytes.Dispose();
        base.Dispose(disposing);
    }

    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length { get { lock (sync) return bytes.Length; } }
    public override long Position { get => Length; set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
}
