namespace Martlet.VoiceActivity.Tests;

public sealed class SpeechEndpointerTests
{
    [Fact]
    public void DefaultsUseExactOnOffEqualitiesAndRealSampleSilenceLookahead()
    {
        var endpointer = new SpeechEndpointer(new());
        AppendConstant(endpointer, 4096, 0.50f);
        endpointer.Append(new(4096, 512, 0.35f));
        for (var i = 0; i < 19; i++) endpointer.Append(new(4608 + i * 512, 512, 0.349999f));

        Assert.Equal(new SpeechSegment(0, 4608, 0, 4608, 14336, SpeechEndpointReason.Silence),
            Assert.Single(endpointer.Complete()));
    }

    [Theory]
    [InlineData(0.499999f, false)]
    [InlineData(0.50f, true)]
    [InlineData(0.500001f, true)]
    [InlineData(1f, true)]
    public void SpeechOnIsInclusive(float score, bool expectedSpeech)
    {
        var result = Run(QuickOptions(), score);
        if (expectedSpeech)
            Assert.Equal(new SpeechSegment(0, 512, 0, 512, 512, SpeechEndpointReason.EndOfInput), Assert.Single(result));
        else
            Assert.Empty(result);
    }

    [Theory]
    [InlineData(0f, true)]
    [InlineData(0.349999f, true)]
    [InlineData(0.35f, false)]
    [InlineData(0.350001f, false)]
    public void SpeechOffIsStrictlyBelowThreshold(float score, bool expectedSilence)
    {
        var segment = Assert.Single(Run(QuickOptions(), 0.5f, score));
        Assert.Equal(expectedSilence
            ? new SpeechSegment(0, 512, 0, 512, 1024, SpeechEndpointReason.Silence)
            : new SpeechSegment(0, 1024, 0, 1024, 1024, SpeechEndpointReason.EndOfInput), segment);
    }

    [Theory]
    [InlineData(0f)]
    [InlineData(0.35f)]
    [InlineData(0.499999f)]
    public void AnyBelowOnScoreResetsUnconfirmedConsecutiveSpeech(float belowOn)
    {
        var options = QuickOptions() with { MinimumSpeechSamples = 1024 };
        Assert.Empty(Run(options, 0.5f, belowOn, 0.5f));
        Assert.Equal(new SpeechSegment(1024, 2048, 1024, 2048, 2048, SpeechEndpointReason.EndOfInput),
            Assert.Single(Run(options, 0.5f, belowOn, 0.5f, 0.5f)));
    }

    [Fact]
    public void HoldBandCannotStartSpeechButKeepsConfirmedSpeechActive()
    {
        Assert.Empty(Run(QuickOptions(), 0.35f, 0.4f, 0.499999f));
        Assert.Equal(new SpeechSegment(0, 2048, 0, 2048, 2048, SpeechEndpointReason.EndOfInput),
            Assert.Single(Run(QuickOptions(), 0.5f, 0.35f, 0.4f, 0.499999f)));
    }

    [Theory]
    [InlineData(0.35f)]
    [InlineData(0.4f)]
    [InlineData(0.5f)]
    [InlineData(1f)]
    public void AtOrAboveOffResetsPendingSilenceAndItsOriginalStart(float reset)
    {
        var options = QuickOptions() with { EndSilenceSamples = 1024 };
        Assert.Equal(new SpeechSegment(0, 1536, 0, 1536, 2560, SpeechEndpointReason.Silence),
            Assert.Single(Run(options, 0.5f, 0f, reset, 0f, 0f)));
    }

    [Fact]
    public void NonWindowAlignedDurationsDecideAtObservedEndAndRetainFirstOnAndLowStarts()
    {
        var options = QuickOptions() with { MinimumSpeechSamples = 513, EndSilenceSamples = 513 };
        Assert.Equal(new SpeechSegment(0, 1024, 0, 1024, 2048, SpeechEndpointReason.Silence),
            Assert.Single(Run(options, 0.5f, 0.5f, 0f, 0f)));
    }

    [Theory]
    [InlineData(0, 8000, 0)]
    [InlineData(1, 0, 512)]
    [InlineData(1, 8000, 0)]
    [InlineData(20, 8000, 2240)]
    [InlineData(5, 123, 2437)]
    public void PreRollClampsExactlyAtZeroWithoutRoundingToWindows(int onsetWindow, int preRoll, int expectedStart)
    {
        var options = QuickOptions() with { PreRollSamples = preRoll };
        var endpointer = new SpeechEndpointer(options);
        for (var i = 0; i < onsetWindow; i++) endpointer.Append(new(i * 512, 512, 0f));
        endpointer.Append(new(onsetWindow * 512, 512, 0.5f));
        endpointer.Append(new((onsetWindow + 1) * 512, 512, 0f));

        Assert.Equal(new SpeechSegment(onsetWindow * 512, (onsetWindow + 1) * 512,
            expectedStart, (onsetWindow + 1) * 512, (onsetWindow + 2) * 512, SpeechEndpointReason.Silence),
            Assert.Single(endpointer.Complete()));
    }

    [Fact]
    public void LaterPreRollStopsAtPreviousReturnedRangeEndNotItsLookaheadDecision()
    {
        var result = Run(QuickOptions() with { PreRollSamples = 8000 },
            0f, 0f, 0f, 0f, 0.5f, 0f, 0f, 0.5f, 0f);

        Assert.Equal(new[]
        {
            new SpeechSegment(2048, 2560, 0, 2560, 3072, SpeechEndpointReason.Silence),
            new SpeechSegment(3584, 4096, 2560, 4096, 4608, SpeechEndpointReason.Silence)
        }, result);
        Assert.Equal(result[0].RangeEndSampleExclusive, result[1].RangeStartSample);
        Assert.All(result, AssertHalfOpen);
    }

    [Theory]
    [InlineData(4095, false)]
    [InlineData(4096, true)]
    [InlineData(4097, true)]
    public void EofRequiresDefaultMinimumRealSpeechAndClosesAtExactInputEnd(int samples, bool confirmed)
    {
        var endpointer = new SpeechEndpointer(new());
        AppendConstant(endpointer, samples, 0.5f);
        var result = endpointer.Complete();
        if (confirmed)
            Assert.Equal(new SpeechSegment(0, samples, 0, samples, samples, SpeechEndpointReason.EndOfInput),
                Assert.Single(result));
        else
            Assert.Empty(result);
    }

    [Theory]
    [InlineData(1, 513, true)]
    [InlineData(1, 514, false)]
    [InlineData(511, 1023, true)]
    [InlineData(511, 1024, false)]
    public void FinalPartialSpeechCountsOnlyValidSourceSamples(int validTail, int minimumSpeech, bool confirmed)
    {
        var endpointer = new SpeechEndpointer(QuickOptions() with { MinimumSpeechSamples = minimumSpeech });
        endpointer.Append(new(0, 512, 0.5f));
        endpointer.Append(new(512, validTail, 0.5f));
        var result = endpointer.Complete();
        if (confirmed)
            Assert.Equal(512 + validTail, Assert.Single(result).SpeechEndSampleExclusive);
        else
            Assert.Empty(result);
    }

    [Fact]
    public void EofIncludesFullAndShortTrailingSilenceThatHasNotQualifiedAnEndpoint()
    {
        var endpointer = new SpeechEndpointer(QuickOptions() with { EndSilenceSamples = 1024 });
        endpointer.Append(new(0, 512, 0.5f));
        endpointer.Append(new(512, 512, 0f));
        endpointer.Append(new(1024, 7, 0f));

        Assert.Equal(new SpeechSegment(0, 1031, 0, 1031, 1031, SpeechEndpointReason.EndOfInput),
            Assert.Single(endpointer.Complete()));
    }

    [Theory]
    [InlineData(513, true)]
    [InlineData(514, false)]
    public void FinalPartialSilenceCanQualifyOnlyUsingItsRealSamples(int endSilenceSamples, bool silenceEndpoint)
    {
        var endpointer = new SpeechEndpointer(QuickOptions() with { EndSilenceSamples = endSilenceSamples });
        endpointer.Append(new(0, 512, 0.5f));
        endpointer.Append(new(512, 512, 0f));
        endpointer.Append(new(1024, 1, 0f));

        Assert.Equal(silenceEndpoint
            ? new SpeechSegment(0, 512, 0, 512, 1025, SpeechEndpointReason.Silence)
            : new SpeechSegment(0, 1025, 0, 1025, 1025, SpeechEndpointReason.EndOfInput),
            Assert.Single(endpointer.Complete()));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(511)]
    [InlineData(512)]
    [InlineData(513)]
    [InlineData(480000)]
    public void ExactConfiguredMaximumHasMaximumInputReasonAndBoundedScoreCount(int maximumSamples)
    {
        var options = QuickOptions() with { MaximumInputSamples = maximumSamples, MinimumSpeechSamples = 1 };
        var endpointer = new SpeechEndpointer(options);
        var count = AppendConstant(endpointer, maximumSamples, 0.5f);

        Assert.Equal(new SpeechSegment(0, maximumSamples, 0, maximumSamples, maximumSamples, SpeechEndpointReason.MaximumInput),
            Assert.Single(endpointer.Complete()));
        Assert.Equal((maximumSamples + 511) / 512, count);
        Assert.InRange(count, 1, VoiceActivityOptions.HardMaximumScores);
    }

    [Fact]
    public void ReachingMaximumOnASilenceDecisionDoesNotRelabelTheEarlierSpeechEnd()
    {
        Assert.Equal(new SpeechSegment(0, 512, 0, 512, 1024, SpeechEndpointReason.Silence),
            Assert.Single(Run(QuickOptions() with { MaximumInputSamples = 1024 }, 0.5f, 0f)));
    }

    [Fact]
    public void ValidMaximumLengthSilenceIsAnEmptyResultNotAnError()
    {
        var endpointer = new SpeechEndpointer(new());
        Assert.Equal(938, AppendConstant(endpointer, 480000, 0f));
        Assert.Empty(endpointer.Complete());
    }

    [Theory]
    [InlineData(1000, 512, 512)]
    [InlineData(513, 512, 2)]
    [InlineData(512, 512, 1)]
    [InlineData(480000, 479744, 512)]
    public void OverflowRejectsWholeScoreInsteadOfClampingToMaximum(int maximum, int accepted, int overflowCount)
    {
        var endpointer = new SpeechEndpointer(QuickOptions() with { MaximumInputSamples = maximum });
        AppendConstant(endpointer, accepted, 0.5f);

        AssertFailure(VoiceActivityFailureCode.PayloadTooLarge,
            () => endpointer.Append(new(accepted, overflowCount, 0.5f)));

        AssertFailedState(endpointer);
    }

    [Fact]
    public void Maximum938thPartialScoreCannotBeFollowedByA939thScore()
    {
        var endpointer = new SpeechEndpointer(new());
        Assert.Equal(938, AppendConstant(endpointer, 480000, 0f));
        AssertFailure(VoiceActivityFailureCode.StreamTruncated, () => endpointer.Append(new(480000, 1, 0f)));
        AssertFailedState(endpointer);
    }

    [Theory]
    [InlineData(float.NaN)]
    [InlineData(float.PositiveInfinity)]
    [InlineData(float.NegativeInfinity)]
    [InlineData(-0.0001f)]
    [InlineData(1.0001f)]
    public void InvalidScoreIsAnExplicitLatchedFailureNotSuccessfulSilence(float score)
    {
        var endpointer = new SpeechEndpointer(new());
        AssertFailure(VoiceActivityFailureCode.InvalidScore, () => endpointer.Append(new(0, 512, score)));
        AssertFailedState(endpointer);
    }

    [Theory]
    [InlineData(int.MinValue)]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(513)]
    [InlineData(int.MaxValue)]
    public void InvalidWindowSampleCountsFailBeforeAnyResult(int validSamples)
    {
        var endpointer = new SpeechEndpointer(new());
        AssertFailure(VoiceActivityFailureCode.InvalidInput, () => endpointer.Append(new(0, validSamples, 0f)));
        AssertFailedState(endpointer);
    }

    [Theory]
    [InlineData(int.MinValue)]
    [InlineData(-1)]
    public void NegativeSourceOffsetsAreInvalidInput(int offset)
    {
        var endpointer = new SpeechEndpointer(new());
        AssertFailure(VoiceActivityFailureCode.InvalidInput, () => endpointer.Append(new(offset, 512, 0f)));
        AssertFailedState(endpointer);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(511)]
    [InlineData(512)]
    [InlineData(int.MaxValue)]
    public void SequenceMustStartAtZero(int offset)
    {
        var endpointer = new SpeechEndpointer(new());
        AssertFailure(VoiceActivityFailureCode.StreamTruncated, () => endpointer.Append(new(offset, 512, 0f)));
        AssertFailedState(endpointer);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(511)]
    [InlineData(513)]
    [InlineData(1024)]
    public void DuplicatedOverlappingUnalignedOrSkippedWindowsFail(int secondOffset)
    {
        var endpointer = new SpeechEndpointer(new());
        endpointer.Append(new(0, 512, 0f));
        AssertFailure(VoiceActivityFailureCode.StreamTruncated, () => endpointer.Append(new(secondOffset, 512, 0f)));
        AssertFailedState(endpointer);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(511)]
    public void EvenAContiguousScoreAfterAPartialWindowIsTruncated(int firstValid)
    {
        var endpointer = new SpeechEndpointer(new());
        endpointer.Append(new(0, firstValid, 0f));
        AssertFailure(VoiceActivityFailureCode.StreamTruncated, () => endpointer.Append(new(firstValid, 512, 0f)));
        AssertFailedState(endpointer);
    }

    [Fact]
    public void EmptySequenceCannotCompleteAsNoActivity()
    {
        var endpointer = new SpeechEndpointer(new());
        AssertFailure(VoiceActivityFailureCode.InvalidInput, () => endpointer.Complete());
        AssertFailedState(endpointer);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ReturnedSegmentsAreAnImmutableCopyUnaffectedByLaterStateFailures(bool appendFirst)
    {
        var endpointer = new SpeechEndpointer(QuickOptions());
        endpointer.Append(new(0, 512, 0.5f));
        var result = endpointer.Complete();
        var expected = new SpeechSegment(0, 512, 0, 512, 512, SpeechEndpointReason.EndOfInput);
        var list = Assert.IsAssignableFrom<IList<SpeechSegment>>(result);
        Assert.True(list.IsReadOnly);
        Assert.Throws<NotSupportedException>(() => list[0] = default);
        Assert.Throws<NotSupportedException>(() => list.Add(default));
        Assert.Throws<NotSupportedException>(list.Clear);
        if (appendFirst)
            AssertFailure(VoiceActivityFailureCode.InvalidState, () => endpointer.Append(new(512, 512, 0f)));
        else
            AssertFailure(VoiceActivityFailureCode.InvalidState, () => endpointer.Complete());
        AssertFailedState(endpointer);
        Assert.Equal(expected, Assert.Single(result));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SegmentOverflowFailsRatherThanReturningTruncatedResults(bool closeAtEof)
    {
        var endpointer = new SpeechEndpointer(QuickOptions() with { MaximumSegments = 1 });
        endpointer.Append(new(0, 512, 0.5f));
        endpointer.Append(new(512, 512, 0f));
        endpointer.Append(new(1024, 512, 0.5f));
        if (closeAtEof)
            AssertFailure(VoiceActivityFailureCode.PayloadTooLarge, () => endpointer.Complete());
        else
            AssertFailure(VoiceActivityFailureCode.PayloadTooLarge, () => endpointer.Append(new(1536, 512, 0f)));
        AssertFailedState(endpointer);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(128)]
    public void ExactlyConfiguredSegmentCapacityIsAcceptedWithNonoverlappingRanges(int maximumSegments)
    {
        var endpointer = new SpeechEndpointer(QuickOptions() with { MaximumSegments = maximumSegments, PreRollSamples = 8000 });
        for (var i = 0; i < maximumSegments; i++)
        {
            endpointer.Append(new(i * 1024, 512, 0.5f));
            endpointer.Append(new(i * 1024 + 512, 512, 0f));
        }
        var result = endpointer.Complete();
        Assert.Equal(maximumSegments, result.Count);
        Assert.All(result, AssertHalfOpen);
        for (var i = 1; i < result.Count; i++)
            Assert.True(result[i].RangeStartSample >= result[i - 1].RangeEndSampleExclusive);
    }

    [Fact]
    public void HardSegmentLimitIsEnforcedAtThe129thEndpoint()
    {
        var endpointer = new SpeechEndpointer(QuickOptions());
        for (var i = 0; i < 128; i++)
        {
            endpointer.Append(new(i * 1024, 512, 0.5f));
            endpointer.Append(new(i * 1024 + 512, 512, 0f));
        }
        endpointer.Append(new(131072, 512, 0.5f));
        AssertFailure(VoiceActivityFailureCode.PayloadTooLarge, () => endpointer.Append(new(131584, 512, 0f)));
        AssertFailedState(endpointer);
    }

    [Fact]
    public void AlteredOptionsControlThresholdsDurationsAndExactPreRoll()
    {
        var options = new VoiceActivityOptions
        {
            MaximumInputSamples = 5000,
            SpeechOnThreshold = 0.8f,
            SpeechOffThreshold = 0.2f,
            MinimumSpeechSamples = 768,
            EndSilenceSamples = 600,
            PreRollSamples = 123
        };
        Assert.Equal(new SpeechSegment(512, 3072, 389, 3072, 4096, SpeechEndpointReason.Silence),
            Assert.Single(Run(options, 0.79f, 0.8f, 0.8f, 0.2f, 0.19f, 0.2f, 0.19f, 0.19f)));
    }

    [Theory]
    [InlineData(511, 512, false)]
    [InlineData(513, 600, false)]
    [InlineData(513, 513, true)]
    public void ActualWindowAdapterPaddingDoesNotInflateExternallySuppliedSpeechEstimates(
        int sourceSamples, int minimumSpeech, bool confirmed)
    {
        var endpointer = new SpeechEndpointer(QuickOptions() with { MinimumSpeechSamples = minimumSpeech });
        using var adapter = new PcmVadWindowAdapter(480000,
            (offset, valid, _) => endpointer.Append(new(offset, valid, 0.5f)));
        adapter.Append(new byte[sourceSamples * 2]);
        adapter.Complete();
        var result = endpointer.Complete();

        if (confirmed)
            Assert.Equal(sourceSamples, Assert.Single(result).SpeechEndSampleExclusive);
        else
            Assert.Empty(result);
    }

    [Theory]
    [InlineData("maximum-zero")]
    [InlineData("maximum-large")]
    [InlineData("segments-zero")]
    [InlineData("segments-large")]
    [InlineData("on-nan")]
    [InlineData("on-infinite")]
    [InlineData("on-large")]
    [InlineData("off-nan")]
    [InlineData("off-negative")]
    [InlineData("off-equals-on")]
    [InlineData("off-above-on")]
    [InlineData("minimum-zero")]
    [InlineData("minimum-large")]
    [InlineData("silence-zero")]
    [InlineData("silence-large")]
    [InlineData("preroll-negative")]
    [InlineData("preroll-large")]
    [InlineData("budget-zero")]
    [InlineData("budget-large")]
    [InlineData("observation-zero")]
    [InlineData("observation-large")]
    public void ConstructorValidatesSuppliedOptions(string invalid)
    {
        var options = invalid switch
        {
            "maximum-zero" => new VoiceActivityOptions { MaximumInputSamples = 0 },
            "maximum-large" => new VoiceActivityOptions { MaximumInputSamples = 480001 },
            "segments-zero" => new VoiceActivityOptions { MaximumSegments = 0 },
            "segments-large" => new VoiceActivityOptions { MaximumSegments = 129 },
            "on-nan" => new VoiceActivityOptions { SpeechOnThreshold = float.NaN },
            "on-infinite" => new VoiceActivityOptions { SpeechOnThreshold = float.PositiveInfinity },
            "on-large" => new VoiceActivityOptions { SpeechOnThreshold = 1.01f },
            "off-nan" => new VoiceActivityOptions { SpeechOffThreshold = float.NaN },
            "off-negative" => new VoiceActivityOptions { SpeechOffThreshold = -0.01f },
            "off-equals-on" => new VoiceActivityOptions { SpeechOffThreshold = 0.5f },
            "off-above-on" => new VoiceActivityOptions { SpeechOffThreshold = 0.6f },
            "minimum-zero" => new VoiceActivityOptions { MinimumSpeechSamples = 0 },
            "minimum-large" => new VoiceActivityOptions { MinimumSpeechSamples = 16001 },
            "silence-zero" => new VoiceActivityOptions { EndSilenceSamples = 0 },
            "silence-large" => new VoiceActivityOptions { EndSilenceSamples = 32001 },
            "preroll-negative" => new VoiceActivityOptions { PreRollSamples = -1 },
            "preroll-large" => new VoiceActivityOptions { PreRollSamples = 8001 },
            "budget-zero" => new VoiceActivityOptions { ProcessingBudget = TimeSpan.Zero },
            "budget-large" => new VoiceActivityOptions { ProcessingBudget = TimeSpan.FromSeconds(11) },
            "observation-zero" => new VoiceActivityOptions { ObservationWait = TimeSpan.Zero },
            "observation-large" => new VoiceActivityOptions { ObservationWait = TimeSpan.FromSeconds(3) },
            _ => throw new ArgumentOutOfRangeException(nameof(invalid))
        };
        AssertFailure(VoiceActivityFailureCode.InvalidInput, () => new SpeechEndpointer(options));
    }

    [Fact]
    public void NullOptionsAreRejected() =>
        AssertFailure(VoiceActivityFailureCode.InvalidInput, () => new SpeechEndpointer(null!));

    private static VoiceActivityOptions QuickOptions() => new()
    {
        MinimumSpeechSamples = 512,
        EndSilenceSamples = 512,
        PreRollSamples = 0
    };

    private static IReadOnlyList<SpeechSegment> Run(VoiceActivityOptions options, params float[] scores)
    {
        var endpointer = new SpeechEndpointer(options);
        for (var i = 0; i < scores.Length; i++) endpointer.Append(new(i * 512, 512, scores[i]));
        return endpointer.Complete();
    }

    private static int AppendConstant(SpeechEndpointer endpointer, int totalSamples, float score)
    {
        var count = 0;
        for (var offset = 0; offset < totalSamples; offset += 512)
        {
            endpointer.Append(new(offset, Math.Min(512, totalSamples - offset), score));
            count++;
        }
        return count;
    }

    private static void AssertHalfOpen(SpeechSegment segment)
    {
        Assert.InRange(segment.RangeStartSample, 0, segment.SpeechStartSample);
        Assert.True(segment.SpeechStartSample < segment.SpeechEndSampleExclusive);
        Assert.Equal(segment.SpeechEndSampleExclusive, segment.RangeEndSampleExclusive);
        Assert.True(segment.RangeEndSampleExclusive <= segment.DecisionSampleOffset);
    }

    private static void AssertFailedState(SpeechEndpointer endpointer)
    {
        AssertFailure(VoiceActivityFailureCode.InvalidState, () => endpointer.Append(new(0, 512, 0f)));
        AssertFailure(VoiceActivityFailureCode.InvalidState, () => endpointer.Complete());
    }

    private static void AssertFailure(VoiceActivityFailureCode code, Action action) =>
        Assert.Equal(code, Assert.Throws<VoiceActivityException>(action).Failure.Code);
}
