using System.Globalization;
using System.IO;
using System.Text.Json;
using Martlet.Conversation;
using Martlet.Core.Contracts;
using Martlet.Core.Lorebooks;
using Martlet.Core.Settings;
using Martlet.Discord;
using Martlet.Providers;

namespace Martlet.Desktop;

/// <summary>Answers Discord turns as Martlet's character: the Discord side (history per place, the ambient gate, one request per
/// place and two at once, [pass] and Discord's limits) is <see cref="DiscordReplier"/>; this is the Thinking side. Each turn
/// builds its request with the live conversation's own <see cref="LiveConversationConfiguration.Request"/> (the persona, its
/// style, lore, memory, the notes prompt and the reply length prompt), adding only the Discord framing after the persona and
/// a note on ambient turns. Nothing of the local conversation changes: its requests, history and caches are its own.
/// <para>Discord replies run on their own text-only runtimes (two lanes), never the local conversation's. They never carry a
/// picture, a recording, tools, Home Assistant or past conversations, and remembered facts only go to a direct message with the
/// owner. When Thinking runs on this PC or the home network (Ollama, a paired host, a LAN server), the model and its prompt
/// cache are shared with the local conversation, so the local conversation always wins: Discord requests there go one at a
/// time, only after the local conversation has been quiet for <see cref="LocalQuiet"/> (an ambient turn is skipped instead
/// of waiting, an addressed one waits up to <see cref="LocalWait"/>), and one still running when the local conversation starts
/// a reply is stopped at once (an addressed turn is asked again once it is quiet). A cloud route is used as it is.</para></summary>
internal sealed class DiscordReplyEngine : IDiscordReplyEngine, IAsyncDisposable
{
    /// <summary>The engine's status in the data directory (counts, times, codes; never what was said or who said it), which
    /// MCP's discord_reply_status reads.</summary>
    internal const string StatusFile = "discord-replies.json";
    internal static TimeSpan LocalQuiet => TimeSpan.FromSeconds(20);
    internal static TimeSpan LocalWait => TimeSpan.FromSeconds(90);
    private static TimeSpan Poll => TimeSpan.FromMilliseconds(250);
    // A merged run of lines is kept within one message's bound (BoundedTextInput.HardMaxUtf8Bytes characters).
    private const int MessageCharacters = 12_000;

    private readonly ISetupService settings;
    private readonly ICredentialStore vault;
    private readonly string? dataDirectory;
    private readonly DesktopMemoryService? memory;
    private readonly LorebookStore? lorebooks;
    private readonly Func<bool> localBusy;
    private readonly TimeProvider clock;
    private readonly Func<IProviderCredentialSource, TimeProvider, ConversationRuntime>? runtimeFactory;
    private readonly Lane[] lanes;
    private readonly SemaphoreSlim laneGate;
    private readonly SemaphoreSlim localGate = new(1, 1);
    private readonly ITimer watch;
    private readonly Lock gate = new();
    private readonly DateTimeOffset startedAt;
    // When the local conversation was last seen replying (controller clock), or long.MinValue for never.
    private long lastBusyAt = long.MinValue;
    private string[] names = ["Martlet"];
    private (string RouteType, string Model, bool Network)? route;
    private long? firstWordsMs, inputTokens, cachedTokens;
    private int waiting, preempted;
    private bool disposed;

    internal DiscordReplyEngine(ISetupService settings, ICredentialStore vault, string? dataDirectory, Func<bool> localBusy,
        DesktopMemoryService? memory = null, LorebookStore? lorebooks = null, TimeProvider? clock = null,
        Func<IProviderCredentialSource, TimeProvider, ConversationRuntime>? runtimeFactory = null,
        DiscordReplyOptions? options = null, Func<double>? random = null)
    {
        this.settings = settings;
        this.vault = vault;
        this.dataDirectory = dataDirectory;
        this.localBusy = localBusy;
        this.memory = memory;
        this.lorebooks = lorebooks;
        this.clock = clock ?? TimeProvider.System;
        this.runtimeFactory = runtimeFactory;
        options ??= new();
        lanes = [.. Enumerable.Range(0, Math.Max(1, options.MaxConcurrent)).Select(_ => new Lane())];
        laneGate = new(lanes.Length, lanes.Length);
        Replier = new(ThinkAsync, () => Volatile.Read(ref names), options, this.clock, random);
        Replier.Changed += _ => WriteStatus();
        startedAt = this.clock.GetUtcNow();
        // Notes when the local conversation was last busy, so Discord waits for it to be quiet on a model it shares.
        watch = this.clock.CreateTimer(_ =>
        {
            // A timer callback that throws would end Martlet: a failed check only means nothing is noted this time.
            try
            {
                if (Busy()) Interlocked.Exchange(ref lastBusyAt, this.clock.GetTimestamp());
            }
            catch (Exception) { }
        }, null, Poll, Poll);
        WriteStatus();
    }

    /// <summary>The Discord side: history, the ambient gate and the counts.</summary>
    internal DiscordReplier Replier { get; }

    public Task<DiscordReply?> ReplyAsync(DiscordTurn turn, CancellationToken token) => Replier.ReplyAsync(turn, token);

    private bool Busy()
    {
        try { return localBusy(); }
        catch (ObjectDisposedException) { return false; }
    }

    /// <summary>The local conversation isn't replying and hasn't for <see cref="LocalQuiet"/>.</summary>
    internal bool LocalIsQuiet
    {
        get
        {
            if (Busy())
            {
                Interlocked.Exchange(ref lastBusyAt, clock.GetTimestamp());
                return false;
            }
            var last = Interlocked.Read(ref lastBusyAt);
            return last == long.MinValue || clock.GetElapsedTime(last) >= LocalQuiet;
        }
    }

    private async Task<string?> ThinkAsync(DiscordTurnContext context, CancellationToken token)
    {
        var loaded = await settings.LoadAsync(token).ConfigureAwait(false);
        if (LiveConversationConfiguration.From(loaded, ModelLimits.Load(dataDirectory), ModelAbilities.Load(dataDirectory)) is not { } configured)
            throw new DiscordReplyException("thinking.not_set_up");
        if (configured.Unavailable(voice: false, microphone: false) is not null) throw new DiscordReplyException("thinking.unavailable");
        var thinking = configured.Route(SetupRole.Llm);
        var network = configured.NetworkThinking;
        lock (gate) route = (thinking.RouteType?.ToString() ?? "OpenAi", thinking.ModelId, network);
        if (configured.Persona?.Name is { Length: > 0 } persona && !persona.Equals("Martlet", StringComparison.OrdinalIgnoreCase))
            Volatile.Write(ref names, [persona, "Martlet"]);
        var prompt = DiscordPrompts.Shape(context);
        if (!network) return await AskAsync(configured, context, prompt, local: false, token).ConfigureAwait(false);
        for (var attempt = 0; ; attempt++)
        {
            await WaitForQuietAsync(context.Turn.MayPass, token).ConfigureAwait(false);
            try
            {
                try { return await AskAsync(configured, context, prompt, local: true, token).ConfigureAwait(false); }
                finally { localGate.Release(); }
            }
            catch (DiscordReplyException error) when (error.Code == "local.preempted" && !context.Turn.MayPass && attempt == 0)
            {
                // The local conversation started a reply: this turn waits for it to be quiet again and asks once more.
            }
        }
    }

    // Takes the local model for Discord: one Discord request at a time, once the local conversation has been quiet. Returns
    // holding localGate.
    private async Task WaitForQuietAsync(bool ambient, CancellationToken token)
    {
        if (ambient)
        {
            if (!LocalIsQuiet || !await localGate.WaitAsync(0, token).ConfigureAwait(false))
                throw new DiscordReplyException("local.busy", skipped: true);
            if (LocalIsQuiet) return;
            localGate.Release();
            throw new DiscordReplyException("local.busy", skipped: true);
        }
        var started = clock.GetTimestamp();
        lock (gate) waiting++;
        WriteStatus();
        try
        {
            while (true)
            {
                var left = LocalWait - clock.GetElapsedTime(started);
                if (left <= TimeSpan.Zero) throw new DiscordReplyException("local.busy", skipped: true);
                if (LocalIsQuiet && await localGate.WaitAsync(0, token).ConfigureAwait(false))
                {
                    if (LocalIsQuiet) return;
                    localGate.Release();
                }
                await Task.Delay(Poll, clock, token).ConfigureAwait(false);
            }
        }
        finally
        {
            lock (gate) waiting--;
            WriteStatus();
        }
    }

    private async Task<string?> AskAsync(LiveConversationConfiguration configured, DiscordTurnContext context, DiscordPrompt prompt,
        bool local, CancellationToken token)
    {
        var turn = context.Turn;
        var persona = configured.Persona;
        var history = prompt.History.Select(message => new TextHistoryMessage(
            message.FromMartlet ? TextHistoryRole.Assistant : TextHistoryRole.User, Bound(message.Text))).ToArray();
        var recalled = await RecallAsync(configured, turn, token).ConfigureAwait(false);
        var lore = await ScanLoreAsync(prompt, persona, token).ConfigureAwait(false);
        ConversationRequest request;
        try
        {
            request = configured.Request(new BoundedTextInput(Bound(prompt.Message)), voice: false, history, recalled, lore,
                out _, out _, out _, extraInstructions: prompt.Instructions, messageNotes: prompt.Note,
                closingInstructions: configured.ReplyLength);
        }
        catch (LiveActionException error) { throw new DiscordReplyException(error.Code); }
        catch (ContractException) { throw new DiscordReplyException("conversation.input_limit"); }

        await laneGate.WaitAsync(token).ConfigureAwait(false);
        Lane lane;
        lock (gate)
        {
            lane = lanes.First(free => !free.Busy);
            lane.Busy = true;
        }
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(token);
        var preempt = local ? WatchLocalAsync(stop) : Task.CompletedTask;
        try
        {
            var authorization = new ConversationAuthorization(configured, voice: false, microphone: false, clock,
                () => !stop.IsCancellationRequested, settings.LoadAsync, vault, stop.Token);
            authorization.BindInput(request.Input, request.Limits.MaxToolRounds);
            lane.Authorization = authorization;
            var run = Runtime(lane).Start(request, authorization, stop.Token);
            var terminal = await run.Completion.ConfigureAwait(false);
            await run.OwnershipRelease.ConfigureAwait(false);
            if (run.Snapshot.Quarantined) lane.Retire();
            token.ThrowIfCancellationRequested();
            if (stop.IsCancellationRequested) throw new DiscordReplyException("local.preempted", skipped: true);
            Note(turn, terminal);
            var text = run.Content.Text;
            if (terminal.State == ConversationState.Completed) return text;
            // A refusal or an empty answer has nothing to add; a reply cut off by the length limit keeps what it said.
            if (terminal.State == ConversationState.Refused || string.IsNullOrWhiteSpace(text) &&
                terminal.ProviderFailure is null or ProviderFailureCode.ResponseSchema) return null;
            if (terminal.ProviderFailure == ProviderFailureCode.OutputTokenLimit && !string.IsNullOrWhiteSpace(text)) return text;
            var code = terminal.ProviderFailure?.ToString() ?? "runtime." + terminal.State;
            ErrorLog.Warn($"Discord reply failed (state {terminal.State}, failure {terminal.Failure}" +
                (terminal.ProviderFailure is { } provider ? $", provider {provider}" : "") +
                $"). Thinking route: {RouteName(configured)}.");
            throw new DiscordReplyException(code);
        }
        finally
        {
            stop.Cancel();
            await preempt.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            lane.Authorization = null;
            lock (gate) lane.Busy = false;
            laneGate.Release();
        }

        static string RouteName(LiveConversationConfiguration configured)
        {
            var route = configured.Route(SetupRole.Llm);
            return $"{route.RouteType?.ToString() ?? "OpenAi"}, {route.Origin ?? "no destination"}, model {route.ModelId}";
        }
    }

    // While a Discord request uses the model the local conversation uses, a local reply starting stops it at once.
    private async Task WatchLocalAsync(CancellationTokenSource stop)
    {
        try
        {
            while (!stop.IsCancellationRequested)
            {
                if (Busy())
                {
                    Interlocked.Exchange(ref lastBusyAt, clock.GetTimestamp());
                    Interlocked.Increment(ref preempted);
                    ErrorLog.Info("Discord reply stopped: the local conversation started a reply on the model it shares.");
                    await stop.CancelAsync().ConfigureAwait(false);
                    return;
                }
                await Task.Delay(TimeSpan.FromMilliseconds(100), clock, stop.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { }
    }

    // Remembered facts are the owner's: they only go to a direct message with the owner (other people would read them), and
    // never while the local conversation is replying, so its own recall never waits for Discord's.
    private async Task<DesktopMemoryRecall?> RecallAsync(LiveConversationConfiguration configured, DiscordTurn turn, CancellationToken token)
    {
        if (memory is null || configured.Memory is not { Enabled: true } settings || !turn.Speaker.IsOwner || !turn.Place.Direct ||
            turn.Source != DiscordTurnSource.Text || Busy())
            return null;
        try { return await memory.RecallAsync(settings, turn.Text, DesktopMemoryService.MaximumRecalledFacts, null, token).ConfigureAwait(false); }
        catch (Exception error) when (error is DesktopMemoryException or Martlet.Memory.MemoryException or ContractException or
            IOException or UnauthorizedAccessException or ObjectDisposedException)
        {
            return null;
        }
    }

    // Lorebooks help but are never required, as in the local conversation.
    private async Task<LorebookScanResult?> ScanLoreAsync(DiscordPrompt prompt, PersonaProfile? persona, CancellationToken token)
    {
        if (lorebooks is null) return null;
        try
        {
            var loaded = await lorebooks.LoadAsync(token).ConfigureAwait(false);
            if (!loaded.Loaded) return null;
            var result = LorebookScanner.Scan(loaded.Library,
                new(prompt.Message, prompt.History.Select(message => message.Text).ToArray(), persona?.Id, persona?.Name), Random.Shared.Next);
            return result.Included.Count == 0 && result.OverBudget.Count == 0 ? null : result;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or ContractException)
        {
            return null;
        }
    }

    private static string Bound(string text) => text.Length <= MessageCharacters ? text : "…" + text[^(MessageCharacters - 1)..];

    // One local log line per Discord reply: how soon and how much the model read from its prompt cache (never what was said).
    private void Note(DiscordTurn turn, ConversationSnapshot terminal)
    {
        lock (gate)
        {
            firstWordsMs = terminal.FirstTextAfter is { } after ? (long)after.TotalMilliseconds : null;
            inputTokens = terminal.InputTokens;
            cachedTokens = terminal.CachedInputTokens;
        }
        var where = turn.Source == DiscordTurnSource.Voice ? "voice call" : turn.Place.Direct ? "direct message" : "server channel";
        var first = terminal.FirstTextAfter is { } words ? $"first words after {words.TotalMilliseconds:0} ms" : "no words";
        var input = terminal.InputTokens is not { } read ? "the model didn't say how many input tokens it read"
            : terminal.CachedInputTokens is { } cached
                ? string.Create(CultureInfo.InvariantCulture, $"{read:N0} input tokens, {cached:N0} of them from the model's prompt cache")
                : string.Create(CultureInfo.InvariantCulture, $"{read:N0} input tokens");
        ErrorLog.Info($"Discord reply ({where}{(turn.Addressed ? "" : ", unprompted")}): {first}; {input}.");
    }

    private ConversationRuntime Runtime(Lane lane)
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            return lane.Runtime ??= runtimeFactory?.Invoke(lane.Credentials, clock) ??
                ConversationRuntime.Create(lane.Credentials, clock: clock, hostText: new HostTextClient());
        }
    }

    /// <summary>Writes <see cref="StatusFile"/>: whether the engine is wired, its counts and last problem, the Thinking route it
    /// last used (route type and model ID) and how it treats a model shared with the local conversation.</summary>
    private void WriteStatus()
    {
        if (dataDirectory is null) return;
        var stats = Replier.Stats;
        object status;
        lock (gate)
            status = new
            {
                wired = true,
                startedAt,
                updatedAt = clock.GetUtcNow(),
                stats.Replies, stats.Passes, stats.Skipped, stats.Failures, stats.Places, stats.Running,
                stats.LastReplyAt, stats.LastPassAt, stats.LastLatencyMs, lastFirstWordsMs = firstWordsMs,
                lastInputTokens = inputTokens, lastCachedTokens = cachedTokens, stats.LastSkip, stats.LastError, stats.LastErrorAt,
                waitingForLocal = waiting, preemptedByLocal = Volatile.Read(ref preempted),
                route = route is { } used ? new { routeType = used.RouteType, model = used.Model, onYourNetwork = used.Network } : null,
                localStrategy = $"Thinking on this PC or your network: one Discord request at a time, only after the local " +
                    $"conversation has been quiet for {LocalQuiet.TotalSeconds:0} s (ambient turns skip, addressed ones wait up to " +
                    $"{LocalWait.TotalSeconds:0} s); a local reply starting stops a running Discord request.",
                lanes = lanes.Length
            };
        lock (lanes)
        {
            try
            {
                Directory.CreateDirectory(dataDirectory);
                var path = Path.Combine(dataDirectory, StatusFile);
                var temporary = path + ".tmp";
                File.WriteAllText(temporary, JsonSerializer.Serialize(status, StatusJson));
                File.Move(temporary, path, overwrite: true);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }
    }

    private static readonly JsonSerializerOptions StatusJson = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public async ValueTask DisposeAsync()
    {
        ConversationRuntime?[] owned;
        lock (gate)
        {
            if (disposed) return;
            disposed = true;
            owned = [.. lanes.Select(lane => lane.Runtime)];
        }
        await watch.DisposeAsync().ConfigureAwait(false);
        foreach (var runtime in owned)
            if (runtime is not null) await runtime.DisposeAsync().ConfigureAwait(false);
    }

    public override string ToString() => nameof(DiscordReplyEngine);

    // One text-only runtime with its own credentials, bound to the one request it runs.
    private sealed class Lane
    {
        private ConversationAuthorization? authorization;
        internal Lane() => Credentials = new(() => Volatile.Read(ref authorization));
        internal ConversationCredentialSource Credentials { get; }
        internal ConversationAuthorization? Authorization { set => Volatile.Write(ref authorization, value); }
        internal ConversationRuntime? Runtime { get; set; }
        internal bool Busy { get; set; }
        // A runtime whose turn was quarantined takes no more turns; the next request gets a new one.
        internal void Retire() => Runtime = null;
    }
}
