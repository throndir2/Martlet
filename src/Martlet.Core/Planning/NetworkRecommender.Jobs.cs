using Martlet.Core.Cluster;

namespace Martlet.Core.Planning;

public static partial class NetworkRecommender
{
    private sealed partial class Planner
    {
        // ---------- Speaking ----------

        private IReadOnlyList<ComponentOption> EngineOptions() => catalog.For(PlanComponent.Voice)
            .Where(o => o.IsLocal && o.HostRoleKind == engine).OrderBy(o => o.UsesGpu ? 0 : 1)
            .ThenBy(o => o.FirstWordMs ?? int.MaxValue).ThenBy(o => o.Id, StringComparer.Ordinal).ToArray();

        private string EngineName => Label(EngineOptions().FirstOrDefault(), engine ?? "");

        /// <summary>Speaking fell back to a Windows voice for lack of room (benefit, reason, preferred computer).</summary>
        private (SetupChangeBenefit Benefit, string Reason, string? Prefer)? starvedVoice;

        private string StarvedNote => $"No computer has room for {EngineName}: Martlet speaks with a Windows voice until one does.";

        /// <summary>Rule 11: the voice engine tries again once a later step frees the room that today's roles held (Thinking
        /// moved to a host, listening off a companion PC), so applying the recommendation never leads to another.</summary>
        private void RetryVoice()
        {
            if (starvedVoice is not { } starved) return;
            starvedVoice = null;
            notes.Remove(StarvedNote);
            decisions.Remove(ClusterJobs.Speaking);
            ChooseVoice(starved.Benefit, starved.Reason, starved.Prefer);
        }

        private void Speaking()
        {
            const string job = ClusterJobs.Speaking;
            if (Settled(job)) return;
            var today = TodayJob(job);
            var host = NodeOf(today?.HostId);
            var now = TodayOption(job);
            if (engine is null)
            {
                Decide(job, today?.HostId, now?.Id ?? today?.OptionId, now is null ? "" : $"{Label(now)}, as you chose.");
                return;
            }
            var options = EngineOptions();
            if (now?.HostRoleKind == engine && host is { Presence: Presence.Here })
            {
                if (Settle(job, engine, now, host, options) is { } forced) ChooseVoice(forced.Benefit, forced.Why, null);
                return;
            }
            if (now?.HostRoleKind == engine && host is { Presence: Presence.Gone })
            {
                ChooseVoice(SetupChangeBenefit.Required, Gone(host, job), null);
                return;
            }
            // Rule 8 before rule 7: the owner's voice engine (their voices are made for it) replaces another engine, or a
            // Windows voice that only stands in until it is ready, even though a Windows voice starts sooner.
            var why = now is null ? $"{EngineName} speaks your replies in the voices you made for it."
                : now.HostRoleKind is { } kind && IsVoice(kind) ? $"{EngineName} is your voice engine, and every computer speaks with the same one; it replaces {Label(now)}."
                : $"{EngineName} is your voice engine; {Label(now)} only stands in until it is ready.";
            ChooseVoice(SetupChangeBenefit.Improvement, why, host is { Presence: Presence.Here } ? host.Id : null);
        }

        private void ChooseVoice(SetupChangeBenefit benefit, string reason, string? prefer)
        {
            const string job = ClusterJobs.Speaking;
            var options = EngineOptions();
            var slot = FindSlot(options, new Query(engine!) { Prefer = prefer })
                ?? FindSlot(options, new Query(engine!) { Hosts = false, Companions = true, MostRoom = true });
            if (slot is null)
            {
                Decide(job, null, catalog.Find(FootprintCatalog.WindowsVoiceId)?.Id,
                    $"{reason} No computer has room for {EngineName}, so Martlet speaks with a Windows voice until one does.", benefit);
                starvedVoice = (benefit, reason, prefer);
                notes.Add(StarvedNote);
                return;
            }
            var why = $"{reason} {Plain(slot.Option)} on {CardText(slot)}{FirstWord(slot.Option)}" +
                (slot.Node.Companion && !singlePc ? ", because no host can run it." : ".");
            Place(slot, engine!, job, benefit, why);
            Decide(job, slot.Node.Id, slot.Option.Id, why, benefit);
        }

        // ---------- Listening ----------

        private IEnumerable<ComponentOption> InAppListening() => catalog.For(PlanComponent.Listening).Where(o => o.IsLocal && o.RunsInApp);

        /// <summary>Today's model and its variants, then the best speech recognizers on a card, then on the processor.</summary>
        private IEnumerable<ComponentOption> ListeningOrder(ComponentOption? now)
        {
            var same = SameModel(now, ListeningRole);
            return same.Concat(catalog.For(PlanComponent.Listening).Where(o => o.IsLocal && o.HostRoleKind == ListeningRole && !same.Contains(o))
                .OrderBy(o => o.UsesGpu ? 0 : 1).ThenByDescending(o => o.QualityTier).ThenBy(o => o.FirstWordMs ?? int.MaxValue)
                .ThenBy(o => o.Id, StringComparer.Ordinal));
        }

        private void Listening()
        {
            const string job = ClusterJobs.Listening;
            if (Settled(job)) return;
            var host = NodeOf(TodayJob(job)?.HostId);
            var now = TodayOption(job);
            if (host is { Presence: Presence.Here })
            {
                if (Settle(job, ListeningRole, now, host, SameModel(now, ListeningRole)) is { } forced) ChooseListening(forced, now);
                return;
            }
            if (host is { Presence: Presence.Gone })
            {
                ChooseListening((SetupChangeBenefit.Required, Gone(host, job), true), now);
                return;
            }
            // In the app on each companion PC, or a hosted provider the owner chose; the upgrade step may give a host the job.
            var option = now ?? InAppListening().FirstOrDefault();
            Decide(job, null, option?.Id, option is null ? ""
                : option.IsLocal ? $"{Label(option)} on each companion PC's processor{FirstWord(option)}."
                : $"{option.DisplayName}, as you chose.");
        }

        private void ChooseListening((SetupChangeBenefit Benefit, string Why, bool Slower) forced, ComponentOption? now)
        {
            const string job = ClusterJobs.Listening;
            var slot = FindSlot(ListeningOrder(now), new Query(ListeningRole) { MaxMs = forced.Slower ? null : now?.FirstWordMs });
            if (slot is not null)
            {
                var why = $"{forced.Why} {Plain(slot.Option)} on {CardText(slot)} hears instead{FirstWord(slot.Option)}.";
                Place(slot, ListeningRole, job, forced.Benefit, why);
                Decide(job, slot.Node.Id, slot.Option.Id, why, forced.Benefit);
                return;
            }
            var app = InAppListening().FirstOrDefault();
            Decide(job, null, app?.Id, $"{forced.Why} {Label(app, "Parakeet")} hears on each companion PC's processor instead.", forced.Benefit);
        }

        /// <summary>Listening in the app (or hosted, when the owner keeps everything local) moves to a host's card when a
        /// speech recognizer there hears as soon (rule 7), so companion PCs' processors stay free for games (rule 6) and the
        /// Listening pool can share the work (rule 9).</summary>
        private void ListeningUpgrade()
        {
            const string job = ClusterJobs.Listening;
            if (!decisions.TryGetValue(job, out var decision) || decision.Frozen || decision.HostId is not null) return;
            if (Find(decision.OptionId) is not { } now || now.IsLocal && !now.RunsInApp) return;
            if (!now.IsLocal && request.Preference != HostingPreference.PreferLocal) return;
            var options = catalog.For(PlanComponent.Listening)
                .Where(o => o.IsLocal && o.UsesGpu && o.HostRoleKind == ListeningRole && NotSlower(o, now))
                .OrderByDescending(o => o.QualityTier).ThenBy(o => o.FirstWordMs ?? int.MaxValue).ThenBy(o => o.Id, StringComparer.Ordinal);
            var slot = FindSlot(options, new Query(ListeningRole) { Optional = true, StrictWindows = true });
            if (slot is null) return;
            var why = $"{Plain(slot.Option)} on {CardText(slot)} hears {Sooner(now, slot.Option)}" +
                (now.IsLocal ? ", and companion PCs' processors stay free for games." : ", and your recordings stay on your computers.");
            Place(slot, ListeningRole, job, SetupChangeBenefit.Improvement, why);
            Decide(job, slot.Node.Id, slot.Option.Id, why, SetupChangeBenefit.Improvement);
        }

        // ---------- Lip-sync ----------

        private ComponentOption[] FaceOptions() => catalog.For(PlanComponent.LipSync)
            .Where(o => o.IsLocal && o.HostRoleKind == LipSyncRole).OrderBy(o => o.Id, StringComparer.Ordinal).ToArray();

        private void LipSync()
        {
            const string job = ClusterJobs.LipSync;
            if (Settled(job)) return;
            var today = TodayJob(job);
            var host = NodeOf(today?.HostId);
            if (today is { Off: true })
            {
                Decide(job, null, today.OptionId, "The character's face follows the voice's loudness, as you chose.", off: true);
                return;
            }
            if (host is { Presence: Presence.Here })
            {
                if (Settle(job, LipSyncRole, TodayOption(job), host, FaceOptions()) is { } forced) ChooseLipSync(forced);
                return;
            }
            if (host is { Presence: Presence.Gone })
            {
                ChooseLipSync((SetupChangeBenefit.Required, Gone(host, job), true));
                return;
            }
            if (today is not null)
            {
                Decide(job, null, today.OptionId, "Each companion PC moves the character's face itself.");
                return;
            }
        }

        /// <summary>Advanced lip-sync where it isn't set up: on a host with room after Thinking (rule 7: Thinking gets the
        /// idle card first).</summary>
        private void NewLipSync()
        {
            const string job = ClusterJobs.LipSync;
            if (decisions.ContainsKey(job) || !Wants(PlanComponent.LipSync)) return;
            var slot = FindSlot(FaceOptions(), new Query(LipSyncRole) { Optional = true, StrictWindows = true });
            if (slot is null) return;
            var why = $"{Label(slot.Option)} on {CardText(slot)} moves the character's face with the voice.";
            Place(slot, LipSyncRole, job, SetupChangeBenefit.Improvement, why);
            Decide(job, slot.Node.Id, slot.Option.Id, why, SetupChangeBenefit.Improvement);
        }

        private void ChooseLipSync((SetupChangeBenefit Benefit, string Why, bool Slower) forced)
        {
            const string job = ClusterJobs.LipSync;
            var slot = FindSlot(FaceOptions(), new Query(LipSyncRole));
            if (slot is not null)
            {
                var why = $"{forced.Why} {Label(slot.Option)} on {CardText(slot)} moves the character's face instead.";
                Place(slot, LipSyncRole, job, forced.Benefit, why);
                Decide(job, slot.Node.Id, slot.Option.Id, why, forced.Benefit);
                return;
            }
            Decide(job, null, "loudness-lipsync",
                $"{forced.Why} No other host has room for advanced lip-sync, so the character's face follows the voice's loudness.", forced.Benefit, off: true);
        }
    }
}
