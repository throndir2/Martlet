namespace Martlet.Core.Nodes;

/// <summary>What keeping this PC's own host service on this PC's Martlet version decides (<see cref="OwnHostFollower"/>).</summary>
public enum OwnHostStep
{
    /// <summary>Nothing to read now: the host service was found current, its update failed for this version (Update hosts
    /// and the host dashboard still run it), or a busy host service's retry isn't due yet.</summary>
    Idle,
    /// <summary>Docker didn't answer or the host service isn't set up here; Martlet reads it again later.</summary>
    NotFound,
    /// <summary>The host service is set up but stopped; Martlet updates it once it runs again.</summary>
    Stopped,
    /// <summary>It runs this PC's version (or a newer one).</summary>
    Current,
    /// <summary>Another route of this Martlet (a run window, a command from another computer, the automatic pass) is
    /// updating it right now.</summary>
    OtherRoute,
    /// <summary>This PC is about to install its own update; the host service follows that version afterwards.</summary>
    AppUpdateFirst,
    /// <summary>Martlet is replying or hearing you; the update starts once it isn't.</summary>
    Conversation,
    /// <summary>Update it now, in the background.</summary>
    Update
}

/// <summary>This PC's own host service as Docker reports it: the gateway container's Martlet version (null when its image
/// names none) and whether it runs.</summary>
public sealed record OwnHostReading(string? Version, bool Running);

/// <summary>Keeps this PC's own host service (Docker Desktop) on the Martlet version this PC runs, whatever Update paired
/// hosts automatically says: Martlet updates itself first and comes back at once, then brings its host service up to the
/// same version in the background. One instance per Martlet run. Once the host service is found current it isn't read again;
/// an update that found it busy with another change is tried again after <see cref="RetryDelay"/>; one that failed isn't
/// tried again for that version (Update hosts and the host dashboard still run it).</summary>
public sealed class OwnHostFollower(TimeSpan? retryDelay = null)
{
    public static readonly TimeSpan DefaultRetryDelay = TimeSpan.FromMinutes(3);

    private string? current;
    private string? failed;

    public TimeSpan RetryDelay { get; } = retryDelay ?? DefaultRetryDelay;

    /// <summary>When an update that found the host service busy is tried again; null when none waits.</summary>
    public DateTimeOffset? RetryAt { get; private set; }

    /// <summary>Whether reading the host service (a Docker call) can lead anywhere now for this PC's version
    /// <paramref name="app"/>.</summary>
    public bool Due(string app, DateTimeOffset now) =>
        current != app && failed != app && (RetryAt is not { } at || now >= at);

    /// <summary>The next step for this PC's version <paramref name="app"/>, given what Docker reported (null: Docker didn't
    /// answer or the host service isn't set up), whether another route updates it right now (<paramref name="claimed"/>),
    /// whether this PC is about to install its own update (<paramref name="appUpdateFirst"/>) and whether Martlet is
    /// replying or hearing you (<paramref name="conversation"/>). A host service found current is remembered.</summary>
    public OwnHostStep Decide(string app, OwnHostReading? reading, bool claimed, bool appUpdateFirst, bool conversation,
        DateTimeOffset now)
    {
        if (!Due(app, now)) return OwnHostStep.Idle;
        if (reading is null) return OwnHostStep.NotFound;
        if (!IsOlder(reading.Version, app))
        {
            Updated(app);
            return OwnHostStep.Current;
        }
        if (!reading.Running) return OwnHostStep.Stopped;
        if (claimed) return OwnHostStep.OtherRoute;
        if (appUpdateFirst) return OwnHostStep.AppUpdateFirst;
        return conversation ? OwnHostStep.Conversation : OwnHostStep.Update;
    }

    /// <summary>The host service was busy with another change, so nothing changed: try again after <see cref="RetryDelay"/>.</summary>
    public void Busy(DateTimeOffset now) => RetryAt = now + RetryDelay;

    /// <summary>The update for <paramref name="app"/> stopped; it isn't tried again automatically for that version.</summary>
    public void Failed(string app)
    {
        failed = app;
        RetryAt = null;
    }

    /// <summary>The host service runs <paramref name="app"/> now (updated by any route).</summary>
    public void Updated(string app)
    {
        current = app;
        failed = null;
        RetryAt = null;
    }

    /// <summary>Whether a host service reporting <paramref name="reported"/> runs an older Martlet than
    /// <paramref name="target"/>; one that reports no version counts as older.</summary>
    public static bool IsOlder(string? reported, string target) =>
        !Version.TryParse(target, out var wanted) || reported is null || !Version.TryParse(reported, out var have) || have < wanted;
}
