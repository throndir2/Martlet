using System.Buffers.Binary;
using System.Reflection;

namespace Martlet.VoiceActivity.Tests;

public sealed class WindowAdapterTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(7)]
    [InlineData(640)]
    [InlineData(1024)]
    [InlineData(3200)]
    public void ArbitraryByteSplitsPreserveEverySampleAndExactWindowOffsets(int fragmentBytes)
    {
        var samples = Enumerable.Range(0, 2501).Select(i => unchecked((short)(i * 7919))).ToArray();
        var pcm = Pcm(samples);
        var original = pcm.ToArray();
        var observed = new List<ObservedWindow>();
        using var adapter = Collect(observed);

        for (var offset = 0; offset < pcm.Length; offset += fragmentBytes)
            adapter.Append(pcm.AsSpan(offset, Math.Min(fragmentBytes, pcm.Length - offset)));
        adapter.Complete();

        Assert.Equal(samples.Length, adapter.SamplesReceived);
        Assert.Equal(5, adapter.WindowCount);
        Assert.Equal(new[] { 0, 512, 1024, 1536, 2048 }, observed.Select(w => w.Offset));
        Assert.Equal(new[] { 512, 512, 512, 512, 453 }, observed.Select(w => w.Valid));
        Assert.Equal(samples.Select(s => s / 32768f), observed.SelectMany(w => w.Samples.Take(w.Valid)));
        Assert.All(observed, w => Assert.Equal(512, w.Samples.Length));
        Assert.All(observed[^1].Samples.Skip(observed[^1].Valid), sample => Assert.Equal(0f, sample));
        Assert.Equal(original, pcm);
        AssertCleared(adapter);
    }

    [Fact]
    public void SignedLittleEndianValuesUse32768RatherThan32767()
    {
        var observed = new List<ObservedWindow>();
        using var adapter = Collect(observed);
        var pcm = Pcm(short.MinValue, -16384, -1, 0, 1, 16384, short.MaxValue);
        for (var i = 0; i < pcm.Length; i++) adapter.Append(pcm.AsSpan(i, 1));
        adapter.Complete();

        var result = Assert.Single(observed);
        Assert.Equal(7, result.Valid);
        Assert.Equal(new[] { -1f, -0.5f, -1 / 32768f, 0f, 1 / 32768f, 0.5f, 32767 / 32768f },
            result.Samples.Take(result.Valid));
    }

    [Theory]
    [InlineData(511, 0, 1)]
    [InlineData(512, 1, 1)]
    [InlineData(513, 1, 2)]
    [InlineData(1024, 2, 2)]
    public void FullWindowEdgesDoNotEmitEarlyOrDuplicateAtCompletion(int samples, int before, int after)
    {
        var observed = new List<ObservedWindow>();
        using var adapter = Collect(observed);
        adapter.Append(Pcm(Enumerable.Repeat((short)1234, samples).ToArray()));
        Assert.Equal(before, adapter.WindowCount);
        adapter.Complete();

        Assert.Equal(after, adapter.WindowCount);
        Assert.Equal(samples, observed.Sum(w => w.Valid));
        Assert.Equal(samples, adapter.SamplesReceived);
        Assert.All(observed.SelectMany(w => w.Samples.Take(w.Valid)), value => Assert.Equal(1234 / 32768f, value));
    }

    [Fact]
    public void ThreeHundredTwentySampleCadenceHasNoRoundingOrContextOverlap()
    {
        var source = Enumerable.Range(0, 2560).Select(i => (short)i).ToArray();
        var observed = new List<ObservedWindow>();
        using var adapter = Collect(observed);
        var counts = new[] { 0, 1, 1, 2, 3, 3, 4, 5 };
        for (var i = 0; i < counts.Length; i++)
        {
            adapter.Append(Pcm(source.AsSpan(i * 320, 320).ToArray()));
            Assert.Equal((i + 1) * 320, adapter.SamplesReceived);
            Assert.Equal(counts[i], adapter.WindowCount);
        }
        adapter.Complete();

        Assert.Equal(5, adapter.WindowCount);
        Assert.All(observed, w => Assert.Equal(512, w.Valid));
        Assert.Equal(new[] { 0, 512, 1024, 1536, 2048 }, observed.Select(w => w.Offset));
        Assert.Equal(source.Select(s => s / 32768f), observed.SelectMany(w => w.Samples));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(511)]
    [InlineData(512)]
    [InlineData(513)]
    [InlineData(1600)]
    [InlineData(480000)]
    public void ExactMaximumIsAcceptedWithoutCountingTensorPadding(int maximumSamples)
    {
        var offset = 0;
        var count = 0;
        using var adapter = new PcmVadWindowAdapter(maximumSamples, (start, valid, window) =>
        {
            Assert.Equal(offset, start);
            Assert.Equal(Math.Min(512, maximumSamples - start), valid);
            Assert.Equal(512, window.Length);
            offset += valid;
            count++;
        });
        for (var remaining = maximumSamples; remaining > 0;)
        {
            var samples = Math.Min(1600, remaining);
            adapter.Append(new byte[samples * 2]);
            remaining -= samples;
        }
        Assert.Equal(maximumSamples / 512, adapter.WindowCount);
        adapter.Complete();

        Assert.Equal(maximumSamples, adapter.SamplesReceived);
        Assert.Equal(maximumSamples, offset);
        Assert.Equal((maximumSamples + 511) / 512, count);
        Assert.Equal(count, adapter.WindowCount);
        Assert.InRange(count, 1, VoiceActivityOptions.HardMaximumScores);
        AssertCleared(adapter);
    }

    [Theory]
    [InlineData(3201)]
    [InlineData(3202)]
    public void OversizedAppendIsRejectedBeforeDecodingOrInvokingConsumer(int chunkBytes)
    {
        var calls = 0;
        using var adapter = new PcmVadWindowAdapter(480000, (_, _, _) => calls++);
        adapter.Append(new byte[] { 0, 64, 123 });
        Assert.Equal(1, adapter.SamplesReceived);

        AssertFailure(VoiceActivityFailureCode.PayloadTooLarge, () => adapter.Append(new byte[chunkBytes]));

        Assert.Equal(1, adapter.SamplesReceived);
        Assert.Equal(0, calls);
        Assert.Equal(0, adapter.WindowCount);
        AssertCleared(adapter);
        AssertFailedState(adapter);
    }

    [Fact]
    public void WholeAppendOverflowIsRejectedBeforeCompletingAnAlreadyPendingWindow()
    {
        var calls = 0;
        using var adapter = new PcmVadWindowAdapter(513, (_, _, _) => calls++);
        adapter.Append(Enumerable.Repeat((byte)127, 1023).ToArray());
        Assert.Equal(511, adapter.SamplesReceived);

        AssertFailure(VoiceActivityFailureCode.PayloadTooLarge, () => adapter.Append(new byte[4]));

        Assert.Equal(511, adapter.SamplesReceived);
        Assert.Equal(0, calls);
        AssertCleared(adapter);
        AssertFailedState(adapter);
    }

    [Fact]
    public void PendingByteCountsAgainstTotalByteCapacity()
    {
        using var adapter = new PcmVadWindowAdapter(1, (_, _, _) => { });
        adapter.Append(new byte[] { 255 });
        AssertFailure(VoiceActivityFailureCode.PayloadTooLarge, () => adapter.Append(new byte[2]));
        Assert.Equal(0, adapter.SamplesReceived);
        Assert.Equal(0, adapter.WindowCount);
        AssertCleared(adapter);
        AssertFailedState(adapter);
    }

    [Fact]
    public void AnyByteBeyondMaximumFailsInsteadOfTruncatingAlreadyEmittedInput()
    {
        var calls = 0;
        using var adapter = new PcmVadWindowAdapter(512, (_, _, _) => calls++);
        adapter.Append(new byte[1024]);
        AssertFailure(VoiceActivityFailureCode.PayloadTooLarge, () => adapter.Append(new byte[1]));
        Assert.Equal(1, calls);
        Assert.Equal(512, adapter.SamplesReceived);
        AssertFailedState(adapter);
        Assert.Equal(1, calls);
    }

    [Fact]
    public void EmptyAppendIsInertButEmptyInputIsAnExplicitLatchedFailure()
    {
        var calls = 0;
        using var adapter = new PcmVadWindowAdapter(512, (_, _, _) => calls++);
        adapter.Append(ReadOnlySpan<byte>.Empty);
        AssertFailure(VoiceActivityFailureCode.InvalidInput, adapter.Complete);
        Assert.Equal(0, calls);
        AssertCleared(adapter);
        AssertFailedState(adapter);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(1025)]
    [InlineData(1027)]
    public void IncompletePcmSampleNeverBecomesPaddedSourceOrSuccessfulCompletion(int byteCount)
    {
        var calls = 0;
        using var adapter = new PcmVadWindowAdapter(480000, (_, _, _) => calls++);
        adapter.Append(Enumerable.Repeat((byte)127, byteCount).ToArray());
        var before = calls;

        AssertFailure(VoiceActivityFailureCode.StreamTruncated, adapter.Complete);

        Assert.Equal(byteCount / 2, adapter.SamplesReceived);
        Assert.Equal(byteCount / 1024, before);
        Assert.Equal(before, calls);
        AssertCleared(adapter);
        AssertFailedState(adapter);
        Assert.Equal(before, calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CompleteIsOneShotAndAppendAfterCompleteCannotReplay(bool appendFirst)
    {
        var calls = 0;
        using var adapter = new PcmVadWindowAdapter(512, (_, _, _) => calls++);
        adapter.Append(Pcm(1));
        adapter.Complete();
        if (appendFirst)
            AssertFailure(VoiceActivityFailureCode.InvalidState, () => adapter.Append(ReadOnlySpan<byte>.Empty));
        else
            AssertFailure(VoiceActivityFailureCode.InvalidState, adapter.Complete);
        AssertFailedState(adapter);
        Assert.Equal(1, calls);
        AssertCleared(adapter);
    }

    [Fact]
    public void DisposeClearsPartialSamplesAndOddByteAndRejectsFurtherWork()
    {
        var adapter = new PcmVadWindowAdapter(512, (_, _, _) => { });
        adapter.Append(new byte[] { 0, 64, 123 });
        Assert.Equal(0.5f, Field<float[]>(adapter, "window")[0]);
        Assert.Equal((byte)123, Field<byte>(adapter, "pendingByte"));

        adapter.Dispose();
        adapter.Dispose();

        AssertCleared(adapter);
        AssertFailure(VoiceActivityFailureCode.Disposed, () => adapter.Append(Pcm(1)));
        AssertFailure(VoiceActivityFailureCode.Disposed, adapter.Complete);
        Assert.Equal(1, adapter.SamplesReceived);
    }

    [Fact]
    public void ReusedWindowIsClearedAtEachSafeReturnWithoutChangingCallerPcm()
    {
        var observed = new List<ObservedWindow>();
        using var adapter = Collect(observed);
        var pcm = Pcm(Enumerable.Repeat((short)16384, 512).ToArray());
        var original = pcm.ToArray();
        adapter.Append(pcm);
        Assert.All(Field<float[]>(adapter, "window"), value => Assert.Equal(0f, value));
        adapter.Append(Pcm(8192));
        adapter.Complete();

        Assert.Equal(original, pcm);
        Assert.All(observed[0].Samples, value => Assert.Equal(0.5f, value));
        Assert.Equal(1, observed[1].Valid);
        Assert.Equal(0.25f, observed[1].Samples[0]);
        Assert.All(observed[1].Samples.Skip(1), value => Assert.Equal(0f, value));
        AssertCleared(adapter);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CallbackExceptionIsFixedAndLatchedWithNoRemainingWindowDelivery(bool finalWindow)
    {
        const string privateMessage = "private-callback-canary";
        var calls = 0;
        using var adapter = new PcmVadWindowAdapter(480000, (_, _, window) =>
        {
            calls++;
            Assert.Equal(0.5f, window[0]);
            throw new InvalidOperationException(privateMessage);
        });
        var pcm = Pcm(Enumerable.Repeat((short)16384, finalWindow ? 17 : 1024).ToArray());
        if (finalWindow) adapter.Append(pcm);
        var failure = finalWindow
            ? Assert.Throws<VoiceActivityException>(adapter.Complete)
            : Assert.Throws<VoiceActivityException>(() => adapter.Append(pcm));

        Assert.Equal(VoiceActivityFailureCode.CallbackFailed, failure.Failure.Code);
        Assert.Equal(new VoiceActivityFailure(VoiceActivityFailureCode.CallbackFailed).Summary, failure.Message);
        Assert.Null(failure.InnerException);
        Assert.DoesNotContain(privateMessage, failure.ToString());
        Assert.Equal(finalWindow ? 17 : 512, adapter.SamplesReceived);
        Assert.Equal(1, adapter.WindowCount);
        AssertCleared(adapter);
        AssertFailedState(adapter);
        Assert.Equal(1, calls);
    }

    [Theory]
    [InlineData(VoiceActivityFailureCode.InvalidScore)]
    [InlineData(VoiceActivityFailureCode.Canceled)]
    [InlineData(VoiceActivityFailureCode.DeadlineExceeded)]
    [InlineData(VoiceActivityFailureCode.PayloadTooLarge)]
    public void TypedConsumerFailureIsPreservedRatherThanMisclassified(VoiceActivityFailureCode code)
    {
        var expected = new VoiceActivityException(code);
        using var adapter = new PcmVadWindowAdapter(512, (_, _, _) => throw expected);

        Assert.Same(expected, Assert.Throws<VoiceActivityException>(() => adapter.Append(new byte[1024])));

        AssertCleared(adapter);
        AssertFailedState(adapter);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SwallowedReentrantStateErrorStillFailsAndDoesNotClearAnActiveBorrow(bool complete)
    {
        PcmVadWindowAdapter? adapter = null;
        var calls = 0;
        adapter = new(480000, (_, _, window) =>
        {
            calls++;
            if (complete)
                AssertFailure(VoiceActivityFailureCode.InvalidState, adapter!.Complete);
            else
                AssertFailure(VoiceActivityFailureCode.InvalidState, () => adapter!.Append(ReadOnlySpan<byte>.Empty));
            Assert.All(window.ToArray(), value => Assert.Equal(0.5f, value));
        });
        using (adapter)
        {
            AssertFailure(VoiceActivityFailureCode.InvalidState,
                () => adapter.Append(Pcm(Enumerable.Repeat((short)16384, 1024).ToArray())));
            Assert.Equal(1, calls);
            Assert.Equal(512, adapter.SamplesReceived);
            AssertCleared(adapter);
            AssertFailedState(adapter);
        }
    }

    [Fact]
    public void ReentrantDisposeDefersClearingUntilBorrowReturnsAndStopsRemainingInput()
    {
        PcmVadWindowAdapter? adapter = null;
        var calls = 0;
        adapter = new(480000, (_, _, window) =>
        {
            calls++;
            adapter!.Dispose();
            Assert.All(window.ToArray(), value => Assert.Equal(0.5f, value));
        });
        using (adapter)
        {
            AssertFailure(VoiceActivityFailureCode.Disposed,
                () => adapter.Append(Pcm(Enumerable.Repeat((short)16384, 1024).ToArray())));
            Assert.Equal(1, calls);
            Assert.Equal(512, adapter.SamplesReceived);
            AssertCleared(adapter);
            AssertFailure(VoiceActivityFailureCode.Disposed, adapter.Complete);
        }
    }

    [Theory]
    [InlineData(int.MinValue)]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(480001)]
    [InlineData(int.MaxValue)]
    public void InvalidCapacityIsRejected(int maximumSamples) =>
        AssertFailure(VoiceActivityFailureCode.InvalidInput,
            () => new PcmVadWindowAdapter(maximumSamples, (_, _, _) => { }));

    [Fact]
    public void MissingConsumerIsRejected() =>
        AssertFailure(VoiceActivityFailureCode.InvalidInput, () => new PcmVadWindowAdapter(512, null!));

    private static PcmVadWindowAdapter Collect(List<ObservedWindow> observed) =>
        new(VoiceActivityOptions.HardMaximumSamples,
            (offset, valid, window) => observed.Add(new(offset, valid, window.ToArray())));

    private static byte[] Pcm(params short[] samples)
    {
        var bytes = new byte[samples.Length * 2];
        for (var i = 0; i < samples.Length; i++)
            BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(i * 2, 2), samples[i]);
        return bytes;
    }

    private static void AssertFailedState(PcmVadWindowAdapter adapter)
    {
        AssertFailure(VoiceActivityFailureCode.InvalidState, () => adapter.Append(Pcm(1)));
        AssertFailure(VoiceActivityFailureCode.InvalidState, adapter.Complete);
    }

    private static void AssertCleared(PcmVadWindowAdapter adapter)
    {
        Assert.All(Field<float[]>(adapter, "window"), value => Assert.Equal(0f, value));
        Assert.Equal(0, Field<int>(adapter, "bufferedSamples"));
        Assert.Equal((byte)0, Field<byte>(adapter, "pendingByte"));
        Assert.False(Field<bool>(adapter, "hasPendingByte"));
        Assert.Null(Field<VadWindowHandler?>(adapter, "consume"));
    }

    private static T Field<T>(PcmVadWindowAdapter adapter, string name) =>
        (T)typeof(PcmVadWindowAdapter).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(adapter)!;

    private static void AssertFailure(VoiceActivityFailureCode code, Action action) =>
        Assert.Equal(code, Assert.Throws<VoiceActivityException>(action).Failure.Code);

    private sealed record ObservedWindow(int Offset, int Valid, float[] Samples);
}
