using System.IO;
using System.Text.Json;
using Martlet.Discord;

namespace Martlet.Mcp;

/// <summary>discord_text_check: feeds simulated Discord messages through Martlet's production text pipeline
/// (<see cref="DiscordTextChat"/>: chat modes, addressing, per-place recent lines, stale-turn dropping, splitting and mention
/// sanitizing) with a fake transport and a fixture reply engine (NOT AI), so text chat is verifiable without a bot token.
/// Without messages it runs fixed scenarios on fixture preferences; with messages it uses the data directory's discord.json.
/// Reads no credentials and contacts nothing.</summary>
internal static class DiscordTextCheck
{
    private const ulong Owner = 1001, Known = 1002, Stranger = 1003, OtherBot = 1004;
    private const ulong Guild = 3001, MentionsChannel = 2001, SometimesChannel = 2002, OffChannel = 2003, AlwaysChannel = 2004;
    private const string FixtureReply = "Hi from the fixture reply engine (not AI).";

    private static readonly DiscordPreferences Fixture = new()
    {
        OwnerUserId = Owner,
        People = [new(Known, "Bo")],
        ServerChat = DiscordChatMode.Mentions,
        DirectChat = DiscordChatMode.Always,
        Channels =
        [
            new(Guild, SometimesChannel, "sometimes", DiscordChatMode.Sometimes),
            new(Guild, OffChannel, "off", DiscordChatMode.Off),
            new(Guild, AlwaysChannel, "always", DiscordChatMode.Always)
        ]
    };

    internal static async Task<object> RunAsync(string directory, JsonElement arguments, CancellationToken cancellation)
    {
        var reply = arguments.ValueKind == JsonValueKind.Object && arguments.TryGetProperty("reply", out var given) &&
            given.ValueKind == JsonValueKind.String ? given.GetString()! : FixtureReply;
        if (reply.Length is 0 or > 4096) throw new ArgumentException("'reply' must be 1-4096 characters.");
        var messages = arguments.ValueKind == JsonValueKind.Object && arguments.TryGetProperty("messages", out var listed) &&
            listed.ValueKind == JsonValueKind.Array ? listed.EnumerateArray().ToArray() : null;
        if (messages is { Length: 0 or > 16 }) throw new ArgumentException("'messages' must have 1-16 entries.");

        var saved = DiscordPreferences.Load(directory);
        var savedSource = File.Exists(Path.Combine(directory, DiscordPreferences.FileName)) ? "saved" : "default";
        var preferences = messages is null ? Fixture : saved.OwnerUserId == 0 ? saved with { OwnerUserId = Owner } : saved;
        var rig = new Rig(preferences, reply);
        object results;
        if (messages is null)
        {
            var scenarios = await ScenariosAsync(rig, cancellation);
            results = new
            {
                passed = scenarios.All(scenario => scenario.Passed),
                scenarios = scenarios.Select(scenario => new { name = scenario.Name, passed = scenario.Passed, detail = scenario.Detail })
            };
        }
        else
        {
            List<object> outcomes = [];
            ulong id = 100;
            foreach (var message in messages)
            {
                var incoming = Parse(message, preferences, ++id);
                var command = message.TryGetProperty("command", out var flag) && flag.ValueKind == JsonValueKind.True;
                outcomes.Add(Report(await rig.SendAsync(incoming, command, cancellation), rig, incoming));
            }
            results = new { messages = outcomes };
        }
        var stats = rig.Chat.Stats;
        return new
        {
            preferences = new
            {
                source = messages is null ? "fixture" : savedSource,
                ownerSet = saved.OwnerUserId != 0,
                serverChat = preferences.ServerChat.ToString(),
                directChat = preferences.DirectChat.ToString(),
                directFromAnyone = preferences.DirectFromAnyone,
                channelRules = preferences.Channels.Count,
                people = preferences.People.Count
            },
            results,
            stats = new
            {
                seen = stats.Seen, considered = stats.Considered, answered = stats.Answered, passed = stats.Passed,
                dropped = stats.Dropped, failed = stats.Failed,
                lastPlaceKind = stats.LastPlaceKind, lastError = stats.LastError
            },
            note = "Fake transport and fixture reply engine (NOT AI, NOT Discord): no token, no network."
        };
    }

    private sealed record Scenario(string Name, bool Passed, string Detail);

    private static async Task<IReadOnlyList<Scenario>> ScenariosAsync(Rig rig, CancellationToken cancellation)
    {
        List<Scenario> scenarios = [];
        ulong id = 0;
        async Task Check(string name, DiscordIncoming incoming, Func<DiscordTextResult, Sent[], bool> expect, bool command = false)
        {
            var before = rig.Transport.Count;
            var result = await rig.SendAsync(incoming, command, cancellation);
            var sent = rig.Transport.Since(before);
            scenarios.Add(new(name, expect(result, sent), Detail(result, sent)));
        }
        DiscordIncoming Server(ulong channel, string text, ulong author = Owner, bool mention = false, bool reply = false, bool bot = false) =>
            new(++id, new(channel, Guild, $"#{channel}", false), new(author, Name(author), author == Owner), text, mention, reply, bot);
        DiscordIncoming Dm(ulong author, string text) =>
            new(++id, new(4000 + author, null, $"DM with {Name(author)}", true), new(author, Name(author), author == Owner), text);

        await Check("owner DM is answered (not quoted)", Dm(Owner, "hello"),
            (r, s) => r.Outcome == DiscordTextOutcome.Answered && s is [{ ReplyTo: null }]);
        await Check("a known person's DM is answered", Dm(Known, "hi there"), (r, s) => r.Outcome == DiscordTextOutcome.Answered && s.Length == 1);
        await Check("a stranger's DM is ignored", Dm(Stranger, "hey"), (r, s) => r.Outcome == DiscordTextOutcome.NotConsidered && s.Length == 0);
        await Check("Mentions channel: chatter is ignored", Server(MentionsChannel, "anyone up for games?"),
            (r, s) => r.Outcome == DiscordTextOutcome.NotConsidered && s.Length == 0);
        await Check("Mentions channel: an @mention gets a quoted reply", Server(MentionsChannel, "what do you think?", mention: true),
            (r, s) => r.Outcome == DiscordTextOutcome.Answered && s is [{ ReplyTo: not null }]);
        await Check("Mentions channel: saying Martlet's name addresses it", Server(MentionsChannel, "martlet, are you there?", Known),
            (r, s) => r.Outcome == DiscordTextOutcome.Answered && r.Addressed);
        await Check("Mentions channel: a reply to Martlet addresses it", Server(MentionsChannel, "ha, true", Known, reply: true),
            (r, s) => r.Outcome == DiscordTextOutcome.Answered && r.Addressed);
        await Check("Sometimes channel: the engine may pass on chatter", Server(SometimesChannel, "nice weather today"),
            (r, s) => r.Outcome == DiscordTextOutcome.Passed && s.Length == 0);
        await Check("Off channel: even a mention is ignored", Server(OffChannel, "hello?", mention: true),
            (r, s) => r.Outcome == DiscordTextOutcome.NotConsidered && s.Length == 0);
        await Check("Always channel: chatter is answered, not quoted", Server(AlwaysChannel, "so what's everyone doing"),
            (r, s) => r.Outcome == DiscordTextOutcome.Answered && s is [{ ReplyTo: null }]);
        await Check("other bots are never answered", Server(AlwaysChannel, "Martlet beep boop", OtherBot, mention: true, bot: true),
            (r, s) => r.Outcome == DiscordTextOutcome.IgnoredBot && s.Length == 0);
        await Check("a long reply is split under 2,000 characters with @everyone neutralized", Server(AlwaysChannel, "tell me a long story"),
            (r, s) => r.Outcome == DiscordTextOutcome.Answered && s.Length >= 2 && s.All(m => m.Length <= DiscordTextFormat.Limit) &&
                      !s.Any(m => m.Mass));
        await Check("/martlet in a group DM is answered through the interaction", new(++id, new(5001, null, "group DM", true),
                new(Known, Name(Known), false), "what's 2+2?"),
            (r, s) => r.Outcome == DiscordTextOutcome.Answered && s is [{ Command: true }], command: true);

        // A newer message while Martlet is still thinking makes the older turn stale.
        var before = rig.Transport.Count;
        var slow = rig.SendAsync(Server(AlwaysChannel, "Martlet, slow question", mention: true), false, cancellation);
        await rig.Thinking.Task.WaitAsync(TimeSpan.FromSeconds(10), cancellation);
        var newer = rig.SendAsync(Server(AlwaysChannel, "actually, never mind that"), false, cancellation);
        await Task.Delay(50, cancellation);
        rig.Release.TrySetResult();
        var results = await Task.WhenAll(slow, newer);
        var sentAfter = rig.Transport.Since(before);
        scenarios.Add(new("a turn made stale by a newer message is dropped; the newer one answers the mention",
            results[0].Outcome == DiscordTextOutcome.Dropped && results[1].Outcome == DiscordTextOutcome.Answered && results[1].Addressed &&
            sentAfter is [{ ReplyTo: not null }], $"{results[0].Outcome} then {Detail(results[1], sentAfter)}"));
        return scenarios;
    }

    private static string Name(ulong author) => author switch
    {
        Owner => "Owner", Known => "Bo", OtherBot => "OtherBot", _ => "Stranger"
    };

    private static string Detail(DiscordTextResult result, Sent[] sent) =>
        $"{result.Outcome} ({result.Mode}{(result.Addressed ? ", addressed" : "")}), {sent.Length} message(s)" +
        (sent.Length > 0 ? $": {string.Join(", ", sent.Select(m => $"{m.Length} chars{(m.ReplyTo is null ? "" : ", quoted")}"))}" : "") +
        (result.Problem is { } problem ? $"; {problem}" : "");

    private static object Report(DiscordTextResult result, Rig rig, DiscordIncoming incoming) => new
    {
        outcome = result.Outcome.ToString(),
        mode = result.Mode.ToString(),
        addressed = result.Addressed,
        place = incoming.Place.GuildId is null ? "DM" : "server",
        sent = rig.Transport.Last(result.Sent.Count).Select(m => new { text = m.Text, quoted = m.ReplyTo is not null, viaCommand = m.Command }),
        recentLines = rig.Chat.Recent.Before(incoming.Place).Count,
        problem = result.Problem
    };

    private static DiscordIncoming Parse(JsonElement message, DiscordPreferences preferences, ulong id)
    {
        if (message.ValueKind != JsonValueKind.Object) throw new ArgumentException("Each message must be an object.");
        string? Text(string name) => message.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
        bool Flag(string name) => message.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;
        var text = Text("text") ?? throw new ArgumentException("Each message needs 'text'.");
        if (text.Length > 2000) throw new ArgumentException("A message's 'text' is at most 2000 characters.");
        var author = (Text("author") ?? "owner") switch
        {
            "owner" => preferences.OwnerUserId,
            "known" => preferences.People.FirstOrDefault()?.UserId ?? Known,
            "stranger" => Stranger,
            "bot" => OtherBot,
            _ => throw new ArgumentException("'author' is owner, known, stranger or bot.")
        };
        ulong channel = message.TryGetProperty("channelId", out var number) && number.ValueKind == JsonValueKind.Number &&
            number.TryGetUInt64(out var parsed) ? parsed : MentionsChannel;
        var place = (Text("place") ?? "server") switch
        {
            "dm" => new DiscordPlace(4000 + author % 1000, null, "DM", true),
            "server" => new DiscordPlace(channel, preferences.Channels.FirstOrDefault(rule => rule.ChannelId == channel)?.GuildId ??
                (preferences.HomeGuildId != 0 ? preferences.HomeGuildId : Guild), $"#{channel}", false),
            _ => throw new ArgumentException("'place' is dm or server.")
        };
        return new(id, place, new(author, author == preferences.OwnerUserId ? "Owner" : "Someone", author == preferences.OwnerUserId),
            text, Flag("mention"), Flag("replyToMartlet"), author == OtherBot);
    }

    private sealed record Sent(ulong Channel, string Text, ulong? ReplyTo, bool Command)
    {
        public int Length => Text.Length;
        public bool Mass => Text.Contains("@everyone", StringComparison.OrdinalIgnoreCase) || Text.Contains("@here", StringComparison.OrdinalIgnoreCase);
    }

    private sealed class Rig
    {
        public Rig(DiscordPreferences preferences, string reply)
        {
            Transport = new();
            var engine = new FixtureEngine(preferences, reply, this);
            Chat = new(Transport, () => engine, message => preferences.TextMode(message.Place, message.Speaker.UserId), () => ["Martlet"],
                new() { MinimumInterval = TimeSpan.FromMilliseconds(20) });
        }

        public FakeTransport Transport { get; }
        public DiscordTextChat Chat { get; }
        public TaskCompletionSource Thinking { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<DiscordTextResult> SendAsync(DiscordIncoming incoming, bool command, CancellationToken token) => command
            ? Chat.CommandAsync(incoming, pieces =>
            {
                foreach (var piece in pieces) Transport.Add(new(incoming.Place.ChannelId, piece, null, true));
                return Task.CompletedTask;
            }, token)
            : Chat.HandleAsync(incoming, token);
    }

    private sealed class FakeTransport : IDiscordTextTransport
    {
        private readonly List<Sent> sent = [];
        public int Count { get { lock (sent) return sent.Count; } }
        public void Add(Sent message) { lock (sent) sent.Add(message); }
        public Sent[] Since(int index) { lock (sent) return [.. sent.Skip(index)]; }
        public Sent[] Last(int count) { lock (sent) return [.. sent.Skip(Math.Max(0, sent.Count - count))]; }
        public IDisposable Typing(DiscordPlace place) => new Nothing();

        public Task<ulong?> SendAsync(DiscordPlace place, string text, ulong? replyTo, CancellationToken token)
        {
            Add(new(place.ChannelId, text, replyTo, false));
            return Task.FromResult<ulong?>((ulong)Count);
        }

        private sealed class Nothing : IDisposable { public void Dispose() { } }
    }

    /// <summary>Answers with a fixed text (a long one with @everyone for "long"), stays quiet on turns it may pass, and holds a
    /// "slow" turn until the stale-turn scenario releases it. Not AI.</summary>
    private sealed class FixtureEngine(DiscordPreferences preferences, string reply, Rig rig) : IDiscordReplyEngine
    {
        public async Task<DiscordReply?> ReplyAsync(DiscordTurn turn, CancellationToken token)
        {
            if (turn.Text.Contains("slow", StringComparison.OrdinalIgnoreCase))
            {
                rig.Thinking.TrySetResult();
                await rig.Release.Task.WaitAsync(token);
            }
            if (DiscordChatRules.MayPass(preferences.TextMode(turn.Place, turn.Speaker.UserId), turn.Addressed)) return null;
            if (turn.Text.Contains("long", StringComparison.OrdinalIgnoreCase))
                return new(string.Join(' ', Enumerable.Repeat("Once upon a time, a little bird sang to the whole server.", 60)) + " @everyone");
            return new(reply);
        }
    }
}
