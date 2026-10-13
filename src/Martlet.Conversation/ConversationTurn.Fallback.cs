using System.Runtime.ExceptionServices;
using Martlet.Core.Contracts;
using Martlet.Core.Streaming;
using Martlet.Providers;

namespace Martlet.Conversation;

// Fallback policy: what a reply does when its Thinking request fails or is refused (the Thinking fallback, the transcript in place of a
// recording) and the race with Backup Thinking for the first words.
public sealed partial class ConversationTurn
{
    // Switches the rest of this reply to the Thinking fallback when there is one and nothing of the failed answer was said.
    private bool FallBack(int textBefore, string reason)
    {
        if (request.Fallback is null) return false;
        lock (Sync)
        {
            if (invalidated || stop.IsCancellationRequested || whole.Expired || text.Length != textBefore) return false;
            textWindow = null;
            fellBackAfter = reason;
            Emit(ConversationEventKind.State);
            return true;
        }
    }

    // The words of a message sent as the user's recording alone, once speech-to-text running beside the reply has them.
    private string? spokenWords;

    // The input without its recording: with the transcript it already carries, or, for a recording sent alone, with its words
    // (waiting, within the reply's own time, for speech-to-text to finish). Null when those words never came.
    private async Task<BoundedTextInput?> WithoutRecordingAsync(BoundedTextInput input)
    {
        if (input.Audio is null) return input;
        if (request.SpokenWords is not { } words) return input.WithoutAudio();
        if (spokenWords is null)
        {
            TraceWaiting("speech-to-text's words for the recording");
            try
            {
                var heard = await words(stop.Token).WaitAsync(whole.Remaining > TimeSpan.Zero ? whole.Remaining : TimeSpan.Zero, Clock,
                    stop.Token).ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(heard)) return null;
                spokenWords = heard.Trim();
            }
            catch (TimeoutException) { return null; }
            finally { TraceWaiting(null); }
            lock (Sync) CheckActive();
        }
        try { return input.WithTranscript(spokenWords); }
        catch (ContractException) { return null; }
    }

    private enum RunStep { None, Words, Ended, Failed }

    // One Thinking stream being read: its checks and its events (with a stop of its own while Backup Thinking races it), when its
    // request started, what the race already read from it, and a backup member's stream and lease.
    private sealed class TextRun(ITextGenerationStream stream, ProviderSequenceValidator validator, CancellationTokenSource? stop,
        CancellationToken token, TimeSpan requestedAfter, ThinkingBackupStream? backup = null) : IAsyncDisposable
    {
        private int disposed;
        internal ITextGenerationStream Stream { get; } = stream;
        internal ProviderSequenceValidator Validator { get; } = validator;
        internal CancellationTokenSource? Stop { get; } = stop;
        internal IAsyncEnumerator<ProviderEvent> Events { get; } = stream.GetAsyncEnumerator(token);
        internal TimeSpan RequestedAfter { get; } = requestedAfter;
        internal ThinkingBackupStream? Backup { get; } = backup;
        internal RunStep Step { get; set; }

        // It answered the race: it has words, or it ended with a whole answer without words (a tool call or a refusal).
        internal bool Answered => Step == RunStep.Words ||
            Step == RunStep.Ended && Validator.Snapshot.Result?.Outcome is TurnOutcome.Completed or TurnOutcome.Refused;

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0) return;
            try { await Events.DisposeAsync().ConfigureAwait(false); }
            finally
            {
                Validator.Dispose();
                Stop?.Dispose();
                if (Backup?.Lease is { } lease) await lease.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    // Backup Thinking's race (IThinkingBackup): the reply's first request reads on alone until its first words. With none Delay
    // after it started, the same request also goes to the backup, and the stream with words first answers. The other is
    // stopped at once and let go off the reply's path, so it never holds up the words. A reply started early asks a paid member
    // only once it is taken. When neither answers (both failed or ended), the reply's own request goes on as if alone: its
    // failure leads to the usual retries and the Thinking fallback.
    private async Task<TextRun> RaceAsync(TextRun primary, IThinkingBackup backup, BoundedTextInput input)
    {
        var delay = backup.Delay;
        var primaryWords = FirstWordsAsync(primary);
        TextRun? second = null;
        Task<RunStep>? secondWords = null;
        CancellationTokenSource? backupStop = null;
        try
        {
            var wait = primary.RequestedAfter + delay - Clock.GetElapsedTime(startedAt);
            if (wait > TimeSpan.Zero)
            {
                using var timer = CancellationTokenSource.CreateLinkedTokenSource(stop.Token);
                await Task.WhenAny(primaryWords, Task.Delay(wait, Clock, timer.Token)).ConfigureAwait(false);
                await timer.CancelAsync().ConfigureAwait(false);
            }
            stop.Token.ThrowIfCancellationRequested();
            if (primaryWords.IsCompleted)
            {
                primary.Step = await primaryWords.ConfigureAwait(false);
                Decided(backup, new(ThinkingBackupOutcome.NotNeeded, delay));
                return primary;
            }
            backupStop = CancellationTokenSource.CreateLinkedTokenSource(stop.Token);
            var ids = NewIds();
            var held = Held;
            var opened = await OpenBackupAsync(backup, input, ids, held, backupStop.Token).ConfigureAwait(false);
            if (opened is null && held && hold is { } taken)
            {
                // Started early: a paid member may answer only once the reply is taken.
                await Task.WhenAny(primaryWords, taken.Task).WaitAsync(stop.Token).ConfigureAwait(false);
                if (!primaryWords.IsCompleted) opened = await OpenBackupAsync(backup, input, ids = NewIds(), false, backupStop.Token).ConfigureAwait(false);
            }
            var askedAfter = Clock.GetElapsedTime(startedAt);
            if (opened is null)
            {
                backupStop.Dispose();
                backupStop = null;
                Decided(backup, new(ThinkingBackupOutcome.NoMember, delay));
                primary.Step = await primaryWords.ConfigureAwait(false);
                return primary;
            }
            try
            {
                second = new(opened.Stream, TextValidator(ids, opened.Stream, input), backupStop, backupStop.Token, askedAfter, opened);
                backupStop = null;
            }
            catch (Exception error) when (error is ContractException or ArgumentException or InvalidOperationException)
            {
                backupStop?.Dispose();
                backupStop = null;
                if (opened.Lease is { } lease) _ = lease.DisposeAsync().AsTask();
                Decided(backup, new(ThinkingBackupOutcome.Failed, delay, opened.Name, askedAfter, Why: "its stream can't be read"));
                primary.Step = await primaryWords.ConfigureAwait(false);
                return primary;
            }
            // The conversation's model had words while the member was chosen: nothing is sent to the member.
            if (primaryWords.IsCompletedSuccessfully)
            {
                primary.Step = primaryWords.Result;
                if (primary.Answered)
                {
                    LetGo(second, null);
                    second = null;
                    Decided(backup, new(ThinkingBackupOutcome.NotNeeded, delay));
                    return primary;
                }
            }
            secondWords = FirstWordsAsync(second);
            TextRun? winner = null;
            Exception? primaryError = null;
            string? backupWhy = null;
            List<Task<RunStep>> pending = [primaryWords, secondWords];
            while (winner is null && pending.Count > 0)
            {
                var done = await Task.WhenAny(pending).ConfigureAwait(false);
                pending.Remove(done);
                var run = done == primaryWords ? primary : second;
                try
                {
                    run.Step = await done.ConfigureAwait(false);
                    if (run.Answered) winner = run;
                    else if (run == second) backupWhy = run.Step == RunStep.Failed ? "it failed" : "it ended without an answer";
                }
                catch (Exception error) when (run == primary) { primaryError = error; }
                catch (Exception error) { backupWhy = error is OperationCanceledException ? "it was stopped" : "it failed"; }
                stop.Token.ThrowIfCancellationRequested();
                // A member that failed lets go of its slot and its computer at once, while the conversation's model goes on.
                if (backupWhy is not null && winner is null) LetGo(second, secondWords);
            }
            if (winner is null)
            {
                // Neither answered: the reply's own request goes on as if alone.
                LetGo(second, secondWords);
                second = null;
                Decided(backup, new(ThinkingBackupOutcome.Failed, delay, opened.Name, askedAfter, Why: backupWhy));
                if (primaryError is not null) ExceptionDispatchInfo.Throw(primaryError);
                return primary;
            }
            var firstWords = Clock.GetElapsedTime(startedAt);
            if (winner == second)
            {
                LetGo(primary, primaryWords);
                lock (Sync)
                {
                    textProvenance = second.Stream.Capabilities.Provenance;
                    openStream = second.Stream;
                    openStreamAfter = askedAfter;
                    TraceStream(second.Stream, askedAfter, backup: true);
                }
                second = null;
                Decided(backup, new(ThinkingBackupOutcome.Won, delay, opened.Name, askedAfter, firstWords));
                return winner;
            }
            LetGo(second, secondWords);
            second = null;
            Decided(backup, new(backupWhy is null ? ThinkingBackupOutcome.Lost : ThinkingBackupOutcome.Failed, delay, opened.Name, askedAfter,
                firstWords, backupWhy));
            return primary;
        }
        catch
        {
            // The turn stopped (or the reply's own request failed with no backup to answer): the backup's stream is let go here,
            // the reply's own by the caller once its read ended.
            backupStop?.Dispose();
            if (second is not null) LetGo(second, secondWords);
            try { primary.Stop?.Cancel(); }
            catch (ObjectDisposedException) { }
            try { await primaryWords.ConfigureAwait(false); }
            catch (Exception) { }
            throw;
        }
    }

    // Reads a stream for the race until it has words to show, ends (a whole answer without words ends it too) or fails.
    private static async Task<RunStep> FirstWordsAsync(TextRun run)
    {
        while (true)
        {
            if (!await run.Events.MoveNextAsync().ConfigureAwait(false)) return RunStep.Ended;
            var snapshot = run.Validator.Accept(run.Events.Current).Snapshot;
            if (snapshot.Result?.Outcome == TurnOutcome.Failed) return RunStep.Failed;
            if (snapshot.QueuedChunks > 0) return RunStep.Words;
        }
    }

    // The backup's stream, or null when no member may take the request or it couldn't be opened.
    private async Task<ThinkingBackupStream?> OpenBackupAsync(IThinkingBackup backup, BoundedTextInput input, CorrelationIds ids,
        bool held, CancellationToken token)
    {
        try { return await backup.OpenAsync(request, input, ids, Epoch, held, token).ConfigureAwait(false); }
        catch (Exception error) when (error is not OperationCanceledException || !token.IsCancellationRequested) { return null; }
    }

    // The stream that lost the race (or failed in it) is stopped at once and let go once its read ended, off the reply's path.
    private static void LetGo(TextRun run, Task<RunStep>? reading)
    {
        try { run.Stop?.Cancel(); }
        catch (ObjectDisposedException) { }
        _ = Task.Run(async () =>
        {
            if (reading is not null)
                try { await reading.ConfigureAwait(false); }
                catch (Exception) { }
            try { await run.DisposeAsync().ConfigureAwait(false); }
            catch (Exception) { }
        });
    }

    private void Decided(IThinkingBackup backup, ThinkingBackupResult result)
    {
        lock (Sync) backupResult = result;
        TraceBackup(result);
        try { backup.Ended(result); }
        catch (Exception error) when (error is not OutOfMemoryException) { }
    }
}
