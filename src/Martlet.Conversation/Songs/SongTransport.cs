using System.Globalization;
using Martlet.Core.Singing;

namespace Martlet.Conversation;

/// <summary>A song's map for playback: its length, tempo, beat grid and sung lines (with sections and vocal onsets). Without a
/// measured grid it uses a straight one (the tempo, or 120 BPM).</summary>
public sealed class SongMap
{
    public SongMap(TimeSpan duration, double? bpm, int beatsPerBar, IReadOnlyList<TimeSpan> beats, IReadOnlyList<TimeSpan> downbeats,
        IReadOnlyList<SongLyricLine> lines)
    {
        Duration = duration;
        Bpm = bpm;
        BeatsPerBar = beatsPerBar is >= 2 and <= 12 ? beatsPerBar : 4;
        Lines = [.. lines.OrderBy(line => line.Start)];
        var grid = beats.Where(beat => beat >= TimeSpan.Zero && beat <= duration).OrderBy(beat => beat).ToArray();
        if (grid.Length < 2)
        {
            var step = TimeSpan.FromSeconds(60.0 / (bpm is >= SongRequest.MinimumBpm and <= SongRequest.MaximumBpm ? bpm.Value : 120));
            var built = new List<TimeSpan>();
            for (var at = grid.Length == 1 ? grid[0] : TimeSpan.Zero; at <= duration; at += step) built.Add(at);
            grid = [.. built];
        }
        Beats = grid;
        var deltas = grid.Zip(grid.Skip(1), (a, b) => (b - a).TotalSeconds).Where(d => d > 0.05).Order().ToArray();
        Beat = TimeSpan.FromSeconds(deltas.Length == 0 ? 0.5 : deltas[deltas.Length / 2]);
        var bars = downbeats.Where(beat => beat >= TimeSpan.Zero && beat <= duration).OrderBy(beat => beat).ToArray();
        Downbeats = bars.Length > 0 ? bars : [.. grid.Where((_, index) => index % BeatsPerBar == 0)];
    }

    public static SongMap Of(SongResult result) =>
        new(result.Duration, result.Bpm, result.BeatsPerBar, result.Beats, result.Downbeats, result.LyricTimestamps);

    public TimeSpan Duration { get; }
    public double? Bpm { get; }
    public int BeatsPerBar { get; }
    public IReadOnlyList<TimeSpan> Beats { get; }
    public IReadOnlyList<TimeSpan> Downbeats { get; }
    public IReadOnlyList<SongLyricLine> Lines { get; }
    /// <summary>One beat (the grid's median spacing).</summary>
    public TimeSpan Beat { get; }
    public TimeSpan Bar => Beat * BeatsPerBar;

    /// <summary>The line being sung at <paramref name="time"/> (0-based), or null in an instrumental stretch: the last line that
    /// started by then, while it lasts (until its end plus a moment, or the next line).</summary>
    public int? LineAt(TimeSpan time)
    {
        var index = -1;
        for (var i = 0; i < Lines.Count && Lines[i].Start <= time + TimeSpan.FromMilliseconds(50); i++) index = i;
        if (index < 0) return null;
        var line = Lines[index];
        var next = index + 1 < Lines.Count ? Lines[index + 1].Start : Duration;
        var end = line.End is { } known ? Min(known + TimeSpan.FromMilliseconds(300), next) : next;
        return time < end ? index : null;
    }

    /// <summary>The first line that starts at or after <paramref name="time"/>, or null.</summary>
    public int? NextLine(TimeSpan time)
    {
        for (var i = 0; i < Lines.Count; i++) if (Lines[i].Start >= time) return i;
        return null;
    }

    /// <summary>The latest downbeat at or before <paramref name="time"/> (the top of the song when none is).</summary>
    public TimeSpan DownbeatAtOrBefore(TimeSpan time)
    {
        var found = TimeSpan.Zero;
        foreach (var downbeat in Downbeats)
        {
            if (downbeat > time) break;
            found = downbeat;
        }
        return found;
    }

    /// <summary>The first beat at or after <paramref name="time"/>, or null after the last.</summary>
    public TimeSpan? BeatAtOrAfter(TimeSpan time)
    {
        foreach (var beat in Beats) if (beat >= time) return beat;
        return null;
    }

    /// <summary>The sections in order of their first line ("verse", "chorus", "verse 2"...).</summary>
    public IReadOnlyList<string> Sections => [.. Lines.Select(line => line.Section).Where(section => section.Length > 0).Distinct(StringComparer.Ordinal)];

    /// <summary>Where <paramref name="time"/> is, in words: "verse line 4 of 12, 0:22 of 1:00".</summary>
    public string Describe(TimeSpan time)
    {
        var clock = $"{SongClock.Of(time)} of {SongClock.Of(Duration)}";
        return LineAt(time) is { } index
            ? $"{(Lines[index].Section.Length > 0 ? Lines[index].Section + " " : "")}line {index + 1} of {Lines.Count}, {clock}"
            : time < (Lines.Count > 0 ? Lines[0].Start : Duration) ? $"the intro, {clock}" : $"between lines, {clock}";
    }

    private static TimeSpan Min(TimeSpan a, TimeSpan b) => a < b ? a : b;

    public override string ToString() => $"{nameof(SongMap)} ({Duration.TotalSeconds:0.0} s, {Lines.Count} lines)";
}

/// <summary>What stopped a song: the user's words (a stop phrase heard while it played), a button (Stop, Esc or the talk
/// button), Martlet's own stop_singing, or the song ending.</summary>
public enum SongStopCause { UserWords, Button, Martlet, Ended, Replaced, Failed }

/// <summary>Where a song stopped and why: its song time, the line being sung (0-based, with its section and words; null in
/// an instrumental stretch), the next line (where a resume from a gap goes) and what stopped it (<see cref="Words"/> holds
/// the user's words for a stop phrase, never logged).</summary>
public sealed record SongStopRecord(string SongId, string Title, TimeSpan At, TimeSpan Duration, int? Line, string? Section,
    string? Text, int? NextLine, int Lines, SongStopCause Cause, string? Words = null, string? Reason = null)
{
    /// <summary>The song played to its end (or stopped after its last line): a resume starts from the top.</summary>
    public bool Ended => Cause == SongStopCause.Ended || Line is null && NextLine is null;

    /// <summary>Martlet's note about the stop, for the end of the conversation (never rewriting earlier messages).</summary>
    public string Note()
    {
        var title = $"\"{Title}\" ({SongId})";
        if (Cause == SongStopCause.Ended) return $"You finished singing {title}. play_song with from=start sings it again.";
        var where = Line is { } line
            ? $"at {SongClock.Of(At)}, in {(Section is { Length: > 0 } ? Section + " " : "")}line {line + 1} of {Lines}, \"{Text}\""
            : $"at {SongClock.Of(At)}, {(NextLine is { } next ? $"before line {next + 1} of {Lines}" : "after the last line")}";
        var why = Cause switch
        {
            SongStopCause.UserWords => Words is { Length: > 0 } words ? $"because the user said: \"{words}\"" : "because the user asked you to",
            SongStopCause.Button => $"because the user pressed {Reason ?? "Stop"}",
            SongStopCause.Martlet => Reason is { Length: > 0 } reason ? $"because you called stop_singing ({reason})" : "because you called stop_singing",
            SongStopCause.Replaced => "because you started another song",
            _ => "because playback failed on this PC"
        };
        var resume = Ended ? "play_song with from=resume starts it from the top."
            : Line is not null
                ? $"play_song with from=resume restarts that line{(Section is { Length: > 0 } ? $" (from=\"resume section\" restarts the {Section})" : "")}."
                : "play_song with from=resume picks up at the next line.";
        return $"You stopped singing {title} {where} {why}. {resume}";
    }
}

/// <summary>Where play_song starts: the very top, or a sung line (with the section it is in), or a moment of the song.</summary>
public sealed record SongTarget(int? Line, TimeSpan? Time, string Describe, bool Top = false);

/// <summary>How a song starts. From the very top it plays as it is (<see cref="Top"/>). Anywhere else the backing enters on a
/// downbeat (<see cref="Entry"/>) <see cref="LeadInBars"/> bar(s) before the target with an equal-power fade-in over the first
/// bar (<see cref="FadeIn"/>), and the vocals stay muted until just before the line's onset (<see cref="VocalsFrom"/>). While
/// Martlet is still talking at <see cref="CommitAt"/>, the band vamps on the bar [<see cref="VampStart"/>,
/// <see cref="VampEnd"/>) (at most <see cref="SongMixer.MaximumVamps"/> times) instead of singing over it.</summary>
public sealed record SongStartPlan(SongTarget Target, bool Top, TimeSpan Entry, TimeSpan FadeIn, int LeadInBars, TimeSpan VocalsFrom,
    TimeSpan Onset, TimeSpan VampStart, TimeSpan VampEnd, TimeSpan CommitAt)
{
    /// <summary>How long before the vocals the backing starts.</summary>
    public TimeSpan LeadIn => Onset - Entry;
}

/// <summary>How a song stops: the vocals end at <see cref="VocalsEnd"/> with a <see cref="VocalsFade"/> fade (a musical stop
/// finishes the current word: the next energy dip within 0.6 s, then 150 ms), the backing fades over <see cref="BackingFade"/>
/// from <see cref="BackingFrom"/> (a musical stop rings to the next beat and fades over one beat; Stop and Esc fade both in
/// about 300 ms), and the song is silent at <see cref="SilentAt"/>. <see cref="Requested"/> is the song time the stop was asked
/// at (what was being heard).</summary>
public sealed record SongStopPlan(bool Musical, TimeSpan Requested, TimeSpan VocalsEnd, TimeSpan VocalsFade, TimeSpan BackingFrom,
    TimeSpan BackingFade)
{
    public TimeSpan SilentAt => Max(VocalsEnd + VocalsFade, BackingFrom + BackingFade);
    public TimeSpan AfterRequest => SilentAt - Requested;
    private static TimeSpan Max(TimeSpan a, TimeSpan b) => a > b ? a : b;
}

/// <summary>The vocals' loudness in 10 ms steps, for musical stops (the next dip after a word) and the character's mouth.</summary>
public sealed class VocalEnvelope
{
    public const int StepMilliseconds = 10;
    private readonly float[] rms;

    public VocalEnvelope(short[] vocals, int sampleRate)
    {
        ArgumentNullException.ThrowIfNull(vocals);
        var step = Math.Max(1, sampleRate * StepMilliseconds / 1000);
        rms = new float[(vocals.Length + step - 1) / step];
        for (var i = 0; i < rms.Length; i++)
        {
            double sum = 0;
            var end = Math.Min(vocals.Length, (i + 1) * step);
            for (var n = i * step; n < end; n++) sum += (double)vocals[n] * vocals[n];
            rms[i] = (float)Math.Sqrt(sum / Math.Max(1, end - i * step)) / 32768f;
        }
        var sorted = rms.Order().ToArray();
        var loudest = sorted.Length == 0 ? 0 : sorted[^1];
        var quiet = sorted.Length == 0 ? 0 : sorted[(int)(sorted.Length * 0.3)];
        // 8% of the loudest, or above the noise floor where the vocals are seldom silent, but never above a quarter of the loudest.
        Threshold = Math.Max(loudest * 0.08f, Math.Min(quiet * 1.5f, loudest * 0.25f));
    }

    /// <summary>Below this the vocals are between words (8% of the loudest, or 1.5 times a quiet moment, at most a quarter of the
    /// loudest).</summary>
    public float Threshold { get; }

    public float At(TimeSpan time)
    {
        var index = (int)(time.TotalMilliseconds / StepMilliseconds);
        return index >= 0 && index < rms.Length ? rms[index] : 0;
    }

    /// <summary>Where the word sung at <paramref name="time"/> ends: the first dip below 1.2 times <see cref="Threshold"/> within
    /// <paramref name="within"/>, or after <paramref name="within"/>.</summary>
    public TimeSpan WordEnd(TimeSpan time, TimeSpan within)
    {
        var start = Math.Max(0, (int)(time.TotalMilliseconds / StepMilliseconds));
        var last = start + (int)(within.TotalMilliseconds / StepMilliseconds);
        for (var i = start; i < Math.Min(last, rms.Length); i++)
            if (rms[i] < Threshold * 1.2f) return TimeSpan.FromMilliseconds(i * StepMilliseconds);
        return time + within;
    }

    /// <summary>The mouth's opening for a vocal level (as the character's loudness lip-sync: -48 dBFS closed, -15 open).</summary>
    public static double Mouth(double rms)
    {
        var decibels = 20 * Math.Log10(rms + 1e-9);
        return Math.Clamp((decibels + 48) / 33, 0, 1);
    }
}

/// <summary>Plans song starts and stops from a song's map: where play_song's "from" points, how the band comes in before it,
/// and how a stop ends musically. Pure and deterministic, so MCP's song_playback_check runs exactly what plays.</summary>
public static class SongTransport
{
    /// <summary>The vocals open this long before a line's onset, so its first consonant is heard.</summary>
    public static TimeSpan VocalPreroll => TimeSpan.FromMilliseconds(80);
    /// <summary>The least lead-in before the singing: half a bar, and at least this long.</summary>
    public static TimeSpan MinimumLeadIn => TimeSpan.FromSeconds(1.5);
    public static TimeSpan MusicalStopWindow => TimeSpan.FromMilliseconds(600);
    public static TimeSpan VocalStopFade => TimeSpan.FromMilliseconds(150);
    public static TimeSpan QuickStopFade => TimeSpan.FromMilliseconds(300);

    /// <summary>Reads play_song's <paramref name="from"/>: start (the default), resume or "resume section" (after
    /// <paramref name="last"/>), a section ("chorus", "verse 2", "the bridge"), line:N (1-based), or a time ("1:05", "65s").
    /// Returns the target, or what to tell the model.</summary>
    public static (SongTarget? Target, string? Problem) Resolve(SongMap map, string? from, SongStopRecord? last)
    {
        ArgumentNullException.ThrowIfNull(map);
        var text = string.Join(' ', (from ?? "").Trim().ToLowerInvariant().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (text.StartsWith("the ", StringComparison.Ordinal)) text = text[4..];
        if (text is "" or "start" or "top" or "beginning" or "the top" or "the beginning" or "0" or "0:00")
            return (new(null, null, "from the top", Top: true), null);
        if (text is "resume" or "resume line" or "resume section" or "resume:section" or "where i stopped" or "where you stopped")
        {
            if (last is null || last.Ended) return (new(null, null, last is null ? "from the top (nothing to resume)" : "from the top (it had ended)", Top: true), null);
            var line = last.Line ?? last.NextLine!.Value;
            if (text.Contains("section", StringComparison.Ordinal) && map.Lines[line].Section is { Length: > 0 } section)
            {
                var first = FirstOf(map, section)!.Value;
                return (new(first, null, $"the {section} where you stopped (line {first + 1})"), null);
            }
            return (new(line, null, $"line {line + 1} where you stopped"), null);
        }
        if (text.StartsWith("line", StringComparison.Ordinal))
        {
            var number = text[4..].TrimStart(':', ' ');
            if (int.TryParse(number, NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n >= 1 && n <= map.Lines.Count)
                return (new(n - 1, null, $"line {n}"), null);
            return (null, $"There's no {text}; the song has lines 1 to {map.Lines.Count}.");
        }
        if (Time(text) is { } time)
        {
            if (time >= map.Duration) return (null, $"The song is only {SongClock.Of(map.Duration)} long.");
            if (time <= TimeSpan.FromMilliseconds(50)) return (new(null, null, "from the top", Top: true), null);
            // A line starting soon after that moment is where it goes; otherwise that moment itself, on its nearest downbeat.
            if (map.NextLine(time) is { } next && map.Lines[next].Start - time <= map.Bar)
                return (new(next, null, $"line {next + 1} at {SongClock.Of(map.Lines[next].Start)}"), null);
            var downbeat = map.DownbeatAtOrBefore(time + map.Bar / 2);
            return (new(null, downbeat, $"{SongClock.Of(downbeat)}"), null);
        }
        var sections = map.Sections;
        var name = text.Replace("first ", "", StringComparison.Ordinal);
        var match = sections.FirstOrDefault(s => s == name) ?? sections.FirstOrDefault(s => s.StartsWith(name, StringComparison.Ordinal)) ??
            (Ordinal(name) is { } ordinal ? sections.FirstOrDefault(s => s == ordinal) : null);
        if (match is not null)
        {
            var first = FirstOf(map, match)!.Value;
            return (new(first, null, $"the {match} (line {first + 1})"), null);
        }
        return (null, sections.Count > 0
            ? $"There's no \"{from}\". Use start, resume, a section ({string.Join(", ", sections)}), line:N or a time like 1:05."
            : $"There's no \"{from}\". Use start, resume, line:N (1 to {map.Lines.Count}) or a time like 1:05.");
    }

    // "second verse" -> "verse 2", "last chorus" -> the last chorus.
    private static string? Ordinal(string text)
    {
        string[] words = ["first", "second", "third", "fourth", "fifth"];
        var parts = text.Split(' ');
        if (parts.Length != 2) return null;
        var at = Array.IndexOf(words, parts[0]);
        return at switch { 0 => parts[1], > 0 => $"{parts[1]} {at + 1}", _ => null };
    }

    private static int? FirstOf(SongMap map, string section)
    {
        for (var i = 0; i < map.Lines.Count; i++) if (map.Lines[i].Section == section) return i;
        return null;
    }

    private static TimeSpan? Time(string text)
    {
        var trimmed = text.TrimEnd('s').Trim();
        var parts = trimmed.Split(':');
        if (parts.Length == 2 && int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var minutes) &&
            double.TryParse(parts[1], NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var seconds) && seconds < 60)
            return TimeSpan.FromSeconds(minutes * 60 + seconds);
        return parts.Length == 1 && double.TryParse(trimmed, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var plain) && plain >= 0
            ? TimeSpan.FromSeconds(plain) : null;
    }

    /// <summary>How the song starts for <paramref name="target"/>. From the top it simply plays; anywhere else the backing enters
    /// on the latest downbeat that leaves at least half a bar (and <see cref="MinimumLeadIn"/>) before the singing: one bar
    /// before the line, two for a long pickup or short bars.</summary>
    public static SongStartPlan PlanStart(SongMap map, SongTarget target)
    {
        ArgumentNullException.ThrowIfNull(map);
        ArgumentNullException.ThrowIfNull(target);
        var first = map.Lines.Count > 0 ? map.Lines[0].Start : map.Duration;
        if (target.Top)
            return new(target, true, TimeSpan.Zero, TimeSpan.Zero, 0, TimeSpan.Zero, first, first, first, first);
        var onset = target.Line is { } line ? map.Lines[line].Start : target.Time ?? TimeSpan.Zero;
        var need = map.Bar / 2 > MinimumLeadIn ? map.Bar / 2 : MinimumLeadIn;
        var entry = onset - need <= TimeSpan.Zero ? TimeSpan.Zero : map.DownbeatAtOrBefore(onset - need);
        var bars = Math.Max(1, map.Downbeats.Count(downbeat => downbeat >= entry && downbeat < onset - map.Beat / 2));
        var fadeLimit = onset - entry - TimeSpan.FromMilliseconds(100);
        var fade = Clamp(map.Bar < fadeLimit ? map.Bar : fadeLimit, TimeSpan.FromMilliseconds(300), map.Bar);
        if (entry == TimeSpan.Zero && onset < need) fade = Clamp(onset - TimeSpan.FromMilliseconds(50), TimeSpan.FromMilliseconds(10), map.Bar);
        var vocals = onset - VocalPreroll < entry ? entry : onset - VocalPreroll;
        // The bar the band repeats while Martlet is still talking: the last whole bar that starts half a bar before the singing.
        var vampStart = map.DownbeatAtOrBefore(onset - map.Bar / 2);
        if (vampStart < entry) vampStart = entry;
        var vampEnd = vampStart + map.Bar;
        var commit = (vocals < vampEnd ? vocals : vampEnd) - TimeSpan.FromMilliseconds(30);
        if (commit < entry) commit = entry;
        return new(target, false, entry, fade, bars, vocals, onset, vampStart, vampEnd, commit);
    }

    /// <summary>How a song stops when asked at <paramref name="heard"/> (the song time being heard) with audio already written up
    /// to <paramref name="written"/>. <paramref name="singing"/>: the vocals are on (not in a lead-in).</summary>
    public static SongStopPlan PlanStop(SongMap map, VocalEnvelope? vocals, TimeSpan heard, TimeSpan written, bool musical, bool singing = true)
    {
        ArgumentNullException.ThrowIfNull(map);
        if (written < heard) written = heard;
        if (!musical) return new(false, heard, written, QuickStopFade, written, QuickStopFade);
        var word = singing && vocals is not null ? vocals.WordEnd(heard, MusicalStopWindow) : heard;
        var vocalsEnd = word < written ? written : word;
        var beat = Clamp(map.Beat, TimeSpan.FromMilliseconds(250), TimeSpan.FromSeconds(1.5));
        var next = map.BeatAtOrAfter(vocalsEnd) is { } at && at - vocalsEnd <= beat ? at : vocalsEnd;
        return new(true, heard, vocalsEnd, singing ? VocalStopFade : TimeSpan.Zero, next < written ? written : next, beat);
    }

    private static TimeSpan Clamp(TimeSpan value, TimeSpan low, TimeSpan high) => value < low ? low : value > high ? high : value;
}
