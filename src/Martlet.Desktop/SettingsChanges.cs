namespace Martlet.Desktop;

/// <summary>Where a version of settings.json came from.</summary>
internal enum SettingsChangeSource
{
    /// <summary>This Martlet saved it: a page, Setup or another part of the desktop.</summary>
    LocalEdit,
    /// <summary>The settings sync saved it: a setting taken from another computer.</summary>
    RemoteSync,
    /// <summary>A switch to another person's settings.</summary>
    AccountSwitch,
    /// <summary>Something outside this Martlet wrote it: Martlet's MCP server (settings_set, character_create) or an edit by hand.</summary>
    File
}

/// <summary>A version of settings.json that Martlet applied (Home, the open conversation), where it came from and when.</summary>
internal sealed record SettingsChange(string Revision, SettingsChangeSource Source, DateTimeOffset At);

/// <summary>The one owner of "which settings.json does Martlet use now, and where did it come from". Every save this Martlet makes
/// is recorded with its source (<see cref="Written"/>, from <c>SettingsStore.Saved</c>; a save inside <see cref="From"/> gets that
/// scope's source, any other save is a <see cref="SettingsChangeSource.LocalEdit"/>), and every version Martlet applies is
/// recorded (<see cref="Applied"/>). The file watcher reads a changed file again only when its version was not applied yet
/// (<see cref="IsApplied"/>), and calls it a change from outside only when this Martlet didn't write it (<see cref="SourceOf"/>).
/// So the watcher doesn't apply again what a page or the settings sync already applied, and a page's own save isn't reported as
/// a change made outside Martlet. Thread-safe.</summary>
internal sealed class SettingsChanges(TimeProvider? clock = null)
{
    /// <summary>How many of this Martlet's own saves are remembered: enough for a burst of saves before the watcher looks.</summary>
    internal const int Remembered = 32;

    private readonly TimeProvider clock = clock ?? TimeProvider.System;
    private readonly object gate = new();
    private readonly AsyncLocal<SettingsChangeSource?> scope = new();
    private readonly LinkedList<(string Revision, SettingsChangeSource Source)> written = new();
    private SettingsChange? last;

    /// <summary>The newest version Martlet applied; null before the first.</summary>
    internal SettingsChange? Last
    {
        get { lock (gate) return last; }
    }

    /// <summary>Saves in this flow (and the work it awaits) until the result is disposed come from <paramref name="source"/>.</summary>
    internal IDisposable From(SettingsChangeSource source)
    {
        var before = scope.Value;
        scope.Value = source;
        return new Scope(() => scope.Value = before);
    }

    /// <summary>Records a save of <paramref name="revision"/> by this Martlet, with the source of the scope it ran in.</summary>
    internal void Written(string revision)
    {
        var source = scope.Value ?? SettingsChangeSource.LocalEdit;
        lock (gate)
        {
            for (var node = written.First; node is not null; node = node.Next)
                if (node.Value.Revision == revision)
                {
                    written.Remove(node);
                    break;
                }
            written.AddLast((revision, source));
            while (written.Count > Remembered) written.RemoveFirst();
        }
    }

    /// <summary>Where <paramref name="revision"/> came from: one of this Martlet's own saves, else <see cref="SettingsChangeSource.File"/>.</summary>
    internal SettingsChangeSource SourceOf(string revision)
    {
        lock (gate)
            for (var node = written.Last; node is not null; node = node.Previous)
                if (node.Value.Revision == revision) return node.Value.Source;
        return SettingsChangeSource.File;
    }

    /// <summary>Whether <paramref name="revision"/> is the version Martlet applied last.</summary>
    internal bool IsApplied(string? revision)
    {
        lock (gate) return revision is not null && last?.Revision == revision;
    }

    /// <summary>Records that Martlet applied <paramref name="revision"/>, which it read because of <paramref name="reason"/>. A
    /// version this Martlet wrote keeps the source it was written with. Returns the change, or null when that version was the one
    /// applied already (or <paramref name="revision"/> is null: there is no file).</summary>
    internal SettingsChange? Applied(string? revision, SettingsChangeSource reason)
    {
        if (revision is null) return null;
        var source = SourceOf(revision) is var own && own != SettingsChangeSource.File ? own : reason;
        lock (gate)
        {
            if (last?.Revision == revision) return null;
            return last = new(revision, source, clock.GetUtcNow());
        }
    }

    private sealed class Scope(Action end) : IDisposable
    {
        private Action? end = end;

        public void Dispose() => Interlocked.Exchange(ref end, null)?.Invoke();
    }
}
