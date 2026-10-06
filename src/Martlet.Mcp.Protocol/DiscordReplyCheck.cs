using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Martlet.Core.Contracts;
using Martlet.Core.Settings;
using Martlet.Discord;
using Martlet.Providers;

namespace Martlet.Mcp;

/// <summary>discord_reply_status and discord_reply_check: Martlet's Discord reply engine. The status reads the desktop's
/// discord-replies.json (counts, times and codes only) and discord.json's chat setup (no token, IDs or names). The check runs the
/// production Discord reply side (<see cref="DiscordReplier"/>: per-place history, the ambient gate, prompt shaping, [pass] and
/// Discord's limits) through the production Chat Completions adapter against a fixture endpoint on 127.0.0.1 (canned replies,
/// NOT AI); only with <c>live</c> it asks Ollama on this PC instead (made-up Discord lines, never anything anyone said). Nothing
/// leaves loopback; no credentials are read.</summary>
internal static class DiscordReplyCheck
{
    private const string StatusFile = "discord-replies.json";

    internal static object Status(string dataDirectory)
    {
        var path = Path.Combine(dataDirectory, StatusFile);
        object? engine = null;
        string file;
        try
        {
            if (File.Exists(path))
            {
                using var document = JsonDocument.Parse(File.ReadAllText(path));
                engine = document.RootElement.Clone();
                file = "loaded";
            }
            else file = "none";
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException) { file = "unreadable"; }
        var preferences = DiscordPreferences.Load(dataDirectory);
        return new
        {
            file,
            note = file == "none" ? "The desktop writes discord-replies.json when it wires the reply engine at start." : null,
            engine,
            discord = new
            {
                configured = preferences.Configured,
                enabled = preferences.Enabled,
                ownerSet = preferences.OwnerUserId != 0,
                serverChat = preferences.ServerChat.ToString(),
                directChat = preferences.DirectChat.ToString(),
                voiceChat = preferences.VoiceChat.ToString(),
                channelRules = preferences.Channels.Count,
                people = preferences.People.Count
            }
        };
    }

    private sealed record Step(string Name, DiscordTurn Turn, TimeSpan Before, string Expected);

    internal static async Task<object> RunAsync(string dataDirectory, string? model, bool live, CancellationToken cancellation)
    {
        if (model is not null)
        {
            try { ChatCompletionsSetup.ModelId(model); }
            catch (ContractException error) { throw new ArgumentException(error.Message); }
        }
        var loaded = await new SettingsStore(dataDirectory).LoadAsync(cancellation);
        var thinking = loaded.Settings?.Setup?.Routes.FirstOrDefault(r => r.Role == SetupRole.Llm);
        var localOllama = thinking is not null && ContextBudget.IsLocalOllama(thinking.RouteType, thinking.Origin);
        var liveModel = model ?? (localOllama ? thinking!.ModelId : null);
        var persona = loaded.Settings?.Companion?.ActivePersona;
        var personaText = persona is null ? null
            : PromptSettings.Fill(loaded.Settings!.Prompts, PromptCatalog.Persona, ("name", persona.Name), ("persona", persona.Text),
                ("style", PromptSettings.Text(loaded.Settings.Prompts, PromptCatalog.StyleHelpful)));
        return new
        {
            fixture = await FixtureAsync(cancellation),
            live = !live ? NotRun("Pass live: true to ask Ollama on this PC (loopback only; it loads the model).")
                : liveModel is null ? NotRun("Thinking isn't Ollama on this PC; pass model to name a model Ollama has.")
                : await LiveAsync(liveModel, persona?.Name, personaText, cancellation)
        };
    }

    private static object NotRun(string why) => new { ran = false, why };

    private static readonly DiscordPlace Channel = new(2002, 1001, "general", Direct: false);
    private static readonly DiscordPlace Call = new(2003, 1001, "Hangout", Direct: false);
    private static readonly DiscordPlace Dm = new(3003, null, "Ana", Direct: true);
    private static readonly DiscordSpeaker Ana = new(1, "Ana", IsOwner: true);
    private static readonly DiscordSpeaker Bo = new(2, "Bo", IsOwner: false);
    private static readonly DiscordSpeaker Cy = new(3, "Cy", IsOwner: false);

    private static DiscordTurn Turn(DiscordPlace place, DiscordSpeaker speaker, string text, bool addressed,
        DiscordTurnSource source = DiscordTurnSource.Text) => new(place, speaker, text, source, addressed, []);

    private static Step[] Steps =>
    [
        new("owner greets the channel (ambient, nothing about Martlet)", Turn(Channel, Ana, "morning all", false), TimeSpan.Zero, "skip:chance"),
        new("addressed in a server channel", Turn(Channel, Bo, "hey Martlet, how are you?", true), TimeSpan.FromSeconds(5), "reply"),
        new("ambient right after Martlet spoke", Turn(Channel, Cy, "did anyone see the game?", false), TimeSpan.FromSeconds(5), "skip:cooldown"),
        new("ambient naming Martlet after the cooldown (model answers [pass])", Turn(Channel, Bo, "martlet probably slept through it", false),
            TimeSpan.FromMinutes(2), "pass"),
        new("addressed, the model writes more than Discord allows", Turn(Channel, Cy, "Martlet, tell me everything", true),
            TimeSpan.FromSeconds(5), "reply"),
        new("addressed in a voice call (markdown and emoji left out)", Turn(Call, Bo, "Martlet can you hear me?", true, DiscordTurnSource.Voice),
            TimeSpan.FromSeconds(5), "reply"),
        new("the owner's direct message", Turn(Dm, Ana, "are you there?", true), TimeSpan.FromSeconds(5), "reply")
    ];

    private static async Task<object> FixtureAsync(CancellationToken cancellation)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var baseUrl = $"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}/v1";
        var requests = new List<byte[]>();
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        var serving = ServeAsync(listener, requests, stop.Token);
        try
        {
            using var adapter = ChatCompletionsTextGenerationAdapter.Create(baseUrl);
            var clock = new StepClock();
            var replier = new DiscordReplier((context, token) => AskAsync(adapter, baseUrl, "fixture-model", Input(context, null),
                new TextGenerationLimits(), token), () => ["Martlet"], clock: clock, random: () => 0.5);
            var results = new List<object>();
            var ok = true;
            foreach (var step in Steps)
            {
                clock.Advance(step.Before);
                int before;
                lock (requests) before = requests.Count;
                var reply = await replier.ReplyAsync(step.Turn, cancellation);
                byte[]? body;
                lock (requests) body = requests.Count > before ? requests[^1] : null;
                var stats = replier.Stats;
                var outcome = reply is not null ? "reply" : body is null ? "skip:" + stats.LastSkip : "pass";
                var limit = step.Turn.Source == DiscordTurnSource.Voice ? DiscordReplyText.VoiceLimit : DiscordReplyText.TextLimit;
                var spokenOk = step.Turn.Source != DiscordTurnSource.Voice || reply is null ||
                    !reply.Text.Contains('*') && !reply.Text.Contains("http", StringComparison.Ordinal);
                var match = outcome == step.Expected && (reply is null || reply.Text.Length <= limit) && spokenOk;
                ok &= match;
                results.Add(new
                {
                    step = step.Name,
                    addressed = step.Turn.Addressed,
                    mayPass = step.Turn.MayPass,
                    expected = step.Expected,
                    outcome,
                    replyCharacters = reply?.Text.Length,
                    reply = reply is null ? null : Trim(reply.Text),
                    sent = body is null ? null : Sent(body),
                    ok = match
                });
            }
            return new
            {
                ok,
                endpoint = baseUrl,
                note = "Fixture endpoint on 127.0.0.1 with canned replies (NOT AI). The desktop builds the same turns into the " +
                    "live conversation's own request (persona, style, lore, memory, reply length) on its own runtimes.",
                stats = replier.Stats,
                steps = results
            };
        }
        finally
        {
            stop.Cancel();
            listener.Stop();
            try { await serving; } catch (Exception error) when (error is OperationCanceledException or SocketException or ObjectDisposedException or IOException) { }
        }
    }

    // Two made-up Discord turns to Ollama on this PC with the saved persona: one addressed to Martlet, one it may pass over.
    private static async Task<object> LiveAsync(string model, string? name, string? persona, CancellationToken cancellation)
    {
        const string baseUrl = GenerationSupport.LocalOllamaChatBaseUrl;
        using (var client = new HttpClient(new SocketsHttpHandler { UseProxy = false }) { Timeout = TimeSpan.FromSeconds(5) })
        {
            try
            {
                using var version = await client.GetAsync("http://127.0.0.1:11434/api/version", cancellation);
                if (!version.IsSuccessStatusCode) return NotRun($"Ollama on this PC answered {(int)version.StatusCode}.");
            }
            catch (HttpRequestException) { return NotRun("Ollama isn't running on this PC."); }
        }
        var limits = new TextGenerationLimits
        {
            MaxOutputTokens = 4096, MaxEvents = 4094, FirstDeltaTimeout = TimeSpan.FromMinutes(2), IdleTimeout = TimeSpan.FromMinutes(2),
            MaxRequestTime = TimeSpan.FromMinutes(2)
        };
        using var adapter = ChatCompletionsTextGenerationAdapter.Create(baseUrl);
        var names = name is { Length: > 0 } ? new[] { name, "Martlet" } : ["Martlet"];
        var timings = new List<long>();
        var replier = new DiscordReplier(async (context, token) =>
        {
            var clock = Stopwatch.StartNew();
            var answer = await AskAsync(adapter, baseUrl, model, Input(context, persona), limits, token);
            timings.Add(clock.ElapsedMilliseconds);
            return answer;
        }, () => names, new() { AmbientChance = 1 });
        DiscordTurn[] turns =
        [
            Turn(Channel, Bo, $"hey {names[0]}, what's your favourite thing to do on a rainy day?", true),
            Turn(Channel, Cy, "brb, grabbing a coffee", false)
        ];
        var results = new List<object>();
        var answered = false;
        foreach (var turn in turns)
        {
            var reply = await replier.ReplyAsync(turn, cancellation);
            answered |= turn.Addressed && reply is not null;
            results.Add(new { addressed = turn.Addressed, mayPass = turn.MayPass, outcome = reply is null ? "quiet" : "reply",
                reply = reply?.Text, ms = timings.Count > 0 ? timings[^1] : (long?)null });
        }
        var stats = replier.Stats;
        return new
        {
            ran = true,
            ok = stats.Failures == 0 && answered,
            model,
            persona = persona is not null,
            note = "Ollama on this PC over loopback; made-up Discord lines, never anything anyone said.",
            stats,
            turns = results
        };
    }

    // The Discord turn as one Chat Completions request: the instructions (after the persona, as the desktop puts them), the
    // earlier messages, the message and its note.
    private static BoundedTextInput Input(DiscordTurnContext context, string? persona)
    {
        var prompt = DiscordPrompts.Shape(context);
        return new(prompt.Message, persona is null ? prompt.Instructions : persona + "\n\n" + prompt.Instructions,
            prompt.History.Select(message => new TextHistoryMessage(message.FromMartlet ? TextHistoryRole.Assistant : TextHistoryRole.User,
                message.Text)), notes: prompt.Note);
    }

    private static async Task<string?> AskAsync(ChatCompletionsTextGenerationAdapter adapter, string baseUrl, string model,
        BoundedTextInput input, TextGenerationLimits limits, CancellationToken cancellation)
    {
        var ids = new CorrelationIds { SessionId = Guid.NewGuid(), TurnId = Guid.NewGuid(), RequestId = Guid.NewGuid() };
        var deadline = DateTimeOffset.UtcNow + limits.MaxRequestTime;
        var selection = new TextModelSelection(ChatCompletionsSetup.Alias, model);
        var authorization = new TextDisclosureAuthorization(new(ChatCompletionsSetup.BaseUri(baseUrl), ProviderRole.Llm, model),
            selection, ids, 1, limits, deadline, true, true);
        var stream = adapter.Stream(new() { Ids = ids, Epoch = 1, Deadline = deadline }, selection, input, limits, authorization,
            cancellation, new GenerationSettings { Reasoning = false });
        var reply = new StringBuilder();
        await foreach (var item in stream.WithCancellation(cancellation))
            if (item.Kind == ProviderEventKind.TextDelta) reply.Append(item.Text);
        if (stream.Result?.Outcome.ToString() != "Completed")
            throw new DiscordReplyException(stream.Result?.Failure?.Code.ToString() ?? "reply.failed");
        return reply.ToString();
    }

    // What the request carried: each message's role and length, the start of the instructions, and the current message's note.
    private static object Sent(byte[] body)
    {
        using var document = JsonDocument.Parse(body);
        var messages = document.RootElement.GetProperty("messages").EnumerateArray().Select(message =>
        {
            var content = message.GetProperty("content").GetString() ?? "";
            return new { role = message.GetProperty("role").GetString(), characters = content.Length, text = Trim(content) };
        }).ToArray();
        return new { messages, tools = document.RootElement.TryGetProperty("tools", out _) };
    }

    private static string Trim(string text) => text.Length > 240 ? text[..240] + "…" : text;

    // A minimal HTTP/1.1 endpoint: records each request body and streams a canned reply chosen by what the request asks.
    private static async Task ServeAsync(TcpListener listener, List<byte[]> requests, CancellationToken cancellation)
    {
        while (!cancellation.IsCancellationRequested)
        {
            using var client = await listener.AcceptTcpClientAsync(cancellation);
            await using var stream = client.GetStream();
            var body = await HearingCheck.ReadRequestAsync(stream, cancellation);
            lock (requests) requests.Add(body);
            var text = Encoding.UTF8.GetString(body);
            var answer = text.Contains("answer exactly " + DiscordPrompts.PassMarker, StringComparison.Ordinal) ? DiscordPrompts.PassMarker
                : text.Contains("tell me everything", StringComparison.Ordinal)
                    ? string.Concat(Enumerable.Repeat("Here is everything I know, one fixture sentence at a time. ", 60))
                : text.Contains("voice call", StringComparison.Ordinal) ? "**Loud and clear!** \ud83d\ude00 See [this](https://example.com)."
                : "Fixture reply (not AI).";
            const string chunk = "{\"id\":\"fixture\",\"object\":\"chat.completion.chunk\",\"model\":\"fixture\",\"choices\":[{\"index\":0,";
            var events = "data: " + chunk + "\"delta\":{\"role\":\"assistant\",\"content\":" + JsonSerializer.Serialize(answer) +
                "},\"finish_reason\":null}]}\n\n" + "data: " + chunk + "\"delta\":{},\"finish_reason\":\"stop\"}]}\n\ndata: [DONE]\n\n";
            var payload = Encoding.UTF8.GetBytes(events);
            var head = Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Type: text/event-stream\r\n" +
                $"Content-Length: {payload.Length}\r\nConnection: close\r\n\r\n");
            await stream.WriteAsync(head, cancellation);
            await stream.WriteAsync(payload, cancellation);
            await stream.FlushAsync(cancellation);
        }
    }

    // The check's own clock, so the ambient cooldown can be stepped through without waiting.
    private sealed class StepClock : TimeProvider
    {
        private DateTimeOffset now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => now;
        public override long GetTimestamp() => now.UtcTicks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        internal void Advance(TimeSpan by) => now += by;
    }
}
