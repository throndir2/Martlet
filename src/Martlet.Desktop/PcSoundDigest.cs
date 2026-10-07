using System.IO;
using System.Text.Json;
using Martlet.Audio;
using Martlet.Conversation;
using Martlet.Sherpa;

namespace Martlet.Desktop;

/// <summary>Companion › Listening › Describe PC sounds: while Martlet hears what this PC plays, the sound digest describes its
/// non-speech sound (music and its mood, game or video sounds, laughter, applause, alarms) in one short line about every ten
/// seconds, for the reply to read beside the transcript. The judge is an audio-capable model in the Thinking pool when there is
/// one, else the small sound tagger on this PC's processor. The last seconds of sound stay in memory only
/// (<see cref="PcSoundBuffer"/>); the line goes to the context board as source "sound" for
/// <see cref="SoundDigestOptions.MaximumAge"/>, never into the conversation's history or memory, and a reply never waits for
/// it. <see cref="StatusFile"/> keeps the state, the judge, counts and times for MCP, never a line or a sound.</summary>
internal sealed class PcSoundDigest : IDisposable
{
    internal const string StatusFile = "sound-digest.json";
    private readonly SoundDigestScheduler scheduler;
    private readonly CpuSoundJudge? cpu;
    private readonly Func<ISoundJudge?> pool;
    private readonly ContextBoard? board;
    private readonly string? dataDirectory;
    private readonly TimeProvider clock;
    private readonly object writing = new();

    /// <param name="buffer">What the PC capture keeps for the digest.</param>
    /// <param name="martletAudible">Martlet's own voice may be in what the PC plays right now.</param>
    /// <param name="pool">An audio-capable Thinking pool member's judge, or null when the pool has none.</param>
    /// <param name="board">Where each line goes for the next reply (the context board, source "sound").</param>
    /// <param name="held">The live conversation needs the pool judge's hardware now (the live floor): the digest skips its turn.</param>
    internal PcSoundDigest(PcSoundBuffer buffer, Func<bool> martletAudible, Func<ISoundJudge?>? pool = null,
        ContextBoard? board = null, string? dataDirectory = null, ISoundJudge? cpuJudge = null,
        string? appDirectory = null, SoundDigestOptions? options = null, Func<bool>? held = null)
    {
        this.pool = pool ?? (() => null);
        this.board = board;
        this.dataDirectory = dataDirectory;
        clock = buffer.Clock;
        if (cpuJudge is null && SoundTagger.Included(appDirectory)) cpu = new CpuSoundJudge(new SoundTagger(appDirectory));
        var fallback = cpuJudge ?? cpu;
        scheduler = new SoundDigestScheduler(buffer, () => this.pool() ?? fallback, martletAudible, Post, options, held);
        scheduler.Changed += WriteStatus;
    }

    /// <summary>Whether the digest runs now (the talk window turns it on while it hears this PC with Describe PC sounds on).</summary>
    internal bool On
    {
        get => scheduler.On;
        set
        {
            scheduler.On = value;
            // A line about what played before is no use once Martlet no longer hears the PC.
            if (!value) board?.Clear(ContextBoard.Sound);
        }
    }

    internal SoundDigestStatus Status => scheduler.Status;

    /// <summary>The newest line while it is still fresh enough for a reply.</summary>
    internal SoundDigestLine? Fresh => scheduler.Fresh;

    internal SoundDigestScheduler Scheduler => scheduler;

    /// <summary>Raised off the dispatcher when the status changed.</summary>
    internal event Action? Changed
    {
        add => scheduler.Changed += value;
        remove => scheduler.Changed -= value;
    }

    /// <summary>Raised off the dispatcher with each new line (after it went to the board).</summary>
    internal event Action<SoundDigestLine>? Posted;

    private void Post(SoundDigestLine line)
    {
        board?.Post(ContextBoard.Sound, Note(line.Text), clock.GetUtcNow() - clock.GetElapsedTime(line.At), scheduler.Options.MaximumAge);
        Posted?.Invoke(line);
    }

    /// <summary>The note the reply reads: what plays on the PC besides words, as the judge described it.</summary>
    internal static string Note(string line) => $"Sound playing on this PC besides speech: {line.TrimEnd('.')}.";

    /// <summary>Which judge describes the sound, for Companion: the pool model or the CPU sound tagger, or why there is none.</summary>
    internal static string JudgeText(string? judge, SoundJudgeKind? kind) => kind switch
    {
        SoundJudgeKind.Pool => $"{judge} in the Thinking pool hears a short clip.",
        SoundJudgeKind.Cpu => "The CPU sound tagger on this PC names what it hears (no Thinking pool model can hear).",
        _ => "No judge: no Thinking pool model can hear, and the CPU sound tagger is missing from Martlet's folder. Reinstall Martlet."
    };

    /// <summary>The last line and how old it is, for Companion's status (never saved).</summary>
    internal static string LastText(SoundDigestLine? last, TimeProvider clock) => last is null ? "No line yet."
        : $"Last: \"{last.Text}\" ({Age(clock.GetElapsedTime(last.At))} ago, {last.Judge}, {last.Took.TotalMilliseconds:N0} ms).";

    private static string Age(TimeSpan age) => age.TotalSeconds < 90 ? $"{Math.Max(0, (int)age.TotalSeconds)} s" : $"{(int)age.TotalMinutes} min";

    private void WriteStatus()
    {
        if (dataDirectory is null) return;
        var status = scheduler.Status;
        var lastAt = status.Last is { } last ? clock.GetUtcNow() - clock.GetElapsedTime(last.At) : (DateTimeOffset?)null;
        var json = JsonSerializer.Serialize(new
        {
            version = 1,
            on = status.On,
            judge = status.Judge,
            judgeKind = status.JudgeKind?.ToString().ToLowerInvariant(),
            runs = status.Runs,
            lines = status.Lines,
            dropped = status.Dropped,
            skipped = status.Skipped,
            lastStep = status.LastStep.ToString(),
            lastAt,
            lastMs = status.LastTook is { } took ? (int?)Math.Round(took.TotalMilliseconds) : null,
            lastJudge = status.Last?.Judge,
            maximumAgeSeconds = (int)scheduler.Options.MaximumAge.TotalSeconds,
            everySeconds = (int)scheduler.Options.Every.TotalSeconds,
            clipSeconds = (int)scheduler.Options.Clip.TotalSeconds
        });
        lock (writing)
        {
            try
            {
                Directory.CreateDirectory(dataDirectory);
                var path = Path.Combine(dataDirectory, StatusFile);
                var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
                File.WriteAllText(temporary, json);
                File.Move(temporary, path, overwrite: true);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }
    }

    public void Dispose()
    {
        scheduler.Dispose();
        cpu?.Dispose();
    }
}
