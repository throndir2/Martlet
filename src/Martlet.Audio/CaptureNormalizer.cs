using System.Buffers.Binary;
using System.Security.Cryptography;
using Martlet.Core.Contracts;

namespace Martlet.Audio;

// Streaming 64-tap windowed-sinc resampler. Source positions use integer rational arithmetic.
public sealed class CaptureNormalizer : IDisposable
{
    private const int TargetRate = 16000;
    private readonly CaptureSourceFormat format;
    private readonly byte[] remainder;
    private readonly double[] history = new double[128];
    private readonly double[][] phases;
    private int remainderCount;
    private long sourceSamples, outputSamples;
    private double first, last;
    private bool finished;
    public long SourceSamples => sourceSamples;
    public long OutputSamples => outputSamples;
    public int PendingSourceBytes => remainderCount;
    public double LastPeak { get; private set; }
    public double LastRms { get; private set; }

    public CaptureNormalizer(CaptureSourceFormat format)
    {
        ArgumentNullException.ThrowIfNull(format);
        format.Validate();
        this.format = format;
        remainder = new byte[format.BlockAlignment];
        var divisor = GreatestCommonDivisor(format.SampleRate, TargetRate);
        phases = new double[TargetRate / divisor][];
        if (format.SampleRate == TargetRate) return;
        for (var phase = 0; phase < phases.Length; phase++)
        {
            var coefficients = new double[64];
            var fraction = (double)(phase * divisor) / TargetRate;
            var cutoff = 0.45 * TargetRate / format.SampleRate;
            double sum = 0;
            for (var tap = 0; tap < coefficients.Length; tap++)
            {
                var x = tap - 31 - fraction;
                var sinc = Math.Abs(x) < 1e-12 ? 2 * cutoff : Math.Sin(2 * Math.PI * cutoff * x) / (Math.PI * x);
                var window = 0.42 - 0.5 * Math.Cos(2 * Math.PI * tap / 63) + 0.08 * Math.Cos(4 * Math.PI * tap / 63);
                sum += coefficients[tap] = sinc * window;
            }
            for (var tap = 0; tap < coefficients.Length; tap++) coefficients[tap] /= sum;
            phases[phase] = coefficients;
        }
    }

    public int MaximumOutputBytes(int inputBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(inputBytes);
        if (inputBytes > format.MaximumPacketBytes) throw new CaptureDeviceException(ErrorCode.PayloadTooLarge);
        return checked((int)(((long)(inputBytes + remainderCount) / format.BlockAlignment * TargetRate
            + format.SampleRate - 1) / format.SampleRate + 1) * 2);
    }

    public int Convert(ReadOnlySpan<byte> source, Span<byte> destination)
    {
        ObjectDisposedException.ThrowIf(finished, this);
        if (destination.Length < MaximumOutputBytes(source.Length))
            throw new ArgumentException("The normalization destination is too small.", nameof(destination));
        var written = 0;
        var count = 0;
        double squares = 0, peak = 0;
        while (!source.IsEmpty)
        {
            var copied = Math.Min(format.BlockAlignment - remainderCount, source.Length);
            source[..copied].CopyTo(remainder.AsSpan(remainderCount));
            remainderCount += copied;
            source = source[copied..];
            if (remainderCount != format.BlockAlignment) continue;
            double mixed = 0;
            for (var channel = 0; channel < format.Channels; channel++)
            {
                var bytes = remainder.AsSpan(channel * format.BitsPerSample / 8);
                double value = format.Encoding == DeviceSampleEncoding.IntegerPcm
                    ? BinaryPrimitives.ReadInt16LittleEndian(bytes) / 32768.0
                    : BinaryPrimitives.ReadSingleLittleEndian(bytes);
                if (!double.IsFinite(value)) throw new CaptureDeviceException(ErrorCode.AudioFormatUnsupported);
                mixed += Math.Clamp(value, -1, 1) / format.Channels;
            }
            CryptographicOperations.ZeroMemory(remainder);
            remainderCount = 0;
            if (sourceSamples == 0) first = mixed;
            last = history[sourceSamples % history.Length] = mixed;
            sourceSamples++;
            squares += mixed * mixed;
            peak = Math.Max(peak, Math.Abs(mixed));
            count++;
            if (format.SampleRate == TargetRate)
            {
                Write(mixed, destination, ref written);
                outputSamples++;
            }
            else
            {
                while (outputSamples * format.SampleRate / TargetRate + 32 < sourceSamples)
                    WriteFiltered(destination, ref written);
            }
        }
        LastPeak = peak;
        LastRms = count == 0 ? 0 : Math.Sqrt(squares / count);
        return written;
    }

    public int Complete(Span<byte> destination)
    {
        ObjectDisposedException.ThrowIf(finished, this);
        if (remainderCount != 0) throw new CaptureDeviceException(ErrorCode.StreamTruncated);
        var remaining = sourceSamples * TargetRate / format.SampleRate - outputSamples;
        if (destination.Length < remaining * 2) throw new ArgumentException("The final normalization destination is too small.", nameof(destination));
        var written = 0;
        while (outputSamples < sourceSamples * TargetRate / format.SampleRate)
            WriteFiltered(destination, ref written);
        Dispose();
        return written;
    }

    private void WriteFiltered(Span<byte> destination, ref int written)
    {
        var numerator = outputSamples * format.SampleRate;
        var center = numerator / TargetRate;
        var phase = (int)(numerator % TargetRate) / GreatestCommonDivisor(format.SampleRate, TargetRate);
        double value = 0;
        for (var tap = 0; tap < 64; tap++)
        {
            var index = center + tap - 31;
            var sample = index < 0 ? first : index >= sourceSamples ? last : history[index % history.Length];
            value += sample * phases[phase][tap];
        }
        Write(value, destination, ref written);
        outputSamples++;
    }

    private static void Write(double value, Span<byte> destination, ref int written)
    {
        var sample = (short)Math.Clamp(Math.Round(value * 32768, MidpointRounding.ToEven), short.MinValue, short.MaxValue);
        BinaryPrimitives.WriteInt16LittleEndian(destination[written..], sample);
        written += 2;
    }

    private static int GreatestCommonDivisor(int a, int b)
    {
        while (b != 0) (a, b) = (b, a % b);
        return a;
    }

    public void Dispose()
    {
        finished = true;
        CryptographicOperations.ZeroMemory(remainder);
        Array.Clear(history);
        remainderCount = 0;
        first = last = LastPeak = LastRms = 0;
    }
}
