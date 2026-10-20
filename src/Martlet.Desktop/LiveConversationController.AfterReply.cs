using System.IO;
using Martlet.Conversation;
using Martlet.Core.Contracts;
using Martlet.Core.Creations;
using Martlet.Core.Lorebooks;
using Martlet.Core.Settings;
using Martlet.Memory;
using Martlet.Participation;
using Martlet.Providers;

namespace Martlet.Desktop;

internal sealed partial class LiveConversationController
{
    /// <summary>What comes after a reply (docs/CONVERSATION.md): learning the names of the voices heard and remembering, each in a
    /// Thinking request of its own, one after the other and off the reply's path (<see cref="EnqueueAfterReplyLocked"/>).</summary>
    private sealed class AfterReplyWork(LiveConversationController owner)
    {
        /// <summary>Queues what comes after a reply: learning the names of the <paramref name="heard"/> voices and remembering
        /// (<paramref name="remember"/>), each in a Thinking request of its own, one after the other (names first, so the facts
        /// remembered next already say the names learned). On a Thinking model on this PC each continues <paramref name="sent"/>,
        /// the reply's own request, so the model's prompt cache keeps the conversation for the next reply. New facts belong to the
        /// speaker among the <paramref name="present"/> voices (unless the model names another).</summary>
        internal void EnqueueAfterReplyLocked(LiveConversationConfiguration configured, bool remember, HeardVoices? heard,
            IReadOnlyList<TextHistoryMessage> earlier, string user, string reply, BoundedTextInput? sent, HeardVoices? present = null)
        {
            remember &= owner.memory is not null && configured.Memory is { Enabled: true };
            if (owner.voices is null) heard = null;
            if (!remember) present = null;
            if (!remember && heard is null || !owner.AutoCapture || owner.disposed || owner.captureQuarantined || owner.capturesPending >= MaximumPendingCaptures)
                return;
            owner.capturesPending++;
            // Earlier lines heard from what the PC played are left out: memory and learning names only read the user.
            var job = new AfterReplyJob(configured, remember, heard,
                LiveConversationConfiguration.WithoutMarked(earlier.LastOrDefault(message => message.Role == TextHistoryRole.User)?.Text),
                earlier.LastOrDefault(message => message.Role == TextHistoryRole.Assistant)?.Text,
                user, reply, configured.LocalThinking ? sent : null, owner.captureCancel.Token, present, owner.Activity());
            owner.captureTail = AfterReplyAsync(owner.captureTail, job);
        }

        /// <param name="Activity">What the user did during the exchange: where its new facts matter.</param>
        internal sealed record AfterReplyJob(LiveConversationConfiguration Configuration, bool Remember, HeardVoices? Heard, string? EarlierUser,
            string? EarlierReply, string User, string Reply, BoundedTextInput? Conversation, CancellationToken Token, HeardVoices? Present = null,
            string? Activity = null)
        {
            public override string ToString() => nameof(AfterReplyJob);
        }

        internal async Task AfterReplyAsync(Task previous, AfterReplyJob job)
        {
            await previous.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            MemoryCaptureReport? report = null;
            IReadOnlyList<Martlet.Core.Speakers.VoiceUpdateResult>? learned = null;
            try
            {
                if (job.Heard is not null)
                {
                    try { learned = await Task.Run(() => LearnNamesAsync(job)).ConfigureAwait(false); }
                    catch (Exception error) { ErrorLog.Warn($"{VoiceNaming.Purpose} from a conversation exchange failed.", error); }
                    if (learned is { Count: > 0 }) owner.VoicesNamed?.Invoke(learned);
                }
                if (job.Remember)
                {
                    try { report = await Task.Run(() => RememberAsync(job)).ConfigureAwait(false); }
                    catch (Exception error)
                    {
                        ErrorLog.Warn("Remembering from a conversation exchange failed.", error);
                        report = new(Failure: "memory." + error.GetType().Name);
                    }
                }
            }
            finally
            {
                lock (owner.gate) owner.capturesPending--;
            }
            if (!job.Remember) return;
            if (report is { Failure: { } failure })
            {
                ErrorLog.Warn($"Remembering failed ({failure}).");
                // Say why once; the same problem again on later exchanges stays in the log until remembering works again.
                lock (owner.gate)
                {
                    if (failure == owner.lastCaptureFailure) return;
                    owner.lastCaptureFailure = failure;
                }
            }
            else if (!job.Token.IsCancellationRequested)
                lock (owner.gate) owner.lastCaptureFailure = null;
            if (report is not null) owner.MemoryCaptured?.Invoke(report);
        }

        /// <summary>Learning names: one Thinking request whose only job is who the people speaking are, from the voices Martlet
        /// recognized (and, when it wasn't sure whose voice said the message, the one it sounded most like) and from what they
        /// said. It goes to a Thinking pool member that takes naming jobs, at the helpers' priority and under the live floor, else to
        /// the conversation's own Thinking model once the conversation is quiet. Only the checked lines change the voice list; a
        /// SPOKE line only labels the message (no name, voiceprint or sample changes).</summary>
        internal async Task<IReadOnlyList<Martlet.Core.Speakers.VoiceUpdateResult>?> LearnNamesAsync(AfterReplyJob job)
        {
            var token = job.Token;
            if (owner.voices is not { } known || job.Heard is not { } heard) return null;
            try
            {
                token.ThrowIfCancellationRequested();
                var naming = VoiceNaming.Context(heard, known.Roster, job.User, job.Configuration.CompanionNames);
                // The people tools only when Martlet wasn't sure whose voice said the message (only a SPOKE line can use what they
                // show) and a pool member that takes naming jobs calls tools. The conversation's own model never gets them.
                var tools = naming.Unattributed && owner.helpers.PoolCallsTools(HelperJobKind.SpeakerNaming) ? PeopleToolsFor(naming) : null;
                var prompt = AfterReply.Prompt(null, naming, job.EarlierUser, job.EarlierReply, job.User, job.Reply, job.Configuration.Prompts,
                    job.Conversation, job.Configuration.FitsContext, companion: job.Configuration.CharacterName);
                // A Thinking pool member reads the short excerpt (it has no copy of this conversation in its cache); the conversation's
                // own model continues the reply's request, after the reply finished speaking.
                var (answer, failure, _) = await owner.helpers.RunAsync(HelperJobKind.SpeakerNaming, VoiceNaming.Purpose, HelperCapability.Text,
                    () => VoiceNaming.Prompt(naming, job.EarlierUser, job.EarlierReply, job.User, job.Reply, job.Configuration.Prompts,
                        job.Configuration.CharacterName, tools is not null).Input,
                    worker => owner.AskAsync(VoiceNaming.Purpose, job.Configuration, prompt.Input, worker), token, tools).ConfigureAwait(false);
                if (answer is null)
                {
                    if (failure is not null && !token.IsCancellationRequested)
                        ErrorLog.Info($"{VoiceNaming.Purpose} got no answer ({failure}); no names changed.");
                    return null;
                }
                token.ThrowIfCancellationRequested();
                var asked = VoiceNaming.Parse(answer, naming, job.Reply);
                var (applied, refused) = known.Apply(asked.Updates);
                // Why lines were left out, never the names (they are personal).
                if (asked.Refused.Count + refused.Count > 0)
                    ErrorLog.Info($"{VoiceNaming.Purpose}: {applied.Count} change(s) made; left out: " +
                        string.Join(", ", asked.Refused.Concat(refused).Select(r => r.Reason).Distinct()) + ".");
                return applied;
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                return null;
            }
            catch (Exception error) when (error is LiveActionException or ContractException or IOException or UnauthorizedAccessException or
                InvalidOperationException)
            {
                ErrorLog.Info($"{VoiceNaming.Purpose} couldn't run ({(error is LiveActionException live ? live.Code : error.GetType().Name)}); " +
                    "no names changed.");
                return null;
            }
        }

        /// <summary>The people tools (<see cref="PeopleTools"/>) for one learning-names request: they read the voice list as it is
        /// when called, and each voice they show may be named in the answer's SPOKE line.</summary>
        internal HelperTools PeopleToolsFor(VoiceNamingContext naming)
        {
            var host = new CheckInToolHost([PeopleTools.SetId], new(VoiceNaming.ToolCaller, VoiceNaming.Purpose, null, owner.clock.GetUtcNow()),
                new Dictionary<string, CheckInToolHandler>(StringComparer.Ordinal)
                {
                    [PeopleTools.SetId] = (call, context, token) =>
                    {
                        if (owner.voices is not { } live) return new(new ConversationToolResult("People: voice recognition isn't available here.", true));
                        var answer = PeopleTools.Run(call, live.Roster, live.IsYours, owner.clock.GetUtcNow());
                        naming.Saw(answer.Shown);
                        return new(answer.Result);
                    }
                });
            return new(host.Tools, host, VoiceNaming.MaximumToolRounds);
        }

        /// <summary>Remembering: one Thinking request with the related facts already saved and the exchange. New facts belong to
        /// the speaker among the voices recognized in the message, unless the model names another.</summary>
        internal async Task<MemoryCaptureReport?> RememberAsync(AfterReplyJob job)
        {
            const string Purpose = "Remembering";
            var token = job.Token;
            // Read after learning names, so the facts and the talk window use the names learned from this same exchange.
            var roster = owner.voices?.Roster;
            // Whose new facts are: the speaker among the voices recognized in the message, when remembering has them.
            var speaker = job.Present?.Speaker?.Voice;
            try
            {
                token.ThrowIfCancellationRequested();
                // Facts that say related things in other words join the lexical matches (finding by meaning, after the reply).
                var similar = await owner.MeaningFactsAsync(job.User, token).ConfigureAwait(false);
                var recalled = await RetryStoreAsync(() => owner.memory!.KnownFactsAsync(job.Configuration.Memory!, job.User,
                    MemoryCapture.MaximumShownFacts, MemoryPeople.Ids(speaker, roster), token, similar, job.Activity), token).ConfigureAwait(false);
                var (known, knownSpaces) = (recalled.Facts, recalled.Spaces);
                var people = MemoryPeople.Labels(known, roster);
                var prompt = AfterReply.Prompt(known, null, job.EarlierUser, job.EarlierReply, job.User, job.Reply, job.Configuration.Prompts,
                    job.Conversation, job.Configuration.FitsContext, job.Present, people, job.Configuration.CharacterName, job.Activity);
                // A Thinking pool member reads the short excerpt (it has no copy of this conversation in its cache); the conversation's
                // own model continues the reply's request as before, after the reply finished speaking.
                AfterReplyPrompt? pooled = null;
                var (answer, failure, onPool) = await owner.helpers.RunAsync(HelperJobKind.Memory, Purpose, HelperCapability.Text,
                    () => (pooled = AfterReply.Prompt(known, null, job.EarlierUser, job.EarlierReply, job.User, job.Reply,
                        job.Configuration.Prompts, null, null, job.Present, people, job.Configuration.CharacterName, job.Activity)).Input,
                    worker => owner.AskAsync(Purpose, job.Configuration, prompt.Input, worker), token).ConfigureAwait(false);
                if (onPool) prompt = pooled!;
                if (answer is null) return !token.IsCancellationRequested && failure is not null ? new(Failure: failure) : null;
                if (MemoryCapture.Parse(answer, prompt.ShownFacts, prompt.Voices, speaker?.Id, job.Activity) is not { Count: > 0 } operations) return null;
                try
                {
                    var shown = known.Take(prompt.ShownFacts).ToArray();
                    var changes = await RetryStoreAsync(() => owner.memory!.RememberAsync(job.Configuration.Memory!.ConfigurationRevision, shown,
                        operations, id => MemoryPeople.Canonical(id, roster), token, knownSpaces), token).ConfigureAwait(false);
                    // Whose each change is, as the talk window names them.
                    var whose = owner.voices?.Roster ?? roster;
                    return changes.Count == 0 ? null
                        : new(changes.Select(change => change with { Person = MemoryPeople.Label(change.VoiceId, whose) }).ToArray());
                }
                catch (Exception error) when (error is LiveActionException or DesktopMemoryException or MemoryException or
                    ContractException or IOException or UnauthorizedAccessException or InvalidOperationException)
                {
                    return RememberingFailure(error, token);
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                return null;
            }
            catch (Exception error) when (error is LiveActionException or DesktopMemoryException or MemoryException or
                ContractException or IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                return RememberingFailure(error, token);
            }
        }

        // The store is shared with recall and the Memory page, and a fact can change while the model reads it: wait and try again.
        internal static async Task<T> RetryStoreAsync<T>(Func<Task<T>> action, CancellationToken token)
        {
            for (var attempt = 0; ; attempt++)
            {
                try
                {
                    return await action().ConfigureAwait(false);
                }
                catch (Exception error) when (attempt < 2 && error is MemoryException { Failure: MemoryFailure.Busy or MemoryFailure.Conflict } or
                    DesktopMemoryException { Code: "memory.busy" })
                {
                    await Task.Delay(TimeSpan.FromSeconds(1 + attempt), token).ConfigureAwait(false);
                }
            }
        }

        // Turning memory off, changing settings or revoking live work simply drops what was still being remembered.
        internal static MemoryCaptureReport? RememberingFailure(Exception error, CancellationToken token)
        {
            var code = error switch
            {
                LiveActionException live => live.Code,
                DesktopMemoryException app => app.Code,
                MemoryException store => "memory." + store.Failure,
                _ => "memory.unavailable"
            };
            return token.IsCancellationRequested || code is "memory.disabled" or "memory.configuration_changed" or
                "conversation.configuration_changed" or "conversation.revoked" ? null : new(Failure: code);
        }
    }
}
