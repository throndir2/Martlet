namespace Martlet.VoiceActivity;

/// <summary>
/// Applies sample-based hysteresis to externally supplied activity estimates, not calibrated confidence
/// or native evidence. Windows must start at zero and be contiguous, with at most one final short window.
/// All output ranges are half-open source-sample ranges; tensor padding never contributes duration.
/// </summary>
public sealed class SpeechEndpointer
{
    private readonly VoiceActivityOptions options;
    private readonly List<SpeechSegment> segments;
    private int nextSampleOffset, scoreCount;
    private int candidateStart = -1, candidateSamples;
    private int speechStart = -1, silenceStart = -1;
    private bool active, receivedPartialWindow, completed, failed;

    public SpeechEndpointer(VoiceActivityOptions options)
    {
        VadCheck.Require(options is not null);
        options!.Validate();
        this.options = options;
        segments = new(options.MaximumSegments);
    }

    public void Append(VoiceActivityWindowScore score)
    {
        try
        {
            EnsureOpen();
            VadCheck.Require(score.SampleOffset >= 0 &&
                score.ValidSamples is >= 1 and <= VoiceActivityOptions.WindowSamples);
            VadCheck.Require(!receivedPartialWindow && score.SampleOffset == nextSampleOffset,
                VoiceActivityFailureCode.StreamTruncated);
            VadCheck.Require(scoreCount < VoiceActivityOptions.HardMaximumScores &&
                score.ValidSamples <= options.MaximumInputSamples - nextSampleOffset,
                VoiceActivityFailureCode.PayloadTooLarge);
            score.Validate();

            nextSampleOffset += score.ValidSamples;
            scoreCount++;
            receivedPartialWindow = score.ValidSamples < VoiceActivityOptions.WindowSamples;
            if (!active)
            {
                if (score.ActivityScore >= options.SpeechOnThreshold)
                {
                    if (candidateStart < 0) candidateStart = score.SampleOffset;
                    candidateSamples += score.ValidSamples;
                    if (candidateSamples >= options.MinimumSpeechSamples)
                    {
                        active = true;
                        speechStart = candidateStart;
                        candidateStart = -1;
                        candidateSamples = 0;
                    }
                }
                else
                {
                    candidateStart = -1;
                    candidateSamples = 0;
                }
                return;
            }

            if (score.ActivityScore >= options.SpeechOffThreshold)
            {
                silenceStart = -1;
                return;
            }

            if (silenceStart < 0) silenceStart = score.SampleOffset;
            if (nextSampleOffset - silenceStart >= options.EndSilenceSamples)
                CloseSegment(silenceStart, SpeechEndpointReason.Silence);
        }
        catch
        {
            failed = true;
            segments.Clear();
            throw;
        }
    }

    /// <summary>Completes once and returns a read-only copy, never a partial result after an error.</summary>
    public IReadOnlyList<SpeechSegment> Complete()
    {
        try
        {
            EnsureOpen();
            VadCheck.Require(scoreCount > 0);
            if (active)
                CloseSegment(nextSampleOffset, nextSampleOffset == options.MaximumInputSamples
                    ? SpeechEndpointReason.MaximumInput : SpeechEndpointReason.EndOfInput);
            var result = Array.AsReadOnly(segments.ToArray());
            completed = true;
            return result;
        }
        catch
        {
            failed = true;
            segments.Clear();
            throw;
        }
    }

    private void CloseSegment(int speechEnd, SpeechEndpointReason reason)
    {
        VadCheck.Require(segments.Count < options.MaximumSegments, VoiceActivityFailureCode.PayloadTooLarge);
        var previousRangeEnd = segments.Count == 0 ? 0 : segments[^1].RangeEndSampleExclusive;
        var rangeStart = Math.Max(previousRangeEnd, Math.Max(0, speechStart - options.PreRollSamples));
        segments.Add(new(speechStart, speechEnd, rangeStart, speechEnd, nextSampleOffset, reason));
        active = false;
        speechStart = -1;
        silenceStart = -1;
    }

    private void EnsureOpen() =>
        VadCheck.Require(!completed && !failed, VoiceActivityFailureCode.InvalidState);
}
