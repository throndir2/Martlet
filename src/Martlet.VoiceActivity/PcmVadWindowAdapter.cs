using System.Buffers.Binary;

namespace Martlet.VoiceActivity;

/// <summary>Consumes a borrowed 512-sample window synchronously; only validSamples belong to the source.</summary>
public delegate void VadWindowHandler(int sampleOffset, int validSamples, ReadOnlySpan<float> window);

/// <summary>
/// Converts canonical mono 16 kHz PCM16LE into consecutive 512-sample windows, without model context.
/// Each append borrows at most 3,200 bytes. The consumer must not retain the window after returning.
/// Only a partial window and, if necessary, one trailing byte are retained between calls.
/// </summary>
public sealed class PcmVadWindowAdapter : IDisposable
{
    private const int MaximumAppendBytes = 3_200;
    private readonly object gate = new();
    private readonly int maximumSamples;
    private readonly float[] window;
    private VadWindowHandler? consume;
    private int bufferedSamples;
    private byte pendingByte;
    private bool hasPendingByte, processing, completed, failed, disposed;

    public PcmVadWindowAdapter(int maximumSamples, VadWindowHandler consume)
    {
        VadCheck.Require(maximumSamples is >= 1 and <= VoiceActivityOptions.HardMaximumSamples && consume is not null);
        this.maximumSamples = maximumSamples;
        this.consume = consume;
        window = new float[VoiceActivityOptions.WindowSamples];
    }

    /// <summary>The number of complete source samples decoded, excluding padding or an unmatched byte.</summary>
    public int SamplesReceived { get; private set; }

    /// <summary>The number of windows offered to the consumer, including a window whose consumer failed.</summary>
    public int WindowCount { get; private set; }

    public void Append(ReadOnlySpan<byte> pcm)
    {
        lock (gate)
        {
            EnsureOpen();
            try
            {
                var receivedBytes = SamplesReceived * 2 + (hasPendingByte ? 1 : 0);
                VadCheck.Require(pcm.Length <= MaximumAppendBytes &&
                    pcm.Length <= maximumSamples * 2 - receivedBytes, VoiceActivityFailureCode.PayloadTooLarge);
                processing = true;
                var offset = 0;
                if (hasPendingByte && !pcm.IsEmpty)
                {
                    var sample = unchecked((short)(pendingByte | (pcm[0] << 8)));
                    pendingByte = 0;
                    hasPendingByte = false;
                    offset = 1;
                    AddSample(sample);
                }

                while (pcm.Length - offset >= 2)
                {
                    AddSample(BinaryPrimitives.ReadInt16LittleEndian(pcm.Slice(offset, 2)));
                    offset += 2;
                }

                if (offset < pcm.Length)
                {
                    pendingByte = pcm[offset];
                    hasPendingByte = true;
                }
            }
            catch
            {
                failed = true;
                throw;
            }
            finally
            {
                processing = false;
                if (failed || disposed) ClearRetainedState();
            }
        }
    }

    /// <summary>Completes once, rejecting empty or byte-truncated input and padding only the final tensor.</summary>
    public void Complete()
    {
        lock (gate)
        {
            EnsureOpen();
            try
            {
                VadCheck.Require(!hasPendingByte, VoiceActivityFailureCode.StreamTruncated);
                VadCheck.Require(SamplesReceived > 0);
                processing = true;
                if (bufferedSamples > 0)
                {
                    window.AsSpan(bufferedSamples).Clear();
                    EmitWindow();
                }
                completed = true;
            }
            catch
            {
                failed = true;
                throw;
            }
            finally
            {
                processing = false;
                ClearRetainedState();
            }
        }
    }

    public void Dispose()
    {
        lock (gate)
        {
            disposed = true;
            consume = null;
            // A consumer may dispose reentrantly; its borrowed span remains valid until it returns.
            if (!processing) ClearRetainedState();
        }
    }

    private void AddSample(short sample)
    {
        window[bufferedSamples++] = sample / 32768f;
        SamplesReceived++;
        if (bufferedSamples == VoiceActivityOptions.WindowSamples) EmitWindow();
    }

    private void EmitWindow()
    {
        var validSamples = bufferedSamples;
        WindowCount++;
        try
        {
            try
            {
                consume!(SamplesReceived - validSamples, validSamples, window);
            }
            catch (VoiceActivityException)
            {
                throw;
            }
            catch (Exception)
            {
                throw new VoiceActivityException(VoiceActivityFailureCode.CallbackFailed);
            }

            VadCheck.Require(!disposed, VoiceActivityFailureCode.Disposed);
            VadCheck.Require(!failed, VoiceActivityFailureCode.InvalidState);
        }
        finally
        {
            window.AsSpan().Clear();
            bufferedSamples = 0;
        }
    }

    private void EnsureOpen()
    {
        if (!disposed && !failed && !completed && !processing) return;
        failed = true;
        if (!processing) ClearRetainedState();
        throw new VoiceActivityException(disposed ? VoiceActivityFailureCode.Disposed : VoiceActivityFailureCode.InvalidState);
    }

    private void ClearRetainedState()
    {
        window.AsSpan().Clear();
        bufferedSamples = 0;
        pendingByte = 0;
        hasPendingByte = false;
        consume = null;
    }
}
