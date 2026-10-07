using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Martlet.Conversation;
using Martlet.Core.Contracts;
using Martlet.Core.Settings;
using Martlet.Providers;

namespace Martlet.Mcp;

/// <summary>backup_thinking_check: Companion › Thinking pool › Backup Thinking. It rehearses the production race
/// (<see cref="ConversationTurn"/> with <see cref="ConversationRequest.Backup"/>) and the production member choice
/// (<see cref="ThinkingBackupMembers.Choose"/>) on fixture turns through the production conversation runtime and Chat
/// Completions adapter, with <see cref="ConversationRuntime.OpenTextAsync"/> for the backup: two fixture endpoints on 127.0.0.1
/// answer after set waits (canned words, NOT AI) and note when the client stopped their stream. It reports which stream gave
/// the reply, when the backup was asked, whether the loser was stopped and the reply latency line.</summary>
internal static class BackupThinkingCheck
{
    internal static readonly string[] Scenarios =
        ["backup-wins", "conversation-wins", "late-conversation", "conversation-fails", "no-member", "held", "let-go", "members"];
    private const string Model = "fixture-model";
    private const string Conversation = "The conversation's model answered.";
    private const string Backup = "The backup member answered.";
    // How far past its due time the backup may be asked here: the fixture runs in real time.
    private const double Slack = 300;

    internal static async Task<object> RunAsync(string? scenario, int? delayMs, CancellationToken cancellation)
    {
        string[] chosen = scenario is null ? Scenarios : Scenarios.Contains(scenario) ? [scenario]
            : throw new ArgumentException($"'scenario' must be one of {string.Join(", ", Scenarios)}.");
        if (delayMs is { } asked && !ThinkingPoolSettings.BackupDelayChoices.Contains(asked))
            throw new ArgumentException($"'delayMs' must be one of {string.Join(", ", ThinkingPoolSettings.BackupDelayChoices)}.");
        var delay = delayMs ?? 900;
        var results = new List<object>();
        var ok = true;
        foreach (var name in chosen)
        {
            if (name == "members")
            {
                var steps = Members();
                var allPassed = steps.All(s => s.Passed);
                ok &= allPassed;
                results.Add(new { scenario = name, ok = allPassed, steps = steps.Select(s => new { s.Name, s.Passed, s.Detail }) });
                continue;
            }
            var run = name switch
            {
                // The conversation's model is slow (3 s to its first words); the member answers 100 ms after it is asked.
                "backup-wins" => await TurnAsync(delay, conversationMs: 3_000, backupMs: 100, cancellation: cancellation),
                "conversation-wins" => await TurnAsync(delay, conversationMs: 200, backupMs: 100, cancellation: cancellation),
                // Asked at the delay, but the conversation's model starts 400 ms later, before the member (1.5 s).
                "late-conversation" => await TurnAsync(delay, conversationMs: delay + 400, backupMs: 1_500, cancellation: cancellation),
                // The conversation's model fails after the member was asked; the member answers.
                "conversation-fails" => await TurnAsync(delay, conversationMs: delay + 200, backupMs: 600, conversationFails: true,
                    cancellation: cancellation),
                "no-member" => await TurnAsync(delay, conversationMs: delay + 600, backupMs: 100, noMember: true, cancellation: cancellation),
                // A reply started early, held for 1.5 s, with only a paid cloud member that may answer: asked once it is taken.
                "held" => await TurnAsync(delay, conversationMs: 4_000, backupMs: 100, holdMs: 1_500, paidOnly: true, cancellation: cancellation),
                // A reply started early and let go at 1.5 s, after its member (on the home network) was asked: both stop.
                "let-go" => await TurnAsync(delay, conversationMs: 4_000, backupMs: 3_000, holdMs: 1_500, letGo: true, cancellation: cancellation),
                _ => throw new ArgumentException(name)
            };
            var passed = name switch
            {
                "backup-wins" => run.Outcome == "Won" && run.Text == Backup && Near(run.AskedAfterMs, delay) &&
                    run.Conversation.StoppedMs is not null && !run.Conversation.Answered && run.Line?.Contains("Backup Thinking won at", StringComparison.Ordinal) == true,
                "conversation-wins" => run.Outcome == "NotNeeded" && run.Text == Conversation && run.Backup.Requests == 0,
                "late-conversation" => run.Outcome == "Lost" && run.Text == Conversation && Near(run.AskedAfterMs, delay) &&
                    run.Backup.StoppedMs is not null && !run.Backup.Answered &&
                    run.Line?.Contains("the conversation's model won", StringComparison.Ordinal) == true,
                "conversation-fails" => run.Outcome == "Won" && run.Text == Backup && run.Conversation.Failed,
                "no-member" => run.Outcome == "NoMember" && run.Text == Conversation && run.Backup.Requests == 0 &&
                    run.Line?.Contains("no member could take it", StringComparison.Ordinal) == true,
                "held" => run.Outcome == "Won" && run.Text == Backup && run.AskedHeld.SequenceEqual([true, false]) &&
                    run.AskedAfterMs >= run.ReleasedAtMs - 30,
                "let-go" => run.Outcome is null && run.State == "Canceled" && run.Backup.Requests == 1 && run.Backup.StoppedMs is not null &&
                    run.Conversation.StoppedMs is not null && run.Text == "",
                _ => false
            };
            ok &= passed;
            results.Add(new { scenario = name, ok = passed, turn = run });
        }
        return new
        {
            ok, delayMs = delay, provenance = "FIXTURE, NOT AI: canned words from two endpoints on 127.0.0.1, nothing played",
            scenarios = results
        };
    }

    private static bool Near(double? at, double due) => at is { } value && value >= due - 30 && value <= due + Slack;

    internal sealed record Endpoint(int Requests, double? AskedMs, double? StoppedMs, bool Answered, bool Failed);

    internal sealed record Turn(string State, string Text, string? Outcome, string? Member, double? AskedAfterMs, double? FirstWordsAfterMs,
        double? ReleasedAtMs, IReadOnlyList<bool> AskedHeld, Endpoint Conversation, Endpoint Backup, string? Line);

    private static async Task<Turn> TurnAsync(int delayMs, int conversationMs, int backupMs, bool conversationFails = false,
        bool noMember = false, int? holdMs = null, bool paidOnly = false, bool letGo = false, CancellationToken cancellation = default)
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        var clock = Stopwatch.StartNew();
        var primary = new Fixture(Conversation, conversationMs, conversationFails, clock);
        var second = new Fixture(Backup, backupMs, false, clock);
        var serving = Task.WhenAll(primary.ServeAsync(stop.Token), second.ServeAsync(stop.Token));
        try
        {
            var voice = new HostSpeechTarget("https://127.0.0.1:9443", "fixture-host", "sha256:" + new string('0', 64), "desktop-fixture",
                Guid.NewGuid(), "chatterbox-turbo", Guid.NewGuid(), new string('0', 64));
            await using var runtime = ConversationRuntime.Create(new SpokenReplyCheck.NoCredentials());
            await using var members = ConversationRuntime.Create(new SpokenReplyCheck.NoCredentials());
            var backup = new FixtureBackup(TimeSpan.FromMilliseconds(delayMs), members, second.BaseUrl, voice, noMember, paidOnly);
            var request = new ConversationRequest(new BoundedTextInput("Say hi.", "Fixture check."),
                new TextModelSelection(ChatCompletionsSetup.Alias, Model), new TextGenerationLimits(), new ConversationLimits(),
                chat: new ChatCompletionsTarget(primary.BaseUrl, Keyless: true)) { Backup = backup };
            var permissions = new SpokenReplyCheck.Permissions(ChatCompletionsSetup.BaseUri(primary.BaseUrl), voice);
            var startedAt = TimeProvider.System.GetTimestamp();
            var turn = holdMs is null ? runtime.Start(request, permissions, cancellation)
                : runtime.StartEarly(request, permissions, prepareVoice: true, cancellation);
            double? released = null;
            if (holdMs is { } hold)
            {
                await Task.Delay(hold, cancellation);
                if (letGo) _ = turn.StopAsync();
                else if (turn.Release()) released = clock.Elapsed.TotalMilliseconds;
            }
            var terminal = await turn.Completion.WaitAsync(TimeSpan.FromSeconds(30), cancellation);
            await turn.OwnershipRelease.WaitAsync(TimeSpan.FromSeconds(10), cancellation);
            // The loser is let go off the reply's path: give its connection a moment to close.
            await Task.Delay(300, cancellation);
            var line = ReplyLatency.Describe(null, startedAt, TimeProvider.System, terminal, $"Thinking {Model}");
            var result = terminal.Backup;
            return new(terminal.State.ToString(), turn.Content.Text, result?.Outcome.ToString(), result?.Member,
                result?.AskedAfter?.TotalMilliseconds, result?.FirstWordsAfter?.TotalMilliseconds, released, backup.AskedHeld,
                primary.Report(), second.Report(), line);
        }
        finally
        {
            await stop.CancelAsync();
            primary.Stop();
            second.Stop();
            try { await serving; }
            catch (Exception error) when (error is OperationCanceledException or SocketException or ObjectDisposedException or IOException) { }
        }
    }

    // The desktop's Backup Thinking stand-in: the production member choice over a fixture pool (a member on the home network
    // that may answer, or with paidOnly a cloud one), then the member's own runtime opens the same request on the backup
    // endpoint (ConversationRuntime.OpenTextAsync).
    private sealed class FixtureBackup(TimeSpan delay, ConversationRuntime runtime, string baseUrl, HostSpeechTarget voice, bool noMember,
        bool paidOnly) : IThinkingBackup
    {
        private readonly List<bool> asked = [];
        internal IReadOnlyList<bool> AskedHeld { get { lock (asked) return [.. asked]; } }
        public TimeSpan Delay => delay;

        public async Task<ThinkingBackupStream?> OpenAsync(ConversationRequest reply, BoundedTextInput input, CorrelationIds ids, long epoch,
            bool held, CancellationToken token)
        {
            lock (asked) asked.Add(held);
            var member = paidOnly ? Cloud : Lan;
            var pool = new ThinkingPoolSettings { Members = [member], BackupThinking = true, AnswersForConversation = noMember ? [] : [member.Key] };
            var plan = pool.Plan([]);
            var choice = ThinkingBackupMembers.Choose(pool, plan, ThinkLonger.Places(plan), LiveResources.None, input, held);
            if (choice.Spot is null) return null;
            var request = new ConversationRequest(input, new TextModelSelection(ChatCompletionsSetup.Alias, Model), reply.TextLimits, reply.Limits,
                chat: new ChatCompletionsTarget(baseUrl, Keyless: true), generation: reply.Generation);
            var stream = await runtime.OpenTextAsync(request, new SpokenReplyCheck.Permissions(ChatCompletionsSetup.BaseUri(baseUrl), voice),
                ids, epoch, token);
            return new(member.Describe(), stream);
        }

        public void Ended(ThinkingBackupResult result) { }
    }

    private static readonly DeepThinkingSettings Lan = new()
    {
        Place = DeepThinkingPlace.Endpoint, Origin = "https://10.77.0.20:8443/v1", ModelId = "qwen3:8b"
    };
    private static readonly DeepThinkingSettings Cloud = new()
    {
        Place = DeepThinkingPlace.Endpoint, Origin = "https://openrouter.ai/api/v1", ModelId = "x-ai/grok-4.3"
    };
    private static readonly DeepThinkingSettings Host = new()
    {
        Place = DeepThinkingPlace.Host, HostId = "diva", HostOrigin = "https://10.77.0.30:9443", HostSpkiFingerprint = "sha256:" + new string('0', 64),
        HostDeviceId = "desktop-fixture", HostCredentialId = Guid.Parse("00000000-0000-0000-0000-000000000001"),
        HostRouteId = SelfHostSetup.DeepThinkingRouteId, ModelId = "qwen3-8b"
    };

    private sealed record Step(string Name, bool Passed, string Detail);

    // The member rules, on fixture pools and requests (no request leaves the process).
    private static List<Step> Members()
    {
        var text = new BoundedTextInput("Say hi.", "Fixture check.");
        var tools = new BoundedTextInput("Say hi.", "Fixture check.", tools: [new TextToolDefinition("think_longer", "Think it over.",
            """{"type":"object","properties":{}}""")]);
        Step Check(string name, ThinkingPoolSettings pool, BoundedTextInput input, bool held, string? expected, LiveResources? live = null)
        {
            var plan = pool.Plan([]);
            var choice = ThinkingBackupMembers.Choose(pool, plan, ThinkLonger.Places(plan), live ?? LiveResources.None, input, held);
            var got = choice.Spot?.Settings.Describe();
            return new(name, got == expected, $"{(got is null ? "none" : got)}: {choice.Why}");
        }
        var all = new ThinkingPoolSettings { Members = [Lan, Cloud, Host], BackupThinking = true };
        return
        [
            Check("off: nothing is asked", all with { BackupThinking = false, AnswersForConversation = [Lan.Key] }, text, false, null),
            Check("no member ticked: nothing is asked", all, text, false, null),
            Check("a cloud member is never asked unless ticked", all with { AnswersForConversation = [Lan.Key] }, text, false, Lan.Describe()),
            Check("a ticked cloud member waits while a reply started early isn't taken", all with { AnswersForConversation = [Cloud.Key] }, text,
                true, null),
            Check("a ticked cloud member answers a reply that is taken", all with { AnswersForConversation = [Cloud.Key] }, text, false,
                Cloud.Describe()),
            Check("a member on the conversation's computer is passed over", all with { AnswersForConversation = [Lan.Key, Cloud.Key] }, text, false,
                Cloud.Describe(), new LiveResources([new LiveResource("thinking", "lan:10.77.0.20", [])])),
            Check("a paired computer can't take a request with tools", all with { AnswersForConversation = [Host.Key] }, tools, false, null),
            Check("an endpoint takes a request with tools", all with { AnswersForConversation = [Host.Key, Lan.Key] }, tools, false, Lan.Describe())
        ];
    }

    // FIXTURE, NOT AI: a Chat Completions endpoint on 127.0.0.1 that sends its headers at once and its one sentence (or, failing,
    // an error) after a set wait, and notes when the client stopped the stream before that.
    private sealed class Fixture(string words, int wordsMs, bool fails, Stopwatch clock)
    {
        private readonly TcpListener listener = Listen();
        private int requests;
        private double? askedMs, stoppedMs;
        private bool answered, failed;
        internal string BaseUrl => $"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}/v1";

        private static TcpListener Listen()
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            return listener;
        }

        internal Endpoint Report()
        {
            lock (this) return new(requests, askedMs, stoppedMs, answered, failed);
        }

        internal void Stop() => listener.Stop();

        internal async Task ServeAsync(CancellationToken cancellation)
        {
            while (!cancellation.IsCancellationRequested)
            {
                using var client = await listener.AcceptTcpClientAsync(cancellation);
                await using var stream = client.GetStream();
                await HearingCheck.ReadRequestAsync(stream, cancellation);
                lock (this)
                {
                    requests++;
                    askedMs ??= clock.Elapsed.TotalMilliseconds;
                }
                // The client stopping the stream closes the connection, which ends this read.
                var closed = Closed(stream, cancellation);
                if (!fails)
                    await WriteAsync(stream, "HTTP/1.1 200 OK\r\nContent-Type: text/event-stream\r\nCache-Control: no-store\r\nConnection: close\r\n\r\n",
                        cancellation);
                if (await Task.WhenAny(closed, Task.Delay(wordsMs, cancellation)) == closed)
                {
                    lock (this) stoppedMs ??= clock.Elapsed.TotalMilliseconds;
                    continue;
                }
                try
                {
                    if (fails)
                    {
                        await WriteAsync(stream, "HTTP/1.1 500 Internal Server Error\r\nContent-Type: application/json\r\nContent-Length: 2\r\n" +
                            "Connection: close\r\n\r\n{}", cancellation);
                        lock (this) failed = true;
                        continue;
                    }
                    const string chunk = "{\"id\":\"fixture\",\"object\":\"chat.completion.chunk\",\"model\":\"fixture\",\"choices\":[{\"index\":0,";
                    await WriteAsync(stream, "data: " + chunk + "\"delta\":{\"role\":\"assistant\",\"content\":\"" + words + "\"},\"finish_reason\":null}]}\n\n",
                        cancellation);
                    await WriteAsync(stream, "data: " + chunk + "\"delta\":{},\"finish_reason\":\"stop\"}]}\n\ndata: [DONE]\n\n", cancellation);
                    lock (this) answered = true;
                }
                catch (IOException)
                {
                    lock (this) stoppedMs ??= clock.Elapsed.TotalMilliseconds;
                }
            }
        }

        private static async Task Closed(NetworkStream stream, CancellationToken cancellation)
        {
            var buffer = new byte[256];
            try
            {
                while (await stream.ReadAsync(buffer, cancellation) > 0) { }
            }
            catch (IOException) { }
            catch (ObjectDisposedException) { }
        }

        private static async Task WriteAsync(NetworkStream stream, string text, CancellationToken cancellation)
        {
            await stream.WriteAsync(Encoding.UTF8.GetBytes(text), cancellation);
            await stream.FlushAsync(cancellation);
        }
    }
}
