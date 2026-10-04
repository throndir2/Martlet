using Martlet.Core.Nodes;

namespace Martlet.Mcp;

/// <summary>host_update_check: rehearses, with the desktop's production <see cref="HostUpdateTracker"/>,
/// <see cref="OwnHostFollower"/> and <see cref="HostEngineBusy"/> reader, how Martlet coordinates its own host service updates.
/// The timeline is the one seen on a host PC right after Martlet updated itself: you press Update this PC's host service (a
/// run window) while Martlet's automatic pass reaches the same host service, both as its local pairing and as this PC's own
/// host service. Martlet must leave the host to your run instead of starting more runs that find it locked by Martlet's own
/// update, an update from elsewhere must be described as such, and once the host is current no stale "busy" note or retry
/// may remain. Then this PC's own host service follows the app's version by itself: updated in the background, giving way
/// to another route, this PC's own pending update and the conversation, retried when busy and settled once current. Pure
/// logic: contacts nothing and touches no Docker, host or data directory.</summary>
internal static class HostUpdateCheck
{
    private const string Version = "0.22.0";
    private const string LocalHost = "diva-host";
    private const string OtherHost = "lab-host";
    // The busy line the engine printed on the real host PC when Martlet's automatic pass collided with its own run window.
    private const string SelfBusyLine = "MARTLET-BUSY updating this host (0 min so far; martlet-host-update-20261003-032119-2532, from Martlet)";
    private const string InstallBusyLine = "MARTLET-BUSY installing ollama (12 min so far; martlet-host-add-20261003-021239-9052, from Martlet)";

    internal static object Run()
    {
        var steps = new List<object>();
        var passed = true;
        void Step(string name, bool ok, string detail)
        {
            steps.Add(new { name, ok, detail });
            passed &= ok;
        }

        var tracker = new HostUpdateTracker();
        var localKey = HostUpdateTracker.Key(LocalHost, onThisPc: true);
        var otherKey = HostUpdateTracker.Key(OtherHost, onThisPc: false);
        Step("keys", localKey == HostUpdateTracker.ThisPc && otherKey == OtherHost,
            $"this PC's own pairing {LocalHost} -> {localKey}; {OtherHost} over SSH or another computer -> {otherKey}");

        var runWindow = tracker.Begin(localKey);
        Step("run-window-claims-host", tracker.IsUpdating(HostUpdateTracker.ThisPc) && tracker.Running && !tracker.IsUpdating(otherKey),
            $"Update this PC's host service running: updating {localKey} = {tracker.IsUpdating(localKey)}, {OtherHost} = {tracker.IsUpdating(otherKey)}");

        // The automatic pass: it skips what Martlet is already updating and only writes notes for hosts it runs itself.
        var skipped = new List<string>();
        var updatedByPass = new List<string>();
        foreach (var (id, key) in new[] { (LocalHost, localKey), (HostUpdateTracker.ThisPc, HostUpdateTracker.ThisPc), (OtherHost, otherKey) })
        {
            if (tracker.IsUpdating(key)) { skipped.Add(id); continue; }
            using (tracker.Begin(key)) tracker.Note(id, $"Updated to Martlet {Version} at 8:21 PM.");
            updatedByPass.Add(id);
        }
        Step("automatic-pass-leaves-host-to-run-window",
            skipped.SequenceEqual([LocalHost, HostUpdateTracker.ThisPc]) && updatedByPass.SequenceEqual([OtherHost]) &&
            !tracker.Notes.ContainsKey(LocalHost) && !tracker.Notes.ContainsKey(HostUpdateTracker.ThisPc) && tracker.Waiting.Count == 0,
            $"skipped {string.Join(", ", skipped)} (no second engine run, no busy note); updated {string.Join(", ", updatedByPass)}");

        var command = tracker.Begin(localKey);
        runWindow.Dispose();
        var stillHeld = tracker.IsUpdating(localKey);
        command.Dispose();
        command.Dispose();
        Step("overlapping-routes-end-separately", stillHeld && !tracker.IsUpdating(localKey) && tracker.IsUpdating(otherKey) == false,
            $"run window ended while a command from another computer still updated it: still claimed = {stillHeld}; after both: {tracker.IsUpdating(localKey)}");

        // An update started outside this Martlet (a console there, another computer over SSH) is reported, not mistaken for an install.
        var self = HostEngineBusy.Read(HostEngineBusy.ExitCode, ["Updating the gateway...", SelfBusyLine]);
        var selfNote = self is null ? "" : HostUpdateTracker.BusyNote(Version, self, new DateTime(2026, 10, 2, 20, 21, 0));
        var install = HostEngineBusy.Read(HostEngineBusy.ExitCode, [InstallBusyLine]);
        var installNote = install is null ? "" : HostUpdateTracker.BusyNote(Version, install, new DateTime(2026, 10, 2, 20, 21, 0));
        Step("busy-note-names-another-update",
            selfNote.StartsWith("Another update of that host was already running (updating this host", StringComparison.Ordinal) &&
            installNote.StartsWith($"Waiting to update to Martlet {Version}: that host is busy (installing ollama", StringComparison.Ordinal),
            $"{selfNote} | {installNote}");

        tracker.Note(LocalHost, selfNote);
        tracker.Wait(LocalHost);
        tracker.Note(HostUpdateTracker.ThisPc, installNote);
        tracker.Wait(HostUpdateTracker.ThisPc);
        tracker.Note(OtherHost, installNote);
        tracker.Wait(OtherHost);
        var seenAt = new DateTime(2026, 10, 2, 20, 21, 30);
        var cleared = tracker.Current(HostUpdateTracker.ThisPc, Version, seenAt, seen: true, [LocalHost]);
        var again = tracker.Current(HostUpdateTracker.ThisPc, Version, seenAt.AddMinutes(3), seen: true, [LocalHost]);
        var updatedNote = $"{HostUpdateTracker.UpdatedNote}{Version} (seen at {seenAt:t}).";
        Step("current-host-replaces-stale-note",
            cleared && !again && tracker.Notes.GetValueOrDefault(LocalHost) == updatedNote &&
            tracker.Notes.GetValueOrDefault(HostUpdateTracker.ThisPc) == updatedNote &&
            !tracker.Waiting.Contains(LocalHost) && !tracker.Waiting.Contains(HostUpdateTracker.ThisPc) &&
            tracker.Notes.GetValueOrDefault(OtherHost) == installNote && tracker.Waiting.Contains(OtherHost),
            $"this PC's host service found current: changed = {cleared}, again = {again}; {LocalHost} now reads " +
            $"\"{tracker.Notes.GetValueOrDefault(LocalHost)}\"; still waiting {string.Join(", ", tracker.Waiting)}");

        var retry = tracker.TakeWaiting();
        Step("retry-takes-waiting", retry.SequenceEqual([OtherHost]) && tracker.Waiting.Count == 0,
            $"retry pass takes {string.Join(", ", retry)}; none wait afterwards until it finds one busy again");

        int askedWait = HostUpdateTracker.AskedLockWaitSeconds;
        Step("asked-update-waits", askedWait is > 0 and < 45 * 60,
            $"Update hosts now waits up to {askedWait / 60} min for another change on the host " +
            "(MARTLET_LOCK_WAIT), inside the 45 min limit of an unattended run; automatic updates stop at once and retry");

        // This PC's own host service follows the app's version after Martlet restarted into its update, in the background and
        // whatever Update paired hosts automatically says.
        const string previous = "0.21.0";
        var at = new DateTimeOffset(2026, 10, 2, 20, 21, 0, TimeSpan.Zero);
        var follower = new OwnHostFollower();
        var older = new OwnHostReading(previous, Running: true);
        OwnHostStep Decide(OwnHostReading? reading, DateTimeOffset now, bool appUpdateFirst = false, bool conversation = false) =>
            follower.Decide(Version, reading, tracker.IsUpdating(HostUpdateTracker.ThisPc), appUpdateFirst, conversation, now);
        var first = Decide(older, at);
        Step("own-host-follows-app-update", first == OwnHostStep.Update,
            $"Martlet restarted as {Version}; its host service runs {previous}: {first} (in the background, after the window is up)");

        var missing = Decide(null, at);
        var stopped = Decide(older with { Running = false }, at);
        Step("own-host-waits-until-it-runs", missing == OwnHostStep.NotFound && stopped == OwnHostStep.Stopped && follower.Due(Version, at),
            $"Docker not answering: {missing}; host service stopped: {stopped}; read again on the next minute's check");

        OwnHostStep claimed;
        using (tracker.Begin(HostUpdateTracker.ThisPc)) claimed = Decide(older, at);
        var ownUpdate = Decide(older, at, appUpdateFirst: true, conversation: true);
        var talking = Decide(older, at, conversation: true);
        Step("own-host-gives-way", claimed == OwnHostStep.OtherRoute && ownUpdate == OwnHostStep.AppUpdateFirst &&
            talking == OwnHostStep.Conversation,
            $"another route updating it: {claimed}; this PC about to install its own update: {ownUpdate}; replying or hearing you: {talking}");

        follower.Busy(at);
        var early = Decide(older, at.AddMinutes(2));
        var due = Decide(older, at + follower.RetryDelay);
        Step("own-host-busy-retries", early == OwnHostStep.Idle && due == OwnHostStep.Update,
            $"busy with another change: not read again for {follower.RetryDelay.TotalMinutes:0} min ({early}), then {due}");

        follower.Updated(Version);
        var afterwards = Decide(older, at.AddHours(1));
        var failing = new OwnHostFollower();
        failing.Failed(Version);
        Step("own-host-settles", afterwards == OwnHostStep.Idle && !follower.Due(Version, at.AddHours(1)) &&
            !failing.Due(Version, at) && failing.Due("0.23.0", at),
            "updated: no further reads this run; a failed update isn't retried automatically for that version (Update hosts " +
            "still runs it) but the next version is");

        return new { exitCode = passed ? 0 : 1, report = new { passed, total = steps.Count, steps } };
    }
}
