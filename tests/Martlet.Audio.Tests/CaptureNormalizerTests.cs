using System.Buffers.Binary;
using System.Reflection;
using Martlet.Core.Contracts;

namespace Martlet.Audio.Tests;

public sealed class CaptureNormalizerTests
{
    internal static byte[] Pcm(params short[] samples)
    {
        var bytes = new byte[samples.Length * 2];
        for (var i = 0; i < samples.Length; i++) BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(i * 2), samples[i]);
        return bytes;
    }
    internal static byte[] Float(params float[] samples)
    {
        var bytes = new byte[samples.Length * 4];
        for (var i = 0; i < samples.Length; i++) BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(i * 4), samples[i]);
        return bytes;
    }
    private static byte[] Convert(CaptureSourceFormat format, byte[] source, int fragmentSize)
    {
        using var normalizer = new CaptureNormalizer(format);
        var result = new List<byte>();
        var buffer = new byte[3264];
        for (var offset = 0; offset < source.Length; offset += fragmentSize)
        {
            var count = normalizer.Convert(source.AsSpan(offset, Math.Min(fragmentSize, source.Length - offset)), buffer);
            result.AddRange(buffer.AsSpan(0, count).ToArray());
        }
        result.AddRange(buffer.AsSpan(0, normalizer.Complete(buffer)).ToArray());
        return result.ToArray();
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(7)]
    [InlineData(200)]
    public void PcmIdentityIsExactAcrossOddFragments(int size)
    {
        var samples = new short[] { short.MinValue, -16384, -1, 0, 1, 16384, short.MaxValue };
        var bytes = Pcm(samples);
        Assert.Equal(bytes, Convert(new(16000, 1, 16, DeviceSampleEncoding.IntegerPcm), bytes, size));
    }

    [Fact]
    public void ChannelMixAndFloatClippingAreRealSampleMath()
    {
        Assert.Equal(Pcm(0, 8192, -8192), Convert(new(16000, 2, 16, DeviceSampleEncoding.IntegerPcm),
            Pcm(-16384, 16384, 0, 16384, -16384, 0), 3));
        Assert.Equal(Pcm(0, 8192, -32768, 32767, 0), Convert(new(16000, 2, 32, DeviceSampleEncoding.IeeeFloat),
            Float(-0.5f, 0.5f, 0f, 0.5f, -2f, -1f, 2f, 1f, -1f, 1f), 7));
    }

    [Theory]
    [InlineData(16000, 1, false)]
    [InlineData(24000, 2, false)]
    [InlineData(32000, 3, true)]
    [InlineData(44100, 2, true)]
    [InlineData(48000, 1, true)]
    [InlineData(48000, 8, false)]
    [InlineData(96000, 8, true)]
    public void AllSupportedRatesProduceExactCountAndChunkInvariantSamples(int rate, int channels, bool floating)
    {
        var format = new CaptureSourceFormat(rate, channels, floating ? 32 : 16,
            floating ? DeviceSampleEncoding.IeeeFloat : DeviceSampleEncoding.IntegerPcm);
        var samples = Enumerable.Range(0, (rate / 3 + 17) * channels)
            .Select(i => (short)(Math.Sin(i / channels * 0.173) * 12000)).ToArray();
        var bytes = floating ? Float(samples.Select(s => s / 32768f).ToArray()) : Pcm(samples);
        var contiguous = Convert(format, bytes, format.MaximumPacketBytes);
        Assert.Equal((long)(samples.Length / channels) * 16000 / rate * 2, contiguous.Length);
        Assert.Equal(contiguous, Convert(format, bytes, 7));
    }

    [Theory]
    [InlineData(24000)]
    [InlineData(44100)]
    [InlineData(48000)]
    [InlineData(96000)]
    public void ResamplingPreservesDcIncludingEdges(int rate)
    {
        var bytes = Pcm(Enumerable.Repeat((short)8192, rate / 10).ToArray());
        var converted = Convert(new(rate, 1, 16, DeviceSampleEncoding.IntegerPcm), bytes, 11);
        Assert.Equal(Pcm(Enumerable.Repeat((short)8192, 1600).ToArray()), converted);
    }

    [Fact]
    public void FractionalRateImpulseMatchesIndependentFullSignalReference()
    {
        const int rate = 44100;
        var source = new short[4410];
        source[731] = 30000;
        var output = Convert(new(rate, 1, 16, DeviceSampleEncoding.IntegerPcm), Pcm(source), 13);
        var expected = new short[1600];
        for (var n = 0; n < expected.Length; n++)
        {
            var position = n * (double)rate / 16000;
            var center = (int)Math.Floor(position);
            var fraction = position - center;
            double sum = 0, value = 0;
            for (var tap = 0; tap < 64; tap++)
            {
                var x = tap - 31 - fraction;
                var cutoff = 0.45 * 16000 / rate;
                var coefficient = (Math.Abs(x) < 1e-12 ? 2 * cutoff : Math.Sin(2 * Math.PI * cutoff * x) / (Math.PI * x))
                    * (0.42 - 0.5 * Math.Cos(2 * Math.PI * tap / 63) + 0.08 * Math.Cos(4 * Math.PI * tap / 63));
                sum += coefficient;
                value += source[Math.Clamp(center + tap - 31, 0, source.Length - 1)] * coefficient;
            }
            expected[n] = (short)Math.Clamp(Math.Round(value / sum), short.MinValue, short.MaxValue);
        }
        Assert.Equal(Pcm(expected), output);
    }

    [Theory]
    [InlineData(1000, 0.45, 0.55)]
    [InlineData(12000, 0, 0.005)]
    public void DownsamplerPassesVoiceBandAndRejectsHighFrequencyAlias(int frequency, double minimum, double maximum)
    {
        var bytes = Float(Enumerable.Range(0, 48000).Select(i => (float)(0.5 * Math.Sin(2 * Math.PI * frequency * i / 48000))).ToArray());
        var result = Convert(new(48000, 1, 32, DeviceSampleEncoding.IeeeFloat), bytes, 1536);
        double squares = 0;
        for (var i = 100; i < 15900; i++)
        {
            var sample = BinaryPrimitives.ReadInt16LittleEndian(result.AsSpan(i * 2)) / 32768.0;
            squares += sample * sample;
        }
        var amplitude = Math.Sqrt(squares / 15800 * 2);
        Assert.InRange(amplitude, minimum, maximum);
    }

    [Theory]
    [InlineData(float.NaN)]
    [InlineData(float.PositiveInfinity)]
    [InlineData(float.NegativeInfinity)]
    public void InvalidFloatIsNotSuccessfulSilence(float sample)
    {
        using var converter = new CaptureNormalizer(new(48000, 1, 32, DeviceSampleEncoding.IeeeFloat));
        var ex = Assert.Throws<CaptureDeviceException>(() => converter.Convert(Float(sample), new byte[3264]));
        Assert.Equal(ErrorCode.AudioFormatUnsupported, ex.Code);
    }

    [Fact]
    public void IncompleteSampleAndOversizedPacketFailExplicitlyAndDisposeClearsHistory()
    {
        using var converter = new CaptureNormalizer(new(48000, 2, 16, DeviceSampleEncoding.IntegerPcm));
        converter.Convert(Pcm(1234, 2345, 3456), new byte[3264]);
        Assert.Equal(2, converter.PendingSourceBytes);
        Assert.Equal(ErrorCode.StreamTruncated, Assert.Throws<CaptureDeviceException>(() => converter.Complete(new byte[3264])).Code);
        Assert.Equal(ErrorCode.PayloadTooLarge, Assert.Throws<CaptureDeviceException>(() => converter.MaximumOutputBytes(19201)).Code);
        converter.Dispose();
        Assert.Equal(0, converter.PendingSourceBytes);
        Assert.All((double[])typeof(CaptureNormalizer).GetField("history", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(converter)!,
            value => Assert.Equal(0, value));
        Assert.Throws<ObjectDisposedException>(() => converter.Convert(Pcm(0), new byte[3264]));
    }

    [Theory]
    [InlineData(8000, 1, 16, DeviceSampleEncoding.IntegerPcm)]
    [InlineData(192000, 1, 16, DeviceSampleEncoding.IntegerPcm)]
    [InlineData(48000, 0, 16, DeviceSampleEncoding.IntegerPcm)]
    [InlineData(48000, 9, 16, DeviceSampleEncoding.IntegerPcm)]
    [InlineData(48000, 2, 24, DeviceSampleEncoding.IntegerPcm)]
    [InlineData(48000, 2, 32, DeviceSampleEncoding.IntegerPcm)]
    [InlineData(48000, 2, 16, DeviceSampleEncoding.IeeeFloat)]
    public void UnsupportedSourceFormatsFailBeforeAllocation(int rate, int channels, int bits, DeviceSampleEncoding encoding)
    {
        Assert.Equal(ErrorCode.AudioFormatUnsupported,
            Assert.Throws<CaptureDeviceException>(() => new CaptureNormalizer(new(rate, channels, bits, encoding))).Code);
    }
}
