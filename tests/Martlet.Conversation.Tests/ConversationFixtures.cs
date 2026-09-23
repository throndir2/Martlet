using System.Collections.Concurrent;
using System.Text;
using Martlet.Audio;
using Martlet.Audio.Tests;
using Martlet.Core.Contracts;
using Martlet.Providers;
using Martlet.Providers.Tests;

namespace Martlet.Conversation.Tests;

internal sealed class RuntimeClock : TimeProvider
{
    private readonly object sync = new();
    private readonly List<ClockTimer> timers = [];
    private long ticks, utcOffset;
    internal bool DeferCallbacks { get; set; }
    public override long TimestampFrequency => TimeSpan.TicksPerSecond;
    public override long GetTimestamp() { lock (sync) return ticks; }
    public override DateTimeOffset GetUtcNow() { lock (sync) return ProviderFixtures.Now.AddTicks(ticks + utcOffset); }
    internal void ShiftUtc(TimeSpan by) { lock (sync) utcOffset += by.Ticks; }
    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        lock (sync)
        {
            var timer = new ClockTimer(this, callback, state);
            timers.Add(timer);
            timer.Change(dueTime, period);
            return timer;
        }
    }
    internal void Advance(TimeSpan by)
    {
        List<ClockTimer> ready;
        lock (sync)
        {
            ticks += by.Ticks;
            ready = DeferCallbacks ? [] : timers.Where(x => x.Due <= ticks).ToList();
            foreach (var timer in ready) timer.Due = timer.Period > 0 ? ticks + timer.Period : long.MaxValue;
        }
        foreach (var timer in ready) timer.Fire();
    }
    private sealed class ClockTimer(RuntimeClock owner, TimerCallback callback, object? state) : ITimer
    {
        internal long Due { get; set; }
        internal long Period { get; private set; }
        private bool disposed;
        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            lock (owner.sync)
            {
                if (disposed) return false;
                Due = dueTime == Timeout.InfiniteTimeSpan ? long.MaxValue : owner.ticks + dueTime.Ticks;
                Period = period == Timeout.InfiniteTimeSpan ? 0 : period.Ticks;
                return true;
            }
        }
        internal void Fire()
        {
            lock (owner.sync) if (disposed) return;
            callback(state);
        }
        public void Dispose() { lock (owner.sync) { disposed = true; owner.timers.Remove(this); } }
        public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
    }
}

internal sealed class FixturePermissions(RuntimeClock clock) : IConversationAuthorizationSource
{
    internal ConcurrentQueue<TextAuthorizationAction> TextActions { get; } = new();
    internal ConcurrentQueue<SpeechAuthorizationAction> SpeechActions { get; } = new();
    internal Func<TextAuthorizationAction, CancellationToken, ValueTask<AuthorizedTextOperation?>>? Text { get; set; }
    internal Func<SpeechAuthorizationAction, CancellationToken, ValueTask<AuthorizedSpeechOperation?>>? Speech { get; set; }

    internal AuthorizedTextOperation Allow(TextAuthorizationAction action, DateTimeOffset? expiry = null)
    {
        var until = expiry ?? Min(action.Context.Deadline, clock.GetUtcNow().AddSeconds(30));
        return new(new(TextFixtures.Binding, action.Model, action.Context.Ids, action.Context.Epoch,
            action.Limits, until, true, true), new(action.Budget, until));
    }
    internal AuthorizedSpeechOperation Allow(SpeechAuthorizationAction action, DateTimeOffset? expiry = null)
    {
        var until = expiry ?? Min(action.Context.Deadline, clock.GetUtcNow().AddSeconds(30));
        return new(new(SpeechFixtures.Binding, action.Selection, action.Input, action.Context.Ids, action.Context.Epoch,
            action.Limits, until, true, true, true), new(action.Budget, until));
    }
    public ValueTask<AuthorizedTextOperation?> AuthorizeTextAsync(TextAuthorizationAction action, CancellationToken token)
    {
        TextActions.Enqueue(action);
        return Text is null ? ValueTask.FromResult<AuthorizedTextOperation?>(Allow(action)) : Text(action, token);
    }
    public ValueTask<AuthorizedSpeechOperation?> AuthorizeSpeechAsync(SpeechAuthorizationAction action, CancellationToken token)
    {
        SpeechActions.Enqueue(action);
        return Speech is null ? ValueTask.FromResult<AuthorizedSpeechOperation?>(Allow(action)) : Speech(action, token);
    }
    private static DateTimeOffset Min(DateTimeOffset a, DateTimeOffset b) => a < b ? a : b;
}

internal sealed class Harness : IAsyncDisposable
{
    internal RuntimeClock Clock { get; } = new();
    internal FixtureCredentials Credentials { get; } = new();
    internal TextRecordingHandler Llm { get; } = new();
    internal TextRecordingHandler Tts { get; } = SpeechFixtures.Handler();
    internal ControlledDevice Device { get; }
    internal FixturePermissions Permissions { get; }
    internal ConversationRuntime Runtime { get; }
    internal Harness(ControlledDevice? device = null, PlaybackOptions? playback = null, bool textOnly = false,
        GeneratedSpeechObserver? generatedSpeech = null)
    {
        Device = device ?? new();
        Permissions = new(Clock);
        Runtime = ConversationRuntime.ForFixture(OpenAiTextGenerationAdapter.CreateForFixture(Llm, Credentials, Clock),
            textOnly ? null : OpenAiSpeechSynthesisAdapter.CreateForFixture(Tts, Credentials, Clock),
            textOnly ? null : Device, playback ?? new(), Clock, generatedSpeech);
    }
    internal static SpeechSynthesisLimits SpeechLimits => new()
    {
        MaxAudioBytes = 9_600, MaxAudioDuration = TimeSpan.FromMilliseconds(200),
        MaxRequestTime = TimeSpan.FromSeconds(30)
    };
    internal static ConversationRequest Request(bool speech = true, ConversationLimits? limits = null,
        TextGenerationLimits? textLimits = null, SpeechSynthesisLimits? speechLimits = null) => new(
        new BoundedTextInput("An explicit typed fixture input."), TextFixtures.Selection, textLimits ?? new(),
        limits ?? new(), speech ? new(SpeechFixtures.Selection, new(OutputPolicy.DefaultAtStart), speechLimits ?? SpeechLimits) : null);
    internal void Answer(params string[] deltas) =>
        Llm.Respond = (_, _) => Task.FromResult(TextRecordingHandler.Sse(Trace(deltas)));
    internal ConversationTurn Start(ConversationRequest? request = null, CancellationToken token = default) =>
        Runtime.Start(request ?? Request(), Permissions, token);
    internal static string Trace(params string[] deltas)
    {
        string text = string.Concat(deltas);
        var trace = TextFixtures.Trace(text);
        var result = new StringBuilder(string.Concat(trace.Take(4)));
        int sequence = 4;
        foreach (var delta in deltas)
            result.Append(TextFixtures.Event("response.output_text.delta", sequence++,
                new { output_index = 0, content_index = 0, item_id = "msg_fixture", delta }));
        result.Append(TextFixtures.Event("response.output_text.done", sequence++,
            new { output_index = 0, content_index = 0, item_id = "msg_fixture", text }));
        result.Append(TextFixtures.Event("response.content_part.done", sequence++,
            new { output_index = 0, content_index = 0, item_id = "msg_fixture", part = TextFixtures.Part(text) }));
        result.Append(TextFixtures.Event("response.output_item.done", sequence++,
            new { output_index = 0, item = TextFixtures.Item(text) }));
        result.Append(TextFixtures.Event("response.completed", sequence,
            new { response = TextFixtures.Response(text, "completed") }));
        return result.ToString();
    }
    internal static async Task Until(Func<bool> condition, RuntimeClock? clock = null)
    {
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        while (!condition())
        {
            await Task.Delay(1, guard.Token);
            clock?.Advance(TimeSpan.FromMilliseconds(5));
        }
    }
    internal static async Task<ConversationSnapshot> Finish(ConversationTurn turn, RuntimeClock? clock = null)
    {
        await Until(() => turn.Completion.IsCompleted, clock);
        return await turn.Completion;
    }
    public async ValueTask DisposeAsync()
    {
        Device.Release.Set();
        var disposal = Runtime.DisposeAsync().AsTask();
        await Until(() => disposal.IsCompleted, Clock);
        await disposal;
    }
}
