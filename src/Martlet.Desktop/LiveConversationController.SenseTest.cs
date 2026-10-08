using Martlet.Conversation;
using Martlet.Core.Settings;

namespace Martlet.Desktop;

// Test vision for an image model on a paired computer (MainWindow.ModelAbilities.cs) whose route sends it nothing now, because Martlet
// found that it doesn't see: the lanes run only a model that takes its kind, so the test goes straight to that model with the same
// runner. A test can then say again that it sees (docs/SENSE_MODELS.md).
internal sealed partial class LiveConversationController
{
    /// <summary>Runs <paramref name="job"/> once on <paramref name="model"/> with the image and audio models' runner, outside the
    /// lanes: for a test of a model that no route sends <paramref name="kind"/> to now, so no other job runs on it. Like a job in a
    /// lane, it waits while a reply that shares the model's computer and graphics card makes its voice. A refused picture or
    /// recording is remembered, as in use.</summary>
    internal async Task<SenseAnswer> TestSenseAsync(SenseKind kind, DeepThinkingSettings model, SenseJob job, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(job);
        job.Validate(kind);
        var place = SensePlace(kind, model);
        while (ReplyMakingItsVoice() && floorRules.Resources.Shares(place))
            await Task.Delay(SenseLanes.HoldPoll, clock, token).ConfigureAwait(false);
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(token);
        limit.CancelAfter(job.Timeout);
        try { return await RunSenseJobOrTestAsync(kind, model, job, limit.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { return SenseAnswer.Failed("it didn't answer in time"); }
    }
}
