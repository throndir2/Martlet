using System.IO.Compression;
using Martlet.Core.Logs;
using Martlet.Diagnostics;

namespace Martlet.Core.Tests;

public sealed class LogShareTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 10, 4, 8, 0, 0, TimeSpan.Zero);
    private static readonly TimeProvider Clock = new FixedClock(Now);
    private readonly string folder = Path.Combine(Path.GetTempPath(), "martlet-log-share-tests-" + Guid.NewGuid().ToString("N"));

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    public void Dispose()
    {
        if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
    }

    private static LogRecord Line(string source, string component, DateTimeOffset at, string message, string level = LogLevels.Info, int ordinal = 0) => new()
    {
        Source = source, Component = component, Seq = LogRules.Seq(at, ordinal), At = at, Level = level, Message = message
    };

    /// <summary>A host as the gateway keeps its log: its own gateway lines, every line it accepts once per stream mark, read
    /// by store position.</summary>
    private sealed class FakeHost(string hostId) : ILogHost
    {
        private readonly Dictionary<string, long> marks = new(StringComparer.Ordinal);
        internal List<LogRecord> Entries { get; } = [];
        internal bool Down { get; set; }
        internal bool Old { get; set; }
        internal int Pushes { get; private set; }

        public string HostId => hostId;

        internal void Own(DateTimeOffset at, string message) => Keep(Line(hostId, LogComponents.Gateway, at, message), null);

        /// <summary>An unclean restart: the host comes back with only its first <paramref name="keep"/> lines and their marks, and
        /// numbers new lines from there.</summary>
        internal void Crash(int keep)
        {
            Entries.RemoveRange(keep, Entries.Count - keep);
            marks.Clear();
            foreach (var entry in Entries) marks[entry.Stream] = Math.Max(marks.GetValueOrDefault(entry.Stream), entry.Seq);
        }

        public Task<(IReadOnlyList<LogRecord> Entries, long Next, bool More)> ReadAsync(long after, int limit, CancellationToken token)
        {
            Check();
            var page = Entries.Skip((int)after).Take(limit).ToArray();
            Served += page.Length;
            var next = after + page.Length;
            return Task.FromResult<(IReadOnlyList<LogRecord>, long, bool)>((page, next, next < Entries.Count));
        }

        /// <summary>How many lines desktops have read from this host.</summary>
        internal int Served { get; private set; }

        public Task<(int Accepted, IReadOnlyList<LogMark> Marks)> PushAsync(LogBatch batch, CancellationToken token)
        {
            Check();
            batch.Validate();
            Pushes++;
            var accepted = 0;
            foreach (var entry in batch.Entries.OrderBy(e => e.Stream, StringComparer.Ordinal).ThenBy(e => e.Seq))
                if (Keep(entry, "pusher")) accepted++;
            IReadOnlyList<LogMark> result = batch.Streams.Select(s => s.Source + "/" + s.Component).Concat(batch.Entries.Select(e => e.Stream))
                .Distinct().Select(s => new LogMark { Source = s[..s.LastIndexOf('/')], Component = s[(s.LastIndexOf('/') + 1)..], Seq = marks.GetValueOrDefault(s) })
                .ToArray();
            return Task.FromResult((accepted, result));
        }

        private bool Keep(LogRecord entry, string? by)
        {
            if (entry.Seq <= marks.GetValueOrDefault(entry.Stream)) return false;
            Entries.Add(by is null || entry.Source == by ? entry : entry with { RelayedBy = by });
            marks[entry.Stream] = entry.Seq;
            return true;
        }

        private void Check()
        {
            if (Old) throw new LogHostException($"{hostId} runs a Martlet older than shared logs.", old: true);
            if (Down) throw new LogHostException($"{hostId} didn't answer.");
        }
    }

    [Fact]
    public async Task Every_computers_lines_reach_every_host_and_desktop_once()
    {
        var h1 = new FakeHost("gpu-a");
        var h2 = new FakeHost("gpu-b");
        h1.Own(Now.AddMinutes(-9), "Gateway gpu-a started.");
        h2.Own(Now.AddMinutes(-9), "Gateway gpu-b started.");
        var a = new LogShare(Path.Combine(folder, "a"), "desktop-a");
        var c = new LogShare(Path.Combine(folder, "c"), "desktop-c");
        LogRecord[] aLines = [Line("desktop-a", LogComponents.Desktop, Now.AddMinutes(-5), "A started"),
            Line("desktop-a", LogComponents.Desktop, Now.AddMinutes(-4), "A failed\nSystem.Exception: x\n   at Y()", LogLevels.Error)];
        LogRecord[] cLines = [Line("desktop-c", LogComponents.HostRuns, Now.AddMinutes(-3), "C ran setup")];

        // C reaches only gpu-b; A reaches both and passes C's lines and each host's own lines on.
        var cResult = await c.RunAsync([h2], cLines, send: true, Now, CancellationToken.None);
        var aResult = await a.RunAsync([h1, h2], aLines, send: true, Now, CancellationToken.None);

        Assert.Equal(1, cResult.Shared);
        Assert.Equal(2, aResult.Shared);
        Assert.Equal("Sharing logs with 2 of 2 hosts.", aResult.Describe());
        foreach (var host in new[] { h1, h2 })
        {
            Assert.Equal(2, host.Entries.Count(e => e.Source == "desktop-a"));
            Assert.Single(host.Entries, e => e.Message == "C ran setup");
            Assert.Single(host.Entries, e => e.Source == "gpu-a");
            Assert.Single(host.Entries, e => e.Source == "gpu-b");
        }
        Assert.Equal("pusher", h1.Entries.Single(e => e.Source == "desktop-c").RelayedBy);
        // A holds every other computer's lines, never its own (they stay in its log files).
        Assert.Equal(["desktop-c", "gpu-a", "gpu-b"], a.Logs.Snapshot().Select(l => l.Source).Distinct().Order());

        // Running again sends nothing new, and C now gets A's lines and gpu-a's through gpu-b.
        var again = await a.RunAsync([h1, h2], aLines, send: true, Now, CancellationToken.None);
        await c.RunAsync([h2], cLines, send: true, Now, CancellationToken.None);
        Assert.Equal(0, again.Sent);
        Assert.Equal(0, again.Received);
        Assert.Equal(["desktop-a", "gpu-a", "gpu-b"], c.Logs.Snapshot().Select(l => l.Source).Distinct().Order());
        Assert.All(new[] { h1, h2 }, h => Assert.Equal(h.Entries.Count, h.Entries.DistinctBy(e => (e.Stream, e.Seq)).Count()));

        // The copy survives a restart.
        var reopened = new LogShare(Path.Combine(folder, "a"), "desktop-a", Clock);
        Assert.Equal("loaded", reopened.Logs.LoadState);
        Assert.Equal(a.Logs.Count, reopened.Logs.Count);
    }

    [Fact]
    public async Task Unreachable_and_older_hosts_are_named_and_reading_sends_nothing()
    {
        var up = new FakeHost("gpu-a");
        var down = new FakeHost("gpu-b") { Down = true };
        var old = new FakeHost("old-box") { Old = true };
        up.Own(Now.AddMinutes(-2), "Gateway gpu-a started.");
        var share = new LogShare(null, "desktop-a");
        LogRecord[] own = [Line("desktop-a", LogComponents.Desktop, Now.AddMinutes(-1), "A started")];

        var read = await share.RunAsync([up, down, old], own, send: false, Now, CancellationToken.None);
        Assert.Equal(0, up.Pushes);
        Assert.Equal(1, read.Received);
        Assert.Equal("Sharing logs with 1 of 3 hosts. Waiting for gpu-b. Update old-box to share its logs.", read.Describe());

        var sent = await share.RunAsync([up, down, old], own, send: true, Now, CancellationToken.None);
        Assert.Equal(1, sent.Sent);
        Assert.Equal([LogShareState.Shared, LogShareState.Unreachable, LogShareState.Old], sent.Hosts.Select(h => h.State));

        // A host that comes back gets what it missed on the next run.
        down.Down = false;
        var back = await share.RunAsync([up, down], own, send: true, Now, CancellationToken.None);
        Assert.Equal(2, back.Shared);
        Assert.Single(down.Entries, e => e.Message == "A started");
        Assert.Single(down.Entries, e => e.Source == "gpu-a");
        Assert.Equal("No Martlet hosts are paired with this PC yet, so it shows only its own logs.",
            (await share.RunAsync([], own, send: true, Now, CancellationToken.None)).Describe());
    }

    [Fact]
    public async Task A_new_host_gets_only_the_last_week_in_batches_and_the_rest_waits()
    {
        var host = new FakeHost("gpu-a");
        var share = new LogShare(null, "desktop-a");
        var old = Line("desktop-a", LogComponents.Desktop, Now.AddDays(-8), "from last month");
        var lines = Enumerable.Range(0, LogBatch.MaximumEntries * LogShare.MaximumBatchesPerRun + 10)
            .Select(i => Line("desktop-a", LogComponents.Desktop, Now.AddMinutes(-1).AddMilliseconds(i), $"{i}")).Prepend(old).ToArray();

        var first = await share.RunAsync([host], lines, send: true, Now, CancellationToken.None);
        Assert.Equal(LogBatch.MaximumEntries * LogShare.MaximumBatchesPerRun, first.Sent);
        Assert.Equal(10, first.Waiting);
        Assert.Contains("10 more lines will follow.", first.Describe(), StringComparison.Ordinal);
        Assert.DoesNotContain(host.Entries, e => e.Message == "from last month");
        // Oldest first, in order, so the host's mark never passes a line it wasn't given.
        Assert.Equal(host.Entries.Select(e => e.Seq).Order(), host.Entries.Select(e => e.Seq));

        var second = await share.RunAsync([host], lines, send: true, Now, CancellationToken.None);
        Assert.Equal(10, second.Sent);
        Assert.Equal(0, second.Waiting);
        Assert.Equal(lines.Length - 1, host.Entries.Count);
    }

    [Fact]
    public async Task A_host_that_restarts_without_saving_is_read_again_and_gets_what_it_lost()
    {
        var host = new FakeHost("gpu-a");
        host.Own(Now.AddMinutes(-9), "Gateway gpu-a started.");
        var a = new LogShare(null, "desktop-a");
        var b = new LogShare(null, "desktop-b");
        LogRecord[] aLines = [Line("desktop-a", LogComponents.Desktop, Now.AddMinutes(-5), "A one"), Line("desktop-a", LogComponents.Desktop, Now.AddMinutes(-4), "A two")];
        LogRecord[] bLines = [Line("desktop-b", LogComponents.Desktop, Now.AddMinutes(-3), "B one")];
        await a.RunAsync([host], aLines, send: true, Now, CancellationToken.None);
        await b.RunAsync([host], bLines, send: true, Now, CancellationToken.None);
        await a.RunAsync([host], aLines, send: true, Now, CancellationToken.None);
        Assert.Equal(4, host.Entries.Count);

        // The host loses everything after its first line, then numbers new lines from there: its own line and another of B's.
        host.Crash(keep: 1);
        host.Own(Now.AddMinutes(-2), "Gateway gpu-a started again.");
        LogRecord[] bMore = [.. bLines, Line("desktop-b", LogComponents.Desktop, Now.AddMinutes(-1), "B two")];
        await b.RunAsync([host], bMore, send: true, Now, CancellationToken.None);
        await a.RunAsync([host], aLines, send: true, Now, CancellationToken.None);

        Assert.All(new[] { "A one", "A two", "B one", "B two", "Gateway gpu-a started again." },
            text => Assert.Single(host.Entries, e => e.Message == text));
        Assert.Contains(a.Logs.Snapshot(), l => l.Message == "B two");
        Assert.Contains(a.Logs.Snapshot(), l => l.Message == "Gateway gpu-a started again.");
    }

    [Fact]
    public void The_copy_keeps_a_week_of_each_valid_line_once_within_bounds()
    {
        var logs = new NetworkLogs(Clock);
        var line = Line("gpu-a", LogComponents.Gateway, Now, "started");
        Assert.Equal(1, logs.Add([line, line, line with { Level = "LOUD", Seq = line.Seq + 1 }]));
        // A week of lines: older ones never come in, and lines leave once they are a week old.
        var sixDays = Line("desktop-b", LogComponents.Desktop, Now.AddDays(-6), "six days ago");
        Assert.Equal(0, logs.Add([Line("desktop-b", LogComponents.Desktop, Now.AddDays(-8), "eight days ago")]));
        Assert.Equal(1, logs.Add([sixDays]));
        Assert.Equal(1, logs.Add([Line("desktop-b", LogComponents.Desktop, Now.AddDays(2), "two days later")], Now.AddDays(2)));
        Assert.DoesNotContain(logs.Snapshot(), l => l.Message == "six days ago");
        Assert.Equal(0, logs.Add([sixDays], Now.AddDays(2)));
        Assert.Equal(["desktop-b/desktop", "gpu-a/gateway"], logs.StreamNames().Order());

        // The caps hold however many lines a week has; the oldest go first.
        var many = Enumerable.Range(1, NetworkLogs.MaximumEntries + 5)
            .Select(i => Line("desktop-c", LogComponents.Desktop, Now.AddSeconds(i), "x")).ToArray();
        logs.Add(many, Now.AddDays(2));
        Assert.Equal(NetworkLogs.MaximumEntries, logs.Count);
        Assert.Equal(many[^1], logs.Snapshot()[^1]);
        Assert.DoesNotContain(logs.Snapshot(), l => l.Message == "started");
        // Lines older than what was dropped to stay in bounds don't come back.
        Assert.Equal(0, logs.Add([line], Now.AddDays(2)));
        Assert.Equal(NetworkLogs.MaximumEntries - 1, logs.CountAfter("desktop-c/desktop", 0, Now.AddDays(-5)));
    }

    [Fact]
    public void The_copy_appends_each_run_to_its_journal_and_is_written_in_full_now_and_then()
    {
        var path = Path.Combine(folder, NetworkLogs.FileName);
        var journal = Path.Combine(folder, NetworkLogs.JournalFileName);
        var logs = new NetworkLogs(Clock);
        logs.Add([Line("gpu-a", LogComponents.Gateway, Now.AddMinutes(-3), "one"), Line("gpu-a", LogComponents.Gateway, Now.AddMinutes(-2), "two")]);
        logs.Save(folder);
        Assert.True(File.Exists(path));
        Assert.False(File.Exists(journal));
        var full = File.ReadAllBytes(path);

        // A later run appends only its new lines; the full copy stays as it was.
        logs.Add([Line("desktop-b", LogComponents.Desktop, Now.AddMinutes(-1), "three")]);
        logs.Save(folder);
        Assert.Equal(full, File.ReadAllBytes(path));
        Assert.Single(File.ReadAllLines(journal));
        var loaded = NetworkLogs.Load(folder, Clock);
        Assert.Equal("loaded", loaded.LoadState);
        Assert.Equal(["one", "two", "three"], loaded.Snapshot().Select(l => l.Message));
        Assert.Equal(0, loaded.Add([Line("desktop-b", LogComponents.Desktop, Now.AddMinutes(-1), "three")]));

        // A line cut short by a power cut is skipped, and the next save writes the copy in full without it.
        File.AppendAllText(journal, "{\"source\":\"desk");
        var cut = NetworkLogs.Load(folder, Clock);
        Assert.Equal(3, cut.Count);
        cut.Add([Line("desktop-b", LogComponents.Desktop, Now, "four")]);
        cut.Save(folder);
        Assert.False(File.Exists(journal));
        Assert.Equal(4, NetworkLogs.Load(folder, Clock).Count);

        // Runs append until the journal outgrows its share of the copy; then the copy is written in full and the journal starts
        // empty.
        var text = new string('w', 2_000);
        var grown = NetworkLogs.Load(folder, Clock);
        full = File.ReadAllBytes(path);
        for (var run = 0; run < 2; run++)
        {
            grown.Add(Enumerable.Range(0, 1_000).Select(i => Line("gpu-b", LogComponents.Gateway, Now.AddSeconds(-run * 1_000 - i), text)).ToArray());
            grown.Save(folder);
            Assert.Equal(run == 0, File.Exists(journal));
            Assert.Equal(run == 0, full.AsSpan().SequenceEqual(File.ReadAllBytes(path)));
        }
        Assert.Equal(2_004, NetworkLogs.Load(folder, Clock).Count);

        File.WriteAllText(path, "{broken");
        var broken = NetworkLogs.Load(folder, Clock);
        Assert.Equal("unreadable", broken.LoadState);
        // An unreadable copy is replaced in full at the next save.
        broken.Add([Line("gpu-a", LogComponents.Gateway, Now, "after")]);
        broken.Save(folder);
        Assert.Equal(["after"], NetworkLogs.Load(folder, Clock).Snapshot().Select(l => l.Message));
        Assert.Equal("none", NetworkLogs.Load(Path.Combine(folder, "missing")).LoadState);
    }

    [Fact]
    public async Task A_restart_goes_on_reading_each_host_from_where_it_stopped()
    {
        var host = new FakeHost("gpu-a");
        for (var i = 0; i < 2_500; i++) host.Own(Now.AddMinutes(-60).AddMilliseconds(i), $"line {i}");
        var directory = Path.Combine(folder, "a");
        var first = new LogShare(directory, "desktop-a", Clock);
        await first.RunAsync([host], [], send: false, Now, CancellationToken.None);
        Assert.Equal(2_500, first.Logs.Count);
        Assert.True(File.Exists(Path.Combine(directory, LogShare.PositionsFileName)));

        // After a restart only the line it read last is read again (to check the host still has it), then the new ones.
        host.Own(Now.AddMinutes(-1), "after the restart");
        var before = host.Served;
        var again = new LogShare(directory, "desktop-a", Clock);
        var result = await again.RunAsync([host], [], send: false, Now, CancellationToken.None);
        Assert.Equal(before + 2, host.Served);
        Assert.Equal(1, result.Received);
        Assert.Equal(2_501, again.Logs.Count);

        // Without its copy, a desktop reads every host from the start again.
        File.Delete(Path.Combine(directory, NetworkLogs.FileName));
        File.Delete(Path.Combine(directory, NetworkLogs.JournalFileName));
        var fresh = new LogShare(directory, "desktop-a", Clock);
        before = host.Served;
        await fresh.RunAsync([host], [], send: false, Now, CancellationToken.None);
        Assert.True(host.Served - before >= 2_501);
        Assert.Equal(2_501, fresh.Logs.Count);
    }

    [Fact]
    public void Save_logs_to_share_writes_every_line_once_oldest_first_with_an_about_file()
    {
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, LogBundle.SuggestedFileName("desktop-a", Now));
        Assert.EndsWith(".zip", path, StringComparison.Ordinal);
        var error = Line("desktop-a", LogComponents.Desktop, Now.AddMinutes(-1), "Unhandled\nSystem.Exception: boom\n   at X()", LogLevels.Error);
        var relayed = Line("desktop-b", LogComponents.Desktop, Now.AddMinutes(-2), "B started") with { RelayedBy = "desktop-a" };
        var gateway = Line("diva-host", LogComponents.Gateway, Now.AddMinutes(-3), "Gateway started", LogLevels.Warn);
        var info = new LogBundleInfo("desktop-a", ["desktop-a", "diva-host"], "0.33.0", "Windows", "Sharing logs with 1 of 1 host.", Now);

        var summary = LogBundle.Save(path, [error, relayed, gateway, error], info, overwrite: false);
        Assert.Throws<IOException>(() => LogBundle.Save(path, [error], info, overwrite: false));

        Assert.Equal((3, 2, 1, 1), (summary.Lines, summary.Computers, summary.Errors, summary.Warnings));
        Assert.Equal(new FileInfo(path).Length, summary.Bytes);
        using var zip = ZipFile.OpenRead(path);
        Assert.Equal([LogBundle.AboutEntry, LogBundle.LinesEntry], zip.Entries.Select(e => e.FullName));
        using var reader = new StreamReader(zip.GetEntry(LogBundle.LinesEntry)!.Open());
        var lines = reader.ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(5, lines.Length);
        Assert.Contains("WARN  diva-host/gateway (this PC): Gateway started", lines[0], StringComparison.Ordinal);
        Assert.Contains("INFO  desktop-b/desktop via desktop-a: B started", lines[1], StringComparison.Ordinal);
        Assert.Contains("ERROR desktop-a/desktop (this PC): Unhandled", lines[2], StringComparison.Ordinal);
        Assert.Equal("    System.Exception: boom", lines[3]);
        Assert.Equal("       at X()", lines[4]);
        Assert.Contains("Martlet 0.33.0", summary.About, StringComparison.Ordinal);
        Assert.Contains("Log sharing: Sharing logs with 1 of 1 host.", summary.About, StringComparison.Ordinal);
        Assert.Contains("never keys, pairing secrets", summary.About, StringComparison.Ordinal);
        Assert.Contains("  desktop-b\n    desktop: 1,", summary.About, StringComparison.Ordinal);
    }
}
