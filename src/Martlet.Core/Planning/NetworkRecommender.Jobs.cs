using Martlet.Core.Cluster;

namespace Martlet.Core.Planning;

public static partial class NetworkRecommender
{
    private sealed partial class Planner
    {
        // ---------- Speaking ----------

        /// <summary>The local options of the voice engine <paramref name="kind"/>: on a graphics card first, then the quickest.</summary>
        private IReadOnlyList<ComponentOption> VoiceOptions(string? kind) => kind is null ? [] : catalog.For(PlanComponent.Voice)
            .Where(o => o.IsLocal && o.HostRoleKind == kind).OrderBy(o => o.UsesGpu ? 0 : 1)
            .ThenBy(o => o.FirstWordMs ?? int.MaxValue).ThenBy(o => o.Id, StringComparer.Ordinal).ToArray();

        /// <summary>The options of the voice engine Speaking uses in the plan (the owner's, or the fallback).</summary>
        private IReadOnlyList<ComponentOption> EngineOptions() => VoiceOptions(voice);

        private string EngineName => Label(VoiceOptions(engine).FirstOrDefault(), engine ?? "");

        /// <summary>No computer has room for the owner's engine (benefit, reason, preferred computer), the note that says so,
        /// and how to take back the fallback's place.</summary>
        private (SetupChangeBenefit Benefit, string Reason, string? Prefer, string Note, Action? Undo)? starvedVoice;

        /// <summary>Rule 11: the voice engine tries again once a later step frees the room that today's roles held (Thinking
        /// moved to a host, listening off a companion PC), so applying the recommendation never leads to another.</summary>
        private void RetryVoice()
        {
            if (starvedVoice is not { } starved) return;
            starvedVoice = null;
            notes.Remove(starved.Note);
            if (cannotSpeak == starved.Note) cannotSpeak = null;
            decisions.Remove(ClusterJobs.Speaking);
            starved.Undo?.Invoke();
            voice = engine;
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
            // hosted voice that only stands in until it is ready, even though a hosted voice may start sooner.
            var why = now is null ? $"{EngineName} speaks your replies in the voices you made for it."
                : now.HostRoleKind is { } kind && IsVoice(kind) ? $"{EngineName} is your voice engine, and every computer speaks with the same one; it replaces {Label(now)}."
                : $"{EngineName} is your voice engine; {Label(now)} only stands in until it is ready.";
            ChooseVoice(SetupChangeBenefit.Improvement, why, host is { Presence: Presence.Here } ? host.Id : null);
        }

        /// <summary>The best place for the voice engine <paramref name="kind"/>: a host (where Speaking runs today when all else
        /// is equal), else the companion PC with the most room.</summary>
        private Slot? VoiceSlot(string kind, string? prefer)
        {
            var options = VoiceOptions(kind);
            return FindSlot(options, new Query(kind) { Prefer = prefer })
                ?? FindSlot(options, new Query(kind) { Hosts = false, Companions = true, MostRoom = true });
        }

        /// <summary>Places the owner's voice engine. When no computer has room for it, Speaking falls back to Chatterbox Nano
        /// (on a graphics card, else on the processor), then to a hosted voice whose key is saved; with neither, Martlet can't
        /// speak yet and a note says how to set up a computer for Nano. A fallback tries the owner's engine again after each
        /// later step (rule 11).</summary>
        private void ChooseVoice(SetupChangeBenefit benefit, string reason, string? prefer)
        {
            const string job = ClusterJobs.Speaking;
            if (VoiceSlot(engine!, prefer) is { } slot)
            {
                var why = $"{reason} {Plain(slot.Option)} on {CardText(slot)}{FirstWord(slot.Option)}" +
                    (slot.Node.Companion && !singlePc ? ", because no host can run it." : ".");
                Place(slot, engine!, job, benefit, why);
                Decide(job, slot.Node.Id, slot.Option.Id, why, benefit);
                return;
            }
            const string nano = FootprintCatalog.FallbackVoiceKind;
            var room = $"No computer has room for {EngineName}";
            if (engine != nano && VoiceSlot(nano, prefer) is { } fallback)
            {
                var node = fallback.Node;
                var old = node.Pending.FirstOrDefault(r => r.Kind == nano && !r.Native);
                var stay = node.Pending.Where(r => IsVoice(r.Kind) && r.Leave is null).ToList();
                var where = $"{Plain(fallback.Option)} on {CardText(fallback)}";
                var why = $"{reason} {room}, so Martlet speaks with {where}{FirstWord(fallback.Option)}.";
                var role = Place(fallback, nano, job, benefit, why);
                voice = nano;
                Decide(job, node.Id, fallback.Option.Id, why, benefit);
                Starve(benefit, reason, prefer, $"{room}: Martlet speaks with {where} instead. To use {EngineName} again, free room " +
                    $"on a computer for it, then choose it in Companion › Voice.", () =>
                    {
                        node.Roles.Remove(role);
                        if (old is not null) node.Pending.Add(old);
                        foreach (var other in stay) other.Leave = null;
                    });
                return;
            }
            var nanoText = engine == nano ? "" : " or Chatterbox Nano";
            var now = TodayOption(job);
            if ((now is { IsLocal: false } ? now : catalog.For(PlanComponent.Voice).FirstOrDefault(o => !o.IsLocal && Configured(o))) is { } hosted)
            {
                Decide(job, null, hosted.Id, $"{reason} {room}{nanoText}, so {hosted.DisplayName} speaks with your saved key.", benefit);
                Starve(benefit, reason, prefer, $"{room}{nanoText}: {hosted.DisplayName} speaks with your saved key instead.", null);
                return;
            }
            // Nowhere to speak: Speaking stays with a computer that is away (it speaks again when that one answers), else
            // nobody does it. Martlet says so instead of going quiet.
            var setUp = "To give Martlet a voice, set up the Martlet host service (it needs Docker) on a computer with an NVIDIA " +
                "card with 4 GB or more, or about 8 free processor threads, so it can run Chatterbox Nano.";
            var today = TodayJob(job);
            if (NodeOf(today?.HostId) is { Presence: Presence.Gone } gone)
                Decide(job, gone.Id, today!.OptionId, $"{room}{nanoText}, so {Lower(job)} stays with {gone.Name} until it answers again.", benefit);
            else
                Decide(job, null, null, $"{reason} {room}{nanoText}, so Martlet can't speak yet. {setUp}", benefit);
            cannotSpeak = $"Martlet can't speak yet: no computer can run {EngineName}{nanoText}. {setUp}";
            Starve(benefit, reason, prefer, cannotSpeak, null);
        }

        /// <summary>Speaking fell back: the note says so, and later steps let the owner's engine try again.</summary>
        private void Starve(SetupChangeBenefit benefit, string reason, string? prefer, string note, Action? undo)
        {
            starvedVoice = (benefit, reason, prefer, note, undo);
            notes.Add(note);
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
                : option.IsLocal ? $"{Label(option)} on {OwnPcs()}'s processor{FirstWord(option)}."
                : $"{option.DisplayName}, as you chose.");
        }

        /// <summary>A new place for listening: a host's card (or another card as fast), else the app's Parakeet on the processor.
        /// One companion PC alone hears in the app first, as the placement engine does, so its card goes to the voice and the
        /// face; the upgrade step moves listening to the card when room is left.</summary>
        private void ChooseListening((SetupChangeBenefit Benefit, string Why, bool Slower) forced, ComponentOption? now)
        {
            const string job = ClusterJobs.Listening;
            var slot = singlePc ? null : FindSlot(ListeningOrder(now), new Query(ListeningRole) { MaxMs = forced.Slower ? null : now?.FirstWordMs });
            if (slot is not null)
            {
                var why = $"{forced.Why} {Plain(slot.Option)} on {CardText(slot)} hears instead{FirstWord(slot.Option)}.";
                Place(slot, ListeningRole, job, forced.Benefit, why);
                Decide(job, slot.Node.Id, slot.Option.Id, why, forced.Benefit);
                return;
            }
            var app = InAppListening().FirstOrDefault();
            Decide(job, null, app?.Id, $"{forced.Why} {Label(app, "Parakeet")} hears on {OwnPcs()}'s processor instead.", forced.Benefit);
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
                Decide(job, null, today.OptionId, $"{OwnPcs(capital: true)} moves the character's face itself.");
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
                $"{forced.Why} {(singlePc ? "This PC has no room left" : "No other host has room")} for advanced lip-sync, so the character's face follows the voice's loudness.",
                forced.Benefit, off: true);
        }
    }
}
