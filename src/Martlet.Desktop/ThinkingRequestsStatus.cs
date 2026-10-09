using System.Globalization;
using System.IO;
using System.Text.Json;
using Martlet.Conversation;

namespace Martlet.Desktop;

/// <summary>Plain words for the Thinking requests page and its status file: kinds, states, sources and times.</summary>
internal static class ThinkingRequestWords
{
    internal static string Kind(ThinkingJobKind kind) => kind switch
    {
        ThinkingJobKind.BargeInJudge => "Barge-in judge",
        ThinkingJobKind.EndOfTurnJudge => "End-of-turn judge",
        ThinkingJobKind.Digest => "Summary",
        ThinkingJobKind.ThinkLonger => "Think longer",
        ThinkingJobKind.TouchZones => "Touch zones",
        ThinkingJobKind.Memory => "Memory",
        ThinkingJobKind.Naming => "Naming",
        ThinkingJobKind.CheckIn => "Check-in",
        _ => "Research"
    };

    internal static string State(ThinkingRequestInfo request) => request.State switch
    {
        ThinkingRequestState.Waiting when request.HeldForConversation => "Waiting for the conversation",
        ThinkingRequestState.Waiting when request.Position > 0 => $"Waiting ({Ordinal(request.Position)} in line)",
        ThinkingRequestState.Waiting => "Waiting",
        ThinkingRequestState.Running => "Running",
        ThinkingRequestState.Paused => "Paused",
        ThinkingRequestState.Succeeded => "Done",
        ThinkingRequestState.Failed => "Failed",
        ThinkingRequestState.TimedOut => "Timed out",
        ThinkingRequestState.Stale => "Dropped (too late)",
        ThinkingRequestState.NoMember => "No member could take it",
        ThinkingRequestState.Preempted => "Stopped for the conversation",
        _ => "Canceled"
    };

    internal static string Source(ThinkingRequestSource source) => source switch
    {
        ThinkingRequestSource.Pool => "Thinking pool job",
        ThinkingRequestSource.Conversation => "Conversation's background work",
        _ => "Simulated (sends nothing)"
    };

    internal static string Origin(ThinkingRequestInfo request) => request.Origin ?? Martlet.Core.Speakers.CompanionNames.Default;

    internal static string Task(ThinkingRequestInfo request) => request.Start.Task ?? Kind(request.Kind);

    internal static string Needs(ThinkingCapability needs) => ThinkingJobResult.Describe(needs);

    /// <summary>"350 ms", "4.2 s", "1 min 42 s".</summary>
    internal static string Time(TimeSpan span)
    {
        if (span < TimeSpan.Zero) span = TimeSpan.Zero;
        if (span.TotalSeconds < 1) return string.Create(CultureInfo.CurrentCulture, $"{span.TotalMilliseconds:0} ms");
        if (span.TotalSeconds < 60) return string.Create(CultureInfo.CurrentCulture, $"{span.TotalSeconds:0.0} s");
        return BackgroundJobs.Duration(span);
    }

    private static string Ordinal(int n) => n switch { 1 => "next", 2 => "2nd", 3 => "3rd", _ => $"{n}th" };
}

internal sealed partial class LiveConversationController
{
    /// <summary>thinking-requests.json in the data directory: every Thinking request kept, with its timings, and the totals by
    /// kind (never a request's text, answer or topic), which MCP's thinking_requests reads.</summary>
    internal const string RequestsStatusFile = "thinking-requests.json";

    /// <summary>How long after a change thinking-requests.json is written (changes in between go in the same write).</summary>
    internal static TimeSpan RequestsStatusDelay { get; } = TimeSpan.FromSeconds(1);

    private string? companionName;
    private int requestsPending;
    private readonly object requestsGate = new();

    /// <summary>Every request for the Thinking pool's slots, with its timings: the Thinking requests page reads it.</summary>
    internal ThinkingRequests ThinkingRequests => jobs.Places.Requests;

    /// <summary>The companion's name, which each new request names as the one it is for.</summary>
    private void NameRequests(LiveConversationConfiguration? configured) => Volatile.Write(ref companionName, configured?.CharacterName);

    // Made in the constructor, after the job list.
    private void StartRequests()
    {
        ThinkingRequests.Origin = () => Volatile.Read(ref companionName);
        ThinkingRequests.Changed += WriteRequestsStatus;
    }

    private void StopRequests() => ThinkingRequests.Changed -= WriteRequestsStatus;

    // Never on the reply's path: a change only starts one delayed write on the thread pool, and changes in between join it.
    private void WriteRequestsStatus()
    {
        if (dataDirectory is null || disposed || Interlocked.Exchange(ref requestsPending, 1) != 0) return;
        Task.Run(async () =>
        {
            await Task.Delay(RequestsStatusDelay).ConfigureAwait(false);
            Interlocked.Exchange(ref requestsPending, 0);
            if (disposed) return;
            var json = RequestsStatusJson();
            lock (requestsGate)
            {
                try
                {
                    var path = Path.Combine(dataDirectory, RequestsStatusFile);
                    File.WriteAllText(path + ".tmp", json);
                    File.Move(path + ".tmp", path, overwrite: true);
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
            }
        }).Forget();
    }

    /// <summary>What thinking-requests.json says now.</summary>
    internal string RequestsStatusJson() => RequestsJson(ThinkingRequests, jobs.Places, clock.GetUtcNow());

    internal static string RequestsJson(ThinkingRequests requests, BackgroundPlaces? places, DateTimeOffset now)
    {
        static double Ms(TimeSpan span) => Math.Round(span.TotalMilliseconds);
        var list = requests.List(places);
        return JsonSerializer.Serialize(new
        {
            schemaVersion = 1, updated = now, active = list.Count(r => r.Active), kept = list.Count,
            totals = requests.Totals.OrderBy(t => t.Key).ToDictionary(t => ThinkingJobKinds.Name(t.Key), t => new
            {
                count = t.Value.Count, succeeded = t.Value.Succeeded, problems = t.Value.Problems, retries = t.Value.Retries,
                preemptions = t.Value.Preemptions, averageWaitMs = Ms(t.Value.AverageWait), maxWaitMs = Ms(t.Value.MaxWaited),
                averageRunMs = Ms(t.Value.AverageRun), maxRunMs = Ms(t.Value.MaxRan)
            }),
            requests = list.Select(r => new
            {
                id = r.Id, kind = r.KindName, source = r.Start.Source.ToString(), task = ThinkingRequestWords.Task(r), origin = r.Origin,
                jobId = r.Start.JobId, priority = r.Priority, fast = r.Fast, needs = ThinkingRequestWords.Needs(r.Start.Needs),
                state = r.State.ToString(), note = r.Note, position = r.Position, waitingForConversation = r.HeldForConversation,
                posted = r.Posted, started = r.Started, finished = r.Finished,
                firstWaitMs = Ms(r.FirstWait), waitedMs = Ms(r.Waited), ranMs = Ms(r.Ran), totalMs = Ms(r.Total),
                attempts = r.Attempts.Select(a => new
                {
                    memberId = a.MemberId, member = a.Member, model = a.Model, started = a.Started, ended = a.Ended,
                    ms = Ms(a.Duration(r.Finished ?? now)), ending = a.Ending
                }),
                retries = r.Retries, preemptions = r.Preemptions, member = r.Last?.Member, model = r.Last?.Model,
                answerCharacters = r.AnswerLength, cut = r.Cut,
                timeoutMs = r.Start.Timeout is { } timeout ? Ms(timeout) : (double?)null, dropWhenStale = r.Start.DropWhenStale,
                maxOutputTokens = r.Start.MaxOutputTokens, reasoning = r.Start.Reasoning, tools = r.Start.Tools
            })
        }, new JsonSerializerOptions { WriteIndented = true });
    }
}
